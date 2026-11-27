#pragma once
#include <QColor>
#include <QFont>
#include <QPainter>
#include <QPainterPath>
#include <QRect>
#include <QRectF>
#include <QString>

struct Cfg;

/// 当前配色集合：全部派生自 Cfg 的 fg/bg/accent/warn/bad
struct Pal
{
    QColor fg, bg, accent, warn, bad;

    static Pal from(const Cfg &c);
    QColor muted() const;     // 标签灰
    QColor titleFg() const;   // 标题灰
    QColor dim() const;       // 未点亮的信号柱/圆环底
    QColor line() const;      // 分隔线
    QColor bgLo() const;      // 渐变顶部
    QColor dash() const;      // 穿透虚线
};

QColor blend(const QColor &a, const QColor &b, double t);
QPainterPath roundRectPath(const QRectF &r, double rad);

/// 文字放不下时逐步缩小字号（最多降 3pt 且 ≥8pt，与 cs 的 FitValueFont 一致）
QFont fitFont(const QFont &base, const QString &text, int maxW);

/// 段弧 WiFi 图标（conn/err 行与托盘图标共用）
void drawWifiArcs(QPainter &p, double cx, double cy, const QColor &c);
/// 信号柱（底部对齐，按点亮数着色）
void drawSignalBars(QPainter &p, double cx, double cy, int n, int lit, double maxW,
                    const QColor &on, const QColor &off);
/// 行首图标（按 row key 几何绘制）
void drawRowIcon(QPainter &p, const QString &key, const QRectF &box, const QColor &c,
                 const QColor &dim, const QColor &bg, double frac, int bars, int barMax,
                 bool charging);
/// 迷你条实心箭头
void drawMiniArrow(QPainter &p, bool up, const QColor &c, double x, double cy);
/// 迷你条错误叹号
void drawMiniErr(QPainter &p, double x, double cy, const QColor &c);
/// 手机号明文/脱敏眼睛图标
void drawEye(QPainter &p, const QRectF &box, bool open, const QColor &c);