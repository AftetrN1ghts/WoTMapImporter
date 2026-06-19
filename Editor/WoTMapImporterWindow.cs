using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WoTMapImporter.Editor.Data;
using WoTMapImporter.Editor.Package;
using WoTMapImporter.Editor.Utils;
using WoTMapImporter.Editor.Xml;

namespace WoTMapImporter.Editor
{
    /// <summary>
    /// EditorWindow for selecting a WoT installation, parsing the map list,
    /// and importing maps into the current Unity scene.
    ///
    /// IMPORTANT: This file deliberately uses ONLY the most basic
    /// EditorGUILayout overloads (no GUILayoutOption arguments anywhere)
    /// because some Unity versions have alternative overloads like
    /// Toggle(bool, Texture, ...) that the compiler prefers over the
    /// params GUILayoutOption[] overload. Avoiding the GUILayoutOption[]
    /// overloads altogether eliminates the ambiguity.
    /// </summary>
    public class WoTMapImporterWindow : EditorWindow
    {
        private string _wotPath = @"C:\Games\World_of_Tanks";
        private string _outputFolder = "Assets/WoTImported";
        private bool _loadObjects = true;
        private bool _loadNormals = true;
        private bool _loadWetness = false;
        private int _maxResolution = 4097;
        private bool _mapsParsed;
        private Vector2 _scroll;
        private List<MapInfo> _maps = new List<MapInfo>();
        private int _selectedMap = -1;
        private string _filter = "";
        private string _status = "";
        private bool _isImporting;

        [MenuItem("Tools/WoT Map Importer")]
        public static void Open()
        {
            var w = GetWindow<WoTMapImporterWindow>(true, "WoT Map Importer", true);
            w.minSize = new Vector2(520, 600);
            w.Show();
        }

        private void OnEnable()
        {
            _wotPath = EditorPrefs.GetString("WoTMapImporter.wotPath", _wotPath);
            _outputFolder = EditorPrefs.GetString("WoTMapImporter.outputFolder", _outputFolder);
        }

