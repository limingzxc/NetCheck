using System.Text;
using NetCheck.Core;
using NetCheck.Tests;

Console.OutputEncoding = Encoding.UTF8;

bool Should(string k) => args.Length == 0 || args.Contains(k);

// ==================================================================
// 校验模式：dotnet run -- verifyjson <路径>
// 用应用自己的模型把导出的 json 反序列化回来。
// 之所以不交给 PowerShell 的 ConvertFrom-Json：它把 json 属性名当大小写不敏感，
// 遇到 Proxy 里的 http_proxy / HTTP_PROXY 会直接报「重复键」——那是解析器的限制，
// 不是导出的问题。用真实模型验证才说明问题。
// ==================================================================
if (args.Length > 0 && args[0] == "verifyjson")
{
    if (args.Length < 2)
    {
        Console.WriteLine("FAIL 用法: CoreTest verifyjson <json路径>");
        Environment.ExitCode = 1;
        return;
    }

    var path = args[1];
    if (!File.Exists(path))
    {
        Console.WriteLine("FAIL 文件不存在: " + path);
        Environment.ExitCode = 1;
        return;
    }

    DiagState? d;
    try
    {
        d = System.Text.Json.JsonSerializer.Deserialize<DiagState>(File.ReadAllText(path));
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL 反序列化失败: " + ex.GetType().Name + ": " + ex.Message);
        Environment.ExitCode = 1;
        return;
    }

    if (d == null)
    {
        Console.WriteLine("FAIL 反序列化得到 null");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("OK Hostname=" + d.Hostname);
    Console.WriteLine("OK Timestamp=" + d.Timestamp);
    Console.WriteLine("OK Adapters=" + (d.Adapters?.Count ?? 0));
    Console.WriteLine("OK ProxyKeys=" + (d.Proxy == null ? "null" : string.Join(",", d.Proxy.Keys)));
    Console.WriteLine("OK DnsServers=" + (d.DnsServers == null ? "null" : string.Join(",", d.DnsServers)));
    // 「当前 DNS」和「对比 DNS」分开报 —— 只看 DnsServers 分不出「对比测速到底跑没跑」
    Console.WriteLine("OK DnsServerTests=" + (d.DnsServerTests?.Count ?? 0));
    Console.WriteLine("OK DnsCompareTests=" + (d.DnsCompareTests?.Count ?? 0));
    Console.WriteLine("OK PortScanHost=" + (d.PortScan?.Host ?? "null"));
    Console.WriteLine("OK PortScanResults=" + (d.PortScan?.Results.Count ?? 0));
    Console.WriteLine("OK PortScanOpen=" + (d.PortScan?.OpenCount ?? 0));
    Console.WriteLine("OK NatType=" + (d.Nat?.Type ?? "null"));
    // 延迟结果里的目标条数。用来断言「只跑 --check latency 时确实只测了延迟」，
    // 因为延迟和带宽同在「连通性」卡片里，光看卡片有没有数据分不出是谁填的。
    Console.WriteLine("OK LatencyTargets=" + (d.Latency?.Targets.Count ?? 0));
    // 出口 IP 是否测到。同理，出口 IP 和延迟、DNS 同在「概览」卡片里，
    // 只能靠这个区分「跑的是 egress 还是 quick」。
    Console.WriteLine("OK Egress=" + (d.PublicIpDirect == null ? "null" : "set"));
    Console.WriteLine("OK Bandwidth=" + (d.BandwidthDirect == null ? "null" : "set"));
    // IPv6 与 IP 归属地：用来断言这两块确实跑到了、并且跟着 json 一起导出
    Console.WriteLine("OK Ipv6=" + (d.Ipv6 == null ? "null" : d.Ipv6.Verdict));
    Console.WriteLine("OK Ipv6Addresses=" + (d.Ipv6?.Addresses.Count ?? 0));
    Console.WriteLine("OK IpLocations=" + d.IpLocations.Count);
    // 网页连通性：目标数 / 结论 / 每个目标跑过的层数 / 解析到地址的目标数。
    // 后两项用来断言「确实跑了逐层探测」，而不是只测了个「通不通」——
    // 层数能看出「第一层失败就停」，解析数能看出 DNS 层真的跑过。
    Console.WriteLine("OK WebTargets=" + (d.Web?.Targets.Count ?? 0));
    Console.WriteLine("OK WebVerdict=" + (d.Web == null ? "null" : d.Web.Verdict));
    Console.WriteLine("OK WebStepCounts=" + (d.Web == null
        ? "null" : string.Join(",", d.Web.Targets.Select(t => t.Steps.Count))));
    Console.WriteLine("OK WebResolved=" + (d.Web?.Targets.Count(t => t.ResolvedIp.Length > 0) ?? 0));
    return;
}

// ==================================================================
// 单元测试模式：dotnet run -- unit
// 纯逻辑断言，不碰网络，几秒钟跑完
// ==================================================================
if (Should("unit"))
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Tests.RunAll();
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine(new string('=', 62));
    Console.WriteLine($"  单元测试：{T.Passed} 通过 / {T.Failed} 失败"
                      + $"   （{sw.Elapsed.TotalMilliseconds:F0} ms）");
    Console.WriteLine(new string('=', 62));

    if (T.Failed > 0)
    {
        Console.WriteLine();
        Console.WriteLine("失败明细：");
        foreach (var f in T.Failures) Console.WriteLine("  ✗ " + f);
        Console.WriteLine();
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine("  全部通过。");
    }

    if (args.Length > 0 && !args.Any(a => a is "quick" or "local" or "egress" or "dns" or "nat"
                                      or "trace" or "port" or "mtu" or "speed" or "bundle"
                                      or "ipv6" or "geo"))
        return;
    Console.WriteLine();
}
var state = new DiagState { Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
var progress = new Progress<string>(m => Console.Error.WriteLine("  · " + m));

async Task Guard(string name, Func<Task> body)
{
    try { await body(); }
    catch (Exception ex) { Console.Error.WriteLine($"  !! {name} 异常: {ex.GetType().Name}: {ex.Message}"); }
}

if (Should("local") || Should("quick"))
{
    Console.Error.WriteLine("[本机网络配置]");
    await Guard("local", async () => await Task.Run(() =>
    {
        state.Adapters = LocalNetInfo.GetAdapters();
        state.Hostname = LocalNetInfo.HostName;
        state.Proxy = LocalNetInfo.GetProxySettings();
        state.Gateway = LocalNetInfo.PrimaryGateway(state.Adapters);
    }));
}

if (Should("ipv6"))
{
    Console.Error.WriteLine("[IPv6]");
    await Guard("ipv6", async () =>
    {
        var adapters = state.Adapters ?? LocalNetInfo.GetAdapters();
        state.Ipv6 = await Ipv6Probe.DetectAsync(adapters, progress, CancellationToken.None);
        Console.Error.WriteLine($"  结论: {state.Ipv6.Verdict}");
        foreach (var a in state.Ipv6.Addresses)
            Console.Error.WriteLine($"  地址: {a.Kind}{(a.IsTemporary ? "（临时）" : "")} {a.Address} [{a.Adapter}]");
        if (state.Ipv6.EgressIp.Length > 0)
            Console.Error.WriteLine($"  出口: {state.Ipv6.EgressIp}");
        foreach (var p in state.Ipv6.Pings)
            Console.Error.WriteLine($"  {p.Region} {p.Name}: " + (p.Ok ? $"{p.Ms:F0} ms" : p.Error));
    });
}

if (Should("nat"))
{
    Console.Error.WriteLine("[NAT 类型]");
    await Guard("nat", async () =>
    {
        var ips = (state.Adapters ?? LocalNetInfo.GetAdapters())
            .Select(a => a.IPv4).Where(x => x.Length > 0).ToList();
        state.Nat = await StunAndNat.DetectAsync(ips, CancellationToken.None);
    });
}

if (Should("egress") || Should("quick"))
{
    Console.Error.WriteLine("[公网出口 IP]");
    await Guard("egress", async () => state.PublicIpDirect = await StunAndNat.GetEgressIpAsync(CancellationToken.None));
    await Guard("http-proxy", async () => state.PublicIpHttpProxy = await PublicIpProbe.QueryHttpAsync(true, CancellationToken.None));
    await Guard("http-direct", async () => state.PublicIpHttpDirect = await PublicIpProbe.QueryHttpAsync(false, CancellationToken.None));
}

if (Should("quick"))
{
    // 「一键检测」在界面里跑的就是全部原子项，这里也跟着把 IPv6 带上，
    // 免得 e2e 断言「quick 跑全了」时两边对不上。
    // 判空是为了不带参数全量跑时不要重复探测 —— 上面那个独立块已经跑过一次了。
    if (state.Ipv6 == null)
    {
        Console.Error.WriteLine("[IPv6]");
        await Guard("ipv6", async () =>
        {
            var adapters = state.Adapters ?? LocalNetInfo.GetAdapters();
            state.Ipv6 = await Ipv6Probe.DetectAsync(adapters, progress, CancellationToken.None);
        });
    }

    Console.Error.WriteLine("[DNS 服务器]");
    await Guard("dns", async () =>
    {
        var dns = LocalNetInfo.AllDnsServers(state.Adapters ?? new List<AdapterInfo>());
        state.DnsServers = dns;
        var tests = new List<DnsServerTest>();
        foreach (var d in dns)
            tests.Add(await DnsProbe.TestServerAsync(d, "www.baidu.com", 2500, CancellationToken.None));
        state.DnsServerTests = tests;
    });

    Console.Error.WriteLine("[网络延迟]");
    await Guard("latency", async () =>
        state.Latency = await LatencyProbe.RunAllAsync(state.Gateway, progress, CancellationToken.None));
}

if (Should("dns"))
{
    Console.Error.WriteLine("[DNS 解析 + 劫持检测]");
    await Guard("dns-deep", async () =>
    {
        var list = new List<DnsResolveTest>();
        foreach (var d in DnsProbe.TestDomains)
            list.Add(await DnsProbe.ResolveAsync(d, 3, CancellationToken.None));
        state.DnsResolve = list;
        state.Hijack = await DnsProbe.DetectHijackAsync(CancellationToken.None);
    });
}

if (Should("trace"))
{
    Console.Error.WriteLine("[路由追踪]");
    await Guard("trace", async () =>
        state.Traceroute = await Traceroute.TraceAsync(
            "223.5.5.5", 30, 1200, new Progress<int>(t => Console.Error.WriteLine($"   第 {t} 跳")), CancellationToken.None));
}

if (Should("port"))
{
    Console.Error.WriteLine("[端口检测]");
    await Guard("port-local", async () =>
    {
        var (ports, err) = PortCheck.ParsePorts("22,80,443,3389,8080,8000-8003");
        if (err.Length > 0) throw new InvalidOperationException(err);
        state.PortScan = await PortCheck.ScanAsync("127.0.0.1", ports, 1500, 64, null, CancellationToken.None);
    });

    // 另外单独跑一次远程目标，验证「开放」路径是否正常
    await Guard("port-remote", async () =>
    {
        var (ports, _) = PortCheck.ParsePorts("53,443,80,8080");
        var remote = await PortCheck.ScanAsync("223.5.5.5", ports, 2500, 16, null, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine("--- 远程端口检测对照：223.5.5.5 ---");
        foreach (var r in remote.Results)
            Console.WriteLine($"  {r.Port,-6} {(r.Open ? "开放" : "关闭"),-5} {r.Ms,7:F1} ms   {r.Error}");
    });
}

if (Should("mtu"))
{
    Console.Error.WriteLine("[MTU 探测]");
    await Guard("mtu", async () =>
        state.Mtu = await MtuProbe.DetectAsync("223.5.5.5", progress, CancellationToken.None));
}

if (Should("speed"))
{
    Console.Error.WriteLine("[带宽测速]");
    await Guard("speed", async () =>
    {
        state.BandwidthDirect = await BandwidthTester.TestAsync(8, false, progress, CancellationToken.None);
        if (!state.BandwidthDirect.Ok)
            state.BandwidthProxy = await BandwidthTester.TestAsync(8, true, progress, CancellationToken.None);
    });
}

// 归属地放在最后统一补：它要拿前面所有步骤产出的 IP 当输入（出口 / DNS / 路由 / NAT / IPv6）。
// 界面里是每跑完一项就顺手补一次，靠 FillAsync 的幂等性保证不会重复请求。
if (Should("geo") || Should("quick") || Should("egress") || Should("trace"))
{
    Console.Error.WriteLine("[IP 归属地]");
    await Guard("geo", async () =>
    {
        var n = IpGeo.PendingCount(state);
        await IpGeo.FillAsync(state, CancellationToken.None);
        Console.Error.WriteLine($"  查询 {n} 个地址，共得到 {state.IpLocations.Count} 条归属地");

        foreach (var kv in state.IpLocations.OrderBy(x => x.Key, StringComparer.Ordinal))
            Console.Error.WriteLine($"  {kv.Key,-46} {kv.Value}");

        // 私有地址不进表，但渲染层照样给结论 —— 两条路都要验一下
        Console.Error.WriteLine($"  Of(10.0.0.1)  = {IpGeo.Of(state, "10.0.0.1")}");
        Console.Error.WriteLine($"  Of(223.5.5.5) = {IpGeo.Of(state, "223.5.5.5")}");

        // 幂等性：再调一次不该产生新的查询
        if (IpGeo.PendingCount(state) != 0)
            Console.Error.WriteLine("  !! FillAsync 之后仍有待查项，幂等性被破坏");
        else
            Console.Error.WriteLine("  OK 再调一次 PendingCount 为 0（不会重复请求）");
    });
}

if (Should("bundle"))
{
    Console.Error.WriteLine("[诊断包导出]");
    await Guard("bundle", async () =>
    {
        foreach (var masked in new[] { false, true })
        {
            var zip = Path.Combine(AppContext.BaseDirectory,
                masked ? "test_bundle_masked.zip" : "test_bundle.zip");
            var res = await DiagBundle.ExportAsync(
                state, ReportBuilder.RenderText(state), zip, progress, CancellationToken.None, masked);
            if (!res.Ok)
            {
                Console.Error.WriteLine($"  导出失败（{(masked ? "脱敏" : "原始")}）: " + res.Error);
                continue;
            }
            Console.Error.WriteLine($"  [{res.Masked} 脱敏] 已生成 {zip}  ({res.SizeBytes / 1024.0:F0} KB)");
            Console.Error.WriteLine("  包含: " + string.Join(", ", res.Entries));

            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            foreach (var e in archive.Entries)
                Console.Error.WriteLine($"    {e.Name}  {e.Length} 字节");

            // 脱敏版要能当场看出确实脱干净了：拿主机名和公网出口 IP 去里面搜，
            // 搜到就说明漏了。这两个值恰好是最该抹掉的。
            if (masked)
            {
                var text = "";
                using var a2 = System.IO.Compression.ZipFile.OpenRead(zip);
                foreach (var e in a2.Entries)
                {
                    using var sr = new StreamReader(e.Open(), System.Text.Encoding.UTF8);
                    text += sr.ReadToEnd();
                }

                var host = state.Hostname;
                if (!string.IsNullOrWhiteSpace(host) && text.Contains(host))
                    Console.Error.WriteLine($"  !! 脱敏包里仍然出现主机名 {host}");
                else
                    Console.Error.WriteLine("  OK 脱敏包里搜不到主机名");

                var ips = state.AllPublicIps();
                var leaked = ips.Where(ip => text.Contains(ip)).ToList();
                if (leaked.Count > 0)
                    Console.Error.WriteLine("  !! 脱敏包里仍然出现公网 IP: " + string.Join(", ", leaked));
                else
                    Console.Error.WriteLine($"  OK 脱敏包里搜不到公网 IP（原有 {ips.Count} 个）");

                if (text.Contains("本包已脱敏"))
                    Console.Error.WriteLine("  OK 说明文件标注了已脱敏");
                else
                    Console.Error.WriteLine("  !! 说明文件没有标注脱敏");
            }
        }
    });
}

Console.WriteLine(ReportBuilder.RenderText(state));
