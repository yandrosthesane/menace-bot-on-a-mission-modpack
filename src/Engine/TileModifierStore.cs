using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace BOAM;

struct TileScoreModifier
{
    public float Utility;
    public float Safety;
    public float Distance;
    public float UtilityByAttacks;
}

static class TileModifierStore
{
    private const string STORE_KEY = "tile-modifier-data";
    private static readonly System.Threading.ManualResetEventSlim _ready = new(true);

    private static ConcurrentDictionary<string, Dictionary<(int x, int z), TileScoreModifier>> GetStore()
    {
        var store = Boundary.GameStore.Read<ConcurrentDictionary<string, Dictionary<(int x, int z), TileScoreModifier>>>(STORE_KEY);
        if (store == null)
        {
            store = new ConcurrentDictionary<string, Dictionary<(int x, int z), TileScoreModifier>>();
            Boundary.GameStore.Write(STORE_KEY, store);
        }
        return store;
    }

    internal static void WaitReady() => _ready.Wait();
    internal static void SetPending() => _ready.Reset();
    internal static void SetReady() => _ready.Set();

    internal static bool TryGet(string actorUuid, out Dictionary<(int x, int z), TileScoreModifier> tileMap)
    {
        return GetStore().TryGetValue(actorUuid, out tileMap);
    }

    internal static void Clear()
    {
        GetStore().Clear();
    }

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

            GetStore()[actor] = tileMap;
        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Warning($"[BOAM] TileModifierStore parse error: {ex.Message}");
        }
    }
}
