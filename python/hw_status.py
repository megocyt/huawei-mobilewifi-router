#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""华为随身 WiFi（随行 WiFi / E5 系列）状态实时监控工具。

纯 Python 标准库实现，无需 pip 安装任何依赖。

设备端为华为 WebUI 2.x（E5783-836 随行WiFi 3 Pro 实测通过），采用 SCRAM
挑战式登录：密码不会明文出库，客户端只需按 PBKDF2-HMAC-SHA256 计算 proof，
故本工具完全离线可用（只要能访问设备 IP）。

用法：
    python hw_status.py                     # 实时刷新面板（Ctrl+C 退出）
    python hw_status.py status              # 打印一次完整状态
    python hw_status.py sms                 # 读短信（--mark 40036 标记已读）
    python hw_status.py plan                # 套餐流量（--limit 100GB 可设置）
    python hw_status.py act reboot --yes    # 操作：重启 / data-on / data-off / clear-traffic
    python hw_status.py watch --interval 2  # 实时面板，2 秒刷新
    python hw_status.py serve --port 8000   # 本地网页面板（含速率曲线）
    python hw_status.py probe               # 探测设备支持哪些接口
    python hw_status.py raw /api/device/signal

    # 免登录模式（只看无需鉴权的接口，不会顶掉网页端登录）
    python hw_status.py status --no-login

    # 连接其他设备
    python hw_status.py --host 192.168.8.1 --user admin --password xxxx status
