# Unity 开放大世界地图 · 制作管线

> 回答一个问题：**2 km 的演示岛，怎么变成 8 km、能流式加载、还能持续往里加内容的开放大世界？**
> 本文只讲管线：数据怎么来、地图怎么组织、场景怎么组装、运行期怎么流式、怎么验收。
> 逐步骤的实施手册（31 步、每步带验收与坑）见 [unity-open-world-steps.md](unity-open-world-steps.md)。
> 画面质量见 [../pipeline-visual-fidelity.md](../pipeline-visual-fidelity.md)，
> 区域气质见 [../worldview/03-art-direction.md](../worldview/03-art-direction.md)。

约束（本机实测，见 [../environment.md](../environment.md)）：
Unity 6000.6.6f1 + URP 17.6，Windows x64，i7-6700K（4 核 8 线程）/ 32 GB / RTX 5060 8 GB，
目标 **1080p 60 fps**。

## 0. 结论

1. **地形不换载体**：继续用自建高度场 + 自建 shader，但把"一次性建全图"换成
   **四叉树 clipmap**（128 m 叶子节点，按距离选层，裙边缝合）。世界数据仍是唯一真源，
   程序化、可复现、顶点色/沙纹 shader 全部保留。
2. **数据从"一张全图"改成"瓦片 + manifest"**：8 km / 1 m 采样，128 m 瓦片 × 4096 块，
   高度用 R16（量化 1.13 cm）。噪声频率改成**世界空间（米）**——否则 `-size 8192`
   只是把同一座岛拉伸 4 倍。
3. **制作流程固化成五段**：`数据 → 地图内容 → 组装 → 运行期流式 → 验证`，
   每段一条命令、一个产物、一个门禁。

| # | 决策 | 选择 | 放弃的方案 | 理由 |
| --- | --- | --- | --- | --- |
| D1 | 地形载体 | 自建高度场 + 四叉树 clipmap | Unity Terrain；第三方流式插件 | 数据真源、程序化、可测；不引第三方 |
| D2 | 世界尺度 | 8 km × 8 km（64 km²），1 m 采样 | 16 km；保持 2 km | 16 倍面积，单人仍可维护；16 km 需要原点重定基 |
| D3 | 流式实现 | 运行时代码流式（StreamingAssets + LRU + 线程建网格） | Addressables / AssetBundle | 地形是"数据"不是"资产"，零包体管理与依赖 |
| D4 | 作者内容 | POI 参数 + 确定性散布 + 少量 prefab | 手摆数万物件；4096 个 tile 场景 | 与"程序化基底"一致，避免场景爆炸 |
| D5 | 服务器边界 | 从 manifest 读，不再写死 | 改常量 | 现在写死 ±1024 m（`gateway/state.go:14`），8 km 走不出去 |

## 1. 现状盘点

| 维度 | 现状（代码实测） | 直接放大到 8 km 会怎样 |
| --- | --- | --- |
| 世界尺寸 | 2048 m（4 km²），`world/world.json` | 面积 ×16 |
| 采样 | 1025²，2 m/顶点 | 1 m 采样需要 8193²，R16 即 134 MB |
| 网格 | 64 块 × 129²；**1,065,024 顶点 / 2,097,152 三角形** | 顶点 ×16，单场景装不下 |
| 碰撞 | 64 个 `MeshCollider`，等于 2.1M 碰撞三角形 | 碰撞三角形 ×16，物理 build 时间线性上涨 |
| 烘焙 | `Assets/Generated/Terrain` **97.4 MB**，全部烘进 `OpenWorld.unity` | 资产 1.5 GB 级，换机器要重烘一遍（分钟级、且不能并行） |
| LOD / 流式 | 无。只有视锥剔除；地形也不投影（`shadowCastingMode = Off`） | 帧时间随世界面积线性上涨 |
| 数据生成 | 噪声按 uv 归一化（`worldgen/world.go:135-138`、`180-206`） | `-size 8192` 只把岛拉伸 4 倍，地貌全变 |
| 摄像机 | far clip 4000 m（`WorldBootstrap.cs:253`） | 8 km 世界需要 5 km 视距 + 地平线处理 |
| 服务器 | 位置夹到 ±1024 m（`gateway/state.go:14`） | 玩家走不出 1 km 外 |
| 现有 8 km 路径 | `scripts/build-world.ps1 -Size 8192` 可跑，默认 4 m/顶点、2049² | 能出图，但是"放大版"，不是开放世界 |

