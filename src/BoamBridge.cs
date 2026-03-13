using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Data;
using Il2CppMenace.Tools;
using MelonLoader;
using Menace.ModpackLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Thin C# bridge plugin — calls the BOAM F# sidecar over HTTP
/// at game hook checkpoints and applies the returned modifications.
/// </summary>
public class BoamBridge : IModpackPlugin
{
    public static BoamBridge Instance { get; private set; }
    public static MelonLogger.Instance Log { get; private set; }

    private HarmonyLib.Harmony _harmony;
    private volatile bool _sidecarAvailable;
    private bool _inTactical;
    private int _initDelay;
    private bool _ready;
    private int _round;
    private int _lastFaction = -1;

    // Synchronous HTTP — avoids async deadlocks under Wine CLR
    internal static string SidecarGet(string path)
    {
        try
        {
            using var client = new System.Net.WebClient();
            return client.DownloadString("http://127.0.0.1:7660" + path);
        }
        catch { return null; }
    }

    internal static string SidecarPost(string path, string json)
    {
        try
        {
            using var client = new System.Net.WebClient();
            client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
            return client.UploadString("http://127.0.0.1:7660" + path, json);
        }
        catch { return null; }
    }

    public void OnInitialize(MelonLogger.Instance logger, HarmonyLib.Harmony harmony)
    {
        Instance = this;
        Log = logger;
        _harmony = harmony;

        // Apply patches immediately on init thread (canonical pattern)
        _harmony.PatchAll(typeof(BoamBridge).Assembly);
        Log.Msg("[BOAM] Bridge plugin initialized, patches registered");
    }

