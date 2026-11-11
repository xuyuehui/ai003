# Unity 开放大世界地图 · 实施步骤（SOP）

> 配套 [unity-open-world.md](unity-open-world.md)。那份文档讲**为什么**，这份讲**怎么做**：
> 把 M0–M4 拆成 41 个可执行步骤，每步给出目标、改动文件、做法、验收命令、常见坑和耗时。
>
> 用法：按顺序做，每步的「验收」必须绿才进下一步；`git commit` 一次只提交一个步骤。
>
> 地形“内容”的制作流水线（骨架/侵蚀/刻蚀/表面/AO/布景，参考市面开放世界做法）见
> [terrain-production-pipeline.md](terrain-production-pipeline.md)；本文的 S1.2、S1.3、S4.4、S4.6、S4.8
> 会被它细化或替换。

## 0. 总览

```
S0 准备 ─▶ M0 数据 ─▶ M1 渲染 ─▶ M2 流式/物理 ─▶ M3 内容/材质 ─▶ M4 门禁
                          └──── S3.7 服务器边界（可提前并行）────┘
```

| 阶段 | 步骤 | 天数 | 结束时必须成立 |
| --- | --- | --- | --- |
| S0 准备 | 3 | 0.5 | 旧管线基线全绿，分支干净 |
| M0 数据 | 8 | 3 | 4096 瓦片可生成、可校验、Unity 能读 |
| M1 渲染 | 8 | 5 | 8 km 地形 60 fps、无裂缝 |
| M2 流式/物理 | 8 | 5 | 传送 3 s 内可走，服务器放开到 4 km |
| M3 内容/材质 | 9 | 8 | 地图校验 0 error，近景三层材质 |
| M4 门禁 | 5 | 4 | `open-world-check` 全绿，`smoke-all` 不回归 |

每步的字段含义：**目标**（做完能做什么）、**改动**（碰哪些文件）、**做法**（按序号执行）、
**验收**（命令 + 预期）、**坑**（这一步最容易错的地方）。

---

## 1. S0 准备（0.5 天）

### S0.1 冻结基线

**目标**：确认旧管线（2 km、全量烘焙）在动工前是绿的，并留下一份可对比的基线。

**改动**：无代码。

**做法**
1. 关掉 Unity 编辑器（batchmode 与 GUI 抢同一份 `Library/` 锁，不关会卡住等锁）。
2. 拉分支：`git switch -c feat/open-world`。
3. 跑三件事并记下时间戳：

```powershell
.\scripts\smoke-all.cmd -SkipUE -SkipUEWorld
.\scripts\test-unity-playmode.cmd
.\scripts\demo-compare.cmd          # 需要 UE；没有 UE 时跳过，用现有 docs/images/demo-compare.png 当基线
```

4. 把 `artifacts/demo/` 里的 JSON 复制到 `artifacts/baseline-open-world/`（git 忽略，只做本地对比）。

**验收**：`go-tests` / `unity` / `unity-pm` 三项 PASS；PlayMode 2/2 通过。

**坑**：PowerShell 执行策略是 Restricted，直接跑 `.ps1` 会被拒；用同名 `.cmd` 或
`powershell -ExecutionPolicy Bypass -File ...`。

### S0.2 目录、命名、日志前缀

**目标**：新代码有稳定的落点，后面 40 步不用反复挪文件。

**改动**：只建目录（先不写实现）。

**做法**
1. Go：

```
server/internal/worldgen/
  spec.go        世界参数与 preset
  field.go       世界空间噪声（Height/Slope/Biome）
  region.go      区域遮罩（M3 用，M0 只留接口）
  tilewrite.go   分块写出 + 量化 + 哈希
  spawn.go       出生点搜索
  manifest.go    manifest 读写
```

2. Unity 运行时 `Assets/Scripts/World/Streaming/`：

```
WorldManifest.cs        manifest 反序列化
HeightTileCache.cs      瓦片 LRU + 双线性采样
WorldTileIndex.cs       坐标/层级换算
ClipmapSelection.cs     四叉树选层（纯函数）
TerrainMeshBuilder.cs   节点网格数组
TerrainNodeView.cs      GameObject + Mesh 上传
WorldStreamer.cs        请求队列 + 预算
TerrainClipmapRenderer.cs  每帧驱动
```

3. Unity 运行时 `Assets/Scripts/World/Map/`：`MapData.cs`、`RegionMask.cs`、`PoiPatch.cs`、`ScatterRenderer.cs`。
4. 编辑器 `Assets/Editor/OpenWorld/`：`RenderStills.cs`、`BenchFlythrough.cs`、`ValidateMap.cs`、`GenerateWorld.cs`。
5. 约定：日志统一 `[AI003][OPENWORLD]`；运行时脚本必须属于 `AI003.Runtime`（现有 asmdef，不新增程序集）；
   编辑器脚本放在 `Assets/Editor`，不得被运行时引用。

**验收**：Unity 编译 0 错误（新目录为空也没关系）；`git status` 只多出空目录/`.meta`。

**坑**：Unity 只给有资源的目录生成 `.meta`；空目录不会入库，先放一个占位 `.cs` 再删。

### S0.3 preset 开关

**目标**：新旧两套世界可以来回切，互不破坏。

**改动**：`Assets/Scripts/World/WorldBootstrap.cs`。

**做法**
1. 加字段 `[SerializeField] private string _preset = "legacy";`，`Awake` 里用
   `Environment.GetEnvironmentVariable("AI003_WORLD_PRESET")` 覆盖。
2. `Build()` 开头按 preset 分派：`legacy` 走现有逻辑（一行不改）；`open` 走新分支（先只读 manifest + 日志）。
3. `open` 分支暂时 `Debug.Log($"[AI003][OPENWORLD] preset=open tiles={manifest.tilesPerAxis}")` 后 return。

**验收**：场景不动，Play 仍进旧世界；设 `AI003_WORLD_PRESET=open` 并**重启编辑器**后只打印日志、不报错。

**坑**：`WorldBootstrap.AutoInstall` 会在任意场景挂载；新分支要保证没有地形时也不抛异常。

---

## 2. M0 数据与坐标（3 天）

### S1.1 WorldSpec 与 CLI

**目标**：把"8 km / 1 m / 128 m 瓦片"变成代码里的常量，而不是散落的魔数。

