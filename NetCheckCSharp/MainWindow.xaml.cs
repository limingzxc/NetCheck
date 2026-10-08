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

    /// <summary>一张检测项卡片的可动部分。</summary>
    private sealed class SectionCard
    {
        public string Key = "";
        public int TabIndex = -1;
        public Border Root = null!;
        public Border Header = null!;
        public Border Body = null!;
        public StackPanel BodyHost = null!;
        public TextBlock HintText = null!;
        public Border BadgeBox = null!;
        public TextBlock BadgeText = null!;
        public TextBlock Chevron = null!;
        public readonly List<CardButton> Buttons = new();
        public bool Collapsed;
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

    /// <summary>最近一次渲染出来的视图模型。收起的卡片不重建内容，展开时要用它补渲染。</summary>
    private ReportView? _view;

    private readonly List<SectionCard> _cards = new();
    private readonly Dictionary<string, SectionCard> _cardByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>分组键 -> 那一行分组标题（整行 + 右侧计数文案）。</summary>
    private readonly Dictionary<string, (Button Root, TextBlock Right)> _groupHeaders = new(StringComparer.Ordinal);

    /// <summary>分组键 -> 组内的卡片。分组标题的点击就是逐张调 SetCollapsed。</summary>
    private readonly Dictionary<string, List<SectionCard>> _groupCards = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (string Label, Func<IProgress<string>, DiagState, CancellationToken, Task> Run)> _tasks;

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _cli = CommandLineOptions.Parse(Environment.GetCommandLineArgs().Skip(1));
        _cli.ApplyTo(_settings);

        InitializeComponent();

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
        BuildCards();
        ApplySettingsToUi();
        RestoreWindowGeometry();

        Loaded += OnLoaded;
    }

    // ==================================================================
    // 卡片构建
    // ==================================================================

    private void BuildCards()
    {
        // 检测选项的输入控件声明在 XAML 里（保留 x:Name），这里把它从 StackPanel 里摘出来，
        // 塞进和其他卡片一模一样的外壳，这样折叠行为、间距、圆角全都统一。
        SectionHost.Children.Remove(OptionsFields);
        OptionsFields.Visibility = Visibility.Visible;

        // 按分组铺卡片流：每组先放一行分组标题，再放组内的卡片。
        // 高级组内部还有一层「二级分组」（网络路径 / 连接能力 / 协议），
        // 常用组没有 —— SubGroupsOf 对常用组返回空数组，下面那段自然跳过。
        foreach (var (groupKey, groupTitle) in CardCatalog.Groups)
        {
            var defs = CardCatalog.CardsInGroup(groupKey);
            if (defs.Length == 0) continue;

            SectionHost.Children.Add(BuildGroupHeader(groupKey, groupTitle));

            // 不属于任何二级分组的卡片排在最前（常用组全部卡片都走这一支）
            foreach (var def in defs.Where(d => d.SubGroup.Length == 0))
                AddCard(BuildCard(def, BodyFor(def)));

            foreach (var sub in CardCatalog.SubGroupsOf(groupKey))
            {
                SectionHost.Children.Add(BuildSubGroupHeader(sub));
                foreach (var def in CardCatalog.CardsInSubGroup(groupKey, sub.Key))
                    AddCard(BuildCard(def, BodyFor(def)));
            }
        }

        // 首次运行才把「高级」组收起来；老用户的配置里没有这些键，
        // 保持展开（不打扰）—— 别拿升级当借口替他改折叠偏好。
        if (_settings.FirstRun)
            _settings.CollapsedCards = CardCatalog.DefaultCollapsedCards();

        foreach (var key in _settings.CollapsedCards)
            if (_cardByKey.TryGetValue(key, out var c))
                SetCollapsed(c, true, persist: false);

        UpdateToggleAllLabel();
        UpdateGroupHeaders();
    }

    /// <summary>
    /// 一行分组标题：左边组名，右边计数与操作提示，整行可点。
    /// 刻意不套卡片外壳 —— 它只是个分隔标签，套了就和真卡片混淆。
    /// </summary>
    private UIElement BuildGroupHeader(string groupKey, string title)
    {
        var label = MakeText(title, 13, Res("MutedBrush"));

        var right = MakeText("", 11.5, Res("MutedBrush"));
        right.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(label, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(label);
        grid.Children.Add(right);

        var btn = new Button
        {
            Style = (Style)FindResource("GroupHeaderButton"),
            Content = grid,
            ToolTip = $"展开或收起「{title}」组的全部卡片",
        };
        // 标题由两个 TextBlock 拼成，UIA 从 Content 推不出名字，显式给一个
        System.Windows.Automation.AutomationProperties.SetName(btn, title);
        btn.Click += (_, _) => ToggleGroup(groupKey);

        _groupHeaders[groupKey] = (btn, right);
        _groupCards[groupKey] = new List<SectionCard>();
        return btn;
    }

    /// <summary>
    /// 一行二级分组标题（网络路径 / 连接能力 / 协议）。
    /// **刻意做成不可点的纯标签**：一级分组标题可点是因为它有明确的语义（整组收起），
    /// 而二级分组再引入一套折叠状态，就会和「组收起」「卡片收起」打架 ——
    /// 三套状态互相覆盖是这类界面最容易出的 bug。它只负责把高级区分成三块，让人扫得动。
    /// </summary>
    private UIElement BuildSubGroupHeader(SubGroupDef sub)
    {
        var bar = new Border
        {
            Width = 3,
            Height = 12,
            Background = Res("FaintBrush"),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var label = MakeText(sub.Title, 12, Res("MutedBrush"), FontWeights.SemiBold);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 2),
        };
        panel.Children.Add(bar);
        panel.Children.Add(label);
        System.Windows.Automation.AutomationProperties.SetName(panel, sub.Title);
        return panel;
    }

    /// <summary>卡片的展示区。只有「检测选项」那张卡用 XAML 里声明的输入控件，其余都是空面板由渲染层填。</summary>
    private UIElement BodyFor(SectionDef def)
        => def.Key == OptionsKey ? OptionsFields : (UIElement)new StackPanel();

    private void AddCard(SectionCard card)
    {
        _cards.Add(card);
        _cardByKey[card.Key] = card;
        SectionHost.Children.Add(card.Root);

        var group = CardCatalog.GroupOfCard(card.Key);
        if (group.Length > 0 && _groupCards.TryGetValue(group, out var list))
            list.Add(card);
    }

    private SectionCard BuildCard(SectionDef def, UIElement body)
    {
        string key = def.Key, title = def.Title, hint = def.Hint;
        int tabIndex = def.TabIndex;
        var actions = def.Actions;

        var card = new SectionCard { Key = key, TabIndex = tabIndex };

        // ---- 标题栏 ----
        card.Chevron = MakeText("\u25BE", 11, Res("FaintBrush"));
        card.Chevron.Margin = new Thickness(10, 0, 0, 0);

        var titleText = MakeText(title, 14, Res("TextBrush"), FontWeights.SemiBold);

        card.BadgeBox = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 2),
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        card.BadgeText = MakeText("", 11.5, Res("MutedBrush"), FontWeights.SemiBold);
        card.BadgeBox.Child = card.BadgeText;

        card.HintText = MakeText(hint, 11.5, Res("MutedBrush"));
        card.HintText.Margin = new Thickness(12, 0, 0, 0);
        card.HintText.TextTrimming = TextTrimming.CharacterEllipsis;

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 标题
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 角标
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 说明
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 运行按钮
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 折叠箭头

        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(card.BadgeBox, 1);
        Grid.SetColumn(card.HintText, 2);
        Grid.SetColumn(card.Chevron, 4);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(card.BadgeBox);
        headerGrid.Children.Add(card.HintText);
        headerGrid.Children.Add(card.Chevron);

        if (actions.Length > 0)
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            foreach (var a in actions)
            {
                var btn = new Button
                {
                    Content = a.Label,
                    Style = (Style)FindResource("SoftButton"),
                    FontSize = 12,
                    Padding = new Thickness(13, 4, 13, 4),
                    // 标题栏里的按钮不要 SoftButton 默认的下边距，否则整行会往下偏 8px
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = a.Tip,
                };
                var cb = new CardButton { Btn = btn, TaskKey = a.TaskKey, Label = a.Label };
                btn.Click += async (_, _) => await RunTaskAsync(cb.TaskKey);
                card.Buttons.Add(cb);
                bar.Children.Add(btn);
            }
            Grid.SetColumn(bar, 3);
            headerGrid.Children.Add(bar);
        }

        card.Header = new Border
        {
            // 必须是 Transparent 而不是 null：Background 为空时整块区域不参与命中测试，点标题折不起来
            Background = Brushes.Transparent,
            Padding = new Thickness(14, 11, 14, 11),
            Cursor = Cursors.Hand,
            Child = headerGrid,
        };
        card.Header.MouseLeftButtonUp += (_, e) =>
        {
            // 标题栏里的按钮自己会吃掉 MouseLeftButtonUp，这里再兜一层，避免以后换控件时误折叠
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
            SetCollapsed(card, !card.Collapsed);
            e.Handled = true;
        };

        // ---- 内容区 ----
        card.BodyHost = body as StackPanel ?? new StackPanel { Children = { body } };
        card.Body = new Border
        {
            Padding = new Thickness(14, 0, 14, 14),
            Child = card.BodyHost,
        };

        // ---- 外壳 ----
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(card.Header, 0);
        Grid.SetRow(card.Body, 1);
        outer.Children.Add(card.Header);
        outer.Children.Add(card.Body);

        card.Root = new Border
        {
            Background = Res("CardBrush"),
            BorderBrush = Res("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = outer,
        };

        return card;
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
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
    // 折叠
    // ==================================================================

    private void SetCollapsed(SectionCard card, bool collapsed, bool persist = true)
    {
        card.Collapsed = collapsed;
        card.Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        card.Chevron.Text = collapsed ? "\u25B8" : "\u25BE";   // ▸ / ▾

        // Render() 会跳过收起的卡片（省掉上千个元素），所以展开时要补一次，
        // 否则展开后看到的还是上次收起之前的内容。
        if (!collapsed && _view != null)
        {
            var def = CardCatalog.Sections.FirstOrDefault(d => d.Key == card.Key);
            if (def != null) FillCardBody(card, def, _view);
        }

        if (persist)
        {
            PersistCollapsed();
            UpdateToggleAllLabel();
        }
    }

    private void PersistCollapsed()
    {
        _settings.CollapsedCards = _cards.Where(c => c.Collapsed).Select(c => c.Key).ToArray();
        _settings.Save();
        UpdateGroupHeaders();
    }

    private void UpdateToggleAllLabel()
        => BtnToggleAll.Content = _cards.Any(c => !c.Collapsed) ? "全部收起" : "全部展开";

    /// <summary>
    /// 点分组标题 = 批量收起 / 展开该组。
    /// **刻意不引入第二套折叠状态**：这里就是逐张调 SetCollapsed，
    /// 折叠状态仍然按卡片键存进 CollapsedCards，不新增字段 ——
    /// 否则「组收起」和「卡片收起」两套状态会互相打架。
    /// </summary>
    private void ToggleGroup(string groupKey)
    {
        if (!_groupCards.TryGetValue(groupKey, out var cards) || cards.Count == 0) return;

        // 只要组里还有展开的，就整体收起；全收着才展开
        bool collapse = cards.Any(c => !c.Collapsed);
        foreach (var c in cards) SetCollapsed(c, collapse, persist: false);

        PersistCollapsed();
        UpdateToggleAllLabel();
    }

    private void UpdateGroupHeaders()
    {
        foreach (var (groupKey, entry) in _groupHeaders)
        {
            if (!_groupCards.TryGetValue(groupKey, out var cards) || cards.Count == 0) continue;
            entry.Right.Text = cards.All(c => c.Collapsed)
                ? $"{cards.Count} 项 · 点击展开"
                : $"{cards.Count} 项 · 点击收起";
        }
    }

    private void SetAllCollapsed(bool collapsed)
    {
        foreach (var c in _cards) SetCollapsed(c, collapsed, persist: false);
        PersistCollapsed();
        UpdateToggleAllLabel();
    }

    private void BtnToggleAll_Click(object sender, RoutedEventArgs e)
        => SetAllCollapsed(_cards.Any(c => !c.Collapsed));

    private void MenuExpandAll_Click(object sender, RoutedEventArgs e) => SetAllCollapsed(false);

    private void MenuCollapseAll_Click(object sender, RoutedEventArgs e) => SetAllCollapsed(true);

    private void MenuShowOptions_Click(object sender, RoutedEventArgs e)
    {
        if (!_cardByKey.TryGetValue(OptionsKey, out var card)) return;
        SetCollapsed(card, false);
        card.Root.BringIntoView();
    }

    private void MenuTab_Click(object sender, RoutedEventArgs e)
    {
        // 菜单项的 Tag 一律写卡片键名，不写数字 —— 数字会随卡片顺序漂移
        if (sender is MenuItem mi && mi.Tag is string s) ScrollToCard(s);
    }

    /// <summary>
    /// 按 <c>--tab</c> 的原始值滚动。两种写法：卡片键名（推荐）与数字索引（兼容）。
    ///
    /// 键名优先。数字绑定了「卡片顺序」这个最容易变的东西 ——
    /// IPv6 卡片插进来，<c>--tab 3</c> 就从 DNS 变成了连通性，
    /// 而已经发出去的脚本不会跟着改。键名没有这个问题。
    /// </summary>
    private void ScrollToTabSpec(string spec)
    {
        var def = CardCatalog.CardForKey(spec)
               ?? (int.TryParse(spec, out var n) ? CardCatalog.CardForTab(n) : null);
        if (def == null) return;
        ScrollToCard(def.Key);
    }

    private void ScrollToTab(int tabIndex)
    {
        var def = CardCatalog.CardForTab(tabIndex);
        if (def != null) ScrollToCard(def.Key);
    }

    /// <summary>
    /// 滚动到某个检测项卡片。以前是切换标签页，现在只是把卡片滚进视野。
    /// 顺带展开它 —— 跳过去却只看到一行标题（卡片是收着的）等于没跳。
    /// </summary>
    private void ScrollToCard(string cardKey)
    {
        if (!_cardByKey.TryGetValue(cardKey, out var card)) return;
        if (card.Collapsed) SetCollapsed(card, false);
        card.Root.BringIntoView();
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
            MessageBox.Show(this, CommandLineOptions.HelpText, "NetCheck 命令行参数",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
            return;
        }

        if (_cli.Errors.Count > 0)
        {
            MessageBox.Show(this,
                "以下命令行参数无法识别，已忽略：\n\n  " + string.Join("\n  ", _cli.Errors)
                + "\n\n用 --help 查看支持的参数。",
                "NetCheck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (_cli.Tab is string tabSpec) ScrollToTabSpec(tabSpec);

        // ---- 开发预览：NETCHECK_UI_DEMO=1 载入样例数据 ----
        if (Environment.GetEnvironmentVariable("NETCHECK_UI_DEMO") == "1")
        {
            _state = SampleState.Build();
            _state.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Render();
            LblStatus.Text = "演示数据 · 未连接网络";
            UpdateExportButtons();

            var demoTab = Environment.GetEnvironmentVariable("NETCHECK_UI_DEMO_TAB");
            if (!string.IsNullOrWhiteSpace(demoTab)) ScrollToTabSpec(demoTab!);
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
        s.CollapsedCards = _cards.Where(c => c.Collapsed).Select(c => c.Key).ToArray();

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
    /// 顺带把同样的摘要写到「检测选项」卡片标题栏上，折叠起来也知道当前配置是什么。
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

    private void SetOptionsHint(string text)
    {
        if (_cardByKey.TryGetValue(OptionsKey, out var card)) card.HintText.Text = text;
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
            // 卡片收起来的时候跑检测等于什么都没看见，先展开
            var card = CardForTask(key);
            if (card != null && card.Collapsed) SetCollapsed(card, false);

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
                ? "已自动读取本机网络配置 · 其余检测请点对应卡片上的按钮"
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
                MessageBox.Show(this, ex.Message, $"{label} 失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
        BtnCancel.IsEnabled = busy;
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

        foreach (var def in SectionDefs)
        {
            if (!_cardByKey.TryGetValue(def.Key, out var card)) continue;

            UpdateBadge(card, def, v);

            // 收起的卡片不重建内容：扫 1024 个端口时，即使卡片是收着的，
            // 每次检测完也会白白构造上千个元素。展开时由 SetCollapsed 补渲染。
            if (!card.Collapsed) FillCardBody(card, def, v);
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

        // 概览卡片额外挂上指标卡和结论条 —— 它们本来就是「全局摘要」，
        // 放进概览卡片比悬在所有内容之上更说得通。
        if (def.TabIndex == ReportViewBuilder.OverviewIndex && v.Cards.Count > 0)
        {
            var grid = new UniformGrid { Columns = MetricColumns(v.Cards.Count), Margin = new Thickness(0, 0, -10, 0) };
            foreach (var c in v.Cards) grid.Children.Add(Renderer.BuildCard(c, ScrollToTab));
            card.BodyHost.Children.Add(grid);
        }

        if (def.TabIndex == ReportViewBuilder.OverviewIndex && v.Chips.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 2) };
            foreach (var c in v.Chips) wrap.Children.Add(Renderer.BuildChip(c));
            card.BodyHost.Children.Add(wrap);
        }

        if (tv != null)
            card.BodyHost.Children.Add(Renderer.BuildTabBody(tv, ScrollToTab));
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
            MessageBox.Show(this, "复制失败：" + ex.Message, "NetCheck",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, "导出失败：" + ex.Message, "NetCheck",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
                var open = MessageBox.Show(this,
                    $"{(masked ? "脱敏诊断包" : "诊断包")}已保存：\n{dlg.FileName}\n\n"
                    + $"包含 {res.Entries.Count} 个文件，"
                    + (masked
                        ? "主机名 / MAC / 公网 IP 已打码，可以放心转发。\n\n"
                        : "内容未脱敏，含主机名与公网出口 IP，转发前请留意。\n\n")
                    + "是否打开所在文件夹？",
                    "导出成功", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"")
                    { UseShellExecute = true });
            }
            else
            {
                LblStatus.Text = "诊断包导出失败";
                MessageBox.Show(this, "导出失败：" + res.Error, "NetCheck",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
        MessageBox.Show(this,
            """
            使用说明

            · 每个检测项是一张卡片，卡片标题栏右侧的按钮就是它的运行入口，
              检测结果直接填在这张卡片里 —— 一个功能只有一个地方能点到。
            · 点卡片标题栏可以折叠；工具栏的「全部收起 / 全部展开」一次处理所有卡片。
            · 标题栏上的数字角标表示该检测项下有待处理的问题。
              概览卡片的角标是全部问题的总数，因为它本身就是那份问题清单。
            · 概览卡片里的每条问题都可以点击，直接跳到对应的检测项卡片看细节。
            · 最上面的「检测选项」卡片可以调整目标主机、端口表达式、并发、超时等参数，
              改动会自动保存；填了超出范围的数值会收敛到边界值并提示你。
              折叠后标题栏仍会显示当前配置摘要。
            · 端口表达式支持逗号 / 空格 / 分号分隔与 8000-8010 这样的范围写法。
            · 在「目标主机」框里按回车 = 开始路由追踪；在「端口表达式」框里按回车 = 开始端口检测。

            快捷键
              F5       一键检测
              Esc      取消当前检测
              Ctrl+C   复制完整报告
              Ctrl+S   导出报告（txt / json）
              Ctrl+B   导出诊断包（zip）

            命令行
              用 --help 查看全部参数，例如：
              NetCheck.exe --host 8.8.8.8 --ports 53,443 --auto --export report.txt
            """,
            "NetCheck 使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.5.0";
        MessageBox.Show(this,
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
            "关于 NetCheck", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var r = MessageBox.Show(this,
                "还有任务正在进行，现在退出会中断它。确定要退出吗？",
                "NetCheck", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
            try { _cts?.Cancel(); } catch { /* 已经结束了 */ }
        }

        SaveWindowGeometry();
        ReadOptionsInto(_settings);
        _settings.Save();

        base.OnClosing(e);
    }
}
