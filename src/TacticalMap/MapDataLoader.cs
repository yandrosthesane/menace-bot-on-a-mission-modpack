// MapDataLoader: loads captured map background, dimension info, and binary
// tile data from disk. Returns a MapData struct — no plugin state dependency.

using System;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace BOAM.TacticalMap;

internal static class MapDataLoader
{
    internal static MapData Load(string backgroundPath, string infoPath, string dataPath,
        MelonLogger.Instance log)
    {
        var data = new MapData { HeightMin = 0, HeightMax = 1 };

        if (File.Exists(backgroundPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(backgroundPath);
                var texture = new Texture2D(2, 2);
                texture.hideFlags = HideFlags.HideAndDontSave;
                if (ImageConversion.LoadImage(texture, bytes))
                {
                    data.BackgroundTexture = texture;
                    log.Msg($"[BOAM] TacticalMap — Loaded captured map: {texture.width}x{texture.height}");
                }
                else
                    UnityEngine.Object.Destroy(texture);
            }
            catch (Exception exception)
            {
                log.Warning($"[BOAM] TacticalMap — Failed to load captured map: {exception.Message}");
            }
        }

        if (File.Exists(infoPath))
        {
            try
            {
                var infoParts = File.ReadAllText(infoPath).Trim().Split(',');
                if (infoParts.Length >= 4)
                {
                    data.TotalX = int.Parse(infoParts[2]);
                    data.TotalZ = int.Parse(infoParts[3]);
                }
            }
            catch { }
        }

        if (File.Exists(dataPath))
        {
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(dataPath)))
                {
                    int sizeX = reader.ReadInt32();
                    int sizeZ = reader.ReadInt32();
                    float heightMin = reader.ReadSingle();
                    float heightMax = reader.ReadSingle();
                    data.TotalX = sizeX;
                    data.TotalZ = sizeZ;
                    data.HeightMin = heightMin;
                    data.HeightMax = heightMax;

                    data.Tiles = new TileData[sizeX * sizeZ];
                    for (int index = 0; index < data.Tiles.Length; index++)
                    {
                        data.Tiles[index].Height = reader.ReadSingle();
                        data.Tiles[index].Flags = reader.ReadByte();
                    }
                }
                log.Msg($"[BOAM] TacticalMap — Loaded tile data: {data.TotalX}x{data.TotalZ}, height [{data.HeightMin:F1}..{data.HeightMax:F1}]");
            }
            catch (Exception exception)
            {
                log.Warning($"[BOAM] TacticalMap — Failed to load tile data: {exception.Message}");
                data.Tiles = null;
            }
        }

        return data;
    }
}
