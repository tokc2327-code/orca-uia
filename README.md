# orca-uia

**A stdio MCP server that lets an agent drive Windows desktop apps through UI Automation 3.**

Not screen-scraping, not pixel coordinates: it walks the UIA control tree, addresses elements by
type/name/AutomationId, and acts through UIA patterns by default — no mouse, no keyboard, and the
target is re-resolved against the live tree before every action, so a stale step can never click
the wrong thing.

- **Windows only** (.NET 8, `net8.0-windows`, FlaUI 5.0.0 / UIA3).
- **No MCP SDK dependency** — JSON-RPC 2.0, newline-delimited, over stdin/stdout.
- **Snapshot-scoped ids**: `app_snapshot` returns `e1, e2, …`; ids die on the next snapshot on purpose.

## Why

Driving a real desktop app from an agent usually means one of: byte-sized screenshots (slow, blind
to structure), blind coordinate clicks (brittle), or an app-specific automation API (doesn't scale).
UIA gives you structure and semantics for every native, WPF, WinForms, Electron and Chromium app on
Windows — this server just makes it addressable over MCP, with the failure modes named.

## Tools

| Tool | What it does |
| --- | --- |
| `app_launch` | Start an exe and attach to its main window. Adds `--force-renderer-accessibility` for known Electron/Chromium hosts only. |
| `app_attach` | Attach to a running window by title substring or pid, preloading the tree (Electron builds it lazily). |
| `app_warmup` | Poll node count until it settles — the usual fix for an empty Electron tree. |
| `app_window` | List / show / hide / focus top-level windows, including hidden and tray windows (UIA can't see those, the HWND is still there). |
| `app_snapshot` | The control tree as text, with snapshot-scoped ids and per-node state (`[on]` / `[off]` / `[selected]` / `[expanded]` / `(disabled)`). |
| `app_act` | Click / set_value / type / press / toggle by snapshot id **or** live target, single step or `steps[]` sequence. Reports post-state and what appeared/vanished, so you don't need a screenshot between steps. |
| `app_wait` | Wait until an element whose name contains a substring appears or disappears. |
| `app_shot` | PNG of one window (`PrintWindow`, screen-copy fallback) for vision-capable clients. |
| `app_cdp` | Escape hatch for Chromium/Electron contenteditable: talks to the renderer's DevTools protocol when UIA has no Value pattern and no HWND (needs `--remote-debugging-port`). |

`app_act` takes `input: "pattern"` (default — UIA patterns only) or `"physical"` (real mouse and
keyboard) when an app only reacts to real input.

## Build

```powershell
dotnet build src/OrcaUia.csproj -c Release
# -> src/bin/Release/net8.0-windows/orca-uia.exe
```

## Wire it into an MCP client

```json
{
  "mcpServers": {
    "uia": { "command": "D:/orca-uia/src/bin/Release/net8.0-windows/orca-uia.exe", "args": [] }
  }
}
```

`examples/cordis.patch.example.yml` is the same thing for the DeepSeek Harness bundle format.

There is also `clients/uia-driver.cjs`, a tiny scripted driver: give it a JSON job
(`{ "title": "window title", "steps": [{ "tool": "app_snapshot", "args": {} }] }`) and it spawns the
server, runs the steps and prints the receipts. Point it at your build with `ORCA_UIA_EXE`.

```powershell
$env:ORCA_UIA_EXE = 'D:/orca-uia/src/bin/Release/net8.0-windows/orca-uia.exe'
node clients/uia-driver.cjs job.json
```

## Known limits

- **Snapshot ids are single-use.** After any action the id may be gone: re-snapshot, or use a live
  target (`{type, name, name_contains, aid}`), which is re-resolved each call.
- **Packaged/UWP apps** (Windows Calculator, Settings) don't answer `app_attach` by title — use
  `pid`. The pid changes on every launch.
- **Electron/Chromium** only builds its accessibility tree when asked: expect `app_warmup`, and add
  `--force-renderer-accessibility` for hosts that don't do it on their own.
- **Elevated windows** can't be driven from a non-elevated process: UIA is blocked by UIPI, and the
  failure looks like an empty tree rather than an error.
- **Password fields** are masked by UIA by design; the server reports `IsPassword` and never tries
  to read the value.
- The engine is single-threaded on one STA apartment (UIA3 requires it) and pumps a Win32 message
  queue between requests — a blocking UIA call stalls the whole server until its timeout.

## Status

Early but usable: it drives Notepad, Windows Calculator and Electron apps (DeepSeek Harness,
Cursor) end to end. MIT licensed. Issues and counter-examples are welcome — especially apps where
the tree is empty, stale, or the action silently doesn't take.
