using System.Collections.Generic;
using UnityEngine;

namespace BOAM.TacticalMap;

internal struct TileData
{
    public float Height;
    public byte Flags; // bit 0: road, 1: blocked, 2: vegetation, 3: structure
}

internal struct OverlayUnit
{
    public string Label;
    public string Actor;    // stable UUID (e.g. "player.carda")
    public int FactionIndex;
    public int X, Y;
    public bool KnownToPlayer;
    public string Template; // template name for icon lookup (e.g. "enemy.alien_stinger")
    public string Leader;   // leader nickname for icon lookup (e.g. "carda")
}

internal struct MapStyle
{
    public string Key;
    public string TileFolder;   // non-null = compose from tile PNGs
    public Dictionary<string, Color> MapColors; // non-null = generate from flat colors
    public int TileSize;        // pixels per tile on screen
}

internal struct EntityStyle
{
    public string Key;
    public Color Background;
    public Color HeaderText;
    public Dictionary<int, Color> FactionColors;
    public float IconSize;
    public int FontSize;
}

internal struct AnchorStyle
{
    public string Key;
    public float X; // 0.0 = left, 1.0 = right
    public float Y; // 0.0 = top, 1.0 = bottom
}

internal struct DisplayStyle
{
    public string Name;
    public string MapStyleKey;
    public string EntityStyleKey;
    public string AnchorKey;
    public float? Opacity;
    public float? MapBrightness;
}

// Links a feature overlay to its TileData.Flags bitmask and tile-mode PNG.
internal readonly record struct OverlayDef(string Name, byte FlagMask, string TileFile);

// Loaded map background data returned by MapDataLoader.
internal struct MapData
{
    public Texture2D BackgroundTexture;
    public int TotalX, TotalZ;
    public TileData[] Tiles;
    public float HeightMin, HeightMax;
}
