using System.Text.RegularExpressions;

namespace NetCheck.Core;

/// <summary>
/// 导出前的文本脱敏。纯字符串处理，不依赖 <see cref="DiagState"/>，所以能单独测。
///
/// **为什么需要它**：诊断包天生就是拿来发给别人的 —— 发给运维、贴到论坛求助。
/// 但里面装着主机名、MAC、公网出口 IP、局域网邻居表，这些凑在一起足以定位到
/// 具体是哪台机器、哪条宽带。用户点「导出诊断包」的时候未必想到这一层，
/// 等贴到论坛上再后悔就晚了。
///
/// **脱什么、留什么**。判断标准只有一条：这条信息能不能定位到具体的人。
/// 能，就打码；不能而且诊断离不开它，就原样保留。
///
/// | 内容 | 处理 | 理由 |
/// |---|---|---|
/// | MAC 地址 | 保留 OUI（前三段），后三段打码 | OUI 只说明网卡厂商，后三段才是设备唯一标识 |
/// | 主机名 | 整个替换成 <c>HOST-***</c> | 主机名经常带姓名或公司缩写 |
/// | 公网 IPv4 | 保留前两段，后两段打码 | 前两段是运营商和地域，后两段才是这一条线路 |
/// | 私有 IPv4 | **原样保留** | 192.168 / 10. / 172.16-31 是通用网段，指向不了任何人，而诊断离不开它 |
/// | 公共 DNS | **原样保留** | 223.5.5.5 这类地址是公开服务，几千万人在用 |
/// | IPv6 | 保留前两组，其余打码 | 后 64 位是接口标识，等于设备指纹 |
/// | <c>C:\Users\&lt;名&gt;</c> | 用户名替换成 <c>***</c> | 路径里直接就是登录名 |
///
/// **必须幂等**：脱敏结果再脱敏一次不能变样。否则「导出时脱敏 + 事后手动再跑一遍」
/// 会把 IPv6 越打越长。三道保险：匹配到已经含 <c>*</c> 的片段直接跳过；
/// IPv6 要求至少 2 个十六进制字符（否则一串冒号也会被当成地址）；IPv4 每个字节校验 ≤ 255。
/// </summary>
public static class Mask
{
    /// <summary>打码后填进去的占位符。</summary>
    public const string Marker = "***";

    /// <summary>主机名的替换值。固定成常量而不是随机值，是为了同一份包里的主机名前后一致。</summary>
    public const string HostMarker = "HOST-***";

    /// <summary>IPv6 打码后补在保留前缀后面的尾巴，凑满 8 组。</summary>
    private const string Ipv6Tail = ":****:****:****:****:****:****";

    /// <summary>
    /// 公共 DNS / 知名解析服务的地址，脱敏时**保留**。
    /// 它们不是身份信息 —— 抹成 <c>223.*.*.*</c> 之后，「DNS」那一节就没法看了，
    /// 而这一节恰恰是发给别人时最需要对方看懂的部分。
    /// </summary>
    private static readonly HashSet<string> PublicResolvers = new(StringComparer.Ordinal)
    {
        "8.8.8.8", "8.8.4.4",                    // Google
        "1.1.1.1", "1.0.0.1",                    // Cloudflare
        "9.9.9.9", "149.112.112.112",            // Quad9
        "208.67.222.222", "208.67.220.220",      // OpenDNS
        "114.114.114.114", "114.114.115.115",    // 114DNS
        "223.5.5.5", "223.6.6.6",                // AliDNS
        "119.29.29.29", "182.254.116.116",       // DNSPod
        "180.76.76.76",                          // 百度
        "101.226.4.6", "218.30.118.6",           // 360
    };

    /// <summary>
    /// 点分四段。前后都排掉数字和点，免得从 <c>1.2.3.4.5</c> 里抠出一半。
    /// 只要求四段数字、不要求每段合法 —— 合法性留给回调里判断，这样像 <c>999.1.1.1</c>
    /// 这种明显不是 IP 的串会原样留下，而不是被当成地址打码。
    /// </summary>
    private static readonly Regex Ipv4Re = new(
        @"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![\d.])",
        RegexOptions.Compiled);

    /// <summary>连字符分隔的 MAC。Windows 的 ipconfig 和 Get-NetAdapter 都是这个写法。</summary>
    private static readonly Regex MacRe = new(
        @"(?<![0-9A-Fa-f-])[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5}(?![0-9A-Fa-f-])",
        RegexOptions.Compiled);

