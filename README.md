# AI003

同一套效果，三套实现：

- **Unity 6（URP）客户端** — `clients/unity/UnityGameClient`
- **Unreal Engine 5 客户端** — `clients/ue5/UE5GameClient`
- **Go 服务器** — `server`

三端通过同一条 WebSocket JSON 协议通信（见 [protocol/README.md](protocol/README.md)）。

## 当前进度

**里程碑 1：开放世界地形 + 角色跑动**（已完成）

- Go 端确定性生成 2048 m × 2048 m 世界：海洋、沙滩、草地、沙漠、岩石、雪顶山峰，
  俯视图见 [world/preview.png](world/preview.png)
- 两端加载**同一份数据**（高度图 / 顶点色 / 生物群系 / 出生点），地形逐顶点一致
- 角色可在世界中跑动：重力、地形碰撞、48° 斜坡、台阶、跳跃、第三人称相机；
  Unity 侧额外支持入水游泳（UE 侧的水面交互待补）
- 角色视觉为 **Kenney Animated Characters: Protagonists**（CC0）骨骼模型 + `Idle` / `Run` / `Jump`
  三段动画，两端加载同一套 FBX 与皮肤贴图，见 [assets/characters/kenney-protagonists](assets/characters/kenney-protagonists/SOURCE.md)
- 支持头less 自动化验证：Unity 数据/网格校验 + PlayMode 物理测试，UE `-game -nullrhi` 冒烟

**里程碑 2：服务器权威状态同步**（已完成基础版）

- 协议 **v2**：`join` / `joined` / `move` / `snapshot`，服务器保存每个玩家的权威位置、朝向、
  速度与状态，按 **10 Hz** 广播快照，见 [protocol/README.md](protocol/README.md)
- 服务器做边界与非法值清洗（位置夹到 ±1024 m、高度 −200..600 m、NaN 归零），
  动画状态（idle/walk/run/jump/swim）也由服务器派生，保证两端表现一致
- Unity / UE 各自 10 Hz 上报本地角色，收到快照后用 `RemoteAvatar`（Unity）与
  `ARemotePlayer`（UE）平滑插值渲染他人；坐标统一为"脚底 + 米 + Unity 轴序"
- 已验证：同时开 Unity Play Mode 与 UE `-game`，两端都能看到对方角色并互相同步位置

| Unity：两个客户端同屏 | UE5：同一场景的远端角色 |
| --- | --- |
| ![Unity sync](docs/images/sync-two-clients.png) | ![UE5 sync](docs/images/sync-ue5.png) |

**里程碑 3：房间副本 + 帧同步战斗**（基础版已完成）

- 房间由服务器固定 **20 Hz** tick 驱动，位置/速度/战斗全部用**整数毫米**，Go/C#/C++ 结果一致
- 多房间：容量 8，`room.enter` 省略房间号即**自动匹配**（人数最少且有位，满则新建），
  `room.list` 返回房间列表；角色间有 400 mm 半径的整数分离，不会重叠站位
- 输入帧广播 `room.input` → `room.frame`；近战规则：3 m 范围、±60° 锥角、25 伤害、
  1 s 冷却、100 HP、死亡 3 s 后原地满血复活
- Unity 用 `RoomClient`（`AI003_ROOM=1` 开启）、UE 用 `NetSyncSubsystem`（`-AI003Room` 开启），
  两端都按服务器下发的常量与帧进行模拟，HUD 显示血量/帧号/人数
- 实测：Unity 与 UE 机器人同房对战，血量 100 → 75 → …，命中/死亡事件两端一致

| Unity：房间战斗 | UE5：同一房间 |
| --- | --- |
| ![Unity room](docs/images/room-combat-unity.png) | ![UE5 room](docs/images/room-combat-ue5.png) |

| Unity 出生点视角 | UE5 出生点视角 | 世界俯视 |
| --- | --- | --- |
| ![Unity](docs/images/world-unity.png) | ![UE5](docs/images/world-ue5.png) | ![overview](docs/images/world-overview.png) |

| Unity 角色（Idle） | UE5 角色（Idle） |
| --- | --- |
| ![Unity character](docs/images/character-unity.png) | ![UE5 character](docs/images/character-ue5.png) |

> 上图是开发期渲染产物，用 `scripts/smoke-unity.cmd` / `scripts/smoke-ue5-world.cmd` 重新生成。
> 两端光照/材质仍在打磨，见"已知差异"。

此前的环境骨架（WebSocket `ping/pong`）仍然保留并通过测试：

