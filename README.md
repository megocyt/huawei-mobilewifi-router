# 华为随身 WiFi 悬浮窗

针对华为随身 WiFi（WebUI 管理页 192.168.8.1 类设备）的 Windows 桌面悬浮窗，实时展示网络状态、信号、速率、套餐流量、在线终端、电量等信息。

同一套设备协议提供两种实现，**共用同一份配置文件**，可互相接管：

| | Python 版（`python/`） | C# 版（`cs/`） |
|---|---|---|
| 依赖 | 仅需 Python 3 标准库（tkinter + ctypes） | 仅需 Windows 自带 .NET Framework 4.8，编译后单 exe 零依赖 |
| 界面 | tkinter 暗色悬浮窗 | WinForms + GDI+ 全自绘科技感卡片 |
| 产物 | `pythonw hw_widget.pyw` 直接运行 | `HwWidget.exe` 双击运行 |

## 功能特性

- 无边框悬浮卡片：可拖动、置顶、锁定位置、滚轮调背景透明度、位置自动记忆
- 多语言：简体中文 / 英语 / 繁体中文（默认简体中文），设置窗口“语言”下拉切换，持久化到配置文件
- 显示内容逐项可配（11 项）：连接状态/运营商、信号格 + RSRP/SINR、实时上下行速率、套餐用量与剩余天数（含进度条）、今日/本月流量、在线终端数、电量与充电状态、本机/WAN IP、运行时长、未读短信提醒、时钟
- 系统托盘：左键显示/隐藏、双击打开设置、右键菜单（刷新、打开网页面板、置顶、锁定、退出）
- 三种数据源（设置里可选）：
  - `auto` 先探本机网页面板（默认 `http://127.0.0.1:8765/data`），不通再直连设备
  - `panel` 只走本机面板 —— 推荐：设备只允许一个登录会话，共用面板不会互相顶号
  - `direct` 直连设备（SCRAM 登录，退出时自动登出归还会话位）
- 错误自动退避重试（避开设备 108005 登录限流），异常气泡通知

## 目录结构

```
huawei/
├── python/                 # Python 版
│   ├── hw_widget.pyw       #   悬浮窗入口（pythonw hw_widget.pyw）
│   └── hw_status.py        #   设备协议库：SCRAM 登录 + 数据采集换算
├── cs/                     # C# 版
│   ├── src/                #   源码
│   │   ├── Device.cs       #     设备协议客户端（SCRAM 登录 / 面板 / 采样）
│   │   ├── Config.cs       #     配置读写（与 Python 版共用 config.json）
│   │   ├── WidgetForm.cs   #     悬浮窗主界面（GDI+ 自绘）+ 托盘 + 采集线程
│   │   ├── SettingsForm.cs #     暗色设置窗口
│   │   └── Program.cs      #     入口：单实例互斥、DPI、托盘图标代码绘制
│   └── build/              #   构建
│       ├── build.ps1       #     一键编译（用系统自带 csc）
│       └── HwWidget.exe    #     编译产物（不入库）
└── screenshots/            # 开发验证截图（不入库）
```

## 使用

### Python 版

```powershell
cd python
pythonw hw_widget.pyw        # 无控制台窗口运行；排错改用 python hw_widget.pyw
```

### C# 版

直接双击 `cs/build/HwWidget.exe`（仓库不收录二进制）。自行编译：

```powershell
powershell -ExecutionPolicy Bypass -File cs/build/build.ps1
```

编译器为 Windows 自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，无需安装任何 SDK 或运行时。

## 配置

配置文件：`%APPDATA%\HWiFiWidget\config.json`（两版通用，键名完全一致）。

首次运行自动使用默认值：设备 `192.168.8.1`、用户 `admin`；密码从环境变量 `HWIFI_PASSWORD`、配置文件或设置窗口（托盘双击 / 悬浮窗右键）获取，外观颜色、字号、透明度与显示项均可在设置中调整。

## 协议说明

设备 WebUI 采用「一次性 CSRF token + XML 请求体 + SCRAM-SHA-256 挑战登录」，实现细节见 `python/hw_status.py` 与 `cs/Device.cs`（两者逻辑一致，均实测可用）：

- `GET /api/webserver/token` 取一次性 token（第 33 位起 32 字符）
- `POST /api/user/challenge_login` → `POST /api/user/authentication_login` 完成 SCRAM 登录
- 状态类接口返回 XML（`monitoring/status`、`device/signal` 等），`system/HostInfo` 返回 JSON，统一解析
- 每机速率对 60 秒窗口内的累计字节求均值（设备计数器为不规则批量刷新，瞬时差不可靠）
