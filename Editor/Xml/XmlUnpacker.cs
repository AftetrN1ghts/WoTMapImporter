using System;
using System.IO;
using System.Text;
using System.Xml;

namespace WoTMapImporter.Editor.Xml
{
    /// <summary>
    /// Parses both "packed" WoT XML (0x62a14e45 magic header) and plain XML.
    /// Layout of packed XML:
    ///   [uint32 magic=0x62a14e45]
    ///   [dictionary: ASCIIZ strings, terminated by empty string]
    ///   [tree: 2-byte children count + 4-byte data descriptor (top 4 bits = type,
    ///          bottom 28 bits = end offset relative to descriptor address) +
    ///          per child: 2-byte name index + 4-byte child descriptor]
    ///   [children data (strings/numbers/floats) - each child's data starts at
    ///    parent_address + cumulative_offset, and ends at parent_address + child.descriptor_end]
    /// </summary>
    public static class XmlUnpacker
    {
        public const uint PACKED_HEADER = 0x62a14e45;
        private const uint END_MASK = 0x0FFFFFFF;

        public static XmlDocument Read(Stream stream)
        {
            stream.Position = 0;
            var br = new BinaryReader(stream, Encoding.UTF8);

            uint header;
            try { header = br.ReadUInt32(); }
            catch { return null; }

            var doc = new XmlDocument();

            if (header == PACKED_HEADER)
            {
                // WoT packed XML has this layout:
                //   [4 bytes magic = 0x62a14e45]
                //   [1 byte version/flags]
                //   [dictionary: ASCIIZ strings, terminated by empty string]
                //   [tree...]
                // The original Simi4 parser does stream.seek(5) before reading the
                // dictionary, so we must skip that 1 byte here. Without it the
                // dictionary read eats into the tree and subsequent reads blow up
                // with "Unable to read beyond the end of the stream".
                if (stream.Length > 5) stream.Position = 5;

                var dict = ReadDictionary(stream);
                var root = doc.CreateElement("root");
                doc.AppendChild(root);
                ReadElement(stream, br, dict, root);
            }
            else
            {
                stream.Position = 0;
                using (var sr = new StreamReader(stream))
                {
                    var content = sr.ReadToEnd();
                    doc.LoadXml(content);
                }
            }
            return doc;
        }

        public static XmlDocument ReadBytes(byte[] data)
        {
            using var ms = new MemoryStream(data);
            return Read(ms);
        }

        // ------------------- packed XML reading -------------------

        private static string[] ReadDictionary(Stream s)
        {
            // Called with stream positioned at offset 5 (after 4-byte magic + 1 byte version).
            // Read ASCIIZ strings until we hit an empty string (single 0x00 byte).
            var dict = new System.Collections.Generic.List<string>();
            while (true)
            {
                var sb = new StringBuilder();
                int b;
                while ((b = s.ReadByte()) != -1 && b != 0)
                {
                    sb.Append((char)b);
                }
                if (b == -1) break; // end of stream before terminator - abort
                if (sb.Length == 0) break; // empty string = terminator
                dict.Add(sb.ToString());
            }
            return dict.ToArray();
        }

        private struct Desc
        {
            public int Type;       // top 4 bits
            public uint End;       // bottom 28 bits - end offset in parent's data area
            public long Address;   // stream position right after descriptor
        }

        private static Desc ReadDesc(Stream s, BinaryReader br)
        {
            uint et = br.ReadUInt32();
            return new Desc
            {
                Type = (int)((et >> 28) & 0xF),
                End = et & END_MASK,
                Address = s.Position,
            };
        }

