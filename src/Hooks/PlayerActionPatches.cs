using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.States;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Harmony patch: intercepts TacticalState.EndTurn to log player endturn actions.
/// Registered manually in OnInitialize (type found via GameType.Find, same as TacticalController).
/// Uses Prefix so the active actor is still set when we read it.
/// </summary>
static class Patch_EndTurn
{
    // Guard: track last actor+round to prevent duplicate logging
    // (game calls EndTurn twice for the last player unit in a faction phase)
    private static int _lastActorId = -1;
    private static int _lastRound = -1;

    public static void Prefix()
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
            if (!DataEvents.ActionLoggingEvent.IsActive) return;

            // Only log during player faction turns
            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != (int)Menace.SDK.FactionType.Player
                && factionId != (int)Menace.SDK.FactionType.PlayerAI) return;

            // Get the active actor before EndTurn clears it
            var activeGameObj = TacticalController.GetActiveActor();
            if (activeGameObj.IsNull) return;

            var actor = new Actor(activeGameObj.Pointer);
            var actorInfo = ActorRegistry.GetActorInfo(actor);
            if (actorInfo == null) return;
            var (gameObj, _, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);

            // Skip duplicate: same actor+round means EndTurn fired twice
            int round = bridge.Round;
            if (entityId == _lastActorId && round == _lastRound) return;
            _lastActorId = entityId;
            _lastRound = round;

            var (tileX, tileZ) = ActorRegistry.GetPos(gameObj);

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round,
                faction = factionId,
                actor = actorUuid,
                actionType = "endturn",
                skillName = "",
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: endturn at ({tileX},{tileZ})");
            QueryCommandClient.Hook("player-action", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] endturn patch error: {ex.Message}");
        }
    }
}


/// <summary>
/// Harmony patch: fires when the mission preview map finishes loading.
/// Captures the map texture + tile data for the tactical minimap and heatmap rendering,
/// and notifies the tactical engine so event-driven navigation knows planmission is ready.
/// </summary>
static class Patch_PreviewReady
{
    // Cache the preview data — written to disk at LaunchMission
    internal static Il2CppMenace.UI.Strategy.MissionPrepUIScreen CachedInstance;
    internal static Il2CppMenace.Strategy.MissionPreviewResult CachedResult;

    public static void Postfix(
        Il2CppMenace.UI.Strategy.MissionPrepUIScreen __instance,
        Il2CppMenace.Strategy.MissionPreviewResult _result)
    {
        try
        {
            CachedInstance = __instance;
            CachedResult = _result;
            BoamBridge.Logger.Msg("[BOAM] Mission preview ready — capturing map data");

            // Always notify engine (even before IsReady — needed for navigation)
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("preview-ready", "{}"));

            // Write immediately — LaunchMission will re-capture if needed
            Patch_LaunchMission.CaptureMapData(__instance, _result);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] preview-ready error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: captures map data right before mission launch (scene transition).
/// This ensures the map texture and tile data are saved before they get unloaded.
/// Uses cached data from OnPreviewReady.
/// </summary>
static class Patch_LaunchMission
{
    public static void Prefix(Il2CppMenace.UI.Strategy.MissionPrepUIScreen __instance)
    {
        try
        {
            var instance = Patch_PreviewReady.CachedInstance ?? __instance;
            var result = Patch_PreviewReady.CachedResult;

            // If OnPreviewReady never fired (e.g. loaded save), get result from Mission
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

            // Clear cache
            Patch_PreviewReady.CachedInstance = null;
            Patch_PreviewReady.CachedResult = null;
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

        // Write to fixed preview dir — copied to battle report at tactical-ready
        var persistentDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "UserData", "BOAM");

        var previewDir = System.IO.Path.Combine(persistentDir, "battle_preview");
        System.IO.Directory.CreateDirectory(previewDir);
        TacticalMap.TacticalMapState.PreviewDir = previewDir;

        var bgPath = System.IO.Path.Combine(previewDir, "mapbg.png");
        var infoPath = System.IO.Path.Combine(previewDir, "mapbg.info");
        var dataPath = System.IO.Path.Combine(previewDir, "mapdata.bin");

        // Save captured PNG
        var pngBytes = UnityEngine.ImageConversion.EncodeToPNG(mapTexture);
        if (pngBytes != null && pngBytes.Length > 0)
        {
            System.IO.File.WriteAllBytes(bgPath, pngBytes);
            System.IO.File.WriteAllText(infoPath, $"{mapTexture.width},{mapTexture.height},{sizeX},{sizeZ}");
        }

        // Update TacticalMapState for the IMGUI overlay
        TacticalMap.TacticalMapState.MapTexture = mapTexture;
        TacticalMap.TacticalMapState.TilesX = sizeX;
        TacticalMap.TacticalMapState.TilesZ = sizeZ;

        // Save raw tile data for palette-based map generation
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

            // Load tile data into state for map generation
            var tileDataArray = new TacticalMap.TileData[sizeX * sizeZ];
            using (var reader = new System.IO.BinaryReader(System.IO.File.OpenRead(dataPath)))
            {
                reader.ReadInt32(); reader.ReadInt32(); // skip sizeX, sizeZ
                reader.ReadSingle(); reader.ReadSingle(); // skip min, max
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
}

/// <summary>
/// Harmony patch: fires when the active actor changes.
/// Sends actor info to the tactical engine for event-driven logging and minimap updates.
/// </summary>
static class Patch_ActiveActorChanged
{
    public static void Postfix(object __instance, Il2CppMenace.Tactical.Actor _activeActor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsTacticalReady) return;

            if (_activeActor == null)
            {
                if (bridge.IsEngineReady)
                    ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("actor-changed",
                        JsonSerializer.Serialize(new { actor = "", faction = 0, x = 0, z = 0 })));
                return;
            }

            var actorInfo = ActorRegistry.GetActorInfo(_activeActor);
            if (actorInfo == null) return;
            var (gameObj, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);
            var (px, pz) = ActorRegistry.GetPos(gameObj);

            // Update minimap overlay
            if (DataEvents.MinimapUnitsEvent.IsActive)
            {
                TacticalMap.TacticalMapState.ActiveActor = actorUuid;
                TacticalMap.TacticalMapState.UpdateUnitPosition(actorUuid, px, pz);
            }

            if (!bridge.IsEngineReady || !DataEvents.ActorChangedEvent.IsActive) return;

            var round = bridge.Round;
            var payload = JsonSerializer.Serialize(new
            {
                actor = actorUuid,
                faction = factionId,
                round,
                x = px,
                z = pz
            });

            BoamBridge.Logger.Msg($"[BOAM] active-actor-changed: {actorUuid} r={round} at ({px},{pz})");
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("actor-changed", payload));

