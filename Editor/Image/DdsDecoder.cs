using System;
using System.IO;
using UnityEngine;

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
            header = new Header
            {
                Size = br.ReadUInt32(),
                Flags = br.ReadUInt32(),
                Height = br.ReadUInt32(),
                Width = br.ReadUInt32(),
                PitchOrLinearSize = br.ReadUInt32(),
                Depth = br.ReadUInt32(),
                MipMapCount = br.ReadUInt32(),
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
        public static Texture2D Read(byte[] data, string name)
        {
            if (!TryReadHeader(data, out var header))
                throw new Exception("Not a DDS file");

            // Strip 128-byte header
            int dataOffset = (int)header.Size + 4;
            if (dataOffset + 4 > data.Length) dataOffset = 128;

            int w = (int)header.Width, h = (int)header.Height;
            var pixels = new Color32[w * h];

            // FourCC handling: 'DXT5', 'DXT1'
            string fourcc = FourCCToString(header.PfFourCC);

            // Determine block size
            int blockSize = fourcc switch
            {
                "DXT5" or "BC4" => 16,
                "DXT1" or "BC1" => 8,
                _ => throw new Exception($"Unsupported DDS FourCC: {fourcc}"),
            };

            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            int totalBlocks = blocksX * blocksY;

            // For DXT5: each block = 16 bytes (8 bytes alpha + 8 bytes RGB)
            // For DXT1: each block = 8 bytes (RGB, 1-bit alpha or none)
            using var ms = new MemoryStream(data, dataOffset, data.Length - dataOffset, false);
            using var br = new BinaryReader(ms);

            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    if (ms.Position + blockSize > ms.Length)
                        break;

                    if (fourcc == "DXT5")
                        DecodeDXT5Block(br, bx, by, w, h, pixels);
                    else
                        DecodeDXT1Block(br, bx, by, w, h, pixels);
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
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
            ulong alphaIdx = br.ReadUInt64();   // 16 3-bit indices, packed LSB first
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
