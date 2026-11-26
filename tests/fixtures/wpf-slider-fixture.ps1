Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="OrcaUiaWpfFixture" Width="420" Height="180" WindowStartupLocation="CenterScreen">
  <StackPanel Margin="20">
    <TextBlock Text="WPF Slider 0..100 (RangeValuePattern)" />
    <Slider Name="wpfSlider" AutomationProperties.AutomationId="wpfSlider"
            Minimum="0" Maximum="100" SmallChange="1" LargeChange="10" Value="0" />
    <TextBlock x:Name="Label" Text="0" />
  </StackPanel>
</Window>
'@
$reader = New-Object System.Xml.XmlNodeReader $xaml
$win = [Windows.Markup.XamlReader]::Load($reader)
$slider = $win.FindName('wpfSlider')
$label = $win.FindName('Label')
$script:last = -1
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(300)
$timer.Add_Tick({
    $v = [int]$slider.Value
    if ($v -ne $script:last) {
        $script:last = $v
        $label.Text = "$v"
        [Console]::WriteLine("STATE:wpf=$v")
        [Console]::Out.Flush()
    }
})
$timer.Start()
[Console]::WriteLine("READY:wpf")
[Console]::Out.Flush()
$win.ShowDialog() | Out-Null
