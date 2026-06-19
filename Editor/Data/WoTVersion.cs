using System;

namespace WoTMapImporter.Editor.Data
{
    /// <summary>
    /// Parses WoT version strings like "1.27.1.0" or "v.1.27.1.0#1234".
    /// Determines which file formats the importer needs to handle.
    /// </summary>
    public readonly struct WoTVersion : IComparable<WoTVersion>
    {
        public readonly int Major, Minor, Patch, Build;
        public readonly string RawString;
        public readonly string Realm;

        public WoTVersion(int major, int minor, int patch, int build, string raw, string realm)
        {
            Major = major; Minor = minor; Patch = patch; Build = build;
            RawString = raw; Realm = realm ?? "RU";
        }

        public static bool TryParse(string versionStr, string realm, out WoTVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(versionStr)) return false;

            string s = versionStr.Trim();
            s = s.Replace("Supertest v.ST ", "v.").Replace(" Common Test", "");
            if (s.Contains("v.")) s = s.Substring(s.IndexOf("v.", StringComparison.Ordinal) + 2);
            if (s.Contains("#")) s = s.Substring(0, s.IndexOf('#'));
            s = s.Trim();
            var parts = s.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) return false;
            int c = parts.Length > 2 && int.TryParse(parts[2], out int v2) ? v2 : 0;
            int d = parts.Length > 3 && int.TryParse(parts[3], out int v3) ? v3 : 0;
            version = new WoTVersion(a, b, c, d, versionStr, realm);
            return true;
        }

        public bool HasCompiledSpace => Major > 0 || (Major == 0 && Minor > 9) || (Major == 0 && Minor == 9 && Patch >= 12);

        public int CompareTo(WoTVersion other)
        {
            int c = Major.CompareTo(other.Major); if (c != 0) return c;
            c = Minor.CompareTo(other.Minor); if (c != 0) return c;
            c = Patch.CompareTo(other.Patch); if (c != 0) return c;
            return Build.CompareTo(other.Build);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}.{Build}";
    }
}
