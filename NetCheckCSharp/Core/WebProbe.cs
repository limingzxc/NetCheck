using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NetCheck.Core;

/// <summary>分级探测里的一层（DNS / TCP / TLS / HTTP）。</summary>
public sealed class WebStep
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public double Ms { get; set; }
    /// <summary>成功时的补充信息（解析到的 IP、TLS 版本、HTTP 状态行）。</summary>
    public string Detail { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>一个网页目标的探测结果。失败时 <see cref="FailedStep"/> 指出卡在哪一层。</summary>
public sealed class WebTargetResult
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "/";
    /// <summary>国内 / 境外 / 自定义。</summary>
    public string Region { get; set; } = "";
    public List<WebStep> Steps { get; set; } = new();

    /// <summary>解析到的地址。TCP 层用的是它，出问题时这一行是最直接的线索。</summary>
    public string ResolvedIp { get; set; } = "";

    /// <summary>HTTP 状态码，0 表示没拿到响应。</summary>
    public int HttpStatus { get; set; }

    public WebStep? FirstFailure => Steps.FirstOrDefault(x => !x.Ok);

    /// <summary>四层全过才算通。</summary>
    public bool Ok => Steps.Count > 0 && FirstFailure == null;

    /// <summary>卡在哪一层（"DNS" / "TCP" / "TLS" / "HTTP"），全通返回空串。</summary>
    public string FailedStep => FirstFailure?.Name ?? "";

    /// <summary>失败层级的一句话解释，直接进问题清单。</summary>
    public string Blame => FailedStep switch
    {
        "DNS" => "域名解析失败",
        "TCP" => "TCP 连接建立失败",
        "TLS" => "TLS 握手失败",
        "HTTP" => "没拿到 HTTP 响应",
        _ => "",
    };
}

/// <summary>
/// HTTP / HTTPS 连通性检测结果。
///
/// 这一项的价值不在「通 / 不通」，而在**指出卡在哪一层**：
/// DNS → TCP → TLS → HTTP 是一条严格的串行链，第一个失败的层级就是问题所在。
/// 「能解析但连不上」和「连上了但 TLS 失败」是两种完全不同的问题（前者多半是目标或链路，
/// 后者常见于证书、中间设备干扰、系统时间不对），只说「网站打不开」对排查毫无帮助。
/// </summary>
public sealed class WebReport
{
    public List<WebTargetResult> Targets { get; set; } = new();

    public bool AnyOk => Targets.Any(t => t.Ok);
    public bool AllOk => Targets.Count > 0 && Targets.All(t => t.Ok);

    private List<WebTargetResult> Failed => Targets.Where(t => !t.Ok).ToList();

    public string Verdict
    {
        get
        {
            if (Targets.Count == 0) return "未检测";
            if (AllOk) return "全部正常";

            var failed = Failed;
            var layers = failed.Select(t => t.FailedStep).Distinct().ToList();

            // 所有失败都卡在同一层 → 那是共因，值得直接把层级写在结论上
            if (!AnyOk && layers.Count == 1) return layers[0] + " 层全部失败";
            if (!AnyOk) return "全部目标不可达";
            return "部分目标不可达";
        }
    }

