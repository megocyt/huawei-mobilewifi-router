# -*- coding: utf-8 -*-
"""华为随身 WiFi 悬浮窗（Windows，纯标准库：tkinter + ctypes）。

特性
  · 无边框悬浮窗：可拖动、可置顶、可锁定位置、背景透明度可调（滚轮直接改）
  · 显示内容逐项可配（连接状态/信号/实时速率/套餐/流量/在线终端/电量/IP/运行时长/时间/未读短信）
  · 系统托盘：纯 ctypes 调 Shell_NotifyIcon；左键显示/隐藏，双击打开设置，右键菜单
  · 管理地址、端口无关；用户名/密码/设备地址随时可改，配置存 %APPDATA%\\HWiFiWidget\\config.json
  · 数据采集复用同目录 hw_status.py（同一套 SCRAM 登录与换算逻辑），零第三方依赖

数据源（设置里可选）
  auto    先试本机网页面板（默认 http://127.0.0.1:8765/data），不通再直连设备
  panel   只走本机面板 —— 推荐：设备只允许一个登录会话，共用面板不会互相顶号
  direct  直连设备（自己登录；退出时自动登出，把会话位还给网页后台）

运行：  pythonw hw_widget.pyw      （不弹控制台；排错时用 python hw_widget.pyw）
"""
from __future__ import annotations

import ctypes
import json
import os
import queue
import socket
import sys
import threading
import time
import tkinter as tk
import urllib.error
import urllib.request
import webbrowser
from ctypes import wintypes
from tkinter import colorchooser, ttk

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hw_status as H  # noqa: E402

CFG_DIR = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "HWiFiWidget")
CFG_PATH = os.path.join(CFG_DIR, "config.json")
LOG_PATH = os.path.join(CFG_DIR, "error.log")

ROWS = [
    ("conn", "连接状态 / 运营商"),
    ("signal", "信号格 + RSRP/SINR"),
    ("rate", "实时上下行速率"),
    ("plan", "套餐用量与剩余天数"),
    ("traffic", "今日 / 本月流量"),
    ("hosts", "在线终端数"),
    ("battery", "电量与充电状态"),
    ("ip", "本机 IP / WAN IP"),
    ("uptime", "设备运行时长"),
    ("sms", "未读短信提醒"),
    ("clock", "当前时间"),
]

DEFAULTS = {
    "host": "192.168.8.1",
    "user": "admin",
    "password": "",
    "source": "auto",
    "panel_url": "http://127.0.0.1:8765/data",
    "interval": 5.0,
    "alpha": 0.88,
    "font_family": "Microsoft YaHei UI",
    "font_size": 12,
    "fg": "#e6edf3",
    "bg": "#0d1117",
    "accent": "#3fb950",
    "warn": "#d29922",
    "bad": "#f85149",
    "rows": {k: k in ("conn", "signal", "rate", "plan", "hosts", "battery") for k, _ in ROWS},
    "topmost": True,
    "borderless": True,
    "locked": False,
    "show_title": True,
    "save_password": True,
    "pos": None,
}


# ---------------------------------------------------------------- 配置 / 日志
def load_cfg():
    cfg = json.loads(json.dumps(DEFAULTS))
    for path in (CFG_PATH, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                        "hw_widget.json")):
        try:
            with open(path, encoding="utf-8") as f:
                saved = json.load(f)
            break
        except (OSError, ValueError):
            saved = None
        finally:
            if saved is not None:
                break
    else:
        saved = None
    for k, v in (saved or {}).items():
        if k == "rows" and isinstance(v, dict):
            cfg["rows"].update(v)
        else:
            cfg[k] = v
    if not cfg.get("password"):
        cfg["password"] = os.environ.get("HWIFI_PASSWORD", "")
    return cfg


def save_cfg(cfg):
    """写配置；APPDATA 不可写（或被沙箱限制）时退回到脚本同目录。"""
    data = json.loads(json.dumps(cfg))
    if not data.get("save_password"):
        data["password"] = ""
    for path in (CFG_PATH, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                        "hw_widget.json")):
        try:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=1)
            return True
        except OSError:
            continue
    return False


def log_error(exc):
    try:
        os.makedirs(CFG_DIR, exist_ok=True)
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write("[%s] %r\n" % (time.strftime("%F %T"), exc))
    except OSError:
        pass