**结论：这不是把 `-Size` 调大，是结构要换。**

### 1.1 之前试过一版，为什么这次不一样

`3911a8f` 做过 512 m 瓦片 + SubScene + Entities `SceneSystem` 的流式
（`TerrainTileStreaming.cs`：视距 1.2 km、卸载 1.6 km），随后在 `ee993cc`
里连同 DOTS 一起整体回退。那次的问题不是"流式"方向错，是**瓦片被做成了资产**：

| 上一次 | 这一次 |
| --- | --- |
| 每块瓦片是一个烘好的 SubScene + 网格资产（换世界要重烘几十个场景） | 瓦片只是 R16 高度数据，网格运行时构建（换世界零场景改动） |
| 依赖 Entities / SceneSystem / ECS 碰撞 | 纯 MonoBehaviour + `MeshCollider`，与现有运行时同栈 |
| 512 m 定长、只有"载入/卸载"两态 | 四叉树 128 m–4096 m 六层，连续过渡 |
| 视距 1.2 km | 视距 4.2 km |
| 场景数随世界面积线性增长 | 场景数恒为 1 |

一句话：**把资产问题变成数据问题**。这也是 D1/D3 的依据。

## 2. 目标与预算

| 项 | 目标 | 说明 |
| --- | --- | --- |
| 世界 | 8 km × 8 km，64 km² | 1 m 采样，海平面 0，高程 −120 .. +620 m |
| 瓦片 | 128 m，64 × 64 = 4096 块 | 129² 采样（含 1 圈 halo） |
| 视距 | 4.2 km（far clip 5000） | 地平线环 + 雾收边 |
| 旅行 | 步行 6 m/s 横穿 22 min；帆撬 11 m/s 横穿 12 min | 设计目标 ≤ 15 min |
| 地形三角形 | ≤ 1.5 M（常态 ~1.2 M） | 现在 2.1 M，但覆盖面积只有 1/16 |
| Draw call | ≤ 900（地形 ≤ 64） | 道具用 GPU instancing |
| 主线程 | ≤ 10 ms/帧；GPU ≤ 14 ms（1080p） | 60 fps 留余量 |
| 流式 | ≤ 2 ms/帧，单帧最多建 2 块 | 后台线程建网格，主线程只 Apply |
| 内存 | 进程峰值 ≤ 3.5 GB；VRAM ≤ 5 GB | 8 GB 卡要留系统与编辑器占用 |
| 进图 | 场景加载到可操作 ≤ 6 s | 预热的 3×3 用同步阻塞，其余异步 |
| 数据生成 | ≤ 60 s（不含侵蚀）；含侵蚀 ≤ 180 s | 67 M 采样点，Go 并行；侵蚀串行 |
| 单块构建 | p95 ≤ 1.5 ms | 16.6k 顶点纯计算 |

所有距离阈值都是**起点值**，M1 用 `Bench Flythrough` 校准（判据：屏幕上地形误差 ≤ 1 px）。

## 3. 管线总览

```
seed + 频段表   ─▶ worldgen v2       ─▶ world/open/manifest.json + 4096 瓦片     [1 数据]
map/*.json      ─▶ Validate Map      ─▶ map-report.json                          [2 内容]
瓦片 + map      ─▶ TerrainClipmap    ─▶ 运行时网格池（不落盘）                    [3 组装]
玩家位置        ─▶ WorldStreamer     ─▶ 每帧 ≤ 2 ms 的加载/卸载                   [4 运行]
全部产物        ─▶ open-world-check  ─▶ artifacts/open-world/*                    [5 验证]
```

