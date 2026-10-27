using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FNHybrid
{
    // Interface X.HYBRID : noir / bleu electric, panneaux à coins coupes, menu de
    // profil multi-comptes et tuiles d'action.
    // Toutes les actions (connexion Epic, serveur, redirection, lancement) sont réelles.
    public class MainForm : Form
    {
        enum View { Home, Console, Accounts, Login }
        enum LoginPhase { Idle, Requesting, Waiting, Error }

        struct Hotspot
        {
            public RectangleF R;
            public string Id;
            public Action Act;
        }

        readonly AppConfig _config;
        readonly EpicAuth _epic;
        readonly LauncherPrefs _prefs;
        readonly AccountBook _accounts;

        EpicLoginResult _login;
        LoginPhase _phase = LoginPhase.Idle;
        DeviceAuthInfo _device;
        string _loginError = "";
        bool _addingAccount;

        View _view = View.Home;
        bool _profileOpen;
        bool _launching;
        bool _serverBusy;
        bool _privateBusy;
        bool _serverRunning;
        bool _privateOn;
        bool _polling;
        bool _restoring;
        bool _updating;
        bool _updateChecked;
        UpdateWindow _updateWindow;
        bool _sessionOffline;
        bool _statsLoaded;
        bool _statsOk;
        long _statsVb;
        int _statsLvl;
        string _statsName;
        int _loginGen;
        string _status = "";
        string _hot;
        float _anim;
        float _u = 1f;
        float _dpiScale = 1f;
        int _logScroll;
        DateTime _statusUntil;

        readonly List<string> _lines = new List<string>();
        readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        readonly List<Hotspot> _hits = new List<Hotspot>();
        RectangleF _hitClip = RectangleF.Empty;

        Timer _animTimer;
        Timer _pollTimer;
        NotifyIcon _tray;

        public MainForm()
        {
            _config = AppConfig.Load();
            _prefs = LauncherPrefs.Load();
            LauncherPrefs.Language_ = _prefs.Language == "en" ? "en" : "fr";
            _accounts = AccountBook.Load();
            _epic = new EpicAuth();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Window;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = "X.HYBRID V1";
            KeyPreview = true;
            Font = Theme.UI(9f);
            MaximizeBox = false;
            MinimizeBox = false;

            ComputeScale();
            ApplyRegion();

            _view = View.Home;

            try { _privateOn = Redirect.IsApplied(_config); } catch (Exception) { }

            Log("X.HYBRID V1 Launcher démarré.");
            Log(S("Langue : ", "Language: ") + (_prefs.Language == "en" ? "English" : "Français")
                + " · " + S("serveur privé Fortnite", "Fortnite private server"));

            BuildTimers();

            // relance du proxy SSL si le mode privé est déjà actif
            if (_privateOn && _config.LaunchRedirect)
            {
                Task.Run(delegate
                {
                    try
                    {
                        ProxyManager.Start();
                        Log("Proxy SSL relancé automatiquement (mode privé actif).");
                    }
                    catch (Exception) { }
                });
            }

            Shown += delegate
            {
                if (Environment.GetEnvironmentVariable("XHYBRID_PREVIEW") == null)
                {
                    RestoreSessionIfAny();
                    // aucune session enregistrée : on demande explicitement la connexion
                    if (_login == null && _accounts.Find(_accounts.ActiveId) == null) ShowLoginPrompt();
                    CheckForUpdates();
                }
                if (_prefs.OpenConsoleOnStart) OpenMini(MiniWindow.Kind.Console, DefaultAnchor());

                // le serveur privé doit tourner même sans session : le jeu en a besoin.
                // Premier lancement : la copie embarquée du serveur et le moteur Node.js
                // s'installent ici (une seule fois), le joueur n'a rien à préparer.
                if (_prefs.AutoStartServer && !ServerManager.IsRunning())
                    Task.Run(delegate
                    {
                        if (Environment.GetEnvironmentVariable("XHYBRID_PREVIEW") == null)
                            EnsureBundledServer();
                        PrepBackend();
                    });
            };

            var pv = Environment.GetEnvironmentVariable("XHYBRID_PREVIEW");
            if (!string.IsNullOrEmpty(pv)) ApplyPreview(pv);
        }

        // ================= fenêtre / échelle =================

        void ComputeScale()
        {
            float dpi;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            _dpiScale = dpi / 96f;

            var wa = Screen.PrimaryScreen.WorkingArea;
            float byWidth = wa.Width * 0.92f / 1140f;
            float byHeight = wa.Height * 0.90f / 680f;
            _u = Math.Min(_dpiScale, Math.Min(byWidth, byHeight));
            if (_u < 0.72f) _u = 0.72f;

            ClientSize = new Size((int)Math.Round(1140f * _u), (int)Math.Round(680f * _u));
        }

        int S(int v) { return (int)Math.Round(v * _u); }
        float S(float v) { return v * _u; }
        float SF(float v) { return v * _u; }

        void ApplyRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), S(12)))
                Region = new Region(p);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        // ================= polices =================

        Font FontOf(string family, float pt, FontStyle style)
        {
            float size = pt * _u / _dpiScale;
            if (size < 1f) size = 1f;
            string key = family + "|" + size.ToString("0.##") + "|" + (int)style;
            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;
            f = new Font(family, size, style, GraphicsUnit.Point);
            _fonts[key] = f;
            return f;
        }

        Font U(float pt) { return FontOf("Segoe UI", pt, FontStyle.Regular); }
        Font UB(float pt) { return FontOf("Segoe UI", pt, FontStyle.Bold); }
        Font US(float pt) { return FontOf("Segoe UI", pt, FontStyle.Bold | FontStyle.Italic); }
        Font Mono(float pt) { return FontOf("Consolas", pt, FontStyle.Regular); }
        Font MonoB(float pt) { return FontOf("Consolas", pt, FontStyle.Bold); }

        static string S(string fr, string en)
        {
            return LauncherPrefs.Language_ == "en" ? en : fr;
        }

        // ================= timers =================

        void BuildTimers()
        {
            _animTimer = new Timer();
            _animTimer.Interval = 50;
            _animTimer.Tick += delegate
            {
                _anim = (_anim + 6f) % 360f;
                if (_prefs.Animations) Invalidate();
            };
            if (_prefs.Animations) _animTimer.Start();

            _pollTimer = new Timer();
            _pollTimer.Interval = 3000;
            _pollTimer.Tick += delegate { PollStatus(); };
            _pollTimer.Start();
        }

        void PollStatus()
        {
            if (_polling) return;
            _polling = true;
            AppConfig cfg = _config;
            bool wasServer = _serverRunning;
            bool wasPrivate = _privateOn;
            Task.Run(delegate
            {
                bool run = false;
                bool priv = false;
                try { run = ServerManager.IsRunning(); } catch (Exception) { }
                try { priv = Redirect.IsApplied(cfg); } catch (Exception) { }

                // le proxy local doit toujours écouter : sinon on ne laisse pas
                // un réglage système pointant vers un port mort.
                try
                {
                    bool game = GameRunning();
                    if (_privateOn && !ProxyManager.IsListening() && !game)
                    {
                        string perr;
                        if (!ProxyManager.EnsureStarted(out perr) && ProxyManager.IsGlobal())
                        {
                            ProxyManager.DisableSystemProxy();
                            FileLog.Write("Proxy local perdu : réglage système restauré (" + perr + ")");
                        }
                    }
                }
                catch (Exception) { }
                Ui(delegate
                {
                    _polling = false;
                    bool changed = run != _serverRunning || priv != _privateOn
                                || wasServer != run || wasPrivate != priv;
                    _serverRunning = run;
                    _privateOn = priv;
                    if (changed) Invalidate();
                });
            });
        }

        // ================= journal =================

        void Log(string s)
        {
            Action add = delegate
            {
                _lines.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
                if (_lines.Count > 400) _lines.RemoveRange(0, _lines.Count - 400);
                FileLog.Write(s);
                if (_miniConsole != null && !_miniConsole.IsDisposed) _miniConsole.Invalidate();
            };
            if (InvokeRequired) BeginInvoke(add);
            else add();
        }

        void Status(string s)
        {
            _status = s ?? "";
            _statusUntil = DateTime.Now.AddSeconds(7);
            Invalidate();
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (Exception) { }
        }

        // exécution synchrone (nécessaire pour les dialogues modaux ouverts depuis un thread de fond)
        T UiInvoke<T>(Func<T> f)
        {
            if (IsDisposed) return default(T);
            if (InvokeRequired) return (T)Invoke(f);
            return f();
        }

        void Notify(string title, string text)
        {
            if (!_prefs.NotifySystem) return;
            try
            {
                if (_tray == null)
                {
                    _tray = new NotifyIcon();
                    _tray.Icon = SystemIcons.Application;
                    _tray.Text = "X.HYBRID V1";
                    _tray.Visible = true;
                }
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(4000);
            }
            catch (Exception) { }
        }

        // ================= rendu =================

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            _hits.Clear();
            _hitClip = RectangleF.Empty;

            int W = ClientSize.Width;
            int H = ClientSize.Height;
            if (W < 40 || H < 40) return;

            PaintAmbient(g, W, H);

            int titleH = S(54);
            int statusH = S(44);
            int pad = S(20);

            int contentTop = titleH + S(16);
            int contentBottom = H - statusH - S(8);

            int mainRight = W - pad;
            if (mainRight < pad + S(360)) mainRight = pad + S(360);

            int tilesH = S(78);
            int tilesTop = contentBottom - tilesH;
            int panelBottom = tilesTop - S(16);

            var panel = new RectangleF(pad, contentTop, mainRight - pad, panelBottom - contentTop);

            if (_profileOpen)
                Hit(new RectangleF(0, 0, W, H), "backdrop", delegate { _profileOpen = false; Invalidate(); });

            PaintPanel(g, panel);
            PaintContentView(g, panel);
            PaintTiles(g, new RectangleF(pad, tilesTop, mainRight - pad, tilesH));
            PaintTitleBar(g, W, titleH, pad);
            PaintStatusBar(g, W, H, statusH, pad);

            if (_profileOpen) PaintProfileMenu(g, W, titleH, pad);

            PaintFrame(g, W, H);
        }

        void PaintAmbient(Graphics g, int W, int H)
        {
            if (!_prefs.GlowBorders) return;

            using (var pen = new Pen(Color.FromArgb(7, 24, 44, 78)))
            {
                for (int x = 0; x < W; x += S(34))
                    g.DrawLine(pen, x, 0, x, H);
                for (int y = 0; y < H; y += S(34))
                    g.DrawLine(pen, 0, y, W, y);
            }

            Theme.Glow(g, new RectangleF(-S(240), H - S(420), S(640), S(640)), Theme.DeepBlue, 34);
            Theme.Glow(g, new RectangleF(W - S(420), -S(240), S(620), S(560)), Theme.DeepBlue, 26);
        }

        void PaintFrame(Graphics g, int W, int H)
        {
            var outer = new RectangleF(1.2f, 1.2f, W - 2.4f, H - 2.4f);
            using (var p = Theme.Round(outer, S(12)))
            {
                using (var glow = new Pen(Color.FromArgb(46, Theme.Bright), S(4)))
                    g.DrawPath(glow, p);
                using (var pen = new Pen(Color.FromArgb(255, 16, 108, 255), Math.Max(1.6f, S(2))))
                    g.DrawPath(pen, p);
                using (var inner = new Pen(Color.FromArgb(120, 26, 88, 168), Math.Max(0.8f, S(1))))
                using (var p2 = Theme.Round(new RectangleF(outer.X + S(2), outer.Y + S(2), outer.Width - S(4), outer.Height - S(4)), S(10)))
                    g.DrawPath(inner, p2);
            }
        }

        void PaintTitleBar(Graphics g, int W, int titleH, int pad)
        {
            // (barre noire, logo a gauche, compte + controles a droite)
            var bar = new RectangleF(0, 0, W, titleH + S(2));
            using (var br = new LinearGradientBrush(new RectangleF(0, 0, W, titleH + 2),
                Color.FromArgb(11, 17, 27), Color.FromArgb(5, 8, 14), 90f))
                g.FillRectangle(br, bar);

            Theme.GlowLine(g, new RectangleF(0, titleH - 1, W, S(2)), Theme.Electric, 190);
            using (var pen = new Pen(Color.FromArgb(70, 20, 62, 110)))
                g.DrawLine(pen, 0, titleH - S(2), W, titleH - S(2));

            // logo gauche + etiquette de version
            var logo = new RectangleF(pad + S(6), 0, S(150), titleH);
            var brandRect = DrawBrand(g, logo, S(136), S(16), StringAlignment.Near);
            var badge = new RectangleF(brandRect.Right + S(10), titleH / 2f - S(10), S(34), S(20));
            using (var p = Theme.Round(badge, S(5)))
            {
                using (var br = new SolidBrush(Color.FromArgb(24, 54, 98))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(185, 72, 162, 255), Math.Max(1f, S(1)))) g.DrawPath(pen, p);
            }
            Theme.Text(g, AppInfo.Badge, UB(7.4f), Color.FromArgb(178, 220, 255), badge,
                StringAlignment.Center, StringAlignment.Center, false);

            int right = W - pad - S(4);
            bool canDrag = true;

            // boutons fenêtre
            var closeR = new RectangleF(right - S(32), (titleH - S(32)) / 2f, S(32), S(32));
            PaintIconButton(g, closeR, "close", "win:close", Color.FromArgb(212, 228, 245), Theme.Red, delegate { Close(); });
            right = (int)closeR.X - S(2);

            var minR = new RectangleF(right - S(32), (titleH - S(32)) / 2f, S(32), S(32));
            PaintIconButton(g, minR, "min", "win:min", Color.FromArgb(212, 228, 245), Theme.Bright, delegate { WindowState = FormWindowState.Minimized; });
            right = (int)minR.X - S(14);

            // bloc profil
            bool on = _login != null;
            string name = on ? _login.DisplayName : S("Se connecter", "Sign in");
            var nameFont = U(10.2f);
            var nameSize = g.MeasureString(name, nameFont);
            float chipW = S(30) + S(10) + Math.Min(nameSize.Width, S(160)) + S(8) + S(12) + S(12);
            var chip = new RectangleF(right - chipW, (titleH - S(36)) / 2f, chipW, S(36));

            if (_hot == "profile") PaintHover(g, chip, S(8));
            DrawAvatar(g, new RectangleF(chip.X + S(1), chip.Y + S(3), S(30), S(30)), on, S(30));
            Theme.Text(g, name, nameFont, on ? Theme.Ink : Theme.Dim,
                new RectangleF(chip.X + S(39), chip.Y, chip.Width - S(66), chip.Height));
            Theme.Icon(g, "chevron-down", new RectangleF(chip.Right - S(23), chip.Y + S(12), S(12), S(12)), Theme.Dim, 1.2f);
            Hit(chip, "profile", delegate
            {
                if (_login == null) StartLogin(false);
                else { _profileOpen = !_profileOpen; Invalidate(); }
            });

            // zone de deplacement (toute la barre restante)
            if (canDrag)
                Hit(new RectangleF(S(6), S(4), chip.X - S(12), titleH - S(8)), "drag", DragWindow);
        }

        void PaintHover(Graphics g, RectangleF r, int radius)
        {
            using (var p = Theme.Round(r, S(radius)))
            using (var br = new SolidBrush(Color.FromArgb(18, 32, 52)))
                g.FillPath(br, p);
        }

        void PaintIconButton(Graphics g, RectangleF r, string icon, string id, Color normal, Color accent, Action act)
        {
            bool hot = _hot == id;
            if (hot)
            {
                using (var p = Theme.Round(r, S(7)))
                using (var br = new SolidBrush(Color.FromArgb(22, 30, 46)))
                    g.FillPath(br, p);
            }
            Theme.Icon(g, icon, new RectangleF(r.X + r.Width / 2f - S(8), r.Y + r.Height / 2f - S(8), S(16), S(16)),
                hot ? accent : normal, 1.4f);
            Hit(r, id, act);
        }

        void DrawAvatar(Graphics g, RectangleF r, bool on, int diameter)
        {
            float d = Math.Min(r.Width, r.Height);
            var circle = new RectangleF(r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
            using (var br = new SolidBrush(on ? Color.FromArgb(17, 48, 90) : Color.FromArgb(9, 20, 36)))
                g.FillEllipse(br, circle);
            using (var pen = new Pen(on ? Color.FromArgb(240, 0, 138, 255) : Color.FromArgb(205, 18, 110, 226),
                Math.Max(1.2f, S(1.5f))))
                g.DrawEllipse(pen, circle);

            Theme.Icon(g, "user",
                new RectangleF(circle.X + d * 0.24f, circle.Y + d * 0.21f, d * 0.52f, d * 0.55f),
                on ? Color.FromArgb(238, 246, 252) : Color.FromArgb(150, 190, 225), 1.7f);
        }

        // ================= grand logo =================

        RectangleF DrawBrand(Graphics g, RectangleF area, float maxWidth, float maxPt, StringAlignment align)
        {
            float size = maxPt;
            const string text = "X.HYBRID";

            using (var probe = Theme.Brand(size))
            {
                float w = MeasureBrand(g, probe, text);
                if (w > 0.5f && maxWidth > 1f) size = size * maxWidth / w;
            }

            using (var f = Theme.Brand(size))
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                sf.FormatFlags &= ~StringFormatFlags.LineLimit;
                sf.Trimming = StringTrimming.None;

                var sz = g.MeasureString(text, f, new PointF(0, 0), sf);
                float x = align == StringAlignment.Center ? area.X + (area.Width - sz.Width) / 2f : area.X;
                float y = area.Y + (area.Height - sz.Height) / 2f;

                float wX = MeasureBrand(g, f, "X.");
                float ratio = sz.Width > 0.5f ? Math.Max(0.06f, Math.Min(0.94f, wX / sz.Width)) : 0.16f;

                if (_prefs.GlowBorders)
                    Theme.Glow(g, new RectangleF(x + sz.Width * 0.08f, y + sz.Height * 0.18f,
                        sz.Width * 0.84f, sz.Height * 0.86f), Theme.Electric, 24);

                using (var br = new LinearGradientBrush(new RectangleF(x, y, Math.Max(1f, sz.Width), Math.Max(1f, sz.Height)),
                    Theme.Bright, Color.White, 0f))
                {
                    var blend = new ColorBlend(4);
                    blend.Positions = new float[] { 0f, Math.Max(0.02f, ratio * 0.7f), ratio, 1f };
                    blend.Colors = new Color[]
                    {
                        Color.FromArgb(126, 200, 255),
                        Theme.Electric,
                        Color.FromArgb(250, 252, 255),
                        Color.FromArgb(198, 216, 240)
                    };
                    br.InterpolationColors = blend;
                    g.DrawString(text, f, br, new RectangleF(x - S(2), y, sz.Width + S(4), sz.Height), sf);
                }

                return new RectangleF(x, y, sz.Width, sz.Height);
            }
        }

        static float MeasureBrand(Graphics g, Font f, string text)
        {
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.None;
                return g.MeasureString(text, f, new PointF(0, 0), sf).Width;
            }
        }

        // ================= panneau central =================

        void PaintPanel(Graphics g, RectangleF panel)
        {
            var path = Theme.Chamfer(panel, S(22), S(30), S(34), S(54));
            using (path)
            {
                using (var br = new LinearGradientBrush(panel,
                    Color.FromArgb(11, 23, 40), Color.FromArgb(5, 8, 14), 90f))
                    g.FillPath(br, path);

                if (_prefs.GlowBorders)
                {
                    var save = g.Save();
                    using (var clip = new Region(path)) g.SetClip(clip, CombineMode.Intersect);
                    Theme.Glow(g, new RectangleF(panel.X + panel.Width * 0.24f, panel.Y - panel.Height * 0.25f,
                        panel.Width * 0.52f, panel.Height * 0.62f), Theme.Electric, 30);
                    g.Restore(save);
                }

                using (var pen = new Pen(Color.FromArgb(215, 32, 96, 164), Math.Max(1f, S(1.4f))))
                    g.DrawPath(pen, path);
            }

            PaintPanelAccents(g, panel);
        }

        void PaintPanelAccents(Graphics g, RectangleF panel)
        {
            float lw = Math.Max(1.2f, S(1.5f));
            using (var pen = new Pen(Color.FromArgb(230, Theme.Bright), lw))
            {
                // aretes lumineuses
                g.DrawLine(pen, panel.X + S(78), panel.Y + S(1), panel.X + panel.Width * 0.3f, panel.Y + S(1));
                g.DrawLine(pen, panel.Right - panel.Width * 0.28f, panel.Bottom - S(1), panel.Right - S(96), panel.Bottom - S(1));
                // aretes coupees
                g.DrawLine(pen, panel.X + S(2), panel.Y + S(21), panel.X + S(21), panel.Y + S(2));
                g.DrawLine(pen, panel.X + S(2), panel.Bottom - S(54), panel.X + S(54), panel.Bottom - S(2));
                g.DrawLine(pen, panel.Right - S(35), panel.Bottom - S(2), panel.Right - S(66), panel.Bottom - S(33));
                // barres verticales a droite
                g.DrawLine(pen, panel.Right - S(1), panel.Y + panel.Height * 0.13f, panel.Right - S(1), panel.Y + panel.Height * 0.33f);
                g.DrawLine(pen, panel.Right - S(1), panel.Y + panel.Height * 0.67f, panel.Right - S(1), panel.Y + panel.Height * 0.85f);
            }

            using (var pen = new Pen(Color.FromArgb(50, Theme.Electric), S(5)))
                g.DrawLine(pen, panel.X + S(4), panel.Bottom - S(52), panel.X + S(52), panel.Bottom - S(4));
            using (var pen = new Pen(Color.FromArgb(40, Theme.Electric), S(5)))
                g.DrawLine(pen, panel.Right - S(40), panel.Y + panel.Height * 0.14f, panel.Right, panel.Y + panel.Height * 0.26f);
        }

        void PaintContentView(Graphics g, RectangleF panel)
        {
            var save = g.Save();
            using (var p = Theme.Chamfer(panel, S(22), S(30), S(34), S(54)))
            using (var clip = new Region(p))
            {
                g.SetClip(clip, CombineMode.Intersect);
                switch (_view)
                {
                    case View.Login: PaintLogin(g, panel); break;
                    case View.Console: PaintConsole(g, panel); break;
                    case View.Accounts: PaintAccounts(g, panel); break;
                    default: PaintHome(g, panel); break;
                }
            }
            g.Restore(save);
        }

        void PaintHome(Graphics g, RectangleF panel)
        {
            bool on = _login != null;
            float targetW = Math.Min(panel.Width * 0.52f, SF(560));
            float logoH = Math.Max(SF(56), panel.Height * 0.215f);

            var logoArea = new RectangleF(panel.X + (panel.Width - targetW) / 2f,
                panel.Y + panel.Height * 0.085f, targetW, logoH);
            var drawn = DrawBrand(g, logoArea, targetW, SF(86), StringAlignment.Center);

            if (_prefs.GlowBorders)
                Theme.Glow(g, new RectangleF(panel.X + panel.Width * 0.3f, drawn.Bottom - S(8), panel.Width * 0.4f, S(40)),
                    Theme.Electric, 26);

            Theme.Text(g, S("LAUNCHER FORTNITE · SERVEUR PRIVÉ", "FORTNITE LAUNCHER · PRIVATE SERVER"),
                UB(7.2f), Color.FromArgb(104, 170, 236),
                new RectangleF(panel.X + S(40), drawn.Bottom + S(1), panel.Width - S(80), S(16)),
                StringAlignment.Center, StringAlignment.Center, false);

            if (_prefs.GlowBorders)
            {
                float lineW = Math.Min(panel.Width * 0.44f, targetW * 0.92f);
                Theme.GlowLine(g, new RectangleF(panel.X + (panel.Width - lineW) / 2f, drawn.Bottom + S(21), lineW, S(4)),
                    Theme.Bright, 150);
            }

            // ---- bandeau d'état : compte, serveur privé, mode ----
            string server = _serverBusy ? S("Opération…", "Working…")
                          : _serverRunning ? S("En ligne", "Online")
                          : S("Arrêté", "Stopped");
            string mode = _privateBusy ? S("Application…", "Applying…")
                        : _privateOn ? S("Privé · SSL", "Private · SSL")
                        : S("En ligne · Epic", "Online · Epic");

            float stripW = Math.Min(panel.Width - S(96), SF(660));
            var strip = new RectangleF(panel.X + (panel.Width - stripW) / 2f,
                panel.Y + panel.Height * 0.40f, stripW, S(64));

            DrawStatusStrip(g, strip,
                new string[] { "user", "server", "shield" },
                new string[] { S("COMPTE", "ACCOUNT"), S("SERVEUR PRIVÉ", "PRIVATE SERVER"), S("MODE", "MODE") },
                new string[]
                {
                    on ? _login.DisplayName : S("Non connecté", "Not signed in"),
                    server,
                    mode
                },
                new Color[]
                {
                    on ? (_sessionOffline ? Theme.Amber : Theme.Green) : Theme.Amber,
                    _serverRunning ? Theme.Green : (_privateOn ? Theme.Amber : Color.FromArgb(132, 154, 180)),
                    _privateOn ? Theme.Bright : Theme.Green
                });

            // ---- accroche ----
            string line1 = on
                ? string.Format(S("Bienvenue sur X.HYBRID V1, {0}.", "Welcome to X.HYBRID V1, {0}."), _login.DisplayName)
                : S("Connecte-toi pour accéder à ton compte", "Sign in to access your account");
            string line2 = on
                ? S("Ton compte Epic Games est connecté.", "Your Epic Games account is connected.")
                : S("et profiter de toutes les fonctionnalités.", "and unlock every feature.");

            float textY = panel.Y + panel.Height * 0.605f;
            Theme.Text(g, line1, U(11.2f), Color.FromArgb(232, 239, 250),
                new RectangleF(panel.X + S(40), textY, panel.Width - S(80), S(24)), StringAlignment.Center, StringAlignment.Center, false);
            Theme.Text(g, line2, U(11.2f), Color.FromArgb(214, 226, 242),
                new RectangleF(panel.X + S(40), textY + S(23), panel.Width - S(80), S(24)), StringAlignment.Center, StringAlignment.Center, false);

            float btnW = Math.Min(SF(400), panel.Width - S(140));
            var btn = new RectangleF(panel.X + (panel.Width - btnW) / 2f, panel.Y + panel.Height * 0.748f, btnW, S(52));

            if (!on)
                PaintPrimary(g, btn, S("SE CONNECTER POUR JOUER", "SIGN IN TO PLAY"), "user", "home:login", true, delegate { StartLogin(false); });
            else if (_privateOn)
                PaintPrimary(g, btn, S("JOUER (SERVEUR PRIVÉ)", "PLAY (PRIVATE SERVER)"), "gamepad", "home:play", true, LaunchGame);
            else
                PaintPrimary(g, btn, S("JOUER EN LIGNE", "PLAY ONLINE"), "gamepad", "home:play", true, LaunchGame);

            string sub;
            if (!on)
                sub = S("Connexion sécurisée Epic Games · aucun mot de passe stocké", "Secure Epic Games sign-in · no password stored");
            else if (!_privateOn)
                sub = S("⇄ Mode en ligne · ton compte Epic Games · vraies parties (Créatif, BR…)",
                        "⇄ Online mode · your Epic Games account · real matches (Creative, BR…)");
            else
                sub = (_serverRunning ? S("Serveur privé en ligne", "Private server online")
                                      : S("Serveur privé arrêté", "Private server offline"))
                      + " · " + S("⇄ mode privé : casier et boutique, pas de partie",
                                  "⇄ private mode: locker and shop, no match");

            var subRect = new RectangleF(panel.X + S(40), btn.Bottom + S(18), panel.Width - S(80), S(18));
            Theme.Text(g, sub, U(8.4f), Theme.Dim2, subRect,
                StringAlignment.Center, StringAlignment.Center, false);

            // Le changement de mode se fait dans la tuile « Mode privé » : un bouton
            // en page d'accueil trop proche du bouton de lancement réarmait la
            // redirection hosts par erreur.
        }

        // bandeau d'information (et non des boutons) : trois segments séparés
        void DrawStatusStrip(Graphics g, RectangleF r, string[] icons, string[] labels, string[] values, Color[] accents)
        {
            using (var p = Theme.Round(r, S(10)))
            {
                using (var br = new LinearGradientBrush(r, Color.FromArgb(9, 21, 37), Color.FromArgb(6, 12, 21), 90f))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(100, 26, 62, 104), Math.Max(1f, S(1))))
                    g.DrawPath(pen, p);
            }

            float segW = r.Width / 3f;
            for (int i = 0; i < 3; i++)
            {
                var seg = new RectangleF(r.X + segW * i, r.Y, segW, r.Height);

                if (i > 0)
                    using (var pen = new Pen(Color.FromArgb(64, 30, 70, 118)))
                        g.DrawLine(pen, seg.X, r.Y + S(15), seg.X, r.Bottom - S(15));

                Theme.Icon(g, icons[i], new RectangleF(seg.X + S(18), r.Y + (r.Height - S(20)) / 2f, S(20), S(20)),
                    Theme.Alpha(accents[i], 220), 1.6f);

                Theme.Text(g, labels[i], UB(6.8f), Color.FromArgb(124, 154, 192),
                    new RectangleF(seg.X + S(50), r.Y + S(13), seg.Width - S(64), S(14)));
                Theme.Text(g, values[i], UB(9.2f), Theme.Mix(accents[i], Theme.Ink, 0.62f),
                    new RectangleF(seg.X + S(50), r.Y + S(29), seg.Width - S(64), S(22)));
            }
        }

        void PaintLogin(Graphics g, RectangleF panel)
        {
            float logoW = Math.Min(panel.Width * 0.3f, SF(300));
            DrawBrand(g, new RectangleF(panel.X + (panel.Width - logoW) / 2f, panel.Y + S(46), logoW, S(44)),
                logoW, SF(26), StringAlignment.Center);

            Theme.Text(g, S("CONNEXION", "SIGN IN"), UB(15f), Theme.Ink,
                new RectangleF(panel.X, panel.Y + S(112), panel.Width, S(26)), StringAlignment.Center, StringAlignment.Center, false);
            Theme.Text(g, S("Epic Games · code d'activation", "Epic Games · activation code"), U(8.6f), Theme.Dim,
                new RectangleF(panel.X, panel.Y + S(138), panel.Width, S(20)), StringAlignment.Center, StringAlignment.Center, false);

            float cx = panel.X + panel.Width / 2f;

            if (_phase == LoginPhase.Requesting)
            {
                DrawSpinner(g, new RectangleF(cx - S(14), panel.Y + panel.Height * 0.42f, S(28), S(28)));
                Theme.Text(g, S("Demande d'un code à Epic Games…", "Requesting a code from Epic Games…"), U(10f), Theme.Ink,
                    new RectangleF(panel.X + S(40), panel.Y + panel.Height * 0.42f + S(46), panel.Width - S(80), S(22)),
                    StringAlignment.Center, StringAlignment.Center, false);
            }
            else if (_phase == LoginPhase.Waiting && _device != null)
            {
                Theme.Text(g, S("Ouvre la page Epic Games et valide ce code :", "Open the Epic Games page and approve this code:"),
                    U(9.6f), Theme.Dim, new RectangleF(panel.X + S(40), panel.Y + panel.Height * 0.34f, panel.Width - S(80), S(22)),
                    StringAlignment.Center, StringAlignment.Center, false);

                var box = new RectangleF(cx - S(140), panel.Y + panel.Height * 0.34f + S(32), S(280), S(62));
                using (var p = Theme.Round(box, S(8)))
                {
                    using (var br = new SolidBrush(Theme.Field)) g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(200, Theme.Electric), Math.Max(1f, S(1.2f)))) g.DrawPath(pen, p);
                }
                Theme.Text(g, _device.UserCode, MonoB(24f), Color.FromArgb(120, 194, 255),
                    box, StringAlignment.Center, StringAlignment.Center, false);

                var open = new RectangleF(cx - S(190), box.Bottom + S(18), S(184), S(38));
                var copy = new RectangleF(cx + S(6), box.Bottom + S(18), S(184), S(38));
                PaintGhost(g, open, S("OUVRIR LA PAGE EPIC", "OPEN EPIC PAGE"), "external", "login:open", true,
                    delegate { try { Process.Start(_device.VerificationUriComplete); } catch (Exception) { } });
                PaintGhost(g, copy, S("COPIER LE CODE", "COPY CODE"), "copy", "login:copy", true,
                    delegate
                    {
                        try { Clipboard.SetText(_device.UserCode); Log(S("Code copié.", "Code copied.")); }
                        catch (Exception) { }
                    });

                DrawSpinner(g, new RectangleF(cx - S(10), copy.Bottom + S(20), S(20), S(20)));
                Theme.Text(g, S("En attente de validation…", "Waiting for approval…"), U(8.6f), Theme.Dim,
                    new RectangleF(panel.X + S(40), copy.Bottom + S(46), panel.Width - S(80), S(22)),
                    StringAlignment.Center, StringAlignment.Center, false);

                var cancel = new RectangleF(cx - S(100), copy.Bottom + S(68), S(200), S(36));
                PaintGhost(g, cancel, S("ANNULER", "CANCEL"), "close", "login:cancel", true, CancelLogin);
            }
            else if (_phase == LoginPhase.Idle)
            {
                var msg = new RectangleF(panel.X + S(56), panel.Y + panel.Height * 0.31f,
                    panel.Width - S(112), S(58));
                Theme.Text(g, S("Connecte-toi à Epic Games pour jouer et retrouver ton compte.",
                    "Sign in to Epic Games to play and load your account."),
                    U(10.4f), Theme.Ink, msg, StringAlignment.Center, StringAlignment.Center, true);

                var hint = new RectangleF(panel.X + S(56), msg.Bottom + S(2), panel.Width - S(112), S(20));
                Theme.Text(g, S("Aucune session enregistrée sur ce PC.", "No saved session on this PC."),
                    U(8.4f), Theme.Dim2, hint, StringAlignment.Center, StringAlignment.Center, false);

                var start = new RectangleF(cx - S(190), panel.Y + panel.Height * 0.52f, S(380), S(50));
                PaintPrimary(g, start, S("SE CONNECTER AVEC EPIC", "SIGN IN WITH EPIC"), "user", "login:start", true,
                    delegate { StartLogin(false); });

                var later = new RectangleF(cx - S(110), start.Bottom + S(12), S(220), S(38));
                PaintGhost(g, later, S("PLUS TARD", "LATER"), "arrow-left", "login:later", true,
                    delegate
                    {
                        _phase = LoginPhase.Idle;
                        _view = View.Home;
                        _loginError = "";
                        Status("");
                        Invalidate();
                    });
            }
            else
            {
                string msg = string.IsNullOrEmpty(_loginError)
                    ? S("La connexion à Epic Games a échoué.", "Epic Games sign-in failed.")
                    : _loginError;

                var box = new RectangleF(panel.X + S(56), panel.Y + panel.Height * 0.34f, panel.Width - S(112), S(74));
                using (var p = Theme.Round(box, S(8)))
                {
                    using (var br = new SolidBrush(Color.FromArgb(26, 42, 20, 32))) g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(150, Theme.Red), Math.Max(1f, S(1.1f)))) g.DrawPath(pen, p);
                }
                Theme.Icon(g, "info", new RectangleF(box.X + S(16), box.Y + S(26), S(22), S(22)), Theme.Red, 1.6f);
                Theme.Text(g, msg, U(9.2f), Color.FromArgb(255, 196, 205),
                    new RectangleF(box.X + S(46), box.Y + S(8), box.Width - S(62), box.Height - S(16)),
                    StringAlignment.Near, StringAlignment.Center, true);

                var retry = new RectangleF(cx - S(190), box.Bottom + S(22), S(184), S(44));
                var back = new RectangleF(cx + S(6), box.Bottom + S(22), S(184), S(44));
                PaintPrimary(g, retry, S("RÉESSAYER", "RETRY"), "refresh", "login:retry", true, delegate { StartLogin(_addingAccount); });
                PaintGhost(g, back, S("RETOUR", "BACK"), "arrow-left", "login:back", true, delegate
                {
                    _phase = LoginPhase.Idle; _view = View.Home; _loginError = ""; Status(""); Invalidate();
                });
            }

            var noteRow = new RectangleF(panel.X + S(40), panel.Bottom - S(34), panel.Width - S(80), S(20));
            Theme.Icon(g, "lock", new RectangleF(cx - S(120), noteRow.Y + S(3), S(14), S(14)), Theme.Dim2, 1.4f);
            Theme.Text(g, S("Aucun mot de passe stocké · jeton de session chiffré (DPAPI)", "No password stored · session token encrypted (DPAPI)"),
                U(8f), Theme.Dim2, new RectangleF(cx - S(100), noteRow.Y, S(300), S(20)),
                StringAlignment.Near, StringAlignment.Center, false);
        }

        void DrawSpinner(Graphics g, RectangleF r)
        {
            using (var pen = new Pen(Color.FromArgb(70, Theme.Electric), Math.Max(1.5f, S(2))))
                g.DrawEllipse(pen, r);
            using (var pen = new Pen(Theme.Bright, Math.Max(1.8f, S(2.4f))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, r, _anim, 110f);
            }
        }

        void PaintConsole(Graphics g, RectangleF panel)
        {
            float x = panel.X + S(30);
            float w = panel.Width - S(60);
            float y = panel.Y + S(24);

            Theme.Icon(g, "terminal", new RectangleF(x, y + S(2), S(18), S(18)), Theme.Bright, 1.6f);
            var consoleTitleFont = UB(11.5f);
            Theme.Text(g, "CONSOLE", consoleTitleFont, Theme.Ink, new RectangleF(x + S(26), y, S(160), S(22)));
            float consoleTitleW = g.MeasureString("CONSOLE", consoleTitleFont).Width;
            Theme.Text(g, S("Journal du launcher en temps réel", "Launcher log in real time"), U(8.2f), Theme.Dim2,
                new RectangleF(x + S(26) + consoleTitleW + S(14), y + S(2), Math.Max(S(60), w - consoleTitleW - S(240)), S(18)));

            var copy = new RectangleF(x + w - S(172), y - S(4), S(82), S(30));
            var clear = new RectangleF(x + w - S(84), y - S(4), S(82), S(30));
            PaintSmall(g, copy, S("COPIER", "COPY"), "console:copy", delegate
            {
                try
                {
                    if (_lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, _lines.ToArray()));
                }
                catch (Exception) { }
            });
            PaintSmall(g, clear, S("EFFACER", "CLEAR"), "console:clear", delegate
            {
                _lines.Clear(); _logScroll = 0; Invalidate();
            });

            var box = new RectangleF(x, y + S(40), w, panel.Bottom - y - S(64));
            using (var p = Theme.Round(box, S(8)))
            {
                using (var br = new SolidBrush(Color.FromArgb(4, 8, 13))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(150, 20, 56, 96))) g.DrawPath(pen, p);
            }

            var txt = new RectangleF(box.X + S(14), box.Y + S(12), box.Width - S(28), box.Height - S(24));
            var save = g.Save();
            g.SetClip(txt);
            var f = Mono(9f);
            float lh = f.GetHeight(g) + S(3);

            var visual = new List<string>();
            foreach (var line in _lines)
                visual.AddRange(Wrap(g, line, f, txt.Width));

            int visible = Math.Max(1, (int)(txt.Height / lh));
            int maxScroll = Math.Max(0, visual.Count - visible);
            if (_logScroll > maxScroll) _logScroll = maxScroll;
            if (_logScroll < 0) _logScroll = 0;

            int start = Math.Max(0, visual.Count - visible - _logScroll);
            float ly = txt.Y;
            for (int i = start; i < visual.Count && ly < txt.Bottom; i++)
            {
                Theme.Text(g, visual[i], f, LogColor(visual[i]), new RectangleF(txt.X, ly, txt.Width, lh));
                ly += lh;
            }

            if (visual.Count == 0)
                Theme.Text(g, S("Aucune entrée pour le moment.", "No entries yet."), U(9f), Theme.Dim2, txt);
            g.Restore(save);

            if (maxScroll > 0)
            {
                var track = new RectangleF(box.Right - S(6), box.Y + S(8), S(3), box.Height - S(16));
                using (var br = new SolidBrush(Color.FromArgb(40, 60, 90, 130))) g.FillRectangle(br, track);
                int shown = Math.Max(1, visible);
                float thumbH = Math.Max(S(18), track.Height * shown / Math.Max(shown, visual.Count));
                float t = maxScroll == 0 ? 0 : (_logScroll / (float)maxScroll);
                var thumb = new RectangleF(track.X, track.Bottom - thumbH - (track.Height - thumbH) * (1f - t), track.Width, thumbH);
                using (var br = new SolidBrush(Color.FromArgb(120, 40, 120, 210))) g.FillRectangle(br, thumb);
            }
        }

        static List<string> Wrap(Graphics g, string text, Font f, float maxW)
        {
            var res = new List<string>();
            if (text == null) return res;
            if (maxW <= 4f) { res.Add(text); return res; }

            foreach (var raw in text.Split('\n'))
            {
                if (raw.Length == 0) { res.Add(""); continue; }
                var words = raw.Split(' ');
                string cur = "";
                foreach (var word in words)
                {
                    string test = cur.Length == 0 ? word : cur + " " + word;
                    if (g.MeasureString(test, f).Width > maxW && cur.Length > 0)
                    {
                        res.Add(cur);
                        cur = word;
                    }
                    else cur = test;
                }
                res.Add(cur);
            }
            return res;
        }

        static Color LogColor(string line)
        {
            if (line.IndexOf("ERREUR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("échec", StringComparison.OrdinalIgnoreCase) >= 0) return Theme.Red;
            if (line.IndexOf("[serveur]", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(120, 196, 255);
            if (line.IndexOf("prêt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("Connecté", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("actif", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(124, 224, 164);
            return Color.FromArgb(158, 180, 205);
        }

        void PaintAccounts(Graphics g, RectangleF panel)
        {
            float x = panel.X + S(30);
            float w = panel.Width - S(60);
            float y = panel.Y + S(24);

            Theme.Icon(g, "user", new RectangleF(x, y + S(2), S(18), S(18)), Theme.Bright, 1.6f);
            string accountsTitle = S("COMPTES EPIC", "EPIC ACCOUNTS");
            var accountsTitleFont = UB(11.5f);
            Theme.Text(g, accountsTitle, accountsTitleFont, Theme.Ink, new RectangleF(x + S(26), y, S(280), S(22)));
            float accountsTitleW = g.MeasureString(accountsTitle, accountsTitleFont).Width;
            Theme.Text(g, S("Une seule session à la fois sur ce PC", "One session at a time on this PC"), U(8.2f), Theme.Dim2,
                new RectangleF(x + S(26) + accountsTitleW + S(14), y + S(2), Math.Max(S(60), w - accountsTitleW - S(60)), S(18)));

            y += S(50);

            if (_login == null)
            {
                float available = panel.Bottom - y - S(18);
                float cardH = Math.Min(S(238), available);
                float cardY = y + Math.Max(0f, (available - cardH) / 2f);
                var card = new RectangleF(x, cardY, w, cardH);
                using (var p = Theme.Round(card, S(12)))
                {
                    using (var br = new LinearGradientBrush(card, Color.FromArgb(12, 26, 46), Color.FromArgb(6, 11, 20), 90f))
                        g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(150, 24, 64, 112), Math.Max(1f, S(1)))) g.DrawPath(pen, p);
                }

                var circle = new RectangleF(card.X + (card.Width - S(56)) / 2f, card.Y + S(28), S(56), S(56));
                using (var br = new SolidBrush(Color.FromArgb(10, 24, 42))) g.FillEllipse(br, circle);
                using (var pen = new Pen(Color.FromArgb(190, 46, 128, 216), Math.Max(1.2f, S(1.4f)))) g.DrawEllipse(pen, circle);
                Theme.Icon(g, "user", new RectangleF(circle.X + S(14), circle.Y + S(12), S(28), S(30)),
                    Color.FromArgb(150, 196, 246), 1.6f);

                Theme.Text(g, S("AUCUN COMPTE CONNECTÉ", "NO ACCOUNT SIGNED IN"), UB(11.5f), Theme.Ink,
                    new RectangleF(card.X + S(24), circle.Bottom + S(14), card.Width - S(48), S(24)),
                    StringAlignment.Center, StringAlignment.Center, false);
                Theme.Text(g, S("Connecte-toi avec Epic Games pour lancer Fortnite et retrouver ton profil.",
                                "Sign in with Epic Games to launch Fortnite and get your profile back."),
                    U(8.8f), Theme.Dim2,
                    new RectangleF(card.X + S(48), circle.Bottom + S(40), card.Width - S(96), S(32)),
                    StringAlignment.Center, StringAlignment.Center, true);

                float cw = Math.Min(SF(300), card.Width - S(64));
                var connect = new RectangleF(card.X + (card.Width - cw) / 2f, card.Bottom - S(58), cw, S(44));
                PaintPrimary(g, connect, S("SE CONNECTER AVEC EPIC", "SIGN IN WITH EPIC"), "plus-circle", "acct:add", true,
                    delegate { StartLogin(false); });
                return;
            }

            bool wide = w >= S(520);
            float leftW = wide ? w * 0.58f : w;
            float cardHh = S(160);
            var card2 = new RectangleF(x, y, leftW, cardHh);

            using (var p = Theme.Round(card2, S(12)))
            {
                using (var br = new LinearGradientBrush(card2, Color.FromArgb(15, 36, 64), Color.FromArgb(6, 12, 22), 30f))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(168, 30, 94, 166), Math.Max(1f, S(1.2f)))) g.DrawPath(pen, p);
            }

            DrawAvatar(g, new RectangleF(card2.X + S(18), card2.Y + S(20), S(56), S(56)), true, S(56));
            Theme.Text(g, _login.DisplayName, UB(13f), Theme.Ink,
                new RectangleF(card2.X + S(88), card2.Y + S(19), card2.Width - S(196), S(24)));
            using (var br = new SolidBrush(_sessionOffline ? Theme.Amber : Theme.Green))
                g.FillEllipse(br, card2.X + S(89), card2.Y + S(50), S(7), S(7));
            Theme.Text(g, _sessionOffline ? S("Session locale", "Local session") : S("Session active", "Active session"),
                U(8.4f), _sessionOffline ? Theme.Amber : Theme.Green,
                new RectangleF(card2.X + S(101), card2.Y + S(45), S(120), S(18)));
            Theme.Text(g, "ID " + ShortId(_login.AccountId), U(8.2f), Theme.Dim2,
                new RectangleF(card2.X + S(88), card2.Y + S(64), card2.Width - S(196), S(18)));

            var copy = new RectangleF(card2.Right - S(102), card2.Y + S(56), S(84), S(26));
            PaintSmall(g, copy, S("COPIER L'ID", "COPY ID"), "acct:copyid", delegate
            {
                try { Clipboard.SetText(_login.AccountId); Status(S("ID copié.", "ID copied.")); }
                catch (Exception) { }
            });

            using (var pen = new Pen(Color.FromArgb(85, 28, 64, 108)))
                g.DrawLine(pen, card2.X + S(18), card2.Y + S(98), card2.Right - S(18), card2.Y + S(98));

            Theme.Icon(g, "shield", new RectangleF(card2.X + S(18), card2.Y + S(112), S(16), S(16)),
                Color.FromArgb(150, 190, 230), 1.5f);
            Theme.Text(g, S("Compte Epic Games vérifié · jeton chiffré", "Verified Epic Games account · encrypted token"),
                U(8.1f), Color.FromArgb(168, 188, 212),
                new RectangleF(card2.X + S(42), card2.Y + S(110), card2.Width - S(184), S(20)));

            var logout = new RectangleF(card2.Right - S(132), card2.Y + S(105), S(114), S(30));
            PaintSmall(g, logout, S("DÉCONNEXION", "SIGN OUT"), "acct:logout", Logout);

            if (wide)
            {
                var sec = new RectangleF(card2.Right + S(16), y, w - leftW - S(16), cardHh);
                using (var p = Theme.Round(sec, S(12)))
                {
                    using (var br = new SolidBrush(Color.FromArgb(7, 14, 25))) g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(140, 22, 54, 96), Math.Max(1f, S(1)))) g.DrawPath(pen, p);
                }

                Theme.Text(g, S("SÉCURITÉ DE LA SESSION", "SESSION SECURITY"), UB(7.6f),
                    Color.FromArgb(200, 96, 176, 255),
                    new RectangleF(sec.X + S(18), sec.Y + S(14), sec.Width - S(36), S(16)));

                AccountFact(g, sec, sec.Y + S(44), "lock", S("Jeton chiffré via DPAPI", "Token encrypted with DPAPI"));
                AccountFact(g, sec, sec.Y + S(74), "user", S("Aucun mot de passe stocké", "No password stored"));
                AccountFact(g, sec, sec.Y + S(104), "globe", S("Connexion via un code Epic", "Sign-in through an Epic code"));
            }

            // ---- profil du serveur privé (lecture seule) ----
            RefreshProfileStats();

            float sy = card2.Bottom + S(26);
            Theme.Text(g, S("PROFIL DU SERVEUR PRIVÉ", "PRIVATE SERVER PROFILE"), UB(7.6f),
                Color.FromArgb(200, 96, 176, 255), new RectangleF(x + S(2), sy, w, S(16)));
            sy += S(23);

            float statW = (w - S(24)) / 3f;
            DrawStatTile(g, new RectangleF(x, sy, statW, S(70)), "V-BUCKS",
                _statsOk ? _statsVb.ToString("N0") : "—", S("Monnaie du compte", "Account currency"));
            DrawStatTile(g, new RectangleF(x + statW + S(12), sy, statW, S(70)), S("NIVEAU", "LEVEL"),
                _statsOk ? _statsLvl.ToString() : "—", S("Niveau de compte", "Account level"));
            DrawStatTile(g, new RectangleF(x + (statW + S(12)) * 2f, sy, statW, S(70)), S("PSEUDO", "NAME"),
                _statsOk && !string.IsNullOrEmpty(_statsName) ? _statsName : "—", S("Nom sur le serveur", "Server name"));
            sy += S(82);

            sy = InfoNote(g, x, sy, w, _statsOk
                ? S("Ces valeurs viennent du profil LawinServer local. Modifie-les depuis Personnalisation.",
                    "These values come from the local LawinServer profile. Change them from Customise.")
                : S("Dossier LawinServer introuvable : les statistiques du profil ne sont pas disponibles.",
                    "LawinServer folder not found: profile stats are not available."));

            InfoNote(g, x, sy + S(4), w,
                S("Se déconnecter efface la session de ce PC. Une seule session Epic peut être active à la fois.",
                  "Signing out clears the session from this PC. Only one Epic session can be active at a time."));
        }

        void DrawStatTile(Graphics g, RectangleF r, string label, string value, string sub)
        {
            using (var p = Theme.Round(r, S(9)))
            {
                using (var br = new LinearGradientBrush(r, Color.FromArgb(11, 24, 41), Color.FromArgb(6, 12, 21), 90f))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(130, 22, 56, 96), Math.Max(1f, S(1)))) g.DrawPath(pen, p);
            }
            Theme.Text(g, label, UB(7f), Color.FromArgb(128, 158, 194),
                new RectangleF(r.X + S(14), r.Y + S(9), r.Width - S(28), S(14)));
            Theme.Text(g, value, UB(14.5f), Color.FromArgb(236, 244, 253),
                new RectangleF(r.X + S(14), r.Y + S(24), r.Width - S(28), S(26)));
            Theme.Text(g, sub, U(7.4f), Theme.Dim2,
                new RectangleF(r.X + S(14), r.Y + S(50), r.Width - S(28), S(14)));
        }

        void AccountFact(Graphics g, RectangleF sec, float y, string icon, string text)
        {
            Theme.Icon(g, icon, new RectangleF(sec.X + S(18), y + S(3), S(15), S(15)),
                Color.FromArgb(150, Theme.Bright), 1.5f);
            Theme.Text(g, text, U(8.3f), Color.FromArgb(196, 212, 232),
                new RectangleF(sec.X + S(42), y, sec.Width - S(58), S(18)));
        }

        // lecture unique du profil LawinServer pour la page Comptes
        void RefreshProfileStats()
        {
            if (_statsLoaded) return;
            _statsLoaded = true;
            _statsOk = false;
            try
            {
                var dir = ToolsServerDir;
                if (dir == null) return;

                long vb;
                int lvl;
                bool okVb = ProfileTools.TryReadVbucks(dir, out vb);
                bool okLvl = ProfileTools.TryReadLevel(dir, out lvl);
                string name = ProfileTools.ReadPseudo(dir);
                if (!okVb && !okLvl && string.IsNullOrEmpty(name)) return;

                _statsVb = vb;
                _statsLvl = lvl;
                _statsName = name;
                _statsOk = true;
            }
            catch (Exception) { }
        }

        static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "—";
            if (id.Length <= 12) return id;
            return id.Substring(0, 8) + "…" + id.Substring(id.Length - 4);
        }

        // ================= tuiles du bas =================

        void PaintTiles(Graphics g, RectangleF row)
        {
            int gap = S(12);
            float tileW = (row.Width - gap * 3) / 4f;
            if (tileW < S(150)) tileW = S(150);

            string[] icons = { "terminal", "user", "gamepad", "key" };
            string[] labels = { S("Console", "Console"), S("Compte", "Account"),
                                S("Personnalisation", "Customise"), S("Mode privé", "Private mode") };
            string[] subs = { S("Journal en direct", "Live log"),
                              S("Profil Epic", "Epic profile"),
                              S("V-Bucks, niveau", "V-Bucks, level"),
                              S("Proxy SSL", "SSL proxy") };
            string[] ids = { "tile:console", "tile:accounts", "tile:play", "tile:login" };
            bool[] active =
            {
                MiniAlive(MiniWindow.Kind.Console),
                _view == View.Accounts,
                MiniAlive(MiniWindow.Kind.Custom),
                MiniAlive(MiniWindow.Kind.Private)
            };
            bool attention = _login == null;

            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                var r = new RectangleF(row.X + i * (tileW + gap), row.Y, tileW, row.Height);
                PaintTile(g, r, icons[idx], labels[idx], subs[idx], ids[idx], active[idx], attention && idx == 3, delegate
                {
                    _profileOpen = false;
                    if (idx == 0) { _view = View.Home; OpenMini(MiniWindow.Kind.Console, r); }
                    else if (idx == 1) { _view = View.Accounts; }
                    else if (idx == 2) { _view = View.Home; OpenMini(MiniWindow.Kind.Custom, r); }
                    else { _view = View.Home; OpenMini(MiniWindow.Kind.Private, r); }
                    Invalidate();
                });
            }
        }

        void PaintTile(Graphics g, RectangleF r, string icon, string label, string sub, string id,
            bool active, bool attention, Action act)
        {
            bool hot = _hot == id;
            using (var p = Theme.Round(r, S(10)))
            {
                if (active)
                {
                    using (var br = new LinearGradientBrush(r, Color.FromArgb(18, 52, 104), Color.FromArgb(7, 22, 44), 90f))
                        g.FillPath(br, p);
                }
                else if (attention)
                {
                    using (var br = new LinearGradientBrush(r, Color.FromArgb(16, 46, 88), Color.FromArgb(7, 18, 34), 90f))
                        g.FillPath(br, p);
                }
                else
                {
                    using (var br = new SolidBrush(hot ? Color.FromArgb(11, 24, 40) : Color.FromArgb(8, 14, 24)))
                        g.FillPath(br, p);
                }

                if ((active || attention) && _prefs.GlowBorders)
                    using (var pen = new Pen(Color.FromArgb(active ? 55 : 42, Theme.Bright), S(6)))
                        g.DrawPath(pen, p);

                Color border = active ? Color.FromArgb(235, Theme.Bright)
                             : attention ? Color.FromArgb(220, 72, 178, 255)
                             : hot ? Color.FromArgb(190, 30, 96, 166)
                             : Color.FromArgb(190, 17, 52, 92);
                using (var pen = new Pen(border, Math.Max(1f, S(1.1f))))
                    g.DrawPath(pen, p);
            }

            // pastille d'icone
            var plate = new RectangleF(r.X + S(12), r.Y + (r.Height - S(36)) / 2f, S(36), S(36));
            using (var p = Theme.Round(plate, S(9)))
            {
                using (var br = new SolidBrush(active ? Color.FromArgb(22, 58, 106) : Color.FromArgb(11, 26, 44)))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(active ? 160 : 95, Theme.Electric), Math.Max(1f, S(1))))
                    g.DrawPath(pen, p);
            }

            Color ic = active ? Color.FromArgb(196, 234, 255)
                     : attention ? Color.FromArgb(146, 214, 255)
                     : hot ? Color.FromArgb(132, 190, 242)
                     : Color.FromArgb(74, 150, 214);
            Theme.Icon(g, icon, new RectangleF(plate.X + S(9), plate.Y + S(9), S(18), S(18)), ic, attention ? 1.9f : 1.6f);

            Theme.Text(g, label, UB(9.2f), active ? Color.FromArgb(214, 238, 255) : Theme.Ink,
                new RectangleF(r.X + S(58), r.Y + S(17), r.Width - S(72), S(20)));
            Theme.Text(g, sub, U(7.7f), Theme.Dim2,
                new RectangleF(r.X + S(58), r.Y + S(37), r.Width - S(72), S(18)));

            if (active)
                using (var br = new SolidBrush(Theme.Green))
                    g.FillEllipse(br, r.Right - S(17), r.Y + S(13), S(6), S(6));

            Hit(r, id, act);
        }

        // ================= boutons =================

        void PaintPrimary(Graphics g, RectangleF r, string label, string icon, string id, bool enabled, Action act)
        {
            bool hot = _hot == id;
            using (var p = Theme.Round(r, S(7)))
            {
                if (enabled)
                {
                    if (_prefs.GlowBorders)
                        using (var glow = new Pen(Color.FromArgb(46, Theme.Bright), S(5)))
                            g.DrawPath(glow, p);

                    using (var br = new LinearGradientBrush(r,
                        hot ? Color.FromArgb(30, 143, 255) : Color.FromArgb(16, 116, 245),
                        hot ? Color.FromArgb(9, 88, 220) : Color.FromArgb(4, 74, 200), 90f))
                        g.FillPath(br, p);

                    using (var pen = new Pen(Color.FromArgb(220, 88, 178, 255), Math.Max(1f, S(1.1f))))
                        g.DrawPath(pen, p);
                }
                else
                {
                    using (var br = new SolidBrush(Color.FromArgb(13, 24, 40))) g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(140, 24, 54, 88))) g.DrawPath(pen, p);
                }
            }

            Color fg = enabled ? Color.White : Theme.Dim2;
            var f = UB(10f);
            var size = g.MeasureString(label, f);
            float iw = S(16);
            float total = size.Width + iw + S(9);
            float startX = r.X + (r.Width - total) / 2f;
            Theme.Icon(g, icon, new RectangleF(startX, r.Y + r.Height / 2f - iw / 2f, iw, iw), fg, 1.6f);
            Theme.Text(g, label, f, fg,
                new RectangleF(startX + iw + S(9), r.Y, size.Width + S(6), r.Height));

            if (enabled) Hit(r, id, act);
        }

        void PaintGhost(Graphics g, RectangleF r, string label, string icon, string id, bool enabled, Action act)
        {
            bool hot = _hot == id;
            using (var p = Theme.Round(r, S(7)))
            {
                using (var br = new SolidBrush(hot ? Color.FromArgb(16, 32, 54) : Color.FromArgb(8, 17, 30)))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(190, hot ? 46 : 28, 104, 172), Math.Max(1f, S(1.1f))))
                    g.DrawPath(pen, p);
            }

            Color fg = enabled ? Theme.Ink : Theme.Dim2;
            var f = UB(8.7f);
            var size = g.MeasureString(label, f);
            float iw = S(14);
            float total = size.Width + iw + S(8);
            float startX = r.X + Math.Max(S(10), (r.Width - total) / 2f);
            Theme.Icon(g, icon, new RectangleF(startX, r.Y + r.Height / 2f - iw / 2f, iw, iw), fg, 1.5f);
            Theme.Text(g, label, f, fg, new RectangleF(startX + iw + S(8), r.Y, size.Width + S(6), r.Height));

            if (enabled) Hit(r, id, act);
        }

        void PaintSmall(Graphics g, RectangleF r, string label, string id, Action act)
        {
            bool hot = _hot == id;
            using (var p = Theme.Round(r, S(6)))
            {
                using (var br = new SolidBrush(hot ? Color.FromArgb(18, 34, 56) : Color.FromArgb(9, 19, 33)))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(180, hot ? 48 : 26, 88, 140))) g.DrawPath(pen, p);
            }
            Theme.Text(g, label, UB(7.8f), hot ? Theme.Ink : Color.FromArgb(198, 214, 234), r,
                StringAlignment.Center, StringAlignment.Center, false);
            Hit(r, id, act);
        }

        // ================= barre de statut =================

        void PaintStatusBar(Graphics g, int W, int H, int statusH, int pad)
        {
            float top = H - statusH;
            using (var br = new LinearGradientBrush(new RectangleF(0, top, W, statusH),
                Color.FromArgb(7, 12, 20), Color.FromArgb(4, 6, 10), 90f))
                g.FillRectangle(br, 0, top, W, statusH);

            Theme.GlowLine(g, new RectangleF(0, top - 1, W, S(2)), Theme.Electric, 120);

            string text = _status;
            if (!string.IsNullOrEmpty(text) && DateTime.Now > _statusUntil) text = null;
            if (string.IsNullOrEmpty(text))
            {
                if (_launching) text = S("Lancement de Fortnite…", "Launching Fortnite…");
                else if (_restoring) text = S("Restauration de la session…", "Restoring session…");
                else if (_serverBusy) text = S("Opération serveur…", "Server operation…");
                else if (_privateBusy) text = S("Application du mode privé…", "Applying private mode…");
                else if (_login == null) text = S("Prêt", "Ready");
                else text = S("Prêt", "Ready") + " — " + (_serverRunning ? S("serveur privé en ligne", "private server online")
                                                                       : S("serveur privé arrêté", "private server offline"));
            }

            // signature à gauche de la barre, à la place de l'ancien bouton réglages
            Theme.Text(g, "by", U(7.8f), Color.FromArgb(118, 144, 176),
                new RectangleF(pad + S(4), top, S(22), statusH));
            Theme.Text(g, "SERVAT", US(9.4f), Color.FromArgb(214, 232, 250),
                new RectangleF(pad + S(25), top, S(96), statusH));

            using (var pen = new Pen(Color.FromArgb(70, 30, 70, 118)))
                g.DrawLine(pen, pad + S(105), top + S(12), pad + S(105), H - S(12));

            Theme.Text(g, text, U(8.6f), Color.FromArgb(196, 214, 236),
                new RectangleF(pad + S(120), top, W - pad - S(280), statusH));

            Theme.Text(g, "X.HYBRID " + AppInfo.Badge, MonoB(7.4f), Color.FromArgb(96, 130, 166),
                new RectangleF(W - pad - S(110), top, S(100), statusH), StringAlignment.Far, StringAlignment.Center, false);
        }

        // ================= menu profil =================

        void PaintProfileMenu(Graphics g, int W, int titleH, int pad)
        {
            if (_login == null) return;

            float w = S(320);
            float h = S(130);
            var menu = new RectangleF(W - pad - w, titleH + S(10), w, h);

            using (var p = Theme.Round(menu, S(10)))
            {
                if (_prefs.GlowBorders)
                    using (var glow = new Pen(Color.FromArgb(34, Theme.Electric), S(6)))
                        g.DrawPath(glow, p);
                using (var br = new SolidBrush(Color.FromArgb(6, 12, 21))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(230, 26, 86, 152), Math.Max(1f, S(1.2f)))) g.DrawPath(pen, p);
            }

            var header = new RectangleF(menu.X + S(12), menu.Y + S(12), menu.Width - S(24), S(50));
            DrawAvatar(g, new RectangleF(header.X, header.Y + S(3), S(44), S(44)), true, S(44));
            Theme.Text(g, _login.DisplayName, U(10.2f), Theme.Ink,
                new RectangleF(header.X + S(56), header.Y + S(2), header.Width - S(60), S(22)));
            Theme.Text(g, _sessionOffline ? S("Session locale", "Local session") : S("En ligne", "Online"),
                U(8.2f), _sessionOffline ? Theme.Amber : Theme.Green,
                new RectangleF(header.X + S(56), header.Y + S(24), S(150), S(18)));
            using (var br = new SolidBrush(_sessionOffline ? Theme.Amber : Theme.Green))
                g.FillEllipse(br, header.X + S(112), header.Y + S(30), S(6), S(6));

            using (var pen = new Pen(Color.FromArgb(90, 26, 56, 92)))
                g.DrawLine(pen, menu.X + S(14), menu.Y + S(66), menu.Right - S(14), menu.Y + S(66));

            float y = menu.Y + S(72);
            var outRow = new RectangleF(menu.X + S(6), y, menu.Width - S(12), S(44));
            if (_hot == "menu:logout")
                using (var p = Theme.Round(outRow, S(6)))
                using (var br = new SolidBrush(Color.FromArgb(16, 30, 50)))
                    g.FillPath(br, p);

            Theme.Icon(g, "logout", new RectangleF(outRow.X + S(14), outRow.Y + S(12), S(18), S(18)),
                Color.FromArgb(205, 224, 244), 1.5f);
            Theme.Text(g, S("Se déconnecter", "Sign out"), U(9f), Theme.Ink,
                new RectangleF(outRow.X + S(44), outRow.Y, outRow.Width - S(52), outRow.Height));
            Hit(outRow, "menu:logout", delegate { _profileOpen = false; Logout(); });
        }

        float AccountRow(Graphics g, RectangleF clip, RectangleF r, SavedAccount acc, bool active)
        {
            if (!r.IntersectsWith(clip)) return r.Bottom;
            bool hasToken = !string.IsNullOrEmpty(acc.Token);

            Theme.Icon(g, "user", new RectangleF(r.X + S(12), r.Y + S(13), S(18), S(18)),
                active ? Theme.Bright : Theme.Dim, 1.5f);
            Theme.Text(g, acc.DisplayName, U(9.1f), active ? Theme.Bright : Theme.Ink,
                new RectangleF(r.X + S(40), r.Y + S(5), r.Width - S(190), S(20)));
            Theme.Text(g, active ? S("Compte actif", "Active account")
                                 : (hasToken ? S("Session enregistrée", "Saved session") : S("Session expirée", "Expired session")),
                U(7.7f), Theme.Dim2, new RectangleF(r.X + S(40), r.Y + S(24), r.Width - S(190), S(16)));

            var switchR = new RectangleF(r.Right - S(100), r.Y + S(9), S(82), S(28));
            if (!active)
                PaintSmall(g, switchR, S("BASCULER", "SWITCH"), "acctrow:" + acc.AccountId,
                    delegate { SwitchAccount(acc); });

            var del = new RectangleF(r.Right - S(28), r.Y + S(13), S(18), S(18));
            if (_hot == "del:" + acc.AccountId)
                using (var br = new SolidBrush(Color.FromArgb(34, 40, 18, 26)))
                    g.FillEllipse(br, new RectangleF(del.X - S(4), del.Y - S(4), S(26), S(26)));
            Theme.Icon(g, "trash", del, Color.FromArgb(168, 190, 214), 1.4f);
            string accId = acc.AccountId;
            Hit(del, "del:" + acc.AccountId, delegate { RemoveAccount(accId); });

            return r.Bottom + S(2);
        }

        float InfoNote(Graphics g, float x, float y, float w, string text)
        {
            var f = U(7.9f);
            var lines = Wrap(g, text, f, w - S(8));
            float lh = S(15);
            for (int i = 0; i < lines.Count; i++)
                Theme.Text(g, lines[i], f, Theme.Dim2, new RectangleF(x + S(2), y + S(8) + i * lh, w - S(8), lh));
            return y + S(14) + lines.Count * lh;
        }

        // ================= interactions =================

        void Hit(RectangleF r, string id, Action act)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (_hitClip.Width > 0 && !_hitClip.IntersectsWith(r)) return;
            var h = new Hotspot();
            h.R = r;
            h.Id = id;
            h.Act = act;
            _hits.Add(h);
        }

        Hotspot? FindHit(Point p)
        {
            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].R.Contains(p)) return _hits[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var hit = FindHit(e.Location);
            string id = hit.HasValue ? hit.Value.Id : null;
            if (id != _hot)
            {
                _hot = id;
                Cursor = (id != null && id != "drag") ? Cursors.Hand
                       : (e.Y <= S(56) ? Cursors.SizeAll : Cursors.Default);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != null) { _hot = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            var hit = FindHit(e.Location);
            if (hit.HasValue)
            {
                if (hit.Value.Act != null) hit.Value.Act();
                return;
            }
            if (e.Y <= S(56)) DragWindow();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            if (_view == View.Console && e.Delta != 0)
            {
                _logScroll += e.Delta > 0 ? 3 : -3;
                if (_logScroll < 0) _logScroll = 0;
                Invalidate();
                return;
            }

        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (_profileOpen) { _profileOpen = false; Invalidate(); return true; }
                if (_view == View.Login && (_phase == LoginPhase.Waiting || _phase == LoginPhase.Requesting)) { CancelLogin(); return true; }
                if (_view != View.Home) { _view = View.Home; Invalidate(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ================= drag =================

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        void DragWindow()
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
        }

        // ================= connexion Epic =================

        void StartLogin(bool addAccount)
        {
            if (_phase == LoginPhase.Requesting || _phase == LoginPhase.Waiting) return;
            int gen = ++_loginGen;

            _addingAccount = addAccount;
            _view = View.Login;
            _profileOpen = false;
            _phase = LoginPhase.Requesting;
            _loginError = "";
            _device = null;
            _logScroll = 0;
            Status(S("Connexion à Epic Games…", "Signing in to Epic Games…"));
            Invalidate();

            Task.Run(async delegate
            {
                try
                {
                    var info = await _epic.StartDeviceAuth();
                    Ui(delegate
                    {
                        if (gen != _loginGen) return;
                        _device = info;
                        _phase = LoginPhase.Waiting;
                        Status(S("En attente de validation…", "Waiting for approval…"));
                        try { Process.Start(info.VerificationUriComplete); } catch (Exception) { }
                        Log(S("Code Epic : ", "Epic code: ") + info.UserCode);
                        Invalidate();
                    });

                    var res = await _epic.WaitDeviceLogin(info);
                    Log(S("Connexion Epic réussie : ", "Epic sign-in succeeded: ") + res.DisplayName);
                    Ui(delegate
                    {
                        if (gen != _loginGen) return;
                        OnLoginSuccess(res, true);
                    });
                }
                catch (OperationCanceledException)
                {
                    Ui(delegate
                    {
                        if (gen != _loginGen) return;
                        if (_phase != LoginPhase.Requesting && _phase != LoginPhase.Waiting) return;
                        _phase = LoginPhase.Idle;
                        _device = null;
                        _view = View.Home;
                        Status("");
                        Log(S("Connexion annulée.", "Sign-in cancelled."));
                        Invalidate();
                    });
                }
                catch (Exception ex)
                {
                    FileLog.Write("STACK: " + ex);
                    string friendly = FriendlyError(ex);
                    Ui(delegate
                    {
                        if (gen != _loginGen) return;
                        _phase = LoginPhase.Error;
                        _loginError = friendly;
                        Status(S("Connexion échouée.", "Sign-in failed."));
                        Log("ERREUR connexion : " + ex.Message);
                        Invalidate();
                    });
                }
            });
        }

        // Demande de connexion affichée au démarrage quand aucun compte n'est enregistré.
        void ShowLoginPrompt()
        {
            if (_login != null) return;
            _view = View.Login;
            _phase = LoginPhase.Idle;
            _device = null;
            _loginError = "";
            _profileOpen = false;
            Status(S("Connecte-toi à Epic Games pour continuer.", "Sign in to Epic Games to continue."));
            Invalidate();
        }

        void CancelLogin()
        {
            _loginGen++;
            _epic.CancelWait();
            _phase = LoginPhase.Idle;
            _device = null;
            _loginError = "";
            _view = View.Home;
            Status("");
            Log(S("Connexion annulée.", "Sign-in cancelled."));
            Invalidate();
        }

        static string FriendlyError(Exception ex)
        {
            string m = ex == null ? "" : (ex.Message ?? "");
            if (m.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0)
                return S("Le code a expiré. Relance la connexion.", "The code expired. Start the sign-in again.");
            if (m.IndexOf("access_denied", StringComparison.OrdinalIgnoreCase) >= 0)
                return S("La connexion a été refusée sur la page Epic Games.", "Sign-in was denied on the Epic Games page.");
            if (m.IndexOf("HttpRequestException", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                m.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ex is System.Net.Http.HttpRequestException)
                return S("Impossible de joindre Epic Games. Vérifie ta connexion Internet.",
                         "Cannot reach Epic Games. Check your Internet connection.");
            return m;
        }

        void OnLoginSuccess(EpicLoginResult res, bool saveAccount)
        {
            if (res == null || string.IsNullOrEmpty(res.AccountId))
            {
                _phase = LoginPhase.Error;
                _loginError = S("Réponse d'Epic Games incomplète.", "Incomplete Epic Games response.");
                Invalidate();
                return;
            }

            _login = res;
            _phase = LoginPhase.Idle;
            _device = null;
            _loginError = "";
            _view = View.Home;
            _profileOpen = false;
            _restoring = false;
            _sessionOffline = false;

            if (saveAccount && _prefs.PersistSession)
            {
                // refresh ET access token sont enregistrés ensemble : le refresh token
                // Epic est à usage unique, il ne doit jamais être perdu
                _accounts.SaveTokens(res.AccountId, res.DisplayName, res.RefreshToken,
                    res.AccessToken, res.ExpiresAt);
            }
            else if (saveAccount)
            {
                _accounts.ActiveId = res.AccountId;
                _accounts.Save();
            }

            Status(S("Connecté. ", "Signed in. ") + res.DisplayName);
            Log(S("Profil actif : ", "Active profile: ") + res.DisplayName
                + " (" + ShortId(res.AccountId) + ")");
            Invalidate();

            Task.Run(new Action(PrepBackend));
        }

        void SwitchAccount(SavedAccount acc)
        {
            if (acc == null) return;
            if (_phase == LoginPhase.Requesting || _phase == LoginPhase.Waiting) return;

            string token = SecureStore.Unprotect(acc.Token);
            _profileOpen = false;

            if (string.IsNullOrEmpty(token))
            {
                StartLogin(true);
                return;
            }

            _restoring = true;
            Status(S("Connexion à ", "Signing in to ") + acc.DisplayName + "…");
            Invalidate();

            Task.Run(async delegate
            {
                try
                {
                    var res = await _epic.Refresh(token);
                    Ui(delegate { OnLoginSuccess(res, true); });
                }
                catch (Exception ex)
                {
                    FileLog.Write("Changement de compte: " + ex);
                    Ui(delegate
                    {
                        _restoring = false;
                        _view = View.Login;
                        _phase = LoginPhase.Error;
                        _loginError = S("La session de ", "The session for ") + acc.DisplayName
                                    + S(" a expiré. Reconnecte-toi.", " has expired. Sign in again.");
                        Status(S("Session expirée.", "Session expired."));
                        Log("ERREUR session : " + ex.Message);
                        Invalidate();
                    });
                }
            });
        }

        void RestoreSessionIfAny()
        {
            if (!_prefs.PersistSession) return;
            var acc = _accounts.Find(_accounts.ActiveId);
            if (acc == null) return;

            // 1) jeton d'accès encore valide : reprise instantanée, sans consommer le
            //    refresh token (usage unique). C'est le cas normal d'une relance rapprochée.
            string access = SecureStore.Unprotect(acc.AccessToken);
            DateTime exp;
            if (!string.IsNullOrEmpty(access) && TryParseUtc(acc.ExpiresAt, out exp) &&
                exp > DateTime.UtcNow.AddMinutes(3))
            {
                var quick = new EpicLoginResult();
                quick.AccountId = acc.AccountId;
                quick.DisplayName = acc.DisplayName;
                quick.AccessToken = access;
                quick.ExpiresAt = exp;
                quick.RefreshToken = SecureStore.Unprotect(acc.Token);
                _login = quick;
                _sessionOffline = false;
                Log(S("Session restaurée pour ", "Session restored for ") + acc.DisplayName);
                Status(S("Connecté. ", "Signed in. ") + acc.DisplayName);
                Invalidate();
                Task.Run(new Action(PrepBackend));
                return;
            }

            string token = SecureStore.Unprotect(acc.Token);
            if (string.IsNullOrEmpty(token))
            {
                KeepLocalSession(acc, S("Aucun jeton de session : reconnecte-toi une seule fois avec Epic Games.",
                                        "No session token: sign in once with Epic Games."));
                return;
            }

            _restoring = true;
            Status(S("Restauration de la session…", "Restoring session…"));
            Invalidate();

            Task.Run(async delegate
            {
                Exception last = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var res = await _epic.Refresh(token);

                        // sauvegarde immédiate (thread de fond) : si la fenêtre se ferme
                        // juste après, le nouveau refresh token est déjà sur le disque
                        _accounts.SaveTokens(res.AccountId, res.DisplayName, res.RefreshToken,
                            res.AccessToken, res.ExpiresAt);
                        Log(S("Session restaurée pour ", "Session restored for ") + res.DisplayName);
                        Ui(delegate { OnLoginSuccess(res, false); });
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        FileLog.Write("Restauration session (" + (attempt + 1) + "): " + ex.Message);
                        if (!IsTransient(ex)) break;
                        System.Threading.Thread.Sleep(1500);
                    }
                }

                string message = last == null ? "" : (last.Message ?? "");
                Ui(delegate
                {
                    // on ne déconnecte jamais automatiquement : le compte reste affiché
                    // et le jeu reste lançable (le lancement passe par Epic Games Launcher)
                    KeepLocalSession(acc, S("Session locale conservée.", "Local session kept."));
                    Log(S("Restauration Epic impossible : la session locale est conservée.",
                          "Epic restore failed: the local session is kept."));
                    if (message.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                        Log(S("Le jeton Epic a expiré : on reste connecté en local, sans déconnexion.",
                              "The Epic token expired: staying signed in locally, no sign-out."));
                });
            });
        }

        static bool TryParseUtc(string value, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrEmpty(value)) return false;
            return DateTime.TryParse(value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out utc);
        }

        static bool IsTransient(Exception ex)
        {
            if (ex is System.Net.Http.HttpRequestException) return true;
            if (ex is TaskCanceledException) return true;
            var m = ex == null ? "" : (ex.Message ?? "");
            return m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("(5", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Session affichée localement, sans jeton Epic valide : l'utilisateur n'est
        // jamais déconnecté de force. Seule l'action « Se déconnecter » efface tout.
        void KeepLocalSession(SavedAccount acc, string note)
        {
            var local = new EpicLoginResult();
            local.AccountId = acc.AccountId;
            local.DisplayName = string.IsNullOrEmpty(acc.DisplayName) ? acc.AccountId : acc.DisplayName;
            local.RefreshToken = SecureStore.Unprotect(acc.Token);

            _login = local;
            _phase = LoginPhase.Idle;
            _device = null;
            _restoring = false;
            _profileOpen = false;
            _sessionOffline = true;
            Status(note);
            Invalidate();
        }

        void Logout()
        {
            if (_login != null)
            {
                Log(S("Déconnecté de ", "Signed out of ") + _login.DisplayName + ".");
                _accounts.Remove(_login.AccountId);
            }
            _login = null;
            _sessionOffline = false;
            _accounts.ActiveId = null;
            _accounts.Save();
            _phase = LoginPhase.Idle;
            _device = null;
            _profileOpen = false;
            _view = View.Home;
            Status(S("Déconnecté.", "Signed out."));
            Invalidate();
        }

        void RemoveAccount(string id)
        {
            var acc = _accounts.Find(id);
            if (acc == null) return;
            bool wasActive = _login != null && _login.AccountId == id;
            _accounts.Remove(id);
            Log(S("Compte retiré : ", "Account removed: ") + acc.DisplayName);
            if (wasActive) { _login = null; _accounts.ActiveId = null; _view = View.Home; }
            _accounts.Save();
            Invalidate();
        }

        // ================= serveur / redirection / lancement =================

        readonly object _backendLock = new object();
        // une seule synchro cosmétiques à la fois (démarrage + lancement)
        readonly object _cosmeticsLock = new object();
        bool _backendBusy;

        void PrepBackend()
        {
            if (!_privateOn)
            {
                Log(S("Mode en ligne : serveur privé et proxy non démarrés.",
                      "Online mode: private server and proxy not started."));
                return;
            }
            lock (_backendLock)
            {
                if (_backendBusy) return;
                _backendBusy = true;
            }
            try
            {
                ProxyManager.Start();
                Log(S("Proxy SSL local actif sur le port ", "Local SSL proxy running on port ") + ProxyManager.ProxyPort + ".");

                EnsureBundledServer();

                var dir = ResolveServerDir();
                if (dir == null)
                    Log(S("ATTENTION : dossier LawinServer introuvable (config : ",
                          "WARNING: LawinServer folder not found (config: ") + (_config.ServerDir ?? S("vide", "empty")) + ")");
                else
                    Log(S("Dossier serveur trouvé : ", "Server folder found: ") + dir);

                if (_config.LaunchRedirect && !Redirect.IsApplied(_config))
                {
                    Redirect.Apply(_config);
                    Log(S("Redirection des serveurs Fortnite -> ", "Fortnite servers redirected to ") + _config.ServerIp + ".");
                }

                SyncCosmetics(dir);

                if (_prefs.AutoStartServer && !ServerManager.IsRunning())
                {
                    Log(S("Démarrage automatique du serveur privé…", "Starting the private server automatically…"));
                    Process p;
                    ServerManager.Start(_config, Log, out p);
                    ServerManager.WaitReady(10);
                }
                Log(ServerManager.IsRunning()
                    ? S("Serveur privé prêt.", "Private server ready.")
                    : S("Serveur privé : pas encore prêt.", "Private server: not ready yet."));
                Ui(Invalidate);
            }
            catch (Exception ex)
            {
                Log(S("Préparation : ", "Preparation: ") + ex.Message);
            }
            finally
            {
                lock (_backendLock) { _backendBusy = false; }
            }
        }

        string ResolveServerDir()
        {
            if (!string.IsNullOrEmpty(_config.ServerDir) && Directory.Exists(_config.ServerDir))
                return _config.ServerDir;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var here = baseDir;
            for (int i = 0; i <= 6; i++)
            {
                var candidate = Path.Combine(here, "LawinServer");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.js")))
                    return candidate;
                var parent = Path.GetDirectoryName(here.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == here) break;
                here = parent;
            }

            // serveur installé par X.HYBRID lui-même (embarqué dans l'exe)
            if (ServerPayload.ServerPresent) return ServerPayload.ServerDir;
            return null;
        }

        // Installe le serveur embarqué dans l'exe quand il manque quelque chose au
        // joueur : ni dossier LawinServer, ni Node.js. Aucun téléchargement, tout
        // est déjà dans l'exécutable. Renvoie false si l'installation a échoué.
        bool EnsureBundledServer()
        {
            if (!ServerPayload.HasPayload) return true;   // compilation de test sans paquet

            bool haveFolder = ResolveServerDir() != null;
            bool haveNode = ServerManager.NodeAvailable(_config);
            if (haveFolder && haveNode) return true;

            if (ServerPayload.IsInstalled)
            {
                if (!haveFolder) { _config.ServerDir = ServerPayload.ServerDir; _config.Save(); }
                return true;
            }

            Log(S("Installation du serveur privé (une seule fois, sans téléchargement)…",
                  "Installing the private server (one time only, nothing to download)…"));
            Ui(delegate { Status(S("Installation du serveur privé…", "Installing the private server…")); Invalidate(); });

            try
            {
                ServerPayload.Install(Log, delegate (int pct)
                {
                    Ui(delegate
                    {
                        if (pct >= 100) Status(S("Serveur privé installé.", "Private server installed."));
                        else Status(S("Installation du serveur privé… ", "Installing the private server… ") + pct + "%");
                        Invalidate();
                    });
                });
            }
            catch (Exception ex)
            {
                FileLog.Write("payload: " + ex);
                Log(S("Installation du serveur impossible : ", "Server installation failed: ") + ex.Message);
                Ui(delegate { Status(S("Installation du serveur impossible.", "Server installation failed.")); Invalidate(); });
                return false;
            }

            if (ResolveServerDir() == null)
            {
                _config.ServerDir = ServerPayload.ServerDir;
                _config.Save();
            }
            Log(S("Serveur privé installé dans ", "Private server installed in ") + ServerPayload.ServerDir);
            if (ServerManager.FindNode(_config) == ServerPayload.NodeExe)
                Log(S("Moteur Node.js embarqué prêt.", "Bundled Node.js runtime ready."));
            return true;
        }

        void LaunchGame()
        {
            if (_launching) return;
            // pas de session : on demande la connexion avant de lancer le jeu
            if (_login == null)
            {
                StartLogin(false);
                return;
            }

            _launching = true;
            Status(S("Préparation du lancement…", "Preparing launch…"));
            Invalidate();
            Action work = _privateOn ? (Action)DoLaunch : LaunchOnline;
            Task.Run(work);
        }

        // Mode en ligne : les serveurs Epic sont utilises tels quels, le jeu peut
        // donc demarrer une vraie partie (Creatif, BR...). Le serveur prive et son
        // proxy ne servent que le casier et la boutique : ils sont coupes ici.
        void LaunchOnline()
        {
            try
            {
                Log(S("Mode en ligne : serveur privé et proxy ignorés.", "Online mode: private server and proxy bypassed."));
                try { if (Redirect.IsApplied(_config)) Redirect.Remove(_config); } catch (Exception) { }
                try { Redirect.RepairDnsCache(_config); } catch (Exception) { }
                try { ProxyManager.DisableSystemProxy(); } catch (Exception) { }
                try { ProxyManager.Stop(); } catch (Exception) { }
                try { ProxyManager.CleanResidue(); } catch (Exception) { }
                if (ServerManager.IsRunning())
                {
                    try { ServerManager.Stop(); Log(S("Serveur privé arrêté : il n'est pas utilisé en ligne.",
                                                    "Private server stopped: not used online.")); }
                    catch (Exception) { }
                }

                Log(S("Lancement de Fortnite avec ton compte Epic Games…",
                      "Launching Fortnite with your Epic Games account…"));
                GameLauncher.LaunchViaEgs(_config, false);
                Log(S("Partie privée en solo : Découvrir → Créatif → lance ton île, personne ne rejoint.",
                      "Private solo game: Discover → Creative → launch your island, nobody joins."));
                Ui(delegate
                {
                    _launching = false;
                    Status(S("Fortnite lancé sur les serveurs Epic (parties réelles).",
                             "Fortnite launched on Epic servers (real matches)."));
                    Invalidate();
                });
                Notify("X.HYBRID V1", S("Fortnite va s'ouvrir sur les serveurs Epic.", "Fortnite is opening on Epic servers."));
            }
            catch (Exception ex)
            {
                FileLog.Write("STACK: " + ex);
                Log("ERREUR lancement en ligne : " + ex.Message);
                Ui(delegate
                {
                    _launching = false;
                    Status(S("Lancement en ligne impossible (voir la console).", "Online launch failed (see the console)."));
                    Invalidate();
                });
                Notify("X.HYBRID V1", S("Lancement impossible : ", "Launch failed: ") + ex.Message);
            }
        }

        void DoLaunch()
        {
            try
            {
                EnsureBundledServer();

                var dir = ResolveServerDir();
                if (dir == null)
                {
                    string picked = UiInvoke(delegate { return PickFolder(S("Sélectionne le dossier de LawinServer", "Select the LawinServer folder")); });
                    if (picked != null)
                    {
                        _config.ServerDir = picked;
                        _config.Save();
                        dir = picked;
                    }
                }
                if (dir == null || !Directory.Exists(dir))
                    throw new Exception(S("Dossier du serveur privé introuvable (le serveur embarqué n'a pas pu s'installer).",
                                          "Private server folder not found (the bundled server could not be installed)."));
                if (_config.ServerDir != dir) { _config.ServerDir = dir; _config.Save(); }

                if (_config.LaunchRedirect && !Redirect.IsApplied(_config))
                {
                    Log(S("Redirection des serveurs Fortnite vers ", "Redirecting Fortnite servers to ") + _config.ServerIp + " …");
                    Redirect.Apply(_config);
                }

                // skins/emotes sortis depuis la dernière synchro, avant que le
                // serveur ne charge le profil en mémoire
                SyncCosmetics(dir);

                if (!ServerManager.IsRunning())
                {
                    if (!_prefs.AutoStartServer)
                        Log(S("Serveur arrêté et démarrage automatique désactivé : lancement sans serveur.",
                              "Server stopped and auto-start disabled: launching without the server."));
                    else
                    {
                        Log(S("Démarrage du serveur privé (port 80)…", "Starting the private server (port 80)…"));
                        Process p;
                        ServerManager.Start(_config, Log, out p);
                        ServerManager.WaitReady(10);
                        if (!ServerManager.IsRunning())
                            throw new Exception(S("Le serveur n'a pas répondu sur le port 80.",
                                                  "The server did not respond on port 80."));
                    }
                }
                Log(S("Serveur privé prêt.", "Private server ready."));

                // le jeu ne peut pas démarrer sans le proxy local : on vérifie qu'il écoute
                string proxyError;
                if (!ProxyManager.EnsureStarted(out proxyError))
                    throw new Exception(S("Proxy local indisponible (", "Local proxy unavailable (") + proxyError + "). "
                        + S("Ferme les autres fenêtres X.HYBRID V1 puis réessaie.", "Close the other X.HYBRID V1 windows and try again."));

                Log(S("Proxy local prêt sur le port ", "Local proxy ready on port ") + ProxyManager.ProxyPort + ".");
                Task.Run(new Action(WatchGameSession));

                var url = GameLauncher.FindEgsLaunchUrl(_config);
                if (string.IsNullOrEmpty(_config.EgsLaunchUrl))
                {
                    _config.EgsLaunchUrl = url;
                    _config.Save();
                }

                Log(S("Demande de lancement du jeu via Epic Games Launcher…",
                      "Requesting the game via the Epic Games Launcher…"));
                GameLauncher.LaunchViaEgs(_config, true);
                Log(S("Epic Games Launcher a reçu la demande ! Fortnite va s'ouvrir sur ton serveur privé.",
                      "Epic Games Launcher received the request! Fortnite will open on your private server."));
                Ui(delegate
                {
                    _launching = false;
                    Status(S("Fortnite lancé via Epic Games Launcher.", "Fortnite launched via the Epic Games Launcher."));
                    Invalidate();
                });
                Notify("X.HYBRID V1", S("Fortnite est en cours de lancement.", "Fortnite is launching."));
            }
            catch (Exception ex)
            {
                FileLog.Write("STACK: " + ex);
                Log("ERREUR lancement : " + ex.Message);
                Ui(delegate
                {
                    _launching = false;
                    Status(S("Erreur au lancement (voir la console).", "Launch error (see the console)."));
                    Invalidate();
                });
                Notify("X.HYBRID V1", S("Lancement impossible : ", "Launch failed: ") + ex.Message);
            }
        }

        void ToggleServer()
        {
            if (_serverBusy) return;
            _serverBusy = true;
            Invalidate();

            Task.Run(delegate
            {
                try
                {
                    if (ServerManager.IsRunning())
                    {
                        ServerManager.Stop();
                        Log(S("Serveur arrêté.", "Server stopped."));
                        Ui(delegate { _serverRunning = false; _serverBusy = false; Status(S("Serveur arrêté.", "Server stopped.")); Invalidate(); });
                        return;
                    }

                    EnsureBundledServer();

                    var dir = ResolveServerDir();
                    if (dir == null)
                    {
                        string picked = UiInvoke(delegate { return PickFolder(S("Sélectionne le dossier de LawinServer", "Select the LawinServer folder")); });
                        if (picked != null)
                        {
                            _config.ServerDir = picked;
                            _config.Save();
                            dir = picked;
                        }
                    }
                    if (dir == null || !Directory.Exists(dir))
                    {
                        Log(S("Dossier du serveur privé introuvable.", "Private server folder not found."));
                        Ui(delegate { _serverBusy = false; Status(S("Dossier serveur introuvable.", "Server folder not found.")); Invalidate(); });
                        return;
                    }
                    _config.ServerDir = dir;
                    _config.Save();

                    Log(S("Démarrage du serveur privé (port 80)…", "Starting the private server (port 80)…"));
                    Process p;
                    ServerManager.Start(_config, Log, out p);
                    ServerManager.WaitReady(10);
                    bool ok = ServerManager.IsRunning();
                    Log(ok ? S("Serveur privé prêt sur le port 80.", "Private server ready on port 80.")
                           : S("Le serveur n'a pas répondu sur le port 80.", "The server did not respond on port 80."));
                    Ui(delegate
                    {
                        _serverRunning = ok;
                        _serverBusy = false;
                        Status(ok ? S("Serveur privé prêt.", "Private server ready.") : S("Serveur indisponible.", "Server unavailable."));
                        Invalidate();
                    });
                }
                catch (Exception ex)
                {
                    Log("ERREUR serveur : " + ex.Message);
                    Ui(delegate { _serverBusy = false; Invalidate(); });
                }
            });
        }

        void TogglePrivateMode()
        {
            if (_privateBusy) return;
            _privateBusy = true;
            Invalidate();

            Task.Run(delegate
            {
                try
                {
                    bool applied;
                    try { applied = Redirect.IsApplied(_config); } catch (Exception) { applied = false; }

                    if (applied)
                    {
                        Redirect.Remove(_config);
                        ProxyManager.Stop();
                        ProxyManager.CleanResidue();
                        Log(S("Mode privé coupé : Epic Games redevient normal.", "Private mode off: Epic Games is back to normal."));
                        Ui(delegate { _privateOn = false; _privateBusy = false; Status(S("Mode privé désactivé.", "Private mode disabled.")); Invalidate(); });
                    }
                    else
                    {
                        Redirect.Apply(_config);
                        ProxyManager.Start();
                        ProxyManager.EnableSystemProxy();
                        Log(S("Mode privé actif : redirection hosts + proxy SSL global sur le port ",
                              "Private mode on: hosts redirect + global SSL proxy on port ") + ProxyManager.ProxyPort + ".");
                        Ui(delegate { _privateOn = true; _privateBusy = false; Status(S("Mode privé activé.", "Private mode enabled.")); Invalidate(); });
                    }
                }
                catch (Exception ex)
                {
                    Log("ERREUR mode privé : " + ex.Message);
                    Ui(delegate { _privateBusy = false; Status(S("Mode privé : erreur.", "Private mode: error.")); Invalidate(); });
                }
            });
        }

        string PickFolder(string title)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = title;
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) == DialogResult.OK)
                    return d.SelectedPath;
            }
            return null;
        }

        void PickServerFolder()
        {
            var picked = PickFolder(S("Sélectionne le dossier de LawinServer", "Select the LawinServer folder"));
            if (picked == null) return;
            _config.ServerDir = picked;
            _config.Save();
            Log(S("Dossier du serveur : ", "Server folder: ") + picked);
            Status(S("Dossier du serveur mis à jour.", "Server folder updated."));
            Invalidate();
        }

        // ---------- surveillance de la partie ----------

        static bool WaitGameStart(int seconds)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                if (GameRunning()) return true;
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }

        void WatchGameSession()
        {
            try
            {
                // ne pas restaurer le proxy avant le démarrage effectif du jeu :
                // Fortnite met plusieurs secondes à apparaître après la demande EGS.
                if (!WaitGameStart(150))
                {
                    try { ProxyManager.DisableSystemProxy(); } catch (Exception) { }
                    Log(S("Fortnite ne s'est pas lancé : réglages réseaux restaurés.",
                          "Fortnite did not start: network settings restored."));
                    Ui(Invalidate);
                    return;
                }

                bool globalForced = false;
                while (GameRunning())
                {
                    if (!ServerManager.IsRunning())
                    {
                        try
                        {
                            Log(S("Le serveur privé est tombé : relance automatique…", "The private server went down: restarting…"));
                            Process p;
                            ServerManager.Start(_config, Log, out p);
                            ServerManager.WaitReady(10);
                            Log(ServerManager.IsRunning()
                                ? S("Serveur privé relancé automatiquement.", "Private server restarted automatically.")
                                : S("Le serveur n'a pas répondu après relance.", "The server did not respond after restart."));
                        }
                        catch (Exception ex) { Log("ERREUR relance serveur : " + ex.Message); }
                    }

                    if (!globalForced && !ProxyManager.IsGlobal())
                    {
                        var tail = ReadGameTail(8192);
                        if (tail != null && tail.IndexOf("QueryKeychain hanged", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            globalForced = true;
                            try
                            {
                                ProxyManager.EnableSystemProxy();
                                Log(S("Fortnite ne passait pas par le proxy réservé : proxy global activé pendant la partie.",
                                      "Fortnite was not using the reserved proxy: global proxy enabled for this session."));
                            }
                            catch (Exception ex) { Log("ERREUR proxy global : " + ex.Message); }
                        }
                    }

                    System.Threading.Thread.Sleep(3000);
                }

                try
                {
                    ProxyManager.DisableSystemProxy();
                    ProxyManager.CleanResidue();
                }
                catch (Exception) { }
                Log(S("Fortnite fermé : réglages réseaux remis comme avant.", "Fortnite closed: network settings restored."));
                Ui(Invalidate);
            }
            catch (Exception) { }
        }

        static bool GameRunning()
        {
            try { return Process.GetProcessesByName("GameClient-Win64-Shipping").Length > 0
                      || Process.GetProcessesByName("FortniteClient-Win64-Shipping").Length > 0; }
            catch (Exception) { return false; }
        }

        static string ReadGameTail(int size)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FortniteGame", "Saved", "Logs", "FortniteGame.log");
                if (!File.Exists(path)) return null;
                using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    long read = Math.Min(len, size);
                    fs.Seek(len - read, SeekOrigin.Begin);
                    var buf = new byte[read];
                    using (var ms = new MemoryStream())
                    {
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
            catch (Exception) { return null; }
        }

        // ================= mises à jour =================

        void CheckForUpdates()
        {
            if (_updateChecked) return;
            _updateChecked = true;

            Task.Run(delegate
            {
                // laisse le temps à la session et au serveur de se restaurer
                System.Threading.Thread.Sleep(2200);
                if (GameRunning()) return;

                var info = UpdateService.Check();
                if (info == null || IsDisposed || !IsHandleCreated) return;

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (_updating || _updateWindow != null || IsDisposed) return;
                        // pas de fenêtre au milieu d'une connexion ou d'un lancement
                        if (_launching || _serverBusy || _privateBusy || GameRunning()) return;
                        if (_phase == LoginPhase.Requesting || _phase == LoginPhase.Waiting) return;

                        Log("Mise à jour disponible : V" + info.Version);
                        var win = new UpdateWindow(info);
                        _updateWindow = win;
                        try { win.ShowDialog(this); }
                        finally { _updateWindow = null; }

                        if (win.Applied)
                        {
                            _updating = true;
                            Close();
                        }
                    }));
                }
                catch (Exception) { }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_updating && _prefs.ConfirmCloseInGame && GameRunning())
            {
                var res = MessageBox.Show(this,
                    S("Fortnite est en cours de partie.\nFermer X.HYBRID V1 coupe le proxy et le serveur privé.\n\nFermer quand même ?",
                      "Fortnite is running.\nClosing X.HYBRID V1 stops the proxy and the private server.\n\nClose anyway?"),
                    "X.HYBRID V1", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (res != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            try { _epic.CancelWait(); } catch (Exception) { }

            // dernier filet de sécurité : on écrit les jetons courants avant de fermer
            try
            {
                if (_login != null && _prefs.PersistSession && !string.IsNullOrEmpty(_login.AccountId))
                    _accounts.SaveTokens(_login.AccountId, _login.DisplayName, _login.RefreshToken,
                        _login.AccessToken, _login.ExpiresAt);
            }
            catch (Exception) { }

            try
            {
                if (_animTimer != null) { _animTimer.Stop(); _animTimer.Dispose(); _animTimer = null; }
                if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); _pollTimer = null; }
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            }
            catch (Exception) { }

            try
            {
                if (!GameRunning()) ProxyManager.DisableSystemProxy();
            }
            catch (Exception) { }

            foreach (var f in _fonts.Values) { try { f.Dispose(); } catch (Exception) { } }
            _fonts.Clear();

            base.OnFormClosing(e);
        }

        // ================= petites fenêtres des tuiles =================

        MiniWindow _miniConsole, _miniAccount, _miniLaunch, _miniPrivate;

        RectangleF DefaultAnchor()
        {
            return new RectangleF(S(20), ClientSize.Height - S(130), S(240), S(78));
        }

        internal bool MiniAlive(MiniWindow.Kind kind)
        {
            var w = MiniFor(kind);
            return w != null && !w.IsDisposed;
        }

        MiniWindow MiniFor(MiniWindow.Kind kind)
        {
            switch (kind)
            {
                case MiniWindow.Kind.Console: return _miniConsole;
                case MiniWindow.Kind.Account: return _miniAccount;
                case MiniWindow.Kind.Custom: return _miniLaunch;
                default: return _miniPrivate;
            }
        }

        internal void OpenMini(MiniWindow.Kind kind, RectangleF anchor)
        {
            var w = MiniFor(kind);
            if (w != null && !w.IsDisposed) { w.Activate(); return; }

            w = new MiniWindow(this, kind);
            switch (kind)
            {
                case MiniWindow.Kind.Console: _miniConsole = w; break;
                case MiniWindow.Kind.Account: _miniAccount = w; break;
                case MiniWindow.Kind.Custom: _miniLaunch = w; break;
                default: _miniPrivate = w; break;
            }

            w.PlaceAbove(anchor);
            w.FormClosed += delegate { Invalidate(); };
            w.Show(this);
            Invalidate();
        }

        // accès partagé avec les petites fenêtres
        internal int Sc(int v) { return S(v); }
        internal float Sc(float v) { return S(v); }
        internal Font F(float pt) { return U(pt); }
        internal Font FB(float pt) { return UB(pt); }
        internal Font FM(float pt) { return Mono(pt); }
        internal LauncherPrefs Prefs { get { return _prefs; } }
        internal EpicLoginResult CurrentLogin { get { return _login; } }
        internal bool IsServerRunning { get { return _serverRunning; } }
        internal bool IsPrivateOn { get { return _privateOn; } }
        internal bool IsBusy { get { return _serverBusy || _privateBusy || _launching; } }
        internal List<string> LogLines { get { return _lines; } }
        internal string ShortIdPublic(string id) { return ShortId(id); }
        internal void MiniSignIn() { StartLogin(false); }
        internal void MiniSignOut() { Logout(); }
        internal void MiniLaunch() { LaunchGame(); }
        internal void MiniToggleServer() { ToggleServer(); }
        internal void MiniTogglePrivate() { TogglePrivateMode(); }

        internal string ToolsServerDir
        {
            get
            {
                try { return ResolveServerDir(); } catch (Exception) { return null; }
            }
        }

        // Ajoute les cosmétiques sortis depuis la dernière synchro (skins,
        // emotes, sprays...). Le serveur garde le profil en mémoire : on
        // l'arrête le temps de l'écriture puis on le relance.
        void SyncCosmetics(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !CosmeticsSync.IsDue(dir)) return;

            lock (_cosmeticsLock)
            {
                if (!CosmeticsSync.IsDue(dir)) return; // déjà faite entre-temps

                bool wasRunning = ServerManager.IsRunning();
                try
                {
                    if (wasRunning)
                    {
                        ServerManager.Stop();
                        _serverRunning = false;
                        System.Threading.Thread.Sleep(500);
                    }

                    Log(S("Recherche des nouveaux cosmétiques…", "Checking for new cosmetics…"));
                    string error;
                    var node = ServerManager.FindNode(_config);
                    if (node == null)
                        Log(S("Cosmétiques : moteur Node.js introuvable.", "Cosmetics: Node.js runtime not found."));
                    else if (!CosmeticsSync.Run(node, dir, Log, out error))
                        Log(S("Cosmétiques : ", "Cosmetics: ") + error);
                }
                catch (Exception ex)
                {
                    FileLog.Write("Cosmétiques : " + ex.Message);
                    Log(S("Cosmétiques : ", "Cosmetics: ") + ex.Message);
                }
                finally
                {
                    if (wasRunning)
                    {
                        try
                        {
                            Process p;
                            ServerManager.Start(_config, Log, out p);
                            ServerManager.WaitReady(10);
                            _serverRunning = ServerManager.IsRunning();
                        }
                        catch (Exception ex)
                        {
                            FileLog.Write("Redémarrage après cosmétiques : " + ex.Message);
                        }
                    }
                    Invalidate();
                }
            }
        }

        // Applique V-Bucks / niveau / pseudo au profil du serveur privé local.
        // Le serveur garde ses profils en mémoire : on le redémarre après écriture.
        internal bool ApplyProfileChanges(long vbucks, int level, string pseudo, out string error)
        {
            error = null;
            var dir = ToolsServerDir;
            if (dir == null)
            {
                error = S("Dossier LawinServer introuvable.", "LawinServer folder not found.");
                return false;
            }

            bool wasRunning = ServerManager.IsRunning();
            try
            {
                if (wasRunning)
                {
                    Log(S("Arrêt du serveur pour appliquer le profil…", "Stopping the server to apply the profile…"));
                    ServerManager.Stop();
                    _serverRunning = false;
                    System.Threading.Thread.Sleep(900);
                }

                if (!ProfileTools.TrySetVbucks(dir, vbucks, out error)) return false;
                if (!ProfileTools.TrySetLevel(dir, level, out error)) return false;
                if (!ProfileTools.TrySetPseudo(dir, pseudo, out error)) return false;
                _statsLoaded = false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                bool restart = wasRunning || _prefs.AutoStartServer;
                if (restart)
                {
                    try
                    {
                        Process p;
                        ServerManager.Start(_config, Log, out p);
                        ServerManager.WaitReady(10);
                        _serverRunning = ServerManager.IsRunning();
                        Log(_serverRunning
                            ? S("Serveur privé relancé avec le nouveau profil.", "Private server restarted with the new profile.")
                            : S("Le serveur n'a pas redémarré.", "The server did not restart."));
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write("Redémarrage du serveur : " + ex.Message);
                    }
                }
                Invalidate();
            }

            Log(S("Profil du serveur mis à jour : ", "Server profile updated: ")
                + vbucks + " V-Bucks · " + S("niveau ", "level ") + level + " · " + pseudo);
            Status(S("Profil appliqué · relance Fortnite.", "Profile applied · relaunch Fortnite."));
            return true;
        }

        internal void ClearLogs()
        {
            _lines.Clear();
            Log(S("Journal effacé.", "Log cleared."));
        }

        internal static string T(string fr, string en)
        {
            return LauncherPrefs.Language_ == "en" ? en : fr;
        }

        // ================= preview (captures hors écran) =================

        internal void ApplyPreview(string state)
        {
            var parts = state.Split(':');
            string head = parts.Length > 0 ? parts[0] : state;

            switch (head)
            {
                case "console":
                    _view = View.Console;
                    _lines.Clear();
                    Log("X.HYBRID V1 Launcher démarré.");
                    Log("Proxy SSL local actif sur le port 8898.");
                    Log("Dossier serveur trouvé : C:\\Users\\kocek\\Desktop\\X.HYBRID\\LawinServer");
                    Log("[serveur] LawinServer démarré sur le port 80");
                    Log("Serveur privé prêt.");
                    Log("Connecté en tant que Solo Servat.");
                    Log("ERREUR lancement : exemple de ligne d'erreur.");
                    break;
                case "accounts":
                    _view = View.Accounts;
                    break;
                case "accountson":
                    _login = new EpicLoginResult();
                    _login.AccountId = "0e3c9a1f6d4b47f6a9f4f2a1b7c3d5e9";
                    _login.DisplayName = "Solo Servat";
                    _view = View.Accounts;
                    break;
                case "loginprompt":
                    _view = View.Login;
                    _phase = LoginPhase.Idle;
                    break;
                case "login":
                    _view = View.Login;
                    _phase = LoginPhase.Waiting;
                    _device = new DeviceAuthInfo();
                    _device.UserCode = "HNLFZBSW";
                    _device.VerificationUriComplete = "https://www.epicgames.com/id/activate";
                    break;
                case "loginerror":
                    _view = View.Login;
                    _phase = LoginPhase.Error;
                    _loginError = "Impossible de joindre Epic Games. Vérifie ta connexion Internet.";
                    break;
                case "connected":
                    _login = new EpicLoginResult();
                    _login.AccountId = "0e3c9a1f6d4b47f6a9f4f2a1b7c3d5e9";
                    _login.DisplayName = "Solo Servat";
                    _view = View.Home;
                    break;
                case "profile":
                    _login = new EpicLoginResult();
                    _login.AccountId = "0e3c9a1f6d4b47f6a9f4f2a1b7c3d5e9";
                    _login.DisplayName = "Solo Servat";
                    _view = View.Home;
                    _profileOpen = true;
                    break;
            }
            Invalidate();
        }

        internal Bitmap RenderPreview()
        {
            var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Window);
                using (var pe = new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height)))
                    OnPaint(pe);
            }
            return bmp;
        }
    }
}
