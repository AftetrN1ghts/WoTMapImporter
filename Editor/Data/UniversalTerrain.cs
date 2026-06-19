using System.Collections.Generic;
using UnityEngine;

namespace WoTMapImporter.Editor.Data
{
    /// <summary>
    /// Parsed terrain metadata for a map (similar to Simi4/WoT-Blender-Addons
    /// UniversalTerrain). Bounds are in chunk coordinates; chunk_size is in
    /// metres. globalMap is the optional wetness/roughness AM texture path.
    /// </summary>
    public class UniversalTerrain
    {
        public float ChunkSize = 100f;
        public int MinX, MaxX, MinY, MaxY;
        public string GlobalMap;

        public int NumChunksX => MaxX - MinX + 1;
        public int NumChunksY => MaxY - MinY + 1;

        public float TotalSizeX => NumChunksX * ChunkSize;
        public float TotalSizeZ => NumChunksY * ChunkSize;
    }

    /// <summary>
    /// One tileable texture layer (grass, dirt, etc.) for terrain chunks.
    /// Mirrors Layer dataclass from Simi4/WoT-Blender-Addons/terrain_loader.py.
    /// </summary>
    public class TerrainLayerDef
    {
        public string Name;             // diffuse texture path, e.g. content/textures/grass.dds
        public string NameNm;           // optional normal map path
        public Vector4 UProjection;     // 4 floats, last is offset
        public Vector4 VProjection;
        public Vector4 Row0, Row1, Row2; // only for new blend format
    }

    /// <summary>
    /// One terrain chunk (matches a *.cdata file).
    /// </summary>
    public class TerrainChunk
    {
        public string ChunkName;        // 8 hex chars, e.g. "0a0b0c0d"
        public Vector2 ChunkPos;        // world position (metres)
        public Texture2D HeightsTex;    // PNG with encoded height in RGB channels
        public Texture2D NormalsTex;    // PNG/DDS normals (may be null)
        public List<Texture2D> BlendTextures;  // new format: DXT5 DDS blend maps (R/G/B/A = weights)
        public List<TerrainLayerDef> Layers;
        public bool IsNewBlendFormat;
    }
}
