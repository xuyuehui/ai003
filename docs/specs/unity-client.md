# Unity 客户端规格

## 1. 基线

| 项 | 值 |
| --- | --- |
| 编辑器 | Unity 6000.6.6f1（Tech Stream） |
| 渲染 | URP 17.6.0，线性色彩空间 |
| 序列化 | Force Text（`m_SerializationMode: 2`），提交所有 `.meta` |
| 输入 | Input System 1.20.0（仅新输入，`activeInputHandler=1`） |
| JSON | Newtonsoft 3.2.2 |
| 测试 | Test Framework 1.8.0（builtin） |
| 脚本后端 | Mono；IL2CPP 模块未装，发行前补 |

## 2. 目录与程序集

```
Assets/Scripts/AI003.Runtime.asmdef    运行时（AI003.Runtime）
  Net/       GatewayClient / ConnectionBootstrap / Envelope
  World/     WorldData / WorldTerrain / WorldBootstrap
  Player/    ThirdPersonController / OrbitCamera
Assets/Editor/                         编辑器工具（Assembly-CSharp-Editor）
Assets/Tests/PlayMode/                 AI003.PlayModeTests
Assets/Shaders/                        AI003/TerrainVertexColor.shader
Assets/Scenes/OpenWorld.unity          生成物（SetupWorldScene）
Assets/StreamingAssets/world/          生成物（generate-world）
```

- 运行时脚本必须属于 `AI003.Runtime`；该程序集显式引用 `Unity.InputSystem`。
- 命名空间：`AI003.Net`、`AI003.World`、`AI003.Player`、`AI003.EditorTools`、`AI003.Tests`。
- 编辑器脚本不得进入运行时程序集；运行时脚本不得引用 `UnityEditor`。

## 3. 代码规范

- C# 9（Unity 默认），UTF-8、LF、4 空格。
- 私有字段 `_camelCase`，公开成员 PascalCase，常量 PascalCase。
- 每帧路径零分配：禁止 LINQ、字符串拼接、`GetComponent` 每帧调用。
- 日志统一 `[AI003]` 前缀，只在状态变化时打印。
- 跨线程规则：网络线程只入队，Unity API 只在主线程调用（见 `GatewayClient` 的队列设计）。
- 可测试性：输入通过 `UseExternalInput/ExternalInput` 注入，物理测试不依赖键盘。

## 4. 世界与渲染

- 数据：`world.json` + `heightmap.f32` + `colors.rgba8` + `biomes.r8`，米制、Y 向上、行优先。
- 地形：8×8 分块，每块 65×65 顶点；`MeshCollider` 非凸；法线由高度图中心差分。
- 顶点色是世界文件里的**线性**值，直接进 shader，不做二次 sRGB 转换。
- 地形材质 `AI003/TerrainVertexColor`（Forward + DepthOnly + DepthNormals + Fog + 阴影）；
  水用 URP/Lit 透明；角色用 URP/Lit。
- 水面：单四边形，边长 4×世界尺寸，位于 y = seaLevel。
- 光照：一盏方向光（软阴影）+ 天空盒环境光；线性雾 320–2200 m。
- 新增地形类型：改 Go 端调色板并重新生成，**不在客户端硬编码颜色**。

### 4.1 世界主题（WorldTheme）

- `Assets/Scripts/World/WorldTheme.cs`：`ScriptableObject`，持有 6 个 biome 色 + 3 个修正色，
  运行时把 `biomes.r8` + 高度 + 坡度重采样成顶点色（线性空间），3×3 平滑避免 biome 接缝。
- `WorldTerrain.Build(data, colorOverride, shaderName)` 是主题入口；不传时行为与旧版完全一致
  （读 `colors.rgba8`），所以**共享的 `world/` 数据与 UE 端不受影响**。
- 色值以 sRGB 记在主题里，写进顶点色前做 `pow(c, 2.2)`，与烘焙色板同一条路径。
- 现有 `WorldTheme.CreateAshkar()` 就是 `docs/worldview/03-art-direction.md` 的色板。

