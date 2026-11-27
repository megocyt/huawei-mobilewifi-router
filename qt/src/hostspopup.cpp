#include "hostspopup.h"

#include <QFontMetrics>
#include <QGuiApplication>
#include <QPainter>
#include <QScreen>

HostsPopup::HostsPopup(QWidget *parent)
    : QWidget(parent,
              Qt::ToolTip | Qt::FramelessWindowHint | Qt::WindowStaysOnTopHint
                  | Qt::WindowDoesNotAcceptFocus)
{
    setAttribute(Qt::WA_ShowWithoutActivating, true);
    setAttribute(Qt::WA_TranslucentBackground, false);
    setFocusPolicy(Qt::NoFocus);
    setAutoFillBackground(false);
}

void HostsPopup::showContent(const QString &text, const QPoint &screenPos, const QFont &font)
{
    if (text == content_ && isVisible())
        return;
    content_ = text;
    font_ = font;
    const QFontMetrics fm(font_);
    int w = 0;
    const QStringList lines = text.split(QLatin1Char('\n'));
    for (const QString &line : lines)
        w = qMax(w, fm.horizontalAdvance(line));
    const int h = fm.lineSpacing() * qMax(1, lines.size());
    w += 24;
    int hh = h + 20;

    QRect wa = QGuiApplication::primaryScreen()->availableGeometry();
    const int x = qMin(screenPos.x(), wa.right() - w - 4);
    const int y = qMin(screenPos.y(), wa.bottom() - hh - 4);
    setGeometry(x, y, w, hh);
    if (!isVisible())
        show();
    else
        update();
}

void HostsPopup::hidePopup()
{
    if (isVisible())
        hide();
    content_.clear();
}

bool HostsPopup::event(QEvent *e)
{
    // 浮层不接受鼠标，避免挡住底下的悬浮窗
    if (e->type() == QEvent::MouseButtonPress || e->type() == QEvent::MouseButtonRelease)
        return true;
    return QWidget::event(e);
}

void HostsPopup::paintEvent(QPaintEvent *)
{
    QPainter p(this);
    p.fillRect(QRect(1, 1, width() - 3, height() - 3), Qt::white);
    p.setPen(QColor(180, 180, 180));
    p.drawRect(QRect(1, 1, width() - 3, height() - 3));
    p.setFont(font_);
    p.setPen(QColor(30, 30, 30));
    p.drawText(QRect(12, 10, width() - 24, height() - 20),
               Qt::AlignLeft | Qt::AlignTop | Qt::TextWordWrap, content_);
}