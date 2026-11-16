using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace WarpConnect {
    public class OutboundReport {
        public string Transport = "unknown";
        public string Ssid = "";
        public string Carrier = "";
        public string mccMnc = "";
        public string Isp = "";
        public string Asn = "";
        public string AsnOrg = "";
        public string Country = "";
        public string City = "";
    }

    public class PostResult {
        public bool Ok;
        public int Status = 0;
        public string Detail = "";
    }

    public static class Report {
        private const string URL_STD = "https://warpconnect-stats.sithu-mhtvps.workers.dev/report";
        private const string URL_HEARTBEAT = "https://warpconnect-stats.sithu-mhtvps.workers.dev/heartbeat";
        private const string URL_BYE = "https://warpconnect-stats.sithu-mhtvps.workers.dev/bye";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<PostResult> SubmitReport(OutboundReport id, string endpoint) =>
            await Post(URL_STD, Payload(id, endpoint, null));

        public static async Task<PostResult> Heartbeat(OutboundReport id, string endpoint, string deviceId) =>
            await Post(URL_HEARTBEAT, Payload(id, endpoint, deviceId));

        public static async Task<PostResult> Bye(string deviceId) =>
            await Post(URL_BYE, "{\"device\":\"" + Escape(deviceId) + "\"}");

        private static string Payload(OutboundReport id, string endpoint, string? deviceId) {
            var sb = new StringBuilder();
            sb.Append("{\"endpoint\":\"").Append(Escape(endpoint)).Append("\",");
            sb.Append("\"transport\":\"").Append(Escape(id.Transport)).Append("\",");
            sb.Append("\"ssid\":\"").Append(Escape(id.Ssid)).Append("\",");
            sb.Append("\"carrier\":\"").Append(Escape(id.Carrier)).Append("\",");
            sb.Append("\"mccmnc\":\"").Append(Escape(id.mccMnc)).Append("\",");
            sb.Append("\"isp\":\"").Append(Escape(id.Isp)).Append("\",");
            sb.Append("\"asn\":\"").Append(Escape(id.Asn)).Append("\",");
            sb.Append("\"asn_org\":\"").Append(Escape(id.AsnOrg)).Append("\",");
            sb.Append("\"country\":\"").Append(Escape(id.Country)).Append("\",");
            sb.Append("\"city\":\"").Append(Escape(id.City)).Append("\"");
            if (!string.IsNullOrEmpty(deviceId)) sb.Append(",\"device\":\"").Append(Escape(deviceId)).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        private static string Escape(string s) => string.IsNullOrEmpty(s)
            ? ""
            : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static async Task<PostResult> Post(string url, string body) {
            var result = new PostResult();
            var kind = url.Substring(url.LastIndexOf('/') + 1);
            try {
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var resp = await _http.PostAsync(url, content)) {
                    result.Status = (int)resp.StatusCode;
                    try {
                        var text = await resp.Content.ReadAsStringAsync();
                        result.Detail = text.Length > 160 ? text.Substring(0, 160) : text;
                    } catch { }
                    result.Ok = result.Status >= 200 && result.Status < 300;
                }
            } catch (Exception e) {
                var chain = e.GetType().Name + ": " + e.Message;
                var inner = e.InnerException;
                while (inner != null) { chain += " <- " + inner.GetType().Name + ": " + inner.Message; inner = inner.InnerException; }
                result.Detail = chain;
            }
            // Diagnostics — the activity card only shows a line; keep a full trail here.
            try {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warpconnect-stats-trace.log");
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + kind +
                    " status=" + result.Status + " ok=" + result.Ok +
                    " detail=" + (result.Detail ?? "").Replace('\r', ' ').Replace('\n', ' ') + "\n");
            } catch { }
            return result;
        }
    }
}
