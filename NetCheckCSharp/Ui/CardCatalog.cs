namespace NetCheck.Ui;

/// <summary>卡片标题栏上的一个「运行」按钮。</summary>
public sealed record ActionDef(string TaskKey, string Label, string Tip);

/// <summary>
/// 一个二级分组（目前只有「高级」组用到）。高级区不按「一张张卡片平铺」，
/// 而是按**诊断语义**再分一层：网络路径 / 连接能力 / 协议。
/// 这样「NAT 和端口都属于连接建立能力」「路由和 MTU 都属于路径诊断」这两句话
/// 就直接长在界面上，用户不用自己归纳。
/// </summary>
public sealed record SubGroupDef(string Key, string Title);

/// <summary>
/// 一个检测项 = 一张卡片。按钮挂在卡片标题栏上，和它要填充的展示区在同一张卡片里 ——
/// 这样同一个功能只有一个入口，不会再出现「左边一列按钮、上面一列按钮、内容还对不上」的割裂感。
/// </summary>
/// <param name="Key">卡片键。折叠状态按它持久化，所以增删卡片时旧配置不会错位。</param>
/// <param name="Group">所属分组（<see cref="CardCatalog.CommonGroup"/> / <see cref="CardCatalog.AdvancedGroup"/>）。</param>
/// <param name="SubGroup">
/// 二级分组键。只有高级组用；常用组一律传空串 ——
/// 常用区本来就短，再加一层标题只会让「打开就能看到结论」这件事变慢。
/// </param>
/// <param name="TabIndex">对应的 <see cref="TabView"/> 索引，-1 表示不是检测项。</param>
public sealed record SectionDef(
    string Key, string Group, string SubGroup, int TabIndex, string Title, string Hint, ActionDef[] Actions);

/// <summary>
/// 卡片目录。**故意放在 Ui 而不是 MainWindow 里**：卡片键到标签页索引这条映射是手写的，
/// 写错了卡片会静默变空（渲染层是 <c>tabIndex &lt; Tabs.Count ? Tabs[i] : null</c>，越界不报错），
/// 放进 MainWindow 的话测试工程引用不到，这条映射就一点保护都没有。
/// 现在是纯数据、不依赖 WPF，<c>CoreTest</c> 能直接断言。
/// </summary>
public static class CardCatalog
{
    /// <summary>「检测选项」卡片的键。它不是检测项，所以不关联任何标签页。</summary>
    public const string OptionsKey = "options";

    /// <summary>表示「这张卡片不是检测项，没有对应的标签页」。</summary>
    public const int NoTab = -1;

    // ---- 一级分组 ----
    // 分组只影响「默认视野」和「界面上的归拢」，不改变任何检测逻辑。
    // 常用组：点一下就有结论，日常体检用。
    // 高级组：已知有问题才用，默认收起 —— 它们的输入参数仍然复用「检测选项」里的全局值。

    public const string CommonGroup = "common";
    public const string AdvancedGroup = "advanced";

    /// <summary>分组按界面显示顺序排列。改这里就改了界面上分组的先后。</summary>
    public static readonly (string Key, string Title)[] Groups =
    {
        (CommonGroup, "常用"),
        (AdvancedGroup, "高级"),
    };

    // ---- 二级分组（只在高级组内生效）----
    // 顺序即界面顺序：先看「路通不通」，再看「连得上吗」，最后看「协议层对不对」。
    public const string PathSubGroup = "netpath";
    public const string ConnSubGroup = "connect";
    public const string ProtoSubGroup = "protocol";

    public static readonly SubGroupDef[] SubGroups =
    {
        new(PathSubGroup, "网络路径"),
        new(ConnSubGroup, "连接能力"),
        new(ProtoSubGroup, "协议"),
    };

    public static readonly SectionDef OptionsCard = new(
        OptionsKey, CommonGroup, "", NoTab, "检测选项", "目标主机 · 网页目标 · 端口 · 并发 · 超时",
        Array.Empty<ActionDef>());

