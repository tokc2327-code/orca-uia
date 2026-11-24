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