# ---------------------------------------------------------------- 采集线程
class Collector(threading.Thread):
    """后台轮询设备/面板，结果塞进队列，由 Tk 主线程取用（Tkinter 只能在主线程操作）。"""

    def __init__(self, cfg, out_q):
        super().__init__(daemon=True)
        self.cfg = dict(cfg)
        self.q = out_q
        self.stop_evt = threading.Event()
        self.dev = None
        self.panel_ok = None
        self.err = ""

    def update_cfg(self, cfg):
        self.cfg = dict(cfg)
        self.panel_ok = None
        if self.dev:
            self.dev.logout(True)
            self.dev = None

    def _use_panel(self):
        src = self.cfg.get("source", "auto")
        if src == "panel":
            return True
        if src == "direct":
            return False
        if self.panel_ok is None:                       # auto：探一下本机面板端口
            hp = self.cfg["panel_url"].split("//", 1)[-1].split("/", 1)[0].split(":")
            try:
                s = socket.create_connection((hp[0], int(hp[1] if len(hp) > 1 else 80)), 0.4)
                s.close()
                self.panel_ok = True
            except OSError:
                self.panel_ok = False
        return bool(self.panel_ok)

    def _from_panel(self):
        req = urllib.request.Request(self.cfg["panel_url"], headers={"User-Agent": "hw-widget"})
        with urllib.request.urlopen(req, timeout=6) as r:
            snap = (json.loads(r.read().decode("utf-8")) or {}).get("snapshot") or {}
        snap["via_panel"] = True
        return snap

    def _from_device(self):
        if self.dev is None:
            self.dev = H.HuaweiWifi(self.cfg["host"], self.cfg["user"], self.cfg["password"],
                                    timeout=8, use_login=True)
            if not self.dev.login(force=True):
                raise H.DeviceError(self.dev.last_login_error or "登录失败",
                                    self.dev.last_login_code)
        return self.dev.snapshot()

    def run(self):
        while not self.stop_evt.is_set():
            t0 = time.time()
            snap = None
            try:
                snap = self._from_panel() if self._use_panel() else self._from_device()
                self.err = ""
            except Exception as e:  # noqa: BLE001
                self.err = str(e) or e.__class__.__name__
                if self.cfg.get("source") == "auto":
                    self.panel_ok = None                # 面板失败就改走直连
                log_error(e)
            self.q.put(("snap", snap, self.err))
            self.stop_evt.wait(max(1.0, float(self.cfg.get("interval", 5)) - (time.time() - t0)))

    def stop(self):
        self.stop_evt.set()
        if self.dev:
            self.dev.logout(True)


# ---------------------------------------------------------------- Win32 声明
user32 = ctypes.WinDLL("user32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class MSG(ctypes.Structure):
    _fields_ = [("hwnd", wintypes.HWND), ("message", wintypes.UINT),
                ("wParam", wintypes.WPARAM), ("lParam", ctypes.c_ssize_t),
                ("time", wintypes.DWORD), ("pt", POINT)]


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.UINT), ("style", wintypes.UINT),
                ("lpfnWndProc", ctypes.c_void_p), ("cbClsExtra", ctypes.c_int),
                ("cbWndExtra", ctypes.c_int), ("hInstance", wintypes.HINSTANCE),
                ("hIcon", wintypes.HICON), ("hCursor", wintypes.HANDLE),
                ("hbrBackground", wintypes.HANDLE), ("lpszMenuName", wintypes.LPCWSTR),
                ("lpszClassName", wintypes.LPCWSTR), ("hIconSm", wintypes.HICON)]


class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("hWnd", wintypes.HWND),
                ("uID", wintypes.UINT), ("uFlags", wintypes.UINT),
                ("uCallbackMessage", wintypes.UINT), ("hIcon", wintypes.HICON),
                ("szTip", wintypes.WCHAR * 128), ("dwState", wintypes.DWORD),
                ("dwStateMask", wintypes.DWORD), ("szInfo", wintypes.WCHAR * 256),
                ("uVersion", wintypes.UINT), ("szInfoTitle", wintypes.WCHAR * 64),
                ("dwInfoFlags", wintypes.DWORD), ("guidItem", ctypes.c_byte * 16),
                ("hBalloonIcon", wintypes.HICON)]


class ICONINFO(ctypes.Structure):
    _fields_ = [("fIcon", wintypes.BOOL), ("xHotspot", wintypes.DWORD),
                ("yHotspot", wintypes.DWORD), ("hbmMask", wintypes.HBITMAP),
                ("hbmColor", wintypes.HBITMAP)]


NIM_ADD, NIM_MODIFY, NIM_DELETE = 0, 1, 2
NIF_MESSAGE, NIF_ICON, NIF_TIP, NIF_INFO = 1, 2, 4, 0x10
WM_USER, WM_CLOSE, WM_DESTROY = 0x400, 0x10, 0x0002
WM_LBUTTONUP, WM_LBUTTONDBLCLK, WM_RBUTTONUP = 0x202, 0x203, 0x205
TPM_RIGHTBUTTON, TPM_RETURNCMD = 0x100, 0x100
MF_STRING = 0
ERROR_ALREADY_EXISTS = 183

# ctypes.wintypes 里没有 LRESULT / LPARAM，自己按 64 位整型补上
LRESULT = ctypes.c_ssize_t
LPARAM = ctypes.c_ssize_t

WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, LPARAM)
# 64 位下必须声明 restype/argtypes，否则句柄被截成 32 位
user32.DefWindowProcW.restype = LRESULT
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, LPARAM]
kernel32.GetModuleHandleW.restype = wintypes.HINSTANCE
kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
kernel32.GetLastError.restype = wintypes.DWORD
user32.RegisterClassExW.argtypes = [ctypes.POINTER(WNDCLASSEXW)]
user32.CreateWindowExW.restype = wintypes.HWND
user32.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                   wintypes.DWORD, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                   ctypes.c_int, wintypes.HWND, wintypes.HMENU,
                                   wintypes.HINSTANCE, wintypes.LPVOID]
user32.GetMessageW.restype = ctypes.c_int
user32.GetMessageW.argtypes = [ctypes.POINTER(MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
user32.PostMessageW.restype = wintypes.BOOL
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, LPARAM]
user32.PostQuitMessage.argtypes = [ctypes.c_int]
user32.CreatePopupMenu.restype = wintypes.HMENU
user32.AppendMenuW.restype = wintypes.BOOL
user32.AppendMenuW.argtypes = [wintypes.HMENU, wintypes.UINT, wintypes.UINT, wintypes.LPCWSTR]
user32.TrackPopupMenu.restype = wintypes.UINT
user32.TrackPopupMenu.argtypes = [wintypes.HMENU, wintypes.UINT, ctypes.c_int, ctypes.c_int,
                                  ctypes.c_int, wintypes.HWND, ctypes.c_void_p]
user32.DestroyMenu.argtypes = [wintypes.HMENU]
user32.GetCursorPos.argtypes = [ctypes.POINTER(POINT)]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.LoadCursorW.restype = wintypes.HANDLE
user32.LoadCursorW.argtypes = [wintypes.HINSTANCE, wintypes.LPCWSTR]
user32.UnregisterClassW.restype = wintypes.BOOL
user32.UnregisterClassW.argtypes = [wintypes.LPCWSTR, wintypes.HINSTANCE]
user32.DestroyWindow.argtypes = [wintypes.HWND]
shell32.Shell_NotifyIconW.restype = wintypes.BOOL
shell32.Shell_NotifyIconW.argtypes = [wintypes.DWORD, ctypes.POINTER(NOTIFYICONDATAW)]
gdi32.CreateBitmap.restype = wintypes.HBITMAP
gdi32.CreateBitmap.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_uint, ctypes.c_uint,
                               ctypes.c_void_p]
# CreateIconIndirect / DestroyIcon 在 user32，不在 gdi32
user32.CreateIconIndirect.restype = wintypes.HICON
user32.CreateIconIndirect.argtypes = [ctypes.POINTER(ICONINFO)]
user32.DestroyIcon.argtypes = [wintypes.HICON]
gdi32.DeleteObject.restype = wintypes.BOOL
gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
kernel32.CreateMutexW.restype = wintypes.HANDLE
kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]


