using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace WarpConnect {
    public class TunnelInfo {
        public string Ip = "", Asn = "", Org = "", Country = "", City = "";
        public string Latency = "— ms", Speed = "— Mbps";
        public bool Ok;
    }

    public static class Net {
        // Approx. download throughput through the tunnel to Cloudflare's speed endpoint.
        public static double MeasureMbps(int bytes) {
            var url = string.Format("https://speed.cloudflare.com/__down?bytes={0}", bytes);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.AllowAutoRedirect = true;
            var sw = Stopwatch.StartNew();
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream()) {
                var buf = new byte[64 * 1024];
                int total = 0, n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0) total += n;
            }
            sw.Stop();
            double secs = sw.Elapsed.TotalSeconds;
            if (secs <= 0 || bytes <= 0) return 0;
            return (bytes * 8.0) / secs / 1e6;
        }

        public static double PingMs(string host, int timeoutMs) {
            try {
                using (var ping = new Ping()) {
                    var reply = ping.Send(host, timeoutMs);
                    if (reply != null && reply.Status == IPStatus.Success) return reply.RoundtripTime;
                }
            } catch (Exception e) { System.Diagnostics.Debug.WriteLine("ping failed: " + e.Message); }
            return -1;
        }

        public static TunnelInfo FetchTunnelInfo() {
            string json = HttpGet("http://ip-api.com/json?fields=query,as,org,countryCode,city", 8000);
            if (json == null) json = HttpGet("https://ip-api.com/json/?fields=query,as,org,countryCode,city", 8000);
            var info = new TunnelInfo();
            if (json == null) { info.Ok = false; return info; }
            var dict = MiniJson.ParseObject(json);
            if (dict != null) {
                string v;
                if (dict.TryGetValue("query", out v)) info.Ip = v;
                if (dict.TryGetValue("as", out v)) info.Asn = v;
                if (dict.TryGetValue("org", out v)) info.Org = v;
                if (dict.TryGetValue("countryCode", out v)) info.Country = v;
                if (dict.TryGetValue("city", out v)) info.City = v;
                info.Ok = !string.IsNullOrEmpty(info.Ip);
            }
            return info;
        }

        public static string HttpGet(string url, int timeoutMs) {
            try {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "WARPConnect/1.0.1";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var reader = new System.IO.StreamReader(s, Encoding.UTF8)) {
                    return reader.ReadToEnd();
                }
            } catch (Exception e) {
                System.Diagnostics.Debug.WriteLine("http get failed for " + url + ": " + e.Message);
                return null;
            }
        }
    }

    // Tiny JSON reader — avoids System.Web.Extensions when only flat objects are needed.
    public static class MiniJson {
        public static Dictionary<string, string> ParseObject(string json) {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return d;
            int i = 0;
            SkipWs(json, ref i);
            if (i >= json.Length || json[i] != '{') return d;
            i++;
            while (true) {
                SkipWs(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == '}') break;
                if (json[i] != '"') break;
                string key = ReadString(json, ref i);
                SkipWs(json, ref i);
                if (i >= json.Length || json[i] != ':') break;
                i++;
                SkipWs(json, ref i);
                if (i >= json.Length) break;
                string val;
                if (json[i] == '"') val = ReadString(json, ref i);
                else if (json[i] == '{') { val = null; SkipObject(json, ref i); }
                else if (json[i] == '[') { val = null; SkipObject(json, ref i); }
                else { val = ReadPrimitive(json, ref i); }
                if (key != null && val != null) d[key] = val;
                SkipWs(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                break;
            }
            return d;
        }

        private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static string ReadString(string s, ref int i) {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length) {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[i + 1]); i += 2; continue; }
                if (c == '"') { i++; return sb.ToString(); }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        private static void SkipObject(string s, ref int i) {
            // Skips any balanced {...} or [...] block starting at json[i].
            char open = s[i];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            while (i < s.Length) {
                char c = s[i];
                if (c == open) { depth++; i++; continue; }
                if (c == close) { depth--; i++; if (depth == 0) return; continue; }
                if (c == '"') { ReadString(s, ref i); continue; }
                i++;
            }
        }

        private static string ReadPrimitive(string s, ref int i) {
            int start = i;
            while (i < s.Length) {
                char c = s[i];
                if (c == ',' || c == '}' || c == ']') break;
                i++;
            }
            return s.Substring(start, i - start).Trim();
        }
    }
}
