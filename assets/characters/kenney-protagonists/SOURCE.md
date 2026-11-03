# Kenney Animated Characters: Protagonists (1.1)

- Source: https://kenney.nl/assets/animated-characters-protagonists
- Author: Kenney (www.kenney.nl)
- License: **CC0 1.0** (public domain) - see [License.txt](License.txt)
- Files used: `characterMedium.fbx` + `idle.fbx` / `jump.fbx` / `run.fbx`
  + skin `skaterMaleA.png`

## Why this pack

- Properly **skinned** humanoid (FBX `Skin`/`Deformer` clusters, Hips/Spine/LeftArm...),
  so Unreal imports it as a real SkeletalMesh + Skeleton and Unity as a rigged model.
- Animations are separate FBX takes (idle / jump / run), which both engines can bind
  to the same skeleton - Unreal through `skeletonPath`, Unity through a shared avatar.
- Includes a **jump** clip, unlike the blocky pack.
- CC0: safe to commit and redistribute.

## Bindings

| Engine | Model | Clips | Skin |
| --- | --- | --- | --- |
| Unity | `characterMedium.fbx` (Generic rig + Avatar) | `Idle` / `Run` / `Jump` (sanitized) | `M_Player` (URP/Lit + `skaterMaleA`) |
| Unreal | `/Game/Characters/KenneyProtagonists/characterMedium` | `/Game/Characters/KenneyProtagonists/Anims/{idle,jump,run}` | `/Game/AI003/M_PlayerSkin` |

Unity notes:

- Imported into `Assets/Resources/Characters/KenneyProtagonists/` so `WorldBootstrap` can
  `Resources.Load` the prefab at runtime.
- `Assets/Editor/SetupPlayerCharacter.cs` builds `Player.prefab`, `PlayerAnimator.controller`
  and the sanitized `Idle/Run/Jump.anim` clips. Apply it whenever the FBX import changes.
- The FBX takes animate the **root transform** (100x scale, Z-up rotation) and their bone
  paths omit the extra `Root` object. The setup script strips root curves and re-paths bones,
  otherwise the character stays in T-pose at 100x size.

Unreal notes:

- Import the mesh through MCP `asset.import`, then **save the skeleton asset** right away:
  the importer persists the mesh but leaves the generated skeleton in memory, and every
  animation that depends on it then fails to load.
- Animation FBX files import onto that skeleton via `skeletonPath`; the Interchange
  pipeline ignores the Python `FbxImportUI` options for pure-animation FBX, so use the
  MCP import path (or `Scripts/repair_character.py` for a clean rebuild).

## Copies

| Location | Purpose |
| --- | --- |
| `assets/characters/kenney-protagonists/` | canonical source + license |
| `clients/unity/UnityGameClient/Assets/Resources/Characters/KenneyProtagonists/` | Unity import |
| `clients/ue5/UE5GameClient/Characters/KenneyProtagonists/` | Unreal FBX source |