**改动**：`server/internal/worldgen/spec.go`（新）、`server/cmd/worldgen/main.go`。

**做法**
1. 定义：

```go
type Spec struct {
    Name                 string
    Seed                 int64
    Size                 float64 // 8192
    Sample               float64 // 1
    Tile                 float64 // 128
    SeaLevel             float64
    MinHeight, MaxHeight float64
}

func OpenSpec() Spec   { /* 上面的取值范围 */ }
func LegacySpec() Spec { /* 2048 / 2 / 256，与现有 world.json 对齐 */ }

func (s Spec) TilesPerAxis() int  { return int(math.Round(s.Size / s.Tile)) }      // 64
func (s Spec) SamplesPerTile() int { return int(math.Round(s.Tile/s.Sample)) + 1 } // 129
```

2. `main.go` 加 `-preset open|legacy`（默认 legacy，保证旧脚本行为不变）、`-jobs`（默认 `runtime.NumCPU()`）。
3. `-preset open` 时尺寸/分辨率从 Spec 来，忽略 `-size/-res`（传了就警告并覆盖）。

**验收**：`go run ./cmd/worldgen -preset legacy -out ../.tmp/legacy` 产出的 `world.json`
与现有 `world/` 字段一致；`-preset open` 打印 8192 / 64 / 129。

**坑**：Legacy 的 `Size=2048, Sample=2, Tile=256`，`TilesPerAxis=8`——别用 open 的 128 m 去算旧数据。

### S1.2 世界空间噪声 Field

**目标**：任意 `(x, z)` 米坐标都能独立算出高度/群系，且与瓦片划分无关（这是并行与断点续跑的前提）。

**改动**：`server/internal/worldgen/field.go`（新）。

**做法**
1. 定义 `type Field struct { spec Spec; continent, mountain, detail, moisture *perlin }`，
   构造函数 `NewField(spec)` 用 `seed`/`seed+101`/`seed+202`/`seed+303` 建噪声。
2. 全部频率改成"米"：

```go
func (f *Field) Height(x, z float64) float64 {
    base := 0.55 + 0.55*f.continent.fbm(x/4096, z/4096, 5, 2.0, 0.5)
    land := base * f.landMask(x, z)              // 0..1，海岸线由它决定
    h := -90 + land*230
    mask := clamp01((land - 0.60) / 0.40)
    if mask > 0 {
        ridge := 1 - math.Abs(f.mountain.fbm(x/1024, z/1024, 4, 2.05, 0.5))
        h += mask * mask * math.Pow(clamp01(ridge), 2.1) * 480
    }
    h += f.detail.fbm(x/128, z/128, 3, 2.1, 0.45) * 16
    return h
}
```

3. `landMask`：M0 先用低频噪声做大陆（`smoothstep` 出海岸），M3 再接 `RegionMask`。
4. 群系/颜色 `BiomeAt(x,z)`：按高度 + 湿度 + 坡度返回 `uint8`（沿用现有 6 种 id，不新增）。
5. 加 `Slope(x,z)`：中心差分（±2 m，1 m 步长），返回 rise/run。

**验收**：`go test ./internal/worldgen -run Field` 里最小几条：同一坐标两次调用相等；
`Height` 在 100 万次采样里无 NaN；8 km 范围高度落在 `[-120, 620]`。

**坑**：**不要**在 Field 里缓存任何"已算过的瓦片"；一旦有状态，并行就不确定了。
浮点必须用 `float64`，`float32` 累加会让两端/两次生成不一致。

### S1.3 瓦片写出与量化

**目标**：把 Field 切成 4096 块，写成 R16，并为 manifest 计算哈希。

**改动**：`server/internal/worldgen/tilewrite.go`（新）。

**做法**
1. 路径与命名：

```go
func (s Spec) HeightPath(dir string, tx, tz int) string {
    return filepath.Join(dir, "height", fmt.Sprintf("h_%02d_%02d.r16", tx, tz))
}
```

2. 量化：`q = round(clamp01((h-min)/(max-min)) * 65535)`，写出小端 `uint16`。
3. 每块 129×129，`i,j ∈ [0,128]`，世界坐标：

```go
ox := -s.Size/2 + float64(tx)*s.Tile
oz := -s.Size/2 + float64(tz)*s.Tile
h := f.Height(ox + float64(i)*s.Sample, oz + float64(j)*s.Sample)
```

4. 群系每块 65×65（2 m），最近邻采样，`uint8`。
5. 并行：worker pool 按瓦片分发，**每块自带 buffer**，不要复用共享切片；结果只写文件。
6. 哈希：每块算 `sha256`，再对所有块哈希排序后拼起来算总哈希，写进 manifest（保证并行下稳定）。
7. 进度：每 256 块打印一次 `tiles 1024/4096 12.3s`。

**验收**：4096 个 `.r16` + 4096 个 `.r8`；随机抽 8 块反量化后与 `f.Height` 误差 ≤ 1.2 cm；
两次生成总哈希一致。

**坑**：文件数 4096+4096，Windows 资源管理器会很慢，属正常；`bufio.Writer` 必须 `Flush`；
不要用 `os.Create` 每块新建再忘记 Close（句柄耗尽）。

### S1.4 出生点搜索与 manifest

**目标**：给出一个"站得住、在陆地上、看得见远景"的出生点，并把世界参数固化成 manifest。

**改动**：`spawn.go`、`manifest.go`（新）。

**做法**
1. 候选网格 32 m 步长，跳过边缘 512 m。
2. 条件：`h ≥ sea+8`、`slope ≤ 0.08`（≈4.6°）、四方位 300 m 处都高于海面（不在小岛上）、
   并给"靠近中心"一个很小的加分（避免出生在世界角落）。
3. 取分数最高者，写 `spawn{x,y,z}`（y = 地形高 + 1.2，给角色胶囊留空间）。
4. manifest 结构照 [unity-open-world.md](unity-open-world.md) §4.3；每层写 `tiles` 数与总哈希。
5. `manifest.json` 写在 `world/open/` 根，与 `height/`、`biome/` 同级。

**验收**：`spawn.y > sea`；`|spawn.y - Height(spawn)| < 2`；重复生成 spawn 相同。

**坑**：候选点过滤要"先便宜后贵"（先高度、再坡度、最后四方位），否则 4 万个候选 × 8 次采样会拖慢 10 s。

