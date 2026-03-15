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
            if (bridge == null || !bridge.IsReady) return;

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
            EngineClient.Post("/hook/player-action", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] endturn patch error: {ex.Message}");
        }
    }
}


/// <summary>
/// Harmony patch: fires when the mission preview map finishes loading.
/// Notifies the tactical engine so event-driven navigation knows planmission is ready.
/// </summary>
static class Patch_PreviewReady
{
    public static void Postfix()
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null) return;
            BoamBridge.Logger.Msg("[BOAM] Mission preview ready");
            if (bridge.IsReady || true) // always send — engine might not be "ready" yet during navigation
                ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/preview-ready", "{}"));
        }
        catch { }
    }
}

/// <summary>
/// Harmony patch: fires when the active actor changes.
/// Sends actor info to the tactical engine for event-driven replay and logging.
/// </summary>
static class Patch_ActiveActorChanged
{
    public static void Postfix(object __instance, Il2CppMenace.Tactical.Actor _activeActor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            if (_activeActor == null)
            {
                ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/actor-changed",
                    JsonSerializer.Serialize(new { actor = "", faction = 0, x = 0, z = 0 })));
                return;
            }

            var actorInfo = ActorRegistry.GetActorInfo(_activeActor);
            if (actorInfo == null) return;
            var (gameObj, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);
            var (px, pz) = ActorRegistry.GetPos(gameObj);

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
            ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/actor-changed", payload));

            // Log select primitive for player factions
            if (factionId == 1 || factionId == 2)
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
                ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/player-action", selectPayload));
            }
        }
        catch { }
    }
}

/// <summary>
/// Harmony patch: logs click primitives when the player (or replay) clicks a tile.
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
            if (bridge == null || !bridge.IsReady) return;

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
            EngineClient.Post("/hook/player-action", payload);
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
            if (bridge == null || !bridge.IsReady) return;

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

            BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: useskill {skillName}");
            EngineClient.Post("/hook/player-action", payload);
        }
        catch { }
    }
}
