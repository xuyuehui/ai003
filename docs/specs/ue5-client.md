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

- 视觉用引擎 BasicShapes（Cylinder + Cube）+ `/Game/AI003/M_Player*`，后续替换为骨骼网格。
- 输入走 legacy 映射（`Config/DefaultInput.ini`），不依赖 Enhanced Input 资产；
  `DefaultPlayerInputClass=/Script/Engine.PlayerInput`。

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