- Unity 批处理模式（编辑器内 C# 联网）→ Go
- Unity **Play Mode**（`ConnectionBootstrap` 自动挂载，等同按下 Play）→ Go
- UE5 C++（`SmokeGameInstance`，头less `-game`）→ Go

## 目录结构

```
ai003/
├─ server/                     Go 模块（module ai003/server）
│  ├─ cmd/server/              网关进程：/healthz + /ws
│  ├─ cmd/pingclient/          命令行冒烟客户端
│  ├─ cmd/worldgen/            世界生成器（导出 world/ 数据）
│  └─ internal/gateway, worldgen   网关与世界生成 + 单元测试
├─ world/                      生成的世界数据（world.json + 二进制数组 + preview.png）
├─ clients/unity/UnityGameClient/   Unity 6 工程（URP 17.6）
│  ├─ Assets/Scripts/Net/      GatewayClient / ConnectionBootstrap
│  ├─ Assets/Scripts/World/    WorldData / WorldTerrain / WorldBootstrap
│  ├─ Assets/Scripts/Player/   ThirdPersonController / OrbitCamera
│  ├─ Assets/Editor/           建场景、世界冒烟、离屏渲染截图
│  └─ Assets/Tests/PlayMode/   联网 + 世界/角色物理测试
├─ clients/ue5/UE5GameClient/  UE 5.8 C++ 工程
│  ├─ Source/UE5GameClient/World/   WorldData / WorldTerrainActor / WorldCharacter / WorldGameMode
│  ├─ Scripts/setup_assets.py       生成 /Game/AI003 材质（Python commandlet）
│  └─ WorldData/                    世界数据副本
├─ protocol/                   三端共享报文定义
├─ scripts/                    PowerShell 构建 / 生成 / 冒烟脚本
└─ docs/environment.md         本机工具链实测记录
```

## 环境要求

| 组件 | 本机版本 | 路径 |
| --- | --- | --- |
| Go | 1.26.3 | `go` 在 PATH |
| Unity | 6000.6.6f1（URP 17.6.0） | `C:\Program Files\Unity 6000.6.6f1` |
| Unreal Engine | 5.8 | `C:\Program Files\Epic Games\UE_5.8` |
| Visual Studio | 2022 17.14 + MSVC 14.44 | C++ 桌面工作负载 |

细节（SDK、许可证、已知限制）见 [docs/environment.md](docs/environment.md)。

## 工程规格

每端有独立的实现规格与忽略规则，改代码前先看对应文档：

| 部分 | 规格 | 忽略文件 |
| --- | --- | --- |
| 仓库级约定 | [docs/specs/README.md](docs/specs/README.md) | [.gitignore](.gitignore) |
| Go 服务器 | [docs/specs/go-server.md](docs/specs/go-server.md) | [server/.gitignore](server/.gitignore) |
| Unity 客户端 | [docs/specs/unity-client.md](docs/specs/unity-client.md) | [UnityGameClient/.gitignore](clients/unity/UnityGameClient/.gitignore) |
| UE5 客户端 | [docs/specs/ue5-client.md](docs/specs/ue5-client.md) | [UE5GameClient/.gitignore](clients/ue5/UE5GameClient/.gitignore) |

## 世界数据与两端一致性

世界不是两端各自用噪声生成的，而是 Go 一次性烘焙成数组，两端只负责渲染：

| 文件 | 内容 |
| --- | --- |
| `world/world.json` | 元数据：seed、尺寸、分辨率、海平面、出生点、文件名 |
| `world/heightmap.f32` | 513×513 的 float32（小端、行优先，单位米），Y 轴向上 |
| `world/colors.rgba8` | 每顶点线性空间 RGBA，两端直接做为顶点色使用 |
| `world/biomes.r8` | 每顶点生物群系 id（海洋/沙滩/草地/沙漠/岩石/雪），用于调试与 HUD |
| `world/preview.png` | 俯视山体阴影图，便于不启动引擎时检查世界 |

- 当前世界：seed `20261103`，2048 m × 2048 m，4 m/格，海平面 0 m，最高峰约 396 m；
  海洋 42%、草地 34%、沙漠 7%、岩石 12%、雪 2%
- 出生点由生成器挑选：海平面以上、坡度 < 15°、半径 30 m 内全是干燥陆地
- 两端都把米换算成自己的单位（Unity 米 / UE 厘米），并直接读取同一份顶点色，
  **不做第二次 sRGB 解码**（UE 用 `CreateMeshSection_LinearColor(..., bSRGBConversion=false)`）

