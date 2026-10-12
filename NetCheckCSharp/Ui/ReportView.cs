using NetCheck.Core;

namespace NetCheck.Ui;

/// <summary>结论条上的一枚胶囊标签。</summary>
public sealed class Chip
{
    public string Text { get; set; } = "";
    public Tone Tone { get; set; } = Tone.Neutral;
}

/// <summary>标签页里的一张区块卡片。渲染器按 Kv → Bars → Adapters → Hops → Ports → Notes 的顺序画。</summary>
public sealed class Section
{
    public string Title { get; set; } = "";
    public string Hint { get; set; } = "";
    public string Verdict { get; set; } = "";
    public Tone VerdictTone { get; set; } = Tone.Neutral;

    public List<KeyValueRow> Kv { get; } = new();
    public List<BarRow> Bars { get; } = new();
    public List<AdapterRow> Adapters { get; } = new();
    public List<HopRow> Hops { get; } = new();
    public List<PortCell> Ports { get; } = new();
    public List<IssueRow> Issues { get; } = new();
    public List<string> Notes { get; } = new();

    public bool IsEmpty =>
        Kv.Count == 0 && Bars.Count == 0 && Adapters.Count == 0 &&
        Hops.Count == 0 && Ports.Count == 0 && Issues.Count == 0 &&
        Notes.Count == 0 && Verdict.Length == 0;
}

public sealed class TabView
{
    public string Title { get; set; } = "";
    public List<Section> Sections { get; } = new();
    public bool HasData { get; set; }
}

public sealed class ReportView
{
    public List<MetricCard> Cards { get; } = new();
    public List<Chip> Chips { get; } = new();
    public List<IssueRow> Issues { get; } = new();
    public List<TabView> Tabs { get; } = new();
    public bool AnyData { get; set; }
}

/// <summary>把 DiagState 映射成界面模型。纯函数，不碰任何 UI 类型。</summary>
public static class ReportViewBuilder
{
    // 索引就是 <see cref="ReportView.Tabs"/> 里的位置，改这里就等于改界面上卡片的顺序。
    // 顺序按「常用在前、高级在后」，高级区内部再按 网络路径 → 连接能力 → 协议 排。
    // 插卡片会让其后所有数字后移一位，所以 --tab 另外支持按卡片键名指定（推荐写法）。
    public const int OverviewIndex = 0;
    public const int LocalIndex = 1;
    public const int EgressIndex = 2;
    public const int LinkIndex = 3;
    public const int DnsIndex = 4;
    public const int PathIndex = 5;
    public const int NatIndex = 6;
    public const int PortIndex = 7;
    public const int Ipv6Index = 8;
    public const int WebIndex = 9;

    /// <summary>标签页总数。--tab 的范围校验与卡片目录的断言都从这里取，不各写一个数字。</summary>
    public const int TabCount = 10;

    // ------------------------------------------------------------------
    // 色调规则
    // ------------------------------------------------------------------

    /// <summary>
    /// 延迟色调。阈值按用途分类取：网页 100 ms 无感，游戏 100 ms 已经能感觉到操作滞后，
    /// 用同一把尺子会把「能打游戏」和「能刷网页」判成同一档。
    /// </summary>
    private static Tone LatencyTone(double ms, string? category = null)
    {
        var c = LatencyProbe.CategoryOf(category);
        return ms <= c.GoodMs ? Tone.Good : ms <= c.WarnMs ? Tone.Warn : Tone.Bad;
    }

    // 网关 / 带宽 / DNS / 丢包 / MTU 的阈值统一在 Ui/Thresholds.cs 里，
    // 颜色渲染与下面的问题清单共用同一份函数 —— 见那个文件的注释。
    private static Tone GatewayTone(double ms) => Thresholds.Gateway(ms);

    private static Tone BandwidthTone(double mbps) => Thresholds.Bandwidth(mbps);

    private static string MsText(double v) => v < 10 ? $"{v:F1} ms" : $"{v:F0} ms";

    /// <summary>
    /// 「地址（归属地）」。用在**一句话里塞多个 IP** 的场合（问题清单、多出口备注）——
    /// 那里是纯文本，塞不进 <see cref="KeyValueRow.Geo"/> 那样的独立小字，只能就地拼进去。
    /// </summary>
    private static string WithGeo(DiagState s, string ip)
    {
        var g = IpGeo.Of(s, ip);
        return g.Length > 0 ? $"{ip}（{g}）" : ip;
    }

    private static string ShortNat(string t)
    {
        if (t.Contains("无 NAT") || t.Contains("公网")) return "公网直连";
        if (t.Contains("多出口")) return "多出口";
        if (t.Contains("对称")) return "对称型";
        if (t.Contains("圆锥") || t.Contains("锥形")) return "圆锥型";
        if (t.Contains("未知")) return "未知";
        if (t.Contains("无法判定")) return "无法判定";
        return t.Length > 6 ? t[..6] : t;
    }

    private static Tone NatTone(string t)
    {
        if (t.Contains("无 NAT") || t.Contains("公网")) return Tone.Good;
        if (t.Contains("圆锥") || t.Contains("锥形")) return Tone.Good;
        if (t.Contains("多出口") || t.Contains("对称")) return Tone.Warn;
        return Tone.Neutral;
    }

    private static string NatHint(string t)
    {
        if (t.Contains("无 NAT") || t.Contains("公网")) return "可直接互联";
        if (t.Contains("圆锥") || t.Contains("锥形")) return "P2P 打洞可行";
        if (t.Contains("多出口") || t.Contains("对称")) return "P2P 打洞困难";
        return "结果不可用";
    }

    /// <summary>
    /// 代理环境变量的去重显示。要做两件事：
    /// 一是按小写键去重（Windows 上 http_proxy 与 HTTP_PROXY 会同时存在，值相同）；
    /// 二是按值合并（同一个代理地址常常同时挂在 http_proxy 和 https_proxy 上，
    /// 分两行显示纯属重复）。合并后形如 http_proxy/https_proxy=http://127.0.0.1:8080。
    /// </summary>
    private static List<KeyValuePair<string, string>> ProxyEntries(Dictionary<string, string> proxy)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keysByValue = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var valueOrder = new List<string>();

        foreach (var kv in proxy.OrderBy(x => x.Key.ToLowerInvariant(), StringComparer.Ordinal))
        {
            var key = kv.Key.ToLowerInvariant();
            if (!seen.Add(key + "=" + kv.Value)) continue;

            if (!keysByValue.TryGetValue(kv.Value, out var keys))
            {
                keys = new List<string>();
                keysByValue[kv.Value] = keys;
                valueOrder.Add(kv.Value);
            }
            keys.Add(key);
        }

