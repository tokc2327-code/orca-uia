# orca-uia MCP 契约

## 传输与初始化

服务器通过标准输入和标准输出传输 JSON-RPC 2.0。每行一条 UTF-8 JSON 消息，输出无 BOM。客户端发送带 `id` 的 `initialize`，然后发送无 `id` 的 `notifications/initialized`，服务端对通知不回复。实测请求 `2025-03-26` 返回 `2025-03-26`，省略该字段也返回这个值。当前实现原样回显客户端提供的版本字符串，未校验支持范围。`initialize` 返回 `protocolVersion`、`capabilities: {"tools":{}}` 和 `serverInfo: {"name":"orca-uia","version":"0.6.0"}`。

`tools/list` 返回 `{"tools":[{"name":…,"description":…,"inputSchema":…},…]}`。`tools/call` 接收 `params.name` 和 `params.arguments`。工具结果为 `{"content":[{"type":"text","text":…}],"isError":false}`。下表中的对象都是 **`text` 内编码的 JSON**。`app_snapshot` 和多步 `app_act` 使用下述文本格式。

未知 JSON-RPC 方法返回顶层 `error.code = -32601`。请求处理中的其他异常可返回 `-32603`。工具失败返回 `isError: true`，`text` 内为 `{error, message, hint}`（`hint` 可为 null），未分类的工具异常为 `{error:"INTERNAL", message}`。多步动作的单步失败写入该步结果，正常完成外层调用时仍为 `isError: false`。

## 工具与参数

必填及可选列取自实测 `tools/list` 的 `inputSchema`。运行时的条件和额外识别字段见下文。

| 工具 | Schema 必填 | Schema 可选 | 成功时的 `text` 内容 |
| --- | --- | --- | --- |
| `app_launch` | `path`（字符串） | `args`、`title_contains`（字符串），`force_accessibility`（布尔），`preload_ms`、`timeout_ms`（整数） | `{launched, pid, launcher_pid, window, nodes, non_activating}`。两个 PID 分别是窗口所属进程与启动器进程，`window` 可为 null。 |
| `app_attach` | 无 | `title_contains`（字符串），`pid`、`preload_ms`（整数），`preload`（布尔） | `{attached, pid, nodes, preload_ms}`。`attached` 为窗口名，返回的 `preload_ms` 是已耗时间。 |
| `app_warmup` | 无 | `min_nodes`、`timeout_ms`（整数） | `{cold_nodes, stable_nodes, elapsed_ms, ready}`。`ready` 表示最终节点数达到 `min_nodes`。 |
| `app_window` | 无 | `action`、`title_contains`、`class_contains`、`foreground_policy`（字符串），`pid`、`settle_ms`（整数） | `list` 返回 `{count, windows:[{hwnd,title,klass,pid,visible,rect}]}`。其他动作返回 `{ok,action,hwnd,title,klass,pid,was_visible,visible_now,rect,hint}`。 |
| `app_cdp` | 无 | `action`、`text`、`selector`、`expression`、`key`、`url_contains`、`title_contains`（字符串），`port`、`timeout_ms`（整数），`clear_first`（布尔） | 按动作分别返回，见下表。 |
| `app_snapshot` | 无 | `interactive_only`（布尔），`max_nodes`（整数） | 缩进文本树，带节点数和耗时表头。节点行包含 ID、控件类型、名称、可选 AutomationId 和状态、矩形 `(x,y,width,height)`。 |
| `app_act` | 无 | `id`、`input`、`type`、`name`、`name_contains`、`aid`、`action`、`text`、`foreground_policy`（字符串），`target`（对象），`steps`（数组），`index`、`idle_ms`、`settle_ms`（整数），`changes`、`stop_on_error`（布尔） | 单步 `{ok,action,input,used,state,elapsed_ms}`，可带 `warning` 和 `changed`。多步返回摘要行和逐步 JSON。 |
| `app_wait` | `name_contains`（字符串） | `state`（字符串），`timeout_ms`（整数） | `{ok,state,elapsed_ms}`。 |
| `app_shot` | `path`（字符串） | `title_contains`（字符串），`pid`（整数） | `{ok,path,width,height,title,pid,visible,method:"printwindow"}`。`path` 是写入的 PNG 的绝对路径。 |

