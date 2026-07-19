# Unity MCP Animation Extension Plan

## Overview

This document tracks planned extensions to the `manage_animation` tool. The tool currently covers Animator component control, AnimatorController asset creation, and AnimationClip creation with keyframe curves. Several authoring workflows have gaps that block common game-development tasks. This plan organizes those gaps into four phases ordered by priority and complexity.

---

## Current State

**Tool:** `manage_animation` (group: `animation`)  
**Python:** `Server/src/services/tools/manage_animation.py`  
**C# dispatcher:** `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs`  
**C# sub-handlers:** `AnimatorRead`, `AnimatorControl`, `ControllerCreate`, `ControllerLayers`, `ControllerBlendTrees`, `ClipCreate`, `ClipPresets`

### Existing Actions (40 total)

| Prefix | Actions |
|--------|---------|
| `animator_` | `get_info`, `get_parameter`, `play`, `crossfade`, `set_parameter`, `set_speed`, `set_enabled` |
| `controller_` | `create`, `add_state`, `add_transition`, `add_parameter`, `get_info`, `assign`, `add_layer`, `remove_layer`, `set_layer_weight`, `create_blend_tree_1d`, `create_blend_tree_2d`, `add_blend_tree_child` |
| `clip_` | `create`, `get_info`, `add_curve`, `set_curve`, `set_vector_curve`, `create_preset`, `assign`, `add_event`, `remove_event` |

---

## Gap Analysis

### Authoring gaps (create-only, no delete/edit)
You can add states and transitions but cannot remove them or change a state's assigned clip after the fact. This makes iterative authoring require destroying and rebuilding controllers.

### No AnimatorOverrideController support
A common pattern — shared base controller with per-character clip swaps — has no tooling at all. There is no way to create or manage `AnimatorOverrideController` assets.

### No AvatarMask support
Layer-based masking (upper-body layer, weapon layer, etc.) requires `AvatarMask` assets. There is no way to create one or assign it to a controller layer.

### Clip editing completeness
Curves can be added but not removed. Clip loop/root-motion import settings cannot be changed after creation. There is no way to duplicate a clip or copy curves between clips.

### No IK Pass control
The IK pass flag on a controller layer cannot be set. This is required for `OnAnimatorIK` callbacks to fire on the layer.

### Runtime diagnostics are shallow
`animator_get_info` returns static structure. There is no way to query what state the Animator is currently in at runtime, current normalized time, or whether a transition is in progress.

---

## Phase 1 — State Machine Completeness

**Goal:** Allow full authoring lifecycle on states, transitions, and parameters. No more destroy-and-rebuild.

### New Actions

#### `controller_remove_state`
Remove a state from a layer. Also removes all transitions to/from that state.

**`properties`:**
```json
{
  "layer_index": 0,
  "state_name": "Run"
}
```

**C# location:** `ControllerCreate.RemoveState()`  
**Unity API:** `AnimatorStateMachine.RemoveState(AnimatorState)`

---

#### `controller_remove_transition`
Remove a specific transition by its source state, destination state, and optional index (when multiple transitions share the same endpoints).

**`properties`:**
```json
{
  "layer_index": 0,
  "from_state": "Walk",
  "to_state": "Run",
  "transition_index": 0
}
```

`from_state` may be `"AnyState"` or `"Entry"`.

**C# location:** `ControllerCreate.RemoveTransition()`  
**Unity API:** `AnimatorStateMachine.RemoveAnyStateTransition()` / `AnimatorState.RemoveTransition()`

---

#### `controller_set_state_motion`
Replace the clip assigned to an existing state without touching transitions or parameters.

**`properties`:**
```json
{
  "layer_index": 0,
  "state_name": "Run",
  "clip_path": "Assets/Animations/Run.anim"
}
```

**C# location:** `ControllerCreate.SetStateMotion()`  
**Unity API:** `AnimatorState.motion = clip`

---

#### `controller_set_default_state`
Set the default (entry) state on a layer.

**`properties`:**
```json
{
  "layer_index": 0,
  "state_name": "Idle"
}
```

**C# location:** `ControllerCreate.SetDefaultState()`  
**Unity API:** `AnimatorStateMachine.defaultState`

---

#### `controller_remove_parameter`
Remove an existing parameter by name.

**`properties`:**
```json
{
  "parameter_name": "Speed"
}
```

**C# location:** `ControllerCreate.RemoveParameter()`  
**Unity API:** `AnimatorController.RemoveParameter(AnimatorControllerParameter)`

