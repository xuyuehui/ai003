# Go 服务器规格

## 1. 基线

| 项 | 值 |
| --- | --- |
| Go | 1.26.3（`go 1.26`） |
| 模块 | `ai003/server` |
| 依赖 | 标准库 + `github.com/gorilla/websocket v1.5.3` |
| CGO | 禁用（纯 Go，便于交叉编译） |
| 产物 | `server/bin/ai003-server.exe`（忽略） |

新增依赖必须能说明"标准库做不到什么"；能用 `net/http` 就不引入框架。

## 2. 目录

```
cmd/server/          网关进程（唯一对外入口）
cmd/pingclient/      命令行冒烟客户端
cmd/worldgen/        世界生成器
internal/gateway/    WebSocket 网关 + 测试
internal/worldgen/   地形/群系/颜色 + 测试
```

- 依赖方向只允许 `cmd → internal`；`internal/*` 之间不互相 import（worldgen 与 gateway 独立）。
- 对外暴露能力前先加测试，再加 cmd 或 HTTP 路由。

## 3. 代码规范

- `gofmt`、`go vet ./...` 必须通过；提交前跑 `go build ./...`。
- 错误用 `fmt.Errorf("context: %w", err)` 包装，不吞错；对外错误带字段上下文。
- 日志用 `log/slog` 结构化字段，禁止 `fmt.Println` 调试残留。
- 每个连接一个读循环；共享写操作加锁；goroutine 必须有退出路径（context 或连接关闭）。
- 配置：flag + 环境变量，默认只绑定回环（`127.0.0.1:8080`）。

## 4. 协议

- 信封 `{type, seq, body}`，语义见 [protocol/README.md](../../protocol/README.md)。
- 新增报文必须同时：更新协议文档、提升 `gateway.ProtocolVersion`、改两端客户端。
- 未知 `type` 回 `error`，不断开连接。
- 连接建立先发 `welcome`；`ping` 必须原样回 `pong` 的 `seq`。
- 单条消息读上限 64 KiB；写超时 10 s；空闲超时 90 s（改这些值要同步文档）。

## 5. 世界生成

- 完全确定性：同一 seed 逐字节一致。随机数用内置 `splitmix64`，**不用** `math/rand` 全局源。
- 输出：`world.json`、`heightmap.f32`（float32 LE、行优先）、`colors.rgba8`（线性）、
  `biomes.r8`、`preview.png`。
- 地形/群系参数改动必须重跑 `scripts/generate-world.cmd`，并确认两端加载表现。
- 单元测试覆盖：
  1. 同 seed 确定性、不同 seed 不同；
  2. 六种群系均存在（海洋/沙滩/草地/沙漠/岩石/雪）；
  3. 出生点在海平面以上、周边 30 m 干燥；
  4. `HeightAt` 双线性采样边界与越界回退。

## 6. 测试

- 表驱动优先；HTTP/WS 用 `httptest`，不连真实端口。
- 每个新报文、错误分支至少一个测试。
- 最低门禁：

```powershell
go build ./...
go vet ./...
go test ./... -count=1
```

## 7. 运行

```powershell
.\scripts\build-server.cmd
.\server\bin\ai003-server.exe -addr 127.0.0.1:8080

# 冒烟
go run ./cmd/pingclient -url ws://127.0.0.1:8080/ws -n 3

# 重新生成世界
go run ./cmd/worldgen -out ..\world
```

## 8. 完成定义

1. `go test ./...` 全绿、`go vet` 无输出；
2. `smoke-all` 的 `go-tests` / `go-client` PASS；
3. 协议变更同步到 `protocol/README.md` 与两端客户端；
4. 世界数据变更后重新生成并提交 `world/`。
