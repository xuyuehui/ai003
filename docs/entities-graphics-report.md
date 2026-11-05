# Entities Graphics 地形复测报告（Phase 2.1）

## 结论

**整张 2 km 世界已通过 SubScene 烘焙 + Entities Graphics 渲染。**

![整张烘焙地形](images/ecs-terrain-full-baked.png)

关键改动：地形材质从自定义顶点色 shader 换成 **URP Lit + 世界顶点色贴图**。
URP Lit 的 DOTS instancing 是官方支持的，256 个 chunk 一起渲染不再出现错位/撕裂；
每顶点颜色烘焙成 513×513 的 `TerrainColor.asset`，chunk 的 UV 本来就是归一化世界坐标，
所以一张纹理覆盖整张地图。

## 复现

1. 打开 `Assets/World/Baked/BakedWorld.unity`，选中 `TerrainSubScene`
   → Inspector **Open** → **Close**（或菜单 `AI003 → Bake Terrain SubScene`）
2. 关闭后 `Assets/SceneDependencyCache/*.sceneWithBuildSettings` 经 `SubSceneImporter` 导入
3. Play：`MaterialMeshInfo` 实体 = **256**；关闭 `[AI003] Terrain` 的 GameObject
   渲染后，整张地形仍由 Entities Graphics 显示

烘焙脚本 `Assets/Editor/BakeTerrainSubScene.cs`：
- 生成 16×16 chunk 内容场景、256 个网格资产、世界颜色贴图与 URP Lit 材质
- 写入 `_SceneGUID`、通过内部 `AssetDatabase.SetImporterOverrideInternal` 选择 `SubSceneImporter`
- 自动 Open/Close 并清理 `SceneDependencyCache` 强制重烘焙

## 限制

- **批处理**：`scripts\bake-unity-terrain.cmd` 用 `RunBatch` 状态机（EditorApplication.update
  里完成 Open/Close + 重新导入），无需人工点 Inspector
- 烘焙产物在 `Assets/SceneDependencyCache`（本机生成缓存，已 gitignore）
- 运行时 ECS 地形**渲染流式**：`TerrainEcsStreaming` 按摄像机距离给 256 个烘焙 chunk 加/去
  `DisableRendering`（900 m 视野；实测 152/256 → 0，见截图）；实体常驻内存，
  真正的内存流式（分块 SubScene/BRG）仍是后续工作
- 骨骼角色仍需 DOTS skinning
## 运行时流式与骨骼/物理补充

- **内存级 tile 流式**：`StreamingWorld.unity` 拆 16 个 512 m tile（独立 SubScene），
  `TerrainTileStreaming` 用 `SceneSystem.LoadSceneAsync/UnloadScene` 按距离加载/卸载；
  实测 14/16 tile、实体 224 → 0 → 240
- **DOTS 骨骼**：`DotsBoneRig` 把 45 根骨骼映射为实体，`BoneAnimationSystem`（Burst）驱动，
  回写 GameObject 骨骼由 SkinnedMeshRenderer 蒙皮（`AI003_DOTS_BONES=1`）
- **ECS 地形碰撞**：`TerrainClampSystem` 用高度图钳制 `TerrainBody` 实体，PlayMode 测试通过
- 烘焙网格资产不入库（约 55 MB），克隆后跑 `scripts/bake-unity-terrain.cmd` 或
  `AI003 → Bake Tiled Streaming Terrain` 生成