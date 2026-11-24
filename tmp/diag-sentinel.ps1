$src = Get-Content 'D:\orca-uia\tests\foreground-sentinel.ps1' -Raw
$m = [regex]::Match($src, '(?s)Add-Type -TypeDefinition @"(.*?)"@ -ReferencedAssemblies "System.Text.Json"')
$code = $m.Groups[1].Value -split "`r?`n"
for ($i = 47; $i -le 62; $i++) { ($i+1).ToString().PadLeft(4) + ': ' + $code[$i] }
'--- 所有含 ? 的行 ---'
for ($i = 0; $i -lt $code.Count; $i++) { if ($code[$i] -match '\?') { ($i+1).ToString().PadLeft(4) + ': ' + $code[$i] } }