        private void OnDisable()
        {
            EditorPrefs.SetString("WoTMapImporter.wotPath", _wotPath);
            EditorPrefs.SetString("WoTMapImporter.outputFolder", _outputFolder);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("World of Tanks - Map Importer", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            DrawSettings();
            EditorGUILayout.Space();

            DrawParseSection();
            EditorGUILayout.Space();

            DrawMapsList();
            EditorGUILayout.Space();

            DrawImportButton();
            EditorGUILayout.Space();

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Paths", EditorStyles.boldLabel);
            _wotPath = EditorGUILayout.TextField("WoT Installation", _wotPath);
            if (GUILayout.Button("Browse..."))
            {
                string sel = EditorUtility.OpenFolderPanel("Select World of Tanks folder", _wotPath, "");
                if (!string.IsNullOrEmpty(sel)) _wotPath = sel;
            }
            _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            _loadObjects = EditorGUILayout.Toggle("Load static objects", _loadObjects);
            _loadNormals = EditorGUILayout.Toggle("Load terrain normals", _loadNormals);
            _loadWetness = EditorGUILayout.Toggle("Load wetness/roughness map", _loadWetness);
            _maxResolution = EditorGUILayout.IntPopup(
                "Max heightmap resolution",
                _maxResolution,
                new[] { "1025", "2049", "4097" },
                new[] { 1025, 2049, 4097 });
        }

        private void DrawParseSection()
        {
            if (GUILayout.Button("Parse WoT (load maps)"))
                Parse();
            if (_mapsParsed)
                EditorGUILayout.LabelField("Maps found: " + _maps.Count);
        }

        private void DrawMapsList()
        {
            if (!_mapsParsed || _maps.Count == 0) return;

            EditorGUILayout.LabelField("Maps", EditorStyles.boldLabel);
            _filter = EditorGUILayout.TextField("Filter", _filter);

            // Most basic BeginScrollView overload - just a Vector2. No options.
            // The window itself has minSize 600 height so the scrollview is naturally
            // tall enough.
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            for (int i = 0; i < _maps.Count; i++)
            {
                var m = _maps[i];
                if (!string.IsNullOrEmpty(_filter))
                {
                    string lowerFilter = _filter.ToLowerInvariant();
                    string lowerName = m.Name.ToLowerInvariant();
                    string lowerLocalized = m.LocalizedName.ToLowerInvariant();
                    if (lowerName.IndexOf(lowerFilter, System.StringComparison.Ordinal) < 0 &&
                        lowerLocalized.IndexOf(lowerFilter, System.StringComparison.Ordinal) < 0)
                        continue;
                }

                EditorGUILayout.BeginHorizontal();
                bool wasSelected = _selectedMap == i;
                bool nowSelected = EditorGUILayout.Toggle(wasSelected);
                if (nowSelected != wasSelected)
                    _selectedMap = nowSelected ? i : -1;
                EditorGUILayout.LabelField(m.LogName + "  (" + m.Name + ")");
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawImportButton()
        {
            using (new EditorGUI.DisabledScope(_isImporting || _selectedMap < 0))
            {
                if (GUILayout.Button("Import Selected Map"))
                    DoImport();
            }
        }

        // ================== actions ==================

        private void Parse()
        {
            _status = "";
            if (!Directory.Exists(_wotPath))
            {
                _status = "Path does not exist: " + _wotPath + "\nCheck 'WoT Installation' field.";
                WoTLogger.Error(_status);
                return;
            }
            string resPath = Path.Combine(_wotPath, "res");
            string pkgsPath = Path.Combine(resPath, "packages");
            string versionXml = Path.Combine(_wotPath, "version.xml");
            if (!Directory.Exists(resPath))
            {
                _status = "'res' folder not found in " + _wotPath +
                          "\nThis is not a valid WoT installation (expected res/packages/scripts.pkg).";
                WoTLogger.Error(_status);
                return;
            }
            if (!Directory.Exists(pkgsPath))
            {
                _status = "'res/packages' folder not found.\nWoT may not be fully installed.";
                WoTLogger.Error(_status);
                return;
            }

            string scriptsPkg = Path.Combine(pkgsPath, "scripts.pkg");
            if (!File.Exists(scriptsPkg))
            {
                // Show first few .pkg files we found, so user knows what's there.
                var pkgFiles = Directory.GetFiles(pkgsPath, "*.pkg");
                string fileList = pkgFiles.Length == 0
                    ? "(no .pkg files found)"
                    : string.Join(", ", System.Linq.Enumerable.Take(pkgFiles, 5)
                        .Select(System.IO.Path.GetFileName));
                _status = "scripts.pkg not found in res/packages.\nFound: " + fileList;
                WoTLogger.Error(_status);
                return;
            }

            WoTLogger.Info("Parsing WoT at " + _wotPath);
            try
            {
                WoTVersion version = default;
                if (File.Exists(versionXml))
                {
                    var versionDoc = new System.Xml.XmlDocument();
                    versionDoc.Load(versionXml);
                    var versionText = versionDoc.SelectSingleNode("/version")?.InnerText
                                      ?? versionDoc.DocumentElement?.InnerText ?? "";
                    string realm = versionDoc.SelectSingleNode("/version/meta/realm")?.InnerText?.Trim() ?? "RU";
                    WoTVersion.TryParse(versionText, realm, out version);
                    WoTLogger.Info("WoT version: " + version + " (" + realm + ")");
                }
                else
                {
                    WoTLogger.Warn("version.xml not found, proceeding without version info");
                }

                var pkgMgr = new WoTPackageManager(pkgsPath, new[] { "scripts.pkg" });
                try
                {
                    _maps = MapListParser.ParseMaps(pkgMgr);
                    _mapsParsed = true;
                    if (_maps.Count == 0)
                    {
                        _status = "Found 0 maps in scripts.pkg. Check that:\n" +
                                  "  - scripts.pkg is the correct file\n" +
                                  "  - It contains scripts/arena_defs/_list_.xml\n" +
                                  "  - Logs: %TEMP%/WoTMapImporter.log";
                    }
                    else
                    {
                        _status = "Found " + _maps.Count + " maps";
                    }
                    WoTLogger.Info(_status);
                }
                finally
                {
                    pkgMgr.Dispose();
                }
            }
            catch (System.Exception e)
            {
                _status = "Parse failed: " + e.Message + "\nLogs: %TEMP%/WoTMapImporter.log";
                WoTLogger.Error(_status);
                WoTLogger.Error(e.StackTrace);
            }
        }

        private void DoImport()
        {
            if (_selectedMap < 0 || _selectedMap >= _maps.Count) return;
            var mapInfo = _maps[_selectedMap];
            string wotResPath = Path.Combine(_wotPath, "res", "packages");
            if (!Directory.Exists(wotResPath))
            {
                _status = "res/packages not found";
                return;
            }
            _isImporting = true;
            try
            {
                var settings = new WoTMapImporter.ImportSettings
                {
                    LoadObjects = _loadObjects,
                    LoadNormals = _loadNormals,
                    LoadWetness = _loadWetness,
                    MaxHeightmapResolution = _maxResolution,
                };

                EditorUtility.DisplayProgressBar("WoT Map Importer", "Starting import...", 0f);
                var result = WoTMapImporter.ImportMap(mapInfo, wotResPath, _outputFolder, settings,
                    (p, msg) =>
                    {
                        EditorUtility.DisplayProgressBar("WoT Map Importer", msg, p);
                    });

                if (result.Errors.Count > 0)
                {
                    _status = "Errors: " + string.Join("; ", result.Errors);
                }
                else
                {
                    _status = "Imported " + mapInfo.LogName + " in " +
                              result.Duration.TotalSeconds.ToString("F2") + "s. " +
                              "Warnings: " + result.Warnings.Count;
                }
                if (result.Root != null)
                    Selection.activeGameObject = result.Root;
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _isImporting = false;
            }
        }
    }
}
