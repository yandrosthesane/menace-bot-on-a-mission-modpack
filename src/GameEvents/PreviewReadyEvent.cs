using System;
using System.Threading;
using MelonLoader;

namespace BOAM.GameEvents;

static class PreviewReadyEvent
{
    internal static bool IsActive => Boundary.GameEvents.PreviewReady;

    private static Il2CppMenace.UI.Strategy.MissionPrepUIScreen _cachedInstance
    {
        get => Boundary.GameStore.Read<Il2CppMenace.UI.Strategy.MissionPrepUIScreen>("preview-instance");
        set => Boundary.GameStore.Write("preview-instance", value);
    }
    private static Il2CppMenace.Strategy.MissionPreviewResult _cachedResult
    {
        get => Boundary.GameStore.Read<Il2CppMenace.Strategy.MissionPreviewResult>("preview-result");
        set => Boundary.GameStore.Write("preview-result", value);
    }

    internal static void Register(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        // OnPreviewReady
        try
        {
            var prepType = typeof(Il2CppMenace.UI.Strategy.MissionPrepUIScreen);
            var m = prepType.GetMethod("OnPreviewReady");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(PreviewReadyEvent), nameof(OnPreviewReady)));
                log.Msg("[BOAM] Patched MissionPrepUIScreen.OnPreviewReady");
            }
        }
        catch (Exception ex) { log.Error($"[BOAM] Failed to patch OnPreviewReady: {ex.Message}"); }

        // LaunchMission
        try
        {
            var prepType = typeof(Il2CppMenace.UI.Strategy.MissionPrepUIScreen);
            var m = prepType.GetMethod("LaunchMission", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?? prepType.GetMethod("LaunchMission", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (m == null)
            {
                foreach (var method in prepType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
                    if (method.Name == "LaunchMission") { m = method; break; }
            }
            if (m != null)
            {
                harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(PreviewReadyEvent), nameof(OnLaunchMission)));
                log.Msg("[BOAM] Patched MissionPrepUIScreen.LaunchMission");
            }
            else
                log.Warning("[BOAM] MissionPrepUIScreen.LaunchMission not found");
        }
        catch (Exception ex) { log.Error($"[BOAM] Failed to patch LaunchMission: {ex.Message}"); }
    }

    public static void OnPreviewReady(
        Il2CppMenace.UI.Strategy.MissionPrepUIScreen __instance,
        Il2CppMenace.Strategy.MissionPreviewResult _result)
    {
        try
        {
            _cachedInstance = __instance;
            _cachedResult = _result;
            BoamBridge.Logger.Msg("[BOAM] Mission preview ready — capturing map data");

            if (IsActive)
                ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.SendEvent("preview-ready", "{}"));

            CaptureMapData(__instance, _result);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] preview-ready error: {ex.Message}");
        }
    }

    public static void OnLaunchMission(Il2CppMenace.UI.Strategy.MissionPrepUIScreen __instance)
    {
        try
        {
            var instance = _cachedInstance ?? __instance;
            var result = _cachedResult;

            if (result == null)
            {
                BoamBridge.Logger.Msg("[BOAM] LaunchMission — no cached preview, reading from Mission.GetPreview()");
                try
                {
                    var mission = __instance.GetMission();
                    if (mission != null)
                        result = mission.GetPreview();
                }
                catch (Exception ex2) { BoamBridge.Logger.Warning($"[BOAM] fallback preview read: {ex2.Message}"); }
            }

            if (result == null)
            {
                BoamBridge.Logger.Warning("[BOAM] LaunchMission — no preview result available, cannot capture map");
                return;
            }

            BoamBridge.Logger.Msg("[BOAM] LaunchMission — capturing map data before scene transition");
            CaptureMapData(instance, result);

            _cachedInstance = null;
            _cachedResult = null;
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] LaunchMission capture error: {ex.Message}");
        }
    }

    internal static void CaptureMapData(
        Il2CppMenace.UI.Strategy.MissionPrepUIScreen instance,
        Il2CppMenace.Strategy.MissionPreviewResult result)
    {
        var mapTexture = instance.m_MapTexture;
        if (mapTexture == null || mapTexture.width == 0)
        {
            BoamBridge.Logger.Msg("[BOAM] m_MapTexture is null or empty — skipping capture");
            return;
        }

        int sizeX = result?.SizeX ?? 0;
        int sizeZ = result?.SizeZ ?? 0;
        if (sizeX == 0 || sizeZ == 0) return;

        var persistentDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "BOAM");
        var previewDir = System.IO.Path.Combine(persistentDir, "battle_preview");
        System.IO.Directory.CreateDirectory(previewDir);
        TacticalMap.TacticalMapState.PreviewDir = previewDir;

        var bgPath = System.IO.Path.Combine(previewDir, "mapbg.png");
        var infoPath = System.IO.Path.Combine(previewDir, "mapbg.info");
        var dataPath = System.IO.Path.Combine(previewDir, "mapdata.bin");

        var pngBytes = UnityEngine.ImageConversion.EncodeToPNG(mapTexture);
        if (pngBytes != null && pngBytes.Length > 0)
        {
            System.IO.File.WriteAllBytes(bgPath, pngBytes);
            System.IO.File.WriteAllText(infoPath, $"{mapTexture.width},{mapTexture.height},{sizeX},{sizeZ}");
        }

        TacticalMap.TacticalMapState.MapTexture = mapTexture;
        TacticalMap.TacticalMapState.TilesX = sizeX;
        TacticalMap.TacticalMapState.TilesZ = sizeZ;

        try
        {
            float heightMin = float.MaxValue, heightMax = float.MinValue;
            using (var writer = new System.IO.BinaryWriter(System.IO.File.Create(dataPath)))
            {
                writer.Write(sizeX);
                writer.Write(sizeZ);
                long minMaxPosition = writer.BaseStream.Position;
                writer.Write(0f);
                writer.Write(0f);

                for (int tileZ = 0; tileZ < sizeZ; tileZ++)
                {
                    for (int tileX = 0; tileX < sizeX; tileX++)
                    {
                        float elevation = 0f;
                        byte flags = 0;
                        if (result.HasRoad(tileX, tileZ)) flags |= 1;
                        if (result.IsBlocked(tileX, tileZ)) flags |= 2;
                        try
                        {
                            var tile = result.Tiles.GetTile(tileX, tileZ);
                            if (tile != null)
                            {
                                elevation = tile.GetElevation();
                                int cover = (int)tile.GetInherentCover();
                                if (cover == 1) flags |= 4;
                                if (cover >= 2) flags |= 8;
                                if (!tile.IsEmpty() && !tile.HasActor())
                                    flags |= 8;
                            }
                        }
                        catch { }

                        if (elevation < heightMin) heightMin = elevation;
                        if (elevation > heightMax) heightMax = elevation;

                        writer.Write(elevation);
                        writer.Write(flags);
                    }
                }
                writer.BaseStream.Seek(minMaxPosition, System.IO.SeekOrigin.Begin);
                writer.Write(heightMin);
                writer.Write(heightMax);
            }

            TacticalMap.TacticalMapState.HeightMin = heightMin;
            TacticalMap.TacticalMapState.HeightMax = heightMax;

            var tileDataArray = new TacticalMap.TileData[sizeX * sizeZ];
            using (var reader = new System.IO.BinaryReader(System.IO.File.OpenRead(dataPath)))
            {
                reader.ReadInt32(); reader.ReadInt32();
                reader.ReadSingle(); reader.ReadSingle();
                for (int i = 0; i < tileDataArray.Length; i++)
                {
                    tileDataArray[i].Height = reader.ReadSingle();
                    tileDataArray[i].Flags = reader.ReadByte();
                }
            }
            TacticalMap.TacticalMapState.TileDataArray = tileDataArray;

            BoamBridge.Logger.Msg($"[BOAM] Map captured: {mapTexture.width}x{mapTexture.height} px, {sizeX}x{sizeZ} tiles, height [{heightMin:F1}..{heightMax:F1}] → {previewDir}");
            Toast.Show($"BOAM: Map captured ({sizeX}x{sizeZ} tiles)", 3f);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Warning($"[BOAM] Failed to save tile data: {ex.Message}");
        }
    }

    internal static void ReloadFromDisk(MelonLogger.Instance log)
    {
        try
        {
            var previewDir = TacticalMap.TacticalMapState.PreviewDir;
            if (string.IsNullOrEmpty(previewDir) || !System.IO.Directory.Exists(previewDir))
            {
                log.Warning("[BOAM] TacticalMap — No preview dir, cannot reload map");
                return;
            }

            var previewBg = System.IO.Path.Combine(previewDir, "mapbg.png");
            var previewInfo = System.IO.Path.Combine(previewDir, "mapbg.info");
            var previewData = System.IO.Path.Combine(previewDir, "mapdata.bin");

            if (!System.IO.File.Exists(previewBg))
            {
                log.Warning("[BOAM] TacticalMap — mapbg.png missing from preview dir");
                return;
            }

            var persistentDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "BOAM");
            var ts = DateTime.Now.ToString("yyyy_MM_dd_HH_mm");
            var sessionDir = System.IO.Path.Combine(persistentDir, "battle_reports", $"battle_{ts}");
            System.IO.Directory.CreateDirectory(sessionDir);
            TacticalMap.TacticalMapState.BattleSessionDir = sessionDir;

            var bgPath = System.IO.Path.Combine(sessionDir, "mapbg.png");
            var infoPath = System.IO.Path.Combine(sessionDir, "mapbg.info");
            var dataPath = System.IO.Path.Combine(sessionDir, "mapdata.bin");

            System.IO.File.Copy(previewBg, bgPath, true);
            if (System.IO.File.Exists(previewInfo)) System.IO.File.Copy(previewInfo, infoPath, true);
            if (System.IO.File.Exists(previewData)) System.IO.File.Copy(previewData, dataPath, true);

            log.Msg($"[BOAM] TacticalMap — Copied preview data to {sessionDir}");

            var data = TacticalMap.MapDataLoader.Load(bgPath, infoPath, dataPath, log);
            if (data.BackgroundTexture != null)
            {
                TacticalMap.TacticalMapState.MapTexture = data.BackgroundTexture;
                TacticalMap.TacticalMapState.TilesX = data.TotalX;
                TacticalMap.TacticalMapState.TilesZ = data.TotalZ;
                TacticalMap.TacticalMapState.TileDataArray = data.Tiles;
                TacticalMap.TacticalMapState.HeightMin = data.HeightMin;
                TacticalMap.TacticalMapState.HeightMax = data.HeightMax;
                log.Msg($"[BOAM] TacticalMap — Reloaded map from disk: {data.TotalX}x{data.TotalZ}");
            }
            else
            {
                log.Warning("[BOAM] TacticalMap — Failed to load map from copied files");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[BOAM] ReloadMapFromDisk error: {ex.Message}");
        }
    }
}
