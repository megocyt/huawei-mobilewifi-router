using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// 程序集版本不在这里硬编码：改由 build.ps1 依据仓库根 VERSION 文件生成
// cs/build/AssemblyInfo.cs 注入，保证 EXE 属性 / 托盘菜单显示 / README / git tag 同源。

namespace HwWidget
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "HWiFiWidget_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show(L.T("app.running"), L.T("tray.title"));
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
            }
        }
    }

    /// <summary>系统托盘 &amp; EXE 图标加载器：优先读取内嵌的 HwWidget.ico
    /// （Fluent 风格透明底信号弧，Windows 11 视觉规范），找不到时回退到代码绘制的
    /// 单色信号 glyph；不再画背景方块以贴合 Win11 任务栏透明风格。</summary>
    public static class TrayIcon
    {
        const string ResName = "HwWidget.ico";
        static Icon embedded;
        static bool loadTried;

        /// <summary>返回可直接用于 NotifyIcon.Icon / Form.Icon 的图标，
        /// 内嵌 ico 含 16/20/24/32/40/48/64/256 多档尺寸，
        /// Windows 会按 SM_CXSMICON 自动挑选。</summary>
        public static Icon Make(int px = 32)
        {
            var ico = LoadEmbedded();
            if (ico != null) return ico;
            return DrawFallback(px);
        }

        /// <summary>按目标像素从多尺寸 ico 中挑出对应 frame（16/24/32/48…）；
        /// 用于小尺寸通知区或大尺寸 About 面板显示。</summary>
        public static Icon MakeFor(int px)
        {
            var ico = LoadEmbedded();
            if (ico == null) return DrawFallback(px);
            try { return new Icon(ico, new Size(px, px)); }
            catch { return ico; }
        }

        static Icon LoadEmbedded()
        {
            if (loadTried) return embedded;
            loadTried = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream(ResName))
                {
                    if (s != null) embedded = new Icon(s);
                }
            }
            catch { embedded = null; }
            return embedded;
        }

        /// <summary>回退路径：透明底的三条信号弧 + 端点，配色对齐 logo
        /// （薄荷绿 #3CCB7F → 深青 #0EA5A0），不画背景方块。</summary>
        static Icon DrawFallback(int px)
        {
            var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float unit = px / 32f;
                float cx = px / 2f, cy = px * 0.72f;
                using (var pen = new Pen(Color.FromArgb(0x3C, 0xCB, 0x7F), 3f * unit))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, cx - 12f * unit, cy - 12f * unit, 24f * unit, 24f * unit, 220f, 100f);
                }
                using (var pen = new Pen(Color.FromArgb(0x28, 0xCB, 0x93), 3f * unit))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, cx - 8f * unit, cy - 8f * unit, 16f * unit, 16f * unit, 220f, 100f);
                }
                using (var pen = new Pen(Color.FromArgb(0x0E, 0xA5, 0xA0), 3f * unit))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, cx - 4f * unit, cy - 4f * unit, 8f * unit, 8f * unit, 220f, 100f);
                }
                using (var b = new SolidBrush(Color.FromArgb(0x0E, 0xA5, 0xA0)))
                    g.FillEllipse(b, cx - 1.6f * unit, cy - 1.6f * unit, 3.2f * unit, 3.2f * unit);
            }
            IntPtr hicon = bmp.GetHicon();
            try { return Icon.FromHandle(hicon); }
            catch { return SystemIcons.Application; }
        }
    }
}
