using System;
using System.IO;
using System.Threading;
using System.Windows;
using HprRadio.Core;
using HprRadio.UI;

namespace HprRadio;

public sealed class App : Application { }

public static class Program
{
    /// <summary>
    /// HprRadio.exe [--config-url URL] [--selftest OUTPUT_DIR]
    ///   --config-url  use another channels.json for this run (testing)
    ///   --selftest    drive the app against the live services, save screenshots and a log, then exit
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        string? configUrl = null, selfTest = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config-url") configUrl = args[i + 1];
            if (args[i] == "--selftest") selfTest = args[i + 1];
        }

        // One copy at a time: starting it again brings the open window to the front.
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, "KG4BHR.HprRadio.Activate", out var first);
        if (!first && selfTest is null)
        {
            activate.Set();
            return 0;
        }

        if (selfTest is not null)
        {
            Directory.CreateDirectory(selfTest);
            Log.FilePath = Path.Combine(selfTest, "live.log");
            File.WriteAllText(Log.FilePath, "");
            Environment.SetEnvironmentVariable("HPR_AUDIO", "dummy");
        }

        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
#pragma warning disable WPF0001
        app.ThemeMode = ThemeMode.System;   // Windows 11 style for scroll bars, menus, expanders
#pragma warning restore WPF0001
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/HprRadio;component/UI/Styles.xaml"),
        });
        if (Environment.GetEnvironmentVariable("HPR_THEME") is { Length: > 0 } forced) Theme.Force(forced == "dark");
        Theme.Apply();
        Theme.Watch();

        var settings = new Settings();
        if (configUrl is not null) settings.OverrideConfigUrl = configUrl;
        Hub hub;
        try { hub = new Hub(settings); }
        catch (Exception e)
        {
            MessageBox.Show("HPR Radio couldn't start:\n\n" + e.Message, "HPR Radio", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        var controls = new MediaControls(hub);
        var window = new MainWindow(hub, controls);
        app.MainWindow = window;
        app.Exit += (_, _) =>
        {
            controls.Dispose();
            hub.Dispose();
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("UNHANDLED " + e.Exception);
            e.Handled = selfTest is null; // keep going for listeners; fail loudly in the self-test
        };

        var waiter = new Thread(() =>
        {
            while (activate.WaitOne())
                app.Dispatcher.InvokeAsync(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                });
        }) { IsBackground = true };
        if (selfTest is null) waiter.Start();

        window.Show();
        hub.Start();
        if (selfTest is not null) SelfTest.Run(app, hub, window, selfTest);
        return app.Run();
    }
}
