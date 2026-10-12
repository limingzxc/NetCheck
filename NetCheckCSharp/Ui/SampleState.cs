using NetCheck.Core;

namespace NetCheck.Ui;

/// <summary>
/// 演示数据。仅在设置环境变量 NETCHECK_UI_DEMO=1 时载入，用于界面预览与离屏截图，
/// 正常运行时不会被执行。数据取自一次真实的校园网实测，便于评估界面在满数据下的排版。
/// </summary>
public static class SampleState
{
    public static DiagState Build()
    {
        var s = new DiagState
        {
            Hostname = "PC-DESKTOP",
            Gateway = "192.168.1.1",
            Proxy = new Dictionary<string, string>(),
        };

        s.Adapters = new List<AdapterInfo>
        {
            new()
            {
                Name = "以太网", Description = "Intel(R) Ethernet Connection (2) I219-V",
                Type = "有线", Status = "已连接",
                IPv4 = "192.168.1.23", Mask = "255.255.255.0", Mac = "8C-16-45-2A-1B-3C",
                Gateway = "192.168.1.1", Dns = { "114.114.114.114", "223.5.5.5" },
                IPv6 = "2408:8207:1234:5678:9abc:def0:1234:5678",
                IPv6Addresses =
                {
                    "2408:8207:1234:5678:9abc:def0:1234:5678",
                    "2408:8207:1234:5678:1a2b:3c4d:5e6f:7080",
                    "fd00:1234:5678::1",
                },
                SpeedMbps = 1000, DhcpEnabled = true, IsUp = true,
            },
            new()
            {
                Name = "WLAN", Description = "Intel(R) Wi-Fi 6 AX201 160MHz",
                Type = "无线", Status = "已断开", IsUp = false,
            },
            new()
            {
                Name = "VMware Network Adapter VMnet8",
                Description = "VMware Virtual Ethernet Adapter for VMnet8",
                Type = "虚拟", Status = "已断开", IsUp = false,
            },
        };

        s.PublicIpDirect = "203.0.113.10";

        s.PublicIpHttpDirect = new List<PublicIpResult>
        {
            new() { Service = "ipify", Ip = "203.0.113.10", Seconds = 0.21 },
            new() { Service = "ip.sb", Ip = "198.51.100.20", Seconds = 0.35 },
            new() { Service = "ipip.net", Ip = "192.0.2.30", Seconds = 0.48 },
        };
        s.PublicIpHttpProxy = new List<PublicIpResult>
        {
            new() { Service = "ipify", Ip = "203.0.113.10", Seconds = 0.24 },
            new() { Service = "ip.sb", Ip = "198.51.100.20", Seconds = 0.38 },
            new() { Service = "ipip.net", Ip = "192.0.2.30", Seconds = 0.51 },
        };

        // IP 归属地。演示数据是死的，直接给结论 —— 截图要能立刻看到效果，
        // 不能让预览依赖外网。真实运行时这张表由 IpGeo.FillAsync 填。
        // 私有地址（192.168.x）故意不写进来：IpGeo.Of 自己就会给出「局域网」。
        s.IpLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["203.0.113.10"] = "示例 · 华东 电信",
            ["198.51.100.20"] = "示例 · 中国香港",
            ["192.0.2.30"] = "示例 · 美国 西海岸",
            ["114.114.114.114"] = "江苏省南京市",
            ["223.5.5.5"] = "浙江省杭州市阿里云",
            ["218.30.118.6"] = "北京市电信",
            ["119.29.29.29"] = "北京市腾讯云",
            ["202.112.36.1"] = "北京市 教育网",
            ["101.4.117.29"] = "北京市 教育网",
            ["101.4.117.66"] = "北京市 教育网",
            ["101.4.115.34"] = "北京市 教育网",
            ["101.4.115.122"] = "北京市 教育网",
            ["2408:8207:1234:5678:9abc:def0:1234:5678"] = "示例 · 华东 电信",
        };

        // IPv6：一条临时地址 + 一条固定地址 + 一条 ULA，覆盖卡片里的三种分类
        s.Ipv6 = new Ipv6Report
        {
            Addresses =
            {
                new Ipv6AddressInfo
                {
                    Address = "2408:8207:1234:5678:9abc:def0:1234:5678",
                    Adapter = "以太网", Kind = "全局", IsTemporary = true,
                },
                new Ipv6AddressInfo
                {
                    Address = "2408:8207:1234:5678:1a2b:3c4d:5e6f:7080",
                    Adapter = "以太网", Kind = "全局",
                },
                new Ipv6AddressInfo { Address = "fd00:1234:5678::1", Adapter = "以太网", Kind = "内网" },
            },
            EgressIp = "2408:8207:1234:5678:9abc:def0:1234:5678",
            Pings =
            {
                new Ipv6PingResult
                {
                    Name = "AliDNS", Host = Ipv6Probe.DomesticTarget,
                    Region = "国内", Ms = 12, Attempts = 1,
                },
                new Ipv6PingResult
                {
                    Name = "Cloudflare", Host = Ipv6Probe.ForeignTarget,
                    Region = "国外", Ms = 178, Attempts = 2,
                },
            },
        };

