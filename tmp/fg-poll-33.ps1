Add-Type -Name U5 -Namespace W5 -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);'
function FgInfo {
    $h = [W5.U5]::GetForegroundWindow()
    $sb = New-Object System.Text.StringBuilder 256
    [W5.U5]::GetWindowText($h, $sb, 256) | Out-Null
    [pscustomobject]@{ hwnd = ('0x{0:X}' -f $h.ToInt64()); title = $sb.ToString() }
}

$env:ORCA_UIA_EXE = 'D:\orca-uia\src\bin\Release\net8.0-windows\orca-uia.exe'
Set-Location D:\orca-uia
Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 1) 先用 app_launch 把计算器起起来（只在这一步还一次前台，不轮询）
& node clients/uia-driver.cjs tmp/fg-launch.json *> D:\orca-uia\evidence\fg-poll-launch.log
'launch: ' + ((Select-String -Path D:\orca-uia\evidence\fg-poll-launch.log -Pattern 'launched').Line -join '')
Start-Sleep -Milliseconds 600

$base = FgInfo
"baseline: $($base.hwnd) $($base.title)"

# 2) 跑 33 步，同时每 40ms 采一次前台窗口
$log = 'D:\orca-uia\evidence\fg-poll-33.log'
$p = Start-Process -FilePath 'node' -ArgumentList 'clients/uia-driver.cjs', 'examples/acceptance-calculator-cn.json' `
    -WorkingDirectory 'D:\orca-uia' -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError 'D:\orca-uia\evidence\fg-poll-33.err'

$samples = 0; $stolen = 0; $firstStolen = $null
while (-not $p.HasExited) {
    $f = FgInfo
    $samples++
    if ($f.hwnd -ne $base.hwnd) {
        $stolen++
        if (-not $firstStolen) { $firstStolen = "$($f.hwnd) $($f.title)" }
    }
    Start-Sleep -Milliseconds 40
}
$p.WaitForExit()
"samples=$samples stolen_samples=$stolen first_stolen=$firstStolen"
'--- SUMMARY ---'
Select-String -Path $log -Pattern 'SUMMARY|FAIL' | ForEach-Object { $_.Line }
'--- foreground after ---'
FgInfo | ForEach-Object { "$($_.hwnd) $($_.title)" }
Get-Process CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
