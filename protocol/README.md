# AI003 三端共享协议（v1，冒烟版）

三个部分（Unity 6 客户端、UE5 客户端、Go 服务器）共用同一套报文，保证"同一效果"可以逐项对齐。
当前版本只包含连通性验证所需的 `ping/pong`，业务协议在后续需求中逐个加入，每次变更都把
`protocolVersion` 加一。

## 传输

| 项目 | 约定 |
| --- | --- |
| 传输层 | WebSocket，文本帧，UTF-8 |
| 地址 | `ws://127.0.0.1:8080/ws`（开发默认） |
| 健康检查 | `GET http://127.0.0.1:8080/healthz` |
| 编码 | JSON，字段名小驼峰 |

## 信封

每一帧都是一个信封，`body` 的具体结构由 `type` 决定：

```json
{ "type": "ping", "seq": 7, "body": { "client": "unity" } }
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `type` | string | 报文类型 |
| `seq` | int64 | 客户端自增序号；`pong`/`error` 原样回传，用于配对 |
| `body` | object | 负载，可为空 |

## 报文

### `welcome`（服务器 → 客户端，连接建立后立即发送）

```json
{
  "type": "welcome",
  "body": {
    "sessionId": "ce130808238f3494",
    "serverVersion": "0.1.0",
    "protocolVersion": 1,
    "serverTime": 1793691407067
  }
}
```

### `ping`（客户端 → 服务器）

```json
{ "type": "ping", "seq": 1, "body": { "client": "unity", "sentAt": 1793691407067 } }
```

`client` 取值：`unity`、`ue5`、`go-pingclient`、`unity-editor`（批处理冒烟）。

### `pong`（服务器 → 客户端）

```json
{ "type": "pong", "seq": 1, "body": { "serverTime": 1793691407067 } }
```

`seq` 必须与对应 `ping` 相同。

### `error`（服务器 → 客户端）

```json
{ "type": "error", "seq": 9, "body": { "message": "unknown message type: nope" } }
```

## 冒烟验收标准

任一客户端通过的标准：收到 `welcome`，连续发送 3 个 `ping`，收到 3 个 `seq` 完全匹配的
`pong`，进程退出码为 0。

## 版本演进

- 新增业务报文时，在本文档追加一节，并把 `gateway.ProtocolVersion` 加一；
- 三端的常量要与 `protocolVersion` 对齐（Unity: `SmokeTest`/`GatewayClient`，UE: `SmokeGameInstance`）；
- 破坏性变更（字段重命名/语义改变）必须提升主版本，并在本文档记录迁移说明。