        // HTTP / HTTPS：三个内置目标全通，自定义那个**故意卡在 TCP 层** ——
        // 截图要能同时展示「四层全过」和「定位到具体某一层」两种形态，
        // 否则这张卡片最有价值的那个能力在预览里完全看不出来。
        s.Web = new WebReport
        {
            Targets =
            {
                new WebTargetResult
                {
                    Name = "百度", Host = "www.baidu.com", Region = "国内",
                    ResolvedIp = "110.242.68.66", HttpStatus = 200,
                    Steps =
                    {
                        new WebStep { Name = "DNS", Ok = true, Ms = 11, Detail = "110.242.68.66 · 共 2 条记录" },
                        new WebStep { Name = "TCP", Ok = true, Ms = 9, Detail = "110.242.68.66:443" },
                        new WebStep { Name = "TLS", Ok = true, Ms = 34, Detail = "Tls13 · 证书 2027-01-08 到期" },
                        new WebStep { Name = "HTTP", Ok = true, Ms = 41, Detail = "HTTP/1.1 200 OK" },
                    },
                },
                new WebTargetResult
                {
                    Name = "腾讯网", Host = "www.qq.com", Region = "国内",
                    ResolvedIp = "121.14.77.201", HttpStatus = 200,
                    Steps =
                    {
                        new WebStep { Name = "DNS", Ok = true, Ms = 8, Detail = "121.14.77.201 · 共 3 条记录" },
                        new WebStep { Name = "TCP", Ok = true, Ms = 7, Detail = "121.14.77.201:443" },
                        new WebStep { Name = "TLS", Ok = true, Ms = 28, Detail = "Tls13 · 证书 2026-12-19 到期" },
                        new WebStep { Name = "HTTP", Ok = true, Ms = 36, Detail = "HTTP/1.1 200 OK" },
                    },
                },
                new WebTargetResult
                {
                    Name = "Cloudflare", Host = "www.cloudflare.com", Region = "境外",
                    ResolvedIp = "104.16.132.229", HttpStatus = 200,
                    Steps =
                    {
                        new WebStep { Name = "DNS", Ok = true, Ms = 46, Detail = "104.16.132.229 · 共 2 条记录" },
                        new WebStep { Name = "TCP", Ok = true, Ms = 172, Detail = "104.16.132.229:443" },
                        new WebStep { Name = "TLS", Ok = true, Ms = 214, Detail = "Tls13 · 证书 2026-11-30 到期" },
                        new WebStep { Name = "HTTP", Ok = true, Ms = 238, Detail = "HTTP/1.1 200 OK" },
                    },
                },
                new WebTargetResult
                {
                    Name = "www.example.com", Host = "www.example.com", Region = "自定义",
                    ResolvedIp = "93.184.215.14",
                    Steps =
                    {
                        new WebStep { Name = "DNS", Ok = true, Ms = 33, Detail = "93.184.215.14 · 共 1 条记录" },
                        new WebStep
                        {
                            Name = "TCP", Ok = false, Ms = 3000,
                            Error = "连接超时",
                        },
                    },
                },
            },
        };

        s.Latency = new LatencyReport
        {
            Gateway = new LatencyTarget
            {
                Name = "网关", Host = "192.168.1.1", Method = "ICMP",
                Min = 0.3, Avg = 0.4, Max = 0.9, Loss = 0,
                Samples = new List<double> { 0.4, 0.3, 0.5, 0.4 },
            },
        };
        // 数值按本机实测的量级编：国内 CDN 边缘节点十几毫秒，
        // 境外平台（Steam）高一个数量级 —— 这样截图里能同时看到「良好」和「偏高」两种色调
        s.Latency.Targets.AddRange(new[]
        {
            Mk("网页浏览", "百度",     "www.baidu.com",   "ICMP",    10.2, 11.4, 18.2, 0, 10.5, 11.2, 14.1, 10.8),
            Mk("网页浏览", "淘宝",     "www.taobao.com",  "ICMP",     6.4,  7.2,  9.8, 0,  6.6,  7.1,  8.3,  6.9),
            Mk("网页浏览", "腾讯网",   "www.qq.com",      "ICMP",     8.1,  9.3, 12.6, 0,  8.4,  9.1, 11.2,  8.9),

            Mk("在线视频", "哔哩哔哩", "www.bilibili.com", "ICMP",   12.9, 14.2, 21.4, 0, 13.1, 14.0, 18.2, 13.8),
            Mk("在线视频", "爱奇艺",   "www.iqiyi.com",    "ICMP",   18.3, 20.1, 27.9, 0, 18.6, 19.8, 24.1, 19.2),
            Mk("在线视频", "腾讯视频", "v.qq.com",         "ICMP",   10.1, 11.3, 16.8, 0, 10.4, 11.1, 14.2, 10.9),

            Mk("游戏",     "腾讯游戏", "game.qq.com",            "ICMP", 7.2,  8.1, 11.4, 0,  7.4,  8.0,  9.8,  7.9),
            Mk("游戏",     "网易游戏", "game.163.com",           "ICMP", 7.5,  8.4, 12.1, 0,  7.7,  8.3, 10.6,  8.2),
            Mk("游戏",     "Steam",    "store.steampowered.com", "ICMP", 70.4, 72.9, 79.6, 0, 71.2, 72.6, 76.4, 72.1),
        });

