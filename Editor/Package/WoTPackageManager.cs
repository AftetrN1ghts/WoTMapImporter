using System.Collections.Generic;
using System.IO;
using WoTMapImporter.Editor.Utils;

namespace WoTMapImporter.Editor.Package
{
    /// <summary>
    /// Manages a set of .pkg files (and optionally res_mods / unpacked folders)
    /// to resolve resource names to byte streams. Mimics Simi4/WoT-Blender-Addons'
    /// UniversalResMgr.
    /// </summary>
    public sealed class WoTPackageManager : System.IDisposable
    {
        private readonly List<PkgFile> _pkgFiles = new List<PkgFile>();
        private readonly Dictionary<string, (PkgFile pkg, string name)> _fileCache
            = new Dictionary<string, (PkgFile, string)>(System.StringComparer.OrdinalIgnoreCase);
        private string _unpackDir;

        public string UnpackDir => _unpackDir;

        public WoTPackageManager(string resPkgPath, IEnumerable<string> pkgNames)
        {
            _unpackDir = resPkgPath;
            foreach (var name in pkgNames)
            {
                string fullPath = Path.Combine(resPkgPath, name);
                if (!File.Exists(fullPath))
                {
                    WoTLogger.Warn($"pkg not found: {name}");
                    continue;
                }
                var pkg = new PkgFile(fullPath);
                _pkgFiles.Add(pkg);
            }
        }

        public void Dispose()
        {
            foreach (var p in _pkgFiles) p.Dispose();
            _pkgFiles.Clear();
            _fileCache.Clear();
        }

        public bool Exists(string name)
        {
            string lower = name.ToLowerInvariant().Replace('\\', '/');
            if (_fileCache.ContainsKey(lower)) return true;
            if (!string.IsNullOrEmpty(_unpackDir))
            {
                string fpath = Path.Combine(_unpackDir, lower.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fpath)) return true;
            }
            foreach (var pkg in _pkgFiles)
            {
                if (pkg.Exists(lower)) return true;
            }
            return false;
        }

        public byte[] ReadBytes(string name)
        {
            string lower = name.ToLowerInvariant().Replace('\\', '/');
            if (_fileCache.TryGetValue(lower, out var cached))
            {
                return cached.pkg.ReadBytes(cached.name);
            }
            foreach (var pkg in _pkgFiles)
            {
                if (pkg.Exists(lower))
                {
                    _fileCache[lower] = (pkg, lower);
                    return pkg.ReadBytes(lower);
                }
            }
            if (!string.IsNullOrEmpty(_unpackDir))
            {
                string fpath = Path.Combine(_unpackDir, lower.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fpath))
                {
                    return File.ReadAllBytes(fpath);
                }
            }
            return null;
        }

        public Stream OpenRead(string name)
        {
            string lower = name.ToLowerInvariant().Replace('\\', '/');
            if (_fileCache.TryGetValue(lower, out var cached))
            {
                return cached.pkg.OpenRead(cached.name);
            }
            foreach (var pkg in _pkgFiles)
            {
                if (pkg.Exists(lower))
                {
                    _fileCache[lower] = (pkg, lower);
                    return pkg.OpenRead(lower);
                }
            }
            if (!string.IsNullOrEmpty(_unpackDir))
            {
                string fpath = Path.Combine(_unpackDir, lower.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fpath))
                {
                    return File.OpenRead(fpath);
                }
            }
            return null;
        }

        public string GetString(string name)
        {
            using var s = OpenRead(name);
            if (s == null) return null;
            using var sr = new StreamReader(s);
            return sr.ReadToEnd();
        }

        public IEnumerable<string> GetFilesWithExtension(string ext)
        {
            foreach (var pkg in _pkgFiles)
            {
                foreach (var n in pkg.GetEntriesWithExtension(ext))
                {
                    yield return n;
                }
            }
        }
    }
}
