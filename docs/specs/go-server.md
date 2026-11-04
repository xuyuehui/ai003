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
cmd/democlient/      无头演示客户端（进房/输入/统计，输出 JSON 摘要）
cmd/worldgen/        世界生成器
internal/gateway/    WebSocket 网关 + 测试 + /demo 指标
internal/room/       房间确定性模拟（帧同步战斗）+ 测试
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

### 4.1 状态同步（协议 v2）

- `internal/gateway/state.go` 维护权威玩家表：
  - `Hub.Join/Leave` 增删玩家，`ApplyMove` 写入清洗后的状态，`Subscribe` 注册快照回调；
  - 快照循环每 `SnapshotRate`（100 ms）跑一次，序列化一次后广播给所有订阅者；
  - 坐标约定见 [protocol/README.md](../../protocol/README.md)：米、脚底、Unity 轴序。
- 清洗规则写在 `sanitize`：`x/z` 夹到 ±1024 m、`y` 夹到 −200..600 m，非有限数归零，
  `yaw` 归一化到 `[0,360)`，`speed` 夹到 `0..100`。
- `anim` 由 `deriveAnim` 统一派生（swim > jump > run > walk > idle），客户端不再各算一套。
- 快照广播用直接写回调，不引入额外队列；单机原型下玩家数量很小，慢客户端由写超时兜底。
- 测试：`state_test.go` 覆盖清洗/动画派生/玩家表，`gateway_test.go` 覆盖
  双人互见、位置同步、断线移除、未 join 先 move 被拒。

### 4.2 房间与帧同步（协议 v3）

- `internal/room` 只做纯模拟，不 import gateway：`Room` 提供 `Enter/Leave/SetInput/Step/Snapshot`，
  `Manager` 提供多房间的 `Enter/Leave/Step/SetInput/Snapshot/List/RoomIDs`。
- 所有数值是整数毫米与 `int8` 输入，**禁止浮点**，保证 Go/C#/C++ 三端逐 tick 一致；
  伤害/冷却/复活/锥角判定/角色分离都写成整数表达式。
- `Manager`：单房容量 `DefaultCapacity=8`；`Enter` 不指定房间时选人数最少且有位的房间，
  都满则新建 `arena-N`；指定房间满员返回 `ErrRoomFull`。房间不自动销毁（原型期复用）。
- `gateway` 起一个 20 Hz 循环遍历 `Manager.RoomIDs()`，逐房 `Step` 并只向该房订阅者广播
  `room.frame`；`room.enter`/`room.input`/`room.leave`/`room.list` 在连接读循环里处理，
  会话到 `{clientID, roomID}` 的映射存在 `sessions`。
- 身份：`join`/`room.enter` 可带 `clientId`（≤64 字符），缺省退回会话 id；房间按它记玩家。
- 断线：defer 里对房间调用 `MarkOffline`（保留 600 ticks = 30 s），显式 `room.leave` 才
  `Leave` 立即移除；宽限内同 `clientId` 再次 `room.enter` 会复活原角色（不重置血量/位置/槽位），
  `Manager.Enter` 会先按玩家找回原房间并绕过容量检查。
- `room.entered` 下发全部模拟常量（tickRate、场地半宽、速度、攻击范围/伤害/冷却、
  最大 HP、复活时间、攻击按键位），客户端不得写死。
- 掉线即 `MarkOffline`；空房保留但循环不广播。
- `room.input` 的 `tick` 是该输入生效的模拟 tick：非零且大于当前 tick 时排入
  `Player.Pending`，`Step` 时按 tick 取出并更新 `LastInput`；缺 tick / 过期 tick 直接
  作为 `LastInput` 立即生效（丢包与旧客户端兼容）。每 tick 会清掉过期排队项，避免堆积。
- 战斗事件：每次真正出拳（冷却结束且按下攻击）先追加 `attack`，再按射程/锥角判定
  `hit`，血量归零追加 `death`，复活追加 `respawn`；落空也会广播 `attack` 供客户端播放动作。
- 测试：`room_test.go` 覆盖移动/夹取/斜向不加速/锥角/冷却/死亡/复活/分离/确定性/
  **重连保留状态/宽限到期/离线不可被攻击/按 tick 生效/出拳事件**，`manager_test.go` 覆盖
  自动匹配、容量、满房、离开腾位、列表、**重连回原房间**；`room_ws_test.go` 覆盖双人同帧、
  输入生效、离开移除、未进入先输入被拒、`room.list`、**同 clientId 重连**。

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
- `/demo` 指标端点有集成测试：初始为空、进房后房间/帧计数增长。
- CI（`.github/workflows/verify.yml`，windows-latest）：`go build/vet/test` +
  `scripts/demo-scenario.cmd -Seconds 6`（与 `demo/baseline.json` 比较）。
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

# 无头技术演示（另开窗口；两个客户端 + 指标）
.\scripts\demo-scenario.cmd -Seconds 8
.\scripts\demo-status.cmd -Json

# 重新生成世界
go run ./cmd/worldgen -out ..\world
```

## 8. 完成定义

1. `go test ./...` 全绿、`go vet` 无输出；
2. `smoke-all` 的 `go-tests` / `go-client` PASS；
3. 协议变更同步到 `protocol/README.md` 与两端客户端；
4. 世界数据变更后重新生成并提交 `world/`。