            // Log select primitive for player factions
            if (DataEvents.ActionLoggingEvent.IsActive && (factionId == 1 || factionId == 2))
            {
                var selectPayload = JsonSerializer.Serialize(new
                {
                    hook = "player-action",
                    round = bridge?.Round ?? 0,
                    faction = factionId,
                    actor = actorUuid,
                    actionType = "select",
                    skillName = "",
                    tile = new { x = px, z = pz }
                });
                BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: select");
                ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("player-action", selectPayload));
            }
        }
        catch { }
    }
}

/// <summary>
/// Harmony patch: logs click primitives when the player clicks a tile.
/// Patched on multiple TacticalAction subclasses to capture all clicks.
/// Logs the concrete action type for diagnostics.
/// </summary>
static class Patch_ClickOnTile
{
    public static void Postfix(object __instance, Il2CppMenace.Tactical.Tile _tile, Actor _activeActor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
            if (!DataEvents.ActionLoggingEvent.IsActive) return;

            // Get the concrete action type name for diagnostics
            var actionClassName = __instance?.GetType()?.Name ?? "unknown";

            // Only log during player turns
            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != 1 && factionId != 2) return;

            // Read tile coordinates from the parameter (what HandleLeftClickOnTile received)
            int tileX = 0, tileZ = 0;
            try { if (_tile != null) { tileX = _tile.GetX(); tileZ = _tile.GetZ(); } } catch { }

            string actorUuid = "";
            if (_activeActor != null)
            {
                var info = ActorRegistry.GetActorInfo(_activeActor);
                if (info.HasValue) actorUuid = ActorRegistry.GetUuid(info.Value.entityId);
            }

            // DIAGNOSTIC: log the action class that handled this click
            BoamBridge.Logger.Msg($"[BOAM] CLICK-DIAG action={actionClassName} actor={actorUuid} tile=({tileX},{tileZ})");

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round = bridge.Round,
                faction = factionId,
                actor = actorUuid,
                actionType = "click",
                skillName = "",
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: click ({tileX},{tileZ})");
            QueryCommandClient.Hook("player-action", payload);
        }
        catch { }
    }
}

/// <summary>
/// Harmony patch: logs useskill primitives when a skill is selected.
/// </summary>
static class Patch_SelectSkill
{
    public static void Postfix(object __instance, Il2CppMenace.Tactical.Skills.Skill _skill, bool __result)
    {
        try
        {
            if (!__result) return; // skill selection failed

            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
            if (!DataEvents.ActionLoggingEvent.IsActive) return;

            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != 1 && factionId != 2) return;

            var skillName = "";
            try { skillName = _skill?.GetTitle() ?? ""; } catch { }

            string actorUuid = "";
            var activeActor = TacticalController.GetActiveActor();
            if (!activeActor.IsNull)
            {
                var info = ActorRegistry.GetActorInfo(new Actor(activeActor.Pointer));
                if (info.HasValue) actorUuid = ActorRegistry.GetUuid(info.Value.entityId);
            }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round = bridge.Round,
                faction = factionId,
                actor = actorUuid,
                actionType = "useskill",
                skillName,
                tile = new { x = 0, z = 0 }
            });

            // Start tracking this skill for duration measurement.
            // AttackTileStart will override for attack skills; for non-attack skills
            // (Deploy, Get Up, etc.) this is the only timer start.
            DataEvents.ActionLoggingEvent.StartPlayerSkillTimer(actorUuid, skillName);

            BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: useskill {skillName}");
            QueryCommandClient.Hook("player-action", payload);
        }
        catch { }
    }
}
