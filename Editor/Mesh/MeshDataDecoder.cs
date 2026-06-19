using System;
using System.IO;
using UnityEngine;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Mesh
{
    /// <summary>
    /// Parses WoT .primitives_processed (BigWorld mesh format). Supports basic
    /// vertex formats (xyznuv, xyznuvtb, xyznuviiiwwtbpc) and triangle lists.
    /// </summary>
    public static class MeshDataDecoder
    {
        public const uint MAGIC = 0x42A14E65;

        public struct Section
        {
            public string Name;
            public long Position;
            public int Length;
        }

        public class DecodedMesh
        {
            public Vector3[] Positions;
            public Vector2[] Uv;
            public Vector2[] Uv2;
            public int[] Indices;
        }

        /// <summary>
        /// Reads packed section table at end of file. Returns a list of (name, position, length).
        /// </summary>
        public static System.Collections.Generic.List<Section> ReadSections(BinaryReader br)
        {
            var result = new System.Collections.Generic.List<Section>();
            br.BaseStream.Seek(-4, SeekOrigin.End);
            int tableStart = br.ReadInt32();
            br.BaseStream.Seek(-4 - tableStart, SeekOrigin.End);

            long position = 4;
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                if (br.BaseStream.Length - br.BaseStream.Position < 4) break;
                int sectionSize = br.ReadInt32();
                if (br.BaseStream.Length - br.BaseStream.Position < 16) break;
                br.ReadBytes(16); // reserved / padding
                if (br.BaseStream.Length - br.BaseStream.Position < 4) break;
                int nameLen = br.ReadInt32();
                if (nameLen <= 0 || nameLen > 256 || br.BaseStream.Length - br.BaseStream.Position < nameLen)
                    break;
                string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                result.Add(new Section { Name = name, Position = position, Length = sectionSize });

                position += sectionSize;
                if (sectionSize % 4 > 0) position += 4 - (sectionSize % 4);
                if (nameLen % 4 > 0)
                {
                    int pad = 4 - (nameLen % 4);
                    if (br.BaseStream.Length - br.BaseStream.Position >= pad)
                        br.ReadBytes(pad);
                }
            }
            return result;
        }

        public static DecodedMesh Decode(byte[] data)
        {
            using var ms = new MemoryStream(data, false);
            using var br = new BinaryReader(ms);
            uint magic = br.ReadUInt32();
            if (magic != MAGIC) throw new Exception($"Not a WoT primitives file (magic {magic:X8})");

            var sections = ReadSections(br);

            // Find indices section and vertices section
            Section? indicesSec = null;
            Section? verticesSec = null;
            foreach (var s in sections)
            {
                if (s.Name.Contains("indices")) indicesSec = s;
                if (s.Name.Contains("vertices")) verticesSec = s;
            }
            if (indicesSec == null || verticesSec == null)
                throw new Exception("Missing indices or vertices section");

            // Parse indices
            br.BaseStream.Seek(indicesSec.Value.Position, SeekOrigin.Begin);
            string indexFmt = ReadCString(br, 64);
            int nIndices = br.ReadInt32();
            int nTriangleGroups = br.ReadUInt16();

            int uintSize = indexFmt.StartsWith("list32") ? 4 : 2;
            int offset = nIndices * uintSize + 72;
            br.BaseStream.Seek(indicesSec.Value.Position + offset, SeekOrigin.Begin);

            // (We don't actually need primitive groups for a single mesh)
            // Skip groups
            br.BaseStream.Seek(nTriangleGroups * 16, SeekOrigin.Current);

            // Read actual indices (after groups)
            br.BaseStream.Seek(indicesSec.Value.Position + 72, SeekOrigin.Begin);
            var rawIndices = new byte[nIndices * uintSize];
            int read = 0;
            while (read < rawIndices.Length)
            {
                int r = br.Read(rawIndices, read, rawIndices.Length - read);
                if (r <= 0) break;
                read += r;
            }

            int[] indices;
            if (uintSize == 2)
            {
                indices = new int[nIndices];
                for (int i = 0; i < nIndices; i++)
                    indices[i] = BitConverter.ToUInt16(rawIndices, i * 2);
            }
            else
            {
                indices = new int[nIndices];
                for (int i = 0; i < nIndices; i++)
                    indices[i] = BitConverter.ToInt32(rawIndices, i * 4);
            }
            // Swap winding (0<->2)
            for (int i = 0; i < indices.Length; i += 3)
            {
                int tmp = indices[i];
                indices[i] = indices[i + 2];
                indices[i + 2] = tmp;
            }

            // Parse vertices
            br.BaseStream.Seek(verticesSec.Value.Position, SeekOrigin.Begin);
            string vertSub = ReadCString(br, 64);
            bool processed = vertSub.Contains("BPVT");
            int vertexCount;
            int vertSize;
            int vertFormatType; // 0=xyznuv, 1=xyznuvtb, 2=xyznuviiiwwtbpc
            if (processed)
            {
                br.ReadInt32(); // version
                vertSub = ReadCString(br, 64);
                vertexCount = br.ReadInt32();
            }
            else
            {
                vertexCount = br.ReadInt32();
            }

            if (vertSub.Contains("xyznuviiiwwtbpc") || vertSub.Contains("set3/xyznuviiiwwtbpc"))
            {
                vertSize = 48; // 12 + 8 + 28 (approx)
                vertFormatType = 2;
            }
            else if (vertSub.Contains("xyznuvtb") || vertSub.Contains("set3/xyznuvtbpc"))
            {
                vertSize = 24; // 12 + 8 + 4
                vertFormatType = 1;
            }
            else if (vertSub.Contains("xyznuv") || vertSub.Contains("set3/xyznuvpc"))
            {
                vertSize = 20; // 12 + 8
                vertFormatType = 0;
            }
            else
            {
                throw new Exception($"Unsupported vertex format: {vertSub}");
            }

            var positions = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            byte[] raw = br.ReadBytes(vertexCount * vertSize);

            int stride = vertSize;
            for (int i = 0; i < vertexCount; i++)
            {
                int off = i * stride;
                positions[i] = new Vector3(
                    BitConverter.ToSingle(raw, off + 0),
                    BitConverter.ToSingle(raw, off + 4),
                    BitConverter.ToSingle(raw, off + 8));
                uvs[i] = new Vector2(
                    BitConverter.ToSingle(raw, off + 12),
                    1f - BitConverter.ToSingle(raw, off + 16));
                if (vertFormatType == 1)
                {
                    // tangent.binormal: 2 floats (4 bytes). Already skipped.
                }
            }

            return new DecodedMesh
            {
                Positions = positions,
                Uv = uvs,
                Indices = indices,
            };
        }

        private static string ReadCString(BinaryReader br, int maxLen)
        {
            var bytes = br.ReadBytes(maxLen);
            int nullIdx = Array.IndexOf<byte>(bytes, 0);
            if (nullIdx < 0) nullIdx = bytes.Length;
            return System.Text.Encoding.UTF8.GetString(bytes, 0, nullIdx);
        }
    }
}