| 阶段 | 输入 | 工具 / 入口 | 产物 | 门禁 |
| --- | --- | --- | --- | --- |
| 1 数据 | seed + 频段表 | `scripts\open-world.ps1` → `worldgen` | `world/open/**` | `world-check`：哈希、接缝、范围 |
| 2 内容 | 设计稿 | `map/*.json` + `AI003/Open World/Validate Map` | `map-report.json` | 间距、坡度、覆盖、视线 |
| 3 组装 | 瓦片 + map | `TerrainClipmap`、`ScatterRenderer` | 运行时网格池 | 构建即验证（无落盘产物） |
| 4 运行 | 场景 | `WorldStreamer` | 帧时间 | `Bench Flythrough` 对照基线 |
| 5 验证 | 上面全部 | `scripts\open-world-check.cmd` | 报告 + 截图 | 全绿才算 M 完成 |

与现有管线的边界：`legacy` preset（2 km、全量烘焙）**保留不动**，
`smoke-all`、`demo-compare`、`demo-room` 继续跑旧数据；新管线用 `open` preset，互不干扰。

## 4. 数据层：worldgen v2

### 4.1 为什么必须改噪声

现在 `u = x / sizeMeters`，噪声频率跟着世界尺寸走：

| 频段 | 现在（uv 系数） | 2 km 时的周期 | 8 km 时会变成 | v2（按米定义） |
| --- | --- | --- | --- | --- |
| 大陆 | 2.1 | 950 m | 3.8 km | 4096 m |
| 山脉 | 5.4 | 380 m | 1.5 km | 1024 m |
| 细节 | 9.3 | 220 m | 870 m | 128 m |
| 湿度 | 3.1 | 660 m | 2.6 km | 2048 m |
| 温度 | `1 - abs(v)*1.6` | 全图南北梯度 | 全图南北梯度 | 区域图（regions） |

同时 `terrainHeight` 用 `d = hypot(u*1.02, v)` 做中心衰减（岛形），放大后"整张图还是一个岛"。
v2 把这两件事都换掉：频段按米，衰减按 `regions.json` 的陆海遮罩。

**兼容策略**：新代码只走 `-preset open`；旧的生成函数保留为 `-preset legacy`，
输出仍写 `world/`，保证旧截图与 `demo-compare` 基线不破。

### 4.2 分块与坐标

```
tileOrigin(tx, tz) = worldMin + (tx, tz) * 128 m,   worldMin = (-4096, -4096)
sample(i, j)       = tileOrigin + (i, j) * 1 m,     i, j ∈ [0, 128]
tile(tx, tz)       覆盖 [tx*128, (tx+1)*128) 米，tx, tz ∈ [0, 64)
```

- 每块 **129² 采样**（128 个格子 + 共享边界），相邻块边界采样**同源同值**。
- 法线、坡度、群系混合需要邻居，因此每块多读 1 圈 halo（实际读 131²，写 129²）。
- 世界坐标原点在中心，Y 向上，米制，行优先——与现有 `world.json` 约定一致。
- 8 km 内 float32 精度足够（8192 m 处最小间隔约 1 mm），**不做原点重定基**；
  将来上 16 km 再引入。

### 4.3 瓦片与 manifest

```
world/open/
  manifest.json              世界参数 + 层描述 + 每层 sha256
  height/h_00_00.r16 …       129² uint16，量化为 [minHeight, maxHeight]
  biome/b_00_00.r8 …          65² uint8（2 m 群系，够用且省 3/4 空间）
map/
  regions.json               区域多边形 + 生成参数
  poi.json                   地标/兴趣点
  routes.json                商路/气流线（spline）
```

```json
{
  "version": 2,
  "world": { "seed": 20261111, "sizeMeters": 8192, "sampleMeters": 1.0, "seaLevel": 0,
             "minHeight": -120, "maxHeight": 620,
             "spawn": { "x": -2048, "y": 52.3, "z": -3072 } },
  "tiling": { "tileMeters": 128, "tilesPerAxis": 64, "halo": 1 },
  "layers": {
    "height": { "format": "r16", "path": "height/h_{tx:00}_{tz:00}.r16", "tiles": 4096 },
    "biome":  { "format": "r8",  "path": "biome/b_{tx:00}_{tz:00}.r8", "sampleMeters": 2.0, "tiles": 4096 }
  },
  "map": { "regions": "regions.json", "poi": "poi.json", "routes": "routes.json" },
  "sha256": { "height": "…", "biome": "…" }
}
```

