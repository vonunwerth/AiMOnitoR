// AI Usage Monitor - Darstellung: Tray-Icons, Detail-Popup, Copilot-Login, App-Steuerung
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AIUsageMonitor
{
    // ================================================================== Theme
    public class Theme
    {
        public bool Dark;
        public Color Bg, Card, Border, Text, Sub, Track, Link, Warn, Crit, Err;

        public static bool RegDark(string valueName)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue(valueName);
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return true;
        }

        public static Theme Current()
        {
            return Make(RegDark("AppsUseLightTheme"));
        }

        public static Theme Make(bool dark)
        {
            Theme t = new Theme();
            t.Dark = dark;
            if (dark)
            {
                t.Bg = Color.FromArgb(32, 32, 32); t.Card = Color.FromArgb(43, 43, 43); t.Border = Color.FromArgb(60, 60, 60);
                t.Text = Color.FromArgb(245, 245, 245); t.Sub = Color.FromArgb(160, 160, 160); t.Track = Color.FromArgb(64, 64, 64);
                t.Link = Color.FromArgb(120, 180, 255);
            }
            else
            {
                t.Bg = Color.FromArgb(243, 243, 243); t.Card = Color.White; t.Border = Color.FromArgb(222, 222, 222);
                t.Text = Color.FromArgb(26, 26, 26); t.Sub = Color.FromArgb(96, 96, 96); t.Track = Color.FromArgb(226, 226, 226);
                t.Link = Color.FromArgb(0, 95, 184);
            }
            t.Warn = Color.FromArgb(232, 163, 23);
            t.Crit = Color.FromArgb(229, 72, 77);
            t.Err = dark ? Color.FromArgb(255, 140, 120) : Color.FromArgb(196, 43, 28);
            return t;
        }

        // Ampel-Skala für das verbleibende Kontingent
        public static readonly Color DarkGreen = Color.FromArgb(22, 140, 60);
        public static readonly Color LightGreen = Color.FromArgb(124, 214, 84);
        public static readonly Color Yellow = Color.FromArgb(245, 210, 20);
        public static readonly Color Orange = Color.FromArgb(245, 135, 30);
        public static readonly Color Red = Color.FromArgb(229, 57, 53);
        public static readonly Color Black = Color.FromArgb(0, 0, 0);

        public static Color Level(double pct)
        {
            double p = Math.Round(pct);
            if (p >= 75) return DarkGreen;
            if (p >= 50) return LightGreen;
            if (p >= 25) return Yellow;
            if (p >= 10) return Orange;
            if (p >= 1) return Red;
            return Black;
        }

        public static Color Status(double? pct, Color fallback, Config cfg)
        {
            return pct.HasValue ? Level(pct.Value) : fallback;
        }
    }

    // ================================================================== Tray-Icon zeichnen
    public static class IconRenderer
    {
        public static Bitmap Render(ProviderResult r, Color brand, int size, bool darkTaskbar, Config cfg)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                Color track = darkTaskbar ? Color.FromArgb(85, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0);
                Color edge = darkTaskbar ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(120, 0, 0, 0);
                int m = Math.Max(1, (int)Math.Round(size * 0.07));
                int gap = Math.Max(2, (int)Math.Round(size / 7.0));
                int inner = size - 2 * m;

                // Anbieter-Kennung: Streifen in Markenfarbe unten über die volle Breite
                int stripe = Math.Max(2, (int)Math.Round(size / 7.0));
                int barTop = m, barBottom = size - m - stripe - 1;   // 1 px Abstand zum Streifen
                int barH = barBottom - barTop;

                bool hasData = r != null && (r.BarA != null || r.BarB != null);
                List<UsageWindow> bars = new List<UsageWindow>();
                if (hasData)
                {
                    if (r.BarA != null) bars.Add(r.BarA);
                    if (r.BarB != null) bars.Add(r.BarB);
                }
                int n = Math.Max(2, bars.Count);
                if (hasData && bars.Count == 1) n = 1;

                int bw = n == 1 ? (int)Math.Round(inner * 0.55) : (inner - gap * (n - 1)) / n;
                int totalW = bw * n + gap * (n - 1);
                int x0 = (size - totalW) / 2;

                for (int i = 0; i < n; i++)
                {
                    Rectangle rc = new Rectangle(x0 + i * (bw + gap), barTop, bw, barH);
                    using (SolidBrush b = new SolidBrush(track)) g.FillRectangle(b, rc);
                    UsageWindow w = i < bars.Count ? bars[i] : null;
                    if (w != null && w.RemainingPct.HasValue)
                    {
                        double pct = w.RemainingPct.Value;
                        int alpha = r.Stale ? 150 : 255;
                        if (Math.Round(pct) < 1)
                        {
                            // leer: ganzer Balken schwarz mit hellem Rand, damit er auch auf dunkler Leiste sichtbar ist
                            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, Theme.Black))) g.FillRectangle(b, rc);
                            using (Pen p = new Pen(edge)) g.DrawRectangle(p, rc.X, rc.Y, rc.Width - 1, rc.Height - 1);
                        }
                        else
                        {
                            int h = Math.Max(1, (int)Math.Round(barH * pct / 100.0));
                            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, Theme.Level(pct))))
                                g.FillRectangle(b, new Rectangle(rc.X, rc.Bottom - h, rc.Width, h));
                        }
                    }
                }

                // Markenstreifen (bei fehlender Einrichtung blass)
                Color sc = (r != null && r.NotConfigured) ? Color.FromArgb(110, brand) : brand;
                using (SolidBrush b = new SolidBrush(sc))
                    g.FillRectangle(b, new Rectangle(x0, size - m - stripe, totalW, stripe));

                // Markierung oben rechts: Fehler (weiß/schwarz mit Rand) bzw. nicht eingerichtet (grau)
                if (r != null && (r.Error != null || r.NotConfigured))
                {
                    int d = Math.Max(3, size / 4);
                    Color c = r.NotConfigured ? Color.FromArgb(150, 150, 150) : (darkTaskbar ? Color.White : Color.Black);
                    using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, size - d, 0, d, d);
                    using (Pen p = new Pen(darkTaskbar ? Color.Black : Color.White)) g.DrawRectangle(p, size - d, 0, d - 1, d - 1);
                }
            }
            return bmp;
        }

        public static string Tooltip(ProviderResult r, string name)
        {
            string t;
            if (r == null || r.Loading && r.UpdatedAt == null) t = name + ": lädt…";
            else if (r.NotConfigured) t = name + ": nicht eingerichtet";
            else if (r.BarA == null && r.Error != null) t = name + ": Fehler – klicken für Details";
            else
            {
                List<string> parts = new List<string>();
                foreach (UsageWindow w in new UsageWindow[] { r.BarA, r.BarB })
                {
                    if (w == null) continue;
                    parts.Add(w.ShortLabel + " " + (w.Unlimited ? "∞" : Fmt.Pct(w.RemainingPct)));
                }
                t = name + " frei: " + string.Join(" · ", parts.ToArray());
                if (r.Error != null) t += " (veraltet)";
            }
            return t.Length > 63 ? t.Substring(0, 62) + "…" : t;
        }
    }

    // ================================================================== Detail-Ansicht (gezeichnet)
    public class Hit
    {
        public Rectangle Rect;
        public string Action;
        public Hit(Rectangle r, string a) { Rect = r; Action = a; }
    }

    public class PopupRenderer
    {
        public List<ProviderResult> Data = new List<ProviderResult>();
        public Dictionary<string, Color> Brands = new Dictionary<string, Color>();
        public Config Cfg;
        public Theme Th;
        public float Scale = 1f;
        public List<Hit> Hits = new List<Hit>();
        public string HoverAction;
        public bool AnyLoading;
        public bool Compact;

        Font fTitle, fName, fBody, fBodyBold, fSmall;

        int S(float v) { return (int)Math.Round(v * Scale); }

        void EnsureFonts()
        {
            if (fBody != null) return;
            fTitle = new Font("Segoe UI Semibold", 11.5f, FontStyle.Regular, GraphicsUnit.Point);
            fName = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
            fBody = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            fBodyBold = new Font("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            fSmall = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        }

        public int Width { get { return S(400); } }

        static Size Measure(Graphics g, string text, Font f, int maxW)
        {
            return TextRenderer.MeasureText(g, text, f, new Size(maxW, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        }

        static void Draw(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags extra)
        {
            TextRenderer.DrawText(g, text, f, r, c,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | extra);
        }

        int Para(Graphics g, bool paint, string text, Font f, int x, int y, int w, Color c)
        {
            Size sz = Measure(g, text, f, w);
            if (paint) Draw(g, text, f, new Rectangle(x, y, w, sz.Height), c, TextFormatFlags.Left);
            return sz.Height;
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Bar(Graphics g, Rectangle r, double? pct, Color fill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int rad = r.Height / 2;
            using (GraphicsPath p = Rounded(r, rad))
            using (SolidBrush b = new SolidBrush(Th.Track)) g.FillPath(b, p);
            if (pct.HasValue && pct.Value > 0)
            {
                int w = Math.Max(r.Height, (int)Math.Round(r.Width * pct.Value / 100.0));
                Rectangle fr = new Rectangle(r.X, r.Y, Math.Min(w, r.Width), r.Height);
                using (GraphicsPath p = Rounded(fr, rad))
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        // Zeichnet (paint=true) bzw. misst nur; liefert die Gesamthöhe
        public int Render(Graphics g, bool paint)
        {
            EnsureFonts();
            if (paint) Hits.Clear();
            int W = Width, pad = S(16), y = pad;
            int cw = W - 2 * pad;

            if (paint)
            {
                g.Clear(Th.Bg);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            }

            // Kopfzeile
            int titleH = Measure(g, "KI-Abos", fTitle, cw).Height;
            if (paint)
            {
                Draw(g, "KI-Abos – verbleibende Limits", fTitle, new Rectangle(pad, y, cw, titleH), Th.Text, TextFormatFlags.Left);
                DateTime? newest = null;
                foreach (ProviderResult r in Data) if (r.UpdatedAt.HasValue && (!newest.HasValue || r.UpdatedAt > newest)) newest = r.UpdatedAt;
                string st = AnyLoading ? "aktualisiere…" : (newest.HasValue ? "Stand " + newest.Value.ToString("HH:mm", Fmt.De) : "");
                Draw(g, st, fSmall, new Rectangle(pad, y + S(3), cw, titleH), Th.Sub, TextFormatFlags.Right);
            }
            y += titleH + S(10);

            foreach (ProviderResult r in Data)
            {
                y = Card(g, paint, r, pad, y, cw) + S(8);
            }

            if (Data.Count == 0)
            {
                y += Para(g, paint, "Alle Anbieter sind in der config.json deaktiviert.", fBody, pad, y, cw, Th.Sub) + S(10);
            }

            // Fußzeile mit Aktionen
            y += S(2);
            string[] labels = { "Aktualisieren", "Einstellungen", "Log", "Beenden" };
            string[] actions = { "refresh", "settings", "log", "quit" };
            int x = pad;
            int fh = Measure(g, "Ag", fBody, cw).Height;
            for (int i = 0; i < labels.Length; i++)
            {
                Size sz = TextRenderer.MeasureText(g, labels[i], fBody, new Size(cw, fh), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                Rectangle hr = new Rectangle(x, y, sz.Width, fh);
                if (i == labels.Length - 1) hr.X = W - pad - sz.Width;
                if (paint)
                {
                    bool hover = HoverAction == actions[i];
                    Font f = hover ? new Font(fBody, FontStyle.Underline) : fBody;
                    Draw(g, labels[i], f, hr, actions[i] == "quit" ? Th.Sub : Th.Link, TextFormatFlags.Left);
                    if (hover) f.Dispose();
                    Hits.Add(new Hit(Rectangle.Inflate(hr, S(4), S(4)), actions[i]));
                }
                x += sz.Width + S(18);
            }
            y += fh + pad;

            if (paint)
            {
                using (Pen p = new Pen(Th.Border)) g.DrawRectangle(p, 0, 0, W - 1, y - 1);
            }
            return y;
        }

        int Card(Graphics g, bool paint, ProviderResult r, int x0, int y0, int w)
        {
            int ip = S(11);
            int x = x0 + ip, cw = w - 2 * ip;
            int y = y0 + ip;
            Color brand;
            if (!Brands.TryGetValue(r.Id, out brand)) brand = Th.Link;

            // Wir zeichnen den Inhalt erst nach dem Kartenhintergrund -> zweistufig: messen, dann malen
            int contentH = CardContent(g, false, r, brand, x, y, cw);
            Rectangle card = new Rectangle(x0, y0, w, contentH + 2 * ip);
            if (paint)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Rounded(card, S(8)))
                {
                    using (SolidBrush b = new SolidBrush(Th.Card)) g.FillPath(b, p);
                    using (Pen pen = new Pen(Th.Border)) g.DrawPath(pen, p);
                }
                g.SmoothingMode = SmoothingMode.None;
                CardContent(g, true, r, brand, x, y, cw);
            }
            return card.Bottom;
        }

        static string DashboardAction(string id)
        {
            switch (id)
            {
                case "claude": return "open:https://claude.ai/settings/usage";
                case "chatgpt": return "open:https://chatgpt.com/codex/settings/usage";
                case "copilot": return "open:https://github.com/settings/copilot/features";
            }
            return null;
        }

        int CardContent(Graphics g, bool paint, ProviderResult r, Color brand, int x, int y0, int cw)
        {
            int y = y0;
            // Kopf: Punkt + Name + Plan, rechts Konto
            int nameH = Measure(g, "Ag", fName, cw).Height;
            if (paint)
            {
                int d = S(9);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(brand)) g.FillEllipse(b, x, y + (nameH - d) / 2, d, d);
                g.SmoothingMode = SmoothingMode.None;
                int nx = x + d + S(8);
                Size ns = TextRenderer.MeasureText(g, r.Name, fName, new Size(cw, nameH), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                string act = DashboardAction(r.Id);
                bool hover = act != null && HoverAction == act;
                Font nf = hover ? new Font(fName, FontStyle.Underline) : fName;
                Draw(g, r.Name, nf, new Rectangle(nx, y, ns.Width + S(4), nameH), Th.Text, TextFormatFlags.Left);
                if (hover) nf.Dispose();
                if (act != null) Hits.Add(new Hit(new Rectangle(nx, y, ns.Width, nameH), act));
                if (!string.IsNullOrEmpty(r.Plan))
                {
                    Size ps = TextRenderer.MeasureText(g, r.Plan, fSmall, new Size(cw, nameH), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    Rectangle pr = new Rectangle(nx + ns.Width + S(8), y + (nameH - ps.Height) / 2, ps.Width + S(10), ps.Height);
                    Draw(g, r.Plan, fSmall, new Rectangle(pr.X + S(5), pr.Y, ps.Width + S(2), ps.Height), Th.Sub, TextFormatFlags.Left);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath p = Rounded(Rectangle.Inflate(pr, 0, S(1)), S(4)))
                    using (Pen pen = new Pen(Th.Border)) g.DrawPath(pen, p);
                    g.SmoothingMode = SmoothingMode.None;
                }
                if (!string.IsNullOrEmpty(r.Account))
                    Draw(g, r.Account, fSmall, new Rectangle(x, y + S(3), cw, nameH), Th.Sub, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            }
            y += nameH + S(7);

            if (r.Loading && r.UpdatedAt == null && r.Windows.Count == 0 && r.Error == null)
            {
                y += Para(g, paint, "Lade Daten…", fBody, x, y, cw, Th.Sub);
                return y - y0;
            }
            if (r.NotConfigured)
            {
                y += Para(g, paint, r.Error ?? "Nicht eingerichtet.", fBody, x, y, cw, Th.Sub);
                if (r.Id == "copilot")
                {
                    y += S(6);
                    int lh = Measure(g, "Ag", fBody, cw).Height;
                    if (paint)
                    {
                        string t = "Jetzt mit GitHub anmelden…";
                        Size sz = TextRenderer.MeasureText(g, t, fBody, new Size(cw, lh), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        bool hover = HoverAction == "copilot-login";
                        Font f = hover ? new Font(fBody, FontStyle.Underline) : fBody;
                        Draw(g, t, f, new Rectangle(x, y, sz.Width + S(4), lh), Th.Link, TextFormatFlags.Left);
                        if (hover) f.Dispose();
                        Hits.Add(new Hit(new Rectangle(x, y, sz.Width, lh), "copilot-login"));
                    }
                    y += lh;
                }
                return y - y0;
            }

            bool first = true;
            int hidden = 0;
            foreach (UsageWindow uw in r.Windows)
            {
                if (Compact && uw != r.BarA && uw != r.BarB) { hidden++; continue; }
                if (!first) y += S(9);
                first = false;
                int lh = Measure(g, "Ag", fBody, cw).Height;
                string val;
                if (uw.Unlimited) val = "unbegrenzt";
                else if (uw.RemainingPct.HasValue) val = Fmt.Pct(uw.RemainingPct) + " frei";
                else val = uw.Note ?? "";
                Color vc = Th.Sub;
                if (uw.RemainingPct.HasValue && !uw.Unlimited)
                {
                    Color lv = Theme.Level(uw.RemainingPct.Value);
                    vc = (lv == Theme.DarkGreen || lv == Theme.LightGreen) ? Th.Text
                       : (lv == Theme.Black ? Th.Err : (lv == Theme.Yellow && !Th.Dark ? Color.FromArgb(160, 125, 0) : lv));
                }
                if (paint)
                {
                    Draw(g, uw.Label, fBody, new Rectangle(x, y, cw, lh), Th.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    Draw(g, val, uw.RemainingPct.HasValue && !uw.Unlimited ? fBodyBold : fBody, new Rectangle(x, y, cw, lh), vc, TextFormatFlags.Right);
                }
                y += lh;
                if (uw.Unlimited || !uw.RemainingPct.HasValue) continue;   // keine Balken für unbegrenzte/reine Info-Zeilen
                y += S(4);
                if (paint)
                {
                    Color fill = Theme.Level(uw.RemainingPct.Value);
                    if (r.Stale) fill = Color.FromArgb(140, fill);
                    Rectangle br = new Rectangle(x, y, cw, S(6));
                    if (Math.Round(uw.RemainingPct.Value) < 1) Bar(g, br, 100, Th.Dark ? Color.FromArgb(10, 10, 10) : Theme.Black);
                    else Bar(g, br, uw.RemainingPct, fill);
                }
                y += S(6) + S(4);
                string reset = Fmt.Reset(uw);
                string line = reset;
                if (!string.IsNullOrEmpty(uw.Note)) line = line == null ? uw.Note : line + "  ·  " + uw.Note;
                if (line != null) y += Para(g, paint, line, fSmall, x, y, cw, Th.Sub);
            }
            if (hidden > 0)
            {
                y += S(6);
                y += Para(g, paint, "+ " + hidden + " weitere" + (hidden == 1 ? "s Limit" : " Limits") + " (Bildschirm zu niedrig für alle)", fSmall, x, y, cw, Th.Sub);
            }

            if (r.Error != null)
            {
                if (r.Windows.Count > 0) y += S(8);
                string msg = r.Windows.Count > 0
                    ? "Letzter Abruf fehlgeschlagen" + (r.UpdatedAt.HasValue ? " (Daten von " + r.UpdatedAt.Value.ToString("HH:mm", Fmt.De) + ")" : "") + ": " + r.Error
                    : r.Error;
                y += Para(g, paint, msg, fSmall, x, y, cw, Th.Err);
            }
            return y - y0;
        }
    }

    // ================================================================== Popup-Fenster
    public class PopupForm : Form
    {
        public PopupRenderer R = new PopupRenderer();
        public event Action<string> ActionClicked;
        public DateTime LastHidden = DateTime.MinValue;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            Text = "AI Usage Monitor";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: nicht in Alt-Tab
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Win11: runde Ecken
        }

        public void Relayout()
        {
            Relayout(Screen.FromControl(this).WorkingArea);
        }

        public void Relayout(Rectangle wa)
        {
            int oldBottom = Bottom;
            bool wasVisible = Visible;
            using (Graphics g = CreateGraphics())
            {
                R.Scale = g.DpiX / 96f;
                int maxH = wa.Height - (int)(24 * R.Scale);
                R.Compact = false;
                int h = R.Render(g, false);
                if (h > maxH) { R.Compact = true; h = R.Render(g, false); }
                Size = new Size(R.Width, Math.Min(h, maxH));
            }
            if (wasVisible)
            {
                // unten verankert bleiben (Popup wächst nach oben)
                int m = (int)(12 * R.Scale);
                int y = Math.Max(wa.Top + m, Math.Min(oldBottom - Height, wa.Bottom - Height - m));
                Location = new Point(Location.X, y);
            }
        }

        public void ShowNear(Point anchor)
        {
            R.Th = Theme.Current();
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            Relayout(wa);
            int m = (int)(12 * R.Scale);
            int x = anchor.X - Width / 2, y = anchor.Y - Height / 2;
            x = Math.Max(wa.Left + m, Math.Min(x, wa.Right - Width - m));
            y = Math.Max(wa.Top + m, Math.Min(y, wa.Bottom - Height - m));
            Location = new Point(x, y);
            Show();
            try { SetForegroundWindow(Handle); } catch { }
            Activate();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (R.Th == null) R.Th = Theme.Current();
            R.Render(e.Graphics, true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            LastHidden = DateTime.Now;
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            else if (e.KeyCode == Keys.F5 && ActionClicked != null) ActionClicked("refresh");
            base.OnKeyDown(e);
        }

        string HitAt(Point p)
        {
            foreach (Hit h in R.Hits) if (h.Rect.Contains(p)) return h.Action;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string a = HitAt(e.Location);
            Cursor = a != null ? Cursors.Hand : Cursors.Default;
            if (a != R.HoverAction) { R.HoverAction = a; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (R.HoverAction != null) { R.HoverAction = null; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            string a = HitAt(e.Location);
            if (a != null && ActionClicked != null) ActionClicked(a);
            base.OnMouseClick(e);
        }
    }

    // ================================================================== Copilot-Login (GitHub Device Flow)
    public class CopilotLoginForm : Form
    {
        Label lblInfo, lblCode, lblStatus;
        Button btnOpen, btnCancel;
        DeviceCode dc;
        volatile bool cancelled;
        public string Token;

        public CopilotLoginForm()
        {
            Text = "GitHub Copilot anmelden";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(420, 210);
            TopMost = true;

            lblInfo = new Label();
            lblInfo.SetBounds(16, 14, 388, 40);
            lblInfo.Text = "Der Monitor meldet sich wie VS Code bei GitHub an (nur Leserecht auf dein Profil), um dein Copilot-Kontingent abzufragen.";
            lblCode = new Label();
            lblCode.SetBounds(16, 60, 388, 50);
            lblCode.Font = new Font("Consolas", 22f, FontStyle.Bold);
            lblCode.TextAlign = ContentAlignment.MiddleCenter;
            lblCode.Text = "…";
            lblStatus = new Label();
            lblStatus.SetBounds(16, 116, 388, 40);
            lblStatus.Text = "Code wird angefordert…";
            btnOpen = new Button();
            btnOpen.SetBounds(16, 164, 250, 32);
            btnOpen.Text = "Code kopieren && GitHub öffnen";
            btnOpen.Enabled = false;
            btnOpen.Click += delegate { OpenGitHub(); };
            btnCancel = new Button();
            btnCancel.SetBounds(284, 164, 120, 32);
            btnCancel.Text = "Abbrechen";
            btnCancel.Click += delegate { cancelled = true; Close(); };
            Controls.AddRange(new Control[] { lblInfo, lblCode, lblStatus, btnOpen, btnCancel });
            AcceptButton = btnOpen; CancelButton = btnCancel;
            FormClosed += delegate { cancelled = true; };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Thread t = new Thread(Worker);
            t.IsBackground = true;
            t.Start();
        }

        void OpenGitHub()
        {
            if (dc == null) return;
            try { Clipboard.SetText(dc.UserCode); } catch { }
            try { Process.Start(dc.VerificationUri); } catch { }
            lblStatus.Text = "Code ist in der Zwischenablage. Im Browser einfügen und 'Authorize' bestätigen – dieses Fenster schließt sich dann automatisch.";
        }

        void UI(Action a)
        {
            if (IsDisposed || cancelled) return;
            try { BeginInvoke(a); } catch { }
        }

        void Worker()
        {
            try
            {
                dc = GitHubDeviceFlow.Start();
                UI(delegate
                {
                    lblCode.Text = dc.UserCode;
                    lblStatus.Text = "Klicke auf den Button, gib den Code auf github.com/login/device ein und bestätige.";
                    btnOpen.Enabled = true;
                    btnOpen.Focus();
                });
                while (!cancelled && DateTime.Now < dc.ExpiresAt)
                {
                    Thread.Sleep(Math.Max(5, dc.Interval) * 1000);
                    if (cancelled) return;
                    string tok = GitHubDeviceFlow.Poll(dc);
                    if (tok != null)
                    {
                        Token = tok;
                        UI(delegate { DialogResult = DialogResult.OK; Close(); });
                        return;
                    }
                }
                UI(delegate { lblStatus.Text = "Zeit abgelaufen. Bitte Fenster schließen und erneut versuchen."; });
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                UI(delegate { lblStatus.Text = msg; lblCode.Text = "–"; });
            }
        }
    }

    // ================================================================== App
    public class TrayApp : ApplicationContext
    {
        Config cfg;
        readonly List<Provider> all = new List<Provider>();
        readonly Dictionary<string, ProviderResult> results = new Dictionary<string, ProviderResult>();
        readonly Dictionary<string, TrayIcon> icons = new Dictionary<string, TrayIcon>();
        readonly Dictionary<string, IntPtr> hicons = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, bool> inflight = new Dictionary<string, bool>();
        readonly Dictionary<string, DateTime> lastFetch = new Dictionary<string, DateTime>();
        readonly Dictionary<string, DateTime> pausedUntil = new Dictionary<string, DateTime>();
        readonly Dictionary<string, int> backoffMin = new Dictionary<string, int>();
        PopupForm popup;
        ContextMenuStrip menu;
        ToolStripMenuItem miAutostart;
        System.Windows.Forms.Timer refreshTimer, tickTimer;

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "AIUsageMonitor";

        public TrayApp()
        {
            cfg = Config.Load();
            all.Add(new ClaudeProvider());
            all.Add(new ChatGPTProvider());
            all.Add(new GeminiProvider());
            all.Add(new CopilotProvider());
            foreach (Provider p in all) p.Cfg = cfg;

            popup = new PopupForm();
            popup.R.Cfg = cfg;
            foreach (Provider p in all) popup.R.Brands[p.Id] = p.Brand;
            popup.ActionClicked += OnAction;
            IntPtr force = popup.Handle; // Handle erzeugen (für BeginInvoke aus Hintergrund-Threads)

            BuildMenu();
            SyncIcons();

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = cfg.RefreshMinutes * 60 * 1000;
            refreshTimer.Tick += delegate { ReloadConfig(); RefreshAll(false); };
            refreshTimer.Start();

            tickTimer = new System.Windows.Forms.Timer();
            tickTimer.Interval = 30 * 1000;
            tickTimer.Tick += delegate { Tick(); };
            tickTimer.Start();

            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.UserPreferenceChanged += OnPrefs;

            RefreshAll(true);
        }

        bool Enabled(Provider p)
        {
            switch (p.Id)
            {
                case "claude": return cfg.Claude;
                case "chatgpt": return cfg.ChatGPT;
                case "gemini": return cfg.Gemini;
                case "copilot": return cfg.Copilot;
            }
            return true;
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("Details anzeigen");
            miShow.Font = new Font(miShow.Font, FontStyle.Bold);
            miShow.Click += delegate { ShowPopup(Cursor.Position); };
            menu.Items.Add(miShow);
            menu.Items.Add("Jetzt aktualisieren", null, delegate { RefreshAll(true); });
            menu.Items.Add(new ToolStripSeparator());
            miAutostart = new ToolStripMenuItem("Mit Windows starten");
            miAutostart.Click += delegate { SetAutostart(!IsAutostart()); };
            menu.Items.Add(miAutostart);
            menu.Items.Add("GitHub Copilot anmelden…", null, delegate { CopilotLogin(); });
            menu.Items.Add("Einstellungen (config.json)", null, delegate { OpenFile(Config.FilePath); });
            menu.Items.Add("Log öffnen", null, delegate { OpenFile(Log.FilePath); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Beenden", null, delegate { Quit(); });
            menu.Opening += delegate { miAutostart.Checked = IsAutostart(); };
        }

        void SyncIcons()
        {
            // Reihenfolge rückwärts anlegen: Windows fügt neue Symbole links an
            for (int i = all.Count - 1; i >= 0; i--)
            {
                Provider p = all[i];
                bool on = Enabled(p);
                TrayIcon ni;
                bool has = icons.TryGetValue(p.Id, out ni);
                if (on && !has)
                {
                    ni = new TrayIcon(p.Id);
                    ni.LeftClick += OnIconClick;
                    ni.RightClick += delegate { TrayIcon.ShowMenu(menu); };
                    icons[p.Id] = ni;
                    ni.Visible = true;
                    UpdateIcon(p);
                }
                else if (!on && has)
                {
                    ni.Dispose();
                    icons.Remove(p.Id);
                    IntPtr h;
                    if (hicons.TryGetValue(p.Id, out h)) { DestroyIcon(h); hicons.Remove(p.Id); }
                    results.Remove(p.Id);
                }
            }
            UpdatePopupData();
        }

        void OnIconClick(object sender, EventArgs e)
        {
            if (popup.Visible) { popup.Hide(); return; }
            if ((DateTime.Now - popup.LastHidden).TotalMilliseconds < 300) return; // Klick hat Popup gerade geschlossen
            ShowPopup(Cursor.Position);
        }

        void ShowPopup(Point at)
        {
            UpdatePopupData();
            popup.ShowNear(at);
        }

        void UpdateIcon(Provider p)
        {
            TrayIcon ni;
            if (!icons.TryGetValue(p.Id, out ni)) return;
            ProviderResult r;
            results.TryGetValue(p.Id, out r);
            int size = SystemInformation.SmallIconSize.Width;
            using (Bitmap bmp = IconRenderer.Render(r, p.Brand, size, Theme.RegDark("SystemUsesLightTheme"), cfg))
            {
                IntPtr h = bmp.GetHicon();
                ni.Update(h, IconRenderer.Tooltip(r, p.Name));
                IntPtr old;
                if (hicons.TryGetValue(p.Id, out old)) DestroyIcon(old);
                hicons[p.Id] = h;
            }
        }

        void UpdatePopupData()
        {
            List<ProviderResult> list = new List<ProviderResult>();
            bool loading = false;
            foreach (Provider p in all)
            {
                if (!Enabled(p)) continue;
                ProviderResult r;
                if (!results.TryGetValue(p.Id, out r))
                {
                    r = p.NewResult();
                    r.Loading = true;
                }
                bool fl;
                if (inflight.TryGetValue(p.Id, out fl) && fl) loading = true;
                list.Add(r);
            }
            popup.R.Data = list;
            popup.R.AnyLoading = loading;
            if (popup.Visible) { popup.Relayout(); popup.Invalidate(); }
        }

        void RefreshAll(bool manual)
        {
            foreach (Provider p in all) if (Enabled(p)) RefreshOne(p, manual);
        }

        void RefreshOne(Provider prov, bool manual)
        {
            bool fl;
            if (inflight.TryGetValue(prov.Id, out fl) && fl) return;
            DateTime pu, lf;
            if (!manual && pausedUntil.TryGetValue(prov.Id, out pu) && DateTime.Now < pu) return;
            if (!manual && prov.MinRefreshMinutes > 1 && lastFetch.TryGetValue(prov.Id, out lf)
                && (DateTime.Now - lf).TotalMinutes < prov.MinRefreshMinutes - 0.1) return;
            inflight[prov.Id] = true;
            lastFetch[prov.Id] = DateTime.Now;
            UpdatePopupData();
            Provider p = prov;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ProviderResult r = p.Fetch();
                try { popup.BeginInvoke((Action)delegate { Apply(p, r); }); } catch { }
            });
        }

        void Apply(Provider p, ProviderResult r)
        {
            inflight[p.Id] = false;
            // Bei 429 exponentiell pausieren (2, 4, 8 … max. 15 Minuten), bei Erfolg zurücksetzen
            if (r.RateLimited)
            {
                int b;
                b = backoffMin.TryGetValue(p.Id, out b) ? Math.Min(15, b * 2) : 2;
                backoffMin[p.Id] = b;
                pausedUntil[p.Id] = DateTime.Now.AddMinutes(b);
                Log.Write(p.Name + ": Rate-Limit, pausiere " + b + " Min.");
            }
            else if (r.PauseMinutes > 0)
            {
                pausedUntil[p.Id] = DateTime.Now.AddMinutes(r.PauseMinutes);
            }
            else if (r.Error == null)
            {
                backoffMin.Remove(p.Id);
                pausedUntil.Remove(p.Id);
            }
            ProviderResult old;
            if (r.Error != null && !r.NotConfigured && results.TryGetValue(p.Id, out old) && old.Windows.Count > 0)
            {
                // alte Werte behalten, aber als veraltet markieren
                old.Error = r.Error;
                old.Stale = true;
                r = old;
            }
            results[p.Id] = r;
            UpdateIcon(p);
            UpdatePopupData();
        }

        void Tick()
        {
            // Nach Ablauf eines Reset-Zeitpunkts sofort neu abfragen (höchstens alle 2 Minuten je Anbieter)
            foreach (Provider p in all)
            {
                if (!Enabled(p)) continue;
                ProviderResult r;
                if (!results.TryGetValue(p.Id, out r)) continue;
                bool passed = false;
                foreach (UsageWindow w in r.Windows)
                    if (w.ResetsAt.HasValue && w.ResetsAt.Value < DateTime.Now && !w.Unlimited) passed = true;
                DateTime lf;
                if (passed && (!lastFetch.TryGetValue(p.Id, out lf) || (DateTime.Now - lf).TotalMinutes >= 2))
                    RefreshOne(p, false);
            }
            if (popup.Visible) popup.Invalidate();
        }

        void ReloadConfig()
        {
            Config n = Config.Load();
            cfg = n;
            foreach (Provider p in all) p.Cfg = cfg;
            popup.R.Cfg = cfg;
            refreshTimer.Interval = cfg.RefreshMinutes * 60 * 1000;
            SyncIcons();
        }

        void OnAction(string a)
        {
            if (a == "refresh") { ReloadConfig(); RefreshAll(true); }
            else if (a == "settings") { popup.Hide(); OpenFile(Config.FilePath); }
            else if (a == "log") { popup.Hide(); OpenFile(Log.FilePath); }
            else if (a == "quit") Quit();
            else if (a == "copilot-login") { popup.Hide(); CopilotLogin(); }
            else if (a.StartsWith("open:")) { popup.Hide(); try { Process.Start(a.Substring(5)); } catch { } }
        }

        void CopilotLogin()
        {
            using (CopilotLoginForm f = new CopilotLoginForm())
            {
                if (f.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(f.Token))
                {
                    cfg = Config.Load();
                    cfg.CopilotToken = f.Token;
                    cfg.Copilot = true;
                    cfg.Save();
                    ReloadConfig();
                    foreach (Provider p in all) if (p.Id == "copilot") RefreshOne(p, true);
                }
            }
        }

        void OnPower(object s, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            try { popup.BeginInvoke((Action)delegate { RefreshAll(false); }); } catch { }
        }

        void OnPrefs(object s, UserPreferenceChangedEventArgs e)
        {
            try { popup.BeginInvoke((Action)delegate { foreach (Provider p in all) UpdateIcon(p); }); } catch { }
        }

        static void OpenFile(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, "");
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch { }
        }

        static bool IsAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex) { MessageBox.Show("Autostart konnte nicht geändert werden: " + ex.Message); }
        }

        void Quit()
        {
            SystemEvents.PowerModeChanged -= OnPower;
            SystemEvents.UserPreferenceChanged -= OnPrefs;
            foreach (TrayIcon ni in icons.Values) ni.Dispose();
            foreach (IntPtr h in hicons.Values) DestroyIcon(h);
            icons.Clear(); hicons.Clear();
            ExitThread();
        }
    }

    // ================================================================== Einstieg
    public static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main(string[] args)
        {
            bool created;
            using (Mutex mtx = new Mutex(true, "Local\\AIUsageMonitor_SingleInstance", out created))
            {
                if (!created) return;
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("UI-Fehler: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Write("Fehler: " + e.ExceptionObject); };
                Log.Write("Start");
                Application.Run(new TrayApp());
            }
        }
    }
}
