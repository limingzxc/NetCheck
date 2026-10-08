namespace NetCheck.Ui;

/// <summary>用于着色和状态判断的语义色调。</summary>
public enum Tone { Neutral, Good, Warn, Bad }

/// <summary>顶部指标卡片。</summary>
public sealed class MetricCard
{
    public string Title { get; set; } = "";
    public string Value { get; set; } = "—";
    public string Sub { get; set; } = "";
    public Tone Tone { get; set; } = Tone.Neutral;
    /// <summary>点击卡片跳转到的标签页索引，-1 表示不可跳转。</summary>
    public int TargetTab { get; set; } = -1;
    public bool HasData { get; set; }
}

/// <summary>延迟/响应的条形行。BarWidth 直接绑定到矩形宽度，避免写转换器。</summary>
public sealed class BarRow
{
    public const double TrackWidth = 150;

    public string Name { get; set; } = "";
    /// <summary>Name 是 IP 时填归属地，渲染成 Name 右边一段浅色小字。空串表示不显示。</summary>
    public string Geo { get; set; } = "";
    public string Detail { get; set; } = "";
    public string ValueText { get; set; } = "";
    public double BarWidth { get; set; }
    public Tone Tone { get; set; } = Tone.Neutral;

    /// <summary>
    /// 按全量程换算条宽。量程取最大值×1.1，让最长的那条几乎占满轨道。
    /// 当最大/最小超过 20 倍时改用平方根压缩 —— 否则 0.4 ms 的网关在 73 ms 的目标旁边
    /// 会被压成看不见的一条线。此时条长只表示量级，不再线性可比。
    /// 注意：不要在这里加绝对下限（例如 max(100ms, ...)），否则全是几毫秒的 DNS
    /// 会被压成一根根小牙签，失去对比意义。
    /// </summary>
    public static double Scale(double value, double maxValue, double minValue = 0)
    {
        var full = Math.Max(1.0, maxValue * 1.1);
        if (value <= 0) return 2;

        double ratio = Math.Min(1.0, value / full);
        bool wideRange = minValue > 0 && maxValue / minValue > 20;
        double shaped = wideRange ? Math.Sqrt(ratio) : ratio;

        return Math.Max(2, Math.Min(TrackWidth, shaped * TrackWidth));
    }

    /// <summary>该组数据是否因为量程跨度过大而启用了平方根压缩。</summary>
    public static bool UsesSqrtScale(double maxValue, double minValue)
        => minValue > 0 && maxValue / minValue > 20;
}

/// <summary>网卡列表行。</summary>
public sealed class AdapterRow
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string IPv4 { get; set; } = "";
    public string Mask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Dns { get; set; } = "";
    public string Speed { get; set; } = "";
    public string Dhcp { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsUp { get; set; }
    public Tone Tone { get; set; } = Tone.Neutral;
}

/// <summary>键值行，用于各种明细表。</summary>
public sealed class KeyValueRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Value 是 IP 时填归属地，渲染成 Value 右边一段浅色小字。空串表示不显示。</summary>
    public string Geo { get; set; } = "";
    public Tone Tone { get; set; } = Tone.Neutral;
    public string Note { get; set; } = "";
}

/// <summary>路由追踪的一跳。</summary>
public sealed class HopRow
{
    public string Ttl { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>这一跳的归属地，渲染在地址右边。</summary>
    public string Geo { get; set; } = "";
    public string MsText { get; set; } = "";
    public string Status { get; set; } = "";
    public Tone Tone { get; set; } = Tone.Neutral;
}

/// <summary>端口状态方块。</summary>
public sealed class PortCell
{
    public int Port { get; set; }
    public string Service { get; set; } = "";
    public bool Open { get; set; }
    public string MsText { get; set; } = "";
    public string Banner { get; set; } = "";
    public Tone Tone { get; set; } = Tone.Neutral;
    public string Tooltip { get; set; } = "";
}

/// <summary>概览页的问题条目。</summary>
public sealed class IssueRow
{
    public Tone Tone { get; set; } = Tone.Warn;
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public int TargetTab { get; set; } = -1;
    public string Level => Tone switch
    {
        Tone.Bad => "严重",
        Tone.Warn => "注意",
        Tone.Good => "正常",
        _ => "信息",
    };
}
