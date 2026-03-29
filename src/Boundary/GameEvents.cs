using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using MelonLoader;
using static BOAM.TacticalMap.JsonHelper;

namespace BOAM.Boundary;

/// <summary>
/// Static flags per game event + enrichment hook chains, read from game_events.json5.
/// </summary>
internal static class GameEvents
{
    // Enrichment hooks: host event name → ordered list of enrichment event names
    internal static Dictionary<string, List<string>> Hooks = new();

    // Feature → required events mapping
    private static readonly Dictionary<string, string[]> FeatureDeps = new()
    {
        ["behaviour"] = new[] {
            "on-turn-start", "on-turn-end", "contact-state", "movement-budget",
            "tile-modifiers", "opponent-tracking", "tile-scores",
            "battle-start", "battle-end", "tactical-ready", "scene-change"
        },
        ["minimap"] = new[] {
            "minimap-units", "actor-changed", "movement-finished", "preview-ready"
        },
        ["heatmaps"] = new[] {
            "tile-scores", "decision-capture"
        },
        ["logging"] = new[] {
            "action-logging", "combat-logging", "decision-capture"
        },
    };
    // Core
    internal static bool OnTurnStart;
    internal static bool OnTurnEnd;
    internal static bool MovementFinished;
    internal static bool ActorChanged;
    internal static bool SceneChange;
    internal static bool BattleStart;
    internal static bool BattleEnd;
    internal static bool TacticalReady;
    internal static bool PreviewReady;

    // Behaviour
    internal static bool ContactState;
    internal static bool MovementBudget;
    internal static bool ObjectiveDetection;
    internal static bool TileModifiers;
    internal static bool OpponentTracking;

    // Observation
    internal static bool TileScores;
    internal static bool DecisionCapture;
    internal static bool MinimapUnits;

    // Logging
    internal static bool ActionLogging;
    internal static bool CombatLogging;

    private static void Activate(string name)
    {
        switch (name)
        {
            case "on-turn-start": OnTurnStart = true; break;
            case "on-turn-end": OnTurnEnd = true; break;
            case "movement-finished": MovementFinished = true; break;
            case "actor-changed": ActorChanged = true; break;
            case "scene-change": SceneChange = true; break;
            case "battle-start": BattleStart = true; break;
            case "battle-end": BattleEnd = true; break;
            case "tactical-ready": TacticalReady = true; break;
            case "preview-ready": PreviewReady = true; break;
            case "contact-state": ContactState = true; break;
            case "movement-budget": MovementBudget = true; break;
            case "objective-detection": ObjectiveDetection = true; break;
            case "tile-modifiers": TileModifiers = true; break;
            case "opponent-tracking": OpponentTracking = true; break;
            case "tile-scores": TileScores = true; break;
            case "decision-capture": DecisionCapture = true; break;
            case "minimap-units": MinimapUnits = true; break;
            case "action-logging": ActionLogging = true; break;
            case "combat-logging": CombatLogging = true; break;
        }
    }

    internal static void Init(string modFolder, MelonLogger.Instance log)
    {
        OnTurnStart = false;
        OnTurnEnd = false;
        MovementFinished = false;
        ActorChanged = false;
        SceneChange = false;
        BattleStart = false;
        BattleEnd = false;
        TacticalReady = false;
        PreviewReady = false;
        ContactState = false;
        MovementBudget = false;
        ObjectiveDetection = false;
        TileModifiers = false;
        OpponentTracking = false;
        TileScores = false;
        DecisionCapture = false;
        MinimapUnits = false;
        ActionLogging = false;
        CombatLogging = false;

        try
        {
            var persistentDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "BOAM");

            var userPath = Path.Combine(persistentDir, "configs", "game_events.json5");
            var defaultPath = Path.Combine(modFolder, "configs", "game_events.json5");

            var configPath = ConfigResolver.Resolve(userPath, defaultPath, "game events config", log);
            var json = StripJsonComments(File.ReadAllText(configPath));

            // Features expand to their dependency events
            var featuresArray = ReadArray(json, "features");
            if (featuresArray != null)
            {
                foreach (Match fm in Regex.Matches(featuresArray, "\"([^\"]+)\""))
                {
                    if (FeatureDeps.TryGetValue(fm.Groups[1].Value, out var deps))
                        foreach (var dep in deps)
                            Activate(dep);
                }
            }

            // Active list adds on top of features
            var eventsArray = ReadArray(json, "active");
            if (eventsArray != null)
            {
                foreach (Match m in Regex.Matches(eventsArray, "\"([^\"]+)\""))
                    Activate(m.Groups[1].Value);
            }

            // Parse enrichment hooks
            Hooks.Clear();
            var hooksObj = ReadObject(json, "hooks");
            if (hooksObj != null)
            {
                foreach (Match hm in Regex.Matches(hooksObj, "\"([^\"]+)\"\\s*:\\s*\\[([^\\]]*)\\]"))
                {
                    var hostName = hm.Groups[1].Value;
                    var enrichments = new List<string>();
                    foreach (Match em in Regex.Matches(hm.Groups[2].Value, "\"([^\"]+)\""))
                        enrichments.Add(em.Groups[1].Value);
                    Hooks[hostName] = enrichments;
                }
            }

            log.Msg($"[BOAM] GameEvents: contact={ContactState} movement={MovementBudget} objectives={ObjectiveDetection} modifiers={TileModifiers} opponents={OpponentTracking} tileScores={TileScores} decisions={DecisionCapture} minimapUnits={MinimapUnits} actionLog={ActionLogging} combatLog={CombatLogging}");
        }
        catch (Exception ex)
        {
            log.Warning($"[BOAM] GameEvents init failed, all disabled: {ex.Message}");
        }
    }
}
