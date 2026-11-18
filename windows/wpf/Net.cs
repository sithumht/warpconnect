using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text;
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
    }

    public static class Net {
        private static readonly HttpClient _http = new HttpClient {
            Timeout = TimeSpan.FromSeconds(6)
        };

        // The exit IP/org/ASN, verified during the speed test — HTTPS-first
        // chain so it works on networks stripping plain HTTP.
        public static async Task<TunnelInfo> FetchTunnelInfoAsync() {
            var info = new TunnelInfo { Ok = false };

            if (await FetchFromIpWhoIsAsync(info)) return info;
            if (await FetchFromIfconfigCoAsync(info)) return info;

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
                Debug.WriteLine("ip-api.com failed: " + e.Message);
            }
            return info;
        }

        private static async Task<bool> FetchFromIpWhoIsAsync(TunnelInfo info) {
            try {
                var json = await _http.GetStringAsync("https://ipwho.is/");
                using (var doc = JsonDocument.Parse(json)) {
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return false;
                    if (!r.TryGetProperty("ip", out var ipProp)) return false;
                    var ip = ipProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(ip)) return false;
                    info.Ip = ip;
                    info.Country = r.TryGetProperty("country_code", out var cc) ? (cc.GetString() ?? "") : "";
                    info.City = r.TryGetProperty("city", out var ct) ? (ct.GetString() ?? "") : "";
                    if (r.TryGetProperty("connection", out var conn)) {
                        var asnNum = 0;
                        if (conn.TryGetProperty("asn", out var a) && a.ValueKind == JsonValueKind.Number) a.TryGetInt32(out asnNum);
                        info.Asn = asnNum > 0 ? "AS" + asnNum : "";
                        info.AsnOrg = conn.TryGetProperty("org", out var o) ? (o.GetString() ?? "") :
                                       conn.TryGetProperty("isp", out var isp) ? (isp.GetString() ?? "") : "";
                        info.Org = info.AsnOrg;
                        info.AsnRaw = string.IsNullOrEmpty(info.Asn) ? info.AsnOrg : (info.Asn + " " + info.AsnOrg);
                    }
                    info.Ok = !string.IsNullOrEmpty(info.Ip);
                    return info.Ok;
                }
            } catch (Exception e) {
                Debug.WriteLine("ipwho.is failed: " + e.Message);
                return false;
            }
        }

        private static async Task<bool> FetchFromIfconfigCoAsync(TunnelInfo info) {
            try {
                var json = await _http.GetStringAsync("https://ifconfig.co/json");
                using (var doc = JsonDocument.Parse(json)) {
                    var r = doc.RootElement;
                    var ip = r.TryGetProperty("ip", out var i) ? (i.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(ip)) return false;
                    info.Ip = ip;
                    info.Country = r.TryGetProperty("country_iso", out var cc) ? (cc.GetString() ?? "") :
                                    (r.TryGetProperty("country", out var n) ? (n.GetString() ?? "") : "");
                    info.Asn = r.TryGetProperty("asn", out var a) ? (a.GetString() ?? "") : "";
                    info.AsnOrg = r.TryGetProperty("asn_org", out var o) ? (o.GetString() ?? "") : "";
                    info.Org = info.AsnOrg;
                    info.AsnRaw = string.IsNullOrEmpty(info.Asn) ? info.AsnOrg : (info.Asn + " " + info.AsnOrg);
                    info.City = "";
                    info.Ok = true;
                    return true;
                }
            } catch (Exception e) {
                Debug.WriteLine("ifconfig.co failed: " + e.Message);
                return false;
            }
        }

        // Ask the stats board which endpoint has carried traffic on this
        // line before (matched by ASN, falling back to the ISP string).
        // Mirrors the Android fast path. Any failure is non-fatal — null.
        public static async Task<string?> FetchStatsRecommendationAsync(string transport, string? asn, string? isp) {
            try {
                string? q = null;
                if (!string.IsNullOrEmpty(asn)) q = "asn=" + Uri.EscapeDataString(asn);
                else if (!string.IsNullOrEmpty(isp)) q = "isp=" + Uri.EscapeDataString(isp);
                if (q == null) return null;
                var json = await _http.GetStringAsync("https://warpconnect-stats.sithu-mhtvps.workers.dev/recommended?transport=" + transport + "&" + q);
                using (var doc = JsonDocument.Parse(json)) {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("endpoint", out var epProp)) return null;
                    var ep = epProp.ValueKind == JsonValueKind.String ? epProp.GetString() : null;
                    if (string.IsNullOrEmpty(ep)) return null;
                    return System.Text.RegularExpressions.Regex.IsMatch(ep, "^\\d{1,3}(\\.\\d{1,3}){3}:\\d{1,5}$") ? ep : null;
                }
            } catch (Exception e) {
                Debug.WriteLine("recommendation fetch failed: " + e.Message);
                return null;
            }
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
