# AI003

同一套效果，三套实现：

- **Unity 6（URP）客户端** — `clients/unity/UnityGameClient`
- **Unreal Engine 5 客户端** — `clients/ue5/UE5GameClient`
- **Go 服务器** — `server`

三端通过同一条 WebSocket JSON 协议通信（见 [protocol/README.md](protocol/README.md)）。
当前仓库处于**环境就绪**状态：三端骨架可编译、可运行，并用真实的 `ping/pong` 冒烟测试
验证了三条链路：

- Unity 批处理模式（编辑器内 C# 联网）→ Go
- Unity **Play Mode**（`ConnectionBootstrap` 自动挂载，等同按下 Play）→ Go
- UE5 C++（`SmokeGameInstance`，头less `-game`）→ Go

## 目录结构

```
ai003/
├─ server/                     Go 模块（module ai003/server）
│  ├─ cmd/server/              网关进程：/healthz + /ws
│  ├─ cmd/pingclient/          命令行冒烟客户端
│  └─ internal/gateway/        WebSocket 网关 + 单元测试
├─ clients/unity/UnityGameClient/   Unity 6 工程（URP 17.6）
│  ├─ Assets/Scripts/Net/      GatewayClient / ConnectionBootstrap
│  └─ Assets/Tests/PlayMode/   ConnectionBootstrap 的 PlayMode 测试
├─ clients/ue5/UE5GameClient/  UE 5.8 C++ 工程
│  └─ Source/UE5GameClient/    SmokeGameInstance（-AI003Smoke 时启用）
├─ protocol/                   三端共享报文定义
├─ scripts/                    PowerShell 构建 / 冒烟脚本
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

## 快速开始

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

用 Unity 6000.6.6f1 打开 `clients/unity/UnityGameClient`，进入 Play Mode。

`ConnectionBootstrap` 会在场景加载前自动挂载（无需改场景），默认连接
`ws://127.0.0.1:8080/ws`，每 2 秒发一次 ping，Console 里可见：

```
[AI003] connected to ws://127.0.0.1:8080/ws
[AI003] welcome: {...}
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

然后双击 `UE5GameClient.uproject` 打开编辑器。正常运行时当前不连服务器；
只有带 `-AI003Smoke` 参数启动才会执行 `SmokeGameInstance` 的 ping 流程。

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
```

## 脚本

| 脚本 | 作用 |
| --- | --- |
| `scripts/build-server.ps1` | `go build` + `go vet` + `go test`，产物 `server/bin/ai003-server.exe` |
| `scripts/smoke-unity.ps1` | Unity 批处理模式跑 `AI003.EditorTools.SmokeTest.Run` |
| `scripts/test-unity-playmode.ps1` | 跑 Unity PlayMode 测试（`ConnectionBootstrap` 自动连接） |
| `scripts/smoke-ue5.ps1` | UBT 编译 + `UnrealEditor-Cmd -game -nullrhi -AI003Smoke` |
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

## 接下来

按需求逐个实现，建议的顺序（待确认）：

1. 确定同步模型与业务报文，扩充 `protocol/`；
2. 三端各自搭一层网络抽象，把 `ping` 替换成真实消息；
3. 添加双端一致的调试场景（同一机位、同一光照、同一后处理），用于对比 URP 与 UE5 的画面；
4. 自动化：`smoke-all.ps1` 接入 CI，增加每端的构建产物检查。