体积：高度 4096 × 33,282 B = **136 MB**；群系 4096 × 4,225 B = **17 MB**。
两者都是**生成物、不入库**；入库的只有 `manifest.json` 与 `map/`，靠 seed + 频段表 + sha256 复现。
需要新增两条忽略规则（旧的 `world/`、`StreamingAssets/world/` 继续提交，不动）：

```
/world/open/
clients/unity/UnityGameClient/Assets/StreamingAssets/open/
```

这是对"`world/` 必须入库"的一次有意例外：8 km 数据 150 MB 级，且由一条命令在 60 s 内
确定性重建；入库反而让 clone 与 CI 变慢。

`manifest.json` 里的 `spawn` 由生成器搜索（干燥、平坦、离海 ≥ 300 m），
服务器边界也由它推导（见 §7.5），不再有第二处真源。

### 4.4 生成命令

```powershell
.\scripts\open-world.ps1                     # preset=open，生成 + 拷贝到 StreamingAssets
.\scripts\open-world.ps1 -Preset legacy      # 旧的 2 km 全图，走老路径
.\scripts\open-world.ps1 -Check              # 只校验：哈希、块数、边界、出生点
```

- Go 侧 4 线程按瓦片并行；单块独立采样（不依赖邻居状态），因此可任意顺序、可断点续跑。
- 目标 ≤ 60 s（不含侵蚀；按 [terrain-production-pipeline.md](terrain-production-pipeline.md) 加侵蚀后 ≤ 180 s）；实测后把数字回填到本节。
- 生成完打印每个层的 sha256，与 `manifest.json` 对不上直接非 0 退出。

### 4.5 跨端关系

按 2026-11-11 的决定：**世界数据不再与 UE 共享**，UE 继续读自己的 `WorldData/`（2 km）。
本次只同步两件事到服务器：位置钳制范围、`world.tide` 等事件的世界尺度。

## 5. 地图内容层

> 地形"内容"的完整制作流水线（骨架 / 侵蚀 / 刻蚀 / 表面 / AO / 布景，含业界参考）
> 见 [terrain-production-pipeline.md](terrain-production-pipeline.md)；本节只保留与整体规划相关的最小信息。

### 5.1 区域

| 区域 | 位置 | 面积 | 高程 | 主材质 | L1 地标 |
| --- | --- | --- | --- | --- | --- |
| 长眠海（沙丘场） | 中央偏西 | 20 km² | −20 .. 180 m | 沙（三尺度沙纹） | 3 |
| 白喉盐海（盐湖） | 东南 | 8 km² | −5 .. 20 m | 盐（龟裂 + 镜面） | 1 |
| 骨脊（玄武岩） | 北 | 12 km² | 80 .. 420 m | 岩（柱状节理） | 2 |
| 霜冠（雪峰） | 西北 | 6 km² | 300 .. 620 m | 雪（高光压缩） | 1 |
| 第一井盆地（出生） | 西南 | 6 km² | 40 .. 90 m | 沙 + 混凝土 | 1 |
| 外围海岸 / 盐海 | 边缘 | 12 km² | −120 .. 0 m | 海床 | — |

`regions.json` 用多边形 + 高度偏置 + 材质权重描述，`worldgen` 采样时按多边形混合，
避免"区域边界一条直线"。

### 5.2 POI 分层

| 层级 | 数量 | 间距 | 内容 | 加载方式 |
| --- | --- | --- | --- | --- |
| L1 地标 | 8 | 2–3 km，互为视线参照 | 井塔、骨拱、盐晶塔 | prefab，常驻（< 20 个） |
| L2 兴趣点 | 64（1 / km²） | 400–1500 m | 营地、残骸、渊盐点、观景点 | prefab，按距离加载 |
| L3 微场景 | 256（4 / km²） | 80–400 m | 石块、车辙、骨堆、脚印 | 确定性散布，无 prefab |

设计规则：帆撬 11 m/s 时，**相邻 L2 的旅行时间约 90 s**；任意 L2 在 1.2 km 内
至少能看到 1 个 L1（否则加一个"视线锚"）。

### 5.3 路线

