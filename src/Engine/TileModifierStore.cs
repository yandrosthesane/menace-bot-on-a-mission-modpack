using System;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BOAM;

/// <summary>
/// Centralized store of per-actor tile score modifiers.
/// The F# engine sends modifiers via the command server; PostProcessTileScores applies them.
/// One combined modifier per actor — multiple sources combine in the engine before sending.
/// </summary>
static class TileModifierStore
{
    /// <summary>
    /// A tile modifier applied during PostProcessTileScores.
    /// AddUtility: added to UtilityScore and UtilityScoreScaled.
    /// MultCombined: multiplied against the combined score.
    /// MinDistance: only apply to tiles at this distance or further from current tile.
    /// MaxDistance: only apply to tiles at this distance or closer (0 = no limit).
    /// </summary>
    internal struct TileModifier
    {
        public float AddUtility;
        public float MultCombined;
        public float MinDistance;
        public float MaxDistance;
        public int TargetX;    // -1 = no target tile (apply to all in range)
        public int TargetZ;
        public bool SuppressAttack; // zero out all attack/skill behavior scores
    }

    private static readonly ConcurrentDictionary<string, TileModifier> _store = new();
    private static readonly System.Threading.ManualResetEventSlim _ready = new(true);

    /// <summary>Block until the engine signals modifiers are ready.</summary>
    internal static void WaitReady()
    {
        _ready.Wait();
    }

    /// <summary>Mark modifiers as pending (engine is computing).</summary>
    internal static void SetPending()
    {
        _ready.Reset();
    }

    /// <summary>Mark modifiers as ready (engine finished sending).</summary>
    internal static void SetReady()
    {
        _ready.Set();
    }

    internal static void Set(string actorUuid, TileModifier modifier)
    {
        _store[actorUuid] = modifier;
    }

    internal static bool TryGet(string actorUuid, out TileModifier modifier)
    {
        return _store.TryGetValue(actorUuid, out modifier);
    }

    internal static void Remove(string actorUuid)
    {
        _store.TryRemove(actorUuid, out _);
    }

    internal static void Clear()
    {
        _store.Clear();
    }

    internal static void SetFromJson(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var actor = root.GetProperty("actor").GetString();
            if (string.IsNullOrEmpty(actor)) return;

            var mod = new TileModifier
            {
                AddUtility = root.TryGetProperty("add_utility", out var au) ? au.GetSingle() : 0f,
                MultCombined = root.TryGetProperty("mult_combined", out var mc) ? mc.GetSingle() : 1f,
                MinDistance = root.TryGetProperty("min_distance", out var mn) ? mn.GetSingle() : 0f,
                MaxDistance = root.TryGetProperty("max_distance", out var mx) ? mx.GetSingle() : 0f,
                TargetX = root.TryGetProperty("target_x", out var tx) ? tx.GetInt32() : -1,
                TargetZ = root.TryGetProperty("target_z", out var tz) ? tz.GetInt32() : -1,
                SuppressAttack = root.TryGetProperty("suppress_attack", out var sa) && sa.GetBoolean(),
            };

            _store[actor] = mod;
        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Warning($"[BOAM] TileModifierStore parse error: {ex.Message}");
        }
    }
}
