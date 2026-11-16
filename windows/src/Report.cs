using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace WarpConnect {
    // Mirrors com.warpconnect.app.Report: voluntary telemetry about which
    // endpoint carried a verified connection, on which network. Every failure
    // is swallowed on purpose — this is best-effort, must never break the tunnel.
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

        public static bool IsEnabled { get { return true; } }

        public static bool SubmitReport(OutboundReport id, string endpoint) {
            var body = Payload(id, endpoint, null);
            return Post(URL_STD, body);
        }

        public static bool Heartbeat(OutboundReport id, string endpoint, string deviceId) {
            var body = Payload(id, endpoint, deviceId);
            return Post(URL_HEARTBEAT, body);
        }

        public static bool Bye(string deviceId) {
            if (string.IsNullOrEmpty(deviceId)) return false;
            var ser = new JavaScriptSerializer();
            var d = new System.Collections.Generic.Dictionary<string, string> { { "device", deviceId } };
            return Post(URL_BYE, ser.Serialize(d));
        }

        private static string Payload(OutboundReport id, string endpoint, string deviceId) {
            var ser = new JavaScriptSerializer();
            var d = new System.Collections.Generic.Dictionary<string, string>();
            d["endpoint"] = endpoint;
            d["transport"] = id != null ? (id.Transport ?? "unknown") : "unknown";
            d["ssid"] = id != null ? (id.Ssid ?? "") : "";
            d["carrier"] = id != null ? (id.Carrier ?? "") : "";
            d["mccmnc"] = id != null ? (id.mccMnc ?? "") : "";
            d["isp"] = id != null ? (id.Isp ?? "") : "";
            d["asn"] = id != null ? (id.Asn ?? "") : "";
            d["asn_org"] = id != null ? (id.AsnOrg ?? "") : "";
            d["country"] = id != null ? (id.Country ?? "") : "";
            d["city"] = id != null ? (id.City ?? "") : "";
            if (!string.IsNullOrEmpty(deviceId)) d["device"] = deviceId;
            return ser.Serialize(d);
        }

        private static bool Post(string url, string body) {
            try {
                var bytes = Encoding.UTF8.GetBytes(body);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                // Older .NET defaults — force TLS 1.2+ for Cloudflare.
                try { req.ServicePoint.Expect100Continue = false; } catch { }
                using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse()) {
                    int code = (int)resp.StatusCode;
                    return code >= 200 && code < 300;
                }
            } catch (Exception e) {
                Debug.WriteLine("stats POST failed: " + e.Message);
                return false;
            }
        }
    }
}
