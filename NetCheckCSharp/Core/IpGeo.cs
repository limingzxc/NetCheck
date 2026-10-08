using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetCheck.Core;

/// <summary>
/// IP 归属地查询。
///
/// 一个 IP 单独摆在那里说明不了什么，「203.0.113.77」和「198.51.100.20」看起来一样陌生；
/// 加上归属地之后就能一眼看出「一个是浙江电信、一个是境外」——
/// 多出口、代理分流、路由在哪一跳出境，这些结论全靠它才立得住。
///
/// **两个源，按地址族分工**：
///
/// | 源 | 地址族 | 返回 | 为什么是这个位置 |
/// |---|---|---|---|
/// | 腾讯 whois.pconline.com.cn | 仅 IPv4 | <c>addr</c> 形如「江苏省南京市电信」 | 中文现成、含运营商，最贴近国内用户的说法 |
/// | ip-api.com | IPv4 + IPv6 | country / regionName / city | 唯一能查 IPv6 的，做兜底 |
///
/// **pconline 有个必须防的坑**：拿 IPv6 去问，它不会报错，
/// 而是**把查询方自己的出口 IP 的归属地返回回来**（实测查询 <c>2408:8207::…</c>
/// 返回了 <c>203.0.113.77</c> 的结果）。照单全收就会给一个境外地址标上「浙江杭州」，
/// 而且看起来完全正常。所以每次都要核对响应里的 <c>ip</c> 字段与请求的是不是同一个，
/// 对不上就当成失败，转去 ip-api。
///
/// **私有地址不发请求** —— 192.168 / 10. / 172.16-31 这些查了也只有「局域网」，
/// 白等一次往返。路由追踪里第一跳几乎必然是私有地址，省下来的是实打实的时间。
/// </summary>
public static class IpGeo
{
    /// <summary>私有 / 保留地址统一显示成这个，不发请求。</summary>
    public const string PrivateText = "局域网";

    /// <summary>
    /// 查过了但没查到。**必须和「还没查」区分开** ——
    /// 否则每渲染一次就重查一遍，路由十几跳会变成十几倍的请求量。
    /// </summary>
    public const string UnknownText = "归属地未知";

    /// <summary>单个 IP 的查询超时。归属地是锦上添花，不该为了它把整个检测拖住。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// <summary>同时最多查几个。路由一次十几跳，不限并发会直接把免费接口打到限流。</summary>
    private const int MaxConcurrency = 4;

