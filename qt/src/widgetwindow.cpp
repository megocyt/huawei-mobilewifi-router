#include "widgetwindow.h"

#include "format.h"
#include "hostspopup.h"
#include "hostsdialog.h"
#include "lang.h"
#include "paintutil.h"
#include "settingsdialog.h"
#include "smsdialog.h"
#include "traymenu.h"
#include "version.h"

#include <QApplication>
#include <QClipboard>
#include <QCloseEvent>
#include <QContextMenuEvent>
#include <QCursor>
#include <QDesktopServices>
#include <QFontMetrics>
#include <QIcon>
#include <QLinearGradient>
#include <QMenu>
#include <QMouseEvent>
#include <QNetworkInterface>
#include <QPainter>
#include <QScreen>
#include <QTime>
#include <QTimer>
#include <QUdpSocket>
#include <QUrl>
#include <QVBoxLayout>
#include <QWaitCondition>
#include <QWheelEvent>
#include <QWindow>

#include <climits>
#include <cmath>

#ifdef Q_OS_WIN
#include <windows.h>
#include <dwmapi.h>
#endif

namespace {

constexpr int W = 396, Pad = 14, Rad = 12, RowGap = 4, IconW = 22;
constexpr int MiniW = 234, MiniH = 30, MiniColW = 78, MiniBarColW = 30;

QString gLocalIp;

QString trunc(const QString &s, int n)
{
    if (s.isEmpty())
        return s;
    return s.size() <= n ? s : s.left(n);
}

/// 缺失字段统一占位 "-"（空 / "-" 均视为未取到）
QString ph(const QString &v)
{
    return (v.isEmpty() || v == QLatin1String("-")) ? QStringLiteral("-") : v;
}

QString numPart(const QString &s)
{
    int i = s.size() - 1;
    while (i >= 0 && !s.at(i).isDigit())
        --i;
    return s.left(i + 1);
}

QString tailUnit(const QString &s)
{
    int i = s.size() - 1;
    while (i >= 0 && !s.at(i).isDigit())
        --i;
    return s.mid(i + 1);
}

QString maskPhone(const QString &phone)
{
    if (phone.size() <= 4)
        return phone;
    if (phone.size() <= 7)
        return phone.left(2) + QString(phone.size() - 2, QLatin1Char('*'));
    return phone.left(3) + QStringLiteral("****") + phone.right(4);
}

/// 与 cs 的 LocalIp 同款：UDP 连接不产生流量，只为拿到出接口地址。
/// cfg.host 允许写成 “http://192.168.8.1:8080”，直接拿去 connectToHost 会解析失败、
/// 永远显示 “-”，所以先去掉协议前缀与端口只留主机名。
QString localIp(const QString &target)
{
    if (!gLocalIp.isEmpty())
        return gLocalIp;
    QString host = target.trimmed();
    const int scheme = host.indexOf(QStringLiteral("://"));
    if (scheme >= 0)
        host = host.mid(scheme + 3);
    if (host.startsWith(QLatin1Char('['))) { // [IPv6]:port
        const int close = host.indexOf(QLatin1Char(']'));
        if (close > 0)
            host = host.mid(1, close - 1);
    } else {
        const int colon = host.indexOf(QLatin1Char(':'));
        if (colon >= 0)
            host = host.left(colon);
    }
    host = host.trimmed();
    if (host.isEmpty())
        host = QStringLiteral("192.168.8.1");

    QUdpSocket u;
    u.connectToHost(host, 9);
    const QHostAddress a = u.localAddress();
    gLocalIp = (a.isNull() || a.toString().isEmpty()) ? QStringLiteral("-") : a.toString();
    return gLocalIp;
}

} // namespace

WidgetWindow::WidgetWindow(QWidget *parent) : QWidget(parent)
{
    cfg_ = Cfg::load();
    Lang::setLang(cfg_.language);
    setWindowTitle(Lang::t(QStringLiteral("tray.title")));
    setWindowFlags(Qt::FramelessWindowHint | Qt::Tool);
    if (cfg_.topmost)
        setWindowFlags(windowFlags() | Qt::WindowStaysOnTopHint);
    setAttribute(Qt::WA_TranslucentBackground, true);
    setAttribute(Qt::WA_NoSystemBackground, true);
    setMouseTracking(true);
    setFocusPolicy(Qt::NoFocus);

    const QIcon appIcon(QStringLiteral(":/HwWidget.ico"));
    logo_ = appIcon.pixmap(20, 20);

    intervalMs_ = int(qMax(1.0, cfg_.interval) * 1000);
    buildFonts();

    tray_ = new TrayIcon(this);
    connect(tray_, &TrayIcon::clicked, this, &WidgetWindow::toggleVisible);
    connect(tray_, &TrayIcon::doubleClicked, this, &WidgetWindow::showSettings);

    clockTimer_ = new QTimer(this);
    clockTimer_->setInterval(1000);
    connect(clockTimer_, &QTimer::timeout, this, [this] {
        // 每秒兜底重应用 DWM 描边属性：BORDER_COLOR=NONE 是易失的合成器状态，
        // 会被系统在失活/显隐/主题事件等时机重置回默认描边（深色迷你条上表现为 1px 白边）
        applyDwmFrame();
        if (appliedLang_ != Lang::lang()) {
            relayout();
            return;
        }
        const QString now = QTime::currentTime().toString(QStringLiteral("HH:mm:ss"));
        for (Row &r : rows_) {
            if (r.key != QLatin1String("clock"))
                continue;
            if (r.value != now) {
                r.value = now;
                update();
            }
            return;
        }
    });
    clockTimer_->start();

    hintTimer_ = new QTimer(this);
    hintTimer_->setSingleShot(true);
    hintTimer_->setInterval(1600);
    connect(hintTimer_, &QTimer::timeout, this, [this] {
        hint_.clear();
        update();
    });

    saveTimer_ = new QTimer(this);
    saveTimer_->setSingleShot(true);
    saveTimer_->setInterval(1200);
    connect(saveTimer_, &QTimer::timeout, this, [this] { cfg_.save(); });

    // 穿透位在 Qt 侧可能被重新创建的窗口抹掉，1s 兜底补一次
    ctTimer_ = new QTimer(this);
    ctTimer_->setInterval(1000);
    connect(ctTimer_, &QTimer::timeout, this, [this] {
        applyClickThrough();
        applyDwmFrame();
    });
    ctTimer_->start();

    rebuildMenu();
    tray_->show();

    dev_ = newDevice();
    wake_ = new QWaitCondition();
    poll_ = std::thread(&WidgetWindow::pollLoop, this);

    applyChrome();

    const QRect wa = screen() ? screen()->availableGeometry() : QRect(0, 0, 1920, 1080);
    if (cfg_.posX != INT_MIN) {
        const int px = qMax(wa.left(), qMin(wa.right() - width() - 8, cfg_.posX));
        const int py = qMax(wa.top(), qMin(wa.bottom() - height() - 8, cfg_.posY));
        move(px, py);
    } else {
        move(wa.right() - W - 40, wa.top() + 60);
    }
}

