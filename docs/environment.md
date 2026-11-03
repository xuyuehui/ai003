# 开发环境说明（本机实测）

记录本机实际使用的版本、路径和验证结果，便于以后复现或在另一台机器上重建。

## 硬件 / 系统

| 项 | 值 |
| --- | --- |
| 系统 | Windows 10 家庭版 19045（64 位） |
| CPU | Intel Core i7-6700K（4 核 8 线程） |
| 内存 | 32 GB |
| GPU | NVIDIA GeForce RTX 5060，驱动 32.0.15.8157 |
| 磁盘 | C: 剩余约 1.2 TB，D: 剩余约 1.1 TB |

> 4 核 CPU 是 UE5 首次编译的主要瓶颈（本工程首次编译约 3 分钟），日常增量编译可接受。

## 三端工具链

### Go 服务器

| 项 | 值 |
| --- | --- |
| Go | go1.26.3 windows/amd64 |
| GOPATH / GOMODCACHE | `C:\Users\xuyuehui\go` / `C:\Users\xuyuehui\go\pkg\mod` |
| 模块名 | `ai003/server` |
| 依赖 | `github.com/gorilla/websocket v1.5.3`（已写入 `go.sum`） |
| 监听 | `127.0.0.1:8080`（可用 `-addr` 或 `AI003_ADDR` 覆盖） |

### Unity 客户端

| 项 | 值 |
| --- | --- |
| 编辑器 | Unity 6000.6.6f1（`C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe`） |
| 安装方式 | 手工安装目录，Unity Hub 3.3.7 已识别为 manual 安装 |
| 渲染管线 | URP 17.6.0（来自官方 `com.unity.template.urp-blank-17.2.6` 模板） |
| JSON 库 | `com.unity.nuget.newtonsoft-json`（清单写 3.2.1，实际解析为 3.2.2） |
| 脚本后端 | Mono（默认；未安装 IL2CPP 模块，需要时在 Hub 里补） |
| 许可证 | Unity Personal ULF，`C:\ProgramData\Unity\Unity_lic.ulf`，有效期至 2026-11-13 |

> 工程目录：`clients/unity/UnityGameClient`。首次打开会下载并解析包（约 20 分钟以内，
> 视网络而定），之后启动很快。

### UE5 客户端

| 项 | 值 |
| --- | --- |
| 引擎 | Unreal Engine 5.8（`++UE5+Release-5.8-CL-58210709`） |
| 路径 | `C:\Program Files\Epic Games\UE_5.8`（29.9 GB，含模板与源码） |
| 工程 | `clients/ue5/UE5GameClient/UE5GameClient.uproject` |
| 模块 | `UE5GameClient`（Runtime），依赖 `WebSockets`、`Json`、`JsonUtilities` |
| 目标 | `UE5GameClientEditor` Win64 Development |
| Target 设置 | `BuildSettingsVersion.V7`、`EngineIncludeOrderVersion.Unreal5_8` |

### Visual Studio / 编译器

| 项 | 值 |
| --- | --- |
| Visual Studio | Community 2022 17.14.11（`C:\Program Files\Microsoft Visual Studio\2022\Community`） |
| MSVC 工具集 | 14.44.35214（目录 `VC\Tools\MSVC\14.44.35207`） |
| Windows SDK | 10.0.19041.0 |
| 其他 | .NET SDK 10.0.201、Node 24.13.0、Python 3.12.0 |

UE 5.8 的要求：Windows SDK ≥ 10.0.19041（首选 10.0.22621），MSVC ≥ 14.38。
本机 SDK 为最低支持版本、MSVC 14.44.35214 在首选区间内，**实测编译通过**。

## 已验证的链路

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| Go 单元测试 | `go test ./...` | PASS |
| Go 世界生成测试 | `go test ./internal/worldgen` | PASS（确定性、全部群系、出生点干燥平坦） |
| Go 健康检查 | `GET /healthz` | `{"status":"ok",...}` |
| Go ping 客户端 | `go run ./cmd/pingclient` | 3/3 pong，SMOKE OK |
| Unity 批处理冒烟 | `scripts/smoke-unity.ps1` | 3/3 pong，`[AI003][SMOKE] PASS` |
| Unity 世界数据 + 网格校验 | `AI003.EditorTools.WorldSmokeTest.Run` | PASS（513² 顶点、64 块、群系齐全） |
| Unity PlayMode 测试 | `scripts/test-unity-playmode.ps1` | 2/2 passed（连接 + 世界落地/跑动） |
| UE5 头less 联网冒烟 | `scripts/smoke-ue5.ps1` | 3/3 pong，`[AI003][SMOKE] PASS` |
| UE5 头less 世界冒烟 | `scripts/smoke-ue5-world.ps1` | PASS（落地 96 cm，1.5 s 跑 653 cm） |