### S1.5 Go 侧测试

**目标**：数据一旦错了，能在 2 秒内发现，而不是等到 Unity 里看裂缝。

**改动**：`world_test.go` 扩充。

**做法**
1. `TestDeterministic`: 同 seed 两次 `GenerateTiles` 到临时目录，逐文件字节比较。
2. `TestSeam`: 相邻瓦片共享边界相等——`tile(tx,tz)` 的 `i=128` 采样必须等于 `tile(tx+1,tz)` 的 `i=0`。
3. `TestQuantization`: 随机 1000 点，反量化误差 ≤ 1.2 cm。
4. `TestSpawn`: 在陆地上、坡度达标。
5. `TestTileIndex`: 世界坐标 ↔ (tx,tz,i,j) 往返。
6. 全部用 `t.TempDir()`，不写仓库。

**验收**：`go test ./internal/worldgen -count=1` 全绿；`go vet ./...` 干净。

**坑**：`GenerateTiles` 要能接受 `-jobs 1`，测试里用 1 线程避免 flaky。

### S1.6 `open-world.ps1` 封装

**目标**：一条命令从 seed 到 Unity 可读，且能只做校验。

**改动**：`scripts/open-world.ps1`（新）、`scripts/open-world.cmd`（新，包装 `-ExecutionPolicy Bypass`）。

**做法**
1. 参数：`-Preset open|legacy`、`-Jobs`、`-OutDir`、`-Check`、`-NoClients`、
   `-Tiles "tx0-tx1,tz0-tz1"`（只重算矩形内的瓦片，M3 迭代用）。
2. 默认流程：`go run ./cmd/worldgen -preset open -jobs N -out world/open` → 拷贝
   `manifest.json`、`height/`、`biome/` 到 `Assets/StreamingAssets/open/` → 打印总哈希与耗时。
3. `-Check`：只读，重算哈希、块数、边界、出生点，不写文件。
4. 加进 `.gitignore`：`/world/open/`、`clients/unity/UnityGameClient/Assets/StreamingAssets/open/`。

**验收**：从零生成 ≤ 60 s（4 核，实测后回填）；`-Check` 在篡改一个字节后非 0 退出。

**坑**：`StreamingAssets/open/` 是 150 MB 级，`Copy-Item` 逐文件慢；用 `robocopy /MIR` 或只在哈希变化时拷。

### S1.7 Unity 读取层

**目标**：Unity 能按坐标取高度，不加载全图。

**改动**：`WorldManifest.cs`、`HeightTileCache.cs`（新）。

**做法**
1. `WorldManifest.Load(dir)`：Newtonsoft 反序列化；字段名用小写（与 JSON 一致）。
2. `HeightTileCache`：
   - 容量 128 块，`Dictionary<int, ushort[]>` + `LinkedList<int>` 做 LRU；
   - `TryGetTile(tx,tz,out ushort[] data)`：内存命中直接返回，否则同步 `File.ReadAllBytes` 并解码；
   - `TrySample(x,z,out float h)`：定位到 (tx,tz) 与块内 (fx,fz)，双线性插值；
   - `HeightAt(x,z)`：越界返回 `seaLevel-50`（与旧 `WorldData` 行为一致）。
3. 线程安全：读路径只在主线程；异步预热（M2）才走后台，用 `ConcurrentQueue` 回主线程（照抄 `GatewayClient` 的模式）。

**验收**：EditMode 测试随机 1000 点，`HeightAt` 与 Go 端导出的期望值（或反量化原始字节）一致，误差 ≤ 1.5 cm。

**坑**：瓦片边界要读相邻块的 halo 才能双线性（`i=128` 时查 `tx+1` 的 `i=0`）；
越界处理必须与旧实现一致，否则旧测试的"落水"逻辑会变。

### S1.8 M0 验收

```powershell
.\scripts\open-world.ps1 -Check
cd server; go test ./... -count=1
.\scripts\smoke-all.cmd -SkipUE -SkipUEWorld
```

**验收**：三条全绿；Unity 里设 `AI003_WORLD_PRESET=open` 能打印瓦片数；旧场景仍正常。

---

## 3. M1 地形渲染（5 天）

### S2.1 WorldTileIndex

**目标**：坐标换算只写一次，别的类都调它。

**改动**：`WorldTileIndex.cs`（新）。

**做法**
1. 常量：`TileMeters = 128`、`SamplesPerTile = 129`、`Levels = 6`。
2. API：

```csharp
public static int   TileX(float worldX);                 // floor((x + half)/128)
public static float TileOriginX(int tx);                 // -4096 + tx*128
public static int   NodeSizeMeters(int level);           // 128 << level
public static int   NodeSampleStep(int level);           // 1 << level
public static bool  NodeContains(int level, int nx, int nz, float x, float z);
public static int   NodeKey(int level, int nx, int nz);  // 位打包，供 HashSet 用
```

3. 位打包：`level(3 bit) | nx(8 bit) | nz(8 bit)`，够用到 8192 m。

**验收**：EditMode 单测往返 10 万次；`NodeContains` 在边界上不重不漏（相邻节点测试）。

**坑**：`TileX` 对负数要用 `Mathf.FloorToInt`，`(int)` 会向 0 截断，边界处错一块。

### S2.2 四叉树选层

**目标**：给定相机位置，输出"这一帧要画哪些节点"，且不闪烁、不重叠。

**改动**：`ClipmapSelection.cs`（新）。

**做法**
1. 分裂距离表（对应方案 §6.1 的覆盖表）：

```csharp
static readonly float[] SplitDistance = { 0f, 192f, 448f, 960f, 1920f, 3840f, 8000f };
// SplitDistance[level]：level 节点距离小于它就分裂成 4 个 level-1 子节点
// level 6（8192 m 根）距离恒为 0，所以根一定分裂
```

2. 递归（根 = level 6 的 8192 m 节点，或 level 5 的 4 个 4096 m 节点）：