WidgetWindow::~WidgetWindow()
{
    stopPoll_ = true;
    if (wake_) {
        wakeMutex_.lock();
        wakeFlag_ = true;
        wake_->wakeAll();
        wakeMutex_.unlock();
    }
    if (poll_.joinable())
        poll_.join();
    delete wake_;
    delete dev_;
}

Device *WidgetWindow::newDevice() const
{
    const QString h = cfg_.host.trimmed();
    const QString url = h.contains(QStringLiteral("://")) ? h : QStringLiteral("http://") + h;
    const QString u = cfg_.user.isEmpty() ? QStringLiteral("admin") : cfg_.user;
    return new Device(url, u, cfg_.password);
}

// ---------------------------------------------------------------- 设备门面（后台线程）

QVector<SmsItem> WidgetWindow::smsQueryAll(QString *err)
{
    if (!devMutex_.tryLock(30000)) {
        if (err)
            *err = Lang::t(QStringLiteral("sms.busy"));
        return {};
    }
    QString e2;
    const QVector<SmsItem> list = dev_ ? dev_->smsAll(&e2) : QVector<SmsItem>();
    devMutex_.unlock();
    if (err)
        *err = e2;
    return list;
}

QString WidgetWindow::smsMarkRead(const QStringList &idx)
{
    if (!devMutex_.tryLock(20000))
        return Lang::t(QStringLiteral("sms.busy"));
    const QString r = dev_ ? dev_->markRead(idx) : Lang::t(QStringLiteral("sms.busy"));
    devMutex_.unlock();
    return r;
}

QString WidgetWindow::smsDelete(const QStringList &idx)
{
    if (!devMutex_.tryLock(20000))
        return Lang::t(QStringLiteral("sms.busy"));
    const QString r = dev_ ? dev_->deleteSms(idx) : Lang::t(QStringLiteral("sms.busy"));
    devMutex_.unlock();
    return r;
}

void WidgetWindow::smsCounts(int *unread, int *total)
{
    const Snap s = snap_;
    if (!haveSnap_) {
        if (unread)
            *unread = 0;
        if (total)
            *total = 0;
        return;
    }
    if (unread)
        *unread = s.smsUnread;
    if (total)
        *total = s.smsInbox;
}

QVector<Host> WidgetWindow::hostsList(int *active, int *total)
{
    const Snap s = snap_;
    if (!haveSnap_) {
        if (active)
            *active = 0;
        if (total)
            *total = 0;
        return {};
    }
    if (active)
        *active = s.hostsActive;
    if (total)
        *total = s.hostsTotal;
    return s.hosts;
}

void WidgetWindow::notifyResample()
{
    wakeMutex_.lock();
    wakeFlag_ = true;
    if (wake_)
        wake_->wakeAll();
    wakeMutex_.unlock();
}

// ---------------------------------------------------------------- 字体 / 行模型

void WidgetWindow::buildFonts()
{
    const int size = qMax(8, cfg_.fontSize);
    // 卡片内所有文字（标题、标签、数值、徽章）共用同一字号，绘制时不再按宽度缩字
    fLabel_ = QFont(cfg_.fontFam);
    fLabel_.setPointSizeF(size);
    fValue_ = QFont(cfg_.fontFam);
    fValue_.setPointSizeF(size);
    fMono_ = QFont(QStringLiteral("Consolas"));
    fMono_.setPointSizeF(size);

    rowH_ = qMax(QFontMetrics(fValue_).height(),
                 qMax(QFontMetrics(fLabel_).height(), QFontMetrics(fMono_).height()));
    computeLabelWidth();
}

void WidgetWindow::computeLabelWidth()
{
    const QStringList probes = {
        QStringLiteral("lbl.network"), QStringLiteral("lbl.model"),   QStringLiteral("lbl.signal"),
        QStringLiteral("lbl.rate"),    QStringLiteral("lbl.plan"),    QStringLiteral("lbl.traffic"),
        QStringLiteral("lbl.hosts"),   QStringLiteral("lbl.battery"), QStringLiteral("lbl.local"),
        QStringLiteral("lbl.wan"),     QStringLiteral("lbl.v6"),      QStringLiteral("lbl.uptime"),
        QStringLiteral("lbl.sms"),     QStringLiteral("lbl.clock"),   QStringLiteral("lbl.status"),
        QStringLiteral("lbl.detail"),  QStringLiteral("lbl.num")};
    const QFontMetrics fm(fLabel_);
    int lw = 0;
    for (const QString &k : probes)
        lw = qMax(lw, fm.horizontalAdvance(Lang::t(k)));
    labelW_ = lw + 2;
    appliedLang_ = Lang::lang();
}

