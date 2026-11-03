"""Creates the materials the AI003 world needs.

Run once per project (scripts/setup-ue-assets.cmd), or let the smoke script run
it automatically:

    UnrealEditor-Cmd.exe <project> -run=PythonScript -script=Scripts/setup_assets.py \
        -unattended -nosplash -nullrhi -stdout

Materials live in /Game/AI003:
  M_TerrainVertexColor  vertex coloured lit surface for the terrain chunks
  M_Ocean               translucent lit water
  M_PlayerBody          teal capsule
  M_PlayerAccent        orange facing marker
"""

import unreal

ROOT = "/Game/AI003"


def recreate_material(name):
    path = "{}/{}".format(ROOT, name)
    if unreal.EditorAssetLibrary.does_asset_exist(path):
        unreal.EditorAssetLibrary.delete_asset(path)
    tools = unreal.AssetToolsHelpers.get_asset_tools()
    material = tools.create_asset(name, ROOT, unreal.Material, unreal.MaterialFactoryNew())
    if material is None:
        raise RuntimeError("could not create {}".format(path))
    return material


def scalar(mel, material, value, pos, prop):
    node = mel.create_material_expression(material, unreal.MaterialExpressionConstant, -400, pos)
    node.set_editor_property("r", value)
    mel.connect_material_property(node, "", prop)


def vector(mel, material, color, pos, prop):
    node = mel.create_material_expression(material, unreal.MaterialExpressionConstant3Vector, -400, pos)
    node.set_editor_property("constant", color)
    mel.connect_material_property(node, "", prop)


def save(material, name):
    mel = unreal.MaterialEditingLibrary
    mel.recompile_material(material)
    unreal.EditorAssetLibrary.save_loaded_asset(material)
    unreal.log("[AI003] created {}/{}".format(ROOT, name))


def make_terrain():
    mel = unreal.MaterialEditingLibrary
    material = recreate_material("M_TerrainVertexColor")
    vertex_color = mel.create_material_expression(
        material, unreal.MaterialExpressionVertexColor, -450, 0)
    # In UE 5.8 the VertexColor outputs are unnamed; "" selects the RGB output.
    mel.connect_material_property(vertex_color, "", unreal.MaterialProperty.MP_BASE_COLOR)
    scalar(mel, material, 0.92, 220, unreal.MaterialProperty.MP_ROUGHNESS)
    scalar(mel, material, 0.15, 340, unreal.MaterialProperty.MP_SPECULAR)
    save(material, "M_TerrainVertexColor")


def make_ocean():
    mel = unreal.MaterialEditingLibrary
    material = recreate_material("M_Ocean")
    material.set_editor_property("blend_mode", unreal.BlendMode.BLEND_TRANSLUCENT)
    material.set_editor_property("shading_model", unreal.MaterialShadingModel.MSM_DEFAULT_LIT)
    material.set_editor_property("two_sided", True)
    vector(mel, material, unreal.LinearColor(0.010, 0.055, 0.085, 1.0), 0,
           unreal.MaterialProperty.MP_BASE_COLOR)
    scalar(mel, material, 0.86, 220, unreal.MaterialProperty.MP_OPACITY)
    scalar(mel, material, 0.3, 340, unreal.MaterialProperty.MP_ROUGHNESS)
    save(material, "M_Ocean")


def make_flat(name, color, roughness):
    mel = unreal.MaterialEditingLibrary
    material = recreate_material(name)
    vector(mel, material, color, 0, unreal.MaterialProperty.MP_BASE_COLOR)
    scalar(mel, material, roughness, 220, unreal.MaterialProperty.MP_ROUGHNESS)
    save(material, name)


def main():
    unreal.log("[AI003] creating materials in {}".format(ROOT))
    make_terrain()
    make_ocean()
    make_flat("M_PlayerBody", unreal.LinearColor(0.06, 0.32, 0.36, 1.0), 0.45)
    make_flat("M_PlayerAccent", unreal.LinearColor(0.75, 0.25, 0.05, 1.0), 0.35)
    unreal.log("[AI003] materials ready")


main()