```csharp
void Visit(int level, int nx, int nz)
{
    int   key      = NodeKey(level, nx, nz);
    float d        = DistanceToNodeAabb(cameraXZ, level, nx, nz);
    bool  expanded = _expanded.Contains(key);

    // 已展开的节点要等距离超过 1.15 倍才折叠，避免相机抖动反复重建
    float threshold = expanded ? SplitDistance[level] * 1.15f : SplitDistance[level];
    if (level > 0 && d < threshold)
    {
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            Visit(level - 1, nx * 2 + i, nz * 2 + j);
        return;
    }

    if (expanded) _expanded.Remove(key);
    _out.Add(new ClipNode(level, nx, nz, d));
}
```

3. 滞回：`_expanded` 跨帧保留，折叠要等距离超过 1.15 倍；否则相机原地抖动会反复重建。
4. 输出按距离升序（近处先建），供 M2 的流式队列直接复用。

**验收**：EditMode 单测：相机在中心时各层节点数落在方案预估范围（L0 4–9、L1 6–9、…、总数 30–45）；
相机移动 10 m 后节点集合变化 ≤ 4 个。

**坑**：距离用节点 AABB（含 Y）还是 XZ？**用 XZ**，否则相机抬高后 LOD 突然变粗。

### S2.3 网格构建（含裙边）

**目标**：把一块瓦片的高度数据变成顶点/三角形数组，纯函数、可后台线程跑。

**改动**：`TerrainMeshBuilder.cs`（新）。

**做法**
1. 顶点（level L，间距 `step = 1 << L` 米）：**每层都用 129 个采样点**，节点边长 `128 << L`，
   于是 L0 是 1 m 间距 / 128 m 边长，L5 是 32 m 间距 / 4096 m 边长——顶点数恒定，三角形数恒定。
2. 顶点结构 28 B：`Vector3 pos; Vector3 normal; Color32 color;`（shader 只用这三个通道，见
   `AshkarTerrain.shader:65-67`）。
3. 法线：中心差分，采样步长 = step；边界处向相邻瓦片多取 1 个采样（halo）。
4. 顶点色：`WorldTheme` 按 biome/高度/坡度/曲率算（沿用现有 `BuildColorGrid` 的公式，改成点采样）。
5. 裙边：四边各加一列顶点，`y -= 12f`，法线沿用边界法线，颜色沿用边界顶点色，三角形朝外。
6. 索引：`UInt32`（129² 顶点 < 65535，其实 UInt16 够，但统一 UInt32 省一次格式分支）。
7. 输出 `TerrainMeshData { Vector3[] Pos; Vector3[] Nrm; Color32[] Col; int[] Idx; Bounds Bounds; }`。

**验收**：EditMode 单测：平面高度场 → 所有法线 ≈ +Y；45° 斜面 → 法线与解析值夹角 < 0.5°；
顶点数 = `129² + 4×129`，三角形数 = `32,768 + 1,024`。

**坑**：裙边深度要大于最大层间高差（取 12 m），太浅会露出缝隙；裙边顶点法线必须沿用边界法线，
留零向量会被光照成黑边。

### S2.4 网格池与上传

**目标**：主线程只做"上传 + 挂组件"，不分配。

**改动**：`TerrainNodeView.cs`（新）。

**做法**
1. 每个 view 持有 `GameObject + MeshFilter + MeshRenderer + Mesh`，由池复用。
2. 上传走 buffer 接口（已确认存在）：

```csharp
mesh.SetVertexBufferParams(count,
    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
    new VertexAttributeDescriptor(VertexAttribute.Normal,   VertexAttributeFormat.Float32, 3),
    new VertexAttributeDescriptor(VertexAttribute.Color,    VertexAttributeFormat.UNorm8, 4));
mesh.SetVertexBufferData(_vertices, 0, 0, count, 0, MeshUpdateFlags.DontRecalculateBounds);
mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
mesh.SetIndexBufferData(_indices, 0, 0, indexCount, MeshUpdateFlags.DontValidateIndices);
mesh.SetSubMesh(0, new SubMeshDescriptor(0, indexCount), MeshUpdateFlags.DontRecalculateBounds);
```

   其中 `_vertices` 是 `TerrainVertex[]`（`[StructLayout(LayoutKind.Sequential)]`，28 B）。
3. 第一版可以先用 `mesh.SetVertices/SetNormals/SetColors` 跑通，再替换成上面的快路径——
   两者接口一致，替换只动 `Apply`。
4. 材质只有一份（`AI003/AshkarTerrain`），在所有 view 间共享。
5. 池上限 128；`Release` 时清空 mesh 但不销毁 GameObject。

**验收**：单节点 `Apply` 用 `Stopwatch` 计时 ≤ 1.5 ms（编辑器里测，打成日志）。

**坑**：`SetVertexBufferParams` 必须在 `SetVertexBufferData` 之前调一次；重复调会重置 buffer。
`MeshUpdateFlags.DontRecalculateBounds` 要求自己设 `mesh.bounds`，否则剔除会错。

### S2.5 Clipmap 渲染循环与 Bootstrap 接入

**目标**：Play 时 8 km 地形出现、随相机移动换层。

**改动**：`TerrainClipmapRenderer.cs`（新）、`WorldBootstrap.cs`。

**做法**
1. `Update()`：选层 → 对每个节点确保有 view（缺则建，预算 2 个/帧）→ 释放不在集合里的 view。
2. `WorldBootstrap.Build()` 的 `open` 分支：读 manifest → 建 `HeightTileCache` → 建
   `TerrainClipmapRenderer` → 起一台自由飞行相机；**不**创建 `WorldTerrain`、**不**调 `BakeTerrain`、
   **不** `SpawnPlayer`（M2 才有碰撞，现在生成角色会直接掉下去）。
3. `FreeFlyCamera`（临时组件，WASD + 右键转向，速度 20–200 m/s）只用于 M1 看地形，M2 删除。
4. HUD 加两行：`tiles`（可见节点数）、`tris`（估算三角形）。

**验收**：Play 后 8 km 地形可见，相机跑 1 km 后近处仍是 1 m 网格；`tiles` 稳定在 30–45。

**坑**：节点世界坐标 = `TileOrigin(level, nx, nz)`，别把 local/global 搞混；`view.transform.position`
用节点原点，顶点用 0..128×step 的局部坐标。

### S2.6 相机、雾、阴影

**目标**：视距与大气匹配，别让远处地形"糊在雾里"或"突然截断"。

**改动**：`WorldBootstrap.cs`（open 分支）、`AshkarLook.ApplyScene`（仅 open preset）。

