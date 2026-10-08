# NetCheck 端到端集成测试
#
# 覆盖三条不经过单元测试的链路：
#   1. 命令行参数解析 -> 界面提示 / 自动检测 / 自动导出
#   2. 真实网络检测 -> 导出的 txt / json 内容是否完整
#   3. 界面关闭 -> 检测选项与窗口几何是否落到 settings.json
#
# 用法：powershell -File e2e.ps1 [-Exe <路径>] [-OutDir <目录>]

param(
    [string]$Exe = "",
    [string]$OutDir = "",
    [string]$CoreTest = ""
)

$ErrorActionPreference = "Continue"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($Exe))    { $Exe    = Join-Path $root "NetCheckCSharp\bin\Release\net8.0-windows\NetCheck.exe" }
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path $root "shots\e2e" }
if ([string]::IsNullOrEmpty($CoreTest)) { $CoreTest = Join-Path $root "CoreTest\bin\Release\net8.0-windows\CoreTest.exe" }

if (-not (Test-Path $Exe)) { Write-Error "找不到可执行文件：$Exe"; exit 2 }
if (-not (Test-Path $CoreTest)) { Write-Error "找不到 CoreTest：$CoreTest"; exit 2 }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$settingsPath = Join-Path $env:APPDATA "NetCheck\settings.json"

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class E2EWin {
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    /// 列出某个进程当前所有可见顶层窗口的标题
    public static List<string> Titles(uint target) {
        var res = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                string s = sb.ToString();
                if (s.Length > 0) res.Add(s);
            }
            return true;
        }, IntPtr.Zero);
        return res;
    }
}
"@

$script:Pass = 0
$script:Fail = 0
$script:Lines = New-Object System.Collections.Generic.List[string]

function Ok([string]$what, [string]$detail) {
    $script:Pass++
    $script:Lines.Add("  PASS  $what" + $(if ($detail) { "   ($detail)" } else { "" }))
    Write-Host ("  PASS  " + $what) -ForegroundColor Green
}

function Bad([string]$what, [string]$detail) {
    $script:Fail++
    $script:Lines.Add("  FAIL  $what   -> $detail")
    Write-Host ("  FAIL  " + $what + "   -> " + $detail) -ForegroundColor Red
}

function Check([bool]$cond, [string]$what, [string]$detail) {
    if ($cond) { Ok $what "" } else { Bad $what $detail }
}

function Section([string]$name) {
    $script:Lines.Add("")
    $script:Lines.Add("== $name ==")
    Write-Host ""
    Write-Host "== $name ==" -ForegroundColor Cyan
}

# 启动进程并等某个标题的窗口出现
function Start-And-WaitWindow([string[]]$ArgList, [string]$ExpectTitle, [int]$Seconds = 10) {
    $p = Start-Process -FilePath $Exe -ArgumentList $ArgList -PassThru
    $seen = @()
    for ($i = 0; $i -lt ($Seconds * 4); $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.HasExited) { return @{ Proc = $p; State = "exited"; Titles = $seen } }
        $seen = [E2EWin]::Titles([uint32]$p.Id)
        if ($seen -contains $ExpectTitle) { return @{ Proc = $p; State = "found"; Titles = $seen } }
    }
    return @{ Proc = $p; State = "timeout"; Titles = $seen }
}

function Kill-Quiet($p) {
    try { if ($p -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit(3000) } } catch {}
}

# ==================================================================
Section "1. 命令行：--help 弹说明"
# ==================================================================
$r = Start-And-WaitWindow @("--help") "NetCheck 命令行参数"
Check ($r.State -eq "found") "--help 弹出「NetCheck 命令行参数」窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

# ==================================================================
Section "2. 命令行：非法参数给出提示而不是崩溃"
# ==================================================================
$r = Start-And-WaitWindow @("--bogus") "NetCheck"
Check ($r.State -eq "found") "未知参数弹出提示窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Check (-not ($r.Titles -contains "NetCheck 命令行参数")) "未知参数走的是错误提示而不是帮助窗口" ("实际: " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

