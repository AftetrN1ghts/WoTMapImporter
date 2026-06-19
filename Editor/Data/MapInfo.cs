using UnityEngine;

namespace WoTMapImporter.Editor.Data
{
    /// <summary>
    /// Data describing one map (from arena_defs/&lt;map&gt;.xml).
    /// </summary>
    public class MapInfo
    {
        public string Name;
        public string LocalizedName;
        public string Geometry;     // e.g. "spaces/karelia"
        public Vector3 BottomLeft;
        public Vector3 UpperRight;

        public string LogName => string.IsNullOrEmpty(LocalizedName) ? Name : LocalizedName;
    }
}
