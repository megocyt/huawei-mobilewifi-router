using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HwWidget
{
    // ====================================================================
    //  逐帧动画辅助：每个控件一个 Timer，属性值向目标插值，到位自动停。
    // ====================================================================
    static class Anim
    {
        class State
        {
            public readonly Dictionary<string, double> values = new Dictionary<string, double>();
            public readonly Dictionary<string, double> targets = new Dictionary<string, double>();
            public Timer timer;
        }
        static readonly Dictionary<Control, State> map = new Dictionary<Control, State>();

        static State Get(Control c)
        {
            State s;
            if (map.TryGetValue(c, out s)) return s;
            s = new State();
            s.timer = new Timer { Interval = 16 };   // ~60fps
            s.timer.Tick += delegate
            {
                bool busy = false;
                var keys = new List<string>(s.values.Keys);
                foreach (var k in keys)
                {
                    double cur = s.values[k], tgt = s.targets[k];
                    double next = cur + (tgt - cur) * 0.35;
                    if (Math.Abs(tgt - next) < 0.004) next = tgt; else busy = true;
                    s.values[k] = next;
                }
                if (!c.IsDisposed) c.Invalidate();
                if (!busy) s.timer.Stop();
            };
            map[c] = s;
            c.Disposed += delegate { s.timer.Dispose(); map.Remove(c); };
            return s;
        }

        public static double Get(Control c, string key)
        {
            double v;
            var s = Get(c);
            if (!s.values.TryGetValue(key, out v)) { s.values[key] = 0; s.targets[key] = 0; v = 0; }
            return v;
        }

        public static void Set(Control c, string key, double target)
        {
            var s = Get(c);
            double v;
            if (!s.values.TryGetValue(key, out v)) { s.values[key] = 0; }
            s.targets[key] = target;
            if (Math.Abs(target - s.values[key]) > 0.004) s.timer.Start();
        }

        /// <summary>立即跳转（不做动画），用于初始化。</summary>
        public static void Snap(Control c, string key, double val)
        {
            var s = Get(c);
            s.values[key] = val;
            s.targets[key] = val;
        }
    }

    // ====================================================================
    //  通用绘制工具
    // ====================================================================
    static class Draw
    {
        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0 || r.Width <= 1 || r.Height <= 1) { p.AddRectangle(r); p.CloseFigure(); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static void GradientV(Graphics g, Rectangle r, Color top, Color bottom)
        {
            if (r.Height <= 0 || r.Width <= 0) return;
            using (var br = new LinearGradientBrush(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(2, r.Height)), top, bottom, 90f))
                g.FillRectangle(br, r);
        }

        public static void Text(Graphics g, string s, Font f, Rectangle r, Color color, ContentAlignment align)
        {
            TextRenderer.DrawText(g, s, f, r, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    static class Pal
    {
        public static readonly Color Accent = Color.FromArgb(0, 120, 212);
        public static readonly Color AccentDark = Color.FromArgb(0, 84, 153);
        public static readonly Color AccentLight = Color.FromArgb(229, 241, 251);
        public static readonly Color CheckBlue = Color.FromArgb(0, 120, 215);   // Win10 复选框蓝
        public static readonly Color SelBlue = Color.FromArgb(204, 232, 255);    // 下拉选中行浅蓝
        public static readonly Color TitleBack = Color.FromArgb(240, 250, 247);  // 奶油色标题栏
        public static readonly Color StripBack = Color.FromArgb(240, 240, 240);  // 顶层 tab 灰条
        public static readonly Color WindowBorder = Color.FromArgb(218, 218, 218);
        public static readonly Color BtnTop = Color.FromArgb(253, 253, 253);     // Snipaste 扁平白按钮
        public static readonly Color BtnBot = Color.FromArgb(253, 253, 253);
        public static readonly Color BtnHoverTop = Color.FromArgb(229, 241, 251);
        public static readonly Color BtnHoverBot = Color.FromArgb(229, 241, 251);
        public static readonly Color BtnPressTop = Color.FromArgb(204, 228, 247);
        public static readonly Color BtnPressBot = Color.FromArgb(204, 228, 247);
        public static readonly Color BtnBorder = Color.FromArgb(187, 187, 187);
        public static readonly Color EditBorder = Color.FromArgb(160, 160, 160);
        public static readonly Color GroupBorder = Color.FromArgb(214, 214, 214);
        public static readonly Color Text = Color.FromArgb(32, 32, 32);   // 非纯黑，观感更柔和
        public static readonly Color TextGray = Color.Gray;
    }

    // ====================================================================
    //  WinButton：hover 渐变 + 按下变色 + 文字居中 + Primary 蓝环
    // ====================================================================
    class WinButton : Control, IButtonControl
    {
        public bool Primary;
        public DialogResult DialogResult { get; set; }

        public WinButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(84, 26);
            Cursor = Cursors.Default;
            TabStop = true;
        }

        public void NotifyDefault(bool value) { Invalidate(); }
        public void PerformClick()
        {
            Anim.Set(this, "press", 1);
            OnClick(EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); Anim.Set(this, "press", 0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Anim.Set(this, "press", 1); Focus(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Anim.Set(this, "press", 0); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Anim.Set(this, "focus", 1); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Anim.Set(this, "focus", 0); base.OnLostFocus(e); }
        // 自绘控件文本变化后主动重绘，否则语言实时切换时按钮旧文字不会被刷新
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Space || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Anim.Set(this, "press", 1); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                Anim.Set(this, "press", 0);
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double h = Anim.Get(this, "hover"), pr = Anim.Get(this, "press"), fo = Anim.Get(this, "focus");

            Color top = Draw.Lerp(Pal.BtnTop, pr > 0.5 ? Pal.BtnPressTop : Pal.BtnHoverTop, h);
            Color bot = Draw.Lerp(Pal.BtnBot, pr > 0.5 ? Pal.BtnPressBot : Pal.BtnHoverBot, h);
            if (pr > 0.5) { top = Pal.BtnPressTop; bot = Pal.BtnPressBot; }
            else if (h > 0) { top = Draw.Lerp(Pal.BtnTop, Pal.BtnHoverTop, h); bot = Draw.Lerp(Pal.BtnBot, Pal.BtnHoverBot, h); }

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Draw.Round(rect, 2))
            {
                var old = g.Clip;
                g.SetClip(path);
                Draw.GradientV(g, rect, top, bot);
                g.ResetClip();
                g.Clip = old;
            }

            Color borderColor = Primary ? Pal.Accent : Draw.Lerp(Pal.BtnBorder, Pal.AccentDark, h);
            using (var path2 = Draw.Round(rect, 2))
            using (var p = new Pen(borderColor))
                g.DrawPath(p, path2);

            // hover/primary 的 inset 蓝环
            double ring = Primary ? Math.Max(0.55, fo) : h * 0.55;
            if (ring > 0.02)
            {
                var inner = new Rectangle(1, 1, Width - 3, Height - 3);
                using (var path3 = Draw.Round(inner, 2))
                using (var p = new Pen(Color.FromArgb((int)(ring * 70), 0, 120, 212)))
                    g.DrawPath(p, path3);
            }

            // 焦点虚线框
            if (fo > 0.5)
            {
                var focusRect = new Rectangle(3, 3, Width - 7, Height - 7);
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (var p = new Pen(Color.FromArgb(80, 0, 0, 0)) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(p, focusRect);
                g.SmoothingMode = old;
            }

            int dy = pr > 0.5 ? 1 : 0;
            var textRect = new Rectangle(4 + 0, dy, Width - 8, Height);
            Draw.Text(g, Text, Font, textRect, Enabled ? Pal.Text : Pal.TextGray, ContentAlignment.MiddleCenter);
        }
    }

    // ====================================================================
    //  WinCheckBox：16px 圆角框 + 勾选弹入动画 + hover 蓝框
    // ====================================================================
    class WinCheckBox : Control
    {
        bool isChecked;
        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Anim.Set(this, "check", value ? 1 : 0);
                Invalidate();
            }
        }
        public event EventHandler CheckedChanged;

        public WinCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AutoSize = true;
            Cursor = Cursors.Default;
            TabStop = true;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var ts = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            return new Size(ts.Width + 26, Math.Max(18, ts.Height + 4));
        }

        protected override void OnAutoSizeChanged(EventArgs e) { base.OnAutoSizeChanged(e); }
        protected override void OnTextChanged(EventArgs e) { PerformAutoSize(); base.OnTextChanged(e); }
        // 继承窗体字体（微软雅黑 9pt 比默认字体宽）后重新自适应，避免文字被截断
        protected override void OnFontChanged(EventArgs e) { PerformAutoSize(); base.OnFontChanged(e); }
        protected override void OnParentChanged(EventArgs e) { PerformAutoSize(); base.OnParentChanged(e); }
        // 句柄创建（字体最终确定）后再自适应一次，防止初始测量字体偏小导致文字被省略号截断
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); PerformAutoSize(); }
        void PerformAutoSize() { if (AutoSize) Size = GetPreferredSize(Size.Empty); }

        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); Checked = !Checked; if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); base.OnMouseDown(e); }
        protected override void OnGotFocus(EventArgs e) { Anim.Set(this, "focus", 1); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Anim.Set(this, "focus", 0); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Space || base.IsInputKey(keyData); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !Checked; if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); e.Handled = true; }
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double h = Anim.Get(this, "hover"), ck = Anim.Get(this, "check"), fo = Anim.Get(this, "focus");

            int boxSize = 15;
            var box = new Rectangle(1, (Height - boxSize) / 2, boxSize - 1, boxSize - 1);

            // 白底（Win10 方角，非圆角）
            using (var b = new SolidBrush(Color.White))
                g.FillRectangle(b, box);

            // 勾选时填蓝（check 动画控制 alpha 渐入）
            if (ck > 0.02)
            {
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1, ck * 1.4)), Pal.CheckBlue)))
                    g.FillRectangle(b, box);
            }

            // 边框
            Color border = ck > 0.5 ? Pal.CheckBlue : Draw.Lerp(Pal.EditBorder, Pal.AccentDark, h);
            using (var p = new Pen(border))
                g.DrawRectangle(p, box);

            // 对勾：scale 弹入（overshoot）
            if (ck > 0.02)
            {
                double t = Math.Min(1, ck * 1.25);
                double scale = 1.0;   // 原生 Win10 对勾不做弹入缩放
                float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
                float s = box.Width / 16f * (float)scale;
                PointF[] pts =
                {
                    new PointF(cx - 4 * s, cy + 0.5f * s),
                    new PointF(cx - 1 * s, cy + 3.5f * s),
                    new PointF(cx + 4.5f * s, cy - 3 * s)
                };
                using (var p = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, pts);
            }

            // 焦点虚线
            if (fo > 0.5)
            {
                var fr = new Rectangle(0, (Height - boxSize) / 2 - 2, boxSize + 1, boxSize + 3);
                var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                using (var p = new Pen(Color.FromArgb(90, 0, 0, 0)) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(p, fr);
                g.SmoothingMode = old;
            }

            // 绘制时字体已最终确定：若因继承前测量的宽度偏小而装不下文字，则主动加宽，避免省略号截断
            if (AutoSize)
            {
                int needW = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.SingleLine).Width + 26;
                if (needW > Width) Size = new Size(needW, Height);
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(22, 0, Width - 22, Height), Pal.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        static double EaseOutBack(double t)
        {
            const double c1 = 1.70158, c3 = c1 + 1;
            double u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }
    }

    // ====================================================================
    //  WinGroupBox：圆角边框 + 标题割入
    // ====================================================================
    class WinGroupBox : Panel
    {
        string caption = "";
        public string Caption
        {
            get { return caption; }
            set { caption = value; Invalidate(); }
        }

        public WinGroupBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;   // 1px 直线保持锐利
            g.Clear(Parent != null ? Parent.BackColor : Color.White);

            int lh = Font.Height;                    // 标题行高，避免文字被 Panel 顶部裁剪
            int top = lh / 2 + 2;                    // 顶边下移，让标题骑跨在顶边线上且完整可见
            int textW = string.IsNullOrEmpty(caption) ? 0 : TextRenderer.MeasureText(caption, Font).Width;
            int gapX = 10, gapW = textW > 0 ? textW + 10 : 0;
            int titleH = lh + 4;                     // 标题绘制区高度

            // 缺口背景擦除（先擦再画边框，避免顶边线穿过标题）
            if (textW > 0)
                using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Color.White))
                    g.FillRectangle(b, gapX + 3, 0, gapW - 6, titleH);

            using (var p = new Pen(Pal.GroupBorder))
            {
                // 顶边（分两段留缺口）
                if (textW > 0)
                {
                    g.DrawLine(p, 0, top, gapX, top);
                    g.DrawLine(p, gapX + gapW, top, Width - 1, top);
                }
                else
                    g.DrawLine(p, 0, top, Width - 1, top);
                // 左 / 右 / 底
                g.DrawLine(p, 0, top, 0, Height - 1);
                g.DrawLine(p, Width - 1, top, Width - 1, Height - 1);
                g.DrawLine(p, 0, Height - 1, Width - 1, Height - 1);
            }

            if (textW > 0)
                TextRenderer.DrawText(g, caption, Font, new Rectangle(gapX + 4, 0, textW + 6, titleH), Pal.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    // ====================================================================
    //  WinTrackBar：4px 轨道 + 蓝 fill + 11x20 thumb（hover 放大变蓝）
    // ====================================================================
    class WinTrackBar : Control
    {
        int minimum = 0, maximum = 100, val = 50;
        public int Minimum { get { return minimum; } set { minimum = value; Clamp(); Invalidate(); } }
        public int Maximum { get { return maximum; } set { maximum = value; Clamp(); Invalidate(); } }
        public int Value
        {
            get { return val; }
            set { val = Math.Max(minimum, Math.Min(maximum, value)); Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
        }
        public event EventHandler ValueChanged;

        public WinTrackBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(180, 26);
        }

        void Clamp() { val = Math.Max(minimum, Math.Min(maximum, val)); }
        double Ratio { get { return maximum > minimum ? (val - (double)minimum) / (maximum - minimum) : 0; } }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); Capture = true; SetFromX(e.X); Anim.Set(this, "drag", 1); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Capture) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Capture = false; Anim.Set(this, "drag", 0); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); base.OnMouseLeave(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? 1 : -1; base.OnMouseWheel(e); }

        void SetFromX(int x)
        {
            double t = (x - ThumbW / 2.0) / (Width - ThumbW);
            t = Math.Max(0, Math.Min(1, t));
            Value = minimum + (int)Math.Round(t * (maximum - minimum));
        }

        const int ThumbW = 11, ThumbH = 20;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double h = Anim.Get(this, "hover"), d = Anim.Get(this, "drag");
            int cy = Height / 2;

            // 轨道
            var rail = new Rectangle(0, cy - 2, Width, 4);
            using (var path = Draw.Round(rail, 2))
            using (var b = new SolidBrush(Color.FromArgb(224, 224, 224)))
                g.FillPath(b, path);

            // 蓝色 fill
            int thumbX = (int)Math.Round(Ratio * (Width - ThumbW)) + ThumbW / 2;
            var fill = new Rectangle(0, cy - 2, thumbX, 4);
            if (fill.Width > 0)
                using (var path = Draw.Round(fill, 2))
                using (var b = new SolidBrush(Pal.Accent))
                    g.FillPath(b, path);

            // thumb：hover/drag 放大 + 变蓝
            double grow = (h > 0 || d > 0) ? 1 + 0.12 * Math.Max(h, d) : 1;
            int tw = (int)(ThumbW * grow), th = (int)(ThumbH * grow);
            var tr = new Rectangle(thumbX - tw / 2, cy - th / 2, tw, th);
            using (var path = Draw.Round(tr, 2))
            {
                var old = g.Clip; g.SetClip(path);
                Color tTop = Draw.Lerp(Color.FromArgb(245, 245, 245), Color.FromArgb(232, 243, 252), Math.Max(h, d));
                Color tBot = Draw.Lerp(Color.FromArgb(216, 216, 216), Color.FromArgb(204, 228, 247), Math.Max(h, d));
                if (d > 0.5) { tTop = Color.FromArgb(204, 228, 247); tBot = Color.FromArgb(153, 200, 235); }
                Draw.GradientV(g, tr, tTop, tBot);
                g.ResetClip(); g.Clip = old;
            }
            using (var path = Draw.Round(tr, 2))
            using (var p = new Pen(Math.Max(h, d) > 0.3 ? Pal.Accent : Color.FromArgb(144, 144, 144)))
                g.DrawPath(p, path);
        }
    }

    // ====================================================================
    //  WinComboBox：自绘 field + 弹出列表动画（TopMost 无边框 Form）
    // ====================================================================
    class WinComboBox : Control
    {
        public readonly List<string> Items = new List<string>();
        int sel = -1;
        bool openState;
        ComboPopup popup;

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                sel = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }
        public string SelectedItem { get { return sel >= 0 && sel < Items.Count ? Items[sel] : ""; } }
        public event EventHandler SelectedIndexChanged;

        public WinComboBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(130, 23);
            Cursor = Cursors.Default;
            TabStop = true;
        }

        protected override void OnMouseDown(MouseEventArgs e) { Toggle(); base.OnMouseDown(e); }
        protected override void OnGotFocus(EventArgs e) { Anim.Set(this, "focus", 1); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Anim.Set(this, "focus", 0); base.OnLostFocus(e); }

        int lastCloseTick;             // 防止失活关闭后同一次点击立即重开

        public void Toggle()
        {
            if (openState) CloseDrop();
            else if (Environment.TickCount - lastCloseTick > 200) OpenDrop();
        }

        void OpenDrop()
        {
            openState = true;
            Anim.Set(this, "open", 1);
            Invalidate();
            popup = new ComboPopup(this);
            Point screen = PointToScreen(new Point(0, Height + 1));
            popup.ShowPopup(screen, this, FindForm());
        }

        public void CloseDrop()
        {
            openState = false;
            lastCloseTick = Environment.TickCount;
            Anim.Set(this, "open", 0);
            if (!IsDisposed) Invalidate();
            if (popup != null) { var p = popup; popup = null; p.ClosePopup(); }
        }

        /// <summary>弹窗失活时由 popup 回调，安全收起。</summary>
        public void CloseDropSafely()
        {
            if (openState) CloseDrop();
        }

        public void Choose(int index)
        {
            CloseDrop();               // 先关弹窗，避免重建时回调已释放的 owner
            SelectedIndex = index;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double h = Anim.Get(this, "hover"), f = Anim.Get(this, "focus"), o = Anim.Get(this, "open");

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, rect);

            // 边框（展开/聚焦变蓝，Snipaste 扁平无凹陷阴影）
            Color border = Draw.Lerp(Pal.EditBorder, Pal.Accent, Math.Max(o, Math.Max(f, h * 0.4)));
            using (var p = new Pen(border)) g.DrawRectangle(p, rect);

            // 文字
            string txt = SelectedItem;
            TextRenderer.DrawText(g, txt, Font, new Rectangle(6, 0, Width - 24, Height), Pal.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // 下拉箭头 ▼（无独立渐变按钮区）
            using (var b = new SolidBrush(Color.FromArgb(96, 96, 96)))
                g.FillPolygon(b, new[] { new Point(Width - 15, Height / 2 - 2), new Point(Width - 7, Height / 2 - 2), new Point(Width - 11, Height / 2 + 3) });
        }

        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); base.OnMouseLeave(e); }

        /// <summary>下拉弹层：无边框 owned Form，可滚动，失活自动关闭。</summary>
        class ComboPopup : Form
        {
            readonly WinComboBox owner;
            int hoverIdx = -1;
            int topIndex;
            const int Pad = 2;
            const int ScrollW = 14;
            bool closing;

            public ComboPopup(WinComboBox cb)
            {
                owner = cb;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                KeyPreview = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                BackColor = Color.White;
            }

            int ItemH { get { return Math.Max(20, owner.Font.Height + 6); } }
            int VisibleRows { get { return Math.Max(1, (Height - Pad * 2) / ItemH); } }
            bool NeedScroll { get { return owner.Items.Count > VisibleRows; } }
            int ListW { get { return Width - (NeedScroll ? ScrollW : 0); } }

            public void ShowPopup(Point screen, WinComboBox cb, Form ownerForm)
            {
                int maxVisible = Math.Max(1, Math.Min(8, cb.Items.Count));
                int listH = maxVisible * ItemH + Pad * 2;
                int w = Math.Max(cb.Width, 130);
                SetBounds(screen.X, screen.Y, w, listH);
                // 让选中项落在可见区间
                if (cb.SelectedIndex >= 0)
                    topIndex = Math.Max(0, Math.Min(cb.SelectedIndex - maxVisible / 2, cb.Items.Count - maxVisible));
                else topIndex = 0;
                if (ownerForm != null) Show(ownerForm); else Show();
                Activate();
            }

            public void ClosePopup()
            {
                if (closing || IsDisposed) return;
                closing = true;
                Close();
            }

            protected override bool ShowWithoutActivation { get { return false; } }
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    const int CS_DROPSHADOW = 0x20000;
                    cp.ClassStyle |= CS_DROPSHADOW;
                    return cp;
                }
            }

            void SetTop(int v)
            {
                int maxTop = Math.Max(0, owner.Items.Count - VisibleRows);
                topIndex = Math.Max(0, Math.Min(maxTop, v));
                Invalidate();
            }

            Rectangle ThumbRect()
            {
                int trackH = Height - Pad * 2;
                int thumbH = Math.Max(20, trackH * VisibleRows / Math.Max(1, owner.Items.Count));
                int maxTop = Math.Max(1, owner.Items.Count - VisibleRows);
                int y = (int)((double)topIndex / maxTop * (trackH - thumbH));
                return new Rectangle(ListW + 3, Pad + y, ScrollW - 6, thumbH);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int idx = (e.Y - Pad) / ItemH + topIndex;
                if (NeedScroll && e.X >= ListW) idx = -1;
                if (idx != hoverIdx) { hoverIdx = idx; Invalidate(); }
                base.OnMouseMove(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                SetTop(topIndex - e.Delta / 120);
                base.OnMouseWheel(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (NeedScroll && e.X >= ListW)
                {
                    var th = ThumbRect();
                    if (e.Y < th.Top) SetTop(topIndex - VisibleRows);
                    else if (e.Y > th.Bottom) SetTop(topIndex + VisibleRows);
                    return;
                }
                int idx = (e.Y - Pad) / ItemH + topIndex;
                if (idx >= 0 && idx < owner.Items.Count) owner.Choose(idx);
                base.OnMouseDown(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { owner.CloseDropSafely(); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { SetTop(topIndex + 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Up) { SetTop(topIndex - 1); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter && hoverIdx >= 0) { owner.Choose(hoverIdx); e.Handled = true; }
                base.OnKeyDown(e);
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                // 弹窗失活（点外部 / 切窗口）→ 收起；owner 可能已释放需保护
                if (!closing && owner != null && !owner.IsDisposed) owner.CloseDropSafely();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Color.White);
                using (var p = new Pen(Color.FromArgb(187, 187, 187)))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                int ih = ItemH;
                for (int row = 0; row < VisibleRows; row++)
                {
                    int i = topIndex + row;
                    if (i >= owner.Items.Count) break;
                    var r = new Rectangle(1, Pad + row * ih, ListW - 2, ih);
                    if (i == owner.SelectedIndex)
                    {
                        using (var b = new SolidBrush(Pal.SelBlue)) g.FillRectangle(b, r);
                        using (var p = new Pen(Pal.Accent)) g.DrawRectangle(p, r);
                    }
                    else if (i == hoverIdx)
                        using (var b = new SolidBrush(Pal.AccentLight)) g.FillRectangle(b, r);
                    Color fc = Pal.Text;
                    TextRenderer.DrawText(g, owner.Items[i], owner.Font, new Rectangle(r.X + 8, r.Y, r.Width - 12, r.Height), fc,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                if (NeedScroll)
                {
                    using (var b = new SolidBrush(Color.FromArgb(240, 240, 240)))
                        g.FillRectangle(b, ListW, Pad, ScrollW, Height - Pad * 2);
                    var th = ThumbRect();
                    using (var b = new SolidBrush(Color.FromArgb(170, 170, 170)))
                    using (var path = Draw.Round(th, 2))
                        g.FillPath(b, path);
                }
            }
        }
    }

    // ====================================================================
    //  WinTabControl：自绘经典 tab 头（渐变 + 选中连通）+ 页面 slide-in 切换
    // ====================================================================
    class WinTabControl : Control
    {
        readonly List<string> captions = new List<string>();
        readonly List<Panel> pages = new List<Panel>();
        int sel;
        int hoverIdx = -1;
        Timer slideTimer;
        double slideT;                    // 1 → 0，新页从右侧 8px 滑入
        Font boldFont;

        /// <summary>子 tab 模式：白底条 + 内容区带 1px 边框（Snipaste 嵌套子页）；
        /// 顶层模式：灰底条 + 内容区无边框（由窗体边框收边）。</summary>
        public bool Sub;

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value < 0 || value >= pages.Count || value == sel) return;
                sel = value;
                for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == sel;
                if (sel < pages.Count) pages[sel].BringToFront();
                StartSlide();
                LayoutPages();
                Invalidate();
            }
        }

        public WinTabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.ContainerControl, true);
            BackColor = Color.White;
            TabStop = false;
        }

        Font BoldFont()
        {
            if (boldFont == null || boldFont.Size != Font.Size) { if (boldFont != null) boldFont.Dispose(); boldFont = new Font(Font, FontStyle.Bold); }
            return boldFont;
        }
        protected override void OnFontChanged(EventArgs e) { if (boldFont != null) { boldFont.Dispose(); boldFont = null; } Invalidate(); base.OnFontChanged(e); }

        /// <summary>清空 tab 页列表，供语言切换重建前调用。</summary>
        public void ResetPages()
        {
            captions.Clear();
            pages.Clear();
            sel = 0;
            hoverIdx = -1;
            slideT = 0;
            if (slideTimer != null) { slideTimer.Stop(); }
            Controls.Clear();
            Invalidate();
        }

        public Panel AddPage(string caption)
        {
            captions.Add(caption);
            var page = new ScrollPage { Visible = captions.Count == 1 };
            pages.Add(page);
            Controls.Add(page);
            LayoutPages();
            Invalidate();
            return page.Content;      // 内容添加到内层 Content，由 ScrollPage 保证只有纵向滚动
        }

        public void SetPageCaption(int i, string caption) { captions[i] = caption; Invalidate(); }

        /// <summary>当前选中的页面 Panel（供外部滚轮转发使用）。</summary>
        public Panel SelectedPage { get { return (sel >= 0 && sel < pages.Count) ? pages[sel] : null; } }

        int HeadH { get { return Sub ? 24 : 28; } }
        Rectangle TabRect(int i)
        {
            int x = Sub ? 6 : 8;
            for (int k = 0; k < i; k++) x += TabWidth(k);
            return new Rectangle(x, 0, TabWidth(i), HeadH);
        }
        // 统一用加粗字体量宽，选中加粗时不引起 tab 宽度跳动
        int TabWidth(int i)
        {
            return TextRenderer.MeasureText(captions[i], BoldFont()).Width + (Sub ? 20 : 26);
        }

        void StartSlide()
        {
            slideT = 1;
            if (slideTimer == null)
            {
                slideTimer = new Timer { Interval = 16 };
                slideTimer.Tick += delegate
                {
                    slideT += (0 - slideT) * 0.35;
                    if (slideT < 0.03) { slideT = 0; slideTimer.Stop(); }
                    LayoutPages();
                };
            }
            slideTimer.Start();
        }

        void LayoutPages()
        {
            var body = new Rectangle(0, HeadH - 1, Width, Height - HeadH + 1);
            for (int i = 0; i < pages.Count; i++)
            {
                int off = (i == sel) ? (int)Math.Round(slideT * 8) : 0;
                pages[i].Bounds = new Rectangle(body.X + 1 + off, body.Y + 1, body.Width - 2, body.Height - 2);
            }
        }

        protected override void OnSizeChanged(EventArgs e) { LayoutPages(); base.OnSizeChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Y < HeadH)
            {
                for (int i = 0; i < captions.Count; i++)
                    if (TabRect(i).Contains(e.Location)) { SelectedIndex = i; break; }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = -1;
            if (e.Y < HeadH)
                for (int i = 0; i < captions.Count; i++)
                    if (TabRect(i).Contains(e.Location)) { idx = i; break; }
            if (idx != hoverIdx)
            {
                if (hoverIdx >= 0) Anim.Set(this, "h" + hoverIdx, 0);
                hoverIdx = idx;
                if (idx >= 0) Anim.Set(this, "h" + idx, 1);
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverIdx >= 0) Anim.Set(this, "h" + hoverIdx, 0);
            hoverIdx = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;   // 1px 直线保持锐利
            int hh = HeadH;
            bool hasSel = sel >= 0 && sel < captions.Count;
            var activeTab = hasSel ? TabRect(sel) : Rectangle.Empty;
            Color line = Sub ? Pal.GroupBorder : Pal.WindowBorder;
            Color tabLine = Color.FromArgb(187, 187, 187);

            // tab 条背景：顶层灰、子页白
            using (var b = new SolidBrush(Sub ? Color.White : Pal.StripBack))
                g.FillRectangle(b, 0, 0, Width, hh);

            // 内容区顶边（选中 tab 处断开连通）；子页另画左/右/底边框
            var body = new Rectangle(0, hh - 1, Width - 1, Height - hh);
            using (var p = new Pen(line))
            {
                if (hasSel)
                {
                    g.DrawLine(p, body.X, body.Y, activeTab.X, body.Y);
                    g.DrawLine(p, activeTab.Right, body.Y, body.Right, body.Y);
                }
                else
                    g.DrawLine(p, body.X, body.Y, body.Right, body.Y);
                if (Sub)
                {
                    g.DrawLine(p, body.X, body.Y, body.X, body.Bottom);
                    g.DrawLine(p, body.X, body.Bottom, body.Right, body.Bottom);
                    g.DrawLine(p, body.Right, body.Y, body.Right, body.Bottom);
                }
            }

            for (int i = 0; i < captions.Count; i++)
            {
                var r = TabRect(i);
                bool active = i == sel;
                if (active)
                {
                    // 白色凸起块：顶/左/右描边，底边开口与内容连通；文字加粗
                    var box = new Rectangle(r.X, 2, r.Width - 1, hh - 2);
                    using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, box);
                    using (var p = new Pen(tabLine))
                    {
                        g.DrawLine(p, box.X, box.Y, box.X, box.Bottom);
                        g.DrawLine(p, box.X, box.Y, box.Right, box.Y);
                        g.DrawLine(p, box.Right, box.Y, box.Right, box.Bottom);
                    }
                    TextRenderer.DrawText(g, captions[i], BoldFont(), new Rectangle(r.X, 1, r.Width, hh), Pal.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                else
                {
                    // 未选中：灰条上的纯文字（无包围盒），hover 微亮
                    double hv = Anim.Get(this, "h" + i);
                    if (hv > 0.02)
                        using (var b = new SolidBrush(Draw.Lerp(Sub ? Color.White : Pal.StripBack, Color.FromArgb(229, 229, 229), hv)))
                            g.FillRectangle(b, r.X, 2, r.Width - 1, hh - 3);
                    TextRenderer.DrawText(g, captions[i], Font, new Rectangle(r.X, 0, r.Width, hh), Color.FromArgb(68, 68, 68),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    /// <summary>双缓冲 Panel，避免页面内容闪烁。</summary>
    class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    /// <summary>纵向滚动页面：内层 Content 宽度始终等于视口宽度，因此横向永不溢出（不出现水平滚动条），
    /// 仅当内容高度超过视口时出现垂直滚动条。子控件请添加到 Content 上，坐标相对 Content。</summary>
    class ScrollPage : Panel
    {
        public readonly Panel Content;

        public ScrollPage()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.White;
            AutoScroll = true;
            Content = new DoubleBufferedPanel { BackColor = Color.White };
            Content.Layout += delegate { Fit(); };
            Controls.Add(Content);
        }

        protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); Fit(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Fit(); }

        void Fit()
        {
            if (Content == null) return;
            int h = 0;
            foreach (Control c in Content.Controls)
                if (c.Bottom > h) h = c.Bottom;
            int w = ClientSize.Width;                 // 已扣除垂直滚动条占位，保证不触发横向溢出
            int ch = Math.Max(h + 6, ClientSize.Height);
            // 只改尺寸不改 Location（位置由 AutoScroll 管理），避免每次 Layout 把滚动重置到顶部
            if (Content.Width != w || Content.Height != ch)
                Content.Size = new Size(w, ch);
        }
    }

    // ====================================================================
    //  WinTextBox：凹陷描边 + focus 蓝环的 TextBox 容器
    // ====================================================================
    class WinTextBox : Panel
    {
        public readonly TextBox Inner;
        public string TextValue { get { return Inner.Text; } set { Inner.Text = value; } }

        public WinTextBox()
        {
            BackColor = Color.White;
            Inner = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Color.White
            };
            Inner.GotFocus += delegate { Anim.Set(this, "focus", 1); };
            Inner.LostFocus += delegate { Anim.Set(this, "focus", 0); };
            Controls.Add(Inner);
            Size = new Size(130, 23);
            LayoutInner();
        }

        public bool UseSystemPasswordChar
        {
            get { return Inner.UseSystemPasswordChar; }
            set { Inner.UseSystemPasswordChar = value; }
        }

        void LayoutInner()
        {
            Inner.Top = (Height - Inner.Height) / 2;
            Inner.Left = 6;
            Inner.Width = Width - 12;
        }
        protected override void OnSizeChanged(EventArgs e) { LayoutInner(); base.OnSizeChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            double f = Anim.Get(this, "focus"), h = Anim.Get(this, "hover");
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, rect);
            Color border = Draw.Lerp(Pal.EditBorder, Pal.Accent, Math.Max(f, h * 0.6));
            using (var p = new Pen(border)) g.DrawRectangle(p, rect);
            if (f > 0.02)
                using (var p = new Pen(Color.FromArgb((int)(f * 60), 0, 120, 212)))
                    g.DrawRectangle(p, new Rectangle(1, 1, Width - 3, Height - 3));
        }
        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); base.OnMouseLeave(e); }
    }

    // ====================================================================
    //  WinNumeric：白底输入 + 右侧上下按钮（渐变 hover）
    // ====================================================================
    class WinNumeric : Control
    {
        readonly TextBox tb;
        decimal val, min, max, step = 1;
        const int BtnW = 18;

        public decimal Minimum { get { return min; } set { min = value; Clamp(); UpdateText(); } }
        public decimal Maximum { get { return max; } set { max = value; Clamp(); UpdateText(); } }
        public decimal Increment { get { return step; } set { step = value; } }
        public decimal Value
        {
            get { Commit(); return val; }
            set { val = value; Clamp(); UpdateText(); }
        }

        public WinNumeric()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            tb = new TextBox { BorderStyle = BorderStyle.None, BackColor = Color.White, TextAlign = HorizontalAlignment.Right };
            tb.Leave += delegate { Commit(); };
            tb.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { Commit(); e.SuppressKeyPress = true; } };
            Controls.Add(tb);
            Size = new Size(60, 23);
        }

        void Clamp() { if (max > min) val = Math.Max(min, Math.Min(max, val)); }
        void UpdateText() { tb.Text = val.ToString("0", CultureInfo_Invariant); }
        static readonly System.Globalization.CultureInfo CultureInfo_Invariant = System.Globalization.CultureInfo.InvariantCulture;
        void Commit()
        {
            decimal v;
            if (decimal.TryParse(tb.Text, System.Globalization.NumberStyles.Any, CultureInfo_Invariant, out v)) val = v;
            Clamp(); UpdateText(); Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            tb.Left = 5;
            tb.Top = (Height - tb.Height) / 2;
            tb.Width = Width - BtnW - 10;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.X >= Width - BtnW)
            {
                Anim.Set(this, e.Y < Height / 2 ? "up" : "down", 1);
                val += e.Y < Height / 2 ? step : -step;
                Clamp(); UpdateText(); Invalidate();
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { Anim.Set(this, "up", 0); Anim.Set(this, "down", 0); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { Anim.Set(this, "hover", 1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Anim.Set(this, "hover", 0); Anim.Set(this, "up", 0); Anim.Set(this, "down", 0); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Anim.Set(this, "focus", 1); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Commit(); Anim.Set(this, "focus", 0); base.OnLostFocus(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { val += e.Delta > 0 ? step : -step; Clamp(); UpdateText(); Invalidate(); base.OnMouseWheel(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            double h = Anim.Get(this, "hover"), f = Anim.Get(this, "focus"), up = Anim.Get(this, "up"), dn = Anim.Get(this, "down");

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, rect);

            int sep = Width - BtnW;
            var upRect = new Rectangle(sep + 1, 1, BtnW - 2, Height / 2 - 1);
            var dnRect = new Rectangle(sep + 1, Height / 2, BtnW - 2, Height - Height / 2 - 2);
            Color upFace = up > 0.3 ? Color.FromArgb(204, 228, 247) : (h > 0.3 ? Pal.AccentLight : Color.FromArgb(253, 253, 253));
            Color dnFace = dn > 0.3 ? Color.FromArgb(204, 228, 247) : (h > 0.3 ? Pal.AccentLight : Color.FromArgb(253, 253, 253));
            using (var b = new SolidBrush(upFace)) g.FillRectangle(b, upRect);
            using (var b = new SolidBrush(dnFace)) g.FillRectangle(b, dnRect);

            Color border = Draw.Lerp(Pal.EditBorder, Pal.Accent, Math.Max(h, f));
            using (var p = new Pen(border)) g.DrawRectangle(p, rect);
            using (var p = new Pen(Color.FromArgb(196, 196, 196)))
            {
                g.DrawLine(p, sep, 1, sep, Height - 2);
                g.DrawLine(p, sep + 1, Height / 2, Width - 2, Height / 2);
            }

            using (var b = new SolidBrush(Color.FromArgb(85, 85, 85)))
            {
                int ax = sep + BtnW / 2;
                g.FillPolygon(b, new[] { new Point(ax - 3, Height / 4 + 2), new Point(ax + 3, Height / 4 + 2), new Point(ax, Height / 4 - 2) });
                g.FillPolygon(b, new[] { new Point(ax - 3, Height - Height / 4 - 2), new Point(ax + 3, Height - Height / 4 - 2), new Point(ax, Height - Height / 4 + 2) });
            }
        }
    }
}
