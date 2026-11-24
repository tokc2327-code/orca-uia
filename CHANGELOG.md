# Changelog

## Unreleased

- `tests/foreground-sentinel.ps1` 重写判定:每条 WinEvent 分三档 —— `steal`(前台离开基准窗口,或焦点落进一个能被激活、当时却不在前面的窗口)、`inert`(焦点落在 `WS_EX_NOACTIVATE` 窗口里,拿不走键盘)、`same-window`(事件落在本来就在前面的窗口里)。只有 `steal` 判失败。旧版只要 root≠基准就记违规,把自己那条命令把控制台顶到前台也算成抢占。
- 基准窗口改为「等前台稳定 400ms 再取」;`-WatchPids` 模式下先等前台离开被盯的进程,等不到就在报告里写 `baselineIsWatched: true`。
- `-WatchPids` 从 `[int[]]` 改成收逗号串(原来用 `-File` 传两个 pid 会参数绑定失败)。`passed` 在 watch 模式下不再要求 `endForeground == baselineForeground`:人自己切窗口不该算违规。
- 报告新增 `watchMode`、`watchedPids`、`baselineIsWatched`、`foregroundUnchanged`、`stealCount` 和三类事件的完整明细。
- 实测(33 步计算器验收,`-WatchPids`):33/33 步全过,`stealCount: 0`、`inertFocusCount: 0`、`sameWindowCount: 0`,前台句柄 0x50842 → 0x50842。反向对照(后台进程对记事本 `SetForegroundWindow`)抓到 1 `FOREGROUND` + 2 `FOCUS`,证明钩子有效。

## 0.5.0 — 2026-11-24

- 首次为基线 `cbb37cd` 的九个 MCP 工具定版，包含 `app_launch`、`app_attach`、`app_warmup`、`app_window`、`app_cdp`、`app_snapshot`、`app_act`、`app_wait` 和 `app_shot`。
- `app_snapshot` 输出快照作用域的元素 ID 和控件状态标记，例如 `[on]`、`[off]`、`[selected]`、`[expanded]`、`(disabled)`。`app_act` 可执行多步操作并报告操作后状态。
- `app_shot` 通过 `PrintWindow` 将目标窗口保存为 PNG。
- 启动或附加窗口时默认尝试设置 `WS_EX_NOACTIVATE`，减少 UIA 操作夺取前台焦点的情况。
- `initialize` 从程序集读取版本。发布提供 Windows x64 单文件，接口见 [MCP 契约](docs/mcp-contract.md)。

