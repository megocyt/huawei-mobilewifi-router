using System;
using System.Collections.Generic;

namespace HwWidget
{
    /// <summary>多语言支持：简体中文 / 英语 / 繁体中文，默认简体中文。
    /// 用法：L.T("key")，占位符用 string.Format(L.T(key), x)。</summary>
    static class L
    {
        public static string Lang = "zh-CN";          // zh-CN | en | zh-TW

        public static readonly string[][] Options =
        {
            new[] { "简体中文", "zh-CN" },
            new[] { "English", "en" },
            new[] { "繁體中文", "zh-TW" },
        };

        static int Idx()
        {
            return Lang == "en" ? 1 : Lang == "zh-TW" ? 2 : 0;
        }

        public static string T(string key)
        {
            string[] v;
            if (!Dict.TryGetValue(key, out v)) return key;
            return v[Idx()];
        }

        /// <summary>运营商名不翻译，保持设备返回原样，仅去掉“中国”前缀。</summary>
        public static string Carrier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("中国", "").Trim();
        }

        // 每项 { zh-CN, en, zh-TW }
        static readonly Dictionary<string, string[]> Dict = new Dictionary<string, string[]>
        {
            // ---- 悬浮窗行标签
            { "lbl.network",  new[]{"网络", "Network", "網路"} },
            { "lbl.signal",   new[]{"信号", "Signal", "信號"} },
            { "lbl.rate",     new[]{"速率", "Speed", "速率"} },
            { "lbl.plan",     new[]{"套餐", "Plan", "套餐"} },
            { "lbl.traffic",  new[]{"流量", "Traffic", "流量"} },
            { "lbl.hosts",    new[]{"终端", "Devices", "終端"} },
            { "lbl.battery",  new[]{"电量", "Battery", "電量"} },
            { "lbl.local",    new[]{"本机", "Local", "本機"} },
            { "lbl.wan",      new[]{"WAN", "WAN", "WAN"} },
            { "lbl.uptime",   new[]{"运行", "Uptime", "運行"} },
            { "lbl.sms",      new[]{"短信", "SMS", "簡訊"} },
            { "lbl.clock",    new[]{"时间", "Clock", "時間"} },
            { "lbl.status",   new[]{"状态", "Status", "狀態"} },
            { "lbl.detail",   new[]{"详情", "Details", "詳情"} },
            { "lbl.num",      new[]{"号码", "Number", "號碼"} },

            // ---- 值文案
            { "v.fail",       new[]{"读取失败", "Read failed", "讀取失敗"} },
            { "v.connected",  new[]{"已连接", "Connected", "已連線"} },
            { "v.disconnected", new[]{"未连接", "Disconnected", "未連線"} },
            { "v.charging",   new[]{"充电中", "Charging", "充電中"} },
            { "v.today",      new[]{"今日", "Today", "今日"} },
            { "v.month",      new[]{"本月", "Month", "本月"} },
            { "fmt.daysleft", new[]{"剩{0}天", "{0}d left", "剩{0}天"} },
            { "fmt.smsunread", new[]{"未读 {0} 条", "{0} unread", "未讀 {0} 則"} },
            { "fmt.retry",    new[]{"，{0} 秒后重试", ", retry in {0}s", "，{0} 秒後重試"} },
            { "u.day",        new[]{"天", "d", "天"} },
            { "u.hour",       new[]{"小时", "h", "小時"} },
            { "u.minute",     new[]{"分", "m", "分"} },
            { "u.second",     new[]{"秒", "s", "秒"} },

            // ---- 右键菜单 / 提示 / 托盘
            { "menu.toggle",  new[]{"显示 / 隐藏悬浮窗", "Show / Hide widget", "顯示 / 隱藏懸浮窗"} },
            { "menu.settings", new[]{"设置…", "Settings…", "設定…"} },
            { "menu.refresh", new[]{"立即刷新", "Refresh now", "立即重新整理"} },
            { "menu.panel",   new[]{"打开网页面板", "Open web panel", "開啟網頁面板"} },
            { "menu.topon",   new[]{"保持置顶", "Keep on top", "保持在最上層"} },
            { "menu.topoff",  new[]{"取消置顶", "Unpin from top", "取消最上層"} },
            { "menu.lock",    new[]{"锁定位置", "Lock position", "鎖定位置"} },
            { "menu.unlock",  new[]{"解锁位置", "Unlock position", "解除鎖定"} },
            { "menu.quit",    new[]{"退出", "Exit", "結束"} },
            { "hint.alpha",   new[]{"背景透明度 {0}%", "Opacity {0}%", "背景透明度 {0}%"} },
            { "hint.locked",  new[]{"位置已锁定", "Position locked", "位置已鎖定"} },
            { "hint.unlocked", new[]{"位置已解锁", "Position unlocked", "位置已解鎖"} },
            { "tray.title",   new[]{"华为随身 WiFi", "Huawei Mobile WiFi", "華為隨身 WiFi"} },
            { "tray.balloon", new[]{"随身 WiFi 悬浮窗", "Mobile WiFi widget", "隨身 WiFi 懸浮窗"} },

            // ---- 设备协议层错误
            { "err.token",    new[]{"无法获取 token", "Cannot get token", "無法取得 token"} },
            { "err.csrf",     new[]{"无法获取 CSRF token", "Cannot get CSRF token", "無法取得 CSRF token"} },
            { "err.challenge", new[]{"登录挑战响应异常", "Login challenge response invalid", "登入挑戰回應異常"} },
            { "err.signature", new[]{"服务器签名校验不通过", "Server signature mismatch", "伺服器簽章驗證失敗"} },
            { "err.panel",    new[]{"面板不可用", "Panel unavailable", "面板不可用"} },
            { "err.login",    new[]{"登录失败", "Login failed", "登入失敗"} },
            { "err.reboot",   new[]{"重启指令已下发", "Reboot command sent", "重啟指令已下發"} },
            { "err.cleared",  new[]{"已清零历史流量统计", "Traffic history cleared", "已清零歷史流量統計"} },
            { "err.don",      new[]{"已开启移动数据", "Mobile data enabled", "已開啟行動數據"} },
            { "err.doff",     new[]{"已关闭移动数据", "Mobile data disabled", "已關閉行動數據"} },
            { "err.planset",  new[]{"套餐设置已保存", "Plan settings saved", "套餐設定已儲存"} },
            { "e.100002", new[]{"设备不支持该接口", "API not supported by device", "裝置不支援此介面"} },
            { "e.100003", new[]{"需要登录（会话无效或已过期）", "Login required (session invalid or expired)", "需要登入（工作階段無效或已過期）"} },
            { "e.100005", new[]{"请求参数错误", "Bad request parameters", "請求參數錯誤"} },
            { "e.100006", new[]{"CSRF token 校验失败", "CSRF token validation failed", "CSRF token 驗證失敗"} },
            { "e.108005", new[]{"登录过于频繁，被设备限流", "Login rate limited by device", "登入過於頻繁，遭裝置限流"} },
            { "e.108006", new[]{"认证失败（密码错误或挑战失效）", "Authentication failed (wrong password or stale challenge)", "認證失敗（密碼錯誤或挑戰失效）"} },
            { "e.113055", new[]{"短信操作被拒（已读过 / 下标不存在）", "SMS action rejected (already read / index missing)", "簡訊操作被拒（已讀過 / 索引不存在）"} },
            { "e.125001", new[]{"账号被锁定", "Account locked", "帳號被鎖定"} },
            { "e.125002", new[]{"会话已注销", "Session logged out", "工作階段已註銷"} },
            { "e.125003", new[]{"会话状态失效，需重开页面", "Session invalid, reload the page", "工作階段狀態失效，需重開頁面"} },
            { "e.other",  new[]{"设备返回错误", "Device error", "裝置返回錯誤"} },

            // ---- 设置窗口
            { "tab.conn",     new[]{"连接", "Connection", "連線"} },
            { "tab.look",     new[]{"外观", "Appearance", "外觀"} },
            { "tab.rows",     new[]{"显示内容", "Content", "顯示內容"} },
            { "grp.device",   new[]{"设备连接", "Device connection", "裝置連線"} },
            { "grp.color",    new[]{"颜色", "Colors", "顏色"} },
            { "grp.behavior", new[]{"行为", "Behavior", "行為"} },
            { "lbl.source",   new[]{"数据源", "Data source", "資料來源"} },
            { "lbl.host",     new[]{"管理地址", "Manage address", "管理位址"} },
            { "lbl.user",     new[]{"登录用户", "Username", "登入使用者"} },
            { "lbl.pwd",      new[]{"登录密码", "Password", "登入密碼"} },
            { "lbl.panel",    new[]{"面板地址", "Panel address", "面板位址"} },
            { "lbl.interval", new[]{"刷新秒数", "Refresh interval (s)", "重新整理秒數"} },
            { "lbl.opacity",  new[]{"透明度", "Opacity", "透明度"} },
            { "lbl.fontsize", new[]{"字号", "Font size", "字級"} },
            { "lbl.language", new[]{"语言", "Language", "語言"} },
            { "chk.savepwd",  new[]{"把密码保存到本机配置文件（不勾则每次启动需重新输入）",
                                    "Save password to local config file (must re-enter on each start if unchecked)",
                                    "將密碼儲存至本機設定檔（未勾選則每次啟動需重新輸入）"} },
            { "chk.top",      new[]{"置顶显示", "Always on top", "最上層顯示"} },
            { "chk.lock",     new[]{"锁定位置（防误拖）", "Lock position (prevent dragging)", "鎖定位置（防誤拖）"} },
            { "chk.border",   new[]{"无边框样式", "Borderless", "無邊框樣式"} },
            { "chk.title",    new[]{"显示标题行", "Show title bar", "顯示標題列"} },
            { "hint.source",  new[]{"panel=走本机面板，不顶号", "panel = use local panel, no session conflict", "panel = 走本機面板，不頂號"} },
            { "hint.host",    new[]{"一般是 192.168.8.1", "Usually 192.168.8.1", "通常為 192.168.8.1"} },
            { "btn.save",     new[]{"保存并应用", "Save & apply", "儲存並套用"} },
            { "btn.cancel",   new[]{"取消", "Cancel", "取消"} },
            { "win.title",    new[]{"悬浮窗设置", "Widget settings", "懸浮窗設定"} },
            { "tip.now",      new[]{"保存后立即生效", "Takes effect right after saving", "儲存後立即生效"} },
            { "color.fg",     new[]{"文字", "Text", "文字"} },
            { "color.bg",     new[]{"底色", "Background", "底色"} },
            { "color.accent", new[]{"正常", "Normal", "正常"} },
            { "color.warn",   new[]{"告警", "Warning", "警告"} },
            { "color.bad",    new[]{"异常", "Error", "異常"} },
            { "app.running",  new[]{"悬浮窗已经在运行了（看系统托盘）",
                                    "The widget is already running (see system tray)",
                                    "懸浮窗已在執行中（看系統匣）"} },

            // ---- 显示内容项（设置页）
            { "row.conn",     new[]{"连接状态 / 运营商", "Connection / Carrier", "連線狀態 / 業者"} },
            { "row.signal",   new[]{"信号格 + RSRP/SINR", "Signal bars + RSRP/SINR", "信號格 + RSRP/SINR"} },
            { "row.rate",     new[]{"实时上下行速率", "Live up/down speed", "即時上下行速率"} },
            { "row.plan",     new[]{"套餐用量与剩余天数", "Plan usage & days left", "套餐用量與剩餘天數"} },
            { "row.traffic",  new[]{"今日 / 本月流量", "Today / Month traffic", "今日 / 本月流量"} },
            { "row.hosts",    new[]{"在线终端数", "Online devices", "線上終端數"} },
            { "row.battery",  new[]{"电量与充电状态", "Battery & charging", "電量與充電狀態"} },
            { "row.msisdn",   new[]{"手机号", "Phone number", "手機號"} },
            { "row.ip",       new[]{"本机 IP / WAN IP", "Local IP / WAN IP", "本機 IP / WAN IP"} },
            { "row.uptime",   new[]{"设备运行时长", "Device uptime", "裝置執行時長"} },
            { "row.sms",      new[]{"未读短信提醒", "Unread SMS alert", "未讀簡訊提醒"} },
            { "row.clock",    new[]{"当前时间", "Current time", "目前時間"} },
        };
    }
}
