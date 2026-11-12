# 引擎 MCP 配置（Unity + Unreal）

> **范围（2026-11-12 起）**：只保留 Unity 的 MCP（`unity`）。Unreal 的 EngineWire 章节是历史记录，不再维护。

两个编辑器各接一个 MCP 服务，DSH 里分别叫 `unreal-engine` 和 `unity`。
两边都只监听 `127.0.0.1`，用于让 AI 直接操作编辑器（摆场景、建资产、跑测试、截图）。

| 引擎 | 服务端 | 版本 | 端口 | DSH serverName |
| --- | --- | --- | --- | --- |
| Unreal Engine 5.8 | **EngineWire**（编辑器插件 + npm stdio 代理） | 0.6.0 | `3000`（原生 MCP HTTP） | `unreal-engine` |
| Unity 6000.6.6f1 | **MCP for Unity**（UPM 包 + Python stdio 服务） | 10.3.0 | `6400`（Unity 侧 stdio 桥） | `unity` |

## DSH 配置

MCP 客户端登记在 DSH 的 profile 补丁里（不在本仓库内）：

`C:\Users\xuyuehui\.dsh\profiles\desktop\cordis.patch.yml`

```yaml
# Unreal：node 代理读取 UE_PROJECT_PATH 下的 capability token，
# 再连到编辑器插件监听的 127.0.0.1:3000/mcp
- insert:
    - id: unreal-engine-mcp
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: unreal-engine
        transport: stdio
        command: 'C:\Program Files\nodejs\node.exe'
        args:
          - 'C:\Users\xuyuehui\.dsh\mcp\unreal-engine-mcp-server\node_modules\unreal-engine-mcp-server\dist\cli.js'
        env:
          UE_PROJECT_PATH: 'D:/works/ai003/clients/ue5/UE5GameClient'
          LOG_LEVEL: info
        cwd: D:/works/ai003/clients/ue5/UE5GameClient
        toolCallTimeoutMs: 720000
        failOnStartupError: false

# Unity：Python 服务经 stdio 连 DSH，再连 Unity 的 6400 桥
- insert:
    - id: unity-mcp
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: unity
        transport: stdio
        command: 'C:\Users\xuyuehui\.local\bin\mcp-for-unity.exe'
        args: ['--transport', 'stdio']
        env:
          LOG_LEVEL: info
          # 默认 300 s 的 Unity 握手等待会让客户端启动超时，这里收紧
          UNITY_MCP_CONNECTION_TIMEOUT: '20'
          UNITY_MCP_COMMAND_TOTAL_TIMEOUT: '120'
          UNITY_MCP_TIMEOUT: '30'
        cwd: D:/works/ai003/clients/unity/UnityGameClient
        toolCallTimeoutMs: 300000
        failOnStartupError: false
```

> 改完这段配置后 DSH 会重新加载 MCP 客户端；如果新加的服务仍显示 disconnected，
> 重启一次 DSH 即可。

## Unreal Engine（EngineWire）

**安装**（第三方插件，~2.6 GB 含编译产物，已 gitignore）：

```powershell
.\scripts\install-enginewire.cmd -Build    # 下载 v0.6.0、解压到 Plugins、启用、编译
```

手动安装等价于：

1. 下载 `EngineWire-plugin-0.6.0.zip`（GitHub Releases），把 `EngineWire/` 拷到
   `clients/ue5/UE5GameClient/Plugins/EngineWire`
2. 在 `UE5GameClient.uproject` 的 `Plugins` 里加 `{"Name":"EngineWire","Enabled":true}`
3. `Config/DefaultGame.ini`：

   ```ini
   [/Script/EngineWire.EngineWireSettings]
   bEnableNativeMCP=True
   NativeMCPPort=3000
   bRequireCapabilityToken=False
   bAlwaysListen=False
   ```

4. 编译：`Build.bat UE5GameClientEditor Win64 Development "-Project=..."`（首次约 22 分钟）

**启动**：双击 `UE5GameClient.uproject` 打开编辑器，状态栏出现 `MCP :3000`。
插件日志会打印 `Native MCP server started on http://127.0.0.1:3000/mcp`。

**验证**：在 DSH 里调用 `unreal` 的 `search`，返回真实能力目录（163 条）即为连通。

