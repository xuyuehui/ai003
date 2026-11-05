# Entities Graphics 地形复测报告（Phase 2.1）

## 结论

未找到可用路径。**运行时创建**与**SubScene 烘焙**两条路都做了复现，结果如下。

## 路径 A：运行时实体（`TerrainStreaming` + `AI003_TERRAIN_ECS=1`）

- URP Lit 材质 + 运行时实体：✅ 可渲染（白色 chunk）
- 自定义地形 shader + **手动创建**实体：✅ 可渲染（1 / 16 / **256** 个）
  - 证据：[256 个手动实体](images/ecs-terrain-manual-256.png)
- 同一套代码在 `TerrainStreaming` 里创建 256 个实体：❌ 不绘制（灰色）
  - 证据：[灰色地面](images/ecs-terrain-runtime-entities.png)

已排除的差异（均一致）：
- 组件集合：`LocalTransform`/`LocalToWorld`/`MaterialMeshInfo`/`RenderBounds`/
  `WorldRenderBounds`/`RenderMeshArray`/`RenderFilterSettings`/`EntitiesGraphicsChunkInfo`
- 材质：`AI003/TerrainVertexColor`，`enableInstancing=true`，shader 存在
- 网格：`BuildChunkMesh` 输出，非空、顶点数正确
- 包围盒：`WorldRenderBounds` 有效（非零、覆盖 chunk）
- 创建时机：`Start`、延迟 0.75 s、延迟 3 s、`WaitForEndOfFrame`、`DisableRendering` 重注册——均无效

## 路径 B：SubScene 烘焙（`Assets/Editor/BakeTerrainSubScene.cs`）

1. 生成 2×2 chunk 内容场景 + 材质/网格资产 + SubScene 引用场景：✅
2. 显式写入 `_SceneGUID`（批处理下 `OnValidate` 不生效）：✅（`BakedWorld.unity` 中可见）
3. 强制 `SubSceneImporter`：
   - `AssetImporter.SetImporterOverride` 不存在
   - 反射找到内部 `AssetDatabase.SetImporterOverrideInternal(path, type)` 并调用成功
   - `.meta` 写入 `importerOverride`（`scriptedImporterType.Hash`），但重新导入时
     仍显示 `with importer (DefaultImporter)`，**没有烘焙产物**（`Library` 下无 entity scene）
4. 复现命令：

```powershell
Unity.exe -batchmode -projectPath clients\unity\UnityGameClient `
  -executeMethod AI003.EditorTools.BakeTerrainSubScene.Run -logFile bake.log -quit
```

## 建议

- 交互式编辑器里用菜单打开/关闭 SubScene 触发一次烘焙，确认是否为批处理限制
- 或改 `BatchRendererGroup` 直绘（需要 shader 支持 `unity_DOTSInstanceData`）
- 运行时路径的差异集中在 `TerrainStreaming` 的创建上下文，下一步可对比
  `EntitiesGraphicsSystem` 的 `EntitiesGraphicsChunkInfo` 批次 ID 是否为空
