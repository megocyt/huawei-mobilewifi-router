using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HwWidget
{
    /// <summary>悬浮窗主界面：无边框圆角卡片，全部 GDI+ 自绘（暗色科技风），
    /// 支持拖动 / 滚轮调透明度 / 右键菜单 / 托盘，功能与 python 版一一对应。</summary>
    public class WidgetForm : Form
    {
        Cfg cfg;
        Device dev;
        readonly object devLock = new object();

        Snap snap;
        string err = "";
        Row[] rows = new Row[0];
        string hint = "";
        string lastErr = "";
        bool hiddenByTray, quitting;

        NotifyIcon tray;
        ContextMenuStrip menu;
        System.Windows.Forms.Timer clockTimer, hintTimer, saveTimer;
        Thread poll;
        volatile bool stopPoll;
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        Font fTitle, fLabel, fValue, fMono;
        int rowH, labelW;                       // 统一行高与标签列宽，保证两列对齐
        const int W = 396, Pad = 14, Rad = 12, RowGap = 4, IconW = 22;
        Point dragOff;
        bool dragging;
        static string localIp = "";

        enum Kind { Text, Signal, Plan, Battery }
        class Row
        {
            public string key, label, value;
            public Color color;
            public Kind kind;
            public double frac;
            public int bars, barMax;
            public bool mono;
            public string tail;
            public bool charging;
        }

        public WidgetForm()
        {
            cfg = Cfg.Load();
            SuspendLayout();
            Text = L.T("tray.title");
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Parse(cfg.bg);
            DoubleBuffered = true;
            KeyPreview = false;
            BuildFonts();
            Application.AddMessageFilter(new WheelFilter(this));

            menu = BuildMenu();
            tray = new NotifyIcon();
            tray.Icon = TrayIcon.Make();
            tray.Visible = true;
            tray.MouseClick += OnTrayClick;
            tray.MouseDoubleClick += delegate { ShowSettings(); };

            clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            clockTimer.Tick += delegate { if (snap != null) Invalidate(); };
            clockTimer.Start();
            hintTimer = new System.Windows.Forms.Timer { Interval = 1600 };
            hintTimer.Tick += delegate { hint = ""; hintTimer.Stop(); Invalidate(); };
            saveTimer = new System.Windows.Forms.Timer { Interval = 1200 };
            saveTimer.Tick += delegate { saveTimer.Stop(); cfg.Save(); };

            ApplyChrome();
            Relayout();
            if (cfg.posX != int.MinValue)
            {
                Rectangle wa2 = Screen.PrimaryScreen.WorkingArea;
                int px = Math.Max(wa2.Left, Math.Min(wa2.Right - Width - 8, cfg.posX));
                int py = Math.Max(wa2.Top, Math.Min(wa2.Bottom - Height - 8, cfg.posY));
                Location = new Point(px, py);
            }
            else
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - W - 40, wa.Top + 60);
            }
            ResumeLayout(false);

            lock (devLock) dev = NewDevice();
            poll = new Thread(PollLoop) { IsBackground = true };
            poll.Start();
        }

        Device NewDevice()
        {
            var d = new Device();
            string h = (cfg.host ?? "").Trim();
            d.baseUrl = h.IndexOf("://") >= 0 ? h : "http://" + h;
            d.user = string.IsNullOrEmpty(cfg.user) ? "admin" : cfg.user;
            d.password = cfg.password;
            return d;
        }

        // ---------------------------------------------------------------- 外观 / 字体
        void BuildFonts()
        {
            float size = Math.Max(8f, cfg.fontSize);
            fTitle = new Font(cfg.fontFam, Math.Max(7.5f, size - 3f), FontStyle.Regular);
            fLabel = new Font(cfg.fontFam, size, FontStyle.Regular);
            fValue = new Font(cfg.fontFam, size, FontStyle.Regular);
            fMono = new Font("Consolas", size, FontStyle.Regular);
            rowH = Math.Max(Math.Max(fValue.Height, fLabel.Height), fMono.Height);
            string[] probes = { L.T("lbl.network"), L.T("lbl.signal"), L.T("lbl.rate"), L.T("lbl.plan"),
                                L.T("lbl.traffic"), L.T("lbl.hosts"), L.T("lbl.battery"), L.T("lbl.local"),
                                L.T("lbl.wan"), L.T("lbl.uptime"), L.T("lbl.sms"), L.T("lbl.clock"),
                                L.T("lbl.status"), L.T("lbl.detail"), L.T("lbl.num") };
            int lw = 0;
            foreach (string t in probes)
            {
                int w2 = TextRenderer.MeasureText(t, fLabel).Width;
                if (w2 > lw) lw = w2;
            }
            labelW = lw + 2;
        }

        void ApplyChrome()
        {
            foreach (Font f in new[] { fTitle, fLabel, fValue, fMono }) if (f != null) f.Dispose();
            BuildFonts();
            TopMost = cfg.topmost;
            Opacity = Math.Max(0.25, Math.Min(1.0, cfg.alpha));
            FormBorderStyle = cfg.borderless ? FormBorderStyle.None : FormBorderStyle.FixedDialog;
            BackColor = Parse(cfg.bg);
            Relayout();
        }

        static Color Parse(string hex)
        {
            try
            {
                string s = hex.TrimStart('#');
                return Color.FromArgb(int.Parse(s.Substring(0, 2), NumberStyles.HexNumber),
                                      int.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
                                      int.Parse(s.Substring(4, 2), NumberStyles.HexNumber));
            }
            catch { return Color.Gainsboro; }
        }

        Color Col(string key)
        {
            switch (key)
            {
                case "accent": return Parse(cfg.accent);
                case "warn": return Parse(cfg.warn);
                case "bad": return Parse(cfg.bad);
                default: return Parse(cfg.fg);
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            int d = rad * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------------------------------------------------------------- 数据 -> 行
        static string LocalIp(string target)
        {
            if (!string.IsNullOrEmpty(localIp)) return localIp;
            try
            {
                using (var u = new UdpClient())
                {
                    u.Connect(string.IsNullOrEmpty(target) ? "192.168.8.1" : target, 9);
                    localIp = ((System.Net.IPEndPoint)u.Client.LocalEndPoint).Address.ToString();
                }
            }
            catch { localIp = "-"; }
            return localIp;
        }

        List<Row> BuildRows()
        {
            var outRows = new List<Row>();
            Snap s = snap;
            if (s == null)
            {
                outRows.Add(new Row { key = "conn", label = L.T("lbl.status"), value = L.T("v.fail"), color = Col("bad") });
                outRows.Add(new Row { key = "err", label = L.T("lbl.detail"), value = Trunc(err, 34), color = Col("fg") });
                return outRows;
            }
            if (!string.IsNullOrEmpty(err))
                outRows.Add(new Row { key = "err", label = L.T("lbl.status"), value = "⚠ " + Trunc(err, 28), color = Col("bad") });

            if (On("conn"))
                outRows.Add(new Row
                {
                    key = "conn", label = L.T("lbl.network"),
                    value = ((s.net ?? "-") + " " + L.Carrier(s.carrier)).Trim(),
                    color = Col(s.connected ? "accent" : "bad")
                });
            if (On("signal"))
            {
                string v = s.rsrp + " dBm · SINR " + s.sinr;
                string lvl = s.rsrpLevel;
                outRows.Add(new Row
                {
                    key = "signal", label = L.T("lbl.signal"), value = v, kind = Kind.Signal,
                    bars = s.sigBars, barMax = Math.Max(1, s.sigMax),
                    color = Col(lvl == "优" || lvl == "良" ? "accent" : lvl == "中" ? "warn" : "bad")
                });
            }
            if (On("rate"))
                outRows.Add(new Row
                {
                    key = "rate", label = L.T("lbl.rate"),
                    value = "↓" + Device.HumanRate(s.rateDown).Replace(" ", "") + "  ↑" + Device.HumanRate(s.rateUp).Replace(" ", ""),
                    color = Col("fg"), mono = true
                });
            if (On("plan") && s.planLimit > 0)
            {
                string u = Device.HumanBytes(s.planUsed, 1).Replace(" ", "");
                string lim = (s.planLimitRaw ?? "").Replace(" ", "");
                string v = TailUnit(u) == TailUnit(lim) ? NumPart(u) + "/" + lim : u + "/" + lim;
                if (s.planDaysLeft >= 0) v += " " + string.Format(L.T("fmt.daysleft"), s.planDaysLeft);
                outRows.Add(new Row
                {
                    key = "plan", label = L.T("lbl.plan"), value = v, kind = Kind.Plan,
                    frac = Math.Max(0, Math.Min(100, s.planPercent)) / 100.0,
                    color = Col(s.planOverLimit ? "bad" : s.planOverThreshold ? "warn" : "accent"),
                    tail = s.planPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                });
            }
            if (On("traffic"))
                outRows.Add(new Row
                {
                    key = "traffic", label = L.T("lbl.traffic"),
                    value = L.T("v.today") + " " + Device.HumanBytes(s.dayUsed, 1).Replace(" ", "") + " · " + L.T("v.month") + " " + Device.HumanBytes(s.monthDown + s.monthUp, 1).Replace(" ", ""),
                    color = Col("fg")
                });
            if (On("hosts"))
                outRows.Add(new Row { key = "hosts", label = L.T("lbl.hosts"), value = s.hostsActive + " / " + s.hostsTotal, color = Col("fg"), mono = true });
            if (On("battery") && s.battery != "-")
            {
                double pct;
                double.TryParse(s.battery.TrimEnd('%'), out pct);
                bool low = pct <= 20 && !s.charging;
                outRows.Add(new Row
                {
                    key = "battery", label = L.T("lbl.battery"), value = s.battery + (s.charging ? " " + L.T("v.charging") : ""),
                    kind = Kind.Battery, frac = pct / 100.0, color = Col(low ? "warn" : "accent"), charging = s.charging
                });
            }
            if (On("msisdn") && !string.IsNullOrEmpty(s.msisdn) && s.msisdn != "-")
                outRows.Add(new Row { key = "msisdn", label = L.T("lbl.num"), value = s.msisdn, color = Col("fg"), mono = true });
            if (On("ip"))
            {
                outRows.Add(new Row { key = "ip", label = L.T("lbl.local"), value = LocalIp(cfg.host), color = Col("fg"), mono = true });
                outRows.Add(new Row { key = "ip2", label = L.T("lbl.wan"), value = s.wanIp, color = Col("fg"), mono = true });
            }
            if (On("uptime") && s.uptime > 0)
                outRows.Add(new Row { key = "uptime", label = L.T("lbl.uptime"), value = Device.HumanDur(s.uptime), color = Col("fg") });
            if (On("sms") && s.smsUnread > 0)
                outRows.Add(new Row { key = "sms", label = L.T("lbl.sms"), value = string.Format(L.T("fmt.smsunread"), s.smsUnread), color = Col("warn") });
            if (On("clock"))
                outRows.Add(new Row { key = "clock", label = L.T("lbl.clock"), value = DateTime.Now.ToString("HH:mm:ss"), color = Col("fg"), mono = true });
            return outRows;
        }

        bool On(string key)
        {
            bool v;
            return cfg.rows.TryGetValue(key, out v) && v;
        }

        static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        static string NumPart(string s)
        {
            int i = s.Length - 1;
            while (i >= 0 && !char.IsDigit(s[i])) i--;
            return s.Substring(0, i + 1);
        }

        static string TailUnit(string s)
        {
            int i = s.Length - 1;
            while (i >= 0 && !char.IsDigit(s[i])) i--;
            return s.Substring(i + 1);
        }

        int HeaderHeight()
        {
            if (!cfg.showTitle) return Pad;
            return Pad - 2 + fValue.Height + 12 + 1 + 8;
        }

        void Relayout()
        {
            rows = BuildRows().ToArray();
            int h = HeaderHeight();
            foreach (Row r in rows)
            {
                h += rowH + RowGap;
                if (r.kind == Kind.Plan) h += 12;
            }
            h += Pad - 3 - RowGap;
            if (ClientSize.Height != h || ClientSize.Width != W)
            {
                ClientSize = new Size(W, h);
                using (var p = Round(new Rectangle(0, 0, W - 1, h - 1), Rad))
                {
                    if (Region != null) Region.Dispose();
                    Region = new Region(p);
                }
            }
            Invalidate();
        }

        // ---------------------------------------------------------------- 绘制
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle rc = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            Color bg = Parse(cfg.bg);
            Color bgLo = Blend(bg, Color.White, 0.055f);
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                                                    bgLo, bg, 90f))
                g.FillRectangle(br, ClientRectangle);

            Color accent = Col("accent");
            Color line = Blend(bg, Color.White, 0.14f);
            // 顶部一条渐隐的主色光带，科技感点睛
            using (var glow = new LinearGradientBrush(new Rectangle(0, 0, rc.Width, 3),
                      Color.FromArgb(0, accent), Color.FromArgb(150, accent), 0f))
                g.FillRectangle(glow, new Rectangle(0, 0, rc.Width, 3));
            using (var p = Round(rc, Rad))
            using (var pen = new Pen(Blend(bg, Color.White, 0.22f), 1))
                g.DrawPath(pen, p);

            int y;
            if (cfg.showTitle)
            {
                y = Pad - 2;
                bool alive = snap != null && string.IsNullOrEmpty(err);
                Color dot = alive ? accent : Col("bad");
                using (var halo = new SolidBrush(Blend(bg, dot, 0.35f)))
                    g.FillEllipse(halo, Pad - 1, y + 4, 11, 11);
                using (var b = new SolidBrush(dot))
                    g.FillEllipse(b, Pad + 1.5f, y + 5.5f, 6, 6);
                string title = hint.Length > 0 ? hint : "HUAWEI WiFi · " + cfg.host;
                TextRenderer.DrawText(g, title, fValue, new Point(Pad + 16, y),
                    Blend(Color.Gray, Parse(cfg.fg), 0.55f), TextFormatFlags.NoPrefix);
                // 右侧网络制式徽章
                string badge = snap == null ? "-" : snap.net;
                Size bs = TextRenderer.MeasureText(badge, fTitle);
                var br = new Rectangle(rc.Right - Pad - bs.Width - 14, y, bs.Width + 14, bs.Height + 6);
                using (var p = Round(br, 4))
                using (var pen = new Pen(Blend(bg, accent, 0.6f), 1))
                {
                    g.DrawPath(pen, p);
                    using (var b = new SolidBrush(Blend(bg, accent, 0.12f))) g.FillPath(b, p);
                }
                TextRenderer.DrawText(g, badge, fTitle,
                    new Point(br.X + 7, br.Y + 3), accent, TextFormatFlags.NoPrefix);
                y += fValue.Height + 12;
                using (var pen = new Pen(line, 1)) g.DrawLine(pen, Pad, y, rc.Right - Pad, y);
                y += 8;
            }
            else y = Pad;

            foreach (Row r in rows)
                DrawRow(g, r, ref y, rc);
        }

        void DrawRow(Graphics g, Row r, ref int y, Rectangle rc)
        {
            Color muted = Blend(Color.Gray, Parse(cfg.fg), 0.38f);
            int right = rc.Right - Pad;
            DrawIcon(g, r, new Rectangle(Pad, y, IconW, rowH), muted);
            int lx = Pad + IconW + 6;
            TextRenderer.DrawText(g, r.label, fLabel,
                new Rectangle(lx, y, labelW, rowH), muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            int tailW = r.kind == Kind.Plan ? 46 : 0;
            int vx = lx + labelW + 8;
            var vRect = new Rectangle(vx, y, right - tailW - vx, rowH);

            Font vf = r.mono ? fMono : fValue;
            TextRenderer.DrawText(g, r.value, vf, vRect, r.color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);

            if (r.kind == Kind.Plan)
            {
                TextRenderer.DrawText(g, r.tail ?? "", fTitle,
                    new Rectangle(right - tailW, y, tailW, rowH), r.color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                int ty = y + rowH + 4;
                var track = new Rectangle(vx, ty, right - vx, 4);
                using (var p = Round(track, 2))
                using (var b = new SolidBrush(Blend(Parse(cfg.bg), Color.White, 0.13f)))
                    g.FillPath(b, p);
                int fw = (int)Math.Round((track.Width - 2) * Math.Max(0, Math.Min(1, r.frac)));
                if (fw > 1)
                    using (var p = Round(new Rectangle(track.X, track.Y, fw + 1, track.Height), 2))
                    using (var b = new SolidBrush(r.color))
                        g.FillPath(b, p);
                y += 12;
            }
            y += rowH + RowGap;
        }

        /// <summary>行首图标列，按 key 几何绘制，颜色跟随行主题色。</summary>
        void DrawIcon(Graphics g, Row r, Rectangle box, Color muted)
        {
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            Color c = r.color;
            Color dim = Blend(Parse(cfg.bg), Color.White, 0.18f);
            using (var pen = new Pen(c, 1.4f))
            using (var penD = new Pen(dim, 1.4f))
            using (var bFill = new SolidBrush(c))
            using (var bDim = new SolidBrush(dim))
            {
                switch (r.key)
                {
                    case "conn":
                    case "err":
                    {
                        // WiFi 三段弧
                        for (int i = 1; i <= 3; i++)
                        {
                            float rr = 3f * i;
                            Pen p = (i == 1 || r.key == "conn") ? pen : penD;
                            g.DrawArc(p, cx - rr, cy + 4 - rr, rr * 2, rr * 2, 225, 90);
                        }
                        g.FillEllipse(bFill, cx - 1.5f, cy + 3, 3, 3);
                        break;
                    }
                    case "signal":
                    {
                        // 信号柱，底部对齐，按强度填色
                        int n = Math.Max(1, r.barMax);
                        float bw = Math.Min(3f, 18f / n);
                        float x0 = cx - (bw * n + (n - 1)) / 2f;
                        for (int i = 0; i < n; i++)
                        {
                            float bh = 3 + i * (11f / Math.Max(1, n - 1));
                            var rb = new RectangleF(x0 + i * (bw + 1), cy + 7 - bh, bw, bh);
                            g.FillRectangle(i < r.bars ? bFill : bDim, rb);
                        }
                        break;
                    }
                    case "rate":
                    {
                        // 下行/上行箭头
                        g.DrawLine(pen, cx - 5, cy - 6, cx - 5, cy + 5);
                        g.DrawLine(pen, cx - 8, cy + 2, cx - 5, cy + 5);
                        g.DrawLine(pen, cx - 2, cy + 2, cx - 5, cy + 5);
                        g.DrawLine(pen, cx + 5, cy + 6, cx + 5, cy - 5);
                        g.DrawLine(pen, cx + 2, cy - 2, cx + 5, cy - 5);
                        g.DrawLine(pen, cx + 8, cy - 2, cx + 5, cy - 5);
                        break;
                    }
                    case "plan":
                    {
                        // 圆环进度
                        var circ = new RectangleF(cx - 7, cy - 7, 14, 14);
                        g.DrawEllipse(penD, circ.X, circ.Y, circ.Width, circ.Height);
                        float sweep = (float)(360 * Math.Max(0, Math.Min(1, r.frac)));
                        if (sweep > 1)
                            g.FillPie(bFill, cx - 7, cy - 7, 14, 14, -90, sweep);
                        using (var bBg = new SolidBrush(Parse(cfg.bg)))
                            g.FillEllipse(bBg, cx - 3.5f, cy - 3.5f, 7, 7);
                        break;
                    }
                    case "traffic":
                    {
                        // 云形
                        g.FillEllipse(bFill, cx - 8, cy - 4, 8, 8);
                        g.FillEllipse(bFill, cx - 2, cy - 7, 10, 10);
                        g.FillRectangle(bFill, cx - 6, cy, 13, 5);
                        break;
                    }
                    case "hosts":
                    {
                        // 叠层终端
                        g.DrawRectangle(pen, cx - 8, cy - 6, 11, 8);
                        g.DrawRectangle(pen, cx - 3, cy - 2, 11, 8);
                        break;
                    }
                    case "battery":
                    {
                        // 电池图标：外框+正极，按电量填充；充电中内部叠加闪电
                        var body = new RectangleF(cx - 9, cy - 5, 16, 10);
                        g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
                        g.FillRectangle(bFill, cx + 7.5f, cy - 2.5f, 2, 5);
                        float fillW = (float)Math.Max(0, Math.Min(1, r.frac)) * 12f;
                        if (r.charging)
                        {
                            using (var bSoft = new SolidBrush(Blend(Parse(cfg.bg), c, 0.35f)))
                                g.FillRectangle(bSoft, cx - 7.5f, cy - 3.5f, fillW, 7);
                            PointF[] bolt = {
                                new PointF(cx + 1.5f, cy - 4.5f), new PointF(cx - 3f, cy + 0.5f),
                                new PointF(cx - 0.5f, cy + 0.5f), new PointF(cx - 2f, cy + 4.5f),
                                new PointF(cx + 3f, cy - 0.5f), new PointF(cx + 0.5f, cy - 0.5f)
                            };
                            g.FillPolygon(bFill, bolt);
                        }
                        else if (fillW > 0.5f)
                            g.FillRectangle(bFill, cx - 7.5f, cy - 3.5f, fillW, 7);
                        break;
                    }
                    case "msisdn":
                    {
                        // 手机：圆角机身 + 底部圆点
                        var phone = new Rectangle((int)(cx - 5), (int)(cy - 7), 10, 14);
                        using (var p = Round(phone, 3)) g.DrawPath(pen, p);
                        g.FillEllipse(bFill, cx - 1.2f, cy + 4.2f, 2.4f, 2.4f);
                        break;
                    }
                    case "ip":
                    case "ip2":
                    {
                        if (r.key == "ip")
                        {
                            // 显示器
                            g.DrawRectangle(pen, cx - 8, cy - 6, 16, 10);
                            g.DrawLine(pen, cx, cy + 4, cx, cy + 6);
                            g.DrawLine(pen, cx - 4, cy + 6, cx + 4, cy + 6);
                        }
                        else
                        {
                            // 地球
                            g.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
                            g.DrawEllipse(pen, cx - 3f, cy - 7, 6, 14);
                            g.DrawLine(pen, cx - 7, cy, cx + 7, cy);
                        }
                        break;
                    }
                    case "uptime":
                    {
                        // 沙漏
                        g.DrawLine(pen, cx - 5, cy - 7, cx + 5, cy - 7);
                        g.DrawLine(pen, cx - 5, cy + 7, cx + 5, cy + 7);
                        g.DrawLine(pen, cx - 5, cy - 7, cx, cy);
                        g.DrawLine(pen, cx + 5, cy - 7, cx, cy);
                        g.DrawLine(pen, cx - 5, cy + 7, cx, cy);
                        g.DrawLine(pen, cx + 5, cy + 7, cx, cy);
                        break;
                    }
                    case "sms":
                    {
                        // 信封
                        g.DrawRectangle(pen, cx - 8, cy - 5, 16, 10);
                        g.DrawLines(pen, new[] { new PointF(cx - 8, cy - 5), new PointF(cx, cy + 1), new PointF(cx + 8, cy - 5) });
                        break;
                    }
                    case "clock":
                    {
                        // 时钟
                        g.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
                        g.DrawLine(pen, cx, cy, cx, cy - 4);
                        g.DrawLine(pen, cx, cy, cx + 3, cy + 1);
                        break;
                    }
                }
            }
        }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // ---------------------------------------------------------------- 采集线程
        void PollLoop()
        {
            while (!stopPoll)
            {
                Snap s = null;
                string e = "";
                try { lock (devLock) { s = dev.Sample(); } }
                catch (Exception ex) { e = ex.Message; try { Cfg.LogError(ex); } catch { } }
                if (s != null && !s.ok) e = s.err;
                else if (s != null) e = "";
                Snap cs = s; string ce = e;
                try
                {
                    if (IsHandleCreated && !stopPoll)
                        BeginInvoke(new Action(delegate
                        {
                            if (stopPoll) return;
                            snap = cs; err = ce;
                            Relayout();
                            UpdateTrayTip();
                            if (!string.IsNullOrEmpty(ce) && ce != lastErr && tray != null)
                                tray.ShowBalloonTip(3000, L.T("tray.balloon"), Trunc(ce, 90), ToolTipIcon.Warning);
                            lastErr = ce;
                        }));
                }
                catch (InvalidOperationException) { }      // dispose 竞态
                wake.WaitOne((int)(Math.Max(1.0, cfg.interval) * 1000));
            }
        }

        void UpdateTrayTip()
        {
            if (tray == null) return;
            var sb = new StringBuilder(L.T("tray.title"));
            if (snap != null)
            {
                sb.Append(" · ").Append(snap.net).Append(' ').Append(snap.conn);
                if (snap.planLimit > 0) sb.Append(" · ").Append(L.T("lbl.plan")).Append(' ').Append(snap.planPercent.ToString("0", CultureInfo.InvariantCulture)).Append('%');
                sb.Append(" · ").Append(L.T("lbl.hosts")).Append(' ').Append(snap.hostsActive);
            }
            if (!string.IsNullOrEmpty(err)) sb.Append(" · ⚠").Append(Trunc(err, 20));
            string tip = sb.ToString();
            if (tip.Length > 60) tip = tip.Substring(0, 60);
            tray.Text = tip;
        }

        // ---------------------------------------------------------------- 交互
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !cfg.locked)
            {
                dragging = true;
                dragOff = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging && !cfg.locked && (e.Button & MouseButtons.Left) != 0)
                Location = new Point(Location.X + e.X - dragOff.X, Location.Y + e.Y - dragOff.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; SavePos(); }
        }

        void SavePos()
        {
            cfg.posX = Location.X; cfg.posY = Location.Y;
            cfg.Save();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left) Do("manage");
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ApplyAlphaStep(e.Delta);
        }

        /// <summary>无边框窗体常常拿不到键盘焦点，Wheel 消息会被发给聚焦窗口；
        /// 用消息过滤把鼠标悬停在本窗体上的滚轮事件直接接管。</summary>
        class WheelFilter : IMessageFilter
        {
            const int WM_MOUSEWHEEL = 0x20A;
            readonly WidgetForm owner;
            public WheelFilter(WidgetForm f) { owner = f; }
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL || owner.quitting) return false;
                if (!owner.Visible) return false;
                if (!owner.Bounds.Contains(Cursor.Position)) return false;
                short delta = unchecked((short)((long)m.WParam.ToInt64() >> 16 & 0xFFFF));
                owner.ApplyAlphaStep((int)delta);
                return true;
            }
        }

        void ApplyAlphaStep(int delta)
        {
            double a = cfg.alpha + (delta > 0 ? 0.05 : -0.05);
            cfg.alpha = Math.Round(Math.Max(0.25, Math.Min(1.0, a)), 2);
            Opacity = cfg.alpha;
            hint = string.Format(L.T("hint.alpha"), (int)Math.Round(cfg.alpha * 100));
            hintTimer.Stop(); hintTimer.Start();
            saveTimer.Stop(); saveTimer.Start();
            Invalidate();
        }

        // ---------------------------------------------------------------- 命令
        void Do(string cmd)
        {
            switch (cmd)
            {
                case "toggle":
                    hiddenByTray = Visible;
                    if (hiddenByTray) { Hide(); }
                    else { Show(); BringToFront(); TopMost = false; TopMost = cfg.topmost; }
                    break;
                case "settings": ShowSettings(); break;
                case "refresh":
                    lock (devLock) { dev.ForgetSession(); }
                    wake.Set();
                    break;
                case "manage":
                    try
                    {
                        string url;
                        lock (devLock) url = dev != null ? dev.baseUrl : null;
                        if (string.IsNullOrEmpty(url))
                        {
                            string h = (cfg.host ?? "").Trim();
                            url = h.IndexOf("://") >= 0 ? h : "http://" + h;
                        }
                        Process.Start(url);
                    }
                    catch { }
                    break;
                case "top":
                    cfg.topmost = !cfg.topmost;
                    TopMost = cfg.topmost;
                    cfg.Save();
                    RebuildMenu();
                    break;
                case "lock":
                    cfg.locked = !cfg.locked;
                    cfg.Save();
                    hint = cfg.locked ? L.T("hint.locked") : L.T("hint.unlocked");
                    hintTimer.Stop(); hintTimer.Start();
                    RebuildMenu();
                    Invalidate();
                    break;
                case "quit": Quit(); break;
            }
        }

        void ShowSettings()
        {
            using (var f = new SettingsForm(cfg))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    cfg = f.Result;
                    L.Lang = cfg.language;
                    lock (devLock)
                    {
                        dev.Logout();
                        dev = NewDevice();
                    }
                    localIp = "";
                    ApplyChrome();
                    RebuildMenu();
                    wake.Set();
                }
            }
        }

        void Quit()
        {
            quitting = true;
            stopPoll = true;
            wake.Set();
            try { if (poll != null) poll.Join(2000); } catch { }
            lock (devLock) if (dev != null) dev.Logout();
            SavePos();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            Close();
        }

        // ---------------------------------------------------------------- 菜单 / 托盘
        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.ShowImageMargin = false;
            m.Padding = new Padding(4, 6, 4, 6);
            Add(m, L.T("menu.toggle"), "toggle");
            Add(m, L.T("menu.settings"), "settings");
            Add(m, L.T("menu.refresh"), "refresh");
            Add(m, L.T("menu.manage"), "manage");
            m.Items.Add(new ToolStripSeparator());
            var t = Add(m, (cfg.topmost ? L.T("menu.topoff") : L.T("menu.topon")), "top"); t.Tag = "top";
            var l = Add(m, (cfg.locked ? L.T("menu.unlock") : L.T("menu.lock")), "lock"); l.Tag = "lock";
            m.Items.Add(new ToolStripSeparator());
            Add(m, L.T("menu.quit"), "quit");
            return m;
        }

        static ToolStripMenuItem Add(ContextMenuStrip m, string text, string cmd)
        {
            var it = new ToolStripMenuItem(text);
            it.AutoSize = false;
            it.Height = 30;
            it.Width = 176;
            it.TextAlign = ContentAlignment.MiddleLeft;
            it.Padding = new Padding(6, 4, 6, 4);
            string c = cmd;
            it.Click += delegate { OwnerDo(c); };
            m.Items.Add(it);
            return it;
        }

        static WidgetForm instance;
        static void OwnerDo(string cmd) { if (instance != null) instance.Do(cmd); }

        void RebuildMenu()
        {
            var old = menu;
            menu = BuildMenu();
            ContextMenuStrip = menu;
            if (tray != null) tray.ContextMenuStrip = menu;
            if (old != null) old.Dispose();
        }

        void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Do("toggle");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            instance = this;
            ContextMenuStrip = menu;
            tray.ContextMenuStrip = menu;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting) { e.Cancel = true; Do("toggle"); return; }
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (var p = Round(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Rad))
            {
                if (Region != null) Region.Dispose();
                Region = new Region(p);
            }
        }
    }

    /// <summary>暗色右键菜单渲染器。</summary>
    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        class Table : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return Color.FromArgb(34, 60, 40); } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color MenuBorder { get { return Color.FromArgb(48, 54, 61); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(34, 60, 40); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(34, 60, 40); } }
            public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(13, 17, 23); } }
            public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(13, 17, 23); } }
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(13, 17, 23); } }
            public override Color ImageMarginGradientBegin { get { return Color.FromArgb(13, 17, 23); } }
            public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(13, 17, 23); } }
            public override Color ImageMarginGradientEnd { get { return Color.FromArgb(13, 17, 23); } }
            public override Color SeparatorDark { get { return Color.FromArgb(48, 54, 61); } }
            public override Color SeparatorLight { get { return Color.FromArgb(48, 54, 61); } }
        }

        public DarkMenuRenderer() : base(new Table()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextRectangle = new Rectangle(
                e.TextRectangle.Left,
                Math.Max(0, (e.Item.Height - e.TextFont.Height) / 2),
                Math.Max(e.TextRectangle.Width, e.Item.Width),
                e.TextFont.Height);
            e.TextColor = e.Item.Selected ? Color.FromArgb(230, 237, 243) : Color.FromArgb(201, 209, 217);
            base.OnRenderItemText(e);
        }
    }
}
