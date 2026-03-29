using System;
using System.Text.Json;
using HarmonyLib;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;

namespace BOAM.GameEvents;

static class DecisionCaptureEvent
{
    internal static bool IsActive => Boundary.GameEvents.DecisionCapture;
}

[HarmonyPatch(typeof(Agent), nameof(Agent.Execute))]
static class Patch_AgentExecute
{
    static void Prefix(Agent __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
            if (!DecisionCaptureEvent.IsActive) return;

            var actor = __instance.m_Actor;
            if (actor == null) return;

            var active = __instance.m_ActiveBehavior;
            if (active == null) return;

            var info = ActorRegistry.GetActorInfo(actor);
            if (info == null) return;
            var (gameObj, factionId, actorEntityId, _) = info.Value;
            var actorUuid = ActorRegistry.GetUuid(actorEntityId);

            int round = bridge.Round;

            var chosenId = (int)active.GetID();
            var chosenName = active.GetName() ?? active.GetID().ToString();
            var chosenScore = active.GetScore();

            object target = null;
            try
            {
                var moveBehavior = active.TryCast<Move>();
                if (moveBehavior != null)
                {
                    var targetTile = moveBehavior.GetTargetTile();
                    if (targetTile?.Tile != null)
                    {
                        target = new
                        {
                            x = targetTile.Tile.GetX(),
                            z = targetTile.Tile.GetZ(),
                            apCost = targetTile.APCost
                        };
                    }
                }
            }
            catch { }

            if (target == null)
            {
                try
                {
                    var skillBehavior = active.TryCast<Il2CppMenace.Tactical.AI.SkillBehavior>();
                    if (skillBehavior != null && skillBehavior.m_TargetTile != null)
                    {
                        target = new
                        {
                            x = skillBehavior.m_TargetTile.GetX(),
                            z = skillBehavior.m_TargetTile.GetZ(),
                            apCost = 0
                        };
                    }
                }
                catch { }
            }

            object attackCandidates = null;
            try
            {
                var attackBehavior = active.TryCast<Attack>();
                if (attackBehavior != null)
                {
                    var candidates = attackBehavior.m_Candidates;
                    if (candidates != null && candidates.Count > 0)
                    {
                        var candList = new System.Collections.Generic.List<object>();
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            try
                            {
                                var cand = candidates[i];
                                if (cand.Target == null) continue;
                                candList.Add(new
                                {
                                    x = cand.Target.GetX(),
                                    z = cand.Target.GetZ(),
                                    score = cand.Score
                                });
                            }
                            catch { }
                        }
                        if (candList.Count > 0)
                            attackCandidates = candList;
                    }
                }
            }
            catch { }

            var alternatives = new System.Collections.Generic.List<object>();
            try
            {
                var behaviors = __instance.GetBehaviors();
                if (behaviors != null)
                {
                    for (int i = 0; i < behaviors.Count; i++)
                    {
                        var b = behaviors[i];
                        if (b == null) continue;
                        alternatives.Add(new
                        {
                            behaviorId = (int)b.GetID(),
                            name = b.GetName() ?? b.GetID().ToString(),
                            score = b.GetScore()
                        });
                    }
                }
            }
            catch { }

            var payload = JsonSerializer.Serialize(new
            {
                round,
                faction = factionId,
                actor = actorUuid,
                chosen = new
                {
                    behaviorId = chosenId,
                    name = chosenName,
                    score = chosenScore
                },
                target,
                alternatives,
                attackCandidates
            });

            BoamBridge.Logger.Msg($"[BOAM] action-decision {actorUuid}: {chosenName}({chosenScore})");
            QueryCommandClient.SendEvent("action-decision", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] action-decision error: {ex.Message}");
        }
    }
}
