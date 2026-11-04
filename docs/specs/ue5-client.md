# UE5 客户端规格

## 1. 基线

| 项 | 值 |
| --- | --- |
| 引擎 | Unreal Engine 5.8（CL 58210709） |
| 编译器 | VS2022 17.14 / MSVC 14.44.35214 / Windows SDK 10.0.19041 |
| 语言标准 | C++20；`BuildSettingsVersion.V7`、`EngineIncludeOrderVersion.Unreal5_8` |
| 插件 | ProceduralMeshComponent、PythonScriptPlugin（工具链）、EngineWire（MCP，第三方） |
| 模块 | `UE5GameClient`（Runtime），API 宏 `UE5GAMECLIENT_API` |

## 2. 目录

```
Source/UE5GameClient/
  World/WorldData.*            世界数据加载（json + f32/rgba8/r8）
  World/WorldTerrainActor.*    8×8 程序化网格 + 海面
  World/WorldCharacter.*       角色 / 相机 / 输入
  World/WorldGameMode.*        世界启动、光照、出生点、自动化开关
  SmokeGameInstance.*          联网冒烟（-AI003Smoke）
Config/DefaultEngine|Game|Input.ini
WorldData/                     生成物（generate-world）
Scripts/setup_assets.py        材质生成
Content/AI003/                 生成的材质
Plugins/EngineWire/            第三方 MCP 插件（不入库）
```

- 头文件与实现同目录；`PublicIncludePaths.Add(ModuleDirectory)` 已开启，include 写 `World/WorldData.h`。
- 新模块/插件依赖写进 `UE5GameClient.Build.cs` 与 `.uproject`。

## 3. 代码规范

- Unreal 命名：`A`（Actor）、`U`（UObject）、`F`（结构/非 UObject），布尔成员 `b` 前缀。
- UCLASS/USTRUCT 必须有 `GENERATED_BODY()`；`*.generated.h` 永远是最后一个 include。
- 反射可见成员用 `UPROPERTY()` 防 GC；组件引用用 `TObjectPtr<T>`。
- 禁止在构造函数里 `LoadObject` 资源，放 `BeginPlay` 或首次使用处。
- 日志：`UE_LOG(LogTemp, Display, TEXT("[AI003] ..."))`；可恢复错误用 Warning，失败用 Error。
- 单位：世界数据是米，UE 是厘米；数据层进出必须 ×100 / ÷100。
- Tick 只在必要时开启；能用事件/定时器就不用 Tick。

## 4. 世界

- `WorldGameMode::InitGame` 读 `WorldData/`，`StartPlay` 生成 8×8 PMC 分块、海面、太阳/天空/雾。
- 每块 65×65 顶点、复杂碰撞（`bUseComplexAsSimpleCollision=true`），地形不投影。
- 顶点色用 `CreateMeshSection_LinearColor(..., bSRGBConversion=false)`，与文件的线性值一致。
- 出生点走 `HandleStartingNewPlayer` + `RestartPlayerAtTransform`，不依赖 PlayerStart。
- 地图固定 `/Engine/Maps/Entry`，不额外维护 umap；环境在运行时生成。

## 5. 角色

| 参数 | 值 |
| --- | --- |
| 胶囊 | 半径 42 cm，半高 96 cm |
| 走 / 冲刺 | 600 / 1000 cm·s⁻¹ |
| 跳跃 | `JumpZVelocity=520` |
| 可行走坡度 | 48° |
| 相机 | SpringArm 650 cm，SocketOffset 80，相机滞后 12 |

- 视觉为骨骼网格 `/Game/Characters/KenneyProtagonists/characterMedium`（Kenney Protagonists，CC0），
  59 骨骼；动画 `/Game/Characters/KenneyProtagonists/Anims/{idle,jump,run}` 绑在同一 Skeleton 上。
- 网格相对变换：`Z=-96`、`Yaw=-90`、`Scale=0.45`（源模型 3.76 m、脚底为轴心，转到 1.7 m 并面向角色 +X）。
- 动画用单节点 `PlayAnimation`：着地按速度切 Idle/Run（Run 播放速率随速度 0.6–1.6），
  离地播 Jump 一次并保持末帧，落地自动切回；没有 AnimBlueprint 依赖。
- 材质 `/Game/AI003/M_PlayerSkin` 采样 `skaterMaleA` 皮肤贴图，必须带 `UsedWithSkeletalMesh` 标记
  （`Scripts/fix_material_flags.py` 负责，否则打包/`-game` 回退默认材质）。
- 输入走 legacy 映射（`Config/DefaultInput.ini`），不依赖 Enhanced Input 资产；
  `DefaultPlayerInputClass=/Script/Engine.PlayerInput`。

### 5.1 状态同步（协议 v2）

- `Net/NetSyncSubsystem.*`（`UGameInstanceSubsystem`）自动连接网关：
  - `join`（`client=ue5`）后 10 Hz 发 `move`；本地角色取 `UGameplayStatics::GetPlayerPawn`；
  - 每 50 ms tick 一次，断线 3 s 重连，`-AI003NoSync` 可整体关闭；
  - 用 `-AI003Url=ws://host:port/ws` 覆盖地址。
