using System;
using System.IO;
using System.Windows;

namespace WarpConnect {
    public partial class App : Application {
        public App() {
            InitializeComponent();
        }

        protected override void OnStartup(StartupEventArgs e) {
            Dispatcher.UnhandledException += delegate(object? s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args) {
                try {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "warpconnect-wpf-crash.log"),
                        DateTime.Now.ToString("HH:mm:ss") + " dispatcher exception:\n" + args.Exception + "\n\n");
                } catch { }
                args.Handled = true; // keep the UI alive
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs args) {
                try {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "warpconnect-wpf-crash.log"),
                        DateTime.Now.ToString("HH:mm:ss") + " domain exception:\n" + args.ExceptionObject + "\n\n");
                } catch { }
            };
            base.OnStartup(e);
            // StartupUri processing silently no-ops on some hosts; create the
            // window explicitly so the app always launches.
            try {
                var w = new MainWindow();
                w.Show();
            } catch (Exception ex) {
                // Surface startup failures instead of "app runs but shows nothing".
                try {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "warpconnect-wpf-crash.log"),
                        ex.ToString());
                } catch { }
                throw;
            }
        }
    }
}
