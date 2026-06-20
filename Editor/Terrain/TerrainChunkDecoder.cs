using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Image;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Terrain
{
    /// <summary>
    /// Decodes *.cdata files (which are ZIP archives containing terrain chunk data).
    /// Mirrors Simi4/WoT-Blender-Addons terrain_loader.py.
    /// </summary>
    public static class TerrainChunkDecoder
    {
        /// <summary>Decode one cdata file into a TerrainChunk instance.</summary>
        public static TerrainChunk Decode(byte[] cdataBytes, string chunkName, Vector2 chunkPos)
        {
            // cdata_processed may use a different internal format. Detect it.
            bool isProcessed = chunkName.EndsWith("o.cdata_processed", StringComparison.OrdinalIgnoreCase) ||
                                chunkName.EndsWith(".cdata_processed", StringComparison.OrdinalIgnoreCase);
            bool isZip = cdataBytes.Length >= 4 &&
                         cdataBytes[0] == 0x50 && cdataBytes[1] == 0x4B && // 'PK'
                         (cdataBytes[2] == 0x03 || cdataBytes[2] == 0x05 || cdataBytes[2] == 0x07);
            WoTLogger.Info($"Decoding chunk {chunkName} (processed={isProcessed}, isZip={isZip}, size={cdataBytes.Length})");

            // For non-ZIP (processed) format, we currently don't have a parser.
            // The Blender addon's terrain_loader.py also expects ZIP layout, so
            // processed files in this WoT client use a custom pre-baked format.
            if (!isZip)
            {
                WoTLogger.Warn($"Chunk {chunkName} is not ZIP; processed-format parser not yet implemented");
                return null;
            }

            using var ms = new MemoryStream(cdataBytes, false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            // Diagnostic: list entries so we know what's in the ZIP
            var entryNames = new List<string>();
            foreach (var e in zip.Entries)
                if (!string.IsNullOrEmpty(e.Name))
                    entryNames.Add(e.FullName);
            WoTLogger.Info($"Chunk {chunkName} ZIP entries ({entryNames.Count}): {string.Join(", ", entryNames.Take(15))}{(entryNames.Count > 15 ? "..." : "")}");

            var chunk = new TerrainChunk
            {
                ChunkName = chunkName,
                ChunkPos = chunkPos,
                Layers = new List<TerrainLayerDef>(),
                BlendTextures = new List<Texture2D>(),
            };

            // New version: blend_textures + layers in separate files
            // Old version: layer 1, layer 2, ... inside cdata
            var blendZ = zip.GetEntry("terrain2/blend_textures");
            if (blendZ != null)
            {
                chunk.IsNewBlendFormat = true;
                using var fr = blendZ.Open();
                chunk.BlendTextures.AddRange(ReadBlendTextures(fr, chunkName));

                var layersZ = zip.GetEntry("terrain2/layers");
                if (layersZ != null)
                {
                    using var fr2 = layersZ.Open();
                    chunk.Layers.AddRange(ReadLayers(fr2));
                }
            }
            else
            {
                chunk.IsNewBlendFormat = false;
                for (int i = 1; i <= 8; i++)
                {
                    var lz = zip.GetEntry($"terrain2/layer {i}");
                    if (lz == null) break;
                    using var fr = lz.Open();
                    ReadOldLayer(fr, chunk, i);
                }
            }

            if (chunk.Layers.Count == 0 || chunk.BlendTextures.Count == 0)
            {
                WoTLogger.Warn($"Chunk {chunkName} has no layers/blend textures, skipping");
                return null;
            }

            // Heights
            var heightsZ = zip.GetEntry("terrain2/heights1");
            if (heightsZ != null)
            {
                using var fr = heightsZ.Open();
                var ms2 = new MemoryStream();
                fr.CopyTo(ms2);
                ms2.Position = 0;
                chunk.HeightsTex = ReadHeightsTexture(ms2.ToArray(), chunkName);
            }
            else
            {
                WoTLogger.Warn($"Chunk {chunkName}: missing terrain2/heights1");
                return null;
            }

            // Normals (optional)
            var normalsZ = zip.GetEntry("terrain2/normals");
            if (normalsZ != null)
            {
                using var fr = normalsZ.Open();
                var ms2 = new MemoryStream();
                fr.CopyTo(ms2);
                ms2.Position = 0;
                chunk.NormalsTex = ReadNormalsTexture(ms2.ToArray(), chunkName);
            }

            return chunk;
        }

        // ============================ HEIGHTS ============================

        /// <summary>
        /// Reads heights PNG. Layout: 4-byte magic, then 8 bytes (uint32 width, uint32 height),
        /// then standard PNG bytes. We strip the 36-byte WoT header prefix and feed the
        /// remaining PNG bytes to Unity's loader.
        /// </summary>
        public static Texture2D ReadHeightsTexture(byte[] data, string chunkName)
        {
            // Skip first 4 bytes (magic) + 8 bytes (width, height) = 12 bytes total before PNG starts.
            // But the PNG header magic 'PNG' starts at offset... wait, in the original
            // code they seek(36) after reading width/height.
            // Let me re-check:
            //   fr.read(4) -> magic
            //   unpack('<2I', fr.read(8)) -> png_width, png_height
            //   fr.seek(36) -> seek to byte 36 from start (4 + 8 = 12, but they seek to 36? maybe padding)
            // Actually fr.seek(36) means seek to byte 36 absolute. So bytes 0-11 are WoT-specific,
            // bytes 12-35 are something else, byte 36+ is PNG data.
            if (data.Length < 36 + 8) return null;
            if (data[36] != 0x89 || data[37] != 'P' || data[38] != 'N' || data[39] != 'G')
            {
                WoTLogger.Warn($"Chunk {chunkName}: PNG header not found at offset 36");
                return null;
            }
            // Strip first 36 bytes
            byte[] pngBytes = new byte[data.Length - 36];
            Buffer.BlockCopy(data, 36, pngBytes, 0, pngBytes.Length);

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
            {
                name = chunkName + "_heights",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };
            if (!tex.LoadImage(pngBytes, false))
            {
                WoTLogger.Error($"Chunk {chunkName}: failed to load heights PNG");
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }

        // ============================ NORMALS ============================

        public static Texture2D ReadNormalsTexture(byte[] data, string chunkName)
        {
            // First 4 bytes = 'nrm\0'
            if (data.Length < 4 + 12) return null;
            if (data[0] != 'n' || data[1] != 'r' || data[2] != 'm' || data[3] != 0)
            {
                WoTLogger.Warn($"Chunk {chunkName}: normals magic not 'nrm'");
                return null;
            }
            ushort version = BitConverter.ToUInt16(data, 4);
            // header = unpack('<6H', fr.read(3 * 4)) -- reads 12 bytes total (6 ushorts)
            // version is header[0]
            ushort w = BitConverter.ToUInt16(data, 8);
            ushort h = BitConverter.ToUInt16(data, 10);

            int dataStart = 4 + 12;
            byte[] payload = new byte[data.Length - dataStart];
            Buffer.BlockCopy(data, dataStart, payload, 0, payload.Length);

            if (version == 1)
            {
                // Payload is PNG; load directly
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                {
                    name = chunkName + "_normals",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                if (tex.LoadImage(payload, false))
                    return tex;
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            else if (version == 2)
            {
                // Payload is DXT5 (raw, no DDS header). Reconstruct DDS header and decode.
                int totalBlocks = ((w + 3) / 4) * ((h + 3) / 4);
                int expectedSize = totalBlocks * 16;
                if (payload.Length < expectedSize)
                {
                    WoTLogger.Warn($"Chunk {chunkName}: normals v2 payload too small ({payload.Length}/{expectedSize})");
                    return null;
                }
                byte[] dds = BuildDdsDxT5Header((int)w, (int)h);
                var full = new byte[dds.Length + expectedSize];
                Buffer.BlockCopy(dds, 0, full, 0, dds.Length);
                Buffer.BlockCopy(payload, 0, full, dds.Length, expectedSize);
                var tex = DdsDecoder.ReadReadable(full, chunkName + "_normals");
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                return tex;
            }
            return null;
        }

        private static byte[] BuildDdsDxT5Header(int w, int h)
        {
            var h32 = new uint[31];
            h32[0] = 124;                          // dwSize
            h32[1] = 0x1 | 0x2 | 0x4 | 0x1000;    // CAPS|HEIGHT|WIDTH|PIXELFORMAT
            h32[2] = (uint)h;                      // dwHeight
            h32[3] = (uint)w;                      // dwWidth
            h32[4] = 0;                            // dwPitchOrLinearSize
            h32[5] = 0;                            // dwDepth
            h32[6] = 0;                            // dwMipMapCount
            // 11 reserved
            h32[18] = 32;                          // pf_Size
            h32[19] = 4;                           // pf_Flags (DDPF_FOURCC)
            h32[20] = BitConverter.ToUInt32(System.Text.Encoding.ASCII.GetBytes("DXT5"), 0);
            h32[21] = 0;
            h32[22] = 0;
            h32[23] = 0;
            h32[24] = 0;
            h32[25] = 0;
            h32[26] = 0x1000;                      // dwCaps (DDSCAPS_TEXTURE)

            var ms = new MemoryStream(4 + 124);
            var bw = new BinaryWriter(ms);
            bw.Write((uint)0x20534444); // "DDS "
            for (int i = 0; i < h32.Length; i++) bw.Write(h32[i]);
            bw.Flush();
            return ms.ToArray();
        }

        // ============================ BLEND TEXTURES (new format) ============================

        public static List<Texture2D> ReadBlendTextures(Stream stream, string chunkName)
        {
            var result = new List<Texture2D>();
            var br = new BinaryReader(stream);
            uint magic = br.ReadUInt32();
            if (magic != 0x00627762) // 'bwb\0' little-endian
                throw new Exception($"Bad blend_textures magic: {magic:X8}");

            int sectionCount = br.ReadInt32();
            // section_sizes = 4 ints (16 bytes)
            br.ReadBytes(16);

            for (int i = 0; i < sectionCount; i++)
            {
                uint secMagic = br.ReadUInt32();
                if (secMagic != 0x00747762) // 'bwt\0'
                    throw new Exception($"Bad blend section magic: {secMagic:X8}");

                // version(I) + xsize(H) + ysize(H) + always19(H) + tex_cnt(H) + padding(Q)
                uint version = br.ReadUInt32();
                ushort xsize = br.ReadUInt16();
                ushort ysize = br.ReadUInt16();
                ushort always19 = br.ReadUInt16();
                ushort texCnt = br.ReadUInt16();
                ulong padding = br.ReadUInt64();
                if (version != 2) throw new Exception($"Blend version {version} not supported");
                if (always19 != 19) throw new Exception($"Blend always19 != 19");
                if (padding != 0) throw new Exception($"Blend padding != 0");

                // Texture names (length + bytes)
                for (int j = 0; j < texCnt; j++)
                {
                    int nameSize = br.ReadInt32();
                    br.ReadBytes(nameSize);
                }

                int blocksX = (xsize + 3) / 4;
                int blocksY = (ysize + 3) / 4;
                int totalBlocks = blocksX * blocksY;
                int dataSize = totalBlocks * 16; // DXT5

                WoTLogger.Info($"  blend[{i}] xsize={xsize} ysize={ysize} totalBlocks={totalBlocks} dataSize={dataSize}");

                byte[] dds = BuildDdsDxT5Header(xsize, ysize);
                WoTLogger.Info($"  blend[{i}] DDS header size={dds.Length}, first 4 bytes={BitConverter.ToString(dds, 0, 4).Replace("-", " ")}, byte 76-80 (FourCC)={BitConverter.ToString(dds, 76, 4).Replace("-", " ")}");
                byte[] full = new byte[dds.Length + dataSize];
                Buffer.BlockCopy(dds, 0, full, 0, dds.Length);
                byte[] blockData = br.ReadBytes(dataSize);
                if (blockData.Length != dataSize)
                    throw new Exception($"Blend texture short read: {blockData.Length}/{dataSize}");
                Buffer.BlockCopy(blockData, 0, full, dds.Length, dataSize);

                // Blend weights must be CPU-readable (we sample them on the CPU
                // when building the splatmap), so use the RGBA32 decoder.
                var tex = DdsDecoder.ReadReadable(full, $"{chunkName}_blend_{i}");
                result.Add(tex);
            }
            return result;
        }

        // ============================ LAYERS (new format) ============================

        public static List<TerrainLayerDef> ReadLayers(Stream stream)
        {
            var result = new List<TerrainLayerDef>();
            var br = new BinaryReader(stream);

            // Header: 'blb\0'
            uint magic = br.ReadUInt32();
            if (magic != 0x00626C62) // 'blb\0'
                throw new Exception($"Bad layers magic: {magic:X8}");

            int mapCount = br.ReadInt32();
            br.ReadBytes(32); // 8 ints section_sizes

            for (int i = 0; i < mapCount; i++)
            {
                result.Add(ReadNewLayerDef(br));
            }
            return result;
        }

        private static TerrainLayerDef ReadNewLayerDef(BinaryReader br)
        {
            uint magic = br.ReadUInt32();
            if (magic != 0x00646C62) // 'bld\0'
                throw new Exception($"Bad layer magic: {magic:X8}");

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int count = br.ReadInt32();

            var uProj = ReadVector4(br);
            var vProj = ReadVector4(br);

            uint flags = br.ReadUInt32();
            // Padding 3 ints
            br.ReadBytes(12);

            // Displacement (3 row vectors = 12 floats)
            var row0 = ReadVector4(br);
            var row1 = ReadVector4(br);
            var row2 = ReadVector4(br);

            int nameLen = br.ReadInt32();
            string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            br.ReadByte(); // padding 1 byte
            string nameNm = name.Replace("_am.", "_nm.").ToLowerInvariant();

            return new TerrainLayerDef
            {
                Name = name.ToLowerInvariant(),
                NameNm = nameNm,
                UProjection = uProj,
                VProjection = vProj,
                Row0 = row0,
                Row1 = row1,
                Row2 = row2,
            };
        }

        private static Vector4 ReadVector4(BinaryReader br)
        {
            return new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        }

        // ============================ OLD LAYER (layer N format) ============================

        private static void ReadOldLayer(Stream stream, TerrainChunk chunk, int idx)
        {
            var br = new BinaryReader(stream);
            uint magic = br.ReadUInt32();
            if (magic != 0x00646C62) // 'bld\0'
                throw new Exception($"Bad old layer magic: {magic:X8}");

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int count = br.ReadInt32();

            var uProj = ReadVector4(br);
            var vProj = ReadVector4(br);

            uint flags = br.ReadUInt32();
            // Padding 3 ints
            br.ReadBytes(12);

            // In old format, row0/1/2 are not displacement matrices. Look for nm name.
            // We need to detect: if padding != (0,0,0), it's (1,0,0) meaning has_nm
            // For now, we read them anyway
            Vector4 row0 = ReadVector4(br);
            Vector4 row1 = ReadVector4(br);
            Vector4 row2 = ReadVector4(br);

            int nameLen = br.ReadInt32();
            string name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            // old format may have tga -> dds conversion
            if (name.EndsWith(".tga")) name = name.Substring(0, name.Length - 4) + ".dds";

            // optional nm name (if padding was (1,0,0))
            string nameNm = null;
            if (stream.Position < stream.Length - 4)
            {
                try
                {
                    int nmLen = br.ReadInt32();
                    if (nmLen > 0 && nmLen < 256 && stream.Position + nmLen <= stream.Length)
                    {
                        nameNm = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nmLen));
                    }
                }
                catch { /* ignore */ }
            }

            // Blend texture (PNG) follows
            if (stream.Position < stream.Length)
            {
                var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                {
                    name = $"{chunk.ChunkName}_blend_{idx}",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                };
                if (tex.LoadImage(ms.ToArray(), false))
                {
                    chunk.BlendTextures.Add(tex);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }

            chunk.Layers.Add(new TerrainLayerDef
            {
                Name = name.ToLowerInvariant(),
                NameNm = nameNm?.ToLowerInvariant(),
                UProjection = uProj,
                VProjection = vProj,
                Row0 = row0,
                Row1 = row1,
                Row2 = row2,
            });
        }
    }
}
