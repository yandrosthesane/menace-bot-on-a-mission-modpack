# Step 10: Vehicle Embark/Disembark System

## Problem

During replay, `move <x> <z>` targeting a vehicle's tile doesn't embark the unit. The unit stops one tile short because the vehicle tile isn't walkable. The game's native embark flow is:

1. Player clicks the vehicle → game moves unit to adjacent tile
2. `m_EnterContainerAfterMovement` is set on the Actor
3. After movement completes, `Entity.ContainEntity()` is called
4. `Actor.OnEnteredContainer()` fires

Our `move` console command only calls `Actor.MoveTo()` which pathfinds to the target tile — it doesn't handle the container entry step.

## Game Container Architecture

### Types

```csharp
public enum EntityContainerType { None=0, Building=1, Vehicle=2, Forest=3, Tower=4 }
public enum MovementAction { Default=0, Enter=1, Leave=2, Backwards=4, Teleport=8 }
```

### Entity.cs — Container Operations

```csharp
// Storage
protected Entity m_ContainedEntity;   // entity inside this container
protected Entity m_ContainerEntity;   // container this entity is inside

// Validation
public bool IsContainableWithin(Entity _entity);
public bool IsContainerForEntities();
public bool IsContainedWithinAnotherEntity();
public bool IsContainingAnotherEntity();

// Operations
public void ContainEntity(Entity _other);           // put entity inside
public bool EjectEntity(Tile _tile);                 // remove and place on tile
public bool IsEntranceTileBlocked(Entity _specific, Tile _target = null);
public bool IsExitTile(Tile _tile);

// Events
protected virtual void OnEnteredContainer(Entity _containerEntity);
protected virtual void OnLeftContainer();
```

### Actor.cs — Movement + Container

```csharp
private Entity m_EnterContainerAfterMovement;   // set before MoveTo, triggers entry after
private bool CanEnterAnyAdjacentVehicle();       // private validation
public bool MoveTo(Tile _tile, ref MovementAction _action, MovementFlags _flags);
public bool MoveToWithSkill(Tile _tile, ref MovementAction _action, IMovementSkill _skill, MovementFlags _flags);
```

### TacticalManager Events

```csharp
// Movement event includes container parameter
public delegate void OnMovementEvent(Actor _actor, Tile _from, Tile _to, MovementAction _action, Entity _container);
public void InvokeOnMovement(Actor _actor, Tile _from, Tile _to, MovementAction _action, Entity _container);
public void InvokeOnMovementFinished(Actor _actor, Tile _to);
```

### EntityTemplate Properties

```csharp
public bool IsContainableInEntities;        // infantry: true
public EntityContainerType ContainerType;   // Vehicle = 2
```

## Implementation (Done)

### Console Command: `embark <vehicleId>` — DevConsole.cs

1. Get the selected actor via `TacticalController.GetActiveActor()`
2. Find the vehicle entity by ID via `EntitySpawner.ListEntities()` scan
3. Get managed proxies via `Il2CppUtils.GetManagedProxy()` as `Entity` type
4. Validate: `vehicle.IsContainerForEntities()` and `actor.IsContainableWithin(vehicle)`
5. Call `vehicle.ContainEntity(actor)` to embark
6. Main-thread queued via `EnqueueMainThread()`

### Console Command: `disembark <x> <z>` — DevConsole.cs

1. Get the selected actor
2. Check `actor.IsContainedWithinAnotherEntity()`
3. Get container via `actor.GetContainerEntity()`
4. Get target tile via `TacticalManager.Get().GetMap().GetTile(x, z)`
5. Call `container.EjectEntity(tile)`
6. Main-thread queued via `EnqueueMainThread()`

### Battle Log — BoamBridge.cs Patch_Movement

Harmony patch on `TacticalManager.InvokeOnMovement` detects embark events:
- Checks `MovementAction.Enter` flag and non-null `_container`
- Logs as `player_embark` with `vehicleId` field (container's entity ID)
- Only fires for player factions (1, 2)

### Replay — Replay.fs

- `ReplayAction` type includes `VehicleId` field
- Parser extracts `vehicleId` from JSONL
- `executeAction` sends `embark <vehicleId>` console command for `player_embark` actions
- `ActionLog.fs` serializes `vehicleId` in JSONL when > 0

### InvokeOnMovementFinished and Embark

`InvokeOnMovementFinished` does NOT fire for embark moves because the unit doesn't land on a regular tile — it enters the container. `InvokeOnMovement` DOES fire with `MovementAction.Enter` and the container entity — this is what `Patch_Movement` hooks into.

## References

- `Assembly-CSharp/Menace/Tactical/Entity.cs` — base container operations
- `Assembly-CSharp/Menace/Tactical/Actor.cs` — `m_EnterContainerAfterMovement`, movement
- `Assembly-CSharp/Menace/Tactical/EntityContainerType.cs` — enum
- `Assembly-CSharp/Menace/Tactical/MovementAction.cs` — Enter/Leave flags
- `Assembly-CSharp/Menace/Tactical/Skills/Effects/EjectEntityHandler.cs` — disembark skill
