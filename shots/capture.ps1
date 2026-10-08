param(
    [string]$Exe = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\NetCheckCSharp\bin\Debug\net8.0-windows\NetCheck.exe",
    [string]$OutDir = "C:\Users\PC\WorkBuddy AI\2026-10-06-15-12-32\shots",
    [string[]]$Tabs = @("overview"),
    [int]$Height = 0,
    [int]$Width = 1080,
    [string]$Suffix = "",
    [switch]$NoDemo
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinApi2 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# 传卡片键名而不是数字。卡片顺序会因为「插入新卡片」而整体后移 ——
# 数字写法会静默拍到错的卡片（渲染层越界不报错，只是卡片是空的）。
# 键名不随顺序漂移，这也正是 --tab 推荐键名的理由。
# $index 只用来给文件名加个排序前缀，顺序跟 CardCatalog 的 TabIndex 保持一致。
$index = @{ overview=0; local=1; egress=2; link=3; dns=4; path=5; nat=6; port=7; ipv6=8; web=9 }
$lines = New-Object System.Collections.Generic.List[string]

foreach ($t in $Tabs) {
    if ($NoDemo) { $env:NETCHECK_UI_DEMO = "0" } else { $env:NETCHECK_UI_DEMO = "1" }
    $env:NETCHECK_UI_DEMO_TAB = "$t"

    $proc = Start-Process -FilePath $Exe -PassThru

    # 等窗口句柄就绪
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh()
        if ($proc.HasExited) { break }
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $h = $proc.MainWindowHandle; break }
    }

    if ($h -eq [IntPtr]::Zero) {
        $lines.Add("tab=$t FAILED: no window handle")
        try { $proc.Kill() } catch {}
        continue
    }

    [WinApi2]::ShowWindow($h, 5) | Out-Null
    [WinApi2]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 1200

    if ($Height -gt 0) {
        [WinApi2]::MoveWindow($h, 40, 20, $Width, $Height, $true) | Out-Null
        Start-Sleep -Milliseconds 1000
    }

    $r = New-Object WinApi2+RECT
    [WinApi2]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $hh = $r.Bottom - $r.Top
    $lines.Add("tab=$t rect=$($r.Left),$($r.Top),${w}x${hh} visible=$([WinApi2]::IsWindowVisible($h))")

    try {
        $bmp = New-Object System.Drawing.Bitmap($w, $hh)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        # PW_RENDERFULLCONTENT = 2，能抓到被遮挡或 DWM 合成的 WPF 窗口
        $ok = [WinApi2]::PrintWindow($h, $hdc, 2)
        $g.ReleaseHdc($hdc)
        $g.Dispose()
        $lines.Add("tab=$t PrintWindow=$ok")

        $name = $t
        $prefix = $index[$t]
        if ($null -eq $prefix) { $prefix = "x" }
        $file = Join-Path $OutDir ("{0}_{1}{2}.png" -f $prefix, $name, $Suffix)
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $lines.Add("tab=$t saved $file")
    } catch {
        $lines.Add("tab=$t capture error: " + $_.Exception.Message)
    }

    try { $proc.Kill(); $proc.WaitForExit(3000) } catch {}
    Start-Sleep -Milliseconds 500
}

Remove-Item Env:\NETCHECK_UI_DEMO -ErrorAction SilentlyContinue
Remove-Item Env:\NETCHECK_UI_DEMO_TAB -ErrorAction SilentlyContinue

[System.IO.File]::WriteAllLines((Join-Path $OutDir "capture.txt"), $lines, [System.Text.Encoding]::UTF8)
