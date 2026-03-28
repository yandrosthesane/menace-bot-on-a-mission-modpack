using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;
using Menace.SDK;
using UnityEngine;
using static BOAM.TacticalMap.JsonHelper;
using static BOAM.TacticalMap.MapGenerator;
using BOAM.Boundary;
using BOAM.Utils;

namespace BOAM.TacticalMap;

/// <summary>
/// In-game IMGUI minimap overlay. Reads unit positions from TacticalMapState
/// (populated by game hooks) rather than polling the game directly.
/// Lifecycle methods are called from BoamBridge.
/// </summary>
internal class TacticalMapOverlay
{
    private MelonLogger.Instance _log;

    // Config
    private KeyCode _toggleKey = KeyCode.M;
    private KeyCode _displayKey = KeyCode.L;
    private KeyCode _mapStyleKey = KeyCode.None;
    private KeyCode _entityStyleKey = KeyCode.None;
    private KeyCode _fowKey = KeyCode.None;
    private KeyCode _anchorKey = KeyCode.None;
    private KeyCode _debugKey = KeyCode.None;
    private KeyCode _labelKey = KeyCode.None;
    private bool _fowEnabled;
    private float _margin = 12f;
    private float _headerHeight = 20f;
    private float _padding = 4f;
    private int _labelFontSize = 9;
    private int _headerFontSize = 11;
    private int _toastFontSize = 14; // legacy — toast now in static Toast class
    private float _defaultOpacity = 1f;
    private float _defaultMapBrightness = 1f;
    private bool _enabled = true;

    private const string SETTINGS_CATEGORY = "BOAM";

    // Runtime state
    private bool _inTactical;
    private bool _ready;
    private bool _overlayVisible;

    // GUI styles
    private GUIStyle _backgroundStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _headerStyle;
    private readonly Dictionary<int, GUIStyle> _dotStyles = new();
    private bool _stylesReady;
    private string _activeMapKey;
    private string _activeEntityKey;
    private int _activeDisplay;

    // Toast rendering moved to static Toast class

    // Map background
    private GUIStyle _mapBackgroundStyle;
    private Texture2D _mapBgOriginal;
    private float _appliedBrightness;

    // Display presets
    private Dictionary<string, MapStyle> _mapStyles;
    private Dictionary<string, EntityStyle> _entityStyles;
    private DisplayStyle[] _displayStyles;
    private Dictionary<string, AnchorStyle> _anchors;
    private OverlayDef[] _overlayDefs = Array.Empty<OverlayDef>();
    private string[] _mapKeys;
    private string[] _entityKeys;
    private string[] _anchorKeys;
    private string _activeAnchorKey;

    // Icon textures loaded from disk — keyed by file path for caching
    private readonly Dictionary<string, Texture2D> _iconCache = new();
    private readonly Dictionary<string, GUIStyle> _iconStyleCache = new();
    // Per-actor caches (cleared when units change)
    private readonly Dictionary<string, GUIStyle> _actorIconCache = new();
    private readonly Dictionary<string, string> _actorLabelCache = new();
    private int _lastUnitSnapshotHash;
    private string _iconsDir;
    private bool _showLabels = true;

    // Mod folder for configs and generated maps
    private string _modFolder;

    internal void Initialize(MelonLogger.Instance logger, string modFolder)
    {
        _log = logger;
        _modFolder = modFolder;

        // User config in persistent assets takes precedence over mod default.
        // Path: BOAM_PERSISTENT_ASSETS env var, or <game_dir>/UserData/CustomPersistentAssets/BOAM
        var persistentDir = Environment.GetEnvironmentVariable("BOAM_PERSISTENT_ASSETS");
        if (string.IsNullOrEmpty(persistentDir))
            persistentDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "UserData", "BOAM");
        var userConfigDir = Path.Combine(persistentDir, "configs");
        var defaultConfigDir = Path.Combine(modFolder, "configs");

        var configPath = ConfigResolver.Resolve(
            Path.Combine(userConfigDir, "tactical_map.json5"),
            Path.Combine(defaultConfigDir, "tactical_map.json5"), "TacticalMap config", _log);
        LoadConfig(configPath);

