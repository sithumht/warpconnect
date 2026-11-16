using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarpConnect {
    public class Settings {
        public string AccountPath { get; set; } = "";
        public string LastEndpoint { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public bool ShareStats { get; set; } = true;

        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarpConnect");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static Settings Load() {
            var s = new Settings();
            s.AccountPath = Path.Combine(Dir, "warpscout-account.json");
            try {
                if (File.Exists(FilePath)) {
                    var s2 = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(FilePath), new JsonSerializerOptions {
                        PropertyNameCaseInsensitive = true
                    });
                    if (s2 != null) {
                        if (!string.IsNullOrEmpty(s2.AccountPath)) s.AccountPath = s2.AccountPath;
                        if (!string.IsNullOrEmpty(s2.LastEndpoint)) s.LastEndpoint = s2.LastEndpoint;
                        if (!string.IsNullOrEmpty(s2.DeviceId)) s.DeviceId = s2.DeviceId;
                        s.ShareStats = s2.ShareStats.HasValue ? s2.ShareStats.Value : true;
                    }
                }
            } catch (Exception e) {
                System.Diagnostics.Debug.WriteLine("settings load failed: " + e.Message);
            }
            if (string.IsNullOrEmpty(s.DeviceId)) {
                s.DeviceId = Guid.NewGuid().ToString();
                try { Directory.CreateDirectory(Dir); s.Save(); } catch { }
            }
            Directory.CreateDirectory(Dir);
            return s;
        }

        public void Save() {
            try {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new SettingsDto {
                    AccountPath = AccountPath,
                    LastEndpoint = LastEndpoint ?? "",
                    DeviceId = DeviceId ?? "",
                    ShareStats = ShareStats
                }));
            } catch (Exception e) {
                System.Diagnostics.Debug.WriteLine("settings save failed: " + e.Message);
            }
        }

        private class SettingsDto {
            [JsonPropertyName("accountPath")] public string? AccountPath { get; set; }
            [JsonPropertyName("lastEndpoint")] public string? LastEndpoint { get; set; }
            [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
            [JsonPropertyName("shareStats")] public bool? ShareStats { get; set; }
        }
    }
}
