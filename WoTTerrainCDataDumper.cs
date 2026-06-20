// Put this file into your Unity project under Assets/Editor/WoTTerrainCDataDumper.cs
// Menu: Tools/WoT/Dump Terrain CData...
// It dumps terrain2/layers and terrain2/blend_textures from WoT *.cdata zip files.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class WoTTerrainCDataDumper
{
    [MenuItem("Tools/WoT/Dump Terrain CData...")]
    public static void DumpTerrainCDataMenu()
    {
        string path = EditorUtility.OpenFilePanel("Choose one .cdata file, or cancel to choose a folder", "", "cdata");
        if (string.IsNullOrEmpty(path))
        {
            path = EditorUtility.OpenFolderPanel("Choose folder with .cdata files", "", "");
            if (string.IsNullOrEmpty(path)) return;
        }

        string outDir = EditorUtility.OpenFolderPanel("Choose output folder for dump", Application.dataPath, "");
        if (string.IsNullOrEmpty(outDir)) return;

        var files = new List<string>();
        if (File.Exists(path)) files.Add(path);
        else files.AddRange(Directory.GetFiles(path, "*.cdata*", SearchOption.TopDirectoryOnly));
        files.Sort(StringComparer.OrdinalIgnoreCase);

        // Limit by default so the dump is readable. Change if needed.
        int maxFiles = Mathf.Min(files.Count, 8);

        var sb = new StringBuilder(1024 * 128);
        sb.AppendLine("=== WoT terrain cdata dump ===");
        sb.AppendLine("Unity: " + Application.unityVersion);
        sb.AppendLine("Input: " + path);
        sb.AppendLine("Files found: " + files.Count + ", dumped: " + maxFiles);
        sb.AppendLine("Expected NEW mapping from Blender plugin:");
        sb.AppendLine("  blend_texture[i].ALPHA -> layer[i*2]");
        sb.AppendLine("  blend_texture[i].GREEN -> layer[i*2+1]");
        sb.AppendLine("If A/G are empty but R/B have data, mapping/channel decode is wrong.");
        sb.AppendLine();

        for (int i = 0; i < maxFiles; i++)
        {
            try
            {
                DumpOne(files[i], outDir, sb);
            }
            catch (Exception e)
            {
                sb.AppendLine("ERROR dumping " + files[i]);
                sb.AppendLine(e.ToString());
            }
        }

        string outPath = Path.Combine(outDir, "WoT_cdata_dump.txt");
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        Debug.Log("WoT cdata dump written: " + outPath);
        EditorUtility.RevealInFinder(outPath);
    }

    private static void DumpOne(string cdataPath, string outDir, StringBuilder sb)
    {
        string chunk = Path.GetFileName(cdataPath).Substring(0, Math.Min(8, Path.GetFileName(cdataPath).Length));
        sb.AppendLine("============================================================");
        sb.AppendLine("CDATA: " + cdataPath);
        sb.AppendLine("Chunk: " + chunk);

        using (var zip = ZipFile.OpenRead(cdataPath))
        {
            sb.AppendLine("Entries:");
            foreach (var e in zip.Entries)
                sb.AppendLine("  " + e.FullName + " size=" + e.Length);

            var blend = zip.GetEntry("terrain2/blend_textures");
            var layers = zip.GetEntry("terrain2/layers");

            if (blend != null)
            {
                sb.AppendLine("-- NEW terrain format detected: terrain2/blend_textures + terrain2/layers --");
                List<string> layerNames = layers != null ? ReadNewLayers(layers, sb) : new List<string>();
                ReadNewBlendTextures(blend, chunk, layerNames, outDir, sb);
            }
            else
            {
                sb.AppendLine("-- OLD terrain format or no blend_textures --");
                for (int i = 1; i <= 8; i++)
                {
                    var oldLayer = zip.GetEntry("terrain2/layer " + i);
                    if (oldLayer == null) continue;
                    ReadOldLayer(oldLayer, i, chunk, outDir, sb);
                }
            }
        }
        sb.AppendLine();
    }

    private static List<string> ReadNewLayers(ZipArchiveEntry entry, StringBuilder sb)
    {
        var names = new List<string>();
        using (var s = entry.Open())
        using (var br = new BinaryReader(s))
        {
            uint magic = br.ReadUInt32();
            sb.AppendLine("layers.magic=" + FourCC(magic) + " 0x" + magic.ToString("X8"));
            if (magic != 0x00626C62) // blb\0
            {
                sb.AppendLine("BAD layers magic, expected blb\\0");
                return names;
            }
            int mapCount = br.ReadInt32();
            uint[] sizes = new uint[8];
            for (int i = 0; i < 8; i++) sizes[i] = br.ReadUInt32();
            sb.AppendLine("layers.mapCount=" + mapCount + " sectionSizes=" + string.Join(",", sizes));

            for (int i = 0; i < mapCount; i++)
            {
                long start = s.Position;
                uint lm = br.ReadUInt32();
                int width = br.ReadInt32();
                int height = br.ReadInt32();
                int count = br.ReadInt32();
                Vector4 u = ReadV4(br);
                Vector4 v = ReadV4(br);
                uint flags = br.ReadUInt32();
                uint p0 = br.ReadUInt32(), p1 = br.ReadUInt32(), p2 = br.ReadUInt32();
                Vector4 row0 = ReadV4(br);
                Vector4 row1 = ReadV4(br);
                Vector4 row2 = ReadV4(br);
                int nameLen = br.ReadInt32();
                string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                if (s.Position < s.Length) br.ReadByte(); // padding byte in new format
                names.Add(name);

                sb.AppendLine($"  layer[{i}] start={start} magic={FourCC(lm)} size={width}x{height} count={count} flags=0x{flags:X8} pad=({p0},{p1},{p2})");
                sb.AppendLine($"    name={name}");
                sb.AppendLine($"    expectedWeight={(i % 2 == 0 ? "blend[" + (i / 2) + "].A" : "blend[" + (i / 2) + "].G")}");
                sb.AppendLine($"    uProj={Fmt(u)} vProj={Fmt(v)} row0={Fmt(row0)} row1={Fmt(row1)} row2={Fmt(row2)}");
            }
        }
        return names;
    }

    private static void ReadNewBlendTextures(ZipArchiveEntry entry, string chunk, List<string> layers, string outDir, StringBuilder sb)
    {
        using (var s = entry.Open())
        using (var br = new BinaryReader(s))
        {
            uint magic = br.ReadUInt32();
            sb.AppendLine("blend.magic=" + FourCC(magic) + " 0x" + magic.ToString("X8"));
            if (magic != 0x00627762) // bwb\0
            {
                sb.AppendLine("BAD blend magic, expected bwb\\0");
                return;
            }
            int sectionCount = br.ReadInt32();
            uint[] sectionSizes = new uint[4];
            for (int i = 0; i < 4; i++) sectionSizes[i] = br.ReadUInt32();
            sb.AppendLine("blend.sectionCount=" + sectionCount + " sectionSizes=" + string.Join(",", sectionSizes));

            for (int bi = 0; bi < sectionCount; bi++)
            {
                long sectionStart = s.Position;
                uint secMagic = br.ReadUInt32();
                uint version = br.ReadUInt32();
                ushort xsize = br.ReadUInt16();
                ushort ysize = br.ReadUInt16();
                ushort always19 = br.ReadUInt16();
                ushort texCnt = br.ReadUInt16();
                ulong padding = br.ReadUInt64();
                sb.AppendLine($"  blend[{bi}] start={sectionStart} magic={FourCC(secMagic)} version={version} size={xsize}x{ysize} always19={always19} texCnt={texCnt} padding={padding}");

                for (int ti = 0; ti < texCnt; ti++)
                {
                    int len = br.ReadInt32();
                    string nm = Encoding.UTF8.GetString(br.ReadBytes(len));
                    sb.AppendLine($"    embeddedName[{ti}]={nm}");
                }

                int dataSize = ((xsize + 3) / 4) * ((ysize + 3) / 4) * 16;
                byte[] dxt5 = br.ReadBytes(dataSize);
                sb.AppendLine($"    dxt5Bytes={dxt5.Length}/{dataSize}");
                if (dxt5.Length != dataSize) return;

                Color32[] px = DecodeDXT5(dxt5, xsize, ysize);
                DumpChannelStats(sb, px, xsize, ysize, bi, layers);
                SaveChannelPreviews(px, xsize, ysize, chunk, bi, outDir, sb);
            }
        }
    }

    private static void DumpChannelStats(StringBuilder sb, Color32[] px, int w, int h, int blendIndex, List<string> layers)
    {
        char[] names = { 'R', 'G', 'B', 'A' };
        for (int ch = 0; ch < 4; ch++)
        {
            int min = 255, max = 0, nonZero = 0, gt16 = 0, gt128 = 0;
            long sum = 0;
            for (int i = 0; i < px.Length; i++)
            {
                int v = Get(px[i], ch);
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                if (v != 0) nonZero++;
                if (v > 16) gt16++;
                if (v > 128) gt128++;
            }
            string mapped = "";
            if (ch == 3)
            {
                int li = blendIndex * 2;
                mapped = li < layers.Count ? " -> layer[" + li + "] " + Path.GetFileName(layers[li]) : " -> layer[" + li + "] MISSING";
            }
            else if (ch == 1)
            {
                int li = blendIndex * 2 + 1;
                mapped = li < layers.Count ? " -> layer[" + li + "] " + Path.GetFileName(layers[li]) : " -> layer[" + li + "] MISSING";
            }
            sb.AppendLine($"    channel {names[ch]}{mapped}: min={min} max={max} avg={(sum / (double)px.Length):F2} nonZero={(100.0 * nonZero / px.Length):F1}% >16={(100.0 * gt16 / px.Length):F1}% >128={(100.0 * gt128 / px.Length):F1}%");
        }
    }

    private static void SaveChannelPreviews(Color32[] px, int w, int h, string chunk, int blendIndex, string outDir, StringBuilder sb)
    {
        char[] names = { 'R', 'G', 'B', 'A' };
        for (int ch = 0; ch < 4; ch++)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var outPx = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                byte v = (byte)Get(px[i], ch);
                outPx[i] = new Color32(v, v, v, 255);
            }
            tex.SetPixels32(outPx);
            tex.Apply(false, false);
            string fn = Path.Combine(outDir, $"{chunk}_blend{blendIndex}_{names[ch]}.png");
            File.WriteAllBytes(fn, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
        sb.AppendLine($"    wrote PNG previews: {chunk}_blend{blendIndex}_R/G/B/A.png");
    }

    private static void ReadOldLayer(ZipArchiveEntry entry, int idx, string chunk, string outDir, StringBuilder sb)
    {
        using (var s = entry.Open())
        using (var br = new BinaryReader(s))
        {
            sb.AppendLine("old layer " + idx + " size=" + entry.Length);
            uint magic = br.ReadUInt32();
            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int count = br.ReadInt32();
            Vector4 u = ReadV4(br);
            Vector4 v = ReadV4(br);
            uint flags = br.ReadUInt32();
            uint p0 = br.ReadUInt32(), p1 = br.ReadUInt32(), p2 = br.ReadUInt32();
            Vector4 row0 = ReadV4(br);
            Vector4 row1 = ReadV4(br);
            Vector4 row2 = ReadV4(br);
            int nameLen = br.ReadInt32();
            string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            sb.AppendLine($"  magic={FourCC(magic)} size={width}x{height} count={count} flags=0x{flags:X8} pad=({p0},{p1},{p2}) name={name}");
            sb.AppendLine($"  uProj={Fmt(u)} vProj={Fmt(v)} row0={Fmt(row0)} row1={Fmt(row1)} row2={Fmt(row2)}");
            // Remaining bytes are usually PNG blend texture. We only report signature.
            long rest = s.Length - s.Position;
            byte[] sig = br.ReadBytes((int)Math.Min(16, rest));
            sb.AppendLine("  remainingBlendBytes=" + rest + " sig=" + BitConverter.ToString(sig));
        }
    }

    private static Color32[] DecodeDXT5(byte[] data, int w, int h)
    {
        var pixels = new Color32[w * h];
        int blocksX = (w + 3) / 4;
        int blocksY = (h + 3) / 4;
        int pos = 0;
        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            if (pos + 16 > data.Length) break;
            byte a0 = data[pos++];
            byte a1 = data[pos++];
            ulong alphaIdx = 0;
            for (int i = 0; i < 6; i++) alphaIdx |= ((ulong)data[pos++]) << (8 * i);
            ushort c0 = (ushort)(data[pos++] | (data[pos++] << 8));
            ushort c1 = (ushort)(data[pos++] | (data[pos++] << 8));
            uint colorIdx = (uint)(data[pos++] | (data[pos++] << 8) | (data[pos++] << 16) | (data[pos++] << 24));

            Color32 col0 = RGB565(c0);
            Color32 col1 = RGB565(c1);
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
            Color32[] cols = { col0, col1, col2, col3 };

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
                alphas[6] = 0;
                alphas[7] = 255;
            }

            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int x = bx * 4 + px;
                int y = by * 4 + py;
                if (x >= w || y >= h) continue;
                int ci = (int)((colorIdx >> (py * 8 + px * 2)) & 3);
                int ai = (int)((alphaIdx >> (py * 12 + px * 3)) & 7);
                Color32 c = cols[ci];
                c.a = alphas[ai];
                pixels[y * w + x] = c;
            }
        }
        return pixels;
    }

    private static int Get(Color32 c, int ch)
    {
        switch (ch)
        {
            case 0: return c.r;
            case 1: return c.g;
            case 2: return c.b;
            case 3: return c.a;
            default: return 0;
        }
    }

    private static Color32 RGB565(ushort v)
    {
        byte r = (byte)((((v >> 11) & 31) << 3) | (((v >> 11) & 31) >> 2));
        byte g = (byte)((((v >> 5) & 63) << 2) | (((v >> 5) & 63) >> 4));
        byte b = (byte)(((v & 31) << 3) | ((v & 31) >> 2));
        return new Color32(r, g, b, 255);
    }

    private static Color32 Lerp(Color32 a, Color32 b, int aw, int bw)
    {
        int t = aw + bw;
        return new Color32((byte)((a.r * aw + b.r * bw) / t), (byte)((a.g * aw + b.g * bw) / t), (byte)((a.b * aw + b.b * bw) / t), 255);
    }

    private static Vector4 ReadV4(BinaryReader br)
    {
        return new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
    }

    private static string Fmt(Vector4 v)
    {
        return $"({v.x:F6},{v.y:F6},{v.z:F6},{v.w:F6})";
    }

    private static string FourCC(uint x)
    {
        char a = (char)(x & 255), b = (char)((x >> 8) & 255), c = (char)((x >> 16) & 255), d = (char)((x >> 24) & 255);
        return $"'{Escape(a)}{Escape(b)}{Escape(c)}{Escape(d)}'";
    }

    private static string Escape(char c)
    {
        return c == '\0' ? "\\0" : c.ToString();
    }
}
