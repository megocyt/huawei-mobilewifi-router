#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""华为随身 WiFi（E5783 一类）WebUI 协议模拟端。

仅用 Python 标准库实现设备侧 HTTP 接口，可被 cs/ 版悬浮窗或 python 面板直接对接：
把悬浮窗“管理地址”填成 http://127.0.0.1:8080 （或本机局域网 IP:端口）即可。

覆盖的协议要点（与 cs/src/Device.cs 完全对齐）：
  - GET  /api/webserver/token        一次性 CSRF token（客户端取第 33 位起）
  - GET  /api/user/state-login       State=0 + userlevel => 客户端跳过 SCRAM 直接判定已登录
  - POST /api/user/challenge_login   SCRAM 挑战（宽松模拟：返回合法 salt/nonce/iterations）
  - POST /api/user/authentication_login  接受任意凭证（不校验 proof、不回 serversignature）
  - GET  /api/monitoring/status | traffic-statistics | month_statistics | start_date
  - GET  /api/device/signal | /api/system/HostInfo(JSON) | /api/net/current-plmn
  - GET  /api/sms/sms-count | /api/device/information | /api/dialup/mobile-dataswitch
  - POST /api/sms/sms-list-contact | set-read | delete-sms
  - POST /api/dialup/mobile-dataswitch | monitoring/clear-traffic | start_date | device/control
