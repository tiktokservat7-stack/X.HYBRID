using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FNHybrid
{
    // Petite fenêtre X.HYBRID ouverte par une tuile du bas.
    // Elle contient uniquement le contenu de sa tuile et ses vraies actions.
    public class MiniWindow : Form
    {
        public enum Kind { Console, Account, Custom, Private }

        struct Zone
        {
            public RectangleF R;
            public string Id;
            public Action Act;
        }

        readonly MainForm _owner;
        readonly Kind _kind;
        readonly List<Zone> _zones = new List<Zone>();
        string _hot;
        int _scroll;
        Timer _tick;
        string _signature = "";

        TextBox _tbVb, _tbLvl, _tbName;
        RectangleF _inVb, _inLvl, _inName;
        string _msg = "";
        bool _msgError;
        bool _applying;

        public MiniWindow(MainForm owner, Kind kind)
        {
            _owner = owner;
            _kind = kind;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(5, 7, 11);
            DoubleBuffered = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Font = owner.F(9f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            ClientSize = new Size(owner.Sc(BaseWidth()), owner.Sc(BaseHeight()));
            Text = "X.HYBRID V1 · " + Title2();

            if (_kind == Kind.Custom) BuildInputs();

            _tick = new Timer();
            _tick.Interval = 400;
            _tick.Tick += delegate { Poll(); };
            _tick.Start();
        }

        int BaseWidth()
        {
            switch (_kind)
            {
                case Kind.Console: return 560;
                case Kind.Account: return 470;
                case Kind.Custom: return 500;
                default: return 490;
            }
        }

        int BaseHeight()
        {
            switch (_kind)
            {
                case Kind.Console: return 390;
                case Kind.Account: return 300;
                case Kind.Custom: return 430;
                default: return 330;
            }
        }

        string Title2()
        {
            switch (_kind)
            {
                case Kind.Console: return MainForm.T("CONSOLE", "CONSOLE");
                case Kind.Account: return MainForm.T("COMPTE", "ACCOUNT");
                case Kind.Custom: return MainForm.T("PERSONNALISATION", "CUSTOMISE");
                default: return MainForm.T("MODE PRIVÉ", "PRIVATE MODE");
            }
        }

        string Icon2()
        {
            switch (_kind)
            {
                case Kind.Console: return "terminal";
                case Kind.Account: return "user";
                case Kind.Custom: return "gamepad";
                default: return "key";
            }
        }

        int Sc(int v) { return _owner.Sc(v); }
        float Sc(float v) { return _owner.Sc(v); }
        Font F(float pt) { return _owner.F(pt); }
        Font FB(float pt) { return _owner.FB(pt); }
        Font FM(float pt) { return _owner.FM(pt); }

        void Poll()
        {
            string sig = (_owner.CurrentLogin == null ? "-" : _owner.CurrentLogin.AccountId)
                       + "|" + _owner.IsServerRunning + "|" + _owner.IsPrivateOn + "|" + _owner.IsBusy
                       + "|" + (_kind == Kind.Console ? _owner.LogLines.Count.ToString() : "");
            if (sig != _signature)
            {
                _signature = sig;
                Invalidate();
            }
        }

        public void PlaceAbove(RectangleF anchor)
        {
            try
            {
                var wa = Screen.FromControl(_owner).WorkingArea;
                var p = _owner.PointToScreen(new Point((int)anchor.X, (int)anchor.Y));
                int x = p.X + (int)(anchor.Width / 2f) - Width / 2;
                int y = p.Y - Height - Sc(10);
                if (y < wa.Top + 4) y = p.Y + (int)anchor.Height + Sc(10);
                if (y + Height > wa.Bottom) y = wa.Bottom - Height - 4;
                x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - Width - 4));
                Location = new Point(x, y);
            }
            catch (Exception) { }
        }

        // ================= rendu =================

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.FromArgb(5, 7, 11));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _zones.Clear();

            int W = ClientSize.Width;
            int H = ClientSize.Height;
            if (W < 40 || H < 40) return;

            var frame = new RectangleF(Sc(1.5f), Sc(1.5f), W - Sc(3f), H - Sc(3f));
            using (var path = Theme.Chamfer(frame, Sc(14), Sc(22), Sc(22), Sc(14)))
            {
                if (_owner.Prefs.GlowBorders)
                    using (var glow = new Pen(Color.FromArgb(30, Theme.Electric), Sc(7)))
                        g.DrawPath(glow, path);
                Theme.FillVertical(g, path, Color.FromArgb(9, 18, 31), Color.FromArgb(4, 7, 12));
                using (var pen = new Pen(Color.FromArgb(235, 0, 122, 240), Math.Max(1f, Sc(1.3f))))
                    g.DrawPath(pen, path);
            }

            float th = Sc(46);
            float brandEnd = DrawBrand(g, Sc(16), th);

            var badge = new RectangleF(brandEnd + Sc(8), th / 2f - Sc(9), Sc(24), Sc(18));
            using (var p = Theme.Round(badge, Sc(4)))
            {
                using (var br = new SolidBrush(Color.FromArgb(24, 54, 98))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(185, 72, 162, 255), Math.Max(1f, Sc(1)))) g.DrawPath(pen, p);
            }
            Theme.Text(g, "V1", FB(6.6f), Color.FromArgb(178, 220, 255), badge,
                StringAlignment.Center, StringAlignment.Center, false);

            float kindX = badge.Right + Sc(11);
            Theme.Icon(g, Icon2(), new RectangleF(kindX, th / 2f - Sc(7), Sc(14), Sc(14)),
                Color.FromArgb(120, 176, 230), 1.5f);
            Theme.Text(g, Title2(), FB(8.2f), Color.FromArgb(188, 208, 232),
                new RectangleF(kindX + Sc(20), 0, W - kindX - Sc(78), th));

            var close = new RectangleF(W - Sc(44), Sc(7), Sc(30), Sc(30));
            if (_hot == "close")
                using (var p = Theme.Round(close, Sc(7)))
                using (var br = new SolidBrush(Color.FromArgb(34, 22, 40, 62)))
                    g.FillPath(br, p);
            Theme.Icon(g, "close", new RectangleF(close.X + Sc(8), close.Y + Sc(8), Sc(14), Sc(14)),
                _hot == "close" ? Theme.Ink : Color.FromArgb(178, 208, 240), 1.7f);
            Zone_(close, "close", Close);

            using (var pen = new Pen(Color.FromArgb(90, 22, 54, 92)))
                g.DrawLine(pen, Sc(14), th, W - Sc(14), th);

            var body = new RectangleF(Sc(16), th + Sc(10), W - Sc(32), H - th - Sc(22));

            switch (_kind)
            {
                case Kind.Console: PaintConsole(g, body); break;
                case Kind.Account: PaintAccount(g, body); break;
                case Kind.Custom: PaintCustom(g, body); break;
                default: PaintPrivate(g, body); break;
            }
        }

        float DrawBrand(Graphics g, float x, float th)
        {
            using (var f = Theme.Brand(Sc(15f)))
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (var blue = new SolidBrush(Theme.Electric))
            using (var white = new SolidBrush(Theme.Ink))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                sf.FormatFlags &= ~StringFormatFlags.LineLimit;
                float w1 = g.MeasureString("X.", f, 1000, sf).Width;
                float w2 = g.MeasureString("HYBRID", f, 1000, sf).Width;
                g.DrawString("X.", f, blue, new RectangleF(x, 0, w1 + Sc(4), th), sf);
                g.DrawString("HYBRID", f, white, new RectangleF(x + w1, 0, Sc(200), th), sf);
                return x + w1 + w2;
            }
        }

        void Zone_(RectangleF r, string id, Action act)
        {
            var z = new Zone();
            z.R = r;
            z.Id = id;
            z.Act = act;
            _zones.Add(z);
        }

        Zone? Find(Point p)
        {
            for (int i = _zones.Count - 1; i >= 0; i--)
                if (_zones[i].R.Contains(p)) return _zones[i];
            return null;
        }

        // ================= console =================

        void PaintConsole(Graphics g, RectangleF body)
        {
            float btnH = Sc(30);
            var box = new RectangleF(body.X, body.Y, body.Width, body.Height - btnH - Sc(10));
            using (var p = Theme.Round(box, Sc(8)))
            {
                using (var br = new SolidBrush(Color.FromArgb(4, 8, 14))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(150, 18, 52, 88))) g.DrawPath(pen, p);
            }

            var lines = _owner.LogLines;
            float lh = Sc(14.5f);
            int max = Math.Max(1, (int)((box.Height - Sc(14)) / lh));
            int total = lines.Count;
            if (_scroll > Math.Max(0, total - max)) _scroll = Math.Max(0, total - max);
            int start = Math.Max(0, total - max - _scroll);

            var save = g.Save();
            g.SetClip(box);
            var f = FM(7.4f);
            for (int i = 0; i < max && start + i < total; i++)
            {
                string s = lines[start + i];
                Theme.Text(g, s, f, LineColor(s),
                    new RectangleF(box.X + Sc(10), box.Y + Sc(8) + i * lh, box.Width - Sc(20), lh),
                    StringAlignment.Near, StringAlignment.Center, false);
            }
            g.Restore(save);

            float y = box.Bottom + Sc(10);
            var copy = new RectangleF(body.Right - Sc(210), y, Sc(100), btnH);
            var clear = new RectangleF(body.Right - Sc(100), y, Sc(100), btnH);
            Btn(g, copy, MainForm.T("COPIER", "COPY"), "copy", false, true, "copy", CopyLogs);
            Btn(g, clear, MainForm.T("EFFACER", "CLEAR"), "trash", false, true, "clear", delegate { _owner.ClearLogs(); _scroll = 0; Invalidate(); });
            Theme.Text(g, total + " " + MainForm.T("lignes", "lines"), F(7.6f), Theme.Dim2,
                new RectangleF(body.X + Sc(2), y, Sc(180), btnH));
        }

        Color LineColor(string s)
        {
            if (s != null && s.IndexOf("ERREUR", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(255, 122, 132);
            if (s != null && s.IndexOf("ATTENTION", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(255, 198, 96);
            if (s != null && s.IndexOf("prêt", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromArgb(112, 222, 170);
            return Color.FromArgb(156, 182, 212);
        }

        void CopyLogs()
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, new List<string>(_owner.LogLines).ToArray())); }
            catch (Exception) { }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_kind != Kind.Console || e.Delta == 0) return;
            _scroll += e.Delta > 0 ? 3 : -3;
            if (_scroll < 0) _scroll = 0;
            Invalidate();
        }

        // ================= compte =================

        void PaintAccount(Graphics g, RectangleF body)
        {
            var login = _owner.CurrentLogin;
            if (login == null)
            {
                float d0 = Sc(50);
                DrawAvatar(g, new RectangleF(body.X + (body.Width - d0) / 2f, body.Y + Sc(6), d0, d0), d0);

                Theme.Text(g, MainForm.T("AUCUN COMPTE CONNECTÉ", "NO ACCOUNT SIGNED IN"), FB(10f), Theme.Ink,
                    new RectangleF(body.X, body.Y + Sc(66), body.Width, Sc(22)),
                    StringAlignment.Center, StringAlignment.Center, false);
                Theme.Text(g, MainForm.T("Connecte-toi avec Epic Games pour jouer sur le serveur privé.",
                        "Sign in with Epic Games to play on the private server."),
                    F(8.4f), Theme.Dim2, new RectangleF(body.X + Sc(24), body.Y + Sc(90), body.Width - Sc(48), Sc(36)),
                    StringAlignment.Center, StringAlignment.Near, true);

                var c = new RectangleF(body.X, body.Bottom - Sc(38), body.Width, Sc(38));
                Btn(g, c, MainForm.T("SE CONNECTER AVEC EPIC", "SIGN IN WITH EPIC"), "user", true, true, "signin",
                    delegate { _owner.MiniSignIn(); Close(); });
                return;
            }

            float d = Sc(58);
            DrawAvatar(g, new RectangleF(body.X, body.Y, d, d), d);
            Theme.Text(g, login.DisplayName, FB(12f), Theme.Ink,
                new RectangleF(body.X + d + Sc(16), body.Y + Sc(4), body.Width - d - Sc(20), Sc(24)));
            Theme.Text(g, MainForm.T("En ligne", "Online"), F(8.4f), Theme.Green,
                new RectangleF(body.X + d + Sc(16), body.Y + Sc(28), Sc(80), Sc(18)));
            using (var br = new SolidBrush(Theme.Green))
                g.FillEllipse(br, body.X + d + Sc(16) + Sc(46), body.Y + Sc(34), Sc(6), Sc(6));
            Theme.Text(g, MainForm.T("ID ", "ID ") + _owner.ShortIdPublic(login.AccountId), F(8f), Theme.Dim2,
                new RectangleF(body.X + d + Sc(16), body.Y + Sc(44), body.Width - d - Sc(20), Sc(16)));

            float y = body.Y + d + Sc(18);
            Row(g, body, y, MainForm.T("Session", "Session"),
                MainForm.T("Mémorisée (chiffrée DPAPI)", "Remembered (DPAPI encrypted)"));
            y += Sc(30);
            Row(g, body, y, MainForm.T("Stockage", "Storage"),
                MainForm.T("Aucun mot de passe conservé", "No password stored"));

            var outR = new RectangleF(body.X, body.Bottom - Sc(38), body.Width, Sc(38));
            Btn(g, outR, MainForm.T("SE DÉCONNECTER", "SIGN OUT"), "logout", false, true, "logout",
                delegate { _owner.MiniSignOut(); Invalidate(); });
        }

        void Row(Graphics g, RectangleF body, float y, string label, string value)
        {
            Theme.Text(g, label, F(8.4f), Theme.Dim2, new RectangleF(body.X, y, Sc(120), Sc(22)));
            Theme.Text(g, value, F(8.4f), Theme.Ink, new RectangleF(body.X + Sc(120), y, body.Width - Sc(120), Sc(22)));
        }

        void DrawAvatar(Graphics g, RectangleF r, float d)
        {
            using (var br = new SolidBrush(Color.FromArgb(17, 48, 90)))
                g.FillEllipse(br, r);
            using (var pen = new Pen(Color.FromArgb(240, 0, 138, 255), Math.Max(1.2f, Sc(1.6f))))
                g.DrawEllipse(pen, r);
            Theme.Icon(g, "user", new RectangleF(r.X + d * 0.24f, r.Y + d * 0.21f, d * 0.52f, d * 0.55f),
                Color.FromArgb(238, 246, 252), 1.7f);
        }

        // ================= personnalisation du profil =================

        void BuildInputs()
        {
            _tbVb = MakeBox();
            _tbLvl = MakeBox();
            _tbName = MakeBox();
            Controls.Add(_tbVb);
            Controls.Add(_tbLvl);
            Controls.Add(_tbName);
            LoadValues();
            LayoutInputs();
        }

        TextBox MakeBox()
        {
            var t = new TextBox();
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Color.FromArgb(6, 12, 21);
            t.ForeColor = Theme.Ink;
            t.Font = F(9.5f);
            t.ShortcutsEnabled = true;
            return t;
        }

        void LoadValues()
        {
            var dir = _owner.ToolsServerDir;
            long vb;
            int lvl;
            string name = null;

            if (dir == null)
            {
                _msg = MainForm.T("Dossier LawinServer introuvable.", "LawinServer folder not found.");
                _msgError = true;
            }
            else
            {
                if (ProfileTools.TryReadVbucks(dir, out vb)) _tbVb.Text = vb.ToString();
                if (ProfileTools.TryReadLevel(dir, out lvl)) _tbLvl.Text = lvl.ToString();
                name = ProfileTools.ReadPseudo(dir);
            }

            if (string.IsNullOrEmpty(_tbVb.Text)) _tbVb.Text = "0";
            if (string.IsNullOrEmpty(_tbLvl.Text)) _tbLvl.Text = "1";

            if (!string.IsNullOrEmpty(name)) _tbName.Text = name;
            else if (_tbName.Text.Length == 0)
            {
                // aucun pseudo configuré côté serveur : on propose celui du compte Epic
                var login = _owner.CurrentLogin;
                _tbName.Text = login != null && ProfileTools.IsValidPseudo(login.DisplayName)
                    ? login.DisplayName : "Player";
            }
        }

        void LayoutInputs()
        {
            if (_kind != Kind.Custom || _tbVb == null) return;

            float th = Sc(46);
            float y0 = th + Sc(10) + Sc(8);
            float step = Sc(42);
            float x = Sc(16) + Sc(142);
            float w = ClientSize.Width - Sc(32) - Sc(142);

            _inVb = new RectangleF(x, y0 + Sc(2), w, Sc(30));
            _inLvl = new RectangleF(x, y0 + step + Sc(2), w, Sc(30));
            _inName = new RectangleF(x, y0 + step * 2 + Sc(2), w, Sc(30));

            Place(_tbVb, _inVb);
            Place(_tbLvl, _inLvl);
            Place(_tbName, _inName);
        }

        void Place(TextBox t, RectangleF r)
        {
            t.Left = (int)r.X + Sc(10);
            t.Top = (int)r.Y + (int)((r.Height - t.Height) / 2f);
            t.Width = Math.Max(Sc(40), (int)r.Width - Sc(20));
        }

        void PaintCustom(Graphics g, RectangleF body)
        {
            float step = Sc(42);
            float y = body.Y + Sc(8);

            Label(g, body.X, y, "V-BUCKS", MainForm.T("Devise du compte", "Account currency"));
            InputFrame(g, _inVb);
            y += step;

            Label(g, body.X, y, MainForm.T("NIVEAU", "LEVEL"), MainForm.T("Niveau de compte", "Account level"));
            InputFrame(g, _inLvl);
            y += step;

            Label(g, body.X, y, MainForm.T("PSEUDO", "NAME"), MainForm.T("Nom du serveur privé", "Private server name"));
            InputFrame(g, _inName);
            y += step + Sc(4);

            if (!string.IsNullOrEmpty(_msg))
                Theme.Text(g, _msg, F(7.9f), _msgError ? Color.FromArgb(255, 132, 140) : Color.FromArgb(120, 226, 174),
                    new RectangleF(body.X, y, body.Width, Sc(18)));
            y += Sc(24);

            var apply = new RectangleF(body.X, y, body.Width, Sc(38));
            Btn(g, apply, MainForm.T("APPLIQUER", "APPLY"), "check", true, !_applying, "apply", ApplyProfile);

            Theme.Text(g, MainForm.T("Applique au serveur privé : relance ensuite Fortnite pour voir le résultat.",
                    "Applies to the private server: relaunch Fortnite afterwards to see it."),
                F(7.4f), Theme.Dim2, new RectangleF(body.X, apply.Bottom + Sc(9), body.Width, Sc(16)),
                StringAlignment.Near, StringAlignment.Center, false);
            Theme.Text(g, MainForm.T("Les couronnes ne sont pas gérées par ce serveur privé.",
                    "Crowns are not handled by this private server."),
                F(7.4f), Color.FromArgb(112, 132, 158), new RectangleF(body.X, apply.Bottom + Sc(26), body.Width, Sc(16)),
                StringAlignment.Near, StringAlignment.Center, false);

            var launch = new RectangleF(body.X, body.Bottom - Sc(38), body.Width, Sc(38));
            Btn(g, launch, MainForm.T("LANCER FORTNITE", "LAUNCH FORTNITE"), "gamepad", false,
                _owner.CurrentLogin != null && !_owner.IsBusy, "play",
                delegate { _owner.MiniLaunch(); Close(); });

            if (_owner.CurrentLogin == null)
                Theme.Text(g, MainForm.T("Connecte-toi pour lancer le jeu.", "Sign in to launch the game."),
                    F(7.5f), Color.FromArgb(255, 180, 96),
                    new RectangleF(body.X, launch.Top - Sc(18), body.Width, Sc(16)));
        }

        void Label(Graphics g, float x, float y, string label, string sub)
        {
            Theme.Text(g, label, FB(8.5f), Color.FromArgb(228, 238, 250),
                new RectangleF(x + Sc(2), y, Sc(132), Sc(17)));
            Theme.Text(g, sub, F(7.1f), Theme.Dim2,
                new RectangleF(x + Sc(2), y + Sc(16), Sc(136), Sc(14)));
        }

        void InputFrame(Graphics g, RectangleF r)
        {
            using (var p = Theme.Round(r, Sc(6)))
            {
                using (var br = new SolidBrush(Color.FromArgb(6, 12, 21))) g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(175, 26, 78, 134))) g.DrawPath(pen, p);
            }
        }

        void ApplyProfile()
        {
            if (_applying) return;

            var dir = _owner.ToolsServerDir;
            if (dir == null)
            {
                Fail(MainForm.T("Dossier LawinServer introuvable.", "LawinServer folder not found."));
                return;
            }

            long vb;
            if (!long.TryParse((_tbVb.Text ?? "").Trim(), out vb))
            {
                Fail(MainForm.T("V-Bucks : nombre invalide.", "V-Bucks: invalid number."));
                return;
            }

            int lvl;
            if (!int.TryParse((_tbLvl.Text ?? "").Trim(), out lvl))
            {
                Fail(MainForm.T("Niveau : nombre invalide.", "Level: invalid number."));
                return;
            }

            string pseudo = (_tbName.Text ?? "").Trim();
            if (!ProfileTools.IsValidPseudo(pseudo))
            {
                Fail(MainForm.T("Pseudo invalide (3 à 16 caractères : lettres, chiffres, _ . -).",
                                "Invalid name (3 to 16 characters: letters, digits, _ . -)."));
                return;
            }

            _applying = true;
            _msgError = false;
            _msg = MainForm.T("Application au serveur privé…", "Applying to the private server…");
            Invalidate();
            Application.DoEvents();

            string error;
            bool ok = _owner.ApplyProfileChanges(vb, lvl, pseudo, out error);

            _applying = false;
            _msgError = !ok;
            _msg = ok
                ? MainForm.T("Appliqué · relance Fortnite pour voir le résultat.", "Applied · relaunch Fortnite to see it.")
                : (string.IsNullOrEmpty(error) ? MainForm.T("Échec de l'application.", "Apply failed.") : error);
            if (ok) LoadValues();
            Invalidate();
        }

        void Fail(string msg)
        {
            _msgError = true;
            _msg = msg;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutInputs();
            Invalidate();
        }

        float Status(Graphics g, RectangleF body, float y, string label, string value, Color dot)
        {
            float h = Sc(38);
            var r = new RectangleF(body.X, y, body.Width, h);
            using (var p = Theme.Round(r, Sc(7)))
            {
                using (var br = new SolidBrush(Color.FromArgb(8, 16, 27)))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(110, 20, 58, 96)))
                    g.DrawPath(pen, p);
            }

            using (var br = new SolidBrush(dot))
                g.FillEllipse(br, r.X + Sc(12), r.Y + h / 2f - Sc(3), Sc(6), Sc(6));
            Theme.Text(g, label, F(8.6f), Color.FromArgb(206, 222, 240),
                new RectangleF(r.X + Sc(26), r.Y, r.Width - Sc(150), h));
            Theme.Text(g, value, F(8.4f), Theme.Dim,
                new RectangleF(r.X + r.Width - Sc(160), r.Y, Sc(148), h), StringAlignment.Far, StringAlignment.Center, false);
            return y + h + Sc(8);
        }

        // ================= mode privé =================

        void PaintPrivate(Graphics g, RectangleF body)
        {
            bool on = _owner.IsPrivateOn;

            var head = new RectangleF(body.X, body.Y, body.Width, Sc(64));
            using (var p = Theme.Round(head, Sc(8)))
            {
                using (var br = new LinearGradientBrush(head,
                    on ? Color.FromArgb(20, 56, 108) : Color.FromArgb(9, 18, 30),
                    Color.FromArgb(6, 12, 21), 90f))
                    g.FillPath(br, p);
                using (var pen = new Pen(Color.FromArgb(on ? 220 : 130, on ? 0 : 30, on ? 150 : 70, on ? 255 : 120)))
                    g.DrawPath(pen, p);
            }

            Theme.Icon(g, "key", new RectangleF(head.X + Sc(14), head.Y + Sc(20), Sc(24), Sc(24)),
                on ? Theme.Bright : Color.FromArgb(150, 182, 214), 1.6f);
            Theme.Text(g, MainForm.T("Mode privé", "Private mode"), FB(10f), Theme.Ink,
                new RectangleF(head.X + Sc(50), head.Y + Sc(10), head.Width - Sc(140), Sc(22)));
            Theme.Text(g, on ? MainForm.T("Actif · serveurs Fortnite redirigés", "Active · Fortnite servers redirected")
                             : MainForm.T("Inactif · Epic Games normal", "Inactive · normal Epic Games"),
                F(7.9f), on ? Color.FromArgb(130, 214, 255) : Theme.Dim2,
                new RectangleF(head.X + Sc(50), head.Y + Sc(32), head.Width - Sc(140), Sc(18)));

            var sw = new RectangleF(head.Right - Sc(64), head.Y + Sc(22), Sc(50), Sc(22));
            using (var p = Theme.Round(sw, sw.Height / 2f))
            {
                using (var br = new LinearGradientBrush(sw,
                    on ? Theme.Electric : Color.FromArgb(24, 36, 52),
                    on ? Color.FromArgb(0, 84, 190) : Color.FromArgb(18, 26, 38), 0f))
                    g.FillPath(br, p);
            }
            using (var br = new SolidBrush(on ? Color.White : Color.FromArgb(126, 150, 178)))
                g.FillEllipse(br, on ? sw.Right - Sc(20) : sw.X + Sc(4), sw.Y + Sc(3), Sc(16), Sc(16));
            Zone_(head, "private", delegate { _owner.MiniTogglePrivate(); Invalidate(); });

            float y = head.Bottom + Sc(12);
            y = Status(g, body, y, MainForm.T("Serveur privé", "Private server"),
                _owner.IsServerRunning ? MainForm.T("En ligne · port 80", "Online · port 80") : MainForm.T("Arrêté", "Stopped"),
                _owner.IsServerRunning ? Theme.Green : Color.FromArgb(140, 168, 196));

            var serverBtn = new RectangleF(body.X, y + Sc(2), body.Width, Sc(38));
            Btn(g, serverBtn, _owner.IsServerRunning ? MainForm.T("ARRÊTER LE SERVEUR", "STOP THE SERVER")
                                                     : MainForm.T("DÉMARRER LE SERVEUR", "START THE SERVER"),
                _owner.IsServerRunning ? "power" : "server", false, !_owner.IsBusy, "server",
                delegate { _owner.MiniToggleServer(); Invalidate(); });

            Theme.Text(g, MainForm.T("La redirection hôte modifie le fichier hosts de Windows (droits administrateur).",
                    "The host redirect edits the Windows hosts file (administrator rights)."),
                F(7.4f), Theme.Dim2, new RectangleF(body.X, body.Bottom - Sc(30), body.Width, Sc(28)),
                StringAlignment.Near, StringAlignment.Center, true);
        }

        // ================= bouton =================

        void Btn(Graphics g, RectangleF r, string label, string icon, bool primary, bool enabled, string id, Action act)
        {
            bool hot = enabled && _hot == id;
            using (var p = Theme.Round(r, Sc(7)))
            {
                if (primary)
                {
                    using (var br = new LinearGradientBrush(r,
                        enabled ? (hot ? Color.FromArgb(30, 146, 255) : Color.FromArgb(14, 118, 244))
                                : Color.FromArgb(26, 36, 50),
                        enabled ? (hot ? Color.FromArgb(8, 98, 224) : Color.FromArgb(4, 74, 184))
                                : Color.FromArgb(18, 24, 34), 90f))
                        g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(enabled ? 220 : 110, 62, 158, 255), Math.Max(1f, Sc(1.1f))))
                        g.DrawPath(pen, p);
                }
                else
                {
                    using (var br = new SolidBrush(hot ? Color.FromArgb(17, 32, 52) : Color.FromArgb(8, 15, 26)))
                        g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(165, 26, 74, 128))) g.DrawPath(pen, p);
                }
            }

            Color c = primary ? (enabled ? Theme.Ink : Theme.Dim2) : (enabled ? Theme.Ink : Theme.Dim2);
            if (!enabled) c = Color.FromArgb(126, 150, 178);

            var f = FB(8.3f);
            float tw = g.MeasureString(label, f).Width;
            float iw = Sc(15);
            float total = iw + Sc(8) + tw;
            float x = r.X + (r.Width - total) / 2f;
            Theme.Icon(g, icon, new RectangleF(x, r.Y + r.Height / 2f - iw / 2f, iw, iw), c, 1.6f);
            Theme.Text(g, label, f, c, new RectangleF(x + iw + Sc(8), r.Y, tw + Sc(6), r.Height));

            if (enabled) Zone_(r, id, act);
        }

        // ================= interactions =================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var z = Find(e.Location);
            string id = z.HasValue ? z.Value.Id : null;
            if (id != _hot)
            {
                _hot = id;
                Cursor = id != null && id != "drag" ? Cursors.Hand
                       : (e.Y <= Sc(46) ? Cursors.SizeAll : Cursors.Default);
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

            var z = Find(e.Location);
            if (z.HasValue)
            {
                if (z.Value.Act != null) z.Value.Act();
                return;
            }
            if (e.Y <= Sc(46)) DragWindow();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _tick.Stop(); _tick.Dispose(); } catch (Exception) { }
            base.OnFormClosed(e);
        }

        internal Bitmap RenderPreview()
        {
            var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using (var g = Graphics.FromImage(bmp))
            using (var pe = new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height)))
                OnPaint(pe);
            return bmp;
        }

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        void DragWindow()
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)0x2, IntPtr.Zero);
        }
    }
}
