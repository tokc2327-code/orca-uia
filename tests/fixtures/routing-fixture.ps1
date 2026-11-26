$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'OrcaUiaRoutingFixture'
$form.Size = New-Object System.Drawing.Size(420, 260)
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(60, 60)

$btn = New-Object System.Windows.Forms.Button
$btn.Name = 'fixtureButton'
$btn.Text = 'PlainButton'
$btn.Location = New-Object System.Drawing.Point(20, 20)
$btn.Size = New-Object System.Drawing.Size(140, 30)
$form.Controls.Add($btn)

$tb = New-Object System.Windows.Forms.TrackBar
$tb.Name = 'fixtureSlider'
$tb.Minimum = 0
$tb.Maximum = 20
$tb.TickFrequency = 1
$tb.SmallChange = 1
$tb.Value = 10
$tb.Location = New-Object System.Drawing.Point(20, 70)
$tb.Size = New-Object System.Drawing.Size(360, 45)
$form.Controls.Add($tb)

$box = New-Object System.Windows.Forms.TextBox
$box.Name = 'fixtureEdit'
$box.Text = ''
$box.Location = New-Object System.Drawing.Point(20, 140)
$box.Size = New-Object System.Drawing.Size(360, 26)
$form.Controls.Add($box)

$form.Add_Shown({
    $info = @{
        pid = $PID
        hwnd = $form.Handle.ToInt64()
        title = $form.Text
    }
    $json = $info | ConvertTo-Json -Compress
    [Console]::Out.WriteLine('READY:' + $json)
    [Console]::Out.Flush()
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 300
$timer.Add_Tick({
    $state = 'STATE:slider=' + $tb.Value + ' edit=[' + $box.Text + '] min=' + $tb.Minimum + ' max=' + $tb.Maximum + ' small=' + $tb.SmallChange
    [Console]::Out.WriteLine($state)
    [Console]::Out.Flush()
})
$timer.Start()

[System.Windows.Forms.Application]::Run($form)
