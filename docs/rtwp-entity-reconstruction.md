# RTwP entity reconstruction research

Local evidence: the installed `Assembly-CSharp.dll` from WotR 2.7.0 was inspected with ILSpyCmd 9.1.0.7988 on 2026-10-01. This is research for the experimental local mod, not a claim of working reconstruction.

## Missing summoned unit

`Kingmaker.Controllers.EntityCreationController.SpawnUnit(BlueprintUnit, Vector3, Quaternion, SceneEntitiesState, string uniqueId = null)` can create a unit with the host's ID. The method also consults `UnitSpawningData` for customization variation, race, gender, voice, handedness, hidden/optimized form, prefab and doll. It returns null for an invalid blueprint, unavailable scene state or prefab. The current `NetworkUnit` checkpoint contains blueprint, origin IDs, position and selected combat fields, but not the full `UnitSpawningData`, equipment, facts, spellbook, AI and summoned-unit parts. Spawning a bare blueprint at the correct coordinates would not reconstruct an equivalent combatant.

The game's `Kingmaker.EntitySystem.Persistence.JsonUtility.UnitSerialization.Serialize(UnitEntityData)` calls `TurnOff`, `PreSave` and `TurnOn` around serialization. It must not be used as an ordinary per-frame RTwP snapshot operation: those calls mutate a live unit. `OwlcatJsonConvert` exposes game-specific JSON settings, but deserializing an entity still requires safe registration, view creation, reference resolution and rollback. None of that is implemented here. Protocol 24 records the source unit's `HoldingState.SceneName` so a client can reject an existing same-ID unit in the wrong scene.

## Missing area effect

`Kingmaker.Controllers.AreaEffectsController.Spawn(MechanicsContext, BlueprintAbilityAreaEffect, TargetWrapper, TimeSpan?)` clones the complete mechanics context, creates an `AreaEffectView`, assigns a new ID, initializes timing/target, registers the entity in a `SceneEntitiesState`, and raises an area-effect spawn event. `AreaEffectEntityData` persists the mechanics context, blueprint, target, creation time, duration and other fields. The network checkpoint currently carries blueprint/caster/owner/source IDs, timing and (since protocol 24) `HoldingState.SceneName`, but not a restorable `MechanicsContext` or `TargetWrapper`. Recalling `Spawn` from those IDs alone could recalculate rules and trigger effects twice, as well as assign the wrong ID.

## Required implementation boundary

Before allowing repair to create a missing unit or area, capture the host's complete creation context at the original spawn event, transfer it with epoch and entity ID, and validate the referenced scene, blueprint and caster on the client. Creation must run only inside a paused repair barrier, suppress duplicate rule effects, verify the canonical checkpoint hash, and roll back the newly created entity if application fails. Until an in-game two-instance test proves this lifecycle, missing entities must continue to fail preflight and keep saving blocked during unsuccessful fallback.