### 4.2 天空与双光

- `Assets/Shaders/AshkarSky.shader`：两色渐变 + 地平线尘带 + 两个太阳圆盘与光晕，不依赖贴图。
- 主光（赫斯）投影；第二盏方向光（维尔）**不投影**，作为暖色填充；
  地形着色器走 `_ADDITIONAL_LIGHTS` 循环，所以第二盏光能照到地形。
- `UniversalRenderPipelineAsset.shadowDistance` 在离屏渲染前临时调到 700 m（默认 50 m 只够角色）。

### 4.3 地形着色器（AshkarTerrain）

- `Assets/Shaders/AshkarTerrain.shader`：顶点色 + 世界空间沙纹法线（随距离淡出，避免摩尔纹）
  + 包裹漫反射 + 掠射镜面 + 附加光 + 雾；ShadowCaster / DepthOnly / DepthNormals 齐备。
- 默认 `AI003/TerrainVertexColor` 保持不变，`demo-compare` 的基线不受影响。

### 4.4 离屏剧照

- `AI003.EditorTools.AshkarStill.Run`：批处理一次渲染三张 `artifacts/ashkar/{s1_dawn,s2_dusk,s4_night}.png`（1920×804）。
- 机位自动搜索：内陆、高处、前方 200–600 m 是下坡、420 m 与 900 m 处仍是陆地（避免看向盐海）；
  夜景单独找"靠近盐海、地形平缓"的点。
- 光照/后期参数在 `AshkarStill.cs` 的 `Look` 结构与 `BuildProfile()`，改完重跑即可。
- **`artifacts/ashkar/` 只放最终件**：交付路径里的 `forest.png` / `steppe.png` 必须来自一次干净的
  GUI 渲染。任何诊断、对照、重渲、实验帧一律写到 `artifacts/_diag/<时间戳>/` 或其它临时目录，
  不许覆盖 `artifacts/ashkar/`。理由：验证的 watcher 只认这两个路径，一次误写就会把坏帧当成交付件
  （2026-11-14 发生过一次：诊断渲染把 `forest.png` 覆盖成品红版）。
- **剧照只能由带图形的编辑器渲染**（`Unity.exe -projectPath ... -executeMethod AI003.EditorTools.AshkarStill.Run`，
  不加 `-batchmode`）。`-batchmode`（即使不带 `-nographics`）下 URP/Lit 植被拿不到环境光：
  同机位、同代码下针叶树区域平均 luma 41.65（GUI）vs 7.24（batchmode），而 `AI003/AshkarTerrain`
  地形不受影响（同帧非植被像素 56.35 vs 56.31）。所以 batchmode 帧只能当几何/掩码口径用
  （`stills-vegmask.ps1` 的 UNLIT 差分），不能用于外观评审；外观结论一律看 GUI 渲染件。
  证据：`artifacts/_diag/forest-batchmode.png` vs `artifacts/snapshots/forest.delivered.png`。

### 4.4.-2 玩家是场景里的预制体实例

- 菜单 `AI003/Author Player` 通过运行时的同一个工厂（`WorldBootstrap.CreatePlayerObject`）
  造一份玩家，存成 `Assets/Prefabs/AshkarPlayer.prefab`，再在出生点实例化。
- Play 时 `WorldBootstrap` 优先复用场景里的 `ThirdPersonController`，只在没有时新建。
- 菜单 `AI003/Author Player Ground`：只把玩家的 **y** 放到地形表面 + 1.2 m（先取烘焙
  `[AI003] Terrain` 的碰撞体，取不到才回退 `WorldData.HeightAt`），x/z 与朝向不动、可重复执行。
  `AuthorOpenWorld` 在 `BakeTerrain` 之后自动跑它，所以换机器重跑作者菜单不会把玩家留在
  旧生成数据的地表以下（事故记录见 `artifacts/20261114-629-verdict.md`）。
