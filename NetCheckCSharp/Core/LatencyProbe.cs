using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetCheck.Core;

/// <summary>
/// 网络延迟探测。
/// ICMP 用 .NET 内置的 Ping 类（Windows 上普通用户即可执行，不需要管理员权限）；
/// TCP 握手计时作为补充，因为它能反映应用层真实可达性。
/// </summary>
public static class LatencyProbe
{
    /// <summary>
    /// 一类探测目标的判定标准。**不同用途对延迟的容忍度差很多**：
    /// 网页 100 ms 完全无感，游戏 100 ms 已经能明显感觉到操作滞后，
    /// 所以阈值必须分类给，一个数打天下会把「能打游戏」和「能刷网页」判成同一档。
    /// </summary>
    public sealed record Category(string Name, string Hint, double GoodMs, double WarnMs);

    public static readonly Category[] Categories =
    {
        new("网页浏览", "打开网页、刷新、搜索", 100, 250),
        new("在线视频", "起播与拖动进度条（缓冲更看带宽）", 120, 300),
        new("游戏",     "实时对战，对延迟最敏感", 50, 100),
    };

    /// <summary>按分类名取判定标准；认不出来的一律按第一类（网页浏览）算。</summary>
    public static Category CategoryOf(string? name)
        => Categories.FirstOrDefault(c => c.Name == name) ?? Categories[0];

    /// <summary>
    /// 探测目标：(分类, 显示名, 主机, 候选端口)。端口不通不代表主机不可达，按顺序回退。
    ///
    /// 选的是**用户实际会用的服务**，不再是 DNS 服务器 ——
    /// 用户关心的是「刷网页卡不卡、看视频转不转圈、打游戏跟不跟得上手」，
    /// 而 DNS 服务器的响应速度在「DNS」那张卡片里已经单独测过，放在这里纯属重复。
    /// 这些域名都会解析到各家 CDN 的边缘节点，所以测出来的就是「到最近的服务节点有多远」。
    /// </summary>
    public static readonly (string Category, string Name, string Host, int[] Ports)[] Targets =
    {
        ("网页浏览", "百度",     "www.baidu.com",   new[] { 443 }),
        ("网页浏览", "淘宝",     "www.taobao.com",  new[] { 443 }),
        ("网页浏览", "腾讯网",   "www.qq.com",      new[] { 443 }),

        ("在线视频", "哔哩哔哩", "www.bilibili.com", new[] { 443 }),
        ("在线视频", "爱奇艺",   "www.iqiyi.com",    new[] { 443 }),
        ("在线视频", "腾讯视频", "v.qq.com",         new[] { 443 }),

        // 国内平台和境外平台（Steam）通常差一个数量级，放在一起对比才有意义
        ("游戏",     "腾讯游戏", "game.qq.com",            new[] { 443 }),
        ("游戏",     "网易游戏", "game.163.com",           new[] { 443 }),
        ("游戏",     "Steam",    "store.steampowered.com", new[] { 443 }),
    };

    public static async Task<LatencyTarget> IcmpAsync(
        string name, string host, int count, int timeoutMs, CancellationToken ct)
    {
        var t = new LatencyTarget { Name = name, Host = host, Method = "ICMP" };
        int lost = 0;

        using var ping = new Ping();
        var options = new PingOptions { DontFragment = false };
        var buffer = new byte[32];

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(host, timeoutMs, buffer, options).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                    t.Samples.Add(reply.RoundtripTime);
                else
                    lost++;
            }
            catch { lost++; }

            if (i < count - 1)
                await Task.Delay(120, ct).ConfigureAwait(false);
        }

        Fill(t, lost, count);
        return t;
    }

    /// <summary>按候选端口依次尝试 TCP 握手，返回第一个连通端口的结果。</summary>
    public static async Task<LatencyTarget> TcpAsync(
        string name, string host, int[] ports, int count, int timeoutMs, CancellationToken ct)
    {
        LatencyTarget? last = null;
        foreach (var port in ports)
        {
            var t = await TcpOnceAsync(name, host, port, count, timeoutMs, ct).ConfigureAwait(false);
            if (t.Ok) return t;
            last = t;
        }
        return last ?? new LatencyTarget { Name = name, Host = host, Method = "TCP" };
    }

    private static async Task<LatencyTarget> TcpOnceAsync(
        string name, string host, int port, int count, int timeoutMs, CancellationToken ct)
    {
        var t = new LatencyTarget { Name = name, Host = host, Method = $"TCP:{port}" };
        int lost = 0;

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                sw.Stop();
                t.Samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { lost++; }

            if (i < count - 1)
                await Task.Delay(60, ct).ConfigureAwait(false);
        }

        Fill(t, lost, count);
        return t;
    }

    private static void Fill(LatencyTarget t, int lost, int total)
    {
        if (t.Samples.Count > 0)
        {
            t.Min = t.Samples.Min();
            t.Max = t.Samples.Max();
            t.Avg = t.Samples.Average();
        }
        t.Loss = total > 0 ? lost * 100.0 / total : 100;
    }

    /// <param name="rounds">每个目标的探测次数。次数越多抖动数据越准，但耗时线性增长。</param>
    public static async Task<LatencyReport> RunAllAsync(
        string? gateway, IProgress<string>? progress, CancellationToken ct, int rounds = 4)
    {
        var report = new LatencyReport();
        int n = Math.Max(1, rounds);

        if (!string.IsNullOrWhiteSpace(gateway))
        {
            progress?.Report($"探测网关 {gateway}…");
            report.Gateway = await IcmpAsync("网关", gateway!, n, 1000, ct).ConfigureAwait(false);
            if (!report.Gateway.Ok)
            {
                // 有些网关不响应 ICMP，退回 TCP 探测
                var tcp = await TcpOnceAsync("网关", gateway!, 80, Math.Max(1, n / 2), 800, ct)
                    .ConfigureAwait(false);
                if (tcp.Ok) report.Gateway = tcp;
            }
        }

        foreach (var (category, name, host, ports) in Targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"探测 {category} · {name}…");

            // 先试 ICMP，它最接近链路真实延迟。
            var icmp = await IcmpAsync(name, host, n, 1500, ct).ConfigureAwait(false);

            LatencyTarget chosen;
            if (icmp.Ok)
            {
                // ICMP 通了就不用再跑 TCP。以前这里是两个都跑、最后只留一个，
                // 白白多花一倍时间；目标从 5 个涨到 9 个之后这个浪费就明显了。
                chosen = icmp;
            }
            else
            {
                progress?.Report($"{name} 不响应 ICMP，改用 TCP 握手…");
                chosen = await TcpAsync(name, host, ports, n, 1500, ct).ConfigureAwait(false);
            }

            chosen.Name = name;
            chosen.Category = category;
            report.Targets.Add(chosen);
        }

        return report;
    }
}