"""
from __future__ import annotations

import argparse
import atexit
import gzip
import hashlib
import hmac
import http.cookiejar
import json
import os
import re
import secrets
import signal
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

DEFAULT_HOST = "192.168.8.1"
DEFAULT_USER = "admin"
DEFAULT_PASSWORD = os.environ.get("HWIFI_PASSWORD", "")

# ---------------------------------------------------------------- 状态码字典
# 来源：设备自带 main.js 中的 MACRO_NET_WORK_TYPE(_EX)_* 常量
NET_TYPE_EX = {
    "0": "无服务", "1": "GSM", "2": "GPRS", "3": "EDGE",
    "21": "IS-95A", "22": "IS-95B", "23": "CDMA1X",
    "24": "EVDO Rev.0", "25": "EVDO Rev.A", "26": "EVDO Rev.B",
    "27": "HRPD Rev.0", "28": "HRPD Rev.A", "29": "HRPD Rev.B", "30": "HRPD 1X",
    "31": "eHRPD Rev.0", "32": "eHRPD Rev.A", "33": "eHRPD Rev.B",
    "41": "WCDMA", "42": "HSDPA", "43": "HSUPA", "44": "HSPA",
    "45": "HSPA+", "46": "DC-HSPA+",
    "61": "TD-SCDMA", "62": "TD-HSDPA", "63": "TD-HSUPA", "64": "TD-HSPA", "65": "TD-HSPA+",
    "81": "WiMAX", "101": "LTE", "1011": "LTE-A", "111": "5G NR",
}
NET_TYPE = {
    "0": "无服务", "1": "GSM", "2": "GPRS", "3": "EDGE", "4": "WCDMA",
    "5": "HSDPA", "6": "HSUPA", "7": "HSPA", "8": "TD-SCDMA", "9": "HSPA+",
    "10": "EVDO Rev.0", "11": "EVDO Rev.A", "12": "EVDO Rev.B", "13": "CDMA1X",
    "14": "UMB", "19": "LTE", "20": "LTE/5G NR",
}
# 3GPP TS 27.007 rat
RAT = {"0": "GSM", "1": "GSM", "2": "UTRAN", "3": "GSM/EDGE", "4": "UTRAN/HSDPA",
       "5": "UTRAN/HSUPA", "6": "UTRAN/HSDPA+HSUPA", "7": "LTE(E-UTRAN)",
       "8": "5G NR", "9": "UTRAN HSPA"}
CONN_STATE = {
    "901": "已连接", "902": "已连接", "0": "未连接",
    "202": "设备不匹配", "100008": "拨号失败",
}
SERVICE_STATE = {"0": "无服务", "1": "限制服务", "2": "正常服务", "3": "正在注册"}
DOMAIN = {"0": "无", "1": "CS", "2": "PS", "3": "CS/PS"}
SIM_STATE = {"255": "未插卡", "256": "需要 PIN", "257": "已就绪", "258": "需要 PIN(未校验)",
             "259": "需要 PUK", "260": "已锁死", "261": "SIM 异常"}
# 由本设备实测推断：插着 USB 且电量 99% 时上报 1，到 100% 后上报 0
BATTERY_STATE = {"0": "未在充电", "1": "充电中"}
ERR_CODE = {
    "100002": "设备不支持该接口",
    "100003": "需要登录（会话无效或已过期）",
    "100005": "请求参数错误",
    "100006": "CSRF token 校验失败（token 已用过或已失效）",
    "108005": "登录被拒（短时间内登录次数过多，或已有其他客户端占用了会话）",
    "108006": "认证失败（密码错误或挑战已失效）",
    # 实测：主动登出后受保护接口返回 125002；登出后不重新打开页面直接重登会返回 125003
    "125002": "会话已注销（未登录）",
    "125003": "会话状态已失效，需重新打开页面后登录",
    # 实测：对已经读过的短信再次“标记已读”会回这个码
    "113055": "短信操作被拒（该条已读过 / 下标不存在 / 参数越界）",
    "125001": "账号被锁定",
}
# 表示“当前会话不可用，需要重新登录”的错误码
NEED_LOGIN = ("100003", "125002")
# 表示“会话上下文已死，必须清 cookie 重新拉页面才能登录”的错误码
DEAD_SESSION = ("125002", "125003")

# 每次轮询都取的接口 / 隔一段时间才取的接口（变化慢，减少设备压力）
FAST_GETS = [
    ("status", "/api/monitoring/status"),
    ("traffic", "/api/monitoring/traffic-statistics"),
    ("signal", "/api/device/signal"),
    ("hosts", "/api/system/HostInfo"),
]
SLOW_GETS = [
    ("device", "/api/device/information"),
    ("basic", "/api/device/basic_information"),
    ("wifi", "/api/wlan/basic-settings"),
    ("multiwifi", "/api/wlan/multi-basic-settings"),
    ("plmn", "/api/net/current-plmn"),
    ("month", "/api/monitoring/month_statistics"),
    ("time", "/api/sntp/timeinfo"),
    ("pin", "/api/pin/status"),
    ("boot", "/api/device/boot_time"),
    ("notify", "/api/monitoring/check-notifications"),
    ("limit", "/api/monitoring/start_date"),
    ("daylimit", "/api/monitoring/daily-data-limit"),
    ("smscount", "/api/sms/sms-count"),
    ("switch", "/api/dialup/mobile-dataswitch"),
    ("netmode", "/api/net/net-mode"),
]
# 无需登录即可读取的接口
ANON_GETS = FAST_GETS[:2] + SLOW_GETS[:1] + SLOW_GETS[4:6]

# 短信类接口（请求体均从设备自带 sms / common chunk 里核对过）
API_SMS_LIST = "/api/sms/sms-list-contact"          # POST {pageindex, readcount}
API_SMS_COUNT = "/api/sms/sms-count"                # GET
API_SMS_SET_READ = "/api/sms/set-read"              # POST {Index: [...]}
API_SMS_DELETE = "/api/sms/delete-sms"              # POST {Index: [...]}
API_PLAN = "/api/monitoring/start_date"           # GET 读 / POST 写
API_PLAN_MONTH = "/api/monitoring/month_statistics"
API_PLAN_DAY = "/api/monitoring/daily-data-limit"
# 操作类接口
API_CONTROL = "/api/device/control"                 # POST {Control: 1} —— 重启
API_CLEAR_TRAFFIC = "/api/monitoring/clear-traffic"   # POST {ClearTraffic: 1}


# ---------------------------------------------------------------- 工具函数
def _int(v):
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return 0


UNITS = {"B": 1, "KB": 1024, "MB": 1024 ** 2, "GB": 1024 ** 3, "TB": 1024 ** 4}


def parse_size(text):
    """把设备里的 “150GB” / “1024MB” / 纯字节字符串转成字节数。"""
    if text in (None, ""):
        return 0
    m = re.search(r"([0-9]+(?:\.[0-9]+)?)\s*([KMGT]?B)?", str(text).strip(), re.I)
    if not m:
        return 0
    mult = UNITS.get((m.group(2) or "B").upper(), 1)
    return int(float(m.group(1)) * mult)


def build_plan(cfg, mon, day):
    """由套餐配置 + 月统计 + 日限额算出使用情况（纯函数，便于单测）。"""
    cfg, mon, day = cfg or {}, mon or {}, day or {}
    used = _int(mon.get("CurrentMonthDownload")) + _int(mon.get("CurrentMonthUpload"))
    limit_b = parse_size(cfg.get("DataLimit"))
    p = {"enabled": str(cfg.get("SetMonthData")) == "1",
         "limit_raw": cfg.get("DataLimit") or "", "limit": limit_b, "used": used,
         "start_day": cfg.get("StartDay") or "", "threshold": cfg.get("MonthThreshold") or "",
         "day_limit_raw": day.get("DayDataLimit") or "", "day_used": _int(mon.get("CurrentDayUsed")),
         "clear_time": mon.get("MonthLastClearTime") or "",
         "month_download": _int(mon.get("CurrentMonthDownload")),
         "month_upload": _int(mon.get("CurrentMonthUpload")),
         "month_duration": _int(mon.get("MonthDuration"))}
    p["remain"] = max(0, limit_b - used) if limit_b else None
    p["percent"] = round(used * 100.0 / limit_b, 1) if limit_b else None
    thr = _int(cfg.get("MonthThreshold"))
    p["over_threshold"] = bool(p["percent"] is not None and thr and p["percent"] >= thr)
    p["over_limit"] = bool(p["percent"] is not None and p["percent"] >= 100)
    # 计费周期已过天数占比，用来判断“用得快不快”
    try:
        import calendar
        import datetime
        today = datetime.date.today()
        start = max(1, _int(cfg.get("StartDay")) or 1)
        dim = calendar.monthrange(today.year, today.month)[1]
        cur = today.day if today.day >= start else today.day + dim
        p["day_percent"] = round(min(100.0, cur * 100.0 / dim), 1)
        p["days_left"] = max(0, dim - cur)
        p["speed_ok"] = (p["percent"] is not None and p["percent"] <= p["day_percent"] + 10)
    except (ValueError, TypeError):
        pass
    return p


def human_bytes(n, digits=1):
    try:
        n = float(n)
    except (TypeError, ValueError):
        return "-"
    neg = "-" if n < 0 else ""
    n = abs(n)
    for unit in ("B", "KB", "MB", "GB", "TB", "PB"):
        if n < 1024 or unit == "PB":
            return "%s%.*f %s" % (neg, digits if unit != "B" else 0, n, unit)
        n /= 1024.0
    return "%s%.1f PB" % (neg, n)


def human_rate(bps):
    if bps is None:
        return "-"
    return human_bytes(abs(float(bps)), 1).replace("/s", "") + "/s"


def human_dur(sec):
    try:
        sec = int(float(sec))
    except (TypeError, ValueError):
        return "-"
    d, sec = divmod(sec, 86400)
    h, sec = divmod(sec, 3600)
    m, s = divmod(sec, 60)
    if d:
        return "%d天%d小时%d分" % (d, h, m)
    if h:
        return "%d小时%d分%d秒" % (h, m, s)
    return "%d分%d秒" % (m, s)


def decode_body(raw):
    """设备基本为 UTF-8，但个别终端名可能是本机编码写入的脏字节。"""
    for enc in ("utf-8", "gb18030"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", "replace")


def xml_to_dict(node):
    """设备多数接口返回 XML，统一转成 dict；同名标签聚合成 list。"""
    out = {}
    for child in node:
        val = xml_to_dict(child) if len(child) else (child.text or "")
        if child.tag in out:
            if not isinstance(out[child.tag], list):
                out[child.tag] = [out[child.tag]]
            out[child.tag].append(val)
        else:
            out[child.tag] = val
    return out


def dict_to_xml(tag, obj):
    """与设备自带 jsonToXml 一致：数组展开成重复的同名标签，字典则下钻一层。

    短信类接口的 {"Index": [1, 2]} 须变成 <Index>1</Index><Index>2</Index>。
    """
    if obj is None:
        return ""
    if isinstance(obj, (list, tuple)):
        return "".join(dict_to_xml(tag, v) for v in obj)
    if isinstance(obj, dict):
        parts = ["<%s>" % tag]
        for k, v in obj.items():
            parts.append(dict_to_xml(k, v))
        parts.append("</%s>" % tag)
        return "".join(parts)
    return "<%s>%s</%s>" % (tag, obj, tag)


def as_list(v):
    if v is None or v == "":
        return []
    return v if isinstance(v, list) else [v]


# ---------------------------------------------------------------- 设备客户端
class DeviceError(RuntimeError):
    def __init__(self, message, code=None):
        super().__init__(message)
        self.code = code


class HuaweiWifi:
    """华为随身 WiFi WebUI 客户端（SCRAM 登录 + XML/JSON 接口）。"""

    def __init__(self, host=DEFAULT_HOST, username=DEFAULT_USER, password=DEFAULT_PASSWORD,
                 timeout=8, use_login=True, debug=False):
        url = host if "://" in host else "http://" + host
        self.base = url.rstrip("/")
        self.username = username
        self.password = password
        self.timeout = timeout
        self.use_login = use_login
        self.debug = debug
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.cookies))
        self.tokens = []            # 一次性 CSRF token 队列（POST 用）
        self.logged_in = False
        self.last_login_error = None
        self.last_login_code = None
        self.login_blocked_until = 0.0   # 108005 退避，避免反复冲击设备
        self.login_fails = 0              # 连续登录失败次数，用于递增退避
        self._last_login_try = 0.0        # 上次尝试登录的时间，限制被顶号时的互踢频率
        self._slow_cache = {}
        self._slow_at = 0.0
        self._prev = None           # 上一次采样，用于计算速率
        self._prev_ts = 0.0
        # 每机速率滞动窗口：实测设备的每机字节计数器是“长时间不动 + 一次跳增很大量”的不规则
        # 批量刷新（真实下载 2.67MB/s 时，40 秒内计数器完全不涨，随后一次 +72MB），
        # 只拿相邻两次采样做差分会长时间得到 0，故保留 host_win_sec 秒的采样序列求均值。
        self._host_win = {}
        self.host_win_sec = 60.0
        # 短信：每 sms_interval 秒拉一页（=0 则只统计未读数，不拉正文）
        self.sms_size = 20
        self.sms_interval = 20.0
        self._sms_at = 0.0

    # ---------------- 底层 HTTP ----------------
    def _request(self, path, data=None, params=None, headers=None):
        url = self.base + path
        if params:
            url += "?" + urllib.parse.urlencode(params)
        h = {
            "User-Agent": "Mozilla/5.0 (hw-status)",
            "X-Requested-With": "XMLHttpRequest",
            "Accept": "application/json, text/javascript, */*; q=0.01",
            "Accept-Encoding": "identity",
            "Referer": self.base + "/index.html",
        }
        h.update(headers or {})
        body = None
        if data is not None:
            body = data.encode("utf-8") if isinstance(data, str) else data
            h.setdefault("Content-Type", "application/x-www-form-urlencoded; charset=UTF-8")
        if self.debug:
            print("[http] %s %s %s" % ("POST" if body else "GET", path, (body or b"")[:200]),
                  file=sys.stderr)
        req = urllib.request.Request(url, data=body, headers=h)
        try:
            with self.opener.open(req, timeout=self.timeout) as resp:
                raw = resp.read()
                if raw[:2] == b"\x1f\x8b":      # 设备偶尔仍返回 gzip
                    raw = gzip.decompress(raw)
                return resp.status, decode_body(raw)
        except urllib.error.HTTPError as e:
            return e.code, decode_body(e.read())

    @staticmethod
    def _parse(text):
        t = (text or "").strip()
        if t.startswith("<"):
            try:
                return xml_to_dict(ET.fromstring(t))
            except ET.ParseError:
                return {"_raw": t}
        try:
            return json.loads(t)
        except ValueError:
            return {"_raw": t}

    def _check(self, obj, path):
        """把 {code:xxxx} 形式的错误转成异常。"""
        if isinstance(obj, dict) and obj.get("code") not in (None, "", "0", "-1"):
            code = str(obj["code"])
            hint = ERR_CODE.get(code, "错误码 " + code)
            raise DeviceError("%s -> %s" % (hint, path), code)
        return obj

    # ---------------- token / 登录 ----------------
    def load_page(self):
        """取首页，拿到 SessionID cookie 与 meta 中的 csrf_token。"""
        _st, body = self._request("/")
        self.tokens = re.findall(r'csrf_token" content="([^"]+)"', body)
        return self.tokens

    def refresh_token(self):
        """/api/webserver/token 返回 64 字符，后 32 位是真正的一次性 token。"""
        _st, body = self._request("/api/webserver/token",
                                  params={"_ResponseSource": "Broswer"})
        r = self._check(self._parse(body), "webserver/token")
        tok = r.get("token") if isinstance(r, dict) else None
        if tok:
            self.tokens = [tok[32:]]
        return tok

    def state_login(self):
        _st, body = self._request("/api/user/state-login", params={"_ResponseSource": "Broswer"})
        return self._parse(body)

    @staticmethod
    def scram(password, salt_hex, iterations, first_nonce, server_nonce):
        """与设备自带 emui-crypto.js 中 CryptoJS.SCRAM 等价的实现。

        注意：固件把 CryptoJS.HmacSHA256(message, key) 的参数顺序用得与
        RFC 5802 相反（key/msg 互换），照抄 RFC 会被设备判为 108006。
        """
        salt = bytes.fromhex(salt_hex)
        auth = ("%s,%s,%s" % (first_nonce, server_nonce, server_nonce)).encode()
        sp = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt,
                                 int(iterations), 32)
        client_key = hmac.new(b"Client Key", sp, hashlib.sha256).digest()
        stored_key = hashlib.sha256(client_key).digest()
        client_sig = hmac.new(auth, stored_key, hashlib.sha256).digest()
        proof = bytes(a ^ b for a, b in zip(client_key, client_sig)).hex()
        server_key = hmac.new(b"Server Key", sp, hashlib.sha256).digest()
        signature = hmac.new(auth, server_key, hashlib.sha256).hexdigest()
        return proof, signature

    def _post(self, path, data, retry=True):
        if not self.tokens:
            self.refresh_token()
        if not self.tokens:
            raise DeviceError("无法获取 CSRF token: " + path)
        xml = '<?xml version="1.0" encoding="UTF-8"?>' + dict_to_xml("request", data)
        headers = {"_ResponseSource": "Broswer", "Accept": "*/*",
                   "__RequestVerificationToken": self.tokens.pop(0)}   # token 一次性
        st, body = self._request(path, data=xml, headers=headers)
        parsed = self._parse(body)
        code = str(parsed.get("code")) if isinstance(parsed, dict) else None
        if retry and code in ("100006", "100003"):
            return self._post(path, data, retry=False)
        return self._check(parsed, path)

    def login(self, force=False):
        """SCRAM 三步登录，成功后本地校验 serversignature。

        设备对登录频率有限制：实测短时间内的连续几次登录会得到 108005，限流
        可持续十余分钟，且期间反复重试只会不断延长窗口，故失败后递增退避。
        正常运行只会登录一次，靠轮询保活，不会触发该限制。
        """
        if self.logged_in and not force:
            return True
        if time.time() < self.login_blocked_until:
            return False
        self._last_login_try = time.time()
        self.last_login_error = None
        try:
            if not self.cookies:
                self.load_page()
            state = self.state_login()
            ptype = str(state.get("password_type", "4"))
            # 实测：未登录 State=-1 且 userlevel 为空；已登录 State=0、userlevel=2
            if not force and str(state.get("State")) == "0" and state.get("userlevel"):
                self.logged_in = True
                return True
            for attempt in (1, 2):
                try:
                    if ptype == "4":
                        self._login_scram(state)
                    else:
                        self._login_legacy(state)
                    break
                except DeviceError as e:
                    # 上一个会话被 logout 作废后，沿用它直接重登会被回 125003，
                    # 必须清掉 cookie 重新打开页面拿新会话（实测有效）。
                    if attempt == 1 and e.code in DEAD_SESSION:
                        self.forget_session()
                        state = self.state_login()
                        ptype = str(state.get("password_type", "4"))
                        continue
                    raise
            self.logged_in = True
            self.last_login_error = None
            self.last_login_code = None
            self.login_fails = 0
            self.login_blocked_until = 0.0
            return True
        except DeviceError as e:
            self.logged_in = False
            self.last_login_error = str(e)
            self.last_login_code = e.code
            if e.code in ("108003", "108005", "108006", "125001"):
                # 设备对登录频率有限制（实测连续几次登录后会回 108005 并持续很久），
                # 递增退避：30s → 60s → 120s → 240s … 最大 15 分钟。
                self.login_fails += 1
                backoff = min(900, 30 * (2 ** (self.login_fails - 1)))
                self.login_blocked_until = time.time() + backoff
            return False

    def _login_scram(self, state):
        if str(state.get("lockstatus")) == "1":
            raise DeviceError("账号已被锁定，请等待或重置设备")
        first = secrets.token_hex(32)
        self.refresh_token()
        ch = self._post("/api/user/challenge_login",
                        {"username": self.username, "firstnonce": first, "mode": 1})
        if "salt" not in ch:
            raise DeviceError("登录挑战失败: %s" % ch.get("code", "?"), ch.get("code"))
        proof, expect = self.scram(self.password, ch["salt"], ch["iterations"],
                                   first, ch["servernonce"])
        resp = self._post("/api/user/authentication_login",
                          {"clientproof": proof, "finalnonce": ch["servernonce"]})
        got = resp.get("serversignature")
        if got and not hmac.compare_digest(got, expect):
            raise DeviceError("服务器签名校验失败（设备可能被中间代理）")

    def _login_legacy(self, state):
        """老固件的 user/login：password_type 1/2 = base64 / sha256 派生。"""
        import base64
        ptype = str(state.get("password_type", "0"))
        if ptype == "2":
            digest = hashlib.sha256(self.password.encode()).hexdigest()
            tok = (self.tokens or [""])[0]
            pwd = base64.b64encode(hashlib.sha256(
                (self.username + base64.b64encode(digest.encode()).decode() + tok)
                .encode()).hexdigest().encode()).decode()
        else:
            pwd = base64.b64encode(self.password.encode()).decode()
        self.refresh_token()
        self._post("/api/user/login", {"Username": self.username, "Password": pwd,
                                       "password_type": ptype})

    def forget_session(self):
        """丢掉会话与 token，下次登录相当于重新打开管理页面。"""
        self.cookies.clear()
        self.tokens = []
        self.logged_in = False
        self.load_page()

    def logout(self, quiet=True):
        """登出设备会话（退出时调用，把唯一的一个会话位还给网页后台）。"""
        if not self.logged_in:
            return True
        try:
            self._post("/api/user/logout", {"Logout": 1})
        except (DeviceError, OSError) as e:
            if not quiet:
                print("登出失败：%s" % e, file=sys.stderr)
        self.logged_in = False
        self.forget_session()          # 不重置的话，重登会被 125003 拒
        if not quiet:
            print("已登出 %s" % self.base, file=sys.stderr)
        return True

    # ---------------- 业务读取 ----------------
    def get(self, path, headers=None):
        params = {"_ResponseSource": "Broswer"}
        h = dict(headers or {})
        if path.endswith("monitoring/status"):
            # 设备自己的前端会带这个头（已从 main.js 核实）。对照实验确认带与不带
            # 都不会影响会话，保留它只为与官方页面行为一致。
            h["Update-Cookie"] = "UpdateCookie"
        _st, body = self._request(path, params=params, headers=h)
        return self._check(self._parse(body), path)

    def login_block_left(self):
        return max(0, int(self.login_blocked_until - time.time()))

    def ensure_login(self):
        if not self.use_login:
            return False
        return self.login()

    # ---------------- 短信 / 套餐 / 操作 ----------------
    def sms_list(self, page=1, size=20):
        """按发件人分组读短信。实测 smstat=="0" 是未读（与设备自带 setSmsRead 一致）。"""
        r = self._post(API_SMS_LIST, {"pageindex": int(page), "readcount": int(size)})
        msgs = []
        for m in as_list((r.get("messages") or {}).get("message")):
            if not isinstance(m, dict):
                continue
            msgs.append({
                "index": m.get("index"), "phone": m.get("phone") or "",
                "content": (m.get("content") or "").replace("&#x0D;", "").strip(),
                "date": m.get("date") or "", "unread": str(m.get("smstat")) == "0",
                "type": m.get("smstype") or "", "unread_count": m.get("unreadcount") or "0",
            })
        msgs.sort(key=lambda x: x["date"], reverse=True)
        # 注意：设备返回的 count 是“本页分组数”而不是总数（实测 size=3 时 count=3），
        # 所以总数一律以 sms-count 的 LocalInbox 为准。
        return {"count": r.get("count") or len(msgs), "page": page, "size": size, "list": msgs}

    def sms_count(self):
        try:
            r = self.get(API_SMS_COUNT)
        except DeviceError:
            return {}
        return {"unread": r.get("LocalUnread") or "0", "inbox": r.get("LocalInbox") or "0",
                "outbox": r.get("LocalOutbox") or "0", "draft": r.get("LocalDraft") or "0",
                "sim_unread": r.get("SimUnread") or "0", "max": r.get("LocalMax") or "0"}

    def sms_mark_read(self, indexes):
        return self._post(API_SMS_SET_READ, {"Index": [int(i) for i in indexes]})

    def sms_delete(self, indexes):
        return self._post(API_SMS_DELETE, {"Index": [int(i) for i in indexes]})

    def plan(self):
        """套餐流量使用情况：直接读设备（单独调用时缓存可能还是空的）。"""
        def rd(path, key):
            try:
                return self.get(path)
            except DeviceError:
                return self._slow_cache.get(key) or {}
        return build_plan(rd(API_PLAN, "limit"), rd(API_PLAN_MONTH, "month"),
                          rd(API_PLAN_DAY, "daylimit"))

    def set_plan(self, limit=None, start_day=None, threshold=None, enabled=None):
        """写套餐设置；None 的字段保持设备当前值（幂等写回可用于验证）。"""
        cur = self.get(API_PLAN)
        body = {"StartDay": start_day if start_day is not None else (cur.get("StartDay") or 1),
                "DataLimit": limit if limit is not None else (cur.get("DataLimit") or "0GB"),
                "MonthThreshold": threshold if threshold is not None
                else (cur.get("MonthThreshold") or 90),
                "SetMonthData": enabled if enabled is not None
                else (cur.get("SetMonthData") or 0)}
        return body, self._post(API_PLAN, body)

    def set_data_switch(self, on):
        return self._post("/api/dialup/mobile-dataswitch", {"dataswitch": "1" if on else "0"})

    def reboot(self):
        return self._post(API_CONTROL, {"Control": 1})

    def clear_traffic(self):
        return self._post(API_CLEAR_TRAFFIC, {"ClearTraffic": 1})

    def do_action(self, name, arg=None):
        """供网页按钮与 CLI 共用的动作入口，返回可读结果字符串。"""
        if name == "reboot":
            self.reboot()
            return "重启指令已下发，设备约 1~2 分钟后恢复"
        if name == "data_on":
            self.set_data_switch(True)
            return "已开启移动数据"
        if name == "data_off":
            self.set_data_switch(False)
            return "已关闭移动数据"
        if name == "clear_traffic":
            self.clear_traffic()
            return "已清零历史流量统计"
        if name == "sms_read":
            self.sms_mark_read(arg or [])
            return "已标记 %d 条为已读" % len(arg or [])
        if name == "sms_delete":
            self.sms_delete(arg or [])
            return "已删除 %d 条短信" % len(arg or [])
        if name == "set_plan":
            a = arg or {}
            body, _r = self.set_plan(limit=a.get("limit"), start_day=a.get("start_day"),
                                     threshold=a.get("threshold"), enabled=a.get("enabled"))
            return "套餐设置已保存：%s / 每月 %s 起 / 阈值 %s%%" % (
                body["DataLimit"], body["StartDay"], body["MonthThreshold"])
        raise DeviceError("未知操作: %s" % name)

    # ---------------- 汇总采集 ----------------
    def heartbeat(self):
        """设备会话空闲 ~60~90 秒即过期，轮询本身就是保活；这里额外显式打点。"""
        try:
            r = self.get("/api/user/heartbeat")
        except DeviceError:
            self.logged_in = False
            return False
        # 设备返回 userlevel=0 代表会话已失效（网页端会因此踢回登录页）
        if str(r.get("userlevel")) in ("0", ""):
            self.logged_in = False
            return False
        return True

    def snapshot(self):
        """采集一轮全部状态，返回结构化 dict。"""
        now = time.time()
        snap = {"ts": now, "base": self.base, "logged_in": self.logged_in,
                "errors": {}, "auth_error": self.last_login_error,
                "login_wait": self.login_block_left()}
        gets = list(FAST_GETS)
        if now - self._slow_at > 15 or not self._slow_cache:
            gets += SLOW_GETS
            self._slow_at = now
        for key, path in gets:
            try:
                data = self.get(path)
            except DeviceError as e:
                if key in self._slow_cache:      # 慢接口失败时沿用旧值
                    snap.setdefault(key, self._slow_cache[key])
                snap["errors"][key] = "%s (%s)" % (e, e.code)
                if e.code in NEED_LOGIN:
                    snap["need_login"] = True
                continue
            except (OSError, urllib.error.URLError) as e:
                snap["errors"][key] = "网络不可达: %s" % e
                raise
            snap[key] = data
            if (key, path) in SLOW_GETS:
                self._slow_cache[key] = data
        # 短信列表：读它是 POST（还要额外的 token 请求），故用独立闸门，
        # sms_interval=0 时完全不拉（只保留慢接口的 sms-count 未读数）。
        if (self.use_login and self.sms_interval
                and now - self._sms_at > self.sms_interval):
            self._sms_at = now
            try:
                self._slow_cache["sms"] = self.sms_list(1, self.sms_size)
            except DeviceError as e:
                snap["errors"]["sms"] = "%s (%s)" % (e, e.code)
        # 会话被别的客户端（如浏览器后台）顶掉时，至少间隔 5 秒再重登，
        # 避免两边形成“互踢循环”最终踩到设备的登录频率限制。
        if (snap.get("need_login") and self.use_login
                and now - self._last_login_try > 5 and self.login(force=True)):
            snap.pop("need_login")
            snap["logged_in"] = True
            self._slow_at = 0.0        # 重登后重取慢接口，否则设备/WiFi 信息会缺失
            return self.snapshot()
        for key in list(self._slow_cache):
            snap.setdefault(key, self._slow_cache[key])
        snap["computed"] = self._compute(snap)
        self._prev = {"traffic": snap.get("traffic"), "ts": now}
        self._prev_ts = now
        return snap

    def _compute(self, snap):
        """把设备给的累计值换算成实时速率、等级、中文状态等。"""
        c = {}
        st = snap.get("status") or {}
        tr = snap.get("traffic") or {}
        sig = snap.get("signal") or {}
        dt = max(0.001, snap["ts"] - self._prev_ts) if self._prev_ts else None

        # 上下行速率：优先用设备自报，其次用两次采样差值
        try:
            down = float(tr.get("CurrentDownloadRate"))
            up = float(tr.get("CurrentUploadRate"))
        except (TypeError, ValueError):
            down = up = None
        if down is None and dt and self._prev:
            p = (self._prev.get("traffic") or {})
            try:
                down = (float(tr["CurrentDownload"]) - float(p["CurrentDownload"])) / dt
                up = (float(tr["CurrentUpload"]) - float(p["CurrentUpload"])) / dt
            except (KeyError, TypeError, ValueError):
                pass
        c["rate_down"], c["rate_up"] = down, up

        icon = st.get("SignalIcon")
        mx = st.get("maxsignal") or "5"
        try:
            c["signal_bars"] = (int(icon), int(mx))
        except (TypeError, ValueError):
            c["signal_bars"] = (0, 5)
        c["net"] = NET_TYPE_EX.get(str(st.get("CurrentNetworkTypeEx"))) or \
            NET_TYPE.get(str(st.get("CurrentNetworkType"))) or \
            st.get("CurrentNetworkType") or "-"
        c["conn"] = CONN_STATE.get(str(st.get("ConnectionStatus")),
                                   "状态码 %s" % st.get("ConnectionStatus"))
        c["service"] = SERVICE_STATE.get(str(st.get("ServiceStatus")), "-")
        c["domain"] = DOMAIN.get(str(st.get("CurrentServiceDomain")), "-")
        c["sim"] = SIM_STATE.get(str((snap.get("pin") or {}).get("SimState")), "-")
        c["battery_state"] = BATTERY_STATE.get(str(st.get("BatteryStatus")), "-")

        def num(v):
            m = re.search(r"-?\d+(?:\.\d+)?", v or "")
            return float(m.group()) if m else None

        c["rsrp"], c["sinr"], c["rsrq"], c["rssi"] = (num(sig.get("rsrp")), num(sig.get("sinr")),
                                                      num(sig.get("rsrq")), num(sig.get("rssi")))
        c["rsrp_level"] = self.rsrp_level(c["rsrp"])
        c["sinr_level"] = self.sinr_level(c["sinr"])

        # 在线终端：HostInfo 可能是 dict(含 Host list) 或直接 list
        hosts = snap.get("hosts")
        rows = as_list(hosts.get("Host") if isinstance(hosts, dict) else hosts)
        online = []
        now = snap["ts"]
        seen = set()
        for h in rows:
            if not isinstance(h, dict):
                continue
            mac = h.get("MACAddress")
            active = str(h.get("Active")).lower() in ("true", "1") or h.get("WlanActive") in (True, "true")
            item = {
                "name": h.get("ActualName") or h.get("HostName") or "(未命名)",
                "mac": mac, "ip": h.get("IPAddress"), "ipv6": h.get("IPv6Address"),
                "ssid": h.get("SsidName"), "band": h.get("Frequency"),
                "brand": h.get("DevBrands"), "vendor": h.get("VendorClassID"),
                "active": bool(active),
                "up": h.get("UpRate"), "down": h.get("DownRate"),
                "tx": h.get("TxKBytes"), "rx": h.get("RxKBytes"),
                "assoc": h.get("AssociatedTime"), "guest": h.get("IsGuest"),
            }
            # 每机速率：用 TxKBytes/RxKBytes 累计值在 host_win_sec 窗口内的均值。实测该累计值是
            # 不规则批量刷新的（可 40~60 秒完全不动，然后一次跳增几十 MB；不管流量是走蜂窝
            # 还是访问设备自身网页，最后都会被计入），而设备自报的速率字段也严重滞后
            # （真实 2.67MB/s 的下载期间只报 29KB/s），所以只能给出区间均速并标出区间长度：
            # 窗口太短会落在“没刷新”的那段上而算出 0，太长会被摊平；用“最近一次跳变区间”
            # 则把十几秒的字节算到两秒头上（合成数据实测虚高 3 倍）。
            def kbytes(v):
                try:
                    return float(v) * 1024.0
                except (TypeError, ValueError):
                    return 0.0

            item["dev_rate_up"], item["dev_rate_down"] = kbytes(h.get("UpRate")), \
                kbytes(h.get("DownRate"))
            try:
                tx, rx = float(h["TxKBytes"]), float(h["RxKBytes"])
            except (KeyError, TypeError, ValueError):
                tx = rx = None
            win = self._host_win.setdefault(mac, {"tx": [], "rx": []})
            for key, val in (("tx", tx), ("rx", rx)):
                if val is None:
                    continue
                pts = win[key]
                pts.append((now, val))               # 每轮都采样，窗口内取首末求均值
                while len(pts) > 2 and now - pts[0][0] > self.host_win_sec:
                    pts.pop(0)
            seen.add(mac)
            for key in ("tx", "rx"):
                pts = win[key]
                if len(pts) < 2:
                    continue
                span = pts[-1][0] - pts[0][0]
                if span > 0.5:
                    # 窗口均值：设备计数器是“每隔十几秒一次性补上这十几秒的字节”，
                    # 用“最近一次跳变区间”会把 15 秒的字节算到 5 秒头上（实测虚高 3 倍），
                    # 所以窗口必须跨过一个完整的刷新周期。
                    rate = max(0.0, (pts[-1][1] - pts[0][1])) * 1024 / span
                    item["calc_" + ("down" if key == "rx" else "up")] = round(rate)
                    item["win_sec"] = round(span, 1)
            # 优先用本工具算出的区间均速（数据最真实），其次用设备自报值
            if item["active"]:
                item["rate_down"] = int(item.get("calc_down") or item["dev_rate_down"] or 0)
                item["rate_up"] = int(item.get("calc_up") or item["dev_rate_up"] or 0)
                # 计数器完全没动过而设备自报也为 0 时， rate_mode 仍为 win（均速 0），
                # 表示“这 60 秒内确实没流量”；连一点自报值都没有时才归为 dev。
                item["rate_mode"] = ("win" if (item.get("calc_down") is not None
                                               or item.get("calc_up") is not None)
                                     else "dev" if (item["dev_rate_down"]
                                                    or item["dev_rate_up"]) else None)
            online.append(item)
        for mac in list(self._host_win):          # 清理已从列表消失的终端
            if mac not in seen:
                self._host_win.pop(mac)
        c["hosts"] = online
        c["hosts_total"] = len(online)
        c["hosts_active"] = sum(1 for o in online if o["active"])
        # 套餐流量：直接复用本轮已取到的设备数据
        c["plan"] = build_plan(snap.get("limit"), snap.get("month"), snap.get("daylimit"))
        sc = snap.get("smscount") or {}
        c["sms_unread"] = _int(sc.get("LocalUnread"))
        c["sms_inbox"] = _int(sc.get("LocalInbox"))
        c["data_switch"] = str((snap.get("switch") or {}).get("dataswitch")) == "1"
        return c

    @staticmethod
    def rsrp_level(v):
        if v is None:
            return "-"
        if v >= -80:
            return "优"
        if v >= -90:
            return "良"
        if v >= -100:
            return "中"
        if v >= -110:
            return "差"
        return "极差"

    @staticmethod
    def sinr_level(v):
        if v is None:
            return "-"
        if v >= 20:
            return "优"
        if v >= 13:
            return "良"
        if v >= 5:
            return "中"
        if v >= 0:
            return "差"
        return "极差"


# ---------------------------------------------------------------- 终端渲染
COLORS = {"dim": "\033[2m", "red": "\033[31m", "green": "\033[32m", "yellow": "\033[33m",
          "blue": "\033[34m", "cyan": "\033[36m", "bold": "\033[1m", "off": "\033[0m"}


def enable_vt():
    """Windows 上开启 ANSI 转义处理；失败则返回 False（改用整屏重绘）。"""
    if os.name != "nt":
        return True
    try:
        import ctypes
        k = ctypes.windll.kernel32
        for std in (-11, -12):
            h = k.GetStdHandle(std)
            mode = ctypes.c_uint32()
            if k.GetConsoleMode(h, ctypes.byref(mode)):
                k.SetConsoleMode(h, mode.value | 0x0004)
        return True
    except Exception:  # noqa: BLE001
        return False


def bars(n, total, width=10, full="#", empty="."):
    try:
        n, total = float(n), float(total)
    except (TypeError, ValueError):
        return "-"
    if total <= 0:
        return "-"
    filled = int(round(width * max(0.0, min(n, total)) / total))
    return full * filled + empty * (width - filled)


def field(label, value, width=22):
    v = "-" if value in (None, "") else value
    return "  %s: %s" % (label.ljust(width // 2), v)


class Painter:
    def __init__(self, color=True, show_all_hosts=False):
        self.color = color and sys.stdout.isatty()
        self.tty = sys.stdout.isatty()
        self.vt = enable_vt() if self.color else False
        self.show_all_hosts = show_all_hosts

    def c(self, text, *names):
        if not self.color:
            return text
        return "".join(COLORS[n] for n in names) + text + COLORS["off"]

    def render(self, snap, hist=None):
        st = snap.get("status") or {}
        co = snap.get("computed") or {}
        sig = snap.get("signal") or {}
        dev = snap.get("device") or {}
        wifi = snap.get("wifi") or {}
        plmn = snap.get("plmn") or {}
        tr = snap.get("traffic") or {}
        month = snap.get("month") or {}
        lines = []
        A = lines.append
        now = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(snap["ts"]))
        model = dev.get("DeviceName") or (snap.get("basic") or {}).get("devicename") or "HUAWEI"
        A(self.c("=" * 74, "dim"))
        A(self.c(" 华为随身 WiFi 实时状态", "bold") + "   %s   %s" % (model, now))
        # 顶部状态栏：紧凑的信号格 + 电池图标
        bn0, bt0 = co["signal_bars"]
        pct = st.get("BatteryPercent") or "0"
        chg = str(st.get("BatteryStatus")) == "1"
        A(" 信号 %s %d/%d %s   电量 [%s] %s%%%s"
          % (self.c(bars(bn0, bt0, width=bt0 or 5, full="█", empty="░"), "cyan"),
             bn0, bt0,
             self.c("强" if co["rsrp_level"] in ("优", "良") else
                    ("中" if co["rsrp_level"] == "中" else "弱"), "dim"),
             self.c(bars(pct, 100, width=8, full="█", empty="░"),
                    "green" if float(pct or 0) > 20 else "red"),
             pct, self.c("［充电中］", "green") if chg else ""))
        A(self.c("=" * 74, "dim"))

        # 网络
        conn = co["conn"]
        ok = str(st.get("ConnectionStatus")) == "901"
        A(" 网络状态   %s   制式 %s   运营商 %s(%s)   域 %s"
          % (self.c(conn, "green" if ok else "red"), co["net"],
             plmn.get("FullName") or "-", plmn.get("Numeric") or "-", co["domain"]))
        A(" 信号       %s %s/%s 格   RSRP %s dBm(%s)  SINR %s dB(%s)  RSRQ %s"
          % (self.c(bars(co["signal_bars"][0], co["signal_bars"][1]), "cyan"),
             co["signal_bars"][0], co["signal_bars"][1],
             "-" if co["rsrp"] is None else co["rsrp"], co["rsrp_level"],
             "-" if co["sinr"] is None else co["sinr"], co["sinr_level"],
             sig.get("rsrq") or "-"))
        A(" 小区       PCI %s  CellID %s  eNB %s  频段 %s  带宽 DL %s / UL %s  MCC/MNC %s"
          % (sig.get("pci") or "-", sig.get("cell_id") or "-", sig.get("enodeb_id") or "-",
             sig.get("bandInfo") or sig.get("band") or "-",
             sig.get("dlbandwidth") or "-", sig.get("ulbandwidth") or "-",
             sig.get("plmn") or "-"))
        if sig.get("dlfrequency") or sig.get("earfcn"):
            A(" 频点       EARFCN %s   下行 %s / 上行 %s   MCS dl %s / ul %s   CQI %s"
              % (sig.get("earfcn") or "-", sig.get("dlfrequency") or "-",
                 sig.get("ulfrequency") or "-", (sig.get("dl_mcs") or "-"),
                 (sig.get("ul_mcs") or "-"), sig.get("cqi0") or "-"))

        # 流量与速率
        A(self.c("-" * 74, "dim"))
        d, u = co.get("rate_down"), co.get("rate_up")
        A(" 实时速率   %s  %s      %s  %s"
          % (self.c(human_rate(d).rjust(12), "green"), self.c("↓ 下行", "dim"),
             self.c(human_rate(u).rjust(12), "yellow"), self.c("↑ 上行", "dim")))
        A(" 本次连接   ↓ %s   ↑ %s   时长 %s"
          % (human_bytes(tr.get("CurrentDownload")), human_bytes(tr.get("CurrentUpload")),
             human_dur(tr.get("CurrentConnectTime"))))
        A(" 今日已用   %s      本月  ↓ %s  ↑ %s   结算日 %s"
          % (human_bytes(month.get("CurrentDayUsed")),
             human_bytes(month.get("CurrentMonthDownload")),
             human_bytes(month.get("CurrentMonthUpload")),
             month.get("MonthLastClearTime") or "-"))
        A(" 累计流量   ↓ %s   ↑ %s   联网总时长 %s"
          % (human_bytes(tr.get("TotalDownload")), human_bytes(tr.get("TotalUpload")),
             human_dur(tr.get("TotalConnectTime"))))
        limit = (snap.get("limit") or {}).get("DataLimit")
        used = month.get("CurrentMonthDownload")
        if limit and used is not None:
            try:
                used = float(used)
                cap = float(re.sub(r"[^0-9.]", "", limit)) * (1024 ** 3)
                A(" 套餐用量   %s %s / %s (%.1f%%)"
                  % (bars(used, cap), human_bytes(used), limit, 100.0 * used / cap))
            except (ValueError, ZeroDivisionError):
                pass

        # WiFi / 终端
        A(self.c("-" * 74, "dim"))
        sec = {}
        mw = snap.get("multiwifi")
        ss = mw.get("Ssids") if isinstance(mw, dict) else None
        for s in as_list(ss.get("Ssid") if isinstance(ss, dict) else None):
            if isinstance(s, dict) and str(s.get("Index")) in ("0", "None"):
                sec = s
                break
        A(" WLAN        %s  信道 %s  带宽 %s  模式 %s  加密 %s  隐藏 %s  防蹭网隔离 %s"
          % (wifi.get("WifiSsid") or "-", wifi.get("WifiChannel") or "-",
             wifi.get("wifibandwidth") or "-", wifi.get("WifiMode") or "-",
             sec.get("WifiAuthmode") or "-", wifi.get("WifiHide") or "-",
             wifi.get("WifiIsolate") or "-"))
        A(" 接入终端   %s 台在线 / 共记录 %s 台   （设备上报 %s/%s）"
          % (self.c(str(co["hosts_active"]), "green"), co["hosts_total"],
             st.get("CurrentWifiUser") or "?", st.get("TotalWifiUser") or "?"))
        host_rows = co["hosts"]
        if not self.show_all_hosts:
            host_rows = [h for h in host_rows if h["active"]]
            hidden = co["hosts_total"] - len(host_rows)
            if hidden:
                A(self.c("   （另有 %d 台曾连接但当前离线，加 --all 一同显示）" % hidden, "dim"))
        for h in sorted(host_rows, key=lambda x: (not x["active"], -x.get("rate_down", 0), x["name"])):
            tag = self.c("在线", "green") if h["active"] else self.c("离线", "dim")
            rate = ""
            if h.get("rate_mode"):
                rate = "  ↓%s ↑%s" % (human_rate(h["rate_down"]), human_rate(h["rate_up"]))
                rate += self.c(" (设备自报)" if h["rate_mode"] == "dev"
                               else " (近%.0fs均速)" % (h.get("win_sec") or 0), "dim")
            elif not h["active"]:
                rate = self.c("  —", "dim")
            extra = " ".join(x for x in [h.get("band"), h.get("brand"), h.get("ip")] if x)
            A("   %s %-18s %-17s %s%s" % (tag, h["name"][:18], (h.get("mac") or "-")[:17],
                                          extra, self.c(rate, "cyan")))

        # 套餐流量使用情况
        pl = co.get("plan") or {}
        if pl.get("limit"):
            pct = pl.get("percent") or 0
            color = "red" if pl.get("over_limit") else ("yellow" if pl.get("over_threshold")
                                                        else "green")
            A(self.c("-" * 74, "dim"))
            A(" 套餐流量   %s %s / %s  已用 %.1f%%  剩余 %s"
              % (self.c(bars(pct, 100), color), human_bytes(pl["used"]), pl["limit_raw"],
                 pct, human_bytes(pl.get("remain"))))
            A(" 计费周期   每月 %s 日起%s  提醒阈值 %s%%  本周期剩 %s 天  上次结算 %s%s"
              % (pl.get("start_day") or "1", "" if pl.get("enabled") else "（未启用统计）",
                 pl.get("threshold") or "-", pl.get("days_left", "-"),
                 pl.get("clear_time") or "-",
                 self.c("   用量已超过提醒阈值！", "red") if pl.get("over_threshold") else ""))
            A(" 今日/本月  今日 %s   本月 ↓%s ↑%s"
              % (human_bytes(pl.get("day_used")), human_bytes(pl.get("month_download")),
                 human_bytes(pl.get("month_upload"))))
        # 短信
        A(self.c("-" * 74, "dim"))
        sms = snap.get("sms") or {}
        msgs = sms.get("list") or []
        A(" 短信       未读 %d 条 / 收件箱 %d 条（正文请用 `sms` 子命令或网页面板查看）"
          % (co.get("sms_unread", 0), co.get("sms_inbox", 0)))
        for m in msgs[:2 if not self.show_all_hosts else 5]:
            mark = self.c("未读", "yellow") if m["unread"] else self.c("已读", "dim")
            A("   %s %-12s %-20s %s" % (mark, (m.get("phone") or "-")[:12],
                                        m.get("date") or "-",
                                        (m.get("content") or "").replace("\n", " ")[:40]))

        # 设备 / SIM
        A(self.c("-" * 74, "dim"))
        A(" 设备       IMEI %s  SN %s" % (dev.get("Imei") or "-", dev.get("SerialNumber") or "-"))
        A(" 固件       %s / %s  WebUI %s   已运行 %s"
          % (dev.get("HardwareVersion") or "-", dev.get("SoftwareVersion") or "-",
             dev.get("WebUIVersion") or "-",
             human_dur(dev.get("uptime") or (snap.get("boot") or {}).get("boot_time"))))
        A(" SIM        %s  ICCID %s  IMSI %s  号码 %s  漫游 %s  飞行模式 %s"
          % (co["sim"], dev.get("Iccid") or "-", dev.get("Imsi") or "-",
             dev.get("Msisdn") or "-", "是" if str(st.get("RoamingStatus")) == "1" else "否",
             "开" if str(st.get("flymode")) == "1" else "关"))
        wan = dev.get("WanIPAddress") or st.get("PrimaryDns")
        A(" 出口       WAN IP %s  IPv6 %s" % (wan or "-", dev.get("WanIPv6Address") or "-"))
        A(" DNS        %s / %s" % (st.get("PrimaryDns") or "-", st.get("SecondaryDns") or "-"))
        A(" 电源       电量 %s%%  档位 %s  USB %s  %s(BatteryStatus=%s)"
          % (st.get("BatteryPercent") or "-", st.get("BatteryLevel") or "-",
             "已接入（供电中）" if str(st.get("usbup")) == "1" else "未接入",
             ("［充电中］" if co.get("battery_state") == "充电中"
              else co.get("battery_state") or "-"), st.get("BatteryStatus") or "-"))
        # 说明：BatteryStatus 的 0/1 含义由多次实测推断（USB 接入且未满电=1 充电中，
        # 充满或电池供电=0），故同时输出原始码供核对。

        # 历史速率曲线
        if hist:
            A(self.c("-" * 74, "dim"))
            A(" 近 %d 次采样  下 %s" % (len(hist), self.spark([h[0] for h in hist])))
            A("              上 %s" % self.spark([h[1] for h in hist]))
        if not snap.get("logged_in"):
            A(self.c(" 提示：当前为免登录模式，RSRP/终端列表/IMEI 等需要登录的字段不可用", "yellow"))
        if snap.get("auth_error"):
            A(self.c(" 登录失败：%s" % snap["auth_error"], "red"))
        if snap.get("login_wait"):
            A(self.c(" 设备限制登录频率，%d 秒后自动重试（期间仍可显示免登录字段）"
                     % snap["login_wait"], "yellow"))
        if snap.get("errors"):
            A(self.c(" 接口异常：%s" % ", ".join(sorted(snap["errors"])), "dim"))
        return "\n".join(lines)

    @staticmethod
    def spark(vals, blocks="▁▂▃▄▅▆▇█"):
        vals = [v or 0 for v in vals]
        mx = max(vals) or 1
        return "".join(blocks[min(7, int(v / mx * 7.999))] if v > 0 else blocks[0] for v in vals)


# ---------------------------------------------------------------- 子命令
_CLEANED = set()


def cleanup_client(dev):
    """退出时登出设备会话（幂等，只有做过登录的客户端才须登出）。"""
    key = id(dev)
    if key in _CLEANED:
        return
    _CLEANED.add(key)
    if not dev.use_login:
        return
    try:
        dev.logout(quiet=False)
    except Exception:  # noqa: BLE001
        pass


def make_client(args):
    dev = HuaweiWifi(args.host, args.user, args.password, timeout=args.timeout,
                     use_login=not args.no_login, debug=args.debug)
    dev.host_win_sec = getattr(args, "rate_window", 60.0)
    # 设备只允许单会话，退出时务必登出，把会话位还给网页后台。
    # Ctrl+C 会被下面各子命令捕获后正常 return，所以 atexit 能执行；
    # SIGTERM / Windows 的控制台关闭事件则显式接一下。
    atexit.register(cleanup_client, dev)

    def _on_signal(signum, frame):        # noqa: ARG001
        cleanup_client(dev)
        raise SystemExit(128 + signum)

    for name in ("SIGTERM", "SIGHUP", "SIGBREAK"):
        sig = getattr(signal, name, None)
        if sig is not None:
            try:
                signal.signal(sig, _on_signal)
            except (ValueError, OSError, RuntimeError):
                pass      # 非主线程/平台不支持则忽略
    if not args.no_login:
        if dev.login(force=True):
            print("已登录 %s（用户 %s）；退出时将自动登出" % (dev.base, args.user),
                  file=sys.stderr)
        else:
            print("登录失败：%s" % dev.last_login_error, file=sys.stderr)
            # 限流(108005)是暂时的，先用免登录字段继续跑，到点自动重试登录
            if not args.keep_going and dev.last_login_code != "108005":
                print("提示：可加 --keep-going 先只看免登录字段，或用 --no-login 不登录。",
                      file=sys.stderr)
                raise SystemExit(2)
            print("已继续运行，将按退避策略自动重试登录。", file=sys.stderr)
    return dev


def cmd_status(args):
    dev = make_client(args)
    snap = dev.snapshot()
    if args.json:
        print(json.dumps(snap, ensure_ascii=False, indent=1, default=str))
    else:
        print(Painter(args.color, args.all).render(snap))
    return 0


def cmd_watch(args):
    dev = make_client(args)
    p = Painter(args.color, args.all)
    hist = []
    last_hb = 0.0
    interval = max(1.0, args.interval)
    while True:
        try:
            snap = dev.snapshot()
            c = snap.get("computed") or {}
            hist.append((c.get("rate_down"), c.get("rate_up")))
            hist = hist[-60:]
            if time.time() - last_hb > 30:      # 会话空闲 ~60s 过期，主动保活
                last_hb = time.time()
                dev.heartbeat()
            frame = p.render(snap, hist if args.spark else None)
            if p.vt:
                sys.stdout.write("\033[H\033[2J" + frame + "\n")
            elif p.tty:
                os.system("cls" if os.name == "nt" else "clear")
                print(frame)
            else:                               # 重定向到文件/管道时追加分隔，不清屏
                print(frame)
                print("\n" + "~" * 74, end="\n\n")
            sys.stdout.flush()
            if args.once:
                return 0
            time.sleep(interval)
        except KeyboardInterrupt:
            sys.stdout.write("\033[0m\n")
            print("已退出。")
            return 0
        except (OSError, urllib.error.URLError, DeviceError) as e:
            print("读取失败：%s\n%s 秒后重试（Ctrl+C 退出）" % (e, interval), file=sys.stderr)
            time.sleep(interval)


def cmd_raw(args):
    dev = make_client(args)
    path = args.path if args.path.startswith("/") else "/api/" + args.path
    if args.login:
        dev.login(force=True)
    data = dev.get(path)
    print(json.dumps(data, ensure_ascii=False, indent=1))
    return 0


def cmd_probe(args):
    """探测设备支持的状态接口，便于适配其他型号。"""
    dev = HuaweiWifi(args.host, args.user, args.password, timeout=args.timeout,
                     use_login=False)
    dev.load_page()
    paths = [p for _k, p in FAST_GETS + SLOW_GETS] + [
        "/api/wlan/station-information", "/api/wlan/multi-security-settings",
        "/api/security/mac-filter", "/api/dhcp/settings", "/api/net/cell-info",
        "/api/net/lte-band-info", "/api/net/net-mode-list", "/api/sms/sms-count",
        "/api/device/boot_time", "/api/monitoring/converged-status",
        "/api/system/deviceinfo", "/api/language/current-language",
        "/api/monitoring/daily-data-limit", "/api/statistic/feature-roam-statistic",
    ]
    results = {"anon": {}, "auth": {}}
    for p in paths:
        try:
            results["anon"][p] = len(json.dumps(dev.get(p), ensure_ascii=False))
        except DeviceError as e:
            results["anon"][p] = e.code or "err"
    if dev.login(force=True):
        for p in paths:
            try:
                results["auth"][p] = len(json.dumps(dev.get(p), ensure_ascii=False))
            except DeviceError as e:
                results["auth"][p] = e.code or "err"
    else:
        print("登录失败：%s（仅输出免登录结果）" % dev.last_login_error, file=sys.stderr)
    print("%-42s %-14s %s" % ("接口", "免登录", "登录后(字段数/错误码)"))
    for p in paths:
        print("%-42s %-14s %s" % (p, results["anon"].get(p), results["auth"].get(p, "-")))
    dev.logout()          # 探测完把会话位还给用户
    return 0


def cmd_sms(args):
    """短信：列表 / 标记已读 / 删除。"""
    if args.delete and not args.yes:
        # 先卡确认，避开不必要的登录（设备登录有频率限制）
        print("删除短信不可恢复，确认请再加 --yes", file=sys.stderr)
        return 4
    dev = make_client(args)
    if args.mark:
        idx = [i.strip() for i in re.split(r"[,\s]+", args.mark) if i.strip()]
        print(dev.do_action("sms_read", idx))
        return 0
    if args.delete:
        idx = [i.strip() for i in re.split(r"[,\s]+", args.delete) if i.strip()]
        print(dev.do_action("sms_delete", idx))
        return 0
    data = dev.sms_list(args.page, args.size)
    cnt = dev.sms_count()
    print("收件箱 %s 条（未读 %s）  发件箱 %s  草稿 %s  本页会话分组 %s 个" % (
        cnt.get("inbox"), cnt.get("unread"), cnt.get("outbox"), cnt.get("draft"),
        data["count"]))
    p = Painter(args.color)
    for i, m in enumerate(data["list"], 1):
        mark = p.c("未读", "yellow") if m["unread"] else p.c("已读", "dim")
        print("%3d. [%s] %s  %s  %s" % (i, m["index"], mark, m["phone"], m["date"]))
        for line in (m["content"] or "").splitlines() or [""]:
            print("     " + line)
        print()
    print("标记已读：python hw_status.py sms --mark 40036,40032")
    return 0


def cmd_plan(args):
    """套餐流量：查看与设置。"""
    dev = make_client(args)
    if args.limit or args.start_day is not None or args.threshold is not None \
            or args.enable is not None:
        en = None if args.enable is None else ("1" if args.enable else "0")
        print(dev.do_action("set_plan", {"limit": args.limit, "start_day": args.start_day,
                                         "threshold": args.threshold, "enabled": en}))
    pl = dev.plan()
    if args.json:
        print(json.dumps(pl, ensure_ascii=False, indent=1, default=str))
        return 0
    p = Painter(args.color)
    if not pl.get("limit"):
        print("设备未设置套餐流量限额（start_date 的 DataLimit 为空或 0）")
    bar = p.c(bars(pl.get("percent") or 0, 100), "red" if pl.get("over_limit")
              else ("yellow" if pl.get("over_threshold") else "green"))
    print(" 套餐限额   %s / %s   启用统计=%s" % (human_bytes(pl.get("used")),
                                                pl.get("limit_raw") or "-",
                                                "是" if pl.get("enabled") else "否"))
    print(" 已用/剩余   %.1f%% / %s   提醒阈值 %s%%%s" % (pl.get("percent") or 0,
                                                        human_bytes(pl.get("remain")),
                                                        pl.get("threshold") or "-",
                                                        p.c("   已超阈值", "red")
                                                        if pl.get("over_threshold") else ""))
    print(" 计费周期   每月 %s 日起，本周期剩 %s 天，上次结算 %s" % (
        pl.get("start_day") or "1", pl.get("days_left", "-"), pl.get("clear_time") or "-"))
    print(" 本月流量   ↓ %s  ↑ %s  合计 %s   联网 %s"
          % (human_bytes(pl.get("month_download")), human_bytes(pl.get("month_upload")),
             human_bytes(pl.get("used")), human_dur(pl.get("month_duration"))))
    print(" 今日       %s（日限额 %s）" % (human_bytes(pl.get("day_used")),
                                          pl.get("day_limit_raw") or "未设置"))
    print("\n设置：python hw_status.py plan --limit 100GB --start-day 1 --threshold 90 --enable 1")
    return 0


def cmd_act(args):
    """设备操作：重启 / 移动数据开关 / 清零历史流量。"""
    if args.action in ("reboot", "clear-traffic") and not args.yes:
        # 先卡确认，避开不必要的登录（设备登录有频率限制）
        print("%s 是不可逆/影响使用的操作，确认请再加 --yes" % args.action, file=sys.stderr)
        return 4
    dev = make_client(args)
    name = {"reboot": "reboot", "clear-traffic": "clear_traffic",
            "data-on": "data_on", "data-off": "data_off"}[args.action]
    print(dev.do_action(name))
    return 0


def cmd_logout(args):
    dev = make_client(args)
    dev.logout()
    print("已注销设备上的登录会话。")
    return 0


# ---------------------------------------------------------------- 网页面板
def cmd_serve(args):
    import threading
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

    dev = make_client(args)
    dev.sms_interval = 0 if args.no_sms else max(10.0, args.sms_interval)
    hist = []
    lock = threading.Lock()          # 设备会话唯一，串行化轮询与操作
    page = HTML.encode("utf-8")

    def json_body(obj, code=200):
        return code, json.dumps(obj, ensure_ascii=False, default=str).encode("utf-8")

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *a):  # 安静
            pass

        def _send(self, code, body, ctype="text/html; charset=utf-8"):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):  # noqa: N802
            path = self.path.split("?", 1)[0]        # 允许 ?v=xxx 强制刷新缓存
            qs = urllib.parse.parse_qs(self.path.split("?", 1)[1] if "?" in self.path else "")
            if path.startswith("/data"):
                try:
                    with lock:
                        snap = dev.snapshot()
                    c = snap.get("computed") or {}
                    hist.append([snap["ts"], c.get("rate_down"), c.get("rate_up"),
                                 c.get("rsrp"), c["signal_bars"][0]])
                    del hist[:-180]
                    code, body = json_body({"snapshot": _slim(snap), "hist": hist})
                    return self._send(code, body, "application/json; charset=utf-8")
                except Exception as e:  # noqa: BLE001
                    code, body = json_body({"error": str(e)})
                    return self._send(500, body, "application/json; charset=utf-8")
            if path.startswith("/sms"):
                try:
                    with lock:
                        data = dev.sms_list(int(qs.get("page", [1])[0]),
                                            int(qs.get("size", [args.sms_size])[0]))
                    code, body = json_body({"ok": True, "data": data})
                except Exception as e:  # noqa: BLE001
                    code, body = json_body({"ok": False, "error": str(e)})
                return self._send(code, body, "application/json; charset=utf-8")
            if path in ("/", "/index.html"):
                return self._send(200, page)
            return self._send(404, b"not found", "text/plain; charset=utf-8")

        def do_POST(self):  # noqa: N802
            path = self.path.split("?", 1)[0]
            if not path.startswith("/act"):
                return self._send(404, b"not found", "text/plain; charset=utf-8")
            try:
                n = int(self.headers.get("Content-Length") or 0)
                req = json.loads(self.rfile.read(n).decode("utf-8") or "{}")
                arg = req.get("arg")
                if req.get("name") in ("sms_read", "sms_delete") and arg:
                    arg = [str(a) for a in arg]
                with lock:
                    msg = dev.do_action(req.get("name"), arg)
                    dev._sms_at = 0.0        # 动作后让下一轮重新拉短信/套餐
                    dev._slow_at = 0.0
                code, body = json_body({"ok": True, "msg": msg})
            except Exception as e:  # noqa: BLE001
                code, body = json_body({"ok": False, "error": str(e)})
            return self._send(code, body, "application/json; charset=utf-8")

    srv = ThreadingHTTPServer((args.bind, args.port), Handler)
    url = "http://%s:%d/" % ("127.0.0.1" if args.bind == "127.0.0.1" else args.bind, args.port)
    print("面板已启动： %s   （Ctrl+C 停止）" % url)
    if args.open:
        try:
            os.startfile(url)  # noqa: S606
        except Exception:  # noqa: BLE001
            pass
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止。")
    return 0


def _slim(snap):
    """精简 JSON，去掉网页用不到的字段。"""
    return json.loads(json.dumps(snap, ensure_ascii=False, default=str))


HTML = """<!doctype html><html lang="zh-cn"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>华为随身WiFi 实时监控</title><style>
:root{--bg:#0d1117;--card:#161b22;--tx:#e6edf3;--dim:#8b949e;--ok:#3fb950;--warn:#d29922;--bad:#f85149;--ac:#58a6ff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--tx);
font:14px/1.5 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif}
header{padding:14px 20px;display:flex;flex-wrap:wrap;gap:12px;align-items:center;
border-bottom:1px solid #21262d;background:#0d1117cc;position:sticky;top:0;backdrop-filter:blur(6px)}
h1{font-size:16px;margin:0 12px 0 0}h2{font-size:13px;color:var(--dim);margin:0 0 10px;font-weight:600}
.pill{padding:3px 10px;border-radius:99px;font-size:12px;background:#21262d}
.on{background:#1f3524;color:var(--ok)}.off{background:#3a1d1d;color:var(--bad)}
main{padding:18px;display:grid;gap:16px;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));max-width:1500px}
.card{background:var(--card);border:1px solid #21262d;border-radius:10px;padding:16px}
.kv{display:grid;grid-template-columns:96px 1fr;gap:5px 10px;font-size:13px}
.kv div:nth-child(odd){color:var(--dim)}
.big{font-size:30px;font-weight:650;letter-spacing:-.5px}
.row{display:flex;gap:18px;flex-wrap:wrap;align-items:flex-end}
.bars{display:flex;gap:3px;align-items:flex-end;height:34px}
.bars i{width:9px;background:#30363d;border-radius:2px;display:block}
.bars i.f{background:var(--ac)}
/* 顶部状态栏：信号格 / 电池 图标 */
.tbx{display:inline-flex;align-items:flex-end;gap:2px;height:18px;cursor:default}
.tbx i{background:#30363d;border-radius:1px;display:block}
.tbx i.f{background:var(--ok)}
.bat{display:inline-flex;align-items:center;gap:5px;font-size:12px;color:var(--dim);cursor:default}
.bat .shell{position:relative;display:inline-block;width:27px;height:14px;
border:1.5px solid #8b949e;border-radius:3px;padding:1.5px}
.bat .shell::after{content:"";position:absolute;right:-5px;top:2.5px;width:2.5px;height:6px;
background:#8b949e;border-radius:0 2px 2px 0}
.bat .fill{display:block;height:100%;border-radius:1px;background:var(--ok);transition:width .3s}
.bat.chg .shell{border-color:var(--ok)}
.bat .bolt{color:var(--ok);font-size:11px}
table{width:100%;border-collapse:collapse;font-size:12.5px}
.scroll{overflow-x:auto;-webkit-overflow-scrolling:touch}
th,td{padding:6px 8px;border-bottom:1px solid #21262d;text-align:left;white-space:nowrap}
th{color:var(--dim);font-weight:500}
canvas{width:100%;height:150px;display:block}
.bar{height:8px;background:#21262d;border-radius:99px;overflow:hidden}
.bar>span{display:block;height:100%;background:linear-gradient(90deg,var(--ac),#a371f7)}
.muted{color:var(--dim);font-size:12px}.full{grid-column:1/-1}
.tag{font-size:11px;padding:1px 6px;border-radius:4px;background:#21262d;color:var(--dim)}
.err{color:var(--bad);font-size:12px}
.btn{background:#21262d;color:var(--tx);border:1px solid #30363d;border-radius:7px;
padding:6px 12px;font-size:12.5px;cursor:pointer;font-family:inherit}
.btn:hover{border-color:var(--ac);background:#1c2530}
.btn.pri{background:#1f6feb;border-color:#1f6feb;color:#fff}
.btn.warn{background:#3a2410;border-color:#9e6a03;color:#e3b341}
.btn.danger{background:#3a1d1d;border-color:#a5403a;color:#ffa198}
.btn:disabled{opacity:.45;cursor:not-allowed}
.ops{display:flex;flex-wrap:wrap;gap:9px;align-items:center}
input[type=text],input[type=number],select{background:#0d1117;color:var(--tx);border:1px solid #30363d;
border-radius:6px;padding:5px 8px;font-size:12.5px;width:104px;font-family:inherit}
label.inl{display:inline-flex;align-items:center;gap:5px;color:var(--dim);font-size:12.5px}
.thin{height:14px;border-radius:99px;position:relative}
.thin>span{border-radius:99px}
.thin em{position:absolute;top:-2px;bottom:-2px;width:2px;background:#f85149;opacity:.85}
.smsgrid{display:flex;gap:10px;align-items:flex-start;padding:9px 0;border-bottom:1px solid #21262d}
.smsgrid.unread{background:linear-gradient(90deg,#1c2a1e55,transparent);border-left:2px solid var(--ok);
padding-left:9px;margin-left:-9px}
.smsgrid .bd{flex:1;min-width:0;white-space:pre-wrap;word-break:break-word;font-size:13px}
.smsgrid .hd{color:var(--dim);font-size:12px;margin-bottom:3px;display:flex;gap:9px;flex-wrap:wrap}
#toast{position:fixed;right:16px;bottom:16px;background:#1f6feb;color:#fff;padding:10px 14px;
border-radius:8px;font-size:13px;opacity:0;transition:.25s;pointer-events:none;max-width:60vw}
#toast.bad{background:#a5403a}
/* 顶部图标按钮（短信）*/
.iconbtn{display:inline-flex;align-items:center;gap:5px;background:#21262d;border:1px solid #30363d;
border-radius:99px;padding:3px 10px 3px 8px;cursor:pointer;color:var(--dim);font:inherit;font-size:12px}
.iconbtn:hover{border-color:var(--ac);color:var(--tx)}
.iconbtn svg{display:block}
.badge{background:var(--bad);color:#fff;border-radius:99px;font-size:11px;line-height:1;
padding:2px 6px;font-weight:700}
/* 电池闪电图标 */
.bolt{position:absolute;inset:0;display:flex;align-items:center;justify-content:center}
.bolt svg{filter:drop-shadow(0 0 2px #0d1117)}
/* 弹窗 */
#mask,#mask2{position:fixed;inset:0;background:#010409c9;display:none;align-items:center;justify-content:center;
z-index:60;padding:18px}
#mask2{z-index:70}
#mask.on,#mask2.on{display:flex}
.dlg{background:var(--card);border:1px solid #30363d;border-radius:12px;width:min(860px,96vw);
max-height:88vh;display:flex;flex-direction:column;overflow:hidden}
.dlg.small{width:min(460px,94vw)}
.dlg h3{margin:0;padding:13px 16px;border-bottom:1px solid #21262d;font-size:14px;
display:flex;align-items:center;gap:9px}
.dlg h3 .x{margin-left:auto;cursor:pointer;color:var(--dim);font-size:17px;line-height:1;padding:0 3px}
.dlg h3 .x:hover{color:var(--tx)}
.dlg .body{padding:12px 16px;overflow:auto;font-size:13px}
.dlg .foot{padding:11px 16px;border-top:1px solid #21262d;display:flex;gap:9px;align-items:center;
flex-wrap:wrap;background:#12171e}
.danger h3{background:#2d1416;color:#ffa198}
.chk{display:flex;gap:8px;align-items:flex-start;font-size:13px;line-height:1.45;cursor:pointer}
.chk input{margin-top:2px}
ul.risk{margin:6px 0 0;padding-left:18px;color:var(--dim);font-size:12.5px}
#mask.cold .btn.pri{background:#30363d;border-color:#30363d;color:var(--dim);cursor:not-allowed}
</style></head><body>
<header><h1>华为随身 WiFi 实时监控</h1>
<span id="conn" class="pill">连接中…</span><span id="model" class="pill"></span>
<span id="net" class="pill"></span>
<span class="tbx" id="hsig" title="信号"></span><span class="bat" id="hbat"></span>
<button class="iconbtn" id="hsms" onclick="openSms()" title="点击查看短信">
<svg width="15" height="12" viewBox="0 0 20 15" aria-hidden="true"><rect x=".7" y=".7" width="18.6"
height="13.6" rx="2" fill="none" stroke="currentColor" stroke-width="1.4"/>
<path d="M1.5 2.2 10 8.6 18.5 2.2" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>
<span id="hsmsN"></span></button>
<span id="time" class="muted"></span></header>
<main>
<div class="card"><h2>网络 / 信号</h2><div class="row"><div><div class="big" id="rsrp">-</div>
<div class="muted">RSRP dBm · <span id="rsrplv"></span></div></div><div class="bars" id="bars"></div></div>
<div class="kv" style="margin-top:12px" id="netkv"></div></div>
<div class="card"><h2>实时速率</h2><div class="row"><div><div class="big" id="dn">-</div>
<div class="muted">↓ 下行</div></div><div><div class="big" id="up">-</div><div class="muted">↑ 上行</div></div></div>
<canvas id="chart"></canvas><div class="kv" id="tr" style="margin-top:10px"></div></div>
<div class="card"><h2>设备 / SIM</h2><div class="kv" id="dev"></div></div>
<div class="card"><h2>WLAN</h2><div class="kv" id="wifi"></div>
<div style="margin-top:10px" class="muted">电量 <span id="batv"></span></div>
<div class="bar"><span id="bat"></span></div></div>
<div class="card full"><h2>操作</h2><div class="ops">
<button class="btn" onclick="openSms()">查看短信<span id="smsHint"></span></button>
<button class="btn" id="bdata" onclick="toggleData()">移动数据：关</button>
<button class="btn warn" onclick="askDanger({title:'清零历史流量统计',
 lines:['设备上的本月/累计流量计数将被归零，无法恢复。','已经统计到的用量历史会丢失。'],
 run:()=>act('clear_traffic')})">清零历史流量</button>
<button class="btn danger" onclick="askDanger({title:'重启随身 WiFi',
 lines:['设备会立即重启，期间全部终端断网约 1~2 分钟。','面板会自动等待会话恢复并继续监控。'],
 run:()=>act('reboot')})">重启设备</button>
<span class="muted" id="actmsg"></span></div>
<div class="muted" style="margin-top:8px">高危操作要两步确认：第 1 步逐条看风险并勾选，第 2 步等 3 秒解锁后点“确认执行”；普通保存只需一次确认。</div></div>
<div class="card"><h2>套餐流量使用情况</h2>
<div class="big" id="pPct">-</div>
<div class="bar thin" style="margin:8px 0 6px"><span id="pBar"></span><em id="pThr" style="left:90%"></em></div>
<div class="kv" id="plan"></div>
<div style="margin-top:12px" class="ops">
<label class="inl">限额<input type="text" id="fLimit" placeholder="100GB"></label>
<label class="inl">每月<input type="number" id="fDay" min="1" max="28" style="width:62px">日起</label>
<label class="inl">阈值<input type="number" id="fThr" min="1" max="100" style="width:62px">%</label>
<button class="btn pri" onclick="savePlan()">保存套餐设置</button></div>
<div class="muted" style="margin-top:6px">留空表示不改动该项。</div></div>
<div class="card full"><h2>接入终端 <span class="muted" id="hcount"></span></h2>
<div class="scroll"><table><thead><tr><th></th><th>名称</th><th>IP</th><th>MAC</th><th>频段</th><th>厂商</th>
<th>↓ 实时</th><th>↑ 实时</th><th>累计↓</th><th>累计↑</th></tr></thead><tbody id="hosts"></tbody></table></div>
<div class="muted" style="margin-top:10px" id="errs"></div>
<div class="muted" style="margin-top:8px">每 2 秒自动刷新。关于“实时”列：实测设备的每机字节计数器是<b>不规则批量刷新</b>的（可 40~60 秒完全不动，然后一次跳增几十 MB；而设备自报的速率字段也严重滞后——真实 2.67MB/s 的下载它只报 29KB/s），只拿相邻两次采样做分会长时间显示 0；所以本工具取 60 秒窗口对累计值求均速，括号里标出实际窗口长度。它是区间均速而非瞬时值，最多滞后一个刷新周期（实测真实 1464KB/s 时面板读出 1231KB/s）。累计值完全没动时才退回设备自报值。无数据显示“—”，离线终端不显示速率；可用 --rate-window 调整窗口。注意：本工具与设备后台 192.168.8.1 网页共用同一个登录会话（设备只允许单会话），两边会互相顶号；工具退出时会自动登出，把会话位还给网页后台。</div></div>
<div id="toast"></div>
<div id="mask" onclick="if(event.target===this)closeDlg()">
<div class="dlg" id="dlg">
<h3><span class="t" id="dTitle">标题</span><span class="x" onclick="closeDlg()" title="关闭">×</span></h3>
<div class="body" id="dBody"></div>
<div class="foot" id="dFoot"></div></div></div>
<div id="mask2" onclick="if(event.target===this)dlgClose('#mask2')">
<div class="dlg small danger">
<h3><span class="t" id="eTitle"></span><span class="x" onclick="dlgClose('#mask2')" title="取消">×</span></h3>
<div class="body" id="eBody"></div>
<div class="foot" id="eFoot"></div></div></div>
</main><script>
const $=s=>document.querySelector(s);
const B=1024;
const hb=n=>{if(n===null||n===undefined||isNaN(n))return '-';let u=['B','KB','MB','GB','PB','EB'],i=0,v=Math.abs(+n);
while(v>=1024&&i<u.length-1){v/=1024;i++;}return (n<0?'-':'')+v.toFixed(v<10&&i?1:0)+' '+u[i];};
const hr=n=>n==null?'-':hb(n)+'/s';
// 每机速率：后端已选定（设备自报瞬时值优先，累计字节窗口均值补完）；离线终端不显示速率
const rt=h=>h.rate_mode?hr(h.rate_down):'—';
const ru=h=>h.rate_mode?hr(h.rate_up):'—';const esc=s=>(s==null?'':(''+s)).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const kv=(el,o)=>{el.innerHTML=Object.entries(o).map(([k,v])=>`<div>${esc(k)}</div><div>${v??'-'}</div>`).join('');};
let H=[],last=0,smsPage=1,smsData=null,smsOpen=false,smsSize=20,dataOn=true,dlgCb=null;
function toast(msg,bad){const t=$('#toast');t.textContent=msg;t.className=bad?'bad':'';
t.style.opacity=1;clearTimeout(toast.h);toast.h=setTimeout(()=>t.style.opacity=0,bad?5200:2600);}
function openDlg(o){
  $('#dlg').className='dlg'+(o.small?' small':'')+(o.danger?' danger':'');
  $('#dTitle').textContent=o.title||'';$('#dBody').innerHTML=o.body||'';$('#dFoot').innerHTML=o.foot||'';
  $('#mask').classList.add('on');dlgCb=o.onClose||null;}
function closeDlg(){$('#mask').classList.remove('on');$('#dlg').classList.remove('danger');
  smsOpen=false;if(dlgCb){const f=dlgCb;dlgCb=null;f();}}
/* 第二个叠在上层的弹窗：专门给高危确认用，不会弄掉底下的短信窗口 */
function dlgOpen(sel,o){const root=$(sel);root.querySelector('.dlg').className='dlg small danger';
  root.querySelector('.t').textContent=o.title||'';
  root.querySelector('.body').innerHTML=o.body||'';root.querySelector('.foot').innerHTML=o.foot||'';
  root.classList.add('on');}
function dlgClose(sel){$(sel).classList.remove('on');}
document.addEventListener('keydown',e=>{if(e.key!=='Escape')return;
  if($('#mask2').classList.contains('on'))dlgClose('#mask2');else closeDlg();});
/* 高危操作：两步确认。第 1 步逐条看风险并勾选，第 2 步 3 秒倒计时解锁后才能执行。*/
function askDanger(o){
  window._danger=o;
  dlgOpen('#mask2',{title:'高危操作确认（第 1/2 步）· '+o.title,
    body:`<ul class="risk">${(o.lines||[]).map(l=>`<li>${esc(l)}</li>`).join('')}</ul>
      <label class="chk" style="margin-top:13px"><input type="checkbox" id="dOk"
        onchange="$('#dGo').disabled=!this.checked"><span>我已逐条了解以上后果，仍然要执行“${esc(o.title)}”</span></label>`,
    foot:`<button class="btn" onclick="dlgClose('#mask2')">取消</button>
      <button class="btn pri" id="dGo" style="margin-left:auto" disabled
        onclick="armDanger()">下一步</button>`});}
function armDanger(){
  const o=window._danger;if(!o||!$('#dOk').checked)return;
  let left=3;
  dlgOpen('#mask2',{title:'高危操作确认（第 2/2 步）· '+o.title,
    body:`<div style="font-size:14px"><b>${esc(o.title)}</b> —— 指令将立即下发到设备，不可撤销。</div>
      <ul class="risk">${(o.lines||[]).map(l=>`<li>${esc(l)}</li>`).join('')}</ul>`,
    foot:`<button class="btn" onclick="dlgClose('#mask2')">取消</button>
      <span class="muted" id="dHint"></span>
      <button class="btn danger" id="dGo" style="margin-left:auto" disabled>请等待 ${left}s</button>`});
  const b=$('#dGo'),hint=$('#dHint');
  clearInterval(window._armT);
  window._armT=setInterval(()=>{left-=1;
    if(left<=0){clearInterval(window._armT);b.disabled=false;b.textContent='确认执行';
      hint.textContent='已解锁，点击才真正执行';return;}
    b.textContent='请等待 '+left+'s';hint.textContent='倒计时防误触';},1000);
  b.onclick=fireDanger;}
function fireDanger(){
  const b=$('#dGo');if(!b||b.disabled)return;
  clearInterval(window._armT);const f=window._danger&&window._danger.run;
  window._danger=null;dlgClose('#mask2');if(f)f();}
async function act(name,arg){
  $('#actmsg').textContent='执行中…';
  try{const r=await (await fetch('/act',{method:'POST',headers:{'Content-Type':'application/json'},
    body:JSON.stringify({name:name,arg:arg})})).json();
    if(r.ok){$('#actmsg').textContent=r.msg;toast(r.msg);if(smsOpen)loadSms(smsPage);tick();return r;}
    $('#actmsg').textContent='失败: '+r.error;toast('操作失败：'+r.error,true);}
  catch(e){$('#actmsg').textContent='失败: '+e;toast('操作失败：'+e,true);}
  return null;}
function toggleData(){
  if(!dataOn){act('data_on');return;}
  askDanger({title:'关闭移动数据',
    lines:['关闭后所有下挂终端都无法上网（仅 WLAN 局域网仍可用）。',
           '随身 WiFi 自身的蜂窝数据开关会被关掉，需再次点本按钮才能恢复。'],
    run:()=>act('data_off')});}
async function savePlan(){
  const a={};const l=$('#fLimit').value.trim(),d=$('#fDay').value,t=$('#fThr').value;
  if(l)a.limit=/^[0-9.]+\\s*([KMGT]?B)?$/i.test(l)?l.replace(/\\s+/g,''):null;
  if(a.limit===null)return toast('限额格式不对，例：100GB / 2048MB',true);
  if(d)a.start_day=+d; if(t)a.threshold=+t;
  if(!Object.keys(a).length)return toast('没有要修改的项（留空即不改）',true);
  const label={limit:'月度限额',start_day:'计费起始日',threshold:'提醒阈值'};
  openDlg({small:true,title:'确认修改套餐流量设置',
    body:`<div>将向设备写入以下设置（未填写的项保持设备当前值）：</div>
      <ul class="risk" style="color:var(--tx)">${Object.entries(a).map(([k,v])=>
        `<li>${label[k]||k}：${esc(v)}</li>`).join('')}</ul>`,
    foot:'<button class="btn" onclick="closeDlg()">取消</button>'+
      '<button class="btn pri" id="pGo" style="margin-left:auto">确认保存</button>'});
  $('#pGo').onclick=()=>{closeDlg();act('set_plan',a);
    setTimeout(()=>{['fLimit','fDay','fThr'].forEach(k=>$('#'+k).value='');},400);};}
/* 短信：点图标弹窗看 */
function smsChk(i,ck){smsChk.on=smsChk.on||new Set();if(ck)smsChk.on.add(String(i));
  else smsChk.on.delete(String(i));syncAll();}
function openSms(){smsOpen=true;loadSms(smsPage||1);}
async function loadSms(page){
  page=Math.max(1,page|0);const size=(smsSize||20);
  let j;try{j=await (await fetch('/sms?page='+page+'&size='+size)).json();}
  catch(e){toast('短信读取失败：'+e,true);return;}
  if(!j.ok){toast('短信读取失败：'+j.error,true);return;}
  smsPage=page;smsData=j.data;smsChk.on=new Set((j.data.list||[])
    .filter(m=>m.unread).map(m=>String(m.index)));renderSms();}
function renderSms(){
  const d=smsData;if(!d)return;
  const list=d.list||[];
  const body=list.length?list.map(m=>`<div class="smsgrid${m.unread?' unread':''}">
<input type="checkbox" ${smsChk.on.has(String(m.index))?'checked':''} onchange="smsChk('${esc(m.index)}',this.checked)"
 style="margin-top:3px">
<div class="bd"><div class="hd"><b>${esc(m.phone)}</b><span>${esc(m.date)}</span>
${m.unread?'<span class="tag" style="color:#3fb950">未读</span>':''}
${+m.unread_count>1?'<span class="tag">共 '+esc(m.unread_count)+' 条</span>':''}
<span class="tag" style="opacity:.55">#${esc(m.index)}</span></div>${esc(m.content)}</div></div>`).join('')
    :'<div class="muted">没有短信</div>';
  openDlg({title:'短信 · 未读 '+(window.UNREAD||0)+' 条 · 收件箱 '+(window.INBOX||'?')+' 条',
    body:body,
    foot:`<label class="chk"><input type="checkbox" id="dAll"
      onchange="smsAll(this.checked)"><span class="muted">全选</span></label>
      <button class="btn" onclick="loadSms(${Math.max(1,smsPage-1)})">← 上一页</button>
      <button class="btn" onclick="loadSms(${smsPage+1})">下一页 →</button>
      <span class="muted">第 ${d.page} 页 · 本页 ${list.length} 组</span>
      <span style="margin-left:auto"></span>
      <button class="btn" onclick="loadSms(${smsPage})">刷新</button>
      <button class="btn pri" onclick="smsAct('sms_read')">标记已读</button>
      <button class="btn danger" onclick="smsAct('sms_delete')">删除所选</button>`,
    onClose:()=>{smsOpen=false;}});
  smsOpen=true;syncAll();}
function syncAll(){$('#dAll')&&($('#dAll').checked=(smsData.list||[]).length>0
  &&(smsData.list||[]).every(m=>smsChk.on.has(String(m.index))));}
function smsAll(on){
  smsChk.on=on?new Set((smsData.list||[]).map(m=>String(m.index))):new Set();
  document.querySelectorAll('#dBody input[type=checkbox]').forEach(el=>{el.checked=!!on;});
  syncAll();}
async function smsAct(name){
  const idx=[...(smsChk.on||new Set())];
  if(!idx.length)return toast('先勾选要处理的短信',true);
  if(name==='sms_read'){await act('sms_read',idx);return;}
  askDanger({title:'删除 '+idx.length+' 条短信',
    lines:['删除后无法从设备上恢复。','选中的下标：'+idx.join(' , ')],
    run:()=>act('sms_delete',idx)});}
function renderPlan(pl){
  pl=pl||{};
  if(!pl.limit){$('#pPct').textContent='未设置';$('#pBar').style.width='0';
    $('#plan').innerHTML='<div>套餐限额</div><div>设备未设置（可在上面的表单里设）</div>';
    $('#pThr').style.display='none';return;}
  const pct=pl.percent||0;
  $('#pPct').textContent=pct.toFixed(1)+'%';
  $('#pPct').style.color=pl.over_limit?'#f85149':pl.over_threshold?'#d29922':'#3fb950';
  $('#pBar').style.width=Math.min(100,pct)+'%';
  $('#pBar').style.background=pl.over_limit?'linear-gradient(90deg,#f85149,#da3633)':
    pl.over_threshold?'linear-gradient(90deg,#d29922,#e3b341)':'linear-gradient(90deg,#58a6ff,#3fb950)';
  const thr=+pl.threshold||100;$('#pThr').style.display='';$('#pThr').style.left=Math.min(99,thr)+'%';
  $('#plan').innerHTML=Object.entries({'已用':hb(pl.used)+' / '+esc(pl.limit_raw),'剩余':hb(pl.remain),
    '提醒阈值':esc(pl.threshold)+'%'+(pl.over_threshold?' · 已超标':''),
    '计费起始':'每月 '+esc(pl.start_day)+' 日','本周期剩':(pl.days_left??'-')+' 天',
    '时间进度':(pl.day_percent??'-')+'%'+(pl.speed_ok===false?' · 用量快于时间进度':''),
    '本月':'↓ '+hb(pl.month_download)+' · ↑ '+hb(pl.month_upload),'今日':hb(pl.day_used),
    '上次结算':esc(pl.clear_time)}).map(([k,v])=>`<div>${k}</div><div>${v}</div>`).join('');
  if(!$('#fLimit').value)$('#fLimit').placeholder=pl.limit_raw||'100GB';
  if(!$('#fDay').value)$('#fDay').placeholder=pl.start_day||'1';
  if(!$('#fThr').value)$('#fThr').placeholder=pl.threshold||'90';}
function draw(){const c=$('#chart'),dpr=devicePixelRatio||1;const w=c.clientWidth,h=c.clientHeight;
c.width=w*dpr;c.height=h*dpr;const x=c.getContext('2d');x.scale(dpr,dpr);x.clearRect(0,0,w,h);
if(H.length<2)return;const dn=H.map(p=>p[1]||0),up=H.map(p=>p[2]||0);const m=Math.max(...dn,...up,1024);
const line=(arr,col)=>{x.beginPath();x.lineWidth=1.6;x.strokeStyle=col;arr.forEach((v,i)=>{
const px=i/(arr.length-1)*w,py=h-4-(v/m)*(h-10);i?x.lineTo(px,py):x.moveTo(px,py);});x.stroke();};
line(dn,'#58a6ff');line(up,'#d29922');x.fillStyle='#8b949e';x.font='11px sans-serif';
x.fillText('峰值 '+hb(m)+'/s',6,12);}
async function tick(){let j;try{j=await (await fetch('/data')).json();}catch(e){$('#conn').textContent='读取失败';return;}
if(j.error){$('#errs').textContent=j.error;return;}
const s=j.snapshot,c=s.computed||{},st=s.status||{},sig=s.signal||{},dv=s.device||{},wl=s.wifi||{},pl=s.plmn||{},tr=s.traffic||{},mo=s.month||{};
H=j.hist||[];draw();
$('#time').textContent=new Date(s.ts*1000).toLocaleString('zh-CN');
const on=String(st.ConnectionStatus)==='901';
$('#conn').textContent='网络 '+(c.conn||'-');$('#conn').className='pill '+(on?'on':'off');
$('#model').textContent=dv.DeviceName||'';$('#net').textContent=(pl.ShortName||'')+' '+(c.net||'');
$('#rsrp').textContent=c.rsrp==null?'-':c.rsrp;$('#rsrplv').textContent=c.rsrp_level+'/'+c.sinr_level+' SINR '+(c.sinr??'-');
const [bn,bt]=c.signal_bars||[0,5];$('#bars').innerHTML=Array.from({length:bt},(_,i)=>
`<i class="${i<bn?'f':''}" style="flex:0 0 9px;height:${8+(i+1)*(26/bt)}px"></i>`).join('');
// 顶部状态栏：信号格图标（与下方大图标同方向：左矮右高，点亮前 n 根，颜色随强弱）
const lv=bn/(bt||1);
const scol=lv>=.8?'#3fb950':lv>=.6?'#7ee787':lv>=.4?'#d29922':'#f85149';
$('#hsig').innerHTML=Array.from({length:bt},(_,i)=>
`<i class="${i<bn?'f':''}" style="flex:0 0 4px;height:${3+(i+1)*(15/bt)}px${i<bn?';background:'+scol:''}"></i>`).join('');
$('#hsig').title=`信号 ${bn}/${bt} 格、RSRP ${c.rsrp??'-'} dBm、SINR ${c.sinr??'-'} dB、RSRQ ${sig.rsrq||'-'}`;
// 顶部状态栏：电池图标（充电时用闪电符号，不再用文字）
const bp=+(st.BatteryPercent||0),chg=String(st.BatteryStatus)==='1',usb=String(st.usbup)==='1';
const bcol=bp>=80?'#3fb950':bp>=40?'#7ee787':bp>=20?'#d29922':'#f85149';
$('#hbat').className='bat'+(chg?' chg':'');
$('#hbat').innerHTML=`<span class="shell"><span class="fill" style="width:${Math.max(0,Math.min(100,bp))}%;background:${bcol}"></span>`
  +(chg?`<span class="bolt"><svg width="9" height="13" viewBox="0 0 10 14" aria-hidden="true">
<path d="M6.4 0 1.2 8.1h2.6L3 14l5.8-8.7H6.1z" fill="#3fb950" stroke="#0d1117"
stroke-width=".7"/></svg></span>`:'')+`</span><span>${bp||'-'}%</span>`;
$('#hbat').title=`电量 ${bp||'-'}%、${chg?'充电中':'未在充电'}（BatteryStatus=${st.BatteryStatus||'-'}）、`+
  `USB 供电${usb?'已接入':'未接入'}、电池档位 ${st.BatteryLevel||'-'}（充电状态由多点实测推断：USB 接入且未满时=1）`;
$('#batv').innerHTML=(bp||'-')+'% '+(chg?'<svg width="8" height="11" viewBox="0 0 10 14" '
 +'aria-label="充电中"><path d="M6.4 0 1.2 8.1h2.6L3 14l5.8-8.7H6.1z" fill="#3fb950" '
  +'stroke="#0d1117" stroke-width=".7"/></svg> 充电中':'');
kv($('#netkv'),{'运营商':esc(pl.FullName||'-')+' ('+esc(pl.Numeric||'-')+')','制式':esc(c.net),'服务':esc(c.service),
'PCI':esc(sig.pci),'小区':esc(sig.cell_id)+' / eNB '+esc(sig.enodeb_id),'频段':esc(sig.bandInfo||sig.band),
'带宽':'DL '+esc(sig.dlbandwidth)+' / UL '+esc(sig.ulbandwidth),'频点':esc(sig.earfcn),
'SINR / RSRQ':esc(c.sinr)+' dB / '+esc(sig.rsrq),'MCS':'dl '+esc(sig.dl_mcs)+' · ul '+esc(sig.ul_mcs)});
$('#dn').textContent=hr(c.rate_down);$('#up').textContent=hr(c.rate_up);
kv($('#tr'),{'本次':'↓ '+hb(tr.CurrentDownload)+' · ↑ '+hb(tr.CurrentUpload)+' · '+esc(Math.round((tr.CurrentConnectTime||0)/60))+' 分钟',
'今日':hb(mo.CurrentDayUsed),'本月':'↓ '+hb(mo.CurrentMonthDownload)+' · ↑ '+hb(mo.CurrentMonthUpload),
'累计':'↓ '+hb(tr.TotalDownload)+' · ↑ '+hb(tr.TotalUpload),'联网时长':Math.round((tr.TotalConnectTime||0)/3600)+' 小时'});
kv($('#dev'),{'IMEI':esc(dv.Imei),'SN':esc(dv.SerialNumber),'ICCID':esc(dv.Iccid),'IMSI':esc(dv.Imsi),
'号码':esc(dv.Msisdn),'SIM':esc(c.sim),'硬件/固件':esc(dv.HardwareVersion)+' / '+esc(dv.SoftwareVersion),
'WebUI':esc(dv.WebUIVersion),'运行':Math.round((dv.uptime||0)/3600)+' 小时','WAN IP':esc(dv.WanIPAddress),
'IPv6':esc(dv.WanIPv6Address),'DNS':esc(st.PrimaryDns)+' / '+esc(st.SecondaryDns)});
let sec={};(((s.multiwifi||{}).Ssids||{}).Ssid||[{}] ).forEach(x=>{if(String(x.Index)==='0')sec=x;});
kv($('#wifi'),{'SSID':esc(wl.WifiSsid),'加密':esc(sec.WifiAuthmode||'-')+' '+(sec.WifiWpaencryptionmodes||''),
'信道':esc(wl.WifiChannel)+' ('+esc(wl.WifiMode||'')+')','频宽':esc(wl.wifibandwidth),'终端上限':esc(wl.TotalWifiUser||st.TotalWifiUser),
'WiFi 开关':String(wl.WifiEnable)==='1'?'开启':'关闭','隐藏网络':String(wl.WifiHide)==='1'?'是':'否','隔离':String(wl.WifiIsolate)==='1'?'是':'否'});
$('#bat').style.width=(st.BatteryPercent||0)+'%';
// 套餐、短信、按钮状态
renderPlan(c.plan||{});
dataOn=!!c.data_switch;$('#bdata').textContent='移动数据：'+(dataOn?'开':'关');
window.UNREAD=c.sms_unread||0;
window.INBOX=c.sms_inbox||0;
$('#hsmsN').innerHTML=window.UNREAD?`<span class="badge">${window.UNREAD}</span>`:'';
$('#hsms').title=window.UNREAD?`有 ${window.UNREAD} 条未读短信，点击查看`:'点击查看短信';
$('#smsHint').innerHTML=window.UNREAD?`（未读 ${window.UNREAD}）`:'';
const hs=c.hosts||[];$('#hcount').textContent=hs.filter(h=>h.active).length+' 台在线 / 共 '+hs.length+' 台';
$('#hosts').innerHTML=hs.sort((a,b)=>(b.active-a.active)||((b.rate_down||0)-(a.rate_down||0))).map(h=>`<tr>
<td>${h.active?'<span class="tag" style="color:#3fb950">在线</span>':'<span class="tag">离线</span>'}</td>
<td>${esc(h.name)}</td><td>${esc(h.ip)}</td><td class="muted">${esc(h.mac)}</td><td>${esc(h.band)}</td>
<td>${esc(h.brand||h.vendor)}</td><td>${rt(h)}</td><td>${ru(h)}</td>
<td>${hb((h.rx||0)*B)}</td><td>${hb((h.tx||0)*B)}</td></tr>`).join('');
$('#errs').textContent=[s.auth_error?('登录异常: '+s.auth_error):'',
  s.login_wait?('设备限制登录频率，'+s.login_wait+' 秒后重试'):'']
  .filter(x=>x).join('   ')+'   '+
  Object.entries(s.errors||{}).map(([k,v])=>k+': '+v).join('   |   ');
last=Date.now();}
tick();setInterval(tick,2000);
</script></body></html>
"""


# ---------------------------------------------------------------- CLI
def build_parser():
    p = argparse.ArgumentParser(
        prog="hw_status.py", description="华为随身 WiFi 状态实时监控工具（标准库实现）",
        formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    p.add_argument("--host", default=os.environ.get("HWIFI_HOST", DEFAULT_HOST),
                   help="设备地址（默认 %(default)s）")
    p.add_argument("--user", default=os.environ.get("HWIFI_USER", DEFAULT_USER),
                   help="登录用户名（默认 %(default)s）")
    p.add_argument("--password", default=os.environ.get("HWIFI_PASSWORD", DEFAULT_PASSWORD),
                   help="登录密码（默认取环境变量 HWIFI_PASSWORD）")
    p.add_argument("--timeout", type=float, default=8, help="单次请求超时秒数")
    p.add_argument("--rate-window", dest="rate_window", type=float, default=60.0,
                   help="每机速率的均值窗口秒数，需大于设备约 20 秒的计数器刷新周期（默认 %(default)s）")
    p.add_argument("--no-login", action="store_true", help="不登录，只读免鉴权字段")
    p.add_argument("--keep-going", action="store_true",
                   help="登录失败时不退出，继续以免登录方式运行")
    p.add_argument("--debug", action="store_true", help="打印 HTTP 细节")
    sub = p.add_subparsers(dest="cmd")

    s = sub.add_parser("status", help="打印一次完整状态")
    s.add_argument("--json", action="store_true", help="输出原始 JSON")
    s.add_argument("--all", action="store_true", help="连同当前离线的历史终端一起列出")
    s.add_argument("--color", action="store_true", default=True)
    s.add_argument("--rate-window", dest="rate_window", type=float, default=60.0,
                   help="每机速率均值窗口秒（默认 %(default)s）")
    s.set_defaults(func=cmd_status)

    w = sub.add_parser("watch", help="终端实时刷新（默认子命令）")
    w.add_argument("--interval", type=float, default=3.0, help="刷新间隔秒（默认 %(default)s）")
    w.add_argument("--once", action="store_true", help="只输出一帧后退出")
    w.add_argument("--all", action="store_true", help="连同当前离线的历史终端一起列出")
    w.add_argument("--spark", action="store_true", help="显示历史速率迷你曲线")
    w.add_argument("--color", action="store_true", default=True)
    w.add_argument("--rate-window", dest="rate_window", type=float, default=60.0,
                   help="每机速率均值窗口秒（默认 %(default)s）")
    w.set_defaults(func=cmd_watch)

    v = sub.add_parser("serve", help="启动本地网页实时监控面板")
    v.add_argument("--port", type=int, default=8765)
    v.add_argument("--bind", default="127.0.0.1", help="监听地址（0.0.0.0 可局域网访问）")
    v.add_argument("--open", action="store_true", help="启动后自动打开浏览器")
    v.add_argument("--rate-window", dest="rate_window", type=float, default=60.0,
                   help="每机速率均值窗口秒（默认 %(default)s）")
    v.add_argument("--no-sms", action="store_true", help="面板不自动拉短信正文（只看未读数）")
    v.add_argument("--sms-interval", dest="sms_interval", type=float, default=20.0,
                   help="面板拉短信正文的间隔秒（默认 %(default)s）")
    v.add_argument("--sms-size", dest="sms_size", type=int, default=20,
                   help="短信每页条数（默认 %(default)s）")
    v.set_defaults(func=cmd_serve)

    r = sub.add_parser("raw", help="请求任意接口")
    r.add_argument("path")
    r.add_argument("--login", action="store_true", help="先登录")
    r.set_defaults(func=cmd_raw)

    q = sub.add_parser("probe", help="探测设备支持的接口（适配其他型号）")
    q.set_defaults(func=cmd_probe)

    sm = sub.add_parser("sms", help="读取短信 / 标记已读 / 删除")
    sm.add_argument("--page", type=int, default=1, help="页码（默认 1）")
    sm.add_argument("--size", type=int, default=20, help="每页条数（默认 %(default)s）")
    sm.add_argument("--mark", help="标记已读，多个 index 用逗号分隔")
    sm.add_argument("--delete", help="删除短信 index（需 --yes 确认）")
    sm.add_argument("--yes", action="store_true", help="确认执行删除")
    sm.add_argument("--color", action="store_true", default=True)
    sm.set_defaults(func=cmd_sms)

    pl = sub.add_parser("plan", help="套餐流量使用情况 / 设置")
    pl.add_argument("--limit", help="月度限额，如 100GB / 2048MB")
    pl.add_argument("--start-day", dest="start_day", type=int, help="每月计费起始日 1-28")
    pl.add_argument("--threshold", type=int, help="提醒阈值百分比 1-100")
    pl.add_argument("--enable", type=int, choices=[0, 1], help="是否启用套餐统计")
    pl.add_argument("--json", action="store_true")
    pl.add_argument("--color", action="store_true", default=True)
    pl.set_defaults(func=cmd_plan)

    ac = sub.add_parser("act", help="设备操作：重启 / 流量开关 / 清零统计")
    ac.add_argument("action", choices=["reboot", "data-on", "data-off", "clear-traffic"])
    ac.add_argument("--yes", action="store_true", help="确认执行重启/清零")
    ac.set_defaults(func=cmd_act)

    o = sub.add_parser("logout", help="注销设备上的会话")
    o.set_defaults(func=cmd_logout)
    return p


def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):   # 避免 GBK 控制台报错
        for stream in (sys.stdout, sys.stderr):
            try:
                stream.reconfigure(encoding="utf-8", errors="replace")
            except Exception:  # noqa: BLE001
                pass
    parser = build_parser()
    args = parser.parse_args(argv)
    for name, val in (("interval", 3.0), ("json", False), ("spark", False),
                      ("color", True), ("all", False), ("once", False)):
        if not hasattr(args, name):
            setattr(args, name, val)
    if not hasattr(args, "func"):
        args.interval = 3.0
        args.func, args.cmd = cmd_watch, "watch"
    func = args.func
    try:
        return func(args)
    except KeyboardInterrupt:
        return 130
    except DeviceError as e:
        print("设备返回错误：%s" % e, file=sys.stderr)
        return 2
    except urllib.error.URLError as e:
        print("无法连接设备 %s：%s\n请确认本机已连上该随身 WiFi 的 WLAN 或 USB 网络共享。"
              % (args.host, e.reason), file=sys.stderr)
        return 3


if __name__ == "__main__":
    sys.exit(main())
