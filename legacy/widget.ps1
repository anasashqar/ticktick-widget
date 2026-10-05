#Requires -Version 5.1
# =====================================================================
#  تذكيراتي  -  أداة تذكيرات ثابتة على سطح المكتب (Windows / PowerShell + WPF)
#  البيانات: reminders.json بنفس المجلد. الأداة بتقرأه كل 20 ثانية،
#  فأي تعديل عليه (منك أو من Claude) بيظهر لحاله.
# =====================================================================

$ErrorActionPreference = 'Stop'
$Dir       = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataFile  = Join-Path $Dir 'reminders.json'
$StateFile = Join-Path $Dir 'widget-state.json'
$LogFile   = Join-Path $Dir 'error.log'
$Utf8      = New-Object System.Text.UTF8Encoding($false)
$Inv       = [Globalization.CultureInfo]::InvariantCulture
$ArCulture = [Globalization.CultureInfo]::GetCultureInfo('ar')
$MonthsAr  = @('كانون الثاني', 'شباط', 'آذار', 'نيسان', 'أيار', 'حزيران', 'تموز', 'آب', 'أيلول', 'تشرين الأول', 'تشرين الثاني', 'كانون الأول')

function Write-Log([string]$msg) {
    try { [IO.File]::AppendAllText($LogFile, "[$((Get-Date).ToString('s'))] $msg`r`n", $Utf8) } catch {}
}

trap {
    Write-Log ($_ | Out-String)
    try { [System.Windows.MessageBox]::Show("صار خطأ بأداة التذكيرات:`n$($_.Exception.Message)", 'تذكيراتي') | Out-Null } catch {}
    exit 1
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing

# ---- نسخة وحدة بس ----
$createdNew = $false
$script:Mutex = New-Object System.Threading.Mutex($true, 'Local\AnasRemindersWidget', [ref]$createdNew)
if (-not $createdNew) { exit 0 }

# =====================================================================
#  البيانات
# =====================================================================
function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $txt = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    if ([string]::IsNullOrWhiteSpace($txt)) { return $null }
    return (ConvertFrom-Json -InputObject $txt)
}

function Set-Prop($o, [string]$name, $value) {
    $o | Add-Member -NotePropertyName $name -NotePropertyValue $value -Force
}

function Load-Items {
    $list = New-Object System.Collections.ArrayList
    try {
        foreach ($o in (Read-JsonFile $DataFile)) {
            if ($o -and $o.title) {
                if (-not $o.id) { Set-Prop $o 'id' ([guid]::NewGuid().ToString('N').Substring(0, 8)) }
                [void]$list.Add($o)
            }
        }
    } catch {
        Write-Log "reminders.json parse error: $($_.Exception.Message)"
        return $null
    }
    if (Test-Path -LiteralPath $DataFile) { $script:LastWrite = (Get-Item -LiteralPath $DataFile).LastWriteTimeUtc }
    else { $script:LastWrite = [datetime]::MinValue }
    return , $list
}

function Save-Items {
    $arr = @($script:Items.ToArray())
    $json = ConvertTo-Json -InputObject $arr -Depth 6
    [IO.File]::WriteAllText($DataFile, $json, $Utf8)
    $script:LastWrite = (Get-Item -LiteralPath $DataFile).LastWriteTimeUtc
}

function Find-Item($id) {
    foreach ($i in $script:Items) { if ([string]$i.id -eq [string]$id) { return $i } }
    return $null
}

# ---- حالة النافذة (المكان، التثبيت، التذكيرات يلي انعرضت) ----
$script:State = [pscustomobject]@{ left = $null; top = $null; pinned = $true; collapsed = $false; hintShown = $false; alerted = @() }
try {
    $s = Read-JsonFile $StateFile
    if ($s) { foreach ($p in $s.PSObject.Properties) { Set-Prop $script:State $p.Name $p.Value } }
} catch { Write-Log "state read error: $($_.Exception.Message)" }