- `app_launch` 默认等待窗口 30000 ms，预加载预算为 1500 ms，已知 Chromium/Electron 程序默认加无障碍启动标志。schema 描述提到了 `preload=false`，当前 `app_launch` 始终执行预加载，`app_attach` 才读取该布尔字段。两者都额外读取 schema 未声明的 `foreground_policy`。默认尝试为窗口设置 `WS_EX_NOACTIVATE`，传 `allow` 会跳过。
- `app_attach` 运行时至少需要 `title_contains` 或 `pid`，默认 `preload=true`、`preload_ms=1500`。附加或启动成功后清除上次快照 ID。`app_warmup`、`app_snapshot`、`app_wait` 和元素操作需要已附加的窗口。
- `app_warmup` 默认 `min_nodes=200`、`timeout_ms=25000`。`app_snapshot` 默认 `interactive_only=true`、`max_nodes=600`。`app_wait` 默认 `state="appear"` 和 5000 ms，使用不区分大小写的名称子串。传 `gone` 等待消失。
- `app_window` 支持 `list`（默认）、`show`、`hide`、`focus`，包括隐藏窗口。按窗口面积降序排列，`list` 最多给 20 条明细，`count` 为全部匹配数，其他动作选首项。矩形是 `[x,y,width,height]`，`hwnd` 是十六进制字符串。默认 `settle_ms=400`，`show` 仅在 `foreground_policy="allow"` 时尝试激活，`focus` 受前台策略约束。
- `app_shot` 优先选择可见且面积较大的匹配窗口，使用 `PrintWindow` 保存 PNG。渲染失败返回 `SHOT_FAILED`，不会从屏幕复制覆盖窗口的像素。

### `app_act` 单步与多步

单步须传 `action`（`click`、`set_value`、`type`、`press`、`toggle`）和目标。目标用当前 `id` 或实时选择器，至少给 `type`、`name`、`name_contains`、`aid` 之一。可用嵌套 `target` 或同名顶层字段，运行时也识别 `automation_id` 作为 `aid` 的别名，以及 `anchor` 作为最近有名称的祖先过滤项（多个候选时）。`index` 为从 0 开始的匹配序号，省略时要求唯一匹配。名称和 AutomationId 精确匹配区分大小写，名称子串匹配忽略大小写。`set_value`、`type` 和 `press` 需要 `text`。`press` 还需要 `input="physical"`，`text` 为虚拟键名。

非空 `steps` 进入多步模式。每步有 `action` 及其参数，`action="wait"` 使用 `app_wait` 的参数。默认 `input="pattern"`、`settle_ms=150`、`foreground_policy="forbid"`。`allow_on_idle` 默认需要 3000 ms 空闲。`changes` 在单步默认关闭，在多步默认启用。`stop_on_error` 默认启用。多步文本以 `# steps ok=成功数/总数 done=执行数 elapsed_ms=耗时` 开头，每步一条 JSON，`i` 从 1 开始。动作成功行含 `i, action, input, used, ok, target, state, elapsed_ms`，可带 `warning` 和 `changed`。等待成功行含 `i, action, ok, result, elapsed_ms`。失败行含 `i, action, ok:false, error, message, hint, elapsed_ms`。

`state` 含 `found`。正常读回时还含 `name, value, password, enabled, toggle, selected, expanded`，有状态标记时含 `flags`，不支持的值可为 null。`changed` 包含非空的 `added`、`removed`、`enabled_changed` 数组（`类型|名称` 字符串），或 `{none:true}`。

### `app_cdp` 动作

连接本机 Chromium/Electron 的远程调试端口，默认 `9222`。默认选择器为 `[contenteditable="true"]`。除 `targets` 外，按 `url_contains`、`title_contains` 取首个匹配页面。连接和调用默认超时 15000 ms，`wait_for` 的轮询默认预算为 10000 ms。指定 `timeout_ms` 会同时覆盖这两个预算。HTTP 列表请求的超时最多 8000 ms。`clear_first` 默认关闭，`key` 默认是 `Enter`。

