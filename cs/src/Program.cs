using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

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
                    MessageBox.Show("悬浮窗已经在运行了（看系统托盘）", "华为随身 WiFi");
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
            }
        }
    }

    /// <summary>代码绘制托盘图标（4 根信号柱 + 小电池），不依赖图标资源文件，
    /// 配色与 python 版 make_icon 一致。</summary>
    public static class TrayIcon
    {
        public static Icon Make(int px = 32)
        {
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.None;
                using (var b = new SolidBrush(Color.FromArgb(0x16, 0x1b, 0x22)))
                    g.FillRectangle(b, 0, 0, px, px);
                int[] hs = { 5, 9, 14, 19 };                       // 左矮右高
                using (var b = new SolidBrush(Color.FromArgb(0x3f, 0xb9, 0x50)))
                    for (int i = 0; i < hs.Length; i++)
                        g.FillRectangle(b, 3 + i * 4, px - 4 - hs[i], 3, hs[i]);
                using (var b = new SolidBrush(Color.FromArgb(0x0d, 0x11, 0x17)))
                    g.FillRectangle(b, 19, 7, 9, 6);               // 电池外壳
                using (var b = new SolidBrush(Color.FromArgb(0x58, 0xa6, 0xff)))
                    g.FillRectangle(b, 20, 8, 6, 4);               // 电量
                using (var b = new SolidBrush(Color.FromArgb(0x8b, 0x94, 0x9e)))
                    g.FillRectangle(b, 28, 9, 1, 2);               // 电池头
            }
            IntPtr hicon = bmp.GetHicon();
            try { return Icon.FromHandle(hicon); }
            catch { return SystemIcons.Application; }
        }
    }
}