        // 「当前 DNS」和「对比 DNS」在样例里刻意用**两组不同的地址**，
        // 正好演示这两段的区别：前者是网卡上配的（一台路由器 + 一台运营商 DNS），
        // 后者只是拿来比一比的公共 DNS。
        s.DnsServers = new List<string> { "192.168.1.1", "218.30.118.6" };
        s.DnsServerTests = new List<DnsServerTest>
        {
            new() { Server = "192.168.1.1", Ms = 0.5, Answers = 2 },
            new() { Server = "218.30.118.6", Ms = 38.6, Answers = 2 },
        };

        // 对比用的公共 DNS，域名和「当前 DNS」一致（www.baidu.com），数字才能横向比。
        // 8.8.8.8 超时是有意为之：它在国内经常被拦或极慢，正好演示
        // 「对比 DNS 段不按绝对阈值上色」—— 用户没在用这台，不该为它报故障。
        s.DnsCompareTests = new List<DnsServerTest>
        {
            new() { Server = "223.5.5.5", Ms = 7.1, Answers = 2 },
            new() { Server = "119.29.29.29", Ms = 9.4, Answers = 2 },
            new() { Server = "114.114.114.114", Ms = 12.6, Answers = 2 },
            new() { Server = "8.8.8.8", Error = "超时" },
        };

        s.DnsResolve = new List<DnsResolveTest>
        {
            new() { Domain = "www.qq.com", Address = "121.14.77.221", Min = 6.1, Avg = 7.0, Max = 8.4, Ok = true },
            new() { Domain = "www.baidu.com", Address = "110.242.68.66", Min = 8.2, Avg = 9.4, Max = 11.0, Ok = true },
            new() { Domain = "www.taobao.com", Address = "106.11.84.3", Min = 7.5, Avg = 8.8, Max = 10.2, Ok = true },
            new() { Domain = "github.com", Address = "20.205.243.166", Min = 42.1, Avg = 58.3, Max = 91.2, Ok = true },
        };

        s.Hijack = new HijackReport
        {
            ProbeDomain = "nx-7f3a9c2e.probe.invalid",
            // 服务器列表和 DnsProbe.CompareServers 保持一致，8.8.8.8 的无响应
            // 也和上面「对比 DNS」里那台对得上 —— 同一份截图里两处自相矛盾很扎眼。
            Checks =
            {
                new HijackCheck { Server = "223.5.5.5", Ok = true, RCode = 3 },
                new HijackCheck { Server = "119.29.29.29", Ok = true, RCode = 3 },
                new HijackCheck { Server = "114.114.114.114", Ok = true, RCode = 3 },
                new HijackCheck { Server = "8.8.8.8", Error = "超时" },
            },
            Comparisons =
            {
                new DnsComparison
                {
                    Domain = "www.baidu.com",
                    Entries =
                    {
                        new DnsComparisonEntry { Server = "223.5.5.5", Ok = true, Addresses = { "110.242.68.66" } },
                        new DnsComparisonEntry { Server = "119.29.29.29", Ok = true, Addresses = { "110.242.68.66" } },
                        new DnsComparisonEntry { Server = "114.114.114.114", Ok = true, Addresses = { "110.242.68.66" } },
                    },
                },
                new DnsComparison
                {
                    Domain = "github.com",
                    Entries =
                    {
                        new DnsComparisonEntry { Server = "223.5.5.5", Ok = true, Addresses = { "20.205.243.166" } },
                        new DnsComparisonEntry { Server = "119.29.29.29", Ok = true, Addresses = { "20.205.243.166" } },
                        new DnsComparisonEntry { Server = "114.114.114.114", Ok = true, Addresses = { "140.82.113.4" } },
                    },
                },
            },
        };

