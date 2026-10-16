# 验证「运行时切换主题」：程序用浅色启动，脚本按 Ctrl+T 切到深色，再截一张图。
# 如果可变画刷方案没生效（例如画刷被 Freeze 了），第二张图会跟第一张一模一样。
#
# 之所以不用 --theme 分别启动两次：那验证的是「启动时读对主题」，
# 而真正有风险的是**不重建界面**就地把颜色换掉 —— 那条路径只有交互能走到。
# 用 Ctrl+T 而不是模拟点标题栏那个按钮：键盘事件不依赖窗口在屏幕上的精确坐标。
#
# 注：截图代码刻意内联而没有抽成函数 —— 在这个脚本里定义函数会让
# Start-Process -PassThru 的返回值变成空，原因不明（同一个写法单独跑没问题）。
# 与其跟 PowerShell 的作用域规则纠缠，不如重复三遍。

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinApiTheme {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

$exe = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\NetCheckCSharp\bin\Debug\net8.0-windows\NetCheck.exe"
$outDir = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\shots\v34_live_switch"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$env:NETCHECK_UI_DEMO = "1"
$env:NETCHECK_UI_DEMO_TAB = "overview"

$app = Start-Process -FilePath $exe -PassThru -ArgumentList "--theme light"
Write-Output "proc null: $($null -eq $app)"

$win = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $app.Refresh()
    if ($app.HasExited) { Write-Output "exited early"; break }
    if ($app.MainWindowHandle -ne [IntPtr]::Zero) { $win = $app.MainWindowHandle; break }
}
Write-Output "hwnd=$win"
if ($win -eq [IntPtr]::Zero) { Write-Output "FAILED: no window"; try { $app.Kill() } catch {}; exit 1 }

[WinApiTheme]::ShowWindow($win, 5) | Out-Null
[WinApiTheme]::MoveWindow($win, 40, 20, 1180, 900, $true) | Out-Null
[WinApiTheme]::SetForegroundWindow($win) | Out-Null
Start-Sleep -Milliseconds 1600

# ---- 三张图：切换前 / 按一次 Ctrl+T 后 / 再按一次后 ----
$names = @("1_before_light", "2_after_ctrl_t_dark", "3_back_to_light")
for ($k = 0; $k -lt 3; $k++) {
    if ($k -gt 0) {
        [WinApiTheme]::SetForegroundWindow($win) | Out-Null
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait("^t")
        Start-Sleep -Milliseconds 1700
        Write-Output "sent Ctrl+T (#$k)"
    }

    $rect = New-Object WinApiTheme+RECT
    [WinApiTheme]::GetWindowRect($win, [ref]$rect) | Out-Null
    $bw = $rect.Right - $rect.Left
    $bh = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($bw, $bh)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $gfx.GetHdc()
    [WinApiTheme]::PrintWindow($win, $dc, 2) | Out-Null
    $gfx.ReleaseHdc($dc)
    $gfx.Dispose()
    $bmp.Save((Join-Path $outDir ($names[$k] + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  saved $($names[$k]).png (${bw}x${bh})"
}

try { $app.Kill(); $app.WaitForExit(3000) } catch {}
Remove-Item Env:\NETCHECK_UI_DEMO -ErrorAction SilentlyContinue
Remove-Item Env:\NETCHECK_UI_DEMO_TAB -ErrorAction SilentlyContinue
Write-Output "done"
