# 地形制作流水线（业界参考 · 本项目版）

> 三份文档分工，互不重复：
> - **本文**：地形"内容"怎么做——从世界骨架到侵蚀、材质、布景、验收。
> - [unity-open-world.md](unity-open-world.md)：整体方案与预算（尺度、流式、服务器）。
> - [unity-open-world-steps.md](unity-open-world-steps.md)：41 步工程 SOP（代码落点、命令）。
>
> 结论：市面上的开放世界地形，**没有一个是"纯噪声直接出图"**。它们都把地形拆成
> **骨架 → 高度场 → 侵蚀 → 表面 → 布景 → 光照 → 校验** 的流水线，只是每一段用手工还是程序化不同。
> 本工程要把其中三段补上：**世界骨架、侵蚀、表面分层**。

## 0. 一页结论

| # | 业界共识 | 我们现在 | 要做的事 |
| --- | --- | --- | --- |
| 1 | 先定世界骨架，再谈高度 | fbm + 中心衰减，没有"区域"概念 | 新增 **T1 骨架**：区域图 + 海岸线 + 山脊线 + 路网 |
| 2 | 高度场是唯一真源，下游全从它派生 | 已经是 | 保持，但把侵蚀写回高度场 |
| 3 | **侵蚀不可省**（水力 + 热力） | 没有 | 新增 **T3 侵蚀**：液滴 + 崩塌，产出 flow 图 |
| 4 | 河道/道路是"刻"出来的，不是画出来的 | 只有散布 | 新增 **T4 刻蚀**：样条 + 下坡约束 + 断面 |
| 5 | 表面按坡度/高度/曲率分层 | 只有顶点色 | 升级 **T5 splat**：4 层 + flow 驱动 |
| 6 | 细节靠 AO/贴花/多尺度法线 | 有法线，无 AO | 新增 **T6**：顶点 AO 烘焙 + 程序化贴花 |
| 7 | 布景有密度与视线规则 | 有程序化地标，无 POI 数据/散布规则 | 强化 **T7**：weenie 视线锚 + 规则散布 |
| 8 | 光照是预设，不是实时 GI | 双日 + 雾 | 固定 **T8**：3 个光段预设 |
| 9 | 每天自动校验世界数据 | 有 smoke，但无地形校验 | 强化 **T9**：数据 + 剪影 + 掠射 + 飞越 |

代价：地形段从 16 天涨到 **28 天**（+12），总工期 **25 天 → 37 天（约 7.5 周）**；
生成时间从 60 s 涨到 **≤ 180 s**（可 `-SkipErosion` 快速迭代）。换来的是"像地貌"而不是"像噪声"。

## 1. 业界样本

### 1.1《刺客信条：起源》— 管线重构 + 每日自动校验

