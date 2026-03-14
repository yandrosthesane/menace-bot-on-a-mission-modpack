# useskill Command — Fixed (2026-03-14)

The `useskill` command was broken due to two issues:

### Bug 1: Wrong namespace for TacticalState
- `GameType.Find("Menace.Tactical.TacticalState")` → returns null
- Correct: `GameType.Find("Menace.States.TacticalState")`
- The SDK's own `TacticalController.cs` and `EntityCombat.cs` both use `Menace.States.TacticalState`

### Bug 2: Wrong execution method
- `skill.Use(tile, null)` does NOT exist on `Il2CppMenace.Tactical.Skills.BaseSkill`
- `SkillContainer.UseSkill()` also doesn't exist at Il2Cpp runtime
- **Correct flow:** `TacticalState.TrySelectSkill(skill)` → creates `SkillAction` internally → `SkillAction.HandleLeftClickOnTile(tile)` for aimed skills
- Self-targeted skills (Deploy, Get Up, Vehicle Rotation) execute **immediately** via `TrySelectSkill` — no click simulation needed

### Bug 3: BaseSkill vs Skill type mismatch
- `GetAllSkills()` returns `BaseSkill` proxies
- `TrySelectSkill()` expects `Skill` (derived type)
- Fix: wrap the BaseSkill's `Pointer` in a `Skill` constructor: `skillManagedType.GetConstructor(typeof(IntPtr)).Invoke(ptr)`

### Key Il2Cpp type mapping
- `Menace.Tactical.Skills.Skill` — the derived type with full functionality
- `Menace.Tactical.Skills.BaseSkill` — base type returned by enumerators
- `Menace.States.TacticalState` — NOT `Menace.Tactical.TacticalState`

### Also fixed: AmbiguousMatchException
All `GetMethod()` calls in useskill replaced with `GetMethods().FirstOrDefault(...)` to avoid Il2Cpp overload ambiguity.

### EntityCombat.UseAbility still broken
`EntityCombat.UseAbility` at line 201 still uses `skill.GetType().GetMethod("Use")` which will fail the same way. Not fixed yet — low priority since `useskill` console command works.
