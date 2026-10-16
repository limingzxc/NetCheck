using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NetCheck.Core;

/// <summary>
/// DNS 相关探测：服务器响应速度、系统解析耗时、劫持检测。
/// </summary>
public static class DnsProbe
{
    public static readonly string[] TestDomains =
        { "www.baidu.com", "www.qq.com", "www.taobao.com", "github.com" };

    /// <summary>用于对比解析结果的公共 DNS。</summary>
    public static readonly string[] CompareServers =
        { "223.5.5.5", "119.29.29.29", "114.114.114.114", "8.8.8.8" };

    /// <summary>
    /// 测「当前 DNS」和「对比 DNS」时统一用这个域名。
    ///
    /// **两边必须是同一个域名，数字才能横向比。** 换个域名，解析结果条数、是否命中 CDN 边缘、
    /// 有没有被缓存都会变，耗时差个几十毫秒很正常 —— 那时候「我的 DNS 比阿里 DNS 慢 30 ms」
    /// 就成了一个由测量口径不一致造出来的假结论。
    /// </summary>
    public const string CompareTestDomain = "www.baidu.com";

    /// <summary>
    /// 测一遍全部对比用的公共 DNS。
    ///
    /// 存在的意义是回答「要不要换个 DNS」：只报当前 DNS 的 36 ms 说明不了任何问题，
    /// 得知道 223.5.5.5 只要 12 ms，用户才知道自己亏了多少。
    /// 服务器之间**并行**跑 —— 串行的话 4 台各超时一次就是 4 倍等待。
    /// </summary>
    public static async Task<List<DnsServerTest>> TestCompareServersAsync(
        int timeoutMs, CancellationToken ct)
    {
        var tasks = CompareServers
            .Select(server => TestServerAsync(server, CompareTestDomain, timeoutMs, ct))
            .ToList();
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    // ------------------------------------------------------------------
    // 单服务器响应速度
    // ------------------------------------------------------------------

    /// <summary>
    /// 测一台 DNS 服务器解析 <paramref name="domain"/> 的耗时。
    /// <paramref name="port"/> 平时不用传；只有单元测试会在 127.0.0.1 的临时端口上
    /// 起一个假应答器来验证收发路径（见 CoreTest 的 DnsProbeLoopback）。
    /// </summary>
    public static async Task<DnsServerTest> TestServerAsync(
        string server, string domain, int timeoutMs, CancellationToken ct, int port = 53)
    {
        var r = new DnsServerTest { Server = server };
        try
        {
            var (rcode, answers, ms) = await QueryAAsync(server, port, domain, timeoutMs, ct)
                .ConfigureAwait(false);
            r.Ms = ms;
            r.Answers = answers.Count;
            if (rcode != 0) r.Error = $"rcode={rcode}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            r.Error = "超时";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            r.Error = ex.Message.Length > 40 ? ex.Message[..40] : ex.Message;
        }
        return r;
    }

    // ------------------------------------------------------------------
    // 统一的 UDP 查询收发
    // ------------------------------------------------------------------

    /// <summary>
    /// 向一台 DNS 发一条 A 记录查询，返回 (rcode, A 记录, 耗时 ms)。
    /// 测速、劫持检测、结果对比三项共用这一份实现 —— 原来是同一段
    /// 「建 socket → 发包 → 收包 → 解析」抄了三遍。
    /// </summary>
    private static async Task<(int RCode, List<string> Answers, double Ms)> QueryAAsync(
        string server, int port, string domain, int timeoutMs, CancellationToken ct)
    {
        var query = BuildQuery(domain, out ushort id);
        using var udp = new UdpClient();
        // Connect 到服务器端点：只接收它的响应。多台并行测试时各用各的 socket，
        // 但连上之后至少不会把别的源地址来的包当成这一台的回答
        udp.Connect(server, port);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var sw = Stopwatch.StartNew();
        // 连上之后必须用**不带端点**的重载发包。再传一次 host/port 会走
        // Socket.SendTo 那条路，而已连接的 socket 不允许指定目标，
        // 会直接抛「Cannot send packets to an arbitrary host」。
        // v2.6.1 发布版「所有公共 DNS 都没有响应」就是这一行造成的：
        // 四台服务器全部命中同一个异常，界面上看起来像网络断了。
        await udp.SendAsync(query, query.Length).ConfigureAwait(false);
        var resp = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
        sw.Stop();

        var (rcode, answers) = ParseResponse(resp.Buffer, id);
        return (rcode, answers, sw.Elapsed.TotalMilliseconds);
    }

    // ------------------------------------------------------------------
    // 系统解析器耗时
    // ------------------------------------------------------------------

    public static async Task<DnsResolveTest> ResolveAsync(string domain, int rounds, CancellationToken ct)
    {
        var r = new DnsResolveTest { Domain = domain };
        var times = new List<double>();

        for (int i = 0; i < rounds; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(domain, ct).ConfigureAwait(false);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                if (r.Address.Length == 0)
                {
                    var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    r.Address = v4?.ToString() ?? addrs.FirstOrDefault()?.ToString() ?? "";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return r; }
        }

        if (times.Count > 0)
        {
            r.Min = times.Min();
            r.Max = times.Max();
            r.Avg = times.Average();
            r.Ok = true;
        }
        return r;
    }

    // ------------------------------------------------------------------
    // 劫持 / 污染检测
    // ------------------------------------------------------------------

    /// <summary>
    /// 用一个随机生成、保证不存在的域名去问各个 DNS。
    /// 正常情况应该返回 NXDOMAIN（rcode=3）或空答案；
    /// 若返回了 A 记录，说明该 DNS 在做广告注入或劫持 —— 这个判据几乎不会误报。
    ///
    /// 四台服务器**并行**查询：串行时一台被拦就要白等满一个超时，
    /// 四台最坏 12 秒起步，而用户只是想知道自己的 DNS 干不干净。
    /// </summary>
    public static async Task<HijackReport> DetectHijackAsync(CancellationToken ct)
    {
        var report = new HijackReport();
        // 随机后缀确保域名一定不存在（.NET 8 没有 GetHexString，用 ToHexString 代替）
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var probeDomain = $"no-such-{suffix}.com";
        report.ProbeDomain = probeDomain;

        report.Checks.AddRange(await Task.WhenAll(
            CompareServers.Select(server => ProbeHijackAsync(server, probeDomain, ct)))
            .ConfigureAwait(false));

        // 再对比几个真实域名在各 DNS 上的解析结果差异。
        // 域名之间仍然串行（两个域名各 3 秒超时，再并行也只省 3 秒，不值得复杂化），
        // 但每个域名下的四台服务器并行 —— 以前 2 域名 × 4 台串行，最坏 24 秒。
        foreach (var domain in new[] { "www.baidu.com", "github.com" })
        {
            ct.ThrowIfCancellationRequested();
            var cmp = new DnsComparison { Domain = domain };
            cmp.Entries.AddRange(await Task.WhenAll(
                CompareServers.Select(server => ProbeCompareAsync(server, domain, ct)))
                .ConfigureAwait(false));
            report.Comparisons.Add(cmp);
        }

        return report;
    }

    /// <summary>向一台 DNS 问探针域名，看它会不会对不存在的域名返回答案。</summary>
    private static async Task<HijackCheck> ProbeHijackAsync(
        string server, string probeDomain, CancellationToken ct)
    {
        var item = new HijackCheck { Server = server };
        try
        {
            var (rcode, answers, _) = await QueryAAsync(server, 53, probeDomain, 3000, ct)
                .ConfigureAwait(false);
            item.RCode = rcode;
            item.Answers = answers;
            item.Ok = true;
            item.Hijacked = rcode == 0 && answers.Count > 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            item.Error = "超时";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Error = ex.Message.Length > 40 ? ex.Message[..40] : ex.Message;
        }
        return item;
    }

    /// <summary>向一台 DNS 问真实域名，记录它给出的地址集合。</summary>
    private static async Task<DnsComparisonEntry> ProbeCompareAsync(
        string server, string domain, CancellationToken ct)
    {
        try
        {
            var (_, answers, _) = await QueryAAsync(server, 53, domain, 3000, ct)
                .ConfigureAwait(false);

            return new DnsComparisonEntry
            {
                Server = server,
                Addresses = answers.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                Ok = true,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsComparisonEntry { Server = server, Error = "超时" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DnsComparisonEntry
            {
                Server = server,
                Error = ex.Message.Length > 40 ? ex.Message[..40] : ex.Message
            };
        }
    }

    // ------------------------------------------------------------------
    // 最小 DNS 报文实现
    // ------------------------------------------------------------------

    private static byte[] BuildQuery(string domain, out ushort id)
    {
        id = (ushort)RandomNumberGenerator.GetInt32(1, 65535);
        var ms = new MemoryStream();
        void W16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        W16(id);
        W16(0x0100);   // RD=1，请求递归
        W16(1);        // QDCOUNT
        W16(0); W16(0); W16(0);

        foreach (var part in domain.Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(part);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0);
        W16(1);        // QTYPE = A
        W16(1);        // QCLASS = IN

        return ms.ToArray();
    }

    /// <summary>返回 (rcode, A 记录列表)。</summary>
    private static (int RCode, List<string> Answers) ParseResponse(byte[] d, ushort expectId)
    {
        var answers = new List<string>();
        if (d.Length < 12) return (-1, answers);

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(0));
        if (id != expectId) return (-1, answers);

        int rcode = d[3] & 0x0F;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(4));
        int an = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(6));

        int pos = 12;
        for (int i = 0; i < qd; i++)
        {
            pos = SkipName(d, pos);
            pos += 4;                       // QTYPE + QCLASS
            if (pos > d.Length) return (rcode, answers);
        }

        for (int i = 0; i < an; i++)
        {
            if (pos >= d.Length) break;
            pos = SkipName(d, pos);
            if (pos + 10 > d.Length) break;

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos));
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 8));
            int rdStart = pos + 10;

            if (type == 1 && rdlen == 4 && rdStart + 4 <= d.Length)
                answers.Add(new IPAddress(d.AsSpan(rdStart, 4).ToArray()).ToString());

            pos = rdStart + rdlen;
        }

        return (rcode, answers);
    }

    /// <summary>跳过域名（处理 0xC0 压缩指针）。</summary>
    private static int SkipName(byte[] d, int pos)
    {
        while (pos < d.Length)
        {
            byte len = d[pos];
            if (len == 0) return pos + 1;
            if ((len & 0xC0) == 0xC0) return pos + 2;
            pos += 1 + len;
        }
        return pos;
    }
}

public sealed class HijackCheck
{
    public string Server { get; set; } = "";
    public bool Ok { get; set; }
    public bool Hijacked { get; set; }
    public int RCode { get; set; }
    public List<string> Answers { get; set; } = new();
    public string Error { get; set; } = "";
}

public sealed class HijackReport
{
    public string ProbeDomain { get; set; } = "";
    public List<HijackCheck> Checks { get; set; } = new();
    public List<DnsComparison> Comparisons { get; set; } = new();
    public bool AnyHijacked => Checks.Any(c => c.Hijacked);
}

public sealed class DnsComparisonEntry
{
    public string Server { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
}

public sealed class DnsComparison
{
    public string Domain { get; set; } = "";
    public List<DnsComparisonEntry> Entries { get; set; } = new();

    /// <summary>各 DNS 返回的地址集合是否完全一致。</summary>
    public bool AllAgree
    {
        get
        {
            var sets = Entries.Where(e => e.Ok && e.Addresses.Count > 0)
                              .Select(e => string.Join(",", e.Addresses))
                              .Distinct()
                              .ToList();
            return sets.Count <= 1;
        }
    }
}
