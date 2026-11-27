#include "cfg.h"
#include "device.h"
#include "format.h"
#include "lang.h"
#include "lightwindow.h"
#include "traymenu.h"
#include "version.h"
#include "widgetwindow.h"

#include <QApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QTextStream>
#include <QTimer>

#ifdef Q_OS_WIN
#include <windows.h>
#include <cstdio>
#endif

/// 程序是 WIN32 子系统（无控制台），--once 时需要挂回父进程控制台才能看到输出；
/// 若 stdout 已被重定向到文件则保持原样。
static void ensureStdout()
{
#ifdef Q_OS_WIN
    if (GetStdHandle(STD_OUTPUT_HANDLE) != INVALID_HANDLE_VALUE
        && GetStdHandle(STD_OUTPUT_HANDLE) != nullptr) {
        return;
    }
    if (AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole()) {
        FILE *f = nullptr;
        freopen_s(&f, "CONOUT$", "w", stdout);
    }
#endif
}

namespace {

/// --once：无 GUI 打一次 snapshot JSON，便于在没装 Qt 的环境里校验协议
int runOnce()
{
    ensureStdout();
    const Cfg cfg = Cfg::load();
    Lang::setLang(cfg.language);
    const QString h = cfg.host.trimmed();
    const QString url = h.contains(QStringLiteral("://")) ? h : QStringLiteral("http://") + h;
    const QString user = cfg.user.isEmpty() ? QStringLiteral("admin") : cfg.user;
    Device dev{url, user, cfg.password};
    const Snap s = dev.sample();

    QJsonObject o;
    o.insert(QStringLiteral("ok"), s.ok);
    o.insert(QStringLiteral("err"), s.err);
    o.insert(QStringLiteral("loggedIn"), s.loggedIn);
    o.insert(QStringLiteral("net"), s.net);
    o.insert(QStringLiteral("conn"), s.conn);
    o.insert(QStringLiteral("connected"), s.connected);
    o.insert(QStringLiteral("carrier"), Lang::carrier(s.carrier));
    o.insert(QStringLiteral("signalBars"), s.sigBars);
    o.insert(QStringLiteral("signalMax"), s.sigMax);
    o.insert(QStringLiteral("rsrp"), s.rsrp);
    o.insert(QStringLiteral("sinr"), s.sinr);
    o.insert(QStringLiteral("rsrq"), s.rsrq);
    o.insert(QStringLiteral("rsrpLevel"), s.rsrpLevel);
    o.insert(QStringLiteral("rateDown"), QString::number(s.rateDown));
    o.insert(QStringLiteral("rateUp"), QString::number(s.rateUp));
    o.insert(QStringLiteral("dayUsed"), QString::number(s.dayUsed));
    o.insert(QStringLiteral("monthDown"), QString::number(s.monthDown));
    o.insert(QStringLiteral("monthUp"), QString::number(s.monthUp));
    o.insert(QStringLiteral("planLimit"), QString::number(s.planLimit));
    o.insert(QStringLiteral("planUsed"), QString::number(s.planUsed));
    o.insert(QStringLiteral("planPercent"), s.planPercent);
    o.insert(QStringLiteral("planDaysLeft"), s.planDaysLeft);
    o.insert(QStringLiteral("planLimitRaw"), s.planLimitRaw);
    o.insert(QStringLiteral("hostsActive"), s.hostsActive);
    o.insert(QStringLiteral("hostsTotal"), s.hostsTotal);
    o.insert(QStringLiteral("smsUnread"), s.smsUnread);
    o.insert(QStringLiteral("smsInbox"), s.smsInbox);
    o.insert(QStringLiteral("battery"), s.battery);
    o.insert(QStringLiteral("charging"), s.charging);
    o.insert(QStringLiteral("wanIp"), s.wanIp);
    o.insert(QStringLiteral("wanIpv6"), s.wanIpv6);
    o.insert(QStringLiteral("msisdn"), s.msisdn);
    o.insert(QStringLiteral("uptime"), QString::number(s.uptime));
    o.insert(QStringLiteral("model"), s.model);

    QJsonArray hosts;
    for (const Host &h : s.hosts) {
        QJsonObject ho;
        ho.insert(QStringLiteral("name"), h.name);
        ho.insert(QStringLiteral("ip"), h.ip);
        ho.insert(QStringLiteral("ip6"), h.ip6);
        ho.insert(QStringLiteral("mac"), h.mac);
        ho.insert(QStringLiteral("band"), h.band);
        ho.insert(QStringLiteral("brand"), h.brand);
        ho.insert(QStringLiteral("active"), h.active);
        ho.insert(QStringLiteral("rx"), humanBytes(double(h.rxK) * 1024.0, 1));
        ho.insert(QStringLiteral("tx"), humanBytes(double(h.txK) * 1024.0, 1));
        ho.insert(QStringLiteral("online"), humanDur(h.assocSec));
        hosts.append(ho);
    }
    o.insert(QStringLiteral("hosts"), hosts);

    QTextStream out(stdout);
    out << QString::fromUtf8(QJsonDocument(o).toJson(QJsonDocument::Indented)) << Qt::endl;
    return s.ok ? 0 : 1;
}

} // namespace

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    QCoreApplication::setApplicationName(QStringLiteral("HwWidgetQt"));
    QCoreApplication::setOrganizationName(QStringLiteral("hw-widget"));
    QCoreApplication::setApplicationVersion(QStringLiteral(HW_VERSION));
    QApplication::setQuitOnLastWindowClosed(false);

    if (app.arguments().contains(QStringLiteral("--once")))
        return runOnce();

    Cfg cfg = Cfg::load();
    Lang::setLang(cfg.language);
    QApplication::setWindowIcon(makeAppIcon(32));

#ifdef Q_OS_WIN
    // 单实例：与 cs 版一致，重复启动只提示不叠加悬浮窗
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\HwWidgetQt.SingleInstance");
    if (mutex && GetLastError() == ERROR_ALREADY_EXISTS) {
        LightWindow::tell(nullptr, QStringLiteral("HwWidgetQt"),
                          Lang::t(QStringLiteral("app.running")));
        return 0;
    }
#endif

    WidgetWindow w;
    w.show();
    // --panel settings|hosts|sms：启动后直接打开对应子窗（便于脚本化验证/直达）
    const QStringList args = app.arguments();
    const int pi = args.indexOf(QStringLiteral("--panel"));
    if (pi >= 0 && pi + 1 < args.size())
        QTimer::singleShot(300, &w, [&w, panel = args.at(pi + 1)] { w.openPanel(panel); });
    return app.exec();
}