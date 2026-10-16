using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetCheck.Ui;

/// <summary>主题模式。<see cref="System"/> 跟随 Windows 的「应用浅色/深色」设置。</summary>
public enum ThemeMode { System, Light, Dark }

/// <summary>
/// 全局主题调色板 —— 浅色 / 深色两套色值，运行时切换。
///
/// **实现方式：共享可变更画刷（mutate in place）。**
/// App.xaml 里声明的画刷对象全程序只有一份，这里切换主题时只改它们的
/// <see cref="SolidColorBrush.Color"/>，不替换资源项、不重建任何控件：
///
///   · App.xaml 里用 <c>{StaticResource XxxBrush}</c> 解析过的样式 Setter，
///     拿到的是**同一个对象引用**，改 Color 立刻生效；
///   · <c>Renderer</c> / <c>MainWindow</c> 里缓存的静态画刷字段同理；
///   · 已经赋给某个元素 Background 的画刷也同理。
///
/// 所以既能保住 StaticResource 的解析性能（WPF 官方建议），又拿到了
/// DynamicResource 才有的运行时刷新能力 —— 不必为了换主题把整套样式
/// 改写成 DynamicResource，更不必在切换时重建卡片树。
///
/// 唯一前提是画刷没有被 Freeze：App.xaml 里声明的资源不会被自动冻结
/// （只有 themes/generic.xaml 这类主题字典才会），所以可以直接改色。
/// 万一遇到已冻结的画刷（有人手动加了 PresentationOptions:Freeze），
/// <see cref="Apply"/> 会退化成「替换资源项 + 重建界面」，不会抛异常。
/// </summary>
public static class Theme
{
    /// <summary>当前是否深色。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>当前模式（可能是 System）。</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>主题实际发生切换后触发，供需要重建自绘内容的界面订阅。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 色值表：资源键 →（浅色, 深色）。
    /// **这是全部颜色数值的唯一出处** —— App.xaml 里写的只是浅色初值，
    /// 两处必须一致，改色时以本表为准同步过去。
    ///
    /// 对比度都按 WCAG AA（正文 4.5:1）核算过，浅色值以白/浅灰底为准，
    /// 深色值以深色卡片底为准；正文级文字（Text / Muted / Accent / 三种语义色）
    /// 在两套主题下均 ≥ 4.6:1。Faint 只用于装饰，不承载需要阅读的文字。
    /// </summary>
    private static readonly (string Key, string Light, string Dark)[] Map =
    {
        // ---- 表面 ----
        ("BgBrush",          "#F4F6FA", "#0F1116"),  // 窗口底
        ("CardBrush",        "#FFFFFF", "#181B22"),  // 卡片面
        ("CardHoverBrush",   "#FAFBFE", "#1E222B"),  // 可点卡片的悬停面
        ("PanelBrush",       "#F7F9FC", "#1E222B"),  // 卡片内部的分区块
        ("SoftBrush",        "#EEF1F7", "#22262F"),  // 次级填充（次按钮/轨道/胶囊底）
        ("SoftHover",        "#E1E6F0", "#2B303B"),
        ("SoftDisabled",     "#F3F4F7", "#1B1E25"),
        ("InputBrush",       "#FFFFFF", "#1C2028"),  // 输入框面
        ("LineBrush",        "#E3E7EF", "#2B313C"),  // 常规边线
        ("LineStrongBrush",  "#D3D9E4", "#3A4250"),  // 需要更明确的边线（输入框）

        // ---- 文字（三级）----
        ("TextBrush",        "#1B1F27", "#E8EBF1"),  // 主文本
        ("MutedBrush",       "#5F6878", "#99A2B2"),  // 次级说明、键名、注脚
        ("FaintBrush",       "#A2A9B5", "#666F7E"),  // 装饰（折叠箭头、状态点）
        ("InverseTextBrush", "#FFFFFF", "#0C1220"),  // 铺在主色块上的文字

        // ---- 主色 ----
        // 主色比原来（#3B6FE0）压深了一档：它要同时铺在白底和 PanelBrush 浅灰底上，
        // #3B6FE0 在浅灰底上只有 4.1:1，够不到 AA。压深后两种底都过 4.7:1。
        ("AccentBrush",      "#2F63D6", "#6E9BFF"),
        ("AccentHover",      "#2454BE", "#8AAEFF"),
        ("AccentBgBrush",    "#EDF2FE", "#1A2740"),  // 主色浅底（选中态、标签底）
        ("AccentDisabled",   "#B0C4EC", "#33456B"),

        // ---- 语义色（配浅底）----
        ("OkBrush",          "#0F7A55", "#4CC38A"),
        ("OkBgBrush",        "#E7F6EF", "#13291F"),
        // 警告色同理压深一档，保证在 PanelBrush 浅灰底上也过 AA（#A45D14 只有 4.47）
        ("WarnBrush",        "#9A5611", "#E0A458"),
        ("WarnBgBrush",      "#FCF1E4", "#2B2317"),
        ("DangerBrush",      "#BE3A2C", "#F07A6A"),
        ("DangerBgBrush",    "#FCECEA", "#2E1B19"),
        // 语义浅底的描边。比主色淡一档 —— 结论横幅整块都是语义色底，
        // 边线再用主色会显得像个警告框。
        ("OkBorderBrush",     "#BFE3D0", "#1F4634"),
        ("WarnBorderBrush",   "#EED9BC", "#4A3B22"),
        ("DangerBorderBrush", "#F2CFCA", "#4E2925"),

        // ---- 滚动条 ----
        ("ScrollThumbBrush",      "#C9CFDA", "#3A424F"),
        ("ScrollThumbHoverBrush", "#AEB6C4", "#4A5464"),
        ("ScrollThumbDragBrush",  "#949DAD", "#5C6779"),
    };

