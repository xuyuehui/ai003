# AI003 技术演示：架构与验证

> 定位：**技术演示（technical demo）**，不是玩法实现。目标是证明同一份世界数据与协议，
> 能让 Unity 6 与 UE5 两个客户端表现一致、能被脚本复现、能被指标量化。

## 1. 一句话架构

一个 Go 服务器持有世界数据与权威状态，通过一条 JSON WebSocket 协议同时服务
Unity 6 与 UE5；两个客户端共享同一份世界字节、同一套角色资产、同一套模拟常量。

```
                         ┌────────────────────────────────────┐
                         │             Go server              │
                         │  gateway: HTTP + WebSocket         │
                         │   ├─ Hub            10 Hz snapshot │
                         │   ├─ room.Manager   多房间/匹配    │
                         │   └─ room.Room      20 Hz 整数模拟 │
                         └───────────────┬────────────────────┘
                             JSON v3     │  ws://127.0.0.1:8080/ws
              ┌──────────────────────────┴──────────────────────────┐
       Unity 6 client                                        UE5 client
       Net/ConnectionBootstrap                               Net/NetSyncSubsystem
       Net/WorldSyncClient → World/RemoteAvatar              ARemotePlayer
       Net/RoomClient (预测/回滚)                             World/WorldCharacter
       World/WorldBootstrap                                  World/WorldGameMode
              └────────────────── shared inputs ──────────────────┘
                    world/ (heightmap, colors, biomes, spawn)
                    assets/characters/kenney-protagonists
                    protocol/README.md
```

## 2. 三条“同源”主线

| 主线 | 做法 | 验证 |
| --- | --- | --- |
| 数据同源 | Go 只生成一次 `world/`，两端加载同一份字节 | `smoke-unity`、`smoke-ue5-world` |
| 协议同源 | v1 信封 → v2 状态 → v3 房间，字段与语义写在 `protocol/README.md` | `cmd/democlient`、Go 单测 |
| 规则同源 | 整数毫米、20 Hz、坐标约定、常量由 `room.entered` 下发 | 房间确定性单测、双端 HUD |

## 3. 关键不变量

- **世界**：seed / 尺寸 / 分辨率固定，生成器确定性；客户端只读，不回写。
- **坐标**：协议用“米 + 脚底 + Unity 轴序”；UE 收发时换算 `(x, z, y) × 100` 并补 96 cm。
- **模拟**：房间内全部整数运算（毫米、`int8` 输入），禁止浮点，三语言结果一致。
- **常量**：速度、攻击范围/伤害/冷却、HP、复活时间都由服务器在 `room.entered` 下发。
- **身份**：`clientId` 持久（Unity `PlayerPrefs` / UE `Saved/ClientId.txt`），
  断线保留 30 s（600 ticks）后移除。

## 4. 可复现的演示场景

| 场景 | 命令 / 操作 | 期望 |
| --- | --- | --- |
| 世界数据一致 | `scripts\smoke-unity.cmd`、`scripts\smoke-ue5-world.cmd` | 两端 PASS，出生点/地形一致 |
| 无头协议 + 指标 | `scripts\demo-scenario.cmd -Seconds 8` | 同房间、约 160 帧、`maxTickGap ≤ 2`、有 attack/hit |
| 双端同机位对照 | `scripts\demo-compare.cmd` | 固定机位/角色/天空；亮度差 2.5、平均像素差 7.2/255、95.6% ≤24 |
| 同房双端 | `scripts\demo-room.cmd` | 一条命令起网关 + 两端同房 + 双端截图 + 计数 |
| Unity ECS | PlayMode `FighterEcsTests` | 实体位置由 `FighterViewSystem` 写回 Transform |
| 对照回归 | `smoke-all -WithCompare` | 亮度差 ≤8、像素差 ≤24 的占比 ≥80% 才算通过 |
| 双端互见 | Unity Play Mode（`AI003_ROOM=1`）+ UE `-game -AI003Room` | 两端截图互见，HP 同步 |
| 客户端预测 | 看 HUD `pred / corr / 误差` | `pred = server + 3`，静止误差 0 |
| 断线重连 | `ConnectionBootstrap.DebugDisconnect()` 或杀 UE | 30 s 内同 `roomId`/`playerId` 恢复 |
| 战斗表现 | 同房对战 | 围栏、前冲、伤害数字、倒地/复活 |