`routes.json` 是一组 spline（商路、干涸河床、沙暴走廊）。三处用途：
散布时沿路线降密度、生成车辙；导航采样；地标布点时保证"路线经过"。

### 5.4 地形补丁（POI 改造地形）

POI 自带 `patch { radius, falloff, height, flatten }`，在**建网格时**按距离叠加到高度采样。
好处：不生成第二份高度图，没有合并冲突，改一个 POI 参数就重算它周围 3 块瓦片。
需要真正手 sculpt 时再引入 `terrain deltas`（每瓦片 R16），属于 Phase C 可选项。

### 5.5 内容校验

| 检查 | 规则 | 失败处理 |
| --- | --- | --- |
| 落地 | 不在水下、坡度 ≤ 20° | 吸附到最近合法点 + 告警 |
| 间距 | 相邻 L2 ∈ [400, 1500] m | 报错 |
| 覆盖 | 每个区域 ≥ 1 个 L1 | 报错 |
| 出生点 | 300 m 内坡度 ≤ 8°，无遮挡 | 报错 |
| 视线 | 任意 L2 在 1.2 km 内可见 ≥ 1 个 L1 | 告警 |
| 密度 | L3 每 km² 4 ± 30% | 告警 |

工具输出 `artifacts/open-world/map-report.json`，`open-world-check` 读它决定退出码。

## 6. 场景组装：四叉树 clipmap

### 6.1 选层

最细的叶子是 128 m 瓦片，父节点边长翻倍、采样间距翻倍（顶点数恒为 129²，三角形恒为 32,768）：

| 层 | 节点边长 | 采样间距 | 顶点 | 三角形 | 作为叶子的距离 | 折叠回父节点 |
| --- | --- | --- | --- | --- | --- | --- |
| L0 | 128 m | 1 m | 129² | 32,768 | 0 – 192 m | > 256 m |
| L1 | 256 m | 2 m | 129² | 32,768 | 192 – 448 m | > 512 m |
| L2 | 512 m | 4 m | 129² | 32,768 | 448 – 960 m | > 1024 m |
| L3 | 1024 m | 8 m | 129² | 32,768 | 960 – 1920 m | > 2048 m |
| L4 | 2048 m | 16 m | 129² | 32,768 | 1920 – 3840 m | > 4096 m |
| L5 | 4096 m | 32 m | 129² | 32,768 | 3840 – 5000 m | — |

表里的三角形是地形主体；裙边另加 4 边 × 128 段 × 2 = **1,024 个**，单节点合计 33,792。

**只有叶子节点渲染**：一个节点要么自己画，要么拆成 4 个子节点画，父子永不重叠——
所以不会出现两层地形同屏 z-fighting。"折叠"列是滞后阈值：子节点保留到距离超过该值才并回父节点，
避免相机在阈值附近抖动时反复重建。

同时可见约 37 个节点 → **~1.2 M 三角形、~40 个 draw call**，全部来自同一材质。
选层规则：距离表优先，屏幕误差修正（节点包围盒投影高度 > 2 px 就再拆一层）。

### 6.2 裙边与接缝

- 每个节点四边各挂一圈**裙边**，向下 12 m（大于最大层间高度差），顶点色取边界顶点。
- 不同层相接处靠裙边遮缝，**不做 index 缝合**——实现简单、可自动化验证。
- 验收：`SeamTest` 在 64 条随机瓦片边界上向下 raycast，必须命中，且与 `HeightAt`
  差值 ≤ 0.1 m。

### 6.3 网格构建

- 后台线程算顶点/法线/顶点色（纯托管数组），主线程用 `Mesh.SetVertexBufferData`
  一次性上传；池化 128 个 `Mesh`，不每帧 new。
- 顶点结构 **28 B**（位置 12 + 法线 12 + 顶点色 4）；地形 shader 只用 `POSITION/NORMAL/COLOR`
  （`AshkarTerrain.shader:65-67`），不需要 UV。单块 16,641 × 28 B ≈ 466 KB，128 块池 ≈ 60 MB。
- 采样从 `HeightTileCache` 取（R16 已在内存），halo 直接从邻居瓦片读，不重复解压。

### 6.4 材质与顶点色