    /// <summary>
    /// 检测项卡片，按界面显示顺序排列。TabIndex 必须与
    /// <see cref="ReportViewBuilder"/> 的索引一一对应，且恰好覆盖 0 ~ TabCount-1。
    /// </summary>
    public static readonly SectionDef[] Sections =
    {
        new("overview", CommonGroup, "", ReportViewBuilder.OverviewIndex, "概览",
            "公网出口 IP · 网络延迟 · 丢包 · 抖动 · 下载带宽",
            new[] { new ActionDef("quick", "一键检测", "把下面全部检测项依次跑一遍（F5），最慢的一项决定总耗时") }),

        new("local", CommonGroup, "", ReportViewBuilder.LocalIndex, "本机网络配置",
            "网卡 · 网关 · DNS · 代理环境变量",
            new[] { new ActionDef("local", "重新读取", "重新读取网卡配置，纯本地操作，瞬时完成") }),

        new("egress", CommonGroup, "", ReportViewBuilder.EgressIndex, "公网出口 IP",
            "原生 UDP 与 HTTP 多路径对比，识别多出口 / 代理",
            new[] { new ActionDef("egress", "出口 IP 检测", "三条路径各查一次公网出口 IP，约 2 秒") }),

        new("link", CommonGroup, "", ReportViewBuilder.LinkIndex, "连通性",
            "延迟 / 丢包 / 抖动（网页 · 视频 · 游戏）· 下载带宽",
            new[]
            {
                new ActionDef("latency", "延迟检测", "对网页 / 视频 / 游戏三类共 9 个目标做多次往返探测"),
                new ActionDef("speed", "带宽测速", "从国内镜像源下载固定时长，默认绕过系统代理"),
            }),

        new("dns", CommonGroup, "", ReportViewBuilder.DnsIndex, "DNS",
            "解析耗时 · 劫持检测 · 多服务器对比",
            new[] { new ActionDef("dns", "DNS 检测", "域名解析耗时 + 劫持检测 + 多服务器对比，约 8 秒") }),

        new("path", AdvancedGroup, PathSubGroup, ReportViewBuilder.PathIndex, "路径",
            "逐跳路由 · 路径 MTU",
            new[]
            {
                new ActionDef("trace", "路由追踪", "ICMP 逐跳追踪，无需管理员权限"),
                new ActionDef("mtu", "MTU 探测", "用 DF 二分查找探测路径 MTU，排查 PMTUD 黑洞"),
            }),

        new("nat", AdvancedGroup, ConnSubGroup, ReportViewBuilder.NatIndex, "NAT 类型",
            "STUN 判定，影响 P2P 直连与打洞成功率",
            new[] { new ActionDef("nat", "NAT 类型检测", "向 4 个 STUN 服务器发起 UDP 探测，5~15 秒") }),

        new("port", AdvancedGroup, ConnSubGroup, ReportViewBuilder.PortIndex, "端口",
            "TCP 连通性检测",
            new[] { new ActionDef("port", "端口检测", "按检测选项里的端口表达式做 TCP 连通性检测") }),

        new("ipv6", AdvancedGroup, ProtoSubGroup, ReportViewBuilder.Ipv6Index, "IPv4 / IPv6",
            "地址分配 · 出口 · 国内 / 国外可达性",
            new[] { new ActionDef("ipv6", "IPv6 检测", "读全局地址，查 IPv6 出口，探测国内 / 国外各一个目标") }),

        new("web", AdvancedGroup, ProtoSubGroup, ReportViewBuilder.WebIndex, "HTTP / HTTPS",
            "按 DNS → TCP → TLS → HTTP 分级定位，指出卡在哪一层",
            new[] { new ActionDef("web", "网页连通性检测", "对国内 / 境外 / 自定义目标逐层探测，定位到具体是哪一层失败") }),
    };

    /// <summary>按界面顺序排列的全部卡片：检测选项在最前，其余按标签页索引。</summary>
    public static readonly SectionDef[] All =
        new[] { OptionsCard }.Concat(Sections.OrderBy(x => x.TabIndex)).ToArray();