def make_icon(px=32):
    """代码画出托盘图标（4 根信号柱 + 小电池），不依赖任何图标文件。"""
    buf = bytearray(px * px * 4)                     # BGRA，自上而下

    def rect(x0, y0, w, h, hexs, alpha=255):
        r, g, b = int(hexs[1:3], 16), int(hexs[3:5], 16), int(hexs[5:7], 16)
        for y in range(y0, min(px, y0 + h)):
            for x in range(x0, min(px, x0 + w)):
                i = (y * px + x) * 4
                buf[i:i + 4] = bytes((b, g, r, alpha))

    rect(0, 0, px, px, "#161b22")
    for i, h in enumerate((5, 9, 14, 19)):           # 左矮右高，和面板图标一致
        rect(3 + i * 4, px - 4 - h, 3, h, "#3fb950")
    rect(19, 7, 9, 6, "#0d1117")                     # 电池外壳
    rect(20, 8, 6, 4, "#58a6ff")                     # 电量
    rect(28, 9, 1, 2, "#8b949e")                     # 电池头
    # CreateBitmap 原点左下 -> 上下翻转；32bpp 时 AND 掩码全 0
    flipped = b"".join(bytes(buf[y * px * 4:(y + 1) * px * 4]) for y in range(px - 1, -1, -1))
    andmask = bytes(px * px // 8)
    hbm_color = gdi32.CreateBitmap(px, px, 1, 32, ctypes.c_char_p(flipped))
    hbm_mask = gdi32.CreateBitmap(px, px, 1, 1, ctypes.c_char_p(andmask))
    hicon = user32.CreateIconIndirect(ICONINFO(0, 0, 0, hbm_mask, hbm_color))
    gdi32.DeleteObject(hbm_color)
    gdi32.DeleteObject(hbm_mask)
    return hicon


class Tray(threading.Thread):
    """托盘图标 + 右键菜单。命令用整数 ID，选中后通过队列交回主线程。"""

    def __init__(self, cmd_q, items):
        super().__init__(daemon=True)
        self.q = cmd_q
        self.items = list(items)                       # [(cmd, 文本)]
        self.map = {i + 1: cmd for i, (cmd, _t) in enumerate(self.items)}
        self.hwnd = None
        self.hicon = make_icon()
        self.nid = None
        self.tip = "华为随身 WiFi"
        self._proc = WNDPROC(self._wndproc)            # 持引用，防被 GC 掉回调

    def _fill(self):
        self.nid.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
        self.nid.hWnd = self.hwnd
        self.nid.uID = 1
        self.nid.uCallbackMessage = WM_USER + 1
        self.nid.hIcon = self.hicon
        self.nid.szTip = self.tip[:127]
        self.nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP

    def set_tip(self, text):
        self.tip = (text or "华为随身 WiFi")[:120]
        if self.nid:
            self.nid.szTip = self.tip
            self.nid.uFlags = NIF_TIP
            shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(self.nid))
            self.nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP

    def notify(self, title, text, kind=1):
        """气泡通知，kind: 1 信息 / 2 警告 / 3 错误。"""
        if not self.nid:
            return
        self.nid.uFlags = NIF_INFO
        self.nid.szInfo = text[:255]
        self.nid.szInfoTitle = title[:63]
        self.nid.dwInfoFlags = kind
        shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(self.nid))
        self.nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP

    def _popup(self):
        menu = user32.CreatePopupMenu()
        for i, (_cmd, text) in enumerate(self.items):
            user32.AppendMenuW(menu, MF_STRING, i + 1, text)
        pt = POINT()
        user32.GetCursorPos(ctypes.byref(pt))
        user32.SetForegroundWindow(self.hwnd)          # 否则菜单不消失
        chosen = int(user32.TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                           pt.x, pt.y, 0, self.hwnd, None))
        user32.DestroyMenu(menu)
        cmd = self.map.get(chosen)
        if cmd:
            self.q.put(("tray", cmd))

    def _wndproc(self, hwnd, msg, wparam, lparam):
        if msg == WM_USER + 1:
            ev = int(lparam) & 0xFFFF
            if ev == WM_LBUTTONUP:
                self.q.put(("tray", "toggle"))
            elif ev == WM_LBUTTONDBLCLK:
                self.q.put(("tray", "settings"))
            elif ev == WM_RBUTTONUP:
                self._popup()
            return 0
        if msg == WM_CLOSE:
            user32.PostQuitMessage(0)
            return 0
        return int(user32.DefWindowProcW(hwnd, msg, wparam, lparam))

    def run(self):
        hinst = kernel32.GetModuleHandleW(None)
        cls = "HWiFiTrayWnd"
        wc = WNDCLASSEXW()
        wc.cbSize = ctypes.sizeof(WNDCLASSEXW)
        wc.lpfnWndProc = ctypes.cast(self._proc, ctypes.c_void_p)
        wc.hInstance = hinst
        wc.hIcon = self.hicon
        wc.hCursor = user32.LoadCursorW(None, 32512)         # IDC_ARROW
        wc.lpszClassName = cls
        user32.RegisterClassExW(ctypes.byref(wc))
        # 参数序：exStyle, 类名, 标题, style, X, Y, W, H, 父窗口, 菜单, 实例, 参数
        # （曾经把 hinst 误传到 hMenu 位，报 1401 无效菜单句柄）
        self.hwnd = user32.CreateWindowExW(0, cls, "hw", 0, 0, 0, 0, 0, None, None, hinst, None)
        if not self.hwnd:
            return
        self.nid = NOTIFYICONDATAW()
        self._fill()
        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(self.nid))
        msg = MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) not in (0, -1):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(self.nid))
        user32.DestroyWindow(self.hwnd)

    def quit(self):
        if self.hwnd:
            user32.PostMessageW(self.hwnd, WM_CLOSE, 0, 0)


# ---------------------------------------------------------------- 数据 -> 文本
def block(n, total, width=5):
    try:
        n, total = float(n), float(total)
    except (TypeError, ValueError):
        return "?"
    k = 0 if total <= 0 else max(0, min(width, round(n / total * width)))
    return "█" * k + "░" * (width - k)


class Locals:
    ip = ""

    @classmethod
    def of(cls, target):
        if not cls.ip:
            target = target or "192.168.8.1"
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                s.connect((target, 9))
                cls.ip = s.getsockname()[0]
            except Exception:  # noqa: BLE001
                cls.ip = "-"
            finally:
                s.close()
        return cls.ip


