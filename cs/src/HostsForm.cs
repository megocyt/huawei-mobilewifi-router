using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HwWidget
{
    /// <summary>终端列表窗口：与短信窗/设置窗同族经典 Win32 浅色风格（自绘奶油标题栏 + DWM 圆角）。
    /// 双击悬浮窗终端行呼出。数据直接取悬浮窗已轮询的 snap.hosts，不再单独访问设备（协议单会话会顶号）。
    /// 在线终端排在前面，离线终端置灰。</summary>
    public class HostsForm : Form
    {
        const int WinW = 760, WinH = 470, TitleH = 32, FooterH = 46;

        readonly WidgetForm w;
        ListView lv;
        Label lblCount;
        WinButton btnRefresh, btnClose;
        Font fOnline, fOffline;
        static readonly Color Online = Color.FromArgb(37, 128, 66);
        static readonly Color Offline = Color.FromArgb(150, 155, 160);

        List<Host> lastList = new List<Host>();   // 上次拉取的终端，排序时本地重排不重访设备
        int lastActive, lastTotal;
        int sortCol = -1;                          // -1 = 默认（在线优先 + 名称）
        bool sortAsc = true;

        public HostsForm(WidgetForm owner)
        {
            w = owner;

            SuspendLayout();
            Text = L.T("hosts.title");
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
            fOnline = new Font(Font, FontStyle.Bold);
            fOffline = Font;

            var titleBar = new WinTitleBar { Left = 0, Top = 0, Width = WinW, Height = TitleH, Title = L.T("hosts.title") };
            try { titleBar.IconImage = TrayIcon.MakeFor(16).ToBitmap(); } catch { }
            titleBar.CloseClicked += delegate { Close(); };
            Controls.Add(titleBar);

            // ---- 工具行：刷新 …… 在线/总数
            int ty = TitleH + 8;
            btnRefresh = MakeButton(L.T("menu.refresh"), 76);
            btnRefresh.Left = 12;
            btnRefresh.Click += delegate { Reload(); };
            lblCount = new Label { AutoSize = true, ForeColor = Color.Gray, Top = ty + 5 };
            lblCount.Left = WinW - 12 - TextRenderer.MeasureText("在线 000 / 共 000 台", Font).Width;
            Controls.Add(btnRefresh); Controls.Add(lblCount);

            // ---- 列表：原生 ListView（经典 Win32 外观），只读展示
            int ly = ty + 34;
            lv = new ListView
            {
                Left = 12,
                Top = ly,
                Width = WinW - 24,
                Height = WinH - ly - FooterH - 10,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                Sorting = SortOrder.None,
            };
            // 列宽按内容自然给定，总宽超出可视区→横向滚动条（信息全部展开，不再靠加宽窗口消除滚动条）
            lv.Columns.Add(L.T("hosts.col.status"), 64);
            lv.Columns.Add(L.T("hosts.col.name"), 168);
            lv.Columns.Add(L.T("hosts.col.ip"), 124);
            lv.Columns.Add(L.T("hosts.col.ip6"), 236);
            lv.Columns.Add(L.T("hosts.col.mac"), 156);
            lv.Columns.Add(L.T("hosts.col.brand"), 104);
            lv.Columns.Add(L.T("hosts.col.band"), 78);
            lv.Columns.Add(L.T("hosts.col.iftype"), 84);
            lv.Columns.Add(L.T("hosts.col.addrsrc"), 92);
            lv.Columns.Add(L.T("hosts.col.ssid"), 132);
            lv.Columns.Add(L.T("hosts.col.rx"), 110);
            lv.Columns.Add(L.T("hosts.col.tx"), 110);
            lv.Columns.Add(L.T("hosts.col.onlinetime"), 110);
            lv.Columns.Add(L.T("hosts.col.lastaccess"), 160);
            lv.Columns.Add(L.T("hosts.col.guest"), 60);
            lv.Columns.Add(L.T("hosts.col.clientid"), 150);
            lv.ColumnClick += delegate(object s, ColumnClickEventArgs e) { OnColumn(e.Column); };
            Controls.Add(lv);

            // ---- 底栏：右关闭
            btnClose = MakeButton(L.T("sms.close"), 84);
            btnClose.Primary = true;
            btnClose.Left = WinW - 12 - btnClose.Width;
            btnClose.Top = WinH - FooterH + 10;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

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

        static string D(string s) { return string.IsNullOrEmpty(s) ? "-" : s; }

        string IfType(string t)
        {
            if (string.IsNullOrEmpty(t)) return "-";
            if (t.Equals("Wireless", StringComparison.OrdinalIgnoreCase)) return L.T("hosts.wireless");
            if (t.Equals("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("Wired", StringComparison.OrdinalIgnoreCase)) return L.T("hosts.ethernet");
            return t;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Reload();
        }

        /// <summary>从悬浮窗快照拉取终端，缓存后渲染。</summary>
        void Reload()
        {
            lastList = w.HostsList(out lastActive, out lastTotal);
            Render();
        }

        void OnColumn(int col)
        {
            if (col == sortCol) sortAsc = !sortAsc;
            else { sortCol = col; sortAsc = true; }
            Render();
        }

        /// <summary>按当前排序列本地重排并填充表格（不重访设备）。</summary>
        void Render()
        {
            var data = new List<Host>(lastList);
            data.Sort(delegate(Host a, Host b)
            {
                int r = sortCol < 0 ? DefaultCmp(a, b) : Cmp(a, b, sortCol);
                if (sortCol >= 0 && !sortAsc) r = -r;
                if (r == 0) r = DefaultCmp(a, b);        // 同值时稳定：在线优先 + 名称
                return r;
            });

            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var h in data)
            {
                var li = new ListViewItem(h.active ? L.T("hosts.online") : L.T("hosts.offline"));
                li.SubItems.Add(D(h.name));
                li.SubItems.Add(D(h.ip));
                li.SubItems.Add(D(h.ip6));
                li.SubItems.Add(D(h.mac));
                li.SubItems.Add(D(h.brand));
                li.SubItems.Add(D(h.band));
                li.SubItems.Add(IfType(h.ifType));
                li.SubItems.Add(D(h.addrSource));
                li.SubItems.Add(D(h.ssid));
                li.SubItems.Add(h.rxK > 0 ? Device.HumanBytes((double)h.rxK * 1024, 1) : "-");
                li.SubItems.Add(h.txK > 0 ? Device.HumanBytes((double)h.txK * 1024, 1) : "-");
                li.SubItems.Add(h.active && h.assocSec > 0 ? Device.HumanDur(h.assocSec) : "-");
                li.SubItems.Add(D(h.lastAccess));
                li.SubItems.Add(h.guest ? L.T("hosts.yes") : L.T("hosts.no"));
                li.SubItems.Add(D(h.vendorId));
                li.ForeColor = h.active ? Online : Offline;
                if (h.active) li.Font = fOnline;
                lv.Items.Add(li);
            }
            lv.EndUpdate();
            lblCount.Text = data.Count == 0 ? L.T("hosts.empty")
                            : string.Format(L.T("hosts.count"), lastActive, lastTotal);
            UpdateArrow();
        }

        static int DefaultCmp(Host a, Host b)
        {
            if (a.active != b.active) return a.active ? -1 : 1;
            return string.CompareOrdinal(a.name ?? "", b.name ?? "");
        }

        // 按列类型选比较方式：数值列按原始数值、日期文本按字典序（yyyy-MM-dd HH:mm:ss 与时间同序）、其余按文本。
        static int Cmp(Host a, Host b, int c)
        {
            switch (c)
            {
                case 0: return a.active.CompareTo(b.active);
                case 1: return string.CompareOrdinal(a.name ?? "", b.name ?? "");
                case 2: return string.CompareOrdinal(a.ip ?? "", b.ip ?? "");
                case 3: return string.CompareOrdinal(a.ip6 ?? "", b.ip6 ?? "");
                case 4: return string.CompareOrdinal(a.mac ?? "", b.mac ?? "");
                case 5: return string.CompareOrdinal(a.brand ?? "", b.brand ?? "");
                case 6: return string.CompareOrdinal(a.band ?? "", b.band ?? "");
                case 7: return string.CompareOrdinal(a.ifType ?? "", b.ifType ?? "");
                case 8: return string.CompareOrdinal(a.addrSource ?? "", b.addrSource ?? "");
                case 9: return string.CompareOrdinal(a.ssid ?? "", b.ssid ?? "");
                case 10: return a.rxK.CompareTo(b.rxK);
                case 11: return a.txK.CompareTo(b.txK);
                case 12: return a.assocSec.CompareTo(b.assocSec);
                case 13: return string.CompareOrdinal(a.lastAccess ?? "", b.lastAccess ?? "");
                case 14: return a.guest.CompareTo(b.guest);
                case 15: return string.CompareOrdinal(a.vendorId ?? "", b.vendorId ?? "");
            }
            return 0;
        }

        // ---------------------------------------------------------------- 表头排序箭头
        void UpdateArrow()
        {
            if (!lv.IsHandleCreated) return;
            for (int i = 0; i < lv.Columns.Count; i++) SetArrow(i, 0);   // 先清除
            if (sortCol >= 0) SetArrow(sortCol, sortAsc ? LVIS_SORTASCENT : LVIS_SORTDESCENT);
        }

        void SetArrow(int col, uint state)
        {
            var it = new LVITEM { mask = LVIF_STATE, iSubItem = col, state = state, stateMask = LVIS_SORTASCENT | LVIS_SORTDESCENT };
            SendMessage(lv.Handle, LVM_SETITEMHEADER, IntPtr.Zero, ref it);
        }

        const int LVM_FIRST = 0x1000, LVM_SETITEMHEADER = LVM_FIRST + 68;
        const uint LVIF_STATE = 0x0008, LVIS_SORTASCENT = 0x0001, LVIS_SORTDESCENT = 0x0002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct LVITEM
        {
            public uint mask; public int iItem; public int iSubItem;
            public uint state; public uint stateMask;
            public IntPtr pszText; public int cchTextMax; public int iImage; public IntPtr lParam;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, ref LVITEM lp);

        // ---------------------------------------------------------------- 外观 / DWM（与短信窗同款）
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using (var p = new Pen(Pal.WindowBorder))
                g.DrawLine(p, 0, WinH - FooterH, Width - 1, WinH - FooterH);   // 底栏分隔线
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00040000;   // WS_THICKFRAME：让 DWM 提供圆角/投影/玻璃描边
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
            if (fOnline != null) fOnline.Dispose();
            base.OnFormClosed(e);
        }

        protected override bool ShowWithoutActivation { get { return false; } }
    }
}
