using System;
using System.Collections.Generic;
using System.IO;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Package
{
    /// <summary>
    /// Minimal parser for WoT's compiled "space.bin" (CompiledSpace) format.
    /// Ported from Simi4/WoT-Blender-Addons map_viewer/compiled_space, but only
    /// the parts needed to place static models:
    ///   - BWTB   : root table (section directory)
    ///   - BWST   : string table (fnv hash -> string)
    ///   - BSMI   : static model instances (transforms + model ids)
    ///   - BSMO   : static models (lod -> renders -> prims/verts names)
    ///   - BSMA   : static materials (-> diffuse texture name)
    ///
    /// Layout primitives (see _base_json_section.py):
    ///   list  field: [u32 elemSize][u32 count][count * elemSize bytes]
    ///   dict  field: [u32 elemSize][elemSize bytes]
    /// </summary>
    public sealed class CompiledSpace
    {
        public class ModelInstance
        {
            public string PrimsName;     // e.g. "content/.../foo.primitives_processed"
            public string VertsDataName; // section name inside the primitives file
            public string PrimsDataName;
            public int PrimitiveGroup;   // primitiveGroup index for this render set
            public string DiffuseTexture;
            public float[] Transform;    // 16 floats, row-major (WoT)
        }

        public readonly List<ModelInstance> Models = new List<ModelInstance>();

        private struct Row { public string Header; public uint Position; public uint Length; }

        // ---- public entry ----

        public static CompiledSpace Parse(byte[] bin)
        {
            var cs = new CompiledSpace();
            try { cs.ParseInternal(bin); }
            catch (Exception e)
            {
                WoTLogger.Error($"CompiledSpace parse failed: {e.Message}\n{e.StackTrace}");
            }
            return cs;
        }

        private void ParseInternal(byte[] bin)
        {
            using var ms = new MemoryStream(bin, false);
            using var br = new BinaryReader(ms);

            var rows = ReadBwtb(br);

            // String table first (everything references it by fnv hash).
            var strings = new Dictionary<uint, string>();
            if (rows.TryGetValue("BWST", out var bwstRow))
                strings = ReadBwst(br, bwstRow);

            if (!rows.TryGetValue("BSMI", out var bsmiRow) ||
                !rows.TryGetValue("BSMO", out var bsmoRow))
            {
                WoTLogger.Warn("space.bin has no BSMI/BSMO sections; no static models to load");
                return;
            }

            var bsmi = ReadBsmi(br, bsmiRow);
            var bsmo = ReadBsmo(br, bsmoRow);
            BsmaData bsma = rows.TryGetValue("BSMA", out var bsmaRow)
                ? ReadBsma(br, bsmaRow) : new BsmaData();

            BuildModels(strings, bsmi, bsmo, bsma);
        }

        // =================== BWTB (root table) ===================

        private static Dictionary<string, Row> ReadBwtb(BinaryReader br)
        {
            // Root row: 4s + 5 u32 = 24 bytes.
            var root = ReadRow(br);
            if (root.Header != "BWTB") throw new Exception($"Not a compiled space (header={root.Header})");
            // rows_num is the 6th field; ReadRow stored it in Length? No - re-read explicitly.
            br.BaseStream.Position = 0;
            string h = ReadHeader(br);
            br.ReadUInt32();                 // int1
            br.ReadUInt32();                 // position
            br.ReadUInt32();                 // int3
            br.ReadUInt32();                 // length
            uint rowsNum = br.ReadUInt32();  // rows_num

            var map = new Dictionary<string, Row>();
            for (uint i = 0; i < rowsNum; i++)
            {
                var r = ReadRow(br);
                map[r.Header] = r;
            }
            return map;
        }

        private static Row ReadRow(BinaryReader br)
        {
            string header = ReadHeader(br);
            br.ReadUInt32();                 // int1
            uint position = br.ReadUInt32();
            br.ReadUInt32();                 // int3
            uint length = br.ReadUInt32();
            br.ReadUInt32();                 // rows_num (unused for children)
            return new Row { Header = header, Position = position, Length = length };
        }

        private static string ReadHeader(BinaryReader br)
        {
            var b = br.ReadBytes(4);
            return System.Text.Encoding.ASCII.GetString(b);
        }

        // =================== BWST (string table) ===================

        private static Dictionary<uint, string> ReadBwst(BinaryReader br, Row row)
        {
            var result = new Dictionary<uint, string>();
            if (row.Length == 0) return result;
            br.BaseStream.Position = row.Position;

            // entries: [u32 size=12][u32 count][count * (hash, offset, length)]
            uint sz = br.ReadUInt32();
            uint cnt = br.ReadUInt32();
            var entries = new (uint hash, uint offset, uint length)[cnt];
            for (uint i = 0; i < cnt; i++)
                entries[i] = (br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32());

            uint stringsSize = br.ReadUInt32();
            long stringsStart = br.BaseStream.Position;
            foreach (var e in entries)
            {
                br.BaseStream.Position = stringsStart + e.offset;
                var bytes = br.ReadBytes((int)e.length);
                result[e.hash] = Latin1(bytes);
            }
            return result;
        }

        // =================== generic list/dict readers ===================

        /// <summary>Reads a "list" field: [u32 elemSize][u32 count] then count*elemSize bytes.
        /// Returns the raw element byte blocks.</summary>
        private static byte[][] ReadListRaw(BinaryReader br, out int elemSize)
        {
            elemSize = (int)br.ReadUInt32();
            int count = (int)br.ReadUInt32();
            var arr = new byte[count][];
            for (int i = 0; i < count; i++)
                arr[i] = br.ReadBytes(elemSize);
            return arr;
        }

        private static void SkipList(BinaryReader br)
        {
            int elemSize = (int)br.ReadUInt32();
            int count = (int)br.ReadUInt32();
            br.BaseStream.Position += (long)elemSize * count;
        }

        // =================== BSMI (instances) ===================

        private class BsmiData
        {
            public float[][] Transforms;     // each 16 floats
            public uint[] ModelIds;          // model index per instance
            public uint[] VisibilityMasks;
        }

        private static BsmiData ReadBsmi(BinaryReader br, Row row)
        {
            var d = new BsmiData();
            br.BaseStream.Position = row.Position;

            // field 0: transforms  '<16f'
            {
                var raw = ReadListRaw(br, out int es);
                d.Transforms = new float[raw.Length][];
                for (int i = 0; i < raw.Length; i++)
                {
                    var t = new float[16];
                    Buffer.BlockCopy(raw[i], 0, t, 0, 16 * 4);
                    d.Transforms[i] = t;
                }
            }
            // field 1: chunk_models (8 bytes each) - skip
            SkipList(br);
            // field 2: visibility_masks '<I'
            {
                var raw = ReadListRaw(br, out int es);
                d.VisibilityMasks = new uint[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                    d.VisibilityMasks[i] = BitConverter.ToUInt32(raw[i], 0);
            }
            // field 3: bsmo_models_id '<2I' (model_id, count) - we take element 0
            {
                var raw = ReadListRaw(br, out int es);
                d.ModelIds = new uint[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                    d.ModelIds[i] = BitConverter.ToUInt32(raw[i], 0);
            }
            // Remaining fields are not needed.
            return d;
        }

        // =================== BSMO (models) ===================

        private class RenderItem
        {
            public uint MaterialIndex;
            public uint PrimitiveIndex;
            public uint VertsNameFnv;
            public uint PrimsNameFnv;
        }

        private class BsmoData
        {
            public (uint begin, uint end)[] ModelsLoddings;     // lod_begin..lod_end
            public (uint begin, uint end)[] LodRenders;         // render_set_begin..end
            public RenderItem[] Renders;
        }

        private static BsmoData ReadBsmo(BinaryReader br, Row row)
        {
            var d = new BsmoData();
            br.BaseStream.Position = row.Position;

            // 0 models_loddings (8 bytes: lod_begin,lod_end)
            {
                var raw = ReadListRaw(br, out int es);
                d.ModelsLoddings = new (uint, uint)[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                    d.ModelsLoddings[i] = (BitConverter.ToUInt32(raw[i], 0), BitConverter.ToUInt32(raw[i], 4));
            }
            SkipList(br); // 1  '<I'
            SkipList(br); // 2  models_colliders (36)
            SkipList(br); // 3  bsp_material_kinds (8)
            SkipList(br); // 4  models_visibility_bounds (24)
            SkipList(br); // 5  model_info_items
            SkipList(br); // 6  model_sound_items '<I'
            SkipList(br); // 7  lod_loddings '<f'
            // 8 lod_renders (8 bytes: render_set_begin, render_set_end)
            {
                var raw = ReadListRaw(br, out int es);
                d.LodRenders = new (uint, uint)[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                    d.LodRenders[i] = (BitConverter.ToUInt32(raw[i], 0), BitConverter.ToUInt32(raw[i], 4));
            }
            // 9 renders (28 bytes: node_begin,node_end,material_index,primitive_index,verts_fnv,prims_fnv,flags)
            {
                var raw = ReadListRaw(br, out int es);
                d.Renders = new RenderItem[raw.Length];
                for (int i = 0; i < raw.Length; i++)
                {
                    var b = raw[i];
                    d.Renders[i] = new RenderItem
                    {
                        MaterialIndex = BitConverter.ToUInt32(b, 8),
                        PrimitiveIndex = BitConverter.ToUInt32(b, 12),
                        VertsNameFnv = BitConverter.ToUInt32(b, 16),
                        PrimsNameFnv = BitConverter.ToUInt32(b, 20),
                    };
                }
            }
            // Remaining fields unused.
            return d;
        }

        // =================== BSMA (materials) ===================

        private class MaterialInfo
        {
            public int KeyFx;
            public int KeyFrom;
            public int KeyTo;
        }

        private class PropInfo
        {
            public uint PropFnv;
            public uint ValueType;
            public uint Value;   // raw; for type 6 it's a string fnv
        }

        private class BsmaData
        {
            public MaterialInfo[] Materials = Array.Empty<MaterialInfo>();
            public PropInfo[] Props = Array.Empty<PropInfo>();
        }

        private static BsmaData ReadBsma(BinaryReader br, Row row)
        {
            var d = new BsmaData();
            if (row.Length == 0) return d;
            br.BaseStream.Position = row.Position;

            // materials: read_entries(MaterialInfo_1_6_0) -> [u32 size=16][u32 count][...]
            {
                uint sz = br.ReadUInt32();
                uint cnt = br.ReadUInt32();
                d.Materials = new MaterialInfo[cnt];
                for (uint i = 0; i < cnt; i++)
                {
                    int keyFx = br.ReadInt32();
                    int keyFrom = br.ReadInt32();
                    int keyTo = br.ReadInt32();
                    br.ReadUInt32(); // identifier_fnv
                    d.Materials[i] = new MaterialInfo { KeyFx = keyFx, KeyFrom = keyFrom, KeyTo = keyTo };
                }
            }
            // fx: read_entries(4, '<I')
            { uint sz = br.ReadUInt32(); uint cnt = br.ReadUInt32(); br.BaseStream.Position += (long)sz * cnt; }
            // props: read_entries(PropertyInfo) size=12
            {
                uint sz = br.ReadUInt32();
                uint cnt = br.ReadUInt32();
                d.Props = new PropInfo[cnt];
                for (uint i = 0; i < cnt; i++)
                {
                    uint propFnv = br.ReadUInt32();
                    uint vt = br.ReadUInt32();
                    uint val = br.ReadUInt32();
                    d.Props[i] = new PropInfo { PropFnv = propFnv, ValueType = vt, Value = val };
                }
            }
            // We don't need matrices/vectors/textures for placement.
            return d;
        }

        // =================== assemble ===================

        private void BuildModels(
            Dictionary<uint, string> strings,
            BsmiData bsmi, BsmoData bsmo, BsmaData bsma)
        {
            uint diffuseMapFnv = Fnv1a32("diffuseMap");

            int n = Math.Min(bsmi.Transforms.Length, bsmi.ModelIds.Length);
            int created = 0;
            for (int i = 0; i < n; i++)
            {
                uint modelId = bsmi.ModelIds[i];
                if (modelId >= bsmo.ModelsLoddings.Length) continue;

                uint lod0 = bsmo.ModelsLoddings[modelId].begin;
                if (lod0 >= bsmo.LodRenders.Length) continue;

                var (rsBegin, rsEnd) = bsmo.LodRenders[lod0];
                for (uint rs = rsBegin; rs <= rsEnd && rs < bsmo.Renders.Length; rs++)
                {
                    var r = bsmo.Renders[rs];

                    // No shader -> skip (matches reference: key_fx == -1).
                    if (r.MaterialIndex < bsma.Materials.Length &&
                        bsma.Materials[r.MaterialIndex].KeyFx == -1)
                        continue;

                    string vertsFull = strings.TryGetValue(r.VertsNameFnv, out var vn) ? vn : null;
                    string primsFull = strings.TryGetValue(r.PrimsNameFnv, out var pn) ? pn : null;
                    if (string.IsNullOrEmpty(primsFull)) continue;

                    SplitName(primsFull, out string primsName, out string primsData);
                    SplitName(vertsFull ?? primsFull, out string vertsName, out string vertsData);

                    primsName = primsName.Replace(".primitives", ".primitives_processed");

                    string diffuse = ResolveDiffuse(strings, bsma, r.MaterialIndex, diffuseMapFnv);

                    Models.Add(new ModelInstance
                    {
                        PrimsName = primsName,
                        VertsDataName = vertsData,
                        PrimsDataName = primsData,
                        PrimitiveGroup = (int)r.PrimitiveIndex,
                        DiffuseTexture = diffuse,
                        Transform = bsmi.Transforms[i],
                    });
                    created++;
                }
            }
            WoTLogger.Info($"CompiledSpace: {Models.Count} render instances from {n} model placements");
        }

        private static string ResolveDiffuse(
            Dictionary<uint, string> strings, BsmaData bsma, uint materialIndex, uint diffuseMapFnv)
        {
            if (materialIndex >= bsma.Materials.Length) return null;
            var mat = bsma.Materials[materialIndex];
            if (mat.KeyFrom < 0 || mat.KeyTo < 0) return null;
            for (int k = mat.KeyFrom; k <= mat.KeyTo && k < bsma.Props.Length; k++)
            {
                var p = bsma.Props[k];
                if (p.PropFnv == diffuseMapFnv && p.ValueType == 6)
                    return strings.TryGetValue(p.Value, out var s) ? s : null;
            }
            return null;
        }

        private static void SplitName(string full, out string name, out string dataName)
        {
            int idx = full.LastIndexOf('/');
            if (idx >= 0) { name = full.Substring(0, idx); dataName = full.Substring(idx + 1); }
            else { name = full; dataName = ""; }
        }

        // FNV-1a 32-bit (WoT uses fnv1a_64 & 0xffffffff -> equivalent low 32 bits).
        public static uint Fnv1a32(string s)
        {
            // Reference uses fnv1a_64(string) & 0xffffffff. Compute 64-bit then mask.
            ulong hval = 0xcbf29ce484222325UL;
            const ulong prime = 0x100000001b3UL;
            foreach (char c in s)
            {
                hval ^= (byte)c;   // Latin-1: char code == byte value for our keys
                hval *= prime;
            }
            return (uint)(hval & 0xffffffff);
        }

        /// <summary>Latin-1 decode (byte value == char code). Avoids relying on
        /// Encoding.Latin1 which isn't available on all Unity runtimes.</summary>
        private static string Latin1(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }
    }
}