**做法**
1. `farClipPlane = 5000`；`shadowDistance = 150`（主光级联）；`RenderSettings.fog` 线性 800–4800。
2. 天空/双日光沿用 `[AI003] Environment` 里已 author 的对象；open preset 不重建。
3. 地形 `shadowCastingMode = Off`（保持现状），接收阴影。

**验收**：360° 环视，地平线处无几何截断；雾在 4.8 km 前把边缘收掉。

**坑**：`shadowDistance` 调大要同步 `shadowCascades`，否则远处阴影块状明显。

### S2.7 五机位出图工具

**目标**：每次改地形都能并排对比，而不是靠记忆。

**改动**：`Assets/Editor/OpenWorld/RenderStills.cs`（新）。

**做法**
1. 五个机位用**规则**算，不硬编码坐标：出生点平视；出生点附近最高点俯拍；
   海岸线（第一个 h<0 的方向）低机位；LOD 交界（距出生点 200 m）侧拍；y=900 高空俯瞰。
2. 渲染 1920×804，写 `artifacts/open-world/still-{1..5}.png`。
3. 批处理入口 `Run()`：照 `AshkarStill` 的写法，`Application.isBatchMode` 时 `EditorApplication.Exit`。

**验收**：一条命令出 5 张图；连跑两次逐像素一致（同一机位、同一光照）。

**坑**：高空俯瞰要临时把 `farClip` 拉到 12000，否则裁掉半个世界；出图后恢复。

### S2.8 M1 验收

| 项 | 目标 | 怎么测 |
| --- | --- | --- |
| 三角形 | ≤ 1.5 M | HUD / Frame Debugger |
| 帧时 | 1080p ≤ 16.7 ms | 站出生点 + 跑 500 m 各 10 s |
| 接缝 | 无裂缝 | 5 机位里 2 张正对 LOD 交界 |
| 稳定性 | 无每帧 GC | Profiler `GC Alloc` = 0 B/帧 |

---

## 4. M2 流式与物理（5 天）

### S3.1 Streamer 状态机与预算

**目标**：加载/卸载有明确状态与上限，不靠"每帧全量重建"。

**改动**：`WorldStreamer.cs`（新）。

**做法**
1. 状态：`Idle → Queued → HeightReady → MeshReady → ColliderReady → Active → Cooldown → Unloaded`。
2. 队列按（距离, 是否在相机前方）排序；每帧 `Stopwatch` 计时，超 2 ms 就停。
3. 卸载：距离 > 4500 m 进 `Cooldown`，下一帧真正释放；释放时回池。
4. 统计：`loaded / queued / builtThisFrame / msThisFrame`，每 2 s 打一条日志。

**验收**：跑图 60 s，`msThisFrame` p95 ≤ 2 ms；`loaded` 不超过池上限。

**坑**：预算计时要在**主线程**测，后台线程耗时单独统计；否则会把线程等待算进去。

### S3.2 异步读盘

**目标**：首帧之后不再有同步 IO 卡顿。

**改动**：`HeightTileCache.cs`。

**做法**
1. `Task.Run(() => File.ReadAllBytes(path))`，完成后把 `(tx,tz,bytes)` 塞 `ConcurrentQueue`，
   `Update` 里出队解码入 LRU。
2. 同一个瓦片去重（`HashSet<int> _inFlight`），避免重复读。
3. 读失败（文件缺失/损坏）→ 记 error、该块用"海平面−50"占位，不抛异常。

**验收**：把 `height/h_10_10.r16` 临时改名，Play 不崩、只报错；恢复后下一帧正常。

**坑**：`Application.streamingAssetsPath` 在编辑器下是 `Assets/StreamingAssets`，打包后是
`<exe>_Data/StreamingAssets`，两边都能 `File.ReadAllBytes`（Windows 限定，够用）。

### S3.3 碰撞体

**目标**：角色站得住，且不会因为 cook 掉帧。

**改动**：`TerrainNodeView.cs`、`WorldStreamer.cs`。

**做法**
1. 只给 L0/L1 建 `MeshCollider`：脚下块用 1 m 网格（129²），邻块用 2 m（65²）。
2. 每帧最多 cook 1 块，`Stopwatch` 计时，超 4 ms 就顺延。
3. 卸载时 `collider.sharedMesh = null` 再销毁，避免物理场景残留。
4. cook 完成前，角色用 `HeightAt` 兜底：`ThirdPersonController` 在 `IsGrounded == false` 且
   脚下 2 m 内无碰撞时，直接把 y 钳到 `HeightAt + 0.02`。

**验收**：从 50 m 高空落到新加载的瓦片上，不穿透；`Physics.Raycast` 在边界两侧都命中。

**坑**：`MeshCollider` 默认 `convex = false`，只适合静态地形——不要给它挂 Rigidbody。

### S3.4 水面跟随

**目标**：8 km 世界里走到哪都有海平面。

**改动**：`TerrainClipmapRenderer.cs` 或独立 `OceanFollower.cs`。

**做法**
1. 水面单平面，边长 `8 × worldSize`，每帧把 XZ 吸附到 128 m 网格、Y = seaLevel。
2. 吸附是为了避免水面随相机连续移动导致的高光闪烁。
3. 材质沿用现有 `CreateWaterMaterial`（URP/Lit 透明）。

**验收**：从出生点走到地图边缘，水面始终在视野内，无接缝。

**坑**：水面在 8 km 外仍然渲染会吃 overdraw；把 `renderer.shadowCastingMode` 关掉、
`receiveShadows = false`（现状就是这样）。

### S3.5 传送与重生

**目标**：调试与测试能瞬移，且瞬移后地形跟得上。

**改动**：`ThirdPersonController.cs`、`WorldStreamer.cs`。

**做法**
1. `ThirdPersonController.Teleport(Vector3)`：置位置、清速度、`CharacterController` 关闭再打开
   （避免残留碰撞状态）。
2. `WorldStreamer.Teleport(x,z)`：清空队列 → 同步建 3×3 的 L0 → 再放开异步。
3. `DebugTeleport(x,z)` 走环境变量或 HUD 快捷键，仅编辑器/测试用。

**验收**：传送到 2 km 外，3 s 内可正常行走，不坠落。

**坑**：传送后相机 `OrbitCamera.Snap()` 要调用，否则会从旧位置飞过来。

### S3.6 PlayMode 测试

