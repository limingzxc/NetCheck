using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetCheck.Core;

/// <summary>
/// 路由追踪。
///
/// 用 .NET 内置 Ping 类的 TTL 选项实现，不需要管理员权限（原始套接字才需要）。
/// 原理：把 TTL 从 1 递增，中间路由器丢弃包时会回一个 ICMP Time Exceeded，
/// .NET 会把该路由器的地址放在 PingReply.Address 里返回。
/// </summary>
public static class Traceroute
{
    public static async Task<TracerouteReport> TraceAsync(
        string target, int maxHops, int timeoutMs, IProgress<int>? progress, CancellationToken ct)
    {
        var report = new TracerouteReport { Target = target };

        // 先解析成 IP，避免每一跳都做一次 DNS
        string ip;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(target, ct).ConfigureAwait(false);
            var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            ip = v4?.ToString() ?? addrs.FirstOrDefault()?.ToString() ?? "";
            if (ip.Length == 0)
            {
                report.Error = "无法解析目标地址";
                return report;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            report.Error = $"解析失败：{ex.Message}";
            return report;
        }

        if (ip != target) report.TargetIp = ip;

        using var ping = new Ping();
        int consecutiveTimeouts = 0;

        for (int ttl = 1; ttl <= maxHops; ttl++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(ttl);

            var hop = new HopInfo { Ttl = ttl };
            var options = new PingOptions(ttl, true);
            var sw = Stopwatch.StartNew();

            try
            {
                var reply = await ping.SendPingAsync(ip, timeoutMs, new byte[32], options).ConfigureAwait(false);
                sw.Stop();

                // 注意：不同 .NET 版本/平台上 TTL 超时返回的状态名不一致
                // （TimeExceeded / TtlExpired 都可能出现），所以不按具体枚举值分支，
                // 而是「到达 / 超时 / 有地址的中间响应」三分法，更稳。
                if (reply.Status == IPStatus.Success)
                {
                    hop.Address = reply.Address?.ToString() ?? ip;
                    hop.Ms = reply.RoundtripTime;
                    hop.Status = "到达";
                    hop.Reached = true;
                }
                else if (reply.Status == IPStatus.TimedOut || reply.Address == null)
                {
                    hop.Status = "超时";
                }
                else
                {
                    // 中间路由器回的 ICMP：此时 RoundtripTime 常为 0，必须用秒表计时
                    hop.Address = reply.Address.ToString();
                    hop.Ms = sw.Elapsed.TotalMilliseconds;
                    hop.Status = "中间跳";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                hop.Status = "失败";
                hop.Error = ex.Message;
            }

            report.Hops.Add(hop);

            if (hop.Reached) { report.Reached = true; break; }

            if (hop.Timeout)
            {
                consecutiveTimeouts++;
                // 连续多跳无响应，通常是中间设备屏蔽了 ICMP，继续跑下去没有意义
                if (consecutiveTimeouts >= 6 && ttl >= 4)
                {
                    report.StoppedEarly = true;
                    break;
                }
            }
            else consecutiveTimeouts = 0;
        }

        return report;
    }
}
