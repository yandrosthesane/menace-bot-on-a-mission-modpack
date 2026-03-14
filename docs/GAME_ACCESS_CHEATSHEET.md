---
name: game access cheatsheet
description: Proven working patterns for accessing game functions — SDK first, direct Il2Cpp types second, reflection last resort
type: reference
---

# Game Access Cheatsheet

Priority order: **SDK wrappers > Direct Il2Cpp types > Reflection**. Never use reflection when a direct type or SDK method exists.

## 1. SDK Wrappers (always prefer these)

These work from ANY thread (HTTP or main).

```csharp
// List all actors (or filter by faction)
GameObj[] actors = EntitySpawner.ListEntities(-1);        // -1 = all, 1 = player
GameObj[] players = EntitySpawner.ListEntities(1);

// Actor info (entityId, name, faction, isAlive)
EntitySpawner.EntityInfo info = EntitySpawner.GetEntityInfo(gameObj);
info.EntityId    // int — dynamic per mission load!
info.FactionIndex
info.IsAlive

// Position
Vector2Int? pos = EntityMovement.GetPosition(gameObj);
pos.Value.x, pos.Value.y   // note: y = z in tile coords

// Template name (the m_Template pattern)
GameObj tplObj = gameObj.ReadObj("m_Template");
string templateName = tplObj.IsNull ? "" : (tplObj.GetName() ?? "");

// Round number
int round = TacticalController.GetCurrentRound();

// Current faction
int faction = TacticalController.GetCurrentFaction();

// Active actor (⚠ MAIN THREAD ONLY for reliable results)
GameObj active = TacticalController.GetActiveActor();

// Vision
int range = LineOfSight.GetVision(gameObj);

// Tile access
GameObj tile = TileMap.GetTile(x, z);
```

## 2. Direct Il2Cpp Types (use for game-specific APIs)

ModpackLoader AND modpack code can use these — Assembly-CSharp.dll is referenced by both.

```csharp
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;
using Il2CppMenace.Tactical.Skills;

// Construct Il2Cpp proxy from pointer
var actor = new Actor(gameObj.Pointer);
var unitActor = new UnitActor(gameObj.Pointer);  // extends Actor
var entity = new Entity(gameObj.Pointer);          // not a Unity GameObject!

// Leader name (works from HTTP thread — tested)
var unitActor = new UnitActor(actorPointer);
var leader = unitActor.GetLeader();
string name = leader?.GetNickname()?.GetTranslated() ?? "";

// Tile methods (from Harmony patch parameters)
int x = tile.GetX();
int z = tile.GetZ();

// Skill title
string skillName = skill.GetTitle() ?? "";

// AI behavior (from Agent patches)
agent.m_Actor           // Actor
agent.m_Tiles           // scored tiles dictionary
agent.m_ActiveBehavior  // chosen behavior
behavior.GetID()        // BehaviorID enum
behavior.GetName()      // string
behavior.GetScore()     // int
behavior.TryCast<Move>()           // safe downcast
behavior.TryCast<Attack>()
behavior.TryCast<SkillBehavior>()

// Faction info (from Harmony patches)
aiFaction.GetIndex()           // int
aiFaction.m_Opponents          // List<Opponent>
opponent.Actor                 // Actor
opponent.TTL, opponent.IsKnown()

// Entity container (for embark/disembark)
// Use reflection for ContainEntity/EjectEntity — see section 3
// But check flags directly:
entity.IsContainerForEntities()         // bool
entity.IsContainableWithin(otherEntity) // bool
entity.IsContainedWithinAnotherEntity() // bool
entity.GetContainerEntity()             // Entity
```

## 3. Reflection (last resort — when SDK/direct types don't expose the method)

### Safe reflection pattern (avoid AmbiguousMatchException)
```csharp
// NEVER: type.GetMethod("MethodName")  — ambiguous with overloads
// ALWAYS: filter with GetMethods + FirstOrDefault
var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .FirstOrDefault(m => m.Name == "MethodName" && m.GetParameters().Length == 0);
```

### GameType.Find — correct namespaces
```csharp
GameType.Find("Menace.Tactical.Actor")              // ✅
GameType.Find("Menace.Tactical.Entity")              // ✅
GameType.Find("Menace.Tactical.Tile")                // ✅
GameType.Find("Menace.Tactical.TacticalManager")     // ✅
GameType.Find("Menace.Tactical.Skills.Skill")        // ✅
GameType.Find("Menace.Tactical.Skills.BaseSkill")    // ✅
GameType.Find("Menace.States.TacticalState")         // ✅ — NOT Menace.Tactical!
GameType.Find("Menace.Tactical.UnitActor")           // ✅

GameType.Find("Menace.Tactical.TacticalState")       // ❌ WRONG NAMESPACE
```

### Managed proxy from pointer
```csharp
var managedType = GameType.Find("Menace.Tactical.Entity")?.ManagedType;
var proxy = Il2CppUtils.GetManagedProxy(gameObj, managedType);
// Then call methods via reflection on proxy
```

