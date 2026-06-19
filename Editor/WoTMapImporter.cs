using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Terrain;
using WoTMapImporter.Editor.Utils;
using WoTMapImporter.Editor.Xml;

namespace WoTMapImporter.Editor
{
    /// <summary>
    /// Orchestrates the full WoT map import flow.
    ///
    /// Architecture (mirrors Simi4's Blender addon):
    ///   1. EXTRACT the relevant .pkg files to a temp directory on disk.
    ///      This is how the original addon reads everything - it never reads
    ///      from ZipFile directly for terrain.
    ///   2. Read space.settings (old) or space.bin (new) from the extracted dir.
    ///   3. Open every *.cdata file in spaces/&lt;map_name&gt;/ (terrain chunks).
    ///   4. Open every *.chunk file (static model placements).
    ///   5. Read textures/models from the extracted dir + any shared*.pkg.
    /// </summary>
    public static class WoTMapImporter
    {
        public class ImportSettings
        {
            public bool LoadObjects = true;
            public bool LoadNormals = true;
            public bool LoadWetness = false;
            public int MaxHeightmapResolution = 4097;
        }

        public class ImportResult
        {
            public GameObject Root;
            public TerrainData TerrainData;
            public List<string> Warnings = new List<string>();
            public List<string> Errors = new List<string>();
            public TimeSpan Duration;
        }

        public static ImportResult ImportMap(
            MapInfo mapInfo,
            string wotResPath,
            string outputFolder,
            ImportSettings settings,
            Action<float, string> progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new ImportResult();
            WoTLogger.Info($"=== Importing {mapInfo.Name} ===");

            string geometry = string.IsNullOrEmpty(mapInfo.Geometry)
                ? $"spaces/{mapInfo.Name}" : mapInfo.Geometry;
            string spaceName = geometry.IndexOf('/') >= 0
                ? geometry.Substring(geometry.IndexOf('/') + 1) : geometry;

            progress?.Invoke(0.05f, "Extracting .pkg files...");

            // 1. Extract relevant .pkg files to a temp directory.
            string extractDir = Path.Combine(Path.GetTempPath(), "WoTMapImporter", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDir);
            WoTLogger.Info($"Extract dir: {extractDir}");

            WoTPackageManager pkgMgr = null;
            try
            {
                ExtractMapPackages(wotResPath, spaceName, extractDir, progress);

                // The actual map content lives in <extractDir>/spaces/<map_name>/
                string spaceDir = Path.Combine(extractDir, geometry.Replace('/', Path.DirectorySeparatorChar));
                WoTLogger.Info($"Space dir: {spaceDir} (exists: {Directory.Exists(spaceDir)})");

                // Also open shared*.pkg (still as ZipFile, used only for resource lookups)
                pkgMgr = new WoTPackageManager(wotResPath,
                    GetSharedPackageNames(wotResPath));

                progress?.Invoke(0.15f, "Loading space settings...");
                UniversalTerrain universalTerrain = LoadTerrainMetadata(spaceDir, pkgMgr, geometry);

                progress?.Invoke(0.3f, "Loading cdata chunks...");
                var chunks = LoadAllChunks(spaceDir, universalTerrain);

                if (chunks.Count == 0)
                {
                    result.Errors.Add("No terrain chunks found at " + spaceDir);
                    return result;
                }

                progress?.Invoke(0.7f, "Building Unity Terrain...");
                var folder = $"{outputFolder}/{mapInfo.Name}";
                var buildResult = TerrainBuilder.Build(folder, mapInfo, universalTerrain, chunks, pkgMgr,
                                                       settings.MaxHeightmapResolution);
                result.TerrainData = buildResult.TerrainData;
                result.Warnings.AddRange(buildResult.Warnings);

                progress?.Invoke(0.9f, "Creating root object...");
                var root = new GameObject($"WoTMap_{mapInfo.Name}");
                if (buildResult.TerrainObject != null)
                    buildResult.TerrainObject.transform.SetParent(root.transform, false);

                result.Root = root;
                string prefabPath = $"{folder}/WoTMap_{mapInfo.Name}.prefab";
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                progress?.Invoke(1.0f, "Done.");
            }
            catch (Exception e)
            {
                WoTLogger.Error($"Import failed: {e.Message}\n{e.StackTrace}");
                result.Errors.Add(e.Message);
            }
            finally
            {
                pkgMgr?.Dispose();
                // Clean up extract dir asynchronously
                try { Directory.Delete(extractDir, recursive: true); }
                catch (Exception ex) { WoTLogger.Warn($"Could not clean up extract dir: {ex.Message}"); }
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            WoTLogger.Info($"=== Import finished in {sw.Elapsed.TotalSeconds:F2}s ===");
            return result;
        }

        // =================== EXTRACTION ===================

        private static void ExtractMapPackages(
            string wotResPath, string spaceName, string extractDir,
            Action<float, string> progress)
        {
            // Extract the map's main package + _bin package + particles package.
            // This matches Simi4's loader.extract_space_pkg().
            string[] packagesToExtract = {
                $"{spaceName}.pkg",
                $"{spaceName}_bin.pkg",
                "particles.pkg",
            };

            int extracted = 0;
            foreach (var pkgName in packagesToExtract)
            {
                string pkgPath = Path.Combine(wotResPath, pkgName);
                if (File.Exists(pkgPath))
                {
                    ExtractZip(pkgPath, extractDir);
                    WoTLogger.Info($"Extracted {pkgName}");
                }
                else
                {
                    WoTLogger.Warn($"pkg not found (skipping): {pkgName}");
                }
                extracted++;
                progress?.Invoke(0.05f + 0.05f * extracted, $"Extracted {extracted}/{packagesToExtract.Length}...");
            }
        }

        private static List<string> GetSharedPackageNames(string wotResPath)
        {
            var list = new List<string>();
            string pkgsDir = wotResPath;
            if (!Directory.Exists(pkgsDir)) return list;
            foreach (var f in Directory.GetFiles(pkgsDir, "shared*.pkg"))
            {
                string fname = Path.GetFileName(f);
                if (fname.Contains("_hd-")) continue;
                list.Add(fname);
            }
            return list;
        }

        private static void ExtractZip(string zipPath, string destDir)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                // Skip directory entries
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string outPath = Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                using var fs = entry.Open();
                using var outFs = File.Create(outPath);
                fs.CopyTo(outFs);
            }
        }

