#include "paintutil.h"

#include "cfg.h"

#include <QFontMetricsF>

#include <cmath>

QColor blend(const QColor &a, const QColor &b, double t)
{
    return QColor(int(a.red() + (b.red() - a.red()) * t),
                  int(a.green() + (b.green() - a.green()) * t),
                  int(a.blue() + (b.blue() - a.blue()) * t));
}

Pal Pal::from(const Cfg &c)
{
    Pal p;
    p.fg = Cfg::parseColor(c.fg);
    p.bg = Cfg::parseColor(c.bg);
    p.accent = Cfg::parseColor(c.accent);
    p.warn = Cfg::parseColor(c.warn);
    p.bad = Cfg::parseColor(c.bad);
    return p;
}

QColor Pal::muted() const { return blend(QColor(Qt::gray), fg, 0.38); }
QColor Pal::titleFg() const { return blend(QColor(Qt::gray), fg, 0.55); }
QColor Pal::dim() const { return blend(bg, Qt::white, 0.18); }
QColor Pal::line() const { return blend(bg, Qt::white, 0.14); }
QColor Pal::bgLo() const { return blend(bg, Qt::white, 0.055); }
QColor Pal::dash() const { return blend(bg, Qt::white, 0.22); }

QPainterPath roundRectPath(const QRectF &r, double rad)
{
    QPainterPath path;
    path.addRoundedRect(r, rad, rad);
    return path;
}

void drawWifiArcs(QPainter &p, double cx, double cy, const QColor &c)
{
    p.save();
    QPen pen(c, 1.4);
    pen.setCapStyle(Qt::RoundCap);
    p.setPen(pen);
    p.setBrush(Qt::NoBrush);
    for (int i = 1; i <= 3; ++i) {
        const double rr = 3.0 * i;
        // cs 用 GDI+ DrawArc(225, 90)：GDI+ 顺时针为正，225° 是左上（10:30），扫 90° 经 12 点到 1:30。
        // Qt 逆时针为正且以 1/16 度计，故换成 135°(=-225°) 起步、扫 -90°，弧才覆盖顶部
        //（此前写成 -135 起步，弧落到左侧，三圈叠成横向的「(((」）
        p.drawArc(QRectF(cx - rr, cy + 4 - rr, rr * 2, rr * 2), 135 * 16, -90 * 16);
    }
    p.setPen(Qt::NoPen);
    p.setBrush(c);
    p.drawEllipse(QPointF(cx, cy + 4.5), 1.5, 1.5);
    p.restore();
}

void drawSignalBars(QPainter &p, double cx, double cy, int n, int lit, double maxW,
                    const QColor &on, const QColor &off)
{
    n = qMax(1, n);
    const double bw = std::min(3.0, maxW / n);
    const double x0 = cx - (bw * n + (n - 1)) / 2.0;
    p.save();
    p.setPen(Qt::NoPen);
    for (int i = 0; i < n; ++i) {
        const double bh = 3 + i * (11.0 / qMax(1, n - 1));
        p.setBrush(i < lit ? on : off);
        p.drawRect(QRectF(x0 + i * (bw + 1), cy + 7 - bh, bw, bh));
    }
    p.restore();
}