## 5. 观测点（演示时直接展示）

| 入口 | 内容 |
| --- | --- |
| `GET /healthz` | 服务、版本、协议版本、serverTime |
| `GET /demo` | 房间列表、世界玩家数、消息/字节量、world snapshot、room frame、`attack/hit/death/respawn` 计数、goroutines |
| `scripts\demo-status.cmd` | 上述指标的易读输出（`-Json` 机器可读） |
| `cmd/democlient` | 无头客户端场景的 JSON 摘要：帧数、首末 tick、最大间隔、HP、事件 |
| `demo/baseline.json` | 指标基线（帧率、tick 间隔、出拳/命中速率），`-UpdateBaseline` 更新 |
| `artifacts/demo/diff.png` | 双端对照的差分热力图（黑=一致，红/黄=差异大） |
| 客户端 HUD | `tick / pred / corr / 误差 / hp / fighters / roomId` |

## 6. 验证矩阵

```powershell
# Go 侧
cd server; go build ./...; go vet ./...; go test ./... -count=1

# 引擎侧
.\scripts\smoke-unity.cmd
.\scripts\test-unity-playmode.cmd
.\scripts\smoke-ue5-world.cmd -SkipBuild

# 演示链路（需服务器已启动）
.\scripts\demo-scenario.cmd -Seconds 8          # 与 demo/baseline.json 对比
.\scripts\demo-scenario.cmd -Seconds 8 -UpdateBaseline
.\scripts\demo-compare.cmd                      # 双端同机位对照（约 2 min）
.\scripts\demo-compare.cmd -MaxBrightnessDelta 8 -MinWithin24 80   # 带阈值
.\scripts\demo-room.cmd                        # 同房双端 + 双端截图
.\scripts\smoke-all.cmd -WithCompare -WithRoom                     # 全链路

# 全链路（可选跳过引擎）
.\scripts\smoke-all.cmd -SkipUnity -SkipUE
```

`demo/baseline.json` 保存帧率、最大 tick 间隔、出拳/命中速率、房间帧速率；
`demo-scenario` 默认按**每秒速率**与基线比较（±50% 容差、tick 间隔 ≤2），
CI 由 [.github/workflows/verify.yml](../.github/workflows/verify.yml) 在
windows-latest 上跑 Go 测试 + 无头演示（引擎不在 CI 内）。

## 7. 非目标

- 玩法深度：技能、任务、经济、匹配大厅、排行榜；
- 生产级规模：当前单网关、房间在内存、无持久化与 AOI；
- 完整反作弊：服务器做边界/非法值/频率约束，但不做行为分析；
- 正式美术与特效：UE 侧战斗反馈仍是 debug 图元。
- 曝光/色调：UE 的 `AEM_Manual` 在该场景会压成黑帧，改用 Histogram + `min=max=0.8 EV`
  锁定曝光；Unity 侧用 ACES tonemapping 卷。仍有细微色彩分级差异（平均像素差 7.2/255）。
- UE 坐标映射：Unity `(x, y, z)` → UE `(x, −z, y)`（`y` 是高度）。早期用 `(x, z, y)`，
  行列式为 −1，是反射——地形会左右镜像；对照图的地形轮廓不一致才暴露出来。同步修正了
  地形行序/法线、出生点、世界同步、房间坐标与朝向。

这些取舍是为了让演示**可复现、可观测、可回归**，而不是把时间花在玩法内容上。
