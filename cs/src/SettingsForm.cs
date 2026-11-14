using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace HwWidget
{
    /// <summary>设置窗口：浅色原生外观，顶部水平选项卡分页。</summary>
    public class SettingsForm : Form
    {
        readonly Cfg cfg;                          // 对话框内编辑副本
        public Cfg Result { get { return cfg; } }

        FlatCombo srcBox;
        SpinBox ivlBox, sizeBox;
        TrackBar alphaBar;
        Label alphaLbl;
        CheckBox savePwdBox, topBox, lockBox, borderBox, titleBox;
        readonly Dictionary<string, CheckBox> rowBoxes = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, ColorBox> colorBtns = new Dictionary<string, ColorBox>();
        static readonly string[][] ColorDefs =
        {
            new[] { "fg", "文字" }, new[] { "bg", "底色" },
            new[] { "accent", "正常" }, new[] { "warn", "告警" }, new[] { "bad", "异常" },
        };
        TextBox hostBox, userBox, pwdBox, panelBox;

        const int LX = 18, CX = 96;               // 标签列 / 控件列起点

        public SettingsForm(Cfg current)
        {
            cfg = current.CloneLike();
            SuspendLayout();
            Text = "悬浮窗设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            TopMost = true;
            ClientSize = new Size(520, 552);

            var tabs = new FlatTabControl { Left = 10, Top = 8, Width = 500, Height = 492 };
            var tConn = new TabPage("连接") { BackColor = Color.White };
            var tLook = new TabPage("外观") { BackColor = Color.White };
            var tRows = new TabPage("显示内容") { BackColor = Color.White };
            tabs.TabPages.Add(tConn);
            tabs.TabPages.Add(tLook);
            tabs.TabPages.Add(tRows);
            Controls.Add(tabs);

            BuildConnPage(tConn);
            BuildLookPage(tLook);
            BuildRowsPage(tRows);

            var cancel = new Button { Text = "取消", Left = 300, Top = 510, Width = 100, Height = 30 };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);
            var ok = new Button { Text = "保存并应用", Left = 410, Top = 510, Width = 100, Height = 30 };
            ok.Click += delegate { SaveAndClose(); };
            Controls.Add(ok);
            AcceptButton = ok;

            ResumeLayout(false);
        }

        // ---------------------------------------------------------------- 公共小件
        static void Lbl(Control p, int y, string text)
        {
            p.Controls.Add(new Label { Text = text, Left = LX, Top = y + 4, AutoSize = true });
        }

        static void Hint(Control p, int x, int y, string text)
        {
            p.Controls.Add(new Label { Text = text, Left = x, Top = y + 4, AutoSize = true, ForeColor = Color.FromArgb(120, 126, 133) });
        }

        static TextBox Tbx(Control p, int y, int w, string text)
        {
            var box = new LineBox { Left = CX, Top = y, Width = w, Height = 24 };
            var t = new TextBox { Text = text, BorderStyle = BorderStyle.None, BackColor = Color.White };
            t.Left = 6;
            t.Top = (box.Height - t.Height) / 2;
            t.Width = w - 12;
            box.Controls.Add(t);
            p.Controls.Add(box);
            return t;
        }

        static LineBox WrapBox(Control p, int x, int y, int w)
        {
            var box = new LineBox { Left = x, Top = y, Width = w, Height = 24 };
            p.Controls.Add(box);
            return box;
        }

        static CheckBox Chk(Control p, int x, int y, string text, bool on)
        {
            var c = new CheckBox { Text = text, Left = x, Top = y, AutoSize = true, Checked = on };
            p.Controls.Add(c);
            return c;
        }

        // ---------------------------------------------------------------- 连接页
        void BuildConnPage(TabPage p)
        {
            int y = 18;
            Lbl(p, y, "数据源");
            srcBox = new FlatCombo(new string[] { "auto", "panel", "direct" }) { Left = CX, Top = y, Width = 120, Height = 24 };
            srcBox.SelectedItem = cfg.source == "panel" || cfg.source == "direct" ? cfg.source : "auto";
            p.Controls.Add(srcBox);
            Hint(p, 232, y, "panel=走本机面板，不顶号");
            y += 38;

            Lbl(p, y, "管理地址");
            hostBox = Tbx(p, y, 200, cfg.host);
            Hint(p, 306, y, "一般是 192.168.8.1");
            y += 38;
            Lbl(p, y, "登录用户");
            userBox = Tbx(p, y, 200, cfg.user);
            y += 38;
            Lbl(p, y, "登录密码");
            pwdBox = Tbx(p, y, 200, cfg.password);
            pwdBox.UseSystemPasswordChar = true;
            y += 38;
            Lbl(p, y, "面板地址");
            panelBox = Tbx(p, y, 340, cfg.panelUrl);
            y += 38;
            Lbl(p, y, "刷新秒数");
            ivlBox = new SpinBox { Left = CX, Top = y, Width = 70, Height = 24, Minimum = 2, Maximum = 120, Increment = 1, Value = (decimal)Math.Max(2, Math.Min(120, cfg.interval)) };
            p.Controls.Add(ivlBox);
            y += 44;
            savePwdBox = Chk(p, LX, y, "把密码保存到本机配置文件（不勾则每次启动需重新输入）", cfg.savePassword);
        }

        // ---------------------------------------------------------------- 外观页
        void BuildLookPage(TabPage p)
        {
            int y = 18;
            Lbl(p, y, "透明度");
            alphaBar = new TrackBar { Left = CX - 4, Top = y - 6, Width = 200, Minimum = 25, Maximum = 100, TickStyle = TickStyle.None };
            alphaBar.Value = (int)Math.Round(cfg.alpha * 100);
            alphaLbl = new Label { Left = 300, Top = y + 4, Width = 46, AutoSize = false };
            alphaBar.Scroll += delegate { alphaLbl.Text = alphaBar.Value + "%"; };
            alphaLbl.Text = alphaBar.Value + "%";
            p.Controls.Add(alphaBar);
            p.Controls.Add(alphaLbl);
            y += 48;

            Lbl(p, y, "字号");
            sizeBox = new SpinBox { Left = CX, Top = y, Width = 60, Height = 24, Minimum = 9, Maximum = 30, Increment = 1, Value = cfg.fontSize };
            p.Controls.Add(sizeBox);
            y += 48;

            p.Controls.Add(new Label { Text = "颜色", Left = LX, Top = y, AutoSize = true, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) });
            y += 26;
            for (int i = 0; i < ColorDefs.Length; i++)
            {
                int col = i;
                p.Controls.Add(new Label { Text = ColorDefs[col][1], Left = LX + col * 84, Top = y + 4, AutoSize = true });
                var b = new ColorBox { Left = LX + col * 84 + 40, Top = y, Width = 26, Height = 24, Value = Hex(ColorOf(cfg, ColorDefs[col][0])) };
                string key = ColorDefs[col][0];
                b.Click += delegate { PickColor(key, b); };
                p.Controls.Add(b);
                colorBtns[key] = b;
            }
            y += 42;

            p.Controls.Add(new Label { Text = "行为", Left = LX, Top = y, AutoSize = true, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) });
            y += 26;
            topBox = Chk(p, LX + 4, y, "置顶显示", cfg.topmost);
            borderBox = Chk(p, 240, y, "无边框样式", cfg.borderless);
            y += 30;
            lockBox = Chk(p, LX + 4, y, "锁定位置（防误拖）", cfg.locked);
            titleBox = Chk(p, 240, y, "显示标题行", cfg.showTitle);
        }

        // ---------------------------------------------------------------- 显示内容页
        void BuildRowsPage(TabPage p)
        {
            for (int i = 0; i < Cfg.RowDefs.Length; i++)
            {
                string[] d = Cfg.RowDefs[i];
                var c = Chk(p, LX + (i % 2) * 230, 18 + (i / 2) * 32, d[1], cfg.rows[d[0]]);
                rowBoxes[d[0]] = c;
            }
        }

        // ---------------------------------------------------------------- 保存
        void PickColor(string key, ColorBox b)
        {
            using (var dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = Hex(ColorOf(cfg, key));
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string hex = "#" + dlg.Color.R.ToString("x2", CultureInfo.InvariantCulture)
                                      + dlg.Color.G.ToString("x2", CultureInfo.InvariantCulture)
                                      + dlg.Color.B.ToString("x2", CultureInfo.InvariantCulture);
                    SetColor(cfg, key, hex);
                    b.Value = dlg.Color;
                }
            }
        }

        static Color ColorOf(Cfg c, string key)
        {
            switch (key)
            {
                case "fg": return ColorTranslator.FromHtml(c.fg);
                case "bg": return ColorTranslator.FromHtml(c.bg);
                case "accent": return ColorTranslator.FromHtml(c.accent);
                case "warn": return ColorTranslator.FromHtml(c.warn);
                default: return ColorTranslator.FromHtml(c.bad);
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

        static Color Hex(Color c) { return Color.FromArgb(c.R, c.G, c.B); }

        void SaveAndClose()
        {
            cfg.source = string.IsNullOrEmpty(srcBox.SelectedItem) ? "auto" : srcBox.SelectedItem;
            cfg.host = string.IsNullOrEmpty(hostBox.Text.Trim()) ? "192.168.8.1" : hostBox.Text.Trim();
            cfg.user = string.IsNullOrEmpty(userBox.Text.Trim()) ? "admin" : userBox.Text.Trim();
            cfg.password = pwdBox.Text;
            cfg.panelUrl = string.IsNullOrEmpty(panelBox.Text.Trim()) ? "http://127.0.0.1:8765/data" : panelBox.Text.Trim();
            cfg.interval = Math.Max(2.0, (double)ivlBox.Value);
            cfg.alpha = alphaBar.Value / 100.0;
            cfg.fontSize = (int)sizeBox.Value;
            cfg.savePassword = savePwdBox.Checked;
            cfg.topmost = topBox.Checked;
            cfg.locked = lockBox.Checked;
            cfg.borderless = borderBox.Checked;
            cfg.showTitle = titleBox.Checked;
            foreach (var kv in rowBoxes) cfg.rows[kv.Key] = kv.Value.Checked;
            cfg.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Qt Fusion 式下拉框：完全自绘。弹出层用页面内嵌覆盖面板（非独立窗口），
    /// 尺寸严格等于控件宽，彻底避免顶层窗口尺寸被运行时改写的问题。</summary>
    class FlatCombo : Panel
    {
        readonly string[] items;
        int sel = -1;
        readonly Panel host;
        readonly QListView lb;
        Form hookedForm;
        Control hookedPage;
        MouseEventHandler dropHook;

        public FlatCombo(string[] entries)
        {
            items = entries;
            BackColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            lb = new QListView();
            lb.MouseUp += delegate
            {
                if (lb.SelectedIndex >= 0) { sel = lb.SelectedIndex; Invalidate(); }
                CloseDrop();
            };
            host = new Panel { BackColor = Color.White, Visible = false };
            host.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var p = new Pen(Color.FromArgb(140, 151, 163)))
                    e.Graphics.DrawRectangle(p, 0, 0, host.Width - 1, host.Height - 1);
                // 右侧常驻滚动条槽：近白轨道 + 细浅灰圆角滑块
                int sx = host.Width - 12;
                using (var b = new SolidBrush(Color.FromArgb(251, 252, 253)))
                    e.Graphics.FillRectangle(b, sx + 1, 1, 10, host.Height - 2);
                using (var p = new Pen(Color.FromArgb(228, 231, 235)))
                    e.Graphics.DrawLine(p, sx + 1, 1, sx + 1, host.Height - 2);
                var thumb = new Rectangle(sx + 3, 3, 6, Math.Max(10, host.Height - 8));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundedRect(thumb, 3))
                using (var b = new SolidBrush(Color.FromArgb(201, 206, 212)))
                    e.Graphics.FillPath(b, path);
            };
            host.Controls.Add(lb);
        }

        static GraphicsPath RoundedRect(Rectangle r, int rad)
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

        public string SelectedItem
        {
            get { return sel < 0 ? "" : items[sel]; }
            set
            {
                for (int i = 0; i < items.Length; i++) if (items[i] == value) { sel = i; break; }
                Invalidate();
            }
        }

        void CloseDrop()
        {
            host.Visible = false;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (host.Visible) { CloseDrop(); return; }
            Control page = Parent;
            if (page == null) return;
            if (host.Parent == null) page.Controls.Add(host);
            lb.Items.Clear();
            lb.Items.AddRange(items);
            lb.Highlight = SelectedItem;
            int h = items.Length * lb.ItemHeight + 2;
            host.BringToFront();
            host.SetBounds(Left, Bottom, Width, h);
            lb.SetBounds(1, 1, Width - 13, h - 2);
            host.Visible = true;
            Invalidate();
            HookOutsideClick(page);
        }

        void HookOutsideClick(Control page)
        {
            if (dropHook == null)
                dropHook = delegate(object s, MouseEventArgs me)
                {
                    if (!host.Visible) return;
                    if (me.Button != MouseButtons.Left) return;
                    Control src = s as Control;
                    Point rel = src.PointToClient(me.Location);
                    if (src == page)
                    {
                        if (!host.Bounds.Contains(rel)) CloseDrop();
                        return;
                    }
                    // 点在其它控件上：相对页面坐标再判一次
                    Point onPage = page.PointToClient(src.PointToScreen(me.Location));
                    if (!host.Bounds.Contains(onPage)) CloseDrop();
                };
            Unhook();
            hookedPage = page;
            page.MouseDown += dropHook;
            hookedForm = FindForm();
            if (hookedForm != null) hookedForm.MouseDown += dropHook;
        }

        void Unhook()
        {
            if (dropHook == null) return;
            if (hookedPage != null) hookedPage.MouseDown -= dropHook;
            if (hookedForm != null) hookedForm.MouseDown -= dropHook;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Unhook();
            base.OnHandleDestroyed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, ClientRectangle);
            // 展开时右侧按钮区浅蓝高亮（Qt Fusion 行为）
            if (host.Visible)
                using (var b = new SolidBrush(Color.FromArgb(205, 228, 247)))
                    g.FillRectangle(b, Width - 28, 1, 27, Height - 2);
            using (var p = new Pen(Color.FromArgb(224, 226, 229))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 2);
            using (var p = new Pen(Color.FromArgb(168, 172, 178))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            TextRenderer.DrawText(g, SelectedItem, Font, new Rectangle(6, 0, Width - 38, Height), Color.Black,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Color.FromArgb(70, 74, 80), 1.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                float vx = Width - 19, vy = Height / 2f - 2f;
                g.DrawLines(p, new[] { new PointF(vx - 5, vy), new PointF(vx, vy + 4.5f), new PointF(vx + 5, vy) });
            }
        }
    }

    /// <summary>Qt Fusion 式下拉列表：当前值行浅蓝底+蓝框，其余白底黑字。</summary>
    class QListView : ListBox
    {
        public string Highlight = "";
        public QListView()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            ItemHeight = 28;
            IntegralHeight = false;
            HorizontalScrollbar = false;
            BackColor = Color.White;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Graphics g = e.Graphics;
            // ListBox 宽度已让开滚动条槽，选项铺满自身区域，不再重复缩进
            Rectangle r = e.Bounds;
            string text = Items[e.Index].ToString();
            bool cur = text == Highlight;
            if (cur)
            {
                using (var b = new SolidBrush(Color.FromArgb(197, 227, 246))) g.FillRectangle(b, r);
                using (var p = new Pen(Color.FromArgb(74, 144, 217)))
                    g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
            }
            else using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, text, Font,
                new Rectangle(r.X + 8, r.Y, r.Width - 14, r.Height), Color.Black,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Qt Fusion 式数字框：LineBox 同款描边，白底，右侧上下黑色小三角无按钮块。</summary>
    class SpinBox : Panel
    {
        readonly TextBox tb;
        decimal val, min, max, step = 1;
        const int BtnW = 18;

        public SpinBox()
        {
            BackColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            tb = new TextBox { BorderStyle = BorderStyle.None, BackColor = Color.White };
            tb.Leave += delegate { Commit(); };
            tb.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) Commit(); };
            Controls.Add(tb);
        }

        public decimal Minimum { get { return min; } set { min = value; if (max == 0) max = min + 1; Clamp(); } }
        public decimal Maximum { get { return max; } set { max = value; Clamp(); } }
        public decimal Increment { get { return step; } set { step = value; } }
        public decimal Value
        {
            get { return val; }
            set { val = value; Clamp(); UpdateText(); }
        }

        void Clamp() { if (max > min) val = Math.Max(min, Math.Min(max, val)); }
        void UpdateText() { tb.Text = val.ToString("0", CultureInfo.InvariantCulture); }
        void Commit()
        {
            decimal v;
            if (decimal.TryParse(tb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) { val = v; }
            Clamp(); UpdateText(); Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); tb.Focus(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || e.X < Width - BtnW) return;
            if (e.Y < Height / 2) val += step; else val -= step;   // 上=加，下=减
            Clamp(); UpdateText(); Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            val += e.Delta > 0 ? step : -step;
            Clamp(); UpdateText(); Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            tb.Left = 6;
            tb.Top = (Height - tb.Height) / 2;
            tb.Width = Width - BtnW - 12;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, ClientRectangle);
            int sep = Width - BtnW;
            using (var b = new SolidBrush(Color.FromArgb(240, 242, 244)))
                g.FillRectangle(b, sep + 1, 1, BtnW - 2, Height - 2);
            using (var p = new Pen(Color.FromArgb(224, 226, 229))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 2);
            using (var p = new Pen(Color.FromArgb(168, 172, 178))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            using (var p = new Pen(Color.FromArgb(200, 203, 208)))
            {
                g.DrawLine(p, sep, 1, sep, Height - 2);                     // 编辑区/按钮区分隔
                g.DrawLine(p, sep + 1, Height / 2, Width - 1, Height / 2);  // 上下按钮分隔
            }
            using (var b = new SolidBrush(Color.FromArgb(70, 74, 80)))
            {
                int ax = sep + BtnW / 2;
                int uy = Height / 4, dy = Height - Height / 4;              // 上/下半区中心
                g.FillPolygon(b, new Point[] { new Point(ax - 3, uy + 2), new Point(ax + 3, uy + 2), new Point(ax, uy - 2) });
                g.FillPolygon(b, new Point[] { new Point(ax - 3, dy - 2), new Point(ax + 3, dy - 2), new Point(ax, dy + 2) });
            }
        }
    }

    /// <summary>自绘选项卡：选中页签涂白并与白色内容区连通。</summary>
    class FlatTabControl : TabControl
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F && m.Msg != 0x0317) return;   // WM_PAINT / WM_PRINTCLIENT
            if (SelectedIndex < 0 || TabCount == 0) return;
            Rectangle r;
            try { r = GetTabRect(SelectedIndex); }
            catch { return; }
            if (r.Width <= 0) return;
            Graphics g = m.Msg == 0x0317 ? Graphics.FromHdc(m.WParam) : CreateGraphics();
            using (g)
            {
                var fill = new Rectangle(r.X - 1, r.Y, r.Width + 2, r.Height + 1);
                using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, fill);
                TextRenderer.DrawText(g, SelectedTab.Text, Font,
                    new Rectangle(r.X, r.Y - 1, r.Width, r.Height), ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>色块选择框：与 LineBox 同款描边，内部留白后填色。</summary>
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
            BackColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, ClientRectangle);
            using (var b = new SolidBrush(value))
                g.FillRectangle(b, 3, 3, Width - 7, Height - 7);
            using (var p = new Pen(Color.FromArgb(224, 226, 229))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 2);
            using (var p = new Pen(Color.FromArgb(168, 172, 178))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>Qt Fusion 式输入框容器：白底，极浅四边框 + 稍深底线。</summary>
    class LineBox : Panel
    {
        public LineBox()
        {
            BackColor = Color.White;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Color.FromArgb(224, 226, 229))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 2);
            using (var p = new Pen(Color.FromArgb(168, 172, 178))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    static class CfgClone
    {
        /// <summary>浅拷贝一份配置给对话框编辑，取消时不污染原配置。</summary>
        public static Cfg CloneLike(this Cfg c)
        {
            var n = new Cfg();
            n.host = c.host; n.user = c.user; n.password = c.password;
            n.source = c.source; n.panelUrl = c.panelUrl;
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