| `action` | 条件参数 | `text` 内的 JSON |
| --- | --- | --- |
| `targets`（默认） | 无 | `{count, targets:[{title,url}]}`，列出全部页面目标。 |
| `eval` | `expression` | `{ok,value}`，`value` 为字符串，JS 异常可作为 `EXC …` 字符串返回。 |
| `focus` | 无 | `{ok,used:"js_focus",focused}`。找不到选择器时 `ok` 和 `focused` 均为 false。 |
| `insert_text` / `type` | `text` | `{ok,action,used:"cdp_insert_text",input:"renderer",value}`。 |
| `clear` | 无 | `{ok,action,value}`。 |
| `click` | 无 | `{ok,action,used:"js_click",selector}`。 |
| `wait_for` | 无 | `{ok,action,selector,waited_ms}`。 |
| `key` | 无 | `{ok,action,key,input:"renderer"}`。 |

## 元素 ID 作用域

`e1, e2, …` 表示当前进程最新一次快照中的元素查找条件。每次 `app_snapshot` 都清空旧映射并从 `e1` 开始编号。客户端必须在下一次快照时丢弃整批旧 ID。当前表中没有的 ID 返回 `UNKNOWN_ID`。由于字符串会在下一次快照重复分配，服务器无法区分同名的旧 ID 和新 ID，收到当前表中已有的字符串会解析当前条目。若当前条目在实时树中找不到候选则返回 `NOT_FOUND`，有多个候选则返回 `AMBIGUOUS_MATCH`。需要跨快照定位时使用实时选择器。

## 错误码

| 代码 | 含义 |
| --- | --- |
| `BAD_ARGS` | 必要参数缺失，或参数值不符合该动作的要求。 |
| `UNKNOWN_ID` | ID 字符串不在当前快照中。 |
| `NOT_ATTACHED` | 该操作需要先附加窗口。 |
| `NOT_FOUND` | 实时树中找不到对应元素，或给定的匹配序号超出范围。 |
| `AMBIGUOUS_MATCH` | 实时树中有多个元素符合条件，无法确定唯一目标。 |
| `WINDOW_NOT_FOUND` | 没有顶层窗口匹配该操作的筛选条件。 |
| `WINDOW_TIMEOUT` | 启动后在给定时间内没找到窗口。 |
| `WINDOW_TOO_SMALL` | 截图时读到的窗口宽度或高度不大于零。 |
| `LAUNCH_FAILED` | Windows 创建进程失败，消息带 Win32 错误号。 |
| `LAUNCH_HANDED_OFF` | 启动器先退出，且调用未传标题来继续寻找窗口。 |
| `PASSWORD_FIELD` | 密码控件上的模式输入 `type` 需要真实按键。 |
| `PHYSICAL_ONLY` | `press` 需要 `input="physical"`。 |
| `NO_PATTERN_TYPING` | 当前控件没有可用于追加文本的 Value 模式或原生输入路径。 |
| `NO_VALUE_PATTERN` | 当前控件没有可用于设置文本的 Value 模式或原生输入路径。 |
| `NO_TOGGLE` | 控件既不支持 Toggle，也不支持 SelectionItem。 |
| `USER_ACTIVE` | `allow_on_idle` 的空闲时间门槛尚未达到。 |
| `FOREGROUND_FORBIDDEN` | 前台策略禁止这次需要接管前台的动作。 |
| `SHOT_FAILED` | `PrintWindow` 没能渲染目标窗口。 |
| `WAIT_TIMEOUT` | 在期限内未看到元素出现或消失。 |
| `UNKNOWN_TOOL` | `tools/call` 给出的工具名不存在。 |
| `CDP_UNAVAILABLE` | 读不到指定调试端口上的页面列表。 |
| `CDP_NO_TARGET` | 无匹配页面，或页面未提供调试 WebSocket 地址。 |
| `CDP_CONNECT` | 调试 WebSocket 连接失败。 |
| `CDP_ERROR` | 调试端对 `Runtime.evaluate` 返回了协议错误。 |
| `CDP_NO_EDITOR` | 插入非空文本时，选择器没有命中编辑器。 |
| `CDP_NO_ELEMENT` | 点击时，选择器没有命中元素。 |
| `CDP_TIMEOUT` | 轮询期间选择器一直没有命中元素。 |
| `INTERNAL` | 工具或单步动作抛出未归类的异常。 |

## V0.6 事件订阅与快照增量

服务端版本为 `0.6.0`。事件能力加入一个工具 `app_events`。原有工具调用方式保持兼容，`app_snapshot` 没有 `since` 时继续返回原快照文本。工具总数为 10。