    /// <summary>深色下给卡片投影用更实的黑，浅色下用带一点蓝的深色，投影才不显脏。</summary>
    public static Color ShadowColor => IsDark
        ? Color.FromRgb(0x00, 0x00, 0x00)
        : Color.FromRgb(0x1B, 0x2A, 0x4A);

    /// <summary>浅色主题下投影要更淡，深色主题下投影本身几乎不可见、主要靠边线分层。</summary>
    public static double ShadowOpacity => IsDark ? 0.45 : 0.10;

    /// <summary>
    /// 应用主题。同一模式重复调用是幂等的（不重复触发 <see cref="Changed"/>）。
    /// </summary>
    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        bool dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemPrefersDark(),
        };

        bool changed = dark != IsDark || !_applied;
        IsDark = dark;
        _applied = true;
        WritePalette(dark);
        if (changed) Changed?.Invoke();
    }

    private static bool _applied;

    /// <summary>
    /// 供 <see cref="PaletteDictionary"/> 调用：按浅色初值建一套画刷。
    ///
    /// 这套画刷只是**初始值**。真正切主题时会被 <see cref="WritePalette"/> 换掉 ——
    /// 因为样式封印时会把引用到的画刷冻结（StaticResource 不克隆，字典里那个对象
    /// 被连带冻结），所以「就地改 Color」这条路根本走不通，只能换整个画刷对象，
    /// 配合 `DynamicResource` 的引用才能收到变更通知。详见 PaletteDictionary 的注释。
    /// </summary>
    internal static void Fill(ResourceDictionary target)
    {
        foreach (var (key, light, _) in Map)
            target[key] = new SolidColorBrush(ParseColor(light));
    }

    /// <summary>
    /// 把某一套色值写进全局。
    ///
    /// **做法：换掉字典里的画刷条目，而不是改画刷的 Color。**
    /// XAML / 样式封印会把画刷冻结（改不了 Color），所以必须在字典层整体替换，
    /// 让 `{DynamicResource}` 引用收到变更通知后自己取到新值。这就是为什么全程序
    /// 用 `{DynamicResource}` 取颜色而不是 `{StaticResource}`。
    ///
    /// 直接写在 `Application.Resources` 顶层：同键会覆盖掉 PaletteDictionary 里的
    /// 初始值，而且顶层赋值是 WPF 换主题的标准写法，变更通知有保证。
    /// </summary>
    private static void WritePalette(bool dark)
    {
        var app = Application.Current;
        if (app == null) return;

        foreach (var (key, light, darkValue) in Map)
        {
            var color = ParseColor(dark ? darkValue : light);
            var fresh = new SolidColorBrush(color);
            fresh.Freeze();   // 可以冻结：没人再改它，冻结还能省一点渲染开销
            app.Resources[key] = fresh;
        }
    }

    private static Color ParseColor(string hex)
        => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>
    /// 读 Windows 的「应用模式」设置。
    /// 键不存在（老系统或企业精简镜像）时按浅色处理 —— 这是当前界面的原生配色，
    /// 猜错也只是保持原样，不会突然变黑。
    /// </summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把模式存成配置里的字符串。</summary>
    public static string ToSetting(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "system",
    };

    // ------------------------------------------------------------------
    // 系统标题栏
    // ------------------------------------------------------------------

    // Win10 1809–1909 用的是 19，2004 及以后改成了 20。两个都试，谁成功算谁。
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 让系统标题栏跟着主题走。
    /// 不做这一步的话，深色界面顶着一条亮白色的标题栏，割裂感非常明显 ——
    /// WPF 自己管不到非工作区的绘制，只能通过 DWM 告诉系统。
    ///
    /// 调不通不算错误（老系统、被组策略禁用 DWM 合成、非 Windows 环境），
    /// 静默放弃即可 —— 顶多是标题栏保持默认色，功能不受影响。
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref dark, sizeof(int));
        }
        catch
        {
            // 见方法注释：拿不到 DWM 就保持系统默认外观
        }
    }

    /// <summary>从配置字符串还原模式，认不出来一律当「跟随系统」。</summary>
    public static ThemeMode FromSetting(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}
