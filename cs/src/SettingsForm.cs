using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HwWidget
{
    /// <summary>设置窗口：经典 Win32 浅色风格。
    /// 奶油色自绘标题栏 + 灰底顶层 tab（选中白凸加粗）+ 白内容区 + 嵌套子 tab（带边框面板）
    /// + 方角 Win10 蓝复选框 + 扁平白按钮/下拉 + GroupBox 割入标题 + 底栏 (?)/恢复默认/取消/确定。
    /// 切换语言时先把当前控件值同步进 cfg，再用新语言重建整个界面。</summary>
    public class SettingsForm : Form, IMessageFilter
    {
        readonly Cfg cfg;
        public Cfg Result { get { return cfg; } }

        WinTitleBar titleBar;
        WinTabControl tabs;
        WinTabControl lookSub;
        WinTextBox hostBox, userBox, pwdBox;
        WinNumeric ivlBox, sizeBox;
        WinTrackBar alphaBar;
        Label alphaVal;
        WinComboBox langBox, fontBox;
        string stylePick;                                  // 样式页当前选中（full/mini），SyncToCfg 才写回 cfg
        StyleCard cardFull, cardMini;
        WinCheckBox savePwdBox, topBox, lockBox, borderBox, titleBox, autoStartBox, ctBorderBox;
        readonly Dictionary<string, WinCheckBox> rowBoxes = new Dictionary<string, WinCheckBox>();
        readonly Dictionary<string, ColorBox> colorBtns = new Dictionary<string, ColorBox>();
        static readonly string[] ColorKeys = { "fg", "bg", "accent", "warn", "bad" };

        WinButton resetBtn, cancelBtn, okBtn;
        WinHelpButton helpBtn;

        const int WinW = 520;             // 窗宽
        const int WinH = 440;             // 窗高
        const int TitleH = 32;            // 自绘标题栏高
        const int FooterH = 46;           // 底栏高
        const int TopHeadH = 28;          // 顶层 tab 条高
        const int Gutter = 14;            // 左边距
        const int RowH = 30;              // 表单行高
        const int FirstY = 24;            // GroupBox 内首行 Y
        const int CtrlH = 24;             // 统一控件高度
        int GbW { get { return tabs.Width - 2 - Gutter * 2; } }

        public SettingsForm(Cfg current)
        {
            cfg = current.CloneLike();

            SuspendLayout();
            Text = L.T("win.title");
            FormBorderStyle = FormBorderStyle.None;      // 自绘标题栏 + 1px 窗边框
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = TrayIcon.MakeFor(32); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ForeColor = Color.FromArgb(32, 32, 32);   // 标签非纯黑，观感柔和
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            TopMost = true;
            ClientSize = new Size(WinW, WinH);

            titleBar = new WinTitleBar { Left = 0, Top = 0, Width = WinW, Height = TitleH, Title = L.T("win.title") };
            try { titleBar.IconImage = TrayIcon.MakeFor(16).ToBitmap(); } catch { }
            titleBar.CloseClicked += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(titleBar);

            tabs = new WinTabControl { Left = 0, Top = TitleH, Width = WinW, Height = WinH - TitleH - FooterH };
            Controls.Add(tabs);
            Application.AddMessageFilter(this);

            BuildUi();
            BuildFooter();
            ResumeLayout(false);
        }

        // ================================================================ 界面构建
        void BuildUi()
        {
            tabs.ResetPages();
            lookSub = null;
            rowBoxes.Clear();
            colorBtns.Clear();
            stylePick = cfg.style;      // 语言切换/恢复默认后重建时以同步后的 cfg 为准

            var tConn = tabs.AddPage(L.T("tab.conn"));
            var tStyle = tabs.AddPage(L.T("tab.style"));
            var tLook = tabs.AddPage(L.T("tab.look"));
            var tRows = tabs.AddPage(L.T("tab.rows"));
            BuildConnPage(tConn);
            BuildStylePage(tStyle);
            BuildLookPage(tLook);
            BuildRowsPage(tRows);
            tabs.SelectedIndex = 0;
        }

        /// <summary>样式页：两张可点缩略图卡片，按用户当前配色真实预览完整卡片 / 迷你监控条效果。</summary>
        void BuildStylePage(Panel p)
        {
            var tip = new Label { Text = L.T("tip.stylepick"), AutoSize = true, ForeColor = Color.Gray, Left = Gutter, Top = 12 };
            p.Controls.Add(tip);

            Color bgc = SafeHtml(cfg.bg), fgc = SafeHtml(cfg.fg), acc = SafeHtml(cfg.accent), wrn = SafeHtml(cfg.warn);
            const int cw = 226, ch = 212, top = 40;
            cardFull = new StyleCard { Left = Gutter, Top = top, Width = cw, Height = ch, Mini = false,
                Title = L.T("style.full"), Desc = L.T("style.full.desc"),
                PBg = bgc, PFg = fgc, PAccent = acc, PWarn = wrn,
                Picked = delegate { stylePick = "full"; RefreshStyleCards(); } };
            cardMini = new StyleCard { Left = Gutter + cw + 16, Top = top, Width = cw, Height = ch, Mini = true,
                Title = L.T("style.mini"), Desc = L.T("style.mini.desc"),
                PBg = bgc, PFg = fgc, PAccent = acc, PWarn = wrn,
                Picked = delegate { stylePick = "mini"; RefreshStyleCards(); } };
            p.Controls.Add(cardFull); p.Controls.Add(cardMini);
            RefreshStyleCards();
        }

        void RefreshStyleCards()
        {
            if (cardFull == null || cardFull.IsDisposed) return;
            cardFull.Selected = stylePick != "mini";
            cardMini.Selected = stylePick == "mini";
            cardFull.Invalidate(); cardMini.Invalidate();
        }

        static Color SafeHtml(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); } catch { return Color.FromArgb(13, 17, 23); }
        }

        /// <summary>连接页（常规布局）：语言 + 开机自启 + 设备连接组 + 安全组。</summary>
        void BuildConnPage(Panel p)
        {
            int y = 14;
            langBox = new WinComboBox { Width = 160, Height = CtrlH };
            string[] langNames = new string[L.Options.Length];
            for (int li = 0; li < L.Options.Length; li++) langNames[li] = L.Options[li][0];
            langBox.Items.AddRange(langNames);
            langBox.SelectedIndex = L.IndexOf(cfg.language);
            langBox.SelectedIndexChanged += OnLanguageChanged;
            PlaceRow(p, y, L.T("lbl.language"), langBox);
            y += RowH + 2;

            autoStartBox = MakeCheck(L.T("chk.autostart"), IsAutoStartEnabled());
            autoStartBox.Left = Gutter; autoStartBox.Top = y;
            p.Controls.Add(autoStartBox);
            y += 28;

            var gb = new WinGroupBox { Caption = L.T("grp.device"), Left = Gutter, Top = y, Width = GbW };
            int gy = FirstY;
            hostBox = MakeTextBox(cfg.host, 200, false);
            PlaceRow(gb, gy, L.T("lbl.host"), hostBox); gy += RowH;
            userBox = MakeTextBox(cfg.user, 200, false);
            PlaceRow(gb, gy, L.T("lbl.user"), userBox); gy += RowH;
            pwdBox = MakeTextBox(cfg.password, 200, true);
            PlaceRow(gb, gy, L.T("lbl.pwd"), pwdBox); gy += RowH;
            ivlBox = new WinNumeric { Minimum = 2, Maximum = 120, Value = (decimal)Math.Max(2, Math.Min(120, cfg.interval)), Width = 74, Height = CtrlH };
            PlaceRow(gb, gy, L.T("lbl.interval"), ivlBox); gy += RowH + 6;
            gb.Height = gy;
            p.Controls.Add(gb);

            var gbSec = new WinGroupBox { Caption = L.T("grp.security"), Left = Gutter, Top = gb.Bottom + 12, Width = GbW, Height = FirstY + 26 + 8 };
            savePwdBox = MakeCheck(L.T("chk.savepwd"), cfg.savePassword);
            savePwdBox.Left = 16; savePwdBox.Top = FirstY;
            gbSec.Controls.Add(savePwdBox);
            p.Controls.Add(gbSec);
        }

        /// <summary>外观页：嵌套子 tab（常规 / 颜色 / 行为），每个子页为带边框面板 + 面板内右下恢复默认。</summary>
        void BuildLookPage(Panel p)
        {
            int bodyH = tabs.Height - TopHeadH - 2;
            lookSub = new WinTabControl { Sub = true, Left = 12, Top = 8, Width = p.Width - 24, Height = bodyH - 16 };
            p.Controls.Add(lookSub);
            int subViewH = lookSub.Height - 24 - 2;

            var g1 = lookSub.AddPage(L.T("sub.general"));
            int y = 14;
            sizeBox = new WinNumeric { Minimum = 9, Maximum = 30, Value = ClampSize(cfg.fontSize), Width = 74, Height = CtrlH };
            PlaceRow(g1, y, L.T("lbl.fontsize"), sizeBox); y += RowH;
            string[] fams = new string[FontFamily.Families.Length];
            int fi = 0;
            foreach (FontFamily ff in FontFamily.Families) fams[fi++] = ff.Name;
            fontBox = new WinComboBox { Width = 240, Height = CtrlH };
            fontBox.Items.AddRange(fams);
            int fsi = Array.IndexOf(fams, cfg.fontFam);
            fontBox.SelectedIndex = fsi >= 0 ? fsi : 0;
            PlaceRow(g1, y, L.T("lbl.font"), fontBox); y += RowH;
            alphaBar = new WinTrackBar { Minimum = 25, Maximum = 100, Value = (int)Math.Round(cfg.alpha * 100), Width = 180, Height = CtrlH + 4 };
            alphaVal = new Label { Text = alphaBar.Value + "%", AutoSize = true, ForeColor = Color.Gray };
            alphaBar.ValueChanged += delegate { alphaVal.Text = alphaBar.Value + "%"; };
            PlaceRow(g1, y, L.T("lbl.opacity"), alphaBar, alphaVal);
            AddInnerReset(g1, subViewH, 0);

            var g2 = lookSub.AddPage(L.T("grp.color"));
            y = 14;
            foreach (var key in ColorKeys)
            {
                var b = new ColorBox { Width = 44, Height = 22, Value = ColorTranslator.FromHtml(HexOf(cfg, key)) };
                string kk = key;
                b.Click += delegate { PickColor(kk, b); };
                PlaceRow(g2, y, L.T("color." + key), b);
                colorBtns[key] = b;
                y += RowH;
            }
            AddInnerReset(g2, subViewH, 1);

            var g3 = lookSub.AddPage(L.T("grp.behavior"));
            y = 14;
            topBox = MakeCheck(L.T("chk.top"), cfg.topmost); topBox.Left = 16; topBox.Top = y; y += 26;
            borderBox = MakeCheck(L.T("chk.border"), cfg.borderless); borderBox.Left = 16; borderBox.Top = y; y += 26;
            lockBox = MakeCheck(L.T("chk.lock"), cfg.locked); lockBox.Left = 16; lockBox.Top = y; y += 26;
            titleBox = MakeCheck(L.T("chk.title"), cfg.showTitle); titleBox.Left = 16; titleBox.Top = y; y += 26;
            ctBorderBox = MakeCheck(L.T("chk.ctborder"), cfg.clickThroughBorder); ctBorderBox.Left = 16; ctBorderBox.Top = y;
            g3.Controls.Add(topBox); g3.Controls.Add(borderBox); g3.Controls.Add(lockBox); g3.Controls.Add(titleBox); g3.Controls.Add(ctBorderBox);
            AddInnerReset(g3, subViewH, 2);

            lookSub.SelectedIndex = 0;
        }

        /// <summary>子页面板内右下角的“恢复默认”（仅重置该子页对应的设置）。</summary>
        void AddInnerReset(Panel page, int viewH, int which)
        {
            var b = new WinButton { Text = L.T("btn.reset"), Height = 26 };
            b.Width = Math.Max(82, MeasureTextW(b.Text, Font) + 26);
            b.Left = page.Width - b.Width - 12;
            b.Top = viewH - b.Height - 10;
            int w = which;
            b.Click += delegate { SyncToCfg(); ResetLook(w); BuildUi(); LayoutFooterButtons(); };
            page.Controls.Add(b);
        }

        void BuildRowsPage(Panel p)
        {
            var gb = new WinGroupBox { Caption = L.T("tab.rows"), Left = Gutter, Top = 10, Width = GbW };
            int colW = (gb.Width - 24) / 2;
            int rows = (Cfg.RowKeys.Length + 1) / 2;
            gb.Height = FirstY + rows * 26 + 6;
            for (int i = 0; i < Cfg.RowKeys.Length; i++)
            {
                var k = Cfg.RowKeys[i];
                bool on;
                cfg.rows.TryGetValue(k, out on);
                var c = MakeCheck(L.T("row." + k), on);
                c.Left = 14 + (i % 2) * colW;
                c.Top = FirstY + (i / 2) * 26;
                gb.Controls.Add(c);
                rowBoxes[k] = c;
            }
            p.Controls.Add(gb);
        }

        void BuildFooter()
        {
            helpBtn = new WinHelpButton { Left = 12, Top = WinH - FooterH + 12, Width = 24, Height = 24, TabIndex = 90 };
            helpBtn.Click += delegate
            {
                MessageBox.Show(this, L.T("tip.now"), L.T("win.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            Controls.Add(helpBtn);

            resetBtn = new WinButton { Text = L.T("btn.reset"), Height = 26, TabIndex = 91 };
            resetBtn.Click += delegate { SyncToCfg(); ResetDefaults(); BuildUi(); LayoutFooterButtons(); };
            Controls.Add(resetBtn);

            cancelBtn = new WinButton { Text = L.T("btn.cancel"), Height = 26, TabIndex = 92 };
            cancelBtn.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancelBtn);

            okBtn = new WinButton { Text = L.T("btn.save"), Height = 26, TabIndex = 93, Primary = true };
            okBtn.Click += delegate { SaveAndClose(); };
            Controls.Add(okBtn);

            LayoutFooterButtons();
            AcceptButton = okBtn;
            CancelButton = cancelBtn;
        }

        /// <summary>底栏按钮贴右缘从右往左排布；语言切换后重新测量宽度。</summary>
        void LayoutFooterButtons()
        {
            const int gap = 8;
            int right = WinW - 12;
            int top = WinH - FooterH + 11;
            okBtn.Text = L.T("btn.save");
            okBtn.Width = Math.Max(80, MeasureTextW(okBtn.Text, Font) + 26);
            okBtn.Left = right - okBtn.Width; okBtn.Top = top;

            cancelBtn.Text = L.T("btn.cancel");
            cancelBtn.Width = Math.Max(76, MeasureTextW(cancelBtn.Text, Font) + 26);
            cancelBtn.Left = okBtn.Left - gap - cancelBtn.Width; cancelBtn.Top = top;

            resetBtn.Text = L.T("btn.reset");
            resetBtn.Width = Math.Max(82, MeasureTextW(resetBtn.Text, Font) + 26);
            resetBtn.Left = cancelBtn.Left - gap - resetBtn.Width; resetBtn.Top = top;
        }

        // ================================================================ 语言实时切换
        void OnLanguageChanged(object sender, EventArgs e)
        {
            string nl = L.Options[langBox.SelectedIndex][1];
            if (nl == cfg.language) return;
            BeginInvoke(new Action(delegate
            {
                SyncToCfg();
                cfg.language = nl;
                L.Lang = nl;
                Text = L.T("win.title");
                titleBar.Title = L.T("win.title");
                BuildUi();
                LayoutFooterButtons();
            }));
        }

        // ================================================================ 布局辅助
        WinTextBox MakeTextBox(string text, int width, bool pwd)
        {
            var t = new WinTextBox { Width = width };
            t.TextValue = text;
            t.UseSystemPasswordChar = pwd;
            return t;
        }

        WinCheckBox MakeCheck(string text, bool chk)
        {
            var c = new WinCheckBox { Text = text, Checked = chk, AutoSize = false };
            c.Width = MeasureTextW(text, Font) + 28;
            c.Height = Math.Max(20, Font.Height + 4);
            return c;
        }

        static int MeasureTextW(string text, Font f)
        {
            return TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.SingleLine).Width;
        }

        /// <summary>一行：Label 左对齐 Gutter，控件紧跟标签右侧（按实测标签宽定位，避免多语言遮挡），可选尾部提示。</summary>
        int PlaceRow(Control parent, int y, string labelText, Control ctl, Control trailing = null)
        {
            var lbl = new Label { Text = labelText, Left = Gutter, Top = y + (ctl.Height - lblHeight) / 2, AutoSize = true };
            parent.Controls.Add(lbl);
            int lblW = TextRenderer.MeasureText(labelText, Font).Width;
            int ctlX = Gutter + lblW + 10;
            ctl.Left = ctlX;
            ctl.Top = y;
            parent.Controls.Add(ctl);
            if (trailing != null)
            {
                trailing.Left = ctl.Right + 8;
                trailing.Top = y + (ctl.Height - trailing.Height) / 2 + 2;
                parent.Controls.Add(trailing);
            }
            return ctlX;
        }

        static readonly int lblHeight = 16;

        decimal ClampSize(int v) { return v < 9 ? 9 : v > 30 ? 30 : v; }

        static string HexOf(Cfg c, string key)
        {
            switch (key)
            {
                case "fg": return c.fg;
                case "bg": return c.bg;
                case "accent": return c.accent;
                case "warn": return c.warn;
                default: return c.bad;
            }
        }

        static void SetColor(Cfg c, string key, string hex)
        {
            switch (key)
            {
                case "fg": c.fg = hex; break;
                case "bg": c.bg = hex; break;
                case "accent": c.accent = hex; break;
                case "warn": c.warn = hex; break;
                case "bad": c.bad = hex; break;
            }
        }

        void PickColor(string key, ColorBox b)
        {
            using (var dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = b.Value;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    b.Value = dlg.Color;
                    string hex = "#" + dlg.Color.R.ToString("x2", CultureInfo.InvariantCulture)
                                      + dlg.Color.G.ToString("x2", CultureInfo.InvariantCulture)
                                      + dlg.Color.B.ToString("x2", CultureInfo.InvariantCulture);
                    SetColor(cfg, key, hex);
                }
            }
        }

        // ================================================================ 值同步
        void SyncToCfg()
        {
            cfg.host = string.IsNullOrEmpty(hostBox.TextValue.Trim()) ? "192.168.8.1" : hostBox.TextValue.Trim();
            cfg.user = string.IsNullOrEmpty(userBox.TextValue.Trim()) ? "admin" : userBox.TextValue.Trim();
            cfg.password = pwdBox.TextValue;
            cfg.interval = Math.Max(2.0, (double)ivlBox.Value);
            cfg.alpha = alphaBar.Value / 100.0;
            cfg.fontSize = (int)sizeBox.Value;
            cfg.style = stylePick;
            var fam = fontBox.SelectedItem;
            cfg.fontFam = string.IsNullOrEmpty(fam) ? "Microsoft YaHei UI" : fam;
            cfg.savePassword = savePwdBox.Checked;
            cfg.topmost = topBox.Checked;
            cfg.locked = lockBox.Checked;
            cfg.borderless = borderBox.Checked;
            cfg.showTitle = titleBox.Checked;
            cfg.clickThroughBorder = ctBorderBox.Checked;
            cfg.autoStart = autoStartBox.Checked;
            foreach (var kv in rowBoxes) cfg.rows[kv.Key] = kv.Value.Checked;
            foreach (var key in ColorKeys)
                SetColor(cfg, key, "#" + colorBtns[key].Value.R.ToString("x2") + colorBtns[key].Value.G.ToString("x2") + colorBtns[key].Value.B.ToString("x2"));
        }

        void ResetDefaults()
        {
            var d = new Cfg();
            cfg.host = d.host; cfg.user = d.user; cfg.password = d.password;
            cfg.interval = d.interval; cfg.alpha = d.alpha;
            cfg.fontFam = d.fontFam; cfg.fontSize = d.fontSize;
            cfg.fg = d.fg; cfg.bg = d.bg; cfg.accent = d.accent; cfg.warn = d.warn; cfg.bad = d.bad;
            cfg.topmost = d.topmost; cfg.borderless = d.borderless; cfg.locked = d.locked;
            cfg.showTitle = d.showTitle; cfg.savePassword = d.savePassword;
            cfg.clickThroughBorder = d.clickThroughBorder; cfg.style = d.style;
            foreach (var kv in d.rows) cfg.rows[kv.Key] = kv.Value;
        }

        /// <summary>子页面板内“恢复默认”：0=常规(字号/字体/透明) 1=颜色 2=行为。</summary>
        void ResetLook(int which)
        {
            var d = new Cfg();
            if (which == 0) { cfg.fontSize = d.fontSize; cfg.fontFam = d.fontFam; cfg.alpha = d.alpha; }
            else if (which == 1) { cfg.fg = d.fg; cfg.bg = d.bg; cfg.accent = d.accent; cfg.warn = d.warn; cfg.bad = d.bad; }
            else { cfg.topmost = d.topmost; cfg.borderless = d.borderless; cfg.locked = d.locked; cfg.showTitle = d.showTitle; cfg.clickThroughBorder = d.clickThroughBorder; }
        }

        void SaveAndClose()
        {
            SyncToCfg();
            cfg.Save();
            ApplyAutoStart(cfg.autoStart);
            DialogResult = DialogResult.OK;
            Close();
        }

        // ------------------------------------------------------------ 开机自启（HKCU Run 注册表）
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "HwWidget";

        static bool IsAutoStartEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    var v = k.GetValue(RunValueName) as string;
                    if (string.IsNullOrEmpty(v)) return false;
                    return NormalizePath(v) == NormalizePath(Application.ExecutablePath);
                }
            }
            catch { return false; }
        }

        static string NormalizePath(string s)
        {
            s = s.Trim().Trim('"');
            try { s = Path.GetFullPath(s); } catch { }
            return s.TrimEnd('\\').ToLowerInvariant();
        }

        static void ApplyAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex) { Cfg.LogError(ex); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            base.OnFormClosed(e);
        }

        /// <summary>自绘底栏分隔线；窗外框/圆角/阴影交给 DWM（见 CreateParams/WndProc）。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using (var p = new Pen(Pal.WindowBorder))
                g.DrawLine(p, 0, WinH - FooterH, Width - 1, WinH - FooterH);   // 底栏分隔线
        }

        // ---------------------------------------------------------------- DWM 圆角 + 阴影 + 玻璃边
        // 保留 WS_THICKFRAME 让 DWM 视本窗为“带框架”窗口（从而自动圆角/投影/玻璃描边），
        // 再用 WM_NCCALCSIZE 把非客户区压成 0（客户区=整窗）以便自绘标题栏；
        // WM_NCHITTEST 恒返 HTCLIENT 禁掉边缘 resize。
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00040000;   // WS_THICKFRAME
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084;
            const int WM_NCPAINT = 0x0085, WM_NCACTIVATE = 0x0086;
            const int WM_NCUAHDRAWCAPTION = 0x00AE, WM_NCUAHDRAWFRAME = 0x00AF;
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)1; return; }   // HTCLIENT
            // 拦截 UAH/非客户区重画：否则失活时 DWM 会补画系统 caption + 一圈边框（“失焦圈”鬼影）
            if (m.Msg == WM_NCUAHDRAWCAPTION || m.Msg == WM_NCUAHDRAWFRAME) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == WM_NCACTIVATE) { m.Result = (IntPtr)1; return; }
            if (m.Msg == WM_NCPAINT) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                // 回归系统标准窗口观感（同 Snipaste 等常规 Win11 窗口）：DWM 平滑圆角 + 默认细描边 + 投影，
                // 不再自绘 Region 圆角（锯齿且无投影），也不再用非默认描边色（会在桌面前形成可见白/黑边）
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));   // DWMWA_WINDOW_CORNER_PREFERENCE
                int border = unchecked((int)0xFFFFFFFF);  // DWMWA_COLOR_DEFAULT：系统默认描边
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));   // DWMWA_BORDER_COLOR
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // 滚轮转发：优先滚动光标所在的嵌套子页，否则滚动顶层当前页
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020a;
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (!IsHandleCreated || !Visible) return false;
            Point client = PointToClient(Control.MousePosition);
            Panel page = null;
            if (lookSub != null && !lookSub.IsDisposed && lookSub.Visible)
            {
                Point sub = lookSub.PointToClient(Control.MousePosition);
                if (lookSub.ClientRectangle.Contains(sub)) page = lookSub.SelectedPage;
            }
            if (page == null)
            {
                if (!tabs.ClientRectangle.Contains(client)) return false;
                page = tabs.SelectedPage;
            }
            if (page == null) return false;
            int delta = unchecked((short)((long)m.WParam >> 16));
            if (delta == 0) return false;
            Point pos = page.AutoScrollPosition;
            int ny = pos.Y + (delta > 0 ? 96 : -96);
            page.AutoScrollPosition = new Point(pos.X, ny);
            return true;
        }
    }

    // ==================================================================== 自绘标题栏（奶油色 + 图标 + 标题 + X，可拖动）
    class WinTitleBar : Control
    {
        public Image IconImage;
        string title = "";
        public string Title { get { return title; } set { title = value; Invalidate(); } }
        public event EventHandler CloseClicked;
        bool hoverX;
        const int XW = 46;

        public WinTitleBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 32;
        }

        Rectangle XRect { get { return new Rectangle(Width - XW, 0, XW, Height); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hx = XRect.Contains(e.Location);
            if (hx != hoverX) { hoverX = hx; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverX = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (XRect.Contains(e.Location)) { if (CloseClicked != null) CloseClicked(this, EventArgs.Empty); }
            else
            {
                // 拖动窗体：释放捕获后发送 WM_NCLBUTTONDOWN(HTCAPTION)
                Native.ReleaseCapture();
                var f = FindForm();
                if (f != null) Native.SendMessage(f.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Pal.TitleBack)) g.FillRectangle(b, 0, 0, Width, Height);
            if (IconImage != null) g.DrawImage(IconImage, 10, (Height - 16) / 2, 16, 16);
            TextRenderer.DrawText(g, title, Font, new Rectangle(32, 0, Width - 32 - XW, Height), Pal.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            // 关闭按钮：hover 红底白 X，否则细灰 X
            if (hoverX)
                using (var b = new SolidBrush(Color.FromArgb(232, 17, 35))) g.FillRectangle(b, XRect);
            Color xc = hoverX ? Color.White : Color.FromArgb(90, 90, 90);
            int cx = Width - XW / 2, cy = Height / 2;
            using (var p = new Pen(xc, 1.2f))
            {
                g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
            }
        }
    }

    // ==================================================================== 底栏圆形 ? 帮助按钮
    class WinHelpButton : Control
    {
        public WinHelpButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }
        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double h = Anim.Get(this, "hover");
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, r);
            using (var p = new Pen(Draw.Lerp(Color.FromArgb(140, 140, 140), Pal.Accent, h))) g.DrawEllipse(p, r);
            TextRenderer.DrawText(g, "?", Font, r, Pal.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>色块按钮：白底 + 灰方边框 + 内部填色，hover 蓝描边。点击弹 ColorDialog。</summary>
    class ColorBox : Control
    {
        Color value = Color.White;
        public Color Value
        {
            get { return value; }
            set { this.value = value; Invalidate(); }
        }
        public ColorBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            double h = Anim.Get(this, "hover");
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(value)) g.FillRectangle(b, rect);
            using (var p = new Pen(Draw.Lerp(Color.FromArgb(122, 122, 122), Pal.Accent, h))) g.DrawRectangle(p, rect);
        }
    }

    // ==================================================================== 样式选择卡片：缩略图按用户当前配色预览两种悬浮窗效果，点选切换
    class StyleCard : Control
    {
        public string Title = "", Desc = "";
        public bool Mini;            // false=完整卡片缩略图，true=迷你监控条缩略图
        public bool Selected;
        public Color PBg = Color.FromArgb(13, 17, 23), PFg = Color.FromArgb(230, 237, 243),
                     PAccent = Color.FromArgb(63, 185, 80), PWarn = Color.FromArgb(210, 153, 34);
        public Action Picked;

        public StyleCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Picked != null) Picked(); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        // 回车/空格键支持键盘选中（TabStop 聚焦后可用）
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var body = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var path = Draw.Round(body, 8))
            using (var b = new SolidBrush(Selected ? Color.FromArgb(240, 246, 252) : Color.White))
                g.FillPath(b, path);
            double hover = Anim.Get(this, "hover");
            Color bc = Selected ? Pal.Accent : Draw.Lerp(Color.FromArgb(198, 200, 204), Pal.Accent, hover * 0.7);
            using (var path = Draw.Round(body, 8))
            using (var p = new Pen(bc, Selected ? 2f : 1f))
                g.DrawPath(p, path);

            // 缩略图区：浅灰铺底，内部居中画真实样式预览
            var thumb = new Rectangle(12, 12, Width - 24, 140);
            using (var b = new SolidBrush(Color.FromArgb(246, 247, 249))) g.FillRectangle(b, thumb);
            using (var p = new Pen(Color.FromArgb(228, 230, 233))) g.DrawRectangle(p, thumb);
            if (Mini) DrawMiniThumb(g, thumb); else DrawFullThumb(g, thumb);

            // 底部标题 + 简述
            TextRenderer.DrawText(g, Title, Font, new Rectangle(14, 158, Width - 28, 18),
                Selected ? Pal.Accent : Color.FromArgb(40, 40, 40),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var fd = new Font(Font.FontFamily, 7.5f))
                TextRenderer.DrawText(g, Desc, fd, new Rectangle(14, 178, Width - 28, 16), Color.Gray,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (Selected)
            {
                var badge = new Rectangle(Width - 32, 6, 20, 20);
                using (var b = new SolidBrush(Pal.Accent)) g.FillEllipse(b, badge);
                using (var p = new Pen(Color.White, 1.8f))
                {
                    float cx = badge.X + badge.Width / 2f, cy = badge.Y + badge.Height / 2f;
                    g.DrawLines(p, new[] { new PointF(cx - 4, cy + 0.5f), new PointF(cx - 1.2f, cy + 3.2f), new PointF(cx + 4, cy - 2.6f) });
                }
            }
        }

        Color Mix(Color a, Color b, double t) { return Draw.Lerp(a, b, t); }

        /// <summary>完整卡片缩略图：标题行 + 分隔线 + 网络/信号/速率/套餐四行示意。</summary>
        void DrawFullThumb(Graphics g, Rectangle thumb)
        {
            int pw = 158, ph = 104;
            var r = new Rectangle(thumb.X + (thumb.Width - pw) / 2, thumb.Y + (thumb.Height - ph) / 2, pw, ph);
            using (var path = Draw.Round(r, 9))
            using (var br = new LinearGradientBrush(r, Mix(PBg, Color.White, 0.06), PBg, 90f))
                g.FillPath(br, path);
            using (var fd = new Font("Microsoft YaHei UI", 6.5f))
            {
                Color muted = Mix(Color.Gray, PFg, 0.38);
                // 标题行：logo 块 + 名称 + 右侧制式徽章
                using (var b = new SolidBrush(Mix(PBg, PAccent, 0.5)))
                    using (var p = Draw.Round(new Rectangle(r.X + 8, r.Y + 7, 10, 10), 3)) g.FillPath(b, p);
                TextRenderer.DrawText(g, "HUAWEI WiFi", fd, new Point(r.X + 22, r.Y + 6), muted, TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, "4G+", fd, new Point(r.Right - 26, r.Y + 6), PAccent, TextFormatFlags.NoPrefix);
                using (var pen = new Pen(Mix(PBg, Color.White, 0.14)))
                    g.DrawLine(pen, r.X + 8, r.Y + 23, r.Right - 8, r.Y + 23);
                // 信息行：左标签右值（标签随界面语言，内容为固定示意数据）
                int ry = r.Y + 29;
                Row(g, fd, r, ry, L.T("lbl.network"), "4G LTE · China", PAccent); ry += 18;
                Row(g, fd, r, ry, L.T("lbl.signal"), null, PAccent);             // 值位画信号柱
                DrawBars(g, r.Right - 20, ry + 3, 4, 3, PAccent, Mix(PBg, Color.White, 0.18)); ry += 18;
                Row(g, fd, r, ry, L.T("lbl.rate"), "↓12.3M ↑4.5M", PFg); ry += 18;
                Row(g, fd, r, ry, L.T("lbl.plan"), "65%", PAccent);
                // 套餐进度条
                var track = new Rectangle(r.X + 8, ry + 12, pw - 16, 3);
                using (var b = new SolidBrush(Mix(PBg, Color.White, 0.13))) g.FillRectangle(b, track);
                using (var b = new SolidBrush(PAccent))
                    using (var p = Draw.Round(new Rectangle(track.X, track.Y, (int)(track.Width * 0.65), 3), 1)) g.FillPath(b, p);
            }
        }

        /// <summary>迷你监控条缩略图：固定尺寸圆角小窗，左侧信号柱 + 单行速度（↑橙 / ↓绿）。</summary>
        void DrawMiniThumb(Graphics g, Rectangle thumb)
        {
            int pw = 150, ph = 24;
            var r = new Rectangle(thumb.X + (thumb.Width - pw) / 2, thumb.Y + (thumb.Height - ph) / 2, pw, ph);
            using (var path = Draw.Round(r, 5))
            using (var br = new LinearGradientBrush(r, Mix(PBg, Color.White, 0.06), PBg, 90f))
                g.FillPath(br, path);
            using (var fd = new Font("Consolas", 6.5f))
            {
                int cy = r.Y + ph / 2;
                DrawBars(g, r.X + 7, cy - 7, 5, 3, PAccent, Mix(PBg, Color.White, 0.18));
                int x = r.X + 28;
                x = Glyph(g, x, cy, 'u', PWarn);
                TextRenderer.DrawText(g, "0.3 KB/s", fd, new Point(x, cy - 7), PWarn, TextFormatFlags.NoPrefix);
                x += TextRenderer.MeasureText(g, "0.3 KB/s", fd).Width + 6;
                x = Glyph(g, x, cy, 'd', PAccent);
                TextRenderer.DrawText(g, "33.1 KB/s", fd, new Point(x, cy - 7), PAccent, TextFormatFlags.NoPrefix);
            }
        }

        void Row(Graphics g, Font f, Rectangle r, int y, string label, string value, Color vc)
        {
            Color muted = Mix(Color.Gray, PFg, 0.38);
            TextRenderer.DrawText(g, label, f, new Point(r.X + 8, y + 1), muted, TextFormatFlags.NoPrefix);
            if (value != null)
                TextRenderer.DrawText(g, value, f, new Point(r.Right - 8 - TextRenderer.MeasureText(g, value, f).Width, y + 1), vc, TextFormatFlags.NoPrefix);
        }

        /// <summary>缩略图上行(u)/下行(d)箭头：实心三角头+矩形短杆（与迷你条真实绘制一致），返回下一个 x 坐标。</summary>
        static int Glyph(Graphics g, int x, int cy, char dir, Color c)
        {
            float cx = x + 3f;
            using (var b = new SolidBrush(c))
            {
                if (dir == 'd')
                {
                    g.FillRectangle(b, cx - 1f, cy - 5f, 2f, 6f);
                    g.FillPolygon(b, new[] { new PointF(cx, cy + 4.5f), new PointF(cx - 2.5f, cy + 0.5f), new PointF(cx + 2.5f, cy + 0.5f) });
                }
                else
                {
                    g.FillPolygon(b, new[] { new PointF(cx, cy - 4.5f), new PointF(cx - 2.5f, cy - 0.5f), new PointF(cx + 2.5f, cy - 0.5f) });
                    g.FillRectangle(b, cx - 1f, cy - 0.5f, 2f, 6f);
                }
            }
            return x + 9;
        }

        static void DrawBars(Graphics g, int x, int top, int n, int lit, Color c, Color dim)
        {
            for (int i = 0; i < n; i++)
                using (var b = new SolidBrush(i < lit ? c : dim))
                {
                    float bh = 2 + i * 2f;
                    g.FillRectangle(b, x + i * 4, top + 11 - bh, 3, bh);
                }
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    }

    static class CfgClone
    {
        /// <summary>浅拷贝一份配置给对话框编辑，取消时不污染原配置。</summary>
        public static Cfg CloneLike(this Cfg c)
        {
            var n = new Cfg();
            n.host = c.host; n.user = c.user; n.password = c.password;
            n.language = c.language;
            n.style = c.style;
            n.interval = c.interval; n.alpha = c.alpha;
            n.fontFam = c.fontFam; n.fontSize = c.fontSize;
            n.fg = c.fg; n.bg = c.bg; n.accent = c.accent; n.warn = c.warn; n.bad = c.bad;
            foreach (var kv in c.rows) n.rows[kv.Key] = kv.Value;
            n.topmost = c.topmost; n.borderless = c.borderless; n.locked = c.locked;
            n.clickThrough = c.clickThrough; n.clickThroughBorder = c.clickThroughBorder;
            n.showTitle = c.showTitle; n.savePassword = c.savePassword;
            n.posX = c.posX; n.posY = c.posY;
            return n;
        }
    }
}