        private static void ReadElement(Stream s, BinaryReader br, string[] dict, XmlElement parent)
        {
            // Defensive: if stream has fewer than 6 bytes left, the tree is malformed.
            if (s.Length - s.Position < 6)
            {
                throw new Exception($"Malformed WoT XML: stream position {s.Position}, length {s.Length}, parent={parent.LocalName}");
            }
            ushort childCount = br.ReadUInt16();
            Desc parentDesc = ReadDesc(s, br);
            var children = new (ushort nameIdx, Desc desc)[childCount];
            for (int i = 0; i < childCount; i++)
            {
                ushort nameIdx = br.ReadUInt16();
                Desc d = ReadDesc(s, br);
                children[i] = (nameIdx, d);
            }

            // The parent's data area starts AFTER all child descriptors:
            // [2 children_count] [4 parent_desc] [6*N children_descs] [data...]
            long dataBase = parentDesc.Address + childCount * 6L;
            uint offset = 0;

            for (int i = 0; i < childCount; i++)
            {
                var (nameIdx, desc) = children[i];
                string name = (nameIdx < dict.Length) ? dict[nameIdx] : "?";
                var elem = parent.OwnerDocument.CreateElement(name);
                parent.AppendChild(elem);

                // Seek to start of this child's data (in parent's data area)
                s.Position = dataBase + offset;

                if (desc.Type == 0)
                {
                    // Nested element - read recursively from current position.
                    // The nested element's [children_count + parent_desc + children_descs]
                    // header starts here. ReadElement reads them in sequence.
                    ReadElement(s, br, dict, elem);
                    offset = desc.End;
                }
                else
                {
                    uint length = desc.End - offset;
                    ReadDataValue(s, br, elem, desc.Type, length);
                    offset = desc.End;
                }
            }

            // After processing all children, position is at end of parent's data
            s.Position = dataBase + parentDesc.End;
        }

        private static void ReadDataValue(Stream s, BinaryReader br, XmlElement elem, int type, uint length)
        {
            switch (type)
            {
                case 1: // string
                    elem.InnerText = ReadString(s, (int)length);
                    break;
                case 2: // number
                    elem.InnerText = ReadNumber(s, br, (int)length).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case 3: // floats
                    {
                        int n = (int)length / 4;
                        if (n == 12)
                        {
                            // 3x3 matrix as 3 "row" subelements
                            var vals = new float[n];
                            for (int i = 0; i < n; i++) vals[i] = br.ReadSingle();
                            for (int row = 0; row < 3; row++)
                            {
                                var rowElem = elem.OwnerDocument.CreateElement($"row{row}");
                                rowElem.InnerText = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "{0} {1} {2}", vals[row * 3], vals[row * 3 + 1], vals[row * 3 + 2]);
                                elem.AppendChild(rowElem);
                            }
                        }
                        else
                        {
                            var sb = new StringBuilder();
                            for (int i = 0; i < n; i++)
                            {
                                if (i > 0) sb.Append(' ');
                                sb.Append(br.ReadSingle().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                            }
                            elem.InnerText = sb.ToString();
                        }
                        break;
                    }
                case 4: // boolean
                    elem.InnerText = length == 0 ? "false" : (br.ReadByte() == 1 ? "true" : "false");
                    break;
                case 5: // base64
                    var b64 = Convert.ToBase64String(br.ReadBytes((int)length));
                    elem.InnerText = b64;
                    break;
                default:
                    throw new Exception($"Unknown element type: {type}");
            }
        }

        private static string ReadString(Stream s, int length)
        {
            if (length == 0) return string.Empty;
            var buf = new byte[length];
            int read = 0;
            while (read < length)
            {
                int r = s.Read(buf, read, length - read);
                if (r <= 0) break;
                read += r;
            }
            return Encoding.UTF8.GetString(buf, 0, read);
        }

        private static long ReadNumber(Stream s, BinaryReader br, int length)
        {
            if (length == 0) return 0;
            switch (length)
            {
                case 1: return br.ReadSByte();
                case 2: return br.ReadUInt16();
                case 4: return br.ReadUInt32();
                case 8: return (long)br.ReadUInt64();
                default: throw new Exception($"Unknown number length: {length}");
            }
        }

        // ------------------- convenience -------------------

        public static string GetText(XmlElement e, string xpath, string fallback = "")
        {
            var node = e.SelectSingleNode(xpath);
            if (node == null) return fallback;
            return node.InnerText ?? fallback;
        }

        public static System.Collections.Generic.IEnumerable<XmlElement> GetChildren(XmlElement e, string name)
        {
            foreach (XmlNode n in e.ChildNodes)
            {
                if (n is XmlElement xe && xe.LocalName == name) yield return xe;
            }
        }
    }
}
