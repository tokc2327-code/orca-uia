Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Probe {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct SI { public int cb; public string r, d, t; public int x,y,xs,ys,xc,yc,fill,flags; public short show, res2; public IntPtr p2, i, o, e; }
  [StructLayout(LayoutKind.Sequential)] public struct PI { public IntPtr hp, ht; public int pid, tid; }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern bool CreateProcessW(string app, string cmd, IntPtr pa, IntPtr ta, bool inh, uint f, IntPtr env, string cwd, ref SI si, out PI pi);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static string Start(string path, short show) {
    var si = new SI { cb = Marshal.SizeOf<SI>(), flags = 1, show = show };
    var cmd = "\"" + path + "\"";
    if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out var pi))
      return "FAILED " + Marshal.GetLastWin32Error();
    CloseHandle(pi.ht); CloseHandle(pi.hp);
    return "pid=" + pi.pid;
  }
}
"@
function Fg { '0x' + [Probe]::GetForegroundWindow().ToString('X') }
'fg before = ' + (Fg)
'start = ' + [Probe]::Start('C:\Windows\System32\charmap.exe', 4)
Start-Sleep -Milliseconds 1200
'fg after  = ' + (Fg)
Get-Process charmap -ErrorAction SilentlyContinue | ForEach-Object { 'charmap hwnd=0x' + $_.MainWindowHandle.ToString('X') }
Get-Process charmap -ErrorAction SilentlyContinue | Stop-Process -Force
'=== 再试 SW_SHOWMINNOACTIVE(7) ==='
'fg before = ' + (Fg)
'start = ' + [Probe]::Start('C:\Windows\System32\charmap.exe', 7)
Start-Sleep -Milliseconds 1200
'fg after  = ' + (Fg)
Get-Process charmap -ErrorAction SilentlyContinue | ForEach-Object { 'charmap hwnd=0x' + $_.MainWindowHandle.ToString('X') }
