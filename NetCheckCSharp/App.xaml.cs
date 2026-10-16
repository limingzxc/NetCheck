using System.Windows;
using Microsoft.Win32;
using NetCheck.Core;
using NetCheck.Ui;

namespace NetCheck;

public partial class App : Application
{
    /// <summary>
    /// 启动时读进来的配置。主窗口直接复用这一份，不再自己读第二次 ——
    /// 两边各读一次的话，App 读到的是启动瞬间的磁盘状态，主窗口读到的可能已经是
    /// 另一个进程改过的状态，主题和窗口尺寸就可能来自不同版本的文件。
    /// </summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>命令行参数。主窗口复用这一份，不重复解析。</summary>
    public static CommandLineOptions Cli { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Dialogs.Show(null, "NetCheck",
                "程序遇到未处理的错误：\n\n" + args.Exception.Message,
                DialogButton.Ok, DialogIcon.Error);
            args.Handled = true;
        };

        // 主题必须在主窗口构建之前落到画刷上。
        // StartupUri 会在 OnStartup 返回后才实例化 MainWindow，所以这里改色时
        // 界面上一个元素都还没创建 —— 它们随后取到的就是正确主题的画刷，
        // 不会出现「先渲染成浅色再跳成深色」的一帧闪烁。
        Settings = AppSettings.Load();
        Cli = CommandLineOptions.Parse(Environment.GetCommandLineArgs().Skip(1));
        Cli.ApplyTo(Settings);

        // --theme 只作用于本次运行，不写回配置 —— 用户界面上选好的偏好不该被一条命令行改掉。
        // 做法是：先把配置里的值记下来，用它算完实际主题后立刻还原，
        // 这样后面任何一次 _settings.Save() 落盘的都是原偏好。
        // 界面上的主题勾选读的是 Theme.Mode（实际生效的那个），所以显示不会错位。
        string configured = Settings.Theme;
        if (Cli.Theme != null) Settings.Theme = Cli.Theme;
        Theme.Apply(Theme.FromSetting(Settings.Theme));
        Settings.Theme = configured;

        // 选了「跟随系统」就跟着系统走：用户在 Windows 设置里切了模式，
        // 程序不用重启也应该跟上。
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (Theme.Mode != ThemeMode.System) return;
            Dispatcher.Invoke(() => Theme.Apply(ThemeMode.System));
        };
    }
}