    public string Detail
    {
        get
        {
            if (Targets.Count == 0) return "";

            if (AllOk)
                return "DNS 解析、TCP 连接、TLS 握手、HTTP 响应四层全部通过，"
                     + "浏览器能打开这些网站。";

            var layers = Failed.Select(t => t.FailedStep).Distinct().ToList();

            // 还有目标能通 → 本机网络本身是好的，问题只出在失败的那几个目标上。
            // 这句必须放在层级线索**前面**：反过来的话，用户先读到「TCP 连接建不起来」
            // 就会以为是自己网络断了，而其实只是个别站点访问不了。
            var scope = AnyOk ? "本机网络没断（还有目标访问成功），问题只出在失败的目标上：" : "";

            if (layers.Count != 1)
                return scope + "它们卡在不同层级，属于目标侧差异"
                     + "（某些站点本身就访问不了），不是本机网络的共性问题。";

            return scope + layers[0] switch
            {
                "DNS" => "所有失败都停在域名解析：DNS 没能把域名换成地址。"
                       + "先跑一次 DNS 检测确认解析是否正常。",
                "TCP" => "域名能解析，但 TCP 连接建不起来：通常是目标不可达、被防火墙拦截，"
                       + "或这条链路被中途重置。",
                "TLS" => "TCP 通了但 TLS 握手失败：常见于证书问题、TLS 被中间设备干扰，"
                       + "或本机系统时间不对导致证书被判为未生效。",
                "HTTP" => "TCP 与 TLS 都通了，但没拿到 HTTP 响应：问题在服务端，不在本机网络。",
                _ => "",
            };
        }
    }
}

/// <summary>
/// 网页连通性检测：对每个目标按 DNS → TCP(443) → TLS → HTTPS GET 逐层跑，第一层失败就停。
///
/// 三个实现上的选择：
///
/// 1. **用裸 socket 而不是 HttpClient。** HttpClient 把 DNS、连接、TLS、HTTP 全揉在一起，
///    失败了只给一句 "连接被关闭" —— 而这正是本项要回答的问题。分层做才能定位到层级。
/// 2. **每个目标最多 11 秒**（2 + 3 + 3 + 3）。目标之间**并行**跑，
///    串行的话最坏 4 个目标 × 11 秒 = 44 秒，用户会以为程序卡死了。
/// 3. **证书走默认校验**。证书过期 / 域名不匹配本身就是体检要发现的问题，
///    放行等于把这类问题藏起来。
/// </summary>
public static class WebProbe
{
    /// <summary>内置目标。国内两个 + 境外一个 —— 境外那个不通本身就是有用信息（能不能上外网）。</summary>
    public static readonly (string Name, string Host, string Region)[] BuiltInTargets =
    {
        ("百度", "www.baidu.com", "国内"),
        ("腾讯网", "www.qq.com", "国内"),
        ("Cloudflare", "www.cloudflare.com", "境外"),
    };

