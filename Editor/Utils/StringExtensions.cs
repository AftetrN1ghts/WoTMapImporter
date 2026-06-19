using System;
using System.Globalization;

namespace WoTMapImporter.Editor.Utils
{
    /// <summary>
    /// Helpers for parsing WoT vector strings ("x y z w") and similar.
    /// </summary>
    public static class StringExtensions
    {
        public static UnityEngine.Vector3 ParseVector3(string s)
        {
            if (string.IsNullOrEmpty(s))
                return UnityEngine.Vector3.zero;
            var parts = s.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return UnityEngine.Vector3.zero;
            return new UnityEngine.Vector3(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        public static UnityEngine.Vector4 ParseVector4(string s)
        {
            if (string.IsNullOrEmpty(s))
                return UnityEngine.Vector4.zero;
            var parts = s.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return new UnityEngine.Vector4(
                    float.Parse(parts[0], CultureInfo.InvariantCulture),
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture),
                    0f);
            return new UnityEngine.Vector4(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture),
                float.Parse(parts[3], CultureInfo.InvariantCulture));
        }

        /// <summary>Converts WoT 4x4 transform (row-major, 4 rows of 4 floats) to Unity Matrix4x4 (column-major).</summary>
        public static UnityEngine.Matrix4x4 ParseTransform4x4(string s)
        {
            var rows = new UnityEngine.Vector4[4];
            var parts = s.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 16)
                return UnityEngine.Matrix4x4.identity;
            for (int r = 0; r < 4; r++)
            {
                rows[r] = new UnityEngine.Vector4(
                    float.Parse(parts[r * 4 + 0], CultureInfo.InvariantCulture),
                    float.Parse(parts[r * 4 + 1], CultureInfo.InvariantCulture),
                    float.Parse(parts[r * 4 + 2], CultureInfo.InvariantCulture),
                    float.Parse(parts[r * 4 + 3], CultureInfo.InvariantCulture));
            }
            // Convert WoT row-major (used by Blender addon as transform_row0..3) to Unity column-major.
            // Rows in WoT = rows in space (already transposed vs OpenGL). Blender re-transposes with
            // Matrix(...).transposed() and flips XZY <-> XYZ. We just rebuild a column-major Matrix4x4
            // from the rows interpreted as world-space basis vectors.
            var m = new UnityEngine.Matrix4x4();
            m.SetColumn(0, rows[0]);
            m.SetColumn(1, rows[1]);
            m.SetColumn(2, rows[2]);
            m.SetColumn(3, rows[3]);
            return m;
        }
    }
}
