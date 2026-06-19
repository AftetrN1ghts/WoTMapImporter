using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEngine;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;
using WoTMapImporter.Editor.Xml;
using WoTStringExtensions = WoTMapImporter.Editor.Utils.StringExtensions;

namespace WoTMapImporter.Editor
{
    /// <summary>
    /// Parses the map list from scripts/arena_defs/_list_.xml in scripts.pkg,
    /// plus per-map info from arena_defs/&lt;map&gt;.xml.
    /// </summary>
    public static class MapListParser
    {
        public static List<MapInfo> ParseMaps(WoTPackageManager resMgr)
        {
            var list = new List<MapInfo>();
            string path = "scripts/arena_defs/_list_.xml";
            byte[] data = resMgr.ReadBytes(path);
            if (data == null)
            {
                WoTLogger.Warn($"'{path}' not found in scripts.pkg");
                return list;
            }
            WoTLogger.Info($"Read '{path}': {data.Length} bytes");

            // Quick sanity check - is it packed XML (0x62a14e45 magic)?
            if (data.Length >= 4)
            {
                uint magic = BitConverter.ToUInt32(data, 0);
                if (magic == XmlUnpacker.PACKED_HEADER)
                    WoTLogger.Info("Detected packed WoT XML format");
                else if (data[0] == '<')
                    WoTLogger.Info("Detected plain XML format");
                else
                    WoTLogger.Warn($"Unknown XML format, first 4 bytes: {data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2}");
            }

            XmlDocument doc;
            try
            {
                doc = XmlUnpacker.ReadBytes(data);
            }
            catch (Exception e)
            {
                WoTLogger.Error($"Failed to parse _list_.xml: {e.Message}");
                WoTLogger.Error(e.StackTrace);
                return list;
            }

            var root = doc.DocumentElement;
            if (root == null)
            {
                WoTLogger.Warn("_list_.xml parsed but root element is null");
                return list;
            }

            WoTLogger.Info($"Root element: <{root.LocalName}>, child count: {root.ChildNodes.Count}");

            int mapNodeCount = 0;
            foreach (XmlElement mapNode in root.SelectNodes("map"))
            {
                mapNodeCount++;
                var idNode = mapNode.SelectSingleNode("id");
                var nameNode = mapNode.SelectSingleNode("name");
                if (idNode == null || nameNode == null) continue;

                string mapName = nameNode.InnerText.Trim();
                if (string.IsNullOrEmpty(mapName)) continue;

                list.Add(new MapInfo
                {
                    Name = mapName,
                    LocalizedName = mapName, // TODO: gettext arenas.mo
                });
            }
            WoTLogger.Info($"Found {mapNodeCount} <map> nodes, {list.Count} with valid id+name");
            return list;
        }

        public static MapInfo ParseMapInfo(WoTPackageManager resMgr, string mapName)
        {
            string path = $"scripts/arena_defs/{mapName}.xml";
            byte[] data = resMgr.ReadBytes(path);
            if (data == null) throw new FileNotFoundException($"arena_defs/{mapName}.xml not found");
            var doc = XmlUnpacker.ReadBytes(data);
            var info = new MapInfo { Name = mapName, LocalizedName = mapName };

            var geom = doc.SelectSingleNode("/root/geometry");
            if (geom != null)
                info.Geometry = geom.InnerText.Trim();
            else
                info.Geometry = $"spaces/{mapName}";

            var bb = doc.SelectSingleNode("/root/boundingBox");
            if (bb != null)
            {
                var blNode = bb.SelectSingleNode("bottomLeft");
                var urNode = bb.SelectSingleNode("upperRight");
                if (blNode != null) info.BottomLeft = WoTStringExtensions.ParseVector3(blNode.InnerText);
                if (urNode != null) info.UpperRight = WoTStringExtensions.ParseVector3(urNode.InnerText);
            }
            return info;
        }
    }
}
