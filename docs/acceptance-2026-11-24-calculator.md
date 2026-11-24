# Acceptance run · Windows Calculator · 2026-11-24

Scripted end-to-end run through the MCP server, 33 steps, every step asserted. Target:
Windows 11 Calculator (`CalculatorApp`), **Simplified Chinese UI**, so the job file matches on
Chinese names (`显示为 N` = "Display is N"); the element ids it drives (`num1Button`, `equalButton`,
…) are locale-independent.

Command:

```powershell
$env:ORCA_UIA_EXE = 'D:/orca-uia/src/bin/Release/net8.0-windows/orca-uia.exe'
node clients/uia-driver.cjs examples/acceptance-calculator-cn.json
```

Result: `SUMMARY steps=33 ok=33 failed=0`, exit code 0.

## What the steps cover

- **Baseline**: attach by window title (`计算器`), clear, snapshot shows `显示为 0`.
- **13 arithmetic cases**, each asserted on the *display name*, all different values so a stale
  expectation cannot pass by accident: `1+2=3`, `7×8=56`, `15−5=10`, `2×3×4=24`, `√81=9`, `9²=81`,
  `1/5=0.2`, `200+10%=220`, `1/3=0.333333333333…`, `0.5+0.5=1`, `−6×2=−12`, `999+1=1,000`, `47×47=2,209`.
- **Edit keys**: 清除条目 + 7 + Backspace → back to `0`.
- **Negative cases**: a stale snapshot id must fail with `UNKNOWN_ID` (`id e999 not in current
  snapshot`), an unknown AutomationId with `NOT_FOUND` (`no element matches [aid=noSuchButton]`).
- **Window enumeration**: `app_window list` still finds the window (`ApplicationFrameWindow`, visible).

Every `app_act` step also asserts that no sub-step came back `"ok":false`, so a click that
silently did nothing fails the run instead of passing.

## What it caught

Real differences between what you'd write from intuition and what the UI actually does:

1. `50` then `%` on its own gives **0**, not 0.5 — percent is relative to the pending operand, so
   the case has to be `200 + 10 %` → `220`.
2. Numbers ≥ 1000 render with a **thousands separator**: `1,000`, `2,209`. Matching on `1000`
   times out.
3. Non-ASCII in replies is **JSON-escaped** (`\u8BA1\u7B97\u5668`), because the server uses the
   default `JsonSerializer` encoder. Valid JSON, but noisy for an agent reading the text — worth
   switching to `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.
4. The display node is only in the **full** snapshot (`interactive_only: false`); with the default
   interactive-only filter, `CalculatorResults` is hidden and there is nothing to assert on.

## Not covered here

One process, one boot. No cross-process or after-hours-uptime run; Electron/Chromium targets and
`app_cdp` are a separate check.

## Foreground: measured, not assumed

The 33 steps above were re-run under `tests/foreground-sentinel.ps1`, watching only the driven
processes (`-WatchPids "<CalculatorApp pid>,<ApplicationFrameHost pid>"`):

| run | steps | `steal` | `inert` | `same-window` | foreground start → end |
|---|---|---|---|---|---|
| calculator acceptance | 33/33 ok | 0 | 0 | 0 | `0x50842` → `0x50842` |

No WinEvent of either kind fired on the driven window: the app is never activated, and no focus
event escapes into it. The earlier "1 FOCUS on the calculator frame" was an artefact of the
measurement: the old sentinel took "whatever had the foreground when the script started" as the
baseline, so when the calculation had just been launched it counted the app's own focus against
itself, and when the console came up it counted the harness.

Control, so the hook is known to be awake: a background process calling `SetForegroundWindow` on
Notepad is caught as 1 `FOREGROUND` + 2 `FOCUS` steals. An `inert` record — a focus event inside a
`WS_EX_NOACTIVATE` window — has not been produced deliberately yet; that branch of the classifier
is code-only.