    /// <summary>
    /// 完整写法的 IPv6：至少 4 组、每组非空。要求每组非空是刻意的 ——
    /// 允许空组的话，打码后那串 <c>:****:****:</c> 里的连续冒号会被再次当成地址匹配，
    /// 结果越打越长。
    /// </summary>
    private static readonly Regex Ipv6FullRe = new(
        @"(?<![0-9A-Fa-f:.])(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}(?![0-9A-Fa-f:])",
        RegexOptions.Compiled);

    /// <summary>
    /// 带 <c>::</c> 压缩写法的 IPv6。先确认这一串里确实有 <c>::</c>，再把整串吃掉。
    /// 时间戳（<c>18:30:53</c>）没有 <c>::</c>，所以不会被误伤。
    /// </summary>
    private static readonly Regex Ipv6ZipRe = new(
        @"(?<![0-9A-Fa-f:.])(?=[0-9A-Fa-f:]*::)[0-9A-Fa-f:]+(?![0-9A-Fa-f:])",
        RegexOptions.Compiled);

    /// <summary>用户目录。<c>C:\Users\PC\...</c> 里的那段就是登录名。</summary>
    private static readonly Regex UserPathRe = new(
        @"([A-Za-z]:\\Users\\)[^\\/:*?""<>|\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 脱敏。
    /// </summary>
    /// <param name="text">原始文本。null / 空串原样返回。</param>
    /// <param name="hostname">
    /// 本机主机名。传进来才替换 —— 它是个任意字符串，只靠正则认不出来。
    /// 按「独立单词」匹配（前后不能是字母数字或下划线），所以像 <c>PC</c> 这种短名字
    /// 不会把正文里的 <c>PC</c> 误伤掉。
    /// </param>
    public static string Apply(string? text, string? hostname = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var s = Ipv4Re.Replace(text, MaskIpv4);
        s = MacRe.Replace(s, MaskMac);
        s = Ipv6FullRe.Replace(s, MaskIpv6);
        s = Ipv6ZipRe.Replace(s, MaskIpv6);

        if (!string.IsNullOrWhiteSpace(hostname))
        {
            var name = hostname!.Trim();
            // 一个字符的名字太容易撞上正文，宁可不换
            if (name.Length >= 2)
                s = Regex.Replace(s,
                    @"(?<![A-Za-z0-9_.-])" + Regex.Escape(name) + @"(?![A-Za-z0-9_-])",
                    HostMarker);
        }

        return UserPathRe.Replace(s, "$1" + Marker);
    }

    private static string MaskIpv4(Match m)
    {
        if (m.Value.Contains('*')) return m.Value;

        int a = int.Parse(m.Groups[1].Value);
        int b = int.Parse(m.Groups[2].Value);
        int c = int.Parse(m.Groups[3].Value);
        int d = int.Parse(m.Groups[4].Value);
        if (a > 255 || b > 255 || c > 255 || d > 255) return m.Value;

        if (PublicResolvers.Contains(m.Value)) return m.Value;
        // 私有 / 保留网段原样保留：它们指向不了具体的人，而诊断包离了它们基本没法看。
        // 判定交给 IpGeo —— 「查不到归属地」和「不该写进诊断包」本来就是同一批地址，
        // 两处各写一份迟早会走偏。顺带把 255.255.255.0 这类掩码也保住了（a >= 224）。
        if (IpGeo.IsPrivateOrReservedIpv4(a, b)) return m.Value;

        return $"{a}.{b}.{Marker}.{Marker}";
    }

    private static string MaskMac(Match m)
    {
        if (m.Value.Contains('*')) return m.Value;
        var g = m.Value.Split('-');
        // 前三段是 OUI，说明「这是哪家的网卡」；后三段才是这块网卡自己
        return string.Join("-", g[0], g[1], g[2], Marker, Marker, Marker);
    }

    private static string MaskIpv6(Match m)
    {
        if (m.Value.Contains('*')) return m.Value;

        var groups = m.Value.Split(':').Where(x => x.Length > 0).ToList();
        // 少于两个十六进制组的多半只是一串冒号或分隔符，别动它
        if (groups.Count == 0 || groups.Sum(x => x.Length) < 2) return m.Value;

        // 前两组留着：能看出是哪个运营商 / 哪个前缀（2408 电信、240e 电信、2409 移动…），
        // 后 64 位是接口标识，等于设备指纹，必须打掉。
        var keep = groups.Count >= 2 ? groups[0] + ":" + groups[1] : groups[0];
        return keep + Ipv6Tail;
    }
}
