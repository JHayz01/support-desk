using System.Windows.Interop;
namespace SupportDesk.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        if (args.SequenceEqual(["--apply-update"])) return UpdateInstaller.Apply();
        using var mutex = new Mutex(true, "Local\\CristalixSupportPublic7", out bool first);
        if (!first)
        {
            MessageBox.Show("Cristalix Support уже запущен. Откройте его через значок в трее.");
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show("Не удалось выполнить действие. Попробуйте повторить его.", "Cristalix Support"); e.Handled = true; };
        var local = new LocalStore();
        var vm = new MainViewModel(local);
        var window = new MainWindow(vm);
        app.MainWindow = window;
        if (!args.Contains("--background"))
            window.ShowPanel();
        else
            new WindowInteropHelper(window).EnsureHandle();
        UpdateInstaller.SignalStarted(args);
        _ = Task.Run(() => { try { UpdateInstaller.CleanOldStages(); } catch { } });
        app.Run();
        return 0;
    }
}