def fmt_rows(snap, cfg, err=""):
    """返回 [(key, 文本, 颜色键)]；颜色键是 fg/accent/warn/bad 之一。"""
    want, out = cfg["rows"], []
    c = (snap or {}).get("computed") or {}
    st = (snap or {}).get("status") or {}
    dev = (snap or {}).get("device") or {}
    pl = c.get("plan") or {}
    if not snap:
        return [("conn", "读取失败", "bad"), ("signal", (err or "无数据")[:34], "fg")]
    if want.get("conn"):
        op = ((snap.get("plmn") or {}).get("ShortName") or "").replace("中国", "")
        out.append(("conn", ("%s %s" % (c.get("net") or "-", op)).strip(),
                    "accent" if c.get("conn") == "已连接" else "bad"))
    if want.get("signal"):
        bn, bt = c.get("signal_bars") or (0, 5)
        txt = "%s %d/%d" % (block(bn, bt), bn, bt)
        if c.get("rsrp") is not None:
            sinr = "%d" % c["sinr"] if c.get("sinr") is not None else "?"
            txt += " %d/%s" % (int(c["rsrp"]), sinr)
        out.append(("signal", txt,
                    "accent" if c.get("rsrp_level") in ("优", "良")
                    else "warn" if c.get("rsrp_level") == "中" else "bad"))
    if want.get("rate"):
        out.append(("rate", "↓ %s  ↑ %s" % (H.human_rate(c.get("rate_down")),
                                            H.human_rate(c.get("rate_up"))), "fg"))
    if want.get("plan") and pl.get("limit"):
        pct = pl.get("percent") or 0
        txt = "套餐 %s/%s %.0f%%" % (H.human_bytes(pl["used"], 0), pl["limit_raw"], pct)
        if pl.get("days_left") is not None:
            txt += " 剩%s天" % pl["days_left"]
        out.append(("plan", txt, "bad" if pl.get("over_limit")
                    else "warn" if pl.get("over_threshold") else "accent"))
    if want.get("traffic"):
        out.append(("traffic", "今日 %s · 本月 %s" % (H.human_bytes(pl.get("day_used"), 0),
                                                     H.human_bytes(pl.get("used"), 0)), "fg"))
    if want.get("hosts"):
        out.append(("hosts", "在线终端 %s/%s" % (c.get("hosts_active", "?"),
                                                 c.get("hosts_total", "?")), "fg"))
    if want.get("battery") and st.get("BatteryPercent"):
        chg = str(st.get("BatteryStatus")) == "1"
        low = False
        try:
            low = float(st["BatteryPercent"]) <= 20 and not chg
        except (TypeError, ValueError):
            pass
        out.append(("battery", "电量 %s%% %s" % (st["BatteryPercent"],
                                                 "充电中" if chg else "未充电"),
                    "warn" if low else "accent"))
    if want.get("ip"):
        out.append(("ip", "本机 %s · WAN %s" % (Locals.of(cfg.get("host")),
                                                dev.get("WanIPAddress") or "-"), "fg"))
    if want.get("uptime") and dev.get("uptime"):
        out.append(("uptime", "已运行 %s" % H.human_dur(dev["uptime"]), "fg"))
    if want.get("sms") and c.get("sms_unread"):
        out.append(("sms", "未读短信 %d 条" % c["sms_unread"], "warn"))
    if want.get("clock"):
        out.append(("clock", time.strftime("%H:%M:%S"), "fg"))
    if err:
        out.insert(0, ("conn", "⚠ " + err[:28], "bad"))
    return out