    /// <summary>某个分组里的全部卡片，按界面顺序。</summary>
    public static SectionDef[] CardsInGroup(string group)
        => All.Where(d => string.Equals(d.Group, group, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// 某个分组里的**检测项**卡片，不含「检测选项」这类非检测项。
    ///
    /// 为什么需要它：检测选项的 <c>Group</c> 写的是常用组（那只是让它在旧的分组逻辑里有归属），
    /// 但它现在单独挂在导航栏的「配置」下 —— 不过滤掉就会在导航里出现两次。
    /// 用 <c>TabIndex != NoTab</c> 判断而不是比键名，这样以后再加别的非检测项面板也不用改这里。
    /// </summary>
    public static SectionDef[] DetectionCardsInGroup(string group)
        => All.Where(d => d.TabIndex != NoTab && string.Equals(d.Group, group, StringComparison.Ordinal)).ToArray();

    /// <summary>某个一级分组里的某个二级分组，按界面顺序。</summary>
    public static SectionDef[] CardsInSubGroup(string group, string subGroup)
        => All.Where(d => string.Equals(d.Group, group, StringComparison.Ordinal)
                       && string.Equals(d.SubGroup, subGroup, StringComparison.Ordinal)).ToArray();

    /// <summary>某个一级分组里出现的二级分组，按 <see cref="SubGroups"/> 的顺序，只返回有卡片的那种。</summary>
    public static SubGroupDef[] SubGroupsOf(string group)
        => SubGroups.Where(sg => CardsInSubGroup(group, sg.Key).Length > 0).ToArray();

    /// <summary>卡片键 -> 它所属的分组键。找不到返回空串。</summary>
    public static string GroupOfCard(string cardKey)
        => All.FirstOrDefault(d => string.Equals(d.Key, cardKey, StringComparison.OrdinalIgnoreCase))?.Group ?? "";

    /// <summary>卡片键 -> 它所属的二级分组键。找不到或不属于任何二级分组都返回空串。</summary>
    public static string SubGroupOfCard(string cardKey)
        => All.FirstOrDefault(d => string.Equals(d.Key, cardKey, StringComparison.OrdinalIgnoreCase))?.SubGroup ?? "";

    /// <summary>
    /// 首次运行时默认收起哪些卡片：「检测选项」+「高级」组。
    /// 选项卡展开时要占约半屏（12 个输入字段），会把真正该第一眼看到的「概览」挤出首屏；
    /// 折叠后标题栏仍然显示当前配置摘要（目标 / 端口 / 并发），信息不丢，改选项走
    /// 「视图 → 显示检测选项」或点标题栏。概览永远不收 —— 首屏必须有内容可看。
    /// 从目录推导而不是写死数组：以后往高级组增删卡片，这里不用跟着改。
    /// </summary>
    public static string[] DefaultCollapsedCards()
        => new[] { OptionsKey }
           .Concat(CardsInGroup(AdvancedGroup).Select(d => d.Key))
           .ToArray();

    /// <summary>任务键 -> 它所在卡片的键。用来在运行时把对应卡片的按钮改成「检测中…」。</summary>
    public static SectionDef? CardForTask(string taskKey)
        => Sections.FirstOrDefault(d => d.Actions.Any(a => a.TaskKey == taskKey));

    /// <summary>标签页索引 -> 卡片定义。找不到返回 null。</summary>
    public static SectionDef? CardForTab(int tabIndex)
        => Sections.FirstOrDefault(d => d.TabIndex == tabIndex);

    /// <summary>
    /// 卡片键 -> 卡片定义，用于 <c>--tab ipv6</c> 这种写法。找不到返回 null。
    /// 「检测选项」这张非检测项的卡片也在内 —— 用 <c>--tab options</c> 直接跳到参数区是合理的。
    /// </summary>
    public static SectionDef? CardForKey(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(d => string.Equals(d.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
