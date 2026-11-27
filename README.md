# 华为随身 WiFi 悬浮窗

针对华为随身 WiFi（WebUI 管理页 192.168.8.1 类设备）的 Windows 桌面悬浮窗，实时展示网络状态、信号、速率、套餐流量、在线终端、电量等信息。

![version](https://img.shields.io/badge/version-0.1.0-brightgreen)

当前版本：`0.1.0`

同一套设备协议提供三种实现，**共用同一份配置文件**，可互相接管：

| | Python 版（`python/`） | C# 版（`cs/`） | Qt 版（`qt/`） |
|---|---|---|---|
| 依赖 | 仅需 Python 3 标准库（tkinter + ctypes） | 仅需 Windows 自带 .NET Framework 4.8，编译后单 exe 零依赖 | 需 Qt 6.2+（C++17），`build.ps1` 自动 `windeployqt` 收集运行库 |
| 界面 | tkinter 暗色悬浮窗 | WinForms + GDI+ 全自绘科技感卡片 | Qt Widgets + QPainter 全自绘，1:1 复刻 C# 版 |
| 产物 | `pythonw hw_widget.pyw` 直接运行 | `HwWidget.exe` 双击运行 | `HwWidgetQt.exe` 双击运行（`qt/build/`） |

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
│   ├── build.ps1           #   一键编译脚本（用系统自带 csc，产物输出到 build/）
│   ├── src/                #   源码
│   │   ├── Device.cs       #     设备协议客户端（SCRAM 登录 / 面板 / 采样）
│   │   ├── Config.cs       #     配置读写（与 Python 版共用 config.json）
│   │   ├── WidgetForm.cs   #     悬浮窗主界面（GDI+ 自绘）+ 托盘 + 采集线程
│   │   ├── SettingsForm.cs #     暗色设置窗口
│   │   └── Program.cs      #     入口：单实例互斥、DPI、托盘图标代码绘制
│   └── build/              #   构建产物目录（不入库）
│       └── HwWidget.exe    #     编译产物
├── qt/                     # Qt 版（C++17 + Qt 6 Widgets）
│   ├── CMakeLists.txt      #   从 ../VERSION 生成 version.h 与 app.rc
│   ├── build.ps1           #   一键定位 Qt → cmake -G Ninja → windeployqt
│   ├── res/                #   lang.json（170 词条 × 6 语言）、图标、qrc/rc 模板
│   └── src/
│       ├── device.*        #     设备协议客户端（SCRAM 登录 / 采样 / 短信）
│       ├── cfg.*           #     config.json 读写（键名与 cs/src/Config.cs 一致）
│       ├── lang.*          #     六语言词条（运行时加载 res/lang.json）
│       ├── format.*        #     字节 / 速率 / 时长换算
│       ├── paintutil.*     #     配色、圆角、行内图标（信号柱 / 电池 / 眼睛）绘制
│       ├── widgetwindow.*  #     悬浮卡片 + 迷你条 + 托盘 + 采集线程
│       ├── lightwindow.*   #     子窗公共基类：自绘标题栏 + Win11 圆角边框
│       ├── settingsdialog.*#     设置窗口（四页签 + 样式缩略图 + 子页）
│       ├── hostsdialog.*   #     终端窗（16 列，可排序）
│       ├── smsdialog.*     #     短信窗（分页 / 标记已读 / 删除）
│       ├── hostspopup.*    #     终端行悬停浮层（在线设备清单）
│       ├── traymenu.*      #     托盘图标与暗色右键菜单
│       └── main.cpp        #     入口：单实例、`--once` 协议自检、`--panel` 直达子窗
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
powershell -ExecutionPolicy Bypass -File cs/build.ps1
```

编译器为 Windows 自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，无需安装任何 SDK 或运行时。

### Qt 版

需要 Qt 6.2+ 套件（MinGW 或 MSVC 均可），`build.ps1` 会按 `-QtDir` 参数 → `$env:QTDIR` → `C:\Qt\6.*` → MSYS2 `mingw64` 顺序自动定位：

```powershell
powershell -ExecutionPolicy Bypass -File qt/build.ps1                        # 自动找 Qt
powershell -ExecutionPolicy Bypass -File qt/build.ps1 -QtDir C:\Qt\6.8.1\mingw_64
powershell -ExecutionPolicy Bypass -File qt/build.ps1 -Clean -Tag           # 清理重编 + 打 tag
```

产物 `qt/build/HwWidgetQt.exe`（已含 Qt 运行库，可直接拷走）。另有协议自检入口，无需 GUI 即可校验设备连通性：

```powershell
.\qt\build\HwWidgetQt.exe --once      # 打印一次 snapshot JSON（字段与 python/hw_status.py 一致）
.\qt\build\HwWidgetQt.exe --panel hosts   # 启动后直接打开终端窗（settings / sms 同理，调试用）
```

## 配置

配置文件：`%APPDATA%\HWiFiWidget\config.json`（三版通用，键名完全一致）。

首次运行自动使用默认值：设备 `192.168.8.1`、用户 `admin`；密码从环境变量 `HWIFI_PASSWORD`、配置文件或设置窗口（托盘双击 / 悬浮窗右键）获取，外观颜色、字号、透明度与显示项均可在设置中调整。

## 协议说明

设备 WebUI 采用「一次性 CSRF token + XML 请求体 + SCRAM-SHA-256 挑战登录」，实现细节见 `python/hw_status.py` 与 `cs/Device.cs`（两者逻辑一致，均实测可用）：

- `GET /api/webserver/token` 取一次性 token（第 33 位起 32 字符）
- `POST /api/user/challenge_login` → `POST /api/user/authentication_login` 完成 SCRAM 登录
- 状态类接口返回 XML（`monitoring/status`、`device/signal` 等），`system/HostInfo` 返回 JSON，统一解析
- 每机速率对 60 秒窗口内的累计字节求均值（设备计数器为不规则批量刷新，瞬时差不可靠）

## 版本与发布

版本号单一真源为仓库根 `VERSION` 文件（形如 `0.1.0`）。执行 `build.ps1` 时会自动：

- 依据 `VERSION` 生成 `cs/build/AssemblyInfo.cs`，把版本注入 EXE 文件属性（右键→属性→详细信息）；
- 同步本 README 顶部的版本徽标与「当前版本」行；
- 悬浮窗托盘菜单底部的「版本」页脚读取 `Application.ProductVersion`，与上述同源。

**升版只改 `VERSION` 一处**，然后重新编译即可全流程联动。发布时打本地 git 标签：

```powershell
powershell -ExecutionPolicy Bypass -File cs/build.ps1 -Tag   # 编译 + 创建 git tag v<版本>
```
