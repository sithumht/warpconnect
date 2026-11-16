using System;
using System.IO;
using System.Web.Script.Serialization;

namespace WarpConnect {
    // Persisted in %APPDATA%\WarpConnect\settings.json. The account itself
    // lives at the path pointed to by AccountPath (same format the CLI uses).
    public class Settings {
        public string AccountPath { get; set; }
        public string LastEndpoint { get; set; }
        public string ThemeGlowScale { get; set; }  // reserved for future theming; never shown

        private static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarpConnect"); } }
        private static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static Settings Load() {
            var s = new Settings();
            s.AccountPath = Path.Combine(Dir, "warpscout-account.json");
            s.LastEndpoint = "";
            s.ThemeGlowScale = "1";
            try {
                if (File.Exists(FilePath)) {
                    var raw = File.ReadAllText(FilePath);
                    var ser = new JavaScriptSerializer();
                    var d = ser.Deserialize<System.Collections.Generic.Dictionary<string, object>>(raw);
                    if (d.ContainsKey("accountPath") && d["accountPath"] != null) s.AccountPath = Convert.ToString(d["accountPath"]);
                    if (d.ContainsKey("lastEndpoint") && d["lastEndpoint"] != null) s.LastEndpoint = Convert.ToString(d["lastEndpoint"]);
                }
            } catch (Exception e) {
                // Corrupt settings must never block the app.
                System.Diagnostics.Debug.WriteLine("settings load failed: " + e.Message);
            }
            Directory.CreateDirectory(Dir);
            return s;
        }

        public void Save() {
            try {
                Directory.CreateDirectory(Dir);
                var ser = new JavaScriptSerializer();
                var d = new System.Collections.Generic.Dictionary<string, string>();
                d["accountPath"] = AccountPath;
                d["lastEndpoint"] = LastEndpoint ?? "";
                File.WriteAllText(FilePath, ser.Serialize(d));
            } catch (Exception e) {
                System.Diagnostics.Debug.WriteLine("settings save failed: " + e.Message);
            }
        }

        public static string AppDir { get { return Dir; } }
    }
}
