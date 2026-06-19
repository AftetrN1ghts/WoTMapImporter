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

            // Convert to [numLayers, res, res] for Unity API
            var splatForUnity = new float[totalLayerCount, targetRes, targetRes];
            for (int li = 0; li < totalLayerCount; li++)
                for (int y = 0; y < targetRes; y++)
                    for (int x = 0; x < targetRes; x++)
                        splatForUnity[li, y, x] = globalSplat[li][y, x];

            // ---- Create TerrainData ----
            float heightRange = ComputeHeightRange(heights);
            var td = new TerrainData
            {
                heightmapResolution = targetRes,
                alphamapResolution = targetRes,
                baseMapResolution = targetRes,
                size = new Vector3(terrain.TotalSizeX, heightRange * 1.2f, terrain.TotalSizeZ),
                name = mapInfo.Name + "_TerrainData",
            };
            td.SetHeights(0, 0, heights);

            if (totalLayerCount > 0)
            {
                td.terrainLayers = CreateTerrainLayers(globalLayers, resMgr, outputPath);
                // SetAlphamaps expects float[splatCount][y,x]. The simplest case is one
                // tile with one channel per pixel. With multiple layers we'll use a
                // simplified approach: distribute layers evenly across tiles.
                td.SetAlphamaps(0, 0, splatForUnity);
            }

            // ---- Save assets ----
            EnsureFolder(outputPath);
            string assetPath = outputPath + "/" + td.name + ".asset";
            AssetDatabase.CreateAsset(td, assetPath);

            var go = UnityEngine.Terrain.CreateTerrainGameObject(td);
            go.name = mapInfo.Name + "_Terrain";
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
                    float b = c.b / 255f;
                    if (b > 0.5f) b -= 1.0039216f;
                    float val = (c.r + c.g * 256f + b * 65536f) / scaleFactor;
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

        private static float ComputeHeightRange(float[,] heights)
        {
            float min = float.MaxValue, max = float.MinValue;
            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    float v = heights[y, x];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            float range = max - min;
            return Mathf.Max(range, 1f);
        }

        // ====================== SPLAT ======================

        private static void FillChunkSplat(
            TerrainChunk chunk,
            List<TerrainLayerDef> globalLayers,
            Dictionary<string, int> globalLayerIdx,
            float[][,] globalSplat,  // [layerIdx][y, x] = weight
            StitchContext ctx)
        {
            int totalLayerCount = globalLayers.Count;
            for (int li = 0; li < chunk.Layers.Count; li++)
            {
                var layer = chunk.Layers[li];
                if (!globalLayerIdx.TryGetValue(layer.Name, out int globalIdx))
                    continue;
                if (globalIdx >= totalLayerCount) continue;

                Texture2D weightTex;
                int weightChannel;
                if (chunk.IsNewBlendFormat)
                {
                    int blendIdx = li / 2;
                    if (blendIdx >= chunk.BlendTextures.Count) continue;
                    weightTex = chunk.BlendTextures[blendIdx];
                    weightChannel = (li % 2 == 0) ? 3 : 1;
                }
                else
                {
                    int blendIdx = li;
                    if (blendIdx >= chunk.BlendTextures.Count) continue;
                    weightTex = chunk.BlendTextures[blendIdx];
                    weightChannel = 0;
                }

                FillSplatFromWeight(chunk, weightTex, weightChannel,
                                    globalSplat[globalIdx], ctx);
            }
        }

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
            EnsureFolder(texFolder);

            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                var tl = new TerrainLayer
                {
                    name = "WoTLayer_" + Path.GetFileNameWithoutExtension(l.Name),
                };

                byte[] diffuseData = resMgr.ReadBytes(l.Name);
                if (diffuseData != null)
                {
                    Texture2D diffuseTex = LoadTexture(diffuseData, l.Name, false);
                    if (diffuseTex != null)
                    {
                        string assetPath = texFolder + "/" + Path.GetFileNameWithoutExtension(l.Name) + ".asset";
                        SaveTextureAsset(diffuseTex, assetPath);
                        tl.diffuseTexture = diffuseTex;
                        tl.tileSize = ComputeTileSize(l);
                    }
                }

                if (!string.IsNullOrEmpty(l.NameNm))
                {
                    byte[] normalData = resMgr.ReadBytes(l.NameNm);
                    if (normalData != null)
                    {
                        Texture2D normalTex = LoadTexture(normalData, l.NameNm, true);
                        if (normalTex != null)
                        {
                            string assetPath = texFolder + "/" + Path.GetFileNameWithoutExtension(l.NameNm) + ".asset";
                            SaveTextureAsset(normalTex, assetPath);
                            tl.normalMapTexture = normalTex;
                        }
                    }
                }
                result[i] = tl;
            }
            return result;
        }

        /// <summary>
        /// Compute tile size from UV projection. U/V projection define how the
        /// texture is mapped onto the terrain (1.0 = one full texture per chunk).
        /// </summary>
        private static Vector2 ComputeTileSize(TerrainLayerDef l)
        {
            // U/V projection vectors: their length represents how often the
            // texture tiles across a unit (chunk) of terrain.
            float ux = l.UProjection.x, uy = l.UProjection.y;
            float vx = l.VProjection.x, vy = l.VProjection.y;
            float uLen = Mathf.Sqrt(ux * ux + uy * uy);
            float vLen = Mathf.Sqrt(vx * vx + vy * vy);
            if (uLen < 0.001f) uLen = 0.1f;
            if (vLen < 0.001f) vLen = 0.1f;
            // Default chunk is 100m. Tile size = chunkSize / projection length.
            float chunkSize = 100f;
            return new Vector2(chunkSize / uLen, chunkSize / vLen);
        }

        private static Texture2D LoadTexture(byte[] data, string name, bool isNormal)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == ".dds")
            {
                try
                {
                    if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == DdsDecoder.MAGIC)
                        return DdsDecoder.Read(data, name);
                }
                catch (Exception e)
                {
                    WoTLogger.Warn($"DDS load failed ({name}): {e.Message}");
                }
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
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

        public static int LargestPow2Plus1(int max)
        {
            int n = 33;
            while ((n - 1) > max) n = (n - 1) / 2 + 1;
            return n;
        }

        public static int LargestPow2Plus1Ceil(int min)
        {
            int n = 33;
            while (n - 1 < min) n = (n - 1) * 2 + 1;
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
