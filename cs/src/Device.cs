using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace HwWidget
{
    /// <summary>一次采样的结果，界面只认这个结构，不关心数据来自设备还是本机面板。</summary>
    public class Snap
    {
        public bool ok;
        public string err = "";
        public bool loggedIn;
        public long ts;

        public string net = "-";          // LTE / 5G NR
        public string conn = "";
        public bool connected;
        public string carrier = "";
        public int sigBars, sigMax = 5;
        public string rsrp = "-", sinr = "-", rsrq = "-";
        public string rsrpLevel = "-";
        public long rateDown, rateUp;
        public long dayUsed, monthDown, monthUp;
        public long planLimit, planUsed, planRemain;
        public double planPercent = -1, planDayPercent = -1;
        public string planLimitRaw = "-", planStartDay = "1", planThreshold = "90";
        public int planDaysLeft = -1;
        public bool planOverThreshold, planOverLimit;
        public int hostsActive, hostsTotal;
        public int smsUnread, smsInbox;
        public string battery = "-";
        public bool charging;
        public string wanIp = "-";
        public string wanIpv6 = "-";
        public string msisdn = "-";
        public long uptime;
        public string model = "-";
        public List<Host> hosts = new List<Host>();
    }

    public class Host
    {
        public string name = "", mac = "", ip = "", band = "", brand = "";
        public bool active;
    }

    public class SmsItem
    {
        public string index = "", phone = "", date = "", content = "";
        public bool unread;
    }

    /// <summary>华为随身 WiFi WebUI 客户端：SCRAM 登录 + XML 请求体 + 一次性 CSRF token。
    /// 协议细节与 hw_status.py 完全一致（实测可用）。单独运行时直接访问设备管理页。</summary>
    public class Device : IDisposable
    {
        public string baseUrl = "http://192.168.8.1";
        public string user = "admin";
        public string password = "";

        CookieJar cookies = new CookieJar();
        string token;
        bool loggedIn;
        long blockedUntil;
        int fails;
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------- 低层 HTTP
        class CookieJar
        {
            readonly Dictionary<string, string> map = new Dictionary<string, string>();
            public string Header
            {
                get
                {
                    var sb = new StringBuilder();
                    foreach (var kv in map)
                        sb.Append(sb.Length > 0 ? "; " : "").Append(kv.Key).Append('=').Append(kv.Value);
                    return sb.ToString();
                }
            }
            public void Store(string setCookie)
            {
                if (string.IsNullOrEmpty(setCookie)) return;
                string pair = setCookie.Split(';')[0];
                int i = pair.IndexOf('=');
                if (i <= 0) return;
                map[pair.Substring(0, i).Trim()] = pair.Substring(i + 1).Trim();
            }
            public void Clear() { map.Clear(); }
        }

        class Reply
        {
            public int status;
            public string body = "";
        }

        Reply Request(string path, string xmlBody, bool sendToken)
        {
            var rep = new Reply();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + path);
                req.Method = xmlBody == null ? "GET" : "POST";
                req.Timeout = 8000;
                req.UserAgent = "Mozilla/5.0 (hw-widget)";
                req.Accept = "application/json, text/javascript, */*; q=0.01";
                req.Headers["X-Requested-With"] = "XMLHttpRequest";
                req.Headers["_ResponseSource"] = "Broswer";
                if (path.IndexOf("monitoring/status") >= 0) req.Headers["Update-Cookie"] = "UpdateCookie";
                string ck = cookies.Header;
                if (ck.Length > 0) req.Headers[HttpRequestHeader.Cookie] = ck;
                if (sendToken && !string.IsNullOrEmpty(token))
                    req.Headers["__RequestVerificationToken"] = token;
                if (xmlBody != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(xmlBody);
                    req.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                    req.ContentLength = data.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    rep.status = (int)resp.StatusCode;
                    cookies.Store(resp.Headers[HttpResponseHeader.SetCookie]);
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        rep.body = sr.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                var hr = we.Response as HttpWebResponse;
                if (hr != null)
                {
                    rep.status = (int)hr.StatusCode;
                    cookies.Store(hr.Headers[HttpResponseHeader.SetCookie]);
                    using (var sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8))
                        rep.body = sr.ReadToEnd();
                }
                else
                {
                    rep.status = -1;
                    rep.body = we.Message;
                }
            }
            catch (Exception e)
            {
                rep.status = -2;
                rep.body = e.Message;
            }
            return rep;
        }

        static Dictionary<string, object> ParseXml(string text)
        {
            var doc = new XmlDocument();
            try { doc.LoadXml(text); }
            catch (XmlException) { return new Dictionary<string, object>(); }
            return NodeToDict(doc.DocumentElement);
        }

        static Dictionary<string, object> NodeToDict(XmlElement node)
        {
            var outDict = new Dictionary<string, object>();
            if (node == null) return outDict;
            foreach (XmlNode child in node.ChildNodes)
            {
                var el = child as XmlElement;
                if (el == null) continue;
                object val = el.HasChildNodes && el.FirstChild is XmlElement
                    ? (object)NodeToDict(el)
                    : el.InnerText;
                if (outDict.ContainsKey(el.Name))
                {
                    object old = outDict[el.Name];
                    var list = old as List<object>;
                    if (list == null) { list = new List<object>(); list.Add(old); outDict[el.Name] = list; }
                    list.Add(val);
                }
                else outDict[el.Name] = val;
            }
            return outDict;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return "";
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static long Lng(Dictionary<string, object> d, string key)
        {
            long v;
            return long.TryParse(Str(d, key).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v)
                ? v : 0;
        }

        static List<object> Arr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return new List<object>();
            var list = v as List<object>;
            if (list != null) return list;
            var arr = v as object[];
            if (arr != null) return new List<object>(arr);
            return new List<object> { v };
        }

        /// <summary>取一次性 token：/api/webserver/token 返回值的第 33 位起 32 字符。</summary>
        bool RefreshToken()
        {
            var r = Request("/api/webserver/token?_ResponseSource=Broswer", null, false);
            var d = ParseXml(r.body);
            string tok = Str(d, "token");
            if (tok.Length < 33) return false;
            token = tok.Substring(32);
            return true;
        }

        static readonly HashSet<string> SessionDead = new HashSet<string>
            { "100003", "125002", "125003" };

        Dictionary<string, object> Get(string path)
        {
            var r = Request(path + (path.IndexOf('?') >= 0 ? "&" : "?") + "_ResponseSource=Broswer", null, false);
            if (r.status < 0) lastNetErr = r.body;   // 传输层失败（拒绝/超时/DNS），body 即异常消息
            var d = ParseAny(r.body);
            string code = Str(d, "code");
            if (!string.IsNullOrEmpty(code))
            {
                var err = new Dictionary<string, object>(d);
                err["__code"] = code;
                // 只有会话类错误才重登；100002（接口不支持）之类不能触发重登，
                // 否则会反复登录踩到 108005 限流（实测持续十几分钟）
                if (SessionDead.Contains(code)) loggedIn = false;
                return err;
            }
            return d;
        }

        /// <summary>设备一部分接口回 XML，/api/system/HostInfo 回 JSON，这里统一转成同构字典。</summary>
        static Dictionary<string, object> ParseAny(string body)
        {
            if (string.IsNullOrEmpty(body)) return new Dictionary<string, object>();
            string t = body.TrimStart();
            if (t.StartsWith("[") || t.StartsWith("{"))
            {
                var wrap = new Dictionary<string, object>();
                try
                {
                    var ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = 8 * 1024 * 1024;
                    object parsed = ser.DeserializeObject(t);
                    var arr = parsed as object[];
                    var dict = parsed as Dictionary<string, object>;
                    if (arr != null) { wrap["__list"] = arr; return wrap; }
                    if (dict != null) return dict;
                    return wrap;
                }
                catch (ArgumentException) { return wrap; }
            }
            return ParseXml(body);
        }

        Dictionary<string, object> Post(string path, Dictionary<string, object> body)
        {
            return PostInner(path, body, true);
        }

        Dictionary<string, object> PostInner(string path, Dictionary<string, object> body, bool retry)
        {
            if (string.IsNullOrEmpty(token) && !RefreshToken()) return Err(L.T("err.csrf"));
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><request>" + ToXml(body) + "</request>";
            var r = Request(path, xml, true);
            token = null;                                     // 一次性，用完即废
            var d = ParseAny(r.body);
            string code = Str(d, "code");
            if (code.Length == 0) return d;
            if (retry && (code == "100006" || code == "100003"))
            {
                RefreshToken();
                return PostInner(path, body, false);
            }
            if (SessionDead.Contains(code)) loggedIn = false;
            var e = new Dictionary<string, object>(d);
            e["__code"] = code;
            return e;
        }

        static string ToXml(Dictionary<string, object> d)
        {
            var sb = new StringBuilder();
            foreach (var kv in d)
            {
                var sub = kv.Value as Dictionary<string, object>;
                var list = kv.Value as List<object>;
                var arr = kv.Value as object[];
                if (sub != null) sb.Append("<" + kv.Key + ">" + ToXml(sub) + "</" + kv.Key + ">");
                else if (list != null) foreach (var o in list) sb.Append("<" + kv.Key + ">" + Fmt(o) + "</" + kv.Key + ">");
                else if (arr != null) foreach (var o in arr) sb.Append("<" + kv.Key + ">" + Fmt(o) + "</" + kv.Key + ">");
                else sb.Append("<" + kv.Key + ">" + Fmt(kv.Value) + "</" + kv.Key + ">");
            }
            return sb.ToString();
        }

        static string Fmt(object v)
        {
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static Dictionary<string, object> Err(string msg)
        {
            var d = new Dictionary<string, object>();
            d["__code"] = "local";
            d["__msg"] = msg;
            return d;
        }

        static string Fail(Dictionary<string, object> d)
        {
            string code = Str(d, "__code");
            if (code.Length == 0) return null;
            string msg = Str(d, "__msg");
            if (msg.Length == 0) msg = DescribeCode(code);
            return msg + "（" + code + "）";
        }

        public static string DescribeCode(string code)
        {
            switch (code)
            {
                case "100002": return L.T("e.100002");
                case "100003": return L.T("e.100003");
                case "100005": return L.T("e.100005");
                case "100006": return L.T("e.100006");
                case "108005": return L.T("e.108005");
                case "108006": return L.T("e.108006");
                case "113055": return L.T("e.113055");
                case "125001": return L.T("e.125001");
                case "125002": return L.T("e.125002");
                case "125003": return L.T("e.125003");
                default: return L.T("e.other");
            }
        }

        // ---------------------------------------------------------------- SCRAM 登录
        static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static byte[] HexDecode(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        static byte[] Hmac(byte[] key, byte[] msg)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(msg);
        }

        static byte[] Sha256(byte[] b)
        {
            using (var h = SHA256.Create()) return h.ComputeHash(b);
        }

        /// <summary>与设备自带 emui-crypto.js 等价：CryptoJS.HmacSHA256(message, key) 的参数
        /// 顺序与 RFC 5802 相反，所以这里 HMAC 的 key/msg 都要按“反”的传。</summary>
        static void Scram(string pwd, string saltHex, int iterations, string first, string server,
                          out string proof, out string signature)
        {
            byte[] salt = HexDecode(saltHex);
            byte[] sp;
            using (var pbkdf = new Rfc2898DeriveBytes(pwd, salt, iterations, HashAlgorithmName.SHA256))
                sp = pbkdf.GetBytes(32);
            byte[] auth = Encoding.UTF8.GetBytes(first + "," + server + "," + server);
            byte[] clientKey = Hmac(Encoding.UTF8.GetBytes("Client Key"), sp);
            byte[] storedKey = Sha256(clientKey);
            byte[] clientSig = Hmac(auth, storedKey);
            byte[] p = new byte[clientKey.Length];
            for (int i = 0; i < p.Length; i++) p[i] = (byte)(clientKey[i] ^ clientSig[i]);
            byte[] serverKey = Hmac(Encoding.UTF8.GetBytes("Server Key"), sp);
            byte[] s = Hmac(auth, serverKey);
            proof = Hex(p);
            signature = Hex(s);
        }

        // 与 python 版对齐：只有设备侧的限流/凭证类错误码才拉长退避，
        // 网络抖动或一次性异常不冲击退避窗口（对应 login() 里的 e.code 判定）。
        static readonly HashSet<string> RateLimitCodes = new HashSet<string>
            { "108003", "108005", "108006", "125001" };
        // 会话位被网页后台/上一次 logout 作废时，重登会被回这两个码，
        // 需要清 cookie 重开页面拿新 SessionID 再试一次。
        static readonly HashSet<string> DeadSessionCodes = new HashSet<string>
            { "125002", "125003" };

        public bool Login(bool force)
        {
            if (loggedIn && !force) return true;
            if (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond < blockedUntil) return false;
            try
            {
                if (cookies.Header.Length == 0)
                    Request("/", null, false);                       // 拿 SessionID cookie
                var st = Get("/api/user/state-login");
                if (Str(st, "State") == "0" && !string.IsNullOrEmpty(Str(st, "userlevel")) && !force)
                {
                    loggedIn = true;
                    return true;
                }
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    string code, why;
                    if (TryScramLogin(out code, out why))
                    {
                        loggedIn = true;
                        fails = 0;
                        errText = "";
                        return true;
                    }
                    // 会话死：清 cookie + 重新拿 SessionID，再走一次 SCRAM
                    if (attempt == 1 && DeadSessionCodes.Contains(code))
                    {
                        ForgetSession();
                        Request("/", null, false);
                        continue;
                    }
                    Block(why, code);
                    return false;
                }
                return false;
            }
            catch (Exception e)
            {
                Block(e.Message, "");
                return false;
            }
        }

        /// <summary>跑一遍 challenge_login → SCRAM → authentication_login。
        /// 成功返回 true；失败时把设备错误码写入 <paramref name="code"/>、
        /// 展示文案写入 <paramref name="why"/>（本地/网络类失败 code 为 "local" 或 ""）。</summary>
        bool TryScramLogin(out string code, out string why)
        {
            code = null; why = null;
            if (!RefreshToken()) { code = "local"; why = L.T("err.token"); return false; }
            string first = NewNonce();
            var ch = Post("/api/user/challenge_login", Dict(
                "username", user, "firstnonce", first, "mode", 1));
            code = Str(ch, "__code");
            if (code.Length > 0) { why = Fail(ch); return false; }
            string salt = Str(ch, "salt"), serverNonce = Str(ch, "servernonce");
            int iter;
            if (!int.TryParse(Str(ch, "iterations"), out iter) || salt.Length < 8 || serverNonce.Length < 8)
            {
                code = "local"; why = L.T("err.challenge");
                return false;
            }
            string proof, expect;
            Scram(password, salt, iter, first, serverNonce, out proof, out expect);
            if (!RefreshToken()) { code = "local"; why = L.T("err.token"); return false; }
            var au = Post("/api/user/authentication_login", Dict("clientproof", proof, "finalnonce", serverNonce));
            code = Str(au, "__code");
            if (code.Length > 0) { why = Fail(au); return false; }
            string gotSig = Str(au, "serversignature");
            if (gotSig.Length > 0 && !string.Equals(gotSig, expect, StringComparison.OrdinalIgnoreCase))
            {
                code = "local"; why = L.T("err.signature");
                return false;
            }
            return true;
        }

        void Block(string why, string code)
        {
            loggedIn = false;
            errText = why;
            // 与 python 版一致：30s → 60s → 120s → 240s → 480s → 900s，首次 30s。
            // 且只在设备限流类错误码上递增退避，网络抖动/单次异常不进入冷却。
            if (RateLimitCodes.Contains(code))
            {
                fails++;
                long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                blockedUntil = now + Math.Min(900000L, 30000L * (1L << Math.Min(5, fails - 1)));
            }
        }

        void FillHosts(Snap s, Dictionary<string, object> d)
        {
            var arr = d == null ? null : d.ContainsKey("__list") ? d["__list"] as object[] : null;
            if (arr == null) return;
            foreach (object o in arr)
            {
                var h = o as Dictionary<string, object>;
                if (h == null) continue;
                var host = new Host();
                host.name = Str(h, "ActualName");
                if (string.IsNullOrEmpty(host.name)) host.name = Str(h, "HostName");
                host.mac = Str(h, "MACAddress");
                host.ip = Str(h, "IPAddress");
                host.band = Str(h, "Frequency");
                host.brand = Str(h, "DevBrands");
                if (string.IsNullOrEmpty(host.brand)) host.brand = Str(h, "VendorClassID");
                host.active = IsActive(h, "Active") || IsActive(h, "WlanActive");
                s.hosts.Add(host);
                s.hostsTotal++;
                if (host.active) s.hostsActive++;
            }
        }

        /// <summary>设备 JSON 返回的 Active / WlanActive 可能是 bool 型 true/false 或数字 1/0，
        /// JavaScriptSerializer 把 true 解析为 System.Boolean，Convert.ToString 得 "True"。</summary>
        static bool IsActive(Dictionary<string, object> h, string key)
        {
            object v;
            if (h == null || !h.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).ToLowerInvariant();
            return s == "true" || s == "1";
        }

        public string errText = "";
        string lastNetErr = "";
        public int WaitSeconds
        {
            get
            {
                long ms = blockedUntil - DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                return ms <= 0 ? 0 : (int)(ms / 1000);
            }
        }

        static string NewNonce()
        {
            var b = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return Hex(b);
        }

        static Dictionary<string, object> Dict(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[Convert.ToString(kv[i], CultureInfo.InvariantCulture)] = kv[i + 1];
            return d;
        }

        public void ForgetSession()
        {
            cookies.Clear();
            token = null;
            loggedIn = false;
        }

        public void Logout()
        {
            if (!loggedIn) return;
            try
            {
                RefreshToken();
                Post("/api/user/logout", Dict("Logout", 1));
            }
            catch { }
            ForgetSession();
        }

        // ---------------------------------------------------------------- 业务读取
        static readonly string[][] NET_EX = new[]
        {
            new[]{"0","无服务"}, new[]{"1","GSM"}, new[]{"2","GPRS"}, new[]{"3","EDGE"},
            new[]{"41","WCDMA"}, new[]{"42","HSDPA"}, new[]{"43","HSUPA"}, new[]{"44","HSPA"},
            new[]{"45","HSPA+"}, new[]{"46","DC-HSPA+"}, new[]{"61","TD-SCDMA"},
            new[]{"101","LTE"}, new[]{"1011","LTE-A"}, new[]{"111","5G NR"},
        };
        static readonly string[][] NET_T = new[]
        {
            new[]{"1","GSM"}, new[]{"4","WCDMA"}, new[]{"7","HSPA"}, new[]{"8","TD-SCDMA"},
            new[]{"9","HSPA+"}, new[]{"19","LTE"}, new[]{"20","LTE/5G"},
        };

        static string Map(string[][] tbl, string key)
        {
            foreach (var row in tbl) if (row[0] == key) return row[1];
            return key;
        }

        public Snap Sample()
        {
            var s = new Snap();
            s.ts = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            lastNetErr = "";
            if (!Login(false))
            {
                s.loggedIn = false;
                s.err = string.IsNullOrEmpty(errText) ? L.T("err.login") : errText;
                if (WaitSeconds > 0) s.err += string.Format(L.T("fmt.retry"), WaitSeconds);
            }
            var status = Get("/api/monitoring/status");
            var traffic = Get("/api/monitoring/traffic-statistics");
            var signal = Get("/api/device/signal");
            var hosts = Get("/api/system/HostInfo");
            FillCore(s, status, traffic, signal);
            // 登录态有缓存：断网时 Login 仍返回 true，核心接口全空代表传输层失败，
            // 零值不是真实数据，必须标 ok=false 让界面走占位分支
            if ((status == null || status.Count == 0) && (traffic == null || traffic.Count == 0) && (signal == null || signal.Count == 0))
            {
                s.ok = false;
                if (s.err.Length == 0) s.err = lastNetErr.Length > 0 ? lastNetErr : L.T("err.net");
            }
            else if (s.err.Length == 0) s.ok = true;
            FillHosts(s, hosts);
            var plmn = Get("/api/net/current-plmn");
            s.carrier = Str(plmn, "ShortName");
            var month = Get("/api/monitoring/month_statistics");
            var limit = Get("/api/monitoring/start_date");
            s.monthDown = Lng(month, "CurrentMonthDownload");
            s.monthUp = Lng(month, "CurrentMonthUpload");
            s.dayUsed = Lng(month, "CurrentDayUsed");
            FillPlan(s, limit, month);
            var sms = Get("/api/sms/sms-count");
            s.smsUnread = (int)Lng(sms, "LocalUnread");
            s.smsInbox = (int)Lng(sms, "LocalInbox");
            var dev = Get("/api/device/information");
            s.wanIp = Str(dev, "WanIPAddress");
            s.wanIpv6 = Str(dev, "WanIPv6Address");
            s.msisdn = Str(dev, "Msisdn");
            s.uptime = Lng(dev, "uptime");
            s.model = Str(dev, "DeviceName");
            if (s.loggedIn && (s.rsrp == "-" || s.hostsTotal == 0))
            {
                // 会话被网页后台顶掉：下次采样重新登录
                loggedIn = false;
            }
            return s;
        }

        static Dictionary<string, object> Sub(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return new Dictionary<string, object>();
            var m = v as Dictionary<string, object>;
            return m ?? new Dictionary<string, object>();
        }

        void FillCore(Snap s, Dictionary<string, object> status, Dictionary<string, object> traffic,
                      Dictionary<string, object> signal)
        {
            s.connected = Str(status, "ConnectionStatus") == "901";
            s.conn = s.connected ? L.T("v.connected") : L.T("v.disconnected");
            s.net = Map(NET_EX, Str(status, "CurrentNetworkTypeEx"));
            if (s.net == Str(status, "CurrentNetworkTypeEx")) s.net = Map(NET_T, Str(status, "CurrentNetworkType"));
            int bars, mx;
            s.sigBars = int.TryParse(Str(status, "SignalIcon"), out bars) ? bars : 0;
            s.sigMax = int.TryParse(Str(status, "maxsignal"), out mx) ? mx : 5;
            s.rsrp = Num(signal, "rsrp");
            s.sinr = Num(signal, "sinr");
            s.rsrq = Num(signal, "rsrq");
            double rv;
            s.rsrpLevel = Level(double.TryParse(s.rsrp, NumberStyles.Any,
                                               CultureInfo.InvariantCulture, out rv) ? (double?)rv : null);
            s.rateDown = Lng(traffic, "CurrentDownloadRate");
            s.rateUp = Lng(traffic, "CurrentUploadRate");
            string bp = Str(status, "BatteryPercent");
            s.battery = bp.Length > 0 ? bp + "%" : "-";
            s.charging = Str(status, "BatteryStatus") == "1";
        }

        static string Num(Dictionary<string, object> d, string key)
        {
            string raw = Str(d, key);
            var sb = new StringBuilder();
            foreach (char ch in raw)
                if (char.IsDigit(ch) || ch == '-' || ch == '.') sb.Append(ch);
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        static string Level(double? rsrp)
        {
            if (rsrp == null) return "-";
            double v = rsrp.Value;
            if (v >= -80) return "优";
            if (v >= -90) return "良";
            if (v >= -100) return "中";
            if (v >= -110) return "差";
            return "极差";
        }

        static void FillPlan(Snap s, Dictionary<string, object> limit, Dictionary<string, object> month)
        {
            s.planStartDay = Str(limit, "StartDay");
            s.planThreshold = Str(limit, "MonthThreshold");
            s.planLimitRaw = Str(limit, "DataLimit");
            s.planLimit = ParseSize(s.planLimitRaw);
            s.planUsed = s.monthDown + s.monthUp;
            if (s.planLimit > 0)
            {
                s.planRemain = Math.Max(0, s.planLimit - s.planUsed);
                s.planPercent = Math.Round(s.planUsed * 100.0 / s.planLimit, 1);
                double thr;
                s.planOverThreshold = double.TryParse(s.planThreshold, out thr) && s.planPercent >= thr;
                s.planOverLimit = s.planPercent >= 100;
            }
            try
            {
                var now = DateTime.Now;
                int start = Math.Max(1, (int)Lng(limit, "StartDay"));
                int dim = DateTime.DaysInMonth(now.Year, now.Month);
                int cur = now.Day >= start ? now.Day : now.Day + dim;
                s.planDayPercent = Math.Round(Math.Min(100.0, cur * 100.0 / dim), 1);
                s.planDaysLeft = Math.Max(0, dim - cur);
            }
            catch { }
        }

        static readonly string[] UNITS = { "B", "KB", "MB", "GB", "TB", "PB" };

        public static long ParseSize(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var sb = new StringBuilder();
            foreach (char ch in text)
                if (char.IsDigit(ch) || ch == '.') sb.Append(ch);
            double num;
            if (!double.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out num)) return 0;
            string up = text.ToUpperInvariant();
            long mul = 1;
            for (int i = UNITS.Length - 1; i >= 0; i--)
                if (up.IndexOf(UNITS[i], StringComparison.Ordinal) >= 0) { mul = (long)Math.Pow(1024, i); break; }
            return (long)(num * mul);
        }

        public static string HumanSize(double bytes, int digits)
        {
            bool neg = bytes < 0;
            double v = Math.Abs(bytes);
            int i = 0;
            while (v >= 1024 && i < UNITS.Length - 1) { v /= 1024; i++; }
            return string.Format(CultureInfo.InvariantCulture, "{0}{1:0.###}", neg ? "-" : "", v);
        }

        /// <summary>带单位的字节数，换算规则与 python 版 human_bytes 一致。</summary>
        public static string HumanBytes(double n, int digits)
        {
            if (double.IsNaN(n) || double.IsInfinity(n)) return "-";
            string neg = n < 0 ? "-" : "";
            double v = Math.Abs(n);
            for (int i = 0; i < UNITS.Length; i++)
            {
                if (v < 1024 || i == UNITS.Length - 1)
                {
                    int dg = i == 0 ? 0 : digits;
                    return neg + Math.Round(v, dg).ToString("0.####", CultureInfo.InvariantCulture)
                           + " " + UNITS[i];
                }
                v /= 1024;
            }
            return "-";
        }

        public static string HumanRate(double bps)
        {
            return HumanBytes(Math.Abs(bps), 1) + "/s";
        }

        public static string HumanDur(long sec)
        {
            if (sec < 0) return "-";
            long d = sec / 86400, h = sec % 86400 / 3600, m = sec % 3600 / 60;
            string ud = L.T("u.day"), uh = L.T("u.hour"), um = L.T("u.minute"), us = L.T("u.second");
            string sp = L.Lang == "zh-CN" || L.Lang == "zh-TW" ? "" : " ";
            if (d > 0) return d + ud + sp + h + uh;
            if (h > 0) return h + uh + sp + m + um;
            return m + um + sp + (sec % 60) + us;
        }

        // ---------------------------------------------------------------- 短信 / 操作
        public List<SmsItem> Sms(int page, int size, out string error)
        {
            error = null;
            if (!Login(false)) { error = errText; return new List<SmsItem>(); }
            var r = Post("/api/sms/sms-list-contact", Dict("pageindex", page, "readcount", size));
            string fail = Fail(r);
            if (fail != null) { error = fail; return new List<SmsItem>(); }
            var list = new List<SmsItem>();
            var msgs = Sub(r, "messages");
            foreach (var o in Arr(msgs, "message"))
            {
                var m = o as Dictionary<string, object>;
                if (m == null) continue;
                var item = new SmsItem();
                item.index = Str(m, "index");
                item.phone = Str(m, "phone");
                item.date = Str(m, "date");
                item.content = Str(m, "content").Replace("&#x0D;", "").Replace("\r", "").Trim();
                item.unread = Str(m, "smstat") == "0";
                list.Add(item);
            }
            list.Sort((a, b) => string.CompareOrdinal(b.date, a.date));
            return list;
        }

        public string MarkRead(List<string> indexes) { return SmsAction("/api/sms/set-read", indexes); }
        public string DeleteSms(List<string> indexes) { return SmsAction("/api/sms/delete-sms", indexes); }

        /// <summary>短信总数（LocalInbox），供短信窗算真实总页数。</summary>
        public int SmsInboxTotal()
        {
            var d = Get("/api/sms/sms-count");
            return (int)Lng(d, "LocalInbox");
        }

        string SmsAction(string path, List<string> indexes)
        {
            if (!Login(false)) return errText;
            var list = new List<object>();
            foreach (var i in indexes) list.Add(i);
            var d = Post(path, Dict("Index", list));
            string fail = Fail(d);
            return fail ?? "ok";
        }

        public string Reboot() { return Act("/api/device/control", Dict("Control", 1), L.T("err.reboot")); }
        public string ClearTraffic() { return Act("/api/monitoring/clear-traffic", Dict("ClearTraffic", 1), L.T("err.cleared")); }
        public string SetDataSwitch(bool on) { return Act("/api/dialup/mobile-dataswitch", Dict("dataswitch", on ? "1" : "0"), on ? L.T("err.don") : L.T("err.doff")); }
        public bool DataOn()
        {
            var d = Get("/api/dialup/mobile-dataswitch");
            return Str(d, "dataswitch") == "1";
        }

        public string SetPlan(string limit, string startDay, string threshold)
        {
            var cur = Get("/api/monitoring/start_date");
            var body = Dict(
                "StartDay", string.IsNullOrEmpty(startDay)
                    ? Str(cur, "StartDay") : startDay,
                "DataLimit", string.IsNullOrEmpty(limit) ? Str(cur, "DataLimit") : limit,
                            "MonthThreshold", string.IsNullOrEmpty(threshold) ? Str(cur, "MonthThreshold") : threshold,
                            "SetMonthData", cur.ContainsKey("SetMonthData") ? Str(cur, "SetMonthData") : "1");
            var r = Post("/api/monitoring/start_date", body);
            return Fail(r) ?? L.T("err.planset");
        }

        string Act(string path, Dictionary<string, object> body, string okMsg)
        {
            if (!Login(false)) return errText;
            var r = Post(path, body);
            string fail = Fail(r);
            return fail ?? okMsg;
        }

        public void Dispose() { Logout(); }
    }
}
