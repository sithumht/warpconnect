using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace WarpConnect {
    public static class Wintun {
        private const string URL = "https://www.wintun.net/builds/wintun-0.14.1.zip";
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public static bool IsInstalled(string baseDir) => File.Exists(Path.Combine(baseDir, "wintun.dll"));

        public static string? Ensure(string baseDir, Action<string> log) {
            if (IsInstalled(baseDir)) return null;
            try {
                log("Downloading WinTUN driver (0.14.1, ~600 KB) from wintun.net…");
                var tmp = Path.Combine(Path.GetTempPath(), "wintun-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var stream = _http.GetStreamAsync(URL).GetAwaiter().GetResult())
                using (var fs = File.Create(tmp)) stream.CopyTo(fs);
                log("Extracting wintun.dll (amd64)…");
                using (var zip = ZipFile.OpenRead(tmp)) {
                    int copied = 0;
                    foreach (var entry in zip.Entries) {
                        if (entry.FullName.Replace('\\', '/').EndsWith("bin/amd64/wintun.dll", StringComparison.OrdinalIgnoreCase)) {
                            entry.ExtractToFile(Path.Combine(baseDir, "wintun.dll"), true);
                            copied++;
                        }
                    }
                    File.Delete(tmp);
                    if (copied != 1) return "wintun.dll not found in the downloaded archive layout";
                }
                log("WinTUN installed (GPLv2, WireGuard LLC).");
                return null;
            } catch (Exception e) {
                return "Could not install WinTUN: " + e.Message + " — download https://www.wintun.net/builds/wintun-0.14.1.zip manually and place bin\\amd64\\wintun.dll next to warpscout.exe";
            }
        }
    }
}