    public void OnSceneLoaded(int buildIndex, string sceneName)
    {
        if (!_sidecarAvailable)
        {
            // Check sidecar on background thread (non-blocking)
            var thread = new Thread(CheckSidecar)
            {
                Name = "BOAM-Check",
                IsBackground = true
            };
            thread.Start();
        }

        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _initDelay = 60;
            _ready = false;
            _round = 1;
            _lastFaction = -1;

            // Movement-finished is handled via direct Harmony patch (Patch_MovementFinished below)
            // SDK TacticalEventHooks fails to init ("TacticalManager type not found") so we patch directly
        }
        else
        {
            _inTactical = false;
            _ready = false;
        }
    }


    public void OnUpdate()
    {
        if (!_inTactical || _ready) return;
        if (_initDelay > 0) { _initDelay--; return; }
        _ready = true;
        Log.Msg("[BOAM] Tactical ready, sidecar hooks active");
    }

    private void CheckSidecar()
    {
        const int maxRetries = 5;
        const int retryDelayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            Log.Msg($"[BOAM] Checking sidecar (attempt {attempt}/{maxRetries})");

            var json = SidecarGet("/status");
            if (json != null)
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    var sidecar = doc.GetProperty("sidecar").GetString();
                    var status = doc.GetProperty("status").GetString();
                    Log.Msg($"[BOAM] Sidecar found: {sidecar} (status: {status})");
                    _sidecarAvailable = true;
                    return;
                }
                catch { }
            }

            if (attempt < maxRetries)
                Thread.Sleep(retryDelayMs);
        }

        Log.Warning("[BOAM] Sidecar not available — AI hooks will be no-ops");
    }

    public bool IsReady => _ready && _sidecarAvailable;
    public int Round => _round;

    public void TrackRound(int factionIdx)
    {
        if (factionIdx <= _lastFaction)
            _round++;
        _lastFaction = factionIdx;
    }

    public void OnGUI() { }

    public void OnUnload()
    {
        if (_sidecarAvailable)
        {
            try { SidecarPost("/shutdown", "{}"); } catch { }
            Log.Msg("[BOAM] Sent shutdown to sidecar");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts AIFaction.OnTurnStart and sends faction state to sidecar.
/// Uses synchronous WebClient to avoid async deadlocks under Wine CLR.
/// </summary>
[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnTurnStart))]
static class Patch_OnTurnStart
{
    static void Prefix(AIFaction __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            int factionIdx = __instance.GetIndex();
            bridge.TrackRound(factionIdx);

            var opponents = __instance.m_Opponents;
            int opponentCount = opponents?.Count ?? 0;

            // Serialize opponent data
            var oppList = new System.Collections.Generic.List<object>();
            if (opponents != null)
            {
                for (int i = 0; i < opponents.Count; i++)
                {
                    try
                    {
                        var opp = opponents[i];
                        var actor = opp.Actor;
                        if (actor == null) continue;

                        var gameObj = new GameObj(actor.Pointer);
                        var pos = EntityMovement.GetPosition(gameObj);
                        int px = 0, pz = 0;
                        if (pos.HasValue) { px = pos.Value.x; pz = pos.Value.y; }
                        var templateObj = gameObj.ReadObj("m_Template");
                        var templateName = templateObj.IsNull ? "" : templateObj.GetName() ?? "";

                        oppList.Add(new
                        {
                            actorId = gameObj.ReadInt("EntityIdx"),
                            templateName,
                            position = new { x = px, z = pz },
                            ttl = opp.TTL,
                            isKnown = opp.IsKnown(),
                            isAlive = actor.IsAlive()
                        });
                    }
                    catch { } // skip individual opponent on error
                }
            }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "on-turn-start",
                faction = factionIdx,
                opponentCount,
                opponents = oppList
            });

            var response = BoamBridge.SidecarPost("/hook/on-turn-start", payload);
            if (response != null)
            {
                BoamBridge.Log.Msg($"[BOAM] on-turn-start f{factionIdx}: sidecar OK ({response.Length}b, {oppList.Count} opponents)");
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] on-turn-start error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts Agent.PostProcessTileScores to capture fully-scored tiles
/// after ALL criterions have evaluated and role-based weighting is applied.
/// This gives the complete picture: Utility (zones, effects), Safety (cover, threats),
/// Distance (movement cost), and the weighted Combined score used for behavior evaluation.
/// Fires once per agent, parallel across agents (safe — each agent has its own tiles).
/// </summary>
[HarmonyPatch(typeof(Agent), "PostProcessTileScores")]
static class Patch_PostProcessTileScores
{
    static void Postfix(Agent __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            var actor = __instance.m_Actor;
            if (actor == null) return;

            var tiles = __instance.m_Tiles;
            if (tiles == null || tiles.Count == 0) return;

            var gameObj = new GameObj(actor.Pointer);
            var actorInfo = EntitySpawner.GetEntityInfo(gameObj);
            int factionId = actorInfo?.FactionIndex ?? 0;
            int actorEntityId = actorInfo?.EntityId ?? 0;

            var templateObj = gameObj.ReadObj("m_Template");
            var actorName = templateObj.IsNull ? $"actor{actorEntityId}" : templateObj.GetName() ?? "";

            // Actor's current position
            var actorPos = EntityMovement.GetPosition(gameObj);
            int actorX = 0, actorZ = 0;
            if (actorPos.HasValue) { actorX = actorPos.Value.x; actorZ = actorPos.Value.y; }

            var tileList = new System.Collections.Generic.List<object>();

            var enumerator = tiles.GetEnumerator();
            while (enumerator.MoveNext())
            {
                try
                {
                    var kvp = enumerator.Current;
                    var tile = kvp.Key;
                    var score = kvp.Value;
                    if (tile == null || score == null) continue;

                    var combined = score.GetScore();
                    if (float.IsInfinity(combined) || float.IsNaN(combined))
                        combined = combined > 0 ? 9999f : -9999f;

                    tileList.Add(new
                    {
                        x = tile.GetX(),
                        z = tile.GetZ(),
                        combined
                    });
                }
                catch { } // skip individual tile on error
            }

            if (tileList.Count == 0) return;

            // Gather all alive units from all factions for overlay
            var unitList = new System.Collections.Generic.List<object>();
            try
            {
                var allActors = EntitySpawner.ListEntities(-1);
                if (allActors != null)
                {
                    foreach (var a in allActors)
                    {
                        var aInfo = EntitySpawner.GetEntityInfo(a);
                        if (aInfo == null || !aInfo.IsAlive) continue;
                        var aPos = EntityMovement.GetPosition(a);
                        if (aPos == null) continue;
                        var aGo = new GameObj(a.Pointer);
                        var aTpl = aGo.ReadObj("m_Template");
                        var templateName = aTpl.IsNull ? "" : (aTpl.GetName() ?? "");

                        // Try to get leader nickname (character name like "rewa", "exconde")
                        var leaderName = "";
                        try
                        {
                            var unitActor = new UnitActor(a.Pointer);
                            var leader = unitActor.GetLeader();
                            if (leader != null)
                            {
                                var nickname = leader.GetNickname();
                                if (nickname != null)
                                    leaderName = nickname.GetTranslated() ?? "";
                            }
                        }
                        catch { } // not all actors are UnitActors

                        unitList.Add(new
                        {
                            faction = aInfo.FactionIndex,
                            x = aPos.Value.x,
                            z = aPos.Value.y,
                            name = templateName,
                            leader = leaderName
                        });
                    }
                }
            }
            catch { } // non-critical — heatmap still works without units

            // Vision range for overlay
            int visionRange = 0;
            try { visionRange = LineOfSight.GetVision(gameObj); } catch { }

            int round = BoamBridge.Instance?.Round ?? 0;

            var payload = JsonSerializer.Serialize(new
            {
                hook = "tile-scores",
                round,
                faction = factionId,
                actorId = actorEntityId,
                actorName,
                actorPosition = new { x = actorX, z = actorZ },
                tiles = tileList,
                units = unitList,
                visionRange
            });

            var response = BoamBridge.SidecarPost("/hook/tile-scores", payload);
            if (response != null)
            {
                BoamBridge.Log.Msg($"[BOAM] tile-scores f{factionId} {actorName}: {tileList.Count} tiles");
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] tile-scores error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts TacticalManager.InvokeOnMovementFinished to capture
/// the actual tile where a unit stopped after moving (AP-limited).
/// Patched directly because SDK TacticalEventHooks fails to init ("TacticalManager type not found").
/// </summary>
[HarmonyPatch(typeof(Il2CppMenace.Tactical.TacticalManager), "InvokeOnMovementFinished")]
static class Patch_MovementFinished
{
    static void Postfix(object __instance, Actor _actor, Il2CppMenace.Tactical.Tile _to)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
            if (_actor == null || _to == null) return;

            var gameObj = new GameObj(_actor.Pointer);
            var info = EntitySpawner.GetEntityInfo(gameObj);
            int factionId = info?.FactionIndex ?? -1;
            int actorId = info?.EntityId ?? 0;
            int tileX = _to.GetX();
            int tileZ = _to.GetZ();

            var payload = JsonSerializer.Serialize(new
            {
                hook = "movement-finished",
                faction = factionId,
                actorId,
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Log.Msg($"[BOAM] movement-finished f{factionId} actor={actorId} tile=({tileX},{tileZ})");
            BoamBridge.SidecarPost("/hook/movement-finished", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] movement-finished error: {ex.Message}");
        }
    }
}
