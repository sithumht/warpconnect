using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WarpConnect {
    public enum DiscState { Ready, Preparing, Handshaking, Connected, Failed }

    // The big glowing CONNECT disc — green when the tunnel is up (disconnect),
    // blue at rest, yellow while working, red on failure. Mirrors the Android
    // app's one-button design.
    public class DiscButton : Control {
        private DiscState _state = DiscState.Ready;
        private float _glowPhase;
        private bool _pressed;
        private readonly System.Windows.Forms.Timer _timer;
        private string _caption = "CONNECT";
        private string _subcaption = "";
        private float _rotation;

        public event EventHandler Clicked;

        public DiscButton() {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            ForeColor = Color.White;
            BackColor = Color.FromArgb(8, 13, 26);
            Cursor = Cursors.Hand;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 50;
            _timer.Tick += delegate {
                _glowPhase += 0.08f;
                if (_glowPhase > (float)Math.PI * 2) _glowPhase -= (float)Math.PI * 2;
                if (_state == DiscState.Preparing || _state == DiscState.Handshaking) _rotation += 9f;
                Invalidate();
            };
            _timer.Start();
        }

        public DiscState State {
            get { return _state; }
            set { _state = value; Repaint(_captionFor(value), _subFor(value)); }
        }
        public string Caption { get { return _caption; } set { Repaint(value, _subcaption); } }
        public string Subcaption { get { return _subcaption; } set { Repaint(_caption, value); } }

        private void Repaint(string caption, string sub) {
            _caption = caption;
            _subcaption = sub;
            Invalidate();
        }

        private static string _captionFor(DiscState s) {
            switch (s) {
                case DiscState.Ready: return "CONNECT";
                case DiscState.Preparing: return "CANCEL";
                case DiscState.Handshaking: return "CANCEL";
                case DiscState.Connected: return "DISCONNECT";
                case DiscState.Failed: return "RETRY";
                default: return "CONNECT";
            }
        }

        private static string _subFor(DiscState s) {
            switch (s) {
                case DiscState.Preparing: return "working…";
                case DiscState.Handshaking: return "verifying…";
                case DiscState.Failed: return "failed";
                default: return "";
            }
        }

        private Color DiscColor() {
            switch (_state) {
                case DiscState.Ready: return ColorTranslator.FromHtml("#38BDF8");
                case DiscState.Preparing:
                case DiscState.Handshaking: return ColorTranslator.FromHtml("#FBBF24");
                case DiscState.Connected: return ColorTranslator.FromHtml("#34D399");
                case DiscState.Failed: return ColorTranslator.FromHtml("#F87171");
                default: return ColorTranslator.FromHtml("#38BDF8");
            }
        }

        private Color DiscDeep() {
            switch (_state) {
                case DiscState.Ready: return ColorTranslator.FromHtml("#0EA5E9");
                case DiscState.Preparing:
                case DiscState.Handshaking: return ColorTranslator.FromHtml("#D97706");
                case DiscState.Connected: return ColorTranslator.FromHtml("#10B981");
                case DiscState.Failed: return ColorTranslator.FromHtml("#EF4444");
                default: return ColorTranslator.FromHtml("#0EA5E9");
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposing && _timer != null) { _timer.Stop(); _timer.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { if (Clicked != null) Clicked(this, EventArgs.Empty); } }
        protected override void OnMouseLeave(EventArgs e) { _pressed = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e) {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            var cx = Width / 2f;
            var cy = Height / 2f;
            var baseR = Math.Min(Width, Height) / 2f - 30f;

            // Breathing glow: translucent layered discs behind the button.
            float glow = (float)(0.5 + 0.5 * Math.Sin(_glowPhase));
            var disc = DiscColor();
            for (int i = 5; i >= 1; i--) {
                var alpha = (int)((6 + glow * 26) * (i / 5.0));
                using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, alpha), disc))) {
                    float r = baseR + i * 16;
                    g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
                }
            }

            // Outer ring
            using (var ring = new Pen(Color.FromArgb(90, disc), 2f)) {
                g.DrawEllipse(ring, cx - baseR - 8, cy - baseR - 8, (baseR + 8) * 2, (baseR + 8) * 2);
            }

            // Disc body with a top-light gradient
            using (var path = new GraphicsPath()) {
                path.AddEllipse(cx - baseR, cy - baseR, baseR * 2, baseR * 2);
                using (var brush = new PathGradientBrush(path)) {
                    brush.CenterColor = Lighten(disc, 0.45f);
                    brush.SurroundColors = new[] { DiscDeep() };
                    if (_pressed) {
                        using (var dim = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillPath(dim, path);
                    }
                    g.FillPath(brush, path);
                }
            }

            // Rotating spinner arc while working
            if (_state == DiscState.Preparing || _state == DiscState.Handshaking) {
                using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 5f)) {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, cx - baseR * 0.62f, cy - baseR * 0.62f, baseR * 1.24f, baseR * 1.24f, _rotation, 100f);
                }
            }

            // Caption
            using (var titleFont = new Font("Segoe UI Semibold", Math.Max(11f, baseR * 0.21f), FontStyle.Bold, GraphicsUnit.Point))
            using (var subFont = new Font("Segoe UI", Math.Max(6.5f, baseR * 0.1f), FontStyle.Regular, GraphicsUnit.Point)) {
                var size = g.MeasureString(_caption, titleFont);
                float subH = string.IsNullOrEmpty(_subcaption) ? 0 : g.MeasureString("x", subFont).Height;
                float total = size.Height + subH;
                var tRect = new RectangleF(cx - baseR, cy - total / 2f, baseR * 2, size.Height);
                TextRenderer.DrawText(g, _caption, titleFont, Rectangle.Round(tRect), Color.FromArgb(4, 31, 20), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (subH > 0) {
                    var sRect = new RectangleF(cx - baseR, cy + total / 2f - subH, baseR * 2, subH);
                    TextRenderer.DrawText(g, _subcaption, subFont, Rectangle.Round(sRect), Color.FromArgb(30, 0, 0, 0), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        private static Color Lighten(Color c, float amount) {
            int r = c.R + (int)((255 - c.R) * amount);
            int g = c.G + (int)((255 - c.G) * amount);
            int b = c.B + (int)((255 - c.B) * amount);
            return Color.FromArgb(c.A, Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
        }
    }
}
