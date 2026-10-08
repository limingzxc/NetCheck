using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NetCheck.Core;

/// <summary>
/// 导出诊断包。
/// 把渲染好的报告 + 原始检测数据 + 系统网络命令的完整输出打包成一个 zip，
/// 可以直接发给运维，省去来回追问「你把 ipconfig 发我看下」。
///
/// 既然用途就是「发给别人」，就必须提供脱敏版本 —— 原始包里带着主机名、MAC、
/// 公网出口 IP 和局域网邻居表。脱敏规则见 <see cref="Mask"/>。
/// </summary>
public static class DiagBundle
{
    /// <summary>采集系统原始信息用的 PowerShell 脚本。</summary>
    private const string PsScript = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'SilentlyContinue'

        function Section($title) { Write-Output ""; Write-Output ("===== " + $title + " =====") }

        Section "网卡与 IP 配置"
        Get-NetIPConfiguration | Out-String -Width 200

        Section "网卡列表"
        Get-NetAdapter | Select-Object Name,InterfaceDescription,Status,LinkSpeed,MacAddress |
            Format-Table -AutoSize | Out-String -Width 200

        Section "DNS 客户端配置"
        Get-DnsClientServerAddress | Select-Object InterfaceAlias,AddressFamily,ServerAddresses |
            Format-Table -AutoSize | Out-String -Width 200

        Section "路由表"
        Get-NetRoute | Sort-Object RouteMetric |
            Select-Object DestinationPrefix,NextHop,RouteMetric,InterfaceAlias |
            Format-Table -AutoSize | Out-String -Width 200

        Section "ARP / 邻居表"
        Get-NetNeighbor | Select-Object InterfaceAlias,IPAddress,LinkLayerAddress,State |
            Format-Table -AutoSize | Out-String -Width 200

        Section "活动 TCP 连接（前 200 条）"
        Get-NetTCPConnection | Select-Object -First 200 LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess |
            Format-Table -AutoSize | Out-String -Width 200

        Section "网卡收发统计"
        Get-NetAdapterStatistics |
            Select-Object Name,ReceivedBytes,ReceivedUnicastPackets,SentBytes,SentUnicastPackets |
            Format-Table -AutoSize | Out-String -Width 200

        Section "代理设置（注册表）"
        Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' |
            Select-Object ProxyEnable,ProxyServer,AutoConfigURL | Format-List | Out-String

        Section "系统信息"
        Get-CimInstance Win32_OperatingSystem |
            Select-Object Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime | Format-List | Out-String
        Get-CimInstance Win32_ComputerSystem |
            Select-Object Manufacturer,Model,TotalPhysicalMemory | Format-List | Out-String