---

#### `controller_edit_transition`
Modify the timing properties of an existing transition (duration, offset, exit time, interruption). Identified by source/destination state and optional index.

**`properties`:**
```json
{
  "layer_index": 0,
  "from_state": "Walk",
  "to_state": "Run",
  "transition_index": 0,
  "has_exit_time": true,
  "exit_time": 0.9,
  "duration": 0.15,
  "offset": 0.0,
  "interruption_source": "None"
}
```

`interruption_source` values: `"None"`, `"Source"`, `"Destination"`, `"SourceThenDestination"`, `"DestinationThenSource"`.

**C# location:** `ControllerCreate.EditTransition()`  
**Unity API:** `AnimatorStateTransition` properties

---

### Files Changed (Phase 1)

| File | Change |
|------|--------|
| `Server/src/services/tools/manage_animation.py` | Add 6 actions to `CONTROLLER_ACTIONS` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add 6 cases to `HandleControllerAction()` |
| `MCPForUnity/Editor/Tools/Animation/ControllerCreate.cs` | Add `RemoveState`, `RemoveTransition`, `SetStateMotion`, `SetDefaultState`, `RemoveParameter`, `EditTransition` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add snake_case aliases to `ParamAliases`: `transition_index`, `interruption_source` |
| `Server/tests/test_manage_animation.py` | Tests for each new action |
| `TestProjects/UnityMCPTests/Assets/Tests/Animation/` | Unity EditMode tests |

---

## Phase 2 — Clip Editing Completeness

**Goal:** Full CRUD on animation curves and improved clip settings control.

### New Actions

#### `clip_remove_curve`
Remove one or all curves from a clip. Targets a curve by `property_path` + `component_type`. Pass no property path to remove all curves.

**`clip_path`:** `"Assets/Animations/Idle.anim"`  
**`properties`:**
```json
{
  "property_path": "m_LocalPosition.x",
  "component_type": "UnityEngine.Transform",
  "relative_path": ""
}
```

**C# location:** `ClipCreate.RemoveCurve()`  
**Unity API:** `AnimationUtility.SetEditorCurve(clip, binding, null)`

---

#### `clip_set_loop_settings`
Change a clip's loop time, root motion extraction, and frame-rate settings after creation.

**`clip_path`:** `"Assets/Animations/Walk.anim"`  
**`properties`:**
```json
{
  "loop_time": true,
  "loop_pose": false,
  "cycle_offset": 0.0,
  "frame_rate": 30.0
}
```

**C# location:** `ClipCreate.SetLoopSettings()`  
**Unity API:** `clip.wrapMode`, `AnimationClipSettings` via `AnimationUtility.SetAnimationClipSettings()`

---

#### `clip_duplicate`
Duplicate an existing clip asset to a new path and return the new path.

**`clip_path`:** `"Assets/Animations/Walk.anim"` (source)  
**`properties`:**
```json
{
  "dest_path": "Assets/Animations/WalkFast.anim"
}
```

**C# location:** `ClipCreate.Duplicate()`  
**Unity API:** `AssetDatabase.CopyAsset()`

---

#### `clip_copy_curves`
Copy all curves from one clip into another clip (additive merge; existing curves in the destination are preserved unless `overwrite: true`).

**`clip_path`:** `"Assets/Animations/Source.anim"` (source)  
**`properties`:**
```json
{
  "dest_clip_path": "Assets/Animations/Dest.anim",
  "overwrite": false
}
```

**C# location:** `ClipCreate.CopyCurves()`  
**Unity API:** `AnimationUtility.GetAllCurves()` + `AnimationUtility.SetEditorCurve()`

---

### Files Changed (Phase 2)

| File | Change |
|------|--------|
| `Server/src/services/tools/manage_animation.py` | Add 4 actions to `CLIP_ACTIONS` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add 4 cases to `HandleClipAction()` |
| `MCPForUnity/Editor/Tools/Animation/ClipCreate.cs` | Add `RemoveCurve`, `SetLoopSettings`, `Duplicate`, `CopyCurves` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add alias `dest_path` → `destPath`, `dest_clip_path` → `destClipPath`, `loop_pose` → `loopPose`, `cycle_offset` → `cycleOffset` |
| `Server/tests/test_manage_animation.py` | Tests for each new action |
| `TestProjects/UnityMCPTests/Assets/Tests/Animation/` | Unity EditMode tests |

---