QVector<WidgetWindow::Row> WidgetWindow::buildRows() const
{
    QVector<Row> out;
    const Snap &s = snap_;
    const Pal pal = Pal::from(cfg_);
    const bool has = haveSnap_ && s.ok;

    if (cfg_.rowOn(QStringLiteral("conn"))) {
        Row r;
        r.key = QStringLiteral("conn");
        r.label = Lang::t(QStringLiteral("lbl.network"));
        if (!has) {
            r.value = err_.isEmpty() ? Lang::t(QStringLiteral("v.fail")) : trunc(err_, 30);
            r.color = pal.bad;
        } else {
            r.value = (s.net + QLatin1Char(' ') + Lang::carrier(s.carrier)).trimmed();
            r.color = s.connected ? pal.accent : pal.bad;
        }
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("model"))) {
        Row r;
        r.key = QStringLiteral("model");
        r.label = Lang::t(QStringLiteral("lbl.model"));
        r.value = ph(has ? s.model : QString());
        r.color = pal.fg;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("signal"))) {
        Row r;
        r.key = QStringLiteral("signal");
        r.label = Lang::t(QStringLiteral("lbl.signal"));
        const QString rsrp = ph(has ? s.rsrp : QString());
        const QString sinr = ph(has ? s.sinr : QString());
        r.value = (rsrp == QLatin1String("-") && sinr == QLatin1String("-"))
                      ? QStringLiteral("-")
                      : rsrp + QStringLiteral(" dBm · SINR ") + sinr;
        r.kind = Signal;
        r.bars = has ? s.sigBars : 0;
        r.barMax = qMax(1, has ? s.sigMax : 5);
        const QString lvl = has ? s.rsrpLevel : QStringLiteral("-");
        r.color = (lvl == QString::fromUtf8("优") || lvl == QString::fromUtf8("良"))
                      ? pal.accent
                      : (lvl == QString::fromUtf8("中") ? pal.warn
                                                        : (lvl == QLatin1String("-") ? pal.fg : pal.bad));
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("rate"))) {
        Row r;
        r.key = QStringLiteral("rate");
        r.label = Lang::t(QStringLiteral("lbl.rate"));
        r.value = has ? QStringLiteral("↓") + QString(humanRate(double(s.rateDown))).remove(QLatin1Char(' '))
                            + QStringLiteral("  ↑")
                            + QString(humanRate(double(s.rateUp))).remove(QLatin1Char(' '))
                      : QStringLiteral("-");
        r.color = pal.fg;
        r.mono = true;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("plan"))) {
        Row r;
        r.key = QStringLiteral("plan");
        r.label = Lang::t(QStringLiteral("lbl.plan"));
        const bool ok = has && s.planLimit > 0;
        r.value = QStringLiteral("-");
        r.tail = QStringLiteral("-");
        r.color = pal.fg;
        if (ok) {
            const QString u = QString(humanBytes(double(s.planUsed), 1)).remove(QLatin1Char(' '));
            const QString lim = QString(s.planLimitRaw).remove(QLatin1Char(' '));
            r.value = tailUnit(u) == tailUnit(lim) ? numPart(u) + QStringLiteral("/") + lim
                                                   : u + QStringLiteral("/") + lim;
            if (s.planDaysLeft >= 0)
                r.value += QLatin1Char(' ')
                           + fmt1(Lang::t(QStringLiteral("fmt.daysleft")), QString::number(s.planDaysLeft));
            r.frac = qBound(0.0, s.planPercent, 100.0) / 100.0;
            r.tail = trimNumber(s.planPercent, 1) + QStringLiteral("%");
            r.color = s.planOverLimit ? pal.bad : (s.planOverThreshold ? pal.warn : pal.accent);
        }
        r.kind = Plan;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("traffic"))) {
        Row r;
        r.key = QStringLiteral("traffic");
        r.label = Lang::t(QStringLiteral("lbl.traffic"));
        r.value = has ? Lang::t(QStringLiteral("v.today")) + QLatin1Char(' ')
                            + QString(humanBytes(double(s.dayUsed), 1)).remove(QLatin1Char(' '))
                            + QStringLiteral(" · ") + Lang::t(QStringLiteral("v.month")) + QLatin1Char(' ')
                            + QString(humanBytes(double(s.monthDown + s.monthUp), 1)).remove(QLatin1Char(' '))
                      : QStringLiteral("-");
        r.color = pal.fg;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("hosts"))) {
        Row r;
        r.key = QStringLiteral("hosts");
        r.label = Lang::t(QStringLiteral("lbl.hosts"));
        r.value = has ? QString::number(s.hostsActive) + QStringLiteral(" / ") + QString::number(s.hostsTotal)
                      : QStringLiteral("-");
        r.color = pal.fg;
        r.mono = true;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("battery"))) {
        Row r;
        r.key = QStringLiteral("battery");
        r.label = Lang::t(QStringLiteral("lbl.battery"));
        const bool ok = has && !s.battery.isEmpty() && s.battery != QLatin1String("-");
        double pct = 0;
        if (ok)
            pct = QString(s.battery).remove(QLatin1Char('%')).toDouble();
        const bool low = ok && pct <= 20 && !s.charging;
        r.value = ok ? s.battery + (s.charging ? QLatin1Char(' ') + Lang::t(QStringLiteral("v.charging"))
                                               : QString())
                     : QStringLiteral("-");
        r.kind = Battery;
        r.frac = pct / 100.0;
        r.color = low ? pal.warn : (ok ? pal.accent : pal.fg);
        r.charging = ok && s.charging;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("msisdn"))) {
        Row r;
        r.key = QStringLiteral("msisdn");
        r.label = Lang::t(QStringLiteral("lbl.num"));
        const bool ok = has && !s.msisdn.isEmpty() && s.msisdn != QLatin1String("-");
        r.value = ok ? (revealed_ ? s.msisdn : maskPhone(s.msisdn)) : QStringLiteral("-");
        r.color = pal.fg;
        r.mono = true;
        r.eye = ok;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("ip"))) {
        Row a;
        a.key = QStringLiteral("ip");
        a.label = Lang::t(QStringLiteral("lbl.local"));
        a.value = localIp(cfg_.host);
        a.color = pal.fg;
        a.mono = true;
        out.append(a);

        Row b;
        b.key = QStringLiteral("ip2");
        b.label = Lang::t(QStringLiteral("lbl.wan"));
        b.value = ph(has ? s.wanIp : QString());
        b.color = pal.fg;
        b.mono = true;
        out.append(b);

        Row c;
        c.key = QStringLiteral("ip6");
        c.label = Lang::t(QStringLiteral("lbl.v6"));
        c.value = ph(has ? s.wanIpv6 : QString());
        c.color = pal.fg;
        c.mono = true;
        out.append(c);
    }
    if (cfg_.rowOn(QStringLiteral("uptime"))) {
        Row r;
        r.key = QStringLiteral("uptime");
        r.label = Lang::t(QStringLiteral("lbl.uptime"));
        r.value = (has && s.uptime > 0) ? humanDur(s.uptime) : QStringLiteral("-");
        r.color = pal.fg;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("sms"))) {
        Row r;
        r.key = QStringLiteral("sms");
        r.label = Lang::t(QStringLiteral("lbl.sms"));
        r.value = has ? fmt(Lang::t(QStringLiteral("fmt.sms")),
                            {QString::number(s.smsUnread), QString::number(s.smsInbox)})
                      : QStringLiteral("-");
        r.color = (has && s.smsUnread > 0) ? pal.warn : pal.fg;
        out.append(r);
    }
    if (cfg_.rowOn(QStringLiteral("clock"))) {
        Row r;
        r.key = QStringLiteral("clock");
        r.label = Lang::t(QStringLiteral("lbl.clock"));
        r.value = QTime::currentTime().toString(QStringLiteral("HH:mm:ss"));
        r.color = pal.fg;
        r.mono = true;
        out.append(r);
    }
    return out;
}

