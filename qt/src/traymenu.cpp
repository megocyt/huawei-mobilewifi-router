#include "traymenu.h"

#include <QDateTime>
#include <QGuiApplication>
#include <QMenu>
#include <QPainter>
#include <QPixmap>
#include <QStyleHints>
#include <QSystemTrayIcon>
#include <QTimer>

namespace {
/// 兜底图标：透明底三条信号弧 + 端点，配色对齐 logo
/// （薄荷绿 #3CCB7F → 深青 #0EA5A0），与 cs 的 TrayIcon.DrawFallback 同形，不画背景方块
QPixmap fallbackIcon(int px)
{
    QPixmap pm(px, px);
    pm.fill(Qt::transparent);
    QPainter p(&pm);
    p.setRenderHint(QPainter::Antialiasing, true);
    const double u = px / 32.0;
    const QPointF c(px / 2.0, px * 0.72);
    const struct {
        double r;
        const char *hex;
    } rings[] = {{12.0, "#3CCB7F"}, {8.0, "#28CB93"}, {4.0, "#0EA5A0"}};
    p.setBrush(Qt::NoBrush);
    for (const auto &ring : rings) {
        QPen pen(QColor(ring.hex), 3.0 * u);
        pen.setCapStyle(Qt::RoundCap);
        p.setPen(pen);
        const double r = ring.r * u;
        p.drawArc(QRectF(c.x() - r, c.y() - r, r * 2, r * 2), 40 * 16, 100 * 16);
    }
    p.setPen(Qt::NoPen);
    p.setBrush(QColor(QStringLiteral("#0EA5A0")));
    p.drawEllipse(c, 1.6 * u, 1.6 * u);
    return pm;
}
} // namespace

QIcon appIcon()
{
    const QIcon ico(QStringLiteral(":/HwWidget.ico"));
    if (!ico.isNull() && !ico.availableSizes().isEmpty())
        return ico;

    QIcon fallback;
    for (int s : {16, 20, 24, 32, 40, 48, 64, 256})
        fallback.addPixmap(fallbackIcon(s));
    return fallback;
}

void styleDarkMenu(QMenu *menu)
{
    menu->setStyleSheet(QStringLiteral(
        "QMenu { background:#0d1117; color:#c9d1d9; border:1px solid #30363d; padding:6px 4px; }"
        "QMenu::item { padding:7px 18px 7px 14px; min-width:150px; }"
        "QMenu::item:selected { background:#223c28; color:#e6edf3; }"
        "QMenu::item:disabled { color:#8b949e; }"
        "QMenu::separator { height:1px; background:#30363d; margin:5px 8px; }"));
}

TrayIcon::TrayIcon(QObject *parent)
    : QObject(parent), tray_(new QSystemTrayIcon(this)), clickTimer_(new QTimer(this))
{
    tray_->setIcon(appIcon());
    clickTimer_->setSingleShot(true);
    clickTimer_->setInterval(QGuiApplication::styleHints()->mouseDoubleClickInterval() + 50);
    connect(clickTimer_, &QTimer::timeout, this, [this] {
        clickTimer_->stop();
        emit clicked();
    });
    connect(tray_, &QSystemTrayIcon::activated, this,
            [this](QSystemTrayIcon::ActivationReason reason) { onActivated(int(reason)); });
}

TrayIcon::~TrayIcon() = default;

void TrayIcon::setMenu(QMenu *menu)
{
    menu_ = menu;
    tray_->setContextMenu(menu_);
}

void TrayIcon::setToolTip(const QString &tip) { tray_->setToolTip(tip); }

void TrayIcon::showMessage(const QString &title, const QString &text)
{
    tray_->showMessage(title, text, QSystemTrayIcon::Warning, 3000);
}

void TrayIcon::show() { tray_->show(); }
void TrayIcon::hide() { tray_->hide(); }

void TrayIcon::onActivated(int reason)
{
    switch (QSystemTrayIcon::ActivationReason(reason)) {
    case QSystemTrayIcon::Trigger:
        // 双击后紧跟的那次单击不能重启定时器，否则 500ms 后会额外 toggle 一次
        if (QDateTime::currentMSecsSinceEpoch() - lastDoubleClick_ <= 500)
            return;
        clickTimer_->stop();
        clickTimer_->start();
        break;
    case QSystemTrayIcon::DoubleClick:
        lastDoubleClick_ = QDateTime::currentMSecsSinceEpoch();
        clickTimer_->stop();
        emit doubleClicked();
        break;
    default:
        break;
    }
}