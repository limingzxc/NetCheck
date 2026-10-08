using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace NetCheck.Core;

/// <summary>
/// 端口连通性检测。
/// 并发 TCP 握手，记录每个端口的通断与握手耗时，对会主动说话的协议顺带抓取 banner。
/// </summary>
public static class PortCheck
{
    /// <summary>常见端口 → 服务名，用于让结果更易读。</summary>
    private static readonly Dictionary<int, string> KnownPorts = new()
    {
        [20] = "FTP-DATA", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP",
        [53] = "DNS", [67] = "DHCP", [69] = "TFTP", [80] = "HTTP", [110] = "POP3",
        [123] = "NTP", [135] = "RPC", [139] = "NetBIOS", [143] = "IMAP", [161] = "SNMP",
        [389] = "LDAP", [443] = "HTTPS", [445] = "SMB", [465] = "SMTPS", [514] = "Syslog",
        [587] = "SMTP-Sub", [636] = "LDAPS", [993] = "IMAPS", [995] = "POP3S",
        [1080] = "SOCKS", [1433] = "MSSQL", [1521] = "Oracle", [2049] = "NFS",
        [2375] = "Docker", [3306] = "MySQL", [3389] = "RDP", [5432] = "PostgreSQL",
        [5900] = "VNC", [5984] = "CouchDB", [6379] = "Redis", [8080] = "HTTP-Alt",
        [8443] = "HTTPS-Alt", [9200] = "Elasticsearch", [11211] = "Memcached",
        [27017] = "MongoDB",
    };

    /// <summary>会主动发送 banner 的端口，可直接读取。</summary>
    private static readonly HashSet<int> BannerFirstPorts =
        new() { 21, 22, 23, 25, 110, 143, 587, 3306, 6379, 27017 };

    /// <summary>解析 "80,443,8000-8010" 这类端口表达式。</summary>
    public static (int[] Ports, string Error) ParsePorts(string spec)
    {
        var set = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(spec))
            return (Array.Empty<int>(), "端口不能为空");

        foreach (var rawPart in spec.Split(new[] { ',', '，', ' ', ';', '；' },
                                           StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;

            if (part.Contains('-'))
            {
                var seg = part.Split('-', 2);
                if (!int.TryParse(seg[0].Trim(), out int a) || !int.TryParse(seg[1].Trim(), out int b))
                    return (Array.Empty<int>(), $"无法解析端口范围：{part}");
                if (a > b) (a, b) = (b, a);
                if (b - a > 5000) return (Array.Empty<int>(), $"端口范围过大（{part}），单次最多 5000 个");
                for (int p = a; p <= b; p++)
                    if (p is >= 1 and <= 65535) set.Add(p);
            }
            else
            {
                if (!int.TryParse(part, out int p))
                    return (Array.Empty<int>(), $"无法解析端口：{part}");
                if (p is < 1 or > 65535) return (Array.Empty<int>(), $"端口超范围：{p}");
                set.Add(p);
            }
        }

        if (set.Count == 0) return (Array.Empty<int>(), "没有解析出有效端口");
        return (set.ToArray(), "");
    }

    public static async Task<PortScanReport> ScanAsync(
        string host, int[] ports, int timeoutMs, int concurrency,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var report = new PortScanReport { Host = host };
        var results = new PortResult[ports.Length];
        int done = 0;

        using var sem = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = new List<Task>(ports.Length);

        for (int i = 0; i < ports.Length; i++)
        {
            int idx = i;
            int port = ports[i];
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    results[idx] = await ProbeOneAsync(host, port, timeoutMs, ct).ConfigureAwait(false);
                    int d = Interlocked.Increment(ref done);
                    progress?.Report((d, ports.Length));
                }
                finally { sem.Release(); }
            }, ct));
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 取消时返回已完成的部分 */ }

        report.Results.AddRange(results.Where(r => r != null).OrderBy(r => r.Port));
        return report;
    }

    private static async Task<PortResult> ProbeOneAsync(
        string host, int port, int timeoutMs, CancellationToken ct)
    {
        var r = new PortResult
        {
            Host = host,
            Port = port,
            Service = KnownPorts.TryGetValue(port, out var s) ? s : "",
        };

        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sw.Stop();

            r.Open = true;
            r.Ms = sw.Elapsed.TotalMilliseconds;
            r.Banner = await TryGrabBannerAsync(client, port, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { r.Error = "超时"; }
        catch (SocketException ex) { r.Error = DescribeSocketError(ex.SocketErrorCode); }
        catch (Exception ex) { r.Error = ex.Message.Length > 40 ? ex.Message[..40] : ex.Message; }

        return r;
    }

    /// <summary>对会主动说话的协议直接读；HTTP 类则先发一个 HEAD 请求。</summary>
    private static async Task<string> TryGrabBannerAsync(TcpClient client, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(900);

            var stream = client.GetStream();

            if (port is 80 or 8080 or 8000 or 8888 or 9200)
            {
                var head = Encoding.ASCII.GetBytes("HEAD / HTTP/1.0\r\nHost: localhost\r\n\r\n");
                await stream.WriteAsync(head, cts.Token).ConfigureAwait(false);
            }
            else if (port is not (21 or 22 or 23 or 25 or 110 or 143 or 587 or 3306 or 6379 or 27017))
            {
                // TLS 类端口不会明文说话，读也读不到东西
                return "";
            }

            var buf = new byte[256];
            int n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
            if (n <= 0) return "";

            var text = Encoding.ASCII.GetString(buf, 0, n)
                .Replace("\r", " ").Replace("\n", " ").Trim();

            return text.Length > 80 ? text[..80] : text;
        }
        catch { return ""; }
    }

    private static string DescribeSocketError(SocketError e) => e switch
    {
        SocketError.ConnectionRefused => "连接被拒绝（端口关闭）",
        SocketError.HostUnreachable => "主机不可达",
        SocketError.NetworkUnreachable => "网络不可达",
        SocketError.TimedOut => "超时",
        SocketError.AccessDenied => "被防火墙拒绝",
        _ => e.ToString(),
    };

    public static string ServiceName(int port) => KnownPorts.TryGetValue(port, out var s) ? s : "";
}