void WidgetWindow::relayout()
{
    if (appliedLang_ != Lang::lang())
        computeLabelWidth();
    if (cfg_.style == QLatin1String("mini")) {
        relayoutMini();
        update();
        return;
    }
    rows_ = buildRows();
    int h = cfg_.showTitle ? (Pad - 2 + QFontMetrics(fValue_).height() + 12 + 1 + 8) : Pad;
    rowTops_.resize(rows_.size());
    hostsRowIdx_ = -1;
    for (int i = 0; i < rows_.size(); ++i) {
        rowTops_[i] = h;
        if (rows_.at(i).key == QLatin1String("hosts"))
            hostsRowIdx_ = i;
        h += rowH_ + RowGap;
        if (rows_.at(i).kind == Plan)
            h += 12;
    }
    h += Pad - 3 - RowGap;
    if (width() != W || height() != h)
        resize(W, h);
    update();
    if (hostsPopup_ && hostsPopup_->isVisible())
        checkHostsHover(mapFromGlobal(QCursor::pos()));
}

void WidgetWindow::relayoutMini()
{
    hideHostsTip();
    // 迷你条列宽按当前字号下最宽的速率串（"888.8 KB/s"）算出：宽度只跟字号走、
    // 不跟数据走，既保持固定尺寸不抖动，也不会把 "KB/s" 截成省略号
    const QFontMetrics fm(fMono_);
    miniColW_ = qMax(MiniColW, fm.horizontalAdvance(QStringLiteral("888.8 KB/s")) + 2);
    const int w = qMax(MiniW, 2 * miniColW_ + 78);
    if (width() != w || height() != MiniH) {
        resize(w, MiniH);
        keepOnScreen();
    }
}

void WidgetWindow::keepOnScreen()
{
    const QRect wa = screen() ? screen()->availableGeometry() : QRect(0, 0, 1920, 1080);
    const int px = qMax(wa.left(), qMin(wa.right() - width() - 4, x()));
    const int py = qMax(wa.top(), qMin(wa.bottom() - height() - 4, y()));
    if (px != x() || py != y())
        move(px, py);
}

QString WidgetWindow::rowKeyAt(int y) const
{
    for (int i = 0; i < rows_.size(); ++i) {
        const int h = rowH_ + RowGap + (rows_.at(i).kind == Plan ? 12 : 0);
        if (y >= rowTops_.at(i) && y < rowTops_.at(i) + h)
            return rows_.at(i).key;
    }
    return QString();
}

// ---------------------------------------------------------------- 绘制

void WidgetWindow::paintEvent(QPaintEvent *)
{
    QPainter p(this);
    p.setRenderHint(QPainter::Antialiasing, true);
    p.setRenderHint(QPainter::TextAntialiasing, true);
    if (cfg_.style == QLatin1String("mini"))
        paintMini(p);
    else
        paintFull(p);
}

void WidgetWindow::paintFull(QPainter &p)
{
    const Pal pal = Pal::from(cfg_);
    const QRect rc(0, 0, width() - 1, height() - 1);
    QLinearGradient grad(0, 0, 0, height());
    grad.setColorAt(0, pal.bgLo());
    grad.setColorAt(1, pal.bg);
    p.setClipPath(roundRectPath(QRectF(0, 0, width(), height()), Rad));
    p.fillRect(rect(), grad);

    if (cfg_.clickThrough && cfg_.clickThroughBorder) {
        QPen pen(pal.dash(), 2);
        pen.setStyle(Qt::DashLine);
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawPath(roundRectPath(QRectF(rc.x() + 2, rc.y() + 2, rc.width() - 4, rc.height() - 4), Rad));
    }

    int y = Pad;
    if (cfg_.showTitle) {
        y = Pad - 2;
        const bool alive = haveSnap_ && err_.isEmpty();
        const QColor dot = alive ? pal.accent : pal.bad;
        int textX = Pad;
        if (!logo_.isNull()) {
            p.drawPixmap(QRect(Pad - 2, y, 20, 20), logo_);
            textX = Pad + 20 + 2;
        }
        p.setPen(Qt::NoPen);
        p.setBrush(blend(pal.bg, dot, 0.35));
        p.drawEllipse(QRectF(textX - 8, y + 20 - 8, 9, 9));
        p.setBrush(dot);
        p.drawEllipse(QRectF(textX - 6, y + 20 - 6, 5, 5));

        const QString title = hint_.isEmpty() ? (QStringLiteral("HUAWEI WiFi · ") + cfg_.host) : hint_;
        p.setFont(fValue_);
        p.setPen(pal.titleFg());
        // 标题靠右不再给 badge 留位（原 -70），超长时用省略号而非硬裁
        const int titleW = rc.right() - Pad - (textX + 2);
        p.drawText(QRect(textX + 2, y, titleW, QFontMetrics(fValue_).height()),
                   Qt::AlignLeft | Qt::AlignVCenter,
                   QFontMetrics(fValue_).elidedText(title, Qt::ElideRight, titleW));

        y += QFontMetrics(fValue_).height() + 12;
        p.setPen(QPen(pal.line(), 1));
        p.drawLine(Pad, y, rc.right() - Pad, y);
        y += 8;
    }

    eyeRect_ = QRect();
    for (const Row &r : rows_)
        drawRow(p, r, y, rc);
}

