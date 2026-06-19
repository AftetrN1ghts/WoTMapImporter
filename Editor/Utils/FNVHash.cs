using System;
using System.Text;

namespace WoTMapImporter.Editor.Utils
{
    /// <summary>
    /// Fowler/Noll/Vo hash. Same implementation as Simi4/WoT-Blender-Addons (FNV-1a 64).
    /// Used by CompiledSpace to look up resource names by FNV.
    /// </summary>
    public static class FNVHash
    {
        public const ulong FNV1_64_INIT = 0xcbf29ce484222325UL;
        public const ulong FNV_64_PRIME = 0x100000001b3UL;

        public static ulong Fnv1A64(string s)
        {
            return Fnv1A64(Encoding.UTF8.GetBytes(s));
        }

        public static ulong Fnv1A64(byte[] data)
        {
            ulong hval = FNV1_64_INIT;
            for (int i = 0; i < data.Length; i++)
            {
                hval ^= data[i];
                hval *= FNV_64_PRIME;
            }
            return hval;
        }

        public static uint Fnv1A32(string s)
        {
            return Fnv1A32(Encoding.UTF8.GetBytes(s));
        }

        public static uint Fnv1A32(byte[] data)
        {
            const uint FNV1_32_INIT = 0x811c9dc5u;
            const uint FNV_32_PRIME = 0x01000193u;
            uint hval = FNV1_32_INIT;
            for (int i = 0; i < data.Length; i++)
            {
                hval ^= data[i];
                hval *= FNV_32_PRIME;
            }
            return hval;
        }
    }
}