```powershell
.\scripts\generate-world.cmd      # 重新生成，并复制到 Unity StreamingAssets 与 UE WorldData
```

UE 的地形/水面/角色材质由 Python 在编辑器里生成一次（`/Game/AI003`）：

```powershell
.\scripts\setup-ue-assets.cmd
```

## 引擎 MCP

两个编辑器都已接好 MCP，AI 可以直接操作编辑器：

| 引擎 | 服务 | 端口 | DSH server |
| --- | --- | --- | --- |
| Unreal Engine 5.8 | EngineWire 0.6.0（插件 + node 代理） | `3000` | `unreal-engine` |
| Unity 6000.6.6f1 | MCP for Unity 10.3.0（UPM 包 + Python 服务） | `6400` | `unity` |

```powershell
.\scripts\check-mcp.cmd               # 检查两个桥是否在监听
.\scripts\install-enginewire.cmd -Build   # 新机器：装 UE 插件并编译
```

安装细节、DSH 配置片段、token/超时/端口冲突的排查见 [docs/mcp.md](docs/mcp.md)。

## 操作方式

| 操作 | 按键 |
| --- | --- |
| 移动 | `W A S D` |
| 视角 | 鼠标（Esc 释放鼠标，左键重新锁定） |
| 冲刺 | `Shift` |
| 跳跃 | `Space` |

Unity 与 UE 的移动速度、重力、跳跃高度、斜坡限制都在各自角色类里，参数已尽量对齐：

| 参数 | Unity | UE |
| --- | --- | --- |
| 走路 / 冲刺 | 6 / 10 m·s⁻¹ | 600 / 1000 cm·s⁻¹ |
| 跳跃高度 | 1.35 m | 约 1.4 m（JumpZVelocity 520） |
| 重力 | -24 m·s⁻² | 默认 980 cm·s⁻² |
| 可行走坡度 | 48° | 48° |

## 快速开始

### 0. 生成世界数据（首次或改 seed 后）

```powershell
.\scripts\generate-world.cmd
.\scripts\setup-ue-assets.cmd     # 仅 UE 首次需要：生成 /Game/AI003 材质
```

### 1. 启动服务器

```powershell
.\scripts\build-server.cmd
.\server\bin\ai003-server.exe            # 监听 127.0.0.1:8080
```

健康检查与命令行冒烟：

```powershell
Invoke-RestMethod http://127.0.0.1:8080/healthz
cd server; go run ./cmd/pingclient -n 3  # 期望 SMOKE OK
```

### 2. Unity 客户端

用 Unity 6000.6.6f1 打开 `clients/unity/UnityGameClient`，打开
`Assets/Scenes/OpenWorld.unity`（`SetupWorldScene` 已生成并加入 Build Settings），进入 Play Mode。

`WorldBootstrap` 会在场景加载后自动建地形、水、太阳、角色与相机，因此在**任意场景**
按 Play 也能直接进世界。`ConnectionBootstrap` 同时自动挂载并连接网关，Console 里可见：

```
[AI003] world ready: 2048 m, 513x513 vertices, 64 chunks, spawn (168, 40, -8)
[AI003] connected to ws://127.0.0.1:8080/ws
[AI003] pong seq=1 rtt=1ms
```

改地址：环境变量 `AI003_WS_URL`，或场景里改组件字段。

### 3. UE5 客户端

```powershell
.\scripts\smoke-ue5.cmd                 # 编译 Editor 目标 + 头less 冒烟
# 或者只编译：
& 'C:\Program Files\Epic Games\UE_5.8\Engine\Build\BatchFiles\Build.bat' `
    UE5GameClientEditor Win64 Development `
    -Project="$PWD\clients\ue5\UE5GameClient\UE5GameClient.uproject" -WaitMutex
```

然后双击 `UE5GameClient.uproject` 打开编辑器，点 Play 即可进入同一个世界：
`WorldGameMode` 会加载 `WorldData/`、生成 64 块地形与水面、布置太阳/天空/雾，并把角色
放在出生点。只想验证连通性时用 `-AI003Smoke`（`SmokeGameInstance` 的 ping 流程）。

### 4. 一键验证

```powershell
.\scripts\smoke-all.cmd            # Go 测试 + pingclient + Unity + PlayMode + UE5
.\scripts\smoke-all.cmd -SkipUE    # 只验 Unity 侧
```

脚本会自己拉起/关闭临时网关，最后打印汇总表。最近一次实测（2026-11-03）：