育碧蒙特利尔为它重做了整套世界制作管线：新地形系统、新的持久 AI，以及用
**Houdini Engine 程序化摆放资产**（树、崖、田）。设计技术总监 Nicholas Routhier 在
[80.lv 的访谈](https://80.lv/articles/building-the-world-of-assassins-creed-origins)里说的是（大意）：

> "做 4 km² 世界的方法，搬到 100+ km² 上不成立。别靠加人解决，重新设计管线。"

他们的另一个关键动作是**每天早上自动跑世界数据校验**：TD 用 C# 写测试脚本，在编辑器里检查
导航元素、AI 出生点、程序化摆放有没有挡住玩法；结果导出成表格、邮件和一张"可视化地图"
（内部工具 Atlas）。

**可迁移**：我们的 `open-world-check` 就是 Atlas 的极简版——数据校验必须是流水线的一等公民，
而不是最后才想起的 QA。

### 1.2《地平线：零之曙光》— Houdini 河道工具

Guerrilla 的环境美术在 Maya 里用 Houdini 插件做河道（[80.lv 的拆解](https://80.lv/articles/horizon-zero-dawn-procedural-rivers-wires)）：
先画一条曲线吸附到地形，然后**强制只向下流**（逐点取最小高度，遇到山就切山，而不是翻山），
河道宽度由**坡度**驱动（峡谷里收窄），最后做阶梯/瀑布让水"有段落"。

**可迁移**：我们的 `routes.json` 只做了"画线"，没做"刻地形"。T4 要抄它的三件事：
曲线吸附、单调下坡、宽度随坡度。

### 1.3 Houdini 地形工具链 — 侵蚀是标准件

SideFX 的 [Terrain 工具集](https://www.sidefx.com/products/houdini/world-building/terrain/)把地形做成
heightfield 节点图：`HeightField Noise / Distort / Terrace / Erode / Flow Field / Scatter`，
其中 **Erode** 明确区分冲积（fluvial）与碎屑（debris），可以叠多遍"模拟几百万年的变化"；
成品可以打包成 HDA 直接进 UE 的 World Partition。

**可迁移**：我们没有 Houdini，但节点图等价于"Go 里的分阶段函数 + JSON 参数"。
T3 就按 `Erode` 的语义实现：**先水力、再热力、保留 flow 图**。

### 1.4《孤岛惊魂 5》— 程序化世界生成进 GDC

育碧在 GDC 2020 讲过程序化世界生成（[GDC 收录页](https://gameindustrylibrary.com/documents/procedural-world-generation-of-far-cry-5)、
[中文纪要](http://www.gamelook.com.cn/2022/04/478366/)）：地形、道路、植被的生成大量借助 Houdini，
美术只做规则与精修。它证明"程序化 + 少量人工"能撑起商业 3A 的开放世界。

**可迁移**：我们的"确定性散布 + POI 精修"是同一条路，只是把 Houdini 换成 Go。

### 1.5《对马岛之魂》— 地形渲染与流式分开做

Sucker Punch 在 GDC 2021 有两场相关分享：
[Samurai Landscapes: Building and Rendering Tsushima Island on PS4](https://gdcvault.com/play/1027352/Samurai-Landscapes-Building-and-Rendering)
讲地形与渲染，
[Zen of Streaming: Building and Loading Ghost of Tsushima](https://gdcvault.com/play/1027205/contactUs)
讲流式加载——摘要里提到，对马岛是 Sucker Punch 上一部作品的 **~15 倍大**，靠加载速度与补丁体积
拿到口碑。它的世界是**手工 + 地标驱动**：用"weenie"（远处可见的视觉磁铁）组织空间
（[地标分类分析](https://www.jikguard.com/news/a-taxonomy-of-weenies-the-landmarks-that-define-ghost-of-tsushima/)）。

**可迁移**：地形渲染（M1）和流式（M2）本来就是两条线，别混在一个里程碑里；
"15 倍大"靠的是数据组织而不是更大的资产包——这正是我们"瓦片是数据、不是场景资产"的理由。
我们的 L1 地标就是 weenie，规则要写成"1.2 km 内至少看见一个"。

### 1.6 UE5 — 当代默认管线长什么样

Epic 的官方工作流是 **World Partition（分区）+ Landscape + PCG + HLOD 层 + Data Layer**：
PCG 生成的 Actor 会自动归入同一 data layer 与 HLOD 层
（[官方文档](https://dev.epicgames.com/documentation/unreal-engine/using-pcg-with-world-partition-in-unreal-engine?application_version=5.9&lang=zh-CN)）。
也就是说，"分区 + 程序化 + 分层 LOD"已经是引擎级的标准答案。

**可迁移**：我们的 clipmap 对应 Landscape 的分区，Scatter 对应 PCG，L5 对应 HLOD——
不用换引擎，但要按同一套分层思路组织数据。

### 1.7 反例：《旷野之息》— 地形为玩法服务

任天堂在 GDC 2017 讲"乘法式"设计（[中文纪要](http://www.gamelook.com.cn/2017/04/288228/)）：
地形不是背景，而是和化学、物理、天气相乘的玩法载体——他们把系统拆成"物理引擎管碰撞与运动、
化学引擎管状态变化"两层。它的地形**手工痕迹极重**，程序化只是辅助。

**结论**：纯程序化出不了"有意图的地形"。我们的补偿手段是 **POI 参数化改造地形（T4/T7）**，
让"手工意图"以数据形式存在，而不是靠人摆；同时把"地形（物理面）"和"材质/元素（化学层）"
分开实现，别在一个 shader 里硬塞。

## 2. 我们的九段流水线

| 段 | 名称 | 输入 | 工具 | 产物 | 门禁 |
| --- | --- | --- | --- | --- | --- |
| T1 | 世界骨架 | seed + `regions.json` + `routes.json` | Go | `macro.r16`（32 m）、海岸/山脊遮罩 | 区域/海岸/山脊覆盖率 |
| T2 | 高度场 | macro + 频段表 | Go | `height.f32`（1 m，8193²） | 坡度直方图、高程范围 |
| T3 | 侵蚀 | height + 参数 | Go | `height_eroded.f32` + `flow.r16` | 沟谷出现、无 NaN、确定性 |
| T4 | 刻蚀 | 侵蚀后高度 + 路线/POI | Go | `height_final.f32` + `route mask` | 单调下坡、断面连续 |
| T5 | 表面 | 高度 + 坡度 + 曲率 + flow + 区域 | Go | `splat.rgba8`（2 m，4 层） | 层间无硬边、texel density |
| T6 | 细节 | 高度 | Go | `ao.r8`（2 m）、细节法线参数 | AO 可读、无摩尔纹 |
| T7 | 布景 | 全部遮罩 + POI | Go + Unity | 散布实例、POI prefab | 密度/视线/90 s 旅行 |
| T8 | 光照 | 美术预设 | Unity | 3 光段 + 雾 | 3 层大气、阴影分辨率 |
| T9 | 校验 | 上面全部 | Go + Unity | 报告 + 5 张图 | 全绿 |

前六段在 **Go 里离线生成**（侵蚀串行以保证确定性，瓦片写出并行），T7–T9 在 Unity 里做。
这与业界"离线生成 + 引擎内精修"的结构一致，只是把 Houdini 换成代码。

## 3. 逐段设计

### T1 世界骨架（macro，32 m 采样，257²）

**目标**：先有"这块大陆长什么样"，再谈高度细节。

**输入**：`regions.json`（5 个区域多边形 + `heightBias` + `spines` 山脊线）、`routes.json`。

**做法**
1. 区域场：多边形内权重 1，边界 256 m 平滑；输出 `regionWeight[5]` 与 `hardRegion`。
2. 大陆：域扭曲（2 km）后的 fbm（4 km 基频）→ `land = smoothstep(-0.05, 0.15, n)`；
   再乘区域偏置，得到海岸线。
3. 山脊线：`regions.json` 里每条 `spines` 折线 → 距离场 `d`；山脊幅度 `A = exp(-(d/400)²)`，
   只在 `land > 0.6` 处生效。**山脊是画的，不是噪声碰运气**——这是对 1.7 的补偿。
4. 盆地：盐湖用高斯碗（半径 1.2 km，深 40 m）叠加。
5. 输出 32 m 的 `macro.r16` 与 `coastDistance`（到海的距离，米）。

**参数**：海岸线目标占比 18–25%；山脊总长 25–40 km；盆地 1 个。

**验收**：128×128 缩略图能读出"一个大陆 + 两条山脊 + 一个盐湖"；海岸线总长 40–80 km，
没有超过 400 m 的直线段。

**坑**：域扭曲别在大陆尺度上做（会把海岸线搅成碎岛）；扭曲幅度 ≤ 600 m。

**参考**：AC Origins 的区域化世界拆分、BOTW 的"地形为玩法服务"。

### T2 高度场（meso，1 m，8193²）

**目标**：一块可侵蚀的连续高度场。

**做法**
1. 双线性上采样 `macro.r16` 到 1 m。
2. 叠加三频段（世界米制）：`fbm(128 m) * 16 m`、`ridged(1024 m) * 脊线幅度 * 480 m`、
   `fbm(4096 m) * 60 m`（大陆起伏）。
3. 海岸带：`-6 ≤ h ≤ 14` 的格子 `h *= 0.92`（把海岸压成缓坡），与现有实现一致。
4. 钳到 `[-120, 620]`；记 `min/max` 到 manifest。

**验收**：坡度直方图（1 m 网格）≈ `<5° 60% / 5–20° 25% / 20–35° 12% / >35° 3%`；
最大坡度 < 70°；无 NaN；两次生成逐字节一致。

**坑**：`ridged` 用 `1 - |noise|` 后要 `pow(x, 2.1)`，否则山脊太圆；指数别超过 3，会变针尖。

### T3 侵蚀（erosion，全局一次）

**目标**：让山有沟、坡有碎石堆，而不是"噪声做的馒头"。

**做法**
1. **水力（液滴）**：在 8193² 上撒 1600 万滴（约 0.24 滴/格），每滴最多 30 步：

| 参数 | 值 | 说明 |
| --- | --- | --- |
| inertia | 0.05 | 方向惯性 |
| capacity | 4 | 携沙容量系数 |
| minSlope | 0.01 | 防除零 |
| erode | 0.3 | 侵蚀速率 |
| deposit | 0.3 | 沉积速率 |
| evaporate | 0.01 | 蒸发 |
| brushRadius | 3 | 侵蚀/沉积笔刷 |
| gravity | 4 | 重力 |

2. **热力（崩塌）**：坡度 > 30° 的格子做 8 次迭代，塌落角 35°（把过陡的坡变成碎石坡）。
3. **flow 图**：D8 最陡下降 + 拓扑累加，得到每格的汇水量；存 4 m R16（对数压缩）。
4. **确定性**：液滴按固定序号串行跑（写操作会互相影响，**不能并行**）；
   RNG 用 `splitmix64(seed ^ dropletIndex)`，不依赖全局状态。
5. 侵蚀结果写 `height_eroded.f32`，flow 单独存；T4 在它上面刻蚀后写 `height_final.f32`。

**耗时**：1600 万滴 × 30 步 ≈ 5 亿次操作，Go 单线程 15–25 s；flow 累加 3–5 s。
加上前两段，总生成时间目标 **≤ 180 s**。

**验收**：山脊两侧出现树枝状沟谷（缩略图上可见）；`flow` 在沟底显著大于坡面；
高度变化幅度 < 40 m（侵蚀不应改变大地形）；两次生成逐字节一致。

**坑**
- 并行会破坏确定性（同一格被多滴同时写）——**别为了快把侵蚀并行化**。
- 侵蚀会把海岸线啃出小锯齿；对 `|h| < 3 m` 的格子降低侵蚀强度。
- 笔刷半径太大会糊掉沟，太小会变噪点；3 m 是 1 m 网格的经验值。

**参考**：Houdini `HeightField Erode`（冲积/碎屑两段）、AC Origins 的 Houdini 地形。

### T4 刻蚀（河道 / 道路 / POI 平台）

**目标**：把"设计意图"刻进地形，而不是靠贴花假装。

**做法**
1. 路线采样：Catmull-Rom 每 2 m 一个点，取原始地形高度 `t[i]`。
2. **目标断面**：`bottom[i] = t[i] - depth`（depth 见下）；再**单调下降**：
   `bottom[i] = min(bottom[i], bottom[i-1])` —— 水/路不会往上爬。
3. 断面形状：`depth = 2 + slope * 6`（越陡越深），`width = base / (1 + slope * 2)`（峡谷收窄）；
   横向 `target = bottom[i] + depth * smoothstep(w, w+bank, d)`，`h = min(h, target)`，bank = 12 m。
   这样中心压到河床、`w+bank` 外回到原地形。
4. POI 平台：`flatten` 半径内 `h = lerp(h, poiHeight, t)`（沿用 SOP S4.3）。
5. 顺序：**先路线、后 POI**（POI 平台覆盖路口），写进注释。
6. 输出 `routeMask.r8`（0–255，供 T5 混"硬土/湿沙"）。

**验收**：沿路线每 2 m 采样，高度单调不增；横断面在 20 m 内平滑回到原地形（差 < 0.2 m）；
路口无台阶。

**坑**：单调约束会让长路线整体下沉——每 200 m 允许一次"重新吸附"到地形，
否则整条路会切进山里 20 m。

**参考**：HZD 河道工具（80.lv 拆解）。

### T5 表面（splat，2 m，4 层）

**目标**：沙、盐、岩在近景能分开，且边界由地形特征决定，不是手涂。

**做法**
1. 每格算 5 个特征：坡度、高度、曲率（拉普拉斯）、`flow`、区域权重。
2. 四层权重（0–255）：

| 层 | 条件 | 备注 |
| --- | --- | --- |
| 细沙 | 坡度 < 12°，高度 < 80 m，flow 低 | 沙丘迎风面 |
| 粗沙/砾 | 12°–26° | 丘间地、碎石坡 |
| 盐壳 | 海拔在 ±8 m 内、flow 低、盐湖区域 | 白喉盐海 |
| 玄武岩 | 坡度 > 28° 或曲率极值 | 骨脊、崖壁 |

3. 权重加 0.03 的蓝噪声抖动，避免硬边；再归一化到 255。
4. 低频色偏：每 512 m 一个 `tint`（±6%），打散平铺感。
5. 输出 `splat.rgba8`（65²/块，2 m），shader 里 4 层混合 + 顶点色只保留 AO 与大色块。

**验收**：5 m 内能数出三种材质；200 m 处无可见平铺；层间过渡宽度 ≥ 3 m。

**坑**：`flow` 单位是"格数"，不同分辨率下要归一化再用；盐壳层别用纯白，会过曝。

**参考**：UE Landscape 的 splat 分层、Houdini 的 mask-by-feature。

### T6 细节（AO + 贴花 + 多尺度法线）

**目标**：近景不"光板"。

**做法**
1. **顶点 AO**：每 2 m 一格，8 方向 × 8 步（步长 8 m，最远 64 m）取最大仰角，
   `ao = 1 - mean(sin(horizon))`，存 R8。生成时算（约 10 s），不需要运行时。
2. **多尺度细节法线**：0.5 m / 2 m / 8 m 三层，按距离淡出（现有 shader 已有 3 层，调参即可）。
3. **贴花**：裂缝（盐壳区）、车辙（沿路线）、脚印（POI 周围），用投影贴花 + 确定性散布。
4. shader 里 `ao` 乘到间接光与顶点色上，不乘直射光（否则阴影会"发灰"）。

**验收**：逆光看崖壁有 AO 层次；5 m 内三档细节可数；200 m 外无摩尔纹；帧时 +≤ 1 ms。

**坑**：AO 在裙边上要取边界值，否则每条 LOD 边界会出现一条暗线。

### T7 布景（POI / 地标 / 散布）

**目标**：地形"有人来过"。

**做法**
1. 散布规则加 `flow` 维度：沟底放圆石、崖脚放碎石堆、盐壳区放盐晶。
2. L1 地标（8 个）按"weenie"规则布：彼此 2–3 km、从任意 L2 位置 1.2 km 内可见。
3. 路线两侧 20 m 内降密度；POI 周围 30 m 内只留手工件。
4. 全部走 SOP S4.4 的 `RenderMeshInstanced` 批次。

**验收**：从任意 L2 点，`Validate Map` 的视线规则通过；散布密度在区域间有差异（不是均匀撒盐）。

**参考**：GoT 的 weenie 视线设计。

### T8 光照与大气

**目标**：地形在三个光段都"立得住"。

**做法**
1. 三个预设：晨（太阳高度 8°、暖）、正午（55°、硬）、暮（3°、长影）。
2. 阴影距离 150 m、4 级联；地形不投影，接收阴影。
3. 雾分 3 层：贴地 0–8 m、中景 300–1200 m、远景 1200–4800 m。
4. 不用实时光追/GI；AO 与顶点色承担间接光。

**验收**：三个光段各出一张图；阴影区不是死黑；逆光有亮边（与 fidelity 文档一致）。

### T9 校验（地形专用）

| 检查 | 手段 | 阈值 |
| --- | --- | --- |
| 数据 | Go `worldcheck` | 无 NaN、块数、哈希、接缝 0 |
| 骨架 | 缩略图 + 覆盖率 | 区域/海岸/山脊达标 |
| 剪影 | 逆光机位（太阳在镜头前方 15°） | 200 m 外沙脊有 3–5 个起伏 |
| 掠射 | 太阳高度 5–8° | 沟谷/碎石坡可读 |
| 材质 | 5 m 近景机位 | 三种材质可辨 |
| 性能 | 飞越 | p95 ≤ 16.7 ms |

**参考**：AC Origins 的每日自动校验——我们的 `open-world-check` 是同一思路的缩小版。

## 4. 数据契约（manifest v2.1）

```json
"layers": {
  "height": { "path": "height/h_{tx:00}_{tz:00}.r16", "sampleMeters": 1.0 },
  "biome":  { "path": "biome/b_{tx:00}_{tz:00}.r8",  "sampleMeters": 2.0 },
  "splat":  { "path": "splat/s_{tx:00}_{tz:00}.rgba8", "sampleMeters": 2.0 },
  "flow":   { "path": "flow/f_{tx:00}_{tz:00}.r16",  "sampleMeters": 4.0 },
  "ao":     { "path": "ao/a_{tx:00}_{tz:00}.r8",     "sampleMeters": 2.0 }
}
```

| 层 | 单块 | 总量（4096 块） | 用途 |
| --- | --- | --- | --- |
| height | 33.3 KB | 136 MB | 网格、碰撞、物理 |
| biome | 4.2 KB | 17 MB | 顶点色、散布 |
| splat | 16.9 KB | 69 MB | 4 层材质混合 |
| flow | 2.2 KB | 9 MB | 材质、散布、河道 |
| ao | 4.2 KB | 17 MB | 间接光遮蔽 |

合计约 **248 MB**（仍不入库，`world/open/` 忽略）。

生成分阶段，中间产物落 `world/open/_work/`：

```powershell
.\scripts\open-world.ps1 -Stage macro      # T1
.\scripts\open-world.ps1 -Stage height     # T2
.\scripts\open-world.ps1 -Stage erode      # T3（可 -SkipErosion）
.\scripts\open-world.ps1 -Stage carve      # T4
.\scripts\open-world.ps1 -Stage tiles      # T5/T6 + 写瓦片
.\scripts\open-world.ps1 -Check
```

## 5. 对现有计划的影响

| 现有步骤 | 变化 | 增量（天） |
| --- | --- | --- |
| S1.2 Field | 拆成 T1 骨架 + T2 高度场 | +3 |
| S1.3 瓦片写出 | 多写 splat / flow / ao 三层 | +1 |
| **新增 T3 侵蚀** | 全局高度场，串行液滴 + 热力 + flow | +3 |
| **新增 T4 刻蚀** | 路线单调下坡 + 断面 + POI 平台 | +2 |
| S4.6 材质分层 v1 | 升级为 4 层 splat（shader + 数据） | +2 |
| **新增 T6 AO** | 生成时烘顶点 AO | +1 |
| S4.4 散布 | 加 flow / 路线维度 | 0（含在 T5/T7） |
| S4.8 地图校验 | 加剪影 / 掠射机位 | 0（含在 T9） |
| **合计** | | **+12** |

总工期：**25 天 → 37 天（约 7.5 周）**；数据生成 **60 s → ≤ 180 s**。
如果时间紧，砍的顺序是：T6 AO → T5 只做 3 层 → T4 只做道路不做河道；**T3 侵蚀不能砍**。

依赖顺序：**T1 → T2 → T3 → T4 →（T5、T6 可并行）→ T7 → T8 → T9**。
T3 依赖 T2 的完整高度场（不能按瓦片切），所以它天然是"全局一次"的；T5 之后才可以分块写盘。

## 6. 验收矩阵（M3 结束）

| 项 | 方法 | 通过线 |
| --- | --- | --- |
| 骨架 | 缩略图 | 5 区域可辨、2 条山脊、1 个盐湖 |
| 侵蚀 | 缩略图 + flow 图 | 树枝状沟谷清晰 |
| 刻蚀 | 沿路线剖面 | 单调、无台阶 |
| 表面 | 近景 5 m | 三材质可辨 |
| AO | 逆光/崖壁 | 层次可读 |
| 布景 | 视线规则 | 100% L2 有 L1 可见 |
| 性能 | 飞越 | p95 ≤ 16.7 ms |
| 数据 | `worldcheck` | 0 error |

## 7. 参考

- [Building the World of Assassin's Creed Origins（80.lv 访谈）](https://80.lv/articles/building-the-world-of-assassins-creed-origins)
- [Horizon Zero Dawn: Procedural Rivers & Wires（80.lv）](https://80.lv/articles/horizon-zero-dawn-procedural-rivers-wires)
- [Houdini Terrain 工具集（SideFX）](https://www.sidefx.com/products/houdini/world-building/terrain/)
- [Procedural World Generation of Far Cry 5（GDC 2020 收录）](https://gameindustrylibrary.com/documents/procedural-world-generation-of-far-cry-5)、[中文纪要](http://www.gamelook.com.cn/2022/04/478366/)
- [Samurai Landscapes: Building and Rendering Tsushima Island on PS4（GDC Vault）](https://gdcvault.com/play/1027352/Samurai-Landscapes-Building-and-Rendering)、[Zen of Streaming（GDC Vault）](https://gdcvault.com/play/1027205/contactUs)
- [A taxonomy of Weenies: the landmarks that define Ghost of Tsushima](https://www.jikguard.com/news/a-taxonomy-of-weenies-the-landmarks-that-define-ghost-of-tsushima/)
- [在虚幻引擎中将 PCG 用于世界分区（Epic 官方文档）](https://dev.epicgames.com/documentation/unreal-engine/using-pcg-with-world-partition-in-unreal-engine?application_version=5.9&lang=zh-CN)
- [《旷野之息》GDC 2017 分享中文纪要](http://www.gamelook.com.cn/2017/04/288228/)
