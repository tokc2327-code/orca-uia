# Changelog

## 0.5.0 — 2026-11-24

- 首次为基线 `cbb37cd` 的九个 MCP 工具定版，包含 `app_launch`、`app_attach`、`app_warmup`、`app_window`、`app_cdp`、`app_snapshot`、`app_act`、`app_wait` 和 `app_shot`。
- `app_snapshot` 输出快照作用域的元素 ID 和控件状态标记，例如 `[on]`、`[off]`、`[selected]`、`[expanded]`、`(disabled)`。`app_act` 可执行多步操作并报告操作后状态。
- `app_shot` 通过 `PrintWindow` 将目标窗口保存为 PNG。
- 启动或附加窗口时默认尝试设置 `WS_EX_NOACTIVATE`，减少 UIA 操作夺取前台焦点的情况。
- `initialize` 从程序集读取版本。发布提供 Windows x64 单文件，接口见 [MCP 契约](docs/mcp-contract.md)。