- `Assets/Tests/PlayMode/AuthoredSceneTests.cs` 守这条约定：加载 `OpenWorld` 后必须
  只有 1 个玩家、1 台相机、1 个 bootstrap。

### 4.4.-1 主相机也在场景里

- 菜单 `AI003/Author Main Camera` 把 `Main Camera`（`Camera` + `OrbitCamera`，`MainCamera` 标签）
  写进场景，位置/朝向按出生点算好；它在编辑模式下的旋转就是开局视角。
- Play 时 `WorldBootstrap` 直接**复用**这台相机（把 `OrbitCamera.Target` 指向玩家），
  只有场景里没有相机时才新建。

### 4.4.0 地形也是静态的（烘焙进场景）

- 菜单 `AI003/Bake Terrain (static)` 把 64 块地形网格（1025²，2 m/顶点）写成
  `Assets/Generated/Terrain/*.asset`，`[AI003] Terrain` 作为普通场景物体存在，
  并给每个 chunk 打 Static 标记；`WorldBootstrap._buildTerrain` 置为 false。
- Play 时只生成玩家与相机，**不再生成地形**。
- 烘焙产物约 97 MB，放在 `Assets/Generated/` 且**不进版本库**；换机器后跑一次该菜单即可。
  若网格缺失，`WorldBootstrap` 会自动回退到运行时构建（不会白屏）。

### 4.4.1 场景元素靠编辑器摆放，不靠运行时生成

- 地标（第一井、井塔、帆撬、人形）**不在 Play 时生成**：菜单 `AI003/Author Ashkar Scene`
  把它们一次性摆进**当前打开的场景**（`[AI003] Dressing` 根节点下），网格存成
  `Assets/Generated/Dressing/*.asset` 并打 Static 标记；之后在编辑器里随便挪、删、改，保存场景即可。
- `WorldBootstrap` 只负责数据、地形（或烘焙场景）与玩家，**不再调用任何摆位代码**。
- 摆放位置以 `world.json` 的出生点为基准，只在首次生成时用；后续以场景文件为准。

### 4.5 程序化地标与载具

- `Assets/Scripts/World/AshkarProps.cs`：井塔（倒锥 + 平台 + 深窗）、第一井（下沉广场 + 钟架）、
  帆撬（三滑板 + 桅杆 + 帆 + 货斗）、人形剪影（锥体 + 头）、冷火晶簇。全部代码生成，无外部模型。
- `Assets/Scripts/World/SailSled.cs`：三滑板物理 —— 各向异性摩擦（侧向 3.2、前向 0.35 阻力）、
  风推力、随速度增长的转向扭矩、贴合坡面；滑板用 0.02 摩擦的 `PhysicsMaterial`，避免碰撞体自身"抓沙"。
- `Rigidbody.sleepThreshold = 0`：风驱动的刚体不能睡着，否则 `AddForce` 被静默忽略。
- PlayMode：`SailSledTests` 在 (4000,0,4000) 的独立平面上验证加速 > 4 m、侧滑 < 40%、转向 > 12°。

## 5. 角色与物理

| 参数 | 值 |
| --- | --- |
| 胶囊 | 高 1.8 m，半径 0.35 m |
| 走 / 冲刺 / 游泳 | 6 / 10 / 3.2 m·s⁻¹ |
| 跳跃 | 1.35 m |
| 重力 | -24 m·s⁻² |
| 可行走坡度 / 台阶 | 48° / 0.45 m |
| 相机 | 第三人称环绕，距离 6.5 m，俯仰 -30..70°，地形遮挡收缩 |