        s.Nat = new NatReport
        {
            LocalPort = 51234,
            Type = "多出口 / 对称型 NAT",
            Detail = "不同目标看到的公网 IP 不同（203.0.113.10、198.51.100.20、192.0.2.30），"
                   + "属于多出口网络；同时外部端口也不固定，P2P 直连打洞成功率很低，需要中继服务器。",
            MappedIps = { "203.0.113.10", "198.51.100.20", "192.0.2.30" },
            MappedPorts = { 51234, 51877, 52103 },
            Observations =
            {
                new StunObservation { Server = "stun.hitv.com:3478", MappedIp = "203.0.113.10", MappedPort = 51234 },
                new StunObservation { Server = "stun.chat.bilibili.com:3478", MappedIp = "198.51.100.20", MappedPort = 51877 },
                new StunObservation { Server = "stun.douyucdn.cn:18000", MappedIp = "192.0.2.30", MappedPort = 52103 },
            },
        };

        s.BandwidthDirect = new BandwidthReport
        {
            Source = "清华镜像", Bytes = 162_500_000, Seconds = 10.0,
        };

        s.Traceroute = new TracerouteReport
        {
            Target = "223.5.5.5", TargetIp = "223.5.5.5",
            Reached = false, StoppedEarly = true,
            Hops =
            {
                new HopInfo { Ttl = 1, Address = "192.168.1.1", Ms = 0.6, Status = "TtlExpired" },
                new HopInfo { Ttl = 2, Address = "100.64.0.1", Ms = 1.4, Status = "TtlExpired" },
                new HopInfo { Ttl = 3, Address = "202.112.36.1", Ms = 4.8, Status = "TtlExpired" },
                new HopInfo { Ttl = 4, Address = "101.4.117.29", Ms = 6.2, Status = "TtlExpired" },
                new HopInfo { Ttl = 5, Address = "101.4.117.66", Ms = 7.1, Status = "TtlExpired" },
                new HopInfo { Ttl = 6, Address = "101.4.115.34", Ms = 9.3, Status = "TtlExpired" },
                new HopInfo { Ttl = 7, Address = "101.4.115.122", Ms = 11.7, Status = "TtlExpired" },
                new HopInfo { Ttl = 8, Address = "223.5.5.5", Ms = 6.8, Status = "TtlExpired" },
                new HopInfo { Ttl = 9, Address = "", Status = "Timeout" },
            },
        };

        s.PortScan = BuildPortScan("223.5.5.5");

        s.Mtu = new MtuReport
        {
            Target = "223.5.5.5", Reachable = true,
            MaxPayload = 1472, PathMtu = 1500, NextPayloadFails = true,
            Tests =
            {
                new MtuTest { Size = 1372, Ok = true },
                new MtuTest { Size = 1472, Ok = true },
                new MtuTest { Size = 1500, Ok = false },
            },
        };

        return s;
    }

    private static LatencyTarget Mk(string category, string name, string host, string method,
                                    double min, double avg, double max, double loss,
                                    params double[] samples) => new()
    {
        Name = name, Category = category, Host = host, Method = method,
        Min = min, Avg = avg, Max = max, Loss = loss,
        Samples = samples.ToList(),
    };

    /// <summary>
    /// 一份接近真实规模的端口扫描结果（34 个端口，4 个开放）。
    /// 开放端口少、关闭端口多，正好覆盖界面里「关闭端口超过 24 个要折叠」的分支。
    /// </summary>
    private static PortScanReport BuildPortScan(string host)
    {
        var known = new Dictionary<int, string>
        {
            [22] = "SSH", [53] = "DNS", [80] = "HTTP", [443] = "HTTPS", [853] = "DoT",
            [3389] = "RDP", [8080] = "HTTP-Alt",
        };
        var open = new HashSet<int> { 53, 80, 443, 853 };

        var ports = new List<int> { 22, 53, 80, 443, 853, 3389, 8080 };
        for (int p = 8000; p <= 8020; p++) ports.Add(p);
        for (int p = 9000; p <= 9005; p++) ports.Add(p);

        var report = new PortScanReport { Host = host };
        foreach (var p in ports)
        {
            bool isOpen = open.Contains(p);
            report.Results.Add(new PortResult
            {
                Host = host,
                Port = p,
                Open = isOpen,
                Ms = isOpen ? 6.5 + (p % 7) * 0.3 : 0,
                Service = known.TryGetValue(p, out var name) ? name : "",
                // 高端口多为防火墙静默丢弃，低端口多是明确拒绝 —— 跟真实扫描一致
                Error = isOpen ? "" : (p < 1024 ? "连接被拒绝" : "超时"),
            });
        }
        return report;
    }
}
