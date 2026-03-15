// ConfigLoader: parses tactical_map_presets.json5 into style arrays, anchors, and overlay definitions.

using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;
using static BOAM.TacticalMap.JsonHelper;
using static BOAM.TacticalMap.ColorParser;

namespace BOAM.TacticalMap;

internal static class ConfigLoader
{
    internal static (Dictionary<string, MapStyle> mapStyles, Dictionary<string, EntityStyle> entityStyles, DisplayStyle[] displayStyles, OverlayDef[] overlayDefs, Dictionary<string, AnchorStyle> anchors)
        LoadStyles(string palettePath, MelonLogger.Instance log)
    {
        if (File.Exists(palettePath))
        {
            try
            {
                var json = StripJsonComments(File.ReadAllText(palettePath));

                var mapStylesJson = ReadArray(json, "MapStyles");
                var entityStylesJson = ReadArray(json, "EntityStyles");

                if (mapStylesJson != null && entityStylesJson != null)
                {
                    var loadedMapStyles = new Dictionary<string, MapStyle>();
                    foreach (var element in SplitJsonArray(mapStylesJson))
                    {
                        var mapStyle = ParseMapStyle(element);
                        if (mapStyle.HasValue) loadedMapStyles[mapStyle.Value.Key] = mapStyle.Value;
                    }

                    var loadedEntityStyles = new Dictionary<string, EntityStyle>();
                    foreach (var element in SplitJsonArray(entityStylesJson))
                    {
                        var entityStyle = ParseEntityStyle(element);
                        if (entityStyle.HasValue) loadedEntityStyles[entityStyle.Value.Key] = entityStyle.Value;
                    }

                    var overlayDefs = Array.Empty<OverlayDef>();
                    var overlayJson = ReadArray(json, "OverlayLayers");
                    if (overlayJson != null)
                    {
                        var definitions = new List<OverlayDef>();
                        foreach (var element in SplitJsonArray(overlayJson))
                        {
                            var overlayName = ReadString(element, "Name");
                            var overlayFlag = ReadInt(element, "Flag", 0);
                            var overlayTileFile = ReadString(element, "TileFile");
                            if (overlayName != null && overlayFlag > 0)
                                definitions.Add(new OverlayDef(overlayName, (byte)overlayFlag, overlayTileFile));
                        }
                        overlayDefs = definitions.ToArray();
                    }

                    var loadedAnchors = new Dictionary<string, AnchorStyle>();
                    var anchorJson = ReadArray(json, "Anchors");
                    if (anchorJson != null)
                    {
                        foreach (var element in SplitJsonArray(anchorJson))
                        {
                            var anchor = ParseAnchorStyle(element);
                            if (anchor.HasValue) loadedAnchors[anchor.Value.Key] = anchor.Value;
                        }
                    }
                    if (loadedAnchors.Count == 0)
                        loadedAnchors["top-right"] = new AnchorStyle { Key = "top-right", X = 1f, Y = 0f };

                    var loadedDisplayStyles = new List<DisplayStyle>();
                    var displayJson = ReadArray(json, "DisplayStyles");
                    if (displayJson != null)
                    {
                        foreach (var element in SplitJsonArray(displayJson))
                        {
                            var ds = ParseDisplayStyle(element);
                            if (ds.HasValue) loadedDisplayStyles.Add(ds.Value);
                        }
                    }

                    if (loadedDisplayStyles.Count == 0 && loadedMapStyles.Count > 0 && loadedEntityStyles.Count > 0)
                    {
                        var firstMapKey = new List<string>(loadedMapStyles.Keys)[0];
                        var firstEntityKey = new List<string>(loadedEntityStyles.Keys)[0];
                        loadedDisplayStyles.Add(new DisplayStyle { Name = "Default", MapStyleKey = firstMapKey, EntityStyleKey = firstEntityKey, AnchorKey = "top-right" });
                    }

                    if (loadedMapStyles.Count > 0 && loadedEntityStyles.Count > 0)
                    {
                        log.Msg($"[BOAM] TacticalMap — Loaded {loadedMapStyles.Count} map + {loadedEntityStyles.Count} entity + {loadedDisplayStyles.Count} display + {loadedAnchors.Count} anchor + {overlayDefs.Length} overlay layer(s)");
                        return (loadedMapStyles, loadedEntityStyles, loadedDisplayStyles.ToArray(), overlayDefs, loadedAnchors);
                    }
                    log.Warning("[BOAM] TacticalMap — presets parsed but had empty style lists");
                }
            }
            catch (Exception exception)
            {
                log.Warning($"[BOAM] TacticalMap — Failed to parse presets: {exception.Message}");
            }
        }
        else
        {
            log.Warning($"[BOAM] TacticalMap — Presets not found at {palettePath}");
        }

        var fallbackMap = new Dictionary<string, MapStyle> { ["captured"] = new MapStyle { Key = "captured", TileSize = 12 } };
        var fallbackEntity = new Dictionary<string, EntityStyle> { ["fallback"] = new EntityStyle {
            Key = "fallback",
            Background = new Color(0.1f, 0.1f, 0.1f, 0.9f),
            HeaderText = Color.white,
            FactionColors = new Dictionary<int, Color> { { 0, Color.gray } },
            IconSize = 10,
        }};
        var fallbackDisplay = new[] { new DisplayStyle { Name = "Default", MapStyleKey = "captured", EntityStyleKey = "fallback", AnchorKey = "top-right" } };
        var fallbackAnchors = new Dictionary<string, AnchorStyle> { ["top-right"] = new AnchorStyle { Key = "top-right", X = 1f, Y = 0f } };
        return (fallbackMap, fallbackEntity, fallbackDisplay, Array.Empty<OverlayDef>(), fallbackAnchors);
    }

