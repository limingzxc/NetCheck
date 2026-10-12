using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NetCheck.Core;

/// <summary>
/// STUN 客户端（RFC 5389）与 NAT 类型判定。
///
/// 关键点一：所有查询必须复用**同一个已绑定的 UDP socket**。
/// 如果每次查询都新建 socket，源端口必然不同，映射端口也会不同，
/// 结果会把任何网络都误判成对称型 NAT。
///
/// 关键点二：服务器要选**国内能直连**的。这是给国内用户用的工具，
/// 一旦把判定押在境外服务器上，网络稍有风吹草动就只剩一台甚至零台可用，
/// 而判定 NAT 类型至少需要两台服务器回应。
/// </summary>
public static class StunAndNat
{
    private const uint MagicCookie = 0x2112A442;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 全部 STUN 服务器都未回应时的哨兵值（不是异常、不是 IP）。
    /// 集中在这里，比较方（<see cref="DiagState.AllPublicIps"/>、Ui 层）引用同一个常量，
    /// 避免「改了一处忘了另一处」之后字符串悄悄对不上。
    /// </summary>
    public const string Unavailable = "不可用";

    /// <summary>
    /// STUN 服务器，按「国内可达」排序。
    ///
    /// 前三台是国内大厂的公共服务（实测 28~35 ms），不依赖任何境外链路。
    /// 最后一台 Cloudflare 只作补充：它在多数网络里走的路径与前三台不同，
    /// 能多提供一个参照点；连不上也不影响结论，只是少一个样本。
    ///
    /// 顺序有意义 —— 出口 IP 探测按这个顺序逐个试，第一个成功就返回。
    /// </summary>
    public static readonly (string Host, int Port)[] Servers =
    {
        ("stun.hitv.com", 3478),
        ("stun.chat.bilibili.com", 3478),
        ("stun.douyucdn.cn", 18000),
        ("stun.cloudflare.com", 3478),
    };

    public static async Task<NatReport> DetectAsync(IReadOnlyCollection<string> localIps, CancellationToken ct)
    {
        var report = new NatReport();

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        report.LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        foreach (var obs in await QueryAllAsync(udp, ct).ConfigureAwait(false))
            report.Observations.Add(obs);

        Classify(report, localIps);
        return report;
    }

    /// <summary>用 STUN 直接取出口地址（走原生 UDP，绕过 HTTP 代理）。</summary>
    public static async Task<string> GetEgressIpAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

