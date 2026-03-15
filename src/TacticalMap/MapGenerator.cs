// MapGenerator: renders TileData[] to Texture2D. Base layers (height gradient)
// are defined here; overlay layers are data-driven via OverlayDef[] from config.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BOAM.TacticalMap;

internal static class MapGenerator
{
    static readonly LayerDef HeightLow   = new("MinHeight",  "height_low.png");
    static readonly LayerDef HeightHigh  = new("MaxHeight",  "height_high.png");
    static readonly LayerDef BlockedLow  = new("BlockedMin", "blocked.png");
    static readonly LayerDef BlockedHigh = new("BlockedMax", null);

    const int COLOR_PIXELS_PER_TILE = 8;

    internal static Texture2D GenerateFromColors(TileData[] tileData, int mapWidth, int mapHeight,
        float heightMin, float heightMax, Dictionary<string, Color> colors, OverlayDef[] overlayDefs)
    {
        int pixelsPerTile = COLOR_PIXELS_PER_TILE;
        Color[] ResolveLayer(LayerDef layer) =>
            colors.TryGetValue(layer.ColorKey, out var color) ? MakeSolidBlock(pixelsPerTile, color) : null;

        var resolvedOverlays = new List<(byte Mask, Color[] Pixels)>();
        foreach (var overlay in overlayDefs)
            if (colors.TryGetValue(overlay.Name, out var color))
                resolvedOverlays.Add((overlay.FlagMask, MakeSolidBlock(pixelsPerTile, color)));

        return Render(tileData, mapWidth, mapHeight, heightMin, heightMax, pixelsPerTile,
            ResolveLayer(HeightLow),  ResolveLayer(HeightHigh),
            ResolveLayer(BlockedLow), ResolveLayer(BlockedHigh),
            resolvedOverlays.ToArray());
    }

    internal static Texture2D GenerateFromTiles(TileData[] tileData, int mapWidth, int mapHeight,
        float heightMin, float heightMax, string folder, string modFolder, OverlayDef[] overlayDefs)
    {
        var tileDirectory = Path.Combine(modFolder, folder);
        if (!Directory.Exists(tileDirectory))
            throw new Exception($"TileFolder not found: {tileDirectory}");

        Color[] ResolvePng(string tileFile) =>
            tileFile != null ? LoadTilePng(tileDirectory, tileFile)?.GetPixels() : null;

        var lowPixels = ResolvePng(HeightLow.TileFile);
        int pixelsPerTile = lowPixels != null ? (int)Math.Sqrt(lowPixels.Length) : COLOR_PIXELS_PER_TILE;

        var resolvedOverlays = new List<(byte Mask, Color[] Pixels)>();
        foreach (var overlay in overlayDefs)
        {
            var tilePixels = ResolvePng(overlay.TileFile);
            if (tilePixels != null) resolvedOverlays.Add((overlay.FlagMask, tilePixels));
        }

        return Render(tileData, mapWidth, mapHeight, heightMin, heightMax, pixelsPerTile,
            lowPixels,                            ResolvePng(HeightHigh.TileFile),
            ResolvePng(BlockedLow.TileFile),       ResolvePng(BlockedHigh.TileFile),
            resolvedOverlays.ToArray());
    }

    static Texture2D Render(TileData[] tileData, int mapWidth, int mapHeight,
        float heightMin, float heightMax, int pixelsPerTile,
        Color[] heightLow, Color[] heightHigh,
        Color[] blockedLow, Color[] blockedHigh,
        (byte Mask, Color[] Pixels)[] overlays)
    {
        int textureWidth = mapWidth * pixelsPerTile, textureHeight = mapHeight * pixelsPerTile;
        var pixels = new Color[textureWidth * textureHeight];
        float heightRange = heightMax - heightMin;
        if (heightRange < 0.01f) heightRange = 1f;

        for (int tileZ = 0; tileZ < mapHeight; tileZ++)
        {
            for (int tileX = 0; tileX < mapWidth; tileX++)
            {
                var tile = tileData[tileZ * mapWidth + tileX];
                float heightNormalized = Math.Clamp((tile.Height - heightMin) / heightRange, 0f, 1f);
                bool blocked = (tile.Flags & 0x02) != 0;
                var gradientLow  = blocked ? blockedLow  : heightLow;
                var gradientHigh = blocked ? blockedHigh : heightHigh;

                int baseX = tileX * pixelsPerTile, baseY = tileZ * pixelsPerTile;
                for (int pixelY = 0; pixelY < pixelsPerTile; pixelY++)
                {
                    for (int pixelX = 0; pixelX < pixelsPerTile; pixelX++)
                    {
                        int subIndex = pixelY * pixelsPerTile + pixelX;
                        Color pixel = Color.Lerp(
                            gradientLow  != null ? gradientLow[subIndex]  : Color.black,
                            gradientHigh != null ? gradientHigh[subIndex] : Color.black, heightNormalized);

                        foreach (var (mask, overlayPixels) in overlays)
                            if ((tile.Flags & mask) != 0)
                                pixel = AlphaBlend(pixel, overlayPixels[subIndex]);

                        pixels[(baseY + pixelY) * textureWidth + baseX + pixelX] = pixel;
                    }
                }
            }
        }

        var texture = new Texture2D(textureWidth, textureHeight);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = FilterMode.Point;
        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }

    internal static Texture2D CropTexture(Texture2D source, int startX, int startY, int cropWidth, int cropHeight)
    {
        var pixels = source.GetPixels(startX, startY, cropWidth, cropHeight);
        var cropped = new Texture2D(cropWidth, cropHeight);
        cropped.hideFlags = HideFlags.HideAndDontSave;
        cropped.filterMode = FilterMode.Point;
        cropped.SetPixels(pixels);
        cropped.Apply();
        return cropped;
    }

    internal static Texture2D LoadTilePng(string directory, string filename)
    {
        var path = Path.Combine(directory, filename);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var texture = new Texture2D(2, 2);
        texture.hideFlags = HideFlags.HideAndDontSave;
        if (ImageConversion.LoadImage(texture, bytes))
        {
            texture.filterMode = FilterMode.Point;
            return texture;
        }
        UnityEngine.Object.Destroy(texture);
        return null;
    }

    internal static Color[] MakeSolidBlock(int size, Color color)
    {
        var pixels = new Color[size * size];
        for (int index = 0; index < pixels.Length; index++) pixels[index] = color;
        return pixels;
    }

    internal static Color AlphaBlend(Color baseColor, Color overlay)
    {
        float alpha = overlay.a;
        return new Color(
            baseColor.r * (1f - alpha) + overlay.r * alpha,
            baseColor.g * (1f - alpha) + overlay.g * alpha,
            baseColor.b * (1f - alpha) + overlay.b * alpha,
            1f);
    }
}

// Links a palette color key (flat-color mode) to a tile PNG filename (texture mode).
internal readonly record struct LayerDef(string ColorKey, string TileFile);
