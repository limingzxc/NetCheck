using System.Net.NetworkInformation;

namespace NetCheck.Core;

/// <summary>
/// MTU 探测。
/// 用「不分片(DF)」的 ICMP 包做二分查找：能通过的最大负载 + 28 字节（20 IP 头 + 8 ICMP 头）= 路径 MTU。
/// 用于排查「能 ping 通但网页打不开」这类 PMTUD 黑洞问题 —— 现象是 TCP 握手成功但大数据传输卡死。
/// </summary>
public static class MtuProbe
{
    /// <summary>以太网默认 MTU 1500 对应的最大 ICMP 负载。</summary>
    public const int EthernetMaxPayload = 1472;

    public static async Task<MtuReport> DetectAsync(
        string host, IProgress<string>? progress, CancellationToken ct)
    {
        var report = new MtuReport { Target = host };

        using var ping = new Ping();
        var options = new PingOptions(64, true);   // DF=1，禁止分片

        // 先用一个小包确认目标是否响应 DF 探测
        progress?.Report("确认目标是否支持 DF 探测…");
        var probe = new byte[64];
        PingReply? first = null;
        try { first = await ping.SendPingAsync(host, 2000, probe, options).ConfigureAwait(false); }
        catch (Exception ex) { report.Error = ex.Message; return report; }

        if (first == null || first.Status != IPStatus.Success)
        {
            report.Error = $"目标不响应「禁止分片」的 ICMP 探测（{first?.Status}），无法测量 MTU。"
                         + "常见于目标主机或中间设备屏蔽了 ICMP。";
            return report;
        }

        report.Reachable = true;

        // 二分查找最大可通过负载
        int lo = 64, hi = 9000;                    // 上限放宽以支持巨型帧
        int best = 64;
        var tested = new List<(int Size, bool Ok)>();

        while (lo <= hi)
        {
            ct.ThrowIfCancellationRequested();
            int mid = (lo + hi) / 2;
            progress?.Report($"测试负载 {mid} 字节…");

            bool ok;
            try
            {
                var reply = await ping.SendPingAsync(host, 1500, new byte[mid], options).ConfigureAwait(false);
                ok = reply.Status == IPStatus.Success;
            }
            catch { ok = false; }

            tested.Add((mid, ok));
            if (ok) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        report.MaxPayload = best;
        report.PathMtu = best + 28;
        report.Tests = tested.Select(t => new MtuTest { Size = t.Size, Ok = t.Ok }).ToList();

        // 再向上探一格确认边界
        try
        {
            var reply = await ping.SendPingAsync(host, 1500, new byte[best + 1], options).ConfigureAwait(false);
            report.NextPayloadFails = reply.Status != IPStatus.Success;
        }
        catch { report.NextPayloadFails = true; }

        return report;
    }
}

public sealed class MtuTest
{
    public int Size { get; set; }
    public bool Ok { get; set; }
}

public sealed class MtuReport
{
    public string Target { get; set; } = "";
    public bool Reachable { get; set; }
    public int MaxPayload { get; set; }
    public int PathMtu { get; set; }
    public bool NextPayloadFails { get; set; }
    public List<MtuTest> Tests { get; set; } = new();
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0 && Reachable;

    public string Assessment
    {
        get
        {
            if (!Ok) return "";
            if (PathMtu >= 1500) return "路径 MTU 正常（1500，标准以太网）";
            if (PathMtu >= 1400) return "路径 MTU 略低，可能是 PPPoE 或隧道封装导致";
            if (PathMtu >= 1200) return "路径 MTU 偏低，建议留意 PMTUD 黑洞问题";
            return "路径 MTU 很低，很可能存在隧道封装或中间设备限制";
        }
    }
}
