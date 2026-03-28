using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace BOAM;

/// <summary>
/// Per-score-type modifier for a single tile.
/// Each field targets a separate game score component independently.
/// </summary>
struct TileScoreModifier
{
    public float Utility;
    public float Safety;
    public float Distance;
    public float UtilityByAttacks;
}

/// <summary>
/// Per-actor, per-tile score modifiers. The F# engine sends tile maps via the command server;
/// PostProcessTileScores applies them. Simple lookup — all logic lives in the engine.
/// </summary>
static class TileModifierStore
{
    private static readonly ConcurrentDictionary<string, Dictionary<(int x, int z), TileScoreModifier>> _store = new();
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

    internal static bool TryGet(string actorUuid, out Dictionary<(int x, int z), TileScoreModifier> tileMap)
    {
        return _store.TryGetValue(actorUuid, out tileMap);
    }

    internal static void Clear()
    {
        _store.Clear();
    }

    /// <summary>
    /// Parse per-actor tile modifier map from JSON.
    /// Format: {"actor":"uuid", "tiles":[{"x":1,"z":2,"u":150.0}, ...]}
    /// </summary>
    internal static void SetFromJson(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var actor = root.GetProperty("actor").GetString();
            if (string.IsNullOrEmpty(actor)) return;

            var tileMap = new Dictionary<(int, int), TileScoreModifier>();
            if (root.TryGetProperty("tiles", out var tilesArr))
            {
                foreach (var tile in tilesArr.EnumerateArray())
                {
                    int x = tile.GetProperty("x").GetInt32();
                    int z = tile.GetProperty("z").GetInt32();
                    var mod = new TileScoreModifier();
                    if (tile.TryGetProperty("utility", out var u)) mod.Utility = u.GetSingle();
                    if (tile.TryGetProperty("safety", out var s)) mod.Safety = s.GetSingle();
                    if (tile.TryGetProperty("distance", out var d)) mod.Distance = d.GetSingle();
                    if (tile.TryGetProperty("utilityByAttacks", out var ua)) mod.UtilityByAttacks = ua.GetSingle();
                    tileMap[(x, z)] = mod;
                }
            }

            _store[actor] = tileMap;
        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Warning($"[BOAM] TileModifierStore parse error: {ex.Message}");
        }
    }
}
