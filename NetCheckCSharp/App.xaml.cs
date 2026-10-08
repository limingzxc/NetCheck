using System.Windows;

namespace NetCheck;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "程序遇到未处理的错误：\n\n" + args.Exception.Message,
                "NetCheck", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
