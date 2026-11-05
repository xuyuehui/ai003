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
- ECS 层（`Assets/Scripts/Ecs/`，asmdef `AI003.Ecs`，引用 `Unity.Entities`/`Unity.Mathematics`/
  `Unity.Collections`/`Unity.Transforms`）：组件 `FighterState`、`LocalFighterTag`、`RemoteFighterTag`、
  `FighterView`、`FighterAnim`；系统 `FighterViewSystem`（`SimulationSystemGroup`）负责远端插值、
  前冲、受击闪红、倒地与 Transform/Animator 写回。
- ECS 系统必须是 unmanaged struct；托管对象（如 `MaterialPropertyBlock`）放 static 字段。
- 桥接：`RoomEcsBridge` 把房间/世界快照转换为实体；网络层不直接写 Transform。
- 边界：网络 I/O、地形、角色物理仍为 MonoBehaviour；预测整数步进暂留 `RoomClient`。
- 测试：`FighterEcsTests.LocalEntityDrivesTransform`（PlayMode）断言实体位置写回 Transform。

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
4. 角色参数与 UE 端保持一致（见 [ue5-client.md](ue5-client.md)）。
