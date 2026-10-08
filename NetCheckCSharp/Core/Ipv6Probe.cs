using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetCheck.Core;

/// <summary>一块网卡上的一条 IPv6 地址。</summary>
public sealed class Ipv6AddressInfo
{
    public string Address { get; set; } = "";
    public string Adapter { get; set; } = "";
    /// <summary>全局 / 内网（ULA） / 链路本地。</summary>
    public string Kind { get; set; } = "";
    /// <summary>疑似隐私扩展生成的临时地址（会随时间变化）。</summary>
    public bool IsTemporary { get; set; }
}

/// <summary>一个 IPv6 可达性目标的结果。</summary>
public sealed class Ipv6PingResult
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>国内 / 国外。</summary>
    public string Region { get; set; } = "";
    public double Ms { get; set; }
    public int Attempts { get; set; }
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0;
}

/// <summary>
/// IPv6 检测结果。
///
/// **这一项刻意不做成「有就绿色、没有就红色」** —— 大量网络本来就只有 IPv4，
/// 把它标成故障等于凭空制造焦虑。分档只关心三件真会出事的情况：
/// 地址分下来了却完全不通（上游没配好）、只能到国内（缺国际路由）、以及正常。
/// </summary>
public sealed class Ipv6Report
{
    public List<Ipv6AddressInfo> Addresses { get; set; } = new();
    public string EgressIp { get; set; } = "";
    public string EgressError { get; set; } = "";
    public List<Ipv6PingResult> Pings { get; set; } = new();
    public string Error { get; set; } = "";

    /// <summary>有没有可用的全局 IPv6 地址（ULA 不算 —— 它出不了网）。</summary>
    public bool Enabled => Addresses.Any(a => a.Kind == "全局");

    public bool DomesticOk => Pings.Any(p => p.Region == "国内" && p.Ok);
    public bool ForeignOk => Pings.Any(p => p.Region == "国外" && p.Ok);
    public bool AnyOk => Pings.Any(p => p.Ok);

    public string Verdict
    {
        get
        {
            if (!Enabled) return "未启用 IPv6";
            if (Pings.Count == 0) return "探测未完成";
            if (!AnyOk) return "有地址但不通";
            if (DomesticOk && !ForeignOk) return "只能到国内";
            if (!DomesticOk && ForeignOk) return "仅国际可达";
            return "双栈正常";
        }
    }

    /// <summary>给结论条用的一句话解释。没有 IPv6 时也给一句，免得用户以为是漏检了。</summary>
    public string Detail
    {
        get
        {
            if (!Enabled)
                return Addresses.Count == 0
                    ? "本机没有全局 IPv6 地址。多数网络只有 IPv4，这不是故障。"
                    : "只有内网 IPv6 地址（ULA），无法访问公网 IPv6 资源。";

            return Verdict switch
            {
                "有地址但不通" => "地址已经分配到本机，但两个公网目标都无响应 —— "
                                + "通常是上游（光猫 / 路由器 / 运营商）没有配好 IPv6 转发，"
                                + "属于最典型的「假双栈」。",
                "只能到国内" => "国内 IPv6 可达、国外不可达，说明有 IPv6 出口但缺少国际路由。",
                "仅国际可达" => "国外可达而国内目标无响应，国内 IPv6 互联质量可能存在问题。",
                "双栈正常" => "国内与国外目标均可达，IPv6 可以正常使用。",
                _ => "",
            };
        }
    }
}

