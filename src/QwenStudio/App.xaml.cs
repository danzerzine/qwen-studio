using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using QwenStudio.Core;

namespace QwenStudio
{
    public partial class App : Application
    {
        // one instance per project folder: a test copy with its own server_config.env can run next to production
        static string Id => "QwenStudio.SingleInstance." + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Paths.Base.ToLowerInvariant())))[..12];
        Mutex mutex;
        EventWaitHandle wake;

        protected override void OnStartup(StartupEventArgs e)
        {
            mutex = new Mutex(true, Id, out bool first);
            if (!first)
            {
                // a copy is already open: bring it to front and quit
                try { EventWaitHandle.OpenExisting(Id + ".wake").Set(); } catch { }
                Shutdown();
                return;
            }
            wake = new EventWaitHandle(false, EventResetMode.AutoReset, Id + ".wake");
            new Thread(() =>
            {
                while (wake.WaitOne())
                    Dispatcher.BeginInvoke(() =>
                    {
                        var w = MainWindow;
                        if (w == null) return;
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate();
                    });
            }) { IsBackground = true }.Start();

            DispatcherUnhandledException += (_, a) =>
            {
                MessageBox.Show(a.Exception.Message, "Qwen Studio", MessageBoxButton.OK, MessageBoxImage.Error);
                a.Handled = true;
            };
            base.OnStartup(e);
            // started by Windows at logon: stay out of the way on the taskbar
            bool auto = e.Args.Contains(Autostart.Flag);
            var w = new MainWindow(auto);
            if (auto) w.WindowState = WindowState.Minimized;
            w.Show();
        }
    }
}
