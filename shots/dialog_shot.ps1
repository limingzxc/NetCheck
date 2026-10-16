# 截「对话框」的图。对话框是独立窗口，capture.ps1 里的 PrintWindow 抓的是主窗口，
# 所以这里改成枚举进程的所有顶层窗口再逐张截。
#
# 用法：
#   .\shots\dialog_shot.ps1 -Theme dark -ExtraArgs @("--help") -Name help_dark
#   .\shots\dialog_shot.ps1 -Theme light -ExtraArgs @("--bad-flag") -Name warn_light
#   .\shots\dialog_shot.ps1 -Theme light -ExtraArgs @("--check","all") -Name confirm_light -CloseAfterMs 4000
#
# CloseAfterMs：等这么久后给主窗口发关闭消息，用来触发「检测中退出」这类确认对话框 ——
# 它只在忙碌时出现，必须等一个耗时检测跑起来再关窗。

param(
    [string]$Exe = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\NetCheckCSharp\bin\Debug\net8.0-windows\NetCheck.exe",
    [string]$OutDir = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\shots\v60_dialog",
    [string]$Theme = "light",
    [string[]]$ExtraArgs = @(),
    [string]$Name = "dialog",
    [int]$CloseAfterMs = 0,
    [string]$DemoMode = ""
)
# DemoMode：透传给程序的环境变量 NETCHECK_DIALOG_DEMO，=1 时弹一个双按钮确认框后退出。
# 注意 param 块里参数之间不能插注释，PowerShell 解析不过去 —— 这条注释只能放外面。

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DlgWin {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint nFlags);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# 刻意不设 NETCHECK_UI_DEMO：演示模式会让 OnLoaded 提前 return，`--check` 之类的
# 参数压根跑不起来，也就触发不了「检测中退出」那种确认框。
$env:NETCHECK_UI_DEMO = $null
$env:NETCHECK_UI_DEMO_TAB = $null
if ($DemoMode) { $env:NETCHECK_DIALOG_DEMO = $DemoMode } else { $env:NETCHECK_DIALOG_DEMO = $null }

$appArgs = @("--theme", $Theme) + $ExtraArgs
$app = Start-Process -FilePath $Exe -PassThru -ArgumentList $appArgs
Write-Output "pid=$($app.Id) args=$($appArgs -join ' ')"

if ($CloseAfterMs -gt 0) {
    Start-Sleep -Milliseconds $CloseAfterMs
    # CloseMainWindow 发的是 WM_CLOSE，和点右上角 X 走同一条路，程序不会被杀掉
    $app.CloseMainWindow() | Out-Null
    Write-Output "sent WM_CLOSE"
}

# 等对话框出现（主窗口 + 对话框两个顶层窗口）
$script:targetPid = $app.Id
$script:handles = @()
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $script:handles = @()
    $cb = [DlgWin+EnumProc]{
        param($h, $p)
        $wp = 0
        [void][DlgWin]::GetWindowThreadProcessId($h, [ref]$wp)
        if ($wp -eq $script:targetPid -and [DlgWin]::IsWindowVisible($h)) { $script:handles += $h }
        return $true
    }
    [void][DlgWin]::EnumWindows($cb, [IntPtr]::Zero)
    if ($app.HasExited) {
        Write-Output "  app exited (round $i)"
        break
    }
    if ($i -ge 6 -and $script:handles.Count -lt 2) {
        Write-Output "  still only $($script:handles.Count) window(s) after $($i * 250) ms"
    }
    if ($script:handles.Count -ge 2) { break }
}
Write-Output "found $($script:handles.Count) top-level windows"

foreach ($h in $script:handles) {
    [DlgWin]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 400

    $r = New-Object DlgWin+RECT
    [DlgWin]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $hh = $r.Bottom - $r.Top
    if ($w -le 0 -or $hh -le 0) { Write-Output "  skip (empty rect)"; continue }

    $bmp = New-Object System.Drawing.Bitmap($w, $hh)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [DlgWin]::PrintWindow($h, $dc, 2) | Out-Null
    $g.ReleaseHdc($dc); $g.Dispose()

    $file = Join-Path $OutDir ("{0}_{1}x{2}.png" -f $Name, $w, $hh)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  saved $file"
}

try { $app.Kill(); $app.WaitForExit(3000) } catch {}
Remove-Item Env:\NETCHECK_UI_DEMO -ErrorAction SilentlyContinue
Remove-Item Env:\NETCHECK_UI_DEMO_TAB -ErrorAction SilentlyContinue
Remove-Item Env:\NETCHECK_DIALOG_DEMO -ErrorAction SilentlyContinue
Write-Output "done"