/// <summary>
/// IPv6 检测。
///
/// **没有全局地址时立即返回，一个包都不发** —— 出口查询和可达性探测都需要先有地址，
/// 硬跑只会让卡片等满超时再给出一个「未启用」的结论，白白浪费几秒。
///
/// **出口查询必须强制走 IPv6。** HttpClient 默认会做 Happy Eyeballs：
/// 哪个地址族先连上就用哪个。目标站同时有 A 和 AAAA 记录时，
/// 结果很可能是走 IPv4 查出来的地址 —— 看起来像在测 IPv6，其实还是 v4 出口。
/// 所以这里用 <see cref="SocketsHttpHandler.ConnectCallback"/> 把候选地址筛成只剩 IPv6。
/// </summary>
public static class Ipv6Probe
{
    /// <summary>国内的 IPv6 目标。AliDNS 的公共解析地址，长期稳定、不屏蔽 ICMP。</summary>
    public const string DomesticTarget = "2400:3200::1";

    /// <summary>国外的 IPv6 目标。Cloudflare 的 1.1.1.1 对应地址。</summary>
    public const string ForeignTarget = "2606:4700:4700::1111";

    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(1500);
    private const int PingAttempts = 2;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    public static async Task<Ipv6Report> DetectAsync(
        IReadOnlyList<AdapterInfo> adapters, IProgress<string>? progress, CancellationToken ct)
    {
        var r = new Ipv6Report { Addresses = CollectAddresses(adapters) };

        if (!r.Enabled)
        {
            progress?.Report("本机没有全局 IPv6 地址，跳过后续探测。");
            return r;
        }

        // 出口查询和可达性探测互不依赖，并行跑 —— 串起来最坏要 8 秒，并行只要 5 秒。
        progress?.Report("查询 IPv6 出口地址并测试可达性…");

        var egress = GetEgressIpAsync(ct);
        var pings = new[]
        {
            PingAsync("AliDNS", DomesticTarget, "国内", ct),
            PingAsync("Cloudflare", ForeignTarget, "国外", ct),
        };

        try
        {
            r.EgressIp = await egress.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { r.EgressError = ex.Message; }

        r.Pings.AddRange(await Task.WhenAll(pings).ConfigureAwait(false));
        return r;
    }

    // ------------------------------------------------------------------
    // 地址收集
    // ------------------------------------------------------------------

    /// <summary>
    /// 把网卡上的 IPv6 地址分类。
    ///
    /// **临时地址不排除，反而要标出来。** 隐私扩展（RFC 4941）生成的临时地址正是实际出网用的那个，
    /// 它每天变一次 —— 「昨天好好的，今天白名单失效了」这类问题全靠看见它才解释得通。
    /// 判定用接口标识符的 u 位（第 9 字节的 0x02）：置位是固定地址，清零是随机生成。
    /// </summary>
    public static List<Ipv6AddressInfo> CollectAddresses(IReadOnlyList<AdapterInfo> adapters)
    {
        var list = new List<Ipv6AddressInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in adapters)
            foreach (var raw in a.IPv6Addresses)
            {
                if (!IPAddress.TryParse(raw, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                    continue;

                // 链路本地（fe80::/10）一律不要。LocalNetInfo 那边已经滤过一遍，
                // 但这是个公开入口，调用方塞进来什么都有可能 —— 而 fe80:: 地址
                // 对「能不能上 IPv6」这个问题毫无信息量，只会把列表撑长。
                var kind = Classify(ip);
                if (kind == "链路本地") continue;

                if (!seen.Add(ip.ToString())) continue;

                list.Add(new Ipv6AddressInfo
                {
                    Address = ip.ToString(),
                    Adapter = a.Name,
                    Kind = kind,
                    IsTemporary = IsTemporary(ip),
                });
            }

        // 全局的排前面，其次内网，最后链路本地
        return list.OrderBy(x => x.Kind switch { "全局" => 0, "内网" => 1, _ => 2 })
                   .ThenBy(x => x.Address, StringComparer.Ordinal)
                   .ToList();
    }

    private static string Classify(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal) return "链路本地";
        if (ip.IsIPv6SiteLocal) return "内网";

        var o = ip.GetAddressBytes();
        if ((o[0] & 0xFE) == 0xFC) return "内网";   // fc00::/7 唯一本地地址
        return "全局";
    }

    /// <summary>u 位清零 = 本地管理 = 随机生成，也就是隐私扩展的临时地址。</summary>
    private static bool IsTemporary(IPAddress ip)
    {
        if (Classify(ip) != "全局") return false;
        var o = ip.GetAddressBytes();
        return (o[8] & 0x02) == 0;
    }

    // ------------------------------------------------------------------
    // 出口 IP
    // ------------------------------------------------------------------

    private static async Task<string> GetEgressIpAsync(CancellationToken ct)
    {
        using var client = CreateIpv6Client();

        foreach (var (name, url, key) in EgressServices)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(HttpTimeout);
                var text = await client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
                var ip = ExtractIp(text, key);
                if (ip.Length > 0) return ip;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 换下一个源 */ }
        }

