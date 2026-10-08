namespace NetCheck.Core;

/// <summary>一组预设端口。Spec 为空表示「自定义」，选中时不覆盖用户已填的内容。</summary>
public sealed record PortPreset(string Name, string Spec, string Hint);

/// <summary>
/// 端口扫描预设。端口表达式沿用 <see cref="PortCheck.ParsePorts"/> 的语法：
/// 逗号/空格/分号分隔，支持 8000-8010 这样的范围。
/// </summary>
public static class PortPresets
{
    public const string CustomName = "自定义";

    /// <summary>
    /// 新装用户的默认端口表达式。**必须和默认目标主机配套** ——
    /// 默认目标是 223.5.5.5（公共 DNS），它上面 53 / 80 / 443 是开放的，
    /// 22 / 3389 / 8080 一定关闭。以前默认给「常用服务」那串（含 3389/8080），
    /// 用户第一次点端口检测就看到两个「关闭」，会以为工具坏了或者自己网络有问题。
    /// </summary>
    public const string DefaultSpec = "53,80,443";

    /// <summary>默认端口预设的名字，与 <see cref="DefaultSpec"/> 对应。</summary>
    public const string DefaultName = "默认目标（公共 DNS）";

    public static readonly PortPreset[] All =
    {
        new(DefaultName, DefaultSpec,
            "与默认目标 223.5.5.5 配套：DNS / HTTP / HTTPS，这三个确实开放"),

        new("常用服务", "22,80,443,3389,8080",
            "扫自己的服务器时最常检查的几个端口"),

        new("Web 服务", "80,443,8000,8008,8080,8081,8443,8888,9000,9090",
            "HTTP/HTTPS 及常见反向代理、面板端口"),

        new("数据库", "1433,1521,3306,5432,6379,9200,11211,27017",
            "SQL Server / Oracle / MySQL / PostgreSQL / Redis / ES / Memcached / MongoDB"),

        new("远程管理", "22,23,3389,5900,5985,5986",
            "SSH / Telnet / RDP / VNC / WinRM"),

        new("邮件与文件共享", "21,22,25,110,143,445,993,995",
            "FTP / SMTP / POP3 / IMAP / SMB"),

        new("公网危险暴露", "21,23,445,1433,3306,3389,6379,9200,11211,27017",
            "这些端口直接暴露在公网风险很高，值得重点确认"),

        new("常见高危 20 个", "21,22,23,25,53,80,110,135,139,143,443,445,1433,1521,3306,3389,5432,5900,6379,27017",
            "渗透测试常用的 TOP 端口"),

        new("1-1024 全量", "1-1024",
            "系统端口全扫，耗时较长（并发与超时可在下方调整）"),

        new(CustomName, "",
            "手动填写端口表达式，例如 22,80,443,8000-8010"),
    };

    /// <summary>按名字找预设；找不到返回「自定义」。</summary>
    public static PortPreset ByName(string? name)
        => All.FirstOrDefault(p => p.Name == name) ?? All[^1];

    /// <summary>预设名列表，供下拉框绑定。</summary>
    public static string[] Names => All.Select(p => p.Name).ToArray();

    /// <summary>
    /// 反查某个端口表达式属于哪个预设，没有匹配的返回「自定义」。
    /// 用于启动时把配置文件里的 Spec 还原成下拉框选中项。
    /// </summary>
    public static string MatchName(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return CustomName;
        var norm = Normalize(spec);
        foreach (var p in All)
        {
            if (p.Spec.Length == 0) continue;
            if (Normalize(p.Spec) == norm) return p.Name;
        }
        return CustomName;
    }

    /// <summary>把 "80, 443" 和 "443,80" 视为同一个集合，便于比对。</summary>
    private static string Normalize(string spec)
    {
        var (ports, err) = PortCheck.ParsePorts(spec);
        return err.Length > 0 ? spec.Trim() : string.Join(",", ports);
    }
}
