# 前台哨兵:跑一条命令,同时盯着 WinEvent 的「前台切换」和「焦点变化」,把命令期间
# 抢走焦点的事件一条条记下来。
#
# 用法:
#   pwsh -File tests\foreground-sentinel.ps1 -RunCommand "node clients\uia-driver.cjs examples\acceptance-calculator-cn.json" -ReportPath evidence\fg.json
#
# 判定分三档(只有第一档算违规):
#   steal       —— 前台从基准窗口被切走,或者焦点落进一个「能被激活、但当前不是前台」的窗口。
#                  这才是真的把键盘从人手里拿走。
#   inert       —— 焦点事件落在带 WS_EX_NOACTIVATE 的窗口上。这类窗口点它、程序性 focus 它都不会激活,
#                  键盘还在人手里;记下来是为了别把「无害的内部焦点」误当成抢占。
#   same-window —— 事件落在当前前台窗口自己身上(比如人在前台窗口里切了个控件)。人没被动过。
#
# 基准窗口不是「脚本一启动时前台是谁」就算数的:先等前台稳定 400ms 再取,免得把自己这条命令
# 把控制台顶到前台的那一下算成基准。
param(
    [Parameter(Mandatory = $true)] [string]$RunCommand,
    [string]$ReportPath = "fg-sentinel-report.json",
    [string]$WatchPids = ""
)

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