```
go-tests   PASS
go-client  PASS
unity      PASS
unity-pm   PASS
ue5        PASS
ue5-world  PASS
```

`unity-pm` 里包含两条 PlayMode 测试：`ConnectionBootstrap` 自动连接、以及世界构建 +
角色落地 + 向前跑动的物理测试。`ue5-world` 是 UE 的 `-AI003WorldSmoke`（头less）。

## 脚本

| 脚本 | 作用 |
| --- | --- |
| `scripts/generate-world.ps1` | 运行 `worldgen` 并分发世界数据到两端 |
| `scripts/setup-ue-assets.ps1` | 用 Python commandlet 生成 `/Game/AI003` 材质（UE 首次） |
| `scripts/build-server.ps1` | `go build` + `go vet` + `go test`，产物 `server/bin/ai003-server.exe` |
| `scripts/smoke-unity.ps1` | Unity 批处理模式跑 `AI003.EditorTools.SmokeTest.Run` |
| `scripts/test-unity-playmode.ps1` | 跑 Unity PlayMode 测试（联网 + 世界/角色物理） |
| `scripts/smoke-ue5.ps1` | UBT 编译 + `UnrealEditor-Cmd -game -nullrhi -AI003Smoke` |
| `scripts/smoke-ue5-world.ps1` | UBT 编译 + `-AI003WorldSmoke`（地形构建 / 落地 / 跑动） |
| `scripts/install-enginewire.ps1` | 下载并启用 EngineWire（UE MCP 插件），可选编译 |
| `scripts/check-mcp.ps1` | 检查 UE 3000 / Unity 6400 两个 MCP 桥 |
| `scripts/mcp-probe.mjs` | 最小 MCP stdio 客户端，用于列出某服务的工具 |
| `scripts/smoke-all.ps1` | 上述全部串起来，带超时与汇总 |

每个 `.ps1` 旁边都有同名 `.cmd`，双击或 `cmd /c` 调用即可（本机 PowerShell 执行策略为
Restricted，`.cmd` 会自动带 `-ExecutionPolicy Bypass`）。

日志与测试结果统一写到 `artifacts/`（已被 git 忽略）。

## 端口

| 用途 | 地址 |
| --- | --- |
| HTTP 健康检查 | `http://127.0.0.1:8080/healthz` |
| WebSocket 网关 | `ws://127.0.0.1:8080/ws` |

改端口：`.\server\bin\ai003-server.exe -addr 0.0.0.0:9000`，客户端用 `AI003_WS_URL` 跟随。

## 已知差异 / 待补

| 项 | 现状 | 计划 |
| --- | --- | --- |
| 断线重连 | 玩家 id 就是会话 id，断线即离房，重连从零开始 | 客户端持久 id + 宽限期保留状态 + 重连续接 |
| 战斗判定 | 服务器驱动帧同步，无客户端预测/回滚 | 输入延迟补偿、回滚或状态校正 |
| 竞技场表现 | 直接叠加在出生点草地上，无边界/特效 | 独立场景、围栏、受击/死亡表现 |
| 客户端预测 | 本地角色仍是“自己算、再上报”，服务器不回滚不纠偏 | 加输入序号 + 服务器校正 + 预测回滚 |
| 远端动画 | 用服务器派生的 anim 名称做单节点切换，无混合、无位移匹配 | 状态机/混合树、脚步同步 |
| UE 光照观感 | 色调偏灰、角色背光面偏暗，天空/雾还在调 | 对齐 Unity 的曝光与补光，必要时加后处理体积 |
| 水下物理 | Unity 有游泳浮力，UE 仍是"水下行走" | UE 补 `MOVE_Swimming` 或自定义浮力 |
| 地形精度 | 4 m 网格、平滑着色，近看缺少细节 | 加贴图/法线细节或提高分辨率 + LOD |
| 世界加载 | 一次性建 64 块，2 km 内全量 | 流式加载 / 分块 LOD，支撑更大世界 |

## 接下来

按需求逐个实现，建议顺序：

1. 房间系统完善：多房间、匹配、容量、断线重连；
2. 战斗表现与判定：角色碰撞、受击/死亡动画、技能与冷却、输入延迟补偿；
3. 状态同步增强：客户端预测/纠偏、速度与朝向插值、断线重连后的续接；
4. 画面统一：同一机位、同一光照、同一后处理的双端对比场景；
5. 世界扩展：流式地形、植被/建筑、可交互物；
6. 自动化：`smoke-all` 接入 CI，增加性能与画面回归。