## 实测耗时（本机，4 核）

| 操作 | 首次 | 之后 |
| --- | --- | --- |
| Go 构建 + 测试 | < 5 s | < 3 s |
| Unity 首次打开/导入包 | 约 9 分钟 | — |
| Unity 批处理冒烟 | 约 60 s | 约 45 s |
| Unity PlayMode 测试 | 约 60 s | 约 45 s |
| UE 首次编译（生成 makefile + UHT + 链接） | 84 s | < 2 s（Target is up to date） |
| UE 头less 冒烟运行 | 约 20 s | 约 20 s |

## 开放世界数据与 UE 材质管线

**世界数据**（`world/`，由 `server/cmd/worldgen` 生成，两端只读）

| 文件 | 大小 | 格式 |
| --- | --- | --- |
| `world.json` | 436 B | seed / 尺寸 / 分辨率 / 海平面 / 出生点 |
| `heightmap.f32` | 1,052,676 B | 513×513 float32 小端，行优先，单位米 |
| `colors.rgba8` | 1,052,676 B | 每顶点线性 RGBA |
| `biomes.r8` | 263,169 B | 每顶点群系 id |
| `preview.png` | ~236 KB | 俯视山体阴影图 |

分发：Unity 读 `Assets/StreamingAssets/world/`，UE 读 `<Project>/WorldData/`，
`scripts/generate-world.cmd` 一次写入两处，并打印 heightmap 的 SHA-256 便于比对。

**UE 材质**由 `clients/ue5/UE5GameClient/Scripts/setup_assets.py` 通过
`UnrealEditor-Cmd -run=PythonScript` 生成到 `/Game/AI003`。踩过的坑：

1. UE 5.8 的 `MaterialExpressionVertexColor` 输出引脚名是**空字符串**，
   `connect_material_property(node, "RGB", ...)` 会静默失败，必须传 `""`。
2. `UProceduralMeshComponent::CreateMeshSection` 的 `FColor` 重载会对颜色再做一次
   sRGB→线性转换；世界数据已经是线性值，因此改用
   `CreateMeshSection_LinearColor(..., bSRGBConversion=false)` 才能与 Unity 一致。
3. Python commandlet 的打印会被吞掉，诊断脚本改为直接写文件到 `artifacts/logs/`。

**离屏出图**（不依赖窗口/Play Mode）

- Unity：`Unity.exe -batchmode -executeMethod AI003.EditorTools.RenderWorldPreview.Run`
  （batchmode 仍初始化 D3D，渲染到 RenderTexture 再读回 PNG）
- UE：`UnrealEditor-Cmd.exe <project> -game -windowed -AI003WorldShot`
  （`FScreenshotRequest` 写到 `artifacts/world-ue5.png` 后自动退出）

## 已知事项

1. **Unity.exe 是 GUI 子系统程序**：PowerShell 调用后立即返回，退出码不可用。冒烟脚本
   通过日志中的 `[AI003][SMOKE] PASS/FAIL` 标记判定结果。
2. **`-batchmode` 首次运行**会编译脚本、导入包，耗时较长；后续运行在 1 分钟内。
3. **UE 首次编译**要生成 makefile、跑 UHT、编译模块并链接 Editor DLL，4 核机器约 3 分钟；
   增量编译在 10 秒级。
4. **VS 的 "Game development with C++" 工作负载未安装**（只装了 Unity 工作负载和 C++ 工具集）。
   当前 UBT 能正常编译；如果以后要调试 UE C++ 或加 C++ 插件，建议在 VS Installer 里补上
   该工作负载（会同时带上 Windows 11 SDK 22621 和 Unreal 调试器可视化工具）。
5. **Unity 缺少 IL2CPP 模块**：当前只有 `windowsstandalonesupport`（Mono）。要做
   Windows IL2CPP 发行包时，在 Unity Hub 里给 6000.6.6f1 添加 "Windows Build Support (IL2CPP)"。
6. **PowerShell 执行策略为 Restricted**：直接跑 `.ps1` 会被拒绝。仓库里的 `.cmd` 包装器
   已带 `-ExecutionPolicy Bypass`；手动调用请用
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-all.ps1`。
7. 本机已有一个 Unity 编辑器实例在跑别的工程（2026-11-01 启动）；互不影响，但会占用内存。
