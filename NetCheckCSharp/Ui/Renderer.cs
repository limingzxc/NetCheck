using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace NetCheck.Ui;

/// <summary>把 ReportView 渲染成 WPF 元素。全部在代码里构造，避免 XAML 里堆一堆 DataTemplate。</summary>
public static class Renderer
{
    // ---- 调色板（浅色主题）----
    // **App.xaml 是唯一权威**：这里只按资源键取用，不再保留第二份数值。
    // 曾经两边各写一套且悄悄漂移过 —— Faint（#9AA1AB vs #A8AEB8）、
    // Line（#E3E6EB vs #DCDFE4）、SoftBg（#F4F6F9 vs #EEF1F6）三处对不上，
    // 改色只改一半，界面从此有两种灰、两种边线。2026-10-12 整改为单向引用。
    //
    // 语义说明见 App.xaml 各键上的注释：Muted 承载所有需要阅读的次级文字，
    // 在白底 / SoftBg 底上都过 WCAG AA；Faint 只用于装饰（断开状态的圆点）。
    //
    // 取不到资源时退回与 App.xaml 当前值一致的兜底色 —— Renderer 可能在
    // 没有 Application 的上下文里被构造（例如离屏渲染、界面之外的调用），
    // 那时宁可颜色不变也不能抛异常。兜底值全项目仅此一处，改 App.xaml 时连带修改。
    private static SolidColorBrush Res(string key, byte r, byte g, byte b)
        => Application.Current?.TryFindResource(key) as SolidColorBrush
           ?? New(r, g, b);

