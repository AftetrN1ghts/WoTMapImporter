using System;
using System.IO;
using System.Text;

namespace WoTMapImporter.Editor.Utils
{
    /// <summary>
    /// Simple logger for WoT importer. Logs to both Unity console and a log file in Temp folder.
    /// </summary>
    public static class WoTLogger
    {
        private static readonly StringBuilder _buffer = new StringBuilder();
        private static readonly object _lock = new object();
        private static string _logPath;

        public static string LogPath
        {
            get
            {
                if (_logPath == null)
                {
                    string tempDir = Path.GetTempPath();
                    _logPath = Path.Combine(tempDir, "WoTMapImporter.log");
                }
                return _logPath;
            }
        }

        public static void Info(string msg) => Write("INFO", msg);
        public static void Debug(string msg) => Write("DEBUG", msg);
        public static void Warn(string msg) => Write("WARN ", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}";
            lock (_lock)
            {
                _buffer.AppendLine(line);
                try
                {
                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
                catch { /* best effort */ }
            }
            UnityEngine.Debug.Log(line);
        }

        public static void Flush()
        {
            lock (_lock)
            {
                UnityEngine.Debug.Log($"[WoTMapImporter] Full log:\n{_buffer}");
                _buffer.Clear();
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _buffer.Clear();
                try { File.Delete(LogPath); } catch { }
            }
        }
    }
}
