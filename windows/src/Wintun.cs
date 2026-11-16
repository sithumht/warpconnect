using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace WarpConnect {
    // WinTUN is a signed driver needed by the Go engine to create the tunnel
    // adapter. It is not bundled (GPL); we fetch the official build from
    // wintun.net on first run and drop it next to warpscout.exe.
    public static class Wintun {
        private const string URL = "https://www.wintun.net/builds/wintun-0.14.1.zip";

        public static bool IsInstalled(string baseDir) {
            return File.Exists(Path.Combine(baseDir, "wintun.dll"));
        }

        // Returns null on success, an error message otherwise.
        public static string Ensure(string baseDir, Action<string> log) {
            if (IsInstalled(baseDir)) return null;
            try {
                log("Downloading WinTUN driver (0.14.1, ~600 KB) from wintun.net…");
                var tmp = Path.Combine(Path.GetTempPath(), "wintun-" + Guid.NewGuid().ToString("N") + ".zip");
                var req = (HttpWebRequest)WebRequest.Create(URL);
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.UserAgent = "WARPConnect/1.0.1";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var fs = File.Create(tmp)) {
                    s.CopyTo(fs);
                }
                log("Extracting wintun.dll (amd64)…");
                using (var zip = ZipFile.OpenRead(tmp)) {
                    // Official layout: wintun/bin/amd64/wintun.dll
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
