using System;
using System.Linq;
using Il2CppMenace.Tactical;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Stable actor UUID registry. Builds the dramatis personae at tactical-ready,
/// maintains bidirectional entity ID ↔ UUID mappings for the session.
/// </summary>
internal static class ActorRegistry
{
    private static System.Collections.Generic.Dictionary<int, string> _entityToUuid = new();
    private static System.Collections.Generic.Dictionary<string, int> _uuidToEntity = new();

    /// Resolve entity ID to stable UUID. Returns "unknown.{entityId}" if not found.
    public static string GetUuid(int entityId)
    {
        return _entityToUuid.TryGetValue(entityId, out var uuid) ? uuid : $"unknown.{entityId}";
    }

    /// Resolve stable UUID to entity ID. Returns -1 if not found.
    public static int GetEntityId(string uuid)
    {
        return _uuidToEntity.TryGetValue(uuid, out var id) ? id : -1;
    }

    /// Faction index to stable name for UUID computation.
    internal static string FactionName(int faction)
    {
        return faction switch
        {
            0 => "neutral",
            1 => "player",
            2 => "allied",
            3 => "civilian",
            4 => "allied_local",
            5 => "enemy_local",
            6 => "pirates",
            7 => "wildlife",
            8 => "constructs",
            9 => "rogue_army",
            _ => $"faction{faction}"
        };
    }

    /// Template short name — last segment after the dot.
    internal static string TemplateShort(string template)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var lastDot = template.LastIndexOf('.');
        return lastDot >= 0 ? template.Substring(lastDot + 1) : template;
    }

    /// Extract common actor info from an Il2Cpp Actor reference.
    internal static (GameObj gameObj, int factionId, int entityId, string templateName)?
        GetActorInfo(Actor actor)
    {
        if (actor == null) return null;
        var gameObj = new GameObj(actor.Pointer);
        var info = EntitySpawner.GetEntityInfo(gameObj);
        var tplObj = gameObj.ReadObj("m_Template");
        var templateName = tplObj.IsNull ? "" : (tplObj.GetName() ?? "");
        return (gameObj, info?.FactionIndex ?? 0, info?.EntityId ?? 0, templateName);
    }

    /// Extract position from a GameObj via EntityMovement.
    internal static (int x, int z) GetPos(GameObj gameObj)
    {
        var pos = EntityMovement.GetPosition(gameObj);
        return pos.HasValue ? (pos.Value.x, pos.Value.y) : (0, 0);
    }

    /// Build the full dramatis personae on the main thread.
    /// Scans all entities, computes stable UUIDs, populates the registries.
    internal static System.Collections.Generic.List<object> BuildDramatisPersonae(MelonLogger.Instance log)
    {
        var result = new System.Collections.Generic.List<object>();
        var entries = new System.Collections.Generic.List<(int entityId, string template, int faction, string leader, int x, int z, bool isAlive)>();

        try
        {
            var allActors = EntitySpawner.ListEntities(-1);
            if (allActors == null) return result;
            foreach (var actor in allActors)
            {
                var info = EntitySpawner.GetEntityInfo(actor);
                if (info == null) continue;
                var pos = EntityMovement.GetPosition(actor);
                var go = new GameObj(actor.Pointer);
                var tplObj = go.ReadObj("m_Template");
                var templateName = tplObj.IsNull ? "" : (tplObj.GetName() ?? "");

                var leaderName = "";
                try
                {
                    var unitActor = new Il2CppMenace.Tactical.UnitActor(actor.Pointer);
                    var leader = unitActor.GetLeader();
                    if (leader != null)
                    {
                        var nickname = leader.GetNickname();
                        if (nickname != null)
                            leaderName = nickname.GetTranslated() ?? "";
                    }
                }
                catch { }

                entries.Add((info.EntityId, templateName, info.FactionIndex,
                    leaderName.ToLowerInvariant(), pos?.x ?? 0, pos?.y ?? 0, info.IsAlive));
            }
        }
        catch (Exception ex)
        {
            log.Error($"[BOAM] BuildDramatisPersonae error: {ex.Message}");
            return result;
        }

        var newEntityToUuid = new System.Collections.Generic.Dictionary<int, string>();
        var newUuidToEntity = new System.Collections.Generic.Dictionary<string, int>();

        var groups = entries
            .GroupBy(e => (e.faction, e.template))
            .ToList();

        foreach (var group in groups)
        {
            var sorted = group.OrderBy(e => e.x).ThenBy(e => e.z).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var e = sorted[i];
                var factionStr = FactionName(e.faction);
                var shortTemplate = TemplateShort(e.template);

                string uuid;
                if (!string.IsNullOrEmpty(e.leader))
                    uuid = $"{factionStr}.{e.leader}";
                else
                    uuid = $"{factionStr}.{shortTemplate}.{i + 1}";

                newEntityToUuid[e.entityId] = uuid;
                newUuidToEntity[uuid] = e.entityId;

                result.Add(new
                {
                    actor = uuid,
                    template = e.template,
                    faction = e.faction,
                    leader = e.leader,
                    x = e.x,
                    z = e.z,
                    isAlive = e.isAlive
                });
            }
        }

        _entityToUuid = newEntityToUuid;
        _uuidToEntity = newUuidToEntity;
        log.Msg($"[BOAM] Dramatis personae: {newEntityToUuid.Count} actors registered");

        return result;
    }
}