        return valueOrder
            .Select(v => new KeyValuePair<string, string>(string.Join("/", keysByValue[v]), v))
            .ToList();
    }

    // ------------------------------------------------------------------
    // 主入口
    // ------------------------------------------------------------------

    public static ReportView Build(DiagState s)
    {
        var v = new ReportView();

        var ips = s.AllPublicIps();
        var bw = BestBandwidth(s);
        var bestLat = RepresentativeLatency(s);
        var worstLoss = WorstLossTarget(s);

        v.Cards.Add(new MetricCard
        {
            Title = "公网出口",
            Value = ips.Count == 0 ? "—" : ips.Count == 1 ? ips[0] : $"{ips.Count} 个",
            Sub = ips.Count == 0 ? "尚未探测" : ips.Count == 1 ? "单一出口" : "多出口网络",
            Tone = ips.Count == 0 ? Tone.Neutral : ips.Count == 1 ? Tone.Good : Tone.Warn,
            TargetTab = EgressIndex,
            HasData = ips.Count > 0,
        });

        v.Cards.Add(new MetricCard
        {
            Title = "网络延迟",
            Value = bestLat == null ? "—" : $"{bestLat.Avg:F1} ms",
            Sub = bestLat == null ? "尚未探测" : bestLat.Name,
            Tone = bestLat == null ? Tone.Neutral : LatencyTone(bestLat.Avg, bestLat.Category),
            TargetTab = LinkIndex,
            HasData = bestLat != null,
        });

        // 延迟、丢包、抖动三个放在一起 —— 「平均延迟正常」不等于「网络正常」：
        // 32 ms / 8% 丢包的网络刷网页还行，打游戏、远程桌面、语音通话都会明显出问题。
        v.Cards.Add(new MetricCard
        {
            Title = "丢包率",
            Value = worstLoss == null ? "—" : $"{worstLoss.Loss:F1}%",
            Sub = worstLoss == null ? "尚未探测"
                                  : worstLoss.Loss > 0 ? "最差：" + worstLoss.Name
                                  : "全部目标无丢包",
            Tone = worstLoss == null ? Tone.Neutral : Thresholds.Loss(worstLoss.Loss),
            TargetTab = LinkIndex,
            HasData = worstLoss != null,
        });

        v.Cards.Add(new MetricCard
        {
            Title = "抖动",
            Value = bestLat == null ? "—" : $"{LatencyReport.Jitter(bestLat.Samples):F1} ms",
            Sub = bestLat == null ? "尚未探测" : bestLat.Name,
            Tone = bestLat == null ? Tone.Neutral : Thresholds.Jitter(LatencyReport.Jitter(bestLat.Samples)),
            TargetTab = LinkIndex,
            HasData = bestLat != null,
        });

        v.Cards.Add(new MetricCard
        {
            Title = "下载带宽",
            Value = bw == null ? "—" : $"{bw.Mbps:F0} Mbps",
            Sub = bw == null ? "尚未测速"
                             : bw.Source + (ReferenceEquals(bw, s.BandwidthDirect) ? "" : " · 经代理"),
            Tone = bw == null ? Tone.Neutral : BandwidthTone(bw.Mbps),
            TargetTab = LinkIndex,
            HasData = bw != null,
        });

        v.Cards.Add(new MetricCard
        {
            Title = "NAT 类型",
            Value = s.Nat == null ? "—" : ShortNat(s.Nat.Type),
            Sub = s.Nat == null ? "尚未检测" : NatHint(s.Nat.Type),
            Tone = s.Nat == null ? Tone.Neutral : NatTone(s.Nat.Type),
            TargetTab = NatIndex,
            HasData = s.Nat != null,
        });

        BuildChips(s, v);
        BuildIssues(s, v, ips, bw, bestLat);

        // 顺序必须和 ReportViewBuilder 的索引常量一一对应 —— CardCatalog 有断言盯着这条映射。
        v.Tabs.Add(TabOverview(s, v, ips, bw, bestLat));
        v.Tabs.Add(TabLocal(s));
        v.Tabs.Add(TabEgress(s, ips));
        v.Tabs.Add(TabLink(s, bw));
        v.Tabs.Add(TabDns(s));
        v.Tabs.Add(TabPath(s));
        v.Tabs.Add(TabNat(s));
        v.Tabs.Add(TabPort(s));
        v.Tabs.Add(TabIpv6(s));
        v.Tabs.Add(TabWeb(s));

        v.AnyData = v.Tabs.Any(t => t.HasData);
        return v;
    }

    /// <summary>
    /// 卡片上代表「网络延迟」的那个目标。
    /// 优先取「网页浏览」这一类：它最接近日常上网体感，和 CDN 边缘节点通信也是所有用途里最通用的一条路径。
    /// 低于 2 ms 的应答多半来自本地缓存或运营商拦截（根本没出网），拿它当代表值
    /// 会得出「延迟 0.5 ms」这种没有意义的结论，所以优先在 ≥2 ms 的目标里取最小值。
    /// </summary>
    private static LatencyTarget? RepresentativeLatency(DiagState s)
    {
        var ok = s.Latency?.Targets.Where(t => t.Ok).OrderBy(t => t.Avg).ToList();
        if (ok == null || ok.Count == 0) return null;

        var web = ok.Where(t => t.Category == LatencyProbe.Categories[0].Name).ToList();
        var pool = web.Count > 0 ? web : ok;

        var real = pool.Where(t => t.Avg >= 2).ToList();
        return real.Count > 0 ? real[0] : pool[0];
    }

    /// <summary>
    /// 概览「丢包率」指标卡取哪个目标 —— **取丢得最狠的那个**，而不是代表延迟的那个。
    ///
    /// 延迟、抖动取代表值是合理的（它们描述「这条路径有多快、稳不稳」），
    /// 但丢包是**故障指示器**：只要任何一条路径在丢包，网络就已经有问题了。
    /// 如果按代表目标取，会出现「百度 0% 丢包 → 概览显示 0.0%」，
    /// 而游戏路径实际在丢 8% —— 概览恰恰在最该报警的时候报了平安。
    /// 所以这里取最大值，并用 Sub 行标明是谁在丢，用户一眼就知道问题出在哪条路径。
    /// </summary>
    private static LatencyTarget? WorstLossTarget(DiagState s)
    {
        var ok = s.Latency?.Targets.Where(t => t.Ok).ToList();
        if (ok == null || ok.Count == 0) return null;
        return ok.OrderByDescending(t => t.Loss).ThenBy(t => t.Avg).First();
    }

    /// <summary>
    /// 把一组延迟目标按**同一量程**画成条形。分组画的意义就在这：
    /// 同一类内部条长可比，跨类比较没有意义（游戏的 50 ms 和网页的 50 ms 不是一个概念）。
    /// </summary>
    private static void AddLatencyBars(Section sec, List<LatencyTarget> group)
    {
        var okAvgs = group.Where(x => x.Ok).Select(x => x.Avg).ToList();
        var maxAvg = okAvgs.DefaultIfEmpty(100).Max();
        var minAvg = okAvgs.DefaultIfEmpty(0).Min();

        if (BarRow.UsesSqrtScale(maxAvg, minAvg))
            sec.Hint += " · 量程跨度大，条形按平方根压缩（只表示量级）";

        foreach (var x in group)
        {
            if (!x.Ok)
            {
                sec.Bars.Add(new BarRow
                {
                    Name = x.Name,
                    Detail = x.Host + " · " + x.Method,
                    ValueText = "不可达",
                    BarWidth = BarRow.TrackWidth,
                    Tone = Tone.Bad,
                });
                continue;
            }

            sec.Bars.Add(new BarRow
            {
                Name = x.Name,
                Detail = $"{x.Method} · 最小 {x.Min:F1} / 最大 {x.Max:F1} ms"
                       + $" · 抖动 {LatencyReport.Jitter(x.Samples):F1} ms"
                       + (x.Loss > 0 ? $" · 丢包 {x.Loss:F0}%" : ""),
                ValueText = $"{x.Avg:F1} ms",
                BarWidth = BarRow.Scale(x.Avg, maxAvg, minAvg),
                Tone = LatencyTone(x.Avg, x.Category),
            });
        }
    }

    private static BandwidthReport? BestBandwidth(DiagState s)
    {
        if (s.BandwidthDirect?.Ok == true) return s.BandwidthDirect;
        if (s.BandwidthProxy?.Ok == true) return s.BandwidthProxy;
        return null;
    }

    // ------------------------------------------------------------------
    // 结论条
    // ------------------------------------------------------------------

    /// <summary>
    /// 结论条只放「指标卡片上没有的」信息。
    /// 卡片已经显示了出口数、延迟、带宽、NAT 类型，这里再重复一遍纯属噪音，
    /// 而且会把结论条挤成两排（窄窗口下尤其明显）。
    /// </summary>
    private static void BuildChips(DiagState s, ReportView v)
    {
        if (s.Hijack != null)
            v.Chips.Add(new Chip
            {
                Text = s.Hijack.AnyHijacked ? "发现 DNS 劫持" : "未发现 DNS 劫持",
                Tone = s.Hijack.AnyHijacked ? Tone.Bad : Tone.Good,
            });

        if (s.Latency?.Gateway?.Ok == true)
            v.Chips.Add(new Chip
            {
                Text = $"网关 {s.Latency.Gateway.Avg:F1} ms",
                Tone = GatewayTone(s.Latency.Gateway.Avg),
            });

        // 丢包/抖动现在有自己的指标卡片了，结论条不再重复 ——
        // 「只放卡片上没有的」这条规则对新增指标同样适用。
        // HTTP/HTTPS 没有指标卡（六张已经被出口/延迟/丢包/抖动/带宽/NAT 占满），
        // 而它回答的「网页到底能不能打开」是最常用的判断，所以在这里补一枚。
        if (s.Web is { Targets.Count: > 0 } web)
            v.Chips.Add(new Chip
            {
                Text = "网页 " + web.Verdict,
                Tone = WebTone(web),
            });

        if (s.Mtu?.Ok == true)
            v.Chips.Add(new Chip
            {
                Text = $"路径 MTU {s.Mtu.PathMtu}",
                Tone = Thresholds.Mtu(s.Mtu.PathMtu),
            });

        // 端口开放是「事实」不是「问题」：扫的端口是你自己选的，开着不等于有毛病。
        // 以前这里给橙色胶囊，但问题清单里没有任何对应条目，属于凭空制造焦虑。
        if (s.PortScan != null && s.PortScan.OpenCount > 0)
            v.Chips.Add(new Chip
            {
                Text = $"{s.PortScan.Host} 开放 {s.PortScan.OpenCount} 个端口",
                Tone = Tone.Neutral,
            });

        if (s.Proxy != null && ProxyEntries(s.Proxy)
                .Any(x => x.Key.Contains("proxy") && !x.Key.Contains("no_proxy") && x.Value.Length > 0))
            v.Chips.Add(new Chip { Text = "已配置 HTTP 代理", Tone = Tone.Warn });
    }

    // ------------------------------------------------------------------
    // 问题清单
    // ------------------------------------------------------------------

    private static void BuildIssues(DiagState s, ReportView v, List<string> ips,
                                    BandwidthReport? bw, LatencyTarget? bestLat)
    {
        var list = v.Issues;

        // ---- 本机 ----
        var adapters = s.Adapters;
        if (adapters != null)
        {
            var up = adapters.Where(a => a.IsUp).ToList();
            if (up.Count == 0)
                list.Add(new IssueRow
                {
                    Tone = Tone.Bad,
                    Title = "没有已连接的网络适配器",
                    Detail = "检测到 " + adapters.Count + " 块网卡，但没有一块处于已连接状态，网络功能不可用。",
                    TargetTab = LocalIndex,
                });
            else if (up.All(a => a.IPv4.Length == 0))
                list.Add(new IssueRow
                {
                    Tone = Tone.Bad,
                    Title = "网卡没有 IPv4 地址",
                    Detail = "已连接的网卡都没有分配到 IPv4 地址，可能是 DHCP 获取失败或需要手动配置。",
                    TargetTab = LocalIndex,
                });
        }

        if (s.Proxy != null)
        {
            var enabled = ProxyEntries(s.Proxy)
                .Where(x => x.Key.Contains("proxy") && !x.Key.Contains("no_proxy") && x.Value.Length > 0)
                .ToList();
            if (enabled.Count > 0)
                list.Add(new IssueRow
                {
                    Tone = Tone.Warn,
                    Title = "系统已配置 HTTP 代理",
                    Detail = "环境变量中设置了 " + string.Join("、", enabled.Select(x => x.Key))
                           + "。代理会改变公网出口与测速结果，本工具的测速已默认绕过代理。",
                    TargetTab = LocalIndex,
                });
        }

        // ---- IPv6 ----
        // 「没有 IPv6」刻意不在这里出现：大量网络本来就只有 IPv4，报出来只是噪音。
        // 只有「有地址却用不了」才是真问题 —— 那说明配置做了一半。
        if (s.Ipv6 is { Enabled: true } v6)
        {
            if (!v6.AnyOk)
                list.Add(new IssueRow
                {
                    Tone = Tone.Bad,
                    Title = "分配到了 IPv6 地址但完全不通",
                    Detail = "本机有全局 IPv6 地址，但国内与国外目标都没有响应。"
                           + "地址分下来了、上游却没配好转发，是目前最常见的「假双栈」—— "
                           + "系统会优先尝试 IPv6、等超时后再回退 IPv4，表现为网页首屏明显变慢。",
                    TargetTab = Ipv6Index,
                });
            else if (v6.DomesticOk && !v6.ForeignOk)
                list.Add(new IssueRow
                {
                    Tone = Tone.Warn,
                    Title = "IPv6 只能访问国内",
                    Detail = "国内目标可达、国外不可达，说明有 IPv6 出口但缺少国际路由。"
                           + "访问境外 IPv6 站点会长时间等待后回退到 IPv4。",
                    TargetTab = Ipv6Index,
                });
        }

        // ---- 公网出口 ----
        // 判据是「一条能用的路径都没有」而不是「某一条路径失败」。
        // STUN 被墙但 HTTP 直连成功，在限制 UDP 的网络里很常见 —— 那种情况下
        // 出口 IP 明明拿到了，再报「未能获取」就是自己吓自己；
        // 反过来 STUN 成功、三台 HTTP 服务全失败也一样（「所有探测路径都失败了」
        // 是句假话）。AllPublicIps() 已经会把哨兵值（不可用 / 解析失败）滤掉，
        // 所以它为空就是「真的一个地址都没拿到」。
        bool egressRan = s.PublicIpDirect != null
                         || s.PublicIpHttpDirect != null
                         || s.PublicIpHttpProxy != null;
        if (egressRan && ips.Count == 0)
            list.Add(new IssueRow
            {
                Tone = Tone.Warn,
                Title = "未能获取公网出口 IP",
                Detail = "所有探测路径都失败了，可能是网络受限、防火墙拦截，或需要代理才能访问外网。",
                TargetTab = EgressIndex,
            });

        if (ips.Count > 1)
            list.Add(new IssueRow
            {
                Tone = Tone.Warn,
                Title = $"检测到 {ips.Count} 个不同的公网出口 IP",
                Detail = "不同目标看到不同的出口地址（" + string.Join("、", ips.Select(x => WithGeo(s, x)))
                       + "）。常见于多线接入、校园网或负载均衡，会导致 P2P 打洞、IP 白名单、会话保持类应用异常。",
                TargetTab = EgressIndex,
            });

        // ---- HTTP / HTTPS 分级连通性 ----
        // 每一条都指明**卡在哪一层** —— 「网站打不开」这句话对排查毫无帮助，
        // 「能解析但连不上」和「连上了 TLS 失败」是两个完全不同的问题。
        if (s.Web is { Targets.Count: > 0 } web)
        {
            foreach (var g in web.Targets.Where(x => !x.Ok))
                list.Add(new IssueRow
                {
                    Tone = g.Region == "境外" && g.FailedStep is "DNS" or "TCP" ? Tone.Warn : Tone.Bad,
                    Title = $"{g.Name} 卡在 {g.FailedStep} 层（{g.Blame}）",
                    Detail = WebLayerHint(g),
                    TargetTab = WebIndex,
                });
        }

        // ---- 延迟 ----
        if (s.Latency != null)
        {
            // 网关：只要条形不是绿色就一定要有一条问题说明。
            // 以前这里写死 >15 ms，于是 5 ms 的网关在图上画成橙色、问题清单里却什么都没有。
            if (s.Latency.Gateway?.Ok == true)
            {
                var gwTone = GatewayTone(s.Latency.Gateway.Avg);
                if (Thresholds.IsProblem(gwTone))
                    list.Add(new IssueRow
                    {
                        Tone = gwTone,
                        Title = $"网关延迟偏高（{s.Latency.Gateway.Avg:F1} ms）",
                        Detail = $"局域网内到网关的延迟通常应低于 {Thresholds.GatewayGoodMs:F0} ms。"
                               + "偏高说明本地链路拥塞、无线信号差或网关负载高。",
                        TargetTab = LinkIndex,
                    });
            }

            // 按用途逐类判定。用户真正想知道的是「能不能流畅刷网页 / 看视频 / 打游戏」，
            // 而不是 9 个目标各自的数字，所以每类最多出一条结论，避免刷屏。
            foreach (var cat in LatencyProbe.Categories)
            {
                var group = s.Latency.Targets
                    .Where(x => x.Ok && x.Category == cat.Name).ToList();
                if (group.Count == 0) continue;

                var best = group.OrderBy(x => x.Avg).First();
                var worst = group.OrderByDescending(x => x.Avg).First();

                if (best.Avg > cat.WarnMs)
                    list.Add(new IssueRow
                    {
                        Tone = Tone.Bad,
                        Title = $"{cat.Name}整体延迟偏高（最快 {best.Name} {best.Avg:F1} ms）",
                        Detail = $"{cat.Name}的可用上限约 {cat.WarnMs:F0} ms，连最快的一个都超过了，"
                               + "这类用途会明显受影响。",
                        TargetTab = LinkIndex,
                    });
                else if (worst.Avg > cat.WarnMs)
                    list.Add(new IssueRow
                    {
                        Tone = Tone.Warn,
                        Title = $"{cat.Name}有目标延迟偏高（{worst.Name} {worst.Avg:F1} ms）",
                        Detail = $"{worst.Name}（{worst.Host}）超过 {cat.WarnMs:F0} ms，"
                               + $"而同类里最快的 {best.Name} 只有 {best.Avg:F1} ms —— "
                               + "问题出在到这一家的路径上，不是整条宽带不行。",
                        TargetTab = LinkIndex,
                    });
                else if (worst.Avg > cat.GoodMs)
                    list.Add(new IssueRow
                    {
                        Tone = Tone.Warn,
                        Title = $"{cat.Name}延迟一般（{worst.Name} {worst.Avg:F1} ms）",
                        Detail = $"{cat.Name}建议低于 {cat.GoodMs:F0} ms。"
                               + $"当前最快的 {best.Name} 是 {best.Avg:F1} ms。",
                        TargetTab = LinkIndex,
                    });
            }

            foreach (var t in s.Latency.Targets.Where(t => t.Ok))
            {
                if (t.Loss > 0)
                    list.Add(new IssueRow
                    {
                        Tone = Thresholds.Loss(t.Loss),
                        Title = Thresholds.Loss(t.Loss) == Tone.Bad
                            ? $"{t.Name} 丢包 {t.Loss:F0}%"
                            : $"{t.Name} 存在轻微丢包（{t.Loss:F0}%）",
                        Detail = Thresholds.Loss(t.Loss) == Tone.Bad
                            ? $"发往 {t.Host} 的探测包有 {t.Loss:F0}% 没有响应，链路质量差。"
                            : $"到 {t.Host} 有少量丢包，可能影响实时音视频与游戏体验。",
                        TargetTab = LinkIndex,
                    });

                if (LatencyReport.Jitter(t.Samples) > Thresholds.JitterWarnMs)
                    list.Add(new IssueRow
                    {
                        Tone = Tone.Warn,
                        Title = $"{t.Name} 抖动较大（{LatencyReport.Jitter(t.Samples):F1} ms）",
                        Detail = "相邻探测的延迟波动明显，说明链路不稳定，即使平均延迟不高也会影响实时应用。",
                        TargetTab = LinkIndex,
                    });
            }

            var allFail = s.Latency.Targets.Count > 0 && s.Latency.Targets.All(t => !t.Ok);
            if (allFail)
                list.Add(new IssueRow
                {
                    Tone = Tone.Bad,
                    Title = "所有延迟目标均不可达",
                    Detail = "ICMP 与 TCP 探测全部失败，外网访问可能已中断。",
                    TargetTab = LinkIndex,
                });
        }

        // ---- DNS ----
        if (s.Hijack?.AnyHijacked == true)
        {
            var bad = s.Hijack.Checks.Where(c => c.Hijacked).Select(c => c.Server).ToList();
            list.Add(new IssueRow
            {
                Tone = Tone.Bad,
                Title = "检测到 DNS 劫持",
                Detail = "对不存在的域名 " + s.Hijack.ProbeDomain + "，以下服务器返回了结果而不是 NXDOMAIN："
                       + string.Join("、", bad) + "。运营商或中间设备可能在对 DNS 应答做重定向。",
                TargetTab = DnsIndex,
            });
        }

        if (s.DnsServerTests != null)
        {
            foreach (var t in s.DnsServerTests.Where(t => !t.Ok))
                list.Add(new IssueRow
                {
                    Tone = Tone.Warn,
                    Title = $"DNS 服务器 {t.Server} 无响应",
                    Detail = "该服务器未能返回解析结果：" + (t.Error.Length > 0 ? t.Error : "超时")
                           + "。若它是主 DNS，域名解析会变慢或失败。",
                    TargetTab = DnsIndex,
                });

            var slow = s.DnsServerTests
                .Where(t => t.Ok && Thresholds.IsProblem(Thresholds.DnsServer(t.Ms))).ToList();
            if (slow.Count > 0)
                list.Add(new IssueRow
                {
                    Tone = slow.Any(t => Thresholds.DnsServer(t.Ms) == Tone.Bad) ? Tone.Bad : Tone.Warn,
                    Title = $"DNS 解析偏慢（最高 {slow.Max(t => t.Ms):F0} ms）",
                    Detail = string.Join("、", slow.Select(t => $"{t.Server} {t.Ms:F0} ms"))
                           + $"。正常应低于 {Thresholds.DnsServerGoodMs:F0} ms，偏慢会拖慢网页首屏。",
                    TargetTab = DnsIndex,
                });
        }

        // 「换个 DNS 会不会更快」—— 对比段的结论条是橙色（Warn），
        // 按「橙色 ⇒ 必有问题条目」这条约定，这里就必须给出对应条目，
        // 否则用户看到橙色却在清单里找不到解释。判据与结论条共用
        // Thresholds.DnsSwitchWorthIt，两边各写一套比较逻辑迟早会漂移。
        if (s.DnsCompareTests is { Count: > 0 } cmps && s.DnsServerTests != null)
        {
            var bestPub = cmps.Where(x => x.Ok).OrderBy(x => x.Ms).FirstOrDefault();
            // 只跟**公网**的当前 DNS 比：局域网那台（多半是路由器）的耗时只代表
            // 到路由器的往返，不含往上递归的时间，拿它比会得出「公共 DNS 慢 14 倍」
            // 这种由测量口径造出来的假结论 —— 与卡片结论条同一口径。
            var curPub = s.DnsServerTests
                .Where(x => x.Ok && IpGeo.IsLookupable(x.Server))
                .OrderBy(x => x.Ms).FirstOrDefault();

            if (bestPub != null && curPub != null
                && Thresholds.DnsSwitchWorthIt(curPub.Ms, bestPub.Ms))
                list.Add(new IssueRow
                {
                    Tone = Tone.Warn,
                    Title = $"可以换个更快的 DNS（省 {curPub.Ms - bestPub.Ms:F0} ms）",
                    Detail = $"当前 DNS（{curPub.Server}）解析 {DnsProbe.CompareTestDomain} 要 {curPub.Ms:F1} ms，"
                           + $"{bestPub.Server} 只要 {bestPub.Ms:F1} ms。当前这台本身不算慢，"
                           + "但网页首屏要等多次解析，换掉能省下这段等待。",
                    TargetTab = DnsIndex,
                });
        }

        // 本机解析器的域名解析。以前「域名解析耗时」那组条形图会按 60 / 300 ms 上色，
        // 但问题清单里对不上任何条目 —— 又是「图上有颜色、清单里没解释」。
        if (s.DnsResolve != null)
        {
            var failed = s.DnsResolve.Where(x => !x.Ok).ToList();
            if (failed.Count > 0)
                list.Add(new IssueRow
                {
                    Tone = Tone.Bad,
                    Title = $"{failed.Count} 个域名解析失败",
                    Detail = string.Join("、", failed.Select(x => x.Domain))
                           + " 没能解析出地址，可能是 DNS 不可达、被污染，或域名本身不存在。",
                    TargetTab = DnsIndex,
                });

            var slowResolve = s.DnsResolve
                .Where(x => x.Ok && Thresholds.IsProblem(Thresholds.DnsResolve(x.Avg))).ToList();
            if (slowResolve.Count > 0)
                list.Add(new IssueRow
                {
                    Tone = slowResolve.Any(x => Thresholds.DnsResolve(x.Avg) == Tone.Bad) ? Tone.Bad : Tone.Warn,
                    Title = $"域名解析偏慢（最高 {slowResolve.Max(x => x.Avg):F0} ms）",
                    Detail = string.Join("、", slowResolve.Select(x => $"{x.Domain} {x.Avg:F0} ms"))
                           + $"（本机解析器，含缓存与转发）。建议低于 {Thresholds.DnsResolveGoodMs:F0} ms。",
                    TargetTab = DnsIndex,
                });
        }

        if (s.Hijack != null && !s.Hijack.AnyHijacked && s.Hijack.Comparisons.Any(c => !c.AllAgree))
            list.Add(new IssueRow
            {
                Tone = Tone.Warn,
                Title = "不同 DNS 服务器的解析结果不一致",
                Detail = "同一域名在不同 DNS 下解析到不同地址。可能是 CDN 就近调度（正常），也可能是缓存不一致或污染。",
                TargetTab = DnsIndex,
            });

        // ---- NAT ----
        if (s.Nat != null)
        {
            if (s.Nat.Type.Contains("未知") || s.Nat.Type.Contains("无法判定"))
                list.Add(new IssueRow
                {
                    Tone = Tone.Warn,
                    Title = "无法判定 NAT 类型",
                    Detail = s.Nat.Detail.Length > 0 ? s.Nat.Detail : "所有 STUN 服务器均不可达。",
                    TargetTab = NatIndex,
                });
            else if (s.Nat.Type.Contains("多出口") || s.Nat.Type.Contains("对称"))
                list.Add(new IssueRow
                {
                    Tone = Tone.Warn,
                    Title = "NAT 类型不利于 P2P 直连（" + ShortNat(s.Nat.Type) + "）",
                    Detail = s.Nat.Detail.Length > 0 ? s.Nat.Detail
                           : "每个目标映射到不同的外部地址或端口，直连打洞成功率低，需要中继。",
                    TargetTab = NatIndex,
                });
        }

        // ---- 带宽 ----
        if (s.BandwidthDirect != null && !s.BandwidthDirect.Ok && s.BandwidthProxy != null && !s.BandwidthProxy.Ok)
            list.Add(new IssueRow
            {
                Tone = Tone.Warn,
                Title = "带宽测速全部失败",
                Detail = "直连与代理两种方式都没有拿到数据：" + s.BandwidthDirect.Error,
                TargetTab = LinkIndex,
            });
        else if (bw != null)
        {
            // 判据与卡片配色同源：卡片转橙/转红就一定在这里有一条。
            // 以前卡片 <20 Mbps 就转橙、清单却要 <10 才报，15 Mbps 的用户看到橙色找不到问题。
            var bwTone = BandwidthTone(bw.Mbps);
            if (Thresholds.IsProblem(bwTone))
                list.Add(new IssueRow
                {
                    Tone = bwTone,
                    Title = $"下载带宽偏低（{bw.Mbps:F1} Mbps）",
                    Detail = $"实测下载速率低于 {Thresholds.BandwidthFairMbps:F0} Mbps，"
                           + "高清视频、大文件传输会明显受限。",
                    TargetTab = LinkIndex,
                });
        }

        // ---- MTU ----
        if (s.Mtu != null)
        {
            if (!s.Mtu.Ok)
                list.Add(new IssueRow
                {
                    Tone = Tone.Warn,
                    Title = "MTU 探测未完成",
                    Detail = s.Mtu.Error.Length > 0 ? s.Mtu.Error : "目标不可达，无法探测路径 MTU。",
                    TargetTab = PathIndex,
                });
            else
            {
                // 以前无论 MTU 是 1450 还是 1200 都只给「注意」，而胶囊在 <1400 时是红色。
                var mtuTone = Thresholds.Mtu(s.Mtu.PathMtu);
                if (Thresholds.IsProblem(mtuTone))
                    list.Add(new IssueRow
                    {
                        Tone = mtuTone,
                        Title = $"路径 MTU 偏低（{s.Mtu.PathMtu}）",
                        Detail = s.Mtu.Assessment,
                        TargetTab = PathIndex,
                    });
            }
        }

        // ---- 路由 ----
        if (s.Traceroute != null && !s.Traceroute.Ok && s.Traceroute.Error.Length > 0)
            list.Add(new IssueRow
            {
                Tone = Tone.Warn,
                Title = "路由追踪失败",
                Detail = s.Traceroute.Error,
                TargetTab = PathIndex,
            });
        else if (s.Traceroute is { Ok: true, Reached: false, StoppedEarly: false } tr)
            // 跑满最大跳数没到达：不是报错，但摘要行会为它亮橙色 ——
            // 亮色就必须有对应条目，否则就是「网关 5 ms」那次事故的重演。
            // （StoppedEarly 走中性色，明确不进这里。）
            list.Add(new IssueRow
            {
                Tone = Tone.Warn,
                Title = $"路由追踪未到达目标（{tr.Hops.Count} 跳）",
                Detail = "已达到最大跳数仍未到达 " + tr.Target + "。可能是目标屏蔽了 ICMP 应答，"
                       + "也可能是后半段路径不通 —— 翻到「路径」卡片看最后一跳停在哪里。",
                TargetTab = PathIndex,
            });

        if (list.Count == 0)
            list.Add(new IssueRow
            {
                Tone = Tone.Good,
                Title = "未发现明显问题",
                Detail = s.Adapters == null && s.Nat == null && s.Latency == null
                    ? "还没有执行任何检测，点击上方的检测按钮开始。"
                    : "在已完成的项目里没有发现异常。",
                TargetTab = OverviewIndex,
            });
    }

    // ------------------------------------------------------------------
    // 标签页
    // ------------------------------------------------------------------

    private static TabView TabOverview(DiagState s, ReportView v, List<string> ips,
                                       BandwidthReport? bw, LatencyTarget? bestLat)
    {
        var t = new TabView { Title = "概览" };

        var sum = new Section { Title = "检测摘要", Hint = "已完成的项目汇总" };

        if (s.Adapters != null)
        {
            int up = s.Adapters.Count(a => a.IsUp);
            sum.Kv.Add(new KeyValueRow
            {
                Key = "本机网卡",
                Value = $"{s.Adapters.Count} 块（{up} 已连接）",
                Tone = up > 0 ? Tone.Good : Tone.Bad,
            });
        }

        if (ips.Count > 0)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "公网出口",
                Value = ips.Count == 1 ? ips[0] : $"{ips.Count} 个",
                Geo = ips.Count == 1 ? IpGeo.Of(s, ips[0]) : "",
                Tone = ips.Count == 1 ? Tone.Good : Tone.Warn,
                // 多出口时把每个地址连同归属地一起列出来，否则「3 个」这个结论没有落脚点
                Note = ips.Count > 1 ? string.Join("、", ips.Select(x => WithGeo(s, x))) : "",
            });

        if (bestLat != null)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "网络延迟",
                Value = $"{bestLat.Avg:F1} ms",
                Tone = LatencyTone(bestLat.Avg, bestLat.Category),
                Note = $"{bestLat.Category} · {bestLat.Name} · 抖动 {LatencyReport.Jitter(bestLat.Samples):F1} ms",
            });

        // 丢包只在真的丢了的时候占一行：0% 是这个摘要里的默认状态，
        // 每个网络都印一遍「丢包 0.0%」纯属噪音。
        var worstLoss = WorstLossTarget(s);
        if (worstLoss is { Loss: > 0 })
            sum.Kv.Add(new KeyValueRow
            {
                Key = "丢包",
                Value = $"{worstLoss.Loss:F1}%",
                Tone = Thresholds.Loss(worstLoss.Loss),
                Note = "最差：" + worstLoss.Name,
            });

        if (s.Ipv6 != null)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "IPv6",
                Value = s.Ipv6.Verdict,
                Tone = Ipv6Tone(s.Ipv6),
                Note = s.Ipv6.Enabled && s.Ipv6.EgressIp.Length > 0
                    ? "出口 " + s.Ipv6.EgressIp
                    : $"{s.Ipv6.Addresses.Count(a => a.Kind == "全局")} 个全局地址",
            });

        if (s.Web is { Targets.Count: > 0 } web)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "HTTP / HTTPS",
                Value = web.Verdict,
                Tone = WebTone(web),
                Note = string.Join("、", web.Targets.Select(
                    x => x.Name + (x.Ok ? " 通过" : " 卡在" + x.FailedStep))),
            });

        if (s.DnsServers != null && s.DnsServers.Count > 0)
        {
            var okTests = s.DnsServerTests?.Where(x => x.Ok).Select(x => x.Ms).ToList();
            int bad = s.DnsServerTests?.Count(x => !x.Ok) ?? 0;
            // 值列只放数量、明细放备注 —— 跟"公网出口"行保持同一种节奏，
            // 否则这一行的值会特别长，把备注推到很远，整张表看起来参差不齐。
            var timing = okTests is { Count: > 0 }
                ? $"解析耗时 {okTests.Min():F1} ~ {okTests.Max():F1} ms" + (bad > 0 ? $"，{bad} 个无响应" : "")
                : (bad > 0 ? $"{bad} 个无响应" : "");
            sum.Kv.Add(new KeyValueRow
            {
                Key = "DNS 服务器",
                Value = $"{s.DnsServers.Count} 个",
                Tone = bad > 0 ? Tone.Warn : Tone.Good,
                Note = string.Join("、", s.DnsServers.Select(x => WithGeo(s, x)))
                     + (timing.Length > 0 ? " · " + timing : ""),
            });
        }

        if (s.Hijack != null)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "DNS 劫持",
                Value = s.Hijack.AnyHijacked ? "发现劫持" : "未发现",
                Tone = s.Hijack.AnyHijacked ? Tone.Bad : Tone.Good,
                Note = "探针域名 " + s.Hijack.ProbeDomain,
            });

        if (s.Nat != null)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "NAT 类型",
                Value = ShortNat(s.Nat.Type),
                Tone = NatTone(s.Nat.Type),
                Note = s.Nat.MappedPorts.Count > 0 ? "外部端口 " + string.Join("、", s.Nat.MappedPorts) : "",
            });

        if (bw != null)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "下载带宽",
                Value = $"{bw.Mbps:F1} Mbps",
                Tone = BandwidthTone(bw.Mbps),
                Note = $"{bw.Source} · {bw.MBps:F1} MB/s",
            });

        if (s.Mtu?.Ok == true)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "路径 MTU",
                Value = s.Mtu.PathMtu.ToString(),
                // 必须走 Thresholds.Mtu —— 和胶囊、问题条目同一个函数。
                // 这里曾内联写死「>=1500 ? Good : Warn」，MTU 1300 时摘要行是橙色、
                // 同屏胶囊却是红色，同一行数据两个说法。
                Tone = Thresholds.Mtu(s.Mtu.PathMtu),
                Note = "目标 " + s.Mtu.Target,
            });

        if (s.Traceroute != null)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "路由追踪",
                Value = s.Traceroute.Hops.Count + " 跳",
                // 「提前结束」不是故障 —— 中间设备屏蔽 ICMP 不代表网络不通
                // （文本报告里也是这么说的），所以是中性色、不进问题清单。
                // 只有「跑满最大跳数还没到」才值得橙色，那一条在问题清单里有对应条目。
                Tone = s.Traceroute.Reached ? Tone.Good
                     : s.Traceroute.StoppedEarly ? Tone.Neutral
                     : Tone.Warn,
                Note = s.Traceroute.Reached ? "已到达 " + s.Traceroute.Target
                       : s.Traceroute.StoppedEarly ? "中途被屏蔽，未能到达" : s.Traceroute.Target,
            });

        if (s.PortScan != null)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "端口检测",
                Value = $"开放 {s.PortScan.OpenCount} / 共 {s.PortScan.Results.Count}",
                // 同上：开放端口数是中性信息，不给警示色（否则概览上会有一个说不清来由的橙色）
                Tone = Tone.Neutral,
                Note = s.PortScan.Host,
            });

        if (s.Proxy != null && s.Proxy.Count > 0)
            sum.Kv.Add(new KeyValueRow
            {
                Key = "系统代理",
                Value = "已配置",
                Tone = Tone.Warn,
                Note = string.Join("；", ProxyEntries(s.Proxy).Select(x => x.Key + "=" + x.Value)),
            });

        if (sum.Kv.Count > 0) t.Sections.Add(sum);

        if (v.Issues.Count > 0)
        {
            var issues = new Section
            {
                Title = "问题与提示",
                Hint = v.Issues.Count(i => i.Tone == Tone.Bad) + " 项严重 · "
                     + v.Issues.Count(i => i.Tone == Tone.Warn) + " 项需注意",
            };
            issues.Issues.AddRange(v.Issues);
            t.Sections.Add(issues);
        }

        if (s.Timestamp.Length > 0)
            t.Sections.Add(new Section
            {
                Title = "本次检测",
                Notes =
                {
                    "检测时间：" + s.Timestamp,
                    "主机名：" + (s.Hostname.Length > 0 ? s.Hostname : "未知"),
                    "未执行的项目在对应标签页中会显示为空。",
                },
            });

        t.HasData = sum.Kv.Count > 0;
        return t;
    }

    private static TabView TabLocal(DiagState s)
    {
        var t = new TabView { Title = "本机" };
        var adapters = s.Adapters;

        if (adapters != null)
        {
            var sec = new Section { Title = "网络适配器", Hint = $"{adapters.Count} 块" };
            foreach (var a in adapters)
            {
                sec.Adapters.Add(new AdapterRow
                {
                    Name = a.Name,
                    Description = a.Description,
                    Type = a.Type,
                    Status = a.Status,
                    IPv4 = a.IPv4,
                    Mask = a.Mask,
                    Gateway = a.Gateway,
                    Mac = a.Mac,
                    Dns = a.Dns.Count > 0 ? string.Join("、", a.Dns) : "—",
                    Speed = a.SpeedMbps > 0 ? $"{a.SpeedMbps} Mbps" : "—",
                    Dhcp = a.DhcpEnabled ? "DHCP" : "静态",
                    IsUp = a.IsUp,
                    Tone = a.IsUp ? Tone.Good : Tone.Neutral,
                });
            }
            t.Sections.Add(sec);
        }

        // 代理来自环境变量，跟读网卡是两条独立路径。以前这里在 adapters == null 时直接
        // return，会把已经拿到的代理信息一起吞掉，所以两者必须分开判断。
        if (s.Proxy != null && s.Proxy.Count > 0)
        {
            var p = new Section { Title = "代理设置", Hint = "来自环境变量（已按大小写去重）" };
            foreach (var kv in ProxyEntries(s.Proxy))
                p.Kv.Add(new KeyValueRow
                {
                    Key = kv.Key,
                    Value = kv.Value.Length > 0 ? kv.Value : "（未设置）",
                    Tone = kv.Value.Length > 0 ? Tone.Warn : Tone.Neutral,
                });
            t.Sections.Add(p);
        }

        t.HasData = t.Sections.Count > 0;
        return t;
    }

    /// <summary>
    /// 「公网出口 IP」卡片。原来这段是「连通性」卡片里的第一个区块，
    /// 但它和延迟/带宽之间没有因果关系 —— 出口 IP 是「我是谁」，延迟是「我有多快」。
    /// 拆成独立卡片后，两个按钮各管自己那张卡，也符合「一个检测项一张卡片」的原则。
    /// </summary>
    private static TabView TabEgress(DiagState s, List<string> ips)
    {
        var t = new TabView { Title = "公网出口 IP" };

        var egress = new Section
        {
            Title = "公网出口",
            Hint = "不同路径看到的出口地址",
        };

        if (s.PublicIpDirect != null)
            egress.Kv.Add(new KeyValueRow
            {
                Key = "原生 UDP（STUN）",
                Value = s.PublicIpDirect,
                Geo = IpGeo.Of(s, s.PublicIpDirect),
                Tone = s.PublicIpDirect == StunAndNat.Unavailable ? Tone.Warn : Tone.Good,
            });

        if (s.PublicIpHttpDirect != null)
            foreach (var r in s.PublicIpHttpDirect)
                egress.Kv.Add(new KeyValueRow
                {
                    Key = "HTTP 直连 · " + r.Service,
                    Value = r.Ok ? r.Ip : "失败",
                    Geo = r.Ok ? IpGeo.Of(s, r.Ip) : "",
                    Tone = r.Ok ? Tone.Good : Tone.Warn,
                    Note = r.Ok ? (r.Extra.Length > 0 ? r.Extra + " · " : "") + $"{r.Seconds * 1000:F0} ms"
                                : r.Error,
                });

        if (s.PublicIpHttpProxy != null)
            foreach (var r in s.PublicIpHttpProxy)
                egress.Kv.Add(new KeyValueRow
                {
                    Key = "经代理 · " + r.Service,
                    Value = r.Ok ? r.Ip : "失败",
                    Geo = r.Ok ? IpGeo.Of(s, r.Ip) : "",
                    Tone = r.Ok ? Tone.Good : Tone.Neutral,
                    Note = r.Ok ? $"{r.Seconds * 1000:F0} ms" : r.Error,
                });

        if (egress.Kv.Count > 0)
        {
            if (ips.Count > 1)
            {
                // 纯文本场合塞不进独立的归属地小字，就地拼进括号里
                var detail = string.Join("、", ips.Select(x => WithGeo(s, x)));
                egress.Notes.Add($"检测到 {ips.Count} 个不同的公网 IP：{detail}。");
                egress.Notes.Add("常见于多线接入、校园网或负载均衡，会导致 P2P 打洞、IP 白名单、会话保持类应用异常。");
            }
            egress.Notes.Add("原生 UDP 路径反映真实网络栈，HTTP 路径会受系统代理影响。");
            t.Sections.Add(egress);
            t.HasData = true;
        }

        return t;
    }

    private static TabView TabLink(DiagState s, BandwidthReport? bw)
    {
        var t = new TabView { Title = "连通性" };

        // ---- 网络质量：网关 / 延迟 / 丢包 / 抖动 ----
        // 这四个数字放在最前面，构成一条从近到远的诊断链：
        //   网关 → 本机到路由器     延迟/丢包/抖动 → 路由器到互联网
        // 网关正常而公网不通，问题就在路由器到运营商那一段；网关本身就不正常，
        // 那是本地链路（网线、无线信号、网关负载）的事。这一句区分比很多高级功能都实用。
        //
        // 「平均延迟正常」也不等于「网络正常」——32 ms 配 8% 丢包的网络刷网页没事，
        // 打游戏、远程桌面、语音通话都会明显出问题，所以丢包与抖动必须和延迟挨着看。
        var bestLat = RepresentativeLatency(s);
        var worstLoss = WorstLossTarget(s);
        LatencyTarget? gwTarget = s.Latency?.Gateway is { Ok: true } g0 ? g0 : null;
        if (gwTarget != null || bestLat != null || worstLoss != null)
        {
            var q = new Section { Title = "网络质量", Hint = "网关 / 延迟 / 丢包 / 抖动" };

            if (gwTarget != null)
                q.Kv.Add(new KeyValueRow
                {
                    Key = "网关",
                    Value = $"{gwTarget.Avg:F1} ms",
                    Tone = GatewayTone(gwTarget.Avg),
                    Note = $"{gwTarget.Host} · 本机 → 路由器，局域网内应低于 {Thresholds.GatewayGoodMs:F0} ms",
                });

            if (bestLat != null)
            {
                q.Kv.Add(new KeyValueRow
                {
                    Key = "延迟",
                    Value = $"{bestLat.Avg:F1} ms",
                    Tone = LatencyTone(bestLat.Avg, bestLat.Category),
                    Note = $"{bestLat.Category} · {bestLat.Name} · 路由器 → 互联网",
                });
                q.Kv.Add(new KeyValueRow
                {
                    Key = "抖动",
                    Value = $"{LatencyReport.Jitter(bestLat.Samples):F1} ms",
                    Tone = Thresholds.Jitter(LatencyReport.Jitter(bestLat.Samples)),
                    Note = $"相邻两次探测的延迟波动，超过 {Thresholds.JitterWarnMs:F0} ms 说明链路不稳",
                });
            }
            if (worstLoss != null)
            {
                q.Kv.Add(new KeyValueRow
                {
                    Key = "丢包率",
                    Value = $"{worstLoss.Loss:F1}%",
                    Tone = Thresholds.Loss(worstLoss.Loss),
                    Note = worstLoss.Loss > 0 ? $"最差：{worstLoss.Name}" : "所有目标都没有丢包",
                });
            }
            t.Sections.Add(q);
            t.HasData = true;
        }

        // ---- 延迟 ----
        if (s.Latency != null)
        {
            // 本地链路单独一段：它是唯一一个「应该低于 3 ms」的目标，
            // 和公网目标放同一把尺子上比，会被压成看不见的一条线
            if (s.Latency.Gateway?.Ok == true)
            {
                var gw = s.Latency.Gateway;
                var sec = new Section { Title = "本地链路", Hint = "到网关，局域网内应低于 3 ms" };
                sec.Bars.Add(new BarRow
                {
                    Name = "网关",
                    Detail = $"{gw.Host} · {gw.Method} · 最小 {gw.Min:F1} / 最大 {gw.Max:F1} ms"
                           + $" · 抖动 {LatencyReport.Jitter(gw.Samples):F1} ms",
                    ValueText = $"{gw.Avg:F1} ms",
                    BarWidth = BarRow.TrackWidth,
                    Tone = GatewayTone(gw.Avg),
                });
                t.Sections.Add(sec);
                t.HasData = true;
            }

            // 按用途分组：网页 / 视频 / 游戏。每组用自己的量程，
            // 这样「游戏里 Steam 明显比别人慢一截」一眼就能看出来。
            foreach (var cat in LatencyProbe.Categories)
            {
                var group = s.Latency.Targets.Where(x => x.Category == cat.Name).ToList();
                if (group.Count == 0) continue;

                var sec = new Section
                {
                    Title = cat.Name,
                    Hint = $"{cat.Hint} · ≤{cat.GoodMs:F0} ms 良好 · ≤{cat.WarnMs:F0} ms 可用",
                };
                AddLatencyBars(sec, group);
                t.Sections.Add(sec);
                t.HasData = true;
            }

            // 分类对不上的（旧版本导出的 json、手工构造的目标）也要显示出来，不能吞掉
            var orphans = s.Latency.Targets
                .Where(x => LatencyProbe.Categories.All(c => c.Name != x.Category)).ToList();
            if (orphans.Count > 0)
            {
                var sec = new Section { Title = "其他目标", Hint = "未归入已知用途分类" };
                AddLatencyBars(sec, orphans);
                t.Sections.Add(sec);
                t.HasData = true;
            }
        }

        // ---- 带宽 ----
        var bwSec = new Section { Title = "下载带宽", Hint = "固定时长流式下载，绕过代理" };
        if (s.BandwidthDirect != null)
        {
            var r = s.BandwidthDirect;
            bwSec.Kv.Add(new KeyValueRow
            {
                Key = "直连实测",
                Value = r.Ok ? $"{r.Mbps:F1} Mbps" : "失败",
                Tone = r.Ok ? BandwidthTone(r.Mbps) : Tone.Warn,
                Note = r.Ok ? $"{r.Source} · 下载 {r.Bytes / 1048576.0:F1} MB / {r.Seconds:F1} s"
                            : r.Error,
            });
        }
        if (s.BandwidthProxy != null)
        {
            var r = s.BandwidthProxy;
            bwSec.Kv.Add(new KeyValueRow
            {
                Key = "经代理实测",
                Value = r.Ok ? $"{r.Mbps:F1} Mbps" : "失败",
                Tone = r.Ok ? BandwidthTone(r.Mbps) : Tone.Neutral,
                Note = r.Ok ? $"{r.Source} · 下载 {r.Bytes / 1048576.0:F1} MB / {r.Seconds:F1} s"
                            : r.Error,
            });
        }
        if (bwSec.Kv.Count > 0)
        {
            bwSec.Notes.Add("1 MB/s ≈ 8 Mbps。测速结果受测速源、线路拥塞与 Wi-Fi 影响，仅供参考。");
            t.Sections.Add(bwSec);
            t.HasData = true;
        }

        return t;
    }

    /// <summary>
    /// IPv6 卡片的色调。
    ///
    /// **「未启用」是中性色而不是警告色。** 大量网络本来就只有 IPv4，
    /// 把它涂成橙色等于给一个不存在的毛病报错 —— 这和 v2.4 立下的
    /// 「图上有颜色，问题清单里就必须有一条对应条目」是同一件事的两面：
    /// 无 IPv6 不产生问题条目，也就不能有颜色。
    /// </summary>
    private static Tone Ipv6Tone(Ipv6Report r) => r.Verdict switch
    {
        "双栈正常" => Tone.Good,
        "有地址但不通" => Tone.Bad,
        "只能到国内" or "仅国际可达" => Tone.Warn,
        _ => Tone.Neutral,
    };

    private static TabView TabIpv6(DiagState s)
    {
        var t = new TabView { Title = "IPv6" };
        if (s.Ipv6 == null) return t;

        var r = s.Ipv6;
        t.HasData = true;

        var sec = new Section
        {
            Title = "IPv6 状态",
            Hint = "地址分配 · 出口 · 国内 / 国外可达性",
            Verdict = r.Verdict,
            VerdictTone = Ipv6Tone(r),
        };

        sec.Kv.Add(new KeyValueRow
        {
            Key = "全局地址",
            Value = r.Addresses.Count(a => a.Kind == "全局").ToString(),
            Tone = r.Enabled ? Tone.Good : Tone.Neutral,
            Note = r.Enabled ? "" : "没有全局地址时，出口与可达性探测会直接跳过",
        });

        if (r.EgressIp.Length > 0)
            sec.Kv.Add(new KeyValueRow
            {
                Key = "IPv6 出口",
                Value = r.EgressIp,
                Geo = IpGeo.Of(s, r.EgressIp),
                Tone = Tone.Good,
            });
        else if (r.Enabled)
            sec.Kv.Add(new KeyValueRow
            {
                Key = "IPv6 出口",
                Value = "未获取到",
                Tone = Tone.Warn,
                Note = r.EgressError.Length > 0 ? r.EgressError : "所有 IPv6 出口接口均不可达",
            });

        if (r.Detail.Length > 0) sec.Notes.Add(r.Detail);
        t.Sections.Add(sec);

        // ---- 地址明细 ----
        if (r.Addresses.Count > 0)
        {
            var addr = new Section { Title = "本机 IPv6 地址", Hint = "已排除链路本地地址" };
            foreach (var a in r.Addresses)
                addr.Kv.Add(new KeyValueRow
                {
                    Key = a.Kind + (a.IsTemporary ? " · 临时" : ""),
                    Value = a.Address,
                    Tone = a.Kind == "全局" ? Tone.Good : Tone.Neutral,
                    Note = a.Adapter,
                });
            if (r.Addresses.Any(a => a.IsTemporary))
                addr.Notes.Add("「临时」是按接口标识符的 u 位判定的：清零即为随机生成，"
                             + "对应 RFC 4941 的隐私扩展地址，会随时间变化 —— "
                             + "如果某项服务按 IP 做了白名单，地址一变就会失效。");
            if (r.Addresses.Any(a => a.Kind == "内网"))
                addr.Notes.Add("「内网」是 ULA 地址（fc00::/7），只能在局域网内互通，出不了公网。");
            t.Sections.Add(addr);
        }

        // ---- 可达性 ----
        if (r.Pings.Count > 0)
        {
            var ping = new Section
            {
                Title = "可达性",
                Hint = "每个目标最多尝试 " + Math.Max(1, r.Pings.Max(p => p.Attempts)) + " 次，1.5 秒超时",
            };
            foreach (var p in r.Pings)
                ping.Kv.Add(new KeyValueRow
                {
                    Key = p.Region + " · " + p.Name,
                    Value = p.Ok ? $"{p.Ms:F0} ms" : "不可达",
                    Tone = p.Ok ? Tone.Good : Tone.Bad,
                    Note = p.Host + (p.Ok ? "" : " · " + p.Error),
                });
            t.Sections.Add(ping);
        }

        return t;
    }

    private static Tone WebTone(WebReport r)
    {
        if (r.Targets.Count == 0) return Tone.Neutral;
        if (r.AllOk) return Tone.Good;
        // 还有目标能通说明本机网络没断，问题在个别目标上 —— 给「注意」而不是「严重」
        return r.AnyOk ? Tone.Warn : Tone.Bad;
    }

    /// <summary>
    /// 「HTTP / HTTPS」卡片。
    /// 每个目标一段，段里按 DNS → TCP → TLS → HTTP 四行列出来，
    /// **第一个失败的行就是问题所在**，段标题上直接写出「卡在 X 层」。
    /// 这张卡片回答的不是「网站能不能打开」，而是「打不开的话，卡在哪一步」。
    /// </summary>
    private static TabView TabWeb(DiagState s)
    {
        var t = new TabView { Title = "HTTP / HTTPS" };
        var r = s.Web;
        if (r == null || r.Targets.Count == 0) return t;

        var head = new Section
        {
            Title = "结论",
            Hint = "DNS → TCP → TLS → HTTP，第一层失败就停",
        };
        head.Verdict = r.Verdict;
        head.VerdictTone = WebTone(r);
        if (r.Detail.Length > 0) head.Notes.Add(r.Detail);
        t.Sections.Add(head);

        foreach (var g in r.Targets)
        {
            var sec = new Section
            {
                Title = g.Name,
                Hint = g.Region + " · " + g.Host
                     + (g.ResolvedIp.Length > 0 ? " → " + g.ResolvedIp : ""),
            };

            if (!g.Ok)
            {
                sec.Verdict = $"卡在 {g.FailedStep} 层 · {g.Blame}";
                sec.VerdictTone = Tone.Bad;
            }

            foreach (var st in g.Steps)
                sec.Kv.Add(new KeyValueRow
                {
                    Key = st.Name,
                    Value = st.Ok ? MsText(st.Ms) : "失败",
                    Tone = st.Ok ? Tone.Good : Tone.Bad,
                    Note = st.Ok ? st.Detail : st.Error,
                });

            // HTTP 层通了但服务端返回 4xx/5xx：链路没问题，只是这个请求被拒。
            // 不判失败，但要说清楚 —— 否则用户会以为「检测通过 = 网站正常」。
            if (g.Ok && g.HttpStatus >= 400)
                sec.Notes.Add($"服务端返回 {g.HttpStatus}：网络链路是通的，是这个请求被服务端拒绝了"
                            + "（限流、需要登录、或路径不对）。");

            t.Sections.Add(sec);
            t.HasData = true;
        }

        var note = new Section { Title = "说明" };
        note.Notes.Add("四层是严格串行的：域名解析 → 建立 TCP 连接 → TLS 握手 → 收到 HTTP 响应。"
                     + "第一层失败，后面就不会再跑 —— 所以「卡在第几层」本身就是结论。");
        note.Notes.Add("HTTP 状态码 4xx / 5xx 不算失败：那说明链路全程通畅，只是服务端拒绝了这个请求。");
        note.Notes.Add("境外目标不通不代表本机有问题，但它能回答「这台机器能不能上外网」。");
        t.Sections.Add(note);

        return t;
    }

    /// <summary>把「卡在第几层」翻译成一句能指导下一步的话。</summary>
    private static string WebLayerHint(WebTargetResult g)
    {
        var body = g.FailedStep switch
        {
            "DNS" => $"域名 {g.Host} 没能解析成地址，说明 DNS 有问题（或域名本身不存在）。"
                   + "先跑一次 DNS 检测看解析是否正常。",
            "TCP" => $"域名解析到了 {g.ResolvedIp}，但 {WebProbe.Port} 端口连不上 —— "
                   + "目标不可达、被防火墙拦截，或这条链路被中途重置。",
            "TLS" => "TCP 已经连上，但 TLS 握手失败：常见于证书问题、TLS 被中间设备干扰，"
                   + "或本机系统时间不对导致证书被判为未生效。",
            "HTTP" => "TCP 与 TLS 都通了，但没拿到 HTTP 响应 —— 问题在服务端，不在本机网络。",
            _ => "",
        };

        var err = g.FirstFailure is { } f && f.Error.Length > 0 ? $"（{f.Error}）" : "";

        // 境外目标不通是个**很可能正常**的结果，别让用户以为本机坏了
        var caveat = g.Region == "境外" && g.FailedStep is "DNS" or "TCP"
            ? " 注意这是境外目标，不通也可能只是本机访问不了境外站点。"
            : "";

        return body + err + caveat;
    }

    private static TabView TabDns(DiagState s)
    {
        var t = new TabView { Title = "DNS" };

        // ---- 当前 DNS：本机网卡上配的那几台 ----
        // 标题从「系统 DNS 服务器」改成「当前 DNS」：用户要能一眼分清
        // 「我正在用的」和「只是拿来比一比的」这两类服务器（后者在下面的「对比 DNS」）。
        if (s.DnsServerTests != null && s.DnsServerTests.Count > 0)
        {
            var okMs = s.DnsServerTests.Where(x => x.Ok).Select(x => x.Ms).ToList();
            var maxMs = okMs.DefaultIfEmpty(100).Max();
            var minMs = okMs.DefaultIfEmpty(0).Min();
            var sec = new Section
            {
                Title = "当前 DNS",
                Hint = $"本机网卡上配置的服务器，解析 {DnsProbe.CompareTestDomain} 的耗时",
            };
            foreach (var x in s.DnsServerTests)
                sec.Bars.Add(new BarRow
                {
                    Name = x.Server,
                    Geo = IpGeo.Of(s, x.Server),
                    Detail = x.Ok ? $"{x.Answers} 条应答" : (x.Error.Length > 0 ? x.Error : "超时"),
                    ValueText = x.Ok ? MsText(x.Ms) : "失败",
                    BarWidth = x.Ok ? BarRow.Scale(x.Ms, maxMs, minMs) : BarRow.TrackWidth,
                    Tone = x.Ok ? Thresholds.DnsServer(x.Ms) : Tone.Bad,
                });
            t.Sections.Add(sec);
            t.HasData = true;
        }
        else if (s.DnsServers != null && s.DnsServers.Count > 0)
        {
            // 归属地要贴着 IP，而这一段的 IP 在 Key 列（定宽 190，放不下），
            // 所以把 IP 挪到值列 —— 行首统一写「DNS」，读起来也还是那个意思。
            var sec = new Section { Title = "当前 DNS", Hint = "未测速" };
            foreach (var d in s.DnsServers)
                sec.Kv.Add(new KeyValueRow
                {
                    Key = "DNS",
                    Value = d,
                    Geo = IpGeo.Of(s, d),
                    Note = "已配置",
                });
            t.Sections.Add(sec);
            t.HasData = true;
        }

        // ---- 域名解析 ----
        if (s.DnsResolve != null && s.DnsResolve.Count > 0)
        {
            var okAvg = s.DnsResolve.Where(x => x.Ok).Select(x => x.Avg).ToList();
            var maxMs = okAvg.DefaultIfEmpty(100).Max();
            var minMs = okAvg.DefaultIfEmpty(0).Min();
            var sec = new Section { Title = "域名解析耗时", Hint = "本机解析器，各 3 次取平均" };
            foreach (var x in s.DnsResolve)
                sec.Bars.Add(new BarRow
                {
                    Name = x.Domain,
                    Detail = x.Ok ? x.Address : "解析失败",
                    ValueText = x.Ok ? MsText(x.Avg) : "失败",
                    BarWidth = x.Ok ? BarRow.Scale(x.Avg, maxMs, minMs) : BarRow.TrackWidth,
                    Tone = x.Ok ? Thresholds.DnsResolve(x.Avg) : Tone.Bad,
                });
            t.Sections.Add(sec);
            t.HasData = true;
        }

        // ---- 劫持检测 ----
        if (s.Hijack != null)
        {
            // 结论文案必须按**实际应答的台数**说。以前是「AnyHijacked 为假」就写
            // 「所有服务器都正确返回 NXDOMAIN」—— 而服务器超时时 AnyHijacked 也是假，
            // 于是这句话会在有服务器根本没应答的情况下宣称「所有服务器都正常」。
            var answered = s.Hijack.Checks.Count(c => c.Ok);
            var silent = s.Hijack.Checks.Where(c => !c.Ok).Select(c => c.Server).ToList();

            var sec = new Section
            {
                Title = "DNS 劫持检测",
                Hint = "用不存在的域名做探针，返回结果即为异常",
            };

            if (s.Hijack.Checks.Count == 0)
            {
                sec.Verdict = "没有可用于探测的 DNS 服务器";
                sec.VerdictTone = Tone.Neutral;
            }
            else if (s.Hijack.AnyHijacked)
            {
                sec.Verdict = "发现 " + s.Hijack.Checks.Count(c => c.Hijacked) + " 台服务器存在劫持行为";
                sec.VerdictTone = Tone.Bad;
            }
            else if (silent.Count == 0)
            {
                sec.Verdict = $"所有服务器对不存在的域名都正确返回 NXDOMAIN";
                sec.VerdictTone = Tone.Good;
            }
            else
            {
                // 有服务器没应答就不能给绿色：我们只验证了 answered 台，剩下的没结论。
                sec.Verdict = $"未发现劫持（{answered}/{s.Hijack.Checks.Count} 台正常应答，"
                            + $"{string.Join("、", silent)} 无响应）";
                sec.VerdictTone = Tone.Neutral;
            }

            sec.Kv.Add(new KeyValueRow { Key = "探针域名", Value = s.Hijack.ProbeDomain });

            foreach (var c in s.Hijack.Checks)
                sec.Kv.Add(new KeyValueRow
                {
                    Key = "　" + c.Server,
                    Value = c.Hijacked ? "被劫持" : c.Ok ? "正常" : "无响应",
                    // 无响应给中性灰而不是橙色：这些是公共 DNS，用户并没有在用它们
                    // （下面的「对比 DNS」段也是这个口径）。给一台用户改不了的服务器
                    // 标橙，等于报一个他无从下手的故障；而且同一份数据在「对比 DNS」
                    // 段里就是灰色的，一张卡片里两种颜色自相矛盾。
                    Tone = c.Hijacked ? Tone.Bad : c.Ok ? Tone.Good : Tone.Neutral,
                    Note = c.Hijacked ? "返回 " + string.Join("、", c.Answers)
                         : c.Ok ? "RCODE " + c.RCode : c.Error,
                });

            t.Sections.Add(sec);
            t.HasData = true;
        }

        // ---- 对比 DNS：公共 DNS，用户并没有在用 ----
        // 把「当前 DNS」和「对比 DNS」分成两段，是为了回答一个「当前 DNS 36 ms」
        // 永远回答不了的问题：换个 DNS 会不会更快。
        // 两段用的是**同一个域名**（DnsProbe.CompareTestDomain），数字才能横向比。
        if (s.DnsCompareTests != null && s.DnsCompareTests.Count > 0)
        {
            var okCmp = s.DnsCompareTests.Where(x => x.Ok).OrderBy(x => x.Ms).ToList();
            var best = okCmp.Count > 0 ? okCmp[0] : null;
            var maxMs = okCmp.Select(x => x.Ms).DefaultIfEmpty(100).Max();
            var minMs = okCmp.Select(x => x.Ms).DefaultIfEmpty(0).Min();

            var sec = new Section
            {
                Title = "对比 DNS",
                Hint = $"公共 DNS，你没有在用它们 · 同样解析 {DnsProbe.CompareTestDomain}",
            };

            foreach (var x in s.DnsCompareTests)
                sec.Bars.Add(new BarRow
                {
                    Name = x.Server,
                    Geo = IpGeo.Of(s, x.Server),
                    Detail = x.Ok ? $"{x.Answers} 条应答" : (x.Error.Length > 0 ? x.Error : "超时"),
                    ValueText = x.Ok ? MsText(x.Ms) : "失败",
                    BarWidth = x.Ok ? BarRow.Scale(x.Ms, maxMs, minMs) : BarRow.TrackWidth,
                    // **刻意不按绝对阈值上色。** 这些服务器用户并没有在用，
                    // 给它们标红等于报一个「用户修不了的故障」；8.8.8.8 在国内慢本来就是常态，
                    // 不是本机网络的问题（和「境外网页目标失败降级」是同一条理由）。
                    // 这一段要回答的是「换一个会不会更快」，所以颜色表达的是**相对排序** ——
                    // 只把最快的那台标绿，其余中性。
                    Tone = x.Ok && ReferenceEquals(x, best) ? Tone.Good : Tone.Neutral,
                });

            var cur = s.DnsServerTests?.Where(x => x.Ok).ToList() ?? new List<DnsServerTest>();
            // 私有地址的 DNS（多半是路由器）耗时只代表「到路由器的往返」，
            // 不含路由器往上递归的时间 —— 拿它和公共 DNS 比是两种口径。
            var curPublic = cur.Where(x => IpGeo.IsLookupable(x.Server)).ToList();

            if (best == null)
            {
                sec.Verdict = "所有公共 DNS 都没有响应";
                sec.VerdictTone = Tone.Neutral;
            }
            else if (cur.Count == 0)
            {
                sec.Verdict = $"最快的公共 DNS：{best.Server}（{MsText(best.Ms)}）";
                sec.VerdictTone = Tone.Neutral;
            }
            else if (curPublic.Count == 0)
            {
                sec.Verdict = $"最快的公共 DNS：{best.Server}（{MsText(best.Ms)}）";
                sec.VerdictTone = Tone.Neutral;
                sec.Notes.Add($"当前 DNS（{string.Join("、", cur.Select(x => x.Server))}）是局域网地址，"
                            + "它的耗时只代表到路由器的往返，不含路由器往上递归的时间，"
                            + "所以这两个数字不能直接比。");
            }
            else
            {
                var curBest = curPublic.OrderBy(x => x.Ms).First();
                // 判据与问题清单共用 Thresholds.DnsSwitchWorthIt —— 两边各写一套就会漂移，
                // 那样「结论条是橙色、清单里却没有对应条目」又会回来。
                if (Thresholds.DnsSwitchWorthIt(curBest.Ms, best.Ms))
                {
                    sec.Verdict = $"换用 {best.Server} 可能明显更快"
                                + $"（{MsText(best.Ms)} vs 当前 {MsText(curBest.Ms)}）";
                    sec.VerdictTone = Tone.Warn;
                }
                else if (curBest.Ms < best.Ms)
                {
                    sec.Verdict = $"当前 DNS 比公共 DNS 还快"
                                + $"（{MsText(curBest.Ms)} vs 最快 {MsText(best.Ms)}），不用换";
                    sec.VerdictTone = Tone.Good;
                }
                else
                {
                    sec.Verdict = $"当前 DNS 与公共 DNS 相当"
                                + $"（{MsText(curBest.Ms)} vs 最快 {MsText(best.Ms)}）";
                    sec.VerdictTone = Tone.Good;
                }
            }

            t.Sections.Add(sec);
            t.HasData = true;
        }

        // ---- 多服务器对比 ----
        if (s.Hijack != null && s.Hijack.Comparisons.Count > 0)
        {
            var sec = new Section { Title = "多服务器解析对比", Hint = "同一域名在不同 DNS 下的结果" };
            foreach (var cmp in s.Hijack.Comparisons)
            {
                var parts = cmp.Entries.Select(e =>
                    e.Ok && e.Addresses.Count > 0
                        ? $"{e.Server} → {string.Join("/", e.Addresses)}"
                        : $"{e.Server} → {(e.Error.Length > 0 ? e.Error : "无结果")}");
                sec.Kv.Add(new KeyValueRow
                {
                    Key = cmp.Domain,
                    Value = cmp.AllAgree ? "一致" : "不一致",
                    Tone = cmp.AllAgree ? Tone.Good : Tone.Warn,
                    Note = string.Join("；", parts),
                });
            }
            sec.Notes.Add("CDN 会按就近原则返回不同地址，结果不一致不一定是故障。");
            t.Sections.Add(sec);
            t.HasData = true;
        }

        return t;
    }

    private static TabView TabNat(DiagState s)
    {
        var t = new TabView { Title = "NAT" };
        if (s.Nat == null) return t;

        var sec = new Section
        {
            Title = "NAT 类型判定",
            Hint = "复用同一个本地端口向多台 STUN 服务器探测",
            Verdict = s.Nat.Type,
            VerdictTone = NatTone(s.Nat.Type),
        };
        sec.Kv.Add(new KeyValueRow { Key = "本地 UDP 端口", Value = s.Nat.LocalPort.ToString() });
        sec.Kv.Add(new KeyValueRow
        {
            Key = "映射到的公网 IP",
            Value = s.Nat.MappedIps.Count == 0 ? "—" : string.Join("、", s.Nat.MappedIps),
            // 多出口时这几个地址往往落在不同城市，归属地一摆出来「为什么两个出口不一样」就有答案了
            Geo = s.Nat.MappedIps.Count == 1 ? IpGeo.Of(s, s.Nat.MappedIps[0]) : "",
            Tone = s.Nat.MappedIps.Count > 1 ? Tone.Warn : Tone.Neutral,
        });
        sec.Kv.Add(new KeyValueRow
        {
            Key = "映射到的外部端口",
            Value = s.Nat.MappedPorts.Count == 0 ? "—" : string.Join("、", s.Nat.MappedPorts),
            Tone = s.Nat.MappedPorts.Count > 1 ? Tone.Warn : Tone.Neutral,
        });
        sec.Kv.Add(new KeyValueRow
        {
            Key = "成功观测点数",
            Value = s.Nat.Observations.Count(o => o.Ok) + " / " + s.Nat.Observations.Count,
        });
        if (s.Nat.Detail.Length > 0) sec.Notes.Add(s.Nat.Detail);
        t.Sections.Add(sec);

        var obs = new Section { Title = "STUN 观测明细", Hint = "每个目标看到的地址" };
        foreach (var o in s.Nat.Observations)
            obs.Kv.Add(new KeyValueRow
            {
                Key = o.Server,
                Value = o.Ok ? $"{o.MappedIp}:{o.MappedPort}" : "失败",
                Tone = o.Ok ? Tone.Neutral : Tone.Warn,
                Note = o.Ok ? "" : o.Error,
            });
        if (obs.Kv.Count > 0) t.Sections.Add(obs);

        t.HasData = true;
        return t;
    }

    private static TabView TabPath(DiagState s)
    {
        var t = new TabView { Title = "路径" };

        if (s.Traceroute != null)
        {
            var sec = new Section
            {
                Title = "路由追踪",
                Hint = "ICMP 逐跳，目标 " + s.Traceroute.Target
                     + (s.Traceroute.TargetIp.Length > 0 ? " → " + s.Traceroute.TargetIp : ""),
            };
            foreach (var h in s.Traceroute.Hops)
                sec.Hops.Add(new HopRow
                {
                    Ttl = h.Ttl.ToString(),
                    Address = h.Timeout ? "*  *  *" : h.Address,
                    // 每一跳都标归属地，是这张表最有价值的一列：
                    // 「第几跳出境」一眼可见，而只看 IP 段是看不出来的。
                    Geo = h.Timeout ? "" : IpGeo.Of(s, h.Address),
                    MsText = h.Timeout ? "超时" : $"{h.Ms:F1} ms",
                    Status = h.Timeout ? "无响应" : h.Reached ? "到达目标" : "中间跳",
                    Tone = h.Timeout ? Tone.Neutral : h.Reached ? Tone.Good : Tone.Neutral,
                });

            if (!s.Traceroute.Reached && s.Traceroute.StoppedEarly)
                sec.Notes.Add("中途连续多跳无响应，通常是中间设备屏蔽了 ICMP，不代表链路断开。");
            if (s.Traceroute.Error.Length > 0) sec.Notes.Add(s.Traceroute.Error);

            t.Sections.Add(sec);
            t.HasData = true;
        }

        if (s.Mtu != null)
        {
            var sec = new Section
            {
                Title = "路径 MTU",
                Hint = "DF 位置位二分查找，目标 " + s.Mtu.Target,
                Verdict = s.Mtu.Ok ? "路径 MTU = " + s.Mtu.PathMtu : "探测未完成",
                VerdictTone = s.Mtu.Ok ? Thresholds.Mtu(s.Mtu.PathMtu) : Tone.Warn,
            };
            if (s.Mtu.Ok)
            {
                sec.Kv.Add(new KeyValueRow { Key = "最大可通过负载", Value = s.Mtu.MaxPayload + " 字节" });
                sec.Kv.Add(new KeyValueRow { Key = "路径 MTU", Value = s.Mtu.PathMtu.ToString() });
                sec.Kv.Add(new KeyValueRow
                {
                    Key = "下一档测试",
                    Value = s.Mtu.NextPayloadFails ? "失败（已到边界）" : "未测试",
                    Tone = Tone.Neutral,
                });
                if (s.Mtu.Assessment.Length > 0) sec.Notes.Add(s.Mtu.Assessment);
            }
            else if (s.Mtu.Error.Length > 0)
            {
                sec.Notes.Add(s.Mtu.Error);
            }
            t.Sections.Add(sec);
            t.HasData = true;
        }

        return t;
    }

    private static TabView TabPort(DiagState s)
    {
        var t = new TabView { Title = "端口" };
        if (s.PortScan == null) return t;

        var sec = new Section
        {
            Title = "端口状态",
            Hint = "目标 " + s.PortScan.Host,
            Verdict = $"开放 {s.PortScan.OpenCount} 个 · 关闭/过滤 {s.PortScan.ClosedCount} 个",
            // 中性色：这些端口是用户自己指定要看的，「开放」是一个事实而不是问题。
            // 之前给橙色，但问题清单里并没有对应条目，属于自相矛盾的信号。
            VerdictTone = Tone.Neutral,
        };
        // 扫 100 个端口时把关闭的一并铺出来会占好几屏，而关闭端口几乎没有诊断价值。
        // 开放的一律显示，关闭的最多铺 24 个，其余折成一行说明。
        const int ClosedLimit = 24;
        var all = s.PortScan.Results.OrderBy(r => r.Port).ToList();
        var openPorts = all.Where(r => r.Open).ToList();
        var closedPorts = all.Where(r => !r.Open).ToList();
        bool folded = closedPorts.Count > ClosedLimit;

        var shown = folded
            ? openPorts.Concat(closedPorts.Take(ClosedLimit)).OrderBy(r => r.Port).ToList()
            : all;

        foreach (var r in shown)
            sec.Ports.Add(new PortCell
            {
                Port = r.Port,
                Service = r.Service.Length > 0 ? r.Service : "—",
                Open = r.Open,
                MsText = r.Open ? $"{r.Ms:F1} ms" : (r.Error.Length > 0 ? r.Error : "关闭"),
                Banner = r.Banner,
                Tone = r.Open ? Tone.Good : Tone.Neutral,
                Tooltip = $"端口 {r.Port}"
                        + (r.Service.Length > 0 ? $"（{r.Service}）" : "")
                        + (r.Open ? $"\n开放，握手 {r.Ms:F1} ms" : $"\n{r.Error}")
                        + (r.Banner.Length > 0 ? "\nBanner: " + r.Banner : ""),
            });

        if (folded)
            sec.Notes.Add($"另有 {closedPorts.Count - ClosedLimit} 个端口未开放，已折叠（关闭或过滤状态，无诊断价值）。");

        // 只在真的出现这两种结果时才解释，否则是噪音
        bool hasTimeout = closedPorts.Any(r => r.Error.Contains("超时"));
        bool hasRefused = closedPorts.Any(r => r.Error.Contains("拒绝"));
        if (hasTimeout && hasRefused)
            sec.Notes.Add("「超时」表示被防火墙静默丢弃，「连接被拒绝」表示主机可达但该端口没有服务监听。");
        else if (hasTimeout)
            sec.Notes.Add("未开放端口全部返回「超时」，说明有防火墙在静默丢弃探测包（而不是明确拒绝）。");
        else if (hasRefused)
            sec.Notes.Add("未开放端口全部返回「连接被拒绝」，说明主机可达、只是该端口没有服务监听。");
        t.Sections.Add(sec);
        t.HasData = true;

        var open = s.PortScan.Results.Where(r => r.Open).ToList();
        if (open.Count > 0)
        {
            var detail = new Section { Title = "开放端口明细", Hint = "从外部看这些服务是可达的" };
            foreach (var r in open)
                detail.Kv.Add(new KeyValueRow
                {
                    Key = r.Port + (r.Service.Length > 0 ? "  " + r.Service : ""),
                    Value = $"{r.Ms:F1} ms",
                    Tone = Tone.Neutral,
                    Note = r.Banner,
                });
            detail.Notes.Add("对外开放的端口越多，攻击面越大。请确认这些服务确实需要暴露。");
            t.Sections.Add(detail);
        }

        return t;
    }
}
