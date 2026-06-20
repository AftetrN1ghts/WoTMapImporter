using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Terrain
{
    /// <summary>
    /// Builds a single Unity Terrain from decoded TerrainChunks.
    /// Matches the visual blending logic of Simi4/WoT-Blender-Addons:
    ///   - new blend format: blend_texture[i].A -> layer[i*2].weight, blend_texture[i].G -> layer[i*2+1].weight
    ///   - old blend format: blend_texture[i].Color -> layer[i].weight
    ///   - chain: out = sum(layer[i].color * w_i)
    /// </summary>
    public static class TerrainBuilder
    {
        public class BuildResult
        {
            public GameObject TerrainObject;
            public TerrainData TerrainData;
            public List<string> Warnings = new List<string>();
        }

        public struct StitchContext
        {
            public int targetRes;
            public float minX, minY;
            public float worldSizeX, worldSizeZ;
            public float chunkSize;
        }

        public static BuildResult Build(
            string outputPath,
            MapInfo mapInfo,
            UniversalTerrain terrain,
            List<TerrainChunk> chunks,
            WoTPackageManager resMgr,
            int maxResolution = 4097)
        {
            _chunkLogCount = 0;
            if (chunks.Count == 0)
                throw new Exception("No terrain chunks to build");

            int chunkPixels = chunks[0].HeightsTex.width;
            int totalPixelsX = terrain.NumChunksX * chunkPixels;
            int totalPixelsZ = terrain.NumChunksY * chunkPixels;

            int targetRes = LargestPow2Plus1(maxResolution);
            if (targetRes > totalPixelsX) targetRes = LargestPow2Plus1Ceil(totalPixelsX);
            if (targetRes > maxResolution) targetRes = maxResolution;

            WoTLogger.Info($"Stitching {chunks.Count} chunks: grid {terrain.NumChunksX}x{terrain.NumChunksY}, " +
                           $"pixels per chunk {chunkPixels}, total {totalPixelsX}x{totalPixelsZ}, " +
                           $"Unity heightmap {targetRes}x{targetRes}");

            // Compute world bounds (lower-left of map)
            float minX = float.MaxValue, minY = float.MaxValue;
            foreach (var c in chunks)
            {
                if (c.ChunkPos.x < minX) minX = c.ChunkPos.x;
                if (c.ChunkPos.y < minY) minY = c.ChunkPos.y;
            }
            var ctx = new StitchContext
            {
                targetRes = targetRes,
                minX = minX,
                minY = minY,
                worldSizeX = terrain.TotalSizeX,
                worldSizeZ = terrain.TotalSizeZ,
                chunkSize = terrain.ChunkSize,
            };

            // ---- Heights ----
            float[,] heights = StitchHeights(chunks, ctx);

            // ---- Collect unique global layers ----
            var globalLayers = new List<TerrainLayerDef>();
            var globalLayerIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in chunks)
            {
                foreach (var layer in chunk.Layers)
                {
                    if (!globalLayerIdx.ContainsKey(layer.Name))
                    {
                        globalLayerIdx[layer.Name] = globalLayers.Count;
                        globalLayers.Add(layer);
                    }
                }
            }
            WoTLogger.Info($"Found {globalLayers.Count} unique terrain layers");

            // ---- Splat data (Unity expects [numLayers, res, res]) ----
            int totalLayerCount = globalLayers.Count;
            // Per-layer weight storage. globalSplat[layerIdx][y, x]
            float[][,] globalSplat = new float[totalLayerCount][,];
            for (int i = 0; i < totalLayerCount; i++)
                globalSplat[i] = new float[targetRes, targetRes];

            foreach (var chunk in chunks)
                FillChunkSplat(chunk, globalLayers, globalLayerIdx, globalSplat, ctx);

            // Diagnostic: how much total weight landed in each layer? If only one
            // layer has non-zero weight, the blend mapping/decoding is wrong.
            for (int li = 0; li < totalLayerCount; li++)
            {
                double sum = 0; float mx = 0;
                for (int y = 0; y < targetRes; y++)
                    for (int x = 0; x < targetRes; x++)
                    { float v = globalSplat[li][y, x]; sum += v; if (v > mx) mx = v; }
                WoTLogger.Info($"  splat layer {li} ({Path.GetFileName(globalLayers[li].Name)}): " +
                               $"sumWeight={sum:F0} maxWeight={mx:F3}");
            }

            // Convert to Unity's alphamap layout. IMPORTANT: Unity expects
            // float[height, width, numLayers] - the LAYER index is the LAST
            // dimension, not the first. Using [numLayers, res, res] makes
            // SetAlphamaps throw "Float array size wrong (layers should be N)".
            // We also normalize the weights per pixel so they sum to 1 (Unity
            // requirement for correct blending).
            var splatForUnity = new float[targetRes, targetRes, totalLayerCount];
            for (int y = 0; y < targetRes; y++)
            {
                for (int x = 0; x < targetRes; x++)
                {
                    float sum = 0f;
                    for (int li = 0; li < totalLayerCount; li++)
                        sum += globalSplat[li][y, x];

                    if (sum > 1e-6f)
                    {
                        for (int li = 0; li < totalLayerCount; li++)
                            splatForUnity[y, x, li] = globalSplat[li][y, x] / sum;
                    }
                    else
                    {
                        // No weight anywhere -> dump everything into layer 0 so the
                        // terrain isn't transparent/black.
                        splatForUnity[y, x, 0] = 1f;
                    }
                }
            }

            // ---- Create TerrainData ----
            // Heights are currently in METRES. Unity's SetHeights expects values
            // normalized to [0,1] relative to TerrainData.size.y; anything > 1 is
            // clamped to 1 (which is what made the terrain a flat plateau before).
            ComputeHeightMinMax(heights, out float hMin, out float hMax);
            float heightRange = Mathf.Max(hMax - hMin, 1f);
            // Add a little headroom so the highest point isn't exactly at 1.0.
            float sizeY = heightRange * 1.05f;

            for (int y = 0; y < targetRes; y++)
                for (int x = 0; x < targetRes; x++)
                    heights[y, x] = Mathf.Clamp01((heights[y, x] - hMin) / sizeY);

            var td = new TerrainData
            {
                heightmapResolution = targetRes,
                alphamapResolution = targetRes,
                baseMapResolution = Mathf.Min(1024, targetRes),
                size = new Vector3(terrain.TotalSizeX, sizeY, terrain.TotalSizeZ),
                name = mapInfo.Name + "_TerrainData",
            };
            td.SetHeights(0, 0, heights);
            WoTLogger.Info($"Heights: min={hMin:F1}m max={hMax:F1}m range={heightRange:F1}m, " +
                           $"terrain size=({terrain.TotalSizeX:F0}, {sizeY:F1}, {terrain.TotalSizeZ:F0})");

            if (totalLayerCount > 0)
            {
                // terrainLayers must be assigned BEFORE SetAlphamaps so that
                // alphamapLayers matches splatForUnity's 3rd dimension.
                td.terrainLayers = CreateTerrainLayers(globalLayers, resMgr, outputPath);
                td.SetAlphamaps(0, 0, splatForUnity); // splatForUnity is [y, x, layer]

                // Read back what Unity actually stored, to prove the alphamap is
                // multi-layer (and not collapsed to layer 0).
                var check = td.GetAlphamaps(0, 0, td.alphamapWidth, td.alphamapHeight);
                var layerCoverage = new double[totalLayerCount];
                int cw = check.GetLength(1), ch = check.GetLength(0);
                int cl = check.GetLength(2);
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                        for (int li = 0; li < cl; li++)
                            layerCoverage[li] += check[y, x, li];
                var sb = new System.Text.StringBuilder();
                for (int li = 0; li < totalLayerCount; li++)
                    sb.Append($"{li}:{layerCoverage[li]:F0} ");
                WoTLogger.Info($"TerrainData alphamap readback ({cw}x{ch}x{cl}) coverage per layer: {sb}");
            }

            // ---- Save assets ----
            EnsureFolder(outputPath);
            string assetPath = outputPath + "/" + td.name + ".asset";
            AssetDatabase.CreateAsset(td, assetPath);

            var go = UnityEngine.Terrain.CreateTerrainGameObject(td);
            go.name = mapInfo.Name + "_Terrain";

            // Make Unity rebuild the composite/basemap so the painted layers show
            // up immediately (the basemap is what you see at a distance; without a
            // flush it can display just layer 0).
            var terrainComp = go.GetComponent<UnityEngine.Terrain>();
            if (terrainComp != null)
            {
                // Use our custom multilayer terrain shader so all N layers render
                // in a single pass (URP's default Terrain/Lit only shows 4).
                var shader = Shader.Find("WoT/TerrainMultilayer");
                if (shader != null)
                {
                    var mat = new Material(shader) { name = mapInfo.Name + "_TerrainMat" };
                    mat.SetFloat("_NumLayers", totalLayerCount);

                    // IMPORTANT: Unity only auto-binds _Control*/_Splat* to the
                    // built-in terrain shader. For a custom material we must wire
                    // them up by hand from the TerrainData.
                    var alphaTextures = td.alphamapTextures; // each RGBA = 4 layer weights
                    WoTLogger.Info($"Binding {alphaTextures.Length} control textures, {td.terrainLayers.Length} splat layers");
                    for (int ci = 0; ci < alphaTextures.Length && ci < 4; ci++)
                        mat.SetTexture("_Control" + ci, alphaTextures[ci]);

                    var tlayers = td.terrainLayers;
                    for (int li = 0; li < tlayers.Length && li < 16; li++)
                    {
                        mat.SetTexture("_Splat" + li, tlayers[li].diffuseTexture);
                        // Terrain UV is 0..1 across the whole terrain; convert the
                        // layer's metric tileSize into a UV-space tiling factor.
                        Vector2 ts = tlayers[li].tileSize;
                        float tilesX = ts.x > 0.001f ? terrain.TotalSizeX / ts.x : 1f;
                        float tilesY = ts.y > 0.001f ? terrain.TotalSizeZ / ts.y : 1f;
                        mat.SetVector("_Splat" + li + "_ST", new Vector4(tilesX, tilesY, 0f, 0f));
                    }

                    terrainComp.materialTemplate = mat;

                    string matPath = outputPath + "/" + mat.name + ".mat";
                    var existingMat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                    if (existingMat != null) AssetDatabase.DeleteAsset(matPath);
                    AssetDatabase.CreateAsset(mat, matPath);
                    WoTLogger.Info($"Terrain uses custom multilayer shader ({totalLayerCount} layers in one pass)");
                }
                else
                {
                    // Fallback: pipeline default (shows 4 layers on URP).
                    var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                             ?? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
                    var pipelineMat = rp != null ? rp.defaultTerrainMaterial : null;
                    if (pipelineMat != null) terrainComp.materialTemplate = pipelineMat;
                    WoTLogger.Warn("WoT/TerrainMultilayer shader not found; using pipeline default (max 4 visible layers)");
                }
                terrainComp.basemapDistance = 100000f; // always use real layers
                terrainComp.Flush();
            }
            td.SetBaseMapDirty();
            EditorUtility.SetDirty(td);
            AssetDatabase.SaveAssets();

            string prefabPath = outputPath + "/" + go.name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);

            return new BuildResult
            {
                TerrainObject = go,
                TerrainData = td,
            };
        }

        // ====================== HEIGHTS ======================

        private static float[,] StitchHeights(List<TerrainChunk> chunks, StitchContext ctx)
        {
            float[,] result = new float[ctx.targetRes, ctx.targetRes];
            foreach (var chunk in chunks)
            {
                if (chunk.HeightsTex == null) continue;
                var pixels = chunk.HeightsTex.GetPixels32();
                int w = chunk.HeightsTex.width;
                int h = chunk.HeightsTex.height;
                float[] chunkHeights = DecodeHeightPixels(pixels, w, h);
                BilinearStitch(chunk, chunkHeights, w, h, ctx, result);
            }
            return result;
        }

        public static float[] DecodeHeightPixels(Color32[] pixels, int w, int h)
        {
            float[] result = new float[w * h];
            const float scaleFactor = 1000f / 256f;
            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    var c = pixels[py * w + px];
                    // Match Blender reference: all channels are normalized [0,1]
                    // floats. Color32 gives bytes (0..255), so we must divide by
                    // 255 for R and G too - previously only B was normalized,
                    // which inflated heights ~255x (e.g. 16000m instead of ~60m).
                    float r = c.r / 255f;
                    float g = c.g / 255f;
                    float b = c.b / 255f;
                    if (b > 0.5f) b -= 1.0039216f;
                    float val = (r + g * 256f + b * 65536f) / scaleFactor;
                    int flippedY = h - py - 1;
                    result[flippedY * w + px] = val;
                }
            }
            return result;
        }

        private static void BilinearStitch(
            TerrainChunk chunk, float[] chunkHeights, int w, int h,
            StitchContext ctx, float[,] result)
        {
            for (int gy = 0; gy < ctx.targetRes; gy++)
            {
                float worldY = ctx.minY + (gy / (float)(ctx.targetRes - 1)) * ctx.worldSizeZ;
                float localY = worldY - chunk.ChunkPos.y;
                if (localY < -0.001f || localY > ctx.chunkSize + 0.001f) continue;
                float pyF = (localY / ctx.chunkSize) * (h - 1);
                int py0 = Mathf.Clamp(Mathf.FloorToInt(pyF), 0, h - 1);
                int py1 = Mathf.Min(py0 + 1, h - 1);
                float pyT = pyF - py0;

                for (int gx = 0; gx < ctx.targetRes; gx++)
                {
                    float worldX = ctx.minX + (gx / (float)(ctx.targetRes - 1)) * ctx.worldSizeX;
                    float localX = worldX - chunk.ChunkPos.x;
                    if (localX < -0.001f || localX > ctx.chunkSize + 0.001f) continue;
                    float pxF = (localX / ctx.chunkSize) * (w - 1);
                    int px0 = Mathf.Clamp(Mathf.FloorToInt(pxF), 0, w - 1);
                    int px1 = Mathf.Min(px0 + 1, w - 1);
                    float pxT = pxF - px0;

                    float h00 = chunkHeights[py0 * w + px0];
                    float h10 = chunkHeights[py0 * w + px1];
                    float h01 = chunkHeights[py1 * w + px0];
                    float h11 = chunkHeights[py1 * w + px1];
                    float h0 = Mathf.Lerp(h00, h10, pxT);
                    float h1 = Mathf.Lerp(h01, h11, pxT);
                    result[gy, gx] = Mathf.Lerp(h0, h1, pyT);
                }
            }
        }

        private static void ComputeHeightMinMax(float[,] heights, out float min, out float max)
        {
            min = float.MaxValue; max = float.MinValue;
            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    float v = heights[y, x];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            if (min > max) { min = 0f; max = 1f; }
        }

        // ====================== SPLAT ======================

        private static void FillChunkSplat(
            TerrainChunk chunk,
            List<TerrainLayerDef> globalLayers,
            Dictionary<string, int> globalLayerIdx,
            float[][,] globalSplat,  // [layerIdx][y, x] = weight
            StitchContext ctx)
        {
            int matched = 0, unmatched = 0, noBlend = 0;
            int totalLayerCount = globalLayers.Count;
            for (int li = 0; li < chunk.Layers.Count; li++)
            {
                var layer = chunk.Layers[li];
                if (!globalLayerIdx.TryGetValue(layer.Name, out int globalIdx))
                    { unmatched++; continue; }
                if (globalIdx >= totalLayerCount) { unmatched++; continue; }

                Texture2D weightTex;
                int weightChannel;
                if (chunk.IsNewBlendFormat)
                {
                    int blendIdx = li / 2;
                    if (blendIdx >= chunk.BlendTextures.Count) { noBlend++; continue; }
                    weightTex = chunk.BlendTextures[blendIdx];
                    weightChannel = (li % 2 == 0) ? 3 : 1;
                }
                else
                {
                    int blendIdx = li;
                    if (blendIdx >= chunk.BlendTextures.Count) { noBlend++; continue; }
                    weightTex = chunk.BlendTextures[blendIdx];
                    weightChannel = 0;
                }

                FillSplatFromWeight(chunk, weightTex, weightChannel,
                                    globalSplat[globalIdx], ctx);
                matched++;
            }
            // Log only the first few chunks so we can see the mapping without
            // flooding the console with 196 lines.
            if (_chunkLogCount < 4)
            {
                WoTLogger.Info($"  chunk {chunk.ChunkName}: layers={chunk.Layers.Count} " +
                               $"blendTex={chunk.BlendTextures.Count} newFmt={chunk.IsNewBlendFormat} " +
                               $"matched={matched} unmatched={unmatched} noBlend={noBlend}");
                if (chunk.Layers.Count > 0)
                {
                    var names = new System.Text.StringBuilder();
                    for (int k = 0; k < chunk.Layers.Count; k++)
                        names.Append($"[{k}]{Path.GetFileName(chunk.Layers[k].Name)} ");
                    WoTLogger.Info($"    layer order: {names}");
                }
                _chunkLogCount++;
            }
        }

        private static int _chunkLogCount = 0;

        private static void FillSplatFromWeight(
            TerrainChunk chunk,
            Texture2D weightTex,
            int weightChannel,
            float[,] splatLayer,        // [y, x] = weight
            StitchContext ctx)
        {
            int w = weightTex.width;
            int h = weightTex.height;
            var pixels = weightTex.GetPixels32();

            for (int gy = 0; gy < ctx.targetRes; gy++)
            {
                float worldY = ctx.minY + (gy / (float)(ctx.targetRes - 1)) * ctx.worldSizeZ;
                float localY = worldY - chunk.ChunkPos.y;
                if (localY < -0.001f || localY > ctx.chunkSize + 0.001f) continue;
                float pyF = (localY / ctx.chunkSize) * (h - 1);
                int py0 = Mathf.Clamp(Mathf.FloorToInt(pyF), 0, h - 1);
                int py1 = Mathf.Min(py0 + 1, h - 1);
                float pyT = pyF - py0;

                for (int gx = 0; gx < ctx.targetRes; gx++)
                {
                    float worldX = ctx.minX + (gx / (float)(ctx.targetRes - 1)) * ctx.worldSizeX;
                    float localX = worldX - chunk.ChunkPos.x;
                    if (localX < -0.001f || localX > ctx.chunkSize + 0.001f) continue;
                    float pxF = (localX / ctx.chunkSize) * (w - 1);
                    int px0 = Mathf.Clamp(Mathf.FloorToInt(pxF), 0, w - 1);
                    int px1 = Mathf.Min(px0 + 1, w - 1);
                    float pxT = pxF - px0;

                    Color32 c00 = pixels[py0 * w + px0];
                    Color32 c10 = pixels[py0 * w + px1];
                    Color32 c01 = pixels[py1 * w + px0];
                    Color32 c11 = pixels[py1 * w + px1];

                    float v = Mathf.Lerp(
                        Mathf.Lerp(ChannelValue(c00, weightChannel), ChannelValue(c10, weightChannel), pxT),
                        Mathf.Lerp(ChannelValue(c01, weightChannel), ChannelValue(c11, weightChannel), pxT),
                        pyT);

                    splatLayer[gy, gx] = Mathf.Max(splatLayer[gy, gx], Mathf.Clamp01(v));
                }
            }
        }

        private static float ChannelValue(Color32 c, int channel)
        {
            switch (channel)
            {
                case 0: return c.r / 255f;
                case 1: return c.g / 255f;
                case 2: return c.b / 255f;
                case 3: return c.a / 255f;
                default: return 0f;
            }
        }

        // ====================== LAYERS ======================

        private static TerrainLayer[] CreateTerrainLayers(
            List<TerrainLayerDef> layers,
            WoTPackageManager resMgr,
            string outputPath)
        {
            var result = new TerrainLayer[layers.Count];
            string texFolder = outputPath + "/Textures";
            string layerFolder = outputPath + "/Layers";
            EnsureFolder(texFolder);
            EnsureFolder(layerFolder);

            int diffuseLoaded = 0, diffuseMissing = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                // Use the index in the asset name so two layers that share a base
                // filename don't overwrite each other's assets.
                string safeBase = $"{i:D2}_" + Path.GetFileNameWithoutExtension(l.Name);
                var tl = new TerrainLayer
                {
                    name = "WoTLayer_" + safeBase,
                    // Sensible default so the terrain isn't a single huge stretched
                    // texel if tileSize ends up 0.
                    tileSize = new Vector2(10f, 10f),
                };

                byte[] diffuseData = resMgr.ReadBytes(l.Name) ?? TryAlternatePaths(resMgr, l.Name);

                if (diffuseData != null)
                {
                    Texture2D diffuseTex = LoadTexture(diffuseData, l.Name, false);
                    if (diffuseTex != null)
                    {
                        string assetPath = texFolder + "/" + safeBase + ".asset";
                        SaveTextureAsset(diffuseTex, assetPath);
                        // Re-load the persisted asset so the TerrainLayer references
                        // a real on-disk asset (important for prefab/TerrainData
                        // serialization - in-memory textures get lost otherwise).
                        var persisted = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                        tl.diffuseTexture = persisted != null ? persisted : diffuseTex;
                        tl.tileSize = ComputeTileSize(l);
                        diffuseLoaded++;
                    }
                    else
                    {
                        diffuseMissing++;
                        WoTLogger.Warn($"Layer diffuse decode failed: {l.Name}");
                    }
                }
                else
                {
                    diffuseMissing++;
                    WoTLogger.Warn($"Layer diffuse NOT FOUND in any pkg: {l.Name}");
                }

                if (!string.IsNullOrEmpty(l.NameNm))
                {
                    byte[] normalData = resMgr.ReadBytes(l.NameNm) ?? TryAlternatePaths(resMgr, l.NameNm);
                    if (normalData != null)
                    {
                        Texture2D normalTex = LoadTexture(normalData, l.NameNm, true);
                        if (normalTex != null)
                        {
                            string assetPath = texFolder + "/" + safeBase + "_nm.asset";
                            SaveTextureAsset(normalTex, assetPath);
                            var persistedNm = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                            tl.normalMapTexture = persistedNm != null ? persistedNm : normalTex;
                        }
                    }
                }

                // CRITICAL: persist the TerrainLayer itself as an asset. Unassigned
                // (in-memory) TerrainLayers don't survive TerrainData/prefab
                // serialization, which makes the terrain render with no textures.
                string layerPath = layerFolder + "/" + safeBase + ".terrainlayer";
                var existingLayer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
                if (existingLayer != null) AssetDatabase.DeleteAsset(layerPath);
                AssetDatabase.CreateAsset(tl, layerPath);

                result[i] = tl;
            }
            AssetDatabase.SaveAssets();
            WoTLogger.Info($"Terrain layers: {diffuseLoaded} diffuse loaded, {diffuseMissing} missing (of {layers.Count})");
            return result;
        }

        /// <summary>
        /// WoT layer names sometimes don't resolve directly. Try a few common
        /// variants used across client versions.
        /// </summary>
        private static byte[] TryAlternatePaths(WoTPackageManager resMgr, string name)
        {
            string n = name.Replace('\\', '/').ToLowerInvariant();

            var candidates = new List<string>();
            // Strip/normalize leading folders.
            if (!n.StartsWith("content/")) candidates.Add("content/" + n);
            if (n.StartsWith("/")) candidates.Add(n.TrimStart('/'));
            // Some references omit the extension.
            if (!n.EndsWith(".dds")) candidates.Add(n + ".dds");
            // Bare filename in any pkg.
            candidates.Add(Path.GetFileName(n));

            foreach (var c in candidates)
            {
                var data = resMgr.ReadBytes(c);
                if (data != null)
                {
                    WoTLogger.Info($"Resolved layer texture via alternate path: '{name}' -> '{c}'");
                    return data;
                }
            }
            return null;
        }

        /// <summary>
        /// Compute tile size from UV projection. U/V projection define how the
        /// texture is mapped onto the terrain (1.0 = one full texture per chunk).
        /// </summary>
        private static Vector2 ComputeTileSize(TerrainLayerDef l)
        {
            // WoT terrain layer UV-projection vectors give tiles-per-metre along
            // the U/V axes. tileSize (metres per tile) = 1 / projectionLength.
            // The previous code used chunkSize/len which produced absurd tiling
            // (rainbow noise). We also clamp to a sane range.
            float uLen = new Vector2(l.UProjection.x, l.UProjection.y).magnitude;
            float vLen = new Vector2(l.VProjection.x, l.VProjection.y).magnitude;

            float uSize = uLen > 1e-5f ? 1f / uLen : 10f;
            float vSize = vLen > 1e-5f ? 1f / vLen : 10f;

            // Reasonable terrain texture tiling is ~1..200 m per tile.
            uSize = Mathf.Clamp(uSize, 1f, 200f);
            vSize = Mathf.Clamp(vSize, 1f, 200f);

            WoTLogger.Info($"  tileSize for {Path.GetFileName(l.Name)}: " +
                           $"uProj={l.UProjection} vProj={l.VProjection} -> ({uSize:F2}, {vSize:F2}) m");
            return new Vector2(uSize, vSize);
        }

        private static Texture2D LoadTexture(byte[] data, string name, bool isNormal)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".dds")
            {
                try
                {
                    if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == DdsDecoder.MAGIC)
                        // Diffuse = sRGB (linear:false); normals = linear (true).
                        return DdsDecoder.Read(data, Path.GetFileNameWithoutExtension(name), isNormal);
                }
                catch (Exception e)
                {
                    WoTLogger.Warn($"DDS load failed ({name}): {e.Message}");
                }
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, isNormal)
            {
                name = Path.GetFileNameWithoutExtension(name),
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            if (tex.LoadImage(data, false))
            {
                // Mark as normal map if needed (UnityEngine sets the import
                // type via textureType when importing as asset; runtime
                // textures can't toggle that, so we just return as-is).
                return tex;
            }
            UnityEngine.Object.DestroyImmediate(tex);
            return null;
        }

        private static void SaveTextureAsset(Texture2D tex, string assetPath)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (existing != null)
                AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(tex, assetPath);
        }

        // ====================== UTILS ======================

        /// <summary>
        /// Largest valid Unity heightmap resolution (of the form 2^k + 1) that is
        /// less than or equal to <paramref name="max"/>. Unity only accepts
        /// 33, 65, 129, 257, 513, 1025, 2049, 4097.
        /// </summary>
        public static int LargestPow2Plus1(int max)
        {
            if (max < 33) return 33;
            int n = 33;
            while (((n - 1) * 2 + 1) <= max && n < 4097)
                n = (n - 1) * 2 + 1;
            return n;
        }

        /// <summary>
        /// Smallest valid Unity heightmap resolution (2^k + 1) that is greater
        /// than or equal to <paramref name="min"/>, capped at 4097.
        /// </summary>
        public static int LargestPow2Plus1Ceil(int min)
        {
            int n = 33;
            while ((n - 1) < min && n < 4097)
                n = (n - 1) * 2 + 1;
            return Mathf.Min(n, 4097);
        }

        public static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = Path.GetDirectoryName(folderPath).Replace('\\', '/');
            string leaf = Path.GetFileName(folderPath);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