**关于 token**：插件默认要求 64 位十六进制 capability token，写在
`<Project>/Saved/MCP/capability-token`；DSH 的 node 代理启动时读取一次。本项目为了
让已启动的代理直接可用，设为 `bRequireCapabilityToken=False`（仅回环）。要恢复认证：

1. `bRequireCapabilityToken=True`，在 Project Settings 里重新生成/填写 `CapabilityToken`
2. 在 DSH 配置的 env 里加 `MCP_AUTOMATION_CAPABILITY_TOKEN: '<token>'`，重启 DSH

## Unity（MCP for Unity）

**包**：`clients/unity/UnityGameClient/Packages/manifest.json`

```json
"com.coplaydev.unity-mcp": "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.3.0"
```

**桥**：默认不自动启动。`Assets/Editor/EnableUnityMcp.cs` 会写入两个 EditorPrefs：

| 键 | 值 | 作用 |
| --- | --- | --- |
| `MCPForUnity.AutoStartOnLoad` | `true` | 编辑器加载时自动起桥 |
| `MCPForUnity.UseHttpTransport` | `false` | 用 stdio 桥（Unity 监听 6400），而不是去连 8080 |

批量执行一次即可：

```powershell
& "C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe" -batchmode -nographics `
  -projectPath "D:\works\ai003\clients\unity\UnityGameClient" `
  -executeMethod AI003.EditorTools.EnableUnityMcp.Run -quit
```

**Python 服务**（一次性安装，国内建议走镜像）：

```powershell
$env:UV_DEFAULT_INDEX = 'https://pypi.tuna.tsinghua.edu.cn/simple'
uv tool install mcpforunityserver==10.3.0
# 安装后入口：C:\Users\xuyuehui\.local\bin\mcp-for-unity.exe
```

**启动**：打开 Unity 工程，等待加载完成，`Get-NetTCPConnection -LocalPort 6400`
应有监听（属于 Unity 进程）。

**验证**：DSH 里 `manage_scene(action="get_active")` 能返回当前场景信息。

## 一键检查

```powershell
.\scripts\check-mcp.cmd               # 只看 3000 / 6400 是否在监听
.\scripts\check-mcp.cmd -ProbeUnity   # 额外拉起 Unity 服务并列出工具
```

最近一次实测（2026-11-03）：

| 检查 | 结果 |
| --- | --- |
| UE `unreal` search | 返回 163 条能力，catalogRevision 正常 |
| UE 编辑器日志 | `Native MCP server started on http://127.0.0.1:3000/mcp` |
| Unity `manage_scene get_active` | `{"isLoaded":true,"rootCount":2}` |
| Unity 服务 `tools/list` | 48 个工具（manage_scene / manage_gameobject / execute_code …） |

## 常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| UE 调用返回 `NOT_CONNECTED` | 编辑器没开、EngineWire 没启用、或 `bEnableNativeMCP=False`；确认 3000 端口 |
| UE 返回 401 / token 错误 | 项目启用了 token 校验，但 DSH 代理没有对应 token：关掉校验或补 `MCP_AUTOMATION_CAPABILITY_TOKEN` |
| Unity 服务 disconnected | 6400 没监听（自动启动没开）、或第一次握手等待 300 s 导致客户端启动超时；检查 EditorPrefs 与 `UNITY_MCP_*` 超时 |
| Unity 命令超时 `connection wedged` | 桥积累了旧连接或被域重载卡住：重启 Unity 编辑器 |
| Unity HTTP 模式连不上 | 8080 被 Go 游戏服占用；本项目固定用 stdio/6400，不要切 HTTP |
| `uvx` 启动极慢 | 网络问题，改用 `uv tool install` + 国内镜像，让 DSH 直接调用 `mcp-for-unity.exe` |

## 安全边界

- 两个服务都只绑定 `127.0.0.1`，不暴露到局域网。
- UE 端当前关闭了 capability token（本地开发便利），Unity 桥本身无认证；
  换机器/多人环境请恢复 UE token，并避免在不可信网络下开放。
- MCP 能执行编辑器代码（Unity 的 `execute_code`、UE 的脚本/资产写入），
  仅在本机受信会话使用。
