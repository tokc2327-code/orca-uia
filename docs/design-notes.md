# Design notes

How the server is put together, and where it is still weak. These are the notes a reviewer asked
for; they're kept in the repo because the constraints are not obvious from the code.

## Shape

```
stdin (reader thread) ──queue──> worker thread (STA) ──> Engine ──> UIA3 / FlaUI
                                          │
                                    Win32 message pump
```

- `Program.cs` — the stdio loop: `initialize` / `ping` / `tools/list` / `tools/call`, newline
  delimited JSON-RPC 2.0, no SDK. The reader thread only feeds a queue, so a slow tool call never
  blocks the pipe and the client can always write.
- `Engine.cs` — everything UIA: attach, per-level `CacheRequest`, snapshot generation with
  snapshot-scoped ids (`e1`, `e2`, …), strict re-resolution before each action, post-action state
  and change sets back to the client. Errors are `ToolError(code, message, hint)`.

## Why one STA thread

UIA3 is apartment-threaded COM. The engine therefore lives on a single STA thread that also owns a
Win32 message queue; some COM/UIA calls post messages back to the calling apartment and will
time out if nobody pumps them. The pump runs between requests.

## Failure modes, named on purpose

| Situation | What comes back |
| --- | --- |
| Snapshot id from an older snapshot | `UNKNOWN_ID` — re-snapshot, or use a live target |
| Target is outside the pinned scope | `SCOPE_DENIED` |
| Nothing attached yet | `NOT_ATTACHED` |
| Window not found by title/pid | `WINDOW_NOT_FOUND` |
| Wait did not observe the state | `WAIT_TIMEOUT` |
| Version mismatch on a write | `VERSION_CONFLICT` |

An agent that only reads the message string is meant to be able to recover: every error says what
to do next, not just what failed.

## Open questions (the ones still worth a look)

1. **Lifetime of `UIA3Automation` / `CacheRequest`** under a long-lived MCP loop: is anything
   leaking or going stale after hours? Not measured yet.
2. **"Strict re-resolution" under a changing UI** — is re-resolving by type+name+aid+index actually
   enough to prevent a click on the wrong element after the window shifts, or does it need a
   structural fingerprint?
3. **Electron/Chromium warmup**: `--force-renderer-accessibility` plus polling until the node count
   settles works, but there is no principled timeout — it is empirical.
4. **Paths that can hang an agent**: one blocking UIA call stalls the whole server; the timeouts are
   per tool call, not per UIA call.

## Acceptance so far

Windows Calculator `1 + 2 = 3` five consecutive times in the same process; Notepad and Electron
apps (DeepSeek Harness, Cursor) end to end. **All within one process, one boot** — there is no
cross-process or cross-restart data yet, so the "works after weeks of uptime" claim is untested.