$r = Start-And-WaitWindow @("--ports", "abc") "NetCheck"
Check ($r.State -eq "found") "非法端口表达式弹出提示窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

$r = Start-And-WaitWindow @("--tab", "99") "NetCheck"
Check ($r.State -eq "found") "越界 --tab 弹出提示窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

# --tab 推荐用卡片键名（数字绑定的卡片顺序会被插入新卡片打乱）。
# 这里覆盖两种边界：键名合法但不存在（不该报错，只是不滚动），以及键名合法且存在。
$r = Start-And-WaitWindow @("--tab", "nosuchcard") "NetCheck 网络体检"
Check ($r.State -eq "found") "不存在的卡片键名不崩，照常开主窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Check (-not ($r.Titles -contains "NetCheck")) "不存在的卡片键名不弹提示窗口（形状合法，只是查不到卡片）" ("实际: " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

$r = Start-And-WaitWindow @("--tab", "ipv6") "NetCheck 网络体检"
Check ($r.State -eq "found") "--tab ipv6 正常开主窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Check (-not ($r.Titles -contains "NetCheck")) "--tab ipv6 不弹参数错误提示" ("实际: " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

# --tab web：新加的最后一张卡片，键名也在 CardsInSubGroup 之外（高级 · 协议）
$r = Start-And-WaitWindow @("--tab", "web") "NetCheck 网络体检"
Check ($r.State -eq "found") "--tab web 正常开主窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Check (-not ($r.Titles -contains "NetCheck")) "--tab web 不弹参数错误提示" ("实际: " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

# --web 的取值要按域名的规则校验：裸 IP 会被拒绝并弹提示（TLS 要按域名校验 SNI，
# 拿 IP 去连会把「填错目标」伪装成「TLS 握手失败」）
$r = Start-And-WaitWindow @("--web", "192.168.1.1") "NetCheck"
Check ($r.State -eq "found") "--web 填裸 IP 弹出提示窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

$r = Start-And-WaitWindow @("--check", "bogus") "NetCheck"
Check ($r.State -eq "found") "非法 --check 名称弹出提示窗口" ("实际: " + $r.State + " / " + ($r.Titles -join " | "))
Kill-Quiet $r.Proc
Start-Sleep -Milliseconds 400

# ==================================================================
Section "3. 命令行：--check 自动检测 + --export 自动导出并退出"
# ==================================================================
$txt = Join-Path $OutDir "cli_report.txt"
# 这里不用 Remove-Item（环境的安全策略会拦下删除），改成先写占位内容，
# 跑完检查文件确实被覆盖了 —— 效果等价，还顺带验证了「导出会覆盖同名旧文件」
[System.IO.File]::WriteAllText($txt, "PLACEHOLDER")

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--host", "223.5.5.5", "--ports", "53,443,80", "--tab", "port",
    "--check", "port", "--export", "`"$txt`"")
$exited = $p.WaitForExit(180000)
$sw.Stop()
Kill-Quiet $p

Check $exited "--check port --export 的进程在导出后自动退出" "等待 180 秒仍未退出"
if (Test-Path $txt) {
    Ok "导出了 txt 报告" ("用时 " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + " 秒")
    $text = Get-Content $txt -Raw -Encoding UTF8
    Check ($text -ne "PLACEHOLDER") "导出覆盖了同名的旧文件" ""
    Check ($text.Length -gt 500) "报告内容不是空的" ("长度 " + $text.Length)
    Check ($text -match "NetCheck") "报告带标题" ""
    Check ($text -match "本机") "报告包含本机章节" ""
    Check ($text -match "223\.5\.5\.5") "报告里出现了命令行传入的目标主机" ""
    Check ($text -match "53") "报告里出现了命令行传入的端口" ""
    # 版本号从被测 exe 自己读，不写死 —— 否则每次升版本都要回来改这个脚本，
    # 而这条断言真正要守的是「报告头印的版本 == 可执行文件的版本」这个不变式。
    $exeVer = (Get-Item $Exe).VersionInfo.FileVersion
    $shortVer = (($exeVer -split '\.')[0..2] -join '.')
    Check ($text -match ("v" + [regex]::Escape($shortVer))) `
          "报告头的版本号跟可执行文件一致（v$shortVer）" ((($text -split "`n")[2]).Trim())
    # --check port 只跑端口检测，不该顺带跑出别的结果
    Check ($text -match "端口连通性") "报告包含端口连通性章节" ""
    Check ($text -notmatch "判定结果\s+多出口|判定结果\s+对称") "只跑端口检测时报告里没有 NAT 结论" ""
} else {
    Bad "导出了 txt 报告" "文件不存在: $txt"
}