    private static readonly SolidColorBrush Ink      = Res("TextBrush",     0x1F, 0x23, 0x28);
    private static readonly SolidColorBrush Muted    = Res("MutedBrush",    0x66, 0x70, 0x80);
    private static readonly SolidColorBrush Faint    = Res("FaintBrush",    0xA8, 0xAE, 0xB8);
    private static readonly SolidColorBrush Line     = Res("LineBrush",     0xDC, 0xDF, 0xE4);
    private static readonly SolidColorBrush CardBg   = Res("CardBrush",     0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush SoftBg   = Res("SoftBrush",     0xEE, 0xF1, 0xF6);
    private static readonly SolidColorBrush Accent   = Res("AccentBrush",   0x2F, 0x6F, 0xD0);
    private static readonly SolidColorBrush Good     = Res("OkBrush",       0x1D, 0x7A, 0x4D);
    private static readonly SolidColorBrush Warn     = Res("WarnBrush",     0xB3, 0x54, 0x1E);
    private static readonly SolidColorBrush Bad      = Res("DangerBrush",   0xC0, 0x39, 0x2B);
    private static readonly SolidColorBrush GoodBg   = Res("OkBgBrush",     0xEA, 0xF6, 0xEF);
    private static readonly SolidColorBrush WarnBg   = Res("WarnBgBrush",   0xFD, 0xF2, 0xE8);
    private static readonly SolidColorBrush BadBg    = Res("DangerBgBrush", 0xFC, 0xEB, 0xE9);
    private static readonly SolidColorBrush AccentBg = Res("AccentBgBrush", 0xEC, 0xF2, 0xFC);

    private const string UiFont = "Microsoft YaHei UI";
    private const string MonoFont = "Consolas, Microsoft YaHei UI";

    private static SolidColorBrush New(byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromRgb(r, g, b)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static Brush ToneBrush(Tone t) => t switch
    {
        Tone.Good => Good,
        Tone.Warn => Warn,
        Tone.Bad => Bad,
        _ => Muted,
    };

    private static Brush ToneBg(Tone t) => t switch
    {
        Tone.Good => GoodBg,
        Tone.Warn => WarnBg,
        Tone.Bad => BadBg,
        _ => SoftBg,
    };

    private static TextBlock Text(string s, double size, Brush brush,
                                  FontWeight? weight = null, string? font = null)
        => new()
        {
            Text = s,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = new FontFamily(font ?? UiFont),
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>
    /// 「主文本 + 右侧归属地」，用于任何显示 IP 的地方（出口、DNS、路由跳）。
    ///
    /// **归属地挂在主文本后面，而不是单开一列。** 它的长度差异极大 ——
    /// 从「局域网」三个字到「示例 · 美国 西海岸」，单开一列要么按最长的留出一大片空白，
    /// 要么按最短的把长地名截断；跟着主文本走则长度自适应，而且天然就贴在 IP 右边，
    /// 三种表格（键值 / 条形 / 路由）用同一个写法，视觉上也统一。
    ///
    /// 颜色取 <see cref="Muted"/>、字重回归常规、字号小半号 —— 它是注脚，
    /// 不能盖过 IP 本身，也不能跟着 IP 的语义色一起变红变绿（那会让人以为归属地也有问题）。
    /// 不用更浅的 Faint：归属地是要读的信息，得满足对比度要求。
    /// </summary>
    private static TextBlock TextWithGeo(string main, string geo, double size, Brush brush,
                                         FontWeight? weight = null, string? font = null)
    {
        var tb = Text(main, size, brush, weight, font);
        if (geo.Length == 0) return tb;

        tb.Inlines.Add(new Run("  " + geo)
        {
            Foreground = Muted,
            FontWeight = FontWeights.Normal,
            FontSize = Math.Max(9.5, size - 1),
        });
        return tb;
    }

    // ==================================================================
    // 顶部指标卡片
    // ==================================================================

    public static UIElement BuildCard(MetricCard c, Action<int> jump)
    {
        var title = Text(c.Title, 12, Muted);
        var value = Text(c.Value, 21, c.HasData ? ToneBrush(c.Tone) : Muted, FontWeights.SemiBold);
        value.Margin = new Thickness(0, 4, 0, 2);
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        var sub = Text(c.Sub, 11.5, Muted);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(value);
        stack.Children.Add(sub);

        var bd = new Border
        {
            Background = CardBg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 11, 14, 12),
            Margin = new Thickness(0, 0, 10, 0),
            Child = stack,
        };

        if (c.HasData && c.TargetTab >= 0)
        {
            bd.Cursor = Cursors.Hand;
            bd.ToolTip = "点击查看详情";
            bd.MouseEnter += (_, _) => bd.BorderBrush = Accent;
            bd.MouseLeave += (_, _) => bd.BorderBrush = Line;
            bd.MouseLeftButtonUp += (_, _) => jump(c.TargetTab);
        }
        else
        {
            bd.Opacity = 0.72;
        }
        return bd;
    }

    // ==================================================================
    // 结论胶囊
    // ==================================================================

    public static UIElement BuildChip(Chip c)
        => new Border
        {
            Background = ToneBg(c.Tone),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 6),
            Child = Text(c.Text, 11.5, ToneBrush(c.Tone), FontWeights.SemiBold),
        };

    // ==================================================================
    // 标签页内容
    // ==================================================================

    /// <summary>
    /// 只要内容、不套 ScrollViewer。卡片式布局里每个检测项已经是外层滚动页面的一段，
    /// 再嵌一层滚动条会出现「滚动条套滚动条」，滚轮停在上面时外层不动，很别扭。
    /// </summary>
    /// <param name="jump">
    /// 点击「问题条目」时跳到对应的检测项卡片。概览卡片里的问题清单是全量汇总，
    /// 用户看到一条问题却不知道去哪看细节，必须能点进去。
    /// </param>
    public static UIElement BuildTabBody(TabView t, Action<int> jump)
    {
        var host = new StackPanel { Margin = new Thickness(0, 2, 6, 8) };

        if (!t.HasData)
        {
            host.Children.Add(new Border
            {
                Background = CardBg,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 30, 20, 30),
                Child = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        Text("这个检测项还没有数据", 13, Muted),
                        Text("点卡片标题栏右侧的按钮开始检测", 11.5, Muted),
                    },
                },
            });
        }
        else
        {
            foreach (var sec in t.Sections)
            {
                if (sec.IsEmpty) continue;
                host.Children.Add(BuildSection(sec, jump));
            }
        }

        return host;
    }

    private static UIElement BuildSection(Section sec, Action<int> jump)
    {
        var body = new StackPanel();

        // 标题行
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Text(sec.Title, 13, Ink, FontWeights.SemiBold));
        if (sec.Hint.Length > 0)
        {
            var h = Text(sec.Hint, 11.5, Muted);
            h.Margin = new Thickness(10, 1, 0, 0);
            head.Children.Add(h);
        }
        body.Children.Add(head);

        // 结论横幅
        if (sec.Verdict.Length > 0)
        {
            body.Children.Add(new Border
            {
                Background = ToneBg(sec.VerdictTone),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = sec.Verdict,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ToneBrush(sec.VerdictTone),
                    FontFamily = new FontFamily(UiFont),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        if (sec.Bars.Count > 0)
            body.Children.Add(BuildBars(sec.Bars));

        if (sec.Ports.Count > 0)
            body.Children.Add(BuildPorts(sec.Ports));

        if (sec.Adapters.Count > 0)
            body.Children.Add(BuildAdapters(sec.Adapters));

        if (sec.Hops.Count > 0)
            body.Children.Add(BuildHops(sec.Hops));

        if (sec.Kv.Count > 0)
            body.Children.Add(BuildKv(sec.Kv));

        if (sec.Issues.Count > 0)
            body.Children.Add(BuildIssues(sec.Issues, jump));

        if (sec.Notes.Count > 0)
        {
            var notes = new StackPanel { Margin = new Thickness(0, sec.Kv.Count > 0 || sec.Bars.Count > 0 ? 8 : 0, 0, 0) };
            foreach (var n in sec.Notes)
            {
                var tb = new TextBlock
                {
                    Text = n,
                    FontSize = 11.5,
                    Foreground = Muted,
                    FontFamily = new FontFamily(UiFont),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                };
                notes.Children.Add(tb);
            }
            body.Children.Add(notes);
        }

        return new Border
        {
            Background = CardBg,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = body,
        };
    }

    // ==================================================================
    // 键值表
    // ==================================================================

    private static UIElement BuildKv(List<KeyValueRow> rows)
    {
        var panel = new StackPanel();

        foreach (var r in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });

            bool hasNote = r.Note.Length > 0;
            if (hasNote)
            {
                // 值列用 Auto 让所有行的值左端对齐；但 Auto 列不会换行，
                // 超长值（多出口 IP 列表、多个 DNS）会直接溢出被裁掉，所以限个宽。
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            var key = Text(r.Key, 12.5, Muted, font: MonoFont);
            key.TextTrimming = TextTrimming.CharacterEllipsis;
            key.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(key, 0);

            var val = TextWithGeo(r.Value, r.Geo, 12.5, ToneBrush(r.Tone),
                                  r.Tone == Tone.Neutral ? null : FontWeights.SemiBold);
            val.TextWrapping = TextWrapping.Wrap;
            if (hasNote) val.MaxWidth = 430;
            Grid.SetColumn(val, 1);

            grid.Children.Add(key);
            grid.Children.Add(val);

            if (hasNote)
            {
                var note = Text(r.Note, 11.5, Muted);
                note.TextWrapping = TextWrapping.Wrap;
                note.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(note, 2);
                grid.Children.Add(note);
            }

            panel.Children.Add(grid);
        }
        return panel;
    }

    // ==================================================================
    // 条形图
    // ==================================================================

    private static UIElement BuildBars(List<BarRow> rows)
    {
        var panel = new StackPanel();

        foreach (var r in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            // 230 而不是 140：DNS 服务器的名字就是 IP，右边还要跟一段归属地
            // （「114.114.114.114  江苏省南京市」实测要 197 px，200 刚好会把它截成「江苏省南京…」）。
            // 每行是各自独立的 Grid，Auto 列不会跨行对齐，所以只能给个够用的定宽。
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarRow.TrackWidth + 12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = TextWithGeo(r.Name, r.Geo, 12.5, Ink, font: MonoFont);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(name, 0);

            // 轨道
            var track = new Grid { Width = BarRow.TrackWidth, HorizontalAlignment = HorizontalAlignment.Left };
            track.Children.Add(new Border
            {
                Height = 9,
                CornerRadius = new CornerRadius(4.5),
                Background = SoftBg,
            });
            track.Children.Add(new Border
            {
                Height = 9,
                Width = r.BarWidth,
                CornerRadius = new CornerRadius(4.5),
                Background = ToneBrush(r.Tone),
                HorizontalAlignment = HorizontalAlignment.Left,
            });
            Grid.SetColumn(track, 1);

            var val = Text(r.ValueText, 12.5, ToneBrush(r.Tone), FontWeights.SemiBold);
            val.HorizontalAlignment = HorizontalAlignment.Right;
            val.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(val, 2);

            var detail = Text(r.Detail, 11.5, Muted, font: MonoFont);
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(detail, 3);

            grid.Children.Add(name);
            grid.Children.Add(track);
            grid.Children.Add(val);
            grid.Children.Add(detail);

            panel.Children.Add(grid);
        }
        return panel;
    }

    // ==================================================================
    // 端口方块
    // ==================================================================

    private static UIElement BuildPorts(List<PortCell> cells)
    {
        var wrap = new WrapPanel();

        foreach (var c in cells)
        {
            // 端口方块的配色走 PortCell.Tone，不再在这里写死 Good/Faint ——
            // 否则映射层调了色调，界面却纹丝不动。
            var fg = ToneBrush(c.Tone);
            var title = Text(c.Port.ToString(), 14.5, fg, FontWeights.SemiBold, MonoFont);
            var sub = Text(c.Service, 10.5, fg);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(title);
            stack.Children.Add(sub);

            var bd = new Border
            {
                Width = 96,
                Height = 54,
                Background = ToneBg(c.Tone),
                BorderBrush = c.Open ? fg : Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = c.Tooltip,
                Child = stack,
                VerticalAlignment = VerticalAlignment.Center,
            };
            wrap.Children.Add(bd);
        }
        return wrap;
    }

    // ==================================================================
    // 网卡卡片
    // ==================================================================

    private static UIElement BuildAdapters(List<AdapterRow> rows)
    {
        var panel = new StackPanel();

        foreach (var a in rows)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = a.IsUp ? Good : Faint,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            head.Children.Add(dot);
            head.Children.Add(Text(a.Name, 13, Ink, FontWeights.SemiBold));

            var badge = new Border
            {
                Background = a.IsUp ? GoodBg : SoftBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text(a.Status, 11, a.IsUp ? Good : Muted, FontWeights.SemiBold),
            };
            head.Children.Add(badge);

            if (a.Type.Length > 0)
            {
                var tp = Text(a.Type, 11, Muted);
                tp.Margin = new Thickness(8, 1, 0, 0);
                head.Children.Add(tp);
            }

            var body = new StackPanel();
            body.Children.Add(head);

            if (a.Description.Length > 0 && a.Description != a.Name)
            {
                var d = Text(a.Description, 11, Muted);
                d.TextWrapping = TextWrapping.Wrap;
                d.Margin = new Thickness(0, 0, 0, 8);
                body.Children.Add(d);
            }

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            for (int i = 0; i < 4; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddPair(grid, 0, 0, "IPv4", a.IPv4.Length > 0 ? a.IPv4 : "—", a.IPv4.Length > 0 ? Ink : Muted, MonoFont);
            AddPair(grid, 0, 1, "子网掩码", a.Mask.Length > 0 ? a.Mask : "—", Ink, MonoFont);
            AddPair(grid, 0, 2, "网关", a.Gateway.Length > 0 ? a.Gateway : "—", Ink, MonoFont);
            AddPair(grid, 0, 3, "MAC", a.Mac.Length > 0 ? a.Mac : "—", Ink, MonoFont);
            body.Children.Add(grid);

            var grid2 = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            for (int i = 0; i < 3; i++)
                grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddPair(grid2, 0, 0, "DNS", a.Dns, Ink, MonoFont);
            AddPair(grid2, 0, 1, "链路速率", a.Speed, Ink, MonoFont);
            AddPair(grid2, 0, 2, "地址分配", a.Dhcp, Ink, MonoFont);
            body.Children.Add(grid2);

            panel.Children.Add(new Border
            {
                Background = SoftBg,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(13, 11, 13, 11),
                Margin = new Thickness(0, 0, 0, 9),
                Child = body,
            });
        }
        return panel;
    }

    private static void AddPair(Grid g, int row, int col, string key, string value, Brush valueBrush, string font)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        stack.Children.Add(Text(key, 10.5, Muted));
        var v = Text(value, 12.5, valueBrush, font: font);
        v.TextTrimming = TextTrimming.CharacterEllipsis;
        v.Margin = new Thickness(0, 1, 0, 0);
        stack.Children.Add(v);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, col);
        g.Children.Add(stack);
    }

    // ==================================================================
    // 路由跳表
    // ==================================================================

    private static UIElement BuildHops(List<HopRow> rows)
    {
        var panel = new StackPanel();

        panel.Children.Add(new Border
        {
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 5),
            Margin = new Thickness(0, 0, 0, 5),
            Child = HopLine("跳", "地址", "", "延迟", "状态", Muted, FontWeights.SemiBold),
        });

        foreach (var h in rows)
            panel.Children.Add(HopLine(h.Ttl, h.Address, h.Geo, h.MsText, h.Status, ToneBrush(h.Tone)));

        return panel;
    }

    private static UIElement HopLine(string ttl, string addr, string geo, string ms, string status,
                                     Brush brush, FontWeight? weight = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });

        var t1 = Text(ttl, 12, brush, weight, MonoFont);
        Grid.SetColumn(t1, 0);
        var t2 = TextWithGeo(addr, geo, 12, brush, weight, MonoFont);
        t2.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(t2, 1);
        var t3 = Text(ms, 12, brush, weight, MonoFont);
        t3.HorizontalAlignment = HorizontalAlignment.Right;
        t3.Margin = new Thickness(0, 0, 14, 0);
        Grid.SetColumn(t3, 2);
        var t4 = Text(status, 12, brush, weight, MonoFont);
        Grid.SetColumn(t4, 3);

        g.Children.Add(t1);
        g.Children.Add(t2);
        g.Children.Add(t3);
        g.Children.Add(t4);
        return g;
    }

    // ==================================================================
    // 问题条目
    // ==================================================================

    /// <summary>
    /// 问题条目。概览卡片里的这一块是全量问题汇总，每一条都指向一个具体的检测项，
    /// 所以整条要能点 —— 否则用户看到「DNS 解析偏慢」还得自己回想该翻到哪张卡片。
    /// 跳转目标就是本条问题自己的 <see cref="IssueRow.TargetTab"/>。
    /// </summary>
    private static UIElement BuildIssues(List<IssueRow> rows, Action<int> jump)
    {
        var panel = new StackPanel();

        foreach (var r in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Background = ToneBg(r.Tone),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 10, 0),
                Child = Text(r.Level, 11, ToneBrush(r.Tone), FontWeights.SemiBold),
            };
            Grid.SetColumn(badge, 0);
            g.Children.Add(badge);

            var right = new StackPanel();
            var title = new TextBlock
            {
                Text = r.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink,
                FontFamily = new FontFamily(UiFont),
                TextWrapping = TextWrapping.Wrap,
            };
            right.Children.Add(title);

            if (r.Detail.Length > 0)
            {
                var d = new TextBlock
                {
                    Text = r.Detail,
                    FontSize = 11.5,
                    Foreground = Muted,
                    FontFamily = new FontFamily(UiFont),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                };
                right.Children.Add(d);
            }
            Grid.SetColumn(right, 1);
            g.Children.Add(right);

            // 指向自己所在页面的问题不跳（跳了等于没动），所以排掉概览页自己
            if (r.TargetTab >= 0 && r.TargetTab != ReportViewBuilder.OverviewIndex)
            {
                int target = r.TargetTab;
                g.Cursor = Cursors.Hand;
                g.ToolTip = "点击查看这一项的详细数据";
                // 用透明底色的 Border 包一层，鼠标悬停时整行有背景反馈
                var hover = new Border
                {
                    Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(-8, 0, 0, 0),
                    Child = g,
                };
                hover.MouseEnter += (_, _) => hover.Background = SoftBg;
                hover.MouseLeave += (_, _) => hover.Background = Brushes.Transparent;
                hover.MouseLeftButtonUp += (_, e) => { jump(target); e.Handled = true; };
                panel.Children.Add(hover);
            }
            else
            {
                g.Margin = new Thickness(8, 0, 0, 12);
                panel.Children.Add(g);
            }
        }
        return panel;
    }
}