| action | 参数 | 内容 |
| --- | --- | --- |
| `subscribe` | `window`，含 `pid` 或 `title_contains`。可选 `kinds`、`debounce_ms`（默认 80，范围 0–1000）、`lease_s`（默认 60，范围 1–900） | 返回 `subscription_id`、初始 `cursor`、窗口身份、过期时间和已注册处理器数。最多四个活跃订阅。 |
| `poll` | `since` 或 `subscription_id`。可选 `limit`（默认 64，范围 1–256）、`max_bytes`（默认 16384，范围 512–262144） | 返回 `events`、下一页 `cursor`、本页 `unmatched` 计数和 `has_more`。不等待未来事件。 |
| `status` | 可选 `subscription_id` | 有 ID 时返回本订阅计数，无 ID 时返回活跃订阅与处理器数量。 |
| `unsubscribe` | `subscription_id` | 停止订阅，返回实际移除的处理器数量。 |

`kinds` 默认包括 `structure`、`property`、`appear`、`gone`、`focus`。焦点由窗口子树内的 UIA `HasKeyboardFocus` 属性通知提供。UIA 回调只入队，窗口身份、名称和文本读回在原 STA 上解析。密码状态无法明确判定时，值与文本留空。属性通知按节点、类别和属性合并，记首次已知旧值、最后新值，标 `merged`。结构通知触发有限子树复读，`appear` 和 `gone` 标 `reconciled`。去抖窗口内出现又消失的节点可能不出现在净变化中。事件流不能作为逐个中间状态的业务审计。

每个事件有 `seq`、`ts`、`kind`、`window`、`node`、`change` 和 `confidence`。使用 `seq` 排序和去重。回调采样的 `ts` 可能因合并或结构复读而与队列顺序不同。只有窗口及节点身份与最近完整快照唯一对应时，`node.id` 才携带该快照的 `eN`，`node.snapshot` 标出服务器内部快照代数。对不上的事件保留 `node.unmatched: true`。

`app_snapshot { since: "<cursor>" }` 返回 `changes`、`cursor`、`unmatched` 和 `has_more`。只输出出现、消失和属性变化，游标仍跨过同一订阅中的其他事件。增量查询不会重建 `eN` 映射。完整快照没有订阅限制，也不会清空事件日志。

游标形态为 `<subscription_id>:<seq>`，从上次已消费序号的下一项读取。重复使用同一游标可以重放尚在日志中的页面。队列最多 1024 条原始通知、日志最多 2048 条已输出事件。游标落后于日志保留窗口，或回调队列发生丢弃时，返回 `EVENTS_CURSOR_STALE`，需要重新订阅并获取完整快照。单条事件超过 `max_bytes` 时返回 `EVENTS_MAX_BYTES`，游标保持原位。字节限制适用于工具返回的内层 JSON，外层 JSON-RPC 编码可能更大。

| 错误码 | 含义 |
| --- | --- |
| `EVENTS_BUSY` | 活跃订阅达到上限。 |
| `EVENTS_UNKNOWN_SUBSCRIPTION` | 无有效订阅 ID，或过旧的结束记录已清除。 |
| `EVENTS_BAD_CURSOR` | 格式错误、与订阅不符或序号超前。 |
| `EVENTS_CURSOR_STALE` | 日志溢出或通知丢失，需要重建观察基线。 |
| `EVENTS_MAX_BYTES` | 当前字节预算装不下一条事件。 |
| `EVENTS_LEASE_EXPIRED` | 租约到期，需重新订阅。 |
| `EVENTS_TARGET_GONE` | 目标窗口已经关闭或 HWND 所属进程改变。 |
| `EVENTS_UNSUBSCRIBED` | 已主动取消。 |
| `EVENTS_CLEANUP_FAILED` | 显式取消时至少一个处理器摘除失败。 |

租约从订阅成功前设定的时点起计算，读取不续租。租约到期、目标丢失和服务退出都会尝试清理 UIA 处理器。没有查询 ID 的 `status` 只展示活跃订阅。


## 版本规则

1.0 之前，破坏性工具接口变更提升 minor 版本。`serverInfo.version` 读取程序集的 InformationalVersion，其值来自项目文件的 `Version`。工具版本升级保持 MCP `protocolVersion` 独立。

