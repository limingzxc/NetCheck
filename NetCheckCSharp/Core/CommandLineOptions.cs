namespace NetCheck.Core;

/// <summary>
/// 命令行参数。支持 <c>--key value</c> 与 <c>--key=value</c> 两种写法。
/// 解析过程不抛异常，无法识别的参数收集到 <see cref="Errors"/> 里，由调用方决定怎么提示。
/// </summary>
public sealed class CommandLineOptions
{
    /// <summary>--check 允许的值。all 表示按顺序把全部检测跑一遍。</summary>
    public static readonly string[] CheckNames =
        { "local", "quick", "egress", "dns", "latency", "speed", "ipv6", "web", "nat", "trace", "mtu", "port", "all" };

    /// <summary>
    /// --check all 展开成哪些检测，以及它们的执行顺序。
    /// 只列**原子检测项**，不列 quick —— quick 就是这些项的顺序执行，
    /// 把它也放进来会让所有项各跑两遍，白白多花一倍时间。
    ///
    /// 顺序按**诊断链从近到远**排，这样「一键检测」的进度读起来是一条完整的排查路径：
    /// 本机 → 公网出口 → DNS → 延迟 → 带宽 → IPv6 → 网页 → NAT → 路由 → MTU → 端口。
    /// 功能上只有一条硬约束：<c>local</c> 必须最前（后面几项都要用网卡信息）。
    /// 顺带一个性质：前四项恰好等于 <c>quick</c> 的展开，所以 all 里再放 quick 一定是重复劳动。
    /// </summary>
    public static readonly string[] AllExpansion =
        { "local", "egress", "dns", "latency", "speed", "ipv6", "web", "nat", "trace", "mtu", "port" };

    /// <summary>--tab 数字写法的上界（含）。卡片增删时要跟着改 —— 单元测试会盯着它和卡片数一致。</summary>
    public const int MaxTabIndex = 9;

    public string? Host { get; private set; }
    public string? Ports { get; private set; }
    /// <summary>
    /// <c>--web &lt;主机&gt;</c>：HTTP/HTTPS 检测的「网页目标」，可带路径与协议头。
    /// 和 <see cref="Host"/> 分开是因为语义不同 —— Host 是路由 / MTU / 端口的**探测对端**（通常是 IP），
    /// 而网页目标必须是一个域名（HTTPS 要校验 SNI 和证书）。
    /// </summary>
    public string? WebTarget { get; private set; }
    /// <summary>
    /// --tab 的原始值，可以是卡片键名（推荐，如 <c>ipv6</c>）或数字索引（兼容）。
    /// 解析留给 Ui 层 —— 键名到卡片的映射在 <c>CardCatalog</c> 里，
    /// 那是 Ui 的职责，Core 不该反过来依赖它。
    /// </summary>
    public string? Tab { get; private set; }
    public bool Auto { get; private set; }
    public string? Check { get; private set; }
    public string? ExportPath { get; private set; }
    /// <summary><c>--theme light|dark|system</c>：本次运行用的主题，覆盖配置文件（不写回）。</summary>
    public string? Theme { get; private set; }
    public bool ShowHelp { get; private set; }

    public List<string> Errors { get; } = new();

    public bool HasAny => Host != null || Ports != null || Tab != null || Auto
                       || Check != null || ExportPath != null || WebTarget != null
                       || Theme != null;

    /// <summary>启动后要自动执行的检测项；没有则返回 null。--auto 等价于 --check quick。</summary>
    public string? AutoCheckKey => Check ?? (Auto ? "quick" : null);