void drawRowIcon(QPainter &p, const QString &key, const QRectF &box, const QColor &c,
                 const QColor &dim, const QColor &bg, double frac, int bars, int barMax,
                 bool charging)
{
    const double cx = box.center().x(), cy = box.center().y();
    p.save();
    p.setRenderHint(QPainter::Antialiasing, true);
    QPen pen(c, 1.4);
    pen.setCapStyle(Qt::RoundCap);
    QPen penD(dim, 1.4);
    QBrush bFill(c);
    QBrush bDim(dim);

    if (key == QLatin1String("conn") || key == QLatin1String("err")) {
        for (int i = 1; i <= 3; ++i) {
            const double rr = 3.0 * i;
            p.setPen((i == 1 || key == QLatin1String("conn")) ? pen : penD);
            p.setBrush(Qt::NoBrush);
            // 与 cs 的 GDI+ DrawArc(225, 90) 等价：135° 起步扫 -90°，弧顶朝上（详见 drawWifiArcs）
            p.drawArc(QRectF(cx - rr, cy + 4 - rr, rr * 2, rr * 2), 135 * 16, -90 * 16);
        }
        p.setPen(Qt::NoPen);
        p.setBrush(bFill);
        p.drawEllipse(QPointF(cx, cy + 4.5), 1.5, 1.5);
    } else if (key == QLatin1String("model")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawPath(roundRectPath(QRectF(cx - 8, cy - 3, 16, 9), 2));
        p.drawLine(QPointF(cx - 5, cy - 3), QPointF(cx - 5, cy - 7));
        p.drawLine(QPointF(cx + 5, cy - 3), QPointF(cx + 5, cy - 7));
        p.setPen(Qt::NoPen);
        p.setBrush(bFill);
        p.drawEllipse(QPointF(cx, cy + 1.6), 1.2, 1.2);
    } else if (key == QLatin1String("signal")) {
        drawSignalBars(p, cx, cy, qMax(1, barMax), bars, 18, c, dim);
    } else if (key == QLatin1String("rate")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawLine(QPointF(cx - 5, cy - 6), QPointF(cx - 5, cy + 5));
        p.drawLine(QPointF(cx - 8, cy + 2), QPointF(cx - 5, cy + 5));
        p.drawLine(QPointF(cx - 2, cy + 2), QPointF(cx - 5, cy + 5));
        p.drawLine(QPointF(cx + 5, cy + 6), QPointF(cx + 5, cy - 5));
        p.drawLine(QPointF(cx + 2, cy - 2), QPointF(cx + 5, cy - 5));
        p.drawLine(QPointF(cx + 8, cy - 2), QPointF(cx + 5, cy - 5));
    } else if (key == QLatin1String("plan")) {
        p.setPen(penD);
        p.setBrush(Qt::NoBrush);
        p.drawEllipse(QPointF(cx, cy), 7, 7);
        const double sweep = 360.0 * qBound(0.0, frac, 1.0);
        if (sweep > 1) {
            p.setPen(Qt::NoPen);
            p.setBrush(bFill);
            // GDI+ FillPie 从 -90° 顺时针；Qt 角度逆时针为正
            p.drawPie(QRectF(cx - 7, cy - 7, 14, 14), 90 * 16, -int(sweep * 16));
        }
        // 内圈直径 11：环宽 1.5，与其它图标的 1.4 描边观感一致（此前内圈只有 7，环过粗）
        p.setPen(Qt::NoPen);
        p.setBrush(bg);
        p.drawEllipse(QPointF(cx, cy), 5.5, 5.5);
    } else if (key == QLatin1String("traffic")) {
        // 描边云朵：三个图形取并集后只画外轮廓，与其它图标同为线性，不再实心填充
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        QPainterPath cloud;
        cloud.addEllipse(QPointF(cx - 4, cy + 0.8), 3.3, 3.3);
        cloud.addEllipse(QPointF(cx + 2.4, cy - 2.0), 4.0, 4.0);
        cloud.addRoundedRect(QRectF(cx - 7.2, cy - 0.4, 13.2, 5.0), 2.5, 2.5);
        p.drawPath(cloud.simplified());
    } else if (key == QLatin1String("hosts")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawRect(QRectF(cx - 8, cy - 6, 11, 8));
        p.drawRect(QRectF(cx - 3, cy - 2, 11, 8));
    } else if (key == QLatin1String("battery")) {
        // 外框 16 宽（与型号/终端/短信图标同宽），不再额外铺 35% 底色，避免比其它图标重
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawRect(QRectF(cx - 8, cy - 5, 15, 10));
        p.setPen(Qt::NoPen);
        p.setBrush(bFill);
        p.drawRect(QRectF(cx + 7, cy - 2.5, 1.8, 5));
        const double fillW = qBound(0.0, frac, 1.0) * 11.0;
        if (charging) {
            QPolygonF bolt;
            bolt << QPointF(cx + 1.2, cy - 4.4) << QPointF(cx - 3.2, cy + 0.6)
                 << QPointF(cx - 0.6, cy + 0.6) << QPointF(cx - 1.8, cy + 4.4)
                 << QPointF(cx + 3.0, cy - 0.6) << QPointF(cx + 0.4, cy - 0.6);
            p.setBrush(bFill);
            p.drawPolygon(bolt);
        } else if (fillW > 0.5) {
            p.drawRect(QRectF(cx - 6.6, cy - 3.6, fillW, 7.2));
        }
    } else if (key == QLatin1String("msisdn")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawPath(roundRectPath(QRectF(cx - 5, cy - 7, 10, 14), 3));
        p.setPen(Qt::NoPen);
        p.setBrush(bFill);
        p.drawEllipse(QPointF(cx, cy + 5.4), 1.2, 1.2);
    } else if (key == QLatin1String("ip")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawRect(QRectF(cx - 8, cy - 6, 16, 10));
        p.drawLine(QPointF(cx, cy + 4), QPointF(cx, cy + 6));
        p.drawLine(QPointF(cx - 4, cy + 6), QPointF(cx + 4, cy + 6));
    } else if (key == QLatin1String("ip2")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawEllipse(QPointF(cx, cy), 7, 7);
        p.drawEllipse(QRectF(cx - 3, cy - 7, 6, 14));
        p.drawLine(QPointF(cx - 7, cy), QPointF(cx + 7, cy));
    } else if (key == QLatin1String("ip6")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        QPolygonF pts;
        for (int i = 0; i < 6; ++i) {
            const double a = M_PI / 3.0 * i - M_PI / 2.0;
            pts << QPointF(cx + 7 * std::cos(a), cy + 7 * std::sin(a));
        }
        p.drawPolygon(pts);
        p.setPen(Qt::NoPen);
        p.setBrush(bFill);
        p.drawEllipse(QPointF(cx, cy), 1.2, 1.2);
    } else if (key == QLatin1String("uptime")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawLine(QPointF(cx - 5, cy - 7), QPointF(cx + 5, cy - 7));
        p.drawLine(QPointF(cx - 5, cy + 7), QPointF(cx + 5, cy + 7));
        p.drawLine(QPointF(cx - 5, cy - 7), QPointF(cx, cy));
        p.drawLine(QPointF(cx + 5, cy - 7), QPointF(cx, cy));
        p.drawLine(QPointF(cx - 5, cy + 7), QPointF(cx, cy));
        p.drawLine(QPointF(cx + 5, cy + 7), QPointF(cx, cy));
    } else if (key == QLatin1String("sms")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawRect(QRectF(cx - 8, cy - 5, 16, 10));
        QPolygonF env;
        env << QPointF(cx - 8, cy - 5) << QPointF(cx, cy + 1) << QPointF(cx + 8, cy - 5);
        p.drawPolyline(env);
    } else if (key == QLatin1String("clock")) {
        p.setPen(pen);
        p.setBrush(Qt::NoBrush);
        p.drawEllipse(QPointF(cx, cy), 7, 7);
        p.drawLine(QPointF(cx, cy), QPointF(cx, cy - 4));
        p.drawLine(QPointF(cx, cy), QPointF(cx + 3, cy + 1));
    }
    p.restore();
}

