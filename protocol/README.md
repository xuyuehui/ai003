# AI003 三端共享协议（v3，状态同步 + 房间帧同步）

三个部分（Unity 6 客户端、UE5 客户端、Go 服务器）共用同一套报文，保证“同一效果”可以逐项对齐。
v1 只有 `ping/pong`；v2 加入服务器权威的玩家状态（`join`/`joined`/`move`/`snapshot`）；
v3 加入房间副本与固定 tick 的帧同步战斗（`room.*`）。每次变更都把 `protocolVersion` 加一。

## 传输

| 项目 | 约定 |
| --- | --- |
| 传输层 | WebSocket，文本帧，UTF-8 |
| 地址 | `ws://127.0.0.1:8080/ws`（开发默认） |
| 健康检查 | `GET http://127.0.0.1:8080/healthz` |
| 编码 | JSON，字段名小驼峰 |

## 观测端点（HTTP，不属于 WebSocket 协议）

| 端点 | 用途 |
| --- | --- |
| `GET /healthz` | 存活与版本：`status`、`version`、`protocolVersion`、`serverTime` |
| `GET /demo` | 技术演示指标：房间、世界玩家数、消息/字节量、world snapshot、room frame、事件计数 |

`/demo` 示例：

```json
{
  "service": "ai003-server",
  "protocolVersion": 3,
  "uptimeSeconds": 59.2,
  "goVersion": "go1.26.3",
  "goroutines": 7,
  "worldPlayers": 0,
  "rooms": [{ "id": "arena-1", "players": 2, "capacity": 8, "tick": 580 }],
  "stats": {
    "messagesOut": 326, "bytesOut": 124133,
    "worldSnapshots": 0, "roomFrames": 580,
    "attackEvents": 11, "hitEvents": 1,
    "deathEvents": 0, "respawnEvents": 0
  }
}
```

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
{ "type": "join", "seq": 1, "body": { "client": "unity", "name": "Alice", "clientId": "9f2c…" } }
```

`client` 取值：`unity`、`ue5`；`name` 可省略（默认等于 `client`）。

`clientId` 是客户端**持久身份**（Unity 存 `PlayerPrefs`，UE 存 `Saved/ClientId.txt`）。
省略时服务器退回会话 id。房间按 `clientId` 记玩家，所以断线重连能回到原角色。

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

`playerId` 当前等于 `clientId`（或会话 id）；未 `join` 就发 `move` 会收到 `error`。

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

## 房间与帧同步战斗（v3）

房间是独立于大世界的一个小竞技场：服务器以固定 **20 Hz** tick 驱动确定性模拟，
客户端发送输入、接收每帧结果。位置/速度/战斗全部用**整数毫米**，Go / C# / C++ 结果一致。

### `room.enter`（客户端 → 服务器）

```json
{ "type": "room.enter", "seq": 31, "body": { "name": "Alice", "room": "arena-2", "clientId": "9f2c…" } }
```

`room` 可省略：省略时由服务器**自动匹配**——放进人数最少且有位的房间，都满则新建
`arena-N`（默认容量 8）。指定不存在的 `room` 会按需创建；满房返回
`error: "arena-2: room full"`。重复进入同一房间会重置该玩家（满血、回到出生槽位）；
**离线状态**下重连同一 `clientId` 不会重置，而是恢复血量/位置/槽位。
服务器按槽位分配出生点（0,0）、(4 m,0)、(-4 m,0)…

### `room.list`（客户端 → 服务器 / 服务器 → 客户端）

```json
{ "type": "room.list", "seq": 30, "body": {} }
```

响应：

```json
{ "type": "room.list", "seq": 30, "body": { "rooms": [
  { "id": "arena-1", "players": 2, "capacity": 8, "tick": 3459 }
] } }
```

### `room.entered`（服务器 → 客户端）

```json
{
  "type": "room.entered",
  "seq": 31,
  "body": {
    "roomId": "arena",
    "playerId": "ce130808238f3494",
    "tick": 3459,
    "state": { "id": "ce13…", "x": 0, "z": 0, "facingX": 0, "facingZ": 1, "hp": 100, "alive": true, "slot": 0 },
    "players": [ ],
    "tickRate": 20,
    "tickMillis": 50,
    "arenaHalfMM": 20000,
    "speedMMPerTick": 250,
    "diagonalMMPerTick": 177,
    "attackRangeMM": 3000,
    "attackDamage": 25,
    "attackCooldownTicks": 20,
    "maxHP": 100,
    "respawnTicks": 60,
    "buttonAttack": 1
  }
}
```

客户端必须使用服务器下发的常量，不要各自写死。

### `room.input`（客户端 → 服务器，20 Hz）

```json
{ "type": "room.input", "seq": 32, "body": { "tick": 3463, "dx": 1, "dz": 0, "buttons": 0 } }
```

`dx/dz` 取值 `-1/0/1`，`buttons` 是位掩码（bit0 = 攻击）。

`tick` 是该输入**生效的模拟 tick**：服务器把它排到对应 tick，缺 tick 或 tick 已过期就按
"上一条输入"立即生效（丢包/旧客户端兼容）。客户端因此可以提前 2–4 个 tick 发送，
并在本地用同一套整数规则先行模拟（客户端预测）；服务器帧到达后若位置偏差超过阈值，
客户端回到服务器状态并重放未确认输入。

### `room.frame`（服务器 → 房间成员，20 Hz）

```json
{
  "type": "room.frame",
  "body": {
    "tick": 3460,
    "inputs": { "ce13…": { "dx": 1, "dz": 0, "buttons": 0 } },
    "players": [
      { "id": "ce13…", "name": "Alice", "x": 250, "z": 0, "facingX": 1, "facingZ": 0, "hp": 100, "alive": true, "slot": 0 }
    ],
    "events": [ { "type": "hit", "source": "ce13…", "target": "ab…", "damage": 25 } ]
  }
}
```

`events` 取值：`attack` / `hit` / `death` / `respawn`。`attack` 在每次真正出拳时广播
（命中或落空都有），客户端据此播放攻击动作；`hit` 带 `damage`。断线或 `room.leave` 后
该玩家从下一帧消失。

### `room.leave`（客户端 → 服务器）/ `room.left`（服务器 → 客户端）

```json
{ "type": "room.leave", "seq": 40, "body": {} }
```

显式离开会**立即**移除玩家；断线不会。

### 断线与重连

- 连接断开时玩家被标记为 `offline`，状态保留 **30 s（600 ticks）**。
- 离线玩家仍在 `room.frame` 里（`offline: true`），但不可被攻击、不移动，复活计时暂停。
- 宽限期内用同一 `clientId` 重新 `room.enter`：回到原房间，恢复血量/位置/槽位，**不重置**。
- 超过宽限期：从房间移除；房间满员时也不再占位。
- 客户端地址/会话都变了也没关系，身份只看 `clientId`。

### 战斗规则（确定性）

| 项 | 值 |
| --- | --- |
| tick | 20 Hz（50 ms） |
| 场地 | 正方形，±20 m |
| 移动 | 250 mm/tick；斜向 177 mm/tick（不加速） |
| 朝向 | 最后一次非零输入方向 |
| 攻击范围 | 3000 mm |
| 攻击锥角 | ±60°（用整数点积判定，无浮点） |
| 伤害 / 冷却 | 25 / 20 ticks（1 s） |
| 生命 / 复活 | 100 / 死亡后 60 ticks（3 s）在出生槽位满血复活 |
| 角色半径 / 分离 | 400 mm；重叠时沿主轴每 tick 各推开 20 mm |
| 房间容量 | 8 人 |
| 断线宽限 | 600 ticks（30 s），期内重连恢复状态 |
| 客户端预测 | 本地领先 3 ticks 模拟；误差 >50 mm 时回滚重放 |

## 冒烟与验收

### `ping` / `pong` / `error`

与 v1 相同，`seq` 必须配对；`error` 的 body 为 `{ "message": "..." }`。

## 验收标准

单个客户端：收到 `welcome`（`protocolVersion=3`），`join` 后收到 `joined`，10 Hz 收到包含自己的
`snapshot`，连续 3 个 `ping` 收到匹配的 `pong`。

双端：Unity 与 UE 同时连接，各自在快照里看到对方，位置/朝向/`anim` 一致；一端退出后，
另一端的下一次快照里不再包含它。

房间：两个客户端 `room.enter` 后收到同一 `room.frame`，输入在下一 tick 生效；一方攻击命中后
另一方的 `hp` 下降 25，命中/死亡/复活事件在所有客户端一致；退出后从下一帧消失。

重连：断开后 30 s 内用同一 `clientId` 重新进房，回到原 `roomId` 且 `playerId`/血量/位置不变；
超过 30 s 后该玩家从帧里消失。

## 版本演进

- v2：新增 `join`/`joined`/`move`/`snapshot` 与坐标约定；`welcome` 增加 `snapshotRateMs`。
- v3：新增 `room.enter`/`room.entered`/`room.input`/`room.frame`/`room.leave`，
  固定 20 Hz、整数毫米、近战/冷却/死亡/复活规则。
- 新增业务报文时，在本文档追加一节，并把 `gateway.ProtocolVersion` 加一；
- 三端的常量要与 `protocolVersion` 对齐（Unity: `WorldSyncClient`/`RoomClient`，
  UE: `NetSyncSubsystem`）；`room.entered` 下发的常量是唯一来源；
- 破坏性变更（字段重命名/语义改变）必须提升主版本，并在本文档记录迁移说明。