        // =================== TERRAIN METADATA ===================

        private static UniversalTerrain LoadTerrainMetadata(
            string spaceDir, WoTPackageManager pkgMgr, string geometry)
        {
            var ut = new UniversalTerrain { ChunkSize = 100f };

            // Old format: space.settings XML
            string settingsPath = Path.Combine(spaceDir, "space.settings");
            if (File.Exists(settingsPath))
            {
                try
                {
                    var doc = XmlUnpacker.ReadBytes(File.ReadAllBytes(settingsPath));
                    ut.MinX = int.Parse(doc.SelectSingleNode("/root/bounds/minX").InnerText.Trim());
                    ut.MaxX = int.Parse(doc.SelectSingleNode("/root/bounds/maxX").InnerText.Trim());
                    ut.MinY = int.Parse(doc.SelectSingleNode("/root/bounds/minY").InnerText.Trim());
                    ut.MaxY = int.Parse(doc.SelectSingleNode("/root/bounds/maxY").InnerText.Trim());
                    WoTLogger.Info($"space.settings bounds: x[{ut.MinX}..{ut.MaxX}] y[{ut.MinY}..{ut.MaxY}]");
                    return ut;
                }
                catch (Exception e)
                {
                    WoTLogger.Warn($"Could not parse space.settings: {e.Message}");
                }
            }

            // New format: space.bin (compiled space)
            string spaceBinPath = Path.Combine(spaceDir, "space.bin");
            if (File.Exists(spaceBinPath))
            {
                WoTLogger.Info($"Found space.bin ({new FileInfo(spaceBinPath).Length} bytes), but CompiledSpace parsing not implemented");
                WoTLogger.Warn("Compiled space metadata extraction not yet supported; bounds will be derived from cdata filenames");
            }

            // Fallback: derive from cdata files we have
            ut.MinX = ut.MinY = 0;
            ut.MaxX = ut.MaxY = 0;
            return ut;
        }

        // =================== CHUNK LOADING ===================

