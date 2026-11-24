> Part of the **Visual Collaboration** ecosystem — structured visual context and verifiable human-agent collaboration.

# orca-uia

**0.5.0** · [中文](README.zh-CN.md)

**Pattern-first, zero-mouse Windows UI Automation MCP server for AI agents.**

Operate Win32, WPF, Electron, and UWP desktop apps via structured control trees and UIA patterns — without hijacking your cursor, stealing your keyboard, or burning tokens on repeated screenshots.

[![.NET 8](https://img.shields.io/badge/.NET-8.0%20Windows-512BD4.svg)](#quick-start)
[![UIA3](https://img.shields.io/badge/Windows-UIA3%20%2F%20FlaUI%205.0-0078D6.svg)](#architecture--known-boundaries)
[![Protocol](https://img.shields.io/badge/MCP-stdio%20JSON--RPC%202.0-brightgreen.svg)](#tools-9)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](#license)

---

## Why orca-uia?

Most desktop agents rely on screenshot-and-click loops that break on window movement, burn vision tokens, and lock the user out of their own mouse and keyboard. **orca-uia** is built around five engineering rules:

- **Zero Mouse / Keyboard by Default (`input: "pattern"`):** Clicks use UIA Patterns (`Invoke`, `Toggle`, `SelectionItem`, `ExpandCollapse`) and text entry uses `ValuePattern` or Win32 `WM_SETTEXT` / `EM_REPLACESEL`. Real OS input (`input: "physical"`) is strictly opt-in.
- **Strict Re-Resolution (Never Guess):** Snapshot IDs (`e1`, `e2`...) are snapshot-scoped recipes, never stale COM handles. Every action re-resolves strictly: `0` matches throws `NOT_FOUND`, `>1` throws `AMBIGUOUS_MATCH`.
- **Multi-Step Execution with Diff Verification (`Execute → Verify`):** Pass `steps[]` with live selectors (`type`, `name`, `name_contains`, `aid`, `anchor`) to execute an entire workflow in **one MCP call**. Every step returns post-state and a control-tree diff (`added` / `removed` / `enabled_changed`), eliminating blind sleeps and intermediate screenshots.
- **Built for Real-World Windows Quirks:** Ships with Chromium/Electron accessibility tree preloading, a built-in CDP bridge (`app_cdp`) for `contenteditable` editors, and Win32 `HWND` window management (`app_window`) for hidden/tray windows that UIA cannot see.
- **Zero MCP SDK Bloat:** Single C# / .NET 8 executable running a dedicated STA thread with a Win32 message pump over newline-delimited stdio JSON-RPC 2.0.

---

## Supported Surfaces

| Surface | Examples | Primary Control Path | Notes |
| --- | --- | --- | --- |
| **Electron / Chromium** | DeepSeek Harness, VS Code, Cursor | UIA3 (`--force-renderer-accessibility`) + `app_cdp` | Use `--remote-debugging-port` for `contenteditable` typing & DOM clicks |
| **Win32 / WPF** | Notepad, native desktop tools | UIA3 Patterns + `WM_SETTEXT` / `EM_REPLACESEL` | Best background compatibility; hidden/tray windows reachable via `app_window` |
| **UWP / Packaged** | Windows Calculator, Settings | UIA3 Patterns | Attach by `pid` or `title_contains` (`ApplicationFrameHost` boundary) |

---

## Quick Start

### 1. Prerequisites

- Windows 10 version 1809 or later, x64.
- **Nothing else** if you use the published build: `dist/win-x64/orca-uia.exe` is a self-contained single file and does not need a .NET runtime installed.
- Only to build from source: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

### 2. Get the executable

From a checkout of this repo:

```powershell
dotnet publish src/OrcaUia.csproj -c Release -r win-x64
# -> dist/win-x64/orca-uia.exe
```

Note the project file lives in `src/`, not at the repo root. Git keeps the output directory via `dist/.gitkeep`; the executable itself is a local build artifact.

Move that one file anywhere and point your MCP client at it — no install step.

### 3. Add to your MCP client

Register the executable as a stdio MCP server:

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

- `examples/cordis.patch.example.yml` is the same thing for the DeepSeek Harness bundle format.
- `clients/uia-driver.cjs` is a tiny scripted driver for testing without an agent: give it a JSON job (`{ "title": "window title", "steps": [{ "tool": "app_snapshot", "args": {} }] }`), it spawns the server, runs the steps and prints the receipts. Point it at your build with `ORCA_UIA_EXE`.
- Full parameter, response, element-ID-scope and error-code reference: [docs/mcp-contract.md](docs/mcp-contract.md).

---

## Tools (9)

| Tool | Purpose | Key Parameters |
| --- | --- | --- |
| `app_launch` | Launch an app (auto-injects `--force-renderer-accessibility` for known Chromium/Electron hosts) and attach | `path`, `args`, `title_contains`, `force_accessibility`, `preload_ms` |
| `app_attach` | Attach to a running top-level window (skips tiny tray/IME stubs) and preload its UIA tree | `title_contains`, `pid`, `preload`, `preload_ms` |
| `app_warmup` | Poll descendant node count until a lazily-built Electron/Chromium AX tree stabilizes | `min_nodes`, `timeout_ms` |
| `app_window` | List, show, hide, or focus top-level `HWND`s — including hidden/tray windows invisible to UIA | `action` (`list\|show\|hide\|focus`), `pid`, `title_contains`, `class_contains` |
| `app_cdp` | Drive Chromium/Electron `contenteditable` editors & DOM directly via DevTools Protocol (zero OS input) | `action` (`targets\|eval\|focus\|insert_text\|clear\|click\|wait_for\|key`), `port`, `selector`, `text` |
| `app_snapshot` | Dump the visible control tree as compact text with snapshot-scoped IDs (`e1`, `e2`...) and state flags | `interactive_only`, `max_nodes` |
| `app_act` | Execute a single action or a multi-step `steps[]` sequence by snapshot ID or live selector, returning post-state & diff | `id` or `target`, `action` (`click\|set_value\|type\|press\|toggle`), `input` (`pattern\|physical`), `steps[]`, `changes` |
| `app_wait` | Wait until an element whose name contains a substring appears or disappears | `name_contains`, `state` (`appear\|gone`), `timeout_ms` |
| `app_shot` | Capture a PNG of a single window (`PrintWindow` with `PW_RENDERFULLCONTENT`) for vision fallback | `path`, `title_contains`, `pid` |

---

## Example: Multi-Step Workflow in One Call

Because live selectors re-resolve against the current tree on every step, an agent can chain interactions across UI transitions without stopping to re-snapshot:

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

Each step returns a structured line with the actual mechanism taken (`used`: `invoke`, `value`, `wm_settext`, etc.), readback `state`, and `changed` (`added` / `removed` / `enabled_changed`).

---

## Running a job without stealing the screen

Driving a real app means the app comes to the front. On a machine someone is using, that costs them their typing and pollutes their screenshots, so a run should give the screen back:

1. **Ask first.** A scripted run takes seconds and moves focus; start it when the machine is free. `app_launch` / `app_window show` already ask UIA to show the window without activating it (`WS_EX_NOACTIVATE`, `SW_SHOWNOACTIVATE`), and `foreground_policy` defaults to `forbid`, so a step that would need the foreground is refused with a hint instead of silently taking over.
2. **Minimise what you launch.** `Start-Process notepad -WindowStyle Minimized`, or launch and move focus back before attaching. A UWP app can ignore the flag — it should be closed afterwards.
3. **Let the driver clean up.** Two job-level options in `clients/uia-driver.cjs`:

```json
{ "title": "…",
  "restore_foreground": "SomeApp",
  "kill_on_exit": "CalculatorApp",
  "steps": [ … ] }
```

`restore_foreground` (or `ORCA_UIA_FOREGROUND` in the environment) re-focusses the window whose title contains that string when the run ends; `kill_on_exit` closes the app the job drove — give a process name, or `true` to use the process the attach reply names. Both print what they did (`FOREGROUND …`, `KILL …`), so a run that fails mid-way still leaves the desktop as it found it.
4. **Measure it, don't assume it.** `tests/foreground-sentinel.ps1` hooks `EVENT_SYSTEM_FOREGROUND` and `EVENT_OBJECT_FOCUS`, runs the job, and sorts every event into three buckets:

   - `steal` — the foreground left the baseline window, or focus landed in an activatable window that was not in front. The keyboard really moved; the run fails.
   - `inert` — focus inside a `WS_EX_NOACTIVATE` window. Such a window cannot activate, so its internal focus cannot take the keyboard; recorded, but not a failure.
   - `same-window` — an event inside whatever was already in front. Nobody was moved.

```powershell
pwsh -File tests\foreground-sentinel.ps1 `
  -RunCommand 'node clients\uia-driver.cjs examples\acceptance-calculator-cn.json' `
  -ReportPath evidence\fg.json -WatchPids "<CalculatorApp pid>,<ApplicationFrameHost pid>"
```

`-WatchPids` narrows the watch to the driven processes. Without it, a person moving their own windows is indistinguishable from a steal and gets reported as one — the script is then only usable on an idle machine. Measured on the 33-step calculator acceptance with `-WatchPids`: **33/33 steps ok, `stealCount: 0`, `inertFocusCount: 0`, `sameWindowCount: 0`, the foreground handle unchanged from start to end.** A hostile control (`SetForegroundWindow` on a Notepad from a background process) is still caught — 1 `FOREGROUND` + 2 `FOCUS` steals — so the hook is awake.

One trap in the setup: if the driven window happens to be in front when the sentinel starts, every focus event inside it is measured against itself, and the run looks cleaner than it is. The constructor waits up to 5 s for the foreground to leave the watched pids and the report says `baselineIsWatched: true` when it could not.

---

## Architecture & Known Boundaries

- **Single STA Thread + Message Pump:** `Program.cs` reads `stdin` on a dedicated reader thread and dispatches all UIA3/COM calls on one STA worker thread with an explicit Win32 `PeekMessage`/`DispatchMessage` pump, preventing cross-apartment COM timeouts.
- **Structured Errors:** Failures return `{ "error": "<CODE>", "message": "...", "hint": "..." }` (`NOT_FOUND`, `AMBIGUOUS_MATCH`, `UNKNOWN_ID`, `NO_VALUE_PATTERN`, `CDP_UNAVAILABLE`, etc.) so agents can self-correct deterministically.
- **Snapshot IDs are scoped to the latest snapshot.** They are re-resolved server-side, but a new snapshot restarts numbering at `e1`: an old ID that happens to name an existing entry will resolve to the *new* entry with that number. Discard IDs after a snapshot, or use a live selector (`{type, name, name_contains, aid}`).
- **Known Windows Boundaries:**
  - **UIPI:** A Medium-IL `orca-uia` process cannot control elevated (Administrator) windows without `uiAccess` signing. The failure looks like an empty tree, not an error.
  - **Electron Editors:** `contenteditable` boxes ignore UIA `ValuePattern` and have no `HWND`; start the target with `--remote-debugging-port=<port>` and use `app_cdp`.
  - **UWP / Packaged Apps:** Launchers hand off to `ApplicationFrameHost.exe`; attaching by title lands on the frame window and can return an empty tree — attach by the owner `pid` instead. The pid changes on every launch.
  - **Password fields** are masked by UIA by design; the server reports `IsPassword` and never tries to read the value.
  - A blocking UIA call stalls the whole server until its timeout, because every call shares the one STA thread.

---

## Roadmap

- [x] 9 core MCP tools over stdio JSON-RPC 2.0 (STA + Win32 message pump)
- [x] Multi-step `steps[]` live-selector execution with tree diff & readback warnings
- [x] Built-in `app_cdp` & `app_window` for Electron and tray apps
- [x] `foreground_policy` guard (`forbid` / `allow_on_idle` / `allow`) plus non-activating launch/show — `WS_EX_NOACTIVATE`, `SW_SHOWNOACTIVATE`
- [x] Foreground sentinel (`tests/foreground-sentinel.ps1`) that sorts each WinEvent into `steal` / `inert` / `same-window` instead of calling every focus event a steal
- [x] No focus events on the driven window during a scripted run — measured on the 33-step calculator acceptance: 0 steals, 0 focus events, foreground handle unchanged start to end
- [ ] Sentinel as an automated acceptance gate (needs an interactive desktop; today it is a manual step)
- [ ] Shadow Desktop (`CreateDesktop`) launcher for invisible Win32 & Electron isolation
- [ ] Raw JSON tree dump for **Visual Protocol v0** cross-surface auditing

---

## Ecosystem

`orca-uia` serves as the Windows Desktop Surface Adapter for **Visual Collaboration** — alongside [`figma-ordered-pins`](https://github.com/tokc2327-code/figma-ordered-pins) for Figma canvases.

## License

MIT — see [LICENSE](LICENSE). Issues and counter-examples are welcome, especially apps where the tree is empty, stale, or the action silently doesn't take.