void WidgetWindow::drawRow(QPainter &p, const Row &r, int &y, const QRect &rc)
{
    const Pal pal = Pal::from(cfg_);
    const int right = rc.right() - Pad;
    drawRowIcon(p, r.key, QRectF(Pad, y, IconW, rowH_), r.color, pal.dim(), pal.bg, r.frac, r.bars,
                r.barMax, r.charging);

    const int lx = Pad + IconW + 6;
    p.setFont(fLabel_);
    p.setPen(pal.muted());
    p.drawText(QRect(lx, y, labelW_, rowH_), Qt::AlignLeft | Qt::AlignVCenter, r.label);

    // 套餐百分比尾巴按当前字号实测宽度，避免同一字号下与数值贴在一起
    const int tailW = r.kind == Plan
                          ? QFontMetrics(fValue_).horizontalAdvance(QStringLiteral("888.8%")) + 8
                          : (r.eye ? 22 : 0);
    const int vx = lx + labelW_ + 8;
    const QRect vRect(vx, y, right - tailW - vx, rowH_);
    // 字号与其它行一致，放不下时只做省略号截断，不缩字号
    const QFont &vf = r.mono ? fMono_ : fValue_;
    p.setFont(vf);
    p.setPen(r.color);
    const QString shown = QFontMetrics(vf).elidedText(r.value, Qt::ElideRight, vRect.width());
    p.drawText(vRect, Qt::AlignRight | Qt::AlignVCenter, shown);

    if (r.eye) {
        eyeRect_ = QRect(right - 18, y + (rowH_ - 14) / 2, 18, 14);
        drawEye(p, QRectF(eyeRect_), revealed_, r.color);
    }

    if (r.kind == Plan) {
        p.setFont(fValue_);
        p.setPen(r.color);
        p.drawText(QRect(right - tailW, y, tailW, rowH_), Qt::AlignRight | Qt::AlignVCenter, r.tail);
        const int ty = y + rowH_ + 4;
        const QRectF track(vx, ty, right - vx, 4);
        p.setPen(Qt::NoPen);
        p.setBrush(blend(pal.bg, Qt::white, 0.13));
        p.drawPath(roundRectPath(track, 2));
        const int fw = int(std::round((track.width() - 2) * qBound(0.0, r.frac, 1.0)));
        if (fw > 1) {
            p.setBrush(r.color);
            p.drawPath(roundRectPath(QRectF(track.x(), track.y(), fw + 1, track.height()), 2));
        }
        y += 12;
    }
    y += rowH_ + RowGap;
}

void WidgetWindow::paintMini(QPainter &p)
{
    const Pal pal = Pal::from(cfg_);
    const int w = width(), h = height();
    QLinearGradient grad(0, 0, 0, h);
    grad.setColorAt(0, pal.bgLo());
    grad.setColorAt(1, pal.bg);
    p.setClipPath(roundRectPath(QRectF(0, 0, w, h), 5));
    p.fillRect(rect(), grad);

    if (cfg_.clickThrough && cfg_.clickThroughBorder) {
        QPen pen(pal.dash(), 2);
        pen.setStyle(Qt::DashLine);
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawPath(roundRectPath(QRectF(2, 2, w - 5, h - 5), 4));
    }

    if (!hint_.isEmpty()) {
        p.setFont(fValue_);
        p.setPen(pal.titleFg());
        p.drawText(rect(), Qt::AlignCenter, hint_);
        return;
    }

    const bool has = haveSnap_ && snap_.ok;
    const int cy = h / 2;

    if (!has && !err_.isEmpty()) {
        drawMiniErr(p, 16, cy, pal.bad);
        const QString t = trunc(err_, 14);
        p.setFont(fValue_);
        p.setPen(pal.bad);
        p.drawText(QPointF(28, cy + QFontMetrics(fValue_).ascent() / 2.0 - 1), t);
        return;
    }

    // 信号柱：左侧底部对齐，按质量着色；无数据置灰
    const int n = has ? qMax(1, snap_.sigMax) : 5;
    const int lit = has ? snap_.sigBars : 0;
    const QString lvl = has ? snap_.rsrpLevel : QStringLiteral("-");
    const QColor sc = !has ? pal.dim()
                           : ((lvl == QString::fromUtf8("优") || lvl == QString::fromUtf8("良"))
                                  ? pal.accent
                                  : (lvl == QString::fromUtf8("中")
                                         ? pal.warn
                                         : (lvl == QLatin1String("-") ? pal.fg : pal.bad)));
    const double bw = std::min(3.0, 18.0 / n);
    const double bx0 = 10;
    p.setPen(Qt::NoPen);
    for (int i = 0; i < n; ++i) {
        const double bh = 3 + i * (11.0 / qMax(1, n - 1));
        p.setBrush(i < lit ? sc : pal.dim());
        p.drawRect(QRectF(bx0 + i * (bw + 1), cy + 7 - bh, bw, bh));
    }

    const QColor up = pal.warn, dn = pal.accent;
    const int aX = 10 + MiniBarColW;
    drawMiniArrow(p, true, up, aX, cy);
    {
        const QString t = has ? humanRate(double(snap_.rateUp)) : QStringLiteral("-");
        const QString shown = QFontMetrics(fMono_).elidedText(t, Qt::ElideRight, miniColW_);
        const int tw = QFontMetrics(fMono_).horizontalAdvance(shown);
        p.setFont(fMono_);
        p.setPen(up);
        p.drawText(QPointF(aX + 14 + miniColW_ - tw, cy + QFontMetrics(fMono_).ascent() / 2.0 - 1),
                   shown);
    }
    drawMiniArrow(p, false, dn, aX + 14 + miniColW_ + 6, cy);
    {
        const QString t = has ? humanRate(double(snap_.rateDown)) : QStringLiteral("-");
        const QString shown = QFontMetrics(fMono_).elidedText(t, Qt::ElideRight, miniColW_);
        const int tw = QFontMetrics(fMono_).horizontalAdvance(shown);
        p.setFont(fMono_);
        p.setPen(dn);
        p.drawText(QPointF(aX + 14 + miniColW_ + 20 + miniColW_ - tw,
                           cy + QFontMetrics(fMono_).ascent() / 2.0 - 1),
                   shown);
    }
}

// ---------------------------------------------------------------- 终端悬停提示

QString WidgetWindow::buildHostsTooltip() const
{
    const QVector<Host> &list = snap_.hosts;
    if (list.isEmpty())
        return QString();
    QString sb = QString::number(snap_.hostsActive) + QStringLiteral(" / ")
                 + QString::number(snap_.hostsTotal) + QLatin1Char('\n');
    int shown = 0;
    for (const Host &h : list) {
        if (!h.active && list.size() > 8)
            continue;
        if (shown >= 15) {
            sb += QStringLiteral("...\n");
            break;
        }
        sb += (h.active ? QStringLiteral("● ") : QStringLiteral("○ "))
              + (h.name.isEmpty() ? QStringLiteral("-") : h.name);
        if (!h.ip.isEmpty())
            sb += QStringLiteral("  ") + h.ip;
        sb += QLatin1Char('\n');
        QString meta = QStringLiteral("   ");
        if (!h.mac.isEmpty())
            meta += h.mac;
        if (!h.brand.isEmpty())
            meta += QStringLiteral("  ") + h.brand;
        if (!h.band.isEmpty())
            meta += QStringLiteral("  ") + h.band;
        sb += meta + QLatin1Char('\n');
        ++shown;
    }
    while (sb.endsWith(QLatin1Char('\n')))
        sb.chop(1);
    return sb;
}

