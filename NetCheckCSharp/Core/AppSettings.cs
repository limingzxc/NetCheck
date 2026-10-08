using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetCheck.Core;

/// <summary>
/// 用户可调的检测参数与界面状态。持久化到 %APPDATA%\NetCheck\settings.json。
/// 所有数值都经过 <see cref="Clamp"/> 收敛，避免手改配置文件后把程序卡死
/// （例如并发填 100000、超时填 0）。
/// </summary>
public sealed class AppSettings
{
    // ---- 检测目标 ----
    public string TargetHost { get; set; } = "223.5.5.5";

    /// <summary>
    /// 「网页目标」—— HTTP / HTTPS 检测额外要测的那个站点，可带协议与路径。
    /// **默认留空**：内置的国内两个 + 境外一个已经能回答「网页通不通」，
    /// 替用户预设一个别人的网站既没意义又像广告。留空时就不测这一项。
    /// </summary>
    public string WebTarget { get; set; } = "";

    /// <summary>默认端口表达式与默认预设名都取自 <see cref="PortPresets"/>，避免两处写死走偏。</summary>
    public string PortSpec { get; set; } = PortPresets.DefaultSpec;
    public string PortPreset { get; set; } = PortPresets.DefaultName;

    // ---- 端口检测 ----
    public int PortConcurrency { get; set; } = 64;
    public int PortTimeoutMs { get; set; } = 1500;

    // ---- 延迟探测 ----
    public int LatencyRounds { get; set; } = 4;

    // ---- 路由追踪 ----
    public int TraceMaxHops { get; set; } = 30;
    public int TraceTimeoutMs { get; set; } = 1200;

    // ---- 带宽测速 ----
    public int SpeedSeconds { get; set; } = 10;

    // ---- DNS ----
    public int DnsTimeoutMs { get; set; } = 2500;

    // ---- 界面 ----
    /// <summary>
    /// 被收起的检测项卡片。存的是卡片键（options / overview / local / link / dns / nat / path / port），
    /// 不是索引 —— 以后增删卡片时旧配置仍然认得出来，不会错位折叠到别的卡片上。
    /// </summary>
    public string[] CollapsedCards { get; set; } = Array.Empty<string>();

    public int WindowWidth { get; set; } = 1180;
    public int WindowHeight { get; set; } = 880;
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// 本次 <see cref="Load"/> 是不是「首次运行」（配置文件根本不存在）。
    /// 用来决定要不要写入默认折叠状态 —— 老用户的配置里没有高级组那几张卡片，
    /// 不能因为升级就替他把卡片收起来。只在文件不存在时为 true；
    /// 文件损坏走的是另一条分支（不设此标志），免得把一个坏文件当成首次运行。
    /// </summary>
    [JsonIgnore]
    public bool FirstRun { get; private set; }

    // ------------------------------------------------------------------

    /// <summary>注意别叫 Directory —— 会和 System.IO.Directory 打架。</summary>
    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetCheck");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>把越界值收敛回可用范围。反序列化后与保存前都要调用。</summary>
    public AppSettings Clamp()
    {
        TargetHost = (TargetHost ?? "").Trim();
        if (TargetHost.Length == 0) TargetHost = "223.5.5.5";

        // 网页目标允许为空（= 不测自定义目标）。填了但认不出来就清空 ——
        // 留着一段没法用的文本，只会让检测时静默少一项，比直接清掉更难发现。
        WebTarget = (WebTarget ?? "").Trim();
        if (WebTarget.Length > 0 && !WebProbe.TryParseTarget(WebTarget, out _, out _)) WebTarget = "";

        PortSpec = (PortSpec ?? "").Trim();
        if (PortSpec.Length == 0) PortSpec = PortPresets.DefaultSpec;

        PortPreset = string.IsNullOrWhiteSpace(PortPreset) ? PortPresets.DefaultName : PortPreset;

        PortConcurrency = Math.Clamp(PortConcurrency, 1, 512);
        PortTimeoutMs = Math.Clamp(PortTimeoutMs, 100, 15000);
        LatencyRounds = Math.Clamp(LatencyRounds, 1, 20);
        TraceMaxHops = Math.Clamp(TraceMaxHops, 1, 64);
        TraceTimeoutMs = Math.Clamp(TraceTimeoutMs, 200, 5000);
        SpeedSeconds = Math.Clamp(SpeedSeconds, 3, 60);
        DnsTimeoutMs = Math.Clamp(DnsTimeoutMs, 300, 10000);

        // 手改配置文件时容易写出 null 或重复项，这里收敛成去重后的非空数组
        CollapsedCards = (CollapsedCards ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        WindowWidth = Math.Clamp(WindowWidth, 940, 4000);
        // 下限必须和 MainWindow.xaml 的 MinHeight 一致（620）。
        // 以前这里是 640、XAML 是 620：手改配置成 630 会被静默拉到 640，
        // 而用户明明能手动把窗口拉到 620，两边对不上。
        WindowHeight = Math.Clamp(WindowHeight, 620, 3000);

        return this;
    }

    /// <summary>
    /// 读取配置。文件不存在或损坏时返回默认值，绝不抛异常。
    /// <paramref name="path"/> 只在测试里传，正常运行走默认位置。
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= SettingsPath;
            if (!File.Exists(path)) return new AppSettings { FirstRun = true };
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<AppSettings>(json);
            return (s ?? new AppSettings()).Clamp();
        }
        catch
        {
            // 配置坏了不应该让程序起不来，退回默认值即可。
            // 注意这里不设 FirstRun —— 文件是坏的，不是没有，不该趁机覆盖用户的折叠偏好。
            return new AppSettings();
        }
    }

    /// <summary>
    /// 保存配置。写失败（磁盘满、目录只读）只静默放弃，不打扰用户。
    /// <paramref name="path"/> 只在测试里传，正常运行走默认位置。
    /// </summary>
    public void Save(string? path = null)
    {
        try
        {
            Clamp();
            path ??= SettingsPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(path, json);
        }
        catch
        {
            // 配置存不下来不影响本次使用
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
