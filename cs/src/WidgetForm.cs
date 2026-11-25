using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
        System.Windows.Forms.Timer clockTimer, hintTimer, saveTimer, trayClickTimer;
        SettingsForm settingsForm;
        SmsForm smsForm;
        HostsForm hostsForm;
        Point smsDownPt; string smsDownRow = ""; bool smsMoved;   // 短信行单击识别（按下后未拖动且在 up 时命中 sms 行）
        long lastTrayDoubleClickTicks;
        Image logoImg;                             // 标题栏左侧 logo，从内嵌 ico 取 64px 帧后缩放
        Thread poll;
        volatile bool stopPoll;
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        Font fTitle, fLabel, fValue, fMono;
        int rowH, labelW;                       // 统一行高与标签列宽，保证两列对齐
        string appliedLang = "";                // labelW 所依据的语言，用于语言实时切换后重算列宽
        const int W = 396, Pad = 14, Rad = 12, RowGap = 4, IconW = 22;
        const int MiniW = 234, MiniH = 30;   // 迷你监控条固定尺寸：速度位数变化不抖动窗口
        const int MiniColW = 78;             // 速度数值列宽（右对齐，固定列宽保证版式不动）
        const int MiniBarColW = 30;          // 信号柱列固定宽度（居左，含间隔）
        Point dragOff;
        bool dragging;
        static string localIp = "";
        HostsPopup hostsPopup;
        int[] rowTops;
        int hostsRowIdx = -1;
        bool msisdnRevealed;
        Rectangle eyeRect = Rectangle.Empty;

        enum Kind { Text, Signal, Plan, Battery }
        bool Mini { get { return cfg != null && cfg.style == "mini"; } }   // 迷你监控条样式（只显速度+信号）
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
            public bool eye;
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
            try { using (var tmp = TrayIcon.MakeFor(64)) logoImg = tmp.ToBitmap(); } catch { logoImg = null; }
            Application.AddMessageFilter(new WheelFilter(this));

            menu = BuildMenu();
            tray = new NotifyIcon();
            tray.Icon = TrayIcon.Make();
            tray.Visible = true;
            tray.MouseClick += OnTrayClick;
            tray.MouseDoubleClick += OnTrayDoubleClick;

            // 区分单击与双击：NotifyIcon 双击时会先触发一次 MouseClick，直接 toggle 会
            // 让“双击开设置”额外附带“隐藏悬浮窗”副作用。延迟 DoubleClickTime + 50ms 后
            // 才执行 toggle；期间如果 MouseDoubleClick 到达则取消 toggle。
            trayClickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime + 50 };
            trayClickTimer.Tick += delegate { trayClickTimer.Stop(); Do("toggle"); };

            clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            clockTimer.Tick += OnClockTick;
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

        /// <summary>行列表只在采样时重建，clock 行的 value 到下一轮采样前不会变；
        /// 每秒重新写入当前时间，时间看起来才会一秒一跳。</summary>
        void OnClockTick(object sender, EventArgs e)
        {
            // 语言实时切换（未保存预览）：重建行（刷新标签文字）并重算标签列宽，避免长译文截断
            if (appliedLang != L.Lang) { Relayout(); return; }
            string now = DateTime.Now.ToString("HH:mm:ss");
            foreach (var r in rows)
            {
                if (r.key != "clock") continue;
                if (r.value != now) r.value = now;
                Invalidate();
                return;
            }
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

        // ---------------------------------------------------------------- 短信窗设备门面
        // 设备协议是单会话：短信操作复用悬浮窗的 dev 与 devLock，避免第二个 Device 实例与轮询互相顶号。
        // 调用均来自短信窗后台线程（非 UI 线程），抢锁超时不会冻结界面。
        public List<SmsItem> SmsQueryAll(ref string err)
        {
            if (!Monitor.TryEnter(devLock, 30000)) { err = L.T("sms.busy"); return null; }
            try { string e2; var l = dev.SmsAll(out e2); err = e2; return l; }
            finally { Monitor.Exit(devLock); }
        }

        public string SmsMarkRead(List<string> idx)
        {
            if (!Monitor.TryEnter(devLock, 20000)) return L.T("sms.busy");
            try { return dev.MarkRead(idx); }
            finally { Monitor.Exit(devLock); }
        }

        public string SmsDelete(List<string> idx)
        {
            if (!Monitor.TryEnter(devLock, 20000)) return L.T("sms.busy");
            try { return dev.DeleteSms(idx); }
            finally { Monitor.Exit(devLock); }
        }

        public void SmsCounts(out int unread, out int total)
        {
            var s = snap;
            if (s == null) { unread = 0; total = 0; }
            else { unread = s.smsUnread; total = s.smsInbox; }
        }

        public void NotifyResample() { wake.Set(); }

        // 终端列表直接取已轮询的 snap.hosts（无需再访问设备，避免顶号）；返回副本防轮询线程并发修改。
        public List<Host> HostsList(out int active, out int total)
        {
            var s = snap;
            var copy = new List<Host>();
            if (s == null) { active = 0; total = 0; return copy; }
            copy.AddRange(s.hosts);
            active = s.hostsActive; total = s.hostsTotal;
            return copy;
        }

        void ShowSms()
        {
            // 模态且不传 owner（与 ShowSettings 同理防焦点错乱）；重入时直接激活已开窗口
            if (smsForm != null && !smsForm.IsDisposed)
            {
                smsForm.Activate();
                smsForm.BringToFront();
                return;
            }
            using (var f = new SmsForm(this))
            {
                smsForm = f;
                try { f.ShowDialog(); }
                finally { smsForm = null; }
            }
        }

        void ShowHosts()
        {
            // 与 ShowSms 同款：模态、不传 owner、重入时激活已开窗口。
            if (hostsForm != null && !hostsForm.IsDisposed)
            {
                hostsForm.Activate();
                hostsForm.BringToFront();
                return;
            }
            using (var f = new HostsForm(this))
            {
                hostsForm = f;
                try { f.ShowDialog(); }
                finally { hostsForm = null; }
            }
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
            ComputeLabelWidth();
        }

        /// <summary>按当前语言的各标签实测最大宽度算标签列宽（字体不随语言变，只有列宽需要）。</summary>
        void ComputeLabelWidth()
        {
            string[] probes = { L.T("lbl.network"), L.T("lbl.model"), L.T("lbl.signal"), L.T("lbl.rate"), L.T("lbl.plan"),
                                L.T("lbl.traffic"), L.T("lbl.hosts"), L.T("lbl.battery"), L.T("lbl.local"),
                                L.T("lbl.wan"), L.T("lbl.v6"), L.T("lbl.uptime"), L.T("lbl.sms"), L.T("lbl.clock"),
                                L.T("lbl.status"), L.T("lbl.detail"), L.T("lbl.num") };
            int lw = 0;
            foreach (string t in probes)
            {
                int w2 = TextRenderer.MeasureText(t, fLabel).Width;
                if (w2 > lw) lw = w2;
            }
            labelW = lw + 2;
            appliedLang = L.Lang;
        }

        void ApplyChrome()
        {
            foreach (Font f in new[] { fTitle, fLabel, fValue, fMono }) if (f != null) f.Dispose();
            BuildFonts();
            TopMost = cfg.topmost;
            Opacity = Math.Max(0.25, Math.Min(1.0, cfg.alpha));
            FormBorderStyle = cfg.borderless ? FormBorderStyle.None : FormBorderStyle.FixedDialog;
            BackColor = Parse(cfg.bg);
            ApplyClickThrough();
            Relayout();
        }

        // 穿透模式：给窗口加 WS_EX_TRANSPARENT 使鼠标命中测试落到下层窗口（点击/滚轮均穿透）。
        // 开启后悬浮窗自身收不到任何鼠标事件，因此只能通过托盘图标右键菜单关闭。
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_LAYERED = 0x00080000;
        const uint LWA_ALPHA = 0x2;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        void ApplyClickThrough()
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            if (cfg.clickThrough)
            {
                if ((ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) != (WS_EX_LAYERED | WS_EX_TRANSPARENT))
                    SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT);
                // 直接按当前透明度设置分层属性，保证加 layered 后窗口仍可见（不依赖 WinForms 的 Opacity 缓存）
                byte a = (byte)(Math.Max(0.25, Math.Min(1.0, cfg.alpha)) * 255);
                SetLayeredWindowAttributes(Handle, 0, a, LWA_ALPHA);
            }
            else if ((ex & WS_EX_TRANSPARENT) != 0)
            {
                SetWindowLong(Handle, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
                Opacity = Math.Max(0.25, Math.Min(1.0, cfg.alpha));   // 交回 WinForms 管理透明度
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyClickThrough();      // 句柄（重）建后扩展样式会丢失，需重新应用
            ApplyDwmFrame();          // DWM 描边/圆角属性同样随句柄重建丢失，需重新应用
        }

        /// <summary>Win11+ 的 DWM 会自动给窗口画一圈 1px 亮色描边（矮而深的迷你条上尤其明显，
        /// 用户反馈的“白边”）；圆角已由 Region 自绘，因此同时禁用 DWM 默认圆角并去掉描边。</summary>
        void ApplyDwmFrame()
        {
            try
            {
                int round = 1;      // DWMWCP_DONOTROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE
                int border = 2;     // DWMWA_COLOR_NONE：不画边框
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));  // DWMWA_BORDER_COLOR
            }
            catch { }   // dwmapi 调用失败不影响功能（Win10 以下属性不存在）
        }

        // “有时候出现白边”的防护：DWM 描边属性可能在句柄重建/显隐/激活切换时被系统重置，
        // 在这些时机统一补应用；并拦截非客户区绘制消息，阻止系统沿窗口边缘补画 1px 亮边
        // （与 SettingsForm 同款处理；迷你条矮，1px 亮边在深色桌面上格外明显）。
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ApplyDwmFrame(); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ApplyDwmFrame(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) ApplyDwmFrame(); }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCPAINT = 0x0085, WM_NCACTIVATE = 0x0086;
            const int WM_NCUAHDRAWCAPTION = 0x00AE, WM_NCUAHDRAWFRAME = 0x00AF;
            if (m.Msg == WM_NCUAHDRAWCAPTION || m.Msg == WM_NCUAHDRAWFRAME) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == WM_NCACTIVATE) { m.Result = (IntPtr)1; return; }
            if (m.Msg == WM_NCPAINT) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
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
            // 失败时 Sample() 返回 ok=false 的零值快照（非 null），故以 ok 判定“是否取到数据”；
            // 行集合只由配置决定：缺数据/请求失败时用 "-" 占位而不删行，
            // 且 Plan/Battery/Signal 的 kind 恒定（进度条额外高度不变），窗口高度不随采样成败闪动。
            bool has = s != null && s.ok;
            if (On("conn"))
            {
                string v; Color c;
                if (!has) { v = string.IsNullOrEmpty(err) ? L.T("v.fail") : Trunc(err, 30); c = Col("bad"); }
                else { v = ((s.net ?? "-") + " " + L.Carrier(s.carrier)).Trim(); c = Col(s.connected ? "accent" : "bad"); }
                outRows.Add(new Row { key = "conn", label = L.T("lbl.network"), value = v, color = c });
            }
            if (On("model"))
                outRows.Add(new Row { key = "model", label = L.T("lbl.model"), value = Ph(has ? s.model : null), color = Col("fg") });
            if (On("signal"))
            {
                string rsrp = Ph(has ? s.rsrp : null), sinr = Ph(has ? s.sinr : null);
                string v = (rsrp == "-" && sinr == "-") ? "-" : rsrp + " dBm · SINR " + sinr;
                string lvl = has ? s.rsrpLevel : "-";
                outRows.Add(new Row
                {
                    key = "signal", label = L.T("lbl.signal"), value = v, kind = Kind.Signal,
                    bars = has ? s.sigBars : 0, barMax = Math.Max(1, has ? s.sigMax : 5),
                    color = Col(lvl == "优" || lvl == "良" ? "accent" : lvl == "中" ? "warn" : lvl == "-" ? "fg" : "bad")
                });
            }
            if (On("rate"))
                outRows.Add(new Row
                {
                    key = "rate", label = L.T("lbl.rate"),
                    value = has ? "↓" + Device.HumanRate(s.rateDown).Replace(" ", "") + "  ↑" + Device.HumanRate(s.rateUp).Replace(" ", "") : "-",
                    color = Col("fg"), mono = true
                });
            if (On("plan"))
            {
                // 始终保留 Plan 行（含进度条额外高度）；无套餐/未取到时占位，避免高度跳变
                bool ok = has && s.planLimit > 0;
                string v = "-", tail = "-"; double frac = 0; Color c = Col("fg");
                if (ok)
                {
                    string u = Device.HumanBytes(s.planUsed, 1).Replace(" ", "");
                    string lim = (s.planLimitRaw ?? "").Replace(" ", "");
                    v = TailUnit(u) == TailUnit(lim) ? NumPart(u) + "/" + lim : u + "/" + lim;
                    if (s.planDaysLeft >= 0) v += " " + string.Format(L.T("fmt.daysleft"), s.planDaysLeft);
                    frac = Math.Max(0, Math.Min(100, s.planPercent)) / 100.0;
                    tail = s.planPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
                    c = Col(s.planOverLimit ? "bad" : s.planOverThreshold ? "warn" : "accent");
                }
                outRows.Add(new Row { key = "plan", label = L.T("lbl.plan"), value = v, kind = Kind.Plan, frac = frac, color = c, tail = tail });
            }
            if (On("traffic"))
                outRows.Add(new Row
                {
                    key = "traffic", label = L.T("lbl.traffic"),
                    value = has ? L.T("v.today") + " " + Device.HumanBytes(s.dayUsed, 1).Replace(" ", "") + " · " + L.T("v.month") + " " + Device.HumanBytes(s.monthDown + s.monthUp, 1).Replace(" ", "") : "-",
                    color = Col("fg")
                });
            if (On("hosts"))
                outRows.Add(new Row { key = "hosts", label = L.T("lbl.hosts"), value = has ? s.hostsActive + " / " + s.hostsTotal : "-", color = Col("fg"), mono = true });
            if (On("battery"))
            {
                bool ok = has && !string.IsNullOrEmpty(s.battery) && s.battery != "-";
                double pct = 0;
                if (ok) double.TryParse(s.battery.TrimEnd('%'), out pct);
                bool low = ok && pct <= 20 && !s.charging;
                outRows.Add(new Row
                {
                    key = "battery", label = L.T("lbl.battery"),
                    value = ok ? s.battery + (s.charging ? " " + L.T("v.charging") : "") : "-",
                    kind = Kind.Battery, frac = pct / 100.0, color = Col(low ? "warn" : ok ? "accent" : "fg"), charging = ok && s.charging
                });
            }
            if (On("msisdn"))
            {
                bool ok = has && !string.IsNullOrEmpty(s.msisdn) && s.msisdn != "-";
                outRows.Add(new Row { key = "msisdn", label = L.T("lbl.num"), value = ok ? (msisdnRevealed ? s.msisdn : MaskPhone(s.msisdn)) : "-", color = Col("fg"), mono = true, eye = ok });
            }
            if (On("ip"))
            {
                outRows.Add(new Row { key = "ip", label = L.T("lbl.local"), value = LocalIp(cfg.host), color = Col("fg"), mono = true });
                outRows.Add(new Row { key = "ip2", label = L.T("lbl.wan"), value = Ph(has ? s.wanIp : null), color = Col("fg"), mono = true });
                outRows.Add(new Row { key = "ip6", label = L.T("lbl.v6"), value = Ph(has ? s.wanIpv6 : null), color = Col("fg"), mono = true });
            }
            if (On("uptime"))
                outRows.Add(new Row { key = "uptime", label = L.T("lbl.uptime"), value = (has && s.uptime > 0) ? Device.HumanDur(s.uptime) : "-", color = Col("fg") });
            if (On("sms"))
                outRows.Add(new Row { key = "sms", label = L.T("lbl.sms"), value = has ? string.Format(L.T("fmt.sms"), s.smsUnread, s.smsInbox) : "-", color = Col(has && s.smsUnread > 0 ? "warn" : "fg") });
            if (On("clock"))
                outRows.Add(new Row { key = "clock", label = L.T("lbl.clock"), value = DateTime.Now.ToString("HH:mm:ss"), color = Col("fg"), mono = true });
            return outRows;
        }

        /// <summary>缺失字段统一占位 "-"（null / 空 / 设备回 "-" 均视为未取到）。</summary>
        static string Ph(string v)
        {
            return string.IsNullOrEmpty(v) || v == "-" ? "-" : v;
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
            if (appliedLang != L.Lang) ComputeLabelWidth();   // 语言实时切换后（未保存预览时）也重算列宽，避免长译文标签被截断
            if (Mini) { RelayoutMini(); Invalidate(); return; }
            rows = BuildRows().ToArray();
            int h = HeaderHeight();
            rowTops = new int[rows.Length];
            hostsRowIdx = -1;
            for (int i = 0; i < rows.Length; i++)
            {
                rowTops[i] = h;
                if (rows[i].key == "hosts") hostsRowIdx = i;
                h += rowH + RowGap;
                if (rows[i].kind == Kind.Plan) h += 12;
            }
            h += Pad - 3 - RowGap;
            if (ClientSize.Height != h || ClientSize.Width != W)
            {
                ClientSize = new Size(W, h);
                using (var p = Round(new Rectangle(0, 0, W, h), Rad))
                {
                    if (Region != null) Region.Dispose();
                    Region = new Region(p);
                }
            }
            Invalidate();
            if (hostsPopup != null && hostsPopup.Visible) RefreshHostsTip();
        }

        /// <summary>迷你监控条：固定尺寸圆角小窗，速度数值变化不再改变窗口宽度。</summary>
        void RelayoutMini()
        {
            HideHostsTip();          // 旧卡片的终端悬停提示在迷你样式下无意义
            if (ClientSize.Height != MiniH || ClientSize.Width != MiniW)
            {
                ClientSize = new Size(MiniW, MiniH);
                using (var p = Round(new Rectangle(0, 0, MiniW, MiniH), 5))
                {
                    if (Region != null) Region.Dispose();
                    Region = new Region(p);
                }
                KeepOnScreen();
            }
        }

        /// <summary>样式切换/尺寸变化后把窗口拉回工作区，避免变宽后超出屏幕边缘。</summary>
        void KeepOnScreen()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int px = Math.Max(wa.Left, Math.Min(wa.Right - Width - 4, Left));
            int py = Math.Max(wa.Top, Math.Min(wa.Bottom - Height - 4, Top));
            if (px != Left || py != Top) Location = new Point(px, py);
        }

        // ---------------------------------------------------------------- 绘制
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Mini) { PaintMini(e.Graphics); return; }
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
            // 不再绘制顶部主色光带与整圈描边（用户不想要顶部那条线）；圆角由 Region 裁剪保证
            if (cfg.clickThrough && cfg.clickThroughBorder)
            {
                // 穿透模式虚线描边（设置可关）：语言无关地提示当前窗口对鼠标“惰性可穿透”
                using (var p = Round(new Rectangle(rc.X + 2, rc.Y + 2, rc.Width - 4, rc.Height - 4), Rad))
                using (var pen = new Pen(Blend(bg, Color.White, 0.22f), 2))   // 贴近背景的微提亮灰，低调提示不抢眼
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }
            }

            int y;
            if (cfg.showTitle)
            {
                y = Pad - 2;
                bool alive = snap != null && string.IsNullOrEmpty(err);
                Color dot = alive ? accent : Col("bad");
                // 左侧 logo：64px 帧缩到 20x20，高 DPI 不糊；右下角叠上状态小圆点
                int logoSize = 20;
                int textX = Pad;
                if (logoImg != null)
                {
                    var old = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(logoImg, Pad - 2, y, logoSize, logoSize);
                    g.InterpolationMode = old;
                    textX = Pad + logoSize + 2;
                }
                using (var halo = new SolidBrush(Blend(bg, dot, 0.35f)))
                    g.FillEllipse(halo, textX - 8, y + logoSize - 8, 9, 9);
                using (var b = new SolidBrush(dot))
                    g.FillEllipse(b, textX - 6, y + logoSize - 6, 5, 5);
                string title = hint.Length > 0 ? hint : "HUAWEI WiFi · " + cfg.host;
                TextRenderer.DrawText(g, title, fValue, new Point(textX + 2, y),
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

            eyeRect = Rectangle.Empty;
            foreach (Row r in rows)
                DrawRow(g, r, ref y, rc);
        }

        // ---------------------------------------------------------------- 迷你监控条绘制
        /// <summary>迷你样式：固定尺寸单行监控条——↑ 上行速率（告警橙）/ ↓ 下行速率（正常绿），
        /// 实心箭头图标与常见流量监控程序一致；右侧信号柱按质量着色；读取失败时显示叹号+错误文本。</summary>
        void PaintMini(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;
            Color bgc = Parse(cfg.bg);
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Blend(bgc, Color.White, 0.055f), bgc, 90f))
                g.FillRectangle(br, ClientRectangle);
            if (cfg.clickThrough && cfg.clickThroughBorder)
                using (var p = Round(new Rectangle(2, 2, w - 5, h - 5), 4))
                using (var pen = new Pen(Blend(bgc, Color.White, 0.22f), 2))
                {
                    pen.DashStyle = DashStyle.Dash;
                    g.DrawPath(pen, p);
                }

            // 拖动提示（滚轮调透明度等）优先居中显示，与完整卡片行为一致
            if (!string.IsNullOrEmpty(hint))
            {
                TextRenderer.DrawText(g, hint, fValue, new Rectangle(0, 0, w, h),
                    Blend(Color.Gray, Parse(cfg.fg), 0.55f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            Snap s = snap;
            bool has = s != null && s.ok;
            int cy = h / 2;
            // 读取失败：叹号 + 错误文本，尺寸保持固定
            if (!has && !string.IsNullOrEmpty(err))
            {
                DrawMiniErr(g, 16, h / 2, Col("bad"));
                string t = Trunc(err, 14);
                Font owned;
                Font vf = FitValueFont(g, fValue, t, w - 30 - 10, out owned);
                TextRenderer.DrawText(g, t, vf, new Point(28, h / 2 - vf.Height / 2), Col("bad"), TextFormatFlags.NoPrefix);
                if (owned != null) owned.Dispose();
                return;
            }

            // 信号柱：左侧底部对齐，按信号质量着色；无数据时置灰（固定列宽，版式不随格数变化）
            int n = has ? Math.Max(1, s.sigMax) : 5;
            int lit = has ? s.sigBars : 0;
            Color sc = has ? Col(s.rsrpLevel == "优" || s.rsrpLevel == "良" ? "accent" : s.rsrpLevel == "中" ? "warn" : s.rsrpLevel == "-" ? "fg" : "bad")
                           : Blend(bgc, Color.White, 0.18f);
            float bw = Math.Min(3f, 18f / n);
            float bx0 = 10;
            for (int i = 0; i < n; i++)
            {
                float bh = 3 + i * (11f / Math.Max(1, n - 1));
                using (var b = new SolidBrush(i < lit ? sc : Blend(bgc, Color.White, 0.18f)))
                    g.FillRectangle(b, bx0 + i * (bw + 1), cy + 7 - bh, bw, bh);
            }

            // 单行速度：箭头与文本同色（↑橙 / ↓绿），数值在固定列内右对齐，位数变化不挪版式
            Color up = Col("warn"), dn = Col("accent");
            int lh = Math.Max(fValue.Height, fMono.Height);
            int aX = 10 + MiniBarColW;               // ↑ 箭头起点（信号柱列右侧）
            DrawMiniArrow(g, true, up, aX, cy);
            DrawMiniRate(g, has ? Device.HumanRate(s.rateUp) : "-", aX + 14, MiniColW, cy, lh, up);
            DrawMiniArrow(g, false, dn, aX + 14 + MiniColW + 6, cy);
            DrawMiniRate(g, has ? Device.HumanRate(s.rateDown) : "-", aX + 14 + MiniColW + 20, MiniColW, cy, lh, dn);
        }

        /// <summary>迷你条速度文本：在固定列内右对齐，超出列宽时自动缩字号（窗口尺寸不变）。</summary>
        void DrawMiniRate(Graphics g, string text, int x, int colW, int cy, int h, Color c)
        {
            Font owned;
            Font vf = FitValueFont(g, fMono, text, colW, out owned);
            int tw = TextRenderer.MeasureText(g, text, vf).Width;
            TextRenderer.DrawText(g, text, vf, new Point(x + colW - tw, cy - vf.Height / 2), c, TextFormatFlags.NoPrefix);
            if (owned != null) owned.Dispose();
        }

        /// <summary>迷你条上行(↑)/下行(↓)箭头：实心三角头 + 矩形短杆，
        /// 与常见流量监控程序字体箭头的形态一致（非线条式）。</summary>
        static void DrawMiniArrow(Graphics g, bool up, Color c, int x, int cy)
        {
            float cx = x + 6f;
            using (var b = new SolidBrush(c))
            {
                if (up)
                {
                    // 三角头在上，杆向下
                    g.FillPolygon(b, new[] { new PointF(cx, cy - 7), new PointF(cx - 4f, cy - 1.5f), new PointF(cx + 4f, cy - 1.5f) });
                    g.FillRectangle(b, cx - 1.2f, cy - 1.5f, 2.4f, 8f);
                }
                else
                {
                    // 杆在上，三角头在下
                    g.FillRectangle(b, cx - 1.2f, cy - 6.5f, 2.4f, 8f);
                    g.FillPolygon(b, new[] { new PointF(cx, cy + 7), new PointF(cx - 4f, cy + 1.5f), new PointF(cx + 4f, cy + 1.5f) });
                }
            }
        }

        /// <summary>迷你条读取错误时的圆圈叹号。</summary>
        static void DrawMiniErr(Graphics g, int x, int cy, Color c)
        {
            using (var pen = new Pen(c, 1.4f))
            {
                g.DrawEllipse(pen, x - 6, cy - 6, 12, 12);
                g.DrawLine(pen, x, cy - 3.5f, x, cy + 1);
                using (var b = new SolidBrush(c))
                    g.FillEllipse(b, x - 1, cy + 2.5f, 2, 2);
            }
        }

        void DrawRow(Graphics g, Row r, ref int y, Rectangle rc)
        {
            Font vfOwned;

            Color muted = Blend(Color.Gray, Parse(cfg.fg), 0.38f);
            int right = rc.Right - Pad;
            DrawIcon(g, r, new Rectangle(Pad, y, IconW, rowH), muted);
            int lx = Pad + IconW + 6;
            TextRenderer.DrawText(g, r.label, fLabel,
                new Rectangle(lx, y, labelW, rowH), muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            int tailW = r.kind == Kind.Plan ? 46 : (r.eye ? 22 : 0);
            int vx = lx + labelW + 8;
            var vRect = new Rectangle(vx, y, right - tailW - vx, rowH);

            Font vf = FitValueFont(g, r.mono ? fMono : fValue, r.value, vRect.Width, out vfOwned);
            TextRenderer.DrawText(g, r.value, vf, vRect, r.color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);
            if (vfOwned != null) { vfOwned.Dispose(); vfOwned = null; }

            if (r.eye)
            {
                eyeRect = new Rectangle(right - 18, y + (rowH - 14) / 2, 18, 14);
                DrawEye(g, eyeRect, msisdnRevealed, r.color);
            }

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

        /// <summary>内容文字放不下时先逐步缩小字号（最多降 3pt，且不低于 8pt），
        /// 缩到下限仍放不下则保持该字号交给 EndEllipsis 截断。
        /// 返回实际使用的字体；若新建了缩字号字体则通过 <paramref name="owned"/> 输出，调用方负责释放。</summary>
        static Font FitValueFont(Graphics g, Font baseFont, string text, int maxW, out Font owned)
        {
            owned = null;
            if (string.IsNullOrEmpty(text) || maxW <= 0) return baseFont;
            float minSize = Math.Max(8f, baseFont.Size - 3f);
            Font cur = baseFont;
            for (float sz = baseFont.Size; sz > minSize;)
            {
                Size need = TextRenderer.MeasureText(g, text, cur, new Size(int.MaxValue, cur.Height),
                    TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                if (need.Width <= maxW) return cur;
                sz = Math.Max(minSize, sz - 0.5f);
                var nf = new Font(cur.FontFamily, sz, cur.Style);
                if (owned != null) owned.Dispose();
                owned = nf;
                cur = nf;
                if (sz == minSize) break;
            }
            return cur;
        }

        static string MaskPhone(string phone)
        {
            if (string.IsNullOrEmpty(phone) || phone.Length <= 4) return phone;
            if (phone.Length <= 7) return phone.Substring(0, 2) + new string('*', phone.Length - 2);
            return phone.Substring(0, 3) + "****" + phone.Substring(phone.Length - 4);
        }

        static void DrawEye(Graphics g, Rectangle box, bool open, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
            // 眼球轮廓（拉伸椭圆）
            float ew = 8f, eh = 5f;
            using (var pen = new Pen(c, 1.3f))
            {
                g.DrawEllipse(pen, cx - ew, cy - eh, ew * 2, eh * 2);
                // 瞳孔
                if (open)
                    g.DrawEllipse(pen, cx - 2.5f, cy - 2.5f, 5, 5);
                else
                {
                    // 关闭状态：横线代替瞳孔
                    g.DrawLine(pen, cx - 2.5f, cy, cx + 2.5f, cy);
                }
            }
            if (!open)
            {
                // 斜线划掉
                using (var pen = new Pen(c, 1.2f))
                    g.DrawLine(pen, cx - 6, cy + 5, cx + 6, cy - 5);
            }
        }

        // ---------------------------------------------------------------- 终端行悬停提示
        void CheckHostsHover(Point p)
        {
            if (Mini || hostsRowIdx < 0 || rowTops == null)
            {
                HideHostsTip(); return;
            }
            int top = rowTops[hostsRowIdx];
            int bot = top + rowH;
            if (p.Y < top || p.Y >= bot || snap == null || snap.hosts.Count == 0)
            {
                HideHostsTip(); return;
            }
            string text = BuildHostsTooltip();
            if (string.IsNullOrEmpty(text)) { HideHostsTip(); return; }
            if (hostsPopup == null) hostsPopup = new HostsPopup();
            Point screen = PointToScreen(new Point(Pad + IconW + 6, bot + 6));
            hostsPopup.ShowContent(text, screen, Parse(cfg.bg), Parse(cfg.fg),
                                   Blend(Parse(cfg.bg), Color.White, 0.35f), fLabel);
        }

        void HideHostsTip()
        {
            if (hostsPopup != null) hostsPopup.HidePopup();
        }

        void RefreshHostsTip()
        {
            CheckHostsHover(PointToClient(Cursor.Position));
        }

        string BuildHostsTooltip()
        {
            var list = snap.hosts;
            if (list.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append(snap.hostsActive).Append(" / ").Append(snap.hostsTotal);
            sb.AppendLine();
            int shown = 0;
            foreach (var h in list)
            {
                if (!h.active && list.Count > 8) continue;
                if (shown >= 15) { sb.AppendLine("..."); break; }
                sb.Append(h.active ? "● " : "○ ").Append(string.IsNullOrEmpty(h.name) ? "-" : h.name);
                if (!string.IsNullOrEmpty(h.ip)) sb.Append("  ").Append(h.ip);
                sb.AppendLine();
                var meta = new StringBuilder("   ");
                if (!string.IsNullOrEmpty(h.mac)) meta.Append(h.mac);
                if (!string.IsNullOrEmpty(h.brand)) meta.Append("  ").Append(h.brand);
                if (!string.IsNullOrEmpty(h.band)) meta.Append("  ").Append(h.band);
                sb.AppendLine(meta.ToString());
                shown++;
            }
            return sb.ToString().TrimEnd();
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
                    case "model":
                    {
                        // 随身 WiFi 机身：圆角矩形 + 顶部两根短天线 + 底部小指示灯
                        var body = new Rectangle((int)(cx - 8), (int)(cy - 3), 16, 9);
                        using (var path = Round(body, 2))
                            g.DrawPath(pen, path);
                        g.DrawLine(pen, cx - 5, cy - 3, cx - 5, cy - 7);
                        g.DrawLine(pen, cx + 5, cy - 3, cx + 5, cy - 7);
                        g.FillEllipse(bFill, cx - 1.2f, cy + 0.4f, 2.4f, 2.4f);
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
                    case "ip6":
                    {
                        // 六边形（v6 提示）+ 中心小点
                        float rr = 7f;
                        var pts = new PointF[6];
                        for (int i = 0; i < 6; i++)
                        {
                            double a = Math.PI / 3.0 * i - Math.PI / 2.0;
                            pts[i] = new PointF(cx + rr * (float)Math.Cos(a), cy + rr * (float)Math.Sin(a));
                        }
                        g.DrawPolygon(pen, pts);
                        g.FillEllipse(bFill, cx - 1.2f, cy - 1.2f, 2.4f, 2.4f);
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
            if (e.Button == MouseButtons.Left && !eyeRect.IsEmpty && eyeRect.Contains(e.Location))
            {
                msisdnRevealed = !msisdnRevealed;
                // rows[].value 只在 BuildRows 时写入，切换后需要立即重写这一行，
                // 否则要等下一次 PollLoop 采样回来才会看到脱敏/明文变化。
                string raw = snap != null ? snap.msisdn : null;
                if (!string.IsNullOrEmpty(raw) && raw != "-")
                    foreach (var r in rows)
                        if (r.key == "msisdn") { r.value = msisdnRevealed ? raw : MaskPhone(raw); break; }
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Left && !cfg.locked)
            {
                dragging = true;
                dragOff = e.Location;
            }
            smsDownPt = e.Location;
            smsDownRow = (e.Button == MouseButtons.Left && !Mini) ? RowKeyAt(e.Y) : "";   // 迷你样式无行概念，rowTops 是旧卡片的残留，不能参与命中
            smsMoved = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging && !cfg.locked && (e.Button & MouseButtons.Left) != 0)
            {
                Location = new Point(Location.X + e.X - dragOff.X, Location.Y + e.Y - dragOff.Y);
                if (!smsMoved && (Math.Abs(e.X - smsDownPt.X) > 3 || Math.Abs(e.Y - smsDownPt.Y) > 3)) smsMoved = true;
                HideHostsTip();
            }
            else
                CheckHostsHover(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            HideHostsTip();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; SavePos(); }
            if (!smsMoved && e.Button == MouseButtons.Left && smsDownRow == "sms") ShowSms();
        }

        /// <summary>按 Y 坐标命中行（含 Plan 行额外高度），返回行 key，未命中返回空。</summary>
        string RowKeyAt(int y)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                int h = rowH + RowGap + (rows[i].kind == Kind.Plan ? 12 : 0);
                if (y >= rowTops[i] && y < rowTops[i] + h) return rows[i].key;
            }
            return "";
        }

        void SavePos()
        {
            cfg.posX = Location.X; cfg.posY = Location.Y;
            cfg.Save();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            // 双击终端行 → 弹出全部终端列表；其余位置 → 打开设备管理页。迷你样式不做行命中。
            if (!Mini && RowKeyAt(e.Y) == "hosts") { HideHostsTip(); ShowHosts(); }
            else Do("manage");
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
                case "sms": ShowSms(); break;
                case "hosts": ShowHosts(); break;
                case "refresh":
                    // ForgetSession 很轻量，但 devLock 会被 stuck 的 Sample 长持；抢不到锁就跳过，
                    // 下一次采样会因会话死自动重登。
                    try { if (Monitor.TryEnter(devLock, 100)) { try { dev.ForgetSession(); } finally { Monitor.Exit(devLock); } } } catch { }
                    wake.Set();
                    break;
                case "manage":
                    try
                    {
                        // baseUrl 在 NewDevice 后不再变更，直读 dev 引用即可（引用读为原子操作）。
                        var d = dev;
                        string url = d != null ? d.baseUrl : null;
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
                case "click":
                    cfg.clickThrough = !cfg.clickThrough;
                    cfg.Save();
                    ApplyClickThrough();
                    hint = cfg.clickThrough ? L.T("hint.clickon") : L.T("hint.clickoff");
                    hintTimer.Stop(); hintTimer.Start();
                    RebuildMenu();
                    Invalidate();
                    break;
                case "style":
                    // 左上角为公共锚点；Relayout 内 KeepOnScreen 负责变尺寸后拉回工作区
                    cfg.style = cfg.style == "mini" ? "full" : "mini";
                    cfg.Save();
                    Relayout();
                    RebuildMenu();
                    break;
                case "quit": Quit(); break;
            }
        }

        void ShowSettings()
        {
            // 防止重入：ShowDialog 开时内部仍会派发 tray 事件，不守卫会弹 N 个设置窗。
            // 已存在时直接激活（处理“双击无反应”的错觉，因为可能已开但被置顶窗口遮位）。
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                if (settingsForm.WindowState == FormWindowState.Minimized)
                    settingsForm.WindowState = FormWindowState.Normal;
                settingsForm.Activate();
                settingsForm.BringToFront();
                return;
            }
            using (var f = new SettingsForm(cfg))
            {
                settingsForm = f;
                int hr;
                try
                {
                    // 不传 owner：WidgetForm 在 toggle 隐藏后作为 owner 会造成子窗焦点错乱，
                    // 表现为“设置窗不可点击”。无 owner 的 ShowDialog 仍为应用内部模态。
                    hr = (int)f.ShowDialog();
                }
                finally { settingsForm = null; }
                if ((DialogResult)hr == DialogResult.OK)
                {
                    cfg = f.Result;
                    L.Lang = cfg.language;
                    // 设备不可达时 devLock 可能被 stuck 的 Sample 长持，这里不能直接 lock，
                    // 否则设置面板确定后 UI 会假死数十秒。抢不到锁时把 Logout+换 dev 排入后台。
                    bool taken = false;
                    try { taken = Monitor.TryEnter(devLock, 300); } catch { }
                    if (taken)
                    {
                        try { dev.Logout(); dev = NewDevice(); }
                        finally { Monitor.Exit(devLock); }
                    }
                    else
                    {
                        var old = dev;
                        var nd = NewDevice();
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            lock (devLock) { try { old.Logout(); } catch { } dev = nd; }
                            wake.Set();
                        });
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
            // 设备不可达时 dev.Sample() 会串行发十几个 8s 超时的 HTTP，整段持 devLock 数十秒；
            // 若 UI 线程在此处 lock(devLock){ dev.Logout(); } 会跟着假死。
            // 拿不到锁就跳过登出（poll 线程是后台线程，进程结束时会被强杀），
            // 保证托盘退出菜单一点就退出。
            bool taken = false;
            try { taken = Monitor.TryEnter(devLock, 200); } catch { }
            if (taken)
            {
                try { if (dev != null) dev.Logout(); } catch { }
                finally { Monitor.Exit(devLock); }
            }
            SavePos();
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (hostsPopup != null) { hostsPopup.Close(); hostsPopup = null; }
            if (logoImg != null) { logoImg.Dispose(); logoImg = null; }
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
            Add(m, L.T("menu.sms"), "sms");
            Add(m, L.T("menu.hosts"), "hosts");
            m.Items.Add(new ToolStripSeparator());
            var t = Add(m, (cfg.topmost ? L.T("menu.topoff") : L.T("menu.topon")), "top"); t.Tag = "top";
            var l = Add(m, (cfg.locked ? L.T("menu.unlock") : L.T("menu.lock")), "lock"); l.Tag = "lock";
            var ct = Add(m, (cfg.clickThrough ? L.T("menu.clickoff") : L.T("menu.clickon")), "click"); ct.Tag = "click";
            var st = Add(m, (cfg.style == "mini" ? L.T("menu.stylefull") : L.T("menu.style")), "style"); st.Tag = "style";
            m.Items.Add(new ToolStripSeparator());
            Add(m, L.T("menu.quit"), "quit");
            // 底部版本页脚：灰色不可点，版本取自 Application.ProductVersion(=InformationalVersion)
            m.Items.Add(new ToolStripSeparator());
            var ver = new ToolStripMenuItem(L.T("menu.version") + "  " + Application.ProductVersion);
            ver.AutoSize = false; ver.Height = 26; ver.Width = 176;
            ver.TextAlign = ContentAlignment.MiddleLeft;
            ver.Padding = new Padding(6, 4, 6, 4);
            ver.Enabled = false;
            m.Items.Add(ver);
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
            if (e.Button != MouseButtons.Left) return;
            // WinForms NotifyIcon 双击时事件顺序为：
            //   MouseClick(1st up) → MouseDoubleClick(2nd down) → MouseClick(2nd up)
            // 处理完双击后紧跟的这一次 MouseClick 不能重启 toggle 定时器，否则
            // 550ms 后会额外触发一次 toggle，表现为“双击时悬浮窗会隐藏/显示”。
            long now = DateTime.UtcNow.Ticks;
            if (now - lastTrayDoubleClickTicks <= TimeSpan.TicksPerMillisecond * 500) return;
            trayClickTimer.Stop();
            trayClickTimer.Start();
        }

        void OnTrayDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lastTrayDoubleClickTicks = DateTime.UtcNow.Ticks;
                trayClickTimer.Stop();      // 取消延迟中的单击 toggle
                ShowSettings();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            instance = this;
            ContextMenuStrip = menu;
            tray.ContextMenuStrip = menu;
            // WinForms 在首次显示时会按自身 CreateParams 重写扩展样式（不含 WS_EX_TRANSPARENT），
            // 把 OnHandleCreated 里加的穿透位抹掉；延迟到消息循环空档再补应用一次，确保启动后持久生效。
            BeginInvoke((MethodInvoker)delegate { ApplyClickThrough(); ApplyDwmFrame(); });
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
            e.TextColor = !e.Item.Enabled ? Color.FromArgb(139, 148, 158)
                            : e.Item.Selected ? Color.FromArgb(230, 237, 243)
                            : Color.FromArgb(201, 209, 217);
            base.OnRenderItemText(e);
        }
    }

    /// <summary>终端详情悬浮弹窗，无边框置顶小窗，自动跟随鼠标位置显示。
    /// 用独立 Form 实现而不走 ToolTip，保证 .NET 4.0 csc 编译下可编译可控样式。</summary>
    class HostsPopup : Form
    {
        string content = "";
        Color bg, fg, border;
        Font font;

        public HostsPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        }

        public void ShowContent(string text, Point screen, Color bg, Color fg, Color border, Font font)
        {
            if (text == content && Visible) return;
            this.content = text;
            this.bg = Color.White;
            this.fg = Color.FromArgb(30, 30, 30);
            this.border = Color.FromArgb(180, 180, 180);
            this.font = font;
            Size sz = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            int w = sz.Width + 24, h = sz.Height + 20;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x = Math.Min(screen.X, wa.Right - w - 4);
            int y = Math.Min(screen.Y, wa.Bottom - h - 4);
            SetBounds(x, y, w, h);
            if (!Visible) Show();
            else Invalidate();
        }

        public void HidePopup()
        {
            if (Visible) Hide();
            content = "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(Color.White))
                g.FillRectangle(b, 1, 1, Width - 3, Height - 3);
            using (var p = new Pen(Color.FromArgb(180, 180, 180)))
                g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
            TextRenderer.DrawText(g, content, font, new Rectangle(12, 10, Width - 24, Height - 20),
                Color.FromArgb(30, 30, 30),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
    }
}