void WidgetWindow::checkHostsHover(const QPoint &pt)
{
    if (cfg_.style == QLatin1String("mini") || hostsRowIdx_ < 0 || rowTops_.isEmpty()) {
        hideHostsTip();
        return;
    }
    const int top = rowTops_.at(hostsRowIdx_);
    const int bot = top + rowH_;
    if (pt.y() < top || pt.y() >= bot || !haveSnap_ || snap_.hosts.isEmpty()) {
        hideHostsTip();
        return;
    }
    const QString text = buildHostsTooltip();
    if (text.isEmpty()) {
        hideHostsTip();
        return;
    }
    if (!hostsPopup_)
        hostsPopup_ = new HostsPopup(nullptr);
    const QPoint screenPos = mapToGlobal(QPoint(Pad + IconW + 6, bot + 6));
    hostsPopup_->showContent(text, screenPos, fLabel_);
}

void WidgetWindow::hideHostsTip()
{
    if (hostsPopup_)
        hostsPopup_->hidePopup();
}

// ---------------------------------------------------------------- 外观 / Win32

void WidgetWindow::applyChrome()
{
    buildFonts();
    const bool wantTop = cfg_.topmost;
    if (bool(windowFlags() & Qt::WindowStaysOnTopHint) != wantTop) {
        const bool wasVisible = isVisible();
        setWindowFlag(Qt::WindowStaysOnTopHint, wantTop);
        if (wasVisible)
            show();
    }
    setWindowOpacity(qBound(0.25, cfg_.alpha, 1.0));
    relayout();
    applyClickThrough();
    applyDwmFrame();
}

