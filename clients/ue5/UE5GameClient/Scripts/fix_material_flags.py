"""Flag the player material for skeletal meshes so cooked/game runs use it.

Without used_with_skeletal_mesh the engine falls back to the default material
outside the editor (LogMaterial: "missing usage flag SkeletalMesh").
"""

import unreal

material = unreal.load_asset("/Game/AI003/M_PlayerSkin")
if material is None:
    raise RuntimeError("M_PlayerSkin not found")

material.set_editor_property("used_with_skeletal_mesh", True)
unreal.MaterialEditingLibrary.recompile_material(material)
unreal.EditorAssetLibrary.save_loaded_asset(material)
print("used_with_skeletal_mesh =", material.get_editor_property("used_with_skeletal_mesh"))