## Phase 3 — AnimatorOverrideController

**Goal:** Full support for override controllers — the standard pattern for sharing one AnimatorController across characters with different clip sets.

### New Actions

#### `controller_create_override`
Create an `AnimatorOverrideController` asset that wraps a base controller.

**`controller_path`:** `"Assets/Animators/HeroOverride.overrideController"`  
**`properties`:**
```json
{
  "base_controller_path": "Assets/Animators/Base.controller"
}
```

Returns the path to the created override controller asset.

**C# location:** New file `ControllerOverride.cs` — `Create()`  
**Unity API:** `AnimatorOverrideController`, `AssetDatabase.CreateAsset()`

---

#### `controller_override_set_clip`
Map an original clip name (from the base controller) to a replacement clip.

**`controller_path`:** `"Assets/Animators/HeroOverride.overrideController"`  
**`properties`:**
```json
{
  "original_clip_name": "Walk",
  "override_clip_path": "Assets/Animations/HeroWalk.anim"
}
```

**C# location:** `ControllerOverride.SetClip()`  
**Unity API:** `AnimatorOverrideController[originalClipName] = replacementClip`

---

#### `controller_override_get_clips`
Return the full override map: a list of `{original, override}` pairs. `override` is `null` for slots that haven't been mapped.

**`controller_path`:** `"Assets/Animators/HeroOverride.overrideController"`

**C# location:** `ControllerOverride.GetClips()`  
**Unity API:** `AnimatorOverrideController.GetOverrides()`

---

#### `controller_override_assign`
Assign an override controller to a GameObject's Animator component (runtime or edit-mode).

**`target`:** `"Hero"`  
**`controller_path`:** `"Assets/Animators/HeroOverride.overrideController"`

**C# location:** `ControllerOverride.AssignToGameObject()`  
**Unity API:** `Animator.runtimeAnimatorController = overrideController`

---

### Files Changed (Phase 3)

| File | Change |
|------|--------|
| `Server/src/services/tools/manage_animation.py` | Add 4 actions to `CONTROLLER_ACTIONS` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add 4 cases to `HandleControllerAction()` |
| `MCPForUnity/Editor/Tools/Animation/ControllerOverride.cs` | **New file.** `Create`, `SetClip`, `GetClips`, `AssignToGameObject` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add aliases: `base_controller_path` → `baseControllerPath`, `original_clip_name` → `originalClipName`, `override_clip_path` → `overrideClipPath` |
| `Server/tests/test_manage_animation.py` | Tests for each new action |
| `TestProjects/UnityMCPTests/Assets/Tests/Animation/` | Unity EditMode tests |

---

## Phase 4 — AvatarMask & Layer Polish

**Goal:** Enable upper-body / lower-body layer setups and IK pass configuration — the two remaining blockers for layered animation rigs.

### New Actions

#### `controller_create_avatar_mask`
Create an `AvatarMask` asset and configure which humanoid body parts (and/or transforms) are included.

**`properties`:**
```json
{
  "mask_path": "Assets/Animations/UpperBodyMask.mask",
  "body_parts": ["LeftArm", "RightArm", "Head"],
  "transform_paths": []
}
```

`body_parts` values map to `AvatarMaskBodyPart` enum: `Root`, `Body`, `Head`, `LeftLeg`, `RightLeg`, `LeftArm`, `RightArm`, `LeftFingers`, `RightFingers`, `LeftFootIK`, `RightFootIK`, `LeftHandIK`, `RightHandIK`.

Returns the created asset path.

**C# location:** New file `ControllerAvatarMask.cs` — `CreateAvatarMask()`  
**Unity API:** `AvatarMask`, `AvatarMask.SetHumanoidBodyPartActive()`, `AssetDatabase.CreateAsset()`

---

#### `controller_assign_avatar_mask`
Assign an existing AvatarMask asset to a specific layer on a controller.

**`controller_path`:** `"Assets/Animators/Player.controller"`  
**`properties`:**
```json
{
  "layer_index": 1,
  "mask_path": "Assets/Animations/UpperBodyMask.mask"
}
```

**C# location:** `ControllerAvatarMask.AssignToLayer()`  
**Unity API:** `AnimatorControllerLayer.avatarMask`

---

#### `controller_set_layer_ik_pass`
Enable or disable the IK pass on a layer. Required for `OnAnimatorIK` callbacks to fire on non-base layers.

**`controller_path`:** `"Assets/Animators/Player.controller"`  
**`properties`:**
```json
{
  "layer_index": 1,
  "ik_pass": true
}
```

