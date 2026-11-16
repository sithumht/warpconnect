using System;
using System.IO;
using System.Windows;

namespace WarpConnect {
    public partial class App : Application {
        public App() {
            InitializeComponent();
        }

        protected override void OnStartup(StartupEventArgs e) {
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
