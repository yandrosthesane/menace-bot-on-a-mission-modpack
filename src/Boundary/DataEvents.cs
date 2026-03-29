using System;
using System.IO;
using System.Text.RegularExpressions;
using MelonLoader;
using static BOAM.TacticalMap.JsonHelper;

namespace BOAM.Boundary;

/// <summary>
/// Static flags per data event, read from the "dataEvents" list in behaviour.json5.
/// Hooks check these flags before gathering data.
/// </summary>
internal static class DataEvents
{
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

            var userPath = Path.Combine(persistentDir, "configs", "behaviour.json5");
            var defaultPath = Path.Combine(modFolder, "configs", "behaviour.json5");

            var configPath = ConfigResolver.Resolve(userPath, defaultPath, "behaviour config", log);
            var json = StripJsonComments(File.ReadAllText(configPath));

            var eventsArray = ReadArray(json, "dataEvents");
            if (eventsArray != null)
            {
                foreach (Match m in Regex.Matches(eventsArray, "\"([^\"]+)\""))
                {
                    switch (m.Groups[1].Value)
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
            }

            log.Msg($"[BOAM] DataEvents: contact={ContactState} movement={MovementBudget} objectives={ObjectiveDetection} modifiers={TileModifiers} opponents={OpponentTracking} tileScores={TileScores} decisions={DecisionCapture} minimapUnits={MinimapUnits} actionLog={ActionLogging} combatLog={CombatLogging}");
        }
        catch (Exception ex)
        {
            log.Warning($"[BOAM] DataEvents init failed, all disabled: {ex.Message}");
        }
    }
}