**C# location:** `ControllerLayers.SetIKPass()`  
**Unity API:** `AnimatorControllerLayer.iKPass`

---

#### `animator_get_state_info`
Return runtime state information for one or all layers: current state name, normalized time, whether a transition is active, the transition's source and destination states.

**`target`:** `"Hero"`  
**`properties`:**
```json
{
  "layer_index": 0
}
```

Omit `layer_index` (or pass `-1`) to return info for all layers.

**C# location:** `AnimatorRead.GetStateInfo()`  
**Unity API:** `Animator.GetCurrentAnimatorStateInfo()`, `Animator.IsInTransition()`, `Animator.GetNextAnimatorStateInfo()`  
**Play-mode only.** Returns an error in edit mode.

---

### Files Changed (Phase 4)

| File | Change |
|------|--------|
| `Server/src/services/tools/manage_animation.py` | Add `controller_create_avatar_mask`, `controller_assign_avatar_mask`, `controller_set_layer_ik_pass` to `CONTROLLER_ACTIONS`; add `animator_get_state_info` to `ANIMATOR_ACTIONS` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add 3 cases to `HandleControllerAction()`, 1 case to `HandleAnimatorAction()` |
| `MCPForUnity/Editor/Tools/Animation/ControllerAvatarMask.cs` | **New file.** `CreateAvatarMask`, `AssignToLayer` |
| `MCPForUnity/Editor/Tools/Animation/ControllerLayers.cs` | Add `SetIKPass()` |
| `MCPForUnity/Editor/Tools/Animation/AnimatorRead.cs` | Add `GetStateInfo()` |
| `MCPForUnity/Editor/Tools/Animation/ManageAnimation.cs` | Add aliases: `mask_path` → `maskPath`, `body_parts` → `bodyParts`, `transform_paths` → `transformPaths`, `ik_pass` → `ikPass` |
| `Server/tests/test_manage_animation.py` | Tests for each new action |
| `TestProjects/UnityMCPTests/Assets/Tests/Animation/` | Unity EditMode tests (AvatarMask, IK pass); PlayMode test for `animator_get_state_info` |

---

## Out of Scope

The following were considered and explicitly deferred:

- **Timeline / Playables** — `PlayableDirector`, `TimelineAsset`, and `AnimationTrack` management. High value but a separate domain; better as `manage_timeline` to keep tool focus.
- **Animation Rigging package constraints** — `TwoBoneIK`, `MultiRotation`, etc. Depends on an optional package; warrants its own tool group.
- **Humanoid muscle curves** — Writing directly to humanoid muscle channels (vs generic float curves) requires a different `EditorCurveBinding` variant and significant test surface.
- **Sprite animation** — `SpriteRenderer` frame-swap clips are structurally different from transform/property curves. Out of scope for this tool.
- **Import-time clip slicing** — Splitting a source FBX into named clips via `ModelImporter.clipAnimations`. Belongs in a `manage_asset` extension, not animation.

---

## Implementation Order

| Phase | Actions Added | Complexity | Status | Unblocks |
|-------|--------------|------------|--------|---------|
| 1 — State Machine Completeness | 6 | Low | ✅ Done | Iterative controller authoring without asset rebuild |
| 2 — Clip Editing | 4 | Low–Medium | ✅ Done | Non-destructive curve updates; clip variants |
| 3 — Override Controller | 4 | Medium | ✅ Done | Per-character clip swaps; shared base controllers |
| 4 — AvatarMask & IK | 4 | Medium | ✅ Done | Layered rigs; upper-body overlays; IK callbacks |

Phases 1 and 2 have no dependencies and can be worked in parallel. Phase 3 depends only on the existing controller pipeline. Phase 4 depends on `controller_add_layer` (already shipped) and the IK pass/AvatarMask APIs.

---

## Testing Requirements

Every new action must have:

1. **Python unit test** in `Server/tests/test_manage_animation.py` — mock the Unity response, assert the params dict sent over the wire.
2. **Unity EditMode test** in `TestProjects/UnityMCPTests/Assets/Tests/Animation/` — create real assets, invoke the handler, assert asset state via `AssetDatabase`.
3. **PlayMode test** (required only for `animator_get_state_info`) — asset-based test that enters PlayMode, waits a frame, and asserts `AnimatorStateInfo` fields.

All tests must pass via `tools/local_harness.py` before a PR is opened.
