using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using NetCheck.Core;
using NetCheck.Ui;

namespace NetCheck;

public partial class MainWindow : Window
{
    // ==================================================================
    // 检测项定义
    // ==================================================================

    // 卡片的静态定义（键 / 标签页索引 / 标题 / 说明 / 按钮）都在 Ui/CardCatalog.cs 里。
    // 放那边是为了让测试工程能引用 —— 「卡片键 -> 标签页索引」这条映射写错了卡片会静默变空，
    // 必须有断言守着，而 MainWindow 依赖 WPF，测试工程引不进来。
    private static readonly SectionDef[] SectionDefs = CardCatalog.Sections;

    private const string OptionsKey = CardCatalog.OptionsKey;

    /// <summary>卡片上的一个按钮，记着它属于哪个任务、原始文案是什么（运行时要临时改成「检测中…」）。</summary>
    private sealed class CardButton
    {
        public Button Btn = null!;
        public string TaskKey = "";
        public string Label = "";
    }

    /// <summary>
    /// 一个检测项在界面上的全部可动部分。
    ///
    /// v2.7 起不再有「卡片外壳」这种东西：左侧导航栏里它是一项
    /// （<see cref="NavItem"/> + 状态点 + 问题角标），右侧详情面板里是它的内容
    /// （<see cref="BodyHost"/>）。运行按钮仍然只有一个入口 ——
    /// 挂在详情面板头部（<see cref="ActionBar"/>），和它要填充的内容在一起。
    /// </summary>
    private sealed class SectionCard
    {
        public string Key = "";
        public int TabIndex = -1;

        // ---- 导航栏 ----
        public RadioButton NavItem = null!;
        /// <summary>状态点：绿=正常、橙=有提醒、红=有严重问题、灰=还没数据。</summary>
        public Border NavDot = null!;
        public Border BadgeBox = null!;
        public TextBlock BadgeText = null!;

        // ---- 详情面板 ----
        public StackPanel BodyHost = null!;
        /// <summary>这个检测项的运行按钮组。切换检测项时挂到 DetailActions 上，切走时摘下。</summary>
        public StackPanel ActionBar = null!;

        public readonly List<CardButton> Buttons = new();
    }

    // ==================================================================
    // 状态
    // ==================================================================

    private DiagState _state = new();
    private readonly AppSettings _settings;
    private readonly CommandLineOptions _cli;

    private CancellationTokenSource? _cts;
    private bool _running;
    /// <summary>当前这次是静默任务（启动时读本机配置）。关窗时不该为它弹确认框。</summary>
    private bool _quietRun;
    /// <summary>往界面回填配置时置位，抑制输入框的变更事件，避免自我覆盖。</summary>
    private bool _loadingOptions;
    /// <summary>正在跑的任务键，用来把对应按钮改成「检测中…」。</summary>
    private string? _activeTaskKey;
    /// <summary>有任何耗时操作在跑（检测或诊断包导出）。用来挡住并发触发 —— 光靠 _running 挡不住诊断包导出。</summary>
    private bool _busy;

    /// <summary>最近一次渲染出来的视图模型。只有当前选中的那一项会重建内容。</summary>
    private ReportView? _view;

    private readonly List<SectionCard> _cards = new();
    private readonly Dictionary<string, SectionCard> _cardByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前选中的检测项键。导航栏里同一时刻只有一项 IsChecked。</summary>
    private string _selectedKey = "overview";

    private readonly Dictionary<string, (string Label, Func<IProgress<string>, DiagState, CancellationToken, Task> Run)> _tasks;

    public MainWindow()
    {
        // 复用 App 启动时读进来的那一份配置与命令行参数（见 App.Settings 的注释）。
        // 只有脱离 App 单独构造窗口的场景（设计器预览、离屏渲染）才自己读一次。
        bool standalone = Application.Current is not App;
        _settings = standalone ? AppSettings.Load() : App.Settings;
        _cli = standalone
            ? CommandLineOptions.Parse(Environment.GetCommandLineArgs().Skip(1))
            : App.Cli;
        if (standalone) _cli.ApplyTo(_settings);

        InitializeComponent();

        // 主题变化时，自绘的阴影颜色不会自动跟着走（DropShadowEffect 的 Color
        // 是普通属性，不是画刷），这里订阅一次，把已有卡片的外壳阴影补上。
        Theme.Changed += OnThemeChanged;

        _tasks = new Dictionary<string, (string, Func<IProgress<string>, DiagState, CancellationToken, Task>)>
        {
            ["local"] = ("本机网络配置", TaskLocalAsync),
            ["quick"] = ("一键检测", TaskQuickAsync),
            ["ipv6"] = ("IPv6 检测", TaskIpv6Async),
            ["web"] = ("网页连通性检测", TaskWebAsync),
            ["egress"] = ("出口 IP 检测", TaskEgressAsync),
            ["latency"] = ("延迟检测", TaskLatencyAsync),
            ["nat"] = ("NAT 类型检测", TaskNatAsync),
            ["dns"] = ("DNS 检测", TaskDnsAsync),
            ["speed"] = ("带宽测速", TaskSpeedAsync),
            ["trace"] = ("路由追踪", TaskTraceAsync),
            ["port"] = ("端口检测", TaskPortAsync),
            ["mtu"] = ("MTU 探测", TaskMtuAsync),
        };

        CmbPreset.ItemsSource = PortPresets.Names;
        BuildNav();
        ApplySettingsToUi();
        RestoreWindowGeometry();
        SyncThemeUi();

        Loaded += OnLoaded;
    }

    // ==================================================================
    // 导航栏构建
    // ==================================================================

    /// <summary>
    /// 生成左侧导航栏。层次和原来的卡片分组完全一致（常用 / 高级 → 网络路径 · 连接能力 · 协议），
    /// 只是从「一行行分组标题 + 卡片」换成了「一列带缩进的导航项」。
    /// </summary>
    private void BuildNav()
    {
        // 检测选项的输入控件声明在 XAML 里（保留 x:Name），这里先摘出来，
        // 选中「检测选项」时再挂回详情面板。
        DetailHost.Children.Remove(OptionsFields);
        OptionsFields.Visibility = Visibility.Visible;

        foreach (var (groupKey, groupTitle) in CardCatalog.Groups)
        {
            // DetectionCardsInGroup 而不是 CardsInGroup：把「检测选项」排掉，
            // 它单独归到下面的「配置」里，否则会在导航栏出现两次。
            var defs = CardCatalog.DetectionCardsInGroup(groupKey);
            if (defs.Length == 0) continue;

            NavHost.Children.Add(NavLabel(groupTitle));

            // 不属于任何二级分组的项直接排在组标签下面（常用组全部走这一支）
            foreach (var def in defs.Where(d => d.SubGroup.Length == 0))
                NavHost.Children.Add(BuildNavItem(def, indent: 0));

            foreach (var sub in CardCatalog.SubGroupsOf(groupKey))
            {
                NavHost.Children.Add(NavLabel(sub.Title, indent: 22));
                foreach (var def in CardCatalog.CardsInSubGroup(groupKey, sub.Key))
                    NavHost.Children.Add(BuildNavItem(def, indent: 10));
            }
        }

        // 「检测选项」不是检测项，单独归到「配置」下 —— 它是改参数的地方，
        // 混在检测项里会让人以为它也是一种「检查」。
        NavHost.Children.Add(NavLabel("配置"));
        NavHost.Children.Add(BuildNavItem(CardCatalog.OptionsCard, indent: 0));

        // 恢复上次停留的检测项。配置里那个键认不出来（手改错了、或那张卡片被删了）
        // 就退回概览，并且**把这个回退写回配置** —— 让配置自愈，
        // 否则每次启动都要重新判断一次，而且用户永远不知道自己的配置已经失效了。
        string want = _settings.SelectedCard;
        if (!_cardByKey.ContainsKey(want))
        {
            want = "overview";
            _settings.SelectedCard = want;
            _settings.Save();
        }
        SelectCard(want, persist: false);
    }