- 深水（水深 >1.6 m 且低于水面）切游泳浮力，目标水面下 0.85 m。
- 参数只允许在角色/相机组件上调，不要在 `Update` 里写魔法数。
- 角色视觉：`Assets/Resources/Characters/KenneyProtagonists/` 下的 Kenney Protagonists（CC0）
  骨骼模型 + `Idle` / `Run` / `Jump` 动画，由 `Assets/Editor/SetupPlayerCharacter.cs` 生成：
  - `Player.prefab`（带 Avatar、Animator、`M_Player` 材质，缩放 0.45 面向 +Z）
  - `PlayerAnimator.controller`（`Speed` / `Grounded` 参数，Idle↔Run↔Jump）
  - `Idle/Run/Jump.anim`：从 FBX 复制的“净化版” clip——去掉根节点位移/旋转/缩放曲线，
    并把骨骼路径补上 `Root/` 前缀（否则 root 缩放 100× 会覆盖 prefab 缩放、曲线也绑不上骨骼）
- `PlayerAnimation` 把实际水平速度归一化到 `Speed`、把 `IsGrounded` 写进 `Grounded`；
  缺 prefab 时 `WorldBootstrap` 回退到胶囊，不会黑屏。
- 生成步骤：Unity 菜单/编辑器里执行 `AI003.EditorTools.SetupPlayerCharacter.Run()`；
  改模型导入设置或动画后需重跑一次。

## 6. 网络

- 协议见 [protocol/README.md](../../protocol/README.md)，当前 v1（`welcome`/`ping`/`pong`/`error`）。
- `GatewayClient`：后台线程收发 + `ConcurrentQueue` 主线程消费；连接超时 10 s。
- `ConnectionBootstrap`：场景加载前自动挂载，默认 `ws://127.0.0.1:8080/ws`，
  2 s 心跳、3 s 重连；地址可用 `AI003_WS_URL` 覆盖。
- `ConnectionBootstrap` 把每一帧同时放进 `TryDequeueGameplay` 队列（上限 1024），
  玩法代码在主线程排空；不要用事件订阅做同步，域重载会丢静态与订阅。
- `WorldSyncClient`（自动挂载）：
  - 连接后发 `join`（`client=unity`），10 Hz 发 `move`（脚底位置、yaw、速度、grounded、swimming）；
  - 收到 `snapshot` 后为除自己外的玩家生成/更新 `RemoteAvatar`，断线时清空；
  - 左下角 HUD 显示 `id / players / remote / ping`。
- `RemoteAvatar`：位置与朝向按 12/s 指数平滑插值，按服务器 `anim` 驱动 Animator
  （walk 用 Run 以 0.7 倍速播放，jump 切空中状态）。
- 新增报文必须同步 Go 与 UE，并提升 `gateway.ProtocolVersion`。

### 6.1 房间战斗（协议 v3）

- `RoomClient`（自动挂载，`AI003_ROOM=1` 才启用，避免影响探索与测试）：
  - 连上后 `room.enter`，20 Hz 发 `room.input`（WASD/方向键 + 空格攻击）；
  - 收 `room.frame` 后按服务器状态驱动本地角色与远端 `RemoteAvatar`，位置/朝向按 20/s 插值；
  - 进入房间时 `WorldSyncClient.Suspend()`（隐藏大世界远端、停止上报 move），离开时 `Resume()`；
  - HUD：血条 + `hp/tick/fighters` + 命中/死亡/复活提示；`RoomClient.AnyActive` 让
    `WorldBootstrap`/`WorldSyncClient` 隐藏自己的 HUD。
- 模拟常量全部来自 `room.entered`，不要在客户端写死速度/伤害/冷却。
- 房间号来自 `room.entered.roomId`（服务器自动匹配或按 `room` 指定），HUD 显示；
  满房/错误只会打日志，客户端每秒重试进房。
- 持久身份 `ClientIdentity.Id`（`PlayerPrefs: AI003.ClientId`，首次生成 GUID），
  `join` 与 `room.enter` 都会带上；断线时 `RoomClient` 不销毁战斗状态，重连后自动重发
  `room.enter`，服务器按 `clientId` 恢复原角色（30 s 宽限）。
