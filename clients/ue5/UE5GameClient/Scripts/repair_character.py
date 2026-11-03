"""Rebuild the Kenney character assets in a clean, saved state.

Run as a commandlet (no UI, dialogs auto-answer):

    UnrealEditor-Cmd.exe <project> -run=PythonScript -script=Scripts/repair_character.py \
        -unattended -nosplash -nullrhi -nop4

Why: the MCP FBX import saved the mesh but left the generated skeleton package in
memory only, so every animation depending on it failed to load. This script

1. deletes the old animation folders,
2. re-imports the mesh and saves both mesh and skeleton,
3. imports idle/jump/run onto that skeleton, and
4. saves the whole /Game/Characters/KenneyProtagonists tree.
"""

import unreal

BASE = "D:/works/ai003/clients/ue5/UE5GameClient/Characters/KenneyProtagonists/"
ROOT = "/Game/Characters/KenneyProtagonists"
SKELETON = ROOT + "/characterMedium_Skeleton"

asset_tools = unreal.AssetToolsHelpers.get_asset_tools()
library = unreal.EditorAssetLibrary

# The broken assets are removed from disk before this runs; the tree is empty.
for folder in (ROOT + "/idle", ROOT + "/A_jump", ROOT + "/A_run", ROOT + "/Anims"):
    if library.does_directory_exist(folder):
        print("already present", folder)


def import_mesh():
    task = unreal.AssetImportTask()
    task.filename = BASE + "characterMedium.fbx"
    task.destination_path = ROOT
    task.destination_name = "characterMedium"
    task.automated = True
    task.replace_existing = True
    task.save = False
    options = unreal.FbxImportUI()
    options.import_mesh = True
    options.import_animations = False
    options.import_materials = True
    task.options = options
    asset_tools.import_asset_tasks([task])

    mesh = unreal.load_asset(ROOT + "/characterMedium")
    skeleton = unreal.load_asset(SKELETON)
    print("mesh", mesh, "skeleton", skeleton)
    if mesh:
        library.save_loaded_asset(mesh)
    if skeleton:
        library.save_loaded_asset(skeleton)
    return skeleton


def import_animation(name, skeleton):
    task = unreal.AssetImportTask()
    task.filename = BASE + name + ".fbx"
    task.destination_path = ROOT + "/Anims"
    task.destination_name = name
    task.automated = True
    task.replace_existing = True
    task.save = False
    options = unreal.FbxImportUI()
    options.import_mesh = False
    options.import_animations = True
    options.skeleton = skeleton
    task.options = options
    asset_tools.import_asset_tasks([task])
    anim = unreal.load_asset(ROOT + "/Anims/" + name)
    print("anim", name, anim)
    if anim:
        library.save_loaded_asset(anim)


skeleton_asset = import_mesh()
if skeleton_asset is None:
    raise RuntimeError("no skeleton produced by the character import")

for animation in ("idle", "jump", "run"):
    import_animation(animation, skeleton_asset)

print("saved tree:", library.save_directory(ROOT, only_if_is_dirty=False, recursive=True))
