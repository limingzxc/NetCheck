param(
    [string]$Exe = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\NetCheckCSharp\bin\Debug\net8.0-windows\NetCheck.exe",
    [string]$OutDir = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\shots\v25_p0"
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MC {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool rp);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

# Group header caption "Gao Ji" (U+9AD8 U+7EA7) built from code points so this
# script stays pure ASCII and never depends on the file's own encoding.
$GroupName = [string]([char]0x9AD8) + [string]([char]0x7EA7)

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$log = New-Object System.Collections.Generic.List[string]
$settings = Join-Path $env:APPDATA "NetCheck\settings.json"

# start from a clean "first run" state so the result is reproducible
$stale = @(Get-Process -Name NetCheck -ErrorAction Ignore)
if ($stale.Count -gt 0) { $stale | Stop-Process -Force -ErrorAction Ignore }
Start-Sleep -Milliseconds 500
if ([System.IO.File]::Exists($settings)) { [System.IO.File]::Delete($settings) }

$env:NETCHECK_UI_DEMO = "1"
$env:NETCHECK_UI_DEMO_TAB = "6"

$proc = Start-Process -FilePath $Exe -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $proc.Refresh()
    if ($proc.HasExited) { break }
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $h = $proc.MainWindowHandle; break }
}
if ($h -eq [IntPtr]::Zero) {
    $log.Add("FAILED: no window handle")
    [System.IO.File]::WriteAllLines((Join-Path $OutDir "click.txt"), $log, [System.Text.Encoding]::UTF8)
    exit
}

[MC]::ShowWindow($h, 5) | Out-Null
[MC]::SetForegroundWindow($h) | Out-Null
[MC]::MoveWindow($h, 40, 20, 1080, 1100, $true) | Out-Null
Start-Sleep -Milliseconds 1500

$r = New-Object MC+RECT
[MC]::GetWindowRect($h, [ref]$r) | Out-Null
$log.Add("rect=$($r.Left),$($r.Top),$($r.Right - $r.Left)x$($r.Bottom - $r.Top)")

function Shot($name) {
    $w = $r.Right - $r.Left
    $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [MC]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $f = Join-Path $OutDir $name
    $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $log.Add("saved $name")
}

# settings.json is UTF-8; PS 5.1 Get-Content defaults to ANSI and would mangle the
# Chinese preset name, making ConvertFrom-Json throw. Always read with -Encoding UTF8.
function Read-Collapsed {
    if (-not [System.IO.File]::Exists($settings)) { return "<no settings.json>" }
    try {
        $j = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
        return "[" + ($j.CollapsedCards -join ",") + "]"
    } catch {
        return "<parse error: " + $_.Exception.Message + ">"
    }
}

Shot "adv_before.png"
$log.Add("collapsed BEFORE: " + (Read-Collapsed))

$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)

$btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
$names = @()
foreach ($b in $btns) { $names += $b.Current.Name }
$log.Add("buttons = " + ($names -join " | "))

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $GroupName)
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)

if ($el -eq $null) {
    $log.Add("FAILED: UIA could not find the group header button")
} else {
    $br = $el.Current.BoundingRectangle
    $log.Add("header rect = $($br.Left),$($br.Top),$($br.Width)x$($br.Height)")
    try {
        $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        $log.Add("InvokePattern.Invoke() called")
    } catch {
        $log.Add("Invoke failed: " + $_.Exception.Message)
    }
    Start-Sleep -Milliseconds 1200
    Shot "adv_after.png"
    $log.Add("collapsed AFTER: " + (Read-Collapsed))
}

try { $proc.Kill(); $proc.WaitForExit(3000) } catch {}

[System.IO.File]::WriteAllLines((Join-Path $OutDir "click.txt"), $log, [System.Text.Encoding]::UTF8)