- 客户端预测：本地按 `speedMMPerTick`/`diagonalMMPerTick` 的整数规则模拟，保持
  `serverTick + 3` 的领先量（掉帧时补 tick）；每个 tick 的输入与预测位置分别存在
  `_pending` / `_predicted`，`room.frame` 到达后比较该 tick 的预测位置，误差 >50 mm 时
  回到服务器位置并重放未确认输入。HUD 显示 `pred`/`corr`/回滚误差。
- 测试调试：`RoomClient.DebugSendInput(dx, dz, attack)`、`DebugHold(dx, dz, attack, seconds)`
  直接驱动预测输入；`ConnectionBootstrap.DebugDisconnect()` 主动断开以验证重连。
- 战斗表现：进房时生成 `[AI003] Arena`（4 面 0.2 m 厚、±20 m 的黄色围栏，无碰撞）；
  `attack` 事件驱动本地/远端前冲（0.35 m 正弦位移）、`hit` 触发受击闪红
  （MaterialPropertyBlock `_BaseColor`）、伤害数字（世界坐标转屏幕、上浮 1 s）、
  本地受击红色暗角，`death` 让远端倒地变灰、`respawn` 恢复；HUD 显示回滚误差。
- 已知限制：竞技场叠加在出生点草地上，本地移动为服务器帧回放不是预测。

## 7. 编辑器自动化与测试

批处理入口（Unity 是 GUI 子系统，脚本必须 `Start-Process` 等待进程退出）：

| 方法 | 作用 |
| --- | --- |
| `AI003.EditorTools.SetupWorldScene.Run` | 生成 `OpenWorld.unity` 并加入 Build Settings |
| `AI003.EditorTools.WorldSmokeTest.Run` | 校验世界数据与分块网格 |
| `AI003.EditorTools.RenderWorldPreview.Run` | 批处理模式离屏渲染截图 |
| `AI003.EditorTools.CaptureDemoShot.Run` | 固定机位对照帧（ACES 卷 + 程序化天空）→ `artifacts/demo/unity.png` |
| `AI003.EditorTools.CaptureRoom.Run` | 进房、等 2 名玩家且预测追平（`pred >= server`）后截图 → `artifacts/demo/room-unity.png` |
| `AI003.EditorTools.EnableUnityMcp.Run` | 打开 MCP 自动启动（stdio 桥 6400） |
| `AI003.EditorTools.AuthorPlayerGround.RunBatch` | 把场景里的玩家落到地形表面（幂等，仅改 y） |

- PlayMode 测试：`ConnectionBootstrapTests`、`WorldPlayModeTests`（落地 + 跑动）。
- 改世界或角色物理后，至少跑 `scripts/test-unity-playmode.cmd`。

## 8. 资产与版本控制

- 提交所有 `.meta`；忽略 `Library/`、`Temp/`、`Build/`、`Logs/`、`UserSettings/` 等
  （见 [.gitignore](../../clients/unity/UnityGameClient/.gitignore)）。
- `OpenWorld.unity` 与 `StreamingAssets/world/` 是生成物：可提交，不要手改。
- 新增第三方包写进 `Packages/manifest.json`，并提交 `packages-lock.json`。

## 9. MCP

- 包 `com.coplaydev.unity-mcp@10.3.0`，stdio 桥监听 `127.0.0.1:6400`。
- 自动启动由 EditorPrefs 控制：`MCPForUnity.AutoStartOnLoad=true`、
  `MCPForUnity.UseHttpTransport=false`。
- 不要切 HTTP 模式：8080 是 Go 网关端口。详见 [../mcp.md](../mcp.md)。

## 10. 完成定义

1. 编辑器 0 编译错误、0 着色器错误；
2. PlayMode 测试通过；
3. `smoke-all` 的 `unity` / `unity-pm` 项 PASS；
4. 场景内容全部 Author 在 `OpenWorld.unity` 里，Play 不生成世界（见 §4.4）。
