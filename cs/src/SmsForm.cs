using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HwWidget
{
    /// <summary>短信管理窗口：与设置窗同族经典 Win32 浅色风格（自绘奶油标题栏 + DWM 圆角）。
    /// 功能：分页列表 + 详情预览 + 标记已读 + 删除（二次确认）；未读用 ● 标记（语言无关）。
    /// 设备操作全部在后台线程执行（协议单会话，与悬浮窗轮询共用 devLock，抢不到锁报"设备忙"）。
    /// 发送短信需 RSA 加密接口，设备协议侧未实现，故本窗口只做收件管理。</summary>
    public class SmsForm : Form
    {
        const int WinW = 660, WinH = 522, TitleH = 32, FooterH = 46;
        const int PageSize = 10;      // 与列表可视高度匹配：一页恰好装满，不出纵向滚动条

        readonly WidgetForm w;
        ListView lv;
        TextBox txtContent;
        Label lblFrom, lblCount, lblPage, lblStatus;
        WinButton btnRefresh, btnRead, btnDel, btnPrev, btnNext, btnClose;
        Font fUnread;
        int page = 1;                     // 1 起
        bool busy;
        List<SmsItem> all = new List<SmsItem>();   // 逐条全部短信（已按时间倒序）
        int PageCount { get { return Math.Max(1, (all.Count + PageSize - 1) / PageSize); } }

        public SmsForm(WidgetForm owner)
        {
            w = owner;

            SuspendLayout();
            Text = L.T("sms.title");
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = TrayIcon.MakeFor(32); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ForeColor = Color.FromArgb(32, 32, 32);
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(WinW, WinH);
            TopMost = true;
            fUnread = new Font(Font, FontStyle.Bold);

            var titleBar = new WinTitleBar { Left = 0, Top = 0, Width = WinW, Height = TitleH, Title = L.T("sms.title") };
            try { titleBar.IconImage = TrayIcon.MakeFor(16).ToBitmap(); } catch { }
            titleBar.CloseClicked += delegate { Close(); };
            Controls.Add(titleBar);

            // ---- 工具行：刷新 / 标记已读 / 删除 …… 计数 …… 翻页 ◀ 页码 ▶
            int ty = TitleH + 8;
            btnRefresh = MakeButton(L.T("menu.refresh"), 76);
            btnRefresh.Left = 12;
            btnRefresh.Click += delegate { if (!busy) Reload(page); };
            btnRead = MakeButton(L.T("sms.markread"), 92);
            btnRead.Left = btnRefresh.Right + 8;
            btnRead.Click += delegate { OpRead(); };
            btnDel = MakeButton(L.T("sms.delete"), 76);
            btnDel.Left = btnRead.Right + 8;
            btnDel.Click += delegate { OpDelete(); };
            btnPrev = MakeButton("‹", 34);
            btnPrev.Click += delegate { if (!busy && page > 1) { page--; SetButtons(); Render(); } };
            btnNext = MakeButton("›", 34);
            btnNext.Left = WinW - 12 - 34;
            btnNext.Click += delegate { if (!busy && page < PageCount) { page++; SetButtons(); Render(); } };
            btnPrev.Left = btnNext.Left - 8 - 34;
            lblPage = new Label { AutoSize = true, Text = "" };
            lblPage.Left = btnPrev.Left - 10 - TextRenderer.MeasureText("第 000 / 000 页", Font).Width;
            lblPage.Top = ty + 5;
            lblCount = new Label { AutoSize = true, ForeColor = Color.Gray, Text = "" };
            lblCount.Left = btnDel.Right + 14;
            lblCount.Top = ty + 5;
            Controls.Add(btnRefresh); Controls.Add(btnRead); Controls.Add(btnDel);
            Controls.Add(btnPrev); Controls.Add(btnNext); Controls.Add(lblPage); Controls.Add(lblCount);

            // ---- 列表：原生 ListView（经典 Win32 外观），多选做批量操作
            int ly = ty + 34;
            lv = new ListView
            {
                Left = 12,
                Top = ly,
                Width = WinW - 24,
                Height = 224,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false,
                Sorting = SortOrder.None,
            };
            // 无状态列：未读靠加粗字体标识；日期列按实测宽度固定，保证时间戳完整不出省略号；
            // 内容列吃掉剩余宽度，列总宽恰好铺满一行，不出横向滚动条
            int dateW = TextRenderer.MeasureText("2026-11-18 15:13:51", Font).Width + 16;
            int fromW = 140;
            lv.Columns.Add(L.T("sms.col.from"), fromW);
            lv.Columns.Add(L.T("sms.col.date"), dateW);
            lv.Columns.Add(L.T("sms.col.content"), lv.Width - fromW - dateW - 6 - SystemInformation.Border3DSize.Width * 2);
            lv.SelectedIndexChanged += delegate { ShowDetail(); UpdateBtnState(); };
            Controls.Add(lv);

            // ---- 详情区
            lblFrom = new Label { AutoSize = true, ForeColor = Color.Gray, Left = 12, Top = lv.Bottom + 8, Text = " " };
            Controls.Add(lblFrom);
            txtContent = new TextBox
            {
                Left = 12,
                Top = lblFrom.Bottom + 3,
                Width = WinW - 24,
                Height = 150,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                TabStop = false,
            };
            Controls.Add(txtContent);

            // ---- 底栏：左状态/错误，右关闭
            btnClose = MakeButton(L.T("sms.close"), 84);
            btnClose.Primary = true;
            btnClose.Left = WinW - 12 - btnClose.Width;
            btnClose.Top = WinH - FooterH + 10;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);
            lblStatus = new Label { AutoSize = true, Left = 12, Top = WinH - FooterH + 15, ForeColor = Color.Gray, Text = " " };
            Controls.Add(lblStatus);

            ResumeLayout(false);
        }

        WinButton MakeButton(string text, int minW)
        {
            var b = new WinButton { Text = text, Height = 26 };
            int tw = TextRenderer.MeasureText(text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.SingleLine).Width;
            b.Width = Math.Max(minW, tw + 26);
            b.Top = TitleH + 8;
            return b;
        }

        // ---------------------------------------------------------------- 数据加载
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Reload(1);
        }

        /// <summary>向设备拉取全部逐条短信（一次取全），完成后渲染当前页。</summary>
        void Reload(int p)
        {
            if (busy) return;
            busy = true;
            page = Math.Max(1, p);
            SetButtons();
            lblStatus.ForeColor = Color.Gray;
            lblStatus.Text = L.T("sms.loading");
            var t = new Thread(delegate()
            {
                string err = "";
                List<SmsItem> list = null;
                try { list = w.SmsQueryAll(ref err); }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke(new Action(delegate { Loaded(list, err); })); }
                catch (InvalidOperationException) { }      // 关窗竞态
            }) { IsBackground = true };
            t.Start();
        }

        void Loaded(List<SmsItem> list, string err)
        {
            busy = false;
            if (list == null)
            {
                lblStatus.Text = string.IsNullOrEmpty(err) ? L.T("v.fail") : err;
                lblStatus.ForeColor = Color.FromArgb(196, 43, 28);
                SetButtons();
                return;
            }
            all = list;
            if (page > PageCount) page = PageCount;
            lblStatus.ForeColor = Color.Gray;
            SetButtons();
            Render();
        }

        /// <summary>从缓存 all 切出当前页渲染；翻页不再访问设备。</summary>
        void Render()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            int start = (page - 1) * PageSize;
            for (int i = start; i < start + PageSize && i < all.Count; i++)
            {
                var it = all[i];
                var li = new ListViewItem(it.phone);
                li.SubItems.Add(it.date);
                li.SubItems.Add(OneLine(it.content));
                li.Tag = it;
                if (it.unread) li.Font = fUnread;      // 加粗即未读，无需状态列
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            lblStatus.Text = all.Count == 0 ? L.T("sms.empty") : " ";
            // 数量：未读 + 逐条短信实际总条数（与网页会话折叠无关，就是设备上的短信条数）
            int u, snapTotal;
            w.SmsCounts(out u, out snapTotal);
            lblCount.Text = string.Format(L.T("sms.count"), u, all.Count);
            lblPage.Text = string.Format(L.T("sms.page"), page, PageCount);
            ShowDetail();
        }

        static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        // ---------------------------------------------------------------- 详情
        void ShowDetail()
        {
            if (lv.SelectedItems.Count == 0) { lblFrom.Text = " "; txtContent.Text = ""; return; }
            var it = lv.SelectedItems[0].Tag as SmsItem;
            if (it == null) return;
            lblFrom.Text = it.phone + "   " + it.date;
            txtContent.Text = it.content;
            txtContent.SelectionStart = 0;
            txtContent.ScrollToCaret();
        }

        // ---------------------------------------------------------------- 批量操作
        List<string> SelectedIndexes(bool unreadOnly)
        {
            var idx = new List<string>();
            foreach (ListViewItem li in lv.SelectedItems)
            {
                var it = li.Tag as SmsItem;
                if (it == null) continue;
                if (unreadOnly && !it.unread) continue;
                idx.Add(it.index);
            }
            return idx;
        }

        void OpRead()
        {
            var idx = SelectedIndexes(true);
            if (idx.Count == 0) return;
            RunOp("read", idx);
        }

        void OpDelete()
        {
            var idx = SelectedIndexes(false);
            if (idx.Count == 0) return;
            if (MessageBox.Show(this, string.Format(L.T("sms.confirm.del"), idx.Count), L.T("sms.title"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            RunOp("del", idx);
        }

        void RunOp(string what, List<string> idx)
        {
            if (busy) return;
            busy = true;
            SetButtons();
            lblStatus.Text = L.T("sms.loading");
            lblStatus.ForeColor = Color.Gray;
            var t = new Thread(delegate()
            {
                string r;
                try { r = what == "read" ? w.SmsMarkRead(idx) : w.SmsDelete(idx); }
                catch (Exception ex) { r = ex.Message; }
                try { BeginInvoke(new Action(delegate { OpDone(what, idx.Count, r); })); }
                catch (InvalidOperationException) { }
            }) { IsBackground = true };
            t.Start();
        }

        void OpDone(string what, int n, string result)
        {
            busy = false;
            if (result != "ok")
            {
                lblStatus.Text = string.Format(L.T("sms.opfail"), n, result);
                lblStatus.ForeColor = Color.FromArgb(196, 43, 28);
                SetButtons();
                return;
            }
            // 删除/已读后从设备重新拉取；若当前页超出新的总页数，Loaded 会自动回夹。
            w.NotifyResample();           // 让悬浮窗立刻重采，刷新未读计数
            Reload(page);
        }

        void SetButtons()
        {
            btnRefresh.Enabled = !busy;
            btnPrev.Enabled = !busy && page > 1;
            btnNext.Enabled = !busy && page < PageCount;
            btnClose.Enabled = true;
            UpdateBtnState();
        }

        void UpdateBtnState()
        {
            bool hasUnread = false, hasAny = false;
            foreach (ListViewItem li in lv.SelectedItems)
            {
                var it = li.Tag as SmsItem;
                if (it == null) continue;
                hasAny = true;
                if (it.unread) hasUnread = true;
            }
            btnRead.Enabled = !busy && hasUnread;
            btnDel.Enabled = !busy && hasAny;
        }

        // ---------------------------------------------------------------- 外观 / DWM（与设置窗同款）
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using (var p = new Pen(Pal.WindowBorder))
                g.DrawLine(p, 0, WinH - FooterH, Width - 1, WinH - FooterH);   // 底栏分隔线
        }

        // 保留 WS_THICKFRAME 让 DWM 提供圆角/投影/玻璃描边，非客户区压成 0 自绘标题栏；
        // 并拦截 UAH/NC 重画，避免失活时系统补画 caption 造成鬼影边框。
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
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                int border = 0x00DADADA;
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (fUnread != null) fUnread.Dispose();
            base.OnFormClosed(e);
        }
    }
}
