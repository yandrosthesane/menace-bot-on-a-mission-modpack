using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Menace.SDK;

namespace BOAM.GameEvents;

static class LosTrackingEvent
{
    internal static bool IsActive => Boundary.GameEvents.LosTracking;

    private const string CACHE_KEY = "los-cache";

    internal static Dictionary<int, Dictionary<IntPtr, (int x, int z)>> GetCache()
    {
        var cache = Boundary.GameStore.Read<Dictionary<int, Dictionary<IntPtr, (int x, int z)>>>(CACHE_KEY);
        if (cache == null)
        {
            cache = new Dictionary<int, Dictionary<IntPtr, (int x, int z)>>();
            Boundary.GameStore.Write(CACHE_KEY, cache);
        }
        return cache;
    }
}

[HarmonyPatch(typeof(Entity), nameof(Entity.SetTile))]
static class Patch_EntitySetTile
{
    static void Postfix(Entity __instance)
    {
        if (!LosTrackingEvent.IsActive) return;
        try
        {
            int entityFaction = __instance.GetFactionID();
            // Only track player units (faction 1 or 2)
            if (entityFaction != 1 && entityFaction != 2) return;

            var tile = __instance.GetTile();
            if (tile == null) return;
            int x = tile.GetX(), z = tile.GetZ();

            var entityPtr = __instance.Pointer;
            var cache = LosTrackingEvent.GetCache();

            // Check each non-player faction
            for (int factionIdx = 3; factionIdx <= 9; factionIdx++)
            {
                var enemies = EntitySpawner.ListEntities(factionIdx);
                if (enemies == null || enemies.Length == 0) continue;

                bool seen = false;
                var targetObj = new GameObj(entityPtr);
                foreach (var enemy in enemies)
                {
                    if (enemy.IsNull || !enemy.IsAlive) continue;
                    if (LineOfSight.CanActorSee(enemy, targetObj))
                    {
                        seen = true;
                        break;
                    }
                }

                if (!cache.TryGetValue(factionIdx, out var factionCache))
                {
                    factionCache = new Dictionary<IntPtr, (int, int)>();
                    cache[factionIdx] = factionCache;
                }

                if (seen)
                {
                    factionCache[entityPtr] = (x, z);
                }
                else if (factionCache.TryGetValue(entityPtr, out var lastSeen))
                {
                    // LOS lost — push investigate event
                    factionCache.Remove(entityPtr);
                    int round = BoamBridge.Instance?.Round ?? 0;
                    var payload = JsonSerializer.Serialize(new
                    {
                        type = "hook",
                        hook = "investigate-event",
                        faction = factionIdx,
                        x = lastSeen.x,
                        z = lastSeen.z,
                        round
                    });
                    ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("investigate-event", payload));
                    BoamBridge.Logger?.Msg($"[BOAM] LOS lost: faction {factionIdx} lost player at ({lastSeen.x},{lastSeen.z})");
                }
            }
        }
        catch { }
    }
}
