# 工程规格（Specs）

本目录是三端的实现规范。改代码前先看对应规格；规格与代码冲突时，先改规格再改代码。

| 部分 | 规格 | 基线 |
| --- | --- | --- |
| Go 服务器 | [go-server.md](go-server.md) | Go 1.26.3 · `module ai003/server` |
| Unity 客户端 | [unity-client.md](unity-client.md) | Unity 6000.6.6f1 · URP 17.6 |
| 共享协议 | [../../protocol/README.md](../../protocol/README.md) | WebSocket JSON v1 |
| MCP | [../mcp.md](../mcp.md) | MCP for Unity 10.3.0 |

**当前范围**：只做 Unity 客户端 + Go 网关。两端**不共享世界数据**，只共享服务器协议与
服务器算出来的世界事件；场景内容全部在编辑器里 Author 好，Play 不生成世界。

### 归档（历史方案，不实施）

以下文档保留作参考，不代表当前方向：

| 文档 | 为什么归档 |
| --- | --- |
| [unity-open-world.md](../archive/unity-open-world.md) · [unity-open-world-steps.md](../archive/unity-open-world-steps.md) · [terrain-production-pipeline.md](../archive/terrain-production-pipeline.md) | 开放大世界方案（8 km / 4096 瓦片 / 流式），已明确不做 |
| [ue5-client.md](../archive/ue5-client.md) | UE5 客户端，已不做 |

## 仓库级约定

### 目录

```
server/                 Go 模块（cmd / internal）
clients/unity/…         Unity 工程
clients/ue5/…           UE5 工程（历史，不维护）
world/                  世界数据（生成物，提交；Unity 自用）
protocol/               三端共享协议
scripts/                构建、生成、冒烟、MCP 脚本
docs/                   环境、MCP、规格
artifacts/              本地输出（不提交）
```

### 生成物（不要手改）

| 位置 | 由谁生成 | 复现命令 |
| --- | --- | --- |
| `world/`、Unity `Assets/StreamingAssets/world/`、UE `WorldData/` | `server/cmd/worldgen` | `scripts/generate-world.cmd` |
| Unity `Assets/Scenes/OpenWorld.unity` 的地形/摆件/灯光 | 菜单 `AI003/Bake Terrain`、`AI003/Author *` | 见 [unity-client.md](unity-client.md) §4.4 |
| 两端截图 | 渲染/截图脚本 | `scripts/smoke-*.cmd`、`RenderWorldPreview` |

### 端口与环境变量

| 用途 | 默认值 | 覆盖方式 |
| --- | --- | --- |
| Go 网关 HTTP | `127.0.0.1:8080` | `-addr` 或 `AI003_ADDR` |
| Go 网关 WebSocket | `ws://127.0.0.1:8080/ws` | 客户端 `AI003_WS_URL` |
| UE MCP（EngineWire） | `http://127.0.0.1:3000/mcp` | 插件设置 `NativeMCPPort` |
| Unity MCP 桥 | `tcp://127.0.0.1:6400` | Unity 包 PortManager |

### 分支与提交

- 主线 `main`，需要隔离的改动开 `feat/*`、`fix/*` 分支后合并。
- 提交信息用 `feat|fix|docs|chore|refactor|test(scope): summary`，
  scope 取 `server`、`unity`、`ue5`、`world`、`mcp`、`repo`。
- 一次提交只做一件事；生成物与代码变更分开提交。

### 忽略策略

- 根 `.gitignore` 只放全仓库通用项（OS/IDE 噪音、`artifacts/`、`.tmp/`、日志）。
- 每端各自维护 `.gitignore`：`server/`、`clients/unity/UnityGameClient/`、
  `clients/ue5/UE5GameClient/`。
- 二进制、构建缓存、第三方插件、本地日志一律不入库；共享数据（`world/`）例外，
  因为它是两端一致性的来源。

### 验收（Definition of Done）

任何三端都适用的最低要求：

1. `.\scripts\smoke-all.cmd` 全 PASS（UE 已归档，默认不跑；Unity 的 PlayMode 也在其中）；
2. 新增/修改的行为有对应测试或冒烟入口；
3. 文档（本目录、`README.md` 或 `protocol/`）同步更新；
4. 没有把生成物或构建产物误提交。