**目标**：流式与接缝进 CI/本地回归，不靠肉眼。

**改动**：`Assets/Tests/PlayMode/OpenWorldStreamingTests.cs`、`OpenWorldSeamTests.cs`、
`OpenWorldBudgetTests.cs`（新）。

**做法**
1. Streaming：等 `WorldBootstrap.IsReady` → `Teleport(±2000)` → 等 3 s → 断言 `IsGrounded`
   且脚下有 L0（`WorldStreamer.NearestLevelAt(player) == 0`）。
2. Seam：随机 64 条瓦片边界，从 `y = maxHeight + 50` 向下 `Physics.Raycast`，断言命中，
   且命中点与 `HeightAt` 差 ≤ 0.1 m。
3. Budget：5 个机位各渲染 2 s，断言 `TerrainClipmapRenderer.VisibleTriangleEstimate ≤ 1.5M`。
4. 不靠环境变量选 preset（编辑器里改环境变量不会热生效，测试进程也不好设）：
   `WorldBootstrap` 暴露 `static void ForcePreset(string)`，测试在 `[OneTimeSetUp]` 里调用。

**验收**：`.\scripts\test-unity-playmode.cmd` 5/5 通过（原 2 + 新 3）。

**坑**：PlayMode 测试之间 `WorldBootstrap` 是静态单例，域重载才清；每个测试用
`[UnityTearDown]` 销毁实例，否则第二个测试拿到脏状态。

### S3.7 服务器边界（可与 M1 并行）

**目标**：玩家能走到 4 km，服务器不再把人夹回 1 km。

**改动**：`server/internal/gateway/state.go`、`server/cmd/server/main.go`、`state_test.go`。

**做法**
1. 新增 `Bounds{Half, MinY, MaxY}`，启动时从 `-world ../world/open`（或 `AI003_WORLD`）读 manifest；
   读不到回退 `legacy` 的 1024/−200/600。
2. 删掉 `worldHalfExtentMeters` 常量，`sanitize` 用 `b.Half`。
3. 加 `-world` 到 `main.go`，默认 `../world/open`（不存在则 `./world`）。
4. 单测：manifest 8192 → `sanitize(5000,0,0)` 夹到 4096；manifest 2048 → 夹到 1024。

**验收**：`go test ./internal/gateway -run Bounds`；Unity 走到 3 km 后 `snapshot` 位置不被回夹。

**坑**：`join`/`move` 的清洗是同一个函数，改一处全生效；别在客户端也加一份 clamp，
否则两边不一致时很难查。

### S3.8 M2 验收

```powershell
.\scripts\test-unity-playmode.cmd
.\scripts\open-world.ps1 -Check
.\scripts\smoke-all.cmd -SkipUE -SkipUEWorld
```

**验收**：PlayMode 5/5；传送 3 s；流式 p95 ≤ 2 ms；无单帧 > 50 ms。

---

## 5. M3 内容与材质（8 天）

### S4.1 regions.json 与 RegionMask

**目标**：地貌按"区域"而不是"全图 uv 梯度"分布。

**改动**：`map/regions.json`（新）、`server/internal/worldgen/region.go`（新）、
`Assets/Scripts/World/Map/RegionMask.cs`（新）。

**做法**
1. 结构：`{ "version":1, "regions":[ { "id","name","polygon":[[x,z],…],"heightBias","material","priority" } ] }`。
2. Go 侧：点在多边形内 → 权重 1，边界 256 m 内平滑过渡；`Field.landMask` 用区域高度偏置。
3. Unity 侧：同一份 JSON，`RegionMask.Sample(x,z)` 返回 `(regionId, weight)`，供材质与散布用。
4. 区域数据**手写**（8 km 只有 5 个区域），不做编辑器绘制——先跑通再加工具。

**验收**：生成后俯视图能看出 5 个区域的颜色/高程差异；`-Check` 通过区域覆盖率（每区 > 3%）。

**坑**：多边形要按逆时针/顺时针统一，point-in-polygon 对自交多边形会给出错误结果；
先用 QGIS/画图确认不自交。

### S4.2 POI 数据与放置工具

**目标**：地标/兴趣点能被记录、复用、校验，而不是散落在场景里。

**改动**：`map/poi.json`（新）、`Assets/Editor/OpenWorld/PlacePoi.cs`（新）。

**做法**
1. 结构：

```json
{ "id":"well-01", "tier":1, "kind":"landmark",
  "x":-2048, "z":-3072, "yaw":40,
  "patch": { "radius":60, "falloff":24, "height":58, "flatten":0.9 },
  "prefab":"World/POI/WellTower" }
```

2. 编辑器工具：Scene 视图点一下 → 取地形高度 → 追加到 `poi.json`（写回仓库文件，不写场景）。
3. 只允许 `tier ∈ {1,2}`；`tier 3` 是程序化散布，不占 POI。

**验收**：新增 POI 后 `Validate Map` 能看到它；删掉后不再出现在场景里。

**坑**：写 JSON 时保持字段顺序与缩进，否则每次 diff 都是一整片——用 `JsonConvert` + 固定格式，
或手写模板。

### S4.3 地形补丁（POI 改地形）

**目标**：井塔/营地把地面压平，而不是悬空或陷进去。

**改动**：`Assets/Scripts/World/Map/PoiPatch.cs`、`TerrainMeshBuilder.cs`。

**做法**
1. 建块时，对落在节点 AABB（外扩 `radius+falloff`）内的 POI，按距离叠加：

```
t = 1 - smoothstep(radius, radius+falloff, dist)
h = lerp(h, poiHeight, t * flatten)
```

2. 只对 L0/L1 应用（远处看不出来，且省时间）；L2 以上用原始高度。
3. 同一位置多个 POI 按 `id` 排序依次应用，保证确定性。

**验收**：POI 中心 1 m 内高度与 `poiHeight` 差 ≤ 0.05 m；接缝处无台阶（±10 m 内高差 < 0.5 m）。

**坑**：补丁范围要覆盖整个 `radius+falloff`，否则边缘出现"坑沿"；半径用世界米，不是采样点数。

### S4.4 确定性散布与实例化

**目标**：每块瓦片自动长石头/车辙，且两次生成一致。

**改动**：`ScatterRenderer.cs`（新）。

