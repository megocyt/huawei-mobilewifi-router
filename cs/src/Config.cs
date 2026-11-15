using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace HwWidget
{
    /// <summary>悬浮窗配置（CS 自含版）。存储于 %APPDATA%\HWiFiWidget\config.json，
    /// 仅保留与设备直连所需的字段；不再写入面板数据源相关旧键。
    /// Python 面板相关字段（source / panel_url）在读取时忽略。
    /// </summary>
    public class Cfg
    {
        public static readonly string[] RowKeys =
        {
            "conn", "model", "signal", "rate", "plan", "traffic", "hosts",
            "battery", "msisdn", "ip", "uptime", "sms", "clock",
        };
        static readonly string[] DefaultOn = { "conn", "model", "signal", "rate", "plan", "hosts", "battery", "msisdn" };

        public string host = "192.168.8.1";
        public string user = "admin";
        public string password = "";
        public string language = "zh-CN";                    // zh-CN | en | zh-TW | ja | ru | ko
        public double interval = 3.0;
        public double alpha = 0.88;
        public string fontFam = "Microsoft YaHei UI";
        public int fontSize = 12;
        public string fg = "#e6edf3";
        public string bg = "#0d1117";
        public string accent = "#3fb950";
        public string warn = "#d29922";
        public string bad = "#f85149";
        public Dictionary<string, bool> rows = new Dictionary<string, bool>();
        public bool topmost = true;
        public bool borderless = true;
        public bool locked = false;
        public bool clickThrough = false;                       // 穿透模式：鼠标点击落到下层窗口
        public bool showTitle = true;
        public bool savePassword = true;
        public int posX = int.MinValue, posY = int.MinValue;

        public Cfg()
        {
            foreach (string k in RowKeys)
            {
                bool on = false;
                foreach (string d in DefaultOn) if (d == k) { on = true; break; }
                rows[k] = on;
            }
        }

        public static string Dir
        {
            get
            {
                string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appdata, "HWiFiWidget");
            }
        }
        static string CfgPath { get { return Path.Combine(Dir, "config.json"); } }
        static string LogPath { get { return Path.Combine(Dir, "error.log"); } }

        public static void LogError(Exception e)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + e + "\n");
            }
            catch { }
        }

        // ---------------------------------------------------------------- 读取
        public static Cfg Load()
        {
            Cfg c = new Cfg();
            Dictionary<string, object> d = null;
            try
            {
                object parsed = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(CfgPath));
                d = parsed as Dictionary<string, object>;
            }
            catch { d = null; }
            if (d == null) return FallbackPassword(c);
            c.host = Str(d, "host", c.host);
            c.user = Str(d, "user", c.user);
            c.password = Str(d, "password", "");
            c.language = Str(d, "language", c.language);
            c.interval = Num(d, "interval", c.interval);
            c.alpha = Num(d, "alpha", c.alpha);
            c.fontFam = Str(d, "font_family", c.fontFam);
            c.fontSize = (int)Num(d, "font_size", c.fontSize);
            c.fg = Str(d, "fg", c.fg);
            c.bg = Str(d, "bg", c.bg);
            c.accent = Str(d, "accent", c.accent);
            c.warn = Str(d, "warn", c.warn);
            c.bad = Str(d, "bad", c.bad);
            c.topmost = Bool(d, "topmost", c.topmost);
            c.borderless = Bool(d, "borderless", c.borderless);
            c.locked = Bool(d, "locked", c.locked);
            c.clickThrough = Bool(d, "click_through", c.clickThrough);
            c.showTitle = Bool(d, "show_title", c.showTitle);
            c.savePassword = Bool(d, "save_password", c.savePassword);
            object rowsObj;
            if (d.TryGetValue("rows", out rowsObj))
            {
                var rd = rowsObj as Dictionary<string, object>;
                if (rd != null)
                    foreach (var kv in rd)
                        if (c.rows.ContainsKey(kv.Key)) c.rows[kv.Key] = Truthy(kv.Value);
            }
            object pos;
            if (d.TryGetValue("pos", out pos))
            {
                var arr = pos as object[];
                if (arr != null && arr.Length >= 2) { c.posX = (int)Num(arr, 0, 0); c.posY = (int)Num(arr, 1, 0); }
            }
            return FallbackPassword(c);
        }

        static Cfg FallbackPassword(Cfg c)
        {
            if (string.IsNullOrEmpty(c.password))
                c.password = Environment.GetEnvironmentVariable("HWIFI_PASSWORD") ?? "";
            if (!IsKnownLang(c.language)) c.language = "zh-CN";
            L.Lang = c.language;
            return c;
        }

        static bool IsKnownLang(string code)
        {
            for (int i = 0; i < L.Options.Length; i++)
                if (L.Options[i][1] == code) return true;
            return false;
        }

        // ---------------------------------------------------------------- 写入
        public void Save()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            Put(sb, "host", host); Put(sb, "user", user);
            Put(sb, "password", savePassword ? password : "");
            Put(sb, "language", language);
            PutNum(sb, "interval", interval); PutNum(sb, "alpha", Math.Round(alpha, 2));
            Put(sb, "font_family", fontFam); PutNum(sb, "font_size", fontSize);
            Put(sb, "fg", fg); Put(sb, "bg", bg); Put(sb, "accent", accent);
            Put(sb, "warn", warn); Put(sb, "bad", bad);
            sb.Append(" \"rows\": {");
            bool first = true;
            foreach (string k in RowKeys)
            {
                sb.Append(first ? "" : ","); first = false;
                sb.Append('"').Append(k).Append("\": ").Append(rows[k] ? "true" : "false");
            }
            sb.Append("},\n");
            PutBool(sb, "topmost", topmost); PutBool(sb, "borderless", borderless);
            PutBool(sb, "locked", locked); PutBool(sb, "click_through", clickThrough);
            PutBool(sb, "show_title", showTitle);
            PutBool(sb, "save_password", savePassword);
            if (posX == int.MinValue) sb.Append(" \"pos\": null\n");
            else sb.Append(" \"pos\": [").Append(posX).Append(", ").Append(posY).Append("]\n");
            sb.Append("}\n");
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(CfgPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { LogError(e); }
        }

        static void Put(StringBuilder sb, string k, string v)
        { sb.Append(" \"").Append(k).Append("\": \"").Append(appendEsc(v)).Append("\",\n"); }
        static void PutNum(StringBuilder sb, string k, double v)
        { sb.Append(" \"").Append(k).Append("\": ").Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append(",\n"); }
        static void PutBool(StringBuilder sb, string k, bool v)
        { sb.Append(" \"").Append(k).Append("\": ").Append(v ? "true" : "false").Append(",\n"); }

        static string appendEsc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 取值小工具
        static bool Truthy(object v)
        {
            if (v is bool) return (bool)v;
            if (v is string) return (string)v == "true";
            if (v is int) return (int)v != 0;
            if (v is long) return (long)v != 0;
            return v != null;
        }

        static string Str(Dictionary<string, object> d, string k, string def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            return v is string ? (string)v : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static double Num(Dictionary<string, object> d, string k, double def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            return Num(new object[] { v }, 0, def);
        }

        static double Num(object[] a, int i, double def)
        {
            try
            {
                object v = a[i];
                if (v is int) return (int)v;
                if (v is long) return (long)v;
                if (v is double) return (double)v;
                return double.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            catch { return def; }
        }

        static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            if (!d.TryGetValue(k, out v)) return def;
            return Truthy(v);
        }
    }
}
