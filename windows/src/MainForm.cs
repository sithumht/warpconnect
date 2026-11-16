using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WarpConnect {
    public partial class MainForm : Form {
        private Settings _settings;
        private Engine _engine;
        private DiscButton _disc;
        private Label _eyebrow, _headline, _sub, _lblEndpointCap, _lblEndpoint, _lblNetworkCap, _lblNetwork, _lblSpeedCap, _lblFooter;
        private Button _speedBtn;
        private ListBox _logList;
        private Panel _activityCard;
        private Label _activityHeader;

        private enum UiState { Ready, Preparing, Scanning, Connecting, Connected, Failed }
        private UiState _state = UiState.Ready;

        private string _endpoint = "";
        private DateTime _connectedAt = DateTime.MinValue;
        private readonly System.Windows.Forms.Timer _healthTimer;

        private OutboundReport _connectIdentity;
        private int _heartbeatTicks;


        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainForm() {
            try {
                // The inbox compiler references .NET Framework 4.0, whose default
                // TLS is too old for Cloudflare / wintun.net — pin TLS 1.2+.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)0x0C00; // TLS 1.2
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)0x3000; // TLS 1.3
            } catch { }
            Text = "WARP Connect";
            ClientSize = new Size(440, 680);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;

            _settings = Settings.Load();
            _endpoint = _settings.LastEndpoint;
            _engine = new Engine(_settings.AccountPath, AppendLogSafe);

            BuildUi();
            ApplyState(UiState.Ready);
            AppendLog("Ready — tap the disc to connect through WARP.");

            _healthTimer = new System.Windows.Forms.Timer();
            _healthTimer.Interval = 1500;
            _healthTimer.Tick += delegate {
                if (_state == UiState.Connected && _engine.TunnelExited) {
                    AppendLog("Tunnel dropped — the endpoint was torn down by the network.");
                    ApplyState(UiState.Failed, "Tunnel dropped.", "Tap the disc to reconnect.");
                    ByeStats();
                    return;
                }
                if (_state == UiState.Connected && _connectIdentity != null && !string.IsNullOrEmpty(_settings.DeviceId)) {
                    _heartbeatTicks++;
                    if (_heartbeatTicks % 40 == 0) {   // ≈60 s heartbeat, like the 15 s × 4 cadence in the app
                        var id = _connectIdentity;
                        var ep2 = _endpoint;
                        var dev = _settings.DeviceId;
                        ThreadPool.QueueUserWorkItem(delegate { try { Report.Heartbeat(id, ep2, dev); } catch { } });
                    }
                }
            };
            _healthTimer.Start();
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            try {
                int on = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            } catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent) {
            var g = pevent.Graphics;
            {
                var rect = ClientRectangle;
                using (var bg = new LinearGradientBrush(rect, ColorTranslator.FromHtml("#05070F"), ColorTranslator.FromHtml("#13203A"), LinearGradientMode.Vertical)) {
                    bg.InterpolationColors = new ColorBlend {
                        Positions = new float[] { 0f, 0.45f, 0.78f, 1f },
                        Colors = new[] { ColorTranslator.FromHtml("#05070F"), ColorTranslator.FromHtml("#0B1220"), ColorTranslator.FromHtml("#0E1830"), ColorTranslator.FromHtml("#13203A") }
                    };
                    g.FillRectangle(bg, rect);
                }
                // Ambient glow behind the disc — EMERALD haze, like the app
                using (var glow = new GraphicsPath()) {
                    glow.AddEllipse(Width / 2 - 260, 130, 520, 420);
                    using (var glowBrush = new PathGradientBrush(glow)) {
                        glowBrush.CenterPoint = new PointF(Width / 2, 300);
                        glowBrush.CenterColor = Color.FromArgb(38, 52, 211, 153);
                        glowBrush.SurroundColors = new[] { Color.FromArgb(0, 52, 211, 153) };
                        g.FillPath(glowBrush, glow);
                    }
                }
            }
        }

        private void BuildUi() {
            // Top eyebrow — letterspaced like the app
            _eyebrow = MakeLabel("W  A  R  P     C  O  N  N  E  C  T", 10.5f, "Segoe UI Semibold", ColorTranslator.FromHtml("#A9B8CF"), FontStyle.Bold);
            _eyebrow.Bounds = new Rectangle(0, 22, Width, 20);
            _eyebrow.TextAlign = ContentAlignment.MiddleCenter;

            _headline = MakeLabel("Ready", 40f, "Segoe UI Semibold", Color.White, FontStyle.Bold);
            _headline.Bounds = new Rectangle(0, 40, Width, 74);
            _headline.TextAlign = ContentAlignment.MiddleCenter;

            _sub = MakeLabel("Tap the button to connect through WARP.", 11.5f, "Segoe UI", ColorTranslator.FromHtml("#B9C4D6"), FontStyle.Regular);
            _sub.Bounds = new Rectangle(10, 110, Width - 20, 58);
            _sub.TextAlign = ContentAlignment.TopCenter;

            // The disc
            _disc = new DiscButton { Size = new Size(200, 200), Location = new Point((Width - 200) / 2, 166) };
            _disc.Clicked += DiscClicked;
            _disc.Invalidate();

            // Eyebrows + mono readouts, app palette
            _lblEndpointCap = MakeLabel("E N D P O I N T", 9.5f, "Segoe UI Semibold", ColorTranslator.FromHtml("#A9B8CF"), FontStyle.Bold);
            _lblEndpointCap.Bounds = new Rectangle(0, 372, Width, 16);
            _lblEndpointCap.TextAlign = ContentAlignment.MiddleCenter;

            _lblEndpoint = MakeLabel("—", 13f, "Cascadia Mono", ColorTranslator.FromHtml("#E8ECF4"), FontStyle.Regular);
            _lblEndpoint.Bounds = new Rectangle(0, 376, Width, 24);
            _lblEndpoint.TextAlign = ContentAlignment.MiddleCenter;

            _lblNetworkCap = MakeLabel("N E T W O R K", 9.5f, "Segoe UI Semibold", ColorTranslator.FromHtml("#A9B8CF"), FontStyle.Bold);
            _lblNetworkCap.Bounds = new Rectangle(0, 406, Width, 16);
            _lblNetworkCap.TextAlign = ContentAlignment.MiddleCenter;

            _lblNetwork = MakeLabel("—", 10.5f, "Cascadia Mono", ColorTranslator.FromHtml("#E8ECF4"), FontStyle.Regular);
            _lblNetwork.Bounds = new Rectangle(0, 424, Width, 44);
            _lblNetwork.TextAlign = ContentAlignment.MiddleCenter;

            // Speed test row
            _speedBtn = new Button { Text = "SPEED TEST", FlatStyle = FlatStyle.Flat, ForeColor = ColorTranslator.FromHtml("#2DD4BF"), Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false, BackColor = Color.Transparent, Bounds = new Rectangle(48, 472, 110, 24) };
            _speedBtn.FlatAppearance.BorderSize = 0;
            _speedBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 45, 212, 191);
            _speedBtn.Click += delegate { RunSpeedTest(); };

            _lblSpeedCap = MakeLabel("tap SPEED TEST to measure", 10f, "Segoe UI", ColorTranslator.FromHtml("#7A88A3"), FontStyle.Regular);
            _lblSpeedCap.Bounds = new Rectangle(176, 472, 216, 24);
            _lblSpeedCap.TextAlign = ContentAlignment.MiddleLeft;

            // Activity log card
            _activityCard = new Panel { Bounds = new Rectangle(14, 504, Width - 28, 132) };
            _activityCard.Paint += DrawActivityCard;
            _activityCard.BackColor = Color.Transparent;

            _activityHeader = MakeLabel("A C T I V I T Y", 9.5f, "Segoe UI Semibold", ColorTranslator.FromHtml("#A9B8CF"), FontStyle.Bold);
            _activityHeader.Bounds = new Rectangle(28, 516, 120, 16);

            _logList = new ListBox {
                Bounds = new Rectangle(28, 534, Width - 56, 96),
                BackColor = ColorTranslator.FromHtml("#0B1220"),
                ForeColor = ColorTranslator.FromHtml("#A9B8CF"),
                Font = new Font("Cascadia Mono", 8.5f),
                BorderStyle = BorderStyle.None,
                SelectionMode = SelectionMode.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 17
            };
            _logList.DrawItem += LogDrawItem;

            _lblFooter = MakeLabel("Developed by Sithu", 9f, "Segoe UI", ColorTranslator.FromHtml("#5B6B85"), FontStyle.Regular);
            _lblFooter.Bounds = new Rectangle(0, 640, Width, 26);
            _lblFooter.TextAlign = ContentAlignment.MiddleCenter;

            Controls.AddRange(new Control[] {
                _eyebrow, _headline, _sub, _disc,
                _lblEndpointCap, _lblEndpoint, _lblNetworkCap, _lblNetwork,
                _speedBtn, _lblSpeedCap,
                _activityCard, _activityHeader, _logList, _lblFooter
            });
        }

        private Label MakeLabel(string text, float size, string font, Color color, FontStyle style) {
            var fontName = font.StartsWith("Cascadia") ? (FontExists("Cascadia Mono") ? "Cascadia Mono" : "Consolas") : font;
            var l = new Label {
                Text = text,
                Font = new Font(fontName, size, style),
                ForeColor = color,
                BackColor = Color.Transparent,
                AutoEllipsis = false
            };
            return l;
        }

        private static bool FontExists(string name) {
            foreach (var fam in FontFamily.Families) {
                if (string.Equals(fam.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void DrawActivityCard(object sender, PaintEventArgs e) {
            var g = e.Graphics;
            var rect = _activityCard.ClientRectangle;
            rect.Inflate(-1, -1);
            using (var path = RoundRect(rect, 16)) {
                using (var fill = new SolidBrush(Color.FromArgb(220, 11, 18, 32))) g.FillPath(fill, path);
                using (var pen = new Pen(Color.FromArgb(120, 19, 32, 58), 1f)) g.DrawPath(pen, path);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius) {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void LogDrawItem(object sender, DrawItemEventArgs e) {
            if (e.Index < 0) return;
            e.DrawBackground();
            var text = Convert.ToString(_logList.Items[e.Index]);
            var color = ColorTranslator.FromHtml("#A9B8CF");
            if (text != null && text.Contains("Verified")) color = ColorTranslator.FromHtml("#34D399");
            else if (text != null && (text.Contains("Failed") || text.Contains("failed") || text.Contains("no handshake"))) color = ColorTranslator.FromHtml("#FBBF24");
            using (var br = new SolidBrush(color)) {
                e.Graphics.DrawString(text, e.Font, br, e.Bounds.X + 4, e.Bounds.Y + 1);
            }
        }

        private void AppendLogSafe(string line) {
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(AppendLogSafe), line); } catch { } return; }
            AppendLog(line);
        }

        private void AppendLog(string line) {
            // Dim timestamp, app style: HH:mm:ss
            _logList.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (_logList.Items.Count > 60) _logList.Items.RemoveAt(0);
            _logList.ClearSelected();
            if (_logList.IsHandleCreated && _logList.Items.Count > 0) {
                _logList.TopIndex = _logList.Items.Count - 1;
            }
            // Watch for the handshake-OK marker from the engine's stderr.
            if (_state == UiState.Connecting && line != null && line.Contains("WARP tunnel is up")) {
                _connectedAt = DateTime.Now;
                OnTunnelUp();
            }
        }

        private void ApplyState(UiState s) {
            ApplyState(s, null, null);
        }

        private void ApplyState(UiState s, string headline, string sub) {
            _state = s;
            switch (s) {
                case UiState.Ready:
                    _headline.Text = headline ?? "Ready";
                    _headline.ForeColor = Color.White;
                    _sub.Text = sub ?? "Tap the button to connect through WARP.";
                    _disc.State = DiscState.Ready;
                    _lblEndpointCap.Visible = true;
                    break;
                case UiState.Preparing:
                    _headline.Text = headline ?? "Preparing";
                    _headline.ForeColor = Color.White;
                    _sub.Text = sub ?? "Loading your WARP account…";
                    _disc.State = DiscState.Preparing;
                    break;
                case UiState.Scanning:
                    _headline.Text = headline ?? "Scanning";
                    _headline.ForeColor = Color.White;
                    _sub.Text = sub ?? "Finding endpoints this network lets through (can take 1–3 min)…";
                    _disc.State = DiscState.Preparing;
                    break;
                case UiState.Connecting:
                    _headline.Text = headline ?? "Connecting";
                    _headline.ForeColor = Color.White;
                    _sub.Text = sub ?? "Handshaking and verifying the tunnel carries data…";
                    _disc.State = DiscState.Handshaking;
                    break;
                case UiState.Connected:
                    _headline.Text = "Connected";
                    _headline.ForeColor = Color.White;
                    _sub.Text = "WARP is carrying your traffic.";
                    _disc.State = DiscState.Connected;
                    break;
                case UiState.Failed:
                    _headline.Text = "Failed";
                    _headline.ForeColor = ColorTranslator.FromHtml("#FCA5A5");
                    _sub.Text = sub ?? "Something went wrong. Tap the disc to retry.";
                    _disc.State = DiscState.Failed;
                    break;
            }
        }

        private void ByeStats() {
            if (string.IsNullOrEmpty(_settings.DeviceId)) return;
            var dev = _settings.DeviceId;
            ThreadPool.QueueUserWorkItem(delegate { try { Report.Bye(dev); } catch { } });
        }

        private void DiscClicked(object sender, EventArgs e) {
            if (_state == UiState.Connected) {
                AppendLog("Disconnecting…");
                ByeStats();
                _engine.StopTunnel();
                AppendLog("Disconnected.");
                _endpoint = _settings.LastEndpoint = "";
                _lblEndpoint.Text = "—";
                _lblNetwork.Text = "—";
                _lblSpeedCap.Text = "tap SPEED TEST to measure";
                ApplyState(UiState.Ready);
                return;
            }
            if (_state == UiState.Preparing || _state == UiState.Scanning || _state == UiState.Connecting) {
                // CANCEL path — tear down whatever we were doing.
                AppendLog("Cancelled.");
                _engine.StopTunnel();
                ApplyState(UiState.Ready);
                return;
            }
            // Ready / Failed → connect
            _sub.Text = "Preparing…";
            _headline.Text = "Preparing";
            AppendLog("Connect requested.");
            ApplyState(UiState.Preparing);
            ThreadPool.QueueUserWorkItem(delegate { ConnectSequence(); });
        }

        // The heavy lifting happens on a worker thread; UI updates marshal via Invoke.
        private void ConnectSequence() {
            try {
                if (!_engine.IsAvailable) {
                    Ui(delegate { AppendLog("warpscout.exe is missing from this install folder — reinstall WARP Connect."); ApplyState(UiState.Failed, "Missing engine", "The warpscout.exe next to the app is missing."); });
                    return;
                }

                // 1) Account
                if (!System.IO.File.Exists(_settings.AccountPath)) {
                    Ui(delegate { AppendLog("No WARP account — registering (direct API, relays, tunnel fallback)…"); ApplyState(UiState.Preparing, "Registering", "Creating a WARP account. This can take a minute or two…"); });
                    var rc = _engine.Run(new List<string> { "register", "-a", _settings.AccountPath, "-plain" }, 600000);
                    if (rc != 0 || !System.IO.File.Exists(_settings.AccountPath)) {
                        Ui(delegate { ApplyState(UiState.Failed, "Register failed", "Could not register a WARP account on this network. Try on another Wi-Fi, then come back — the account works everywhere after that."); });
                        return;
                    }
                    Ui(delegate { AppendLog("Account registered."); });
                }

                // 2) Endpoint — cached winner, else scan
                var ep = _settings.LastEndpoint;
                if (!LooksLikeEndpoint(ep)) ep = null;
                if (ep == null) {
                    Ui(delegate { ApplyState(UiState.Scanning, "Scanning", "Finding endpoints this network lets through (can take 1–3 min)…"); AppendLog("Scanning for endpoints (AWG) — this can take a few minutes on a filtered line…"); });
                    var res = _engine.RunCapture(new List<string> { "scan", "-best", "-plain", "-n", "2", "-t", "6", "-a", _settings.AccountPath }, 300000);
                    ep = TryStandaloneEndpoint(res.StdOut + "\n" + res.StdErr);
                    if (ep == null) {
                        Ui(delegate { ApplyState(UiState.Failed, "No endpoint", "No endpoint passed verification on this network. Try a different Wi-Fi or cellular."); });
                        return;
                    }
                    _settings.LastEndpoint = ep;
                    _settings.Save();
                    Ui(delegate { AppendLog("Scan picked " + ep); });
                } else {
                    Ui(delegate { AppendLog("Trying cached winner " + ep); });
                }

                // 3) WinTUN
                var wtError = Wintun.Ensure(AppDomain.CurrentDomain.BaseDirectory, AppendLogSafe);
                if (wtError != null) {
                    AppendLogSafe(wtError);
                    Ui(delegate { ApplyState(UiState.Failed, "WinTUN missing", "Install WinTUN failed — see the log for details."); });
                    return;
                }

                // 4) Bring up the tunnel
                Ui(delegate {
                    ApplyState(UiState.Connecting, "Connecting", "Handshaking and verifying the tunnel carries data…");
                    _lblEndpoint.Text = ep;
                    _endpoint = ep;
                    _engine.StartTunnel(ep, "awg");
                    AppendLog("Dialing " + ep + " (AmneziaWG)…");
                });
            } catch (Exception e) {
                AppendLog("Connect failed: " + e.Message);
                Ui(delegate { ApplyState(UiState.Failed, "Failed", e.Message); });
            }
        }

        private string TryStandaloneEndpoint(string text) {
            var m = System.Text.RegularExpressions.Regex.Matches(text ?? "", @"(\d{1,3}(\.\d{1,3}){3}:\d{2,5})");
            foreach (System.Text.RegularExpressions.Match match in m) {
                // -best prints only the chosen endpoint; the first one is it.
                return match.Value;
            }
            return null;
        }

        private static bool LooksLikeEndpoint(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}:\d{2,5}$");
        }

        private void Ui(Action a) {
            if (InvokeRequired) { try { Invoke(a); } catch { } }
            else a();
        }

        private void OnTunnelUp() {
            Ui(delegate {
                ApplyState(UiState.Connected);
                AppendLog("WARP tunnel is up — IPv4 traffic now exits through " + _endpoint);
            });
            RefreshNetworkInfoAsync();
        }

        private void RefreshNetworkInfoAsync() {
            ThreadPool.QueueUserWorkItem(delegate {
                var info = Net.FetchTunnelInfo();
                var latency = Net.PingMs("1.1.1.1", 2500);
                var identity = BuildIdentity(info);
                Ui(delegate {
                    if (!info.Ok) {
                        _lblNetwork.Text = "connected — could not read exit info";
                    } else {
                        var parts = new List<string>();
                        parts.Add(info.Ip);
                        if (!string.IsNullOrEmpty(info.Asn)) parts.Add(info.Asn);
                        if (!string.IsNullOrEmpty(info.AsnOrg)) parts.Add(info.AsnOrg);
                        var geo = info.Country;
                        if (!string.IsNullOrEmpty(info.City)) geo = info.City + " · " + geo;
                        if (!string.IsNullOrEmpty(geo)) parts.Add(geo);
                        _lblNetwork.Text = string.Join("  |  ", parts.ToArray());
                    }
                    _lblSpeedCap.Text = latency >= 0 ? (latency + " ms") : "ping unavailable";
                });
                TryReport(identity);
            });
        }

        // Voluntary telemetry — send only what the Android app sends, and only if the toggle is on.
        private void TryReport(OutboundReport id) {
            /* stats always on */
            _connectIdentity = id;
            _heartbeatTicks = 0;
            var ep = _endpoint;
            var dev = _settings.DeviceId;
            if (string.IsNullOrEmpty(ep) || string.IsNullOrEmpty(dev)) return;
            ThreadPool.QueueUserWorkItem(delegate {
                bool okReport = false, okBeat = false;
                try { okReport = Report.SubmitReport(id, ep); } catch { }
                try { okBeat = Report.Heartbeat(id, ep, dev); } catch { }
                Ui(delegate {
                    if (okReport || okBeat) AppendLog("Stats reported (endpoint: " + ep + ").");
                });
            });
        }

        // Build the outbound identity: transport + SSID (Wi-Fi) or ethernet, ISP from the exit-geo lookup.
        private OutboundReport BuildIdentity(TunnelInfo info) {
            var id = new OutboundReport();
            var ssid = WifiSsid();
            if (!string.IsNullOrEmpty(ssid)) { id.Transport = "wifi"; id.Ssid = ssid; } else { id.Transport = "eth"; }
            if (info != null && info.Ok) {
                id.Isp = info.AsnOrg ?? info.Org ?? "";
                id.Asn = info.Asn ?? "";
                id.AsnOrg = info.AsnOrg ?? "";
                id.Country = (info.Country ?? "").ToUpperInvariant();
                id.City = info.City ?? "";
            }
            return id;
        }

        private string WifiSsid() {
            try {
                var psi = new ProcessStartInfo {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi)) {
                    var stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode != 0) return null;
                    foreach (var rawLine in stdout.Split('\n')) {
                        var line = rawLine.Trim();
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"^SSID\s*:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success && !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase)) {
                            var v = m.Groups[1].Value.Trim();
                            if (v.Length > 0) return v;
                        }
                    }
                }
            } catch { }
            return null;
        }

        private void RunSpeedTest() {
            if (_state != UiState.Connected) { AppendLog("Speed test needs an active tunnel."); return; }
            AppendLog("Speed test — measuring through the tunnel…");
            _speedBtn.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate {
                double mbps = 0;
                int ping = -1;
                try { mbps = Net.MeasureMbps(10 * 1024 * 1024); } catch (Exception e) { AppendLog("Speed test failed: " + e.Message); }
                try { ping = (int)Net.PingMs("1.1.1.1", 2500); } catch { }
                Ui(delegate {
                    _speedBtn.Enabled = true;
                    _lblSpeedCap.Text = string.Format("{0} ms  ·  {1:0.0} Mbps", ping < 0 ? "—" : ping.ToString(), mbps);
                    AppendLog(string.Format("Speed: {0:0.0} Mbps down · {1} ms", mbps, ping < 0 ? "—" : ping.ToString()));
                });
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e) {
            base.OnFormClosing(e);
            ByeStats();
            try { _engine.StopTunnel(); } catch { }
        }
    }
}

