"""Persist the imported character assets and flag the material for skeletal meshes.

The MCP import saved the mesh but left the generated skeleton package in memory,
which breaks every animation that depends on it at load time.
"""

import unreal

SAVED = unreal.EditorAssetLibrary.save_directory(
    "/Game/Characters/KenneyProtagonists", only_if_is_dirty=False, recursive=True
)
print("saved character directory:", SAVED)

material = unreal.load_asset("/Game/AI003/M_PlayerSkin")
if material is not None:
    for flag in ("used_with_skeletal_mesh", "used_with_static_mesh"):
        try:
            material.set_editor_property(flag, True)
        except Exception as exc:  # noqa: BLE001
            print("flag skipped", flag, exc)
    unreal.MaterialEditingLibrary.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material)
    print("material flags set:", material.get_path_name())
else:
    print("material missing")
