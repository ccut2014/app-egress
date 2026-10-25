using System.Security.Principal;
using System.Windows;
using AppEgress.Config;
using AppEgress.Core;
using AppEgress.Localization;
using AppEgress.UI;
using AppEgress.UI.Theme;
using AppEgress.UI.ViewModels;

namespace AppEgress;

internal static class Program
{
    private const string MutexName = AppIdentity.SingleInstanceMutex;
    private const string ShowEventName = AppIdentity.ShowEvent;

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

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Write(Loc.T("LogUnhandled", e.ExceptionObject));

        // Язык нужен до первого окна: настройки читаются раньше проверки прав (это обычный файл в %AppData%).
        var settings = SettingsStore.Load();
        Loc.SetLanguage(settings.Language);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Logger.Write(Loc.T("LogUnhandledUi", e.Exception));
            e.Handled = true;
        };
        ThemeManager.Initialize(app.Resources);

        if (!IsAdministrator())
        {
            ConfirmWindow.Info(null, Loc.T("AdminRequiredTitle"), Loc.T("AdminRequiredText"));
            return;
        }

        bool minimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        Logger.Write(Loc.T("LogStarted", Loc.Language));

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
        }) { IsBackground = true, Name = "AppEgress show listener" };
        listener.Start();

        if (!minimized)
            window.Show();
        engine.Start();
        app.Run();

        tray.Dispose();
        Logger.Write(Loc.T("LogExited"));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
