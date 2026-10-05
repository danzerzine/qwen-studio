using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>Appends an unhandled exception to logs\studio\crash.log (any thread, never throws).</summary>
        static void Record(Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Paths.Logs, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {ex}{Environment.NewLine}{Environment.NewLine}"); }
            catch { }
        }

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

            AppDomain.CurrentDomain.UnhandledException += (_, a) => Record(a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, a) => { Record(a.Exception); a.SetObserved(); };
            DispatcherUnhandledException += (_, a) =>
            {
                Record(a.Exception);
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