- 保留 `AI003/AshkarTerrain` 与顶点色路径：颜色由 biome + 高度 + 坡度 + 曲率在**建块时**
  算好，写入顶点色，shader 不变。
- 材质分层（[fidelity L3](../pipeline-visual-fidelity.md)）第一步放在 shader 里做：
  沙/盐/岩三套参数按坡度/高度混合，顶点色只保留大色块与 AO。
- 同一材质、同一 shader 变体 → 地形天然合批；不要给不同区域建不同材质。

### 6.5 散布与实例化

- L3 微场景不进 `map/poi.json`，由 `hash(tileSeed, index)` 确定性生成：
  规则（坡度/高度/群系/离路线距离）过滤，输出实例矩阵，走 `Graphics.DrawMeshInstanced`。
- 每块上限：岩石 24、枯木 8、残骸 4；> 600 m 不生成，靠地形色块与雾。
- L1/L2 prefab 按距离加载，卸载即 `Destroy`；不用对象池（数量少，避免状态残留）。

### 6.6 水面与地平线

- 水面沿用单平面（`WorldTerrain.BuildWater`），但改为**跟随相机 XZ、按 128 m 吸附**，
  边长 8× 世界尺寸；否则 8 km 世界里玩家会走出水面。
- 地平线：L5 + 线性雾（far 5000）+ 天空盒；不做真体积雾（URP 17 没有）。

## 7. 运行期

### 7.1 状态机

```
Idle → Requested → HeightLoaded → MeshBuilt → ColliderReady → Active
                                                               │
                                            (离开半径 + 滞后) → Cooldown → Unloaded
```

- 请求队列按"距离 + 方向"排序（相机前方优先），每帧预算 2 ms。
- 卸载只回收 `Mesh` 与 `MeshCollider`，高度瓦片留在 LRU 里（再进来不用读盘）。

### 7.2 缓存与预算

| 缓存 | 容量 | 内存 | 淘汰 |
| --- | --- | --- | --- |
| 高度瓦片 LRU | 128 块 | 4.1 MB | 最久未用 |
| 网格池 | 128 个 | 60 MB | 与瓦片同生命周期 |
| 碰撞网格 | 16 个 | ~5 MB | 只留 L0/L1 |

### 7.3 碰撞

- 只有 L0/L1 节点建 `MeshCollider`：脚下块 1 m，邻块 2 m（65²）。
- 每帧最多 cook 1 块，预算 4 ms；cook 完成前角色用 `HeightAt` 兜底（不会掉出世界）。
- 远景不建碰撞：玩家身边 448 m 内一定有 L0/L1，更远处没有碰撞需求；物理查询走高度采样。

### 7.4 首帧与传送

- 进场景时同步预热：出生瓦片 3×3 的 L0 + 外圈 L1，**≤ 1.5 s**，期间用黑场。
- 预热完成再 `SpawnPlayer`，避免"先落地再长出地形"。
- 调试传送（`DebugTeleport(x,z)`）后强制重建 3×3，PlayMode 测试用它验证流式。

### 7.5 与玩家、网络的关系

- `WorldData.HeightAt` 改为走 `HeightTileCache`：命中返回，未命中同步读盘并缓存
  （只在物理兜底与服务器校验时用，不在每帧路径上）。
- 服务器：`worldHalfExtentMeters` 从 `manifest.json` 推导（4096），
  垂直范围从 `minHeight/maxHeight` 推导——**一处真源**。
- `WorldSyncClient` 的 `move` 上报不变；只是坐标范围变大。

## 8. 烘焙（Phase C，可选）

只有当"手摆的建筑/光照"多到需要时才做，默认不做：

| 内容 | 做法 | 增量条件 |
| --- | --- | --- |
| L1 地标光照 | 每个 POI 一个 tile 场景，单独 lightmap | 场景哈希变化 |
| 导航 | 512 m 一块 NavMesh，跨块 off-mesh link | 地形或道具变化 |
| 静态合批 | 按 POI 合批，不合全图 | — |

产物进 `Assets/World/Baked/`，不入库（与现有 `Generated/` 一致），
由 `AI003/Open World/Bake Content` 一条菜单重烘。

## 9. 工具链

