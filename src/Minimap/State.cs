using System;
using System.Collections.Generic;
using UnityEngine;

namespace BOAM.TacticalMap;

internal static class TacticalMapState
{
    // --- Map background (GameStore-backed) ---

    internal static Texture2D MapTexture
    {
        get => Boundary.GameStore.Read<Texture2D>("map-texture");
        set => Boundary.GameStore.Write("map-texture", value);
    }

    internal static int TilesX
    {
        get => Boundary.GameStore.Read<int>("map-tiles-x");
        set => Boundary.GameStore.Write("map-tiles-x", value);
    }

    internal static int TilesZ
    {
        get => Boundary.GameStore.Read<int>("map-tiles-z");
        set => Boundary.GameStore.Write("map-tiles-z", value);
    }

    internal static TileData[] TileDataArray
    {
        get => Boundary.GameStore.Read<TileData[]>("map-tile-data");
        set => Boundary.GameStore.Write("map-tile-data", value);
    }

    internal static float HeightMin
    {
        get => Boundary.GameStore.Read<float>("map-height-min");
        set => Boundary.GameStore.Write("map-height-min", value);
    }

    internal static float HeightMax
    {
        get => Boundary.GameStore.Read("map-height-max", 1f);
        set => Boundary.GameStore.Write("map-height-max", value);
    }

    internal static string BattleSessionDir
    {
        get => Boundary.GameStore.Read<string>("battle-session-dir");
        set => Boundary.GameStore.Write("battle-session-dir", value);
    }

    internal static string PreviewDir
    {
        get => Boundary.GameStore.Read<string>("preview-dir");
        set => Boundary.GameStore.Write("preview-dir", value);
    }

    // --- Round tracking (GameStore-backed) ---

    internal static int CurrentRound
    {
        get => Boundary.GameStore.Read<int>("minimap-round");
        set => Boundary.GameStore.Write("minimap-round", value);
    }

    internal static int CurrentFaction
    {
        get => Boundary.GameStore.Read<int>("minimap-faction");
        set => Boundary.GameStore.Write("minimap-faction", value);
    }

    internal static string ActiveActor
    {
        get => Boundary.GameStore.Read("minimap-active-actor", "");
        set => Boundary.GameStore.Write("minimap-active-actor", value);
    }

    // --- Unit positions (thread-safe, local) ---

    private static readonly Dictionary<string, OverlayUnit> Units = new();
    private static readonly object _unitsLock = new();
    private static bool _unitsDirty;
    private static List<OverlayUnit> _cachedSnapshot = new();

    internal static void SetUnits(List<OverlayUnit> units)
    {
        lock (_unitsLock)
        {
            Units.Clear();
            foreach (var u in units)
                Units[u.Actor] = u;
            _unitsDirty = true;
        }
    }

    internal static void UpdateUnitPosition(string actor, int x, int z)
    {
        lock (_unitsLock)
        {
            if (Units.TryGetValue(actor, out var existing))
            {
                existing.X = x;
                existing.Y = z;
                Units[actor] = existing;
                _unitsDirty = true;
            }
        }
    }

    internal static List<OverlayUnit> GetUnitsSnapshot()
    {
        lock (_unitsLock)
        {
            if (_unitsDirty)
            {
                _cachedSnapshot = new List<OverlayUnit>(Units.Values);
                _unitsDirty = false;
            }
            return _cachedSnapshot;
        }
    }

    internal static void Reset()
    {
        Boundary.GameStore.Clear();
        lock (_unitsLock) { Units.Clear(); _unitsDirty = false; }
    }
}