public sealed class ForegroundSentinel : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WM_QUIT = 0x0012;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;

    private delegate void WinEventDelegate(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate proc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hHook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    public sealed class WinEvent
    {
        public string timestamp { get; set; } = "";
        public string eventType { get; set; } = "";
        public string classification { get; set; } = "";
        public string hwnd { get; set; } = "";
        public string rootHwnd { get; set; } = "";
        public int pid { get; set; }
        public string process { get; set; } = "";
        public string uwpChildProcess { get; set; } = "";
        public string klass { get; set; } = "";
        public string title { get; set; } = "";
        public bool isApplicationFrameHost { get; set; }
        public bool noActivate { get; set; }
        public string foregroundAtEvent { get; set; } = "";
    }

    private readonly IntPtr _baselineFg;
    private readonly HashSet<int> _watchPids;
    private readonly bool _baselineIsWatched;
    private readonly List<WinEvent> _steals = new();
    private readonly List<WinEvent> _inert = new();
    private readonly List<WinEvent> _sameWindow = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread _thread;
    private uint _threadId;
    private WinEventDelegate _callback; // 必须强引用，防 GC 回收回调导致崩溃

    public ForegroundSentinel(int[] watchPids)
    {
        _watchPids = new HashSet<int>(watchPids ?? Array.Empty<int>());
        // 只盯被驱动进程时,基准不能选成被驱动的窗口本身:那会把「应用自己拿到焦点」当成基准内的事。
        // 给它最多 5 秒,等前台离开被盯的进程再取基准。
        if (_watchPids.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
                if (!_watchPids.Contains((int)fgPid)) break;
                Thread.Sleep(100);
            }
        }
        _baselineFg = WaitStableForeground(400, 4000);
        GetWindowThreadProcessId(_baselineFg, out uint bp);
        _baselineIsWatched = _watchPids.Count > 0 && _watchPids.Contains((int)bp);
    }

    private static IntPtr WaitStableForeground(int quietMs, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        IntPtr last = GetForegroundWindow();
        long since = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(50);
            IntPtr now = GetForegroundWindow();
            if (now != last) { last = now; since = sw.ElapsedMilliseconds; continue; }
            if (sw.ElapsedMilliseconds - since >= quietMs) break;
        }
        return GetForegroundWindow();
    }

    public IntPtr BaselineForeground => _baselineFg;
    public bool BaselineIsWatched => _baselineIsWatched;
    public IReadOnlyList<WinEvent> Violations => _steals;
    public IReadOnlyList<WinEvent> InertFocus => _inert;
    public IReadOnlyList<WinEvent> SameWindow => _sameWindow;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(5000);
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _callback = OnWinEvent;
        IntPtr hFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        IntPtr hFocus = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (hFg != IntPtr.Zero) UnhookWinEvent(hFg);
        if (hFocus != IntPtr.Zero) UnhookWinEvent(hFocus);
    }

    private void OnWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr root = GetAncestor(hwnd, 2 /* GA_ROOT */);
        if (root == IntPtr.Zero) root = hwnd;
        IntPtr fgNow = GetForegroundWindow();

        bool isForegroundEvent = eventType == EVENT_SYSTEM_FOREGROUND;
        long ex = GetWindowLongPtr(root, GWL_EXSTYLE).ToInt64();
        bool noActivate = (ex & WS_EX_NOACTIVATE) != 0;

        // 判定
        string kind;
        if (isForegroundEvent)
        {
            // 前台事件:root 就是新前台。只有基准窗口自己重新上前台不算抢。
            kind = root == _baselineFg ? "same-window" : "steal";
        }
        else
        {
            if (root == fgNow) kind = "same-window";      // 焦点就落在当前前台窗口里,人没被动
            else if (noActivate) kind = "inert";           // 不可激活窗口里的内部焦点,拿不走键盘
            else kind = "steal";
        }

        GetWindowThreadProcessId(root, out uint pid);
        string procName = SafeProcName((int)pid);
        string klass = SafeClass(root);
        bool isAppFrame = string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(klass, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);
        if (_watchPids.Count > 0 && !_watchPids.Contains((int)pid) && !isAppFrame) return;

        var record = new WinEvent
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            eventType = isForegroundEvent ? "FOREGROUND" : "FOCUS",
            classification = kind,
            hwnd = "0x" + hwnd.ToString("X"),
            rootHwnd = "0x" + root.ToString("X"),
            pid = (int)pid,
            process = procName,
            uwpChildProcess = isAppFrame ? FindUwpCoreProcess(root, pid) : "",
            klass = klass,
            title = SafeTitle(root),
            isApplicationFrameHost = isAppFrame,
            noActivate = noActivate,
            foregroundAtEvent = "0x" + fgNow.ToString("X"),
        };

        lock (_steals)
        {
            if (kind == "steal") _steals.Add(record);
            else if (kind == "inert") _inert.Add(record);
            else _sameWindow.Add(record);
        }
    }

    private static string FindUwpCoreProcess(IntPtr frameHwnd, uint hostPid)
    {
        string found = "";
        EnumChildWindows(frameHwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint cpid);
            if (cpid != 0 && cpid != hostPid) { found = SafeProcName((int)cpid); return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string SafeProcName(int pid) { try { return Process.GetProcessById(pid).ProcessName; } catch { return ""; } }
    private static string SafeTitle(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    private static string SafeClass(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }

    public string ToReportJson(int cmdExitCode)
    {
        IntPtr endFg = GetForegroundWindow();
        bool watchMode = _watchPids.Count > 0;
        bool fgUnchanged = endFg == _baselineFg;
        return JsonSerializer.Serialize(new
        {
            // 只盯被驱动进程时,人自己切窗口不算违规(那几下事件根本不在监控范围内);
            // 不指定 WatchPids 时才要求「跑完前台还在原处」。
            passed = _steals.Count == 0 && cmdExitCode == 0 && (watchMode || fgUnchanged),
            commandExitCode = cmdExitCode,
            watchMode = watchMode,
            watchedPids = _watchPids,
            baselineIsWatched = _baselineIsWatched,
            baselineForeground = "0x" + _baselineFg.ToString("X"),
            endForeground = "0x" + endFg.ToString("X"),
            foregroundUnchanged = fgUnchanged,
            stealCount = _steals.Count,
            violations = _steals,
            inertFocusCount = _inert.Count,
            inertFocus = _inert,
            sameWindowCount = _sameWindow.Count,
            sameWindow = _sameWindow
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (_thread != null) _thread.Join(2000);
        _ready.Dispose();
    }
}
"@

$watch = @()
if ($WatchPids) { $watch = @($WatchPids -split '[,;\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ }) }
$sentinel = [ForegroundSentinel]::new($watch)
if ($null -eq $sentinel) { Write-Error '[sentinel] could not construct ForegroundSentinel - the C# did not compile'; exit 2 }
$sentinel.Start()
Write-Host "[sentinel] baseline foreground = 0x$($sentinel.BaselineForeground.ToString('X'))"

$exitCode = 0
try {
    Invoke-Expression $RunCommand
    $exitCode = $LASTEXITCODE
} finally {
    Start-Sleep -Milliseconds 300
    $sentinel.Dispose()
}

$json = $sentinel.ToReportJson($exitCode)
$dir = Split-Path -Parent $ReportPath
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
Set-Content -Path $ReportPath -Value $json -Encoding UTF8

Write-Host "[sentinel] steals=$($sentinel.Violations.Count)  inert-focus=$($sentinel.InertFocus.Count)  same-window=$($sentinel.SameWindow.Count)  command-exit=$exitCode"
if ($sentinel.Violations.Count -gt 0) {
    Write-Error "[sentinel] FAILED: detected $($sentinel.Violations.Count) foreground/focus steal(s). See $ReportPath"
    exit 1
}
Write-Host "[sentinel] PASSED: 0 foreground/focus steals. Report saved to $ReportPath"
exit $exitCode
