using System;
using System.IO;
using UnityEngine;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Image
{
    /// <summary>
    /// Minimal DDS reader. Supports DXT5 (used by WoT blend_textures and v2 normals),
    /// BC1/DXT1. Other formats throw. Output is RGBA32.
    /// </summary>
    public static class DdsDecoder
    {
        public const uint MAGIC = 0x20534444; // "DDS "

        public struct Header
        {
            public uint Size;
            public uint Flags;
            public uint Height;
            public uint Width;
            public uint PitchOrLinearSize;
            public uint Depth;
            public uint MipMapCount;
            public uint PfSize, PfFlags, PfFourCC, PfRGBBitCount;
            public uint PfRBitMask, PfGBitMask, PfBBitMask, PfABitMask;
        }

        public static bool TryReadHeader(byte[] data, out Header header)
        {
            header = default;
            if (data == null || data.Length < 128) return false;
            if (BitConverter.ToUInt32(data, 0) != MAGIC) return false;

            using var ms = new MemoryStream(data, false);
            using var br = new BinaryReader(ms);
            br.ReadUInt32(); // magic
            uint size = br.ReadUInt32();
            uint flags = br.ReadUInt32();
            uint height = br.ReadUInt32();
            uint width = br.ReadUInt32();
            uint pitch = br.ReadUInt32();
            uint depth = br.ReadUInt32();
            uint mip = br.ReadUInt32();
            // DDS_HEADER has dwReserved1[11] (44 bytes) between dwMipMapCount and
            // the pixel-format struct. This block was missing before, which made
            // every subsequent field (PfFourCC etc.) read 44 bytes too early and
            // produced "Unsupported DDS FourCC: \0\0\0" / garbage.
            for (int r = 0; r < 11; r++) br.ReadUInt32();
            header = new Header
            {
                Size = size,
                Flags = flags,
                Height = height,
                Width = width,
                PitchOrLinearSize = pitch,
                Depth = depth,
                MipMapCount = mip,
                PfSize = br.ReadUInt32(),
                PfFlags = br.ReadUInt32(),
                PfFourCC = br.ReadUInt32(),
                PfRGBBitCount = br.ReadUInt32(),
                PfRBitMask = br.ReadUInt32(),
                PfGBitMask = br.ReadUInt32(),
                PfBBitMask = br.ReadUInt32(),
                PfABitMask = br.ReadUInt32(),
            };
            return true;
        }

        /// <summary>Reads DXT5 (or DXT1) texture as Texture2D (RGBA32).</summary>
        public static Texture2D Read(byte[] data, string name, bool linear = true)
        {
            if (!TryReadHeader(data, out var header))
                throw new Exception("Not a DDS file");

            int dataOffset = (int)header.Size + 4;        // 4-byte magic + header.Size
            if (dataOffset + 4 > data.Length) dataOffset = 128;

            int w = (int)header.Width, h = (int)header.Height;
            string fourcc = FourCCToString(header.PfFourCC);

            // Detect DX10 extended header (FourCC 'DX10' = 0x30315844). The real
            // DXGI format follows the 124-byte header (20 extra bytes).
            if (fourcc == "DX10")
            {
                uint dxgiFormat = BitConverter.ToUInt32(data, 128); // first field of DDS_HEADER_DXT10
                dataOffset = 148;                                   // 4 + 124 + 20
                var fmt10 = DxgiToUnity(dxgiFormat);
                if (fmt10 != (TextureFormat)0)
                    return BuildCompressed(data, dataOffset, w, h, fmt10, name, linear);
                WoTLogger.Warn($"DDS {name}: unsupported DXGI format {dxgiFormat}, falling back");
            }

            // Classic FourCC formats - hand them to Unity's native block decoder
            // via LoadRawTextureData (far more reliable than manual block decode).
            TextureFormat tf = fourcc switch
            {
                "DXT1" => TextureFormat.DXT1,
                "DXT5" => TextureFormat.DXT5,
                "ATI2" or "BC5U" => TextureFormat.BC5,
                "BC4U" or "ATI1" => TextureFormat.BC4,
                _ => (TextureFormat)0,
            };

            if (tf != (TextureFormat)0)
                return BuildCompressed(data, dataOffset, w, h, tf, name, linear);

            // Uncompressed RGBA/BGRA fallback.
            if (header.PfRGBBitCount == 32)
                return BuildUncompressed32(data, dataOffset, w, h, header, name, linear);

            throw new Exception($"Unsupported DDS FourCC: '{fourcc}' (bitcount={header.PfRGBBitCount})");
        }

        private static Texture2D BuildCompressed(
            byte[] data, int dataOffset, int w, int h, TextureFormat fmt, string name, bool linear)
        {
            int blockBytes = (fmt == TextureFormat.DXT1 || fmt == TextureFormat.BC4) ? 8 : 16;
            int mip0Size = ((w + 3) / 4) * ((h + 3) / 4) * blockBytes;

            var tex = new Texture2D(w, h, fmt, false, linear)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            int avail = data.Length - dataOffset;
            if (avail < mip0Size)
            {
                UnityEngine.Object.DestroyImmediate(tex);
                throw new Exception($"DDS {name}: truncated ({avail}/{mip0Size} bytes for {fmt})");
            }
            var mip0 = new byte[mip0Size];
            Buffer.BlockCopy(data, dataOffset, mip0, 0, mip0Size);
            tex.LoadRawTextureData(mip0);
            tex.Apply(false, false);
            return tex;
        }

        private static Texture2D BuildUncompressed32(
            byte[] data, int dataOffset, int w, int h, Header header, string name, bool linear)
        {
            // Assume BGRA8 (most common for uncompressed WoT dds). Swap to RGBA.
            int size = w * h * 4;
            if (data.Length - dataOffset < size)
                throw new Exception($"DDS {name}: truncated uncompressed data");
            var px = new Color32[w * h];
            bool bgr = header.PfBBitMask == 0xFF; // B in low byte => BGRA
            for (int i = 0; i < w * h; i++)
            {
                int o = dataOffset + i * 4;
                byte b0 = data[o], b1 = data[o + 1], b2 = data[o + 2], a = data[o + 3];
                px[i] = bgr ? new Color32(b2, b1, b0, a) : new Color32(b0, b1, b2, a);
            }
            // DDS rows are top-to-bottom; Unity textures are bottom-to-top -> flip.
            var flipped = new Color32[w * h];
            for (int y = 0; y < h; y++)
                Array.Copy(px, y * w, flipped, (h - 1 - y) * w, w);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear)
            {
                name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(flipped);
            tex.Apply(false, false);
            return tex;
        }

        private static TextureFormat DxgiToUnity(uint dxgi)
        {
            // Common DXGI_FORMAT values.
            switch (dxgi)
            {
                case 71: case 72: return TextureFormat.DXT1;   // BC1_UNORM / _SRGB
                case 74: case 75: return TextureFormat.DXT5;   // BC3_UNORM / _SRGB
                case 80: return TextureFormat.BC4;             // BC4_UNORM
                case 83: return TextureFormat.BC5;             // BC5_UNORM
                case 98: case 99: return TextureFormat.BC7;    // BC7_UNORM / _SRGB
                default: return (TextureFormat)0;
            }
        }

        /// <summary>
        /// Decodes a DXT1/DXT5 DDS to an uncompressed RGBA32 Texture2D that is
        /// CPU-readable via GetPixels32(). Used for blend/normal maps where we
        /// must read pixel weights on the CPU (compressed textures via
        /// LoadRawTextureData are NOT readable).
        /// </summary>
        public static Texture2D ReadReadable(byte[] data, string name)
        {
            if (!TryReadHeader(data, out var header))
                throw new Exception("Not a DDS file");

            int dataOffset = (int)header.Size + 4;
            if (dataOffset + 4 > data.Length) dataOffset = 128;

            int w = (int)header.Width, h = (int)header.Height;
            var pixels = new Color32[w * h];
            string fourcc = FourCCToString(header.PfFourCC);

            int blockSize = fourcc switch
            {
                "DXT5" => 16,
                "DXT1" => 8,
                _ => throw new Exception($"ReadReadable unsupported FourCC: {fourcc}"),
            };

            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            using var ms = new MemoryStream(data, dataOffset, data.Length - dataOffset, false);
            using var br = new BinaryReader(ms);
            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                {
                    if (ms.Position + blockSize > ms.Length) break;
                    if (fourcc == "DXT5") DecodeDXT5Block(br, bx, by, w, h, pixels);
                    else DecodeDXT1Block(br, bx, by, w, h, pixels);
                }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        private static string FourCCToString(uint v)
        {
            return new string(new[] {
                (char)(v & 0xFF),
                (char)((v >> 8) & 0xFF),
                (char)((v >> 16) & 0xFF),
                (char)((v >> 24) & 0xFF)
            });
        }

        // ------------------- DXT5 -------------------

        private static void DecodeDXT5Block(BinaryReader br, int bx, int by, int w, int h, Color32[] pixels)
        {
            byte a0 = br.ReadByte();
            byte a1 = br.ReadByte();
            // DXT5 alpha indices are 48 bits = 6 bytes (NOT 8). Reading a full
            // UInt64 here consumed 2 extra bytes and desynced every subsequent
            // block, producing constant/garbage weights -> one layer covering all.
            byte ai0 = br.ReadByte(), ai1 = br.ReadByte(), ai2 = br.ReadByte();
            byte ai3 = br.ReadByte(), ai4 = br.ReadByte(), ai5 = br.ReadByte();
            ulong alphaIdx =
                  (ulong)ai0
                | ((ulong)ai1 << 8)
                | ((ulong)ai2 << 16)
                | ((ulong)ai3 << 24)
                | ((ulong)ai4 << 32)
                | ((ulong)ai5 << 40);   // 16 3-bit indices, packed LSB first
            ushort c0 = br.ReadUInt16();
            ushort c1 = br.ReadUInt16();
            uint colorIdx = br.ReadUInt32();    // 16 2-bit indices, packed LSB first

            // Decode colors
            Color32 col0 = RGB565ToColor32(c0);
            Color32 col1 = RGB565ToColor32(c1);
            Color32 col2, col3;
            if (c0 > c1)
            {
                col2 = Lerp(col0, col1, 2, 1);
                col3 = Lerp(col0, col1, 1, 2);
            }
            else
            {
                col2 = Lerp(col0, col1, 1, 1);
                col3 = new Color32(0, 0, 0, 0);
            }
            Color32[] colors = { col0, col1, col2, col3 };

            // Decode alphas
            byte[] alphas = new byte[8];
            alphas[0] = a0; alphas[1] = a1;
            if (a0 > a1)
            {
                alphas[2] = (byte)((6 * a0 + 1 * a1) / 7);
                alphas[3] = (byte)((5 * a0 + 2 * a1) / 7);
                alphas[4] = (byte)((4 * a0 + 3 * a1) / 7);
                alphas[5] = (byte)((3 * a0 + 4 * a1) / 7);
                alphas[6] = (byte)((2 * a0 + 5 * a1) / 7);
                alphas[7] = (byte)((1 * a0 + 6 * a1) / 7);
            }
            else
            {
                alphas[2] = (byte)((4 * a0 + 1 * a1) / 5);
                alphas[3] = (byte)((3 * a0 + 2 * a1) / 5);
                alphas[4] = (byte)((2 * a0 + 3 * a1) / 5);
                alphas[5] = (byte)((1 * a0 + 4 * a1) / 5);
                alphas[6] = (byte)(0);
                alphas[7] = (byte)(255);
            }

            for (int py = 0; py < 4; py++)
            {
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px;
                    int y = by * 4 + py;
                    if (x >= w || y >= h) continue;
                    int colorBit = (int)((colorIdx >> (py * 8 + px * 2)) & 0x3);
                    int alphaBit = (int)((alphaIdx >> (py * 12 + px * 3)) & 0x7);
                    var c = colors[colorBit];
                    c.a = alphas[alphaBit];
                    pixels[y * w + x] = c;
                }
            }
        }

        private static Color32 RGB565ToColor32(ushort v)
        {
            int r5 = (v >> 11) & 0x1F;
            int g6 = (v >> 5) & 0x3F;
            int b5 = v & 0x1F;
            // Scale to 8 bits
            byte r = (byte)((r5 << 3) | (r5 >> 2));
            byte g = (byte)((g6 << 2) | (g6 >> 4));
            byte b = (byte)((b5 << 3) | (b5 >> 2));
            return new Color32(r, g, b, 255);
        }

        private static Color32 Lerp(Color32 a, Color32 b, int aw, int bw)
        {
            int total = aw + bw;
            return new Color32(
                (byte)((a.r * aw + b.r * bw) / total),
                (byte)((a.g * aw + b.g * bw) / total),
                (byte)((a.b * aw + b.b * bw) / total),
                255);
        }

        // ------------------- DXT1 -------------------

        private static void DecodeDXT1Block(BinaryReader br, int bx, int by, int w, int h, Color32[] pixels)
        {
            ushort c0 = br.ReadUInt16();
            ushort c1 = br.ReadUInt16();
            uint colorIdx = br.ReadUInt32();

            Color32 col0 = RGB565ToColor32(c0);
            Color32 col1 = RGB565ToColor32(c1);
            Color32 col2, col3;
            if (c0 > c1)
            {
                col2 = Lerp(col0, col1, 2, 1);
                col3 = Lerp(col0, col1, 1, 2);
            }
            else
            {
                col2 = Lerp(col0, col1, 1, 1);
                col3 = new Color32(0, 0, 0, 255);  // black for DXT1
            }
            Color32[] colors = { col0, col1, col2, col3 };

            for (int py = 0; py < 4; py++)
            {
                for (int px = 0; px < 4; px++)
                {
                    int x = bx * 4 + px;
                    int y = by * 4 + py;
                    if (x >= w || y >= h) continue;
                    int colorBit = (int)((colorIdx >> (py * 8 + px * 2)) & 0x3);
                    pixels[y * w + x] = colors[colorBit];
                }
            }
        }
    }
}
