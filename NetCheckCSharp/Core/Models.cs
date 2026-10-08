namespace NetCheck.Core;

/// <summary>单块网卡的配置信息。</summary>
public sealed class AdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string IPv4 { get; set; } = "";
    public string IPv6 { get; set; } = "";
    /// <summary>
    /// 这块网卡上全部非链路本地的 IPv6 地址。
    /// 保留单值的 <see cref="IPv6"/> 只是为了兼容旧数据，新代码一律看这个列表 ——
    /// 一块网卡同时挂「固定地址 + 隐私扩展临时地址」是常态，只留一个会丢掉另一半信息。
    /// </summary>
    public List<string> IPv6Addresses { get; set; } = new();
    public string Mask { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Gateway { get; set; } = "";
    public List<string> Dns { get; set; } = new();
    public long SpeedMbps { get; set; }
    public bool DhcpEnabled { get; set; }
    public bool IsUp { get; set; }
}

/// <summary>某个 HTTP 接口返回的公网 IP。</summary>
public sealed class PublicIpResult
{
    public string Service { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Extra { get; set; } = "";
    public string Error { get; set; } = "";
    public double Seconds { get; set; }
    public bool Ok => Error.Length == 0 && Ip.Length > 0;
}

/// <summary>单个目标的延迟探测结果。</summary>
public sealed class LatencyTarget
{
    public string Name { get; set; } = "";
    /// <summary>用途分类（网页浏览 / 在线视频 / 游戏）。不同分类的判定阈值不一样。</summary>
    public string Category { get; set; } = "";
    public string Host { get; set; } = "";
    public string Method { get; set; } = "";
    public double Min { get; set; }
    public double Avg { get; set; }
    public double Max { get; set; }
    public double Loss { get; set; }
    public List<double> Samples { get; set; } = new();
    public bool Ok => Samples.Count > 0;
}

public sealed class LatencyReport
{
    public LatencyTarget? Gateway { get; set; }
    public List<LatencyTarget> Targets { get; set; } = new();
    /// <summary>抖动 = 相邻样本差的平均绝对值，比 min/max 更能反映稳定性。</summary>
    public static double Jitter(IReadOnlyList<double> samples)
    {
        if (samples.Count < 2) return 0;
        double sum = 0;
        for (int i = 1; i < samples.Count; i++) sum += Math.Abs(samples[i] - samples[i - 1]);
        return sum / (samples.Count - 1);
    }
}

public sealed class DnsServerTest
{
    public string Server { get; set; } = "";
    public double Ms { get; set; }
    public int Answers { get; set; }
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0;
}

public sealed class DnsResolveTest
{
    public string Domain { get; set; } = "";
    public double Min { get; set; }
    public double Avg { get; set; }
    public double Max { get; set; }
    public string Address { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class StunObservation
{
    public string Server { get; set; } = "";
    public string MappedIp { get; set; } = "";
    public int MappedPort { get; set; }
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0 && MappedIp.Length > 0;
}

public sealed class NatReport
{
    public int LocalPort { get; set; }
    public List<StunObservation> Observations { get; set; } = new();
    public string Type { get; set; } = "未知";
    public string Detail { get; set; } = "";
    public List<string> MappedIps { get; set; } = new();
    public List<int> MappedPorts { get; set; } = new();
}

public sealed class BandwidthReport
{
    public string Source { get; set; } = "";
    public long Bytes { get; set; }
    public double Seconds { get; set; }
    public double Mbps => Seconds > 0 ? Bytes * 8 / Seconds / 1_000_000 : 0;
    public double MBps => Seconds > 0 ? Bytes / Seconds / 1_048_576 : 0;
    public bool FileExhausted { get; set; }
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0 && Bytes > 0;
}

/// <summary>路由追踪的单个跳。</summary>
public sealed class HopInfo
{
    public int Ttl { get; set; }
    public string Address { get; set; } = "";
    public double Ms { get; set; }
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Reached { get; set; }
    public bool Timeout => Address.Length == 0;
}

public sealed class TracerouteReport
{
    public string Target { get; set; } = "";
    /// <summary>目标解析出的 IP，与 Target 不同时才有值。</summary>
    public string TargetIp { get; set; } = "";
    public List<HopInfo> Hops { get; set; } = new();
    public bool Reached { get; set; }
    /// <summary>连续多跳无响应而提前结束（中间设备屏蔽了 ICMP）。</summary>
    public bool StoppedEarly { get; set; }
    public string Error { get; set; } = "";
    public bool Ok => Error.Length == 0 && Hops.Count > 0;
}

/// <summary>端口探测的单个结果。</summary>
public sealed class PortResult
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool Open { get; set; }
    public double Ms { get; set; }
    public string Banner { get; set; } = "";
    public string Service { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class PortScanReport
{
    public string Host { get; set; } = "";
    public List<PortResult> Results { get; set; } = new();
    public int OpenCount => Results.Count(r => r.Open);
    public int ClosedCount => Results.Count(r => !r.Open);
}

/// <summary>报告的一行。GUI 与文本导出共用同一份内容描述。</summary>
public enum LineKind { Header, Label, Value, Warn, Ok, Note }

public sealed record ReportLine(LineKind Kind, string Text, bool NewLine = true);

/// <summary>所有检测结果的累积容器。单独重跑某一项只覆盖对应字段。</summary>
public sealed class DiagState
{
    public string Timestamp { get; set; } = "";
    public string Hostname { get; set; } = "";

    public List<AdapterInfo>? Adapters { get; set; }
    public Dictionary<string, string>? Proxy { get; set; }
    public string? Gateway { get; set; }

    /// <summary>IPv6 检测结果。没有全局地址时也是非 null 的（里面是空的地址列表）。</summary>
    public Ipv6Report? Ipv6 { get; set; }

    /// <summary>HTTP / HTTPS 分级连通性检测结果。</summary>
    public WebReport? Web { get; set; }

    /// <summary>
    /// IP → 归属地，形如 <c>"203.0.113.77" → "示例 · 华东 电信"</c>。
    /// **放在状态里而不是查询模块里**，一是渲染层要按 IP 查表，
    /// 二是它会跟着诊断包一起导出 —— 收件人不用重新查一遍就能看懂每个地址在哪。
    /// 同时它也是「已经查过」的记录，重复调用 <see cref="IpGeo.FillAsync"/> 不会把请求量翻倍。
    /// </summary>
    public Dictionary<string, string> IpLocations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? PublicIpDirect { get; set; }
    public List<PublicIpResult>? PublicIpHttpProxy { get; set; }
    public List<PublicIpResult>? PublicIpHttpDirect { get; set; }

    public LatencyReport? Latency { get; set; }

    public List<string>? DnsServers { get; set; }
    public List<DnsServerTest>? DnsServerTests { get; set; }
    public List<DnsResolveTest>? DnsResolve { get; set; }
    public HijackReport? Hijack { get; set; }

    /// <summary>
    /// 公共 DNS 的对比测速（<see cref="DnsProbe.CompareServers"/>，域名统一用
    /// <see cref="DnsProbe.CompareTestDomain"/>）。
    ///
    /// 和 <see cref="DnsServerTests"/> **刻意分开存**：前者是「你正在用的」，
    /// 后者是「你可以换的」。混在一个列表里，界面就没法把这两件事分开讲，
    /// 用户也分不清哪台是自己配的、哪台只是拿来比一比的。
    /// </summary>
    public List<DnsServerTest>? DnsCompareTests { get; set; }

    public NatReport? Nat { get; set; }

    public BandwidthReport? BandwidthDirect { get; set; }
    public BandwidthReport? BandwidthProxy { get; set; }

    public TracerouteReport? Traceroute { get; set; }
    public PortScanReport? PortScan { get; set; }
    public MtuReport? Mtu { get; set; }

    /// <summary>跨路径收集到的全部公网 IP，用于判断是否存在多出口。</summary>
    public List<string> AllPublicIps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in new[] { PublicIpHttpProxy, PublicIpHttpDirect })
            if (list != null)
                foreach (var r in list)
                    if (r.Ok && r.Ip != "解析失败") set.Add(r.Ip);
        if (!string.IsNullOrWhiteSpace(PublicIpDirect) && PublicIpDirect != "不可用")
            set.Add(PublicIpDirect!);
        return set.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
