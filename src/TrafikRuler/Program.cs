using System.Security.Principal;
using System.Windows;
using TrafikRuler.Config;
using TrafikRuler.Core;
using TrafikRuler.UI;
using TrafikRuler.UI.Theme;
using TrafikRuler.UI.ViewModels;

namespace TrafikRuler;

internal static class Program
{
    private const string MutexName = @"Global\TrafikRuler.SingleInstance";
    private const string ShowEventName = @"Global\TrafikRuler.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            // Уже запущено: просим работающий экземпляр показать окно.
            showEvent.Set();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Write("Необработанная ошибка: " + e.ExceptionObject);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Logger.Write("Необработанная ошибка UI: " + e.Exception);
            e.Handled = true;
        };
        ThemeManager.Initialize(app.Resources);

        if (!IsAdministrator())
        {
            ConfirmWindow.Info(null, "Нужны права администратора",
                "TrafikRuler перехватывает сетевой трафик, а для этого его нужно запускать от имени администратора.");
            return;
        }

        bool minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        Logger.Write("TrafikRuler запущен");

        var settings = SettingsStore.Load();
        using var engine = new RuleEngine(settings);
        using var viewModel = new MainViewModel(engine);
        var window = new MainWindow(viewModel);

        TrayController? tray = null;
        void Exit()
        {
            window.AllowClose = true;
            window.Close();
            tray?.Dispose();
            app.Shutdown();
        }
        tray = new TrayController(engine, window, Exit);

        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
                window.Dispatcher.BeginInvoke(window.ShowAndActivate);
        }) { IsBackground = true, Name = "TrafikRuler show listener" };
        listener.Start();

        if (!minimized)
            window.Show();
        engine.Start();
        app.Run();

        tray.Dispose();
        Logger.Write("TrafikRuler завершён");
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