void WidgetWindow::applyClickThrough()
{
#ifdef Q_OS_WIN
    // 用 windowHandle() 而不是 winId()：后者在 nativeEvent 期间会强制创建平台窗口，造成重入崩溃
    const HWND h = windowHandle() ? HWND(windowHandle()->winId()) : nullptr;
    if (!h)
        return;
    LONG_PTR ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
    if (cfg_.clickThrough) {
        // Qt 的 WA_TranslucentBackground 已是分层窗口，只需补 WS_EX_TRANSPARENT；
        // 不能调 SetLayeredWindowAttributes，否则会打乱 Qt 的 alpha 合成
        if (!(ex & WS_EX_TRANSPARENT))
            SetWindowLongPtrW(h, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
    } else if (ex & WS_EX_TRANSPARENT) {
        SetWindowLongPtrW(h, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
    }
#endif
}

void WidgetWindow::applyDwmFrame()
{
#ifdef Q_OS_WIN
    const HWND h = windowHandle() ? HWND(windowHandle()->winId()) : nullptr;
    if (!h)
        return;
    int round = 1;   // DWMWCP_DONOTROUND
    DwmSetWindowAttribute(h, 33, &round, sizeof(int));
    // DWMWA_BORDER_COLOR：DEFAULT=0xFFFFFFFF，NONE=0xFFFFFFFE（不能写成 1 或 2）
    int border = int(0xFFFFFFFE);
    DwmSetWindowAttribute(h, 34, &border, sizeof(int));
    MARGINS m{-1, -1, -1, -1};
    DwmExtendFrameIntoClientArea(h, &m);
#endif
}

bool WidgetWindow::nativeEvent(const QByteArray &eventType, void *message, qintptr *result)
{
#ifdef Q_OS_WIN
    if (eventType == "windows_generic_MSG" && message) {
        MSG *msg = static_cast<MSG *>(message);
        switch (msg->message) {
        case WM_NCPAINT:
            // 完全吞掉，阻止系统沿窗口边缘补画 1px 亮边
            *result = 0;
            return true;
        case WM_NCACTIVATE:
            *result = 1;
            return true;
        case WM_SETTINGCHANGE:
        case WM_WINDOWPOSCHANGED:
        case WM_STYLECHANGED:
        case WM_DPICHANGED:
            // 不能在消息回调里直接调 DWM：窗口创建期间会重入平台层导致栈损坏，
            // 改为稍后投递（用标志位合并请求，避免拖动时排队堆积）
            if (!dwmPending_) {
                dwmPending_ = true;
                QMetaObject::invokeMethod(
                    this,
                    [this] {
                        dwmPending_ = false;
                        applyDwmFrame();
                    },
                    Qt::QueuedConnection);
            }
            break;
        default:
            break;
        }
    }
#else
    Q_UNUSED(eventType)
    Q_UNUSED(message)
    Q_UNUSED(result)
#endif
    return QWidget::nativeEvent(eventType, message, result);
}

void WidgetWindow::showEvent(QShowEvent *e)
{
    QWidget::showEvent(e);
    applyClickThrough();
    applyDwmFrame();
}

void WidgetWindow::closeEvent(QCloseEvent *e)
{
    if (!quitting_) {
        e->ignore();
        toggleVisible();
        return;
    }
    QWidget::closeEvent(e);
}

// ---------------------------------------------------------------- 交互

void WidgetWindow::mousePressEvent(QMouseEvent *e)
{
    if (e->button() == Qt::LeftButton && !eyeRect_.isNull() && eyeRect_.contains(e->pos())) {
        revealed_ = !revealed_;
        const QString raw = haveSnap_ ? snap_.msisdn : QString();
        if (!raw.isEmpty() && raw != QLatin1String("-"))
            for (Row &r : rows_) {
                if (r.key == QLatin1String("msisdn")) {
                    r.value = revealed_ ? raw : maskPhone(raw);
                    break;
                }
            }
        update();
        return;
    }
    if (e->button() == Qt::LeftButton && !cfg_.locked) {
        dragging_ = true;
        dragOff_ = e->pos();
    }
    pressPt_ = e->pos();
    pressRow_ = (e->button() == Qt::LeftButton && cfg_.style != QLatin1String("mini"))
                    ? rowKeyAt(e->pos().y())
                    : QString();
    moved_ = false;
}

void WidgetWindow::mouseMoveEvent(QMouseEvent *e)
{
    if (dragging_ && !cfg_.locked && (e->buttons() & Qt::LeftButton)) {
        move(x() + e->pos().x() - dragOff_.x(), y() + e->pos().y() - dragOff_.y());
        if (!moved_ && (qAbs(e->pos().x() - pressPt_.x()) > 3 || qAbs(e->pos().y() - pressPt_.y()) > 3))
            moved_ = true;
        hideHostsTip();
    } else {
        checkHostsHover(e->pos());
    }
}

void WidgetWindow::mouseReleaseEvent(QMouseEvent *e)
{
    if (dragging_) {
        dragging_ = false;
        savePos();
    }
    if (!moved_ && e->button() == Qt::LeftButton && pressRow_ == QLatin1String("sms"))
        showSms();
}

void WidgetWindow::mouseDoubleClickEvent(QMouseEvent *e)
{
    if (e->button() != Qt::LeftButton)
        return;
    if (cfg_.style != QLatin1String("mini") && rowKeyAt(e->pos().y()) == QLatin1String("hosts")) {
        hideHostsTip();
        showHosts();
    } else {
        doCmd(QStringLiteral("manage"));
    }
}

void WidgetWindow::wheelEvent(QWheelEvent *e)
{
    applyAlphaStep(e->angleDelta().y());
    e->accept();
}

void WidgetWindow::leaveEvent(QEvent *e)
{
    QWidget::leaveEvent(e);
    hideHostsTip();
}

void WidgetWindow::contextMenuEvent(QContextMenuEvent *e)
{
    if (menu_)
        menu_->exec(e->globalPos());
}

void WidgetWindow::applyAlphaStep(int delta)
{
    const double a = cfg_.alpha + (delta > 0 ? 0.05 : -0.05);
    cfg_.alpha = std::round(qBound(0.25, a, 1.0) * 100) / 100.0;
    setWindowOpacity(cfg_.alpha);
    // 不弹提示：滚轮微调透明度的频率很高，气泡会盖住标题行打断视线
    saveTimer_->stop();
    saveTimer_->start();
    update();
}

void WidgetWindow::savePos()
{
    cfg_.posX = x();
    cfg_.posY = y();
    cfg_.save();
}

// ---------------------------------------------------------------- 命令

void WidgetWindow::toggleVisible()
{
    if (isVisible()) {
        hide();
    } else {
        show();
        raise();
        activateWindow();
        if (!cfg_.topmost) {
            setWindowFlag(Qt::WindowStaysOnTopHint, false);
        } else {
            setWindowFlag(Qt::WindowStaysOnTopHint, true);
        }
    }
}

void WidgetWindow::doCmd(const QString &cmd)
{
    if (cmd == QLatin1String("toggle"))
        toggleVisible();
    else if (cmd == QLatin1String("settings"))
        showSettings();
    else if (cmd == QLatin1String("sms"))
        showSms();
    else if (cmd == QLatin1String("hosts"))
        showHosts();
    else if (cmd == QLatin1String("refresh")) {
        // 抢不到锁就跳过，下一轮采样会因会话死自动重登
        if (devMutex_.tryLock(100)) {
            if (dev_)
                dev_->forgetSession();
            devMutex_.unlock();
        }
        this->notifyResample();
    } else if (cmd == QLatin1String("manage")) {
        QString url = dev_ ? dev_->baseUrl : QString();
        if (url.isEmpty()) {
            const QString h = cfg_.host.trimmed();
            url = h.contains(QStringLiteral("://")) ? h : QStringLiteral("http://") + h;
        }
        QDesktopServices::openUrl(QUrl(url));
    } else if (cmd == QLatin1String("top")) {
        cfg_.topmost = !cfg_.topmost;
        applyChrome();
        cfg_.save();
        rebuildMenu();
    } else if (cmd == QLatin1String("lock")) {
        cfg_.locked = !cfg_.locked;
        cfg_.save();
        hint_ = cfg_.locked ? Lang::t(QStringLiteral("hint.locked"))
                            : Lang::t(QStringLiteral("hint.unlocked"));
        hintTimer_->stop();
        hintTimer_->start();
        rebuildMenu();
        update();
    } else if (cmd == QLatin1String("click")) {
        cfg_.clickThrough = !cfg_.clickThrough;
        cfg_.save();
        applyClickThrough();
        // 不弹提示：菜单项本身已在「穿透模式 / 关闭穿透模式」之间切换，状态自明
        rebuildMenu();
        update();
    } else if (cmd == QLatin1String("style")) {
        cfg_.style = cfg_.style == QLatin1String("mini") ? QStringLiteral("full") : QStringLiteral("mini");
        cfg_.save();
        relayout();
        rebuildMenu();
    } else if (cmd == QLatin1String("quit")) {
        quit();
    }
}

void WidgetWindow::showSettings()
{
    if (settings_) {
        settings_->raise();
        settings_->activateWindow();
        return;
    }
    auto *dlg = new SettingsDialog(cfg_, nullptr);
    settings_ = dlg;
    connect(dlg, &QDialog::finished, this, [this, dlg](int) { onSettingsClosed(dlg); });
    dlg->show();
    dlg->raise();
    dlg->activateWindow();
}

void WidgetWindow::onSettingsClosed(SettingsDialog *dlg)
{
    settings_ = nullptr;
    const bool saved = dlg->saved();
    Cfg next = dlg->result();
    dlg->deleteLater();
    if (!saved) {
        // 取消：语言预览要回退到已保存值
        Lang::setLang(cfg_.language);
        relayout();
        return;
    }
    cfg_ = next;
    cfg_.save();
    Lang::setLang(cfg_.language);
    intervalMs_ = int(qMax(1.0, cfg_.interval) * 1000);

    // 设备不可达时 Sample 会长时间持锁，抢不到锁就把 Logout+换 dev 排到后台
    if (devMutex_.tryLock(300)) {
        Device *old = dev_;
        dev_ = newDevice();
        devMutex_.unlock();
        if (old)
            delete old;
    } else {
        Device *old = dev_;
        Device *fresh = newDevice();
        std::thread([this, old, fresh] {
            devMutex_.lock();
            if (old)
                old->logout();
            dev_ = fresh;
            devMutex_.unlock();
            notifyResample();
        }).detach();
    }
    gLocalIp.clear();
    applyChrome();
    rebuildMenu();
    notifyResample();
}

void WidgetWindow::showSms()
{
    if (smsDlg_) {
        smsDlg_->raise();
        smsDlg_->activateWindow();
        return;
    }
    auto *dlg = new SmsDialog(this, nullptr);
    smsDlg_ = dlg;
    connect(dlg, &QDialog::finished, this, [this] {
        smsDlg_ = nullptr;
        notifyResample();
    });
    dlg->show();
    dlg->raise();
    dlg->activateWindow();
}

void WidgetWindow::showHosts()
{
    if (hostsDlg_) {
        hostsDlg_->raise();
        hostsDlg_->activateWindow();
        return;
    }
    auto *dlg = new HostsDialog(this, nullptr);
    hostsDlg_ = dlg;
    connect(dlg, &QDialog::finished, this, [this] { hostsDlg_ = nullptr; });
    dlg->show();
    dlg->raise();
    dlg->activateWindow();
}

void WidgetWindow::openPanel(const QString &name)
{
    if (name == QLatin1String("hosts"))
        showHosts();
    else if (name == QLatin1String("sms"))
        showSms();
    else
        showSettings();
}

void WidgetWindow::quit()
{
    quitting_ = true;
    stopPoll_ = true;
    notifyResample();
    if (poll_.joinable())
        poll_.join();
    if (devMutex_.tryLock(200)) {
        if (dev_)
            dev_->logout();
        devMutex_.unlock();
    }
    savePos();
    if (tray_)
        tray_->hide();
    if (hostsPopup_)
        hostsPopup_->hidePopup();
    close();
    QCoreApplication::quit();
}

// ---------------------------------------------------------------- 菜单 / 托盘

void WidgetWindow::buildMenu()
{
    menu_ = new QMenu(this);
    styleDarkMenu(menu_);
    const auto add = [this](const QString &text, const QString &cmd) {
        QAction *a = menu_->addAction(text);
        connect(a, &QAction::triggered, this, [this, cmd] { doCmd(cmd); });
        return a;
    };
    add(Lang::t(QStringLiteral("menu.toggle")), QStringLiteral("toggle"));
    add(Lang::t(QStringLiteral("menu.settings")), QStringLiteral("settings"));
    add(Lang::t(QStringLiteral("menu.refresh")), QStringLiteral("refresh"));
    add(Lang::t(QStringLiteral("menu.manage")), QStringLiteral("manage"));
    add(Lang::t(QStringLiteral("menu.sms")), QStringLiteral("sms"));
    add(Lang::t(QStringLiteral("menu.hosts")), QStringLiteral("hosts"));
    menu_->addSeparator();
    add(cfg_.topmost ? Lang::t(QStringLiteral("menu.topoff")) : Lang::t(QStringLiteral("menu.topon")),
        QStringLiteral("top"));
    add(cfg_.locked ? Lang::t(QStringLiteral("menu.unlock")) : Lang::t(QStringLiteral("menu.lock")),
        QStringLiteral("lock"));
    add(cfg_.clickThrough ? Lang::t(QStringLiteral("menu.clickoff"))
                          : Lang::t(QStringLiteral("menu.clickon")),
        QStringLiteral("click"));
    add(cfg_.style == QLatin1String("mini") ? Lang::t(QStringLiteral("menu.stylefull"))
                                            : Lang::t(QStringLiteral("menu.style")),
        QStringLiteral("style"));
    menu_->addSeparator();
    add(Lang::t(QStringLiteral("menu.quit")), QStringLiteral("quit"));
    menu_->addSeparator();
    QAction *ver = menu_->addAction(Lang::t(QStringLiteral("menu.version")) + QStringLiteral("  ")
                                    + QStringLiteral(HW_VERSION));
    ver->setEnabled(false);
}

void WidgetWindow::rebuildMenu()
{
    QMenu *old = menu_;
    buildMenu();
    if (tray_)
        tray_->setMenu(menu_);
    if (old) {
        old->deleteLater();
    }
}

void WidgetWindow::updateTrayTip()
{
    if (!tray_)
        return;
    QString tip = Lang::t(QStringLiteral("tray.title"));
    if (haveSnap_) {
        tip += QStringLiteral(" · ") + snap_.net + QLatin1Char(' ') + snap_.conn;
        // 当日使用量：悬停托盘就能看到今天用了多少（CurrentDayUsed，与卡片「流量」行同源）。
        // 三项同为 0 时视为月度统计没取到，不显示，避免误报 0B。
        if (snap_.dayUsed > 0 || snap_.monthDown + snap_.monthUp > 0 || snap_.planLimit > 0) {
            tip += QStringLiteral(" · ") + Lang::t(QStringLiteral("v.today")) + QLatin1Char(' ')
                   + QString(humanBytes(double(snap_.dayUsed), 1)).remove(QLatin1Char(' '));
        }
        if (snap_.planLimit > 0)
            tip += QStringLiteral(" · ") + Lang::t(QStringLiteral("lbl.plan")) + QLatin1Char(' ')
                   + QString::number(int(std::round(snap_.planPercent))) + QLatin1Char('%');
        tip += QStringLiteral(" · ") + Lang::t(QStringLiteral("lbl.hosts")) + QLatin1Char(' ')
               + QString::number(snap_.hostsActive);
    }
    if (!err_.isEmpty())
        tip += QStringLiteral(" · ⚠") + trunc(err_, 20);
    if (tip.size() > 100)
        tip = tip.left(100); // NT6+ 的 szTip 是 128 字符，100 留余量又不截掉最后一项
    tray_->setToolTip(tip);
}

// ---------------------------------------------------------------- 采集线程

void WidgetWindow::pollLoop()
{
    while (!stopPoll_) {
        Snap s;
        {
            QMutexLocker<QMutex> locker(&devMutex_);
            if (dev_)
                s = dev_->sample();
        }
        const QString e = (!s.ok && !s.err.isEmpty()) ? s.err : QString();
        QMetaObject::invokeMethod(this, [this, s, e] { onSnap(s, e); }, Qt::QueuedConnection);

        QMutexLocker<QMutex> lock(&wakeMutex_);
        if (!wakeFlag_ && !stopPoll_)
            wake_->wait(&wakeMutex_, intervalMs_.load());
        wakeFlag_ = false;
    }
}

void WidgetWindow::onSnap(const Snap &s, const QString &err)
{
    if (stopPoll_)
        return;
    snap_ = s;
    haveSnap_ = true;
    err_ = err;
    relayout();
    updateTrayTip();
    if (!err.isEmpty() && err != lastErr_ && tray_)
        tray_->showMessage(Lang::t(QStringLiteral("tray.balloon")), trunc(err, 90));
    lastErr_ = err;
}