        return "";
    }

    private static readonly (string Name, string Url, string Key)[] EgressServices =
    {
        ("api6.ipify.org", "https://api6.ipify.org?format=json", "ip"),
        ("ip-api.com", "http://ip-api.com/json/?fields=status,query", "query"),
    };

    /// <summary>从 JSON 里抠出地址。**只认 IPv6** —— 源站偶尔会回一个 v4，那说明强制没生效，宁可不显示。</summary>
    private static string ExtractIp(string json, string key)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var v = doc.RootElement.TryGetProperty(key, out var el) ? el.GetString() ?? "" : "";
            return IPAddress.TryParse(v, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                ? ip.ToString()
                : "";
        }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    /// <summary>
    /// 一个只会连 IPv6 的 HttpClient。
    /// 走不了系统代理 —— 经代理出去查的是代理的地址，测不出本机 IPv6 通不通。
    /// </summary>
    private static HttpClient CreateIpv6Client()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = HttpTimeout,
            ConnectCallback = async (ctx, token) =>
            {
                var addrs = await Dns.GetHostAddressesAsync(
                    ctx.DnsEndPoint.Host, AddressFamily.InterNetworkV6, token).ConfigureAwait(false);

                if (addrs.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);

                var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addrs[0], ctx.DnsEndPoint.Port), token)
                                .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        var client = new HttpClient(handler) { Timeout = HttpTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NetCheck/2.0");
        return client;
    }

    // ------------------------------------------------------------------
    // 可达性
    // ------------------------------------------------------------------

    private static async Task<Ipv6PingResult> PingAsync(
        string name, string host, string region, CancellationToken ct)
    {
        var r = new Ipv6PingResult { Name = name, Host = host, Region = region };

        try
        {
            using var ping = new Ping();
            double best = 0;

            for (int i = 0; i < PingAttempts; i++)
            {
                ct.ThrowIfCancellationRequested();
                r.Attempts++;

                PingReply reply;
                try
                {
                    reply = await ping.SendPingAsync(host, (int)PingTimeout.TotalMilliseconds)
                                    .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    r.Error = ex.Message;
                    continue;
                }

                if (reply.Status == IPStatus.Success)
                {
                    // Windows 的 ICMP RTT 以整毫秒返回，直连目标可能是 0，那实际是亚毫秒
                    var ms = reply.RoundtripTime;
                    if (best == 0 || ms < best) best = ms;
                    r.Error = "";
                    r.Ms = best;
                    return r;                      // 一次成功就够，不必把两次都耗完
                }

                r.Error = Describe(reply.Status);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { r.Error = ex.Message; }

        if (r.Error.Length == 0) r.Error = "无响应";
        return r;
    }

    private static string Describe(IPStatus s) => s switch
    {
        IPStatus.TimedOut => "超时",
        IPStatus.DestinationHostUnreachable => "目标主机不可达",
        IPStatus.DestinationNetworkUnreachable => "目标网络不可达",
        IPStatus.DestinationUnreachable => "目标不可达",
        IPStatus.PacketTooBig => "报文过大",
        IPStatus.BadDestination => "地址无效",
        _ => s.ToString(),
    };
}
