#include "traymenu.h"

#include "paintutil.h"

#include <QDateTime>
#include <QGuiApplication>
#include <QMenu>
#include <QPainter>
#include <QPixmap>
#include <QStyleHints>
#include <QSystemTrayIcon>
#include <QTimer>

QIcon makeAppIcon(int size)
{
    QPixmap pm(size, size);
    pm.fill(Qt::transparent);
    QPainter p(&pm);
    p.setRenderHint(QPainter::Antialiasing, true);

    const QColor bg(0x0d, 0x11, 0x17);
    const QColor accent(0x3f, 0xb9, 0x50);
    p.setPen(Qt::NoPen);
    p.setBrush(bg);
    p.drawRoundedRect(QRectF(0.5, 0.5, size - 1.0, size - 1.0), size * 0.22, size * 0.22);

    // 三段 WiFi 弧 + 圆点，按图标尺寸等比缩放
    const double s = size / 16.0;
    p.translate(0, size * 0.06);
    p.scale(s, s);
    drawWifiArcs(p, 8.0, 8.0, accent);
    p.end();
    return QIcon(pm);
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
    tray_->setIcon(makeAppIcon(32));
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