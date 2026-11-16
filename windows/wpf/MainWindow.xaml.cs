using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WarpConnect {
    public partial class MainWindow : Window {
        public enum UiState { Ready, Preparing, Scanning, Connecting, Connected, Failed }

        private readonly Settings _settings;
        private readonly Engine _engine;
        private UiState _state = UiState.Ready;
        private int _heartbeatTicks;
        private bool _sequenceRunning;
        private OutboundReport? _connectIdentity;

        public MainWindow() {
            try {
                _settings = Settings.Load();
                _engine = new Engine(_settings.AccountPath, AppendLogSafe);
                InitializeComponent();
                LogList.ItemsSource = _log;
                Visibility = Visibility.Visible;
                ApplyState(UiState.Ready);
                UpdateStatsToggleUi();
                AppendLog("Ready — tap the disc to connect through WARP.");

                Closing += MainWindow_Closing;
                Application.Current.Exit += delegate { try { _engine.StopTunnel(); } catch { } };

                var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                watchdog.Tick += delegate {
                    if (_state == UiState.Connected && _engine.TunnelExited) {
                        AppendLog("Tunnel dropped — the endpoint was torn down by the network.");
                        ApplyState(UiState.Failed);
                        ByeStats();
                        return;
                    }
                    if (_state == UiState.Connected && _settings.ShareStats && !string.IsNullOrEmpty(_settings.DeviceId)) {
                        _heartbeatTicks++;
                        if (_heartbeatTicks % 40 == 0) { // ≈60s heartbeat
                            var id = _connectIdentity ?? new OutboundReport { Transport = "eth" };
                            var ep = _settings.LastEndpoint ?? "";
                            var dev = _settings.DeviceId;
                            _ = Report.Heartbeat(id, ep, dev);
                        }
                    }
                };
                watchdog.Start();
            } catch (Exception ex) {
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warpconnect-wpf-crash.log"), ex.ToString());
                throw;
            }
        }

        // ---------- Window chrome ----------

        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { /* intentionally empty; drag is title-bar only */ }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
            try { DragMove(); } catch { }
        }

        private void ToggleMaximize() {
            WindowState = WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) {
            Close();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e) {
            ByeStats();
            try { _engine.StopTunnel(); } catch { }
        }

        // ---------- Stats toggle ----------

        private void StatsToggle_Click(object sender, RoutedEventArgs e) {
            _settings.ShareStats = !_settings.ShareStats;
            try { _settings.Save(); } catch { }
            UpdateStatsToggleUi();
            AppendLog(_settings.ShareStats
                ? "Stats reporting ON — send endpoint + network with each verified connect."
                : "Stats reporting OFF.");
        }

        private void UpdateStatsToggleUi() {
            BtnStatsToggle.Content = _settings.ShareStats ? "SHARE STATS  ·  ON" : "SHARE STATS  ·  OFF";
            BtnStatsToggle.Foreground = _settings.ShareStats ? new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)) : new SolidColorBrush(Color.FromRgb(0x5E, 0x6B, 0x85));
            BtnStatsToggle.BorderBrush = _settings.ShareStats ? new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)) : new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x55));
        }

        // ---------- Disc ----------

        private void Disc_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) { }
        private void Disc_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            ClickDisc();
        }

        private static void Trace(string line) {
            try {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "warpconnect-trace.log");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\n");
            } catch { }
        }

        private void ClickDisc() {
            Trace("ClickDisc entered; state=" + _state);
            try {
                switch (_state) {
                case UiState.Connected:
                    AppendLog("Disconnecting…");
                    ByeStats();
                    _engine.StopTunnel();
                    AppendLog("Disconnected.");
                    _settings.LastEndpoint = "";
                    _settings.Save();
                    TxtEndpoint.Text = "—";
                    TxtNetwork.Text = "—";
                    TxtSpeed.Text = "tap SPEED TEST to measure";
                    ApplyState(UiState.Ready);
                    return;

                case UiState.Preparing:
                case UiState.Scanning:
                case UiState.Connecting:
                    AppendLog("Cancelled.");
                    _engine.StopTunnel();
                    ApplyState(UiState.Ready);
                    return;

                default: // Ready / Failed
                    if (_sequenceRunning) { AppendLogSafe("Connect already in progress — wait for it to finish."); return; }
                    _sequenceRunning = true;
                    AppendLog("Connect requested.");
                    ApplyState(UiState.Preparing);
                    Trace("state=Preparing applied; starting sequence");
                    StartConnectSequence();
                    return;
            }
            } catch (Exception ex) {
                Trace("ClickDisc EXCEPTION: " + ex);
                throw;
            }
        }

        private void StartConnectSequence() {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { ConnectSequence(); });
        }

        private void ApplyState(UiState s) {
            _state = s;
            Color headline;
            Color sub;
            string headlineText;
            string subText;
            string discCaption;
            string discSub;
            Brush discGlow;
            Brush discFill;
            Brush discRing;

            switch (s) {
                case UiState.Ready:
                    headlineText = "Ready";
                    subText = "Tap the button to connect through WARP.";
                    discCaption = "CONNECT";
                    discSub = "";
                    headline = Color.FromRgb(0xF2, 0xF7, 0xFF);
                    sub = Color.FromRgb(0xA9, 0xB8, 0xCF);
                    discGlow = MakeBlueGlow();
                    discFill = MakeBlueFill();
                    discRing = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                    break;
                case UiState.Preparing:
                    headlineText = "Preparing";
                    subText = "Loading your WARP account…";
                    discCaption = "CANCEL";
                    discSub = "working…";
                    headline = Color.FromRgb(0xF2, 0xF7, 0xFF);
                    sub = Color.FromRgb(0xA9, 0xB8, 0xCF);
                    discGlow = MakeAmberGlow();
                    discFill = MakeAmberFill();
                    discRing = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                    break;
                case UiState.Scanning:
                    headlineText = "Scanning";
                    subText = "Finding endpoints this network lets through (can take 1–3 min)…";
                    discCaption = "CANCEL";
                    discSub = "verifying…";
                    headline = Color.FromRgb(0xF2, 0xF7, 0xFF);
                    sub = Color.FromRgb(0xA9, 0xB8, 0xCF);
                    discGlow = MakeAmberGlow();
                    discFill = MakeAmberFill();
                    discRing = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                    break;
                case UiState.Connecting:
                    headlineText = "Connecting";
                    subText = "Handshaking and verifying the tunnel carries data…";
                    discCaption = "CANCEL";
                    discSub = "verifying…";
                    headline = Color.FromRgb(0xF2, 0xF7, 0xFF);
                    sub = Color.FromRgb(0xA9, 0xB8, 0xCF);
                    discGlow = MakeAmberGlow();
                    discFill = MakeAmberFill();
                    discRing = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                    break;
                case UiState.Connected:
                    headlineText = "Connected";
                    subText = "WARP is carrying your traffic.";
                    discCaption = "DISCONNECT";
                    discSub = "";
                    headline = Color.FromRgb(0xF2, 0xF7, 0xFF);
                    sub = Color.FromRgb(0xA9, 0xB8, 0xCF);
                    discGlow = MakeGreenGlow();
                    discFill = MakeGreenFill();
                    discRing = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                    break;
                case UiState.Failed:
                    headlineText = "Failed";
                    subText = "Something went wrong. Tap the disc to retry.";
                    discCaption = "RETRY";
                    discSub = "failed";
                    headline = Color.FromRgb(0xFC, 0xA5, 0xA5);
                    sub = Color.FromRgb(0xA9, 0xB8, 0xCF);
                    discGlow = MakeRedGlow();
                    discFill = MakeRedFill();
                    discRing = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                    break;
                default:
                    headlineText = "Ready";
                    subText = "Tap the button to connect through WARP.";
                    discCaption = "CONNECT";
                    discSub = "";
                    headline = Color.FromRgb(0xF2, 0xF7, 0xFF);
                    sub = Color.FromRgb(0xA9, 0xB8, 0xCF);
                    discGlow = MakeBlueGlow();
                    discFill = MakeBlueFill();
                    discRing = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                    break;
            }

            TxtHeadline.Text = headlineText;
            TxtHeadline.Foreground = new SolidColorBrush(headline);
            TxtSub.Text = subText;
            TxtSub.Foreground = new SolidColorBrush(sub);
            TxtDiscCaption.Text = discCaption;
            TxtDiscSub.Text = discSub;
            DiscGlow.Fill = discGlow;
            DiscInner.Visibility = s == UiState.Ready ? Visibility.Visible : Visibility.Collapsed;
            DiscFill.Fill = discFill;
            DiscRing.Stroke = discRing;
            DiscRing.Opacity = 0.6;
        }

        private RadialGradientBrush MakeBlueFill() => new RadialGradientBrush {
            GradientOrigin = new Point(0.38, 0.34),
            Center = new Point(0.38, 0.34),
            RadiusX = 0.78,
            RadiusY = 0.78,
            GradientStops = {
                new GradientStop(Color.FromRgb(0x8F, 0xE1, 0xFF), 0),
                new GradientStop(Color.FromRgb(0x4C, 0xCF, 0xFF), 0.25),
                new GradientStop(Color.FromRgb(0x0E, 0xA5, 0xE9), 0.72),
                new GradientStop(Color.FromRgb(0x07, 0x59, 0x85), 1),
            }
        };

        private RadialGradientBrush MakeGreenFill() => new RadialGradientBrush {
            GradientOrigin = new Point(0.38, 0.34),
            Center = new Point(0.38, 0.34),
            RadiusX = 0.78,
            RadiusY = 0.78,
            GradientStops = {
                new GradientStop(Color.FromRgb(0x8C, 0xFF, 0xD8), 0),
                new GradientStop(Color.FromRgb(0x4A, 0xFF, 0xAD), 0.28),
                new GradientStop(Color.FromRgb(0x34, 0xD3, 0x99), 0.72),
                new GradientStop(Color.FromRgb(0x04, 0x78, 0x57), 1),
            }
        };

        private RadialGradientBrush MakeAmberFill() => new RadialGradientBrush {
            GradientOrigin = new Point(0.38, 0.34),
            Center = new Point(0.38, 0.34),
            RadiusX = 0.78,
            RadiusY = 0.78,
            GradientStops = {
                new GradientStop(Color.FromRgb(0xFF, 0xF3, 0xA5), 0),
                new GradientStop(Color.FromRgb(0xFE, 0xE0, 0x73), 0.28),
                new GradientStop(Color.FromRgb(0xFB, 0xBF, 0x24), 0.72),
                new GradientStop(Color.FromRgb(0xB4, 0x53, 0x09), 1),
            }
        };

        private RadialGradientBrush MakeRedFill() => new RadialGradientBrush {
            GradientOrigin = new Point(0.38, 0.34),
            Center = new Point(0.38, 0.34),
            RadiusX = 0.78,
            RadiusY = 0.78,
            GradientStops = {
                new GradientStop(Color.FromRgb(0xFF, 0xC4, 0xC4), 0),
                new GradientStop(Color.FromRgb(0xFA, 0x8D, 0x8D), 0.28),
                new GradientStop(Color.FromRgb(0xF8, 0x71, 0x71), 0.72),
                new GradientStop(Color.FromRgb(0xB9, 0x1C, 0x1C), 1),
            }
        };

        private RadialGradientBrush MakeBlueGlow() => GlowFrom(0x38, 0xBD, 0xF8);
        private RadialGradientBrush MakeGreenGlow() => GlowFrom(0x34, 0xD3, 0x99);
        private RadialGradientBrush MakeAmberGlow() => GlowFrom(0xFB, 0xBF, 0x24);
        private RadialGradientBrush MakeRedGlow() => GlowFrom(0xF8, 0x71, 0x71);

        private static RadialGradientBrush GlowFrom(byte r, byte g, byte b) {
            var c1 = new Color { A = 0xFF, R = r, G = g, B = b };
            var c2 = new Color { A = 0x66, R = r, G = g, B = b };
            var c3 = new Color { A = 0x03, R = r, G = g, B = b };
            return new RadialGradientBrush {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.88,
                RadiusY = 0.88,
                GradientStops = {
                    new GradientStop(Color.FromArgb(0xFF, r, g, b), 0),
                    new GradientStop(Color.FromArgb(0x88, r, g, b), 0.34),
                    new GradientStop(Color.FromArgb(0x18, r, g, b), 0.64),
                    new GradientStop(Color.FromArgb(0x00, 0x08, 0x18, 0x26), 1),
                }
            };
        }

        // ---------- Activity log ----------

        public class LogEntry {
            public string Text { get; set; } = "";
            public Brush KindBrush { get; set; } = Brushes.LightSteelBlue;
        }

        private readonly ObservableCollection<LogEntry> _log = new ObservableCollection<LogEntry>();

        private void AppendLogSafe(string line) {
            try {
                Dispatcher.Invoke(new Action(() => AppendLog(line)));
            } catch { }
        }

        private void AppendLog(string line) {
            var kind = LogKindFor(line);
            _log.Add(new LogEntry { Text = DateTime.Now.ToString("HH:mm:ss") + "  " + line, KindBrush = kind });
            while (_log.Count > 60) _log.RemoveAt(0);
            LogList.UpdateLayout();
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            // Watch for the engine's handshake-OK marker — that's the real tunnel-up signal.
            if (_state == UiState.Connecting && line != null && line.Contains("WARP tunnel is up")) {
                OnTunnelUp();
            }
        }

        private Brush LogKindFor(string line) {
            var lower = line.ToLowerInvariant();
            if (lower.Contains("verified") || lower.Contains("tunnel is up") || lower.Contains("handshake")) return new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            if (lower.Contains("failed") || lower.Contains("no handshake") || lower.Contains("did not pass")) return new SolidColorBrush(Color.FromRgb(0xFC, 0xD3, 0x4D));
            if (lower.Contains("stats reported")) return new SolidColorBrush(Color.FromRgb(0x7D, 0xF3, 0xD8));
            if (lower.Contains("cancel")) return new SolidColorBrush(Color.FromRgb(0x8F, 0xA6, 0xC8));
            return new SolidColorBrush(Color.FromRgb(0xA9, 0xB8, 0xCF));
        }

        // ---------- Connect flow ----------

        private void ConnectSequence() {
            try {
                Trace("ConnectSequence started");
                ConnectSequenceInner();
            } finally {
                _sequenceRunning = false;
            }
        }

        private void ConnectSequenceInner() {
            try {
                if (!_engine.IsAvailable) {
                    Ui(delegate { AppendLog("warpscout.exe is missing from this install folder — reinstall WARP Connect."); ApplyState(UiState.Failed); });
                    return;
                }

                // 1) Account
                if (!System.IO.File.Exists(_settings.AccountPath)) {
                    Ui(delegate { AppendLog("No WARP account — registering (direct API, relays, tunnel fallback)…"); ApplyState(UiState.Preparing); });
                    var rc = _engine.Run(new List<string> { "register", "-a", _settings.AccountPath, "-plain" }, 600000);
                    if (rc != 0 || !System.IO.File.Exists(_settings.AccountPath)) {
                        Ui(delegate { ApplyState(UiState.Failed); });
                        AppendLogSafe("Register failed — could not register a WARP account on this network. Try another Wi-Fi network.");
                        return;
                    }
                    Ui(delegate { AppendLog("Account registered."); });
                }

                // 2) Endpoint — cached winner, else scan
                var ep = _settings.LastEndpoint;
                if (!LooksLikeEndpoint(ep)) ep = null;
                if (ep == null) {
                    Ui(delegate { ApplyState(UiState.Scanning); AppendLog("Scanning for endpoints (AWG) — this can take a few minutes on a filtered line…"); });
                    var res = _engine.RunCapture(new List<string> { "scan", "-best", "-plain", "-n", "2", "-t", "6", "-a", _settings.AccountPath }, 300000);
                    ep = TryStandaloneEndpoint(res.StdOut + "\n" + res.StdErr);
                    if (ep == null) {
                        Ui(delegate { ApplyState(UiState.Failed); });
                        AppendLogSafe("No endpoint passed verification on this network. Try a different Wi-Fi or cellular network.");
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
                    Ui(delegate { ApplyState(UiState.Failed); });
                    return;
                }

                // 4) Bring up the tunnel
                Ui(delegate {
                    ApplyState(UiState.Connecting);
                    TxtEndpoint.Text = ep;
                    _engine.StartTunnel(ep, "awg");
                    AppendLog("Dialing " + ep + " (AmneziaWG)…");
                });
            } catch (Exception e) {
                Trace("ConnectSequence EXCEPTION: " + e);
                AppendLogSafe("Connect failed: " + e.Message);
                Ui(delegate { ApplyState(UiState.Failed); });
            }
        }

        private string TryStandaloneEndpoint(string text) {
            var m = System.Text.RegularExpressions.Regex.Matches(text ?? "", @"(\d{1,3}(\.\d{1,3}){3}:\d{2,5})");
            foreach (System.Text.RegularExpressions.Match match in m) {
                return match.Value;
            }
            return null!;
        }

        private static bool LooksLikeEndpoint(string? s) {
            if (string.IsNullOrEmpty(s)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}:\d{2,5}$");
        }

        private void Ui(Action a) {
            Dispatcher.Invoke(a);
        }

        // ---------- Tunnel up / refresh ----------

        private async void RefreshNetworkInfoAsync() {
            try {
                var info = await Net.FetchTunnelInfoAsync();
                var latency = Net.PingMs("1.1.1.1", 2500);
                var identity = BuildIdentity(info);
                Dispatcher.Invoke(delegate {
                    if (!info.Ok) {
                        TxtNetwork.Text = "connected — could not read exit info";
                    } else {
                        var parts = new List<string>();
                        parts.Add(info.Ip);
                        if (!string.IsNullOrEmpty(info.Asn)) parts.Add(info.Asn);
                        if (!string.IsNullOrEmpty(info.AsnOrg)) parts.Add(info.AsnOrg);
                        var geo = info.Country;
                        if (!string.IsNullOrEmpty(info.City)) geo = info.City + " · " + geo;
                        if (!string.IsNullOrEmpty(geo)) parts.Add(geo);
                        TxtNetwork.Text = string.Join("  |  ", parts.ToArray());
                    }
                    TxtSpeed.Text = latency >= 0 ? (latency + " ms") : "ping unavailable";
                });
                await MaybeReport(identity, _settings.LastEndpoint ?? "");
            } catch (Exception e) {
                AppendLog("exit info refresh failed: " + e.Message);
            }
        }

        private async System.Threading.Tasks.Task MaybeReport(OutboundReport id, string endpoint) {
            if (!_settings.ShareStats) return;
            _connectIdentity = id;
            _heartbeatTicks = 0;
            var ep = endpoint;
            var dev = _settings.DeviceId;
            try { await Report.SubmitReport(id, ep); } catch { }
            try { await Report.Heartbeat(id, ep, dev); } catch { }
            AppendLogSafe("Stats reported (endpoint: " + ep + ").");
        }

        private async void ByeStats() {
            if (!_settings.ShareStats || string.IsNullOrEmpty(_settings.DeviceId)) return;
            try { await Report.Bye(_settings.DeviceId); } catch { }
        }

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

        private string? WifiSsid() {
            try {
                var psi = new ProcessStartInfo {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi)) {
                    if (p == null) return null;
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

        // ---------- Speed test ----------

        private void SpeedTest_Click(object sender, RoutedEventArgs e) {
            if (_state != UiState.Connected) { AppendLog("Speed test needs an active tunnel."); return; }
            AppendLog("Speed test — measuring through the tunnel…");
            BtnSpeedTest.IsEnabled = false;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                double mbps = 0;
                int ping = -1;
                try { mbps = Net.MeasureMbpsAsync(10 * 1024 * 1024).GetAwaiter().GetResult(); } catch (Exception ex) { AppendLog("Speed test failed: " + ex.Message); }
                try { ping = Net.PingMs("1.1.1.1", 2500); } catch { }
                Dispatcher.Invoke(delegate {
                    BtnSpeedTest.IsEnabled = true;
                    TxtSpeed.Text = string.Format("{0} ms  ·  {1:0.0} Mbps", ping < 0 ? "—" : ping.ToString(), mbps);
                    AppendLog(string.Format("Speed: {0:0.0} Mbps down · {1} ms", mbps, ping < 0 ? "—" : ping.ToString()));
                });
            });
        }

        // ---------- On tunnel up ----------

        private void OnTunnelUp() {
            Ui(delegate {
                ApplyState(UiState.Connected);
                AppendLog("WARP tunnel is up — IPv4 traffic now exits through " + (_settings.LastEndpoint ?? ""));
            });
            RefreshNetworkInfoAsync();
        }
    }
}