# ---------------------------------------------------------------- 悬浮窗
class Widget:
    ITEMS = [("show", "显示 / 隐藏悬浮窗"), ("settings", "设置…"), ("refresh", "立即刷新"),
             ("panel", "打开网页面板"), ("top", "切换置顶"), ("lock", "锁定位置"),
             ("quit", "退出")]

    def __init__(self):
        self.cfg = load_cfg()
        self.q = queue.Queue()
        self.snap, self.err, self.hidden, self._drag = None, "", False, None
        self.labels = {}
        self.tray = None
        self.root = tk.Tk()
        self.root.withdraw()
        self.win = tk.Toplevel(self.root)
        self.build()
        self.coll = Collector(self.cfg, self.q)
        self.coll.start()
        self.tray = Tray(self.q, self.ITEMS)
        self.tray.start()
        self.win.after(200, self.pump)
        self.win.after(1000, self.tick_clock)

    # ---- 窗口构建 ----
    def build(self):
        cfg, w = self.cfg, self.win
        w.title("华为随身 WiFi")
        w.overrideredirect(bool(cfg.get("borderless", True)))
        w.attributes("-topmost", bool(cfg.get("topmost", True)))
        w.attributes("-alpha", max(0.25, min(1.0, float(cfg.get("alpha", .88)))))
        w.configure(bg=cfg["bg"])
        w.resizable(False, False)
        fam = cfg.get("font_family") or "Microsoft YaHei UI"
        size = int(cfg.get("font_size", 12))
        self.font, self.small = (fam, size), (fam, max(8, size - 3))
        for ch in w.winfo_children():
            ch.destroy()
        self.title_lbl = None
        if cfg.get("show_title", True):
            self.title_lbl = tk.Label(w, bg=cfg["bg"], fg=cfg["accent"], font=self.small,
                                      anchor="w")
            self.title_lbl.pack(fill="x", padx=10, pady=(6, 0))
        self.body = tk.Frame(w, bg=cfg["bg"])
        self.body.pack(fill="both", expand=True, padx=10, pady=(2, 8))
        self.labels = {}
        for key, _label in ROWS:
            if cfg["rows"].get(key):
                lbl = tk.Label(self.body, text="…", bg=cfg["bg"], fg=cfg["fg"],
                               font=self.font, anchor="w", justify="left")
                lbl.pack(fill="x")
                self.labels[key] = lbl
        self.set_title()
        w.bind("<Button-1>", self.on_press)
        w.bind("<B1-Motion>", self.on_motion)
        w.bind("<ButtonRelease-1>", self.on_release)
        w.bind("<Double-Button-1>", lambda e: self.do("panel"))
        w.bind("<Button-3>", self.on_menu)
        w.bind("<MouseWheel>", self.on_wheel)
        self.render()
        w.update_idletasks()
        pos = cfg.get("pos")
        if pos:
            w.geometry("+%d+%d" % (int(pos[0]), int(pos[1])))
        else:
            w.geometry("+%d+%d" % (w.winfo_screenwidth() - w.winfo_reqwidth() - 40, 60))
        w.deiconify()

    def set_title(self, text=None):
        if self.title_lbl:
            self.title_lbl.configure(text=text or "华为随身 WiFi · %s" % self.cfg["host"])

    def render(self):
        rows = fmt_rows(self.snap, self.cfg, self.err)
        for key, text, color in rows:
            lbl = self.labels.get(key)
            if lbl is not None:
                lbl.configure(text=text, fg=self.cfg.get(color, self.cfg["fg"]))
        if self.tray:
            self.tray.set_tip("华为随身 WiFi\n" + " | ".join(t for _k, t, _c in rows)[:100])

    def tick_clock(self):
        lbl = self.labels.get("clock")
        if lbl is not None and self.snap:
            lbl.configure(text=time.strftime("%H:%M:%S"))
        self.win.after(1000, self.tick_clock)

    # ---- 交互 ----
    def on_press(self, e):
        if not self.cfg.get("locked"):
            self._drag = (e.x_root - self.win.winfo_x(), e.y_root - self.win.winfo_y())

    def on_motion(self, e):
        if self._drag and not self.cfg.get("locked"):
            self.win.geometry("+%d+%d" % (e.x_root - self._drag[0], e.y_root - self._drag[1]))

    def on_release(self, _e):
        if self._drag:
            self.cfg["pos"] = [self.win.winfo_x(), self.win.winfo_y()]
            save_cfg(self.cfg)
        self._drag = None

    def on_wheel(self, e):
        a = float(self.cfg.get("alpha", .88)) + (0.05 if e.delta > 0 else -0.05)
        self.cfg["alpha"] = round(max(0.25, min(1.0, a)), 2)
        self.win.attributes("-alpha", self.cfg["alpha"])
        self.set_title("背景透明度 %d%%" % round(self.cfg["alpha"] * 100))
        self.win.after(1500, self.set_title)
        save_cfg(self.cfg)

    def on_menu(self, e):
        m = tk.Menu(self.win, tearoff=0, bg=self.cfg["bg"], fg=self.cfg["fg"],
                    activebackground=self.cfg["accent"], relief="flat", bd=0, font=self.small)
        m.add_command(label="隐藏悬浮窗", command=lambda: self.do("toggle"))
        m.add_command(label="设置…", command=lambda: self.do("settings"))
        m.add_command(label="立即刷新", command=lambda: self.do("refresh"))
        m.add_command(label="打开网页面板", command=lambda: self.do("panel"))
        m.add_separator()
        m.add_command(label="取消置顶" if self.cfg["topmost"] else "保持置顶",
                      command=lambda: self.do("top"))
        m.add_command(label="已锁定位置" if self.cfg["locked"] else "锁定位置",
                      command=lambda: self.do("lock"))
        m.add_separator()
        m.add_command(label="退出", command=lambda: self.do("quit"))
        m.tk_popup(e.x_root, e.y_root)

    # ---- 命令 ----
    def do(self, cmd):
        if cmd in ("toggle", "show", "hide"):
            self.hidden = (cmd == "toggle" and not self.hidden) or cmd == "hide"
            if self.hidden:
                self.win.withdraw()
            else:
                self.win.deiconify()
                self.win.lift()
        elif cmd == "settings":
            if self.hidden:
                self.hidden = False
                self.win.deiconify()
            Settings(self.win, self.cfg, self.apply_cfg)
        elif cmd == "refresh":
            self.coll.update_cfg(self.cfg)
        elif cmd == "panel":
            webbrowser.open(self.cfg["panel_url"].rsplit("/", 1)[0] + "/")
        elif cmd == "top":
            self.cfg["topmost"] = not self.cfg["topmost"]
            self.win.attributes("-topmost", self.cfg["topmost"])
            save_cfg(self.cfg)
        elif cmd == "lock":
            self.cfg["locked"] = not self.cfg["locked"]
            save_cfg(self.cfg)
            self.set_title("位置已锁定" if self.cfg["locked"] else "位置已解锁")
            self.win.after(1500, self.set_title)
        elif cmd == "quit":
            self.quit()

    def apply_cfg(self, cfg):
        self.cfg = cfg
        Locals.ip = ""
        self.coll.update_cfg(cfg)
        self.build()

    def pump(self):
        try:
            while True:
                item = self.q.get_nowait()
                if item[0] == "snap":
                    _tag, snap, err = item
                    if snap:
                        self.snap, self.err = snap, err
                    else:
                        self.err = err or "无数据"
                        if self.tray and err:
                            self.tray.notify("随身 WiFi 悬浮窗", err[:120], 2)
                    self.render()
                elif item[0] == "tray":
                    self.do(item[1])
        except queue.Empty:
            pass
        except Exception as e:  # noqa: BLE001
            log_error(e)
        self.win.after(300, self.pump)

    def quit(self):
        try:
            self.cfg["pos"] = [self.win.winfo_x(), self.win.winfo_y()]
            save_cfg(self.cfg)
            self.coll.stop()
            if self.tray:
                self.tray.quit()
            self.root.destroy()
        except Exception as e:  # noqa: BLE001
            log_error(e)
        os._exit(0)


