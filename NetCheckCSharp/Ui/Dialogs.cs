using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetCheck.Ui;

/// <summary>对话框上的按钮组合。</summary>
public enum DialogButton
{
    /// <summary>只有一个「确定」。</summary>
    Ok,
    /// <summary>「确定」+「取消」。</summary>
    OkCancel,
    /// <summary>「是」+「否」。</summary>
    YesNo,
}

/// <summary>对话框的语义图标，决定左侧圆形标记和标题的颜色。</summary>
public enum DialogIcon
{
    None,
    Info,
    Warning,
    Error,
    Question,
}

/// <summary>
/// 统一风格的模态对话框。
///
/// **取代 <see cref="MessageBox"/>**，两个原因：
/// 系统消息框是操作系统画的，不跟随应用主题 —— 深色模式下它还是一块惨白，
/// 和整套深色界面放在一起非常割裂；它的按钮也是 Windows 默认样式，和
/// 全套自绘控件（圆角、主色、悬停反馈）放在一起更显得像两个程序。
///
/// 这个对话框里所有颜色都走 <see cref="Theme"/> 的资源键、按钮直接用
/// App.xaml 里的 <c>PrimaryButton</c> / <c>SoftButton</c> 样式，
/// 所以深浅两套主题下都一样，且切换主题时跟着变。
/// </summary>
public static class Dialogs
{
    /// <summary>
    /// 弹一个模态对话框。
    /// </summary>
    /// <param name="message">正文。多行文本直接用换行，窗口会自动换行并在内容过长时滚动。</param>
    /// <returns>
    /// 点了「确定 / 是」返回 <c>true</c>；点了「取消 / 否」、按 Esc、或直接关窗返回 <c>false</c>。
    /// </returns>
    public static bool Show(Window? owner, string title, string message,
                            DialogButton buttons = DialogButton.Ok,
                            DialogIcon icon = DialogIcon.None)
    {
        var dlg = new DialogWindow(title, message, buttons, icon);
        // 显式设 Owner 才能居中在父窗口上；拿不到就不设（ShowDialog 居中屏幕）
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }

    // ==================================================================
    // 实现
    // ==================================================================

    private sealed class DialogWindow : Window
    {
        public DialogWindow(string title, string message, DialogButton buttons, DialogIcon icon)
        {
            Title = title;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            MinWidth = 372;
            MinHeight = 150;
            MaxWidth = 720;
            MaxHeight = 560;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Background = Brush("BgBrush");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;

            // 高度上限按父窗口的比例算，不写死 560：主窗口被用户拉矮时，
            // 一个比它本身还高的对话框会顶出屏幕，标题栏和按钮都可能跑到屏幕外。
            // （Owner 是在构造之后才设的，所以只能在 Loaded 里取。）
            Loaded += (_, _) =>
            {
                double ownerHeight = Owner?.ActualHeight ?? 0;
                if (ownerHeight <= 0) return;
                MaxHeight = Math.Max(220, Math.Min(660, ownerHeight * 0.82));
            };

            var root = new Grid { Margin = new Thickness(22, 20, 22, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- 正文：左侧语义标记 + 右侧文字 ----
            var text = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = Brush("TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21,
            };

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (icon != DialogIcon.None)
            {
                var mark = new Border
                {
                    Width = 34,
                    Height = 34,
                    CornerRadius = new CornerRadius(17),
                    Background = ToneBrush(icon),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 14, 0),
                    Child = new TextBlock
                    {
                        Text = Glyph(icon),
                        FontSize = 17,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brush("InverseTextBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                Grid.SetColumn(mark, 0);
                body.Children.Add(mark);
            }

            Grid.SetColumn(text, 1);
            body.Children.Add(text);

            // 长正文（命令行参数、使用说明）要能滚动。高度上限交给窗口自己的
            // MaxHeight（按父窗口比例算），这里不再套一层 420 的固定上限。
            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetRow(scroll, 0);
            root.Children.Add(scroll);

            // ---- 按钮栏 ----
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
            };

            foreach (var b in ButtonLabels(buttons))
            {
                var btn = new Button
                {
                    Content = b.Label,
                    Style = (Style)FindResource(b.Primary ? "PrimaryButton" : "SoftButton"),
                    MinWidth = 78,
                    Padding = new Thickness(16, 7, 16, 7),
                    // 样式自带下边距 8，在按钮栏里会把整排撑歪
                    Margin = new Thickness(0, 0, 8, 0),
                    IsDefault = b.Primary,
                    IsCancel = !b.Primary,
                };
                btn.Click += (_, _) => DialogResult = b.Primary;
                footer.Children.Add(btn);
            }

            var footerBox = new Border
            {
                Child = footer,
                Margin = new Thickness(22, 0, 14, 0),
            };
            Grid.SetRow(footerBox, 1);
            root.Children.Add(footerBox);

            Content = root;

            // Esc = 取消。IsCancel 已经能让系统标题栏处理，这里再兜一层键盘
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                DialogResult = false;
                e.Handled = true;
            };
        }

        /// <summary>窗口句柄就绪后才能让系统标题栏跟着主题走。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Theme.ApplyTitleBar(this);
        }

        private static string Glyph(DialogIcon icon) => icon switch
        {
            DialogIcon.Error => "×",
            DialogIcon.Warning => "!",
            DialogIcon.Question => "?",
            _ => "i",
        };

        /// <summary>
        /// 语义色。Error / Warning 是明确的红橙；Info / Question 用主色 ——
        /// 它们只是「告知」和「询问」，远远不到需要警示的程度，用主色就够了。
        /// </summary>
        private static Brush ToneBrush(DialogIcon icon) => icon switch
        {
            DialogIcon.Error => Brush("DangerBrush"),
            DialogIcon.Warning => Brush("WarnBrush"),
            _ => Brush("AccentBrush"),
        };

        /// <summary>按钮从左到右的排列。主操作（推荐的那个）放左边。</summary>
        private static (string Label, bool Primary)[] ButtonLabels(DialogButton b) => b switch
        {
            DialogButton.OkCancel => new[] { ("确定", true), ("取消", false) },
            DialogButton.YesNo => new[] { ("是", true), ("否", false) },
            _ => new[] { ("确定", true) },
        };

        private static Brush Brush(string key)
            => Application.Current?.TryFindResource(key) as Brush
               ?? new SolidColorBrush(Colors.Gray);
    }
}
