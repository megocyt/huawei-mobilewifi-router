using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HwWidget
{
    /// <summary>设置窗口（暗色）。保存后回写 cfg 并落盘，悬浮窗立即重建。</summary>
    public class SettingsForm : Form
    {
        readonly Cfg cfg;                          // 目标配置（对话框内直接编辑副本）
        public Cfg Result { get { return cfg; } }

        ComboBox srcBox;
        TextBox hostBox, userBox, pwdBox, panelBox;
        NumericUpDown ivlBox, sizeBox;
        TrackBar alphaBar;
        Label alphaLbl;
        CheckBox savePwdBox, topBox, lockBox, borderBox, titleBox;
        readonly Dictionary<string, CheckBox> rowBoxes = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, Button> colorBtns = new Dictionary<string, Button>();
        static readonly string[][] ColorDefs =
        {
            new[] { "fg", "文字" }, new[] { "bg", "底色" },
            new[] { "accent", "正常" }, new[] { "warn", "告警" }, new[] { "bad", "异常" },
        };

        static readonly Color Bg = Color.FromArgb(13, 17, 23);
        static readonly Color Panel = Color.FromArgb(22, 27, 34);
        static readonly Color Txt = Color.FromArgb(201, 209, 217);
        static readonly Color Muted = Color.FromArgb(139, 148, 158);
        static readonly Color Line = Color.FromArgb(48, 54, 61);
        static readonly Color Field = Color.FromArgb(13, 17, 23);

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
            BackColor = Bg;
            ForeColor = Txt;
            Font = new Font("Microsoft YaHei UI", 9f);
            TopMost = true;
            ClientSize = new Size(468, 800);        // 占位，构造末尾按实际布局重算

            int y = 12;
            y = Group(y, "设备连接", g =>
            {
                int r = 26;
                Lbl(g, 12, r, "数据源");
                srcBox = new ComboBox { Left = 110, Top = r - 3, Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
                srcBox.Items.AddRange(new object[] { "auto", "panel", "direct" });
                Dark(srcBox);
                srcBox.SelectedItem = cfg.source == "panel" || cfg.source == "direct" ? cfg.source : "auto";
                g.Controls.Add(srcBox);
                Hint(g, 230, r, "panel=走本机网页面板，最省设备会话");
                r += 28;
                Lbl(g, 12, r, "管理地址");
                hostBox = Tbx(g, 110, r, 150, cfg.host);
                Hint(g, 270, r, "一般是 192.168.8.1");
                r += 28;
                Lbl(g, 12, r, "登录用户");
                userBox = Tbx(g, 110, r, 150, cfg.user);
                r += 28;
                Lbl(g, 12, r, "登录密码");
                pwdBox = Tbx(g, 110, r, 150, cfg.password);
                pwdBox.UseSystemPasswordChar = true;
                r += 28;
                Lbl(g, 12, r, "面板地址");
                panelBox = Tbx(g, 110, r, 300, cfg.panelUrl);
                r += 28;
                Lbl(g, 12, r, "刷新秒数");
                ivlBox = new NumericUpDown { Left = 110, Top = r - 3, Width = 70, Minimum = 2, Maximum = 120, Value = (decimal)Math.Max(2, Math.Min(120, cfg.interval)) };
                Dark(ivlBox);
                g.Controls.Add(ivlBox);
                r += 30;
                savePwdBox = Chk(g, 12, r, "把密码保存到本机配置文件（不勾则每次启动需重新输入）", cfg.savePassword);
                return r + 26;
            });

            y = Group(y, "外观", g =>
            {
                int r = 26;
                Lbl(g, 12, r, "透明度");
                alphaBar = new TrackBar { Left = 100, Top = r - 6, Width = 160, Minimum = 25, Maximum = 100, TickStyle = TickStyle.None };
                alphaBar.Value = (int)Math.Round(cfg.alpha * 100);
                alphaLbl = new Label { Left = 266, Top = r, Width = 46, AutoSize = false, ForeColor = Muted };
                alphaBar.Scroll += delegate { alphaLbl.Text = alphaBar.Value + "%"; };
                alphaLbl.Text = alphaBar.Value + "%";
                g.Controls.Add(alphaBar);
                g.Controls.Add(alphaLbl);
                Lbl(g, 330, r, "字号");
                sizeBox = new NumericUpDown { Left = 380, Top = r - 3, Width = 56, Minimum = 9, Maximum = 30, Value = cfg.fontSize };
                Dark(sizeBox);
                g.Controls.Add(sizeBox);
                r += 42;
                foreach (string[] cd in ColorDefs)
                {
                    Lbl(g, 12 + Array.IndexOf(ColorDefs, cd) * 86, r, cd[1]);
                    var b = new Button
                    {
                        Left = 48 + Array.IndexOf(ColorDefs, cd) * 86,
                        Top = r - 3,
                        Width = 40,
                        Height = 20,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Hex(ColorOf(cfg, cd[0]))
                    };
                    b.FlatAppearance.BorderColor = Line;
                    string key = cd[0];
                    b.Click += delegate { PickColor(key, b); };
                    g.Controls.Add(b);
                    colorBtns[key] = b;
                }
                r += 34;
                topBox = Chk(g, 12, r, "置顶显示", cfg.topmost);
                lockBox = Chk(g, 150, r, "锁定位置（防误拖）", cfg.locked);
                borderBox = Chk(g, 320, r, "无边框", cfg.borderless);
                r += 26;
                titleBox = Chk(g, 12, r, "显示标题行", cfg.showTitle);
                return r + 26;
            });

            y = Group(y, "显示内容", g =>
            {
                int r = 26;
                for (int i = 0; i < Cfg.RowDefs.Length; i++)
                {
                    string[] d = Cfg.RowDefs[i];
                    var c = Chk(g, 12 + (i % 2) * 220, r + (i / 2) * 24, d[1], cfg.rows[d[0]]);
                    rowBoxes[d[0]] = c;
                }
                return r + ((Cfg.RowDefs.Length + 1) / 2) * 24 + 4;
            });

            var tip = new Label
            {
                Text = "改动立即生效",
                Left = 14, Top = y + 13, AutoSize = true, ForeColor = Muted
            };
            Controls.Add(tip);
            var ok = Btn(y + 6, "保存并应用", true);
            ok.Click += delegate { SaveAndClose(); };
            var cancel = Btn(y + 6, "取消", false);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = ok;

            // 按实际堆叠高度定窗口尺寸
            ClientSize = new Size(468, y + 6 + 28 + 14);

            ResumeLayout(false);
        }

        Button Btn(int y, string text, bool accent)
        {
            var b = new Button
            {
                Text = text,
                Top = y,
                Width = 96,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = accent ? Color.FromArgb(35, 64, 42) : Panel,
                ForeColor = Txt,
                DialogResult = accent ? DialogResult.OK : DialogResult.Cancel
            };
            b.FlatAppearance.BorderColor = accent ? Color.FromArgb(63, 185, 80) : Line;
            b.Location = new Point(468 - 12 - (accent ? 96 + 8 + 96 : 96), y);
            Controls.Add(b);
            return b;
        }

        void PickColor(string key, Button b)
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
                    b.BackColor = dlg.Color;
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

        // ---------------------------------------------------------------- 布局小件
        int Group(int y, string title, Func<Panel, int> fill)
        {
            var cap = new Label { Text = " " + title, Left = 10, Top = y, AutoSize = true, ForeColor = Color.FromArgb(88, 166, 255) };
            Controls.Add(cap);
            var p = new Panel { Left = 10, Top = y + 20, Width = 448, Height = 10, BorderStyle = BorderStyle.None, BackColor = Panel };
            Controls.Add(p);
            int inner = fill(p);
            p.Height = inner;
            // 描边
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            };
            return y + 20 + inner + 12;
        }

        void Lbl(Control parent, int x, int y, string text)
        {
            parent.Controls.Add(new Label
            {
                Text = text,
                Left = x,
                Top = y + 3,
                AutoSize = true,
                ForeColor = Muted
            });
        }

        void Hint(Control parent, int x, int y, string text)
        {
            parent.Controls.Add(new Label
            {
                Text = text,
                Left = x,
                Top = y + 3,
                AutoSize = true,
                ForeColor = Color.FromArgb(110, 118, 129)
            });
        }

        TextBox Tbx(Control parent, int x, int y, int w, string text)
        {
            var t = new TextBox { Left = x, Top = y, Width = w, Text = text };
            Dark(t);
            parent.Controls.Add(t);
            return t;
        }

        static void Dark(Control c)
        {
            c.BackColor = Field;
            c.ForeColor = Txt;
            var t = c as TextBoxBase;
            if (t != null) { t.BorderStyle = BorderStyle.FixedSingle; return; }
            var n = c as NumericUpDown;
            if (n != null) { n.BorderStyle = BorderStyle.FixedSingle; return; }
            var cb = c as ComboBox;
            if (cb != null) { cb.FlatStyle = FlatStyle.Flat; }
        }

        CheckBox Chk(Control parent, int x, int y, string text, bool on)
        {
            var c = new CheckBox
            {
                Text = text,
                Left = x,
                Top = y,
                AutoSize = true,
                Checked = on,
                ForeColor = Txt,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(c);
            return c;
        }

        void SaveAndClose()
        {
            cfg.source = (string)srcBox.SelectedItem ?? "auto";
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
