#include "lightwindow.h"

#include "lang.h"

#include <QApplication>
#include <QFontMetrics>
#include <QIcon>
#include <QMessageBox>
#include <QMouseEvent>
#include <QPainter>
#include <QPushButton>
#include <QShowEvent>
#include <QWindow>

#ifdef Q_OS_WIN
#include <windows.h>
#include <dwmapi.h>
#endif

namespace Light {
QColor accent() { return QColor(0, 120, 212); }
QColor accentLight() { return QColor(229, 241, 251); }
QColor titleBack() { return QColor(240, 250, 247); }
QColor stripBack() { return QColor(240, 240, 240); }
QColor windowBorder() { return QColor(218, 218, 218); }
QColor btnFace() { return QColor(253, 253, 253); }
QColor btnHover() { return QColor(229, 241, 251); }
QColor btnPress() { return QColor(204, 228, 247); }
QColor btnBorder() { return QColor(187, 187, 187); }
QColor editBorder() { return QColor(160, 160, 160); }
QColor groupBorder() { return QColor(214, 214, 214); }
QColor text() { return QColor(32, 32, 32); }
QColor gray() { return QColor(120, 120, 120); }
} // namespace Light

LightWindow::LightWindow(QWidget *parent) : QDialog(parent, Qt::Window | Qt::FramelessWindowHint)
{
    setAttribute(Qt::WA_DeleteOnClose, false);
    setMouseTracking(true);
    setFont(QFont(QStringLiteral("Microsoft YaHei UI"), 9));
    body_ = new QWidget(this);
    body_->move(1, TitleH);
}

void LightWindow::applyRoundFrame(QWidget *w)
{
#ifdef Q_OS_WIN
    const HWND h = w->windowHandle() ? HWND(w->windowHandle()->winId()) : nullptr;
    if (!h)
        return;
    int round = 2;   // DWMWCP_ROUND
    DwmSetWindowAttribute(h, 33, &round, sizeof(int));
    int border = 0x00DADADA;
    DwmSetWindowAttribute(h, 34, &border, sizeof(int));
#else
    Q_UNUSED(w)
#endif
}

bool LightWindow::askYesNo(QWidget *parent, const QString &title, const QString &text)
{
    QMessageBox box(parent);
    box.setWindowTitle(title);
    box.setText(text);
    box.setIcon(QMessageBox::Question);
    QPushButton *yes = box.addButton(Lang::t(QStringLiteral("btn.yes")), QMessageBox::YesRole);
    box.addButton(Lang::t(QStringLiteral("btn.no")), QMessageBox::NoRole);
    box.setDefaultButton(yes);
    box.setEscapeButton(box.buttons().last());
    box.exec();
    return box.clickedButton() == yes;
}

void LightWindow::tell(QWidget *parent, const QString &title, const QString &text)
{
    QMessageBox box(parent);
    box.setWindowTitle(title);
    box.setText(text);
    box.setIcon(QMessageBox::Information);
    box.addButton(Lang::t(QStringLiteral("btn.save")), QMessageBox::AcceptRole);
    box.exec();
}

bool LightWindow::filterNative(const QByteArray &eventType, void *message, qintptr *result)
{
#ifdef Q_OS_WIN
    if (eventType == "windows_generic_MSG" && message) {
        MSG *msg = static_cast<MSG *>(message);
        switch (msg->message) {
        case WM_NCCALCSIZE:
            if (msg->wParam != 0) {
                *result = 0;
                return true;
            }
            break;
        case WM_NCHITTEST:
            *result = HTCLIENT;
            return true;
        case 0x00AE:                // WM_NCUAHDRAWCAPTION（部分旧 SDK 未定义此宏）
        case 0x00AF:                // WM_NCUAHDRAWFRAME
        case WM_NCPAINT:
            *result = 0;
            return true;
        case WM_NCACTIVATE:
            *result = 1;
            return true;
        default:
            break;
        }
    }
#else
    Q_UNUSED(eventType)
    Q_UNUSED(message)
    Q_UNUSED(result)
#endif
    return false;
}

void LightWindow::showEvent(QShowEvent *e)
{
    QDialog::showEvent(e);
    applyRoundFrame(this);
}

bool LightWindow::nativeEvent(const QByteArray &eventType, void *message, qintptr *result)
{
    if (filterNative(eventType, message, result))
        return true;
    return QDialog::nativeEvent(eventType, message, result);
}

void LightWindow::paintEvent(QPaintEvent *)
{
    QPainter p(this);
    p.fillRect(rect(), Qt::white);

    const QRect tb(0, 0, width(), TitleH);
    p.fillRect(tb, Light::titleBack());
    p.setPen(Light::windowBorder());
    p.drawLine(0, TitleH - 1, width() - 1, TitleH - 1);

    const QIcon icon(QStringLiteral(":/HwWidget.ico"));
    if (!icon.isNull())
        p.drawPixmap(QRect(10, (TitleH - 16) / 2, 16, 16), icon.pixmap(16, 16));

    QFont f = font();
    f.setBold(true);
    p.setFont(f);
    p.setPen(Light::text());
    p.drawText(QRect(34, 0, width() - 80, TitleH), Qt::AlignLeft | Qt::AlignVCenter, windowTitle());

    // 关闭按钮
    closeRect_ = QRect(width() - 40, 4, 32, TitleH - 8);
    if (closeHover_) {
        p.fillRect(closeRect_, QColor(232, 17, 35));
        p.setPen(Qt::white);
    } else {
        p.setPen(Light::gray());
    }
    QFont cf = font();
    cf.setBold(false);
    p.setFont(cf);
    p.drawText(closeRect_, Qt::AlignCenter, QStringLiteral("✕"));

    // 外框
    p.setPen(Light::windowBorder());
    p.setBrush(Qt::NoBrush);
    p.drawRect(QRect(0, 0, width() - 1, height() - 1));
}

void LightWindow::mousePressEvent(QMouseEvent *e)
{
    if (e->button() != Qt::LeftButton)
        return;
    if (closeRect_.contains(e->pos())) {
        close();
        return;
    }
    if (e->pos().y() < TitleH) {
        dragging_ = true;
        dragOff_ = e->pos();
    }
}

void LightWindow::mouseMoveEvent(QMouseEvent *e)
{
    const bool hover = closeRect_.contains(e->pos());
    if (hover != closeHover_) {
        closeHover_ = hover;
        update();
    }
    if (dragging_ && (e->buttons() & Qt::LeftButton))
        move(x() + e->pos().x() - dragOff_.x(), y() + e->pos().y() - dragOff_.y());
}

void LightWindow::mouseReleaseEvent(QMouseEvent *e)
{
    Q_UNUSED(e)
    dragging_ = false;
}