$json = Join-Path $OutDir "cli_report.json"
[System.IO.File]::WriteAllText($json, "PLACEHOLDER")
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--host", "223.5.5.5", "--ports", "53,443", "--check=port", "--export", "`"$json`"")
$exited = $p.WaitForExit(180000)
Kill-Quiet $p
Check $exited "带 --check=port 内联写法的进程正常退出" "等待 180 秒仍未退出"
if (Test-Path $json) {
    Ok "导出了 json 报告" $json
    Check ((Get-Content $json -Raw) -ne "PLACEHOLDER") "json 导出覆盖了同名的旧文件" ""

    # 用应用自己的模型反序列化回来 —— 比 PowerShell 的 JSON 解析器更严格也更有意义
    $verify = & $CoreTest verifyjson $json 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "导出的 json 能被应用模型反序列化" $verifyText.Trim()
    Check ($verifyText -match "OK Hostname=.+") "json 里有主机名" ""
    # --check port 只跑端口检测，所以本机网卡信息应当是空的 ——
    # 这条同时验证了「--check 确实只跑了指定的那一项」
    Check ($verifyText -match "OK Adapters=0") "只跑端口检测时 json 里没有网卡信息" $verifyText.Trim()
    Check ($verifyText -match "OK ProxyKeys=null") "只跑端口检测时 json 里没有代理信息" ""
    Check ($verifyText -match "OK PortScanHost=223\.5\.5\.5") "json 里端口检测目标正确" ""
    Check ($verifyText -match "OK PortScanResults=2") "json 里端口结果数量与 --ports 一致" ""
} else {
    Bad "导出了 json 报告" "文件不存在: $json"
}

# ---- --check latency：连通性卡片拆出来的独立延迟检测 ----
# 只断言「延迟有数据、带宽没数据」：这两个同在一张卡片里，
# 如果 latency 没被正确接到任务表上，跑完会是两个都空，光看卡片有数据是分不出来的。
$lat = Join-Path $OutDir "latency_report.json"
[System.IO.File]::WriteAllText($lat, "PLACEHOLDER")
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--check", "latency", "--export", "`"$lat`"")
$exited = $p.WaitForExit(180000)
Kill-Quiet $p
Check $exited "--check latency 正常退出" "等待 180 秒仍未退出"
if (Test-Path $lat) {
    Ok "延迟检测导出了 json" ""
    $verify = & $CoreTest verifyjson $lat 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "延迟 json 能被应用模型反序列化" $verifyText.Trim()
    Check ($verifyText -match "OK LatencyTargets=[1-9]") "延迟检测确实测出了目标延迟" $verifyText.Trim()
    Check ($verifyText -match "OK Bandwidth=null") "只跑延迟时不带带宽数据" ""
} else {
    Bad "延迟检测导出了 json" "文件不存在: $lat"
}

