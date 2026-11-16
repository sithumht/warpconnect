using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WarpConnect {
    public class TunnelInfo {
        public string Ip = "", AsnRaw = "", Org = "", Country = "", City = "";
        public string Asn = "";
        public string AsnOrg = "";
        public string Latency = "— ms", Speed = "— Mbps";
        public bool Ok;

        public static TunnelInfo SplitAs(string org, string countryCode, string city, string ip) {
            var info = new TunnelInfo { Ip = ip, Org = org, Country = countryCode?.ToUpperInvariant() ?? "", City = city ?? "", Ok = !string.IsNullOrEmpty(ip) };
            return info;
        }
    }

    public static class Net {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<TunnelInfo> FetchTunnelInfoAsync() {
            var info = new TunnelInfo { Ok = false };
            try {
                var json = await _http.GetStringAsync("http://ip-api.com/json?fields=query,as,org,countryCode,city");
                using (var doc = JsonDocument.Parse(json)) {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("query", out var ip)) info.Ip = ip.GetString() ?? "";
                    if (root.TryGetProperty("as", out var asn)) info.AsnRaw = asn.GetString() ?? "";
                    if (root.TryGetProperty("org", out var org)) info.Org = org.GetString() ?? "";
                    if (root.TryGetProperty("countryCode", out var cc)) info.Country = cc.GetString() ?? "";
                    if (root.TryGetProperty("city", out var city)) info.City = city.GetString() ?? "";
                }
                if (!string.IsNullOrEmpty(info.AsnRaw)) {
                    var sp = info.AsnRaw.IndexOf(' ');
                    if (sp > 0) { info.Asn = info.AsnRaw.Substring(0, sp); info.AsnOrg = info.AsnRaw.Substring(sp + 1); }
                    else { info.Asn = info.AsnRaw; info.AsnOrg = info.Org; }
                } else { info.AsnOrg = info.Org; }
                info.Ok = !string.IsNullOrEmpty(info.Ip);
            } catch (Exception e) {
                Debug.WriteLine("ip-api lookup failed: " + e.Message);
            }
            return info;
        }

        public static async Task<double> MeasureMbpsAsync(long bytes, CancellationToken cancellationToken = default) {
            var url = string.Format("https://speed.cloudflare.com/__down?bytes={0}", bytes);
            var sw = Stopwatch.StartNew();
            long total = 0;
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)) {
                resp.EnsureSuccessStatusCode();
                using (var stream = await resp.Content.ReadAsStreamAsync(cancellationToken)) {
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = await stream.ReadAsync(buf, 0, buf.Length, cancellationToken)) > 0) total += n;
                }
            }
            sw.Stop();
            var secs = sw.Elapsed.TotalSeconds;
            if (secs <= 0 || total <= 0) return 0;
            return total * 8.0 / secs / 1e6;
        }

        public static int PingMs(string host, int timeoutMs) {
            try {
                using (var ping = new Ping()) {
                    var reply = ping.Send(host, timeoutMs);
                    if (reply != null && reply.Status == IPStatus.Success) return (int)reply.RoundtripTime;
                }
            } catch (Exception e) {
                Debug.WriteLine("ping failed: " + e.Message);
            }
            return -1;
        }
    }
}