- 坐标换算：发送时 `x=UE.X/100`、`y=(UE.Z-96)/100`（脚底）、`z=UE.Y/100`；
  接收时反过来 `UE = (x, z, y) * 100`，并给胶囊中心补 96 cm。yaw 直接透传。
- `Net/RemotePlayer.*`：轻量 Actor（SceneComponent + SkeletalMesh），加载同一套
  Kenney 网格与 `Anims/*`，按服务器 `anim` 单节点播放；位置/朝向 12/s 插值。
- 状态同步与冒烟互不影响：`-AI003WorldSmoke`/`-AI003WorldShot` 不依赖服务器，
  服务器不在时子系统只打 warning 并重连。
- 已知限制：远端没有骨骼混合/位移匹配，突变位置靠插值追上；UE 侧水下仍是行走。

### 5.2 房间战斗（协议 v3）

- `NetSyncSubsystem` 内实现房间：`-AI003Room` 开启，连上后自动 `room.enter`，
  20 Hz 发 `room.input`（WASD/方向键 + 空格攻击）。
- 进房时 `DisableMovement()` 并把 `RoomCenter` 设成角色当前位置；之后按 `room.frame`
  直接 `SetActorLocation`（房间坐标 → `RoomCenter + (x,z)*100cm`）。
- 远端用 `ARemotePlayer` 显示，动画由帧间位移推导（run/walk/idle）。
- 世界快照在房间激活时忽略（`ApplySnapshot` 早退），避免同一玩家出现两个化身。
- HUD 用 `GEngine->AddOnScreenDebugMessage` 显示 `roomId/hp/tick/fighters`。
- 持久身份：`Saved/ClientId.txt`（首次 `FGuid` 生成），`join`/`room.enter` 携带；
  断线时 `bRoomActive=false`，重连后自动重发 `room.enter`，服务器按 `clientId` 恢复角色。
- 客户端预测：本地以 `RoomSpeedCm`/`RoomDiagCm` 模拟并保持 `ServerTick + 3` 领先；
  `PendingCommands`/`PredictedHistory` 保存每个 tick 的输入与位置，`room.frame` 到达后
  误差 >5 cm 时回滚到服务器位置并重放；HUD 显示 `pred` 与 `corr`。
- 调试：`-AI003RoomBot` 让客户端自动走向场地中心并周期性攻击（用于双端对战验证）。
- 已知限制：单连接只进一个房间（自动匹配），无预测/回滚，房间叠加在大世界出生点。

## 6. 材质

- `/Game/AI003` 下材质由 `Scripts/setup_assets.py` 通过 Python commandlet 生成，重跑会覆盖；
  要改材质请改脚本，不要只改资产。
- 5.8 的 `MaterialExpressionVertexColor` 输出引脚名是**空串**，连接必须传 `""`。
- 水面材质：translucent、two-sided、roughness 0.3、opacity 0.86。

## 7. 构建与验证

```powershell
& 'C:\Program Files\Epic Games\UE_5.8\Engine\Build\BatchFiles\Build.bat' `
    UE5GameClientEditor Win64 Development `
    "-Project=D:\works\ai003\clients\ue5\UE5GameClient\UE5GameClient.uproject" -WaitMutex

.\scripts\smoke-ue5.cmd          # 联网 ping
.\scripts\smoke-ue5-world.cmd    # 世界构建 / 落地 / 跑动
```

- 自动化开关：`-AI003Smoke`（联网）、`-AI003WorldSmoke`（世界）、`-AI003WorldShot`（截图）。
- 头less 运行必须带 `-nullrhi -nosplash -unattended -log`。
- 进程退出码用 `FPlatformMisc::RequestExitWithStatus` 给出；脚本仍以日志标记
  `[AI003][WORLD] PASS/FAIL` 判定。

## 8. 资产与版本控制

- 忽略 `Binaries/`、`Intermediate/`、`DerivedDataCache/`、`Saved/`、`Plugins/EngineWire/`
  （见 [.gitignore](../../clients/ue5/UE5GameClient/.gitignore)）。
- `Content/**.uasset`、`Config/*.ini`、`Source/**`、`WorldData/**` 提交。
- 当前无 Git LFS；单文件 >50 MB 的资源先讨论再入库。
- 引擎插件（EngineWire）用 `scripts/install-enginewire.ps1` 重建，不入库。

## 9. EngineAssociation

- 提交前保持 `"EngineAssociation": "5.8"` 便于移植；编辑器打开后可能改写为本机安装 GUID，
  提交时改回版本号。

## 10. MCP

- EngineWire 0.6.0，原生 MCP `http://127.0.0.1:3000/mcp`；本机回环关闭 capability token。
- 多人/非受信环境恢复 `bRequireCapabilityToken=True`，并给 DSH 配
  `MCP_AUTOMATION_CAPABILITY_TOKEN`。详见 [../mcp.md](../mcp.md)。

## 11. 完成定义

1. UBT 编译 0 error；
2. `smoke-ue5` 与 `smoke-ue5-world` PASS；
3. 新增能力有对应 `-AI003*` 冒烟开关或自动化测试；
4. 世界/角色参数与 Unity 端保持一致（见 [unity-client.md](unity-client.md)）。
