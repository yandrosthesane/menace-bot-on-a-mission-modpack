using System;
using System.Collections.Generic;
using UnityEngine;

namespace BOAM.TacticalMap;

/// <summary>
/// Shared singleton state for the tactical minimap overlay.
/// Updated by game hooks (tile-scores, movement-finished, actor-changed).
/// Read by the IMGUI overlay for rendering.
/// </summary>
internal static class TacticalMapState
{
    // --- Map background ---

    /// <summary>Captured map texture (set at OnPreviewReady, used by overlay + saved to battle session).</summary>
    internal static Texture2D MapTexture;

    /// <summary>Tile grid dimensions.</summary>
    internal static int TilesX, TilesZ;

    /// <summary>Binary tile data for map generation (heights + flags).</summary>
    internal static TileData[] TileDataArray;
    internal static float HeightMin, HeightMax;

    /// <summary>Battle session directory — created at tactical-ready from preview data.</summary>
    internal static string BattleSessionDir;

    /// <summary>Fixed preview directory — map data written here at preview-ready, copied to battle report at tactical-ready.</summary>
    internal static string PreviewDir;

    // --- Unit positions (updated by hooks) ---

    /// <summary>Current known unit positions, keyed by actor UUID.</summary>
    internal static readonly Dictionary<string, OverlayUnit> Units = new();
    private static readonly object _unitsLock = new();

    /// <summary>Dirty flag — set when units change, cleared when snapshot is taken.</summary>
    private static bool _unitsDirty;
    private static List<OverlayUnit> _cachedSnapshot = new();

    /// <summary>Replace all unit positions (called from tile-scores hook which provides full unit list).</summary>
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

    /// <summary>Update a single unit's position (called from movement-finished, actor-changed).</summary>
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

    /// <summary>Get a snapshot of current units for rendering. Only allocates when data changed.</summary>
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

    // --- Round tracking ---

    internal static int CurrentRound;
    internal static int CurrentFaction;
    internal static string ActiveActor = "";

    // --- Lifecycle ---

    /// <summary>Reset all state when leaving tactical scene.</summary>
    internal static void Reset()
    {
        MapTexture = null;
        TilesX = 0;
        TilesZ = 0;
        TileDataArray = null;
        HeightMin = 0;
        HeightMax = 1;
        BattleSessionDir = null;
        CurrentRound = 0;
        CurrentFaction = 0;
        ActiveActor = "";
        lock (_unitsLock) { Units.Clear(); }
    }
}
