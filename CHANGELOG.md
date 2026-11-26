# Changelog

## Unreleased

- `tests/foreground-sentinel.ps1` 重写判定:每条 WinEvent 分三档 —— `steal`(前台离开基准窗口,或焦点落进一个能被激活、当时却不在前面的窗口)、`inert`(焦点落在 `WS_EX_NOACTIVATE` 窗口里,拿不走键盘)、`same-window`(事件落在本来就在前面的窗口里)。只有 `steal` 判失败。旧版只要 root≠基准就记违规,把自己那条命令把控制台顶到前台也算成抢占。
- 基准窗口改为「等前台稳定 400ms 再取」;`-WatchPids` 模式下先等前台离开被盯的进程,等不到就在报告里写 `baselineIsWatched: true`。
- `-WatchPids` 从 `[int[]]` 改成收逗号串(原来用 `-File` 传两个 pid 会参数绑定失败)。`passed` 在 watch 模式下不再要求 `endForeground == baselineForeground`:人自己切窗口不该算违规。
- 报告新增 `watchMode`、`watchedPids`、`baselineIsWatched`、`foregroundUnchanged`、`stealCount` 和三类事件的完整明细。
- 实测(33 步计算器验收,`-WatchPids`):33/33 步全过,`stealCount: 0`、`inertFocusCount: 0`、`sameWindowCount: 0`,前台句柄 0x50842 → 0x50842。反向对照(后台进程对记事本 `SetForegroundWindow`)抓到 1 `FOREGROUND` + 2 `FOCUS`,证明钩子有效。

## 0.6.0 — 2026-11-26

- 新增 `app_events` 工具（支持 `subscribe`、`poll`、`status`、`unsubscribe`），工具总数从 9 个增至 10 个，提供窗口作用域 UIA 事件订阅、游标分页与租约过期清理（最多 4 个活跃订阅）；事件流不作为逐条中间状态的业务审计，去抖窗口内出现又消失的节点可能不出现。
- `app_snapshot` 新增可选参数 `since`，传入事件游标时返回仅包含出现、消失与属性变化的 JSON 增量，不传时保持原有文本快照输出。
- 新增事件相关错误码：`EVENTS_BUSY`、`EVENTS_UNKNOWN_SUBSCRIPTION`、`EVENTS_BAD_CURSOR`、`EVENTS_CURSOR_STALE`、`EVENTS_MAX_BYTES`、`EVENTS_LEASE_EXPIRED`、`EVENTS_TARGET_GONE`、`EVENTS_UNSUBSCRIBED`、`EVENTS_CLEANUP_FAILED`。
- `set_value` 新增可选参数 `unit`（`auto`、`native`、`percent`，默认 `auto`），传入其他值报 `BAD_ARGS`；针对仅暴露百分比 `ValuePattern` 的 Win32 TrackBar 等控件，写入前按控件范围换算，写入后通过 `TBM_GETPOS` 轮询真值校验，不符时报 `VALUE_READBACK_MISMATCH`。
- 收紧 `set_value` 的范围与真值校验：越界严卡控件区间并报 `VALUE_OUT_OF_RANGE`，范围退化或需换算但读不到真值时报 `NO_GROUND_TRUTH`，并调整宽范围滑块的回读容差以避免合法写入误报；成功回执新增 `unit`、`requested`、`applied`、`applied_verified`、`native_value`、`native_min`、`native_max` 字段（拿不到真值时不输出 `applied` 与 `applied_verified`）。
- 改进找窗与事件订阅的资源管理：找窗阶段及时释放候选窗口引用，事件处理器支持幂等注册，无焦点预热注册失败时可回滚。
- 版本号升级至 `0.6.0`，[MCP 契约](docs/mcp-contract.md) 补充事件接口、`unit` 参数及新增错误码说明，并补充事件租约与滑块真值写入的实时回归测试。

## 0.5.0 — 2026-11-24

- 首次为基线 `cbb37cd` 的九个 MCP 工具定版，包含 `app_launch`、`app_attach`、`app_warmup`、`app_window`、`app_cdp`、`app_snapshot`、`app_act`、`app_wait` 和 `app_shot`。
- `app_snapshot` 输出快照作用域的元素 ID 和控件状态标记，例如 `[on]`、`[off]`、`[selected]`、`[expanded]`、`(disabled)`。`app_act` 可执行多步操作并报告操作后状态。
- `app_shot` 通过 `PrintWindow` 将目标窗口保存为 PNG。
- 启动或附加窗口时默认尝试设置 `WS_EX_NOACTIVATE`，减少 UIA 操作夺取前台焦点的情况。
- `initialize` 从程序集读取版本。发布提供 Windows x64 单文件，接口见 [MCP 契约](docs/mcp-contract.md)。

