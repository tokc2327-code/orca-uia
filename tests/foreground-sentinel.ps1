# 用法示例：
# pwsh -File foreground-sentinel.ps1 -RunCommand "pwsh -File tmp\orca-nomouse-run.ps1" -ReportPath "evidence\fg-sentinel.json"
param(
    [Parameter(Mandatory = $true)] [string]$RunCommand,
    [string]$ReportPath = "fg-sentinel-report.json",
    [int[]]$WatchPids = @()
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    public sealed class Violation
    {
        public string timestamp { get; set; } = "";
        public string eventType { get; set; } = "";
        public string hwnd { get; set; } = "";
        public string rootHwnd { get; set; } = "";
        public int pid { get; set; }
        public string process { get; set; } = "";
        public string uwpChildProcess { get; set; } = "";
        public string klass { get; set; } = "";
        public string title { get; set; } = "";
        public bool isApplicationFrameHost { get; set; }
    }

    private readonly IntPtr _baselineFg;
    private readonly HashSet<int> _watchPids;
    private readonly List<Violation> _violations = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private WinEventDelegate? _callback; // 必须强引用，防 GC 回收回调导致崩溃

    public ForegroundSentinel(int[]? watchPids)
    {
        _baselineFg = GetForegroundWindow();
        _watchPids = new HashSet<int>(watchPids ?? Array.Empty<int>());
    }

    public IntPtr BaselineForeground => _baselineFg;
    public IReadOnlyList<Violation> Violations => _violations;

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
        if (root == _baselineFg) return; // 仍停留在基准前台窗口内，不算抢占

        GetWindowThreadProcessId(root, out uint pid);
        string procName = SafeProcName((int)pid);
        string klass = SafeClass(root);
        bool isAppFrame = string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(klass, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);

        // 若指定了 WatchPids，仅拦截目标 PID 或 ApplicationFrameHost；未指定则拦截一切离开基准窗口的抢占
        if (_watchPids.Count > 0 && !_watchPids.Contains((int)pid) && !isAppFrame) return;

        string uwpChild = isAppFrame ? FindUwpCoreProcess(root, pid) : "";
        lock (_violations)
        {
            _violations.Add(new Violation
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                eventType = eventType == EVENT_SYSTEM_FOREGROUND ? "FOREGROUND" : "FOCUS",
                hwnd = "0x" + hwnd.ToString("X"),
                rootHwnd = "0x" + root.ToString("X"),
                pid = (int)pid,
                process = procName,
                uwpChildProcess = uwpChild,
                klass = klass,
                title = SafeTitle(root),
                isApplicationFrameHost = isAppFrame
            });
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
        return JsonSerializer.Serialize(new
        {
            passed = _violations.Count == 0 && endFg == _baselineFg && cmdExitCode == 0,
            commandExitCode = cmdExitCode,
            baselineForeground = "0x" + _baselineFg.ToString("X"),
            endForeground = "0x" + endFg.ToString("X"),
            violationCount = _violations.Count,
            violations = _violations
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _ready.Dispose();
    }
}
"@ -ReferencedAssemblies "System.Text.Json"

$sentinel = [ForegroundSentinel]::new($WatchPids)
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

if ($sentinel.Violations.Count -gt 0) {
    Write-Error "[sentinel] FAILED: detected $($sentinel.Violations.Count) foreground/focus steal(s). See $ReportPath"
    exit 1
}
Write-Host "[sentinel] PASSED: 0 foreground/focus steals. Report saved to $ReportPath"
exit $exitCode