# ---------------------------------------------------------------- 设置窗口
class Settings(tk.Toplevel):
    def __init__(self, parent, cfg, on_save):
        super().__init__(parent)
        self.on_save = on_save
        self.cfg = json.loads(json.dumps(cfg))
        self.title("悬浮窗设置")
        self.configure(bg=self.cfg["bg"])
        self.resizable(False, False)
        self.attributes("-topmost", True)
        style = ttk.Style(self)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure(".", background=self.cfg["bg"], foreground="#c9d1d9",
                        fieldbackground="#0d1117", bordercolor="#30363d")
        style.configure("TLabelframe", background=self.cfg["bg"], foreground="#8b949e")
        style.configure("TLabelframe.Label", background=self.cfg["bg"], foreground="#8b949e")
        style.configure("TCheckbutton", background=self.cfg["bg"], foreground="#c9d1d9")
        style.configure("TButton", background="#21262d", foreground="#e6edf3")
        self.build()
        self.grab_set()
        self.protocol("WM_DELETE_WINDOW", self.destroy)

    def row(self, parent, i, label, widget, hint=""):
        tk.Label(parent, text=label, bg=parent["bg"], fg="#8b949e").grid(
            row=i, column=0, sticky="w", padx=(0, 8), pady=2)
        widget.grid(row=i, column=1, columnspan=1 + bool(hint), sticky="w")
        if hint:
            tk.Label(parent, text=hint, bg=parent["bg"], fg="#6e7681").grid(
                row=i, column=2, sticky="w", padx=6)

    def build(self):
        cfg = self.cfg
        pad = ttk.Frame(self, padding=10)
        pad.pack(fill="both", expand=True)

        g = ttk.LabelFrame(pad, text=" 设备连接 ", padding=8)
        g.pack(fill="x")
        self.src = tk.StringVar(value=cfg["source"])
        self.host = tk.StringVar(value=cfg["host"])
        self.user = tk.StringVar(value=cfg["user"])
        self.pwd = tk.StringVar(value=cfg["password"])
        self.panel = tk.StringVar(value=cfg["panel_url"])
        self.ivl = tk.DoubleVar(value=cfg["interval"])
        self.row(g, 0, "数据源", ttk.Combobox(g, textvariable=self.src, width=10,
                                             values=["auto", "panel", "direct"],
                                             state="readonly"),
                 "panel=走本机网页面板，最省设备会话")
        self.row(g, 1, "管理地址", ttk.Entry(g, textvariable=self.host, width=20),
                 "一般是 192.168.8.1")
        self.row(g, 2, "登录用户", ttk.Entry(g, textvariable=self.user, width=20))
        self.row(g, 3, "登录密码", ttk.Entry(g, textvariable=self.pwd, width=20, show="*"))
        self.row(g, 4, "面板地址", ttk.Entry(g, textvariable=self.panel, width=20))
        self.row(g, 5, "刷新秒数", ttk.Spinbox(g, textvariable=self.ivl, from_=2, to=120,
                                              increment=1, width=7))
        self.savepwd = tk.IntVar(value=1 if cfg.get("save_password", True) else 0)
        ttk.Checkbutton(g, text="把密码保存到本机配置文件（不勾则每次启动需重新输入）",
                        variable=self.savepwd).grid(row=6, column=0, columnspan=3,
                                                    sticky="w", pady=(4, 0))

        a = ttk.LabelFrame(pad, text=" 外观 ", padding=8)
        a.pack(fill="x", pady=(8, 0))
        self.alpha = tk.IntVar(value=int(round(cfg["alpha"] * 100)))
        self.fsize = tk.IntVar(value=int(cfg["font_size"]))
        tk.Label(a, text="背景透明度", bg=a["bg"], fg="#8b949e").grid(row=0, column=0, sticky="w")
        ttk.Scale(a, from_=25, to=100, variable=self.alpha, orient="horizontal",
                  length=160).grid(row=0, column=1, sticky="w")
        self.alabel = tk.Label(a, text="%d%%" % self.alpha.get(), width=5, bg=a["bg"],
                               fg="#8b949e")
        self.alabel.grid(row=0, column=2)
        self.alpha.trace_add("write", lambda *x: self.alabel.configure(
            text="%d%%" % self.alpha.get()))
        tk.Label(a, text="字号", bg=a["bg"], fg="#8b949e").grid(row=0, column=3, sticky="e",
                                                               padx=(12, 4))
        ttk.Spinbox(a, from_=9, to=30, textvariable=self.fsize, width=5).grid(row=0, column=4)
        self.swatches = {}
        cw = tk.Frame(a, bg=a["bg"])
        cw.grid(row=1, column=0, columnspan=5, sticky="w", pady=(6, 0))
        for key, label in (("fg", "文字"), ("bg", "底色"), ("accent", "正常色"), ("warn", "告警色")):
            tk.Label(cw, text=label, bg=cw["bg"], fg="#8b949e").pack(side="left", padx=(0, 4))
            b = tk.Button(cw, bg=cfg[key], width=3, relief="flat", bd=0,
                          activebackground=cfg[key], command=lambda k=key: self.pick(k))
            b.pack(side="left", padx=(0, 10))
            self.swatches[key] = b
        self.topmost = tk.IntVar(value=1 if cfg["topmost"] else 0)
        self.locked = tk.IntVar(value=1 if cfg["locked"] else 0)
        self.border = tk.IntVar(value=1 if cfg["borderless"] else 0)
        self.showtitle = tk.IntVar(value=1 if cfg["show_title"] else 0)
        for i, (text, var) in enumerate((("置顶显示", self.topmost), ("锁定位置（防误拖）",
                                                                self.locked),
                                        ("无边框样式", self.border), ("显示标题行",
                                                                self.showtitle))):
            ttk.Checkbutton(a, text=text, variable=var).grid(
                row=2 + i // 2, column=(i % 2) * 2, columnspan=2, sticky="w", pady=(3, 0))

        r = ttk.LabelFrame(pad, text=" 显示内容 ", padding=8)
        r.pack(fill="x", pady=(8, 0))
        self.vars = {}
        for i, (key, label) in enumerate(ROWS):
            v = tk.IntVar(value=1 if cfg["rows"].get(key) else 0)
            self.vars[key] = v
            ttk.Checkbutton(r, text=label, variable=v).grid(
                row=i // 2, column=i % 2, sticky="w", padx=(0, 10), pady=1)

        btn = ttk.Frame(pad)
        btn.pack(fill="x", pady=(10, 0))
        tk.Label(btn, text="改动立即生效；位置会随窗口拖动自动记忆",
                 bg=self["bg"], fg="#6e7681").pack(side="left")
        ttk.Button(btn, text="保存并应用", command=self.save).pack(side="right")
        ttk.Button(btn, text="取消", command=self.destroy).pack(side="right", padx=6)

    def pick(self, key):
        _rgb, hexv = colorchooser.askcolor(color=self.cfg[key], parent=self, title="选择颜色")
        if hexv:
            self.cfg[key] = hexv
            self.swatches[key].configure(bg=hexv, activebackground=hexv)
            self.configure(bg=self.cfg["bg"])

    def save(self):
        cfg = self.cfg
        cfg.update({
            "source": self.src.get(), "host": self.host.get().strip() or "192.168.8.1",
            "user": self.user.get().strip() or "admin", "password": self.pwd.get(),
            "panel_url": self.panel.get().strip() or DEFAULTS["panel_url"],
            "interval": max(2.0, float(self.ivl.get())),
            "alpha": max(0.25, min(1.0, self.alpha.get() / 100.0)),
            "font_size": max(8, int(self.fsize.get())),
            "topmost": bool(self.topmost.get()), "locked": bool(self.locked.get()),
            "borderless": bool(self.border.get()), "show_title": bool(self.showtitle.get()),
            "save_password": bool(self.savepwd.get()),
            "rows": {k: bool(v.get()) for k, v in self.vars.items()},
        })
        save_cfg(cfg)
        self.on_save(cfg)
        self.destroy()


# ---------------------------------------------------------------- 入口
def main():
    if os.name != "nt":
        print("这个悬浮窗只在 Windows 上运行")
        return 1
    # 单实例：用命名互斥量；GetLastError 必须用 use_last_error 的 DLL 句柄取才靠谱
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateMutexW(None, False, "HWiFiWidget_SingleInstance")
    if ctypes.get_last_error() == ERROR_ALREADY_EXISTS:
        print("悬浮窗已经在运行了（看系统托盘）")
        return 0
    Widget().root.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