**做法**
1. 种子：`hash(tileSeed, tileX, tileZ, index)`（整数哈希，不用 `UnityEngine.Random`，它带全局状态）。
2. 规则：坡度 < 0.6、高度 > 海面 + 2、离路线 > 20 m（S4.5 之后）、密度按区域材质。
3. 每块上限：岩石 24、枯木 8、残骸 4；> 600 m 不生成。
4. 渲染：

```csharp
var rp = new RenderParams            // struct，默认构造 + 对象初始化器
{
    material = material,
    worldBounds = new Bounds(center, size),
    shadowCastingMode = ShadowCastingMode.On,
    receiveShadows = true,
    layer = gameObject.layer,
};
Graphics.RenderMeshInstanced(ref rp, mesh, 0, _matrices, _count);
```

5. 每块一个实例数组，随节点加载/卸载一起建/释放。

**验收**：同一相机位置两次 Play 的实例矩阵逐字节一致；draw call 增量 ≤ 3。

**坑**：`RenderMeshInstanced` 的 `worldBounds` 必须覆盖所有实例，否则会被整体剔除（"石头消失"）；
上限 `1023` 个/批，超过要分批。

### S4.5 routes.json

**目标**：商路/河床把 POI 串起来，并在地形上留下可读的痕迹。

**改动**：`map/routes.json`（新）、`RegionMask` 或独立 `RouteField`。

**做法**
1. 结构：`{ "id":"salt-road", "points":[[x,z],…], "width": 12 }`，点之间用 Catmull-Rom 插值。
2. 用途：散布降密度（离路线 < 20 m 不摆大石）、地形轻微压平（±1 m）、材质混入"硬土"。
3. 只做 3–5 条，总长 ≤ 40 km。

**验收**：路线在俯视图里连续；沿路线走 1 km 无台阶/断层。

**坑**：压平要在补丁之后应用，否则被 POI 覆盖；顺序写进代码注释。

### S4.6 材质分层 v1

**目标**：沙/盐/岩三种东西在近景能分开（对应 fidelity L3 的第一步）。

**改动**：`Assets/Shaders/AshkarTerrain.shader`。

**做法**
1. 在现有 shader 里加按坡度/高度的三套参数混合（不新增 pass）：
   `smoothstep(0.55, 0.75, up)` 决定岩/沙；`height` 决定盐（近海平面 ±5 m）。
2. 顶点色继续承担大色块；细节法线按材质切换强度。
3. 不做 splat 贴图（那是 L3 的第二步），先证明"参数分层"有效。

**验收**：5 机位近景能数出三种材质；帧时增量 ≤ 1 ms。

**坑**：裙边三角形是竖直面，但法线沿用边界（朝上）；如果图省事改成竖直法线，
地平线上会出现一条亮边。

### S4.7 八个 L1 地标

**目标**：2–3 km 一个参照物，走路有方向感。

**改动**：`Assets/Resources/World/POI/*.prefab`（新）、`PoiLoader.cs`。

**做法**
1. 复用 `AshkarProps` 的井塔/骨拱/盐晶塔，导出成 prefab（一次性的编辑器脚本）。
2. 按 `poi.json` 的 `prefab` 字段加载：`Resources.Load<GameObject>`，距离 < 1.5 km 时实例化。
3. 卸载：距离 > 3 km 销毁；8 个地标体量小，实测峰值内存后再决定要不要常驻。

**验收**：从任意 L2 位置 1.2 km 内至少看到 1 个 L1（`Validate Map` 的视线规则）。

**坑**：`Resources` 目录会全部打进包，prefab 别引大贴图；8 个地标总大小控制在 50 MB 内。

### S4.8 地图校验工具

**目标**：内容规则自动化，替代"看起来还行"。

**改动**：`Assets/Editor/OpenWorld/ValidateMap.cs`（新）。

**做法**
1. 规则照方案 §5.5：落地、间距、覆盖、出生点、视线、密度。
2. 输出 `artifacts/open-world/map-report.json`：每条规则的 `id / pass / detail`。
3. 批处理 `Run()`：有 error 时 `EditorApplication.Exit(1)`；warn 不影响退出码。
4. 日志 `[AI003][OPENWORLD] PASS/FAIL`，与 `WorldSmokeTest` 同格式。

**验收**：手工把两个 POI 挪到 100 m 内，`Validate Map` 报错；恢复后 0 error。

**坑**：视线检测用 64 个方向采样 + 高度比较，不要真做 64 次 raycast（慢且要等物理）。

### S4.9 M3 验收

| 项 | 目标 |
| --- | --- |
| 地图校验 | 0 error，warn ≤ 5 |
| 旅行 | 相邻 L2 帆撬 90 s 可达（抽 10 对实测） |
| 材质 | 5 机位近景三种材质可辨 |
| 性能 | 帧时 ≤ 16.7 ms（含散布与地标） |

---

## 6. M4 门禁与文档（4 天）

### S5.1 Bench Flythrough

**目标**：一条固定航线，量化"这次改动有没有让帧变差"。

**改动**：`Assets/Editor/OpenWorld/BenchFlythrough.cs`（新）。

**做法**
1. 航线：出生点 → 沙脊 → 海岸 → 骨脊 → 回到出生点，Catmull-Rom，60 s，匀速。
2. 采样：每帧 `Time.unscaledDeltaTime`、`TerrainClipmapRenderer.VisibleTriangleEstimate`、
   `loaded`、`msThisFrame`；结束后算 p50/p95/max。
3. draw call 用 `ProfilerRecorder` 读渲染类计数；读不到就标记 `null`，不要伪造。
4. 输出 `artifacts/open-world/bench.json`，含 Git 提交号与世界哈希。

**验收**：连跑两次 p95 差异 < 5%；与基线对比超标即失败。

**坑**：编辑器里的帧时受 Inspector 影响；基准用 `-batchmode -nographics` 跑不了渲染，
所以用 batchmode **带图形**（现有 `AshkarStill` 就是这么做的）。

### S5.2 截图回归

**目标**：视觉退化要在 CI/本地被发现。

**改动**：`scripts/open-world-check.ps1`（新）、`scripts/open-world-check.cmd`（新）。

