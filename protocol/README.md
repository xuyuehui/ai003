# AI003 三端共享协议（v2，状态同步）

三个部分（Unity 6 客户端、UE5 客户端、Go 服务器）共用同一套报文，保证“同一效果”可以逐项对齐。
v1 只有 `ping/pong`；v2 加入服务器权威的玩家状态：`join` / `joined` / `move` / `snapshot`。
每次变更都把 `protocolVersion` 加一。

## 传输

| 项目 | 约定 |
| --- | --- |
| 传输层 | WebSocket，文本帧，UTF-8 |
| 地址 | `ws://127.0.0.1:8080/ws`（开发默认） |
| 健康检查 | `GET http://127.0.0.1:8080/healthz` |
| 编码 | JSON，字段名小驼峰 |

## 信封

```json
{ "type": "move", "seq": 7, "body": { } }
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `type` | string | 报文类型 |
| `seq` | int64 | 客户端自增序号；`pong`/`error`/`joined` 原样回传，用于配对 |
| `body` | object | 负载，可为空 |

## 坐标约定

状态同步的位置是**跨引擎统一**的，三端都按这一套转换：

| 项 | 约定 |
| --- | --- |
| 单位 | 米 |
| 原点 | 世界中心（`world/world.json` 的 2048 m 世界） |
| 轴序 | 与 Unity 一致：`x` 东、`y` 高度、`z` 北 |
| 位置语义 | **脚底**（角色与地面的接触点），不是胶囊中心 |
| 朝向 | `yaw` 角度，0 = 朝 `+z`，绕 `+y` 右手方向为正 |

引擎换算：

| 引擎 | 位置 | 朝向 |
| --- | --- | --- |
| Unity | 直接使用 `transform.position`（本来就在脚底） | `transform.eulerAngles.y` |
| Unreal | `(x, y, z) * 100` → `(X, Z, Y)`，再给胶囊中心 `+96 cm` | `ActorRotation.Yaw` 直接用 |

> UE 的 `X` 对应协议的 `z`、`Y` 对应 `x`；由于两边 yaw 都绕自身 up 轴、0 指向“北/前”，
> 数值可以直接透传。

## 报文

### `welcome`（服务器 → 客户端，连接建立后立即发送）

```json
{
  "type": "welcome",
  "body": {
    "sessionId": "ce130808238f3494",
    "serverVersion": "0.1.0",
    "protocolVersion": 2,
    "snapshotRateMs": 100,
    "serverTime": 1793691407067
  }
}
```

### `join`（客户端 → 服务器）

```json
{ "type": "join", "seq": 1, "body": { "client": "unity", "name": "Alice" } }
```

`client` 取值：`unity`、`ue5`；`name` 可省略（默认等于 `client`）。

### `joined`（服务器 → 客户端）

```json
{
  "type": "joined",
  "seq": 1,
  "body": {
    "playerId": "ce130808238f3494",
    "name": "Alice",
    "protocolVersion": 2,
    "snapshotRateMs": 100
  }
}
```

`playerId` 当前等于会话 id；未 `join` 就发 `move` 会收到 `error`。

### `move`（客户端 → 服务器，建议 10 Hz）

```json
{
  "type": "move",
  "seq": 12,
  "body": {
    "pos": { "x": 168.0, "y": 39.71, "z": -8.0 },
    "yaw": 90.0,
    "speed": 6.0,
    "grounded": true,
    "swimming": false
  }
}
```

服务器会清洗：位置夹到 `±1024 m`、高度夹到 `-200..600 m`，非有限数归零，`yaw` 归一化，
`speed` 夹到 `0..100`。被清洗后的值就是权威值。

### `snapshot`（服务器 → 所有已 join 的客户端，10 Hz）

```json
{
  "type": "snapshot",
  "seq": 0,
  "body": {
    "tick": 1123,
    "serverTime": 1793714022496,
    "players": [
      {
        "id": "ce130808238f3494",
        "name": "Alice",
        "client": "unity",
        "pos": { "x": 168.0, "y": 39.71, "z": -8.0 },
        "yaw": 90.0,
        "speed": 6.0,
        "grounded": true,
        "swimming": false,
        "anim": "walk"
      }
    ]
  }
}
```

`anim` 由服务器派生（所有客户端看到同一个状态）：

| 条件 | anim |
| --- | --- |
| `swimming` | `swim` |
| `!grounded` | `jump` |
| `speed > 6.5` | `run` |
| `speed > 0.2` | `walk` |
| 其他 | `idle` |

断线时该玩家从下一次快照起消失。

### `ping` / `pong` / `error`

与 v1 相同，`seq` 必须配对；`error` 的 body 为 `{ "message": "..." }`。

## 验收标准

单个客户端：收到 `welcome`（`protocolVersion=2`），`join` 后收到 `joined`，10 Hz 收到包含自己的
`snapshot`，连续 3 个 `ping` 收到匹配的 `pong`。

双端：Unity 与 UE 同时连接，各自在快照里看到对方，位置/朝向/`anim` 一致；一端退出后，
另一端的下一次快照里不再包含它。

## 版本演进

- v2：新增 `join`/`joined`/`move`/`snapshot` 与坐标约定；`welcome` 增加 `snapshotRateMs`。
- 新增业务报文时，在本文档追加一节，并把 `gateway.ProtocolVersion` 加一；
- 三端的常量要与 `protocolVersion` 对齐（Unity: `WorldSyncClient`，UE: `NetSyncSubsystem`）；
- 破坏性变更（字段重命名/语义改变）必须提升主版本，并在本文档记录迁移说明。