    /// <summary>
    /// .NET Core 默认只带 UTF-8 / UTF-16 等少数几种编码，**GBK（代码页 936）不在其中**，
    /// 要用必须显式注册。这不是可选项：腾讯 whois.pconline.com.cn 声明
    /// <c>Content-Type: text/html; charset=GBK</c>，发的也真是 GBK 字节，
    /// 不注册的话 <c>HttpClient.GetStringAsync</c> 会直接抛异常 ——
    /// 表现出来就是「这个接口明明能通，却每次都静默落到备用源」，
    /// 而且因为备用源也能出结果，这个坑不会以任何可见的方式暴露出来。
    /// </summary>
    static IpGeo()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 极端环境下注册失败也不该让整个模块起不来 */ }
    }

    private static readonly Lazy<HttpClient> Direct =
        new(() => PublicIpProbe.CreateClient(false, Timeout));

    private static readonly Lazy<HttpClient> ViaProxy =
        new(() => PublicIpProbe.CreateClient(true, Timeout));

    // ------------------------------------------------------------------
    // 地址分类
    // ------------------------------------------------------------------

    /// <summary>
    /// 私有 / 保留 / 不可路由的 IPv4。判定标准与 <see cref="Mask"/> 打码时用的是同一套 ——
    /// 「查不到归属地」和「不该写进诊断包」本来就是同一批地址，两处各写一份迟早会走偏。
    /// </summary>
    public static bool IsPrivateOrReservedIpv4(int a, int b) =>
        a is 0 or 10 or 127 || a >= 224
        || (a == 169 && b == 254)                  // 链路本地
        || (a == 172 && b >= 16 && b <= 31)        // 172.16.0.0/12
        || (a == 192 && b == 168)                  // 192.168.0.0/16
        || (a == 100 && b >= 64 && b <= 127);      // 运营商级 NAT 100.64.0.0/10

    /// <summary>这个地址能不能查到有意义的归属地。查不了的一律返回 <c>false</c>。</summary>
    public static bool IsLookupable(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        if (!IPAddress.TryParse(ip.Trim(), out var addr)) return false;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var o = addr.GetAddressBytes();
            return !IsPrivateOrReservedIpv4(o[0], o[1]);
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6Multicast) return false;
            if (IPAddress.IsLoopback(addr)) return false;
            var o = addr.GetAddressBytes();
            // fc00::/7 唯一本地地址（ULA），和 IPv4 的私有段是一回事
            if ((o[0] & 0xFE) == 0xFC) return false;
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    /// <summary>
    /// 查一个 IP 的归属地。**不含缓存** —— 缓存放在 <see cref="DiagState.IpLocations"/> 里，
    /// 这样它能跟着诊断包一起导出去，换个进程再看还是同一份结论。
    /// 私有地址直接返回「局域网」，一个包都不发。
    /// </summary>
    public static async Task<string> LookupAsync(string? ip, CancellationToken ct)
    {
        var target = (ip ?? "").Trim();
        if (target.Length == 0) return "";
        if (!IPAddress.TryParse(target, out var addr)) return "";
        if (!IsLookupable(target)) return PrivateText;

        var isV4 = addr.AddressFamily == AddressFamily.InterNetwork;

        // IPv4 先问 pconline：它给的是「江苏省南京市电信」这种现成中文，比拼英文 ISP 好用得多
        if (isV4)
        {
            var a = await QueryPconlineAsync(target, ct).ConfigureAwait(false);
            if (a.Length > 0) return a;
        }

        var b = await QueryIpApiAsync(target, ct).ConfigureAwait(false);
        if (b.Length > 0) return b;

        return UnknownText;
    }

    private static async Task<string> QueryPconlineAsync(string ip, CancellationToken ct)
    {
        var url = "https://whois.pconline.com.cn/ipJson.jsp?json=true&ip=" + Uri.EscapeDataString(ip);
        var text = await GetAsync(url, ct).ConfigureAwait(false);
        if (text.Length == 0) return "";

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // 关键校验：它会把查询方的出口 IP 当成结果返回。对不上说明这个响应不是给我们的。
            var got = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
            if (!string.Equals(got, ip, StringComparison.OrdinalIgnoreCase)) return "";

            var err = root.TryGetProperty("err", out var errEl) ? errEl.GetString() ?? "" : "";
            if (err.Length > 0) return "";

            var addr = root.TryGetProperty("addr", out var addrEl) ? addrEl.GetString() ?? "" : "";
            // pconline 的 addr 形如「浙江省杭州市 电信」——「地址 + 空格 + 运营商」。
            // 中文里不加空格更顺眼，而且这段文本要贴在 IP 右边，越短越好。
            return Clean(addr).Replace(" ", "");
        }
        catch (JsonException) { return ""; }
    }

    private static async Task<string> QueryIpApiAsync(string ip, CancellationToken ct)
    {
        var url = "http://ip-api.com/json/" + Uri.EscapeDataString(ip) + "?lang=zh-CN&fields=status,country,regionName,city,isp";
        var text = await GetAsync(url, ct).ConfigureAwait(false);
        if (text.Length == 0) return "";

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            if (status != "success") return "";

            var parts = new List<string>();
            foreach (var key in new[] { "country", "regionName", "city" })
            {
                var v = root.TryGetProperty(key, out var el) ? el.GetString() ?? "" : "";
                v = TrimSuffix(v);
                if (v.Length > 0 && !parts.Contains(v)) parts.Add(v);
            }
            return Clean(string.Join(" ", parts));
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>
    /// 取文本。先直连，失败了再试代理 ——
    /// 有些网络只放行代理，直连会被静默丢弃；而归属地接口本身对走哪条路并不敏感
    /// （查的是别人的地址，不是我们的出口）。
    /// </summary>
    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        var direct = await TryGetAsync(Direct.Value, url, ct).ConfigureAwait(false);
        if (direct.Length > 0) return direct;

        ct.ThrowIfCancellationRequested();
        return await TryGetAsync(ViaProxy.Value, url, ct).ConfigureAwait(false);
    }

    private static async Task<string> TryGetAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            // 不用 GetStringAsync：它按响应头里的 charset 解码，而 pconline 声明的 GBK
            // 在 .NET Core 上属于「没注册的代码页」，会抛异常。这里自己拿字节再解码，
            // 顺带把「charset 不认识」和「干脆没声明」两种情况都兜住。
            using var resp = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return "";

            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            return Decode(bytes, resp.Content.Headers.ContentType?.CharSet).Trim().TrimStart('\uFEFF');
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return ""; }
    }

    /// <summary>
    /// 按响应声明的字符集解码；认不出来就按 UTF-8 试。
    /// 公开出来是为了让这条「GBK 陷阱」有断言守着 —— 它是纯函数，不碰网络。
    /// </summary>
    public static string Decode(byte[] bytes, string? charset)
    {
        var name = (charset ?? "").Trim().Trim('"');
        if (name.Length > 0)
        {
            try { return Encoding.GetEncoding(name).GetString(bytes); }
            catch (ArgumentException) { /* 没见过的字符集名，落到 UTF-8 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>去掉「浙江省」「杭州市」这类后缀，让拼出来的字符串短一点。</summary>
    private static string TrimSuffix(string s)
    {
        foreach (var suffix in new[] { "特别行政区", "维吾尔自治区", "壮族自治区", "回族自治区", "自治区", "省", "市" })
            if (s.Length > suffix.Length + 1 && s.EndsWith(suffix, StringComparison.Ordinal))
                return s[..^suffix.Length];
        return s;
    }

    private static string Clean(string s)
    {
        var t = (s ?? "").Trim();
        // pconline 对空结果会回一串空白，别把空白当成一个有效结论
        return t.Length == 0 ? "" : t;
    }

    // ------------------------------------------------------------------
    // 批量补齐
    // ------------------------------------------------------------------

    /// <summary>
    /// 把 <see cref="DiagState"/> 里所有能查的 IP 的归属地补进 <see cref="DiagState.IpLocations"/>。
    ///
    /// **可以重复调用**：已经在表里的不再查（包括 <see cref="UnknownText"/> 这种「查过但没有」的结论），
    /// 所以出口 IP、DNS、路由三项各自跑完之后都能顺手调一次，不会把请求量翻倍。
    /// </summary>
    public static async Task FillAsync(DiagState s, CancellationToken ct)
    {
        var todo = Pending(s);
        if (todo.Count == 0) return;

        using var gate = new SemaphoreSlim(MaxConcurrency);
        var tasks = todo.Select(async ip =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var text = await LookupAsync(ip, ct).ConfigureAwait(false);
                lock (s.IpLocations) s.IpLocations[ip] = text;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 单个查不到不影响别的，渲染层会显示成未知 */ }
            finally { gate.Release(); }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 这一轮还需要查哪些地址。
    /// 单独暴露一个数量给调用方，是为了让「查询归属地…」这句进度只在真有活干的时候出现 ——
    /// 一台只有私有地址的机器上，闪一下又立刻消失的进度条比没有更让人困惑。
    /// </summary>
    public static int PendingCount(DiagState s) => Pending(s).Count;

    private static List<string> Pending(DiagState s) =>
        CollectLookupTargets(s)
            .Where(IsLookupable)
            .Where(ip => !s.IpLocations.ContainsKey(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>状态里所有「显示了 IP、值得标归属地」的位置。</summary>
    private static IEnumerable<string> CollectLookupTargets(DiagState s)
    {
        if (!string.IsNullOrWhiteSpace(s.PublicIpDirect)) yield return s.PublicIpDirect!.Trim();

        foreach (var list in new[] { s.PublicIpHttpProxy, s.PublicIpHttpDirect })
            if (list != null)
                foreach (var r in list)
                    if (r.Ok) yield return r.Ip;

        if (s.DnsServers != null)
            foreach (var d in s.DnsServers) yield return d;

        if (s.Adapters != null)
            foreach (var a in s.Adapters)
                foreach (var d in a.Dns) yield return d;

        // STUN 看到的映射地址是运营商侧的公网 IP，往往和 HTTP 路径查出来的不是同一个 ——
        // 「为什么两个出口不一样」经常靠这一条才能说清（一个在本地机房、一个已经出境）。
        if (s.Nat != null)
            foreach (var ip in s.Nat.MappedIps) yield return ip;

        // IPv6 出口：ip-api 是唯一能查它的源，走的是同一套流程
        if (s.Ipv6 != null && s.Ipv6.EgressIp.Length > 0) yield return s.Ipv6.EgressIp;

        if (s.Traceroute != null)
            foreach (var h in s.Traceroute.Hops)
                if (!h.Timeout && h.Address.Length > 0) yield return h.Address;
    }

    /// <summary>
    /// 取归属地文本，供渲染层贴在 IP 右边。空串表示「这里不该出现归属地」，调用方据此决定要不要占位。
    ///
    /// 三条规则：
    /// <list type="bullet">
    /// <item>私有 / 保留地址直接给「局域网」—— 它们查不到，但也从来不需要查，
    /// 路由第一跳、网关、内网 DNS 全靠这条才有个像样的说明。</item>
    /// <item><see cref="UnknownText"/> 不外显。它是「查过了但没查到」的哨兵值，
    /// 留在表里是为了避免重复查询；真显示出来，一次断网就会让每一跳后面都拖着「归属地未知」，
    /// 反而把地址本身挤没了。</item>
    /// <item>其余情况返回空串（还没查过）。</item>
    /// </list>
    /// </summary>
    public static string Of(DiagState s, string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "";
        var t = ip.Trim();

        if (s.IpLocations != null && s.IpLocations.TryGetValue(t, out var v)
            && v.Length > 0 && v != UnknownText)
            return v;

        if (IPAddress.TryParse(t, out _) && !IsLookupable(t)) return PrivateText;
        return "";
    }
}