**做法**
1. 跑 `RenderStills` → 与 `docs/images/open-world/still-*.png` 基线比较（首次跑自动生成基线）。
2. 指标照 `demo-compare`：平均差、≤24 的像素占比；阈值 平均 ≤ 8、占比 ≥ 90%。
3. 更新基线用 `-UpdateBaseline`，与现有脚本同风格。

**验收**：故意把雾改远 1 km，脚本应报失败。

**坑**：不同 GPU 的色调映射有 ±2/255 的天然差；阈值别压到 3。

### S5.3 CI

**目标**：无 GPU 的 CI 也能拦住数据错误。

**改动**：`.github/workflows/verify.yml`。

**做法**
1. 新 job `open-world`（windows-latest）：`go test ./...` → `go run ./cmd/worldgen -preset open -out .tmp/open`
   → `go run ./cmd/worldcheck`（新，纯 Go 校验：块数/哈希/接缝/出生点）。
2. 不跑 Unity（GitHub 无 GPU 且授权麻烦）；Unity 侧靠本地 `open-world-check`。
3. CI 时间预算 ≤ 3 分钟。

**验收**：PR 里改坏一个种子 → CI 红；正常 PR 全绿。

**坑**：CI 上跑 4096 块生成要 60 s+，用 `-jobs 2` 并只跑 `worldcheck` 的抽样模式（抽 64 块）。

### S5.4 文档同步

**目标**：规格与实现不脱节。

**改动**
1. [unity-client.md](unity-client.md) §4：地形章节按新管线重写（保留 legacy 说明）。
2. [README.md](../../README.md) 已知差异表、脚本表：加 `open-world.ps1`。
3. [docs/specs/README.md](README.md)：索引已加，补基线列。
4. 本目录 [unity-open-world.md](unity-open-world.md)：把"实测后回填"的数字填上。

**验收**：文档里的每条命令都能直接跑通。

### S5.5 总验收

```powershell
.\scripts\open-world.ps1 -Check
.\scripts\open-world-check.cmd
.\scripts\smoke-all.cmd -SkipUE -SkipUEWorld
.\scripts\test-unity-playmode.cmd
```

**验收**：4 条全绿；5 张截图与基线差在阈值内；`bench.json` p95 ≤ 16.7 ms。

---

## 7. 日常制作循环（内容作者视角）

改一个区域/地标，不需要重生成 4096 块：

```powershell
# 1) 只重算受影响瓦片（POI 半径 200 m → 3×3 块）
.\scripts\open-world.ps1 -Tiles "30-32,40-42"

# 2) 进 Unity，Play（启动编辑器前设好 AI003_WORLD_PRESET=open），看实机效果

# 3) 改 map/poi.json 或 regions.json 后，跑校验
.\scripts\open-world.ps1 -Check

# 4) 满意了再出图与基准
#    AI003/Open World/Render Stills
#    AI003/Open World/Bench Flythrough
```

任务粒度建议：**一次只改一个 POI 或一条路线**；`map/*.json` 的 diff 就是评审材料。

---

## 8. 常见坑清单

| # | 坑 | 症状 | 处理 |
| --- | --- | --- | --- |
| 1 | Unity 单实例锁 | batchmode 卡住不退出 | 先关 GUI 编辑器 |
| 2 | `(int)` 截断负数 | 瓦片边界错一块 | 用 `Mathf.FloorToInt` |
| 3 | 共享 buffer 并行写 | 瓦片内容随机错乱 | 每 worker 独立 buffer |
| 4 | `float32` 累加 | 两次生成不一致 | Field 全用 `float64` |
| 5 | 哈希按完成顺序拼 | 并行下总哈希不稳 | 按 (tx,tz) 排序后拼 |
| 6 | `SetVertexBufferParams` 缺失 | 网格花屏/顶点错位 | 上传前调一次 |
| 7 | 忘记设 `mesh.bounds` | 节点被整体剔除 | `DontRecalculateBounds` 时手动设 |
| 8 | 裙边法线为零 | 边缘黑边 | 沿用边界法线，不要留零向量 |
| 9 | LOD 距离用 3D | 相机抬高后突变粗 | 用 XZ 距离 |
| 10 | 无滞回 | 相机抖动反复重建 | `_expanded` 集合 + 1.15 倍 |
| 11 | `MeshCollider` 挂 Rigidbody | 物理警告/穿透 | 地形碰撞体保持静态 |
| 12 | 同步读盘在 Update | 单帧 50 ms 卡顿 | 全部走 `Task` |
| 13 | 卸载后物理残留 | 角色站在空气上 | 先 `sharedMesh = null` |
| 14 | 水面连续移动 | 高光闪烁 | 128 m 吸附 |
| 15 | `Resources` 塞大图 | 包体爆炸 | prefab 共享材质/贴图 |
| 16 | JSON 字段顺序 | diff 一整片 | 固定序列化顺序 |
| 17 | 服务器/客户端双 clamp | 位置对不上 | 只在服务器 clamp |
| 18 | 域重载清静态 | 测试第二个用例失败 | `UnityTearDown` 清理 |
| 19 | `StreamingAssets/open` 入库 | 仓库 150 MB | `.gitignore` |
| 20 | 截图基线跨 GPU | CI 永远红 | 阈值 ≥ 8/255 |

---

## 9. 检查表

- [ ] S0 基线绿，分支 `feat/open-world`
- [ ] S1.1 Spec / S1.2 Field / S1.3 瓦片 / S1.4 spawn+manifest
- [ ] S1.5 Go 测试 5/5；S1.6 一条命令；S1.7 Unity 读取；S1.8 M0 验收
- [ ] S2.1 索引 / S2.2 选层 / S2.3 网格 / S2.4 池 / S2.5 循环 / S2.6 相机 / S2.7 出图
- [ ] S2.8 1.5M 三角形、60 fps、无裂缝
- [ ] S3.1 状态机 / S3.2 异步 / S3.3 碰撞 / S3.4 水面 / S3.5 传送 / S3.6 测试 / S3.7 服务器
- [ ] S3.8 传送 3 s、p95 ≤ 2 ms
- [ ] S4.1 区域 / S4.2 POI / S4.3 补丁 / S4.4 散布 / S4.5 路线 / S4.6 材质 / S4.7 地标 / S4.8 校验
- [ ] S4.9 0 error、90 s 旅行、三种材质
- [ ] S5.1 基准 / S5.2 截图 / S5.3 CI / S5.4 文档 / S5.5 总验收
