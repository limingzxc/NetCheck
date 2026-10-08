using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace NetCheck.Core;

/// <summary>
/// 本机网络配置。
/// 这是 C# 相比 Python 版本最明显的优势：NetworkInterface.GetAllNetworkInterfaces()
/// 直接给出网卡、IP、掩码、网关、DNS、MAC、DHCP 状态，
/// 完全不需要解析 ipconfig 文本，也就没有 GBK 解码和中英文标签匹配的坑。
/// </summary>
public static class LocalNetInfo
{
    public static string HostName
    {
        get
        {
            try { return Dns.GetHostName(); }
            catch { return Environment.MachineName; }
        }
    }

    public static List<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return list; }

        foreach (var nic in nics)
        {
            try
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var a = new AdapterInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Type = DescribeType(nic.NetworkInterfaceType),
                    Status = DescribeStatus(nic.OperationalStatus),
                    IsUp = nic.OperationalStatus == OperationalStatus.Up,
                    Mac = FormatMac(nic.GetPhysicalAddress()),
                    SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : 0,
                };

                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); }
                catch { list.Add(a); continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4.Length == 0)
                    {
                        a.IPv4 = ua.Address.ToString();
                        if (ua.IPv4Mask != null) a.Mask = ua.IPv4Mask.ToString();
                    }
                    else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6
                             && !ua.Address.IsIPv6LinkLocal)
                    {
                        // 一块网卡同时挂稳定地址和隐私扩展临时地址是常态，两个都要留着 ——
                        // IPv6 卡片要靠这个区分「固定地址」和「每天变一次的临时地址」。
                        var s6 = ua.Address.ToString();
                        if (!a.IPv6Addresses.Contains(s6)) a.IPv6Addresses.Add(s6);
                        if (a.IPv6.Length == 0) a.IPv6 = s6;
                    }
                }

                foreach (var gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork && a.Gateway.Length == 0)
                    {
                        a.Gateway = gw.Address.ToString();
                        break;
                    }
                }

                foreach (var dns in props.DnsAddresses)
                    a.Dns.Add(dns.ToString());
                a.Dns = a.Dns.Distinct().ToList();

                try
                {
                    // IsDhcpEnabled 在 IPv4 属性上，不在 IPInterfaceProperties 上
                    a.DhcpEnabled = props.GetIPv4Properties()?.IsDhcpEnabled ?? false;
                }
                catch { /* 部分虚拟网卡不支持 IPv4 属性查询 */ }

                // 只要「已启用」或者「有 IP」就展示，避免把禁用的虚拟网卡全列出来
                if (a.IsUp || a.IPv4.Length > 0 || a.Gateway.Length > 0)
                    list.Add(a);
            }
            catch { /* 单个网卡读取失败不影响其它 */ }
        }

        return list
            .OrderByDescending(x => x.Gateway.Length > 0)
            .ThenByDescending(x => x.IsUp)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    public static string? PrimaryGateway(IEnumerable<AdapterInfo> adapters)
        => adapters.FirstOrDefault(a => a.IsUp && a.Gateway.Length > 0)?.Gateway
           ?? adapters.FirstOrDefault(a => a.Gateway.Length > 0)?.Gateway;

    public static List<string> AllDnsServers(IEnumerable<AdapterInfo> adapters)
    {
        var seen = new List<string>();
        foreach (var a in adapters.Where(x => x.IsUp || x.IPv4.Length > 0))
            foreach (var d in a.Dns)
                if (!seen.Contains(d)) seen.Add(d);
        return seen;
    }

    /// <summary>环境变量 + 系统代理设置（注册表）。</summary>
    public static Dictionary<string, string> GetProxySettings()
    {
        var d = new Dictionary<string, string>();
        foreach (var k in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY", "all_proxy", "ALL_PROXY" })
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrWhiteSpace(v)) d[k] = v!;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key != null)
            {
                var enable = key.GetValue("ProxyEnable");
                var server = key.GetValue("ProxyServer") as string;
                var pac = key.GetValue("AutoConfigURL") as string;

                if (enable is int e && e != 0 && !string.IsNullOrWhiteSpace(server))
                    d["系统代理(已启用)"] = server!;
                if (!string.IsNullOrWhiteSpace(pac))
                    d["自动配置脚本(PAC)"] = pac!;
            }
        }
        catch { /* 注册表不可读时忽略 */ }

        return d;
    }

    private static string FormatMac(PhysicalAddress pa)
    {
        var bytes = pa.GetAddressBytes();
        if (bytes.Length == 0) return "";
        return string.Join("-", bytes.Select(b => b.ToString("X2")));
    }

    private static string DescribeType(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "有线",
        NetworkInterfaceType.Wireless80211 => "无线",
        NetworkInterfaceType.Ppp => "拨号/VPN",
        NetworkInterfaceType.Tunnel => "隧道",
        NetworkInterfaceType.Loopback => "回环",
        _ => t.ToString()
    };

    private static string DescribeStatus(OperationalStatus s) => s switch
    {
        OperationalStatus.Up => "已连接",
        OperationalStatus.Down => "已断开",
        OperationalStatus.Dormant => "休眠",
        OperationalStatus.NotPresent => "不存在",
        OperationalStatus.LowerLayerDown => "下层断开",
        _ => s.ToString()
    };
}