    public const int Port = 443;

    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);

    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NetCheck/2.0";

    /// <summary>
    /// 跑一遍全部目标。<paramref name="customTarget"/> 是用户填的「网页目标」，
    /// 空串表示不测（不做成默认目标 —— 默认填一个别人的网站没有意义）。
    /// </summary>
    public static async Task<WebReport> DetectAsync(
        string? customTarget, IProgress<string>? progress, CancellationToken ct)
    {
        var defs = BuiltInTargets
            .Select(x => (Name: x.Name, Host: x.Host, Region: x.Region, Path: "/"))
            .ToList();

        if (TryParseTarget(customTarget, out var host, out var path))
            defs.Add((Name: host, Host: host, Region: "自定义", Path: path));

        var r = new WebReport();
        progress?.Report($"逐层探测 {defs.Count} 个目标（DNS → TCP → TLS → HTTP）…");

        var tasks = defs.Select(d => ProbeAsync(d.Name, d.Host, d.Region, d.Path, ct)).ToList();
        r.Targets.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));

        progress?.Report($"完成：{r.Verdict}。");
        return r;
    }

    /// <summary>
    /// 解析用户填的「网页目标」。接受 `example.com`、`example.com/a/b`、
    /// `https://example.com/a`、`http://example.com:8080/a` 几种写法，只取主机与路径。
    /// 认不出来就返回 false，不测 —— 悄悄测一个猜出来的域名比不测更糟。
    ///
    /// 只收**纯 ASCII 域名**，两类输入刻意拒绝：
    /// - **裸 IP**：本项要跑 TLS，而 TLS 握手要靠 SNI 送域名、证书也要按域名校验。
    ///   拿 IP 去连，服务端多半给一张默认证书，校验失败后报出来的是「TLS 握手失败」，
    ///   把「你填错了目标」伪装成「你的网络有问题」。测 IP 该用「端口连通性」。
    /// - **非 ASCII 域名**（如 `例子.测试`）：真实握手需要先转 Punycode，
    ///   直接把中文塞给 socket 只会解析失败。与其在内部悄悄转码、让用户不知道
    ///   到底测了哪个域名，不如拒绝并让用户填 Punycode 形式。
    /// </summary>
    public static bool TryParseTarget(string? input, out string host, out string path)
    {
        host = "";
        path = "/";
        var s = (input ?? "").Trim();
        if (s.Length == 0) return false;

        var schemeAt = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeAt >= 0) s = s[(schemeAt + 3)..];

        // 去掉可能的 user@ 前缀与查询串
        var at = s.IndexOf('@');
        if (at >= 0) s = s[(at + 1)..];
        var q = s.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) s = s[..q];

        var slash = s.IndexOf('/');
        if (slash >= 0)
        {
            path = s[slash..];
            s = s[..slash];
        }

        // 去掉端口：本项固定测 443，带端口的写法只会让人以为测的是那个端口
        var colon = s.LastIndexOf(':');
        if (colon > 0) s = s[..colon];

        s = s.Trim().TrimEnd('.');
        if (s.Length == 0 || !s.Contains('.')) return false;
        if (s.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\')) return false;

        // 裸 IP 与非 ASCII 域名：见方法注释，两者都会把「填错目标」伪装成「网络有问题」
        if (IPAddress.TryParse(s, out _)) return false;
        if (s.Any(c => c >= 128)) return false;

        // 每一段都要有内容，挡住 "a..b"、"a.-b" 这类一眼就是笔误的写法
        if (s.Split('.').Any(x => x.Length == 0)) return false;

        host = s;
        if (path.Length == 0) path = "/";
        return true;
    }

    // ------------------------------------------------------------------

    private static async Task<WebTargetResult> ProbeAsync(
        string name, string host, string region, string path, CancellationToken ct)
    {
        var t = new WebTargetResult { Name = name, Host = host, Region = region, Path = path };

        // ---- 1. DNS ----
        var dns = await Step("DNS", ct, async token =>
        {
            var addrs = await WithTimeout(
                c => Dns.GetHostAddressesAsync(host, c), DnsTimeout, token).ConfigureAwait(false);

            if (addrs.Length == 0) throw new Exception("没有解析到任何地址");

            // 优先用 IPv4 建连接：本项测的是「网站能不能打开」，
            // 而 AAAA 记录在 IPv6 不通的网络里会先超时一轮，把问题引到无关的地方去。
            var pick = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? addrs[0];
            return (pick, $"{pick} · 共 {addrs.Length} 条记录");
        }).ConfigureAwait(false);

        t.Steps.Add(dns.Step);
        if (!dns.Ok) return t;
        t.ResolvedIp = dns.Value.ToString();

        // ---- 2. TCP ----
        // socket 建在 Step 外面：后面 TLS / HTTP 两层要接着用它，
        // 而 Step 的返回值语义是「这一层的结果」，不是「把资源交出来」。
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        var tcp = await Step("TCP", ct, async token =>
        {
            await WithTimeout(async c =>
            {
                await socket.ConnectAsync(new IPEndPoint(dns.Value, Port), c).ConfigureAwait(false);
                return true;
            }, ConnectTimeout, token).ConfigureAwait(false);
            return (socket, $"{dns.Value}:{Port}");
        }).ConfigureAwait(false);

        t.Steps.Add(tcp.Step);
        if (!tcp.Ok) return t;

        // ---- 3. TLS ----
        using var network = new NetworkStream(socket, ownsSocket: false);
        using var ssl = new SslStream(network, leaveInnerStreamOpen: true);

        var tls = await Step("TLS", ct, async token =>
        {
            await WithTimeout(async c =>
            {
                // SslProtocols.None = 让系统协商，不写死 TLS 1.2（写死会把只支持 1.3 的站点判成失败）
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = SslProtocols.None,
                    }, c).ConfigureAwait(false);
                return true;
            }, TlsTimeout, token).ConfigureAwait(false);

            var cert = ssl.RemoteCertificate as X509Certificate2;
            var exp = cert == null ? "" : $" · 证书 {cert.NotAfter:yyyy-MM-dd} 到期";
            return (ssl, $"{ssl.SslProtocol}{exp}");
        }).ConfigureAwait(false);

        t.Steps.Add(tls.Step);
        if (!tls.Ok) return t;

        // ---- 4. HTTP ----
        var http = await Step("HTTP", ct, async token =>
        {
            var req = $"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
                    + $"User-Agent: {UserAgent}\r\nAccept: */*\r\nConnection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(req);

            var status = await WithTimeout(async c =>
            {
                await ssl.WriteAsync(bytes, c).ConfigureAwait(false);
                await ssl.FlushAsync(c).ConfigureAwait(false);

                var buf = new byte[512];
                int n = await ssl.ReadAsync(buf, c).ConfigureAwait(false);
                if (n <= 0) throw new Exception("服务端没有返回任何数据");

                var head = Encoding.ASCII.GetString(buf, 0, n);
                var line = head.Split('\n')[0].Trim();
                if (!line.StartsWith("HTTP/", StringComparison.Ordinal))
                    throw new Exception("返回的不是 HTTP 响应");
                return line;
            }, HttpTimeout, token).ConfigureAwait(false);

            // 状态码单独取出来：4xx/5xx 说明**链路是通的**，只是服务端拒绝了这个请求，
            // 对「网络体检」来说这依然是「通了」，所以不判失败，但要把码显示出来。
            var parts = status.Split(' ', 3);
            var code = parts.Length >= 2 && int.TryParse(parts[1], out var c0) ? c0 : 0;
            return (code, status);
        }).ConfigureAwait(false);

        t.Steps.Add(http.Step);
        t.HttpStatus = http.Ok ? http.Value : 0;
        return t;
    }

    /// <summary>
    /// 跑一层，把成功 / 失败 / 耗时统一成 <see cref="WebStep"/>。
    /// 这里刻意让**所有异常都变成「这一层失败」而不是往上抛** ——
    /// 某一层失败正是本项要报告的结果，不是意外。
    /// </summary>
    private static async Task<(WebStep Step, bool Ok, T Value)> Step<T>(
        string name, CancellationToken ct, Func<CancellationToken, Task<(T Value, string Detail)>> work)
        where T : notnull
    {
        var step = new WebStep { Name = name };
        var sw = Stopwatch.StartNew();
        try
        {
            var (value, detail) = await work(ct).ConfigureAwait(false);
            sw.Stop();
            step.Ok = true;
            step.Ms = sw.Elapsed.TotalMilliseconds;
            step.Detail = detail;
            return (step, true, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            step.Ok = false;
            step.Ms = sw.Elapsed.TotalMilliseconds;
            step.Error = Describe(ex);
            return (step, false, default!);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException => "超时",
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.HostNotFound => "域名不存在",
            SocketError.ConnectionRefused => "连接被拒绝",
            SocketError.TimedOut => "连接超时",
            SocketError.NetworkUnreachable => "网络不可达",
            SocketError.HostUnreachable => "主机不可达",
            _ => se.Message,
        },
        System.Security.Authentication.AuthenticationException => "证书或 TLS 协商失败",
        _ => ex.Message.Length > 0 ? ex.Message : ex.GetType().Name,
    };

    /// <summary>给每一步套一个超时，并且**把超时变成 TimeoutException**（而不是 OperationCanceledException）。</summary>
    private static async Task<T> WithTimeout<T>(
        Func<CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("超时");
        }
    }
}