| 命令 / 菜单 | 作用 | 产物 | 幂等 |
| --- | --- | --- | --- |
| `scripts\open-world.ps1` | 生成 8 km 数据并拷到 StreamingAssets | `world/open/**` | 是（同 seed 同字节） |
| `scripts\open-world.ps1 -Check` | 哈希/块数/边界/出生点 | 控制台 + 退出码 | 只读 |
| `AI003/Open World/Validate Map` | 校验 POI/区域/路线规则 | `artifacts/open-world/map-report.json` | 只读 |
| `AI003/Open World/Render Stills` | 5 个固定机位出图 | `artifacts/open-world/*.png` | 是 |
| `AI003/Open World/Bench Flythrough` | 固定航线 60 s，采帧时间/三角形/draw call | `artifacts/open-world/bench.json` | 是 |
| `AI003/Open World/Rebuild Cache` | 清空网格池与瓦片 LRU | — | 是 |
| `scripts\open-world-check.cmd` | 串起上面全部，对照基线 | 报告 | 是 |

批处理入口用现有约定：`Unity.exe -batchmode -executeMethod …`，靠日志里的
`[AI003][OPENWORLD] PASS/FAIL` 判定（Unity 是 GUI 子系统，退出码不可用）。

## 10. 验证与门禁

| 门禁 | 检查 | 阈值 |
| --- | --- | --- |
| G1 数据 | 4096 块齐全、sha256 一致、相邻边界高度差 = 0 | 任一不满足即失败 |
| G2 内容 | `map-report.json` 无 error | 0 error |
| G3 帧 | `bench.json` 的 p95 帧时 / 三角形 / draw call | ≤ 16.7 ms / ≤ 1.5 M / ≤ 900 |
| G4 接缝 | `SeamTest` 64 条边界 | 命中率 100%，误差 ≤ 0.1 m |
| G5 视觉 | 5 机位与基线平均差 | ≤ 8/255（与 `demo-compare` 同口径） |
| G6 内存 | 峰值工作集 / VRAM | ≤ 3.5 GB / ≤ 5 GB |

PlayMode 测试（`Assets/Tests/PlayMode/`）：

1. `OpenWorldStreamingTests`：传送到 2 km 外，3 s 内脚下有 L0、不坠落、块数在预算内。
2. `OpenWorldSeamTests`：随机边界 raycast，验证无缝隙。
3. `OpenWorldBudgetTests`：5 个机位下三角形与 draw call 不超预算。
4. `AI003/Open World/Validate Map`（编辑器工具）：POI 间距、坡度、区域覆盖。

CI（GitHub Actions 无 GPU）：只跑 G1/G2（Go 数据校验 + worldcheck），不跑 Unity；
G3–G6 在本地 `open-world-check` 跑，基线 JSON 入库，改动后手动更新。

## 11. 里程碑

| 里程碑 | 工期 | 交付 | 验收 |
| --- | --- | --- | --- |
| **M0 数据与坐标** | 3 天 | worldgen v2（分块、世界空间噪声、并行、manifest）、`open-world.ps1`、校验 | 4096 块 ≤ 60 s；边界零差；`legacy` 回归全绿 |
| **M1 地形渲染** | 5 天 | `TerrainClipmap`、裙边、网格池、5 机位 | 8 km 全景 60 fps；三角形 ≤ 1.5 M；接缝截图无裂缝 |
| **M2 流式与物理** | 5 天 | `WorldStreamer`、`HeightTileCache`、碰撞、水面跟随、传送 | 传送 3 s 内可走；流式 ≤ 2 ms/帧；无 > 50 ms 卡顿 |
| **M3 内容与材质** | 8 天 | `regions/poi/routes`、散布、材质分层 v1、8 个 L1 | 校验 0 error；任意 L2 90 s 可达；近景 3 层材质 |
| **M4 门禁与文档** | 4 天 | bench 基线、截图回归、CI、规格同步 | `open-world-check` 全绿；`smoke-all` 不回归 |

