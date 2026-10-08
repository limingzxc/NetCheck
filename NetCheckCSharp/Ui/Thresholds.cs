namespace NetCheck.Ui;

/// <summary>
/// 所有「数值 → 色调」的判定集中在这里，**颜色渲染与问题清单共用同一份函数**。
///
/// 之前这两处各写了一套数字，于是出现口径裂缝：网关 5 ms 时条形画成橙色（阈值 3 / 15），
/// 但问题清单只在 &gt;15 ms 时才提醒 —— 用户看到橙色却找不到对应的问题条目；
/// 带宽 15 Mbps 同理（卡片 &lt;20 就转橙，问题清单却要 &lt;10 才报）。
/// 现在只要 <see cref="IsProblem"/> 为真就一定有问题条目，反过来也成立。
///
/// 放在 Ui 层是因为它要同时被 <see cref="Renderer"/> 与 <see cref="ReportViewBuilder"/> 使用，
/// 而且不依赖 WPF，测试工程能直接引用断言。
/// </summary>
public static class Thresholds
{
    // ------------------------------------------------------------------
    // 网关（局域网内到路由器的往返延迟）
    // ------------------------------------------------------------------

    /// <summary>局域网内到网关正常应在 3 ms 以内（有线甚至 &lt;1 ms）。</summary>
    public const double GatewayGoodMs = 3;

    /// <summary>超过 15 ms 基本可以确定是无线信号差、链路拥塞或网关负载高。</summary>
    public const double GatewayWarnMs = 15;

    public static Tone Gateway(double ms)
        => ms <= GatewayGoodMs ? Tone.Good
         : ms <= GatewayWarnMs ? Tone.Warn
         : Tone.Bad;

    // ------------------------------------------------------------------
    // 下载带宽
    // ------------------------------------------------------------------

    /// <summary>百兆以上算「良好」，指标卡片给绿色。</summary>
    public const double BandwidthGoodMbps = 100;

    /// <summary>20 Mbps 是「够用但不快」的下限：能看 4K，但大文件下载会等。</summary>
    public const double BandwidthFairMbps = 20;

    /// <summary>低于 5 Mbps 属于明显不达标。</summary>
    public const double BandwidthWarnMbps = 5;

    /// <summary>
    /// 注意 20~100 Mbps 返回 <see cref="Tone.Neutral"/>（灰色）而不是绿色 ——
    /// 灰色表示「不评价」，这样它就不会进问题清单，也不会被当成好成绩。
    /// </summary>
    public static Tone Bandwidth(double mbps)
        => mbps >= BandwidthGoodMbps ? Tone.Good
         : mbps >= BandwidthFairMbps ? Tone.Neutral
         : mbps >= BandwidthWarnMbps ? Tone.Warn
         : Tone.Bad;

    // ------------------------------------------------------------------
    // DNS
    // ------------------------------------------------------------------

    /// <summary>单台 DNS 服务器解析一次域名的耗时。</summary>
    public const double DnsServerGoodMs = 50;

    public const double DnsServerWarnMs = 200;

    public static Tone DnsServer(double ms)
        => ms <= DnsServerGoodMs ? Tone.Good
         : ms <= DnsServerWarnMs ? Tone.Warn
         : Tone.Bad;

    /// <summary>
    /// 本机解析器的域名解析耗时。比单台 DNS 服务器宽松 ——
    /// 它包含了系统缓存、hosts、多级转发，本来就会更慢一点。
    /// </summary>
    public const double DnsResolveGoodMs = 60;

    public const double DnsResolveWarnMs = 300;

    public static Tone DnsResolve(double ms)
        => ms <= DnsResolveGoodMs ? Tone.Good
         : ms <= DnsResolveWarnMs ? Tone.Warn
         : Tone.Bad;

    /// <summary>
    /// 「换个公共 DNS 值不值」的判据：要比当前 DNS 快这么多毫秒、并且快这么多倍，
    /// 才给一句「换掉可能明显更快」。
    ///
    /// 只看绝对差值会把「2 ms vs 1 ms」也算成值得换，所以要求同时满足倍数条件。
    /// </summary>
    public const double DnsSwitchGainMs = 15;

    public const double DnsSwitchGainRatio = 2.0;

    /// <summary>
    /// 「换用最快的公共 DNS 是否值得」的完整判据。
    /// **卡片上的结论条和问题清单共用这一个函数** —— 各写一套比较逻辑正是
    /// 本文件开头说的「口径裂缝」的来源。
    ///
    /// 注意这里比较的是**耗时数字**，不是色调：当前 DNS 39 ms 在
    /// <see cref="DnsServer"/> 下是绿色（≤50 ms 就是良好），但换掉能省 30 ms，
    /// 两件事并不矛盾 —— 前者回答「现在这样行不行」，后者回答「能不能更好」。
    /// </summary>
    /// <param name="currentMs">当前 DNS 里最快的那台公网服务器的耗时。</param>
    /// <param name="bestPublicMs">对比列表里最快那台的耗时。</param>
    public static bool DnsSwitchWorthIt(double currentMs, double bestPublicMs)
        => currentMs - bestPublicMs >= DnsSwitchGainMs
           && currentMs >= bestPublicMs * DnsSwitchGainRatio;

    // ------------------------------------------------------------------
    // 丢包与抖动
    // ------------------------------------------------------------------

    /// <summary>丢包达到这个比例会明显影响实时音视频与游戏。</summary>
    public const double LossBadPercent = 20;

    /// <summary>相邻探测的延迟波动超过这个值说明链路不稳定。</summary>
    public const double JitterWarnMs = 30;

    /// <summary>
    /// 丢包色调。**零丢包要显式返回 Good** —— 概览的「丢包率」指标卡在 0% 时也该是绿的，
    /// 而原来的两档写法（非 Bad 即 Warn）会把 0% 画成橙色，等于给一个完全正常的网络报警。
    /// 调用方原先都自带 <c>if (loss &gt; 0)</c> 守卫，所以这里补上零档不会改变既有行为。
    /// </summary>
    public static Tone Loss(double percent)
        => percent <= 0 ? Tone.Good
         : percent >= LossBadPercent ? Tone.Bad
         : Tone.Warn;

    /// <summary>抖动色调。抖动只有「偏大 / 正常」两档，没有「严重」档。</summary>
    public static Tone Jitter(double ms)
        => ms >= JitterWarnMs ? Tone.Warn : Tone.Good;

    // ------------------------------------------------------------------
    // 路径 MTU
    // ------------------------------------------------------------------

    /// <summary>以太网标准 MTU。达到它说明路径没有额外封装（PPPoE / VPN / 隧道）。</summary>
    public const int MtuGood = 1500;

    /// <summary>1400~1500 之间通常是隧道或 PPPoE 开销，可用但需要留意 PMTUD。</summary>
    public const int MtuWarn = 1400;

    public static Tone Mtu(int mtu)
        => mtu >= MtuGood ? Tone.Good
         : mtu >= MtuWarn ? Tone.Warn
         : Tone.Bad;

    // ------------------------------------------------------------------

    /// <summary>
    /// 这个色调是否值得进问题清单。**这是配色与问题清单之间的唯一约定**：
    /// 只要某个数值被判成 Warn / Bad，就必须有一条同色调的问题条目解释它。
    /// </summary>
    public static bool IsProblem(Tone t) => t is Tone.Warn or Tone.Bad;
}