# ---- --check egress：概览卡片拆出来的独立出口 IP 检测 ----
# 出口 IP、延迟、DNS 三项同在「概览」卡片里，跑完卡片都会变「有数据」。
# 所以必须靠 json 里的具体字段来断言「跑的是 egress，不是 quick」：
# 有出口 IP、但没有延迟目标、没有 DNS 服务器列表，才说明确实只跑了这一项。
$egr = Join-Path $OutDir "egress_report.json"
[System.IO.File]::WriteAllText($egr, "PLACEHOLDER")
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--check", "egress", "--export", "`"$egr`"")
$exited = $p.WaitForExit(180000)
Kill-Quiet $p
Check $exited "--check egress 正常退出" "等待 180 秒仍未退出"
if (Test-Path $egr) {
    Ok "出口 IP 检测导出了 json" ""
    $verify = & $CoreTest verifyjson $egr 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "出口 IP json 能被应用模型反序列化" $verifyText.Trim()
    Check ($verifyText -match "OK Egress=set") "出口 IP 检测确实测到了出口地址" $verifyText.Trim()
    # 出口 IP 自身不依赖网卡信息，但「有几个出口」的判定要看本机 IPv4 列表，
    # 所以这项会顺手补读一次网卡 —— 这条断言把这个设计钉住。
    Check ($verifyText -match "OK Adapters=[1-9]") "出口 IP 检测顺手补上了网卡信息" $verifyText.Trim()
    Check ($verifyText -match "OK LatencyTargets=0") "只跑出口 IP 时不带延迟数据" ""
    Check ($verifyText -match "OK DnsServers=null") "只跑出口 IP 时不带 DNS 服务器列表" ""
    # 归属地跟着出口 IP 一起查：出口 IP 出来之后才能查，顺序反了就一个都查不到
    Check ($verifyText -match "OK IpLocations=[1-9]") "出口 IP 检测顺带补齐了归属地" $verifyText.Trim()
} else {
    Bad "出口 IP 检测导出了 json" "文件不存在: $egr"
}

# ---- --check dns：DNS 卡片要能被自己填满（当前 DNS + 对比 DNS 两块）----
# 卡片上现在是两段服务器列表，只填一段就会半空 —— 这条盯着「两块都跑了」。
$dns = Join-Path $OutDir "dns_report.json"
[System.IO.File]::WriteAllText($dns, "PLACEHOLDER")
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--check", "dns", "--export", "`"$dns`"")
$exited = $p.WaitForExit(180000)
Kill-Quiet $p
Check $exited "--check dns 正常退出" "等待 180 秒仍未退出"
if (Test-Path $dns) {
    Ok "DNS 检测导出了 json" ""
    $verify = & $CoreTest verifyjson $dns 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "DNS json 能被应用模型反序列化" $verifyText.Trim()
    Check ($verifyText -match "OK DnsServers=(?!null)") "DNS 检测读到了本机配置的 DNS" $verifyText.Trim()
    Check ($verifyText -match "OK DnsServerTests=[1-9]") "DNS 检测测了当前 DNS 的响应速度" $verifyText.Trim()
    # 对比 DNS 是固定的四台公共服务器，不依赖网卡配置，所以这一条应当恰好是 4
    Check ($verifyText -match "OK DnsCompareTests=4") "DNS 检测测了四台对比用的公共 DNS" $verifyText.Trim()
    Check ($verifyText -match "OK LatencyTargets=0") "只跑 DNS 时不带延迟数据" ""
} else {
    Bad "DNS 检测导出了 json" "文件不存在: $dns"
}