$script:Alerted = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($a in @($script:State.alerted)) { if ($a) { [void]$script:Alerted.Add([string]$a) } }

function Save-State {
    Set-Prop $script:State 'alerted' @($script:Alerted)
    try { [IO.File]::WriteAllText($StateFile, (ConvertTo-Json -InputObject $script:State -Depth 4), $Utf8) }
    catch { Write-Log "state save error: $($_.Exception.Message)" }
}

# =====================================================================
#  التواريخ
# =====================================================================
function Parse-Date([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $fmts = [string[]]@('yyyy-MM-ddTHH:mm', 'yyyy-MM-ddTHH:mm:ss', 'yyyy-MM-dd HH:mm', 'yyyy-MM-dd')
    $d = [datetime]::MinValue
    if ([datetime]::TryParseExact($s.Trim(), $fmts, $Inv, [Globalization.DateTimeStyles]::None, [ref]$d)) { return $d }
    return $null
}

function Get-DueInfo($it) {
    $d = Parse-Date ([string]$it.due)
    if ($null -eq $d) {
        if ($it.priority -eq 'urgent') { return @{ Text = ''; Level = 'urgent' } }
        return @{ Text = ''; Level = 'none' }
    }
    $days = [int][math]::Round(($d.Date - (Get-Date).Date).TotalDays)
    $day  = $d.ToString('dddd', $ArCulture)
    if ($days -lt 0)  { return @{ Text = "متأخر $(-$days) يوم"; Level = 'over' } }
    if ($days -eq 0)  { return @{ Text = 'اليوم'; Level = 'today' } }
    if ($days -eq 1)  { return @{ Text = "بكرا · $day"; Level = 'soon' } }
    if ($days -eq 2)  { return @{ Text = "بعد يومين · $day"; Level = 'soon' } }
    $lvl = 'ok'
    if ($it.priority -eq 'urgent') { $lvl = 'urgent' }
    return @{ Text = "باقي $days أيام · $day"; Level = $lvl }
}

# كتابة سريعة للموعد: @اليوم  @بكرا  @+3  @2026-10-08
function Resolve-Due([string]$tok) {
    $tok = $tok.Trim()
    if ($tok -match '^\d{4}-\d{2}-\d{2}$') { return $tok }
    if ($tok -eq 'اليوم') { return (Get-Date).ToString('yyyy-MM-dd', $Inv) }
    if ($tok -match '^(بكرا|بكره|غدا|غداً)$') { return (Get-Date).AddDays(1).ToString('yyyy-MM-dd', $Inv) }
    if ($tok -match '^\+(\d{1,3})$') { return (Get-Date).AddDays([int]$Matches[1]).ToString('yyyy-MM-dd', $Inv) }
    return $null
}

# ---- نظام الألوان (متباين كفاية على الخلفية الغامقة) ----
$Colors = @{
    over   = '#F87171'
    today  = '#FB923C'
    urgent = '#FB7185'
    soon   = '#FACC15'
    ok     = '#4ADE80'
    none   = '#94A3B8'
}
$Rank = @{ over = 0; today = 1; urgent = 2; soon = 3; ok = 4; none = 5 }

$BrushConv = New-Object System.Windows.Media.BrushConverter
function Brush([string]$hex) { return $BrushConv.ConvertFromString($hex) }
function Thick($l, $t, $r, $b) { return New-Object System.Windows.Thickness($l, $t, $r, $b) }

# =====================================================================
#  الواجهة
#  ملاحظة: WPF ما بيعكس Margin/Border مع RTL، فالقيم هون مكتوبة (يسار،فوق،يمين،تحت) فعلياً.
# =====================================================================
$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="تذكيراتي" Width="340" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ShowInTaskbar="False" ResizeMode="NoResize" FlowDirection="RightToLeft"
        FontFamily="Segoe UI" UseLayoutRounding="True" TextOptions.TextFormattingMode="Display">
  <Window.Resources>

    <!-- أزرار أيقونات صغيرة (هدف ضغط 28px) -->
    <Style x:Key="IconBtn" TargetType="Button">
      <Setter Property="Width" Value="28"/>
      <Setter Property="Height" Value="28"/>
      <Setter Property="Foreground" Value="#B8FFFFFF"/>
      <Setter Property="FontFamily" Value="Segoe MDL2 Assets"/>
      <Setter Property="FontSize" Value="12"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Focusable" Value="False"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="Button">
            <Border x:Name="b" Background="Transparent" CornerRadius="7">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="b" Property="Background" Value="#2EFFFFFF"/>
                <Setter Property="Foreground" Value="#FFFFFF"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- مربع إنجاز دائري (بدل الصغير الافتراضي) -->
    <Style x:Key="CircleCheck" TargetType="CheckBox">
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="CheckBox">
            <Grid Background="Transparent" Width="28" Height="28">
              <Border x:Name="Ring" Width="20" Height="20" CornerRadius="10" BorderThickness="2"
                      BorderBrush="#8CFFFFFF" Background="Transparent"
                      HorizontalAlignment="Center" VerticalAlignment="Center">
                <TextBlock x:Name="Tick" Text="&#xE73E;" FontFamily="Segoe MDL2 Assets" FontSize="10"
                           Foreground="#14151A" HorizontalAlignment="Center" VerticalAlignment="Center"
                           Visibility="Collapsed"/>
              </Border>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Ring" Property="BorderBrush" Value="#4ADE80"/>
              </Trigger>
              <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Ring" Property="Background" Value="#4ADE80"/>
                <Setter TargetName="Ring" Property="BorderBrush" Value="#4ADE80"/>
                <Setter TargetName="Tick" Property="Visibility" Value="Visible"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- مربع الكتابة مع حالة تركيز واضحة -->
    <Style x:Key="Input" TargetType="TextBox">
      <Setter Property="Foreground" Value="White"/>
      <Setter Property="CaretBrush" Value="White"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="TextBox">
            <Border x:Name="bd" Background="#14FFFFFF" BorderBrush="#26FFFFFF" BorderThickness="1"
                    CornerRadius="9" Padding="12,8,12,8">
              <ScrollViewer x:Name="PART_ContentHost" Focusable="False"
                            HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="bd" Property="BorderBrush" Value="#40FFFFFF"/>
              </Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True">
                <Setter TargetName="bd" Property="BorderBrush" Value="#60A5FA"/>
                <Setter TargetName="bd" Property="Background" Value="#1FFFFFFF"/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Window.Resources>

  <Border CornerRadius="16" Background="#F2191A1F" BorderBrush="#2EFFFFFF" BorderThickness="1" Padding="14,12,14,14" Margin="6">
    <Border.Effect>
      <DropShadowEffect BlurRadius="18" ShadowDepth="3" Opacity="0.4"/>
    </Border.Effect>
    <DockPanel>

      <!-- الرأس: العنوان + التاريخ + أزرار -->
      <Grid x:Name="Header" DockPanel.Dock="Top" Background="Transparent" Cursor="SizeAll">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <StackPanel VerticalAlignment="Center">
          <StackPanel Orientation="Horizontal">
            <TextBlock Text="تذكيراتي" Foreground="White" FontSize="16" FontWeight="SemiBold"/>
            <Border x:Name="CountBadge" Background="#33FFFFFF" CornerRadius="10" Padding="8,1,8,2" Margin="0,2,8,0" VerticalAlignment="Center">
              <TextBlock x:Name="CountText" Foreground="White" FontSize="11.5" FontWeight="SemiBold"/>
            </Border>
          </StackPanel>
          <TextBlock x:Name="DateText" Foreground="#A6FFFFFF" FontSize="11.5" Margin="0,2,0,0"/>
        </StackPanel>
        <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Top">
          <Button x:Name="BtnPin"      Style="{StaticResource IconBtn}" ToolTip="تثبيت فوق كل النوافذ"/>
          <Button x:Name="BtnCollapse" Style="{StaticResource IconBtn}" ToolTip="تصغير / تكبير"/>
          <Button x:Name="BtnHide"     Style="{StaticResource IconBtn}" Content="&#xE8BB;" ToolTip="إخفاء (بترجع من الأيقونة جنب الساعة)"/>
        </StackPanel>
      </Grid>

      <!-- ملخص بوضع التصغير: أهم مهمة -->
      <TextBlock x:Name="Summary" DockPanel.Dock="Top" Visibility="Collapsed" Foreground="#D9FFFFFF"
                 FontSize="12.5" Margin="0,8,0,0" TextTrimming="CharacterEllipsis"/>

      <StackPanel x:Name="Body" Margin="0,12,0,0">
        <ScrollViewer MaxHeight="440" VerticalScrollBarVisibility="Auto">
          <StackPanel x:Name="ListPanel"/>
        </ScrollViewer>
        <Grid Margin="0,6,0,0">
          <TextBox x:Name="NewBox" Style="{StaticResource Input}"/>
          <TextBlock x:Name="NewHint" Text="+  مهمة جديدة ثم Enter      @بكرا   @+3   @2026-10-08"
                     Foreground="#8CFFFFFF" FontSize="11.5" Margin="13,0,13,0"
                     VerticalAlignment="Center" IsHitTestVisible="False"/>
        </Grid>
      </StackPanel>
    </DockPanel>
  </Border>
</Window>
'@

$app = New-Object System.Windows.Application
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$app.Add_DispatcherUnhandledException({
    Write-Log $_.Exception.ToString()
    $_.Handled = $true
})

$window      = [Windows.Markup.XamlReader]::Parse($xaml)
$Header      = $window.FindName('Header')
$CountText   = $window.FindName('CountText')
$CountBadge  = $window.FindName('CountBadge')
$DateText    = $window.FindName('DateText')
$Summary     = $window.FindName('Summary')
$BtnPin      = $window.FindName('BtnPin')
$BtnCollapse = $window.FindName('BtnCollapse')
$BtnHide     = $window.FindName('BtnHide')
$Body        = $window.FindName('Body')
$ListPanel   = $window.FindName('ListPanel')
$NewBox      = $window.FindName('NewBox')
$NewHint     = $window.FindName('NewHint')
$IconStyle   = $window.FindResource('IconBtn')
$CheckStyle  = $window.FindResource('CircleCheck')

# ---------------------------------------------------------------------
function New-Text([string]$text, [double]$size, [string]$color) {
    $tb = New-Object System.Windows.Controls.TextBlock
    $tb.Text = $text
    $tb.FontSize = $size
    $tb.Foreground = Brush $color
    $tb.TextWrapping = [System.Windows.TextWrapping]::Wrap
    return $tb
}

function New-Chip([string]$text, [string]$color) {
    $b = New-Object System.Windows.Controls.Border
    $b.CornerRadius = New-Object System.Windows.CornerRadius(9)
    $b.Background = Brush ('#2B' + $color.Substring(1))
    $b.Padding = Thick 8 1 8 2
    $b.Margin = Thick 6 5 0 0
    $t = New-Text $text 11.5 $color
    $t.FontWeight = [System.Windows.FontWeights]::SemiBold
    $b.Child = $t
    return $b
}

function New-Section([string]$title, [int]$count) {
    $t = New-Text "$title  ·  $count" 11.5 '#9EFFFFFF'
    $t.FontWeight = [System.Windows.FontWeights]::SemiBold
    $t.Margin = Thick 4 6 4 6
    return $t
}

function New-Row($it) {
    $info  = Get-DueInfo $it
    $done  = [bool]$it.done
    $accent = $Colors[$info.Level]
    if ($done) { $accent = '#475569' }

    $border = New-Object System.Windows.Controls.Border
    $border.CornerRadius = New-Object System.Windows.CornerRadius(10)
    $border.Background = Brush '#14FFFFFF'
    $border.BorderBrush = Brush $accent
    $border.BorderThickness = Thick 0 0 3 0      # شريط اللون على جهة البداية (يمين) بالعربي
    $border.Padding = Thick 6 7 10 9
    $border.Margin = Thick 0 0 0 8
    if ($done) { $border.Opacity = 0.55 }
    $border.Add_MouseEnter({ $this.Background = Brush '#24FFFFFF' })
    $border.Add_MouseLeave({ $this.Background = Brush '#14FFFFFF' })

    $grid = New-Object System.Windows.Controls.Grid
    $c0 = New-Object System.Windows.Controls.ColumnDefinition; $c0.Width = [System.Windows.GridLength]::Auto
    $c1 = New-Object System.Windows.Controls.ColumnDefinition; $c1.Width = New-Object System.Windows.GridLength(1, [System.Windows.GridUnitType]::Star)
    $c2 = New-Object System.Windows.Controls.ColumnDefinition; $c2.Width = [System.Windows.GridLength]::Auto
    [void]$grid.ColumnDefinitions.Add($c0); [void]$grid.ColumnDefinitions.Add($c1); [void]$grid.ColumnDefinitions.Add($c2)

    # دائرة الإنجاز
    $cb = New-Object System.Windows.Controls.CheckBox
    $cb.Style = $CheckStyle
    $cb.IsChecked = $done
    $cb.Tag = [string]$it.id
    $cb.Margin = Thick 4 -4 0 0
    $cb.VerticalAlignment = [System.Windows.VerticalAlignment]::Top
    $cb.ToolTip = 'خلصت'
    $cb.Add_Click({ Toggle-Done $this.Tag })
    [System.Windows.Controls.Grid]::SetColumn($cb, 0)
    [void]$grid.Children.Add($cb)

    # النص
    $sp = New-Object System.Windows.Controls.StackPanel
    $title = New-Text ([string]$it.title) 14 '#FFFFFF'
    $title.FontWeight = [System.Windows.FontWeights]::SemiBold
    if ($done) { $title.TextDecorations = [System.Windows.TextDecorations]::Strikethrough }
    if ($it.link) {
        $title.Cursor = [System.Windows.Input.Cursors]::Hand
        $title.Tag = [string]$it.link
        $title.ToolTip = 'اضغط لفتح الرابط'
        $title.Add_MouseLeftButtonUp({ try { Start-Process $this.Tag } catch { Write-Log $_.Exception.Message } })
    }
    [void]$sp.Children.Add($title)

    if ($it.note -and -not $done) {
        $note = New-Text ([string]$it.note) 12 '#B3FFFFFF'
        $note.Margin = Thick 0 3 0 0
        [void]$sp.Children.Add($note)
    }

    if (-not $done) {
        $wp = New-Object System.Windows.Controls.WrapPanel
        if ($it.priority -eq 'urgent') { [void]$wp.Children.Add((New-Chip 'ضروري' $Colors.urgent)) }
        if ($info.Text) { [void]$wp.Children.Add((New-Chip $info.Text $Colors[$info.Level])) }
        if ($wp.Children.Count -gt 0) { [void]$sp.Children.Add($wp) }
    }
    [System.Windows.Controls.Grid]::SetColumn($sp, 1)
    [void]$grid.Children.Add($sp)

    # زر جانبي: فتح الرابط، أو حذف إذا منجزة
    $btn = $null
    if ($done) {
        $btn = New-Object System.Windows.Controls.Button
        $btn.Content = [string][char]0xE74D
        $btn.ToolTip = 'حذف'
        $btn.Tag = [string]$it.id
        $btn.Add_Click({ Delete-Reminder $this.Tag })
    } elseif ($it.link) {
        $btn = New-Object System.Windows.Controls.Button
        $btn.Content = [string][char]0xE8A7
        $btn.ToolTip = 'فتح الرابط'
        $btn.Tag = [string]$it.link
        $btn.Add_Click({ try { Start-Process $this.Tag } catch { Write-Log $_.Exception.Message } })
    }
    if ($btn) {
        $btn.Style = $IconStyle
        $btn.VerticalAlignment = [System.Windows.VerticalAlignment]::Top
        $btn.Margin = Thick 0 -3 0 0
        [System.Windows.Controls.Grid]::SetColumn($btn, 2)
        [void]$grid.Children.Add($btn)
    }

    $border.Child = $grid
    return $border
}

function Render {
    $script:RenderDay = (Get-Date).Date
    $ListPanel.Children.Clear()

    $now = Get-Date
    $DateText.Text = "$($now.ToString('dddd', $ArCulture))، $($now.Day) $($MonthsAr[$now.Month - 1])"

    $open = @($script:Items | Where-Object { -not $_.done })
    $done = @($script:Items | Where-Object { $_.done })

    $open = @($open | Sort-Object `
        @{ Expression = { $Rank[(Get-DueInfo $_).Level] } }, `
        @{ Expression = { $d = Parse-Date ([string]$_.due); if ($null -eq $d) { [datetime]::MaxValue } else { $d } } })

    # تجميع حسب الأولوية: الآن / قريباً / لاحقاً
    $groups = @(
        @{ Title = 'الآن';   Levels = @('over', 'today', 'urgent') },
        @{ Title = 'قريباً'; Levels = @('soon') },
        @{ Title = 'لاحقاً'; Levels = @('ok', 'none') }
    )
    foreach ($g in $groups) {
        $lv = $g.Levels
        $items = @($open | Where-Object { $lv -contains (Get-DueInfo $_).Level })
        if ($items.Count -eq 0) { continue }
        [void]$ListPanel.Children.Add((New-Section $g.Title $items.Count))
        foreach ($it in $items) { [void]$ListPanel.Children.Add((New-Row $it)) }
    }

    if ($open.Count -eq 0) {
        $e = New-Text 'خلصت كل مهامك، يعطيك العافية' 13 '#B3FFFFFF'
        $e.Margin = Thick 4 6 4 12
        [void]$ListPanel.Children.Add($e)
    }

    if ($done.Count -gt 0) {
        [void]$ListPanel.Children.Add((New-Section 'منجزة' $done.Count))
        $recent = @($done | Sort-Object @{ Expression = { [string]$_.doneAt } } -Descending | Select-Object -First 4)
        foreach ($it in $recent) { [void]$ListPanel.Children.Add((New-Row $it)) }
    }

    $CountText.Text = [string]$open.Count
    $hot = @($open | Where-Object { (Get-DueInfo $_).Level -in @('over', 'today', 'urgent') }).Count
    if ($hot -gt 0) { $CountBadge.Background = Brush '#D9DC2626' } else { $CountBadge.Background = Brush '#33FFFFFF' }

    # ملخص وضع التصغير
    if ($open.Count -gt 0) {
        $top = $open[0]
        $ti = Get-DueInfo $top
        $sum = [string]$top.title
        if ($ti.Text) { $sum = "$sum  ·  $($ti.Text)" }
        $Summary.Text = $sum
        $Summary.Foreground = Brush '#E6FFFFFF'
    } else {
        $Summary.Text = 'ما في مهام'
    }

    $tray.Text = "تذكيراتي - $($open.Count) مهام"
}

# ---------------------------------------------------------------------
function Toggle-Done($id) {
    $it = Find-Item $id
    if (-not $it) { return }
    $new = -not [bool]$it.done
    Set-Prop $it 'done' $new
    if ($new) { Set-Prop $it 'doneAt' ((Get-Date).ToString('yyyy-MM-ddTHH:mm', $Inv)) } else { Set-Prop $it 'doneAt' '' }
    Save-Items
    Render
}

function Delete-Reminder($id) {
    $it = Find-Item $id
    if ($it) { $script:Items.Remove($it); Save-Items; Render }
}

function Add-Reminder([string]$text) {
    $t = $text.Trim()
    if (-not $t) { return }
    $due = ''
    if ($t -match '^(.*?)\s*@(\S+)$') {
        $base = $Matches[1].Trim()
        $tok  = $Matches[2]
        $d = Resolve-Due $tok
        if ($d) { $due = $d; $t = $base }
    }
    if (-not $t) { return }
    $o = [pscustomobject]@{
        id       = [guid]::NewGuid().ToString('N').Substring(0, 8)
        title    = $t
        note     = ''
        link     = ''
        due      = $due
        priority = 'normal'
        remind   = @()
        done     = $false
        created  = (Get-Date).ToString('yyyy-MM-ddTHH:mm', $Inv)
    }
    [void]$script:Items.Add($o)
    Save-Items
    Render
}

# ---------------------------------------------------------------------
function Apply-Pin {
    $window.Topmost = [bool]$script:State.pinned
    if ($script:State.pinned) { $BtnPin.Content = [string][char]0xE840; $BtnPin.Foreground = Brush '#FFFFFF' }
    else { $BtnPin.Content = [string][char]0xE718; $BtnPin.Foreground = Brush '#80FFFFFF' }
}

function Apply-Collapse {
    if ($script:State.collapsed) {
        $Body.Visibility = 'Collapsed'; $Summary.Visibility = 'Visible'; $BtnCollapse.Content = [string][char]0xE70D
    } else {
        $Body.Visibility = 'Visible'; $Summary.Visibility = 'Collapsed'; $BtnCollapse.Content = [string][char]0xE70E
    }
}

function Show-Widget {
    $window.Show()
    if ($window.WindowState -eq 'Minimized') { $window.WindowState = 'Normal' }
    [void]$window.Activate()
}

function Toggle-Visible {
    if ($window.IsVisible) { $window.Hide() } else { Show-Widget }
}

function Exit-App {
    try { $timer.Stop() } catch {}
    Save-State
    $tray.Visible = $false
    $tray.Dispose()
    $app.Shutdown()
}

# ---------------------------------------------------------------------
#  التنبيهات: كل مهمة فيها "remind": ["2026-10-06T10:00", ...]
# ---------------------------------------------------------------------
function Check-Alerts {
    $now = Get-Date
    $fired = New-Object System.Collections.ArrayList
    foreach ($it in $script:Items) {
        if ($it.done) { continue }
        foreach ($r in @($it.remind)) {
            $t = Parse-Date ([string]$r)
            if ($null -eq $t) { continue }
            $key = "$($it.id)|$r"
            if ($now -ge $t -and -not $script:Alerted.Contains($key)) {
                [void]$script:Alerted.Add($key)
                if (-not $fired.Contains([string]$it.title)) { [void]$fired.Add([string]$it.title) }
            }
        }
    }
    if ($fired.Count -gt 0) {
        Save-State
        $script:State.collapsed = $false
        Apply-Collapse
        Show-Widget
        $msg = ($fired -join "`n")
        if ($msg.Length -gt 250) { $msg = $msg.Substring(0, 250) + '...' }
        $tray.ShowBalloonTip(15000, 'تذكير', $msg, [System.Windows.Forms.ToolTipIcon]::Info)
        [System.Media.SystemSounds]::Exclamation.Play()
    }
}

function Tick {
    $timer.Interval = [TimeSpan]::FromSeconds(20)
    if (Test-Path -LiteralPath $DataFile) {
        $lw = (Get-Item -LiteralPath $DataFile).LastWriteTimeUtc
        if ($lw -ne $script:LastWrite) {
            $r = Load-Items
            if ($null -ne $r) { $script:Items = $r; Render }
        }
    }
    if ((Get-Date).Date -ne $script:RenderDay) { Render }
    Check-Alerts
}

# =====================================================================
#  أيقونة جنب الساعة
# =====================================================================
$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.SystemIcons]::Information
$tray.Text = 'تذكيراتي'
$tray.Visible = $true
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$menu.RightToLeft = [System.Windows.Forms.RightToLeft]::Yes
[void]$menu.Items.Add('إظهار / إخفاء', $null, { Toggle-Visible })
[void]$menu.Items.Add('فتح ملف التذكيرات', $null, { Start-Process notepad.exe -ArgumentList "`"$DataFile`"" })
[void]$menu.Items.Add('إرجاع المكان الافتراضي', $null, {
    $wa = [System.Windows.SystemParameters]::WorkArea
    $window.Left = $wa.Right - $window.ActualWidth - 12
    $window.Top = $wa.Top + 12
    Set-Prop $script:State 'left' $window.Left; Set-Prop $script:State 'top' $window.Top; Save-State
    Show-Widget
})
[void]$menu.Items.Add('-')
[void]$menu.Items.Add('خروج', $null, { Exit-App })
$tray.ContextMenuStrip = $menu
$tray.Add_MouseDoubleClick({ Show-Widget })

# =====================================================================
#  الأحداث
# =====================================================================
$Header.Add_MouseLeftButtonDown({
    try { $window.DragMove() } catch {}
    Set-Prop $script:State 'left' $window.Left
    Set-Prop $script:State 'top'  $window.Top
    Save-State
})

$BtnPin.Add_Click({ $script:State.pinned = -not [bool]$script:State.pinned; Apply-Pin; Save-State })
$BtnCollapse.Add_Click({ $script:State.collapsed = -not [bool]$script:State.collapsed; Apply-Collapse; Save-State })
$BtnHide.Add_Click({
    $window.Hide()
    if (-not $script:State.hintShown) {
        $tray.ShowBalloonTip(6000, 'تذكيراتي', 'الأداة لسا شغالة. دبل كليك على الأيقونة جنب الساعة لترجعها.', [System.Windows.Forms.ToolTipIcon]::Info)
        $script:State.hintShown = $true
        Save-State
    }
})

$NewBox.Add_TextChanged({
    if ([string]::IsNullOrEmpty($NewBox.Text)) { $NewHint.Visibility = 'Visible' } else { $NewHint.Visibility = 'Collapsed' }
})
$NewBox.Add_KeyDown({
    if ($_.Key -eq [System.Windows.Input.Key]::Return) {
        Add-Reminder $NewBox.Text
        $NewBox.Text = ''
        $_.Handled = $true
    } elseif ($_.Key -eq [System.Windows.Input.Key]::Escape) {
        $NewBox.Text = ''
    }
})

# =====================================================================
#  تشغيل
# =====================================================================
$script:Items = Load-Items
if ($null -eq $script:Items) { $script:Items = New-Object System.Collections.ArrayList }

Apply-Pin
Apply-Collapse
Render

$window.Add_SourceInitialized({
    $wa = [System.Windows.SystemParameters]::WorkArea
    $vl = [System.Windows.SystemParameters]::VirtualScreenLeft
    $vt = [System.Windows.SystemParameters]::VirtualScreenTop
    $vw = [System.Windows.SystemParameters]::VirtualScreenWidth
    $vh = [System.Windows.SystemParameters]::VirtualScreenHeight
    $l = $script:State.left; $t = $script:State.top
    if ($null -ne $l -and $null -ne $t -and $l -ge $vl -and $l -le ($vl + $vw - 60) -and $t -ge $vt -and $t -le ($vt + $vh - 40)) {
        $window.Left = [double]$l; $window.Top = [double]$t
    } else {
        $window.Left = $wa.Right - $window.Width - 12
        $window.Top = $wa.Top + 12
    }
})

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(3)
$timer.Add_Tick({ Tick })
$timer.Start()

$window.Show()
[void]$app.Run()