        var presetsPath = ConfigResolver.Resolve(
            Path.Combine(userConfigDir, "tactical_map_presets.json5"),
            Path.Combine(defaultConfigDir, "tactical_map_presets.json5"), "TacticalMap presets", _log);
        (_mapStyles, _entityStyles, _displayStyles, _overlayDefs, _anchors) = ConfigLoader.LoadStyles(presetsPath, logger);
        _mapKeys = _mapStyles.Keys.ToArray();
        _entityKeys = _entityStyles.Keys.ToArray();
        _anchorKeys = _anchors.Keys.ToArray();
        _activeMapKey = _displayStyles[0].MapStyleKey;
        _activeEntityKey = _displayStyles[0].EntityStyleKey;
        _activeAnchorKey = _displayStyles[0].AnchorKey;

        _iconsDir = Path.Combine(persistentDir, "icons");
        EnsureIcons(modFolder);
        RegisterSettings();
        _log.Msg($"[BOAM] TacticalMap initialized (toggle={_toggleKey}, {_displayStyles.Length} presets)");
    }

    /// <summary>
    /// Check if icons directory has content; if missing, ask the tactical engine to generate them.
    /// The game runs under Wine so we can't spawn native binaries directly — delegate to the
    /// tactical engine which runs natively and has access to boam-icons.
    /// </summary>
    private void EnsureIcons(string modFolder)
    {
        try
        {
            if (Directory.Exists(_iconsDir) && Directory.GetFiles(_iconsDir, "*.png", SearchOption.AllDirectories).Length > 0)
                return;

            _log.Warning($"[BOAM] No icons found in {_iconsDir}");
            Toast.Show("BOAM: No icons found — run 'boam-icons --force' or start the tactical engine", 8f);
        }
        catch (Exception ex)
        {
            _log.Warning($"[BOAM] Icon check failed: {ex.Message}");
        }
    }

    internal void OnSceneLoaded(string sceneName)
    {
        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _ready = false;
            _mapBackgroundStyle = null;
            _mapBgOriginal = null;
            _appliedBrightness = -1f;
        }
        else
        {
            _inTactical = false;
            _ready = false;
        }
    }

    internal void OnTacticalReady()
    {
        _ready = true;
        UpdateMapBackground();
        var sessionDir = TacticalMapState.BattleSessionDir;
        if (!string.IsNullOrEmpty(sessionDir))
        {
            Toast.Show($"BOAM — {sessionDir}", 5f);
        }
    }

    internal void OnUpdate()
    {
        if (!_inTactical || !_enabled) return;

        if (Input.GetKeyDown(_toggleKey))
        {
            _overlayVisible = !_overlayVisible;
            _log.Msg($"[BOAM] TacticalMap — Overlay {(_overlayVisible ? "shown" : "hidden")}");
        }

        if (Input.GetKeyDown(_displayKey))
        {
            _activeDisplay = (_activeDisplay + 1) % _displayStyles.Length;
            var ds = _displayStyles[_activeDisplay];
            _activeMapKey = ds.MapStyleKey;
            _activeEntityKey = ds.EntityStyleKey;
            _activeAnchorKey = ds.AnchorKey;
            _stylesReady = false;
            Toast.Show($"TacticalMap — {ds.Name}", 2f);
            if (_ready) UpdateMapBackground();
        }

        if (Input.GetKeyDown(_mapStyleKey))
        {
            int idx = Array.IndexOf(_mapKeys, _activeMapKey);
            _activeMapKey = _mapKeys[(idx + 1) % _mapKeys.Length];
            Toast.Show($"TacticalMap — Loading {_activeMapKey}...", 30f);
            if (_ready) UpdateMapBackground();
            Toast.Show($"TacticalMap — Map: {_activeMapKey}", 2f);
        }

        if (Input.GetKeyDown(_entityStyleKey))
        {
            int idx = Array.IndexOf(_entityKeys, _activeEntityKey);
            _activeEntityKey = _entityKeys[(idx + 1) % _entityKeys.Length];
            _stylesReady = false;
        }

        if (Input.GetKeyDown(_fowKey))
            _fowEnabled = !_fowEnabled;

        if (Input.GetKeyDown(_labelKey))
        {
            _showLabels = !_showLabels;
            Toast.Show($"TacticalMap — Labels: {(_showLabels ? "ON" : "OFF")}", 2f);
        }

        if (Input.GetKeyDown(_anchorKey))
        {
            int idx = Array.IndexOf(_anchorKeys, _activeAnchorKey);
            _activeAnchorKey = _anchorKeys[(idx + 1) % _anchorKeys.Length];
            Toast.Show($"TacticalMap — Anchor: {_activeAnchorKey}", 2f);
        }
    }

    internal void OnGUI()
    {
        try { OnGUIInner(); }
        catch (Exception ex) { _log?.Error($"[BOAM] TacticalMap — OnGUI crash: {ex}"); }
    }

    private void OnGUIInner()
    {
        if (!_inTactical || !_ready || !_overlayVisible || !_enabled) return;

        var units = TacticalMapState.GetUnitsSnapshot();
        if (units.Count == 0) return;

        int mapTotalX = TacticalMapState.TilesX;
        int mapTotalZ = TacticalMapState.TilesZ;
        if (mapTotalX <= 0 || mapTotalZ <= 0) return;

        EnsureStyles();

        float opacity = EffectiveOpacity;
        float brightness = EffectiveMapBrightness;
        var savedColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, opacity);
        EnsureMapBrightness(brightness);

        int tileSize = _mapStyles[_activeMapKey].TileSize;

        // Full map viewport (no cropping — show entire grid)
        float mapWidth = mapTotalX * tileSize;
        float mapHeight = mapTotalZ * tileSize;
        float panelWidth = mapWidth + _padding * 2;
        float panelHeight = mapHeight + _padding * 2 + _headerHeight;

        var anchor = _anchors[_activeAnchorKey];
        float panelX = _margin + anchor.X * (Screen.width  - panelWidth  - 2 * _margin);
        float panelY = _margin + anchor.Y * (Screen.height - panelHeight - 2 * _margin);
        float mapLeft = panelX + _padding;
        float mapTop = panelY + _headerHeight;

        GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "", _backgroundStyle);

        int round = TacticalMapState.CurrentRound;
        GUI.Label(new Rect(mapLeft, panelY + 2f, mapWidth, 18f),
                  $"R{round} | {_displayStyles[_activeDisplay].Name} | O:{opacity:F1} B:{brightness:F1}{(_fowEnabled ? " | FoW" : "")}", _headerStyle);

        if (_mapBackgroundStyle != null)
            GUI.Box(new Rect(mapLeft, mapTop, mapWidth, mapHeight), "", _mapBackgroundStyle);

        // Units — same icon resolution as heatmap: leader → template → faction
        float iconSz = _entityStyles[_activeEntityKey].IconSize;
        // Invalidate per-actor caches when unit set changes
        int snapshotHash = units.Count > 0 ? units[0].GetHashCode() ^ units.Count : 0;
        if (snapshotHash != _lastUnitSnapshotHash)
        {
            _actorIconCache.Clear();
            _actorLabelCache.Clear();
            _lastUnitSnapshotHash = snapshotHash;
        }
        try
        {
            foreach (var unit in units)
            {
                if (_fowEnabled && !unit.KnownToPlayer) continue;

                float centerX = mapLeft + (unit.X + 0.5f) * tileSize;
                float centerY = mapTop + (mapTotalZ - unit.Y - 0.5f) * tileSize;

                float unitLeft = centerX - iconSz * 0.5f;
                float unitTop = centerY - iconSz * 0.5f;

                // Cached icon resolution
                if (!_actorIconCache.TryGetValue(unit.Actor, out var style))
                {
                    style = ResolveUnitIcon(unit);
                    _actorIconCache[unit.Actor] = style;
                }
                GUI.Box(new Rect(unitLeft, unitTop, iconSz, iconSz), "", style);

                if (_showLabels && _labelStyle.fontSize > 0)
                {
                    // Cached label
                    if (!_actorLabelCache.TryGetValue(unit.Actor, out var label))
                    {
                        label = NamingHelper.ShortLabel(unit.Label);
                        _actorLabelCache[unit.Actor] = label;
                    }
                    _labelStyle.normal.textColor = GetFactionColor(unit.FactionIndex);
                    float labelW = _labelStyle.fontSize * 8f;
                    float labelH = _labelStyle.fontSize * 1.4f;
                    float labelX = centerX - labelW * 0.5f;
                    float labelY = unitTop + iconSz + 1f;
                    GUI.Label(new Rect(labelX, labelY, labelW, labelH), label, _labelStyle);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[BOAM] TacticalMap — OnGUI unit draw error: {ex.Message}");
        }

        GUI.color = savedColor;
        GUIUtility.keyboardControl = 0;
    }

    // --- Map Background ---

    private void UpdateMapBackground()
    {
        _mapBackgroundStyle = null;
        _mapBgOriginal = null;
        _appliedBrightness = -1f;

        int mapTotalX = TacticalMapState.TilesX;
        int mapTotalZ = TacticalMapState.TilesZ;
        if (mapTotalX <= 0 || mapTotalZ <= 0) return;

        var mapStyle = _mapStyles[_activeMapKey];
        Texture2D fullMap = ResolveFullMapTexture(mapStyle, mapTotalX, mapTotalZ);
        if (fullMap == null)
        {
            _log.Warning($"[BOAM] TacticalMap — ResolveFullMapTexture returned null for style '{mapStyle.Key}'");
            return;
        }
        _log.Msg($"[BOAM] TacticalMap — Map background resolved: {fullMap.width}x{fullMap.height} for style '{mapStyle.Key}'");
        SetBackgroundTexture(fullMap);
    }

    private Texture2D ResolveFullMapTexture(MapStyle mapStyle, int mapTotalX, int mapTotalZ)
    {
        // Try saved PNG first
        var safeKey = mapStyle.Key.ToLowerInvariant().Replace(' ', '_');
        var pngPath = Path.Combine(_modFolder, $"map_{safeKey}.png");

        if (File.Exists(pngPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(pngPath);
                var texture = new Texture2D(2, 2);
                texture.hideFlags = HideFlags.HideAndDontSave;
                if (ImageConversion.LoadImage(texture, bytes))
                {
                    texture.filterMode = FilterMode.Point;
                    return texture;
                }
                UnityEngine.Object.Destroy(texture);
            }
            catch (Exception e)
            {
                _log.Warning($"[BOAM] TacticalMap — Failed to load {pngPath}: {e.Message}");
            }
        }

        // Try generating from tile data
        var tileData = TacticalMapState.TileDataArray;
        if (tileData != null)
        {
            Texture2D texture = null;
            try
            {
                if (mapStyle.TileFolder != null)
                    texture = GenerateFromTiles(tileData, mapTotalX, mapTotalZ,
                        TacticalMapState.HeightMin, TacticalMapState.HeightMax,
                        mapStyle.TileFolder, _modFolder, _overlayDefs);
                else if (mapStyle.MapColors != null)
                    texture = GenerateFromColors(tileData, mapTotalX, mapTotalZ,
                        TacticalMapState.HeightMin, TacticalMapState.HeightMax,
                        mapStyle.MapColors, _overlayDefs);
            }
            catch (Exception e)
            {
                _log.Warning($"[BOAM] TacticalMap — Failed to generate map: {e.Message}");
            }

            if (texture != null)
            {
                var pngBytes = ImageConversion.EncodeToPNG(texture);
                File.WriteAllBytes(pngPath, pngBytes);
                _log.Msg($"[BOAM] TacticalMap — Generated {pngPath} ({texture.width}x{texture.height})");
                return texture;
            }
        }

        // Fallback: captured game texture
        return TacticalMapState.MapTexture;
    }

    private void SetBackgroundTexture(Texture2D texture)
    {
        _mapBgOriginal = texture;
        _appliedBrightness = -1f;
        _mapBackgroundStyle = null;
    }

    private void EnsureMapBrightness(float brightness)
    {
        if (_mapBgOriginal == null) return;
        if (_mapBackgroundStyle != null && Math.Abs(brightness - _appliedBrightness) < 0.001f) return;

        Texture2D display;
        if (Math.Abs(brightness - 1f) < 0.001f)
        {
            display = _mapBgOriginal;
        }
        else
        {
            var srcPixels = _mapBgOriginal.GetPixels();
            for (int i = 0; i < srcPixels.Length; i++)
            {
                var c = srcPixels[i];
                srcPixels[i] = new Color(
                    Math.Min(c.r * brightness, 1f),
                    Math.Min(c.g * brightness, 1f),
                    Math.Min(c.b * brightness, 1f),
                    c.a);
            }
            display = new Texture2D(_mapBgOriginal.width, _mapBgOriginal.height);
            display.hideFlags = HideFlags.HideAndDontSave;
            display.filterMode = FilterMode.Point;
            display.SetPixels(srcPixels);
            display.Apply();
        }

        _mapBackgroundStyle = new GUIStyle();
        _mapBackgroundStyle.normal.background = display;
        _mapBackgroundStyle.border = new RectOffset(0, 0, 0, 0);
        _mapBackgroundStyle.padding = new RectOffset(0, 0, 0, 0);
        _mapBackgroundStyle.margin = new RectOffset(0, 0, 0, 0);
        _appliedBrightness = brightness;
    }

    // --- Styles ---

    private void EnsureStyles()
    {
        if (_backgroundStyle != null && _backgroundStyle.normal.background == null)
            _stylesReady = false;

        if (_stylesReady) return;
        _stylesReady = true;

        var entityStyle = _entityStyles[_activeEntityKey];

        _backgroundStyle = new GUIStyle(GUI.skin.box);
        _backgroundStyle.normal.background = MakeTexture(entityStyle.Background);

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = entityStyle.FontSize >= 0 ? entityStyle.FontSize : _labelFontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };
        _labelStyle.normal.textColor = Color.white;

        _headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = _headerFontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _headerStyle.normal.textColor = entityStyle.HeaderText;

        _dotStyles.Clear();
        foreach (var factionEntry in entityStyle.FactionColors)
        {
            var style = new GUIStyle(GUI.skin.box);
            style.normal.background = MakeTexture(factionEntry.Value);
            _dotStyles[factionEntry.Key] = style;
        }
    }

    private GUIStyle GetDotStyle(int factionIndex)
    {
        if (_dotStyles.TryGetValue(factionIndex, out var style))
            return style;

        var newStyle = new GUIStyle(GUI.skin.box);
        newStyle.normal.background = MakeTexture(Color.gray);
        _dotStyles[factionIndex] = newStyle;
        return newStyle;
    }

    private static Texture2D MakeTexture(Color color)
    {
        var texture = new Texture2D(1, 1);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private Color GetFactionColor(int factionIndex)
    {
        var entityStyle = _entityStyles[_activeEntityKey];
        return entityStyle.FactionColors.TryGetValue(factionIndex, out var color) ? color : Color.gray;
    }

    // --- Settings ---

    private float EffectiveOpacity
    {
        get
        {
            var ds = _displayStyles[_activeDisplay];
            return ds.Opacity ?? ModSettings.Get<float>(SETTINGS_CATEGORY, "MinimapOpacity");
        }
    }

    private float EffectiveMapBrightness
    {
        get
        {
            var ds = _displayStyles[_activeDisplay];
            return ds.MapBrightness ?? ModSettings.Get<float>(SETTINGS_CATEGORY, "MinimapBrightness");
        }
    }

    private void RegisterSettings()
    {
        ModSettings.Register(SETTINGS_CATEGORY, settings =>
        {
            settings.AddHeader("Tactical Map");
            settings.AddSlider("MinimapOpacity", "Minimap Opacity", 0.1f, 1f, _defaultOpacity);
            settings.AddSlider("MinimapBrightness", "Minimap Brightness", 0.1f, 4f, _defaultMapBrightness);
        });
    }

    // --- Icons ---
    // Resolution order matches HeatmapRenderer: leader → template → faction fallback.

    /// <summary>Try to load a PNG from disk, caching the result.</summary>
    private Texture2D TryLoadIcon(string path)
    {
        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        if (!File.Exists(path))
        {
            _iconCache[path] = null;
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2);
            tex.hideFlags = HideFlags.HideAndDontSave;
            if (ImageConversion.LoadImage(tex, bytes))
            {
                tex.filterMode = FilterMode.Bilinear;
                _iconCache[path] = tex;
                return tex;
            }
            UnityEngine.Object.Destroy(tex);
        }
        catch { }

        _iconCache[path] = null;
        return null;
    }

    /// <summary>Resolve icon for a unit: leader → template → faction fallback.</summary>
    private GUIStyle ResolveUnitIcon(OverlayUnit unit)
    {
        var tplDir = Path.Combine(_iconsDir, "templates");

        // 1. Try leader icon
        if (!string.IsNullOrEmpty(unit.Leader))
        {
            var leaderPath = Path.Combine(tplDir, unit.Leader.ToLower() + ".png");
            var leaderTex = TryLoadIcon(leaderPath);
            if (leaderTex != null) return GetOrCreateStyle(leaderPath, leaderTex);
        }

        // 2. Try template icon
        var tplName = NamingHelper.TemplateFileName(unit.Template);
        if (!string.IsNullOrEmpty(tplName))
        {
            var tplPath = Path.Combine(tplDir, tplName + ".png");
            var tplTex = TryLoadIcon(tplPath);
            if (tplTex != null) return GetOrCreateStyle(tplPath, tplTex);
        }

        // 3. Faction fallback
        var facPath = Path.Combine(_iconsDir, "factions", NamingHelper.FactionIconName(unit.FactionIndex) + ".png");
        var facTex = TryLoadIcon(facPath);
        if (facTex != null) return GetOrCreateStyle(facPath, facTex);

        // 4. Colored dot fallback
        return GetDotStyle(unit.FactionIndex);
    }

    private GUIStyle GetOrCreateStyle(string key, Texture2D tex)
    {
        if (_iconStyleCache.TryGetValue(key, out var cached))
            return cached;

        var style = new GUIStyle();
        style.normal.background = tex;
        style.border = new RectOffset(0, 0, 0, 0);
        style.padding = new RectOffset(0, 0, 0, 0);
        style.margin = new RectOffset(0, 0, 0, 0);
        _iconStyleCache[key] = style;
        return style;
    }

    // --- Config ---

    private void LoadConfig(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                _log.Msg($"[BOAM] TacticalMap — Config not found at {configPath}, using defaults");
                return;
            }

            var json = StripJsonComments(File.ReadAllText(configPath));

            if (ReadString(json, "ToggleKey") is string toggleStr && Enum.TryParse<KeyCode>(toggleStr, out var parsedToggle))
                _toggleKey = parsedToggle;
            if (ReadString(json, "DisplayKey") is string displayStr && Enum.TryParse<KeyCode>(displayStr, out var parsedDisplay))
                _displayKey = parsedDisplay;
            if (ReadString(json, "MapStyleKey") is string mapStr && Enum.TryParse<KeyCode>(mapStr, out var parsedMap))
                _mapStyleKey = parsedMap;
            if (ReadString(json, "EntityStyleKey") is string entityStr && Enum.TryParse<KeyCode>(entityStr, out var parsedEntity))
                _entityStyleKey = parsedEntity;
            if (ReadString(json, "DebugKey") is string debugStr && Enum.TryParse<KeyCode>(debugStr, out var parsedDebug))
                _debugKey = parsedDebug;
            if (ReadString(json, "FogOfWarKey") is string fowStr && Enum.TryParse<KeyCode>(fowStr, out var parsedFow))
                _fowKey = parsedFow;
            if (ReadString(json, "AnchorKey") is string anchorStr && Enum.TryParse<KeyCode>(anchorStr, out var parsedAnchor))
                _anchorKey = parsedAnchor;
            if (ReadString(json, "LabelKey") is string labelStr && Enum.TryParse<KeyCode>(labelStr, out var parsedLabel))
                _labelKey = parsedLabel;

            _fowEnabled = ReadBool(json, "FogOfWarDefault", false);
            _showLabels = ReadBool(json, "LabelsDefault", true);
            _enabled = ReadBool(json, "Enabled", true);
            _margin = ReadInt(json, "Margin", 12);
            _headerHeight = ReadInt(json, "HeaderHeight", 20);
            _padding = ReadInt(json, "Padding", 4);
            _labelFontSize = ReadInt(json, "LabelFontSize", 9);
            _headerFontSize = ReadInt(json, "HeaderFontSize", 11);
            _toastFontSize = ReadInt(json, "ToastFontSize", 14);
            _defaultOpacity = ReadFloat(json, "Opacity", 1f);
            _defaultMapBrightness = ReadFloat(json, "MapBrightness", 1f);

            _log.Msg($"[BOAM] TacticalMap — Config loaded from {configPath}");
        }
        catch (Exception exception)
        {
            _log.Warning($"[BOAM] TacticalMap — Failed to load config: {exception.Message}");
        }
    }
}