### Pointer constructor (cast between Il2Cpp types)
```csharp
// When you have a BaseSkill but need Skill:
var skillType = GameType.Find("Menace.Tactical.Skills.Skill")?.ManagedType;
var ptrCtor = skillType.GetConstructor(new[] { typeof(IntPtr) });
var skillProxy = ptrCtor.Invoke(new object[] { baseSkill.Pointer });
```

### Skill execution (the working pattern)
```csharp
// 1. Get TacticalState
var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
var ts = tsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .FirstOrDefault(m => m.Name == "Get").Invoke(null, null);

// 2. TrySelectSkill — self-targeting skills execute immediately
ts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .FirstOrDefault(m => m.Name == "TrySelectSkill")
    .Invoke(ts, new[] { skillProxy });

// 3. For aimed skills, get current action and click
var action = ts.GetType().GetMethods(...)
    .FirstOrDefault(m => m.Name == "GetCurrentAction").Invoke(ts, null);
action?.GetType().GetMethods(...)
    .FirstOrDefault(m => m.Name == "HandleLeftClickOnTile")
    .Invoke(action, new[] { tileProxy });
```

## 4. Harmony Patches (proven patterns from BoamBridge.cs)

### Attribute-based (preferred — works for types in Assembly-CSharp)
```csharp
[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnTurnStart))]
[HarmonyPatch(typeof(TacticalManager), "InvokeOnMovementFinished")]
[HarmonyPatch(typeof(TacticalManager), "InvokeOnMovement")]
[HarmonyPatch(typeof(TacticalManager), "InvokeOnSkillUse")]
[HarmonyPatch(typeof(Agent), nameof(Agent.Execute))]
[HarmonyPatch(typeof(Agent), "PostProcessTileScores")]
```

### Manual patch (for types resolved via GameType.Find)
```csharp
var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
var method = tsType.GetMethod("EndTurn", BindingFlags.Public | BindingFlags.Instance);
harmony.Patch(method, prefix: new HarmonyMethod(typeof(MyPatch), nameof(MyPatch.Prefix)));
```

## 5. Helper patterns from BoamBridge

```csharp
// Extract actor info tuple (reusable)
var (gameObj, factionId, entityId, templateName) = BoamBridge.GetActorInfo(actor).Value;

// Extract position
var (x, z) = BoamBridge.GetPos(gameObj);

// Synchronous HTTP (avoids async deadlocks under Wine CLR)
using var client = new System.Net.WebClient();
client.DownloadString(url);          // GET
client.UploadString(url, json);      // POST
// NEVER use HttpClient async under Wine — use WebClient sync
```

## 6. DANGER — Things that DON'T work

- `Entity` pointers are NOT `UnityEngine.GameObject` — `new GameObject(entity.Pointer)` crashes
- `skill.Use(tile, null)` does NOT exist on `BaseSkill` — use `TacticalState.TrySelectSkill`
- `SkillContainer.UseSkill()` does NOT exist at Il2Cpp runtime
- `GetMethod("Name")` without parameter filtering → `AmbiguousMatchException` on Il2Cpp types
- `m_EnterContainerAfterMovement` on Actor is NOT accessible via reflection (returns null)
- `HttpClient` async deadlocks under Wine CLR — use `WebClient` sync
- `TacticalController.SetActiveActor()` doesn't reliably change the game's active actor
- `TacticalController.GetActiveActor()` from HTTP thread may return stale data — use main thread
- `Entity.ContainEntity()` / `EjectEntity()` — raw container ops, leave game state inconsistent (UI, camera, skills not updated). Use native click flow instead.
- `GameType.Find("Menace.Tactical.NoneAction")` — NOT FOUND. Action classes not resolvable via GameType.Find.
- `GetCurrentAction().HandleLeftClickOnTile()` — `HandleLeftClickOnTile` not found via reflection on the returned base type (`TacticalAction`). Virtual methods not exposed on Il2Cpp base types. Need direct Il2Cpp type cast.
- Game's native embark flow: `ComputeActorPath(Tile)` + `ExecuteActorTravel()` on TacticalState — confirmed via diagnostic patches. But clicking the vehicle tile through `NoneAction.HandleLeftClickOnTile` is the proper UI-level approach.

## 7. Threading

| Operation | Thread Safety |
|-----------|--------------|
| `EntitySpawner.ListEntities()` | Any thread |
| `EntitySpawner.GetEntityInfo()` | Any thread |
| `EntityMovement.GetPosition()` | Any thread |
| `gameObj.ReadObj("m_Template")` | Any thread |
| `UnitActor.GetLeader()` | Any thread |
| `TacticalController.GetActiveActor()` | Main thread only (stale from HTTP) |
| `TacticalState.TrySelectSkill()` | Main thread only |
| `EntityMovement.MoveTo()` | Main thread only |
| `Entity.ContainEntity()` | Main thread only |
| Harmony patch callbacks | Game's calling thread (usually main) |
