using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HwWidget
{
    /// <summary>设置窗口：经典 Win32 自绘控件风格（带动画），支持语言实时切换。
    /// 切换语言时先把当前控件值同步进 cfg，再用新语言重建整个界面。</summary>
    public class SettingsForm : Form, IMessageFilter
    {
        readonly Cfg cfg;
        public Cfg Result { get { return cfg; } }

        WinTabControl tabs;
        WinTextBox hostBox, userBox, pwdBox;
        WinNumeric ivlBox, sizeBox;
        WinTrackBar alphaBar;
        Label alphaVal;
        WinComboBox langBox, fontBox;
        WinCheckBox savePwdBox, topBox, lockBox, borderBox, titleBox, autoStartBox;
        readonly Dictionary<string, WinCheckBox> rowBoxes = new Dictionary<string, WinCheckBox>();
        readonly Dictionary<string, ColorBox> colorBtns = new Dictionary<string, ColorBox>();
        readonly Dictionary<string, Label> i18nLabels = new Dictionary<string, Label>();
        static readonly string[] ColorKeys = { "fg", "bg", "accent", "warn", "bad" };

        WinButton resetBtn, cancelBtn, okBtn;
        Button helpBtn;

        const int PageW = 560;            // tab 内容宽度（加宽以容纳英文长标签，避免横向裁切）
        const int Gutter = 14;            // GroupBox 左边距
        const int RowH = 34;              // 表单行高（含间距）
        const int FirstY = 28;            // GroupBox 内首行 Y（标题下方留白）
        const int CtrlH = 24;             // 统一控件高度
        const int PageH = 486;            // tab 内容高度（加高以容纳外观页含开机自启行的全部内容，避免触发滚动）
        const int ScrollReserve = 22;     // 为垂直滚动条预留宽度，避免多余横向滚动条
        int GbW { get { return PageW - Gutter * 2 - ScrollReserve; } }   // GroupBox 统一宽度

        public SettingsForm(Cfg current)
        {
            cfg = current.CloneLike();

            SuspendLayout();
            Text = L.T("win.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = TrayIcon.MakeFor(32); ShowIcon = true; } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;   // 与自绘控件库一致：像素级 1:1 布局，避免 DPI 缩放导致控件宽度与绘制文字量度不一致而截断
            BackColor = SystemColors.Control;
            TopMost = true;

            tabs = new WinTabControl { Left = 10, Top = 8, Width = PageW, Height = PageH };
            Controls.Add(tabs);
            Application.AddMessageFilter(this);   // 拦截滚轮，转发到当前 tab 页，保证内容超高时可滚动

            BuildUi();
            BuildFooter();

            ClientSize = new Size(tabs.Right + 10, tabs.Bottom + 42);
            ResumeLayout(false);
        }

        // ================================================================ 界面构建
        void BuildUi()
        {
            tabs.ResetPages();          // 先清空 captions / pages，避免语言切换重建时叠加
            tabs.Controls.Clear();
            i18nLabels.Clear();
            rowBoxes.Clear();
            colorBtns.Clear();

            var tConn = tabs.AddPage(L.T("tab.conn"));
            var tLook = tabs.AddPage(L.T("tab.look"));
            var tRows = tabs.AddPage(L.T("tab.rows"));
            BuildConnPage(tConn);
            BuildLookPage(tLook);
            BuildRowsPage(tRows);
            tabs.SelectedIndex = 0;
        }

        void BuildConnPage(Panel p)
        {
            var gb = new WinGroupBox { Text = "", Caption = L.T("grp.device"), Left = Gutter, Top = 10, Width = GbW };
            int gy = FirstY;

            hostBox = MakeTextBox(cfg.host, 180, false);
            PlaceRow(gb, gy, L.T("lbl.host"), hostBox);
            gy += RowH;

            userBox = MakeTextBox(cfg.user, 180, false);
            PlaceRow(gb, gy, L.T("lbl.user"), userBox);
            gy += RowH;

            pwdBox = MakeTextBox(cfg.password, 180, true);
            PlaceRow(gb, gy, L.T("lbl.pwd"), pwdBox);
            gy += RowH;

            ivlBox = new WinNumeric { Minimum = 2, Maximum = 120, Value = (decimal)Math.Max(2, Math.Min(120, cfg.interval)), Width = 74, Height = CtrlH };
            PlaceRow(gb, gy, L.T("lbl.interval"), ivlBox);
            gy += RowH + 4;
            gb.Height = gy;
            p.Controls.Add(gb);

            var gbSec = new WinGroupBox { Caption = L.T("grp.security"), Left = Gutter, Top = gb.Bottom + 12, Width = GbW, Height = FirstY + 26 + 12 };
            savePwdBox = MakeCheck(L.T("chk.savepwd"), cfg.savePassword);
            savePwdBox.Left = 16; savePwdBox.Top = FirstY;
            gbSec.Controls.Add(savePwdBox);
            p.Controls.Add(gbSec);
        }

        void BuildLookPage(Panel p)
        {
            int y = 10;

            // 语言行（页面顶层，不在 GroupBox 内）
            langBox = new WinComboBox { Width = 160, Height = CtrlH };
            string[] langNames = new string[L.Options.Length];
            for (int li = 0; li < L.Options.Length; li++) langNames[li] = L.Options[li][0];
            langBox.Items.AddRange(langNames);
            langBox.SelectedIndex = L.IndexOf(cfg.language);
            langBox.SelectedIndexChanged += OnLanguageChanged;
            PlaceRow(p, y, L.T("lbl.language"), langBox);
            y += RowH + 6;

            // 字体组
            var gbFont = new WinGroupBox { Caption = L.T("grp.font"), Left = Gutter, Top = y, Width = GbW };
            int fy = FirstY;
            sizeBox = new WinNumeric { Minimum = 9, Maximum = 30, Value = ClampSize(cfg.fontSize), Width = 74, Height = CtrlH };
            PlaceRow(gbFont, fy, L.T("lbl.fontsize"), sizeBox);
            fy += RowH;

            alphaBar = new WinTrackBar { Minimum = 25, Maximum = 100, Value = (int)Math.Round(cfg.alpha * 100), Width = 180, Height = CtrlH + 4 };
            alphaVal = new Label { Text = alphaBar.Value + "%", AutoSize = true, ForeColor = Color.Gray };
            alphaBar.ValueChanged += delegate { alphaVal.Text = alphaBar.Value + "%"; };
            PlaceRow(gbFont, fy, L.T("lbl.opacity"), alphaBar, alphaVal);
            fy += RowH + 2;

            string[] fams = new string[FontFamily.Families.Length];
            int fi = 0;
            foreach (FontFamily ff in FontFamily.Families) fams[fi++] = ff.Name;
            fontBox = new WinComboBox { Width = 260, Height = CtrlH };
            fontBox.Items.AddRange(fams);
            int fsi = Array.IndexOf(fams, cfg.fontFam);
            fontBox.SelectedIndex = fsi >= 0 ? fsi : 0;
            PlaceRow(gbFont, fy, L.T("lbl.font"), fontBox);
            fy += RowH + 4;
            gbFont.Height = fy;
            p.Controls.Add(gbFont);
            y = gbFont.Bottom + 12;

            // 颜色组
            var gbColor = new WinGroupBox { Caption = L.T("grp.color"), Left = Gutter, Top = y, Width = GbW, Height = 22 + 20 + 34 + 12 };
            int innerW = gbColor.Width - 28;
            int cellW = innerW / ColorKeys.Length;
            for (int i = 0; i < ColorKeys.Length; i++)
            {
                var key = ColorKeys[i];
                int cellX = 14 + i * cellW;
                var nameLbl = new Label { Text = L.T("color." + key), Left = cellX, Top = 24, Width = cellW - 4, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
                // 用窗体实际字体（微软雅黑 9pt）测量，而非尚未继承父字体前的默认字体；超宽则逐级缩小字号避免裁切
                Font mfont = Font;
                float fs = Font.Size;
                while (TextRenderer.MeasureText(nameLbl.Text, mfont).Width > cellW - 4 && fs > 6.5f)
                {
                    fs -= 0.5f;
                    mfont = new Font(Font.FontFamily, fs);
                    nameLbl.Font = mfont;
                }
                gbColor.Controls.Add(nameLbl);
                var b = new ColorBox { Left = cellX + 2, Top = 50, Width = cellW - 10, Height = 22, Value = ColorTranslator.FromHtml(HexOf(cfg, key)) };
                string kk = key;
                b.Click += delegate { PickColor(kk, b); };
                gbColor.Controls.Add(b);
                colorBtns[key] = b;
            }
            p.Controls.Add(gbColor);
            y = gbColor.Bottom + 12;

            // 行为组
            var gbBeh = new WinGroupBox { Caption = L.T("grp.behavior"), Left = Gutter, Top = y, Width = GbW, Height = FirstY + 30 * 3 + 10 };
            int col2 = 16 + Math.Max(TextRenderer.MeasureText(L.T("chk.top"), Font).Width,
                                     TextRenderer.MeasureText(L.T("chk.lock"), Font).Width) + 34;
            topBox = MakeCheck(L.T("chk.top"), cfg.topmost);
            topBox.Left = 16; topBox.Top = FirstY;
            borderBox = MakeCheck(L.T("chk.border"), cfg.borderless);
            borderBox.Left = col2; borderBox.Top = FirstY;
            lockBox = MakeCheck(L.T("chk.lock"), cfg.locked);
            lockBox.Left = 16; lockBox.Top = FirstY + 30;
            titleBox = MakeCheck(L.T("chk.title"), cfg.showTitle);
            titleBox.Left = col2; titleBox.Top = FirstY + 30;
            autoStartBox = MakeCheck(L.T("chk.autostart"), IsAutoStartEnabled());
            autoStartBox.Left = 16; autoStartBox.Top = FirstY + 60;
            gbBeh.Controls.Add(topBox);
            gbBeh.Controls.Add(borderBox);
            gbBeh.Controls.Add(lockBox);
            gbBeh.Controls.Add(titleBox);
            gbBeh.Controls.Add(autoStartBox);
            p.Controls.Add(gbBeh);
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
            helpBtn = new Button { Text = "?", Left = tabs.Left + 2, Top = tabs.Bottom + 10, Width = 28, Height = 26, FlatStyle = FlatStyle.System, TabIndex = 90 };
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

        /// <summary>按各按钮实际文本测量宽度，从右往左依次排布；语言切换后需重调，避免日/俄等较长译文被截断。</summary>
        void LayoutFooterButtons()
        {
            const int gap = 8;
            int right = tabs.Right;
            int top = tabs.Bottom + 10;
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
            // 避免在 SelectedIndexChanged 回调里直接 dispose sender，延后一帧重建
            BeginInvoke(new Action(delegate
            {
                SyncToCfg();               // 先保住用户已编辑的值
                cfg.language = nl;
                L.Lang = nl;               // 全局语言立即生效（悬浮窗也同步）
                Text = L.T("win.title");
                BuildUi();                 // 重建界面（tab 头 / GroupBox / 所有标签重新测量布局）
                LayoutFooterButtons();     // 底部按钮随语言重新测量宽度与位置
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

        /// <summary>用窗体实际字体显式算宽，关闭 AutoSize，彻底避免继承前测量字体偏小导致的文字截断。</summary>
        WinCheckBox MakeCheck(string text, bool chk)
        {
            var c = new WinCheckBox { Text = text, Checked = chk, AutoSize = false };
            c.Width = MeasureTextW(text, Font) + 28;
            c.Height = Math.Max(20, Font.Height + 4);
            return c;
        }

        /// <summary>按 NoPrefix 量宽，与 WinCheckBox 绘制时的 TextFormatFlags.NoPrefix 一致，
        /// 否则 & 会被当作助记符导致测量偏窄、实际文字被省略号截断。</summary>
        static int MeasureTextW(string text, Font f)
        {
            return TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.SingleLine).Width;
        }

        /// <summary>一行：Label 左对齐 Gutter，控件紧跟标签右侧，可选尾部提示。返回控件 Left。</summary>
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
        /// <summary>把当前所有控件的值写回 cfg（重建前和保存前都要调用）。</summary>
        void SyncToCfg()
        {
            cfg.host = string.IsNullOrEmpty(hostBox.TextValue.Trim()) ? "192.168.8.1" : hostBox.TextValue.Trim();
            cfg.user = string.IsNullOrEmpty(userBox.TextValue.Trim()) ? "admin" : userBox.TextValue.Trim();
            cfg.password = pwdBox.TextValue;
            cfg.interval = Math.Max(2.0, (double)ivlBox.Value);
            cfg.alpha = alphaBar.Value / 100.0;
            cfg.fontSize = (int)sizeBox.Value;
            var fam = fontBox.SelectedItem;
            cfg.fontFam = string.IsNullOrEmpty(fam) ? "Microsoft YaHei UI" : fam;
            cfg.savePassword = savePwdBox.Checked;
            cfg.topmost = topBox.Checked;
            cfg.locked = lockBox.Checked;
            cfg.borderless = borderBox.Checked;
            cfg.showTitle = titleBox.Checked;
            cfg.autoStart = autoStartBox.Checked;
            foreach (var kv in rowBoxes) cfg.rows[kv.Key] = kv.Value.Checked;
            foreach (var key in ColorKeys)
                SetColor(cfg, key, "#" + colorBtns[key].Value.R.ToString("x2") + colorBtns[key].Value.G.ToString("x2") + colorBtns[key].Value.B.ToString("x2"));
        }

        /// <summary>把 cfg 替换成出厂默认（控件重建前调用）。</summary>
        void ResetDefaults()
        {
            var d = new Cfg();
            cfg.host = d.host; cfg.user = d.user; cfg.password = d.password;
            cfg.interval = d.interval; cfg.alpha = d.alpha;
            cfg.fontFam = d.fontFam; cfg.fontSize = d.fontSize;
            cfg.fg = d.fg; cfg.bg = d.bg; cfg.accent = d.accent; cfg.warn = d.warn; cfg.bad = d.bad;
            cfg.topmost = d.topmost; cfg.borderless = d.borderless; cfg.locked = d.locked;
            cfg.showTitle = d.showTitle; cfg.savePassword = d.savePassword;
            foreach (var kv in d.rows) cfg.rows[kv.Key] = kv.Value;
            // language / posX / posY 不重置
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

        /// <summary>注册表 Run 项是否指向当前 exe（路径归一化后比较，避免大小写/相对路径差异）。</summary>
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

        /// <summary>开启则写入 Run 项（路径加引号防空格），关闭则删除；失败静默忽略不影响其余设置。</summary>
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

        // 滚轮转发：光标位于 tab 内容区时，滚动当前页（子控件如复选框会吞掉滚轮消息，故用全局消息筛选）
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020a;
            if (m.Msg != WM_MOUSEWHEEL) return false;
            if (!IsHandleCreated || !Visible) return false;
            Point screen = Control.MousePosition;
            Point client = PointToClient(screen);
            Rectangle body = tabs.ClientRectangle;
            if (!body.Contains(client)) return false;
            var page = tabs.SelectedPage;
            if (page == null) return false;
            int delta = unchecked((short)((long)m.WParam >> 16));   // 高 16 位为有符号滚轮增量
            if (delta == 0) return false;
            Point pos = page.AutoScrollPosition;
            int ny = pos.Y + (delta > 0 ? 96 : -96);
            page.AutoScrollPosition = new Point(pos.X, ny);
            return true;
        }
    }

    /// <summary>色块按钮：白底 + 灰边框 + 内部填色，hover 蓝描边动画。点击弹 ColorDialog。</summary>
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
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            double h = Anim.Get(this, "hover");
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(value))
            using (var path = Draw.Round(rect, 2))
                g.FillPath(b, path);
            using (var path = Draw.Round(rect, 2))
            using (var p = new Pen(Draw.Lerp(Color.FromArgb(122, 122, 122), Pal.Accent, h)))
                g.DrawPath(p, path);
            if (h > 0.05)
            {
                var inner = new Rectangle(1, 1, Width - 3, Height - 3);
                using (var path = Draw.Round(inner, 2))
                using (var p = new Pen(Color.FromArgb((int)(h * 90), 0, 120, 212)))
                    g.DrawPath(p, path);
            }
        }
    }

    static class CfgClone
    {
        /// <summary>浅拷贝一份配置给对话框编辑，取消时不污染原配置。</summary>
        public static Cfg CloneLike(this Cfg c)
        {
            var n = new Cfg();
            n.host = c.host; n.user = c.user; n.password = c.password;
            n.language = c.language;               // 必须拷贝，否则下拉框默认选中回退到 zh-CN
            n.interval = c.interval; n.alpha = c.alpha;
            n.fontFam = c.fontFam; n.fontSize = c.fontSize;
            n.fg = c.fg; n.bg = c.bg; n.accent = c.accent; n.warn = c.warn; n.bad = c.bad;
            foreach (var kv in c.rows) n.rows[kv.Key] = kv.Value;
            n.topmost = c.topmost; n.borderless = c.borderless; n.locked = c.locked;
            n.showTitle = c.showTitle; n.savePassword = c.savePassword;
            n.posX = c.posX; n.posY = c.posY;
            return n;
        }
    }
}