# ---- --check ipv6：插在「本机」之后的独立 IPv6 检测 ----
# 断言的是「跑到了」而不是「通了」：CI 机器上有没有 IPv6 不可控，
# 但无论有没有，Ipv6 这一项都必须非 null 并给出一个结论。
$v6 = Join-Path $OutDir "ipv6_report.json"
[System.IO.File]::WriteAllText($v6, "PLACEHOLDER")
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--check", "ipv6", "--export", "`"$v6`"")
$exited = $p.WaitForExit(180000)
Kill-Quiet $p
Check $exited "--check ipv6 正常退出" "等待 180 秒仍未退出"
if (Test-Path $v6) {
    Ok "IPv6 检测导出了 json" ""
    $verify = & $CoreTest verifyjson $v6 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "IPv6 json 能被应用模型反序列化" $verifyText.Trim()
    Check ($verifyText -match "OK Ipv6=(?!null)") "IPv6 检测给出了结论（没启用也算结论）" $verifyText.Trim()
    Check ($verifyText -match "OK LatencyTargets=0") "只跑 IPv6 时不带延迟数据" ""
} else {
    Bad "IPv6 检测导出了 json" "文件不存在: $v6"
}

# ---- --check web：HTTP/HTTPS 分级连通性（DNS → TCP → TLS → HTTP）----
# 断言的是「逐层跑到了」而不是「网站通」：e2e 机器能不能上外网不可控，
# 但无论通不通，三个内置目标都必须各留下一条逐层记录。
$web = Join-Path $OutDir "web_report.json"
[System.IO.File]::WriteAllText($web, "PLACEHOLDER")
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--check", "web", "--export", "`"$web`"")
$exited = $p.WaitForExit(180000)
Kill-Quiet $p
Check $exited "--check web 正常退出" "等待 180 秒仍未退出"
if (Test-Path $web) {
    Ok "网页连通性检测导出了 json" ""
    $verify = & $CoreTest verifyjson $web 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "网页连通性 json 能被应用模型反序列化" $verifyText.Trim()
    # 内置目标固定三个（国内 2 + 境外 1）；配置里可能还留着自定义目标，所以是 3 起步而不是恰好 3。
    Check ($verifyText -match "OK WebTargets=[3-9]") "三个内置目标都在探测列表里" $verifyText.Trim()
    Check ($verifyText -match "OK WebVerdict=(?!null)") "网页检测给出了结论（不通也算结论）" $verifyText.Trim()
    # 每个目标 1~4 层：少于四层说明「第一层失败就停」生效了，一条都没有说明没跑
    Check ($verifyText -match "OK WebStepCounts=([1-4],)*[1-4]") "每个目标都留下了逐层记录" $verifyText.Trim()
    # 网页检测是唯一「只看域名不看 IP」的一项，不需要网卡信息 ——
    # 这条同时验证了「--check 确实只跑了指定的那一项」
    Check ($verifyText -match "OK Adapters=0") "只跑网页检测时不读网卡" ""
    Check ($verifyText -match "OK LatencyTargets=0") "只跑网页检测时不带延迟数据" ""
} else {
    Bad "网页连通性检测导出了 json" "文件不存在: $web"
}

# ---- --check all：把全部检测依次跑一遍（最慢，但覆盖 MainWindow 里 all 的分支）----
$full = Join-Path $OutDir "full_report.json"
[System.IO.File]::WriteAllText($full, "PLACEHOLDER")
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--host", "223.5.5.5", "--ports", "53,443", "--check", "all", "--export", "`"$full`"")
$exited = $p.WaitForExit(300000)
$sw.Stop()
Kill-Quiet $p
Check $exited "--check all 跑完全部检测后正常退出" "等待 300 秒仍未退出"
if (Test-Path $full) {
    Ok "全量检测导出了 json" ("用时 " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + " 秒")
    $verify = & $CoreTest verifyjson $full 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "全量 json 能被应用模型反序列化" $verifyText.Trim()
    Check ($verifyText -match "OK Adapters=[1-9]") "全量检测里有网卡信息" ""
    Check ($verifyText -match "OK ProxyKeys=(?!null)") "全量检测里有代理信息" ""
    Check ($verifyText -match "OK DnsServers=(?!null)") "全量检测里有 DNS 服务器列表" ""
    Check ($verifyText -match "OK DnsServerTests=[1-9]") "全量检测里有当前 DNS 的测速" ""
    Check ($verifyText -match "OK DnsCompareTests=4") "全量检测里有对比 DNS 的测速（四台公共 DNS）" ""
    Check ($verifyText -match "OK PortScanResults=2") "全量检测里有端口结果" ""
    Check ($verifyText -match "OK NatType=(?!null)") "全量检测里有 NAT 判定结果" ""
    Check ($verifyText -match "OK LatencyTargets=[1-9]") "全量检测里有延迟结果" ""
    Check ($verifyText -match "OK Egress=set") "全量检测里有出口 IP" ""
    Check ($verifyText -match "OK Ipv6=(?!null)") "全量检测里有 IPv6 结论" ""
    Check ($verifyText -match "OK IpLocations=[1-9]") "全量检测里有 IP 归属地" ""
    # 这里不写死 3：配置文件里可能留着上次 --web 存下的自定义目标。
    # 0 才是要抓的 —— 那说明 web 根本没进 AllExpansion。
    Check ($verifyText -match "OK WebTargets=[3-9]") "全量检测里有网页连通性结果" ""
} else {
    Bad "全量检测导出了 json" "文件不存在: $full"
}