        // 逐个试，第一个成功就返回。列表是按「国内可达」排的，
        // 正常情况下第一台就有回应，走不到最后。
        foreach (var (host, port) in Servers)
        {
            var obs = await QueryOneAsync(udp, host, port, ct).ConfigureAwait(false);
            if (obs.Ok) return obs.MappedIp;
        }
        return Unavailable;
    }

    /// <summary>
    /// 并发查询全部服务器，共用一个 socket。
    ///
    /// 早先是串行 for 循环，问题在于：只要有一台服务器被拦掉，就要为它单独等满一个超时。
    /// 四台里三台不通就是九秒起步，而用户只是想知道自己的 NAT 类型。
    /// 并发之后总耗时只取决于最慢的那一台，最坏也就一个超时。
    ///
    /// 共用一个 socket 就必须按事务 ID 分发响应：回复可能乱序到达，也可能根本不来。
    /// </summary>
    private static async Task<List<StunObservation>> QueryAllAsync(UdpClient udp, CancellationToken ct)
    {
        var results = new List<StunObservation>();
        var pending = new Dictionary<string, StunObservation>(StringComparer.Ordinal);

        foreach (var (host, port) in Servers)
        {
            var obs = new StunObservation { Server = $"{host}:{port}" };
            results.Add(obs);

            var request = BuildBindingRequest(out var txId);
            try
            {
                await udp.SendAsync(request, request.Length, host, port).ConfigureAwait(false);
                pending[Convert.ToHexString(txId)] = obs;
            }
            catch (Exception ex)
            {
                // DNS 解析失败 / 网络不可达：只影响这一台，其余照常
                obs.Error = ex.Message;
            }
        }

        if (pending.Count == 0) return results;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(QueryTimeout);

        try
        {
            while (pending.Count > 0)
            {
                var res = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (res.Buffer.Length < 20) continue;

                var key = Convert.ToHexString(res.Buffer.AsSpan(8, 12));
                if (!pending.Remove(key, out var obs)) continue;   // 别人的包，或重复包

                if (TryParseMappedAddress(res.Buffer, out var ip, out var p))
                {
                    obs.MappedIp = ip;
                    obs.MappedPort = p;
                }
                else
                {
                    obs.Error = "响应中无映射地址属性";
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 到点收工，剩下没回应的按超时记
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            foreach (var obs in pending.Values) obs.Error = ex.Message;
            pending.Clear();
        }

        foreach (var obs in pending.Values) obs.Error = "超时";
        return results;
    }

    private static async Task<StunObservation> QueryOneAsync(UdpClient udp, string host, int port, CancellationToken ct)
    {
        var obs = new StunObservation { Server = $"{host}:{port}" };
        try
        {
            var request = BuildBindingRequest(out var txId);
            var myKey = Convert.ToHexString(txId);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);

            await udp.SendAsync(request, request.Length, host, port).ConfigureAwait(false);

            // 共用一个 socket 时可能收到其它服务器的响应，必须按事务 ID 匹配
            while (!timeout.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (res.Buffer.Length < 20) continue;
                if (Convert.ToHexString(res.Buffer.AsSpan(8, 12)) != myKey) continue;

                if (TryParseMappedAddress(res.Buffer, out var ip, out var p))
                {
                    obs.MappedIp = ip;
                    obs.MappedPort = p;
                }
                else
                {
                    obs.Error = "响应中无映射地址属性";
                }
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            obs.Error = "超时";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            obs.Error = ex.Message;
        }
        return obs;
    }

    /// <summary>
    /// 从观测结果推出 NAT 类型。纯函数，不碰网络 —— 所以各分支都能在单元测试里覆盖，
    /// 这也是它公开的原因（真实网络下很难凑齐「只有一台服务器回应」这类场景）。
    /// </summary>
    public static void Classify(NatReport r, IReadOnlyCollection<string> localIps)
    {
        var ok = r.Observations.Where(o => o.Ok).ToList();
        if (ok.Count == 0)
        {
            r.Type = "未知";
            r.Detail = "所有 STUN 服务器均不可达，可能被防火墙或网络策略拦截了 UDP。";
            return;
        }

        r.MappedIps = ok.Select(o => o.MappedIp).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        r.MappedPorts = ok.Select(o => o.MappedPort).Distinct().OrderBy(x => x).ToList();

        if (r.MappedIps.Count > 1)
        {
            r.Type = "多出口 / 对称型 NAT";
            r.Detail = $"不同目标看到的公网 IP 不同（{string.Join("、", r.MappedIps)}），"
                     + "说明链路存在多出口或中间网关 —— 分流代理（国内直连、境外走代理）也会是这个结果。"
                     + "P2P 打洞成功率低，建议走中继。";
        }
        else if (localIps.Contains(r.MappedIps[0]))
        {
            r.Type = "公网直连（无 NAT）";
            r.Detail = "本机地址即为公网地址，无 NAT 转换。";
        }
        else if (ok.Count < 2)
        {
            // 只有一台服务器回应。圆锥型和对称型的区别，看的是**同一本地端口
            // 在不同目标下映射到的外部端口是否一致** —— 只有一个目标就无从比较。
            //
            // 这里必须老实说测不出来。早先的版本会直接落到下面
            // MappedPorts.Count == 1 那个分支，把「没测出来」当成「测出来是圆锥型」，
            // 而圆锥型恰好是「好消息」，等于默默给了用户一个假的绿灯。
            r.Type = "无法判定";
            r.Detail = $"只有 {ok.Count} 台 STUN 服务器返回了结果，不足以比较端口映射行为，"
                     + "判定需要至少两台服务器都回应。";
        }
        else if (r.MappedPorts.Count == 1)
        {
            r.Type = "圆锥型 NAT（Cone）";
            r.Detail = $"同一本地端口在不同目标下映射到相同外部端口（{r.MappedPorts[0]}），"
                     + "端口映射稳定，P2P 打洞成功率高。";
        }
        else
        {
            r.Type = "对称型 NAT（Symmetric）";
            r.Detail = $"同一本地端口在不同目标下映射到不同外部端口（{string.Join("、", r.MappedPorts)}），"
                     + "P2P 打洞困难，需要 TURN 中继。";
        }
    }

    private static byte[] BuildBindingRequest(out byte[] txId)
    {
        txId = RandomNumberGenerator.GetBytes(12);
        var buf = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0), 0x0001);   // Binding Request
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), 0);        // length
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), MagicCookie);
        txId.CopyTo(buf.AsSpan(8));
        return buf;
    }

    private static bool TryParseMappedAddress(byte[] data, out string ip, out int port)
    {
        ip = "";
        port = 0;
        if (data.Length < 20) return false;

        int attrLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        int i = 20;
        int end = Math.Min(20 + attrLen, data.Length);

        while (i + 4 <= end)
        {
            ushort aType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i));
            int aLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 2));
            if (i + 4 + aLen > data.Length) break;

            var val = data.AsSpan(i + 4, aLen);

            if (aType == 0x0020 && aLen >= 8)          // XOR-MAPPED-ADDRESS
            {
                port = BinaryPrimitives.ReadUInt16BigEndian(val) ^ (ushort)(MagicCookie >> 16);
                var addr = new byte[4];
                for (int k = 0; k < 4; k++)
                    addr[k] = (byte)(val[4 + k] ^ (byte)(MagicCookie >> (24 - k * 8)));
                ip = new IPAddress(addr).ToString();
                return true;
            }
            if (aType == 0x0001 && aLen >= 8)          // MAPPED-ADDRESS（旧格式）
            {
                port = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2));
                ip = new IPAddress(val.Slice(4, 4).ToArray()).ToString();
                return true;
            }

            i += 4 + aLen + ((4 - aLen % 4) % 4);
        }
        return false;
    }
}