合计约 **4–5 周**（单人，含调试）。顺序不能颠倒：M0 的数学生成错了，后面全部返工。
每一步的输入、改动文件、验收命令与常见坑见 [unity-open-world-steps.md](unity-open-world-steps.md)。
若按 [terrain-production-pipeline.md](terrain-production-pipeline.md) 补齐骨架/侵蚀/刻蚀/表面/AO，
工期约 **7.5 周**（+12 天），数据生成预算相应放宽到 180 s。

## 12. 风险与回退

| 风险 | 影响 | 处理 |
| --- | --- | --- |
| 接缝裂缝 | 画面穿帮 | 裙边 + `SeamTest` 自动拦截；必要时上 index 缝合 |
| 4 核 CPU 建网格卡顿 | 掉帧 | 后台线程 + 每帧 2 块 + 池化；超预算先降 L0 半径 |
| 内存峰值 | OOM | LRU 上限 + G6 门禁；先砍网格池到 96 |
| 噪声一改全图变 | 返工 | `legacy` preset 保留；新数据独立目录，不覆盖旧图 |
| 服务器边界不同步 | 走不出 1 km | M2 内先改 `state.go` 读 manifest；PlayMode 加越界测试 |
| 烘焙/生成时间超预算 | 迭代变慢 | 按瓦片增量 + hash；生成只重算变化的块 |
| 视觉退化 | 评审不过 | 固定机位截图回归 + fidelity 清单 |
| 8 km 精度 | 抖动 | 目前够用；上 16 km 时整体做原点重定基 |

## 13. 不做的事

- **不换 Unity Terrain**：会丢掉顶点色、双日光与确定性数据链，收益不抵重写成本。
- **不再走 SubScene / Entities 流式**：`3911a8f` → `ee993cc` 已经证明，瓦片一旦变成场景资产，
  换世界就是重烘几十个场景；这条路线不再尝试。
- **不引第三方流式/植被插件**：与"可复现、无黑盒"的仓库原则冲突。
- **不做 4096 个 tile 场景**：内容量撑不起，场景管理成本反而更高。
- **不先做远景 HLOD/impostor**：L5 + 雾已经够用，先解决近景与流式。
- **不先做植被系统**：沙海没有树；先把地形、POI、散布跑通。

## 14. 改动清单

| 位置 | 改动 |
| --- | --- |
| `server/cmd/worldgen/main.go`、`server/internal/worldgen/` | v2：preset、分块导出、世界空间噪声、并行、sha256 |
| `server/internal/gateway/state.go` | 位置钳制从 manifest 读，删掉 `worldHalfExtentMeters` 常量 |
| `scripts/build-world.ps1`、`scripts/generate-world.ps1` | 保留旧路径；新增 `scripts/open-world.ps1` |
| `.gitignore`、`clients/unity/UnityGameClient/.gitignore` | 忽略 `world/open/` 与 `StreamingAssets/open/` |
| `Assets/Scripts/World/WorldData.cs` | 支持分块读取与 `HeightAt` 走缓存，保留旧全图加载 |
| `Assets/Scripts/World/WorldBootstrap.cs` | 按 preset 选择 legacy / open 两套构建 |
| `Assets/Scripts/World/Streaming/*`（新） | `WorldTileIndex`、`HeightTileCache`、`TerrainClipmap`、`TerrainChunkMeshBuilder`、`WorldStreamer` |
| `Assets/Scripts/World/Map/*`（新） | `MapData`（regions/poi/routes）、`PoiPatches`、`ScatterRenderer` |
| `Assets/Editor/OpenWorld/*`（新） | `ValidateMap`、`RenderStills`、`BenchFlythrough` |
| `Assets/Tests/PlayMode/` | 新增 3 个 PlayMode 测试（流式/接缝/预算）+ `Validate Map` 编辑器校验 |
| [unity-client.md](unity-client.md) §4 | 地形章节按本文件重写 |
| `README.md` 已知差异表 | "世界加载"一行改为 8 km 流式 |

## 15. 参考

- 画面分级与验收：[../pipeline-visual-fidelity.md](../pipeline-visual-fidelity.md)
- 区域气质与色板：[../worldview/03-art-direction.md](../worldview/03-art-direction.md)
- 现有 Unity 规格：[unity-client.md](unity-client.md)
- 世界数据约定：[../../world/world.json](../../world/world.json)、`server/internal/worldgen`
