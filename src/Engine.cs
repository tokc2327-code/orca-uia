// Core UIA engine. Design rules taken from the 2026-11-22 research report:
//  1. Warm up Chromium/Electron trees: subscribe UIA events + poll node count until stable.
//  2. One CacheRequest per level (cross-process property reads are the slow part).
//  3. Logical ids are snapshot-scoped; every action RE-RESOLVES a recipe (never caches handles).
//  4. Strict resolution: 0 matches -> NOT_FOUND, >1 -> AMBIGUOUS_MATCH (never guess).
//  5. Every action returns post-state so the caller can assert (snapshot -> act -> verify).
//
// NOTE: written against FlaUI 5.0.0 from memory and NOT compiled/tested (the authoring
// sandbox is Linux). Expect to fix a few API-name compile errors on first build.
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace OrcaUia;

public class ToolError : Exception
{
    public string Code { get; }
    public string? Hint { get; }
    public ToolError(string code, string message, string? hint = null) : base(message) { Code = code; Hint = hint; }
}

// Win32 access for what UIA cannot reach: hidden windows, and edit controls whose
// ValuePattern silently drops writes.
internal static class NativeWin
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    // SW_SHOWNOACTIVATE: show, but do not take the keyboard. 5 would be SW_SHOW, 9 SW_RESTORE.
    public const int SW_SHOWNOACTIVATE = 4;
    // Push a window to the bottom of the z-order without moving, resizing or activating it.
    public static void SinkWithoutActivating(IntPtr h) => SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    // A newly launched app activates itself on its first ShowWindow no matter what STARTUPINFO
    // asked for, so "never steal the foreground" cannot be done at launch. What we can do is
    // give it straight back: the caller's window is put in front again right after the new
    // window appears, so the person loses the keyboard for a blink instead of for good.
    // SetForegroundWindow alone is refused for a background process, hence the AttachThreadInput
    // dance with whoever holds the foreground right now.
    public static void ForceForeground(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        var fg = GetForegroundWindow();
        if (fg == h) return;
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(fgThread, myThread, true);
        try { SetForegroundWindow(h); }
        finally { if (attached) AttachThreadInput(fgThread, myThread, false); }
    }

    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    // WS_EX_NOACTIVATE: a click or a programmatic focus on this window no longer activates it, so
    // the person's foreground window never moves and there is nothing to "give back" (restoring
    // every step made the driven window flicker between front and back).
    public static bool MakeNonActivating(IntPtr h, out bool ok)
    {
        ok = false;
        if (h == IntPtr.Zero) return false;
        try
        {
            var cur = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((cur & WS_EX_NOACTIVATE) != 0) { ok = true; return true; }
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(cur | WS_EX_NOACTIVATE));
            var now = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            ok = (now & WS_EX_NOACTIVATE) != 0;
            return ok;
        }
        catch (Exception) { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    public const int STARTF_USESHOWWINDOW = 0x1;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string app, string cmdLine, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    // Process.Start cannot ask for SW_SHOWNOACTIVATE, so the very first window of a freshly
    // launched app takes the keyboard. CreateProcessW can: the app comes up without activating.
    public static Process StartWithoutActivating(string path, string args)
    {
        var cmdLine = new StringBuilder("\"").Append(path).Append('"');
        if (!string.IsNullOrWhiteSpace(args)) cmdLine.Append(' ').Append(args.Trim());
        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = (short)SW_SHOWNOACTIVATE
        };
        var dir = Path.GetDirectoryName(path);
        if (!CreateProcessW(null, cmdLine.ToString(), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out var pi))
            throw new ToolError("LAUNCH_FAILED", $"CreateProcessW failed ({Marshal.GetLastWin32Error()}) for {path}",
                "check the path; a .cmd/.bat launcher must be started through cmd.exe");
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return Process.GetProcessById(pi.dwProcessId);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string s);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    // Trackbar ground truth. A common-control trackbar has no RangeValuePattern, and its
    // ValuePattern text is a PERCENTAGE (MSAA accValue convention, see IAccessible::get_accValue:
    // "Numeric values returned from scroll bar and trackbar accessible objects indicate percentages").
    // TBM_GETPOS is the only way to learn what the control actually holds.
    public const uint TBM_GETPOS = 0x0400, TBM_GETRANGEMIN = 0x0401, TBM_GETRANGEMAX = 0x0402, TBM_GETLINESIZE = 0x0418;
    public static int TrackBarQuery(IntPtr h, uint msg) => SendMessage(h, msg, IntPtr.Zero, IntPtr.Zero).ToInt32();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    public struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    // Milliseconds since the last keyboard/mouse input anywhere on the desktop. TickCount and
    // dwTime are both wrap-around counters, so the uint subtraction stays correct at the wrap.
    public static uint GetIdleMs()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0; // cannot tell: report "active" so a gate stays closed
        return unchecked((uint)Environment.TickCount - lii.dwTime);
    }
}

public class Recipe
{
    public ControlType Type;
    public string? AutoId;
    public string? Name;
    public string? Anchor; // nearest named ancestor
}

public partial class Engine : IDisposable
{
    readonly UIA3Automation auto = new();
    AutomationElement? win;
    FlaUI.Core.EventHandlers.FocusChangedEventHandlerBase? focusWarmupHandler;
    readonly Dictionary<string, Recipe> recipes = new();
    int idCounter;

    void EnsureFocusWarmup()
    {
        focusWarmupHandler ??= auto.RegisterFocusChangedEvent(_ => { });
    }

    static readonly HashSet<ControlType> Interactive = new()
    {
        ControlType.Button, ControlType.Edit, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.ComboBox, ControlType.MenuItem, ControlType.TabItem, ControlType.Hyperlink,
        ControlType.ListItem, ControlType.TreeItem, ControlType.Slider, ControlType.SplitButton
    };

    CacheRequest NewCache(TreeScope scope)
    {
        var cr = new CacheRequest { TreeScope = scope, AutomationElementMode = AutomationElementMode.Full };
        var p = auto.PropertyLibrary.Element;
        cr.Add(p.Name); cr.Add(p.AutomationId); cr.Add(p.ControlType);
        cr.Add(p.BoundingRectangle); cr.Add(p.IsOffscreen); cr.Add(p.IsEnabled);
        return cr;
    }

    public string Dispatch(string tool, JsonObject a) => tool switch
    {
        "app_attach" => Attach(a),
        "app_launch" => Launch(a),
        "app_warmup" => Warmup(a),
        "app_window" => Window(a),
        "app_cdp" => Cdp(a),
        "app_snapshot" => a["since"] is null ? Snapshot(a) : SnapshotDelta(a),
        "app_events" => Events(a),
        "app_act" => Act(a),
        "app_wait" => Wait(a),
        "app_shot" => Shot(a),
        _ => throw new ToolError("UNKNOWN_TOOL", tool)
    };

    // ---------- attach / launch ----------

    AutomationElement? FindWindow(int? pid, string? titleContains)
    {
        // Picking the first match is not enough: apps carry tray stubs, IME windows and
        // invisible 15x15 helpers under the same pid (cc-switch's real window was hidden and
        // the match landed on a 15x15 stub with zero nodes). Take the biggest plausible one.
        var big = Pick(pid, titleContains, skipTiny: true);
        // A minimized window can report a degenerate rect, so a size filter alone would
        // make it unreachable; fall back to the biggest of whatever matched.
        return big ?? Pick(pid, titleContains, skipTiny: false);
    }