        private static List<TerrainChunk> LoadAllChunks(
            string spaceDir, UniversalTerrain ut)
        {
            var chunks = new List<TerrainChunk>();

            if (!Directory.Exists(spaceDir))
            {
                WoTLogger.Error($"spaceDir does not exist: {spaceDir}");
                return chunks;
            }

            // Diagnostic: dump the contents of spaceDir so we know what's there.
            WoTLogger.Info($"=== Contents of {spaceDir} (first 30 files) ===");
            try
            {
                var allFiles = Directory.GetFiles(spaceDir, "*", SearchOption.AllDirectories);
                WoTLogger.Info($"Total files: {allFiles.Length}");
                for (int i = 0; i < Math.Min(30, allFiles.Length); i++)
                {
                    string rel = allFiles[i].Substring(spaceDir.Length + 1).Replace('\\', '/');
                    long size = new FileInfo(allFiles[i]).Length;
                    WoTLogger.Info($"  [{size,8} B] {rel}");
                }
                if (allFiles.Length > 30)
                    WoTLogger.Info($"  ... and {allFiles.Length - 30} more");
            }
            catch (Exception e)
            {
                WoTLogger.Warn($"Could not list spaceDir contents: {e.Message}");
            }

            // Find all cdata files in the extracted space directory.
            // Files can be:
            //   - XXXXXXXX.cdata          (old format, unprocessed)
            //   - XXXXXXXXo.cdata_processed (new format, processed/optimized)
            //   - XXXXXXXX.cdata_processed (also seen)
            // Simi4 uses glob('*.cdata') which catches all three.
            var cdataPaths = Directory.GetFiles(spaceDir, "*.cdata*");
            // Filter to actual cdata files (not just any *.cdata* prefix)
            cdataPaths = cdataPaths.Where(p => {
                string n = Path.GetFileName(p).ToLowerInvariant();
                return n.EndsWith(".cdata") || n.EndsWith(".cdata_processed");
            }).ToArray();
            WoTLogger.Info($"Found {cdataPaths.Length} .cdata/.cdata_processed files in {spaceDir}");
            if (cdataPaths.Length == 0)
            {
                cdataPaths = Directory.GetFiles(spaceDir, "*.cdata*", SearchOption.AllDirectories);
                cdataPaths = cdataPaths.Where(p => {
                    string n = Path.GetFileName(p).ToLowerInvariant();
                    return n.EndsWith(".cdata") || n.EndsWith(".cdata_processed");
                }).ToArray();
                WoTLogger.Info($"Recursive search: {cdataPaths.Length} .cdata files");

                // Last resort: find any file matching XXXXYYY pattern (with optional
                // 'o' suffix and optional extension).
                if (cdataPaths.Length == 0)
                {
                    WoTLogger.Info("Falling back to hex-name pattern search...");
                    var allFiles = Directory.GetFiles(spaceDir, "*", SearchOption.AllDirectories);
                    var hexFiles = new List<string>();
                    foreach (var f in allFiles)
                    {
                        string baseN = Path.GetFileNameWithoutExtension(f);
                        // Strip optional trailing 'o' (e.g. "00000000o" -> "00000000")
                        if (baseN.EndsWith("o")) baseN = baseN.Substring(0, baseN.Length - 1);
                        if (baseN.Length != 8) continue;
                        bool isHex = true;
                        for (int i = 0; i < 8; i++)
                        {
                            char c = baseN[i];
                            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                            { isHex = false; break; }
                        }
                        if (isHex) hexFiles.Add(f);
                    }
                    cdataPaths = hexFiles.ToArray();
                    WoTLogger.Info($"Hex-name search: {cdataPaths.Length} files matching XXXXYYYY[o] pattern");
                }
            }

            // Sort for deterministic processing
            Array.Sort(cdataPaths, StringComparer.OrdinalIgnoreCase);

            // Discover bounds from chunk names if not set
            if (ut.MinX == ut.MaxX && ut.MinY == ut.MaxY && cdataPaths.Length > 0)
            {
                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                foreach (var path in cdataPaths)
                {
                    ParseChunkName(Path.GetFileName(path), out int hexX, out int hexY);
                    if (hexX < minX) minX = hexX;
                    if (hexX > maxX) maxX = hexX;
                    if (hexY < minY) minY = hexY;
                    if (hexY > maxY) maxY = hexY;
                }
                ut.MinX = minX; ut.MaxX = maxX; ut.MinY = minY; ut.MaxY = maxY;
                WoTLogger.Info($"Discovered terrain bounds from cdata: x[{minX}..{maxX}] y[{minY}..{maxY}]");
            }

            int idx = 0;
            foreach (var path in cdataPaths)
            {
                string baseName = Path.GetFileName(path);
                ParseChunkName(baseName, out int hexX, out int hexY);
                Vector2 chunkPos = new Vector2(hexX * ut.ChunkSize, hexY * ut.ChunkSize);

                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    if (data.Length < 4) continue;

                    var chunk = TerrainChunkDecoder.Decode(data, baseName, chunkPos);
                    if (chunk != null)
                        chunks.Add(chunk);
                }
                catch (Exception e)
                {
                    WoTLogger.Warn($"Failed to decode chunk {baseName}: {e.Message}");
                }

                idx++;
            }
            WoTLogger.Info($"Loaded {chunks.Count}/{cdataPaths.Length} terrain chunks");
            return chunks;
        }

        private static void ParseChunkName(string name, out int hexX, out int hexY)
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            hexX = 0; hexY = 0;
            if (baseName.Length < 8) return;
            try
            {
                hexX = unchecked((short)Convert.ToInt32(baseName.Substring(0, 4), 16));
                hexY = unchecked((short)Convert.ToInt32(baseName.Substring(4, 8), 16));
            }
            catch { }
        }
    }
}