成功响应一律不带 <code> 子元素（客户端以 code 是否存在判定出错）。
数值会随时间做随机游走，流量按速率对时间积分累积，操作类接口真正改动状态。
"""

import argparse
import json
import random
import threading
import time
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

# ------------------------------------------------------------------ 小工具


def esc(text):
    """XML 文本转义。"""
    return (str(text).replace("&", "&amp;").replace("<", "&lt;")
            .replace(">", "&gt;"))


def xml_response(tag, body):
    """把已拼好的内部片段包成 <tag>...</tag>。"""
    return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><{}>{}</{}>".format(tag, body, tag)


def pairs_xml(d):
    """扁平字典 -> 一串 <k>v</k>。None/嵌套字典跳过（各接口按需自拼）。"""
    out = []
    for k, v in d.items():
        out.append("<{}>{}</{}>".format(k, esc(v), k))
    return "".join(out)


# ------------------------------------------------------------------ 模拟状态


class Sim:
    """设备侧的可变状态；refresh() 在每次读取前推进随机游走与流量积分。"""

    def __init__(self, args):
        self.lock = threading.Lock()
        self.start_ts = time.time()
        self.last_tick = self.start_ts

        self.model = args.model
        self.net_type_ex = "111" if args.net5g else "101"     # 111=5G NR, 101=LTE
        self.net_type = "20" if args.net5g else "19"
        self.carrier = args.carrier
        self.msisdn = args.msisdn
        self.lan_ip = args.lan_ip
        self.wan_ip = args.wan_ip
        self.wan_ipv6 = args.wan_ipv6

        self.data_on = True
        self.battery = args.battery
        self.charging = args.charging
        self.signal_icon = args.signal_icon

        # 速率（字节/秒）随机游走的中心值
        self._down_center = max(0.0, args.down)
        self._up_center = max(0.0, args.up)
        self.down = int(self._down_center)
        self.up = int(self._up_center)

        # 套餐与已用量（字节）
        self.month_down = args.month_down_mb * 1024 * 1024
        self.month_up = args.month_up_mb * 1024 * 1024
        self.day_used = args.day_used_mb * 1024 * 1024
        self.data_limit = args.data_limit          # 形如 "100 GB"，客户端 ParseSize 解析
        self.start_day = "1"
        self.threshold = "90"
        self.set_month_data = "1"

        self.hosts = [
            {"ActualName": "iPhone-12", "MACAddress": "A4:83:E7:1A:2B:3C",
             "IPAddress": self.lan_ip + ".100", "Frequency": "2.4G",
             "DevBrands": "Apple", "Active": True},
            {"ActualName": "MateBook-Pro", "MACAddress": "F0:72:8C:5D:6E:7F",
             "IPAddress": self.lan_ip + ".101", "Frequency": "5G",
             "DevBrands": "HUAWEI", "Active": True},
            {"ActualName": "MioT-Sensor", "MACAddress": "C4:B3:01:9A:8B:7C",
             "IPAddress": self.lan_ip + ".102", "Frequency": "2.4G",
             "DevBrands": "Xiaomi", "Active": False},
        ]

        base = time.time()
        self._sid = 1000
        self.sms = [
            self._mk_sms("中国联通", "10010", "您的流量已使用 60%，详情查看客户端。", base - 3600 * 26, False),
            self._mk_sms("10086", "10086", "本月套餐剩余流量 40GB。", base - 3600 * 5, False),
            self._mk_sms("华为终端", "950800", "设备固件可更新，建议在 Wi-Fi 环境下升级。", base - 3600 * 30, True),
        ]

        # 一次性 CSRF token 表（宽松：记录但不强校验）
        self.tokens = set()

    def _mk_sms(self, phone, sender, content, ts, read):
        self._sid += 1
        return {
            "index": str(self._sid),
            "phone": phone,
            "name": sender,
            "date": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts)),
            "content": content,
            "smstat": "1" if read else "0",       # 0=未读, 1=已读
        }

    def refresh(self):
        """推进时间相关量。调用方须持锁。"""
        now = time.time()
        dt = max(0.0, now - self.last_tick)
        self.last_tick = now
        if dt <= 0:
            return
        # 电量缓慢变化
        if self.charging:
            self.battery = min(100, self.battery + dt * 0.02)
            if self.battery >= 100:
                self.charging = False
        else:
            self.battery = max(1, self.battery - dt * 0.01)
            if self.battery <= 20:
                self.charging = True
        self.battery = round(self.battery)

        if not self.data_on:
            self.down = self.up = 0
            return

        # 速率围绕中心值做有界随机游走
        def drift(cur, center, floor, ceil):
            step = random.uniform(-0.25, 0.25) * (center or 1)
            v = int(cur + step + ((center - cur) * 0.1))
            return max(floor, min(ceil, v))

        self.down = drift(self.down, self._down_center, 0, int(self._down_center * 3 + 1))
        self.up = drift(self.up, self._up_center, 0, int(self._up_center * 3 + 1))

        # 把这段时间的速率积分进流量计数
        self.month_down += int(self.down * dt)
        self.month_up += int(self.up * dt)
        self.day_used += int((self.down + self.up) * dt)

        # 信号格随速率轻微浮动
        if random.random() < 0.1:
            self.signal_icon = max(1, min(5, self.signal_icon + random.choice([-1, 1])))

    def uptime(self):
        return int(time.time() - self.start_ts)

    def connected(self):
        return self.data_on

    def sms_counts(self):
        unread = sum(1 for m in self.sms if m["smstat"] == "0")
        return unread, len(self.sms)

    def issue_token(self):
        """返回设备风格的 token 字符串：32 位前缀 + 32 位真令牌（客户端取 [32:]）。"""
        real = "".join(random.choice("0123456789ABCDEF") for _ in range(32))
        prefix = "".join(random.choice("0123456789ABCDEF") for _ in range(32))
        tok = prefix + real
        self.tokens.add(real)
        return tok


# ------------------------------------------------------------------ HTTP 处理


class Handler(BaseHTTPRequestHandler):
    sim = None  # 由 main 注入
    protocol_version = "HTTP/1.1"

    # ---- 输出 ------------------------------------------------------
    def _send(self, body, content_type="application/xml; charset=utf-8"):
        data = body.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _send_xml(self, inner, tag="response"):
        self._send(xml_response(tag, inner))

    def _send_err(self, code):
        # 成功响应不带 code；仅错误路径显式回 code，供客户端 DescribeCode
        self._send_xml("<code>{}</code>".format(code))

    def log_message(self, fmt, *args):
        if getattr(self.server, "verbose", False):
            super().log_message(fmt, *args)

    # ---- 读取 POST 体（XML：<request><k>v</k>...</request>）------
    def _read_body(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length).decode("utf-8", "replace") if length else ""
        fields = {}
        multi = {}
        if raw.strip():
            try:
                root = ET.fromstring(raw)
                for child in root:
                    if child.tag == "Index":
                        multi.setdefault("Index", []).append((child.text or "").strip())
                    else:
                        fields[child.tag] = (child.text or "").strip()
            except ET.ParseError:
                pass
        return fields, multi

    # ---- GET 分发 --------------------------------------------------
    def do_GET(self):
        path = urlparse(self.path).path
        s = self.sim
        with s.lock:
            s.refresh()
            if path == "/":
                self._serve_index()
            elif path.endswith("/api/webserver/token"):
                self._send_xml("<token>{}</token>".format(s.issue_token()))
            elif path.endswith("/api/user/state-login"):
                # 直接判定已登录：State=0 + userlevel 非空（键名必须小写，与客户端一致），
                # 客户端据此跳过整个 SCRAM 流程
                self._send_xml("<State>0</State><userlevel>admin</userlevel>"
                               "<isapi_support>1</isapi_support>")
            elif path.endswith("/api/monitoring/status"):
                self._send_xml(pairs_xml({
                    "ConnectionStatus": "901" if s.connected() else "0",
                    "NetworkAdband": "",
                    "CurrentNetworkType": s.net_type,
                    "CurrentNetworkTypeEx": s.net_type_ex if s.connected() else "0",
                    "SignalIcon": s.signal_icon,
                    "maxsignal": 5,
                    "BatteryPercent": int(s.battery),
                    "BatteryStatus": 1 if s.charging else 0,
                    "MobileConnectionStatus": "CONNECT" if s.connected() else "DISCONNECT",
                }))
            elif path.endswith("/api/monitoring/traffic-statistics"):
                self._send_xml(pairs_xml({
                    "CurrentDownloadRate": s.down,
                    "CurrentUploadRate": s.up,
                    "TotalDownload": s.month_down,
                    "TotalUpload": s.month_up,
                }))
            elif path.endswith("/api/device/signal"):
                rsrp = -95 + (s.signal_icon - 3) * 6 + random.randint(-1, 1)
                self._send_xml(pairs_xml({
                    "rsrp": rsrp,
                    "sinr": 12 + (s.signal_icon - 3) * 3,
                    "rsrq": -10 - (5 - s.signal_icon),
                    "rssi": -50 - (5 - s.signal_icon) * 4,
                    "mac": "00:11:22:33:44:55",
                }))
            elif path.endswith("/api/net/current-plmn"):
                self._send_xml(pairs_xml({
                    "State": 1 if s.connected() else 0,
                    "ShortName": s.carrier,
                    "LongName": s.carrier,
                    "NetworkType": s.net_type,
                }))
            elif path.endswith("/api/monitoring/month_statistics"):
                self._send_xml(pairs_xml({
                    "CurrentMonthDownload": s.month_down,
                    "CurrentMonthUpload": s.month_up,
                    "CurrentDayUsed": s.day_used,
                }))
            elif path.endswith("/api/monitoring/start_date"):
                self._send_xml(pairs_xml({
                    "StartDay": s.start_day,
                    "DataLimit": s.data_limit,
                    "MonthThreshold": s.threshold,
                    "SetMonthData": s.set_month_data,
                }))
            elif path.endswith("/api/sms/sms-count"):
                unread, total = s.sms_counts()
                self._send_xml(pairs_xml({
                    "LocalUnread": unread,
                    "LocalInbox": total,
                    "LocalDraft": 0,
                    "SimUnread": 0,
                }))
            elif path.endswith("/api/device/information"):
                self._send_xml(pairs_xml({
                    "DeviceName": s.model,
                    "MacAddress1": "00:11:22:33:44:55",
                    "WanIPAddress": s.wan_ip if s.connected() else "-",
                    "WanIPv6Address": s.wan_ipv6 if s.connected() else "-",
                    "Msisdn": s.msisdn,
                    "uptime": s.uptime(),
                }))
            elif path.endswith("/api/dialup/mobile-dataswitch"):
                self._send_xml(pairs_xml({"dataswitch": "1" if s.data_on else "0"}))
            elif path.endswith("/api/system/HostInfo"):
                # 该接口设备回 JSON 数组，客户端 ParseAny 走 __list 分支
                self._send(json.dumps(s.hosts, ensure_ascii=False),
                           content_type="application/json; charset=utf-8")
            else:
                self._send_xml(pairs_xml({}))

    # ---- POST 分发 -------------------------------------------------
    def do_POST(self):
        path = urlparse(self.path).path
        fields, multi = self._read_body()
        s = self.sim
        with s.lock:
            s.refresh()

            if path.endswith("/api/user/challenge_login"):
                salt = "".join(random.choice("0123456789abcdef") for _ in range(16))
                nonce = "".join(random.choice("0123456789abcdef") for _ in range(16))
                self._send_xml("<salt>{}</salt><servernonce>{}</servernonce>"
                               "<iterations>1000</iterations>".format(salt, nonce))
            elif path.endswith("/api/user/authentication_login"):
                # 宽松接受：不回 serversignature（客户端 gotSig 为空则跳过校验）
                self._send_xml("")
            elif path.endswith("/api/dialup/mobile-dataswitch"):
                s.data_on = fields.get("dataswitch") == "1"
                self._send_xml("")
            elif path.endswith("/api/monitoring/clear-traffic"):
                s.month_down = s.month_up = s.day_used = 0
                self._send_xml("")
            elif path.endswith("/api/monitoring/start_date"):
                if fields.get("StartDay"):
                    s.start_day = fields["StartDay"]
                if fields.get("DataLimit"):
                    s.data_limit = fields["DataLimit"]
                if fields.get("MonthThreshold"):
                    s.threshold = fields["MonthThreshold"]
                self._send_xml("")
            elif path.endswith("/api/device/control"):
                # 重启：复位运行时间与当日用量，模拟设备重启后重新计数
                s.start_ts = time.time()
                s.last_tick = s.start_ts
                s.day_used = 0
                self._send_xml("")
            elif path.endswith("/api/sms/sms-list-contact"):
                self._send_sms_list(fields)
            elif path.endswith("/api/sms/set-read"):
                self._mark_read(multi.get("Index", []))
                self._send_xml("")
            elif path.endswith("/api/sms/delete-sms"):
                self._delete_sms(multi.get("Index", []))
                self._send_xml("")
            elif path.endswith("/api/user/logout"):
                self._send_xml("")
            else:
                self._send_xml("")

    # ---- 具体业务响应 ----------------------------------------------
    def _serve_index(self):
        html = ("<!doctype html><meta charset=utf-8><title>Huawei WiFi (sim)</title>"
                "<p>随身 WiFi WebUI 模拟端正在运行。悬浮窗“管理地址”填本机地址即可。")
        self._send(html, content_type="text/html; charset=utf-8")

    def _send_sms_list(self, fields):
        s = self.sim
        try:
            page = max(1, int(fields.get("pageindex") or 1))
        except ValueError:
            page = 1
        try:
            size = max(1, int(fields.get("readcount") or 20))
        except ValueError:
            size = 20
        ordered = sorted(s.sms, key=lambda m: m["date"], reverse=True)
        chunk = ordered[(page - 1) * size: page * size]
        items = []
        for m in chunk:
            items.append(
                "<message><index>{index}</index><phone>{phone}</phone>"
                "<date>{date}</date><content>{content}</content>"
                "<smstat>{smstat}</smstat></message>".format(
                    index=m["index"], phone=esc(m["phone"]), date=m["date"],
                    content=esc(m["content"]), smstat=m["smstat"]))
        self._send_xml("<Count>{}</Count><messages>{}</messages>".format(len(chunk), "".join(items)))

    def _mark_read(self, indexes):
        want = set(indexes)
        for m in self.sim.sms:
            if m["index"] in want:
                m["smstat"] = "1"

    def _delete_sms(self, indexes):
        want = set(indexes)
        self.sim.sms = [m for m in self.sim.sms if m["index"] not in want]


# ------------------------------------------------------------------ 入口


def parse_args():
    ap = argparse.ArgumentParser(description="华为随身 WiFi WebUI 协议模拟端")
    ap.add_argument("--host", default="127.0.0.1", help="监听地址（局域网可填 0.0.0.0）")
    ap.add_argument("--port", type=int, default=8080, help="监听端口（默认 8080）")
    ap.add_argument("--model", default="E5783", help="设备型号 DeviceName")
    ap.add_argument("--carrier", default="China Mobile", help="运营商 ShortName")
    ap.add_argument("--msisdn", default="10086", help="SIM 号码")
    ap.add_argument("--lan-ip", default="192.168.8", help="局域网网段前缀（宿主 IP 由此生成）")
    ap.add_argument("--wan-ip", default="10.45.12.180", help="模拟 WAN IPv4")
    ap.add_argument("--wan-ipv6", default="2409:8a00::1c2f", help="模拟 WAN IPv6")
    ap.add_argument("--net5g", action="store_true", help="模拟 5G NR（默认 LTE）")
    ap.add_argument("--battery", type=int, default=76, help="初始电量百分比")
    ap.add_argument("--charging", action="store_true", help="初始为充电中")
    ap.add_argument("--signal-icon", type=int, default=4, help="初始信号格 1-5")
    ap.add_argument("--down", type=int, default=1_200_000, help="下行速率中心值 字节/秒")
    ap.add_argument("--up", type=int, default=180_000, help="上行速率中心值 字节/秒")
    ap.add_argument("--data-limit", default="100 GB", help="套餐流量上限（含单位）")
    ap.add_argument("--month-down-mb", type=int, default=20_480, help="本月已下载 MB")
    ap.add_argument("--month-up-mb", type=int, default=2_048, help="本月已上传 MB")
    ap.add_argument("--day-used-mb", type=int, default=512, help="今日已用 MB")
    ap.add_argument("-v", "--verbose", action="store_true", help="打印每条请求访问日志")
    return ap.parse_args()


def main():
    args = parse_args()
    sim = Sim(args)
    Handler.sim = sim
    ThreadingHTTPServer.allow_reuse_address = True
    httpd = ThreadingHTTPServer((args.host, args.port), Handler)
    httpd.verbose = args.verbose

    print("随身 WiFi 模拟端已启动")
    print("  监听   : http://{}:{}/".format(args.host, args.port))
    if args.host in ("0.0.0.0", ""):
        print("  悬浮窗“管理地址”可填: 127.0.0.1:{} 或 本机局域网IP:{}".format(args.port, args.port))
    else:
        print("  悬浮窗“管理地址”请填: {}:{}（登录用户/密码任意）".format(args.host, args.port))
    print("  Ctrl+C 停止")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止")
        httpd.shutdown()


if __name__ == "__main__":
    main()