    AutomationElement? Pick(int? pid, string? titleContains, bool skipTiny)
    {
        AutomationElement? best = null;
        long bestArea = -1;
        var desktop = auto.GetDesktop();
        var keepBase = new List<AutomationElement?> { win, lastSnapshotWindow };
        foreach (var watch in watches.Values) keepBase.Add(watch.Root);
        var keepArr = keepBase.ToArray();
        try
        {
            foreach (var w in desktop.FindAllChildren())
            {
                string title; int wpid; Rectangle r;
                try
                {
                    title = w.Properties.Name.ValueOrDefault ?? "";
                    wpid = w.Properties.ProcessId.ValueOrDefault;
                    r = w.BoundingRectangle;
                }
                catch (Exception) { ReleaseElement(w, keepArr); continue; }
                if (pid.HasValue && wpid != pid.Value) { ReleaseElement(w, keepArr); continue; }
                if (titleContains != null && !title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)) { ReleaseElement(w, keepArr); continue; }
                if (skipTiny && (r.Width < 80 || r.Height < 60)) { ReleaseElement(w, keepArr); continue; } // tray stubs / IME / message windows
                long area = (long)Math.Max(r.Width, 0) * Math.Max(r.Height, 0);
                if (area > bestArea)
                {
                    if (best != null) ReleaseElement(best, keepArr);
                    bestArea = area; best = w;
                }
                else
                {
                    ReleaseElement(w, keepArr);
                }
            }
        }
        finally
        {
            var deskKeep = best is null ? keepArr : keepArr.Append(best).ToArray();
            ReleaseElement(desktop, deskKeep);
        }
        return best;
    }

    // Electron/Chromium build the accessibility tree lazily: right after attaching, UIA
    // only sees the window frame. Asking for the node count a few times makes the app
    // build it. Cheap on native apps (the count is stable on the first poll).
    int Preload(int maxMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        int prev = -1, cur = CountNodes(), stable = 0;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            Thread.Sleep(150);
            cur = CountNodes();
            if (cur == prev) { if (++stable >= 2) break; }
            else { stable = 0; prev = cur; }
        }
        return cur;
    }

    string Attach(JsonObject a)
    {
        var title = a["title_contains"]?.GetValue<string>();
        var pid = a["pid"]?.GetValue<int>();
        if (title == null && pid == null) throw new ToolError("BAD_ARGS", "need title_contains or pid");
        win = FindWindow(pid, title) ?? throw new ToolError("WINDOW_NOT_FOUND", "no top-level window matched",
            "windows under 80x60 are ignored (tray stubs, IME windows); if the app is minimized to the tray, open its window first");
        recipes.Clear();
        // Attaching to a window someone else started: make it non-activating too, so UIA pattern
        // actions cannot pull the foreground away from whoever is typing.
        if (a["foreground_policy"]?.GetValue<string>() != "allow")
        {
            IntPtr hw = IntPtr.Zero;
            try { hw = new IntPtr(win.Properties.NativeWindowHandle.ValueOrDefault); } catch (Exception) { }
            NativeWin.MakeNonActivating(hw, out _);
        }
        // Subscribing to UIA events makes UiaClientsAreListening() true -> Chromium builds its tree.
        EnsureFocusWarmup();
        var sw = Stopwatch.StartNew();
        int nodes = (a["preload"]?.GetValue<bool>() ?? true) ? Preload(a["preload_ms"]?.GetValue<int>() ?? 1500) : 0;
        return Json(new
        {
            attached = win.Properties.Name.ValueOrDefault,
            pid = win.Properties.ProcessId.ValueOrDefault,
            nodes,
            preload_ms = sw.ElapsedMilliseconds
        });
    }

    string Launch(JsonObject a)
    {
        var path = a["path"]?.GetValue<string>() ?? throw new ToolError("BAD_ARGS", "path required");
        var args = a["args"]?.GetValue<string>() ?? "";
        // For Electron/Chromium hosts you start yourself, this makes the AX tree eager.
        // Only Chromium-based hosts understand the flag; anything else treats it as a file
        // name (notepad opened a "file not found" dialog). Default: add it only for known
        // Chromium hosts, and let the caller force it on or off explicitly.
        var force = a["force_accessibility"]?.GetValue<bool>() ?? LooksLikeChromium(path);
        if (force) args += " --force-renderer-accessibility";
        // Launch without activating: no restore-and-raise on the first window either.
        var fgBefore = NativeWin.GetForegroundWindow();
        var proc = NativeWin.StartWithoutActivating(path, args);
        var title = a["title_contains"]?.GetValue<string>(); // needed when the launcher exits (e.g. code.cmd)
        var deadline = DateTime.UtcNow.AddMilliseconds(a["timeout_ms"]?.GetValue<int>() ?? 30000);
        while (DateTime.UtcNow < deadline)
        {
            var w = FindWindow(title == null ? proc.Id : null, title);
            if (w != null)
            {
                win = w; recipes.Clear();
                EnsureFocusWarmup();
                // Report the window owner's pid, not the launcher's: launchers (and
                // packaged apps) exit at once and a different process owns the window.
                // Property reads can throw on a just-created element; fall back to the launcher's pid.
                int ownerPid;
                string ownerName;
                try { ownerPid = w.Properties.ProcessId.ValueOrDefault; ownerName = w.Properties.Name.ValueOrDefault; }
                catch (Exception) { ownerPid = proc.Id; ownerName = null; }
                // The app has just activated itself; hand the keyboard straight back to whoever
                // had it. Once only: the window is made non-activating so it will not grab again,
                // which is what used to make it flicker front/back on every step.
                bool keepForeground = a["foreground_policy"]?.GetValue<string>() == "allow";
                bool noActivate = false;
                if (!keepForeground)
                {
                    IntPtr hw = IntPtr.Zero;
                    try { hw = new IntPtr(w.Properties.NativeWindowHandle.ValueOrDefault); } catch (Exception) { }
                    noActivate = NativeWin.MakeNonActivating(hw, out _);
                    NativeWin.ForceForeground(fgBefore);
                }
                int preloaded = Preload(a["preload_ms"]?.GetValue<int>() ?? 1500);
                // The app's first window can still activate during its own startup; one more
                // hand-back, and only if the foreground really moved.
                if (!keepForeground) NativeWin.ForceForeground(fgBefore);
                return Json(new { launched = true, pid = ownerPid, launcher_pid = proc.Id, window = ownerName, nodes = preloaded, non_activating = noActivate });
            }
            // Launcher exited without a title to search by: it handed off (UWP, or an
            // existing browser main process took the request). Fail fast instead of
            // waiting the full timeout.
            if (title == null && proc.HasExited)
                throw new ToolError("LAUNCH_HANDED_OFF", "launcher exited before a window appeared",
                    "the app was probably handed off to an existing process or is a UWP package; retry with title_contains, or attach by pid");
            Thread.Sleep(250);
        }
        throw new ToolError("WINDOW_TIMEOUT", "no window appeared", "pass title_contains if the launcher spawns another process");
    }

    // ---------- CDP: the only way into a Chromium/Electron editor ----------

    // A contenteditable has no Value pattern and is not an HWND, so neither UIA nor a Win32
    // message can put text in it. The renderer's own DevTools protocol can: Input.insertText
    // writes inside the page, with no OS keyboard and no mouse.
    // Requires the app to have been started with --remote-debugging-port=<port>.
    string Cdp(JsonObject a)
    {
        var action = a["action"]?.GetValue<string>() ?? "targets";
        var port = a["port"]?.GetValue<int>() ?? 9222;
        var root = "http://127.0.0.1:" + port;
        var timeoutMs = a["timeout_ms"]?.GetValue<int>() ?? 15000;
        var sel = a["selector"]?.GetValue<string>() ?? "[contenteditable=\"true\"]";
        var selJs = JsonSerializer.Serialize(sel);

        string listText;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 8000)) };
            listText = http.GetStringAsync(root + "/json/list").GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            throw new ToolError("CDP_UNAVAILABLE", "cannot reach " + root + "/json/list: " + e.Message,
                "start the app with --remote-debugging-port=" + port + " (Electron/Chromium only)");
        }

        var pages = (JsonNode.Parse(listText) as JsonArray)?.OfType<JsonObject>()
            .Where(t => (string?)t["type"] == "page").ToList() ?? new List<JsonObject>();
        var urlFilter = a["url_contains"]?.GetValue<string>();
        var titleFilter = a["title_contains"]?.GetValue<string>();
        JsonObject pick = null;
        foreach (var t in pages)
        {
            if (urlFilter != null && !((string?)t["url"] ?? "").Contains(urlFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (titleFilter != null && !((string?)t["title"] ?? "").Contains(titleFilter, StringComparison.OrdinalIgnoreCase)) continue;
            pick = t; break;
        }

        if (action == "targets")
            return Json(new
            {
                count = pages.Count,
                targets = pages.Select(t => new { title = (string?)t["title"] ?? "", url = (string?)t["url"] })
            });
        if (pick == null)
            throw new ToolError("CDP_NO_TARGET", "no page target matched", "call app_cdp with action:\"targets\"");

        var wsUrl = (string?)pick["webSocketDebuggerUrl"];
        if (wsUrl == null) throw new ToolError("CDP_NO_TARGET", "target has no webSocketDebuggerUrl", null);

        using var ws = new System.Net.WebSockets.ClientWebSocket();
        var cts = new System.Threading.CancellationTokenSource(timeoutMs);
        try { ws.ConnectAsync(new Uri(wsUrl), cts.Token).GetAwaiter().GetResult(); }
        catch (Exception e) { throw new ToolError("CDP_CONNECT", "websocket connect failed: " + e.Message, null); }

        int nextId = 0;
        JsonObject Call(string method, JsonObject prms)
        {
            nextId++;
            var msg = new JsonObject { ["id"] = nextId, ["method"] = method, ["params"] = prms }.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(msg);
            ws.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token)
              .GetAwaiter().GetResult();
            var buf = new byte[1 << 16];
            while (true)
            {
                var sb = new StringBuilder();
                System.Net.WebSockets.WebSocketReceiveResult r;
                do
                {
                    r = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).GetAwaiter().GetResult();
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                var node = JsonNode.Parse(sb.ToString()) as JsonObject;
                if (node == null) continue;
                if ((int?)node["id"] == nextId) return node;
            }
        }

        string JsEval(string expr)
        {
            var node = Call("Runtime.evaluate", new JsonObject
            {
                ["expression"] = expr, ["returnByValue"] = true, ["awaitPromise"] = true
            });
            if (node["error"] is JsonObject err)
                throw new ToolError("CDP_ERROR", err.ToJsonString(), null);
            var res = node["result"] as JsonObject;
            if (res?["exceptionDetails"] is JsonObject exc)
                return "EXC " + ((string?)exc["text"] ?? "");
            return (res?["result"] as JsonObject)?["value"]?.ToString() ?? "";
        }

        void SendKey(string key, string code, int vk)
        {
            foreach (var type in new[] { "keyDown", "keyUp" })
                Call("Input.dispatchKeyEvent", new JsonObject
                {
                    ["type"] = type, ["key"] = key, ["code"] = code,
                    ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk
                });
        }

        string FocusEditor() => JsEval(
            "(()=>{const e=document.querySelector(" + selJs + ");if(!e)return 'NOTFOUND';e.focus();return 'ok';})()");

        string ReadEditor() => JsEval(
            "(()=>{const e=document.querySelector(" + selJs + ");return e?(e.value!==undefined?e.value:(e.innerText||'')):'';})()");

        string SelectAll() => JsEval(
            "(()=>{const e=document.querySelector(" + selJs + ");if(!e)return 'NOTFOUND';e.focus();const s=getSelection(),r=document.createRange();r.selectNodeContents(e);s.removeAllRanges();s.addRange(r);return 'ok';})()");

        switch (action)
        {
            case "focus":
            {
                var f = FocusEditor();
                return Json(new { ok = f == "ok", used = "js_focus", focused = f != "NOTFOUND" });
            }
            case "eval":
            {
                var expr = a["expression"]?.GetValue<string>() ?? throw new ToolError("BAD_ARGS", "expression required");
                return Json(new { ok = true, value = JsEval(expr) });
            }
            case "insert_text":
            case "type":
            {
                var text = a["text"]?.GetValue<string>() ?? throw new ToolError("BAD_ARGS", "text required");
                if (FocusEditor() == "NOTFOUND" && text.Length > 0)
                    throw new ToolError("CDP_NO_EDITOR", "no element matched " + sel,
                        "pass selector, e.g. [contenteditable=\"true\"] or textarea");
                if (a["clear_first"]?.GetValue<bool>() ?? false)
                {
                    SelectAll();
                    SendKey("Backspace", "Backspace", 8);
                }
                if (text.Length > 0) Call("Input.insertText", new JsonObject { ["text"] = text });
                return Json(new { ok = true, action, used = "cdp_insert_text", input = "renderer", value = ReadEditor() });
            }
            case "clear":
            {
                SelectAll();
                SendKey("Backspace", "Backspace", 8);
                return Json(new { ok = true, action = "clear", value = ReadEditor() });
            }
            case "click":
            {
                // DOM click: for buttons and rows that only exist inside the page, where the
                // UIA tree has nothing to address (or the same name repeats ten times).
                var r = JsEval("(()=>{const e=document.querySelector(" + selJs + ");if(!e)return 'NOTFOUND';e.click();return 'ok';})()");
                if (r == "NOTFOUND")
                    throw new ToolError("CDP_NO_ELEMENT", "no element matched " + sel, "pass a CSS selector that exists in the page");
                return Json(new { ok = true, action = "click", used = "js_click", selector = sel });
            }
            case "wait_for":
            {
                // Poll a selector instead of sleeping a fixed time after a click.
                var waitMs = a["timeout_ms"]?.GetValue<int>() ?? 10000;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < waitMs)
                {
                    var hit = JsEval("(()=>{return document.querySelector(" + selJs + ") ? 'yes' : 'no';})()");
                    if (hit == "yes")
                        return Json(new { ok = true, action = "wait_for", selector = sel, waited_ms = sw.ElapsedMilliseconds });
                    Thread.Sleep(150);
                }
                throw new ToolError("CDP_TIMEOUT", "selector never appeared: " + sel,
                    "check the page state with action:\"eval\" or a longer timeout_ms");
            }
            case "key":
            {
                var key = a["key"]?.GetValue<string>() ?? "Enter";
                string code;
                int vk;
                switch (key)
                {
                    case "Enter": code = "Enter"; vk = 13; break;
                    case "Tab": code = "Tab"; vk = 9; break;
                    case "Escape": code = "Escape"; vk = 27; break;
                    case "Backspace": code = "Backspace"; vk = 8; break;
                    case "ArrowDown": code = "ArrowDown"; vk = 40; break;
                    case "ArrowUp": code = "ArrowUp"; vk = 38; break;
                    default: code = key; vk = 0; break;
                }
                FocusEditor();
                SendKey(key, code, vk);
                return Json(new { ok = true, action = "key", key, input = "renderer" });
            }
            default:
                throw new ToolError("BAD_ARGS", "action must be targets|eval|focus|insert_text|type|clear|click|wait_for|key",
                    "needs the app restarted with --remote-debugging-port");
        }
    }

    // ---------- windows that UIA cannot see (hidden / tray) ----------

    // A hidden window is not part of the UIA desktop tree, so attach can never reach it, but
    // the HWND is still there and ShowWindow on it costs nothing: no tray click, no real mouse.
    sealed class WinInfo
    {
        public IntPtr H;
        public string Title = "";
        public string Cls = "";
        public int Pid;
        public bool Visible;
        public NativeWin.RECT Rect;
        public long Area => (long)Math.Max(0, Rect.R - Rect.L) * Math.Max(0, Rect.B - Rect.T);
    }

    string Window(JsonObject a)
    {
        var action = a["action"]?.GetValue<string>() ?? "list";
        var pid = a["pid"]?.GetValue<int>();
        var title = a["title_contains"]?.GetValue<string>();
        var cls = a["class_contains"]?.GetValue<string>();
        var all = new List<WinInfo>();
        NativeWin.EnumWindows((h, _) =>
        {
            var w = new WinInfo { H = h };
            NativeWin.GetWindowThreadProcessId(h, out uint p);
            w.Pid = (int)p;
            var tb = new StringBuilder(512); NativeWin.GetWindowText(h, tb, 512); w.Title = tb.ToString();
            var cb = new StringBuilder(256); NativeWin.GetClassName(h, cb, 256); w.Cls = cb.ToString();
            w.Visible = NativeWin.IsWindowVisible(h);
            NativeWin.GetWindowRect(h, out w.Rect);
            all.Add(w);
            return true;
        }, IntPtr.Zero);

        IEnumerable<WinInfo> q = all;
        if (pid.HasValue) q = q.Where(w => w.Pid == pid.Value);
        if (title != null) q = q.Where(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (cls != null) q = q.Where(w => w.Cls.Contains(cls, StringComparison.OrdinalIgnoreCase));
        var hits = q.OrderByDescending(w => w.Area).ToList();

        if (action == "list")
            return Json(new
            {
                count = hits.Count,
                windows = hits.Take(20).Select(w => new
                {
                    hwnd = "0x" + w.H.ToString("X"),
                    title = Trunc(w.Title),
                    klass = w.Cls,
                    pid = w.Pid,
                    visible = w.Visible,
                    rect = new[] { w.Rect.L, w.Rect.T, w.Rect.R - w.Rect.L, w.Rect.B - w.Rect.T }
                })
            });

        var best = hits.FirstOrDefault()
            ?? throw new ToolError("WINDOW_NOT_FOUND", "no hwnd matched (hidden windows are included)",
                "call app_window with action:\"list\" to see every top-level window of that process");
        bool before = best.Visible;
        // Showing a window is not a reason to take the keyboard away from whoever is typing.
        // Default: show without activating, then sink to the bottom of the z-order. Only an
        // explicit foreground_policy:"allow" gets the old restore-and-raise behaviour.
        if (action == "show")
        {
            if (a["foreground_policy"]?.GetValue<string>() == "allow")
            {
                NativeWin.ShowWindow(best.H, 9 /* SW_RESTORE */);
                NativeWin.SetForegroundWindow(best.H);
            }
            else
            {
                NativeWin.ShowWindow(best.H, NativeWin.SW_SHOWNOACTIVATE);
                NativeWin.SinkWithoutActivating(best.H);
            }
        }
        else if (action == "hide") NativeWin.ShowWindow(best.H, 0 /* SW_HIDE */);
        else if (action == "focus")
        {
            AssertForegroundAllowed(a, "app_window.focus");
            NativeWin.SetForegroundWindow(best.H);
        }
        else throw new ToolError("BAD_ARGS", "action must be list|show|hide|focus");

        Thread.Sleep(a["settle_ms"]?.GetValue<int>() ?? 400);
        NativeWin.GetWindowRect(best.H, out var after);
        return Json(new
        {
            ok = true,
            action,
            hwnd = "0x" + best.H.ToString("X"),
            title = Trunc(best.Title),
            klass = best.Cls,
            pid = best.Pid,
            was_visible = before,
            visible_now = NativeWin.IsWindowVisible(best.H),
            rect = new[] { after.L, after.T, after.R - after.L, after.B - after.T },
            hint = "now call app_attach with title_contains to read the tree"
        });
    }

    // ---------- screenshot ----------

    // A PNG of one window, for clients that can actually look at pixels. PrintWindow with
    // PW_RENDERFULLCONTENT works on windows that are partly covered; when it fails (some
    // DWM/composited surfaces), fall back to copying the screen region.
    string Shot(JsonObject a)
    {
        var path = a["path"]?.GetValue<string>()
            ?? throw new ToolError("BAD_ARGS", "path required", "absolute path of the .png to write, e.g. C:/tmp/win.png");
        var pid = a["pid"]?.GetValue<int>();
        var title = a["title_contains"]?.GetValue<string>();
        var all = new List<WinInfo>();
        NativeWin.EnumWindows((h, _) =>
        {
            var w = new WinInfo { H = h };
            NativeWin.GetWindowThreadProcessId(h, out uint p);
            w.Pid = (int)p;
            var tb = new StringBuilder(512); NativeWin.GetWindowText(h, tb, 512); w.Title = tb.ToString();
            NativeWin.GetWindowRect(h, out w.Rect);
            w.Visible = NativeWin.IsWindowVisible(h);
            all.Add(w);
            return true;
        }, IntPtr.Zero);

        var hit = all.Where(w => pid == null || w.Pid == pid)
                     .Where(w => title == null || w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                     .Where(w => w.Area > 0)
                     .OrderByDescending(w => w.Visible).ThenByDescending(w => w.Area)
                     .Select(w => (WinInfo?)w).FirstOrDefault()
            ?? throw new ToolError("WINDOW_NOT_FOUND", "no window matched", "call app_window with action:\"list\"");

        NativeWin.GetWindowRect(hit.H, out var rc);
        int w2 = rc.R - rc.L, h2 = rc.B - rc.T;
        if (w2 <= 0 || h2 <= 0) throw new ToolError("WINDOW_TOO_SMALL", $"window rect is {w2}x{h2}", "restore the window first");
        string used = "printwindow";
        using var bmp = new Bitmap(w2, h2);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok;
            try { ok = NativeWin.PrintWindow(hit.H, hdc, 2 /* PW_RENDERFULLCONTENT */); }
            finally { g.ReleaseHdc(hdc); }
            if (!ok)
                // No screen-copy fallback on purpose: copying the screen region would hand back
                // whatever the person has in front of that window, not the window itself.
                throw new ToolError("SHOT_FAILED", "PrintWindow could not render this window",
                    "the window may be minimised or on another desktop; restore it without activating, or use app_cdp Page.captureScreenshot for Electron");
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return Json(new
        {
            ok = true,
            path = Path.GetFullPath(path),
            width = w2,
            height = h2,
            title = Trunc(hit.Title),
            pid = hit.Pid,
            visible = hit.Visible,
            method = used
        });
    }

    // ---------- warmup ----------

    int CountNodes()
    {
        using (NewCache(TreeScope.Element).Activate())
            return Win().FindAllDescendants().Length;
    }

    string Warmup(JsonObject a)
    {
        int minNodes = a["min_nodes"]?.GetValue<int>() ?? 200;
        int timeout = a["timeout_ms"]?.GetValue<int>() ?? 25000;
        var sw = Stopwatch.StartNew();
        int cold = CountNodes(), prev = cold, cur = cold, stableRuns = 0;
        while (sw.ElapsedMilliseconds < timeout)
        {
            Thread.Sleep(500);
            cur = CountNodes();
            stableRuns = cur == prev ? stableRuns + 1 : 0;
            prev = cur;
            if (cur >= minNodes && stableRuns >= 2) break;
        }
        return Json(new { cold_nodes = cold, stable_nodes = cur, elapsed_ms = sw.ElapsedMilliseconds, ready = cur >= minNodes });
    }

    // ---------- snapshot ----------

    string Snapshot(JsonObject a)
    {
        bool interactiveOnly = a["interactive_only"]?.GetValue<bool>() ?? true;
        int maxNodes = a["max_nodes"]?.GetValue<int>() ?? 600;
        recipes.Clear(); idCounter = 0;
        var sb = new StringBuilder();
        int emitted = 0;
        var sw = Stopwatch.StartNew();
        Walk(Win(), 0, null, interactiveOnly, maxNodes, sb, ref emitted);
        MarkSnapshot();
        sb.Insert(0, $"# nodes={emitted} elapsed_ms={sw.ElapsedMilliseconds} (ids valid until next snapshot)\n");
        return sb.ToString();
    }

    void Walk(AutomationElement el, int depth, string? anchor, bool interactiveOnly, int max, StringBuilder sb, ref int emitted)
    {
        if (emitted >= max) return;
        // Property reads must happen while the cache request is active: with
        // AutomationElementMode.Full and an active cache, FlaUI serves them from the
        // cached payload; outside the scope each read is a cross-process COM call.
        var rows = new List<Row>();
        using (NewCache(TreeScope.Element | TreeScope.Children).Activate())
        {
            foreach (var k in el.FindAllChildren())
            {
                if (emitted + rows.Count >= max) break;
                var type = k.Properties.ControlType.ValueOrDefault;
                var name = k.Properties.Name.ValueOrDefault;
                var autoId = k.Properties.AutomationId.ValueOrDefault;
                bool offscreen = k.Properties.IsOffscreen.ValueOrDefault;
                bool enabled = k.Properties.IsEnabled.ValueOrDefault;
                bool named = !string.IsNullOrWhiteSpace(name);
                bool show = Interactive.Contains(type) || (!interactiveOnly && (named || type == ControlType.Document));
                Rectangle rect = default;
                if (show && !offscreen) rect = k.BoundingRectangle;
                rows.Add(new Row
                {
                    Kid = k,
                    Anchor = named ? name : anchor,
                    Show = show && !offscreen,
                    Type = type,
                    Name = name,
                    AutoId = autoId,
                    Enabled = enabled,
                    Rect = rect,
                    Depth = depth,
                    NameOrNull = named ? name : null
                });
            }
        }

        foreach (var row in rows)
        {
            if (emitted >= max) return;
            if (row.Show)
            {
                var id = "e" + (++idCounter);
                recipes[id] = new Recipe
                {
                    Type = row.Type,
                    AutoId = string.IsNullOrEmpty(row.AutoId) ? null : row.AutoId,
                    Name = row.NameOrNull,
                    Anchor = anchor
                };
                sb.Append(new string(' ', Math.Min(row.Depth, 12)))
                  .Append($"{id} {row.Type} \"{Trunc(row.Name)}\"")
                  .Append(string.IsNullOrEmpty(row.AutoId) ? "" : $" [aid={Trunc(row.AutoId)}]")
                  .Append(row.Enabled ? "" : " (disabled)")
                  .Append(Stateful.Contains(row.Type) ? string.Concat(StateOf(row.Kid).Select(t => " [" + t + "]")) : "")
                  .Append($" ({row.Rect.X},{row.Rect.Y},{row.Rect.Width},{row.Rect.Height})\n");
                emitted++;
            }
            Walk(row.Kid, depth + 1, row.Anchor, interactiveOnly, max, sb, ref emitted);
        }
    }

    readonly struct Row
    {
        public AutomationElement Kid { get; init; }
        public string? Anchor { get; init; }
        public bool Show { get; init; }
        public ControlType Type { get; init; }
        public string? Name { get; init; }
        public string? NameOrNull { get; init; }
        public string? AutoId { get; init; }
        public bool Enabled { get; init; }
        public Rectangle Rect { get; init; }
        public int Depth { get; init; }
    }

    // ---------- resolve (strict) ----------

    AutomationElement Resolve(string id)
    {
        if (!recipes.TryGetValue(id, out var r))
            throw new ToolError("UNKNOWN_ID", $"id {id} not in current snapshot", "call app_snapshot again");

        var cf = auto.ConditionFactory;
        // Filter inside the cache scope so AutomationId/Name come from the cached payload.
        List<AutomationElement> list;
        using (NewCache(TreeScope.Element).Activate())
        {
            IEnumerable<AutomationElement> q = Win().FindAllDescendants(cf.ByControlType(r.Type));
            if (r.AutoId != null)
            {
                var byId = q.Where(c => c.Properties.AutomationId.ValueOrDefault == r.AutoId).ToArray();
                if (byId.Length > 0) q = byId;
            }
            if (r.Name != null) q = q.Where(c => c.Properties.Name.ValueOrDefault == r.Name);
            list = q.ToList();
        }

        if (list.Count > 1 && r.Anchor != null)
            list = list.Where(c => NearestNamedAncestor(c) == r.Anchor).ToList();

        if (list.Count == 0) throw new ToolError("NOT_FOUND", $"{id} ({r.Type} \"{r.Name}\") no longer resolves", "wait or re-snapshot");
        if (list.Count > 1) throw new ToolError("AMBIGUOUS_MATCH", $"{id} matches {list.Count} elements", "re-snapshot with non-interactive_only or use a more specific target");
        return list[0];
    }

    string? NearestNamedAncestor(AutomationElement e)
    {
        // Name reads go through the cache; the Parent hop itself is still a live call.
        using (NewCache(TreeScope.Element).Activate())
        {
            var p = e.Parent;
            int guard = 0;
            while (p != null && guard++ < 30)
            {
                var n = p.Properties.Name.ValueOrDefault;
                if (!string.IsNullOrWhiteSpace(n)) return n;
                p = p.Parent;
            }
            return null;
        }
    }

    // ---------- act ----------

    string Act(JsonObject a)
    {
        if (a["steps"] is JsonArray steps && steps.Count > 0) return RunSteps(steps, a);
        var action = a["action"]?.GetValue<string>() ?? throw new ToolError("BAD_ARGS", "action required");
        var text = a["text"]?.GetValue<string>();
        var input = a["input"]?.GetValue<string>() ?? "pattern";
        var el = ResolveTarget(a);
        var before = (a["changes"]?.GetValue<bool>() ?? false) ? SnapshotNames() : null;
        var sw = Stopwatch.StartNew();

        // A pattern action on a UWP/native window can still activate it; give the foreground back
        // afterwards so the person who was typing keeps it (unless policy says otherwise).
        bool giveBack = (a["foreground_policy"]?.GetValue<string>() ?? "forbid") != "allow";
        var fgWas = giveBack ? NativeWin.GetForegroundWindow() : IntPtr.Zero;
        var extra = new JsonObject();
        var used = RunAction(el, action, text, input, a, a["unit"]?.GetValue<string>(), extra);

        Thread.Sleep(a["settle_ms"]?.GetValue<int>() ?? 150);
        if (giveBack) NativeWin.ForceForeground(fgWas);
        var st = StateOfEl(el);
        var r = new JsonObject
        {
            ["ok"] = true,
            ["action"] = action,
            ["input"] = input,
            ["used"] = used,
            ["state"] = st,
            ["elapsed_ms"] = sw.ElapsedMilliseconds
        };
        foreach (var kv in extra) r[kv.Key] = kv.Value?.DeepClone();
        // Ground truth has already judged the write (and the pattern echoes percent, not the value
        // asked for), so the text-comparison warning would only contradict a verified result.
        if (extra["applied_verified"]?.GetValue<bool>() != true)
        {
            var warn = TypeWarning(action, text, st);
            if (warn != null) r["warning"] = warn;
        }
        if (before != null) r["changed"] = Diff(before);
        return r.ToJsonString();
    }

    object StateOf(string id)
    {
        try
        {
            var el = Resolve(id);
            string? value = el.Patterns.Value.IsSupported ? el.Patterns.Value.Pattern.Value.ValueOrDefault : null;
            string? name; bool enabled;
            using (NewCache(TreeScope.Element).Activate())
            {
                name = el.Properties.Name.ValueOrDefault;
                enabled = el.Properties.IsEnabled.ValueOrDefault;
            }
            return new { found = true, name, value, enabled };
        }
        catch (ToolError e) { return new { found = false, reason = e.Code }; } // element gone after action is often the success signal
    }

    // ---------- selectors (live lookup, no snapshot needed) ----------

    // A snapshot id dies with the next snapshot, so a multi-step sequence is awkward:
    // step 3 needs an id that only exists after step 2 changed the UI. A selector is
    // looked up in the live tree on every use, so a whole sequence can be written up
    // front and still re-resolve against whatever the UI looks like at that moment.
    AutomationElement ResolveTarget(JsonObject a)
    {
        var id = a["id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(id)) return Resolve(id);
        var sel = a["target"] as JsonObject ?? a;
        var type = sel["type"]?.GetValue<string>();
        var name = sel["name"]?.GetValue<string>();
        var contains = sel["name_contains"]?.GetValue<string>();
        var aid = sel["aid"]?.GetValue<string>() ?? sel["automation_id"]?.GetValue<string>();
        var anchor = sel["anchor"]?.GetValue<string>();
        int index = sel["index"]?.GetValue<int>() ?? -1;
        if (type == null && name == null && contains == null && aid == null)
            throw new ToolError("BAD_ARGS", "give id, or target {type | name | name_contains | aid}",
                "ids are snapshot-scoped; a target is re-looked-up live");
        return ResolveSelector(type, name, contains, aid, anchor, index);
    }

    readonly struct Hit
    {
        public AutomationElement El { get; init; }
        public string Name { get; init; }
        public string Aid { get; init; }
        public ControlType Type { get; init; }
    }

    AutomationElement ResolveSelector(string? type, string? name, string? contains, string? aid, string? anchor, int index)
    {
        ControlType? ct = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<ControlType>(type, true, out var t))
                throw new ToolError("BAD_ARGS", $"unknown ControlType '{type}'", "e.g. Button, Edit, TabItem, TreeItem");
            ct = t;
        }
        var hits = new List<Hit>();
        using (NewCache(TreeScope.Element).Activate())
        {
            foreach (var k in Win().FindAllDescendants())
            {
                var t = k.Properties.ControlType.ValueOrDefault;
                if (ct != null && t != ct.Value) continue;
                var nm = k.Properties.Name.ValueOrDefault ?? "";
                if (name != null && !string.Equals(nm, name, StringComparison.Ordinal)) continue;
                if (contains != null && !nm.Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
                var a = k.Properties.AutomationId.ValueOrDefault ?? "";
                if (aid != null && !string.Equals(a, aid, StringComparison.Ordinal)) continue;
                hits.Add(new Hit { El = k, Name = nm, Aid = a, Type = t });
                if (hits.Count > 200) break;
            }
        }
        if (hits.Count > 1 && anchor != null)
            hits = hits.Where(h => NearestNamedAncestor(h.El) == anchor).ToList();

        var desc = Describe(type, name, contains, aid);
        if (hits.Count == 0)
            throw new ToolError("NOT_FOUND", $"no element matches {desc}", "app_snapshot to see what is there");
        if (index < 0 && hits.Count > 1)
            throw new ToolError("AMBIGUOUS_MATCH", $"{hits.Count} elements match {desc}: {Summarize(hits)}",
                "narrow it with name/aid/anchor, or pass index (0-based)");
        if (index >= hits.Count)
            throw new ToolError("NOT_FOUND", $"index {index} out of range, only {hits.Count} match {desc}", "index is 0-based");
        return hits[index >= 0 ? index : 0].El;
    }

    static string Describe(string? type, string? name, string? contains, string? aid)
    {
        var parts = new List<string>();
        if (type != null) parts.Add(type);
        if (name != null) parts.Add($"\"{name}\"");
        if (contains != null) parts.Add($"~\"{contains}\"");
        if (aid != null) parts.Add($"[aid={aid}]");
        return parts.Count == 0 ? "(unconstrained)" : string.Join(" ", parts);
    }

    static string Summarize(List<Hit> hits) =>
        string.Join("; ", hits.Take(6).Select(h =>
            $"{h.Type} \"{Trunc(h.Name)}\"{(h.Aid.Length == 0 ? "" : $" [aid={h.Aid}]")}"));

    static string NameOf(AutomationElement el)
    {
        try { return el.Properties.Name.ValueOrDefault ?? ""; } catch (Exception) { return ""; }
    }

    JsonObject StateOfEl(AutomationElement el)
    {
        var o = new JsonObject();
        try
        {
            string? name; bool enabled;
            using (NewCache(TreeScope.Element).Activate())
            {
                name = el.Properties.Name.ValueOrDefault;
                enabled = el.Properties.IsEnabled.ValueOrDefault;
            }
            string? value = el.Patterns.Value.IsSupported ? el.Patterns.Value.Pattern.Value.ValueOrDefault : null;
            bool pw;
            try { pw = el.Properties.IsPassword.ValueOrDefault; } catch (Exception) { pw = false; }
            o["found"] = true;
            o["name"] = name;
            o["value"] = value;
            o["password"] = pw;
            o["enabled"] = enabled;
            var tags = StateOf(el);
            if (tags.Count > 0) o["flags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray());
            o["toggle"] = ToggleOf(el);
            o["selected"] = SelectedOf(el);
            o["expanded"] = ExpandedOf(el);
        }
        catch (Exception) { o["found"] = false; }
        return o;
    }

    // ---------- control state (what an action flips, what a name/value hides) ----------

    static readonly HashSet<ControlType> Stateful = new()
    {
        ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.TabItem,
        ControlType.MenuItem, ControlType.ListItem, ControlType.TreeItem, ControlType.ComboBox,
        ControlType.SplitButton, ControlType.DataItem, ControlType.Hyperlink
    };

    static string? ToggleOf(AutomationElement el)
    {
        try { if (el.Patterns.Toggle.IsSupported) return el.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault.ToString(); }
        catch (Exception) { }
        return null;
    }

    static bool? SelectedOf(AutomationElement el)
    {
        try { if (el.Patterns.SelectionItem.IsSupported) return el.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault; }
        catch (Exception) { }
        return null;
    }

    static string? ExpandedOf(AutomationElement el)
    {
        try { if (el.Patterns.ExpandCollapse.IsSupported) return el.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault.ToString(); }
        catch (Exception) { }
        return null;
    }

    // Short tags for the snapshot line, e.g. "off" / "on" / "selected" / "collapsed".
    static List<string> StateOf(AutomationElement el)
    {
        var tags = new List<string>();
        var t = ToggleOf(el);
        if (t != null) tags.Add(t == "On" ? "on" : t == "Off" ? "off" : t);
        var s = SelectedOf(el);
        if (s == true) tags.Add("selected");
        else if (s == false) tags.Add("unselected");
        var e = ExpandedOf(el);
        if (e != null) tags.Add(e == "Collapsed" ? "collapsed" : e == "Expanded" ? "expanded" : e);
        return tags;
    }

    // SendInput typing into WinUI/RichEdit editors drops characters now and then (spaces,
    // or everything after a tab re-renders). Say so instead of reporting success: the caller
    // has to know whether the text really landed before the next step.
    static string? TypeWarning(string action, string? text, JsonObject st)
    {
        if (text == null) return null;
        if (action != "type" && action != "set_value") return null;
        if (st["password"]?.GetValue<bool>() == true) return null; // UIA masks password values, so comparing is meaningless
        var v = st["value"]?.GetValue<string>();
        if (v == null) return null; // no Value pattern: nothing to compare against
        // A contenteditable (Electron/Chromium editors, RichEdit) accepts SetValue and drops it:
        // read back the value instead of claiming the write happened.
        if (action == "set_value")
            return v == text
                ? null
                : $"the control ignored the value write (reads back \"{Trunc(v)}\"); it likely needs real keystrokes: action=type with input:\"physical\"";
        return v.Contains(text, StringComparison.Ordinal)
            ? null
            : $"only part of the text landed (value=\"{Trunc(v)}\"); retry action=type, or use action=set_value";
    }

    // A physical click or keystroke takes the pointer and keyboard away from whoever is using
    // the machine. UIA patterns do not, so they never come through here; physical input has to
    // be opted into per call. Default is forbid: failing closed beats stealing the cursor.
    static void AssertForegroundAllowed(JsonObject? a, string op)
    {
        var policy = a?["foreground_policy"]?.GetValue<string>() ?? "forbid";
        if (policy == "allow") return;
        if (policy == "allow_on_idle")
        {
            int need = a?["idle_ms"]?.GetValue<int>() ?? 3000;
            uint idle = NativeWin.GetIdleMs();
            if (idle >= (uint)need) return;
            throw new ToolError("USER_ACTIVE", $"{op} needs an idle machine: only {idle} ms since the last input, {need} ms required",
                "retry after the person stops typing, or pass foreground_policy:\"allow\" to take over now");
        }
        if (policy == "forbid")
            throw new ToolError("FOREGROUND_FORBIDDEN", $"{op} would steal foreground from the person using the machine",
                "use a UIA pattern instead (input:\"pattern\"), or app_cdp, or pass foreground_policy:\"allow\"");
        throw new ToolError("BAD_ARGS", $"foreground_policy must be allow|allow_on_idle|forbid, got \"{policy}\"");
    }

    // ---------- one action, no snapshot bookkeeping ----------

    // Never touch the real pointer or keyboard unless the caller says input:"physical".
    // The default path uses UIA patterns only, so a sequence can run while the person keeps
    // using the machine. Returns the path that was actually taken, for the receipt.
    // ---------- set_value ground truth ----------

    // What the control really holds, read from the control itself rather than from the pattern
    // that just echoed our request. Only common-control trackbars have such a back channel.
    sealed class GroundTruth
    {
        public IntPtr Hwnd;
        public int Value, Min, Max, LineSize;
    }

    static GroundTruth? TrackBarTruth(AutomationElement el)
    {
        try
        {
            var h = new IntPtr(el.Properties.NativeWindowHandle.ValueOrDefault);
            if (h == IntPtr.Zero) return null;
            var cls = new StringBuilder(256);
            if (NativeWin.GetClassName(h, cls, 256) == 0) return null;
            if (cls.ToString().IndexOf("msctls_trackbar32", StringComparison.OrdinalIgnoreCase) < 0) return null;
            return new GroundTruth
            {
                Hwnd = h,
                Value = NativeWin.TrackBarQuery(h, NativeWin.TBM_GETPOS),
                Min = NativeWin.TrackBarQuery(h, NativeWin.TBM_GETRANGEMIN),
                Max = NativeWin.TrackBarQuery(h, NativeWin.TBM_GETRANGEMAX),
                LineSize = NativeWin.TrackBarQuery(h, NativeWin.TBM_GETLINESIZE)
            };
        }
        catch (Exception) { return null; }
    }

    static bool LooksSliderLike(AutomationElement el)
    {
        try
        {
            var t = el.Properties.ControlType.ValueOrDefault.ToString();
            return t.IndexOf("Slider", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Spin", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception) { return false; }
    }

    static bool TryNum(string s, out double v) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)
        && double.IsFinite(v);

    static string RunAction(AutomationElement el, string action, string? text, string input, JsonObject? policy = null,
        string? unitRaw = null, JsonObject? extra = null)
    {
        bool physical = string.Equals(input, "physical", StringComparison.OrdinalIgnoreCase);
        switch (action)
        {
            case "click":
                // An offscreen control cannot be clicked (its rect is a placeholder); ask the
                // control to scroll itself into view first. UIA-only, no wheel, no mouse.
                bool scrolled = false;
                try
                {
                    if (el.Properties.IsOffscreen.ValueOrDefault && el.Patterns.ScrollItem.IsSupported)
                    {
                        el.Patterns.ScrollItem.Pattern.ScrollIntoView();
                        scrolled = true;
                        Thread.Sleep(150);
                    }
                }
                catch (Exception) { }
                var pre = scrolled ? "scroll+" : "";
                if (el.Patterns.Invoke.IsSupported) { el.Patterns.Invoke.Pattern.Invoke(); return pre + "invoke"; }
                if (el.Patterns.Toggle.IsSupported) { el.Patterns.Toggle.Pattern.Toggle(); return pre + "toggle"; }
                if (el.Patterns.SelectionItem.IsSupported) { el.Patterns.SelectionItem.Pattern.Select(); return pre + "selection"; }
                if (el.Patterns.ExpandCollapse.IsSupported) { el.Patterns.ExpandCollapse.Pattern.Expand(); return pre + "expand"; }
                if (!physical)
                {
                    // Focus is a UIA call, not a mouse move: enough for the next set_value.
                    el.Focus();
                    return pre + "focus";
                }
                AssertForegroundAllowed(policy, "app_act.click(physical)");
                el.Click();
                return pre + "physical";
            case "set_value":
                if (text == null) throw new ToolError("BAD_ARGS", "text required");
                if (physical)
                {
                    AssertForegroundAllowed(policy, "app_act.set_value(physical)");
                    el.Focus();
                    Keyboard.Type(text);
                    var physicalValue = el.Patterns.Value.IsSupported ? el.Patterns.Value.Pattern.Value.ValueOrDefault : null;
                    if (physicalValue != text) throw new ToolError("VALUE_READBACK_MISMATCH", "physical input did not read back the requested value");
                    return "physical";
                }
                if (el.Patterns.Value.IsSupported && !el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault)
                {
                    var unit = (unitRaw ?? "auto").Trim().ToLowerInvariant();
                    if (unit != "auto" && unit != "native" && unit != "percent")
                        throw new ToolError("BAD_ARGS", $"unit must be auto|native|percent, got \"{unitRaw}\"");
                    var truth = TrackBarTruth(el);
                    if (truth != null && truth.Max <= truth.Min && LooksSliderLike(el))
                        throw new ToolError("NO_GROUND_TRUTH",
                            $"slider has degenerate trackbar range {truth.Min}..{truth.Max}, so set_value cannot convert or verify the write",
                            "ensure the control is initialized with Maximum > Minimum before calling set_value");
                    if (unit == "native" && truth == null && LooksSliderLike(el) && TryNum(text, out _) && !el.Patterns.RangeValue.IsSupported)
                        throw new ToolError("NO_GROUND_TRUTH",
                            "unit=native was asked for a slider whose ValuePattern speaks percent, and this element has no readable trackbar position to confirm the result",
                            "use unit=percent, or action=type with input:\"physical\"");
                      // A trackbar answers its ValuePattern in percent (MSAA convention), while a caller
                    // naming a control means the value that control shows. Default to the control's own
                    // scale, convert for the write, and check the result against TBM_GETPOS - the echo
                    // of our own request proves nothing (set 20 on a 0..20 bar used to land on 4 and
                    // still read back "20").
                    double requested;
                    var numeric = TryNum(text, out requested);
                    var effective = unit == "auto" ? "native" : unit;
                    var writeText = text;
                    var converted = false;
                    double targetNative = double.NaN;
                    double nativeTol = 0.5d;
                    if (truth != null && numeric && truth.Max > truth.Min)
                    {
                        var span = (double)truth.Max - truth.Min;
                        var halfStep = truth.LineSize > 0 ? truth.LineSize / 2d : 0.5d;
                        nativeTol = Math.Max(0.5d, span / 100d) + halfStep;
                        if (effective == "native")
                        {
                            targetNative = requested;
                            var pct = (requested - truth.Min) * 100d / span;
                            if (requested < truth.Min || requested > truth.Max)
                                throw new ToolError("VALUE_OUT_OF_RANGE",
                                    $"{requested} is outside this control's {truth.Min}..{truth.Max}; the write would have to be {pct:0.#}%",
                                    "read the range from a snapshot, or pass unit=percent to write the percentage itself");
                            writeText = pct.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                            converted = true;
                        }
                        else
                        {
                            if (requested < 0d || requested > 100d)
                                throw new ToolError("VALUE_OUT_OF_RANGE",
                                    $"unit=percent accepts 0..100, got {requested}");
                            targetNative = Math.Round(requested * span / 100d) + truth.Min;
                        }
                    }
                    el.Patterns.Value.Pattern.SetValue(writeText);
                    bool pwRead;
                    try { pwRead = el.Properties.IsPassword.ValueOrDefault; } catch (Exception) { pwRead = false; }
                    var read = el.Patterns.Value.Pattern.Value.ValueOrDefault;
                    if (!converted && !pwRead && read != writeText)
                        throw new ToolError("VALUE_READBACK_MISMATCH", $"ValuePattern read back \\\"{Trunc(read)}\\\" after setting the requested value");
                    // A trackbar applies the write a little later than it echoes it: read the position
                    // until it settles on the target or stops moving, otherwise we judge a stale 16.
                    if (truth != null && numeric && !double.IsNaN(targetNative))
                    {
                        var settled = truth.Value;
                        for (var i = 0; i < 20; i++)
                        {
                            if (Math.Abs(settled - targetNative) <= 0.5d) break;
                            Thread.Sleep(50);
                            settled = NativeWin.TrackBarQuery(truth.Hwnd, NativeWin.TBM_GETPOS);
                            if (i >= 2 && settled == truth.Value) break;
                            truth.Value = settled;
                        }
                        truth.Value = NativeWin.TrackBarQuery(truth.Hwnd, NativeWin.TBM_GETPOS);
                    }
                    if (truth != null && numeric && !double.IsNaN(targetNative) && Math.Abs(truth.Value - targetNative) > nativeTol)
                        throw new ToolError("VALUE_READBACK_MISMATCH",
                            $"trackbar holds {truth.Value} on its {truth.Min}..{truth.Max} scale, but {effective} {requested} means {targetNative} (tolerance {nativeTol:0.##})",
                            effective == "percent"
                                ? "the control's percentage range may not be 0..100; pass unit=native and the control's own value"
                                : "the control snapped to another value - check its SmallChange, or pass unit=percent");
                    if (extra != null && truth != null && numeric && !double.IsNaN(targetNative))
                    {
                        extra["unit"] = effective;
                        extra["requested"] = requested;
                        extra["applied"] = truth.Value;
                        extra["applied_verified"] = true;
                        extra["native_value"] = truth.Value;
                        extra["native_min"] = truth.Min;
                        extra["native_max"] = truth.Max;
                    }
                    else if (extra != null && truth != null)
                    {
                        extra["applied_verified"] = false;
                        extra["warning"] = "cannot confirm trackbar ground truth (degenerate range or non-numeric value)";
                    }
                    return "value";
                }
                if (el.Patterns.RangeValue.IsSupported && !el.Patterns.RangeValue.Pattern.IsReadOnly.ValueOrDefault)
                {
                    var unit = (unitRaw ?? "auto").Trim().ToLowerInvariant();
                    if (unit != "auto" && unit != "native" && unit != "percent")
                        throw new ToolError("BAD_ARGS", $"unit must be auto|native|percent, got \"{unitRaw}\"");
                    if (!TryNum(text, out var requested) || !double.IsFinite(requested))
                        throw new ToolError("BAD_ARGS", "RangeValuePattern requires a numeric text value");
                    var range = el.Patterns.RangeValue.Pattern;
                    var min = range.Minimum.ValueOrDefault;
                    var max = range.Maximum.ValueOrDefault;
                    var small = range.SmallChange.ValueOrDefault;
                    var tolerance = double.IsFinite(small) ? Math.Max(Math.Abs(small) / 2d, 1e-9) : 1e-9;
                    var effective = unit == "auto" ? "native" : unit;
                    double target;
                    if (effective == "percent")
                    {
                        if (requested < -0.5d || requested > 100.5d)
                            throw new ToolError("VALUE_OUT_OF_RANGE", $"unit=percent accepts 0..100, got {requested}");
                        target = Math.Round(requested * (max - min) / 100d) + min;
                    }
                    else
                    {
                        target = requested;
                    }
                    if (target < min - tolerance || target > max + tolerance)
                        throw new ToolError("VALUE_OUT_OF_RANGE", $"{target} is outside this control's {min}..{max}");
                    range.SetValue(min <= max ? Math.Clamp(target, min, max) : target);
                    var read = range.Value.ValueOrDefault;
                    if (Math.Abs(read - target) > tolerance)
                        throw new ToolError("VALUE_READBACK_MISMATCH",
                            effective == "percent"
                                ? $"RangeValuePattern read back {read} on {min}..{max}, but percent {requested} means {target} (tolerance {tolerance})"
                                : $"RangeValuePattern read back {read} after requesting {requested} (tolerance {tolerance})");
                    if (extra != null)
                    {
                        extra["unit"] = effective;
                        extra["requested"] = requested;
                        extra["applied"] = read;
                        extra["applied_verified"] = true;
                        extra["native_value"] = read;
                        extra["native_min"] = min;
                        extra["native_max"] = max;
                    }
                    return "range_value";
                }
                throw new ToolError("UNSUPPORTED_PATTERN", "element has no writable ValuePattern or RangeValuePattern", "use action=type with input:\"physical\", or app_cdp for Chromium/Electron");
            case "type":
                if (text == null) throw new ToolError("BAD_ARGS", "text required");
                if (physical)
                {
                    AssertForegroundAllowed(policy, "app_act.type(physical)");
                    el.Focus();
                    Keyboard.Type(text);
                    if (el.Patterns.Value.IsSupported)
                    {
                        var typedValue = el.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";
                        if (!typedValue.Contains(text, StringComparison.Ordinal)) throw new ToolError("VALUE_READBACK_MISMATCH", "physical text was not present in ValuePattern readback");
                    }
                    return "physical";
                }
                if (el.Patterns.Value.IsSupported && !el.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault)
                {
                    bool pw;
                    try { pw = el.Properties.IsPassword.ValueOrDefault; } catch (Exception) { pw = false; }
                    if (pw) throw new ToolError("PASSWORD_FIELD", "password inputs cannot be typed into without real keystrokes", "use input:\"physical\"");
                    var cur = el.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";
                    var expected = cur + text;
                    el.Patterns.Value.Pattern.SetValue(expected);
                    var read = el.Patterns.Value.Pattern.Value.ValueOrDefault ?? "";
                    if (read != expected) throw new ToolError("VALUE_READBACK_MISMATCH", $"ValuePattern read back \\\"{Trunc(read)}\\\" after typing");
                    return "value_append";
                }
                throw new ToolError("UNSUPPORTED_PATTERN", "element has no writable ValuePattern for text input", "pass input:\"physical\" or use app_cdp for Chromium/Electron");            case "press":
                if (text == null || !Enum.TryParse<VirtualKeyShort>(text, true, out var key))
                    throw new ToolError("BAD_ARGS", "text must be a VirtualKeyShort name, e.g. ENTER, TAB, ESCAPE");
                if (!physical) throw new ToolError("PHYSICAL_ONLY", "a key press is real input", "pass input:\"physical\" to send it");
                AssertForegroundAllowed(policy, "app_act.press(physical)");
                el.Focus();
                Keyboard.Type(key);
                return "physical";
            case "toggle":
                if (el.Patterns.Toggle.IsSupported) { el.Patterns.Toggle.Pattern.Toggle(); return "toggle"; }
                if (el.Patterns.SelectionItem.IsSupported) { el.Patterns.SelectionItem.Pattern.Select(); return "selection"; }
                throw new ToolError("NO_TOGGLE", "element supports neither Toggle nor SelectionItem");
            default:
                throw new ToolError("BAD_ARGS", "action must be click|set_value|type|press|toggle");
        }
    }

    // ---------- a whole sequence in one call ----------

    string RunSteps(JsonArray steps, JsonObject a)
    {
        int settleDefault = a["settle_ms"]?.GetValue<int>() ?? 150;
        bool reportChanges = a["changes"]?.GetValue<bool>() ?? true;
        bool stopOnError = a["stop_on_error"]?.GetValue<bool>() ?? true;
        var sb = new StringBuilder();
        var all = Stopwatch.StartNew();
        int done = 0, ok = 0;
        foreach (var node in steps)
        {
            done++;
            var st = node as JsonObject ?? new JsonObject();
            var action = st["action"]?.GetValue<string>() ?? "";
            var sw = Stopwatch.StartNew();
            try
            {
                if (action == "wait")
                {
                    sb.Append(new JsonObject
                    {
                        ["i"] = done,
                        ["action"] = "wait",
                        ["ok"] = true,
                        ["result"] = JsonNode.Parse(Wait(st)),
                        ["elapsed_ms"] = sw.ElapsedMilliseconds
                    }.ToJsonString()).Append('\n');
                    ok++;
                    continue;
                }
                var text = st["text"]?.GetValue<string>();
                var input = st["input"]?.GetValue<string>() ?? a["input"]?.GetValue<string>() ?? "pattern";
                var el = ResolveTarget(st);
                var target = DescribeTarget(el);
                var before = reportChanges ? SnapshotNames() : null;
                // foreground_policy may be set per step or once for the whole call.
                var pol = st["foreground_policy"] != null ? st : a;
                bool giveBack = (pol["foreground_policy"]?.GetValue<string>() ?? "forbid") != "allow";
                var fgWas = giveBack ? NativeWin.GetForegroundWindow() : IntPtr.Zero;
                var stepExtra = new JsonObject();
                var used = RunAction(el, action, text, input, pol,
                    st["unit"]?.GetValue<string>() ?? a["unit"]?.GetValue<string>(), stepExtra);
                Thread.Sleep(st["settle_ms"]?.GetValue<int>() ?? settleDefault);
                if (giveBack) NativeWin.ForceForeground(fgWas);
                var state = StateOfEl(el);
                var line = new JsonObject
                {
                    ["i"] = done,
                    ["action"] = action,
                    ["input"] = input,
                    ["used"] = used,
                    ["ok"] = true,
                    ["target"] = target,
                    ["state"] = state,
                    ["elapsed_ms"] = sw.ElapsedMilliseconds
                };
                var warn = TypeWarning(action, text, state);
                if (stepExtra["applied_verified"]?.GetValue<bool>() != true && warn != null) line["warning"] = warn;
                foreach (var kv in stepExtra) line[kv.Key] = kv.Value?.DeepClone();
                if (before != null) line["changed"] = Diff(before);
                sb.Append(line.ToJsonString()).Append('\n');
                ok++;
            }
            catch (Exception ex)
            {
                var te = ex as ToolError;
                sb.Append(new JsonObject
                {
                    ["i"] = done,
                    ["action"] = action,
                    ["ok"] = false,
                    ["error"] = te?.Code ?? "INTERNAL",
                    ["message"] = ex.Message,
                    ["hint"] = te?.Hint,
                    ["elapsed_ms"] = sw.ElapsedMilliseconds
                }.ToJsonString()).Append('\n');
                if (stopOnError) break;
            }
        }
        sb.Insert(0, $"# steps ok={ok}/{steps.Count} done={done} elapsed_ms={all.ElapsedMilliseconds}\n");
        return sb.ToString();
    }

    string DescribeTarget(AutomationElement el)
    {
        try
        {
            ControlType t; string nm;
            using (NewCache(TreeScope.Element).Activate())
            {
                t = el.Properties.ControlType.ValueOrDefault;
                nm = el.Properties.Name.ValueOrDefault ?? "";
            }
            return $"{t} \"{Trunc(nm)}\"";
        }
        catch (Exception) { return ""; }
    }

    // ---------- cheap "what changed" ----------

    // Name+type is enough to answer "did the click land" without dumping the tree again.
    Dictionary<string, string> SnapshotNames(int cap = 4000)
    {
        var d = new Dictionary<string, string>();
        using (NewCache(TreeScope.Element).Activate())
        {
            foreach (var k in Win().FindAllDescendants())
            {
                var nm = k.Properties.Name.ValueOrDefault;
                if (string.IsNullOrWhiteSpace(nm)) continue;
                var key = k.Properties.ControlType.ValueOrDefault + "|" + Trunc(nm);
                d[key] = k.Properties.IsEnabled.ValueOrDefault ? "on" : "off";
                if (d.Count >= cap) break;
            }
        }
        return d;
    }

    JsonObject Diff(Dictionary<string, string> before, int max = 12)
    {
        var after = SnapshotNames();
        var added = after.Keys.Where(k => !before.ContainsKey(k)).Take(max).ToList();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).Take(max).ToList();
        var toggled = after.Where(kv => before.TryGetValue(kv.Key, out var b) && b != kv.Value)
                           .Select(kv => kv.Key).Take(max).ToList();
        var o = new JsonObject();
        if (added.Count > 0) o["added"] = new JsonArray(added.Select(x => (JsonNode)x).ToArray());
        if (removed.Count > 0) o["removed"] = new JsonArray(removed.Select(x => (JsonNode)x).ToArray());
        if (toggled.Count > 0) o["enabled_changed"] = new JsonArray(toggled.Select(x => (JsonNode)x).ToArray());
        if (o.Count == 0) o["none"] = true;
        return o;
    }

    // ---------- wait ----------

    string Wait(JsonObject a)
    {
        var name = a["name_contains"]?.GetValue<string>() ?? throw new ToolError("BAD_ARGS", "name_contains required");
        var state = a["state"]?.GetValue<string>() ?? "appear";
        int timeout = a["timeout_ms"]?.GetValue<int>() ?? 5000;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeout)
        {
            bool present;
            using (NewCache(TreeScope.Element).Activate())
                present = Win().FindAllDescendants()
                    .Any(e => (e.Properties.Name.ValueOrDefault ?? "").Contains(name, StringComparison.OrdinalIgnoreCase));
            if ((state == "appear") == present) return Json(new { ok = true, state, elapsed_ms = sw.ElapsedMilliseconds });
            Thread.Sleep(200);
        }
        throw new ToolError("WAIT_TIMEOUT", $"'{name}' did not {state} within {timeout}ms");
    }

    // ---------- helpers ----------

    // Chromium/Electron hosts that accept --force-renderer-accessibility. Anything else
    // (notepad, calc, native Win32 apps) would treat the flag as a file name.
    static readonly string[] ChromiumHosts =
    {
        "chrome", "msedge", "chromium", "brave", "vivaldi", "opera", "thorium", "arc",
        "electron", "code", "codium", "cursor", "figma", "discord", "slack", "teams",
        "spotify", "obsidian", "notion", "postman", "insomnia", "whatsapp", "telegram",
        "claude", "chatgpt", "notepad-free", "chrome_proxy"
    };

    static bool LooksLikeChromium(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return ChromiumHosts.Any(h => name.Contains(h));
    }

    AutomationElement Win() => win ?? throw new ToolError("NOT_ATTACHED", "call app_launch or app_attach first");
    static string Trunc(string? s) => s == null ? "" : (s.Length > 60 ? s[..60] + "…" : s).Replace("\n", " ");
    static string Json(object o) => JsonSerializer.Serialize(o);
    public void Dispose()
    {
        DisposeEvents();
        if (focusWarmupHandler is not null)
        {
            try { auto.UnregisterFocusChangedEvent(focusWarmupHandler); } catch { }
            focusWarmupHandler = null;
        }
        auto.Dispose();
    }
}

public static class Tools
{
    static JsonObject Tool(string name, string desc, JsonObject props, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = desc,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
        }
    };
    static JsonObject S(string d) => new() { ["type"] = "string", ["description"] = d };
    static JsonObject I(string d) => new() { ["type"] = "integer", ["description"] = d };
    static JsonObject B(string d) => new() { ["type"] = "boolean", ["description"] = d };

    static JsonObject EventDefinition() => Tool("app_events", "Subscribe to window-scoped UIA events, poll a cursor, inspect status or unsubscribe.",
        new JsonObject {
            ["action"] = S("subscribe|poll|status|unsubscribe"),
            ["window"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["pid"] = I("window pid"), ["title_contains"] = S("window title substring") } },
            ["kinds"] = new JsonObject { ["type"] = "array", ["items"] = S("structure|property|appear|gone|focus") },
            ["debounce_ms"] = I("0..1000; default 80"), ["lease_s"] = I("1..900; default 60"),
            ["subscription_id"] = S("subscription ID"), ["since"] = S("subscription cursor"),
            ["limit"] = I("1..256; default 64"), ["max_bytes"] = I("512..262144; default 16384"),
        }, "action");

    public static JsonArray Definitions() => new()
    {
        Tool("app_launch", "Start an app (adds --force-renderer-accessibility only for Electron/Chromium hosts) and attach to its main window. Preloads the tree unless preload=false.",
            new JsonObject { ["path"] = S("exe path"), ["args"] = S("arguments"), ["title_contains"] = S("window title substring; use when the launcher exits early"),
                ["force_accessibility"] = B("add --force-renderer-accessibility; default: only for known Chromium/Electron hosts"),
                ["preload_ms"] = I("how long to wait for a lazily-built tree; default 1500, stops early when the node count settles"),
                ["timeout_ms"] = I("default 30000") }, "path"),
        Tool("app_attach", "Attach to an already running window by title substring or pid. Preloads the tree (Electron/Chromium build it lazily) unless preload=false.",
            new JsonObject { ["title_contains"] = S("title substring"), ["pid"] = I("process id"),
                ["preload"] = B("default true"), ["preload_ms"] = I("default 1500") }),
        Tool("app_warmup", "Wake a lazily-built Chromium accessibility tree: poll node count until stable. Call after launch/attach for Electron apps.",
            new JsonObject { ["min_nodes"] = I("default 200"), ["timeout_ms"] = I("default 25000") }),
        Tool("app_window", "Find, list, show or hide a top-level window by pid/title/class. Hidden and tray windows are included: UIA cannot see those, but the HWND is still there and ShowWindow brings them back without touching the mouse.",
            new JsonObject { ["action"] = S("list (default) | show | hide | focus"), ["pid"] = I("process id"),
                ["title_contains"] = S("title substring"), ["class_contains"] = S("window class substring, e.g. Tauri Window"),
                ["foreground_policy"] = S("allow|allow_on_idle|forbid (default forbid): show never activates unless allow; focus needs this"),
                ["settle_ms"] = I("default 400") }),
        Tool("app_cdp", "Chromium/Electron editors (contenteditable) have no Value pattern and no HWND, so UIA and WM_SETTEXT both fail. This talks to the renderer's DevTools protocol instead: full text lands in the box with no OS keyboard and no mouse. Needs the app started with --remote-debugging-port=<port>.",
            new JsonObject { ["action"] = S("targets (default) | eval | focus | insert_text | clear | click | wait_for | key"),
                ["port"] = I("default 9222"), ["text"] = S("text for insert_text"),
                ["selector"] = S("CSS selector, default [contenteditable=\"true\"]"),
                ["expression"] = S("JS for eval"), ["key"] = S("Enter | Tab | Escape | Backspace | ArrowDown | ArrowUp"),
                ["clear_first"] = B("empty the box first, default false"), ["url_contains"] = S("page url filter"),
                ["title_contains"] = S("page title filter"), ["timeout_ms"] = I("default 15000") }),
        Tool("app_snapshot", "Return the control tree as text with snapshot-scoped ids (e1, e2...). Ids die on the next snapshot.",
            new JsonObject { ["interactive_only"] = B("default true"), ["max_nodes"] = I("default 600"), ["since"] = S("optional event cursor; returns JSON delta") }),
        EventDefinition(),
        Tool("app_act", "Act on an element, addressed either by a snapshot id or by a live target {type|name|name_contains|aid}[+index] - a target is looked up in the live tree each time, so it survives earlier steps. Pass steps[] to run a sequence in ONE call; every step reports post-state and (changes, default true) what appeared/vanished, so no screenshot and no re-snapshot is needed between steps.",
            new JsonObject {
                ["id"] = S("e-id from the latest snapshot"),
                ["input"] = S("pattern (default: UIA patterns only, never touches the mouse or keyboard) | physical (real mouse/keyboard)"),
                ["target"] = new JsonObject { ["type"] = "object", ["description"] = "selector: {type, name, name_contains, aid, anchor, index}" },
                ["type"] = S("ControlType name (shorthand for target.type)"),
                ["name"] = S("exact Name (shorthand for target.name)"),
                ["name_contains"] = S("Name substring (shorthand for target.name_contains)"),
                ["aid"] = S("AutomationId (shorthand for target.aid)"),
                ["index"] = I("0-based pick when several elements match; omit to require a unique match"),
                ["action"] = S("click|set_value|type|press|toggle (single-step form)"),
                ["text"] = S("value / text / key name"),
                ["unit"] = S("set_value only: auto (default) | native (the control's own Min..Max) | percent (0..100). Common-control trackbars (msctls_trackbar32) answer ValuePattern in percent, so auto treats them as percent and verifies against TBM_GETPOS; the receipt then carries unit/requested/applied/applied_verified/native_value/native_min/native_max"),
                ["steps"] = new JsonObject { ["type"] = "array", ["description"] = "sequence: [{action|wait, id|target|type|name|name_contains|aid|index, text, settle_ms}]" },
                ["changes"] = B("report appeared/removed/enabled-changed nodes; default true for steps, false for a single act"),
                ["stop_on_error"] = B("stop the sequence at the first failed step; default true"),
                ["foreground_policy"] = S("allow|allow_on_idle|forbid (default forbid): whether physical input may take the foreground; also settable per step"),
                ["idle_ms"] = I("allow_on_idle: required quiet time before taking over, default 3000"),
                ["settle_ms"] = I("default 150") }),
        Tool("app_wait", "Wait until an element whose name contains a substring appears or disappears.",
            new JsonObject { ["name_contains"] = S("substring"), ["state"] = S("appear|gone"), ["timeout_ms"] = I("default 5000") }, "name_contains"),
        Tool("app_shot", "Save a PNG of one top-level window via PrintWindow (no screen copy: the pixels always belong to that window, never to whatever covers it) so a vision-capable client can look at it. Returns the absolute path and size.",
            new JsonObject { ["path"] = S("absolute .png path to write"), ["title_contains"] = S("window title substring"), ["pid"] = I("process id") }, "path"),
    };
}