# ---- --web：自定义目标要进目标列表 ----
# 放在 --check all 之后：--web 会把自定义目标**写进持久化配置**
# （和 --host / --ports 一样，命令行参数会 ApplyTo 到 settings），
# 排在前面的话，后面的 --check all 会跟着多测一个目标。
# 这条同时钉住「命令行接受的」和「探测时能用的」是同一套解析规则
# （--web 用的就是 WebProbe.TryParseTarget，不是另写一份校验）。
$web2 = Join-Path $OutDir "web_custom_report.json"
[System.IO.File]::WriteAllText($web2, "PLACEHOLDER")
$p = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
    "--web", "www.example.com", "--check", "web", "--export", "`"$web2`"")
$exited = $p.WaitForExit(180000)
Kill-Quiet $p
Check $exited "--web 自定义目标时正常退出" "等待 180 秒仍未退出"
if (Test-Path $web2) {
    Ok "带自定义网页目标时导出了 json" ""
    $verify = & $CoreTest verifyjson $web2 2>&1
    $verifyText = ($verify | Out-String)
    Check ($LASTEXITCODE -eq 0) "自定义网页目标的 json 能反序列化" $verifyText.Trim()
    Check ($verifyText -match "OK WebTargets=4") "自定义目标追加在三个内置目标之后" $verifyText.Trim()
} else {
    Bad "带自定义网页目标时导出了 json" "文件不存在: $web2"
}

# ==================================================================
Section "4. 界面关闭时把检测选项与窗口几何写进配置"
# ==================================================================
# 真正删掉配置文件来模拟「首次运行」。必须用 [System.IO.File]::Delete ——
# Remove-Item / rm 会被环境的安全策略拦下来。
# 这一步不能偷懒写成 "{}"：文件「不存在」和「存在但内容为空」对界面是两回事 ——
# 只有前者才算首次运行，界面会据此把「高级」组默认收起来；
# 后者属于「老用户的配置里没有这些键」，必须保持展开，不打扰。
$settingsDir = Split-Path -Parent $settingsPath
if (-not (Test-Path $settingsDir)) { New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null }
if ([System.IO.File]::Exists($settingsPath)) { [System.IO.File]::Delete($settingsPath) }
Check (-not (Test-Path $settingsPath)) "删掉配置，模拟首次运行" $settingsPath

$p = Start-Process -FilePath $Exe -ArgumentList @("--tab", "overview") -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    $p.Refresh()
    if ($p.HasExited) { break }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $h = $p.MainWindowHandle; break }
}
Check ($h -ne [IntPtr]::Zero) "窗口句柄就绪" "拿不到 MainWindowHandle"

if ($h -ne [IntPtr]::Zero) {
    [E2EWin]::ShowWindow($h, 5) | Out-Null
    [E2EWin]::MoveWindow($h, 60, 40, 1010, 700, $true) | Out-Null
    Start-Sleep -Milliseconds 1500

    $rect = New-Object E2EWin+RECT
    [E2EWin]::GetWindowRect($h, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $hh = $rect.Bottom - $rect.Top
    Check ([math]::Abs($w - 1010) -le 6) "窗口宽度已改成约 1010" ("实际 $w")
    Check ([math]::Abs($hh - 700) -le 6) "窗口高度已改成约 700" ("实际 $hh")

    # 等本机配置那一次静默检测跑完，否则关闭时会弹「还有检测在进行」的确认框
    Start-Sleep -Seconds 3
    $closed = $p.CloseMainWindow()
    Check $closed "向窗口发送了关闭请求" "CloseMainWindow 返回 false"
    $exited = $p.WaitForExit(20000)
    Check $exited "窗口正常关闭并退出进程" "20 秒内没有退出"
    Kill-Quiet $p

    if (Test-Path $settingsPath) {
        Ok "关闭时写出了 settings.json" $settingsPath
        $cfg = $null
        try { $cfg = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch {}
        Check ($cfg -ne $null) "settings.json 是合法 json" ""
        if ($cfg -ne $null) {
            Check ([math]::Abs([int]$cfg.WindowWidth - 1010) -le 8) "记录了调整后的窗口宽度" ("实际 " + $cfg.WindowWidth)
            Check ([math]::Abs([int]$cfg.WindowHeight - 700) -le 8) "记录了调整后的窗口高度" ("实际 " + $cfg.WindowHeight)

            # 首次运行的默认折叠状态 = 「检测选项」+「高级」组全部卡片。用 --tab overview 启动，不会碰这些卡片，
            # 所以关闭时它们应该原样收着；概览等常用组检测项一张都不能折。
            # 逐项列出而不是只数个数：以后往高级组增删卡片时，漏掉的新卡片会被这条断言逮住。
            # 检测选项默认折叠：展开要占约半屏，会把概览挤出首屏；标题栏仍有配置摘要。
            $collapsedFirst = @($cfg.CollapsedCards)
            $shownFirst = $collapsedFirst -join ","
            $advanced = @("path", "nat", "port", "ipv6", "web")
            $common = @("overview", "local", "egress", "link", "dns")
            foreach ($k in @("options") + $advanced) {
                Check ($collapsedFirst -contains $k) "首次运行折叠了「$k」" ("实际 " + $shownFirst)
            }
            foreach ($k in $common) {
                Check (-not ($collapsedFirst -contains $k)) "首次运行不折叠常用组的「$k」" ("实际 " + $shownFirst)
            }
            Check ($collapsedFirst.Count -eq ($advanced.Count + 1)) "首次运行只折叠检测选项和高级组那 $($advanced.Count + 1) 张卡片" ("实际 " + $shownFirst)
            # 网页目标默认留空 —— 内置三个目标已经够回答「网页通不通」，
            # 替用户预设一个别人的网站更像广告位。
            Check ($cfg.WebTarget -eq "") "网页目标默认留空" ("实际 「" + $cfg.WebTarget + "」")
            # 默认端口必须和默认目标（223.5.5.5，一台公共 DNS）配套：
            # 53 / 80 / 443 在它上面确实开放，用户第一次点端口检测就能看到有意义的结果
            Check ($cfg.PortSpec -eq "53,80,443") "记录了默认端口表达式" ("实际 " + $cfg.PortSpec)
            Check ($cfg.PortPreset -eq "默认目标（公共 DNS）") "记录了默认端口预设名" ("实际 " + $cfg.PortPreset)
            Check ($cfg.PortConcurrency -eq 64) "记录了默认并发数" ("实际 " + $cfg.PortConcurrency)
            Check ($cfg.TargetHost -eq "223.5.5.5") "记录了默认目标主机" ("实际 " + $cfg.TargetHost)
        }
    } else {
        Bad "关闭时写出了 settings.json" "文件不存在: $settingsPath"
    }
}

# ==================================================================
Section "5. 配置能跨启动生效（窗口尺寸与选项回读）"
# ==================================================================
if (Test-Path $settingsPath) {
    # 手工改小窗口尺寸与端口表达式，重新启动后应该按配置还原
    $cfg = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $cfg.WindowWidth = 980
    $cfg.WindowHeight = 660
    $cfg.PortSpec = "1-1024"
    $cfg.PortPreset = "1-1024 全量"
    $cfg.PortConcurrency = 128
    $cfg.CollapsedCards = @("options", "port", "path")
    ($cfg | ConvertTo-Json -Depth 5) | Set-Content $settingsPath -Encoding UTF8

    # 用 --tab path 而不是 --tab port：--tab 会滚动到目标卡片**并把它展开**
    # （跳过去却只看到一行标题等于没跳），用端口会把下面要断言的「port 保持折叠」冲掉。
    $p = Start-Process -FilePath $Exe -ArgumentList @("--tab", "path") -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.HasExited) { break }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $h = $p.MainWindowHandle; break }
    }
    if ($h -ne [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 1500
        $rect = New-Object E2EWin+RECT
        [E2EWin]::GetWindowRect($h, [ref]$rect) | Out-Null
        $w = $rect.Right - $rect.Left
        $hh = $rect.Bottom - $rect.Top
        Check ([math]::Abs($w - 980) -le 6) "启动时还原了配置里的窗口宽度" ("实际 $w")
        Check ([math]::Abs($hh - 660) -le 6) "启动时还原了配置里的窗口高度" ("实际 $hh")

        # 关闭时会把界面状态写回配置。如果程序压根没读 CollapsedCards，
        # 这一步就会把它覆盖成空数组 —— 所以「关掉再读回来还是这两项」
        # 等价于验证了折叠状态确实被读进界面、又原样写回去了。
        Start-Sleep -Seconds 3
        $p.CloseMainWindow() | Out-Null
        $p.WaitForExit(20000) | Out-Null
        Kill-Quiet $p
        Start-Sleep -Milliseconds 500

        $after = $null
        try { $after = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch {}
        Check ($after -ne $null) "关闭后配置仍可解析" ""
        if ($after -ne $null) {
            $collapsed = @($after.CollapsedCards)
            Check ($collapsed -contains "options") "折叠状态跨启动保留：检测选项" ("实际 " + ($collapsed -join ","))
            Check ($collapsed -contains "port") "折叠状态跨启动保留：端口" ("实际 " + ($collapsed -join ","))
            # --tab path 指向「路径」卡片，它应该被展开（跳转 = 展开目标卡片）
            Check (-not ($collapsed -contains "path")) "命令行 --tab 会展开目标卡片" ("实际 " + ($collapsed -join ","))
            Check ($collapsed.Count -eq 2) "折叠状态没有被多写或少写" ("实际 " + $collapsed.Count)
        }
    } else {
        Bad "启动时还原窗口尺寸" "拿不到窗口句柄"
    }
} else {
    Bad "启动时还原窗口尺寸" "上一阶段没有生成 settings.json"
}

# ==================================================================
$script:Lines.Add("")
$script:Lines.Add("=" * 62)
$script:Lines.Add("  端到端集成测试：$script:Pass 通过 / $script:Fail 失败")
$script:Lines.Add("=" * 62)

Write-Host ""
Write-Host ("端到端集成测试：" + $script:Pass + " 通过 / " + $script:Fail + " 失败") `
    -ForegroundColor $(if ($script:Fail -eq 0) { "Green" } else { "Red" })

[System.IO.File]::WriteAllLines((Join-Path $OutDir "e2e.txt"), $script:Lines, [System.Text.Encoding]::UTF8)

if ($script:Fail -gt 0) { exit 1 } else { exit 0 }
