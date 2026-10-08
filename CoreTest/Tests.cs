using System.Collections;
using NetCheck.Core;
using NetCheck.Ui;

namespace NetCheck.Tests;

/// <summary>
/// 极简断言器。不引第三方测试框架 —— 这个项目要能在一个只有 .NET SDK 的机器上
/// 直接 <c>dotnet run -- unit</c> 跑起来，不依赖 NuGet 网络。
/// 失败不抛异常而是累计，这样一次就能看到全部问题。
/// </summary>
internal static class T
{
    public static int Passed;
    public static int Failed;
    public static readonly List<string> Failures = new();

    private static string _group = "";

    public static void Group(string name) => _group = name;

    private static void Check(bool ok, string what, string detail = "")
    {
        if (ok) { Passed++; return; }
        Failed++;
        Failures.Add($"[{_group}] {what}" + (detail.Length > 0 ? "   → " + detail : ""));
    }

    public static void True(bool cond, string what) => Check(cond, what);
    public static void False(bool cond, string what) => Check(!cond, what);
    public static void NotNull(object? o, string what) => Check(o != null, what, "实际是 null");

    public static void Eq<T>(T actual, T expected, string what)
        => Check(EqualityComparer<T>.Default.Equals(actual, expected), what,
                 $"期望 {Fmt(expected)}，实际 {Fmt(actual)}");

    public static void Near(double actual, double expected, double tol, string what)
        => Check(Math.Abs(actual - expected) <= tol, what,
                 $"期望 {expected:F4} ± {tol}，实际 {actual:F4}");

    public static void Ports(int[] actual, int[] expected, string what)
        => Check(actual.SequenceEqual(expected), what,
                 $"期望 [{string.Join(",", expected)}]，实际 [{string.Join(",", actual)}]");

    public static void NoThrow(Action act, string what)
    {
        try { act(); Check(true, what); }
        catch (Exception ex) { Check(false, what, ex.GetType().Name + ": " + ex.Message); }
    }

    private static string Fmt(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(Fmt)) + "]",
        _ => v.ToString() ?? "",
    };
}

internal static class Tests
{
    public static void RunAll()
    {
        ParsePorts();
        ServiceNames();
        Presets();
        Jitter();
        LatencyCategories();
        NatClassify();
        BarScale();
        Settings();
        CommandLine();
        ReportViewCore();
        IssueRules();
        ThresholdConsistency();
        CardCatalogChecks();
        ProxyMerging();
        PortFolding();
        PublicIpDedup();
        Masking();
        IpGeoChecks();
        GeoInViews();
        Ipv6Checks();
        Ipv6View();
        QualitySummary();
        WebProbeChecks();
        WebReportChecks();
        WebView();
        DnsCompareView();
        HijackVerdict();
        TextReport();
    }

    // ==================================================================
    // 1. 端口表达式解析
    // ==================================================================

    private static void ParsePorts()
    {
        T.Group("PortCheck.ParsePorts");

        int[] P(string spec, out string err)
        {
            var (p, e) = PortCheck.ParsePorts(spec);
            err = e;
            return p;
        }

        T.Ports(P("80", out var e0), new[] { 80 }, "单个端口");
        T.Eq(e0, "", "单个端口不报错");

        T.Ports(P("443,80", out _), new[] { 80, 443 }, "逗号分隔并按数字升序");
        T.Ports(P("443 80", out _), new[] { 80, 443 }, "空格分隔");
        T.Ports(P("443;80", out _), new[] { 80, 443 }, "分号分隔");
        T.Ports(P("443，80", out _), new[] { 80, 443 }, "中文逗号分隔");
        T.Ports(P("443；80", out _), new[] { 80, 443 }, "中文分号分隔");
        T.Ports(P("  22 , 80  ", out _), new[] { 22, 80 }, "片段两侧空白被裁掉");
        T.Ports(P("80,,443", out _), new[] { 80, 443 }, "中间的空片段被跳过");
        T.Ports(P("80,80,443,443", out _), new[] { 80, 443 }, "重复端口去重");

        var range = P("8000-8010", out var e1);
        T.Eq(range.Length, 11, "范围展开成 11 个端口");
        T.Eq(range[0], 8000, "范围起点");
        T.Eq(range[^1], 8010, "范围终点");
        T.Eq(e1, "", "范围写法不报错");

        var reversed = P("8010-8000", out _);
        T.Eq(reversed.Length, 11, "倒序范围自动翻转");
        T.Eq(reversed[0], 8000, "倒序范围的起点仍是最小值");

        T.Eq(P("1-1024", out _).Length, 1024, "1-1024 全量展开");
        // 22,80,443,3389,8080 = 5 个 + 8000-8003 = 4 个
        T.Eq(P("22,80,443,3389,8080,8000-8003", out _).Length, 9, "混合写法：5 个单端口 + 4 个范围端口");
        T.Ports(P("65535", out _), new[] { 65535 }, "端口 65535 合法");

        T.Eq(P("", out var e2).Length, 0, "空串返回空数组");
        T.True(e2.Length > 0, "空串报错");
        T.Eq(P("   ", out var e3).Length, 0, "纯空白返回空数组");
        T.True(e3.Length > 0, "纯空白报错");

        T.Eq(P("abc", out var e4).Length, 0, "字母返回空数组");
        T.True(e4.Length > 0, "字母报错");
        T.Eq(P("80,abc", out var e5).Length, 0, "混有非法片段时整体失败");
        T.True(e5.Length > 0, "混有非法片段时报错");

        T.Eq(P("0", out var e6).Length, 0, "端口 0 越界，返回空数组");
        T.True(e6.Length > 0, "端口 0 报错");
        T.Eq(P("65536", out var e7).Length, 0, "端口 65536 越界，返回空数组");
        T.True(e7.Length > 0, "端口 65536 报错");
        T.Eq(P("-1", out _).Length, 0, "负数端口不合法");

        T.Eq(P("1-20000", out var e8).Length, 0, "超过 5000 个的范围被拒绝");
        T.True(e8.Contains("5000"), "超范围提示里说明了上限");
        T.Eq(P("8000-", out var e9).Length, 0, "残缺范围返回空");
        T.True(e9.Length > 0, "残缺范围报错");
        T.Eq(P("-8000", out var e10).Length, 0, "缺起点的范围返回空");
        T.True(e10.Length > 0, "缺起点的范围报错");

        // 范围里的越界端口应该被静默丢掉，而不是整体失败
        var clipped = P("65530-65540", out var e11);
        T.Eq(e11, "", "范围端点轻微越界不算错误");
        T.Eq(clipped.Length, 6, "越界部分被裁掉，只留 65530-65535");
        T.Eq(clipped[^1], 65535, "裁剪后最后一个端口是 65535");
    }

    private static void ServiceNames()
    {
        T.Group("PortCheck.ServiceName");
        T.Eq(PortCheck.ServiceName(21), "FTP", "21 → FTP");
        T.Eq(PortCheck.ServiceName(22), "SSH", "22 → SSH");
        T.Eq(PortCheck.ServiceName(443), "HTTPS", "443 → HTTPS");
        T.Eq(PortCheck.ServiceName(3389), "RDP", "3389 → RDP");
        T.Eq(PortCheck.ServiceName(6379), "Redis", "6379 → Redis");
        T.Eq(PortCheck.ServiceName(27017), "MongoDB", "27017 → MongoDB");
        T.Eq(PortCheck.ServiceName(12345), "", "未知端口返回空串");
        T.Eq(PortCheck.ServiceName(0), "", "端口 0 返回空串");
    }

    // ==================================================================
    // 2. 端口预设
    // ==================================================================

    private static void Presets()
    {
        T.Group("PortPresets");

        T.Eq(PortPresets.MatchName("22,80,443,3389,8080"), "常用服务", "顺序一致时命中常用服务");
        T.Eq(PortPresets.MatchName("8080,3389,443,80,22"), "常用服务", "顺序打乱后仍命中同一预设");
        T.Eq(PortPresets.MatchName(" 22 , 80 , 443 , 3389 , 8080 "), "常用服务", "带空白与多余分隔符仍命中");
        T.Eq(PortPresets.MatchName("1-1024"), "1-1024 全量", "范围写法命中全量预设");
        T.Eq(PortPresets.MatchName(""), "自定义", "空串归到自定义");
        T.Eq(PortPresets.MatchName("   "), "自定义", "纯空白归到自定义");
        T.Eq(PortPresets.MatchName(null), "自定义", "null 归到自定义");
        T.Eq(PortPresets.MatchName("1234"), "自定义", "不匹配任何预设时归到自定义");
        T.Eq(PortPresets.MatchName("abc"), "自定义", "非法表达式归到自定义");

        T.Eq(PortPresets.ByName("数据库").Name, "数据库", "按名字查预设");
        T.Eq(PortPresets.ByName("不存在的预设").Name, "自定义", "查不到时回退自定义");
        T.Eq(PortPresets.ByName(null).Name, "自定义", "null 回退自定义");
        T.Eq(PortPresets.ByName(PortPresets.CustomName).Spec, "", "自定义预设的 Spec 是空串");

        T.Eq(PortPresets.Names.Length, PortPresets.All.Length, "名字数量与预设数量一致");
        T.Eq(PortPresets.Names.Distinct().Count(), PortPresets.Names.Length, "预设名不重复");
        T.Eq(PortPresets.All[^1].Name, PortPresets.CustomName, "最后一项固定是自定义");
        T.True(PortPresets.All.Length >= 8, "至少提供 8 组预设");

        // 每个具名预设的表达式都要能解析，并且能反查回自己
        foreach (var preset in PortPresets.All.Where(x => x.Spec.Length > 0))
        {
            var (ports, err) = PortCheck.ParsePorts(preset.Spec);
            T.Eq(err, "", $"预设「{preset.Name}」的表达式可解析");
            T.True(ports.Length > 0, $"预设「{preset.Name}」解析出了端口");
            T.Eq(PortPresets.MatchName(preset.Spec), preset.Name, $"预设「{preset.Name}」能反查回自己");
            T.True(preset.Hint.Length > 0, $"预设「{preset.Name}」带了说明文字");
        }

        // 预设之间不能撞车，否则反查会拿到错误的名字
        var specs = PortPresets.All
            .Where(x => x.Spec.Length > 0)
            .Select(x => string.Join(",", PortCheck.ParsePorts(x.Spec).Ports))
            .ToList();
        T.Eq(specs.Distinct().Count(), specs.Count, "没有两个预设的端口集合完全相同");
    }

    // ==================================================================
    // 3. 抖动计算
    // ==================================================================

    private static void Jitter()
    {
        T.Group("LatencyReport.Jitter");

        T.Near(LatencyReport.Jitter(Array.Empty<double>()), 0, 1e-9, "空样本抖动为 0");
        T.Near(LatencyReport.Jitter(new[] { 5.0 }), 0, 1e-9, "单样本抖动为 0");
        T.Near(LatencyReport.Jitter(new[] { 10.0, 10, 10, 10 }), 0, 1e-9, "完全稳定时抖动为 0");
        T.Near(LatencyReport.Jitter(new[] { 10.0, 12, 10 }), 2, 1e-9, "上下各波动 2，平均后为 2");
        T.Near(LatencyReport.Jitter(new[] { 0.0, 10 }), 10, 1e-9, "两点之差就是抖动");
        T.Near(LatencyReport.Jitter(new[] { 10.0, 30, 50 }), 20, 1e-9, "单调递增时取平均步长");
        T.Near(LatencyReport.Jitter(new[] { 50.0, 30, 10 }), 20, 1e-9, "单调递减同理");
        T.Near(LatencyReport.Jitter(new[] { 1.0, 100 }), 99, 1e-9, "大跳变");
        // 抖动只关心相邻差，与整体偏移无关
        T.Near(LatencyReport.Jitter(new[] { 1000.0, 1002, 1000 }), 2, 1e-9, "整体偏移不影响抖动");
    }

    // ==================================================================
    // 3.5 延迟目标的分类
    // ==================================================================

    private static void LatencyCategories()
    {
        T.Group("LatencyProbe 分类");

        T.Eq(LatencyProbe.Categories.Select(c => c.Name).Distinct().Count(),
             LatencyProbe.Categories.Length, "分类名没有重复");
        T.True(LatencyProbe.Categories.Length >= 3, "至少覆盖三类用途");

        foreach (var c in LatencyProbe.Categories)
        {
            T.True(c.GoodMs > 0 && c.GoodMs < c.WarnMs, $"{c.Name} 阈值有序（良好 < 可用）");
            T.True(c.Hint.Length > 0, $"{c.Name} 有用途说明");
        }

        T.Eq(LatencyProbe.CategoryOf("游戏").Name, "游戏", "按名字取分类");
        T.Eq(LatencyProbe.CategoryOf("不存在的分类").Name, LatencyProbe.Categories[0].Name,
             "认不出的分类退回第一类，不抛异常");
        T.Eq(LatencyProbe.CategoryOf(null).Name, LatencyProbe.Categories[0].Name, "null 也退回第一类");
        T.Eq(LatencyProbe.CategoryOf("").Name, LatencyProbe.Categories[0].Name, "空串也退回第一类");

        // 游戏对延迟最敏感，阈值必须最严；否则「能打游戏」和「能刷网页」会被判成同一档
        var game = LatencyProbe.CategoryOf("游戏");
        var web = LatencyProbe.CategoryOf("网页浏览");
        T.True(game.GoodMs < web.GoodMs, "游戏的「良好」阈值比网页更严");
        T.True(game.WarnMs < web.WarnMs, "游戏的「可用」阈值比网页更严");

        foreach (var (cat, name, host, ports) in LatencyProbe.Targets)
        {
            T.True(LatencyProbe.Categories.Any(c => c.Name == cat),
                   $"目标「{name}」的分类「{cat}」是已声明的");
            T.True(host.Length > 0, $"目标「{name}」有主机名");
            T.True(ports.Length > 0, $"目标「{name}」有候选端口");
        }
        T.Eq(LatencyProbe.Targets.Select(t => t.Name).Distinct().Count(),
             LatencyProbe.Targets.Length, "目标显示名没有重复");
        T.Eq(LatencyProbe.Targets.Select(t => t.Host).Distinct().Count(),
             LatencyProbe.Targets.Length, "目标主机没有重复");

        foreach (var c in LatencyProbe.Categories)
            T.True(LatencyProbe.Targets.Any(t => t.Category == c.Name),
                   $"分类「{c.Name}」至少有一个探测目标，不能是空分类");

        // 这条是需求本身：延迟测的应该是用户实际会用的服务，不是 DNS 服务器。
        // DNS 服务器的响应速度在「DNS」那张卡片里已经单独测过了，放这里纯属重复。
        // DNS 服务器一律是裸 IP，服务一律是域名 —— 用这个把回归挡住。
        foreach (var (_, name, host, _) in LatencyProbe.Targets)
            T.False(System.Net.IPAddress.TryParse(host, out _),
                    $"目标「{name}」（{host}）应该是域名而不是裸 IP —— 延迟测服务，不测 DNS 服务器");
    }

    // ==================================================================
    // 4. 条形图量程
    // ==================================================================

    /// <summary>
    /// STUN 服务器列表与 NAT 类型判定。
    ///
    /// 判定逻辑是纯函数，而真实网络下凑不齐「只有一台服务器回应」这类场景，
    /// 所以直接喂构造好的观测结果，把每个分支都走一遍。
    /// </summary>
    private static void NatClassify()
    {
        // ---- 服务器列表 ----
        var servers = StunAndNat.Servers;
        T.True(servers.Length >= 3,
            "至少 3 台 STUN 服务器 —— 判定 NAT 类型需要 ≥2 个成功样本，得留冗余");
        T.Eq(servers.Select(s => s.Host + ":" + s.Port).Distinct().Count(), servers.Length,
            "STUN 服务器没有重复");

        foreach (var (host, port) in servers)
        {
            T.True(host.Length > 0, "服务器主机名非空");
            T.True(port > 0 && port <= 65535, $"{host} 的端口 {port} 合法");
        }

        // 这台工具主要给国内用户用，判定不能押在境外服务器上：
        // 谷歌的 STUN 在境内基本连不上，cloudflare 时通时不通。
        T.False(servers.Any(s => s.Host.Contains("google", StringComparison.OrdinalIgnoreCase)),
            "列表里不能有谷歌的 STUN 服务器（境内基本不可达）");
        T.True(servers.Count(s => s.Host.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)) <= 1,
            "境外服务器最多留 1 台作补充，不能当主力");

        var domestic = servers.Count(s =>
            !s.Host.Contains("google", StringComparison.OrdinalIgnoreCase) &&
            !s.Host.Contains("cloudflare", StringComparison.OrdinalIgnoreCase));
        T.True(domestic >= 3, $"至少 3 台服务器不依赖境外链路（实际 {domestic} 台）");

        // ---- 判定：一台都没通 ----
        var none = new NatReport();
        none.Observations.Add(new StunObservation { Server = "a:3478", Error = "超时" });
        StunAndNat.Classify(none, new[] { "192.168.1.5" });
        T.Eq(none.Type, "未知", "一台都没通 → 未知");
        T.Eq(none.MappedIps.Count, 0, "未知时不产生映射 IP");
        T.Eq(none.MappedPorts.Count, 0, "未知时不产生映射端口");

