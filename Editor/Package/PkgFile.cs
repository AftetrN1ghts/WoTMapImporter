using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace WoTMapImporter.Editor.Package
{
    /// <summary>
    /// A `.pkg` file is just a ZIP archive. Wraps System.IO.Compression.ZipArchive to make
    /// lookups by lowercased name fast.
    /// </summary>
    public sealed class PkgFile : IDisposable
    {
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, ZipArchiveEntry> _entries;
        private bool _disposed;

        public string Name { get; }

        public PkgFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Pkg file not found", path);
            Name = Path.GetFileName(path);
            _archive = ZipFile.OpenRead(path);
            _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _archive.Entries)
            {
                _entries[e.FullName.Replace('\\', '/')] = e;
            }
        }

        public bool Exists(string name)
        {
            return _entries.ContainsKey(name.Replace('\\', '/'));
        }

        public byte[] ReadBytes(string name)
        {
            if (!_entries.TryGetValue(name.Replace('\\', '/'), out var entry))
                return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public Stream OpenRead(string name)
        {
            if (!_entries.TryGetValue(name.Replace('\\', '/'), out var entry))
                return null;
            return entry.Open();
        }

        public IEnumerable<string> GetEntries()
        {
            return _entries.Keys;
        }

        public IEnumerable<string> GetEntriesWithExtension(string ext)
        {
            foreach (var name in _entries.Keys)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    yield return name;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _archive?.Dispose();
        }
    }
}