    private TextBlock NavLabel(string text, double indent = 0)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("MutedBrush"),
            Margin = new Thickness(17 + indent, 18, 12, 6),
        };
        return t;
    }

    private UIElement BuildNavItem(SectionDef def, double indent)
    {
        var card = new SectionCard { Key = def.Key, TabIndex = def.TabIndex };

        card.NavDot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Res("FaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        // 刻意不设 Foreground：让它从 RadioButton 继承，选中时才会跟着模板触发器变成主色。
        var label = new TextBlock
        {
            Text = def.Title,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        card.BadgeBox = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        card.BadgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        };
        card.BadgeBox.Child = card.BadgeText;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(card.NavDot, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(card.BadgeBox, 2);
        grid.Children.Add(card.NavDot);
        grid.Children.Add(label);
        grid.Children.Add(card.BadgeBox);

        card.NavItem = new RadioButton
        {
            Style = (Style)FindResource("NavItem"),
            Content = grid,
            GroupName = "NetCheckNav",
            Margin = new Thickness(indent, 0, 0, 0),
            ToolTip = def.Hint,
        };
        System.Windows.Automation.AutomationProperties.SetName(card.NavItem, def.Title);
        card.NavItem.Checked += (_, _) => SelectCard(card.Key);

        // ---- 详情面板的容器与运行按钮 ----
        card.BodyHost = new StackPanel();

        card.ActionBar = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < def.Actions.Length; i++)
        {
            var a = def.Actions[i];
            // 一张卡片有两个以上运行按钮时，只有第一个用主按钮，其余用次级按钮。
            // 全用主按钮的话，路径页「路由追踪 + MTU 探测」两个并排的实心蓝块
            // 会互相抢注意力，而且看不出哪个是更常用的那个。
            var btn = new Button
            {
                Content = a.Label,
                Style = (Style)FindResource(i == 0 ? "PrimaryButton" : "SoftButton"),
                FontSize = 12.5,
                Padding = new Thickness(16, 7, 16, 7),
                // 样式自带 8px 右外边距，并排两个按钮时正好当间距；
                // 最后一个再抹掉，否则整组右侧多出一截空白
                Margin = i == def.Actions.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 8, 0),
                ToolTip = a.Tip,
            };
            var cb = new CardButton { Btn = btn, TaskKey = a.TaskKey, Label = a.Label };
            btn.Click += async (_, _) => await RunTaskAsync(cb.TaskKey);
            card.Buttons.Add(cb);
            card.ActionBar.Children.Add(btn);
        }

        _cards.Add(card);
        _cardByKey[card.Key] = card;
        return card.NavItem;
    }

    // ==================================================================
    // 选中与详情
    // ==================================================================

    /// <summary>防止「设置 IsChecked → 触发 Checked → 又调回这里」的递归。</summary>
    private bool _suppressNav;

    /// <summary>
    /// 切换到某个检测项。导航栏点击、菜单、命令行 <c>--tab</c>、
    /// 以及问题条目跳转，全都走这一个入口。
    /// </summary>
    private void SelectCard(string cardKey, bool persist = true)
    {
        if (_suppressNav) return;
        if (!_cardByKey.TryGetValue(cardKey, out var card)) return;

        _selectedKey = card.Key;

        // 让导航栏的选中态跟上。赋 IsChecked 会再触发一次 Checked 事件，
        // 用 _suppressNav 挡掉那一层 —— 否则会无限递归下去。
        // （RadioButton.IsChecked 是 bool?，所以和 true 比而不是直接当条件用。）
        if (card.NavItem.IsChecked != true)
        {
            _suppressNav = true;
            try { card.NavItem.IsChecked = true; }
            finally { _suppressNav = false; }
        }

        // 详情面板头部：标题、说明、运行按钮
        var def = CardCatalog.CardForKey(card.Key);
        DetailTitle.Text = def?.Title ?? card.Key;
        // 选项页的副标题是当前配置摘要（「目标 223.5.5.5 · 常用服务（5 个端口）· 并发 64」），
        // 其余项用目录里那句固定说明。
        DetailHint.Text = card.Key == OptionsKey ? _optionsHint : (def?.Hint ?? "");

        // 按钮对象只建一次（SetBusy 要改它们的文案），这里只是把对应那一组挂上去。
        // 挂之前先从原来的父节点摘掉 —— 同一个元素不能有两个父节点。
        DetailActions.Children.Clear();
        if (card.ActionBar.Children.Count > 0) DetailActions.Children.Add(card.ActionBar);

        FillDetail(card);

        if (persist)
        {
            _settings.SelectedCard = card.Key;
            _settings.Save();
        }

        // 换了一项就回到顶部，否则从上一项滚到一半的位置接着看，会以为内容缺了
        DetailScroll.ScrollToTop();
    }

    /// <summary>
    /// 把某一项的展示区挂进详情面板。
    /// **只有当前选中这一项的内容会被重建**（见 <see cref="Render"/>）——
    /// 扫 1024 个端口时，十个检测项的内容全建一遍会白白构造上千个元素。
    /// </summary>
    private void FillDetail(SectionCard card)
    {
        DetailHost.Children.Clear();

        if (card.Key == OptionsKey)
        {
            DetailHost.Children.Add(OptionsFields);
            return;
        }

        DetailHost.Children.Add(card.BodyHost);

        if (_view != null)
        {
            var def = CardCatalog.CardForKey(card.Key);
            if (def != null) FillCardBody(card, def, _view);
        }
    }

    /// <summary>
    /// 导航项左侧的状态点。比图标更有用：一眼扫下来就知道哪几项有问题。
    /// 检测选项不是检测项，没有状态，永远中性灰。
    /// </summary>
    private void UpdateNavDots(ReportView v)
    {
        foreach (var card in _cards)
        {
            var def = CardCatalog.CardForKey(card.Key);
            if (def == null) continue;

            card.NavDot.Background = NavTone(def, v) switch
            {
                Tone.Bad => Res("DangerBrush"),
                Tone.Warn => Res("WarnBrush"),
                Tone.Good => Res("OkBrush"),
                _ => Res("FaintBrush"),
            };
        }
    }

    /// <summary>
    /// 一项的总体状态。没有数据是中性（还没测，不是「正常」—— 报绿等于撒谎）；
    /// 有数据且没问题才是绿。概览项汇总全部问题，其余项只看指向自己的那些。
    /// </summary>
    private static Tone NavTone(SectionDef def, ReportView v)
    {
        if (def.TabIndex < 0 || def.TabIndex >= v.Tabs.Count) return Tone.Neutral;
        if (!v.Tabs[def.TabIndex].HasData) return Tone.Neutral;

        var mine = def.TabIndex == ReportViewBuilder.OverviewIndex
            ? v.Issues
            : v.Issues.Where(i => i.TargetTab == def.TabIndex).ToList();

        if (mine.Any(i => i.Tone == Tone.Bad)) return Tone.Bad;
        if (mine.Any(i => i.Tone == Tone.Warn)) return Tone.Warn;
        return Tone.Good;
    }


    private static TextBlock MakeText(string s, double size, Brush brush, FontWeight? weight = null)
        => new()
        {
            Text = s,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            VerticalAlignment = VerticalAlignment.Center,
        };

    private Brush Res(string key) => (Brush)FindResource(key);

    // ==================================================================
    // 菜单
    // ==================================================================

    private void MenuShowOptions_Click(object sender, RoutedEventArgs e) => SelectCard(OptionsKey);

    private void MenuNav_Click(object sender, RoutedEventArgs e)
    {
        // 菜单项的 Tag 一律写卡片键名，不写数字 —— 数字会随卡片顺序漂移
        if (sender is MenuItem mi && mi.Tag is string s) SelectCard(s);
    }

    // ==================================================================
    // 主题
    // ==================================================================

    /// <summary>
    /// 应用栏上的太阳 / 月亮按钮：在浅色与深色之间直接对调。
    /// 它不写「跟随系统」—— 那个状态只能从「视图」菜单进（见 <see cref="MenuTheme_Click"/>），
    /// 因为一个两态按钮表达不了三态，硬塞会让「跟随系统」这个选项悄悄消失。
    /// </summary>
    private void BtnTheme_Click(object sender, RoutedEventArgs e)
        => ApplyTheme(Theme.IsDark ? ThemeMode.Light : ThemeMode.Dark, persist: true);

    /// <summary>「视图」菜单里的主题三选一，Tag 就是配置文件里存的字符串。</summary>
    private void MenuTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string s)
            ApplyTheme(Theme.FromSetting(s), persist: true);
    }

    /// <summary>「视图 → 切换浅色 / 深色」，跟 Ctrl+T、标题栏按钮是同一件事。</summary>
    private void MenuToggleTheme_Click(object sender, RoutedEventArgs e)
        => ApplyTheme(Theme.IsDark ? ThemeMode.Light : ThemeMode.Dark, persist: true);

    private void ApplyTheme(ThemeMode mode, bool persist)
    {
        Theme.Apply(mode);
        _settings.Theme = Theme.ToSetting(mode);
        if (persist) _settings.Save();
        SyncThemeUi();
    }

    /// <summary>把当前主题状态同步到界面：菜单勾选 + 明暗图标。</summary>
    private void SyncThemeUi()
    {
        IconSun.Visibility = Theme.IsDark ? Visibility.Collapsed : Visibility.Visible;
        IconMoon.Visibility = Theme.IsDark ? Visibility.Visible : Visibility.Collapsed;
        BtnTheme.ToolTip = Theme.IsDark ? "切换到浅色主题" : "切换到深色主题";

        // 三个主题项是互斥的单选，MenuItem 自己不会互斥，得手工清一遍
        var items = new[] { MenuThemeSystem, MenuThemeLight, MenuThemeDark };
        string current = Theme.ToSetting(Theme.Mode);
        foreach (var item in items)
            item.IsChecked = Equals(item.Tag, current);
    }

    /// <summary>
    /// 主题变了。XAML 声明的界面（标题栏、工具栏、状态栏、导航栏容器）靠
    /// `{DynamicResource}` 自己跟着换；代码在运行时临时建出来的东西要手工补一遍 —
    /// 导航项的状态点/角标，以及当前详情面板里的内容（它们拿的是取色时的快照，
    /// 不重建就停在旧配色上）。
    /// </summary>
    private void OnThemeChanged()
    {
        // 系统标题栏是 DWM 画的，改不了颜色只能重新告诉它一次
        Theme.ApplyTitleBar(this);

        if (_view != null)
        {
            // 导航栏的状态点和角标
            foreach (var def in SectionDefs)
                if (_cardByKey.TryGetValue(def.Key, out var card)) UpdateBadge(card, def, _view);
            UpdateNavDots(_view);

            // 详情面板内容（指标块、结论横幅、表格…全部重建一次）
            if (_cardByKey.TryGetValue(_selectedKey, out var selected))
            {
                var def = CardCatalog.CardForKey(selected.Key);
                if (def != null) FillCardBody(selected, def, _view);
            }
        }

        SyncThemeUi();
    }

    /// <summary>
    /// 窗口句柄就绪后才能设置系统标题栏的明暗 —— WindowInteropHelper.Handle 在
    /// 构造函数里还是 IntPtr.Zero，那时调用等于什么也没做。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Theme.ApplyTitleBar(this);
    }

    private void UpdateStatusDot(bool busy)
        => StatusDot.Background = busy ? Res("AccentBrush") : Res("FaintBrush");

    /// <summary>
    /// 按 <c>--tab</c> 的原始值切换检测项。两种写法：卡片键名（推荐）与数字索引（兼容）。
    ///
    /// 键名优先。数字绑定了「卡片顺序」这个最容易变的东西 ——
    /// IPv6 卡片插进来，<c>--tab 3</c> 就从 DNS 变成了连通性，
    /// 而已经发出去的脚本不会跟着改。键名没有这个问题。
    /// </summary>
    private void SelectTabSpec(string spec)
    {
        var def = CardCatalog.CardForKey(spec)
               ?? (int.TryParse(spec, out var n) ? CardCatalog.CardForTab(n) : null);
        if (def == null) return;
        SelectCard(def.Key);
    }

    private void ScrollToTab(int tabIndex)
    {
        var def = CardCatalog.CardForTab(tabIndex);
        if (def != null) SelectCard(def.Key);
    }

    // ==================================================================
    // 启动
    // ==================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // ---- 命令行：--help 直接弹说明后退出 ----
        if (_cli.ShowHelp)
        {
            Dialogs.Show(this, "NetCheck 命令行参数", CommandLineOptions.HelpText,
                DialogButton.Ok, DialogIcon.Info);
            Application.Current.Shutdown();
            return;
        }

        if (_cli.Errors.Count > 0)
        {
            Dialogs.Show(this, "NetCheck",
                "以下命令行参数无法识别，已忽略：\n\n  " + string.Join("\n  ", _cli.Errors)
                + "\n\n用 --help 查看支持的参数。",
                DialogButton.Ok, DialogIcon.Warning);
        }

        if (_cli.Tab is string tabSpec) SelectTabSpec(tabSpec);

        // ---- 开发预览：NETCHECK_UI_DEMO=1 载入样例数据 ----
        if (Environment.GetEnvironmentVariable("NETCHECK_UI_DEMO") == "1")
        {
            _state = SampleState.Build();
            _state.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Render();
            LblStatus.Text = "演示数据 · 未连接网络";
            UpdateExportButtons();

            var demoTab = Environment.GetEnvironmentVariable("NETCHECK_UI_DEMO_TAB");
            if (!string.IsNullOrWhiteSpace(demoTab)) SelectTabSpec(demoTab!);
            return;
        }

        // ---- --check / --auto：启动后直接跑指定检测，可导出后自动退出 ----
        if (_cli.AutoCheckKey is string checkKey)
        {
            var keys = checkKey == "all"
                ? CommandLineOptions.AllExpansion
                : new[] { checkKey };

            foreach (var k in keys) await RunTaskAsync(k);

            if (_cli.ExportPath is string path)
            {
                // 给了导出路径就当作无人值守调用：写完文件直接退出
                ExportTo(path);
                Application.Current.Shutdown();
                return;
            }
            return;
        }

        // ---- 默认：静默读取本机配置（纯本地操作，无网络请求、无流量消耗）----
        await RunTaskAsync("local", quiet: true);
    }

    // ==================================================================
    // 检测选项
    // ==================================================================

    private void ApplySettingsToUi()
    {
        _loadingOptions = true;
        try
        {
            TxtHost.Text = _settings.TargetHost;
            TxtWebTarget.Text = _settings.WebTarget;
            TxtPorts.Text = _settings.PortSpec;
            CmbPreset.SelectedItem = PortPresets.ByName(_settings.PortPreset).Name;
            TxtConcurrency.Text = _settings.PortConcurrency.ToString();
            TxtPortTimeout.Text = _settings.PortTimeoutMs.ToString();
            TxtRounds.Text = _settings.LatencyRounds.ToString();
            TxtMaxHops.Text = _settings.TraceMaxHops.ToString();
            TxtTraceTimeout.Text = _settings.TraceTimeoutMs.ToString();
            TxtSpeedSeconds.Text = _settings.SpeedSeconds.ToString();
            TxtDnsTimeout.Text = _settings.DnsTimeoutMs.ToString();
        }
        finally
        {
            _loadingOptions = false;
        }
        UpdatePortHint();
    }

    /// <summary>
    /// 把界面上的值读进配置。
    /// </summary>
    /// <returns>
    /// 是否有数值被 <see cref="AppSettings.Clamp"/> 收敛过。
    /// 有的话调用方要告诉用户一声 —— 以前填 99999 会被静默改成 512，
    /// 用户看着自己刚输入的框突然变成别的数字，只会以为程序坏了。
    /// </returns>
    private bool ReadOptionsInto(AppSettings s)
    {
        s.TargetHost = TxtHost.Text.Trim();
        s.WebTarget = TxtWebTarget.Text.Trim();
        s.PortSpec = TxtPorts.Text.Trim();
        s.PortPreset = CmbPreset.SelectedItem as string ?? PortPresets.CustomName;
        s.PortConcurrency = ParseInt(TxtConcurrency.Text, s.PortConcurrency);
        s.PortTimeoutMs = ParseInt(TxtPortTimeout.Text, s.PortTimeoutMs);
        s.LatencyRounds = ParseInt(TxtRounds.Text, s.LatencyRounds);
        s.TraceMaxHops = ParseInt(TxtMaxHops.Text, s.TraceMaxHops);
        s.TraceTimeoutMs = ParseInt(TxtTraceTimeout.Text, s.TraceTimeoutMs);
        s.SpeedSeconds = ParseInt(TxtSpeedSeconds.Text, s.SpeedSeconds);
        s.DnsTimeoutMs = ParseInt(TxtDnsTimeout.Text, s.DnsTimeoutMs);

        var before = Snapshot(s);
        s.Clamp();
        return !before.Equals(Snapshot(s));
    }

    /// <summary>
    /// 参与 Clamp 的字段快照，用来判断「刚才那次收敛有没有真的改动什么」。
    /// 文本字段也要算进来 —— 把目标主机清空后 Clamp 会悄悄填回 223.5.5.5、
    /// 网页目标填了个认不出来的值会被清空，那同样是「输入框自己跳成别的值」，用户需要一句解释。
    /// </summary>
    private static (int, int, int, int, int, int, int, string, string, string, string) Snapshot(AppSettings s) => (
        s.PortConcurrency, s.PortTimeoutMs, s.LatencyRounds, s.TraceMaxHops,
        s.TraceTimeoutMs, s.DnsTimeoutMs, s.SpeedSeconds,
        s.TargetHost, s.WebTarget, s.PortSpec, s.PortPreset);

    private static int ParseInt(string? text, int fallback)
        => int.TryParse((text ?? "").Trim(), out var v) ? v : fallback;

    /// <summary>任一选项失焦：写回配置、落盘，并把 Clamp 之后的值回填给用户看。</summary>
    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingOptions) return;
        bool clamped = ReadOptionsInto(_settings);
        _settings.Save();
        ApplySettingsToUi();
        if (clamped) LblStatus.Text = "有数值超出允许范围，已自动收敛到边界值";
    }

    private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingOptions) return;

        var preset = PortPresets.ByName(CmbPreset.SelectedItem as string);
        if (preset.Spec.Length > 0)
        {
            _loadingOptions = true;
            try { TxtPorts.Text = preset.Spec; }
            finally { _loadingOptions = false; }
        }

        ReadOptionsInto(_settings);
        _settings.Save();
        UpdatePortHint();
    }

    /// <summary>
    /// 端口表达式下方的实时提示：解析结果 + 最坏耗时估算。
    /// 顺带把同样的摘要更新到「检测选项」页头部的说明行上，
    /// 不选中选项页时也照样记着，切过去就是最新的。
    /// </summary>
    private void UpdatePortHint()
    {
        var (ports, err) = PortCheck.ParsePorts(TxtPorts.Text);
        string presetName = CmbPreset.SelectedItem as string ?? PortPresets.CustomName;
        string shortName = presetName == PortPresets.CustomName ? "自定义" : presetName;

        if (err.Length > 0)
        {
            LblPortHint.Foreground = Res("WarnBrush");
            LblPortHint.Text = "表达式有误：" + err;
            SetOptionsHint($"目标 {TxtHost.Text.Trim()} · 端口表达式有误");
            return;
        }

        LblPortHint.Foreground = Res("MutedBrush");
        if (ports.Length == 0)
        {
            LblPortHint.Text = "还没有端口";
            SetOptionsHint($"目标 {TxtHost.Text.Trim()} · 还没有端口");
            return;
        }

        int concurrency = Math.Max(1, ParseInt(TxtConcurrency.Text, _settings.PortConcurrency));
        int timeout = Math.Max(1, ParseInt(TxtPortTimeout.Text, _settings.PortTimeoutMs));
        double worst = Math.Ceiling((double)ports.Length / concurrency) * timeout / 1000.0;

        LblPortHint.Text = $"{shortName} · 共 {ports.Length} 个端口 · 最坏约 {worst:F1} 秒";
        SetOptionsHint($"目标 {TxtHost.Text.Trim()} · {shortName}（{ports.Length} 个端口）· 并发 {concurrency}");
    }

    /// <summary>
    /// 「检测选项」的说明文字。它同时是详情面板头部那一行副标题 ——
    /// 选项页讲的就是当前配置是什么，所以两处用同一句话。
    /// 不选中选项页时也照样记着，切过去就是最新的。
    /// </summary>
    private string _optionsHint = CardCatalog.OptionsCard.Hint;

    private void SetOptionsHint(string text)
    {
        _optionsHint = text;
        if (_selectedKey == OptionsKey) DetailHint.Text = text;
    }

    private void BtnResetOptions_Click(object sender, RoutedEventArgs e)
    {
        var d = new AppSettings();
        _settings.TargetHost = d.TargetHost;
        _settings.PortSpec = d.PortSpec;
        _settings.PortPreset = d.PortPreset;
        _settings.PortConcurrency = d.PortConcurrency;
        _settings.PortTimeoutMs = d.PortTimeoutMs;
        _settings.LatencyRounds = d.LatencyRounds;
        _settings.TraceMaxHops = d.TraceMaxHops;
        _settings.TraceTimeoutMs = d.TraceTimeoutMs;
        _settings.SpeedSeconds = d.SpeedSeconds;
        _settings.DnsTimeoutMs = d.DnsTimeoutMs;
        _settings.Save();

        ApplySettingsToUi();
        LblStatus.Text = "检测选项已恢复默认值";
    }

    // ==================================================================
    // 窗口几何
    // ==================================================================

    private void RestoreWindowGeometry()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowGeometry()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = (int)Math.Round(Width);
            _settings.WindowHeight = (int)Math.Round(Height);
        }
        else
        {
            // 最大化 / 最小化时 Width 是屏幕尺寸，要取还原后的边界
            var b = RestoreBounds;
            if (b.Width > 0 && b.Height > 0)
            {
                _settings.WindowWidth = (int)Math.Round(b.Width);
                _settings.WindowHeight = (int)Math.Round(b.Height);
            }
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    // ==================================================================
    // 检测任务
    // ==================================================================

    /// <summary>
    /// 补读网卡列表与网关地址。**所有「顺带需要本机信息」的任务都必须走这里。**
    ///
    /// 以前每个任务各写一份 <c>if (s.Adapters == null)</c>，而且只判断网卡：
    /// <see cref="TaskNatAsync"/> 与 <see cref="TaskDnsAsync"/> 会设 <c>Adapters</c> 却不设 <c>Gateway</c>，
    /// 于是「先跑 NAT 或 DNS、再跑延迟」时，<see cref="TaskLatencyAsync"/> 看到 <c>Adapters != null</c>
    /// 就整个跳过，网关地址一直是空 —— 连通性卡片里的「本地链路」那一段会静默消失，
    /// 用户只会觉得「怎么少了一块」，看不出是顺序问题。
    ///
    /// 条件因此统一成「网卡或网关任一为空就补」，而且两件事各自判空，不会因为网卡已经有了就漏掉网关。
    /// </summary>
    private static async Task EnsureLocalInfoAsync(DiagState s, CancellationToken ct)
    {
        if (s.Adapters != null && !string.IsNullOrEmpty(s.Gateway)) return;

        var adapters = s.Adapters ?? await Task.Run(LocalNetInfo.GetAdapters, ct);
        s.Adapters = adapters;
        if (string.IsNullOrEmpty(s.Gateway)) s.Gateway = LocalNetInfo.PrimaryGateway(adapters);
    }

    private static async Task TaskLocalAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        // 文案不再写「本机网络配置」：一键检测会把这一步包成
        // 「【1/11】本机网络配置 · …」，这里再重复一遍会变成绕口令。
        p.Report("读取网卡与代理设置…");
        await Task.Run(() =>
        {
            var adapters = LocalNetInfo.GetAdapters();
            s.Adapters = adapters;
            s.Hostname = LocalNetInfo.HostName;
            s.Proxy = LocalNetInfo.GetProxySettings();
            s.Gateway = LocalNetInfo.PrimaryGateway(adapters);
        }, ct);
    }

    /// <summary>
    /// 一键检测 = 把每一个检测项依次跑一遍。
    ///
    /// 以前它只跑「本机 + 出口 + DNS 服务器 + 延迟」四项，名字却叫「一键检测」——
    /// 用户点完之后 IPv6、NAT、带宽、路由、端口、MTU 全是空的，
    /// 只能一个一个再点过去，等于「一键」之后还要手动点六下。
    /// 现在它按 <see cref="CommandLineOptions.AllExpansion"/> 的顺序跑全部原子项 ——
    /// 那份顺序表和 <c>--check all</c> 共用，不会再出现「按钮跑的和命令行跑的不是一回事」。
    ///
    /// 顺序不是随便排的：local 最先（后面几项都要网卡信息），ipv6 紧跟其后（读同一份数据），
    /// 然后才是出口 / 延迟 / NAT / DNS，最后是慢的带宽测速、路由追踪、端口扫描、MTU。
    /// 慢的排在后面，用户在前面几秒就能看到内容陆续填出来。
    /// </summary>
    private async Task TaskQuickAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        var steps = CommandLineOptions.AllExpansion;
        for (int i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!_tasks.TryGetValue(steps[i], out var step))
                continue;

            // 带序号的前缀，让「跑到哪儿了」一眼可见 —— 一共十项，
            // 光看「DNS 检测…」不知道还剩几项，也不知道后面还有什么。
            var label = $"【{i + 1}/{steps.Length}】{step.Label}";
            var inner = new Progress<string>(msg => p.Report(label + " · " + msg));
            await step.Run(inner, s, ct);
        }

        p.Report("全部检测已完成。");
    }

    /// <summary>
    /// 只查公网出口 IP。以前它只能靠「一键检测」顺带跑出来，
    /// 想看一眼出口就得连带等 DNS 和延迟（约 6 秒），粒度太粗。
    /// </summary>
    private static async Task TaskEgressAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        // 出口 IP 本身不依赖网卡信息，但「有几个出口」的判定要看本机有几个 IPv4 地址，
        // 单独点这一项时适配器列表可能还是空的，这里补一次，
        // 免得结论条上少一个维度。已经有数据就不重复读。
        await EnsureLocalInfoAsync(s, ct);

        // 三条路径都要测：多出口网络下它们会给出不同的 IP，
        // 只测一条会得出错误但看起来正常的结论。
        p.Report("探测公网出口 IP（原生 UDP）…");
        s.PublicIpDirect = await StunAndNat.GetEgressIpAsync(ct);

        p.Report("探测公网出口 IP（HTTP 经代理）…");
        s.PublicIpHttpProxy = await PublicIpProbe.QueryHttpAsync(true, ct);

        p.Report("探测公网出口 IP（HTTP 直连）…");
        s.PublicIpHttpDirect = await PublicIpProbe.QueryHttpAsync(false, ct);

        // 出口 IP 出来之后才能查归属地 —— 查询要拿 IP 当输入，顺序不能反
        await FillGeoAsync(p, s, ct);
    }

    /// <summary>
    /// 补齐 IP 归属地。挂在每个「产生了新 IP」的任务末尾即可，顺序无所谓：
    /// <see cref="IpGeo.FillAsync"/> 只查还没查过的，重复调用不会把请求量翻倍。
    /// 没活干时连进度都不报 —— 一台只有私有地址的机器上，闪一下又消失的提示比没有更让人困惑。
    /// </summary>
    private static async Task FillGeoAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        if (IpGeo.PendingCount(s) == 0) return;
        p.Report("查询 IP 归属地…");
        await IpGeo.FillAsync(s, ct);
    }

    /// <summary>
    /// IPv6 检测。**没有全局地址时几毫秒就结束** —— 出口查询和可达性探测都需要先有地址，
    /// 硬跑只会让卡片等满超时再给出一个「未启用」的结论。
    /// </summary>
    private static async Task TaskIpv6Async(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        // 地址来自网卡，和「本机网络配置」读的是同一个 API 的同一个集合
        await EnsureLocalInfoAsync(s, ct);

        s.Ipv6 = await Ipv6Probe.DetectAsync(s.Adapters ?? new List<AdapterInfo>(), p, ct);
    }

    /// <summary>
    /// 网页连通性检测。目标 = 内置的国内两个 + 境外一个，外加「检测选项」里填的网页目标（留空就不测）。
    /// 它是唯一一个**不看 IP 只看域名**的检测项 —— 因为 TLS 要校验 SNI 和证书，
    /// 拿 IP 去连一个虚拟主机根本代表不了「浏览器能不能打开」。
    /// </summary>
    private async Task TaskWebAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        s.Web = await WebProbe.DetectAsync(_settings.WebTarget, p, ct);
    }

    /// <summary>
    /// 只测延迟。以前延迟只能靠「一键检测」顺带跑出来，「连通性」卡片就没法自己填满自己，
    /// 拆出来之后每张卡片都有能独立把它跑出数据的按钮。
    /// </summary>
    private async Task TaskLatencyAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        // 关键：必须用 EnsureLocalInfoAsync 而不是只判 Adapters。
        // 单独点「延迟检测」时要拿到网关，否则本地链路那一段不会出现。
        await EnsureLocalInfoAsync(s, ct);
        await RunLatencyAsync(p, s, ct);
    }

    private async Task RunLatencyAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        p.Report($"测量网络延迟（每目标 {_settings.LatencyRounds} 次）…");
        s.Latency = await LatencyProbe.RunAllAsync(s.Gateway, p, ct, _settings.LatencyRounds);
    }

    private static async Task TaskNatAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        p.Report("准备本地地址…");
        await EnsureLocalInfoAsync(s, ct);
        var localIps = (s.Adapters ?? new List<AdapterInfo>())
            .Select(a => a.IPv4).Where(x => x.Length > 0).ToList();

        p.Report("向 STUN 服务器发起探测…");
        s.Nat = await StunAndNat.DetectAsync(localIps, ct);

        // STUN 看到的映射地址是运营商侧的公网 IP，往往和 HTTP 路径查出来的不是同一个
        await FillGeoAsync(p, s, ct);
    }

    private async Task TaskDnsAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        var list = new List<DnsResolveTest>();
        for (int i = 0; i < DnsProbe.TestDomains.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var dom = DnsProbe.TestDomains[i];
            p.Report($"解析 {dom}（{i + 1}/{DnsProbe.TestDomains.Length}）…");
            list.Add(await DnsProbe.ResolveAsync(dom, 3, ct));
        }
        s.DnsResolve = list;

        p.Report("检测 DNS 劫持…");
        s.Hijack = await DnsProbe.DetectHijackAsync(ct);

        // 顺手补上系统 DNS 列表，避免用户没跑一键检测时这里空着
        if (s.DnsServers == null)
        {
            await EnsureLocalInfoAsync(s, ct);
            var dns = LocalNetInfo.AllDnsServers(s.Adapters ?? new List<AdapterInfo>());
            s.DnsServers = dns;
            var tests = new List<DnsServerTest>();
            foreach (var server in dns)
            {
                ct.ThrowIfCancellationRequested();
                tests.Add(await DnsProbe.TestServerAsync(
                    server, DnsProbe.CompareTestDomain, _settings.DnsTimeoutMs, ct));
            }
            s.DnsServerTests = tests;
        }

        // 对比用的公共 DNS：只报「当前 DNS 36 ms」说明不了问题，
        // 得知道 223.5.5.5 只要 12 ms，用户才知道自己亏了多少、要不要换。
        // 和上面用同一个域名，两边数字才能横向比。
        p.Report("对比公共 DNS…");
        s.DnsCompareTests = await DnsProbe.TestCompareServersAsync(_settings.DnsTimeoutMs, ct);

        await FillGeoAsync(p, s, ct);
    }

    private async Task TaskSpeedAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        int secs = _settings.SpeedSeconds;
        p.Report($"直连测速中（约 {secs} 秒）…");
        var r = await BandwidthTester.TestAsync(secs, useProxy: false, p, ct);
        s.BandwidthDirect = r;

        if (!r.Ok)
        {
            p.Report("直连失败，改用代理测速…");
            s.BandwidthProxy = await BandwidthTester.TestAsync(secs, useProxy: true, p, ct);
        }
    }

    private async Task TaskTraceAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        var target = _settings.TargetHost;
        if (target.Length == 0) throw new InvalidOperationException("请先在「检测选项」里填写目标主机");

        s.Traceroute = await Traceroute.TraceAsync(
            target, _settings.TraceMaxHops, _settings.TraceTimeoutMs,
            new Progress<int>(ttl => p.Report($"第 {ttl} 跳…")), ct);

        // 路由跳数是所有检测里 IP 最多的一项（十几跳），归属地在这里最有价值：
        // 「第几跳出境」只看 IP 段是看不出来的
        await FillGeoAsync(p, s, ct);
    }

    private async Task TaskPortAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        var host = _settings.TargetHost;
        if (host.Length == 0) throw new InvalidOperationException("请先在「检测选项」里填写目标主机");

        var (ports, err) = PortCheck.ParsePorts(_settings.PortSpec);
        if (err.Length > 0) throw new InvalidOperationException(err);
        if (ports.Length == 0) throw new InvalidOperationException("端口表达式为空，请先填写要检测的端口");

        s.PortScan = await PortCheck.ScanAsync(
            host, ports, _settings.PortTimeoutMs, _settings.PortConcurrency,
            new Progress<(int Done, int Total)>(x => p.Report($"端口 {x.Done}/{x.Total}…")), ct);
    }

    private async Task TaskMtuAsync(IProgress<string> p, DiagState s, CancellationToken ct)
    {
        var target = _settings.TargetHost;
        if (target.Length == 0) throw new InvalidOperationException("请先在「检测选项」里填写目标主机");

        s.Mtu = await MtuProbe.DetectAsync(target, p, ct);
    }

    // ==================================================================
    // 任务调度
    // ==================================================================

    /// <summary>任务键 -> 卡片。用来在运行时把对应卡片的按钮改成「检测中…」。</summary>
    private SectionCard? CardForTask(string taskKey)
    {
        // 映射规则只在 CardCatalog 里写一份，这里不再重复一遍 FirstOrDefault(...)，
        // 免得两处哪天走偏（CardCatalog 那份是有断言守着的）。
        var def = CardCatalog.CardForTask(taskKey);
        if (def == null) return null;
        return _cardByKey.TryGetValue(def.Key, out var c) ? c : null;
    }

    private async Task RunTaskAsync(string key, bool quiet = false)
    {
        if (_busy) return;
        if (!_tasks.TryGetValue(key, out var def)) return;

        // 用户在输入框里改了参数但没失焦，这里补一次同步，保证用到的是最新值
        if (!quiet) ReadOptionsInto(_settings);

        _busy = true;
        _running = true;
        _quietRun = quiet;
        _activeTaskKey = key;
        _cts = new CancellationTokenSource();
        var label = def.Label;

        if (!quiet)
        {
            // 切到这一项再跑 —— 跑完了却停在别的页面上，等于什么都没看见。
            // （以前是「展开对应卡片」，现在导航取代了折叠，等价动作就是选中它。）
            var card = CardForTask(key);
            if (card != null) SelectCard(card.Key);

            SetBusy(true);
            Bar.IsIndeterminate = true;
            Bar.Visibility = Visibility.Visible;
            LblStatus.Text = $"{label}：准备中…";
        }

        var progress = new Progress<string>(m =>
        {
            if (!quiet) LblStatus.Text = $"{label}：{m}";
        });

        var sw = Stopwatch.StartNew();
        try
        {
            await def.Run(progress, _state, _cts.Token);
            sw.Stop();
            _state.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Render();

            LblStatus.Text = quiet
                ? "已自动读取本机网络配置 · 其余检测请在左侧选中项目，再点右上方运行按钮"
                : $"{label}：完成（用时 {sw.Elapsed.TotalSeconds:F1} 秒）";
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            LblStatus.Text = $"{label}：已取消";
        }
        catch (Exception ex)
        {
            sw.Stop();
            LblStatus.Text = $"{label}：失败（{ex.Message}）";
            if (!quiet)
                Dialogs.Show(this, $"{label} 失败", ex.Message, DialogButton.Ok, DialogIcon.Warning);
        }
        finally
        {
            _running = false;
            _quietRun = false;
            _activeTaskKey = null;
            _busy = false;              // 必须在 SetBusy / UpdateExportButtons 之前清掉
            _cts?.Dispose();
            _cts = null;
            if (!quiet)
            {
                Bar.IsIndeterminate = false;
                Bar.Visibility = Visibility.Collapsed;
                SetBusy(false);
            }
            UpdateExportButtons();
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var card in _cards)
        {
            foreach (var cb in card.Buttons)
            {
                cb.Btn.IsEnabled = !busy;
                cb.Btn.Content = busy && cb.TaskKey == _activeTaskKey ? "检测中…" : cb.Label;
            }
        }

        // 工具栏的「一键检测」是卡片按钮之外的第二条入口，不一起挡掉就绕过去了。
        // 「诊断包」同理，只是它还要看有没有数据可导，所以交给 UpdateExportButtons 统一算。
        BtnQuick.IsEnabled = !busy;
        // 「取消」空闲时直接收起来，而不是留着个灰掉的按钮占位置 ——
        // 它只在忙的时候有意义，闲着摆在那儿纯粹是噪音。
        BtnCancel.IsEnabled = busy;
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusDot(busy);
    }

    /// <summary>
    /// 「检测」菜单在忙碌时整体置灰。卡片按钮已经被 <see cref="SetBusy"/> 挡住了，
    /// 但菜单是另一条入口，不挡就等于没挡。
    /// 唯独「取消当前检测」要留着 —— 正在忙的时候恰恰是它最有用的时候。
    /// </summary>
    private void MenuDetect_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        foreach (var item in MenuDetect.Items.OfType<MenuItem>())
        {
            if (Equals(item.Tag, "cancel")) continue;
            item.IsEnabled = !_busy;
        }
    }

    /// <summary>
    /// 「有没有数据可导出」只从 <see cref="ReportView.AnyData"/> 取，不在这里另写一套判断。
    /// 之前这里手写了一串 <c>!= null</c>，漏了 <c>Latency</c>，而且和映射层是两份逻辑 ——
    /// 今天被掩盖（所有会产生延迟数据的任务都会顺带设 Adapters），
    /// 但只要以后加一个「只填延迟」的任务，就会出现「跑完延迟、导出按钮还是灰的」。
    /// </summary>
    private void UpdateExportButtons()
    {
        bool has = _view?.AnyData == true;
        BtnSave.IsEnabled = has;
        BtnCopy.IsEnabled = has;
        // 诊断包会拉起 ipconfig / route print 等一堆子进程，正在检测时不给点，
        // 否则两边的进度条和状态栏会互相覆盖。
        BtnBundle.IsEnabled = has && !_busy;
        BtnBundleMask.IsEnabled = has && !_busy;
    }

    // ==================================================================
    // 渲染
    // ==================================================================

    private void Render()
    {
        var v = ReportViewBuilder.Build(_state);
        _view = v;

        // 导航栏的状态点和问题角标全部刷新 —— 它们正是「一眼看出哪项有问题」的来源
        foreach (var def in SectionDefs)
        {
            if (!_cardByKey.TryGetValue(def.Key, out var card)) continue;
            UpdateBadge(card, def, v);
        }
        UpdateNavDots(v);

        // **只重建当前选中那一项的内容。** 十一个检测项的内容全建一遍，
        // 扫 1024 个端口时会白白构造上千个元素，而其中十个根本看不见。
        // 切到别的项时由 FillDetail() 用最新的 _view 补渲染。
        if (_cardByKey.TryGetValue(_selectedKey, out var selected))
        {
            var def = CardCatalog.CardForKey(selected.Key);
            if (def != null) FillCardBody(selected, def, v);
        }

        UpdateSummary(v);
        UpdateExportButtons();
    }

    /// <summary>
    /// 标题栏角标。概览卡片是特例：**它本身就是问题清单**，
    /// 所以它的角标统计全部问题，而不是按 TargetTab 过滤 ——
    /// 否则把卡片全收起来时，用户看到连通性/DNS/NAT 各有一个数字，
    /// 唯独装着问题清单的那张是空的，反而找不到问题在哪。
    /// </summary>
    private void UpdateBadge(SectionCard card, SectionDef def, ReportView v)
    {
        bool isOverview = def.TabIndex == ReportViewBuilder.OverviewIndex;

        int bad = isOverview
            ? v.Issues.Count(x => x.Tone == Tone.Bad)
            : v.Issues.Count(x => x.TargetTab == def.TabIndex && x.Tone == Tone.Bad);
        int warn = isOverview
            ? v.Issues.Count(x => x.Tone == Tone.Warn)
            : v.Issues.Count(x => x.TargetTab == def.TabIndex && x.Tone == Tone.Warn);

        int total = bad + warn;
        if (total > 0)
        {
            card.BadgeText.Text = total.ToString();
            card.BadgeText.Foreground = Res(bad > 0 ? "DangerBrush" : "WarnBrush");
            card.BadgeBox.Background = Res(bad > 0 ? "DangerBgBrush" : "WarnBgBrush");
            card.BadgeBox.Visibility = Visibility.Visible;
            card.BadgeBox.ToolTip = bad > 0 ? $"{bad} 项严重问题、{warn} 项提醒" : $"{warn} 项提醒";
        }
        else
        {
            card.BadgeBox.Visibility = Visibility.Collapsed;
        }
    }

    private void FillCardBody(SectionCard card, SectionDef def, ReportView v)
    {
        card.BodyHost.Children.Clear();

        var tv = def.TabIndex >= 0 && def.TabIndex < v.Tabs.Count ? v.Tabs[def.TabIndex] : null;

        // 概览页 = 仪表盘：结论横幅 → 指标块 → 结论胶囊 → 问题清单 → 检测摘要。
        // 顺序就是「先给结论、再给证据、最后给明细」，打开就能决定要不要往下看。
        if (def.TabIndex == ReportViewBuilder.OverviewIndex)
        {
            var (verdict, tone, summary) = HeroState(v);
            card.BodyHost.Children.Add(Renderer.BuildHero(
                verdict, tone, summary, v.AnyData ? _state.Timestamp : ""));

            if (v.Cards.Count > 0)
            {
                var grid = new UniformGrid
                {
                    Columns = MetricColumns(v.Cards.Count),
                    Margin = new Thickness(0, 0, -10, 0),
                };
                foreach (var c in v.Cards) grid.Children.Add(Renderer.BuildCard(c, ScrollToTab));
                card.BodyHost.Children.Add(grid);
            }

            if (v.Chips.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 2) };
                foreach (var c in v.Chips) wrap.Children.Add(Renderer.BuildChip(c));
                card.BodyHost.Children.Add(wrap);
            }
        }

        if (tv != null)
            card.BodyHost.Children.Add(Renderer.BuildTabBody(tv, ScrollToTab));
    }

    /// <summary>
    /// 概览结论横幅要的三样东西：一句话结论、它的色调、以及关键指标摘要。
    ///
    /// 结论只由问题清单决定 —— 和角标、状态点用的是同一份数据。
    /// 不另起一套判断，否则会出现「角标说 2 个问题、横幅说一切正常」这种自相矛盾。
    /// </summary>
    private static (string Verdict, Tone Tone, string Summary) HeroState(ReportView v)
    {
        int bad = v.Issues.Count(i => i.Tone == Tone.Bad);
        int warn = v.Issues.Count(i => i.Tone == Tone.Warn);

        var (verdict, tone) = !v.AnyData
            ? ("还没有检测数据", Tone.Neutral)
            : bad > 0 ? ($"发现 {bad} 项严重问题", Tone.Bad)
            : warn > 0 ? ($"网络基本正常，{warn} 项需要留意", Tone.Warn)
            : ("网络状态良好", Tone.Good);

        // 摘要取前四张有数据的指标块，拼成「标题 值」的形式。
        // 四张是上限：再多就会换行，横幅从「一眼扫过」变成「需要读」。
        var parts = v.Cards
            .Where(c => c.HasData)
            .Take(4)
            .Select(c => $"{c.Title} {c.Value}")
            .ToArray();

        return (verdict, tone, string.Join(" · ", parts));
    }

    /// <summary>
    /// 概览指标卡的列数。固定 4 列在指标涨到 6 张时会出现「4 + 2」——
    /// 第二行两张卡缩在左边，右边空一大块。这里在 3 和 4 列里挑空格数少的：
    /// 6 张 → 3+3 铺满，5 张 → 3+2，7 张 → 4+3；4 张以内单行铺满。
    /// </summary>
    private static int MetricColumns(int count)
    {
        if (count <= 4) return Math.Max(1, count);
        int best = 4, bestEmpty = int.MaxValue;
        for (int cols = 4; cols >= 3; cols--)
        {
            int rows = (count + cols - 1) / cols;
            int empty = rows * cols - count;
            if (empty < bestEmpty) { bestEmpty = empty; best = cols; }
        }
        return best;
    }

    private void UpdateSummary(ReportView v)
    {
        if (!v.AnyData)
        {
            LblSummary.Text = "暂无数据";
            return;
        }

        int bad = v.Issues.Count(i => i.Tone == Tone.Bad);
        int warn = v.Issues.Count(i => i.Tone == Tone.Warn);

        LblSummary.Text = bad > 0
            ? $"发现 {bad} 项严重问题" + (warn > 0 ? $"、{warn} 项提醒" : "")
            : warn > 0
                ? $"有 {warn} 项需要注意"
                : "未发现问题";
    }

    // ==================================================================
    // 命令：按钮 / 菜单项 / 快捷键三者共用同一份实现
    // ==================================================================

    private void DoCancel()
    {
        try { _cts?.Cancel(); } catch { /* 已经结束了 */ }
        LblStatus.Text = "正在取消…";
    }

    private void DoCopy()
    {
        try
        {
            Clipboard.SetText(ReportBuilder.RenderText(_state));
            LblStatus.Text = "报告已复制到剪贴板";
        }
        catch (Exception ex)
        {
            Dialogs.Show(this, "NetCheck", "复制失败：" + ex.Message,
                DialogButton.Ok, DialogIcon.Warning);
        }
    }

    private void DoSave()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"netcheck_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = "文本报告 (*.txt)|*.txt|JSON 数据 (*.json)|*.json",
            DefaultExt = ".txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        ExportTo(dlg.FileName);
    }

    /// <summary>按扩展名决定导出格式。菜单、快捷键、命令行 --export 共用。</summary>
    private void ExportTo(string path)
    {
        try
        {
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            else
            {
                File.WriteAllText(path, ReportBuilder.RenderText(_state), new UTF8Encoding(true));
            }
            LblStatus.Text = "已导出到 " + path;
        }
        catch (Exception ex)
        {
            Dialogs.Show(this, "NetCheck", "导出失败：" + ex.Message,
                DialogButton.Ok, DialogIcon.Error);
        }
    }

    /// <param name="masked">
    /// 脱敏版：主机名 / MAC / 公网 IP 打码后再打包。文件名里也带「脱敏」二字 ——
    /// 用户手里往往同时躺着两份包，光看时间戳分不出哪份能往外发。
    /// </param>
    private async Task DoBundleAsync(bool masked = false)
    {
        // 导出诊断包会跑一堆系统命令（ipconfig / route print / netsh …），
        // 期间再点一次检测会让两边的进度条和状态栏互相覆盖，结果也是半新半旧。
        // _running 只覆盖检测，挡不住这里，所以用 _busy。
        if (_busy) return;

        var suffix = masked ? "脱敏" : "";
        var dlg = new SaveFileDialog
        {
            FileName = $"NetCheck诊断包{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            Filter = "压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
        };
        if (dlg.ShowDialog(this) != true) return;

        _busy = true;
        SetBusy(true);
        Bar.IsIndeterminate = true;
        Bar.Visibility = Visibility.Visible;
        try
        {
            var prog = new Progress<string>(m => LblStatus.Text = $"导出诊断包{(masked ? "（脱敏）" : "")}：" + m);
            var res = await DiagBundle.ExportAsync(
                _state, ReportBuilder.RenderText(_state), dlg.FileName, prog, CancellationToken.None, masked);

            if (res.Ok)
            {
                LblStatus.Text = $"{(masked ? "脱敏诊断包" : "诊断包")}已导出（{res.SizeBytes / 1024.0:F0} KB）";
                var open = Dialogs.Show(this, "导出成功",
                    $"{(masked ? "脱敏诊断包" : "诊断包")}已保存：\n{dlg.FileName}\n\n"
                    + $"包含 {res.Entries.Count} 个文件，"
                    + (masked
                        ? "主机名 / MAC / 公网 IP 已打码，可以放心转发。\n\n"
                        : "内容未脱敏，含主机名与公网出口 IP，转发前请留意。\n\n")
                    + "是否打开所在文件夹？",
                    DialogButton.YesNo, DialogIcon.Info);
                if (open)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"")
                    { UseShellExecute = true });
            }
            else
            {
                LblStatus.Text = "诊断包导出失败";
                Dialogs.Show(this, "NetCheck", "导出失败：" + res.Error,
                    DialogButton.Ok, DialogIcon.Error);
            }
        }
        finally
        {
            _busy = false;
            Bar.IsIndeterminate = false;
            Bar.Visibility = Visibility.Collapsed;
            SetBusy(false);
            UpdateExportButtons();
        }
    }

    // ==================================================================
    // 菜单事件
    // ==================================================================

    private async void BtnQuick_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("quick");
    private async void BtnEgress_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("egress");
    private async void BtnIpv6_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("ipv6");
    private async void BtnWeb_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("web");
    private async void BtnLatency_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("latency");
    private async void BtnNat_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("nat");
    private async void BtnDns_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("dns");
    private async void BtnSpeed_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("speed");
    private async void BtnTrace_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("trace");
    private async void BtnPort_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("port");
    private async void BtnMtu_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("mtu");
    private async void BtnLocal_Click(object sender, RoutedEventArgs e) => await RunTaskAsync("local");

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DoCancel();
    private void BtnCopy_Click(object sender, RoutedEventArgs e) => DoCopy();
    private void BtnSave_Click(object sender, RoutedEventArgs e) => DoSave();
    private async void BtnBundle_Click(object sender, RoutedEventArgs e) => await DoBundleAsync();
    private async void BtnBundleMasked_Click(object sender, RoutedEventArgs e) => await DoBundleAsync(masked: true);

    private void MenuOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.SettingsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.SettingsDirectory)
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LblStatus.Text = "打开配置目录失败：" + ex.Message;
        }
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuHelp_Click(object sender, RoutedEventArgs e)
    {
        Dialogs.Show(this, "NetCheck 使用说明",
            """
            界面怎么用

            · 左边一列是全部检测项，点一项就在右边看它的详情。一次只看一项。
            · 每项左边一个状态点：绿=正常、橙=有提醒、红=有严重问题、灰=还没测过；
              右边的数字是问题条数。所以不用逐个点开就知道哪几项有问题。
            · 每项的运行按钮在右边详情面板的右上角，和它要填充的内容在一起 ——
              一个功能只有一个地方能点到。点下去会先切到那一项再跑。
            · 最上面的「概览」是汇总：有结论横幅、六项指标、结论胶囊和问题清单。
              清单里每一条都可以点，直接跳到对应的检测项看细节。
            · 「配置 → 检测选项」可以调目标主机、端口表达式、并发、超时等参数，
              改动会自动保存；填了超出范围的数值会收敛到边界值并提示你。
            · 端口表达式支持逗号 / 空格 / 分号分隔与 8000-8010 这样的范围写法。
            · 在「目标主机」框里按回车 = 开始路由追踪；在「端口表达式」框里按回车 = 开始端口检测。
            · 浅色 / 深色两套主题，Ctrl+T 切换；要固定成某一种走「视图」菜单。

            快捷键
              F5       一键检测
              Esc      取消当前检测
              Ctrl+C   复制完整报告
              Ctrl+S   导出报告（txt / json）
              Ctrl+B   导出诊断包（zip）
              Ctrl+Shift+B  脱敏诊断包
              Ctrl+T   切换浅色 / 深色主题

            命令行
              用 --help 查看全部参数，例如：
              NetCheck.exe --host 8.8.8.8 --ports 53,443 --auto --export report.txt
            """,
            DialogButton.Ok, DialogIcon.Info);
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.5.0";
        Dialogs.Show(this, "关于 NetCheck",
            $"""
             NetCheck 网络体检  v{ver}

             检测项目
               本机网卡 · 公网出口 IP · 网络延迟 / 丢包 / 抖动 · DNS 解析与劫持
               IPv6 · HTTP/HTTPS 分级连通性 · NAT 类型（STUN）
               带宽测速 · 路由追踪 · 端口检测 · MTU 探测
               （「一键检测」就是把这十一项依次跑一遍）

             配置文件
               {AppSettings.SettingsPath}
             """,
            DialogButton.Ok, DialogIcon.Info);
    }

    // ==================================================================
    // 快捷键
    // ==================================================================

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        // 焦点在输入框里时让 Ctrl+C 走系统复制，别抢用户的文本
        bool inTextBox = Keyboard.FocusedElement is TextBox;

        switch (e.Key)
        {
            case Key.F5:
                e.Handled = true;
                _ = RunTaskAsync("quick");
                break;

            case Key.Escape:
                if (_running)
                {
                    e.Handled = true;
                    DoCancel();
                }
                break;

            case Key.C when ctrl && !inTextBox:
                e.Handled = true;
                DoCopy();
                break;

            case Key.S when ctrl:
                e.Handled = true;
                DoSave();
                break;

            case Key.T when ctrl:
                // 在两种明暗之间对调。跟标题栏右侧那个按钮同一件事 ——
                // 「跟随系统」只能从「视图」菜单选，快捷键和按钮都只负责快速对调。
                e.Handled = true;
                ApplyTheme(Theme.IsDark ? ThemeMode.Light : ThemeMode.Dark, persist: true);
                break;

            case Key.B when ctrl:
                e.Handled = true;
                // 同一支里按 shift 分流：Ctrl+B 出原始包，Ctrl+Shift+B 出脱敏包。
                // 不把 Shift 写进 when 条件，是为了让两者共用这一个 case。
                _ = DoBundleAsync(masked: shift);
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>目标主机框按回车直接追踪，省掉「填完再去点按钮」这一步。</summary>
    private async void TxtHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _running) return;
        e.Handled = true;
        await RunTaskAsync("trace");
    }

    private async void TxtPorts_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _running) return;
        e.Handled = true;
        await RunTaskAsync("port");
    }

    /// <summary>网页目标框按回车直接跑网页连通性检测。留空也允许 —— 那时跑的就是内置的三个目标。</summary>
    private async void TxtWebTarget_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _running) return;
        e.Handled = true;
        // 回车要先把输入框里的值落进配置，否则检测读到的还是上一次的旧值
        Options_Changed(sender, e);
        await RunTaskAsync("web");
    }

    // ==================================================================
    // 关闭
    // ==================================================================

    protected override void OnClosing(CancelEventArgs e)
    {
        // 静默的启动读取（本机配置）不该拦着用户关窗口 —— 它只读注册表和网卡，没有副作用。
        // 诊断包导出也算「忙」：它正在写 zip，半途关掉会留下一个残缺文件。
        if (_busy && !_quietRun)
        {
            if (!Dialogs.Show(this, "NetCheck",
                    "还有任务正在进行，现在退出会中断它。确定要退出吗？",
                    DialogButton.OkCancel, DialogIcon.Question))
            {
                e.Cancel = true;
                return;
            }
            try { _cts?.Cancel(); } catch { /* 已经结束了 */ }
        }

        SaveWindowGeometry();
        ReadOptionsInto(_settings);
        _settings.Save();

        // Theme.Changed 是静态事件，订阅者会被它一直抓着不放。
        // 窗口关了却还挂在事件上，下次再开一个窗口就会收到两份通知（这次是重复改色，无害，
        // 但同一模式重复触发本身就不该发生）。
        Theme.Changed -= OnThemeChanged;

        base.OnClosing(e);
    }
}
