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

    public static class Report {
        private const string URL_STD = "https://warpconnect-stats.sithu-mhtvps.workers.dev/report";
        private const string URL_HEARTBEAT = "https://warpconnect-stats.sithu-mhtvps.workers.dev/heartbeat";
        private const string URL_BYE = "https://warpconnect-stats.sithu-mhtvps.workers.dev/bye";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<bool> SubmitReport(OutboundReport id, string endpoint) =>
            await Post(URL_STD, Payload(id, endpoint, null));

        public static async Task<bool> Heartbeat(OutboundReport id, string endpoint, string deviceId) =>
            await Post(URL_HEARTBEAT, Payload(id, endpoint, deviceId));

        public static async Task<bool> Bye(string deviceId) {
            if (string.IsNullOrEmpty(deviceId)) return false;
            return await Post(URL_BYE, "{\"device\":\"" + Escape(deviceId) + "\"}");
        }

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

        private static async Task<bool> Post(string url, string body) {
            try {
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var resp = await _http.PostAsync(url, content)) {
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                }
            } catch {
                return false;
            }
        }
    }
}