    private static MapStyle? ParseMapStyle(string json)
    {
        var key = ReadString(json, "Key");
        if (key == null) return null;

        var tileFolder = ReadString(json, "TileFolder");

        Dictionary<string, Color> mapColors = null;
        var mapColorsJson = ReadObject(json, "MapColors");
        if (mapColorsJson != null)
        {
            mapColors = new Dictionary<string, Color>();
            var colorPairs = ReadAllStringPairs(mapColorsJson);
            foreach (var colorEntry in colorPairs)
                mapColors[colorEntry.Key] = ParseHex(colorEntry.Value);
        }

        return new MapStyle
        {
            Key = key,
            TileFolder = tileFolder,
            MapColors = mapColors,
            TileSize = ReadInt(json, "TileSize", 12),
        };
    }

    private static EntityStyle? ParseEntityStyle(string json)
    {
        var key = ReadString(json, "Key");
        if (key == null) return null;

        var backgroundHex = ReadString(json, "Background");
        var headerTextHex = ReadString(json, "HeaderText");

        var factionColors = new Dictionary<int, Color>();
        var factionColorsJson = ReadObject(json, "FactionColors");
        if (factionColorsJson != null)
        {
            var factionPairs = ReadAllStringPairs(factionColorsJson);
            foreach (var factionEntry in factionPairs)
            {
                var factionIndex = FactionNameToIndex(factionEntry.Key);
                if (factionIndex.HasValue)
                    factionColors[factionIndex.Value] = ParseHex(factionEntry.Value);
            }
        }

        return new EntityStyle
        {
            Key = key,
            Background = backgroundHex != null ? ParseHex(backgroundHex) : new Color(0.07f, 0.03f, 0.01f, 0.90f),
            HeaderText = headerTextHex != null ? ParseHex(headerTextHex) : new Color(0.97f, 0.68f, 0.40f),
            FactionColors = factionColors,
            IconSize = ReadInt(json, "IconSize", 10),
            FontSize = ReadInt(json, "FontSize", 9),
        };
    }

    private static DisplayStyle? ParseDisplayStyle(string json)
    {
        var name = ReadString(json, "Name");
        var mapStyleKey = ReadString(json, "MapStyle");
        var entityStyleKey = ReadString(json, "EntityStyle");
        if (name == null || mapStyleKey == null || entityStyleKey == null) return null;

        var anchorKey = ReadString(json, "Anchor") ?? "top-right";

        float? opacity = null;
        float? mapBrightness = null;
        if (HasKey(json, "Opacity"))
            opacity = ReadFloat(json, "Opacity", 1f);
        if (HasKey(json, "MapBrightness"))
            mapBrightness = ReadFloat(json, "MapBrightness", 1f);

        return new DisplayStyle
        {
            Name = name,
            MapStyleKey = mapStyleKey,
            EntityStyleKey = entityStyleKey,
            AnchorKey = anchorKey,
            Opacity = opacity,
            MapBrightness = mapBrightness,
        };
    }

    private static AnchorStyle? ParseAnchorStyle(string json)
    {
        var key = ReadString(json, "Key");
        if (key == null) return null;

        return new AnchorStyle
        {
            Key = key,
            X = ReadFloat(json, "X", 1f),
            Y = ReadFloat(json, "Y", 0f),
        };
    }

    private static int? FactionNameToIndex(string name)
    {
        return name switch
        {
            "Player"            => (int)Menace.SDK.FactionType.Player,
            "PlayerAI"          => (int)Menace.SDK.FactionType.PlayerAI,
            "Civilian"          => (int)Menace.SDK.FactionType.Civilian,
            "AlliedLocalForces" => (int)Menace.SDK.FactionType.AlliedLocalForces,
            "EnemyLocalForces"  => (int)Menace.SDK.FactionType.EnemyLocalForces,
            "Pirates"           => (int)Menace.SDK.FactionType.Pirates,
            "Wildlife"          => (int)Menace.SDK.FactionType.Wildlife,
            "Constructs"        => (int)Menace.SDK.FactionType.Constructs,
            "RogueArmy"         => (int)Menace.SDK.FactionType.RogueArmy,
            "Default"           => 0,
            _                   => null
        };
    }
}