        // ---- 判定：只有一台回应，必须老实说测不出来 ----
        var one = new NatReport();
        one.Observations.Add(new StunObservation { Server = "a:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        StunAndNat.Classify(one, new[] { "192.168.1.5" });
        T.Eq(one.Type, "无法判定", "只有 1 台回应 → 无法判定，不能猜");
        T.True(one.Detail.Contains("至少两台"), "详情要说明为什么测不出来");
        // 本次改动的核心回归点：单样本 + 唯一映射端口，早先会掉进
        // 「MappedPorts.Count == 1 → 圆锥型」那个分支，
        // 等于把「没测出来」当成「测出来是好的」，还给了个绿灯。
        T.False(one.Type.Contains("圆锥"), "单样本不能被当成圆锥型 NAT");

        // 失败的那些不能算进样本
        var mixed = new NatReport();
        mixed.Observations.Add(new StunObservation { Server = "a:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        mixed.Observations.Add(new StunObservation { Server = "b:3478", Error = "超时" });
        mixed.Observations.Add(new StunObservation { Server = "c:3478", Error = "超时" });
        StunAndNat.Classify(mixed, new[] { "192.168.1.5" });
        T.Eq(mixed.Type, "无法判定", "3 台里只成功 1 台，仍然算样本不足");

        // ---- 判定：两台都通、端口一致 → 圆锥型 ----
        var cone = new NatReport();
        cone.Observations.Add(new StunObservation { Server = "a:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        cone.Observations.Add(new StunObservation { Server = "b:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        StunAndNat.Classify(cone, new[] { "192.168.1.5" });
        T.Eq(cone.Type, "圆锥型 NAT（Cone）", "两台都通且映射端口一致 → 圆锥型");
        T.Eq(cone.MappedPorts.Count, 1, "圆锥型只有一个映射端口");

        // ---- 判定：端口不同 → 对称型 ----
        var sym = new NatReport();
        sym.Observations.Add(new StunObservation { Server = "a:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        sym.Observations.Add(new StunObservation { Server = "b:3478", MappedIp = "1.2.3.4", MappedPort = 5001 });
        StunAndNat.Classify(sym, new[] { "192.168.1.5" });
        T.Eq(sym.Type, "对称型 NAT（Symmetric）", "两台映射端口不同 → 对称型");

        // ---- 判定：出口 IP 不同 → 多出口（优先级高于端口比较）----
        var multi = new NatReport();
        multi.Observations.Add(new StunObservation { Server = "a:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        multi.Observations.Add(new StunObservation { Server = "b:3478", MappedIp = "5.6.7.8", MappedPort = 5000 });
        StunAndNat.Classify(multi, new[] { "192.168.1.5" });
        T.Eq(multi.Type, "多出口 / 对称型 NAT", "不同目标看到不同公网 IP → 多出口");
        T.Eq(multi.MappedIps.Count, 2, "多出口时记下两个出口 IP");

        // ---- 判定：映射 IP 就是本机 IP → 无 NAT ----
        var pub = new NatReport();
        pub.Observations.Add(new StunObservation { Server = "a:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        pub.Observations.Add(new StunObservation { Server = "b:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        StunAndNat.Classify(pub, new[] { "1.2.3.4" });
        T.Eq(pub.Type, "公网直连（无 NAT）", "映射 IP 就是本机 IP → 无 NAT");

        // 本机 IP 在列表里，但两个目标看到的不一样 —— 多出口优先
        var pubMulti = new NatReport();
        pubMulti.Observations.Add(new StunObservation { Server = "a:3478", MappedIp = "1.2.3.4", MappedPort = 5000 });
        pubMulti.Observations.Add(new StunObservation { Server = "b:3478", MappedIp = "5.6.7.8", MappedPort = 5000 });
        StunAndNat.Classify(pubMulti, new[] { "1.2.3.4" });
        T.Eq(pubMulti.Type, "多出口 / 对称型 NAT", "多出口的判断优先于「无 NAT」");
    }

    private static void BarScale()
    {
        T.Group("BarRow.Scale");
        const double W = BarRow.TrackWidth;

        T.Near(BarRow.Scale(0, 100), 2, 1e-9, "0 值给最小可见宽度");
        T.Near(BarRow.Scale(-5, 100), 2, 1e-9, "负值给最小可见宽度");
        T.Near(BarRow.Scale(50, 100), 50 / 110.0 * W, 1e-6, "半量程按比例线性");
        T.Near(BarRow.Scale(100, 100), 100 / 110.0 * W, 1e-6, "满量程留出 10% 余量，不铺满轨道");
        T.Near(BarRow.Scale(200, 100), W, 1e-9, "超量程被裁到轨道宽");
        T.Near(BarRow.Scale(100, 0), W, 1e-9, "最大值为 0 时也不会除零");
        T.Near(BarRow.Scale(0.001, 100), 2, 1e-9, "极小值不会小于最小宽度");

        T.False(BarRow.UsesSqrtScale(100, 10), "跨度 10 倍不用平方根压缩");
        T.False(BarRow.UsesSqrtScale(100, 5), "跨度 20 倍是边界，不启用");
        T.True(BarRow.UsesSqrtScale(100, 4), "跨度 25 倍启用平方根压缩");
        T.False(BarRow.UsesSqrtScale(100, 0), "没有最小值时不启用");
        T.False(BarRow.UsesSqrtScale(0, 0), "全 0 数据不启用");

        // 平方根压缩的意义：跨度 100 倍时，最小的那根仍要看得见。
        // 线性量程下 1/110 只有 1.36 px，会被 Math.Max 兜到 2 px —— 也就是一条线。
        double tinyLinear = BarRow.Scale(1, 100);
        double tinySqrt = BarRow.Scale(1, 100, 1);
        T.Near(tinyLinear, 2, 1e-9, "线性量程下 1 ms 被兜到 2 px（一条线）");
        T.True(tinySqrt > 10, "平方根压缩下 1 ms 至少 10 px，肉眼可见");
        T.True(tinySqrt > tinyLinear * 5, "平方根压缩把最小条的可见度提高了一个量级");

        // 单调性必须保持，否则排序会看起来是错的
        T.True(BarRow.Scale(100, 100, 1) > BarRow.Scale(50, 100, 1), "平方根压缩保持单调（高段）");
        T.True(BarRow.Scale(50, 100, 1) > BarRow.Scale(10, 100, 1), "平方根压缩保持单调（中段）");
        T.True(BarRow.Scale(10, 100, 1) > BarRow.Scale(1, 100, 1), "平方根压缩保持单调（低段）");
        T.True(BarRow.Scale(100, 100, 1) <= W + 1e-9, "平方根压缩不超轨道宽");
    }

    // ==================================================================
    // 5. 配置持久化
    // ==================================================================

    private static void Settings()
    {
        T.Group("AppSettings");

        var d = new AppSettings();
        T.Eq(d.PortConcurrency, 64, "默认并发 64");
        T.Eq(d.PortTimeoutMs, 1500, "默认端口超时 1500 ms");
        T.Eq(d.LatencyRounds, 4, "默认探测次数 4");
        T.Eq(d.TraceMaxHops, 30, "默认最大跳数 30");
        T.Eq(d.SpeedSeconds, 10, "默认测速时长 10 秒");
        // 默认端口必须和默认目标主机（223.5.5.5，一台公共 DNS）配套：
        // 53 / 80 / 443 在它上面确实是开放的，22 / 3389 / 8080 一定关闭。
        // 以前默认给「常用服务」那串，用户第一次点端口检测就看到两个「关闭」。
        T.Eq(d.PortSpec, PortPresets.DefaultSpec, "默认端口表达式取自 PortPresets.DefaultSpec");
        T.Eq(d.PortSpec, "53,80,443", "默认端口表达式与默认目标配套");
        T.Eq(d.PortPreset, PortPresets.DefaultName, "默认预设名取自 PortPresets.DefaultName");
        T.Eq(d.TargetHost, "223.5.5.5", "默认目标主机");
        T.Eq(d.CollapsedCards.Length, 0, "默认没有折叠任何卡片");
        T.False(d.WindowMaximized, "默认不最大化");

        // ---- 越界收敛 ----
        T.Eq(new AppSettings { PortConcurrency = 99999 }.Clamp().PortConcurrency, 512, "并发上限 512");
        T.Eq(new AppSettings { PortConcurrency = 0 }.Clamp().PortConcurrency, 1, "并发下限 1");
        T.Eq(new AppSettings { PortConcurrency = -5 }.Clamp().PortConcurrency, 1, "负并发收敛到 1");
        T.Eq(new AppSettings { PortTimeoutMs = 10 }.Clamp().PortTimeoutMs, 100, "端口超时下限 100");
        T.Eq(new AppSettings { PortTimeoutMs = 999999 }.Clamp().PortTimeoutMs, 15000, "端口超时上限 15000");
        T.Eq(new AppSettings { LatencyRounds = 0 }.Clamp().LatencyRounds, 1, "探测次数下限 1");
        T.Eq(new AppSettings { LatencyRounds = 999 }.Clamp().LatencyRounds, 20, "探测次数上限 20");
        T.Eq(new AppSettings { TraceMaxHops = 0 }.Clamp().TraceMaxHops, 1, "最大跳数下限 1");
        T.Eq(new AppSettings { TraceMaxHops = 999 }.Clamp().TraceMaxHops, 64, "最大跳数上限 64");
        T.Eq(new AppSettings { TraceTimeoutMs = 10 }.Clamp().TraceTimeoutMs, 200, "追踪超时下限 200");
        T.Eq(new AppSettings { TraceTimeoutMs = 99999 }.Clamp().TraceTimeoutMs, 5000, "追踪超时上限 5000");
        T.Eq(new AppSettings { SpeedSeconds = 1 }.Clamp().SpeedSeconds, 3, "测速时长下限 3");
        T.Eq(new AppSettings { SpeedSeconds = 9999 }.Clamp().SpeedSeconds, 60, "测速时长上限 60");
        T.Eq(new AppSettings { DnsTimeoutMs = 1 }.Clamp().DnsTimeoutMs, 300, "DNS 超时下限 300");
        T.Eq(new AppSettings { DnsTimeoutMs = 99999 }.Clamp().DnsTimeoutMs, 10000, "DNS 超时上限 10000");
        // 折叠卡片列表是手改配置文件时最容易写坏的字段，收敛规则要逐条盯住
        T.Eq(new AppSettings { CollapsedCards = null! }.Clamp().CollapsedCards.Length, 0,
            "折叠列表为 null 时收敛成空数组");
        T.Eq(new AppSettings { CollapsedCards = new[] { "port", "port", "dns" } }.Clamp().CollapsedCards.Length, 2,
            "折叠列表去重");
        T.Eq(new AppSettings { CollapsedCards = new[] { "port", "", "   " } }.Clamp().CollapsedCards.Length, 1,
            "折叠列表丢掉空白项");
        T.Eq(new AppSettings { CollapsedCards = new[] { "  port  " } }.Clamp().CollapsedCards[0], "port",
            "折叠项两侧空白被裁掉");
        T.Eq(new AppSettings { CollapsedCards = new[] { "PORT", "port" } }.Clamp().CollapsedCards.Length, 1,
            "折叠列表去重忽略大小写");
        T.Eq(new AppSettings { CollapsedCards = new[] { "dns", "port" } }.Clamp().CollapsedCards[0], "dns",
            "折叠列表保持原有顺序");
        T.Eq(new AppSettings { WindowWidth = 100 }.Clamp().WindowWidth, 940, "窗口宽度下限 940");
        T.Eq(new AppSettings { WindowWidth = 99999 }.Clamp().WindowWidth, 4000, "窗口宽度上限 4000");
        // 下限必须和 MainWindow.xaml 的 MinHeight 一致（620）。
        // 以前这里是 640、XAML 是 620：手改配置成 630 会被静默拉到 640，而用户明明能手动拉到 620。
        T.Eq(new AppSettings { WindowHeight = 100 }.Clamp().WindowHeight, 620, "窗口高度下限 620（与 XAML MinHeight 一致）");
        T.Eq(new AppSettings { WindowHeight = 99999 }.Clamp().WindowHeight, 3000, "窗口高度上限 3000");

        // ---- 文本字段兜底 ----
        T.Eq(new AppSettings { TargetHost = "   " }.Clamp().TargetHost, "223.5.5.5", "空白目标主机回退默认");
        T.Eq(new AppSettings { TargetHost = null! }.Clamp().TargetHost, "223.5.5.5", "null 目标主机回退默认");
        T.Eq(new AppSettings { TargetHost = "  8.8.8.8  " }.Clamp().TargetHost, "8.8.8.8", "目标主机两侧空白被裁掉");
        T.Eq(new AppSettings { PortSpec = "" }.Clamp().PortSpec, PortPresets.DefaultSpec, "空端口表达式回退默认");
        T.Eq(new AppSettings { PortSpec = null! }.Clamp().PortSpec, PortPresets.DefaultSpec, "null 端口表达式回退默认");
        T.Eq(new AppSettings { PortPreset = "  " }.Clamp().PortPreset, PortPresets.DefaultName, "空预设名回退默认");

        // ---- 克隆是独立副本 ----
        var orig = new AppSettings { PortConcurrency = 100, TargetHost = "1.1.1.1" };
        var copy = orig.Clone();
        copy.PortConcurrency = 7;
        copy.TargetHost = "2.2.2.2";
        T.Eq(orig.PortConcurrency, 100, "Clone 后改副本不影响原件（并发）");
        T.Eq(orig.TargetHost, "1.1.1.1", "Clone 后改副本不影响原件（主机）");

        // ---- 落盘 / 读回往返 ----
        var dir = Path.Combine(Path.GetTempPath(), "netcheck_test_" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "settings.json");
        try
        {
            var src = new AppSettings
            {
                TargetHost = "1.1.1.1",
                PortSpec = "1-1024",
                PortPreset = "1-1024 全量",
                PortConcurrency = 200,
                PortTimeoutMs = 3000,
                LatencyRounds = 7,
                TraceMaxHops = 12,
                TraceTimeoutMs = 900,
                SpeedSeconds = 25,
                DnsTimeoutMs = 800,
                CollapsedCards = new[] { "options", "port" },
                WindowWidth = 1500,
                WindowHeight = 950,
                WindowMaximized = true,
            };
            src.Save(file);
            T.True(File.Exists(file), "Save 写出了文件");

            var back = AppSettings.Load(file);
            T.Eq(back.TargetHost, "1.1.1.1", "往返：目标主机");
            T.Eq(back.PortSpec, "1-1024", "往返：端口表达式");
            T.Eq(back.PortPreset, "1-1024 全量", "往返：预设名");
            T.Eq(back.PortConcurrency, 200, "往返：并发");
            T.Eq(back.PortTimeoutMs, 3000, "往返：端口超时");
            T.Eq(back.LatencyRounds, 7, "往返：探测次数");
            T.Eq(back.TraceMaxHops, 12, "往返：最大跳数");
            T.Eq(back.TraceTimeoutMs, 900, "往返：追踪超时");
            T.Eq(back.SpeedSeconds, 25, "往返：测速时长");
            T.Eq(back.DnsTimeoutMs, 800, "往返：DNS 超时");
            T.Eq(back.CollapsedCards.Length, 2, "往返：折叠卡片数量");
            T.True(back.CollapsedCards.Contains("port"), "往返：折叠卡片内容");
            T.Eq(back.WindowWidth, 1500, "往返：窗口宽度");
            T.Eq(back.WindowHeight, 950, "往返：窗口高度");
            T.True(back.WindowMaximized, "往返：最大化状态");

            // ---- 坏配置不能让程序起不来 ----
            File.WriteAllText(file, "{ 这不是 JSON ");
            T.Eq(AppSettings.Load(file).PortConcurrency, 64, "损坏的 JSON 回退到默认值");
            File.WriteAllText(file, "");
            T.Eq(AppSettings.Load(file).PortConcurrency, 64, "空文件回退到默认值");
            File.WriteAllText(file, "null");
            T.NotNull(AppSettings.Load(file), "JSON 字面量 null 时返回默认对象而不是 null");
            File.WriteAllText(file, "[]");
            T.NotNull(AppSettings.Load(file), "JSON 类型不匹配时返回默认对象");

            // ---- 手改配置文件写入越界值也要被收敛 ----
            File.WriteAllText(file, "{\"PortConcurrency\": 99999, \"TargetHost\": \"\", \"SpeedSeconds\": 0}");
            var clamped = AppSettings.Load(file);
            T.Eq(clamped.PortConcurrency, 512, "读取时收敛越界并发");
            T.Eq(clamped.TargetHost, "223.5.5.5", "读取时补齐空目标主机");
            T.Eq(clamped.SpeedSeconds, 3, "读取时收敛越界测速时长");

            // ---- 只写部分字段时，其余用默认值而不是 0 ----
            File.WriteAllText(file, "{\"TargetHost\": \"9.9.9.9\"}");
            var partial = AppSettings.Load(file);
            T.Eq(partial.TargetHost, "9.9.9.9", "部分字段：读到了写入的项");
            T.Eq(partial.PortConcurrency, 64, "部分字段：未写入的项保持默认值");

            T.NotNull(AppSettings.Load(Path.Combine(dir, "nope.json")), "文件不存在时返回默认对象");
            T.Eq(AppSettings.Load(Path.Combine(dir, "nope.json")).PortTimeoutMs, 1500,
                  "文件不存在时默认值正确");

            // ---- 首次运行标志：只有「文件根本不存在」才算首次运行 ----
            // 界面靠它决定要不要把「高级」组默认收起来。判宽了会覆盖老用户的折叠偏好。
            T.True(AppSettings.Load(Path.Combine(dir, "nope.json")).FirstRun,
                   "文件不存在时 FirstRun = true");
            T.Eq(AppSettings.Load(Path.Combine(dir, "nope.json")).CollapsedCards.Length, 0,
                   "首次运行本身不带折叠状态（默认收起由界面按分组推导）");

            T.False(AppSettings.Load(file).FirstRun, "文件存在时 FirstRun = false");

            File.WriteAllText(file, "{ 坏掉了");
            T.False(AppSettings.Load(file).FirstRun,
                    "文件损坏不算首次运行 —— 不能趁机覆盖用户的折叠偏好");

            var freshPath = Path.Combine(dir, "fresh.json");
            new AppSettings().Save(freshPath);
            T.False(File.ReadAllText(freshPath).Contains("FirstRun"),
                    "FirstRun 不落盘（它是运行期标志，不是用户设置）");

            // 保存到不存在的目录应该自动创建
            var deep = Path.Combine(dir, "a", "b", "c.json");
            new AppSettings { PortConcurrency = 32 }.Save(deep);
            T.True(File.Exists(deep), "Save 会自动创建缺失的目录");
            T.Eq(AppSettings.Load(deep).PortConcurrency, 32, "深层目录也能读回");

            // 写到非法路径不应该抛异常
            T.NoThrow(() => new AppSettings().Save(Path.Combine(dir, "no\0way", "x.json")),
                      "写入非法路径时静默失败而不是抛异常");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败无所谓 */ }
        }

        T.True(AppSettings.SettingsPath.EndsWith("settings.json"), "配置文件名是 settings.json");
        T.True(AppSettings.SettingsDirectory.Contains("NetCheck"), "配置目录带应用名");
    }

    // ==================================================================
    // 6. 命令行参数
    // ==================================================================

    private static void CommandLine()
    {
        T.Group("CommandLineOptions");

        var o = CommandLineOptions.Parse(new[] { "--host", "8.8.8.8" });
        T.Eq(o.Host, "8.8.8.8", "--host 空格分隔");
        T.Eq(o.Errors.Count, 0, "--host 合法时没有错误");

        o = CommandLineOptions.Parse(new[] { "--host=8.8.8.8" });
        T.Eq(o.Host, "8.8.8.8", "--host=值 内联写法");

        o = CommandLineOptions.Parse(new[] { "--host= 1.1.1.1 " });
        T.Eq(o.Host, "1.1.1.1", "内联值两侧空白被裁掉");

        o = CommandLineOptions.Parse(new[] { "--ports", "22,80" });
        T.Eq(o.Ports, "22,80", "--ports 合法表达式");
        T.Eq(o.Errors.Count, 0, "--ports 合法时没有错误");

        o = CommandLineOptions.Parse(new[] { "--ports=8000-8010" });
        T.Eq(o.Ports, "8000-8010", "--ports 内联范围写法");

        o = CommandLineOptions.Parse(new[] { "--ports", "abc" });
        T.Eq(o.Ports, null, "非法 --ports 不写入");
        T.True(o.Errors.Count > 0, "非法 --ports 报错");

        o = CommandLineOptions.Parse(new[] { "--ports", "99999" });
        T.Eq(o.Ports, null, "越界 --ports 不写入");
        T.True(o.Errors.Count > 0, "越界 --ports 报错");

        // ---- --tab：数字（兼容）与卡片键名（推荐）两种写法 ----
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", "0" }).Tab, "0", "--tab 数字下边界 0");
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", CommandLineOptions.MaxTabIndex.ToString() }).Tab,
             CommandLineOptions.MaxTabIndex.ToString(),
             "--tab 数字上边界（用常量而不是写死的数字，加卡片时这里不用改）");
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", (CommandLineOptions.MaxTabIndex + 1).ToString() }).Tab,
             null, "--tab 超范围不写入");
        T.True(CommandLineOptions.Parse(new[] { "--tab", (CommandLineOptions.MaxTabIndex + 1).ToString() })
                .Errors.Count > 0, "--tab 超范围报错");
        T.True(CommandLineOptions.Parse(new[] { "--tab", "-1" }).Errors.Count > 0, "--tab 负数报错");
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", "-1" }).Tab, null, "--tab 负数不写入");

        // 键名写法：Core 不认识卡片键，只做「看起来像不像键名」的粗筛，
        // 存在性由 Ui 层的 CardCatalog 判定 —— 反过来让 Core 依赖 Ui 才是错的。
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", "ipv6" }).Tab, "ipv6", "--tab 接受卡片键名");
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", "ipv6" }).Errors.Count, 0, "--tab 键名不报错");
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", "IPV6" }).Tab, "ipv6", "--tab 键名统一转小写");
        T.True(CommandLineOptions.Parse(new[] { "--tab", "1x" }).Errors.Count > 0,
               "--tab 既不是数字也不像键名时报错");
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", "1x" }).Tab, null, "非法 --tab 不写入");
        T.True(CommandLineOptions.Parse(new[] { "--tab" }).Errors.Count > 0, "--tab 缺值时报错");
        T.True(CommandLineOptions.Parse(new[] { "--tab", "ipv6" }).HasAny, "--tab 算作「有参数」");

        o = CommandLineOptions.Parse(new[] { "--auto" });
        T.True(o.Auto, "--auto 置位");
        T.True(o.HasAny, "--auto 算作「有参数」");
        T.Eq(o.Errors.Count, 0, "--auto 没有错误");

        o = CommandLineOptions.Parse(new[] { "--export", "\"C:\\a b\\r.txt\"" });
        T.Eq(o.ExportPath, "C:\\a b\\r.txt", "--export 去掉外层引号");

        o = CommandLineOptions.Parse(new[] { "--export", "out.json" });
        T.Eq(o.ExportPath, "out.json", "--export 相对路径原样保留");

        T.True(CommandLineOptions.Parse(new[] { "--help" }).ShowHelp, "--help 置位");
        T.True(CommandLineOptions.Parse(new[] { "-?" }).ShowHelp, "-? 等价于 --help");
        T.True(CommandLineOptions.Parse(new[] { "/?" }).ShowHelp, "/? 等价于 --help");
        T.True(CommandLineOptions.Parse(new[] { "--HELP" }).ShowHelp, "参数名大小写不敏感");

        o = CommandLineOptions.Parse(new[] { "--bogus" });
        T.True(o.Errors.Count > 0, "未知参数被收集为错误");
        T.True(o.Errors[0].Contains("--bogus"), "错误信息里回显了原参数");

        o = CommandLineOptions.Parse(new[] { "--host" });
        T.True(o.Errors.Count > 0, "缺少参数值时报错");
        T.Eq(o.Host, null, "缺少参数值时不写入");

        o = CommandLineOptions.Parse(new[] { "--ports" });
        T.True(o.Errors.Count > 0, "--ports 缺值时报错");

        o = CommandLineOptions.Parse(Array.Empty<string>());
        T.False(o.HasAny, "无参数时 HasAny 为假");
        T.Eq(o.Errors.Count, 0, "无参数时没有错误");
        T.False(o.ShowHelp, "无参数时不显示帮助");
        T.False(o.Auto, "无参数时不自动检测");

        // 多个参数可以混写
        o = CommandLineOptions.Parse(new[] { "--host", "8.8.8.8", "--ports=53,443", "--auto", "--export", "r.txt" });
        T.Eq(o.Host, "8.8.8.8", "混写：主机");
        T.Eq(o.Ports, "53,443", "混写：端口");
        T.True(o.Auto, "混写：自动检测");
        T.Eq(o.ExportPath, "r.txt", "混写：导出路径");
        T.Eq(o.Errors.Count, 0, "混写没有错误");

        // 空字符串参数被跳过
        o = CommandLineOptions.Parse(new[] { "", "  ", "--auto" });
        T.True(o.Auto, "空参数被跳过，后面的参数照常解析");
        T.Eq(o.Errors.Count, 0, "空参数不产生错误");

        // ---- ApplyTo ----
        var s = new AppSettings { TargetHost = "223.5.5.5", PortSpec = "80", PortPreset = "自定义" };
        CommandLineOptions.Parse(new[] { "--host", "1.1.1.1" }).ApplyTo(s);
        T.Eq(s.TargetHost, "1.1.1.1", "ApplyTo 覆盖主机");
        T.Eq(s.PortSpec, "80", "ApplyTo 不动未传入的端口");

        var s2 = new AppSettings();
        CommandLineOptions.Parse(new[] { "--ports", "8080,3389,443,80,22" }).ApplyTo(s2);
        T.Eq(s2.PortSpec, "8080,3389,443,80,22", "ApplyTo 写入端口表达式");
        T.Eq(s2.PortPreset, "常用服务", "ApplyTo 自动反查出预设名");

        var s3 = new AppSettings();
        CommandLineOptions.Parse(Array.Empty<string>()).ApplyTo(s3);
        T.Eq(s3.TargetHost, "223.5.5.5", "空参数 ApplyTo 不改动任何东西");
        T.Eq(s3.PortSpec, PortPresets.DefaultSpec, "空参数 ApplyTo 不动端口表达式");

        // 命令行传入越界值也要被 Clamp
        var s4 = new AppSettings();
        CommandLineOptions.Parse(new[] { "--host", "  " }).ApplyTo(s4);
        T.Eq(s4.TargetHost, "223.5.5.5", "ApplyTo 内部也会 Clamp");

        // ---- --check ----
        T.Eq(CommandLineOptions.Parse(new[] { "--check", "port" }).Check, "port", "--check port 被识别");
        T.Eq(CommandLineOptions.Parse(new[] { "--check", "PORT" }).Check, "port", "--check 值大小写不敏感");
        T.Eq(CommandLineOptions.Parse(new[] { "--check=all" }).Check, "all", "--check=all 内联写法");
        T.Eq(CommandLineOptions.Parse(new[] { "--check", "port" }).Errors.Count, 0, "合法 --check 没有错误");
        T.Eq(CommandLineOptions.Parse(new[] { "--check", "bogus" }).Check, null, "非法 --check 不写入");
        T.True(CommandLineOptions.Parse(new[] { "--check", "bogus" }).Errors.Count > 0, "非法 --check 报错");
        T.True(CommandLineOptions.Parse(new[] { "--check", "bogus" }).Errors[0].Contains("port"),
               "非法 --check 的错误提示里列出了可选值");
        T.True(CommandLineOptions.Parse(new[] { "--check" }).Errors.Count > 0, "--check 缺值时报错");
        T.True(CommandLineOptions.Parse(new[] { "--check", "port" }).HasAny, "--check 算作「有参数」");

        foreach (var name in CommandLineOptions.CheckNames)
            T.Eq(CommandLineOptions.Parse(new[] { "--check", name }).Check, name, $"--check {name} 合法");
        T.True(CommandLineOptions.CheckNames.Contains("quick"), "--check 支持 quick");
        T.True(CommandLineOptions.CheckNames.Contains("latency"),
               "--check 支持 latency（连通性卡片要能独立跑延迟，不能只靠一键检测顺带）");
        T.True(CommandLineOptions.CheckNames.Contains("egress"),
               "--check 支持 egress（出口 IP 要能单独跑，不能只靠一键检测顺带）");
        T.Eq(CommandLineOptions.CheckNames.Distinct().Count(), CommandLineOptions.CheckNames.Length,
             "--check 可选值没有重复项");

        // ---- --check all 的展开表 ----
        // 这些断言守的是一个不变式：all 必须恰好覆盖所有「原子检测项」一次，
        // 既不漏（漏了就有检测项跑不到），也不重（重了就会白白多跑一遍）。
        var all = CommandLineOptions.AllExpansion;
        T.Eq(all.Distinct().Count(), all.Length, "all 展开表里没有重复项");
        T.Eq(all[0], "local", "all 展开表的第一个必须是 local —— 后面几项都要用网卡信息");
        T.False(all.Contains("all"), "all 展开表里不能再有 all（会无限递归）");
        T.False(all.Contains("quick"),
               "all 展开表里不能有 quick —— 界面上的「一键检测」就是按这张表跑的，"
               + "把 quick 放进来会无限递归");
        foreach (var k in all)
            T.True(CommandLineOptions.CheckNames.Contains(k), $"all 展开项「{k}」本身是个合法的 --check 值");

        // 反向：CheckNames 里除 quick/all 之外的每一项，都必须在 all 展开表里出现且只出现一次。
        // 这条比上面那条更要紧 —— 新增检测项却忘了加进 all，用户跑 --check all 就永远拿不到它。
        var primitives = CommandLineOptions.CheckNames.Where(k => k != "quick" && k != "all").ToList();
        T.Eq(all.Length, primitives.Count, "all 展开表的项数 == 原子检测项数");
        foreach (var k in primitives)
            T.Eq(all.Count(x => x == k), 1, $"原子检测项「{k}」在 all 展开表里恰好出现一次");

        // 界面上的「一键检测」直接复用这张表，所以这里的不变式就是「一键检测确实全跑」。
        // 以前 quick 只跑 local+egress+dns+latency，用户点完还得手动再点六下 ——
        // 那才是这一组断言真正要守住的东西。
        foreach (var k in new[] { "local", "egress", "dns", "latency", "speed", "ipv6", "web", "nat", "trace", "mtu", "port" })
            T.True(all.Contains(k), $"一键检测会跑到「{k}」");
        T.Eq(all.Length, 11, "一键检测覆盖全部 11 个原子检测项");

        // 前四项恰好等于 quick 的展开 —— 这条不是巧合而是顺序设计的副产品，
        // 也正是「all 里再放 quick 就是纯重复劳动」的量化依据。
        T.Eq(string.Join(",", all.Take(4)), "local,egress,dns,latency",
             "all 的前四项就是 quick 的展开，所以再放 quick 一定是重复跑");

        // AutoCheckKey：--auto 等价于 --check quick，--check 优先
        T.Eq(CommandLineOptions.Parse(new[] { "--auto" }).AutoCheckKey, "quick", "--auto 等价于 --check quick");
        T.Eq(CommandLineOptions.Parse(new[] { "--check", "nat" }).AutoCheckKey, "nat", "--check 直接生效");
        T.Eq(CommandLineOptions.Parse(new[] { "--check", "nat", "--auto" }).AutoCheckKey, "nat",
             "--check 与 --auto 同时给出时以 --check 为准");
        T.Eq(CommandLineOptions.Parse(Array.Empty<string>()).AutoCheckKey, null, "没有自动检测参数时返回 null");
        T.Eq(CommandLineOptions.Parse(new[] { "--tab", "3" }).AutoCheckKey, null, "只有 --tab 时不自动检测");

        T.True(CommandLineOptions.HelpText.Contains("--host"), "帮助文本提到 --host");
        T.True(CommandLineOptions.HelpText.Contains("--ports"), "帮助文本提到 --ports");
        T.True(CommandLineOptions.HelpText.Contains("--tab"), "帮助文本提到 --tab");
        T.True(CommandLineOptions.HelpText.Contains("--check"), "帮助文本提到 --check");
        T.True(CommandLineOptions.HelpText.Contains("--export"), "帮助文本提到 --export");
        T.True(CommandLineOptions.HelpText.Contains("--help"), "帮助文本提到 --help");
    }

    // ==================================================================
    // 7. 视图模型骨架
    // ==================================================================

    private static void ReportViewCore()
    {
        T.Group("ReportViewBuilder.骨架");

        var v = ReportViewBuilder.Build(new DiagState());
        T.False(v.AnyData, "空状态 AnyData 为假");
        T.Eq(v.Tabs.Count, ReportViewBuilder.TabCount, "始终构建 TabCount 个分类");
        T.Eq(v.Cards.Count, 6, "始终构建 6 张指标卡（出口 / 延迟 / 丢包 / 抖动 / 带宽 / NAT）");
        T.Eq(v.Chips.Count, 0, "空状态没有结论胶囊");
        T.Eq(v.Issues.Count, 1, "空状态恰好一条提示");
        T.True(v.Issues.All(i => i.Tone == Tone.Good), "空状态只给「未发现问题」");
        T.True(v.Tabs.All(t => !t.HasData), "空状态下每个分类都没有数据");
        // 概览页会带上那条「未发现问题」的提示区块，其余分类完全空白
        T.Eq(v.Tabs[ReportViewBuilder.OverviewIndex].Sections.Count, 1, "空状态的概览只有一条提示区块");
        T.Eq(v.Tabs[ReportViewBuilder.OverviewIndex].Sections[0].Title, "问题与提示", "概览里的唯一区块是问题与提示");
        T.True(v.Tabs.Skip(1).All(t => t.Sections.Count == 0), "空状态下除概览外的分类都没有区块");

        // 分类顺序必须与导航栏一致
        T.Eq(v.Tabs.Count, ReportViewBuilder.TabCount, "标签页数量 == TabCount 常量");
        T.Eq(v.Tabs[ReportViewBuilder.OverviewIndex].Title, "概览", "0 号分类是概览");
        T.Eq(v.Tabs[ReportViewBuilder.LocalIndex].Title, "本机", "1 号分类是本机");
        T.Eq(v.Tabs[ReportViewBuilder.Ipv6Index].Title, "IPv6", "2 号分类是 IPv6（插在本机之后、连通性之前）");
        T.Eq(v.Tabs[ReportViewBuilder.LinkIndex].Title, "连通性", "3 号分类是连通性");
        T.Eq(v.Tabs[ReportViewBuilder.DnsIndex].Title, "DNS", "4 号分类是 DNS");
        T.Eq(v.Tabs[ReportViewBuilder.NatIndex].Title, "NAT", "5 号分类是 NAT");
        T.Eq(v.Tabs[ReportViewBuilder.PathIndex].Title, "路径", "6 号分类是路径");
        T.Eq(v.Tabs[ReportViewBuilder.PortIndex].Title, "端口", "7 号分类是端口");

        // 每张卡片都要有标题，没数据的卡片 HasData 必须为假
        foreach (var c in v.Cards)
        {
            T.True(c.Title.Length > 0, $"卡片「{c.Title}」有标题");
            T.True(c.Value.Length > 0, $"卡片「{c.Title}」有占位值");
        }
        T.True(v.Cards.All(c => !c.HasData), "空状态下所有卡片都没有数据");
        T.True(v.Cards.All(c => c.Value == "—"), "空状态下卡片显示占位破折号");

        // 有了本机数据之后
        var s = new DiagState
        {
            Hostname = "PC-1",
            Adapters = new List<AdapterInfo>
            {
                new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2", Gateway = "10.0.0.1", Dns = { "223.5.5.5" } },
            },
        };
        v = ReportViewBuilder.Build(s);
        T.True(v.AnyData, "有本机数据后 AnyData 为真");
        T.True(v.Tabs[ReportViewBuilder.LocalIndex].HasData, "本机分类有数据");
        T.True(v.Tabs[ReportViewBuilder.LocalIndex].Sections.Count > 0, "本机分类有区块");

        // 指标卡可跳转的索引必须落在合法范围内
        foreach (var c in v.Cards.Where(c => c.TargetTab >= 0))
            T.True(c.TargetTab < v.Tabs.Count, $"卡片「{c.Title}」的跳转目标合法");
    }

    // ==================================================================
    // 8. 问题判定规则
    // ==================================================================

    private static void IssueRules()
    {
        T.Group("ReportViewBuilder.问题规则");

        static bool Has(ReportView v, string part) => v.Issues.Any(i => i.Title.Contains(part));

        // 指标卡按标题取，不按索引 —— 索引会随卡片增删漂移
        // （这一批就在「网络延迟」后面插了丢包率和抖动两张），
        // 而「NAT 类型卡片显示什么」这条断言跟它是第几张卡毫无关系。
        static MetricCard Card(ReportView v, string title)
            => v.Cards.First(c => c.Title == title);

        // ---- 本机 ----
        var v = ReportViewBuilder.Build(new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "WLAN", IsUp = false } },
        });
        T.True(Has(v, "没有已连接的网络适配器"), "全部网卡断开 → 严重问题");
        T.True(v.Issues.Any(i => i.Title.Contains("没有已连接") && i.Tone == Tone.Bad), "全部网卡断开判为严重");
        T.True(v.Issues.Any(i => i.TargetTab == ReportViewBuilder.LocalIndex), "该问题指向本机分类");

        v = ReportViewBuilder.Build(new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "" } },
        });
        T.True(Has(v, "网卡没有 IPv4 地址"), "网卡已连接但没有 IPv4 → 严重问题");

        v = ReportViewBuilder.Build(new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2" } },
        });
        T.False(Has(v, "没有已连接"), "正常网卡不产生告警");
        T.False(Has(v, "没有 IPv4"), "有 IPv4 时不产生告警");

        // ---- 公网出口 ----
        v = ReportViewBuilder.Build(new DiagState
        {
            PublicIpDirect = "不可用",
            PublicIpHttpDirect = new List<PublicIpResult> { new() { Service = "a", Ip = "", Error = "timeout" } },
        });
        T.True(Has(v, "未能获取公网出口 IP"), "所有探测路径失败 → 提示未能获取出口 IP");

        v = ReportViewBuilder.Build(new DiagState
        {
            PublicIpHttpDirect = new List<PublicIpResult>
            {
                new() { Service = "a", Ip = "1.1.1.1" },
                new() { Service = "b", Ip = "2.2.2.2" },
            },
        });
        T.True(Has(v, "个不同的公网出口 IP"), "两个出口 IP → 多出口告警");
        T.True(v.Issues.Any(i => i.Title.Contains("个不同的公网出口") && i.Tone == Tone.Warn), "多出口判为提醒");
        T.True(v.Issues.Any(i => i.Title.Contains("个不同的公网出口") && i.TargetTab == ReportViewBuilder.EgressIndex),
               "多出口告警指向「公网出口 IP」卡片");
        T.Eq(Card(v, "公网出口").Value, "2 个", "公网出口卡片显示出口个数");
        T.Eq(Card(v, "公网出口").Sub, "多出口网络", "公网出口卡片副标题说明多出口");
        T.Eq(Card(v, "公网出口").Tone, Tone.Warn, "多出口时卡片是警示色");

        v = ReportViewBuilder.Build(new DiagState
        {
            PublicIpHttpDirect = new List<PublicIpResult> { new() { Service = "a", Ip = "1.1.1.1" } },
        });
        T.False(Has(v, "个不同的公网出口 IP"), "只有一个出口时不告警");
        T.Eq(v.Cards[0].Value, "1.1.1.1", "单出口卡片直接显示 IP");
        T.Eq(v.Cards[0].Tone, Tone.Good, "单出口时卡片是正常色");

        // ---- 延迟 ----
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Gateway = new LatencyTarget
                {
                    Name = "网关", Host = "10.0.0.1", Method = "ICMP",
                    Min = 20, Avg = 22, Max = 25, Samples = { 20, 22, 25 },
                },
            },
        });
        T.True(Has(v, "网关延迟偏高"), "网关 >15 ms → 告警");
        // 22 ms 超过 GatewayWarnMs，条形和问题条目都该是「严重」——两者必须同色调
        T.True(v.Issues.Any(i => i.Title.Contains("网关延迟偏高") && i.Tone == Tone.Bad),
            "网关 22 ms 判为严重（与 Thresholds.Gateway 同源）");
        T.Eq(Thresholds.Gateway(22), Tone.Bad, "阈值函数本身也判为严重");

        // 5 ms：以前条形是橙色（阈值 3 / 15）但问题清单里一条都没有，
        // 用户看到橙色却找不到解释。现在只要不是绿色就一定有问题条目。
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Gateway = new LatencyTarget
                {
                    Name = "网关", Host = "10.0.0.1", Method = "ICMP",
                    Min = 4, Avg = 5, Max = 6, Samples = { 4, 5, 6 },
                },
            },
        });
        T.True(Has(v, "网关延迟偏高"), "网关 5 ms 也要提醒（条形已转橙，必须有问题条目解释）");
        T.True(v.Issues.Any(i => i.Title.Contains("网关延迟偏高") && i.Tone == Tone.Warn),
            "网关 5 ms 判为提醒");

        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Gateway = new LatencyTarget { Name = "网关", Host = "10.0.0.1", Method = "ICMP", Min = 0.3, Avg = 0.4, Max = 0.9, Samples = { 0.4, 0.3 } },
            },
        });
        T.False(Has(v, "网关延迟偏高"), "网关 0.4 ms 不告警");
        T.Eq(Thresholds.Gateway(0.4), Tone.Good, "网关 0.4 ms 阈值判为正常");

        // 丢包分档
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "阿里 DNS", Host = "223.5.5.5", Method = "ICMP", Loss = 25, Samples = { 6, 7, 9 } },
                },
            },
        });
        T.True(v.Issues.Any(i => i.Title.Contains("丢包") && i.Tone == Tone.Bad), "丢包 ≥20% 判为严重");

        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "阿里 DNS", Host = "223.5.5.5", Method = "ICMP", Loss = 10, Samples = { 6, 7, 9 } },
                },
            },
        });
        T.True(v.Issues.Any(i => i.Title.Contains("轻微丢包") && i.Tone == Tone.Warn), "轻微丢包判为提醒");

        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "阿里 DNS", Host = "223.5.5.5", Method = "ICMP", Loss = 0, Samples = { 6, 7, 9 } },
                },
            },
        });
        T.False(Has(v, "丢包"), "不丢包时不告警");

        // 抖动
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "某目标", Host = "1.1.1.1", Method = "ICMP", Loss = 0, Samples = { 10, 80, 10, 80 } },
                },
            },
        });
        T.True(Has(v, "抖动较大"), "抖动 >30 ms → 告警");

        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "某目标", Host = "1.1.1.1", Method = "ICMP", Loss = 0, Samples = { 10, 12, 10, 12 } },
                },
            },
        });
        T.False(Has(v, "抖动较大"), "抖动 2 ms 不告警");

        // 分类阈值：同一份 80 ms，在游戏里算「一般」，在网页里完全没问题。
        // 这正是「延迟要按用途分别判定」的核心，必须锁住。
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "Steam", Category = "游戏", Host = "store.steampowered.com",
                                        Method = "ICMP", Min = 79, Avg = 80, Max = 81, Loss = 0,
                                        Samples = { 79, 80, 81 } },
                },
            },
        });
        T.True(v.Issues.Any(i => i.Title.Contains("游戏") && i.Title.Contains("延迟一般")),
               "游戏类 80 ms → 「延迟一般」提醒");

        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "百度", Category = "网页浏览", Host = "www.baidu.com",
                                        Method = "ICMP", Min = 79, Avg = 80, Max = 81, Loss = 0,
                                        Samples = { 79, 80, 81 } },
                },
            },
        });
        T.False(Has(v, "延迟一般"), "同样 80 ms，网页类不告警 —— 阈值确实是按分类给的");

        // 连最快的一个都超过「可用」上限 → 整类都不行，判严重
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "腾讯游戏", Category = "游戏", Host = "game.qq.com",
                                        Method = "ICMP", Min = 150, Avg = 152, Max = 154, Loss = 0,
                                        Samples = { 150, 152, 154 } },
                },
            },
        });
        T.True(v.Issues.Any(i => i.Tone == Tone.Bad && i.Title.Contains("整体延迟偏高")),
               "游戏类全部超标 → 判为严重");

        // 同类里只有个别目标慢：要说清「是到这一家的路径问题」，而不是整条宽带不行
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "腾讯游戏", Category = "游戏", Host = "game.qq.com",
                                        Method = "ICMP", Min = 8, Avg = 8, Max = 8, Loss = 0,
                                        Samples = { 8, 8, 8 } },
                    new LatencyTarget { Name = "Steam", Category = "游戏", Host = "store.steampowered.com",
                                        Method = "ICMP", Min = 140, Avg = 140, Max = 140, Loss = 0,
                                        Samples = { 140, 140, 140 } },
                },
            },
        });
        T.True(v.Issues.Any(i => i.Title.Contains("有目标延迟偏高") && i.Tone == Tone.Warn),
               "同类内部分目标慢 → 提醒而不是严重");
        T.False(v.Issues.Any(i => i.Title.Contains("整体延迟偏高")),
                "最快的一个正常，就不该判成整体超标");

        // 每类最多出一条延迟结论，9 个目标不能刷出 9 条
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "A", Category = "游戏", Host = "a.com", Method = "ICMP", Avg = 200, Loss = 0, Samples = { 200, 200 } },
                    new LatencyTarget { Name = "B", Category = "游戏", Host = "b.com", Method = "ICMP", Avg = 210, Loss = 0, Samples = { 210, 210 } },
                    new LatencyTarget { Name = "C", Category = "游戏", Host = "c.com", Method = "ICMP", Avg = 220, Loss = 0, Samples = { 220, 220 } },
                },
            },
        });
        T.Eq(v.Issues.Count(i => i.Title.Contains("整体延迟偏高")), 1, "同类多个目标超标只出一条结论");

        // 旧版本导出的数据没有 Category，不该因此漏掉丢包判定
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "老数据", Host = "1.1.1.1", Method = "ICMP", Loss = 50, Samples = { 5 } },
                },
            },
        });
        T.True(v.Issues.Any(i => i.Title.Contains("丢包")), "无分类的旧数据仍然走丢包判定");

        // 全部不可达
        v = ReportViewBuilder.Build(new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget { Name = "A", Host = "1.1.1.1", Method = "ICMP", Loss = 100 },
                    new LatencyTarget { Name = "B", Host = "8.8.8.8", Method = "TCP:443", Loss = 100 },
                },
            },
        });
        T.True(v.Issues.Any(i => i.Title == "所有延迟目标均不可达" && i.Tone == Tone.Bad), "全部不可达判为严重");

        // ---- DNS ----
        v = ReportViewBuilder.Build(new DiagState
        {
            Hijack = new HijackReport
            {
                ProbeDomain = "nx-probe.invalid",
                Checks = { new HijackCheck { Server = "1.2.3.4", Ok = true, RCode = 0, Hijacked = true } },
            },
        });
        T.True(v.Issues.Any(i => i.Title == "检测到 DNS 劫持" && i.Tone == Tone.Bad), "DNS 劫持判为严重");
        T.True(v.Chips.Any(c => c.Text == "发现 DNS 劫持" && c.Tone == Tone.Bad), "劫持时结论条给出胶囊");

        v = ReportViewBuilder.Build(new DiagState
        {
            Hijack = new HijackReport
            {
                ProbeDomain = "nx-probe.invalid",
                Checks = { new HijackCheck { Server = "1.2.3.4", Ok = true, RCode = 3 } },
            },
        });
        T.False(Has(v, "检测到 DNS 劫持"), "正常返回 NXDOMAIN 不算劫持");
        T.True(v.Chips.Any(c => c.Text == "未发现 DNS 劫持" && c.Tone == Tone.Good), "无劫持时胶囊是正常色");

        // DNS 服务器无响应
        v = ReportViewBuilder.Build(new DiagState
        {
            DnsServerTests = new List<DnsServerTest>
            {
                new() { Server = "1.2.3.4", Error = "超时" },
                new() { Server = "223.5.5.5", Ms = 6.8, Answers = 2 },
            },
        });
        T.True(Has(v, "1.2.3.4 无响应"), "DNS 服务器无响应 → 告警");
        T.False(Has(v, "223.5.5.5 无响应"), "有响应的 DNS 不告警");

        // DNS 解析偏慢
        v = ReportViewBuilder.Build(new DiagState
        {
            DnsServerTests = new List<DnsServerTest>
            {
                new() { Server = "223.5.5.5", Ms = 450, Answers = 2 },
            },
        });
        T.True(Has(v, "DNS 解析偏慢"), "解析 >200 ms → 告警");

        v = ReportViewBuilder.Build(new DiagState
        {
            DnsServerTests = new List<DnsServerTest> { new() { Server = "223.5.5.5", Ms = 6.8, Answers = 2 } },
        });
        T.False(Has(v, "DNS 解析偏慢"), "解析 6.8 ms 不告警");

        // 多服务器解析不一致
        v = ReportViewBuilder.Build(new DiagState
        {
            Hijack = new HijackReport
            {
                Checks = { new HijackCheck { Server = "1.2.3.4", Ok = true, RCode = 3 } },
                Comparisons =
                {
                    new DnsComparison
                    {
                        Domain = "github.com",
                        Entries =
                        {
                            new DnsComparisonEntry { Server = "223.5.5.5", Ok = true, Addresses = { "20.205.243.166" } },
                            new DnsComparisonEntry { Server = "8.8.8.8", Ok = true, Addresses = { "140.82.113.4" } },
                        },
                    },
                },
            },
        });
        T.True(Has(v, "解析结果不一致"), "同一域名不同 DNS 结果不同 → 告警");

        // ---- NAT ----
        v = ReportViewBuilder.Build(new DiagState
        {
            Nat = new NatReport { Type = "多出口 / 对称型 NAT", Detail = "不同目标看到不同出口。" },
        });
        T.True(Has(v, "NAT 类型不利于 P2P 直连"), "对称型 / 多出口 NAT → 告警");

        v = ReportViewBuilder.Build(new DiagState
        {
            Nat = new NatReport { Type = "未知", Detail = "所有 STUN 服务器均不可达。" },
        });
        T.True(Has(v, "无法判定 NAT 类型"), "NAT 未知 → 告警");

        // 样本不足（有回应但不够比较）与「一台都没通」是两种情况，
        // 但对用户来说都是「这次没测出来」，所以同样要告警、同样显示「无法判定」。
        v = ReportViewBuilder.Build(new DiagState
        {
            Nat = new NatReport { Type = "无法判定", Detail = "只有 1 台 STUN 服务器返回了结果。" },
        });
        T.True(Has(v, "无法判定 NAT 类型"), "NAT 样本不足 → 同样告警");
        T.Eq(Card(v, "NAT 类型").Value, "无法判定", "样本不足时卡片显示「无法判定」");
        T.Eq(Card(v, "NAT 类型").Sub, "结果不可用", "样本不足时卡片提示结果不可用");
        T.Eq(Card(v, "NAT 类型").Tone, Tone.Neutral, "样本不足是「没测出来」而不是「网络有问题」，用中性色");

        v = ReportViewBuilder.Build(new DiagState
        {
            Nat = new NatReport { Type = "无 NAT（公网 IP）", Detail = "直接就是公网地址。" },
        });
        T.False(Has(v, "NAT 类型不利于"), "公网直连不告警");
        T.Eq(Card(v, "NAT 类型").Value, "公网直连", "公网直连时卡片显示「公网直连」");
        T.Eq(Card(v, "NAT 类型").Tone, Tone.Good, "公网直连时卡片是正常色");

        v = ReportViewBuilder.Build(new DiagState
        {
            Nat = new NatReport { Type = "端口受限圆锥型 NAT", Detail = "打洞可行。" },
        });
        T.False(Has(v, "NAT 类型不利于"), "圆锥型 NAT 不告警");

        // ---- 带宽 ----
        v = ReportViewBuilder.Build(new DiagState
        {
            BandwidthDirect = new BandwidthReport { Source = "镜像", Bytes = 10_000_000, Seconds = 10 },
        });
        T.True(Has(v, "下载带宽偏低"), "低于 10 Mbps → 告警");

        v = ReportViewBuilder.Build(new DiagState
        {
            BandwidthDirect = new BandwidthReport { Source = "镜像", Bytes = 162_500_000, Seconds = 10 },
        });
        T.False(Has(v, "下载带宽偏低"), "130 Mbps 不告警");
        T.Eq(Card(v, "下载带宽").Value, "130 Mbps", "带宽卡片显示 130 Mbps");
        T.Eq(Card(v, "下载带宽").Tone, Tone.Good, "130 Mbps 判为正常");

        v = ReportViewBuilder.Build(new DiagState
        {
            BandwidthDirect = new BandwidthReport { Error = "连接失败" },
            BandwidthProxy = new BandwidthReport { Error = "连接失败" },
        });
        T.True(Has(v, "带宽测速全部失败"), "直连与代理都失败 → 告警");

        // ---- MTU ----
        v = ReportViewBuilder.Build(new DiagState
        {
            Mtu = new MtuReport { Target = "1.1.1.1", Reachable = true, MaxPayload = 1472, PathMtu = 1500 },
        });
        T.False(Has(v, "路径 MTU 偏低"), "MTU 1500 不告警");
        T.True(v.Chips.Any(c => c.Text == "路径 MTU 1500"), "MTU 正常时给出胶囊");

        v = ReportViewBuilder.Build(new DiagState
        {
            Mtu = new MtuReport { Target = "1.1.1.1", Reachable = true, MaxPayload = 1372, PathMtu = 1400 },
        });
        T.True(Has(v, "路径 MTU 偏低"), "MTU 1400 → 告警");

        v = ReportViewBuilder.Build(new DiagState
        {
            Mtu = new MtuReport { Target = "1.1.1.1", Reachable = false, Error = "目标不可达" },
        });
        T.True(Has(v, "MTU 探测未完成"), "MTU 探测失败 → 告警");

        // ---- 路由 ----
        v = ReportViewBuilder.Build(new DiagState
        {
            Traceroute = new TracerouteReport { Target = "1.1.1.1", Error = "DNS 解析失败" },
        });
        T.True(Has(v, "路由追踪失败"), "路由追踪报错 → 告警");

        v = ReportViewBuilder.Build(new DiagState
        {
            Traceroute = new TracerouteReport
            {
                Target = "1.1.1.1",
                Hops = { new HopInfo { Ttl = 1, Address = "10.0.0.1", Ms = 0.6, Status = "TtlExpired" } },
            },
        });
        T.False(Has(v, "路由追踪失败"), "路由追踪成功不告警");

        // ---- 结论胶囊 ----
        v = ReportViewBuilder.Build(new DiagState
        {
            PortScan = new PortScanReport
            {
                Host = "223.5.5.5",
                Results = { new PortResult { Port = 443, Open = true, Ms = 7 }, new PortResult { Port = 80, Open = false } },
            },
        });
        T.True(v.Chips.Any(c => c.Text == "223.5.5.5 开放 1 个端口"), "端口开放时胶囊文字正确");
        // 端口是你自己指定要看的，「开放」是事实不是问题：给中性色。
        // 以前给橙色胶囊，但问题清单里没有任何对应条目 —— 凭空制造焦虑。
        T.True(v.Chips.Any(c => c.Text.Contains("开放 1 个端口") && c.Tone == Tone.Neutral),
            "端口开放胶囊是中性色（没有对应问题条目）");
        T.False(v.Issues.Any(i => i.Title.Contains("端口")), "端口开放不产生问题条目");
        T.Eq(v.Tabs[ReportViewBuilder.PortIndex].Sections[0].VerdictTone, Tone.Neutral,
            "端口页结论条也是中性色");

        v = ReportViewBuilder.Build(new DiagState
        {
            PortScan = new PortScanReport
            {
                Host = "223.5.5.5",
                Results = { new PortResult { Port = 80, Open = false } },
            },
        });
        T.False(v.Chips.Any(c => c.Text.Contains("开放")), "没有开放端口时不产生胶囊");

        // ---- 全部正常时不应该冒出告警 ----
        var clean = ReportViewBuilder.Build(new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2" } },
            PublicIpHttpDirect = new List<PublicIpResult> { new() { Service = "a", Ip = "1.1.1.1" } },
            DnsServerTests = new List<DnsServerTest> { new() { Server = "223.5.5.5", Ms = 6.8, Answers = 2 } },
            BandwidthDirect = new BandwidthReport { Source = "镜像", Bytes = 162_500_000, Seconds = 10 },
            Nat = new NatReport { Type = "端口受限圆锥型 NAT" },
            Mtu = new MtuReport { Target = "1.1.1.1", Reachable = true, PathMtu = 1500 },
            Latency = new LatencyReport
            {
                Gateway = new LatencyTarget { Name = "网关", Host = "10.0.0.1", Method = "ICMP", Avg = 0.4, Samples = { 0.4, 0.3 } },
            },
        });
        T.Eq(clean.Issues.Count(i => i.Tone != Tone.Good), 0, "全部正常时没有告警项");
        T.True(clean.Issues.Any(i => i.Title == "未发现明显问题"), "全部正常时给出「未发现明显问题」");
    }

    // ==================================================================
    // 8b. 阈值口径统一（配色与问题清单必须同源）
    // ==================================================================

    private static void ThresholdConsistency()
    {
        T.Group("Thresholds.阈值同源");

        // ---- 每一档的上下沿 ----
        T.Eq(Thresholds.Gateway(3), Tone.Good, "网关 3 ms 是良好上沿");
        T.Eq(Thresholds.Gateway(3.01), Tone.Warn, "网关刚过 3 ms 转提醒");
        T.Eq(Thresholds.Gateway(15), Tone.Warn, "网关 15 ms 是提醒上沿");
        T.Eq(Thresholds.Gateway(15.01), Tone.Bad, "网关刚过 15 ms 转严重");

        T.Eq(Thresholds.Bandwidth(100), Tone.Good, "带宽 100 Mbps 是良好下沿");
        T.Eq(Thresholds.Bandwidth(99.9), Tone.Neutral, "带宽 99.9 Mbps 是中性（不评价）");
        T.Eq(Thresholds.Bandwidth(20), Tone.Neutral, "带宽 20 Mbps 是中性下沿");
        T.Eq(Thresholds.Bandwidth(19.9), Tone.Warn, "带宽 19.9 Mbps 转提醒");
        T.Eq(Thresholds.Bandwidth(5), Tone.Warn, "带宽 5 Mbps 是提醒下沿");
        T.Eq(Thresholds.Bandwidth(4.9), Tone.Bad, "带宽 4.9 Mbps 转严重");

        T.Eq(Thresholds.DnsServer(50), Tone.Good, "DNS 服务器 50 ms 是良好上沿");
        T.Eq(Thresholds.DnsServer(50.1), Tone.Warn, "DNS 服务器刚过 50 ms 转提醒");
        T.Eq(Thresholds.DnsServer(200), Tone.Warn, "DNS 服务器 200 ms 是提醒上沿");
        T.Eq(Thresholds.DnsServer(200.1), Tone.Bad, "DNS 服务器刚过 200 ms 转严重");

        T.Eq(Thresholds.DnsResolve(60), Tone.Good, "域名解析 60 ms 是良好上沿");
        T.Eq(Thresholds.DnsResolve(300), Tone.Warn, "域名解析 300 ms 是提醒上沿");
        T.Eq(Thresholds.DnsResolve(300.1), Tone.Bad, "域名解析刚过 300 ms 转严重");

        T.Eq(Thresholds.Mtu(1500), Tone.Good, "MTU 1500 良好");
        T.Eq(Thresholds.Mtu(1499), Tone.Warn, "MTU 1499 转提醒");
        T.Eq(Thresholds.Mtu(1400), Tone.Warn, "MTU 1400 是提醒下沿");
        T.Eq(Thresholds.Mtu(1399), Tone.Bad, "MTU 1399 转严重");

        T.Eq(Thresholds.Loss(20), Tone.Bad, "丢包 20% 是严重下沿");
        T.Eq(Thresholds.Loss(19.9), Tone.Warn, "丢包 19.9% 是提醒");

        // 只有 Warn / Bad 才算「问题」——Neutral 和 Good 都不该进问题清单
        T.False(Thresholds.IsProblem(Tone.Good), "Good 不算问题");
        T.False(Thresholds.IsProblem(Tone.Neutral), "Neutral 不算问题");
        T.True(Thresholds.IsProblem(Tone.Warn), "Warn 算问题");
        T.True(Thresholds.IsProblem(Tone.Bad), "Bad 算问题");

        // 带宽阈值必须单调：带宽越大，色调只会更好不会更差。
        // 注意方向 —— 带宽是「越高越好」，所以序号（越小越好）应当单调不增。
        static int Rank(Tone t) => t switch
        {
            Tone.Good => 0,
            Tone.Neutral => 1,
            Tone.Warn => 2,
            Tone.Bad => 3,
            _ => 1,
        };
        int prev = int.MaxValue;
        foreach (var mbps in new[] { 1.0, 4.9, 5, 19.9, 20, 99.9, 100, 500, 1000 })
        {
            int r = Rank(Thresholds.Bandwidth(mbps));
            T.True(r <= prev, $"带宽 {mbps} Mbps 的色调不比更小的值更差（单调）");
            prev = r;
        }

        // ---- 同源不变式：数值被判成 Warn/Bad，问题清单里就必须有一条同色调的解释 ----
        // 这组断言就是这次修复的核心。以前颜色和清单各写一套阈值，
        // 于是「网关 5 ms」在图上画成橙色、清单里却一条都没有。
        static DiagState Gw(double ms) => new()
        {
            Latency = new LatencyReport
            {
                Gateway = new LatencyTarget
                {
                    Name = "网关", Host = "10.0.0.1", Method = "ICMP",
                    Min = ms, Avg = ms, Max = ms, Samples = { ms },
                },
            },
        };
        // Mbps = Bytes * 8 / Seconds / 1e6，取 Seconds = 10 反推 Bytes
        static DiagState Bw(double mbps) => new()
        {
            BandwidthDirect = new BandwidthReport
            {
                Source = "镜像", Bytes = (long)(mbps * 1_000_000 * 10 / 8), Seconds = 10,
            },
        };
        static DiagState Mtu(int pathMtu) => new()
        {
            Mtu = new MtuReport { Target = "1.1.1.1", Reachable = true, PathMtu = pathMtu },
        };
        static DiagState DnsSrv(double ms) => new()
        {
            DnsServerTests = new List<DnsServerTest> { new() { Server = "223.5.5.5", Ms = ms, Answers = 2 } },
        };
        static DiagState DnsRes(double ms) => new()
        {
            DnsResolve = new List<DnsResolveTest>
            {
                new() { Domain = "www.baidu.com", Ok = true, Avg = ms, Address = "1.2.3.4" },
            },
        };

        var cases = new (string What, DiagState State, string Keyword, Tone Expected)[]
        {
            ("网关 5 ms（橙色）", Gw(5), "网关延迟偏高", Tone.Warn),
            ("网关 22 ms（红色）", Gw(22), "网关延迟偏高", Tone.Bad),
            ("带宽 12 Mbps（橙色）", Bw(12), "下载带宽偏低", Tone.Warn),
            ("带宽 3 Mbps（红色）", Bw(3), "下载带宽偏低", Tone.Bad),
            ("MTU 1450（橙色）", Mtu(1450), "路径 MTU 偏低", Tone.Warn),
            ("MTU 1300（红色）", Mtu(1300), "路径 MTU 偏低", Tone.Bad),
            ("DNS 服务器 300 ms（红色）", DnsSrv(300), "DNS 解析偏慢", Tone.Bad),
            ("域名解析 400 ms（红色）", DnsRes(400), "域名解析偏慢", Tone.Bad),
        };

        foreach (var c in cases)
        {
            var rv = ReportViewBuilder.Build(c.State);
            var hit = rv.Issues.FirstOrDefault(i => i.Title.Contains(c.Keyword));
            T.NotNull(hit, $"{c.What} → 有问题条目「{c.Keyword}」");
            if (hit != null)
                T.Eq(hit.Tone, c.Expected, $"{c.What} → 问题条目色调与阈值同源");
        }

        // 反方向：处于「良好」或「中性」的数值不该凭空冒出问题条目
        T.False(ReportViewBuilder.Build(Gw(1)).Issues.Any(i => i.Title.Contains("网关延迟偏高")),
            "网关 1 ms 不产生问题条目");
        T.False(ReportViewBuilder.Build(Bw(50)).Issues.Any(i => i.Title.Contains("下载带宽偏低")),
            "带宽 50 Mbps（中性）不产生问题条目");
        T.False(ReportViewBuilder.Build(Mtu(1500)).Issues.Any(i => i.Title.Contains("路径 MTU 偏低")),
            "MTU 1500 不产生问题条目");
        T.False(ReportViewBuilder.Build(DnsSrv(30)).Issues.Any(i => i.Title.Contains("DNS 解析偏慢")),
            "DNS 30 ms 不产生问题条目");
    }

    // ==================================================================
    // 8c. 卡片目录（卡片键 ↔ 标签页索引 这条手写映射）
    // ==================================================================

    private static void CardCatalogChecks()
    {
        T.Group("CardCatalog.卡片目录");

        var secs = CardCatalog.Sections;
        var tabs = ReportViewBuilder.Build(new DiagState()).Tabs;

        // 渲染层是 tabIndex < Tabs.Count ? Tabs[i] : null，越界不报错、只是卡片变空。
        // 所以这条映射必须靠断言守住 —— 这就是把它从 MainWindow 抽到 Ui 的唯一理由。
        T.Eq(secs.Length, tabs.Count, "检测项卡片数量 == 标签页数量");
        T.Eq(secs.Select(s => s.TabIndex).Distinct().Count(), secs.Length, "卡片索引不重复");
        T.Ports(secs.Select(s => s.TabIndex).OrderBy(x => x).ToArray(),
            Enumerable.Range(0, tabs.Count).ToArray(), "卡片索引恰好覆盖 0 ~ 标签页数-1");

        foreach (var i in Enumerable.Range(0, tabs.Count))
            T.NotNull(CardCatalog.CardForTab(i), $"标签页 {i} 有对应卡片");
        foreach (var s in secs)
            T.Eq(CardCatalog.CardForTab(s.TabIndex)?.Key, s.Key, $"索引 {s.TabIndex} 能反查回卡片「{s.Key}」");

        // 每个任务都必须能在界面上点到 —— 命令行有、界面上没有入口的功能等于没有
        var taskKeys = secs.SelectMany(s => s.Actions).Select(a => a.TaskKey).ToList();
        T.Eq(taskKeys.Distinct().Count(), taskKeys.Count, "没有两个按钮指向同一个任务");
        foreach (var k in CommandLineOptions.CheckNames.Where(x => x != "all"))
            T.True(taskKeys.Contains(k), $"任务「{k}」在界面上有按钮入口");
        foreach (var k in taskKeys)
            T.True(CommandLineOptions.CheckNames.Contains(k), $"按钮任务键「{k}」是合法的 --check 值");

        // 任务 -> 卡片 的往返
        foreach (var s in secs)
            foreach (var a in s.Actions)
                T.Eq(CardCatalog.CardForTask(a.TaskKey)?.Key, s.Key, $"任务「{a.TaskKey}」能反查到卡片「{s.Key}」");
        T.Eq(CardCatalog.CardForTask("不存在的任务"), null, "未知任务返回 null");

        // 目录自身的一致性
        var keys = CardCatalog.All.Select(s => s.Key).ToArray();
        T.Eq(keys.Distinct().Count(), keys.Length, "卡片键不重复");
        T.Eq(CardCatalog.All.Length, secs.Length + 1, "All = 检测选项 + 各检测项");
        T.Eq(CardCatalog.All[0].Key, CardCatalog.OptionsKey, "「检测选项」排在最前");
        T.Eq(CardCatalog.OptionsCard.TabIndex, CardCatalog.NoTab, "检测选项卡片不关联标签页");
        T.Eq(CardCatalog.OptionsCard.Actions.Length, 0, "检测选项卡片没有运行按钮");
        T.False(secs.Any(s => s.Key == CardCatalog.OptionsKey), "检测项列表里不含检测选项卡片");

        var rest = CardCatalog.All.Skip(1).Select(s => s.TabIndex).ToArray();
        T.True(rest.SequenceEqual(rest.OrderBy(x => x)), "All 里的检测项按标签页索引升序");

        foreach (var s in secs)
        {
            T.True(s.Title.Length > 0, $"卡片「{s.Key}」有标题");
            T.True(s.Hint.Length > 0, $"卡片「{s.Key}」有说明");
            T.True(s.Actions.Length > 0, $"卡片「{s.Key}」至少有一个运行按钮");
            T.True(tabs[s.TabIndex].Title.Length > 0, $"卡片「{s.Key}」对应的标签页有标题");
            foreach (var a in s.Actions)
            {
                T.True(a.Label.Length > 0, $"任务「{a.TaskKey}」有按钮文案");
                T.True(a.Tip.Length > 0, $"任务「{a.TaskKey}」有按钮提示");
            }
        }

        // 每个标签页都要有卡片认领（上面已断言），反过来也要成立：
        // 卡片不重复引用同一个标签页（已由「索引不重复」覆盖），这里再确认没有 -1 混进来
        T.False(secs.Any(s => s.TabIndex == CardCatalog.NoTab), "检测项卡片的索引不能是 NoTab");

        // ---- 分组 ----
        T.Eq(CardCatalog.Groups.Length, 2, "两个分组");
        T.Eq(CardCatalog.Groups[0].Key, CardCatalog.CommonGroup, "第一个分组是常用");
        T.Eq(CardCatalog.Groups[1].Key, CardCatalog.AdvancedGroup, "第二个分组是高级");
        foreach (var (gk, gt) in CardCatalog.Groups)
            T.True(gt.Length > 0, $"分组「{gk}」有标题");

        // 每张卡片都必须属于某个已声明的分组 —— 分组键写错的话，
        // 这张卡片在界面上会落到两个分组标题之间，看起来像个孤儿（而且不会报错）
        foreach (var d in CardCatalog.All)
            T.True(CardCatalog.Groups.Any(g => g.Key == d.Group),
                $"卡片「{d.Key}」的分组「{d.Group}」是已声明的分组");

        // CardsInGroup 和 All 必须对得上：并集是全部卡片，且两两不重叠
        var byGroup = CardCatalog.Groups.SelectMany(g => CardCatalog.CardsInGroup(g.Key)).ToArray();
        T.Eq(byGroup.Length, CardCatalog.All.Length, "各组卡片数之和 == 卡片总数");
        T.Eq(byGroup.Select(x => x.Key).Distinct().Count(), byGroup.Length, "没有卡片同时属于两个分组");

        T.Eq(CardCatalog.GroupOfCard(CardCatalog.OptionsKey), CardCatalog.CommonGroup, "检测选项属于常用组");
        T.Eq(CardCatalog.GroupOfCard("不存在的卡片"), "", "未知卡片的分组是空串");

        // 默认收起的是「检测选项」+「高级」组 —— 选项卡展开要占约半屏，会把概览挤出首屏；
        // 折叠后标题栏仍有配置摘要，信息不丢。从目录推导，以后往高级组增删卡片这里不用改
        var defCollapsed = CardCatalog.DefaultCollapsedCards()
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var advKeys = CardCatalog.CardsInGroup(CardCatalog.AdvancedGroup)
            .Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        T.True(defCollapsed.SequenceEqual(new[] { CardCatalog.OptionsKey }.Concat(advKeys).OrderBy(x => x, StringComparer.Ordinal)),
            "默认收起的是检测选项 + 高级组全部卡片");
        T.True(defCollapsed.Length > 1, "默认收起集合不是空的");
        T.True(defCollapsed.Contains(CardCatalog.OptionsKey), "检测选项在默认收起之列（首屏让给概览）");
        T.False(defCollapsed.Contains("overview"), "概览不在默认收起之列");

        // ---- 二级分组（只有高级组用）----
        // 高级区按诊断语义再分一层：网络路径 / 连接能力 / 协议。
        // 「NAT 和端口都属于连接建立能力」「路由和 MTU 都属于路径诊断」这两句话
        // 直接长在界面上，用户不用自己归纳。
        T.Eq(CardCatalog.SubGroups.Length, 3, "三个二级分组");
        T.Eq(string.Join(",", CardCatalog.SubGroups.Select(x => x.Key)),
             "netpath,connect,protocol", "二级分组的顺序就是界面顺序");
        foreach (var sg in CardCatalog.SubGroups)
            T.True(sg.Title.Length > 0, $"二级分组「{sg.Key}」有标题");

        // 常用组不加二级分组 —— 常用区本来就短，再套一层只会让「打开就看到结论」变慢
        T.Eq(CardCatalog.SubGroupsOf(CardCatalog.CommonGroup).Length, 0, "常用组没有二级分组");
        T.Eq(CardCatalog.SubGroupsOf(CardCatalog.AdvancedGroup).Length, 3, "高级组三个二级分组都有卡片");

        // 高级组的每张卡片都必须落在某个二级分组里。
        // 漏填的话这张卡片会掉在最后一个二级分组标题之外，界面上看起来像孤儿，而且不报错。
        foreach (var d in CardCatalog.CardsInGroup(CardCatalog.AdvancedGroup))
        {
            T.True(d.SubGroup.Length > 0, $"高级组的卡片「{d.Key}」必须属于某个二级分组");
            T.True(CardCatalog.SubGroups.Any(sg => sg.Key == d.SubGroup),
                $"卡片「{d.Key}」的二级分组「{d.SubGroup}」是已声明的");
        }
        // 反过来：常用组的卡片一个二级分组都不许有（有的话界面上会多出孤零零的标题）
        foreach (var d in CardCatalog.CardsInGroup(CardCatalog.CommonGroup))
            T.Eq(d.SubGroup, "", $"常用组的卡片「{d.Key}」不该有二级分组");

        // 并集 == 高级组全部卡片，且不重叠
        var bySub = CardCatalog.SubGroups
            .SelectMany(sg => CardCatalog.CardsInSubGroup(CardCatalog.AdvancedGroup, sg.Key)).ToArray();
        T.Eq(bySub.Length, CardCatalog.CardsInGroup(CardCatalog.AdvancedGroup).Length,
             "各二级分组的卡片数之和 == 高级组卡片数");
        T.Eq(bySub.Select(x => x.Key).Distinct().Count(), bySub.Length, "没有卡片同时属于两个二级分组");

        // 分组内容要符合语义，不是随便分的
        T.Eq(string.Join(",", CardCatalog.CardsInSubGroup(CardCatalog.AdvancedGroup, CardCatalog.PathSubGroup).Select(x => x.Key)),
             "path", "「网络路径」只有路由 / MTU 那张卡");
        T.Eq(string.Join(",", CardCatalog.CardsInSubGroup(CardCatalog.AdvancedGroup, CardCatalog.ConnSubGroup).Select(x => x.Key)),
             "nat,port", "「连接能力」= NAT + 端口");
        T.Eq(string.Join(",", CardCatalog.CardsInSubGroup(CardCatalog.AdvancedGroup, CardCatalog.ProtoSubGroup).Select(x => x.Key)),
             "ipv6,web", "「协议」= IPv4/IPv6 + HTTP/HTTPS");

        T.Eq(CardCatalog.SubGroupOfCard("web"), CardCatalog.ProtoSubGroup, "按卡片键反查二级分组");
        T.Eq(CardCatalog.SubGroupOfCard("overview"), "", "常用组卡片反查得到空串");
        T.Eq(CardCatalog.SubGroupOfCard("不存在的卡片"), "", "未知卡片反查得到空串");
    }

    // ==================================================================
    // 9. 代理环境变量去重合并
    // ==================================================================

    private static void ProxyMerging()
    {
        T.Group("ReportViewBuilder.代理去重");

        Section? ProxySection(ReportView v)
            => v.Tabs[ReportViewBuilder.LocalIndex].Sections.FirstOrDefault(x => x.Title == "代理设置");

        // 大小写重复 + 同值键合并
        var v = ReportViewBuilder.Build(new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2" } },
            Proxy = new Dictionary<string, string>
            {
                ["http_proxy"] = "http://127.0.0.1:8080",
                ["HTTP_PROXY"] = "http://127.0.0.1:8080",
                ["https_proxy"] = "http://127.0.0.1:8080",
                ["no_proxy"] = "",
            },
        });

        var sec = ProxySection(v);
        T.NotNull(sec, "本机分类里有代理设置区块");
        T.Eq(sec!.Kv.Count, 2, "大小写重复被去掉、同值键被合并，剩 2 条");
        T.True(sec.Kv.Any(r => r.Key == "http_proxy/https_proxy"), "同值键合并成 http_proxy/https_proxy");
        T.True(sec.Kv.Any(r => r.Key == "http_proxy/https_proxy" && r.Value == "http://127.0.0.1:8080"),
               "合并后的值保持不变");
        T.True(sec.Kv.Any(r => r.Key == "no_proxy"), "no_proxy 单独成行");
        T.True(sec.Kv.Any(r => r.Key == "no_proxy" && r.Value == "（未设置）"), "空值显示为「（未设置）」");

        var issue = v.Issues.FirstOrDefault(i => i.Title == "系统已配置 HTTP 代理");
        T.NotNull(issue, "配置了代理会产生提醒");
        T.True(issue!.Detail.Contains("http_proxy/https_proxy"), "提醒里列出了生效的变量名");
        T.False(issue.Detail.Contains("no_proxy"), "no_proxy 不算「已启用代理」");
        T.True(v.Chips.Any(c => c.Text == "已配置 HTTP 代理"), "结论条给出代理胶囊");
        T.True(v.Chips.Any(c => c.Text == "已配置 HTTP 代理" && c.Tone == Tone.Warn), "代理胶囊是警示色");

        // 不同值的代理要分成两条
        v = ReportViewBuilder.Build(new DiagState
        {
            Proxy = new Dictionary<string, string>
            {
                ["http_proxy"] = "http://a:1",
                ["https_proxy"] = "http://b:2",
            },
        });
        sec = ProxySection(v);
        T.Eq(sec!.Kv.Count, 2, "不同值的代理分成两条");
        T.True(sec.Kv.Any(r => r.Key == "http_proxy" && r.Value == "http://a:1"), "第一条是 http_proxy");
        T.True(sec.Kv.Any(r => r.Key == "https_proxy" && r.Value == "http://b:2"), "第二条是 https_proxy");

        // 全部空值时不产生提醒
        v = ReportViewBuilder.Build(new DiagState
        {
            Proxy = new Dictionary<string, string> { ["http_proxy"] = "", ["https_proxy"] = "" },
        });
        T.False(v.Issues.Any(i => i.Title == "系统已配置 HTTP 代理"), "代理值为空时不提醒");
        T.False(v.Chips.Any(c => c.Text == "已配置 HTTP 代理"), "代理值为空时不给胶囊");

        // 没有代理设置时不出现区块
        v = ReportViewBuilder.Build(new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2" } },
            Proxy = new Dictionary<string, string>(),
        });
        T.Eq(ProxySection(v), null, "没有代理环境变量时不出代理区块");

        // 概览页的「系统代理」也要用合并后的结果
        v = ReportViewBuilder.Build(new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2" } },
            Proxy = new Dictionary<string, string>
            {
                ["http_proxy"] = "http://p:1",
                ["HTTP_PROXY"] = "http://p:1",
            },
        });
        var overview = v.Tabs[ReportViewBuilder.OverviewIndex];
        var sum = overview.Sections.FirstOrDefault(x => x.Title == "检测摘要");
        T.NotNull(sum, "概览页有检测摘要区块");
        var proxyRow = sum!.Kv.FirstOrDefault(r => r.Key == "系统代理");
        T.NotNull(proxyRow, "摘要里有系统代理一行");
        T.Eq(proxyRow!.Note, "http_proxy=http://p:1", "摘要里的代理说明已去重");
    }

    // ==================================================================
    // 10. 端口列表折叠
    // ==================================================================

    private static void PortFolding()
    {
        T.Group("ReportViewBuilder.端口折叠");

        static PortScanReport Scan(int openCount, int closedCount)
        {
            var r = new PortScanReport { Host = "223.5.5.5" };
            for (int i = 0; i < openCount; i++)
                r.Results.Add(new PortResult { Port = 100 + i, Open = true, Ms = 7.5, Service = "SVC" });
            for (int i = 0; i < closedCount; i++)
                r.Results.Add(new PortResult { Port = 5000 + i, Open = false, Error = "超时" });
            return r;
        }

        static Section PortSection(ReportView v)
            => v.Tabs[ReportViewBuilder.PortIndex].Sections[0];

        // 关闭端口超过 24 个 → 折叠
        var v = ReportViewBuilder.Build(new DiagState { PortScan = Scan(4, 30) });
        var sec = PortSection(v);
        T.Eq(sec.Ports.Count, 28, "4 个开放 + 24 个关闭 = 28 个方块");
        T.Eq(sec.Ports.Count(p => p.Open), 4, "开放端口一个不少");
        T.True(sec.Notes.Any(n => n.Contains("另有 6 个端口未开放")), "折叠说明给出了被隐藏的数量");
        T.True(sec.Ports.Select(p => p.Port).SequenceEqual(sec.Ports.Select(p => p.Port).OrderBy(x => x)),
               "折叠后仍按端口号升序排列");

        // 关闭端口刚好 24 个 → 不折叠
        v = ReportViewBuilder.Build(new DiagState { PortScan = Scan(2, 24) });
        sec = PortSection(v);
        T.Eq(sec.Ports.Count, 26, "刚好 24 个关闭端口时不折叠");
        T.False(sec.Notes.Any(n => n.Contains("已折叠")), "不折叠时没有折叠说明");

        // 关闭端口 25 个 → 折叠 1 个
        v = ReportViewBuilder.Build(new DiagState { PortScan = Scan(2, 25) });
        sec = PortSection(v);
        T.Eq(sec.Ports.Count, 26, "25 个关闭端口折叠掉 1 个");
        T.True(sec.Notes.Any(n => n.Contains("另有 1 个端口未开放")), "折叠说明数量为 1");

        // 只有开放端口时不折叠也不提示
        v = ReportViewBuilder.Build(new DiagState { PortScan = Scan(3, 0) });
        sec = PortSection(v);
        T.Eq(sec.Ports.Count, 3, "只有开放端口时全部显示");
        T.False(sec.Notes.Any(n => n.Contains("已折叠")), "没有关闭端口时不折叠");

        // 端口方块的展示字段
        v = ReportViewBuilder.Build(new DiagState
        {
            PortScan = new PortScanReport
            {
                Host = "1.1.1.1",
                Results =
                {
                    new PortResult { Port = 443, Open = true, Ms = 7.42, Service = "HTTPS", Banner = "nginx" },
                    new PortResult { Port = 8080, Open = false, Error = "连接被拒绝" },
                },
            },
        });
        sec = PortSection(v);
        var open = sec.Ports.First(p => p.Open);
        var closed = sec.Ports.First(p => !p.Open);
        T.Eq(open.MsText, "7.4 ms", "开放端口显示握手耗时");
        T.Eq(open.Tone, Tone.Good, "开放端口是正常色");
        T.Eq(closed.MsText, "连接被拒绝", "关闭端口显示具体原因");
        T.Eq(closed.Tone, Tone.Neutral, "关闭端口是中性色");
        T.Eq(closed.Service, "—", "没有服务名的端口显示破折号");
        T.True(open.Tooltip.Contains("443"), "开放端口 tooltip 带端口号");
        T.True(open.Tooltip.Contains("nginx"), "有 Banner 时 tooltip 带上它");

        // 开放端口明细区块
        T.True(v.Tabs[ReportViewBuilder.PortIndex].Sections.Any(s => s.Title == "开放端口明细"),
               "有开放端口时给出明细区块");
        var detail = v.Tabs[ReportViewBuilder.PortIndex].Sections.First(s => s.Title == "开放端口明细");
        T.Eq(detail.Kv.Count, 1, "明细只列开放端口");
        T.True(detail.Kv[0].Key.Contains("443"), "明细的键带端口号");
        T.True(detail.Kv[0].Key.Contains("HTTPS"), "明细的键带服务名");

        // 没有开放端口时不给明细区块
        v = ReportViewBuilder.Build(new DiagState
        {
            PortScan = new PortScanReport { Host = "1.1.1.1", Results = { new PortResult { Port = 80 } } },
        });
        T.False(v.Tabs[ReportViewBuilder.PortIndex].Sections.Any(s => s.Title == "开放端口明细"),
                "没有开放端口时不给明细区块");

        // 折叠时的说明文字随错误类型变化
        v = ReportViewBuilder.Build(new DiagState
        {
            PortScan = new PortScanReport
            {
                Host = "1.1.1.1",
                Results =
                {
                    new PortResult { Port = 80, Open = false, Error = "超时" },
                    new PortResult { Port = 81, Open = false, Error = "连接被拒绝" },
                },
            },
        });
        sec = PortSection(v);
        T.True(sec.Notes.Any(n => n.Contains("超时") && n.Contains("连接被拒绝")),
               "同时出现超时与拒绝时给出对照说明");
    }

    // ==================================================================
    // 11. 公网 IP 汇总
    // ==================================================================

    private static void PublicIpDedup()
    {
        T.Group("DiagState.AllPublicIps");

        var s = new DiagState
        {
            PublicIpDirect = "9.9.9.9",
            PublicIpHttpDirect = new List<PublicIpResult>
            {
                new() { Service = "a", Ip = "9.9.9.9" },      // 与 UDP 结果重复
                new() { Service = "b", Ip = "1.1.1.1" },
                new() { Service = "c", Ip = "", Error = "超时" },
                new() { Service = "d", Ip = "解析失败" },      // 哨兵值，应被过滤
            },
            PublicIpHttpProxy = new List<PublicIpResult>
            {
                new() { Service = "a", Ip = "9.9.9.9" },
                new() { Service = "b", Ip = "1.1.1.1" },
            },
        };

        var ips = s.AllPublicIps();
        T.Eq(ips.Count, 2, "跨路径去重后剩 2 个出口 IP");
        T.Eq(ips[0], "1.1.1.1", "结果按字符串升序排列");
        T.Eq(ips[1], "9.9.9.9", "结果按字符串升序排列（第二个）");

        // 没有数据时返回空
        T.Eq(new DiagState().AllPublicIps().Count, 0, "没有数据时返回空列表");

        // 哨兵值全部被过滤
        var sentinel = new DiagState
        {
            PublicIpDirect = "不可用",
            PublicIpHttpDirect = new List<PublicIpResult> { new() { Service = "a", Ip = "解析失败" } },
        };
        T.Eq(sentinel.AllPublicIps().Count, 0, "哨兵值「不可用」「解析失败」被过滤");

        // 大小写不敏感去重（同一个 IPv6 大小写不同）
        var hex = new DiagState
        {
            PublicIpHttpDirect = new List<PublicIpResult> { new() { Service = "a", Ip = "2001:DB8::1" } },
            PublicIpHttpProxy = new List<PublicIpResult> { new() { Service = "a", Ip = "2001:db8::1" } },
        };
        T.Eq(hex.AllPublicIps().Count, 1, "IPv6 大小写不同视为同一个地址");
    }

    // ==================================================================
    // 12. 文本报告
    // ==================================================================

    // ==================================================================
    // 15. 导出脱敏
    // ==================================================================

    private static void Masking()
    {
        T.Group("Mask");

        // ---- 公网 IPv4：留前两段，抹后两段 ----
        T.Eq(Mask.Apply("出口 203.0.113.77"), "出口 203.0.***.***", "公网 IPv4 只留前两段");
        T.Eq(Mask.Apply("198.51.100.20"), "198.51.***.***", "另一个公网段同样处理");

        // ---- 私有 / 保留网段原样保留：诊断离不开，而且指向不了具体的人 ----
        T.Eq(Mask.Apply("192.168.1.100"), "192.168.1.100", "192.168 网段保留");
        T.Eq(Mask.Apply("10.0.0.1"), "10.0.0.1", "10. 网段保留");
        T.Eq(Mask.Apply("172.16.5.5"), "172.16.5.5", "172.16 网段保留");
        T.Eq(Mask.Apply("172.31.255.254"), "172.31.255.254", "172.31 仍在私有段内");
        T.Eq(Mask.Apply("172.32.0.1"), "172.32.***.***", "172.32 已经出了私有段，要打码");
        T.Eq(Mask.Apply("127.0.0.1"), "127.0.0.1", "回环地址保留");
        T.Eq(Mask.Apply("169.254.1.1"), "169.254.1.1", "链路本地保留");
        T.Eq(Mask.Apply("100.64.0.1"), "100.64.0.1", "运营商级 NAT 段保留");
        T.Eq(Mask.Apply("100.128.0.1"), "100.128.***.***", "100.128 已出 CGNAT 段");
        T.Eq(Mask.Apply("0.0.0.0"), "0.0.0.0", "0.0.0.0 保留");
        T.Eq(Mask.Apply("255.255.255.0"), "255.255.255.0", "子网掩码不能被当成公网地址打掉");

        // ---- 公共 DNS 保留：抹掉之后 DNS 那一节就没法看了 ----
        T.Eq(Mask.Apply("223.5.5.5"), "223.5.5.5", "阿里 DNS 保留");
        T.Eq(Mask.Apply("8.8.8.8"), "8.8.8.8", "Google DNS 保留");
        T.Eq(Mask.Apply("114.114.114.114"), "114.114.114.114", "114DNS 保留");
        T.Eq(Mask.Apply("223.4.4.4"), "223.4.***.***", "跟公共 DNS 同段但不是它，照常打码");

        // ---- 不误伤：版本号、时间戳、小数、越界数字 ----
        T.Eq(Mask.Apply("版本 v2.5.4"), "版本 v2.5.4", "三段版本号不是 IP");
        T.Eq(Mask.Apply("2026-10-07 18:30:53"), "2026-10-07 18:30:53", "时间戳不受影响");
        T.Eq(Mask.Apply("延迟 6.2 ms，抖动 1.4 ms"), "延迟 6.2 ms，抖动 1.4 ms", "小数不受影响");
        T.Eq(Mask.Apply("999.1.1.1"), "999.1.1.1", "有字节越界的四段数字不算地址");
        T.Eq(Mask.Apply("1.2.3.4.5"), "1.2.3.4.5", "五段数字不算地址");

        // ---- MAC：OUI 说明厂商，留着；后三段才是设备本身 ----
        T.Eq(Mask.Apply("A4-B1-C1-12-34-56"), "A4-B1-C1-***-***-***", "MAC 只留厂商前缀");
        T.Eq(Mask.Apply("a4-b1-c1-12-34-56"), "a4-b1-c1-***-***-***", "小写 MAC 同样处理");
        T.Eq(Mask.Apply("A4-B1-C1-12-34"), "A4-B1-C1-12-34", "只有五段，不是 MAC");

        // ---- 主机名：只按独立单词替换，不做子串 ----
        T.Eq(Mask.Apply("主机 PC-TEST 在线", "PC-TEST"), "主机 HOST-*** 在线", "主机名整体替换");
        T.Eq(Mask.Apply("PC-TESTNAME", "PC-TEST"), "PC-TESTNAME", "主机名不做子串替换");
        T.Eq(Mask.Apply("PC-TEST", "pc-test"), "PC-TEST", "主机名大小写敏感");
        T.Eq(Mask.Apply("PC 上的问题", "PC"), "HOST-*** 上的问题",
             "两字符的主机名照换 —— 前后有词边界兜着，不会吃掉正文");
        T.Eq(Mask.Apply("APC 上的问题", "PC"), "APC 上的问题", "紧挨着字母时不算独立单词");
        T.Eq(Mask.Apply("ABC 上的问题", "A"), "ABC 上的问题", "单字符主机名不换，太容易撞上正文");
        T.Eq(Mask.Apply("任意文本", null), "任意文本", "不传主机名时只做模式脱敏");

        // ---- 用户目录 ----
        T.Eq(Mask.Apply(@"C:\Users\PC\AppData\Local"), @"C:\Users\***\AppData\Local",
             "路径里的登录名被打码");
        T.Eq(Mask.Apply(@"C:\Windows\System32"), @"C:\Windows\System32", "其它路径不动");

        // ---- IPv6：前 64 位是运营商前缀，后 64 位是设备指纹 ----
        T.Eq(Mask.Apply("2408:8207:1234:5678:9abc:def0:1234:5678"),
             "2408:8207:****:****:****:****:****:****", "完整写法保留前两组");
        T.Eq(Mask.Apply("2001:db8::1"),
             "2001:db8:****:****:****:****:****:****", "压缩写法同样保留前两组");
        T.Eq(Mask.Apply("fe80::1"), "fe80:1:****:****:****:****:****:****",
             "链路本地 IPv6 也打码（它常常是由 MAC 推导出来的）");
        T.Eq(Mask.Apply("18:30:53"), "18:30:53", "两段冒号的时间不会被当成 IPv6");

        // ---- 幂等：结果再脱敏一次不能变样 ----
        const string sample =
            "主机 PC-TEST / 出口 203.0.113.77 / 网关 192.168.1.1\r\n" +
            "MAC A4-B1-C1-12-34-56 / DNS 223.5.5.5 / 路径 C:\\Users\\PC\\netcheck\r\n" +
            "IPv6 2408:8207:1234:5678:9abc:def0:1234:5678\r\n" +
            "时间 2026-10-07 18:30:53 / 版本 v2.5.4";
        var once = Mask.Apply(sample, "PC-TEST");
        T.Eq(Mask.Apply(once, "PC-TEST"), once, "脱敏是幂等的（跑第二遍结果不变）");

        T.False(once.Contains("PC-TEST"), "结果里不再有主机名");
        T.False(once.Contains("203.0.113.77"), "结果里不再有原始公网 IP");
        T.False(once.Contains("A4-B1-C1-12-34-56"), "结果里不再有原始 MAC");
        T.False(once.Contains("2408:8207:1234:5678"), "结果里不再有原始 IPv6");
        T.True(once.Contains("192.168.1.1"), "网关（私有地址）保留下来了");
        T.True(once.Contains("223.5.5.5"), "公共 DNS 保留下来了");
        T.True(once.Contains("18:30:53"), "时间保留下来了");

        // ---- 脱敏后的 json 必须还能被自己的模型解析回来 ----
        // 诊断包里的「原始数据.json」也要过脱敏，如果替换把引号或逗号吃掉了，
        // 收件人拿到的就是一份坏文件 —— 这条断言专门守这个。
        var st = new DiagState { Hostname = "PC-TEST", PublicIpDirect = "203.0.113.77" };
        st.Adapters = new List<AdapterInfo>
        {
            new() { Name = "以太网", Mac = "A4-B1-C1-12-34-56", IPv4 = "10.0.0.2", Gateway = "10.0.0.1" },
        };
        var rawJson = System.Text.Json.JsonSerializer.Serialize(st);
        var maskedJson = Mask.Apply(rawJson, st.Hostname);

        T.NoThrow(() => System.Text.Json.JsonSerializer.Deserialize<DiagState>(maskedJson),
                  "脱敏后的 json 仍能被模型解析回来");
        T.False(maskedJson.Contains("PC-TEST"), "json 里不含主机名");
        T.False(maskedJson.Contains("203.0.113.77"), "json 里不含原始公网 IP");
        T.False(maskedJson.Contains("A4-B1-C1-12-34-56"), "json 里不含原始 MAC");
        T.True(maskedJson.Contains("10.0.0.2"), "json 里的局域网 IP 保留");

        // 空输入不能炸
        T.Eq(Mask.Apply(""), "", "空串原样返回");
        T.Eq(Mask.Apply(null), "", "null 返回空串而不是抛异常");
    }

    // ==================================================================
    // IP 归属地
    // ==================================================================

    private static void IpGeoChecks()
    {
        T.Group("IpGeo.归属地判定");

        // ---- 私有 / 保留地址：这套判定和 Mask 打码时用的是同一个函数 ----
        // 「查不到归属地」和「不该写进诊断包」本来就是同一批地址，
        // 两处各写一份迟早会走偏 —— 这就是把它抽成 public 的理由。
        T.True(IpGeo.IsPrivateOrReservedIpv4(10, 0), "10.0.0.0/8 私有");
        T.True(IpGeo.IsPrivateOrReservedIpv4(172, 16), "172.16.0.0/12 下边界");
        T.True(IpGeo.IsPrivateOrReservedIpv4(172, 31), "172.16.0.0/12 上边界");
        T.False(IpGeo.IsPrivateOrReservedIpv4(172, 32), "172.32 已经出了 172.16/12");
        T.True(IpGeo.IsPrivateOrReservedIpv4(192, 168), "192.168.0.0/16 私有");
        T.False(IpGeo.IsPrivateOrReservedIpv4(192, 169), "192.169 不是私有段");
        T.True(IpGeo.IsPrivateOrReservedIpv4(127, 0), "127.0.0.0/8 回环");
        T.True(IpGeo.IsPrivateOrReservedIpv4(169, 254), "169.254.0.0/16 链路本地");
        T.False(IpGeo.IsPrivateOrReservedIpv4(169, 253), "169.253 不是链路本地");
        T.True(IpGeo.IsPrivateOrReservedIpv4(100, 64), "100.64.0.0/10 运营商级 NAT 下边界");
        T.True(IpGeo.IsPrivateOrReservedIpv4(100, 127), "100.64.0.0/10 上边界");
        T.False(IpGeo.IsPrivateOrReservedIpv4(100, 128), "100.128 出了运营商级 NAT 段");
        T.True(IpGeo.IsPrivateOrReservedIpv4(224, 0), "224.0.0.0/4 组播");
        T.True(IpGeo.IsPrivateOrReservedIpv4(255, 255), "掩码 255.255.255.0 也算保留（a >= 224）");
        T.False(IpGeo.IsPrivateOrReservedIpv4(223, 5), "223.5.5.5 是公网地址");

        // ---- 该不该发请求 ----
        T.False(IpGeo.IsLookupable(""), "空串不查");
        T.False(IpGeo.IsLookupable(null), "null 不查");
        T.False(IpGeo.IsLookupable("不可用"), "「不可用」这种占位文本不查");
        T.False(IpGeo.IsLookupable("10.0.0.1"), "私有 IPv4 不发请求");
        T.False(IpGeo.IsLookupable("fe80::1"), "链路本地 IPv6 不查");
        T.False(IpGeo.IsLookupable("::1"), "IPv6 回环不查");
        T.False(IpGeo.IsLookupable("fc00::1"), "ULA（fc00::/7）下边界不查");
        T.False(IpGeo.IsLookupable("fd00::1"), "ULA 不查 —— 它只在局域网内有意义");
        T.True(IpGeo.IsLookupable("2408:8207::1"), "全局 IPv6 可查");
        T.True(IpGeo.IsLookupable("223.5.5.5"), "公网 IPv4 可查");
        T.True(IpGeo.IsLookupable("  223.5.5.5  "), "两侧空白不影响判定");

        // ---- 显示取值 ----
        var s = new DiagState();
        T.Eq(IpGeo.Of(s, "223.5.5.5"), "", "还没查过时返回空串");
        T.Eq(IpGeo.Of(s, null), "", "null 返回空串");
        s.IpLocations["223.5.5.5"] = "浙江省杭州市阿里云";
        T.Eq(IpGeo.Of(s, "223.5.5.5"), "浙江省杭州市阿里云", "查到就直接给结论");
        T.Eq(IpGeo.Of(s, "  223.5.5.5  "), "浙江省杭州市阿里云", "查表前会裁掉两侧空白");
        s.IpLocations["8.8.8.8"] = IpGeo.UnknownText;
        T.Eq(IpGeo.Of(s, "8.8.8.8"), "",
             "「归属地未知」不外显 —— 否则一次断网会让每一跳后面都拖着一条，把地址本身挤没");
        T.Eq(IpGeo.Of(s, "192.168.1.1"), IpGeo.PrivateText, "私有地址不发请求也知道是局域网");
        T.Eq(IpGeo.Of(s, "fe80::1"), IpGeo.PrivateText, "链路本地 IPv6 也算局域网");
        T.Eq(IpGeo.Of(s, "不可用"), "", "占位文本不给归属地");

        // ---- 待查数量：调用方据此决定要不要显示进度 ----
        var a = new DiagState { PublicIpDirect = "1.1.1.1" };
        T.Eq(IpGeo.PendingCount(a), 1, "公网出口算一个待查目标");
        a.IpLocations["1.1.1.1"] = "美国";
        T.Eq(IpGeo.PendingCount(a), 0, "查过之后不再计入 —— 这就是重复调用不会翻倍请求量的原因");
        a.IpLocations["1.1.1.1"] = IpGeo.UnknownText;
        T.Eq(IpGeo.PendingCount(a), 0, "「查过但没查到」也算查过，不能反复重试");

        var b = new DiagState { PublicIpDirect = "10.0.0.1" };
        T.Eq(IpGeo.PendingCount(b), 0, "私有地址不占查询名额");

        var c = new DiagState
        {
            DnsServers = new List<string> { "223.5.5.5", "223.5.5.5", "119.29.29.29" },
            Traceroute = new TracerouteReport
            {
                Hops =
                {
                    new HopInfo { Ttl = 1, Address = "10.0.0.1" },
                    new HopInfo { Ttl = 2, Address = "202.112.36.1" },
                    new HopInfo { Ttl = 3 },                    // 超时跳，地址为空
                },
            },
        };
        T.Eq(IpGeo.PendingCount(c), 3, "DNS 与路由跳去重，私有地址和超时跳都不占名额");

        // 同一个地址同时出现在 DNS、路由、出口里，只能算一次
        var dup = new DiagState
        {
            PublicIpDirect = "223.5.5.5",
            DnsServers = new List<string> { "223.5.5.5" },
            Traceroute = new TracerouteReport { Hops = { new HopInfo { Ttl = 1, Address = "223.5.5.5" } } },
        };
        T.Eq(IpGeo.PendingCount(dup), 1, "同一地址跨来源出现只查一次");

        // ---- 字符集：pconline 声明并发送的是 GBK ----
        // .NET Core 默认不带 GBK（代码页 936），不显式注册的话 GetStringAsync 会抛异常。
        // 这个坑不会以任何可见的方式暴露 —— 备用源照样出结果，只是永远拿不到运营商信息。
        // 字节是硬编码的，不从 Encoding.GetEncoding 现算：那样测的就是「注册有没有生效」而不是解码本身。
        var gbkZhongGuo = new byte[] { 0xD6, 0xD0, 0xB9, 0xFA };            // 「中国」的 GBK 编码
        var utf8ZhongGuo = new byte[] { 0xE4, 0xB8, 0xAD, 0xE5, 0x9B, 0xBD }; // 「中国」的 UTF-8 编码

        T.Eq(IpGeo.Decode(gbkZhongGuo, "GBK"), "中国", "GBK 字节按响应声明的字符集解码");
        T.Eq(IpGeo.Decode(gbkZhongGuo, "gbk"), "中国", "字符集名大小写不敏感");
        T.Eq(IpGeo.Decode(gbkZhongGuo, "\"GBK\""), "中国", "charset 两侧带引号也能认");
        T.Eq(IpGeo.Decode(gbkZhongGuo, " gbk "), "中国", "charset 两侧空白被裁掉");
        T.Eq(IpGeo.Decode(utf8ZhongGuo, "utf-8"), "中国", "UTF-8 源正常解码");
        T.Eq(IpGeo.Decode(utf8ZhongGuo, null), "中国", "没声明 charset 时按 UTF-8");
        T.Eq(IpGeo.Decode(utf8ZhongGuo, ""), "中国", "charset 为空串时按 UTF-8");
        T.Eq(IpGeo.Decode(utf8ZhongGuo, "没见过的字符集"), "中国", "认不出的字符集名落到 UTF-8");
        T.Eq(IpGeo.Decode(Array.Empty<byte>(), "GBK"), "", "空字节返回空串");
    }

    private static void GeoInViews()
    {
        T.Group("归属地展示");

        var s = new DiagState
        {
            PublicIpDirect = "203.0.113.10",
            PublicIpHttpDirect = new List<PublicIpResult>
            {
                new() { Service = "ipify", Ip = "203.0.113.10", Seconds = 0.21 },
            },
            DnsServers = new List<string> { "223.5.5.5", "10.0.0.1" },
            DnsServerTests = new List<DnsServerTest>
            {
                new() { Server = "223.5.5.5", Ms = 12, Answers = 2 },
                new() { Server = "10.0.0.1", Ms = 1, Answers = 2 },
            },
            Traceroute = new TracerouteReport
            {
                Target = "223.5.5.5", TargetIp = "223.5.5.5", Reached = true,
                Hops =
                {
                    new HopInfo { Ttl = 1, Address = "192.168.1.1", Ms = 0.6, Status = "TtlExpired" },
                    new HopInfo { Ttl = 2, Address = "202.112.36.1", Ms = 4.8, Status = "TtlExpired" },
                    new HopInfo { Ttl = 3, Address = "223.5.5.5", Ms = 6.8, Reached = true },
                    new HopInfo { Ttl = 4, Status = "Timeout" },
                },
            },
        };
        s.IpLocations["203.0.113.10"] = "示例 · 华东 电信";
        s.IpLocations["223.5.5.5"] = "浙江省杭州市阿里云";
        s.IpLocations["202.112.36.1"] = "北京市 教育网";

        var v = ReportViewBuilder.Build(s);

        // ---- 出口 IP（现在有独立卡片）----
        var egress = v.Tabs[ReportViewBuilder.EgressIndex].Sections.First(x => x.Title == "公网出口");
        var udp = egress.Kv.First(r => r.Key.Contains("UDP"));
        T.Eq(udp.Geo, "示例 · 华东 电信", "原生 UDP 出口后面带归属地");
        T.Eq(egress.Kv.First(r => r.Key.Contains("HTTP 直连")).Geo, "示例 · 华东 电信",
             "HTTP 直连出口后面也带归属地");

        // ---- DNS 服务器（现在叫「当前 DNS」）----
        var dnsBars = v.Tabs[ReportViewBuilder.DnsIndex].Sections
            .First(x => x.Title == "当前 DNS").Bars;
        T.Eq(dnsBars.First(b => b.Name == "223.5.5.5").Geo, "浙江省杭州市阿里云", "公网 DNS 带归属地");
        T.Eq(dnsBars.First(b => b.Name == "10.0.0.1").Geo, IpGeo.PrivateText,
             "内网 DNS 标「局域网」，不发请求");

        // ---- 路由每一跳 ----
        var hops = v.Tabs[ReportViewBuilder.PathIndex].Sections
            .First(x => x.Title == "路由追踪").Hops;
        T.Eq(hops.Count, 4, "四跳都渲染出来");
        T.Eq(hops[0].Geo, IpGeo.PrivateText, "第一跳（网关）是局域网");
        T.Eq(hops[1].Geo, "北京市 教育网", "中间跳带归属地");
        T.Eq(hops[2].Geo, "浙江省杭州市阿里云", "到达目标那一跳也带归属地");
        T.Eq(hops[3].Geo, "", "超时跳没有地址，也就没有归属地");

        // ---- 概览的出口行 ----
        var sum = v.Tabs[ReportViewBuilder.OverviewIndex].Sections.First(x => x.Title == "检测摘要");
        T.Eq(sum.Kv.First(r => r.Key == "公网出口").Geo, "示例 · 华东 电信", "概览的出口行也带归属地");

        // ---- 多出口时问题条目里要逐个标出来 ----
        var multi = new DiagState
        {
            PublicIpDirect = "1.1.1.1",
            PublicIpHttpDirect = new List<PublicIpResult> { new() { Service = "ipify", Ip = "8.8.8.8" } },
        };
        multi.IpLocations["1.1.1.1"] = "美国";
        multi.IpLocations["8.8.8.8"] = "美国";
        var mv = ReportViewBuilder.Build(multi);
        var issue = mv.Issues.First(i => i.Title.Contains("个不同的公网出口"));
        T.True(issue.Detail.Contains("1.1.1.1（美国）"), "多出口的问题详情里带上归属地");
        T.True(issue.Detail.Contains("8.8.8.8（美国）"), "两个地址都要标");

        // 没有归属地时不能凭空多出一对空括号
        var bare = new DiagState
        {
            PublicIpDirect = "1.1.1.1",
            PublicIpHttpDirect = new List<PublicIpResult> { new() { Service = "ipify", Ip = "8.8.8.8" } },
        };
        var bv = ReportViewBuilder.Build(bare);
        var bareIssue = bv.Issues.First(i => i.Title.Contains("个不同的公网出口"));
        T.False(bareIssue.Detail.Contains("（）"), "查不到归属地时不留空括号");
    }

    // ==================================================================
    // IPv6
    // ==================================================================

    private static void Ipv6Checks()
    {
        T.Group("Ipv6Probe");

        // ---- 地址分类 ----
        // u 位（接口标识符第 1 字节的 0x02）置位 = 固定地址，清零 = 随机生成（RFC 4941 的隐私扩展地址）
        var adapters = new List<AdapterInfo>
        {
            new()
            {
                Name = "以太网",
                IPv6Addresses =
                {
                    "2408:8207:1234:5678:9c2b:3c4d:5e6f:7080",   // 0x9c → u 位清零 → 临时
                    "2408:8207:1234:5678:9abc:def0:1234:5678",   // 0x9a → u 位置位 → 固定
                    "fd00:1234:5678::1",                          // ULA
                    "fe80::1",                                    // 链路本地（GetAdapters 已经滤掉，这里再确认一次）
                },
            },
            new() { Name = "WLAN", IPv6Addresses = { "2408:8207:1234:5678:9c2b:3c4d:5e6f:7080" } },
        };

        var list = Ipv6Probe.CollectAddresses(adapters);
        T.Eq(list.Count, 3, "重复地址跨网卡去重，链路本地被排除");
        T.Eq(list[0].Kind, "全局", "全局地址排最前");
        T.Eq(list.Count(x => x.Kind == "全局"), 2, "两条全局地址都保留 —— 临时地址正是实际出网用的那条");
        T.Eq(list.First(x => x.Address.EndsWith("9c2b:3c4d:5e6f:7080")).IsTemporary, true,
             "u 位清零判为临时地址");
        T.Eq(list.First(x => x.Address.EndsWith("9abc:def0:1234:5678")).IsTemporary, false,
             "u 位置位判为固定地址");
        T.Eq(list.Last().Kind, "内网", "ULA 排在内网档");
        T.False(list.Any(x => x.Kind == "链路本地"), "链路本地地址不进列表");

        // ---- 结论分档 ----
        var r = new Ipv6Report();
        T.Eq(r.Verdict, "未启用 IPv6", "没有地址时结论是「未启用」");
        T.False(r.Enabled, "没有全局地址时 Enabled 为假");
        T.True(r.Detail.Contains("不是故障"), "无 IPv6 的说明要写清「这不是故障」");

        r.Addresses.Add(new Ipv6AddressInfo { Address = "fd00::1", Kind = "内网" });
        T.False(r.Enabled, "只有 ULA 不算启用 —— 它出不了公网");
        T.Eq(r.Verdict, "未启用 IPv6", "只有 ULA 时结论仍是未启用");

        r.Addresses.Add(new Ipv6AddressInfo { Address = "2408:8207::1", Kind = "全局" });
        T.True(r.Enabled, "有全局地址即启用");
        T.Eq(r.Verdict, "探测未完成", "有地址但还没探测时给中性结论");

        r.Pings.Add(new Ipv6PingResult { Region = "国内", Error = "超时" });
        r.Pings.Add(new Ipv6PingResult { Region = "国外", Error = "超时" });
        T.Eq(r.Verdict, "有地址但不通", "两个目标都不通 = 假双栈");
        T.True(r.Detail.Contains("假双栈"), "「有地址但不通」的说明要点出假双栈这个成因");

        r.Pings.Clear();
        r.Pings.Add(new Ipv6PingResult { Region = "国内", Ms = 12 });
        r.Pings.Add(new Ipv6PingResult { Region = "国外", Error = "超时" });
        T.Eq(r.Verdict, "只能到国内", "国内通、国外不通");

        r.Pings.Clear();
        r.Pings.Add(new Ipv6PingResult { Region = "国外", Ms = 178 });
        r.Pings.Add(new Ipv6PingResult { Region = "国内", Error = "超时" });
        T.Eq(r.Verdict, "仅国际可达", "反过来也认得出来");

        r.Pings.Clear();
        r.Pings.Add(new Ipv6PingResult { Region = "国内", Ms = 12 });
        r.Pings.Add(new Ipv6PingResult { Region = "国外", Ms = 178 });
        T.Eq(r.Verdict, "双栈正常", "都通");

        // ---- 探测目标 ----
        T.True(Ipv6Probe.DomesticTarget.StartsWith("2400:"), "国内目标是 AliDNS 的 IPv6 地址");
        T.True(Ipv6Probe.ForeignTarget.StartsWith("2606:4700"), "国外目标是 Cloudflare 的 IPv6 地址");
    }

    private static void Ipv6View()
    {
        T.Group("IPv6 卡片");

        // ---- 无 IPv6：中性色，且不能产生问题条目 ----
        var s = new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2" } },
            Ipv6 = new Ipv6Report(),
        };
        var v = ReportViewBuilder.Build(s);
        var sec = v.Tabs[ReportViewBuilder.Ipv6Index].Sections.First(x => x.Title == "IPv6 状态");

        T.True(v.Tabs[ReportViewBuilder.Ipv6Index].HasData, "IPv6 卡片有数据（哪怕结论是「没启用」）");
        T.Eq(sec.Verdict, "未启用 IPv6", "无 IPv6 时结论是「未启用 IPv6」");
        T.Eq(sec.VerdictTone, Tone.Neutral,
             "「没启用 IPv6」必须是中性色 —— 大量网络本来就只有 IPv4，标成故障是凭空制造焦虑");
        T.False(v.Issues.Any(i => i.TargetTab == ReportViewBuilder.Ipv6Index),
                "没有 IPv6 不产生问题条目（有颜色就必须有问题条目，反之亦然）");
        T.True(v.Tabs[ReportViewBuilder.OverviewIndex].Sections
                .First(x => x.Title == "检测摘要").Kv.Any(r => r.Key == "IPv6"),
               "概览的摘要里有 IPv6 一行");

        // ---- 有地址但不通：严重，且必须有对应问题条目 ----
        var bad = new DiagState
        {
            Adapters = new List<AdapterInfo> { new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2" } },
            Ipv6 = new Ipv6Report
            {
                Addresses = { new Ipv6AddressInfo { Address = "2408:8207::1", Kind = "全局" } },
                Pings =
                {
                    new Ipv6PingResult { Region = "国内", Error = "超时" },
                    new Ipv6PingResult { Region = "国外", Error = "超时" },
                },
            },
        };
        var bv = ReportViewBuilder.Build(bad);
        var bsec = bv.Tabs[ReportViewBuilder.Ipv6Index].Sections.First(x => x.Title == "IPv6 状态");
        T.Eq(bsec.VerdictTone, Tone.Bad, "有地址却完全不通 = 严重");
        var bIssue = bv.Issues.FirstOrDefault(i => i.TargetTab == ReportViewBuilder.Ipv6Index);
        T.NotNull(bIssue, "有颜色就必须有问题条目");
        T.Eq(bIssue!.Tone, Tone.Bad, "问题条目也是严重级");
        T.True(bIssue.Detail.Contains("假双栈"), "问题详情解释了成因");

        // ---- 只能到国内：提醒 ----
        var half = new DiagState
        {
            Ipv6 = new Ipv6Report
            {
                Addresses = { new Ipv6AddressInfo { Address = "2408:8207::1", Kind = "全局" } },
                Pings =
                {
                    new Ipv6PingResult { Region = "国内", Ms = 12 },
                    new Ipv6PingResult { Region = "国外", Error = "超时" },
                },
            },
        };
        var hv = ReportViewBuilder.Build(half);
        T.Eq(hv.Tabs[ReportViewBuilder.Ipv6Index].Sections.First(x => x.Title == "IPv6 状态").VerdictTone,
             Tone.Warn, "只能到国内 = 提醒");
        T.True(hv.Issues.Any(i => i.TargetTab == ReportViewBuilder.Ipv6Index), "同样要有一条问题条目");

        // ---- 正常：良好，且没有问题条目 ----
        var good = new DiagState
        {
            Ipv6 = new Ipv6Report
            {
                Addresses = { new Ipv6AddressInfo { Address = "2408:8207::1", Kind = "全局" } },
                EgressIp = "2408:8207::1",
                Pings =
                {
                    new Ipv6PingResult { Region = "国内", Ms = 12 },
                    new Ipv6PingResult { Region = "国外", Ms = 178 },
                },
            },
        };
        good.IpLocations["2408:8207::1"] = "示例 · 华东 电信";
        var gv = ReportViewBuilder.Build(good);
        var gsec = gv.Tabs[ReportViewBuilder.Ipv6Index].Sections.First(x => x.Title == "IPv6 状态");
        T.Eq(gsec.VerdictTone, Tone.Good, "双栈正常 = 良好");
        T.Eq(gsec.Kv.First(r => r.Key == "IPv6 出口").Geo, "示例 · 华东 电信",
             "IPv6 出口也带归属地");
        T.False(gv.Issues.Any(i => i.TargetTab == ReportViewBuilder.Ipv6Index), "正常时不报问题");

        // ---- 地址明细：临时地址要给解释 ----
        var temp = new DiagState
        {
            Ipv6 = new Ipv6Report
            {
                Addresses =
                {
                    new Ipv6AddressInfo { Address = "2408:8207::1", Kind = "全局", IsTemporary = true },
                    new Ipv6AddressInfo { Address = "fd00::1", Kind = "内网" },
                },
            },
        };
        var tv = ReportViewBuilder.Build(temp);
        var addrSec = tv.Tabs[ReportViewBuilder.Ipv6Index].Sections.First(x => x.Title == "本机 IPv6 地址");
        T.True(addrSec.Kv.Any(r => r.Key.Contains("临时")), "临时地址在键里标出来");
        T.True(addrSec.Notes.Any(n => n.Contains("白名单")),
               "临时地址要解释清楚「地址会变，按 IP 做白名单会失效」");
        T.True(addrSec.Notes.Any(n => n.Contains("ULA")), "ULA 也要解释清楚");
    }

    // ==================================================================
    // 网络质量摘要（网关 / 延迟 / 丢包 / 抖动）
    // ==================================================================

    private static void QualitySummary()
    {
        T.Group("网络质量摘要");

        // 阈值：零丢包必须显式归到 Good。
        // 原来的两档写法（非 Bad 即 Warn）会把 0% 画成橙色 —— 指标卡上等于给完全正常的网络报警。
        T.Eq(Thresholds.Loss(0), Tone.Good, "零丢包是正常色");
        T.Eq(Thresholds.Loss(1), Tone.Warn, "有一点丢包就是提醒");
        T.Eq(Thresholds.Loss(Thresholds.LossBadPercent), Tone.Bad, "丢包到严重档");
        T.Eq(Thresholds.Jitter(Thresholds.JitterWarnMs - 1), Tone.Good, "阈值以下抖动正常");
        T.Eq(Thresholds.Jitter(Thresholds.JitterWarnMs), Tone.Warn, "抖动达到阈值是提醒");

        var s = new DiagState
        {
            Latency = new LatencyReport
            {
                Gateway = new LatencyTarget
                {
                    Name = "网关", Host = "192.168.1.1", Method = "ICMP",
                    Min = 0.3, Avg = 0.4, Max = 0.9, Loss = 0,
                    Samples = { 0.4, 0.3, 0.5, 0.4 },
                },
                Targets =
                {
                    new LatencyTarget
                    {
                        Name = "淘宝", Host = "www.taobao.com", Category = "网页浏览", Method = "ICMP",
                        Min = 7, Avg = 7.2, Max = 8, Loss = 0, Samples = { 7.0, 7.4, 7.2 },
                    },
                    // 游戏路径在丢包 —— 而代表延迟的「淘宝」是 0%，
                    // 所以「丢包取最差目标」这条规则正是为了不让它被盖掉
                    new LatencyTarget
                    {
                        Name = "Steam", Host = "store.steampowered.com", Category = "游戏", Method = "ICMP",
                        Min = 70, Avg = 72, Max = 74, Loss = 8, Samples = { 71, 73, 72 },
                    },
                },
            },
        };

        var v = ReportViewBuilder.Build(s);
        var q = v.Tabs[ReportViewBuilder.LinkIndex].Sections.First(x => x.Title == "网络质量");

        T.Eq(q.Kv.Count, 4, "网关 / 延迟 / 抖动 / 丢包 四行");
        T.Eq(q.Kv[0].Key, "网关", "网关排第一 —— 诊断链从「本机到路由器」开始");
        T.Eq(q.Kv[0].Tone, Tone.Good, "0.4 ms 的网关是正常色");
        T.True(q.Kv[0].Note.Contains("本机 → 路由器"), "网关那一行点明它测的是哪一段");
        T.Eq(q.Kv[1].Key, "延迟", "第二行是延迟");
        T.True(q.Kv[1].Note.Contains("路由器 → 互联网"), "延迟那一行点明它测的是哪一段");
        T.Eq(q.Kv[2].Key, "抖动", "第三行是抖动");
        T.Eq(q.Kv[3].Key, "丢包率", "最后一行是丢包率");

        T.Eq(q.Kv[3].Value, "8.0%", "丢包取最差目标，不是代表目标");
        T.True(q.Kv[3].Note.Contains("Steam"), "并且写明是谁在丢");
        T.Eq(q.Kv[3].Tone, Tone.Warn, "8% 是提醒级");

        // ---- 指标卡：延迟 / 丢包 / 抖动 三个挨着放 ----
        T.Eq(v.Cards.Count, 6, "六张指标卡");
        T.Eq(string.Join(",", v.Cards.Select(c => c.Title)),
             "公网出口,网络延迟,丢包率,抖动,下载带宽,NAT 类型",
             "延迟、丢包、抖动必须相邻 —— 「平均延迟正常」不等于「网络正常」");
        T.Eq(v.Cards.First(c => c.Title == "丢包率").Value, "8.0%", "丢包指标卡取最差值");
        T.True(v.Cards.First(c => c.Title == "丢包率").Sub.Contains("Steam"), "副标题指出最差目标");
        T.Eq(v.Cards.First(c => c.Title == "丢包率").Tone, Tone.Warn, "8% 是提醒色");
        T.Eq(v.Cards.First(c => c.Title == "网络延迟").Value, "7.2 ms", "延迟仍取代表目标（网页类）");

        // ---- 零丢包：绿色 + 说明「全部目标无丢包」 ----
        var clean = new DiagState
        {
            Latency = new LatencyReport
            {
                Targets =
                {
                    new LatencyTarget
                    {
                        Name = "淘宝", Host = "h", Category = "网页浏览", Method = "ICMP",
                        Min = 7, Avg = 7, Max = 7, Loss = 0, Samples = { 7, 7 },
                    },
                },
            },
        };
        var cv = ReportViewBuilder.Build(clean);
        var lossCard = cv.Cards.First(c => c.Title == "丢包率");
        T.Eq(lossCard.Tone, Tone.Good, "0% 丢包是正常色");
        T.Eq(lossCard.Sub, "全部目标无丢包", "0% 时的说明");
        T.False(cv.Issues.Any(i => i.Title.Contains("丢包")), "零丢包不产生问题条目");

        // ---- 网关延迟偏高：卡片与问题清单必须同色调 ----
        var badGw = new DiagState
        {
            Latency = new LatencyReport
            {
                Gateway = new LatencyTarget
                {
                    Name = "网关", Host = "10.0.0.1", Method = "ICMP",
                    Min = 20, Avg = 22, Max = 25, Loss = 0, Samples = { 20, 22, 25 },
                },
            },
        };
        var bv = ReportViewBuilder.Build(badGw);
        var bq = bv.Tabs[ReportViewBuilder.LinkIndex].Sections.First(x => x.Title == "网络质量");
        T.Eq(bq.Kv[0].Tone, Tone.Bad, "22 ms 的网关是严重色");
        T.True(bv.Issues.Any(i => i.Title.Contains("网关延迟偏高")), "同色调的问题条目必须存在");
    }

    // ==================================================================
    // HTTP / HTTPS 分级连通性
    // ==================================================================

    private static void WebProbeChecks()
    {
        T.Group("WebProbe 目标解析");

        T.True(WebProbe.TryParseTarget("example.com", out var h1, out var p1), "裸域名认得出来");
        T.Eq(h1, "example.com", "主机名");
        T.Eq(p1, "/", "没写路径时默认 /");

        T.True(WebProbe.TryParseTarget("https://example.com/a/b", out var h2, out var p2), "带协议与路径");
        T.Eq(h2, "example.com", "协议头被剥掉");
        T.Eq(p2, "/a/b", "路径保留");

        T.True(WebProbe.TryParseTarget("http://example.com:8080/x", out var h3, out var p3), "带端口");
        T.Eq(h3, "example.com", "端口被去掉（本项固定测 443，留着端口只会让人误以为测的是它）");
        T.Eq(p3, "/x", "路径仍然保留");

        T.True(WebProbe.TryParseTarget("  example.com.  ", out var h4, out _), "首尾空白与末尾的点");
        T.Eq(h4, "example.com", "都清掉了");

        T.True(WebProbe.TryParseTarget("https://user@example.com/", out var h5, out _), "带 user@ 前缀");
        T.Eq(h5, "example.com", "user@ 被剥掉");

        T.True(WebProbe.TryParseTarget("example.com?q=1", out var h6, out _), "带查询串");
        T.Eq(h6, "example.com", "查询串被剥掉");

        // Punycode 写法要放行 —— 中文域名被拒绝之后，这是用户唯一的补救路径
        T.True(WebProbe.TryParseTarget("xn--fsqu00a.xn--0zwm56d", out var h7, out _),
               "Punycode 形式的中文域名是合法目标");
        T.Eq(h7, "xn--fsqu00a.xn--0zwm56d", "原样保留，不做任何转码");

        // 认不出来的一律拒绝 —— 悄悄测一个猜出来的域名，比不测更糟
        foreach (var bad in new[]
        {
            "", "   ", "localhost", "/path", "http://", "a b.com",
            // 裸 IP：TLS 要按域名校验，拿 IP 去连会把「填错目标」伪装成「TLS 握手失败」
            "192.168.1.1", "1.2.3.4", "::1",
            // 非 ASCII：真实握手要先转 Punycode，与其内部悄悄转码不如让用户自己填
            "例子.测试", "中文.中国", "café.com",
            // 空标签：一眼就是笔误（注意末尾的点是 FQDN 写法，会被正常清掉，不算错）
            "example..com", ".example.com",
        })
            T.False(WebProbe.TryParseTarget(bad, out _, out _), $"认不出来的输入被拒绝：「{bad}」");

        T.Eq(WebProbe.BuiltInTargets.Length, 3, "三个内置目标");
        T.Eq(WebProbe.BuiltInTargets.Count(x => x.Region == "国内"), 2, "两个国内");
        T.Eq(WebProbe.BuiltInTargets.Count(x => x.Region == "境外"), 1, "一个境外 —— 它回答「能不能上外网」");
        foreach (var (n, host, _) in WebProbe.BuiltInTargets)
        {
            T.True(host.Contains('.'), $"内置目标「{n}」是域名而不是裸 IP（TLS 要校验 SNI）");
            T.False(host.Contains("://"), $"内置目标「{n}」不带协议前缀");
        }
        T.Eq(WebProbe.Port, 443, "探测端口固定 443");
    }

    private static void WebReportChecks()
    {
        T.Group("WebReport 结论与分层");

        static WebStep Step(string name, bool ok) => new()
        {
            Name = name, Ok = ok, Ms = ok ? 10 : 0, Error = ok ? "" : "超时",
        };

        static WebTargetResult Tgt(string name, string region, params bool[] chain)
        {
            var names = new[] { "DNS", "TCP", "TLS", "HTTP" };
            var t = new WebTargetResult { Name = name, Region = region, Host = name };
            for (int i = 0; i < chain.Length; i++) t.Steps.Add(Step(names[i], chain[i]));
            return t;
        }

        // 全通
        var all = new WebReport { Targets = { Tgt("a", "国内", true, true, true, true), Tgt("b", "境外", true, true, true, true) } };
        T.True(all.AllOk, "四层全过");
        T.Eq(all.Verdict, "全部正常", "结论");
        T.True(all.Detail.Contains("四层全部通过"), "详情说明四层都过");

        // 部分失败
        var part = new WebReport { Targets = { Tgt("a", "国内", true, true, true, true), Tgt("b", "境外", true, false) } };
        T.False(part.AllOk, "有目标失败");
        T.True(part.AnyOk, "但还有目标能通");
        T.Eq(part.Verdict, "部分目标不可达", "部分失败");
        T.True(part.Detail.Contains("本机网络没断"), "还有目标能通 → 先定性：本机网络是好的");
        T.True(part.Detail.Contains("TCP 连接建不起来"), "再按层级给线索");

        // 全部失败且卡在同一层 → 共因，把层级写进结论
        var same = new WebReport { Targets = { Tgt("a", "国内", true, true, false), Tgt("b", "境外", true, true, false) } };
        T.False(same.AnyOk, "全失败");
        T.Eq(same.Verdict, "TLS 层全部失败", "共因层级直接写进结论");
        T.True(same.Detail.Contains("证书"), "详情解释这一层常见的原因");
        T.False(same.Detail.Contains("本机网络没断"), "全失败时不能说「本机网络是好的」");

        // 全部失败但卡在不同层 → 不能硬指一层
        var diff = new WebReport { Targets = { Tgt("a", "国内", false), Tgt("b", "境外", true, false) } };
        T.Eq(diff.Verdict, "全部目标不可达", "层级不一致时不硬指某一层");
        T.True(diff.Detail.Contains("目标侧差异"), "说明这不是本机的共性问题");

        // 每一层的解释文案都要有，且各不相同
        var hints = new[] { "DNS", "TCP", "TLS", "HTTP" }.Select(layer =>
        {
            var r = new WebReport { Targets = { Tgt("a", "国内", true, true, true, true) } };
            var names = new[] { "DNS", "TCP", "TLS", "HTTP" };
            r.Targets[0].Steps.Clear();
            for (int i = 0; i < names.Length; i++) r.Targets[0].Steps.Add(Step(names[i], names[i] != layer));
            return r.Detail;
        }).ToList();
        T.True(hints.All(x => x.Length > 0), "四层的解释文案都不是空的");
        T.Eq(hints.Distinct().Count(), 4, "四层的解释各不相同");

        // 空报告
        T.Eq(new WebReport().Verdict, "未检测", "没测过");
        T.Eq(new WebReport().Detail, "", "没测过就没有解释");

        // FailedStep / Blame
        var tlsFail = Tgt("x", "国内", true, true, false);
        T.Eq(tlsFail.FailedStep, "TLS", "第一个失败的层级");
        T.Eq(tlsFail.Blame, "TLS 握手失败", "失败层级有人话解释");
        T.False(tlsFail.Ok, "有失败层就不算通");

        var ok = Tgt("y", "国内", true, true, true, true);
        T.Eq(ok.FailedStep, "", "全通时没有失败层");
        T.Eq(ok.Blame, "", "全通时也没有失败解释");
        T.True(ok.Ok, "全通");
    }

    private static void WebView()
    {
        T.Group("HTTP / HTTPS 卡片");

        static WebStep S(string name, bool ok, double ms, string detail = "", string err = "") => new()
        {
            Name = name, Ok = ok, Ms = ms, Detail = detail, Error = err,
        };

        // 没测过 → 空卡片
        var empty = ReportViewBuilder.Build(new DiagState());
        T.False(empty.Tabs[ReportViewBuilder.WebIndex].HasData, "没测过就没有数据");
        T.False(empty.Issues.Any(i => i.TargetTab == ReportViewBuilder.WebIndex), "没测过不产生问题条目");

        var s = new DiagState
        {
            Web = new WebReport
            {
                Targets =
                {
                    new WebTargetResult
                    {
                        Name = "百度", Host = "www.baidu.com", Region = "国内",
                        ResolvedIp = "110.242.68.66", HttpStatus = 200,
                        Steps =
                        {
                            S("DNS", true, 11, "110.242.68.66 · 共 2 条记录"),
                            S("TCP", true, 9, "110.242.68.66:443"),
                            S("TLS", true, 34, "Tls13 · 证书 2027-01-08 到期"),
                            S("HTTP", true, 41, "HTTP/1.1 200 OK"),
                        },
                    },
                    new WebTargetResult
                    {
                        Name = "www.example.com", Host = "www.example.com", Region = "自定义",
                        ResolvedIp = "93.184.215.14",
                        Steps =
                        {
                            S("DNS", true, 33, "93.184.215.14 · 共 1 条记录"),
                            S("TCP", false, 3000, err: "连接超时"),
                        },
                    },
                },
            },
        };

        var v = ReportViewBuilder.Build(s);
        var t = v.Tabs[ReportViewBuilder.WebIndex];
        T.True(t.HasData, "有数据");
        T.Eq(t.Title, "HTTP / HTTPS", "卡片标题");

        // ---- 结论段 ----
        T.Eq(t.Sections[0].Title, "结论", "第一段是结论");
        T.Eq(t.Sections[0].Verdict, "部分目标不可达", "结论文字");
        T.Eq(t.Sections[0].VerdictTone, Tone.Warn,
             "还有目标能通说明本机网络没断，问题在个别目标上 —— 给提醒而不是严重");
        // 只有一个目标失败、且只有一层 → 走「层级线索」分支（「目标侧差异」那条留给
        // 多个目标卡在不同层的情况，在 WebReportChecks 里覆盖）。
        // 这里额外守住顺序：先定性（本机网络是好的）再给线索，反了会让人误判成自己断网。
        T.True(t.Sections[0].Notes.Any(n => n.Contains("本机网络没断")), "结论先给定性");
        T.True(t.Sections[0].Notes.Any(n => n.Contains("TCP 连接建不起来")), "再按层级给线索");

        // ---- 失败的目标：段标题直接写出卡在哪一层 ----
        var bad = t.Sections.First(x => x.Title == "www.example.com");
        T.Eq(bad.Verdict, "卡在 TCP 层 · TCP 连接建立失败", "段标题就是结论");
        T.Eq(bad.VerdictTone, Tone.Bad, "失败层是严重色");
        T.Eq(bad.Kv.Count, 2, "第一层失败就停，只渲染跑过的两步");
        T.Eq(bad.Kv[0].Key, "DNS", "第一步是 DNS");
        T.Eq(bad.Kv[1].Key, "TCP", "第二步是 TCP");
        T.Eq(bad.Kv[0].Tone, Tone.Good, "通过的那步是绿色");
        T.Eq(bad.Kv[1].Tone, Tone.Bad, "失败的那步是红色");
        T.Eq(bad.Kv[1].Value, "失败", "失败那步显示「失败」而不是耗时");
        T.Eq(bad.Kv[1].Note, "连接超时", "失败原因写在备注里");
        T.True(bad.Hint.Contains("93.184.215.14"), "段标题带上解析到的地址");

        // ---- 通过的目标：四层齐全 ----
        var ok = t.Sections.First(x => x.Title == "百度");
        T.Eq(ok.Verdict, "", "全通的目标不写「卡在…」");
        T.Eq(ok.Kv.Count, 4, "四层都渲染出来");
        T.Eq(string.Join(",", ok.Kv.Select(x => x.Key)), "DNS,TCP,TLS,HTTP", "顺序就是探测顺序");
        T.Eq(ok.Kv[3].Note, "HTTP/1.1 200 OK", "HTTP 层显示状态行");
        T.True(ok.Kv.All(x => x.Tone == Tone.Good), "四层全绿");

        // ---- 问题清单 ----
        var issue = v.Issues.First(i => i.TargetTab == ReportViewBuilder.WebIndex);
        T.True(issue.Title.Contains("卡在 TCP 层"), "问题标题写明层级");
        T.Eq(issue.Tone, Tone.Bad, "非境外目标失败 = 严重");
        T.True(issue.Detail.Contains("93.184.215.14"), "详情里带解析到的地址");
        T.True(issue.Detail.Contains("防火墙") || issue.Detail.Contains("不可达"), "详情给出可能原因");

        // ---- 概览摘要 + 结论条 ----
        var sum = v.Tabs[ReportViewBuilder.OverviewIndex].Sections.First(x => x.Title == "检测摘要");
        var row = sum.Kv.FirstOrDefault(r => r.Key == "HTTP / HTTPS");
        T.NotNull(row, "摘要里有 HTTP / HTTPS 一行");
        T.Eq(row!.Value, "部分目标不可达", "摘要里的结论");
        T.True(row.Note.Contains("卡在TCP"), "摘要的备注逐个目标写出卡在哪层");
        T.True(v.Chips.Any(c => c.Text == "网页 部分目标不可达"), "结论条上有一枚网页胶囊");

        // ---- 境外目标不通：降为提醒，并说明这可能正常 ----
        var foreign = new DiagState
        {
            Web = new WebReport
            {
                Targets =
                {
                    new WebTargetResult
                    {
                        Name = "Cloudflare", Host = "www.cloudflare.com", Region = "境外",
                        Steps = { S("DNS", false, 2000, err: "超时") },
                    },
                },
            },
        };
        var fv = ReportViewBuilder.Build(foreign);
        var fi = fv.Issues.First(i => i.TargetTab == ReportViewBuilder.WebIndex);
        T.Eq(fi.Tone, Tone.Warn, "境外目标不通降为提醒 —— 很可能只是访问不了境外站点");
        T.True(fi.Detail.Contains("境外"), "详情里点明这是境外目标");
        T.Eq(fv.Tabs[ReportViewBuilder.WebIndex].Sections[0].VerdictTone, Tone.Bad,
             "但卡片本身仍然如实标红（一个目标都没通）");

        // ---- 4xx/5xx：链路是通的，不判失败，但要说明 ----
        var four = new DiagState
        {
            Web = new WebReport
            {
                Targets =
                {
                    new WebTargetResult
                    {
                        Name = "某站", Host = "x.com", Region = "国内", HttpStatus = 403,
                        ResolvedIp = "1.2.3.4",
                        Steps =
                        {
                            S("DNS", true, 10, "1.2.3.4"), S("TCP", true, 8, "1.2.3.4:443"),
                            S("TLS", true, 30, "Tls13"), S("HTTP", true, 35, "HTTP/1.1 403 Forbidden"),
                        },
                    },
                },
            },
        };
        var qv = ReportViewBuilder.Build(four);
        T.True(qv.Tabs[ReportViewBuilder.WebIndex].Sections
                .First(x => x.Title == "某站").Notes.Any(n => n.Contains("403")),
               "4xx 要给一句说明，否则用户会以为「检测通过 = 网站正常」");
        T.False(qv.Issues.Any(i => i.TargetTab == ReportViewBuilder.WebIndex),
                "4xx 不产生问题条目 —— 链路全程通畅，只是服务端拒绝了这个请求");
        T.Eq(qv.Tabs[ReportViewBuilder.WebIndex].Sections[0].VerdictTone, Tone.Good, "结论仍是正常");
    }

    /// <summary>
    /// 「当前 DNS」和「对比 DNS」分成两段。
    ///
    /// 分开的价值是回答一个「当前 DNS 36 ms」永远回答不了的问题：换个 DNS 会不会更快。
    /// 这几条断言盯三件事：两段确实分开、对比段的配色是**相对**的（不按绝对阈值报红）、
    /// 以及三种结论分支各自走对。
    /// </summary>
    private static void DnsCompareView()
    {
        T.Group("DNS 卡片：当前 / 对比分开");

        static DnsServerTest S(string server, double ms, string err = "") => new()
        {
            Server = server, Ms = ms, Answers = err.Length == 0 ? 2 : 0, Error = err,
        };

        // ---- 没测过：两段都不该出现 ----
        var empty = ReportViewBuilder.Build(new DiagState());
        T.False(empty.Tabs[ReportViewBuilder.DnsIndex].HasData, "没测过就没有数据");
        T.False(empty.Tabs[ReportViewBuilder.DnsIndex].Sections.Any(x => x.Title == "对比 DNS"),
                "没测过就不该冒出「对比 DNS」段");

        // ---- 场景一：当前 DNS 是公网的，且明显比公共 DNS 慢 → 建议更换 ----
        var slow = new DiagState
        {
            DnsServers = new List<string> { "218.30.118.6" },
            DnsServerTests = new List<DnsServerTest> { S("218.30.118.6", 38.6) },
            DnsCompareTests = new List<DnsServerTest>
            {
                S("223.5.5.5", 7.1), S("119.29.29.29", 9.4),
                S("114.114.114.114", 12.6), S("8.8.8.8", 0, "超时"),
            },
        };
        var t = ReportViewBuilder.Build(slow).Tabs[ReportViewBuilder.DnsIndex];
        T.True(t.Sections.Any(x => x.Title == "当前 DNS"), "当前 DNS 独立成段");
        var cmp = t.Sections.First(x => x.Title == "对比 DNS");
        T.Eq(cmp.Bars.Count, 4, "四台对比 DNS 都渲染出来");
        T.True(cmp.Verdict.Contains("223.5.5.5") && cmp.Verdict.Contains("明显更快"),
               "当前 DNS 慢一倍以上 → 结论直接说换哪台");
        T.Eq(cmp.VerdictTone, Tone.Warn, "值得换是提醒色");
        T.True(cmp.Hint.Contains("你没有在用"), "标题说明这些服务器用户并没有在用");

        // 对比段刻意不按绝对阈值上色：8.8.8.8 超时也只给中性色，不是红色 ——
        // 用户没在用这台，给它标红等于报一个用户修不了的故障
        T.Eq(cmp.Bars.First(b => b.Name == "8.8.8.8").Tone, Tone.Neutral,
             "对比段里失败的那台也是中性色，不是红色");
        T.Eq(cmp.Bars.First(b => b.Name == "223.5.5.5").Tone, Tone.Good, "最快的那台标绿");
        T.Eq(cmp.Bars.First(b => b.Name == "119.29.29.29").Tone, Tone.Neutral,
             "其余中性 —— 这一段表达的是相对排序，不是绝对好坏");
        T.True(cmp.Bars.First(b => b.Name == "223.5.5.5").ValueText.Contains("ms"), "最快那台显示耗时");
        T.Eq(cmp.Bars.First(b => b.Name == "8.8.8.8").ValueText, "失败", "失败那台显示「失败」");
        T.True(cmp.Bars.All(b => b.Geo.Length >= 0), "归属地字段可用（公网地址会带归属地）");

        // 对比 DNS 无响应**不该**进问题清单 —— 用户没在用它们
        T.False(ReportViewBuilder.Build(slow).Issues.Any(i =>
                    i.Title.Contains("8.8.8.8") || i.Detail.Contains("8.8.8.8")),
              "对比用的公共 DNS 无响应不该进问题清单");

        // ---- 反方向：结论条是橙色（Warn），清单里就**必须**有一条对应条目 ----
        // 「橙色 ⇒ 必有问题条目」这条不变式对结论条同样成立。
        // 以前对比段只给颜色不给条目，用户看到橙色却在清单里找不到解释。
        var switchRow = ReportViewBuilder.Build(slow).Issues
            .FirstOrDefault(i => i.Title.Contains("可以换个更快的 DNS"));
        T.NotNull(switchRow, "建议换 DNS 时，问题清单里必须有一条对应条目");
        if (switchRow != null)
        {
            T.Eq(switchRow.Tone, Tone.Warn, "条目色调与结论条同源（橙）");
            T.Eq(switchRow.TargetTab, ReportViewBuilder.DnsIndex, "条目要能跳回 DNS 卡片");
            T.True(switchRow.Detail.Contains("223.5.5.5"), "条目要指出换哪一台更快");
        }

        // ---- 场景二：当前 DNS 是局域网地址 → 两种口径，不能直接比 ----
        var lan = new DiagState
        {
            DnsServers = new List<string> { "192.168.1.1" },
            DnsServerTests = new List<DnsServerTest> { S("192.168.1.1", 0.5) },
            DnsCompareTests = new List<DnsServerTest> { S("223.5.5.5", 7.1), S("119.29.29.29", 9.4) },
        };
        var lanCmp = ReportViewBuilder.Build(lan).Tabs[ReportViewBuilder.DnsIndex]
            .Sections.First(x => x.Title == "对比 DNS");
        // 局域网那台 0.5 ms 看着最快，但那只代表到路由器的往返，不含往上递归的时间。
        // 拿它去比会得出「公共 DNS 慢 14 倍」这种假结论，所以这里刻意不给出「相当 / 更快」的判断。
        T.False(lanCmp.Verdict.Contains("明显更快"), "局域网地址不能触发「建议更换」");
        T.False(lanCmp.Verdict.Contains("相当"), "也不该说「相当」—— 两种口径没法比");
        T.True(lanCmp.Notes.Any(n => n.Contains("局域网")), "要解释为什么不能直接比");
        T.Eq(lanCmp.VerdictTone, Tone.Neutral, "不评价，所以是中性的");

        // ---- 场景三：当前 DNS 比公共 DNS 还快 → 明确说「不用换」 ----
        var faster = new DiagState
        {
            DnsServers = new List<string> { "218.30.118.6" },
            DnsServerTests = new List<DnsServerTest> { S("218.30.118.6", 6.2) },
            DnsCompareTests = new List<DnsServerTest> { S("223.5.5.5", 7.1), S("119.29.29.29", 9.4) },
        };
        var fasterCmp = ReportViewBuilder.Build(faster).Tabs[ReportViewBuilder.DnsIndex]
            .Sections.First(x => x.Title == "对比 DNS");
        T.True(fasterCmp.Verdict.Contains("还快") && fasterCmp.Verdict.Contains("不用换"),
               "当前 DNS 更快时要明说不用换");
        T.Eq(fasterCmp.VerdictTone, Tone.Good, "「不用换」是绿色");

        // ---- 场景四：当前 DNS 略慢但不值得折腾 → 「相当」 ----
        var fine = new DiagState
        {
            DnsServers = new List<string> { "218.30.118.6" },
            DnsServerTests = new List<DnsServerTest> { S("218.30.118.6", 9.0) },
            DnsCompareTests = new List<DnsServerTest> { S("223.5.5.5", 7.1) },
        };
        var fineCmp = ReportViewBuilder.Build(fine).Tabs[ReportViewBuilder.DnsIndex]
            .Sections.First(x => x.Title == "对比 DNS");
        T.True(fineCmp.Verdict.Contains("相当"), "差得不多就说「相当」");
        T.Eq(fineCmp.VerdictTone, Tone.Good, "「相当」是绿色");

        // 只看绝对差值会把「2 ms vs 1 ms」也算成值得换，所以还要求快够倍数
        var tiny = new DiagState
        {
            DnsServers = new List<string> { "218.30.118.6" },
            DnsServerTests = new List<DnsServerTest> { S("218.30.118.6", 2.0) },
            DnsCompareTests = new List<DnsServerTest> { S("223.5.5.5", 1.0) },
        };
        var tinyCmp = ReportViewBuilder.Build(tiny).Tabs[ReportViewBuilder.DnsIndex]
            .Sections.First(x => x.Title == "对比 DNS");
        T.False(tinyCmp.Verdict.Contains("明显更快"), "快一倍但只差 1 ms，不值得让用户去改配置");

        // ---- 场景五：一台都问不通 ----
        var none = new DiagState
        {
            DnsServers = new List<string> { "192.168.1.1" },
            DnsServerTests = new List<DnsServerTest> { S("192.168.1.1", 0.5) },
            DnsCompareTests = new List<DnsServerTest> { S("8.8.8.8", 0, "超时") },
        };
        var noneCmp = ReportViewBuilder.Build(none).Tabs[ReportViewBuilder.DnsIndex]
            .Sections.First(x => x.Title == "对比 DNS");
        T.True(noneCmp.Verdict.Contains("没有响应"), "一台都问不通也要给个结论，不能空着");
        T.Eq(noneCmp.VerdictTone, Tone.Neutral, "这不是用户的故障，所以不给告警色");

        // 反方向：不值得换的时候不能凭空冒出条目
        // （放在这里是因为 faster / fine / tiny / lan 在上面才刚声明）
        foreach (var (name, st) in new (string Name, DiagState State)[]
                 {
                     ("当前更快", faster), ("差得不多", fine),
                     ("只差 1 ms", tiny), ("当前是局域网地址", lan),
                 })
            T.False(ReportViewBuilder.Build(st).Issues.Any(i => i.Title.Contains("可以换个更快的 DNS")),
                    $"{name} → 不该产生「换 DNS」条目");

        // ---- 判据本身：既要差够毫秒，也要差够倍数 ----
        // 卡片结论条与问题条目共用这一个函数，所以它的边界就是那条不变式的边界。
        T.True(Thresholds.DnsSwitchWorthIt(39, 7.1), "39 ms vs 7.1 ms 值得换");
        T.False(Thresholds.DnsSwitchWorthIt(2.0, 1.0), "快一倍但只差 1 ms，不值得让用户去改配置");
        T.False(Thresholds.DnsSwitchWorthIt(20, 12), "只差 8 ms（不到 15 ms）不值得");
        T.False(Thresholds.DnsSwitchWorthIt(25, 14), "差 11 ms 且不到两倍，不值得");
        T.False(Thresholds.DnsSwitchWorthIt(6, 7.1), "当前本来就更快，当然不换");

        // ---- 常数与列表 ----
        T.Eq(DnsProbe.CompareTestDomain, "www.baidu.com", "对比测速用的域名");
        T.Eq(DnsProbe.CompareServers.Length, 4, "四台公共 DNS");
        T.True(DnsProbe.CompareServers.All(IpGeo.IsLookupable),
               "对比列表里全是公网地址 —— 私有地址的耗时不代表解析能力");
        T.True(Thresholds.DnsSwitchGainMs > 0 && Thresholds.DnsSwitchGainRatio > 1,
               "「值得换」的两个判据都要是正数，倍数要大于 1");
    }

    // ==================================================================
    // 劫持检测段的结论文案与色调
    // ==================================================================

    private static void HijackVerdict()
    {
        T.Group("DNS 卡片：劫持检测");

        static Section Sec(DiagState st) => ReportViewBuilder
            .Build(st).Tabs[ReportViewBuilder.DnsIndex]
            .Sections.First(x => x.Title == "DNS 劫持检测");

        static DiagState With(params HijackCheck[] checks) => new()
        {
            Hijack = new HijackReport
            {
                ProbeDomain = "nx-probe.invalid",
                Checks = checks.ToList(),
            },
        };

        // ---- 全部应答且都返回 NXDOMAIN → 可以说「所有服务器」 ----
        var allOk = Sec(With(
            new HijackCheck { Server = "223.5.5.5", Ok = true, RCode = 3 },
            new HijackCheck { Server = "119.29.29.29", Ok = true, RCode = 3 }));
        T.True(allOk.Verdict.Contains("所有服务器"), "全部应答时才敢说「所有服务器」");
        T.Eq(allOk.VerdictTone, Tone.Good, "全都正常是绿色");
        // 只断言服务器那几行 —— 「探针域名」那行本来就不该有色调（中性）
        T.True(allOk.Kv.Where(x => x.Key.Trim() != "探针域名").All(x => x.Tone == Tone.Good),
               "应答正常的服务器都是绿色");

        // ---- 有一台超时：结论不能说「所有服务器」，也不能给绿色 ----
        // 以前 AnyHijacked 为假就直接写「所有服务器都正确返回 NXDOMAIN」，
        // 而超时的服务器让 AnyHijacked 也是假 —— 于是这句话在明明有服务器没应答时宣称全都正常。
        var partial = Sec(With(
            new HijackCheck { Server = "223.5.5.5", Ok = true, RCode = 3 },
            new HijackCheck { Server = "119.29.29.29", Ok = true, RCode = 3 },
            new HijackCheck { Server = "114.114.114.114", Ok = true, RCode = 3 },
            new HijackCheck { Server = "8.8.8.8", Error = "超时" }));
        T.False(partial.Verdict.Contains("所有服务器"),
                "有服务器没应答就不能说「所有服务器」");
        T.True(partial.Verdict.Contains("3/4"), "结论要报出实际应答的台数");
        T.True(partial.Verdict.Contains("8.8.8.8"), "结论要点名是谁没应答");
        T.Eq(partial.VerdictTone, Tone.Neutral, "只验证了 3 台，不能给绿色");
        T.Eq(partial.Kv.First(x => x.Key.Contains("8.8.8.8")).Tone, Tone.Neutral,
             "公共 DNS 无响应是中性灰 —— 用户没在用这台，标橙等于报一个他修不了的故障");

        // 无响应不该进问题清单：同一份数据在「对比 DNS」段也是灰的，
        // 一张卡片里同一件事两种颜色自相矛盾。
        T.False(ReportViewBuilder.Build(With(
                    new HijackCheck { Server = "223.5.5.5", Ok = true, RCode = 3 },
                    new HijackCheck { Server = "8.8.8.8", Error = "超时" }))
                .Issues.Any(i => i.Detail.Contains("8.8.8.8") || i.Title.Contains("8.8.8.8")),
              "劫持探测里公共 DNS 无响应不该进问题清单");

        // ---- 真的被劫持：结论和行色都必须是红的，且进问题清单 ----
        var hij = With(new HijackCheck { Server = "1.2.3.4", Ok = true, RCode = 0, Hijacked = true });
        var hijSec = Sec(hij);
        T.True(hijSec.Verdict.Contains("劫持"), "有劫持时结论明说");
        T.Eq(hijSec.VerdictTone, Tone.Bad, "劫持是红色");
        T.Eq(hijSec.Kv.First(x => x.Key.Contains("1.2.3.4")).Tone, Tone.Bad, "被劫持那台是红色");
        T.True(ReportViewBuilder.Build(hij).Issues.Any(i => i.Title == "检测到 DNS 劫持"),
              "劫持必须进问题清单");

        // ---- 一台都没有（空列表）：不能凭空宣称「所有服务器都正常」 ----
        var none = Sec(new DiagState { Hijack = new HijackReport { ProbeDomain = "x.invalid" } });
        T.False(none.Verdict.Contains("所有服务器"), "没有服务器时不能说「所有服务器」");
        T.Eq(none.VerdictTone, Tone.Neutral, "没得测就是中性，不是绿色");
    }

    private static void TextReport()
    {
        T.Group("ReportBuilder");

        T.Eq(ReportBuilder.DisplayWidth("abc"), 3, "ASCII 每个字符算 1 列");
        T.Eq(ReportBuilder.DisplayWidth("中文"), 4, "汉字每个算 2 列");
        T.Eq(ReportBuilder.DisplayWidth("a中"), 3, "混排宽度正确");
        T.Eq(ReportBuilder.DisplayWidth(""), 0, "空串宽度为 0");
        T.Eq(ReportBuilder.Pad("abc", 6), "abc   ", "右侧补齐到 6 列");
        T.Eq(ReportBuilder.Pad("abc", 2), "abc", "目标宽度更小时不截断");
        T.Eq(ReportBuilder.Pad("中文", 6), "中文  ", "按显示宽度而不是字符数补齐");
        T.Eq(ReportBuilder.DisplayWidth(ReportBuilder.Pad("中文", 6)), 6, "补齐后显示宽度正好是目标值");

        // 空状态也要能渲染出报告，不能抛异常
        string text = "";
        T.NoThrow(() => text = ReportBuilder.RenderText(new DiagState()), "空状态可以渲染成文本报告");
        T.True(text.Length > 0, "空状态报告不是空字符串");
        T.True(text.Contains("NetCheck"), "报告带标题");
        T.True(text.Contains("本机"), "报告包含本机章节");

        // 有数据时关键信息都要出现
        var s = new DiagState
        {
            Timestamp = "2026-01-01 00:00:00",
            Hostname = "PC-TEST",
            Gateway = "10.0.0.1",
            Adapters = new List<AdapterInfo>
            {
                new() { Name = "以太网", IsUp = true, IPv4 = "10.0.0.2", Mask = "255.255.255.0", Gateway = "10.0.0.1", Dns = { "223.5.5.5" } },
            },
            PublicIpDirect = "1.1.1.1",
            Ipv6 = new Ipv6Report
            {
                Addresses = { new Ipv6AddressInfo { Address = "2408:8207::1", Adapter = "以太网", Kind = "全局" } },
                EgressIp = "2408:8207::1",
                Pings =
                {
                    new Ipv6PingResult { Region = "国内", Name = "AliDNS", Host = Ipv6Probe.DomesticTarget, Ms = 12 },
                    new Ipv6PingResult
                    {
                        Region = "国外", Name = "Cloudflare",
                        Host = Ipv6Probe.ForeignTarget, Error = "超时",
                    },
                },
            },
            Nat = new NatReport { Type = "对称型 NAT", LocalPort = 51234 },
            BandwidthDirect = new BandwidthReport { Source = "清华镜像", Bytes = 162_500_000, Seconds = 10 },
            Mtu = new MtuReport { Target = "1.1.1.1", Reachable = true, MaxPayload = 1472, PathMtu = 1500 },
            DnsCompareTests = new List<DnsServerTest>
            {
                new() { Server = "223.5.5.5", Ms = 7.1, Answers = 2 },
                new() { Server = "8.8.8.8", Error = "超时" },
            },
            PortScan = new PortScanReport { Host = "1.1.1.1", Results = { new PortResult { Port = 443, Open = true, Ms = 7 } } },
            Traceroute = new TracerouteReport
            {
                Target = "1.1.1.1",
                Hops = { new HopInfo { Ttl = 1, Address = "10.0.0.1", Ms = 0.6, Status = "TtlExpired" } },
            },
        };

        s.IpLocations["1.1.1.1"] = "示例 · 华东 电信";
        s.IpLocations["223.5.5.5"] = "浙江省杭州市阿里云";

        T.NoThrow(() => text = ReportBuilder.RenderText(s), "满数据可以渲染成文本报告");
        T.True(text.Contains("PC-TEST"), "报告包含主机名");
        T.True(text.Contains("2026-01-01 00:00:00"), "报告包含时间戳");
        T.True(text.Contains("10.0.0.2"), "报告包含本机 IPv4");
        T.True(text.Contains("223.5.5.5"), "报告包含 DNS 服务器");
        T.True(text.Contains("1.1.1.1"), "报告包含公网出口 IP");
        T.True(text.Contains("对称型"), "报告包含 NAT 类型");
        T.True(text.Contains("130"), "报告包含带宽数值");
        T.True(text.Contains("1500"), "报告包含路径 MTU");
        T.True(text.Contains("443"), "报告包含端口检测结果");

        // ---- 归属地：就地拼在 IP 后面，用全角括号圈起来 ----
        T.True(text.Contains("1.1.1.1（示例 · 华东 电信）"), "出口 IP 后面跟上归属地");
        T.True(text.Contains("223.5.5.5（浙江省杭州市阿里云）"), "DNS 服务器后面跟上归属地");

        // ---- 对比 DNS 是章内的小标题，不占章号 ----
        // 用 Line 而不是 H 渲染，所以「一共 13 章」那条断言不会被它顶掉。
        T.True(text.Contains("对比公共 DNS"), "报告里能看到对比 DNS 那一块");
        T.True(text.Contains(DnsProbe.CompareTestDomain), "写明用的是哪个域名 —— 两组数字才可比");
        T.False(text.Contains("五之二"), "对比 DNS 不该单独占一个章号");
        T.True(text.Contains("归属地"), "路由表有归属地列头");

        // 路由表的列必须按显示宽度对齐（中文地名一个字符顶两个位）
        var hopLines = text.Split('\n')
            .Where(l => l.TrimStart().StartsWith("1 ") && l.Contains("10.0.0.1"))
            .ToList();
        T.True(hopLines.Count > 0, "路由表里能找到第一跳那一行");
        // 路由表里归属地是独立一列，不带括号（带括号会把列宽算歪）
        T.True(hopLines[0].Contains("局域网"), "路由跳的归属地跟在地址后面");
        T.True(hopLines[0].IndexOf("局域网", StringComparison.Ordinal)
               > hopLines[0].IndexOf("10.0.0.1", StringComparison.Ordinal),
               "归属地排在地址右边");
        T.True(hopLines[0].Contains("10.0.0.1             局域网"),
               "地址列按显示宽度补到 20 列，中文地名不会把后面的延迟列挤歪");

        // ---- IPv6 章节 ----
        T.True(text.Contains("二、IPv6"), "IPv6 是第二章（插在本机配置之后、公网出口之前）");
        T.True(text.Contains("2408:8207::1"), "报告里能看到 IPv6 地址");
        T.True(text.Contains("只能到国内"), "结论写进了报告");
        T.True(text.Contains(Ipv6Probe.DomesticTarget), "列出了国内目标地址");
        T.True(text.Contains("不可达"), "国外目标不可达也如实写出来");

        // 章节编号必须连续，插了新章节之后不能出现两个「二」或者跳号
        var numbered = text.Split('\n')
            .Where(l => l.Length > 0 && "一二三四五六七八九十".Contains(l[0]) && l.Contains('、'))
            .Select(l => l[..(l.IndexOf('、') + 1)])
            .ToList();
        T.Eq(numbered.Count, 13, "一共 13 章");
        T.Eq(numbered.Distinct().Count(), numbered.Count, "章节编号不重复");

        // 所有行都不该带控制字符或未替换的占位符
        T.False(text.Contains("{{"), "报告里没有未替换的模板占位符");
        T.False(text.Contains("\r\r"), "报告里没有重复回车");

        // 版本号必须来自程序集，不能再出现「改了 csproj 但报告还印旧号」的问题
        var asmVer = typeof(ReportBuilder).Assembly.GetName().Version!;
        T.Eq(ReportBuilder.Version, $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}",
             "报告版本号与程序集版本一致");
        T.True(text.Contains("v" + ReportBuilder.Version), "报告头印出了正确的版本号");

        // 报告行模型：每行都要有内容
        var lines = ReportBuilder.Build(s);
        T.True(lines.Count > 0, "报告行列表非空");
        T.True(lines.All(l => l.Text != null), "没有 null 行");
    }
}