void drawMiniArrow(QPainter &p, bool up, const QColor &c, double x, double cy)
{
    const double cx = x + 6.0;
    p.save();
    p.setPen(Qt::NoPen);
    p.setBrush(c);
    if (up) {
        QPolygonF head;
        head << QPointF(cx, cy - 7) << QPointF(cx - 4, cy - 1.5) << QPointF(cx + 4, cy - 1.5);
        p.drawPolygon(head);
        p.drawRect(QRectF(cx - 1.2, cy - 1.5, 2.4, 8));
    } else {
        p.drawRect(QRectF(cx - 1.2, cy - 6.5, 2.4, 8));
        QPolygonF head;
        head << QPointF(cx, cy + 7) << QPointF(cx - 4, cy + 1.5) << QPointF(cx + 4, cy + 1.5);
        p.drawPolygon(head);
    }
    p.restore();
}

void drawMiniErr(QPainter &p, double x, double cy, const QColor &c)
{
    p.save();
    QPen pen(c, 1.4);
    p.setPen(pen);
    p.setBrush(Qt::NoBrush);
    p.drawEllipse(QPointF(x, cy), 6, 6);
    p.drawLine(QPointF(x, cy - 3.5), QPointF(x, cy + 1));
    p.setPen(Qt::NoPen);
    p.setBrush(c);
    p.drawEllipse(QPointF(x, cy + 3.5), 1, 1);
    p.restore();
}

void drawEye(QPainter &p, const QRectF &box, bool open, const QColor &c)
{
    p.save();
    p.setRenderHint(QPainter::Antialiasing, true);
    const double cx = box.center().x(), cy = box.center().y();
    const double ew = 8, eh = 5;
    QPen pen(c, 1.3);
    p.setPen(pen);
    p.setBrush(Qt::NoBrush);
    p.drawEllipse(QRectF(cx - ew, cy - eh, ew * 2, eh * 2));
    if (open)
        p.drawEllipse(QPointF(cx, cy), 2.5, 2.5);
    else
        p.drawLine(QPointF(cx - 2.5, cy), QPointF(cx + 2.5, cy));
    if (!open) {
        p.setPen(QPen(c, 1.2));
        p.drawLine(QPointF(cx - 6, cy + 5), QPointF(cx + 6, cy - 5));
    }
    p.restore();
}