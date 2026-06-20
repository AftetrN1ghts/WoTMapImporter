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
            // Root object that contains all terrain chunks. Kept in TerrainObject
            // for backward compatibility with the previous single-terrain API.
            public GameObject TerrainObject;
            public TerrainData TerrainData; // first chunk TerrainData, for compatibility
            public List<GameObject> TerrainObjects = new List<GameObject>();
            public List<TerrainData> TerrainDatas = new List<TerrainData>();
            public List<string> Warnings = new List<string>();
        }

        public struct StitchContext
        {
            public int targetRes;
            public float minX, minY;
            public float worldSizeX, worldSizeZ;
            public float chunkSize;
        }

        /// <summary>
        /// Builds terrain as separate Unity Terrain chunks, one chunk per *.cdata.
        /// This follows the Blender addon's behaviour: it does NOT stitch all cdata
        /// into one giant terrain.  Per-chunk building also fixes splat painting
        /// because blend_textures are local to each chunk and layer order is local.
        ///
        /// Blender reference for new blend format:
        ///   blend_texture[i].A -> layer[i*2]
        ///   blend_texture[i].G -> layer[i*2+1]
        /// and the blend texture V coordinate is flipped by Mapping Scale (1, -1, 1).
        /// </summary>
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

            EnsureFolder(outputPath);
            EnsureFolder(outputPath + "/TerrainData");

            // Sort so prefab hierarchy is deterministic.
            chunks.Sort((a, b) =>
            {
                int by = a.ChunkPos.y.CompareTo(b.ChunkPos.y);
                return by != 0 ? by : a.ChunkPos.x.CompareTo(b.ChunkPos.x);
            });

            // Use one vertical range for all chunks, otherwise neighbouring Terrain
            // objects would have different size.y and would not line up.
            ComputeGlobalHeightRange(chunks, out float hMin, out float hMax);
            float heightRange = Mathf.Max(hMax - hMin, 1f);
            float sizeY = heightRange * 1.05f;

            WoTLogger.Info($"Building CHUNKED terrain: {chunks.Count} chunks, grid {terrain.NumChunksX}x{terrain.NumChunksY}, " +
                           $"chunkSize={terrain.ChunkSize:F1}m, height min={hMin:F1} max={hMax:F1} sizeY={sizeY:F1}");

            var result = new BuildResult();
            var root = new GameObject(mapInfo.Name + "_TerrainChunks");
            result.TerrainObject = root;

            var terrainByCoord = new Dictionary<Vector2Int, UnityEngine.Terrain>();
            Texture2D globalMapTex = LoadGlobalMapTexture(terrain, resMgr, outputPath);

            for (int ci = 0; ci < chunks.Count; ci++)
            {
                var chunk = chunks[ci];
                if (chunk.HeightsTex == null || chunk.Layers == null || chunk.Layers.Count == 0 ||
                    chunk.BlendTextures == null || chunk.BlendTextures.Count == 0)
                {
                    result.Warnings.Add($"Chunk {chunk.ChunkName}: missing heights/layers/blends, skipped");
                    continue;
                }

                int srcHeightRes = chunk.HeightsTex.width;
                int heightRes = Mathf.Min(LargestPow2Plus1Ceil(srcHeightRes), maxResolution);
                heightRes = Mathf.Max(33, heightRes);

                float[,] heights = BuildLocalHeights(chunk, heightRes, hMin, sizeY);
                int alphaRes = GuessChunkAlphamapResolution(chunk, heightRes);
                float[,,] alphamaps = BuildLocalAlphamaps(chunk, alphaRes);
                // Raw WoT control maps are NOT normalized.  Blender uses the exact
                // A/G weights from terrain2/blend_textures in a sum chain. Unity
                // TerrainData.SetAlphamaps normalizes weights, so those textures
                // cannot be used for WoT rendering; they are only kept as fallback.
                Texture2D[] rawControls = BuildRawControlTextures(chunk, alphaRes);
                rawControls = SaveRawControlTextures(rawControls, outputPath, mapInfo.Name, chunk.ChunkName);

                var td = new TerrainData
                {
                    heightmapResolution = heightRes,
                    alphamapResolution = alphaRes,
                    baseMapResolution = Mathf.Min(1024, alphaRes),
                    size = new Vector3(terrain.ChunkSize, sizeY, terrain.ChunkSize),
                    name = mapInfo.Name + "_" + chunk.ChunkName + "_TerrainData",
                };

                td.SetHeights(0, 0, heights);
                td.terrainLayers = CreateTerrainLayers(chunk.Layers, resMgr, outputPath, chunk.ChunkName + "_");
                td.SetAlphamaps(0, 0, alphamaps);
                td.SetBaseMapDirty();
                EditorUtility.SetDirty(td);

                string tdPath = outputPath + "/TerrainData/" + td.name + ".asset";
                var oldTd = AssetDatabase.LoadAssetAtPath<TerrainData>(tdPath);
                if (oldTd != null) AssetDatabase.DeleteAsset(tdPath);
                AssetDatabase.CreateAsset(td, tdPath);

                var go = UnityEngine.Terrain.CreateTerrainGameObject(td);
                go.name = chunk.ChunkName + "_Terrain";
                // Heights were normalized as (worldHeight - hMin) / sizeY, so the
                // Terrain object must be shifted down to hMin to restore WoT height.
                go.transform.position = new Vector3(chunk.ChunkPos.x, hMin, chunk.ChunkPos.y);
                go.transform.SetParent(root.transform, true);

                var tc = go.GetComponent<UnityEngine.Terrain>();
                if (tc != null)
                {
                    BindTerrainMaterial(tc, td, chunk.Layers, terrain, outputPath, mapInfo.Name, chunk.ChunkName, rawControls, globalMapTex);
                    tc.basemapDistance = 100000f;
                    tc.Flush();

                    int hx = Mathf.RoundToInt(chunk.ChunkPos.x / terrain.ChunkSize);
                    int hy = Mathf.RoundToInt(chunk.ChunkPos.y / terrain.ChunkSize);
                    terrainByCoord[new Vector2Int(hx, hy)] = tc;
                }

                result.TerrainObjects.Add(go);
                result.TerrainDatas.Add(td);
                if (result.TerrainData == null) result.TerrainData = td;

                if (ci < 6)
                    LogChunkAlphamapStats(chunk, alphamaps);
            }

            StitchTerrainNeighbours(terrainByCoord);

            AssetDatabase.SaveAssets();
            string prefabPath = outputPath + "/" + root.name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

            WoTLogger.Info($"Chunked terrain built: {result.TerrainObjects.Count}/{chunks.Count} Terrain chunks");
            return result;
        }

        private static void ComputeGlobalHeightRange(List<TerrainChunk> chunks, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            foreach (var chunk in chunks)
            {
                if (chunk.HeightsTex == null) continue;
                var pixels = chunk.HeightsTex.GetPixels32();
                var decoded = DecodeHeightPixels(pixels, chunk.HeightsTex.width, chunk.HeightsTex.height);
                for (int i = 0; i < decoded.Length; i++)
                {
                    float v = decoded[i];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            if (min > max) { min = 0f; max = 1f; }
        }

        private static float[,] BuildLocalHeights(TerrainChunk chunk, int targetRes, float hMin, float sizeY)
        {
            int w = chunk.HeightsTex.width;
            int h = chunk.HeightsTex.height;
            var src = DecodeHeightPixels(chunk.HeightsTex.GetPixels32(), w, h);
            var dst = new float[targetRes, targetRes];

            for (int y = 0; y < targetRes; y++)
            {
                float pyF = (y / (float)(targetRes - 1)) * (h - 1);
                int py0 = Mathf.Clamp(Mathf.FloorToInt(pyF), 0, h - 1);
                int py1 = Mathf.Min(py0 + 1, h - 1);
                float pyT = pyF - py0;
                for (int x = 0; x < targetRes; x++)
                {
                    float pxF = (x / (float)(targetRes - 1)) * (w - 1);
                    int px0 = Mathf.Clamp(Mathf.FloorToInt(pxF), 0, w - 1);
                    int px1 = Mathf.Min(px0 + 1, w - 1);
                    float pxT = pxF - px0;

                    float h00 = src[py0 * w + px0];
                    float h10 = src[py0 * w + px1];
                    float h01 = src[py1 * w + px0];
                    float h11 = src[py1 * w + px1];
                    float hm = Mathf.Lerp(Mathf.Lerp(h00, h10, pxT), Mathf.Lerp(h01, h11, pxT), pyT);
                    dst[y, x] = Mathf.Clamp01((hm - hMin) / sizeY);
                }
            }
            return dst;
        }

        private static int GuessChunkAlphamapResolution(TerrainChunk chunk, int fallback)
        {
            int res = fallback;
            if (chunk.BlendTextures != null && chunk.BlendTextures.Count > 0 && chunk.BlendTextures[0] != null)
                res = Mathf.Max(chunk.BlendTextures[0].width, chunk.BlendTextures[0].height);
            return Mathf.Clamp(res, 16, 2048);
        }

        private static float[,,] BuildLocalAlphamaps(TerrainChunk chunk, int alphaRes)
        {
            int layerCount = Mathf.Max(1, chunk.Layers.Count);
            var maps = new float[alphaRes, alphaRes, layerCount];

            for (int li = 0; li < chunk.Layers.Count; li++)
            {
                if (!TryGetBlendSource(chunk, li, out Texture2D weightTex, out int channel))
                    continue;
                if (weightTex == null) continue;

                var pixels = weightTex.GetPixels32();
                int w = weightTex.width;
                int h = weightTex.height;

                for (int y = 0; y < alphaRes; y++)
                {
                    float v = alphaRes == 1 ? 0f : y / (float)(alphaRes - 1);
                    // Match Blender terrain_loader.py: blend texture is sampled via
                    // Mapping Scale (1, -1, 1).  In local terrain UV this is 1-v.
                    float pyF = (1f - v) * (h - 1);
                    int py0 = Mathf.Clamp(Mathf.FloorToInt(pyF), 0, h - 1);
                    int py1 = Mathf.Min(py0 + 1, h - 1);
                    float pyT = pyF - py0;
                    for (int x = 0; x < alphaRes; x++)
                    {
                        float u = alphaRes == 1 ? 0f : x / (float)(alphaRes - 1);
                        float pxF = u * (w - 1);
                        int px0 = Mathf.Clamp(Mathf.FloorToInt(pxF), 0, w - 1);
                        int px1 = Mathf.Min(px0 + 1, w - 1);
                        float pxT = pxF - px0;

                        Color32 c00 = pixels[py0 * w + px0];
                        Color32 c10 = pixels[py0 * w + px1];
                        Color32 c01 = pixels[py1 * w + px0];
                        Color32 c11 = pixels[py1 * w + px1];
                        float weight = Mathf.Lerp(
                            Mathf.Lerp(ChannelValue(c00, channel), ChannelValue(c10, channel), pxT),
                            Mathf.Lerp(ChannelValue(c01, channel), ChannelValue(c11, channel), pxT),
                            pyT);
                        maps[y, x, li] = Mathf.Clamp01(weight);
                    }
                }
            }

            // Unity wants weights per pixel normalized to sum=1.
            for (int y = 0; y < alphaRes; y++)
            {
                for (int x = 0; x < alphaRes; x++)
                {
                    float sum = 0f;
                    for (int li = 0; li < layerCount; li++) sum += maps[y, x, li];
                    if (sum > 1e-6f)
                    {
                        for (int li = 0; li < layerCount; li++) maps[y, x, li] /= sum;
                    }
                    else
                    {
                        maps[y, x, 0] = 1f;
                    }
                }
            }

            return maps;
        }

        private static Texture2D[] BuildRawControlTextures(TerrainChunk chunk, int alphaRes)
        {
            int layerCount = Mathf.Max(1, chunk.Layers.Count);
            int controlCount = Mathf.Clamp((layerCount + 3) / 4, 1, 4);
            var pixels = new Color32[controlCount][];
            for (int i = 0; i < controlCount; i++)
                pixels[i] = new Color32[alphaRes * alphaRes];

            for (int li = 0; li < chunk.Layers.Count && li < 16; li++)
            {
                if (!TryGetBlendSource(chunk, li, out Texture2D weightTex, out int channel))
                    continue;
                if (weightTex == null) continue;

                var srcPixels = weightTex.GetPixels32();
                int w = weightTex.width;
                int h = weightTex.height;
                int ci = li / 4;
                int ch = li % 4;

                for (int y = 0; y < alphaRes; y++)
                {
                    float v = alphaRes == 1 ? 0f : y / (float)(alphaRes - 1);
                    // Blender mapping_node Scale=(1,-1,1), so blend V is flipped.
                    float pyF = (1f - v) * (h - 1);
                    int py0 = Mathf.Clamp(Mathf.FloorToInt(pyF), 0, h - 1);
                    int py1 = Mathf.Min(py0 + 1, h - 1);
                    float pyT = pyF - py0;

                    for (int x = 0; x < alphaRes; x++)
                    {
                        float u = alphaRes == 1 ? 0f : x / (float)(alphaRes - 1);
                        float pxF = u * (w - 1);
                        int px0 = Mathf.Clamp(Mathf.FloorToInt(pxF), 0, w - 1);
                        int px1 = Mathf.Min(px0 + 1, w - 1);
                        float pxT = pxF - px0;

                        Color32 c00 = srcPixels[py0 * w + px0];
                        Color32 c10 = srcPixels[py0 * w + px1];
                        Color32 c01 = srcPixels[py1 * w + px0];
                        Color32 c11 = srcPixels[py1 * w + px1];
                        float weight = Mathf.Lerp(
                            Mathf.Lerp(ChannelValue(c00, channel), ChannelValue(c10, channel), pxT),
                            Mathf.Lerp(ChannelValue(c01, channel), ChannelValue(c11, channel), pxT),
                            pyT);
                        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(weight * 255f), 0, 255);

                        int pi = y * alphaRes + x;
                        var dst = pixels[ci][pi];
                        switch (ch)
                        {
                            case 0: dst.r = b; break;
                            case 1: dst.g = b; break;
                            case 2: dst.b = b; break;
                            case 3: dst.a = b; break;
                        }
                        pixels[ci][pi] = dst;
                    }
                }
            }

            var textures = new Texture2D[controlCount];
            for (int ci = 0; ci < controlCount; ci++)
            {
                var tex = new Texture2D(alphaRes, alphaRes, TextureFormat.RGBA32, false, true)
                {
                    name = chunk.ChunkName + "_WoTRawControl" + ci,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                tex.SetPixels32(pixels[ci]);
                tex.Apply(false, false);
                textures[ci] = tex;
            }
            return textures;
        }

        private static Texture2D[] SaveRawControlTextures(Texture2D[] controls, string outputPath, string mapName, string chunkName)
        {
            if (controls == null) return null;
            string folder = outputPath + "/Controls";
            EnsureFolder(folder);
            for (int i = 0; i < controls.Length; i++)
            {
                if (controls[i] == null) continue;
                controls[i].name = mapName + "_" + chunkName + "_RawControl" + i;
                string path = folder + "/" + controls[i].name + ".asset";
                var old = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (old != null) AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(controls[i], path);
                var persisted = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (persisted != null) controls[i] = persisted;
            }
            return controls;
        }

        private static bool TryGetBlendSource(TerrainChunk chunk, int layerIndex, out Texture2D tex, out int channel)
        {
            tex = null;
            channel = 0;
            if (chunk.IsNewBlendFormat)
            {
                int blendIdx = layerIndex / 2;
                if (blendIdx >= chunk.BlendTextures.Count) return false;
                tex = chunk.BlendTextures[blendIdx];
                // Blender: Alpha for even layers, Green for odd layers.
                channel = (layerIndex % 2 == 0) ? 3 : 1;
                return true;
            }
            else
            {
                if (layerIndex >= chunk.BlendTextures.Count) return false;
                tex = chunk.BlendTextures[layerIndex];
                // Old blend was RGB color mask. Unity alphamap is scalar; Red is
                // the closest equivalent to the previous importer and works for
                // grayscale masks.
                channel = 0;
                return true;
            }
        }

        private static Texture2D LoadGlobalMapTexture(UniversalTerrain terrain, WoTPackageManager resMgr, string outputPath)
        {
            if (terrain == null || string.IsNullOrEmpty(terrain.GlobalMap))
                return null;

            byte[] data = resMgr.ReadBytes(terrain.GlobalMap) ?? TryAlternatePaths(resMgr, terrain.GlobalMap);
            if (data == null)
            {
                WoTLogger.Warn($"Global terrain AM map not found: {terrain.GlobalMap}");
                return null;
            }

            // Blender sets global_AM colorspace to Non-Color, so load linear=true.
            Texture2D tex = LoadTexture(data, terrain.GlobalMap, true);
            if (tex == null)
            {
                WoTLogger.Warn($"Global terrain AM decode failed: {terrain.GlobalMap}");
                return null;
            }

            string folder = outputPath + "/Textures";
            EnsureFolder(folder);
            tex.name = "WoT_GlobalAM_" + Path.GetFileNameWithoutExtension(terrain.GlobalMap);
            string path = folder + "/" + tex.name + ".asset";
            var old = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (old != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(tex, path);
            var persisted = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            WoTLogger.Info($"Loaded global terrain AM map: {terrain.GlobalMap}");
            return persisted != null ? persisted : tex;
        }

        private static void BindTerrainMaterial(UnityEngine.Terrain terrainComp, TerrainData td, List<TerrainLayerDef> layerDefs, UniversalTerrain terrainInfo, string outputPath, string mapName, string chunkName, Texture2D[] rawControls, Texture2D globalMapTex)
        {
            var shader = Shader.Find("WoT/TerrainMultilayer");
            if (shader == null)
            {
                var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                         ?? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
                var pipelineMat = rp != null ? rp.defaultTerrainMaterial : null;
                if (pipelineMat != null) terrainComp.materialTemplate = pipelineMat;
                WoTLogger.Warn("WoT/TerrainMultilayer shader not found; using pipeline default terrain material");
                return;
            }

            var mat = new Material(shader) { name = mapName + "_" + chunkName + "_TerrainMat" };
            int layerCount = td.terrainLayers != null ? td.terrainLayers.Length : 0;
            mat.SetFloat("_NumLayers", Mathf.Min(layerCount, 16));

            // Use raw WoT control textures, not TerrainData.alphamapTextures.
            // TerrainData alphamaps are normalized by Unity, while WoT/Blender uses
            // original non-normalized blend weights.
            var controlTextures = rawControls != null && rawControls.Length > 0 ? rawControls : td.alphamapTextures;
            for (int i = 0; i < controlTextures.Length && i < 4; i++)
                mat.SetTexture("_Control" + i, controlTextures[i]);

            if (globalMapTex != null)
            {
                mat.SetTexture("_GlobalMap", globalMapTex);
                mat.SetFloat("_UseGlobalMap", 1f);
                mat.SetVector("_GlobalMap_ST", new Vector4(1f, 1f, 0f, 0f));
                mat.SetVector("_TerrainGlobal", new Vector4(
                    terrainInfo.MinX * terrainInfo.ChunkSize,
                    terrainInfo.MinY * terrainInfo.ChunkSize,
                    Mathf.Max(terrainInfo.TotalSizeX, 1f),
                    Mathf.Max(terrainInfo.TotalSizeZ, 1f)));
            }
            else
            {
                mat.SetFloat("_UseGlobalMap", 0f);
            }

            var tlayers = td.terrainLayers;
            var layerU = new Vector4[16];
            var layerV = new Vector4[16];
            for (int li = 0; tlayers != null && li < tlayers.Length && li < 16; li++)
            {
                mat.SetTexture("_Splat" + li, tlayers[li].diffuseTexture);

                // Keep _ST as identity; WoT tile UVs are calculated in the shader
                // from world position and the original BigWorld u/v projections.
                mat.SetVector("_Splat" + li + "_ST", new Vector4(1f, 1f, 0f, 0f));

                // TerrainData only stores Unity TerrainLayer assets, so use the
                // parallel chunk.Layers list passed into this method.
            }
            for (int li = 0; layerDefs != null && li < layerDefs.Count && li < 16; li++)
            {
                layerU[li] = layerDefs[li].UProjection;
                layerV[li] = layerDefs[li].VProjection;
            }
            // Fill unused slots with a harmless 1:1 mapping to avoid undefined data.
            for (int li = layerDefs != null ? Mathf.Min(layerDefs.Count, 16) : 0; li < 16; li++)
            {
                layerU[li] = new Vector4(1f, 0f, 0f, 0f);
                layerV[li] = new Vector4(0f, 0f, 1f, 0f);
            }
            mat.SetVectorArray("_LayerU", layerU);
            mat.SetVectorArray("_LayerV", layerV);

            terrainComp.materialTemplate = mat;
            string matFolder = outputPath + "/Materials";
            EnsureFolder(matFolder);
            string matPath = matFolder + "/" + mat.name + ".mat";
            var old = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (old != null) AssetDatabase.DeleteAsset(matPath);
            AssetDatabase.CreateAsset(mat, matPath);
        }

        private static void StitchTerrainNeighbours(Dictionary<Vector2Int, UnityEngine.Terrain> terrains)
        {
            foreach (var kv in terrains)
            {
                var c = kv.Key;
                terrains.TryGetValue(new Vector2Int(c.x - 1, c.y), out var left);
                terrains.TryGetValue(new Vector2Int(c.x, c.y + 1), out var top);
                terrains.TryGetValue(new Vector2Int(c.x + 1, c.y), out var right);
                terrains.TryGetValue(new Vector2Int(c.x, c.y - 1), out var bottom);
                kv.Value.SetNeighbors(left, top, right, bottom);
            }
        }

        private static void LogChunkAlphamapStats(TerrainChunk chunk, float[,,] maps)
        {
            int h = maps.GetLength(0), w = maps.GetLength(1), l = maps.GetLength(2);
            var sb = new System.Text.StringBuilder();
            for (int li = 0; li < l; li++)
            {
                double sum = 0;
                float max = 0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float v = maps[y, x, li];
                        sum += v;
                        if (v > max) max = v;
                    }
                sb.Append($"{li}:{sum:F0}/{max:F2} ");
            }
            WoTLogger.Info($"  chunk {chunk.ChunkName}: alphamap {w}x{h} layers={l} weights {sb}");
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
            string outputPath,
            string assetPrefix = "")
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
                string safeBase = assetPrefix + $"{i:D2}_" + Path.GetFileNameWithoutExtension(l.Name);
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