        # ipconfig 是原生程序，输出走系统 ANSI 代码页（中文系统为 GBK），
        # 必须先切到 936 解码，再切回 UTF-8 写出，否则中文会乱码。
        [Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding(936)
        $ipcfg = ipconfig /all | Out-String -Width 200
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        Section "ipconfig /all（原始输出）"
        Write-Output $ipcfg
        """;

    /// <param name="mask">
    /// 是否脱敏。为 true 时四个产物全部过一遍 <see cref="Mask"/> ——
    /// **包括报告和 json**，因为这两份里同样带着主机名和公网出口 IP，
    /// 只脱系统信息那一个是漏的。具体脱什么见 <see cref="Mask"/> 的注释。
    /// </param>
    public static async Task<BundleResult> ExportAsync(
        DiagState state, string reportText, string zipPath,
        IProgress<string>? progress, CancellationToken ct, bool mask = false)
    {
        var result = new BundleResult { Masked = mask };
        var temp = Path.Combine(Path.GetTempPath(), "netcheck_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);

        // 四份产物统一在这里过一遍。放在写文件之前、而不是各自写各自的，
        // 是为了保证「以后新增一个产物」时不会忘记脱敏 —— 只有这一个出口。
        string Sanitize(string text) => mask ? Mask.Apply(text, state.Hostname) : text;

        try
        {
            // 1. 渲染好的报告
            progress?.Report(mask ? "写入报告（已脱敏）…" : "写入报告…");
            await File.WriteAllTextAsync(Path.Combine(temp, "报告.txt"),
                Sanitize(reportText), new UTF8Encoding(true), ct).ConfigureAwait(false);

            // 2. 原始结构化数据
            progress?.Report("写入原始数据…");
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            await File.WriteAllTextAsync(Path.Combine(temp, "原始数据.json"),
                Sanitize(json), new UTF8Encoding(false), ct).ConfigureAwait(false);

            // 3. 系统原始信息
            progress?.Report("采集系统网络信息…");
            var sysInfo = await CollectSystemInfoAsync(ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(temp, "系统信息.txt"),
                Sanitize(sysInfo), new UTF8Encoding(true), ct).ConfigureAwait(false);

            // 4. 说明文件
            progress?.Report("写入说明…");
            await File.WriteAllTextAsync(Path.Combine(temp, "说明.txt"),
                Sanitize(BuildReadme(state, mask)), new UTF8Encoding(true), ct).ConfigureAwait(false);

            // 5. 打包
            progress?.Report("压缩…");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(temp, zipPath, CompressionLevel.Optimal, false);

            var fi = new FileInfo(zipPath);
            result.Ok = true;
            result.Path = zipPath;
            result.SizeBytes = fi.Length;
            result.Entries = Directory.GetFiles(temp).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 临时目录清理失败不影响结果 */ }
        }

        return result;
    }

    private static async Task<string> CollectSystemInfoAsync(CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(),
            "netcheck_collect_" + Guid.NewGuid().ToString("N")[..8] + ".ps1");

        try
        {
            // 写成带 BOM 的 UTF-8，Windows PowerShell 5.1 才能正确读中文
            await File.WriteAllTextAsync(scriptPath, PsScript, new UTF8Encoding(true), ct)
                            .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "（无法启动 PowerShell 采集系统信息）";

            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(true); } catch { }
                return "（采集系统信息超时）";
            }

            var text = await stdout.ConfigureAwait(false);
            var err = await stderr.ConfigureAwait(false);

            if (text.Length == 0 && err.Length > 0)
                return "（采集失败）\n" + err;

            return text;
        }
        catch (Exception ex)
        {
            return $"（采集系统信息出错：{ex.Message}）";
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
        }
    }

    private static string BuildReadme(DiagState s, bool mask)
    {
        var sb = new StringBuilder();
        sb.AppendLine(mask ? "NetCheck 诊断包（已脱敏）" : "NetCheck 诊断包");
        sb.AppendLine("================");
        sb.AppendLine();
        sb.AppendLine($"生成时间：{s.Timestamp}");
        sb.AppendLine($"主机名  ：{s.Hostname}");
        if (mask)
        {
            // 收件人看到 HOST-*** / 203.0.*.* 会以为数据坏了，得先说清楚是故意的
            sb.AppendLine();
            sb.AppendLine("**本包已脱敏，可以安全转发。** 打码规则：");
            sb.AppendLine("  · 主机名      → HOST-***");
            sb.AppendLine("  · MAC 地址    → 保留厂商前缀，后三段打码");
            sb.AppendLine("  · 公网 IPv4   → 保留前两段（运营商/地域），后两段打码");
            sb.AppendLine("  · IPv6        → 保留前两组，其余打码");
            sb.AppendLine("  · 用户名      → C:\\Users\\***");
            sb.AppendLine("  · 局域网 IP、公共 DNS 地址（223.5.5.5 等）原样保留 —— 它们定位不到人，");
            sb.AppendLine("    而诊断离不开。");
            sb.AppendLine("  · 延迟、丢包、路由跳数、端口状态等测量结果全部保留。");
        }
        sb.AppendLine();
        sb.AppendLine("包含内容：");
        sb.AppendLine("  报告.txt      —— 体检结果的完整文字报告（可直接阅读）");
        sb.AppendLine("  原始数据.json —— 所有检测项的结构化数据（便于脚本处理）");
        sb.AppendLine("  系统信息.txt  —— 系统网络命令的原始输出（网卡/路由/ARP/TCP连接/统计等）");
        sb.AppendLine();
        sb.AppendLine("快速结论：");

        if (s.Adapters is { Count: > 0 })
        {
            var a = s.Adapters.FirstOrDefault(x => x.IsUp && x.Gateway.Length > 0) ?? s.Adapters[0];
            sb.AppendLine($"  · 主用网卡 {a.Name}，IPv4 {a.IPv4}，网关 {a.Gateway}");
        }

        var ips = s.AllPublicIps();
        if (ips.Count > 1)
            sb.AppendLine($"  · 检测到 {ips.Count} 个不同公网出口 IP（{string.Join("、", ips)}），链路存在多出口或中间代理");

        if (s.Nat != null)
            sb.AppendLine($"  · NAT 类型：{s.Nat.Type}");

        if (s.Latency != null)
        {
            var worst = s.Latency.Targets.Where(t => t.Ok).OrderByDescending(t => t.Avg).FirstOrDefault();
            if (worst != null)
                sb.AppendLine($"  · 最慢目标 {worst.Name} 平均 {worst.Avg:F1} ms");
        }

        if (s.DnsServerTests != null)
        {
            var bad = s.DnsServerTests.Where(t => !t.Ok).ToList();
            if (bad.Count > 0)
                sb.AppendLine($"  · 有 {bad.Count} 个 DNS 服务器无响应：{string.Join("、", bad.Select(b => b.Server))}");
        }

        if (s.Hijack is { AnyHijacked: true })
            sb.AppendLine("  · 警告：检测到 DNS 劫持迹象");

        if (s.BandwidthDirect is { Ok: true } bw)
            sb.AppendLine($"  · 下载带宽约 {bw.Mbps:F1} Mbps");

        if (s.Traceroute is { Ok: true } tr && !tr.Reached)
            sb.AppendLine($"  · 路由追踪未到达目标（{tr.Hops.Count} 跳后中断）");

        if (s.PortScan is { } ps)
            sb.AppendLine($"  · 端口检测 {ps.Host}：开放 {ps.OpenCount} 个 / 共 {ps.Results.Count} 个");

        return sb.ToString();
    }
}

public sealed class BundleResult
{
    public bool Ok { get; set; }
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public List<string> Entries { get; set; } = new();
    public string Error { get; set; } = "";

    /// <summary>这一包是不是脱敏过的。导出成功的提示里要写出来，免得用户自己都记不清发出去的是哪一版。</summary>
    public bool Masked { get; set; }
}