    public static CommandLineOptions Parse(IEnumerable<string> args)
    {
        var o = new CommandLineOptions();
        var list = args.ToList();

        for (int i = 0; i < list.Count; i++)
        {
            var raw = list[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // 拆成 key / value，同时支持 --key=value
            string key = raw, value = "";
            int eq = raw.IndexOf('=');
            bool inlineValue = false;
            if (raw.StartsWith("--") && eq > 2)
            {
                key = raw[..eq];
                value = raw[(eq + 1)..];
                inlineValue = true;
            }

            string Next()
            {
                if (inlineValue) return value;
                return i + 1 < list.Count ? list[++i] : "";
            }

            switch (key.ToLowerInvariant())
            {
                case "--host":
                    var h = Next().Trim();
                    if (h.Length == 0) o.Errors.Add("--host 缺少参数值");
                    else o.Host = h;
                    break;

                case "--web":
                    var w = Next().Trim();
                    // 用 WebProbe 自己的解析器校验，保证「命令行接受的」和「探测时能用的」是同一套规则
                    if (w.Length == 0) o.Errors.Add("--web 缺少参数值");
                    else if (!WebProbe.TryParseTarget(w, out _, out _))
                        o.Errors.Add($"--web 需要形如 example.com 或 https://example.com/path 的域名，收到「{w}」");
                    else o.WebTarget = w;
                    break;

                case "--ports":
                    var p = Next().Trim();
                    if (p.Length == 0) o.Errors.Add("--ports 缺少参数值");
                    else
                    {
                        var (parsed, err) = PortCheck.ParsePorts(p);
                        if (err.Length > 0) o.Errors.Add($"--ports 无效：{err}");
                        else o.Ports = p;
                    }
                    break;

                case "--tab":
                    var t = Next().Trim();
                    if (t.Length == 0) o.Errors.Add("--tab 缺少参数值");
                    else if (int.TryParse(t, out var tab))
                    {
                        if (tab < 0 || tab > MaxTabIndex)
                            o.Errors.Add($"--tab 数字范围是 0~{MaxTabIndex}（键名写法不受此限）");
                        else o.Tab = t;
                    }
                    // 卡片键名。只做「长得像不像键名」的形状校验（首字符是字母，其余字母/数字/连字符）——
                    // 真正存不存在要问 CardCatalog，而它在 Ui 层，Core 不能反过来依赖它。
                    // 首字符必须是字母这一条把「1x」这类明显的手滑挡在外面，不会静默吞掉。
                    else if (char.IsAsciiLetter(t[0])
                             && t.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
                        o.Tab = t.ToLowerInvariant();
                    else
                        o.Errors.Add($"--tab 只接受数字或卡片键名，收到「{t}」");
                    break;

                case "--export":
                    var e = Next().Trim().Trim('"');
                    if (e.Length == 0) o.Errors.Add("--export 缺少参数值");
                    else o.ExportPath = e;
                    break;

                case "--auto":
                    o.Auto = true;
                    break;

                case "--theme":
                    var th = Next().Trim().ToLowerInvariant();
                    if (th is not ("light" or "dark" or "system"))
                        o.Errors.Add($"--theme 只接受 light / dark / system，收到「{th}」");
                    else o.Theme = th;
                    break;

                case "--check":
                    var c = Next().Trim().ToLowerInvariant();
                    if (c.Length == 0) o.Errors.Add("--check 缺少参数值");
                    else if (!CheckNames.Contains(c))
                        o.Errors.Add($"--check 不认识「{c}」，可选：" + string.Join(" / ", CheckNames));
                    else o.Check = c;
                    break;

                case "--help":
                case "-?":
                case "/?":
                    o.ShowHelp = true;
                    break;

                default:
                    o.Errors.Add($"无法识别的参数「{raw}」");
                    break;
            }
        }

        return o;
    }

    /// <summary>把命令行值覆盖到配置上。只覆盖显式传入的项，其余保持配置文件里的值。</summary>
    public void ApplyTo(AppSettings s)
    {
        if (Host != null) s.TargetHost = Host;
        if (WebTarget != null) s.WebTarget = WebTarget;
        // --theme 刻意**不写回配置文件**：它是一次性的「这次用哪个主题」，
        // 不该把用户在界面上选好的偏好覆盖掉。所以这里改完，调用方保存配置时要么跳过、
        // 要么在应用主题后把 s.Theme 恢复成配置里的原值 —— 见 App.OnStartup。
        if (Ports != null)
        {
            s.PortSpec = Ports;
            s.PortPreset = PortPresets.MatchName(Ports);
        }
        s.Clamp();
    }

    public static string HelpText =>
        """
        NetCheck 网络体检

        用法：NetCheck.exe [选项]

          --host <主机>     设置探测目标（路由追踪 / MTU / 端口检测共用），覆盖配置文件
          --web <域名>      设置「网页目标」（HTTP/HTTPS 检测用），可带协议与路径：
                              example.com / https://example.com/a
          --ports <表达式>  设置端口表达式，如 22,80,443,8000-8010
          --tab <键名>      启动后直接滚动到某张卡片。**推荐用卡片键名**，它不随卡片顺序变化：
                              overview 概览   local 本机配置  egress 公网出口
                              link     连通性  dns   DNS        path   路径
                              nat      NAT     port  端口       ipv6   IPv4/IPv6
                              web      HTTP/HTTPS
                            数字写法仍然支持，但含义按当前顺序：
                              0 概览  1 本机  2 出口  3 连通性  4 DNS
                              5 路径  6 NAT   7 端口  8 IPv6    9 网页
          --check <名称>    启动后自动执行指定的检测，跑完停在界面上
                            local   本机配置    egress  公网出口 IP
                            dns     DNS 检测    latency 延迟 / 丢包 / 抖动
                            speed   带宽测速    ipv6    IPv4 / IPv6
                            web     网页连通性  nat     NAT 类型
                            trace   路由追踪    mtu     路径 MTU
                            port    端口检测
                            quick   一键检测的快速预设（= local + egress + dns + latency）
                            all     全部 11 个原子检测项依次跑一遍（比 quick 更全、更慢）
          --auto            等价于 --check quick
          --theme <模式>    本次运行使用的主题，不写回配置文件：
                              light   浅色
                              dark    深色
                              system  跟随 Windows 的应用模式设置
          --export <路径>   检测完成后把结果导出到指定文件（.txt / .json）
                            配合 --check / --auto 使用时导出完就自动退出，
                            方便在脚本里无人值守地采集网络数据
          --help            显示本帮助

        示例：
          NetCheck.exe --check port --host 223.5.5.5 --ports 53,443 --export r.json
          NetCheck.exe --check web --web https://example.com/a --export web.json
          NetCheck.exe --host 8.8.8.8 --ports 53,443 --auto --export report.txt
          NetCheck.exe --tab port --ports 1-1024
          NetCheck.exe --theme dark --check dns
        """;
}
