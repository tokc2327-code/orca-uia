> 属于 **Visual Collaboration** 生态——结构化的视觉上下文,以及可核验的人机协作。

# orca-uia

**0.6.0**

**面向 AI agent 的 Windows UI Automation MCP 服务:优先用 Pattern,默认不碰鼠标键盘。**

通过结构化的控件树和 UIA Pattern 操作 Win32、WPF、Electron、UWP 桌面应用——不抢你的光标、不占你的键盘,也不用反复截图烧 token。

[![.NET 8](https://img.shields.io/badge/.NET-8.0%20Windows-512BD4.svg)](#快速开始)
[![UIA3](https://img.shields.io/badge/Windows-UIA3%20%2F%20FlaUI%205.0-0078D6.svg)](#架构与已知边界)
[![Protocol](https://img.shields.io/badge/MCP-stdio%20JSON--RPC%202.0-brightgreen.svg)](#工具9-个)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](#许可证)

---

## 为什么做 orca-uia?

多数桌面 agent 靠「截图 + 点击」的循环干活:窗口一动就失效、vision token 烧得飞快,而且会把用户挡在自己的鼠标键盘之外。**orca-uia** 建立在五条工程规则上:

- **默认零鼠标/零键盘(`input: "pattern"`)**:点击走 UIA Pattern(`Invoke`、`Toggle`、`SelectionItem`、`ExpandCollapse`),输入走 `ValuePattern` 或 Win32 的 `WM_SETTEXT` / `EM_REPLACESEL`。真实的系统输入(`input: "physical"`)必须显式打开。
- **严格重解析,不猜**:快照 ID(`e1`、`e2`…)是随快照失效的「配方」,不是会过期的 COM 句柄。每次动作都严格重解析:匹配 `0` 个抛 `NOT_FOUND`,匹配 `>1` 个抛 `AMBIGUOUS_MATCH`。
- **多步执行 + 差异校验(Execute → Verify)**:用 `steps[]` 加实时选择器(`type`、`name`、`name_contains`、`aid`、`anchor`),**一次 MCP 调用**跑完整个流程。每一步都返回动作后的状态和控件树差异(`added` / `removed` / `enabled_changed`),不用盲等 sleep,也不用中间截图。
- **为 Windows 的真实毛病做的**:Chromium/Electron 的可访问性树预热、给 `contenteditable` 兜底的 CDP 桥(`app_cdp`)、以及给 UIA 看不见的隐藏窗口/托盘窗口用的 Win32 `HWND` 窗口管理(`app_window`)。
- **不背 MCP SDK 的包袱**:单个 C# / .NET 8 可执行文件,一条专用 STA 线程 + Win32 消息泵,走换行分隔的 stdio JSON-RPC 2.0。

---

## 支持的界面

| 界面类型 | 例子 | 主要控制路径 | 说明 |
| --- | --- | --- | --- |
| **Electron / Chromium** | DeepSeek Harness、VS Code、Cursor | UIA3(`--force-renderer-accessibility`)+ `app_cdp` | `contenteditable` 的输入和 DOM 点击要用 `--remote-debugging-port` |
| **Win32 / WPF** | 记事本、原生桌面工具 | UIA3 Pattern + `WM_SETTEXT` / `EM_REPLACESEL` | 后台兼容性最好;隐藏/托盘窗口可用 `app_window` 找到 |
| **UWP / 打包应用** | Windows 计算器、设置 | UIA3 Pattern | 按 `pid` 或 `title_contains` 附加(会遇到 `ApplicationFrameHost` 边界) |

---

## 快速开始

### 1. 前提

- Windows 10 1809 或更高,x64。
- 用发布版的话**不需要装别的东西**:`dist/win-x64/orca-uia.exe` 是自包含单文件,不需要单独安装 .NET 运行时。
- 只有从源码构建才需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

### 2. 拿到可执行文件

在本仓库的检出目录里:

```powershell
dotnet publish src/OrcaUia.csproj -c Release -r win-x64
# -> dist/win-x64/orca-uia.exe
```

注意工程文件在 `src/` 下,不在仓库根目录。发布目录靠 `dist/.gitkeep` 保留,可执行文件本身是本地构建产物。

把这一个文件挪到任何地方,让 MCP 客户端指向它即可,没有安装步骤。

### 3. 接进 MCP 客户端

把可执行文件注册成一个 stdio MCP 服务:

```json
{
  "mcpServers": {
    "orca-uia": {
      "command": "D:/orca-uia/dist/win-x64/orca-uia.exe",
      "args": []
    }
  }
}
```

- `examples/cordis.patch.example.yml` 是同一件事的 DeepSeek Harness 插件包写法。
- `clients/uia-driver.cjs` 是一个很小的脚本驱动器,不开 agent 也能测:给它一份 JSON 任务
  (`{ "title": "窗口标题", "steps": [{ "tool": "app_snapshot", "args": {} }] }`),它会拉起服务、按步执行并打印回执;用 `ORCA_UIA_EXE` 指向你的构建。
- 参数、返回、元素 ID 作用域、错误码的完整说明见 [docs/mcp-contract.md](docs/mcp-contract.md)。

---

## 工具(9 个)

| 工具 | 作用 | 关键参数 |
| --- | --- | --- |
| `app_launch` | 启动应用(只对已知的 Chromium/Electron 宿主自动加 `--force-renderer-accessibility`)并附加 | `path`、`args`、`title_contains`、`force_accessibility`、`preload_ms` |
| `app_attach` | 按标题子串或 pid 附加到已在运行的顶层窗口(跳过托盘/输入法的碎窗口)并预加载控件树 | `title_contains`、`pid`、`preload`、`preload_ms` |
| `app_warmup` | 轮询后代节点数直到稳定——Electron 树是懒构建的,这通常就是空树的解法 | `min_nodes`、`timeout_ms` |
| `app_window` | 列出 / 显示 / 隐藏 / 聚焦顶层 `HWND`,包括 UIA 看不见的隐藏窗口和托盘窗口 | `action`(`list\|show\|hide\|focus`)、`pid`、`title_contains`、`class_contains` |
| `app_cdp` | 用 DevTools 协议直接驱动 Chromium/Electron 的 `contenteditable` 与 DOM(零系统输入) | `action`(`targets\|eval\|focus\|insert_text\|clear\|click\|wait_for\|key`)、`port`、`selector`、`text` |
| `app_snapshot` | 把可见控件树导成紧凑文本,带快照作用域的 ID(`e1`、`e2`…)和状态标记 | `interactive_only`、`max_nodes` |
| `app_act` | 按快照 ID 或实时选择器执行单步动作或多步 `steps[]` 序列,返回动作后状态与差异 | `id` 或 `target`、`action`(`click\|set_value\|type\|press\|toggle`)、`input`(`pattern\|physical`)、`steps[]`、`changes` |
| `app_wait` | 等一个名字含某子串的元素出现或消失 | `name_contains`、`state`(`appear\|gone`)、`timeout_ms` |
| `app_shot` | 用 `PrintWindow`(`PW_RENDERFULLCONTENT`)把单个窗口存成 PNG,给看得懂图的客户端兜底 | `path`、`title_contains`、`pid` |

---

## 例子:一次调用跑完多步流程

因为实时选择器每一步都对当前控件树重解析,agent 可以跨界面切换连续操作,不用停下来重新快照:

```json
{
  "tool": "app_act",
  "arguments": {
    "changes": true,
    "stop_on_error": true,
    "steps": [
      { "action": "click", "type": "TabItem", "name": "Settings" },
      { "action": "wait", "name_contains": "API Key", "state": "appear" },
      { "action": "set_value", "type": "Edit", "name_contains": "API Key", "text": "sk-demo-key" },
      { "action": "click", "type": "Button", "name": "Save" }
    ]
  }
}
```

每一步返回一行结构化的结果:实际用了什么机制(`used`:`invoke`、`value`、`wm_settext` 等)、回读的 `state`、以及 `changed`(`added` / `removed` / `enabled_changed`)。

---

## 跑任务时不抢屏幕

驱动真实应用就意味着应用会被带到前面。在有人正在用的机器上,这会打断人家打字、弄脏人家的截图,所以一次运行应该把屏幕还回去:

1. **先问一句。** 脚本化运行只要几秒,但会挪动焦点;等机器空下来再跑。`app_launch` / `app_window show` 本身已经要求 UIA 不激活地显示窗口(`WS_EX_NOACTIVATE`、`SW_SHOWNOACTIVATE`),`foreground_policy` 默认是 `forbid`,所以需要抢前台的步骤会被拒绝并给出提示,而不是悄悄接管。
2. **少启动一点。** 用 `Start-Process notepad -WindowStyle Minimized`,或者启动后先把焦点挪回来再附加。UWP 应用可能不认这个参数——那它应该在跑完后被关掉。
3. **让驱动器收拾现场。** `clients/uia-driver.cjs` 支持两个任务级选项:

```json
{ "title": "…",
  "restore_foreground": "SomeApp",
  "kill_on_exit": "CalculatorApp",
  "steps": [ … ] }
```

`restore_foreground`(或环境变量 `ORCA_UIA_FOREGROUND`)在运行结束时把标题含该串的窗口重新聚焦;`kill_on_exit` 关掉这次任务驱动的应用——给进程名,或给 `true` 表示用附加回执里报的那个进程。两者都会打印自己做了什么(`FOREGROUND …`、`KILL …`),所以中途失败的一次运行也会把桌面还原成原样。
4. **量出来,别猜。** `tests/foreground-sentinel.ps1` 挂上 `EVENT_SYSTEM_FOREGROUND` 和 `EVENT_OBJECT_FOCUS`,跑完把每条事件分成三档:

   - `steal` —— 前台离开了基准窗口,或焦点落进一个「能被激活、但当时不在前面」的窗口。键盘真的被拿走了,这次运行判失败。
   - `inert` —— 焦点落在带 `WS_EX_NOACTIVATE` 的窗口里。这种窗口激活不了,内部焦点拿不走键盘;照样记下来,但不算失败。
   - `same-window` —— 事件落在本来就在前面的那扇窗里,谁都没被动。

```powershell
pwsh -File tests\foreground-sentinel.ps1 `
  -RunCommand 'node clients\uia-driver.cjs examples\acceptance-calculator-cn.json' `
  -ReportPath evidence\fg.json -WatchPids "<CalculatorApp pid>,<ApplicationFrameHost pid>"
```

`-WatchPids` 把监控收到被驱动的进程上。不给它的话,人自己切窗口和抢占分不开,都会被记成违规——那这脚本就只能在机器空着时跑。**33 步计算器验收,带 `-WatchPids` 的实测:33/33 步全过,`stealCount: 0`、`inertFocusCount: 0`、`sameWindowCount: 0`,跑完前台句柄和开始一模一样。** 反向对照也做了:后台进程对记事本调 `SetForegroundWindow`,哨兵抓到 1 条 `FOREGROUND` + 2 条 `FOCUS`,说明钩子不是睡着的。

还有个坑:如果被驱动的窗口在哨兵启动时正好在前台,它内部的焦点事件就是拿它自己当基准,跑出来会显得比实际干净。构造函数会给最多 5 秒等前台离开被盯的进程,等不到就在报告里写 `baselineIsWatched: true`。

---

## 架构与已知边界

- **单条 STA 线程 + 消息泵**:`Program.cs` 用一条专用读线程读 `stdin`,所有 UIA3/COM 调用都投递到同一条 STA 工作线程,并显式跑 Win32 的 `PeekMessage`/`DispatchMessage` 泵,避免跨单元 COM 超时。
- **结构化错误**:失败返回 `{ "error": "<CODE>", "message": "...", "hint": "..." }`(`NOT_FOUND`、`AMBIGUOUS_MATCH`、`UNKNOWN_ID`、`NO_VALUE_PATTERN`、`CDP_UNAVAILABLE` 等),agent 可以确定性地自我纠正。
- **快照 ID 只在最新一份快照里有效。** 服务端会重解析,但新快照会从 `e1` 重新编号:一个旧 ID 如果恰好撞上当前存在的编号,会解析到**新**条目。拍完新快照就丢掉旧 ID,或者用实时选择器(`{type, name, name_contains, aid}`)。
- **Windows 侧的已知边界:**
  - **UIPI**:中等完整性级别的 `orca-uia` 进程无法操作提权的(管理员)窗口,除非做 `uiAccess` 签名。失败表现为空树,而不是报错。
  - **Electron 编辑器**:`contenteditable` 不认 UIA 的 `ValuePattern`,也没有 `HWND`;用 `--remote-debugging-port=<port>` 启动目标,然后走 `app_cdp`。
  - **UWP / 打包应用**:启动器会移交给 `ApplicationFrameHost.exe`;按标题附加会落在框架窗口上,可能拿到空树——改按所属 `pid` 附加。pid 每次启动都会变。
  - **密码框**按 UIA 的设计是被遮住的;服务只报 `IsPassword`,从不尝试读值。
  - 一次阻塞的 UIA 调用会卡住整个服务直到超时,因为所有调用共用那一条 STA 线程。

---

## 验证过的,和这不是什么

`orca-uia` 是一份**参考实现**:它存在是为了说明「Windows 桌面可以被结构化地驱动,而不是靠截图」,仅此而已。下面打了勾的都在本机实测过——Windows 11、.NET 8、UIA3 / FlaUI 5.0、计算器——用 `tests/` 里的脚本可以复现。

### 已验证

- [x] stdio JSON-RPC 2.0 上的 9 个核心 MCP 工具(STA + Win32 消息泵)
- [x] `steps[]` 多步实时选择器执行,带控件树差异与回读告警
- [x] 内置 `app_cdp` 与 `app_window`,覆盖 Electron 和托盘应用
- [x] `foreground_policy` 三档(`forbid` / `allow_on_idle` / `allow`)+ 不激活的启动与显示——`WS_EX_NOACTIVATE`、`SW_SHOWNOACTIVATE`
- [x] 前台哨兵(`tests/foreground-sentinel.ps1`),把每条 WinEvent 分成 `steal` / `inert` / `same-window`,而不是把每条焦点事件都当抢占
- [x] 脚本化运行期间,被驱动窗口上没有任何焦点事件——33 步计算器验收实测:0 抢占、0 焦点事件、前台句柄全程没变

### 不做什么

这些都是**边界,不是待办**:

- **不做按软件逐个适配。** 没有「让它在某某应用上跑通」的补丁,不追第三方改版。控件树为空、过期、或者动作静默不生效的应用,就保持那样。
- **不做全量 UIA 封装。** 九个工具,不是一个把所有 Pattern 都包一遍的库。
- **不支持提权窗口。** 中等完整性级别进程驱动不了管理员窗口,`uiAccess` 签名不在范围内。
- **不做影子桌面。** `CreateDesktop` 隔离、虚拟显示器、不可见 Electron 托管,这里都不打算做。
- **不把哨兵做成 CI 门。** 它要一个交互式桌面会话,只能是手工一步。
- **没有路线图,也没有支持承诺。** 想要新功能,答案是 fork。

### 维护声明

按现状(As-Is)分享,从 **Visual Collaboration** 生态里抽出来的独立模块。不承诺响应时间、不为第三方应用做兼容补丁、没有发布节奏。欢迎提 issue 和反例——尤其是控件树为空、过期、或者动作静默没生效的应用,那些能让这份参考实现更诚实。它们算贡献,不算支持合同。`MIT` 许可:拿去 fork、拿去改、拿去发你自己的。

---

## 生态

`orca-uia` 是 **Visual Collaboration** 的 Windows 桌面适配器——Figma 画布那边是 [`figma-ordered-pins`](https://github.com/tokc2327-code/figma-ordered-pins)。

## 许可证

MIT,见 [LICENSE](LICENSE)。欢迎提 issue 和反例,尤其是控件树为空、过期、或者动作静默没生效的应用。
