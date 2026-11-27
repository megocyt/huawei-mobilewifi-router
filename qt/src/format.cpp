#include "format.h"

#include "lang.h"

#include <QStringList>

#include <cmath>

namespace {
const char *const kUnits[] = {"B", "KB", "MB", "GB", "TB", "PB"};
constexpr int kUnitCount = 6;
} // namespace

QString trimNumber(double v, int digits)
{
    QString s = QString::number(v, 'f', digits);
    if (s.contains(QLatin1Char('.'))) {
        while (s.endsWith(QLatin1Char('0')))
            s.chop(1);
        if (s.endsWith(QLatin1Char('.')))
            s.chop(1);
    }
    return s;
}

QString humanBytes(double n, int digits)
{
    if (std::isnan(n) || std::isinf(n))
        return QStringLiteral("-");
    const QString neg = n < 0 ? QStringLiteral("-") : QString();
    double v = std::fabs(n);
    for (int i = 0; i < kUnitCount; i++) {
        if (v < 1024 || i == kUnitCount - 1) {
            const int dg = i == 0 ? 0 : digits;
            return neg + trimNumber(std::round(v * std::pow(10.0, dg)) / std::pow(10.0, dg), 4)
                   + QLatin1Char(' ') + QLatin1String(kUnits[i]);
        }
        v /= 1024;
    }
    return QStringLiteral("-");
}

QString humanRate(double bps) { return humanBytes(std::fabs(bps), 1) + QStringLiteral("/s"); }

QString humanDur(qint64 sec)
{
    if (sec < 0)
        return QStringLiteral("-");
    const qint64 d = sec / 86400, h = sec % 86400 / 3600, m = sec % 3600 / 60;
    const QString ud = Lang::t(QStringLiteral("u.day")), uh = Lang::t(QStringLiteral("u.hour")),
                  um = Lang::t(QStringLiteral("u.minute")), us = Lang::t(QStringLiteral("u.second"));
    const QString sp = (Lang::lang() == QLatin1String("zh-CN") || Lang::lang() == QLatin1String("zh-TW"))
                           ? QString()
                           : QStringLiteral(" ");
    if (d > 0)
        return QString::number(d) + ud + sp + QString::number(h) + uh;
    if (h > 0)
        return QString::number(h) + uh + sp + QString::number(m) + um;
    return QString::number(m) + um + sp + QString::number(sec % 60) + us;
}

qint64 parseSize(const QString &text)
{
    if (text.isEmpty())
        return 0;
    QString digitsOnly;
    digitsOnly.reserve(text.size());
    for (const QChar ch : text)
        if (ch.isDigit() || ch == QLatin1Char('.'))
            digitsOnly.append(ch);
    bool ok = false;
    const double num = digitsOnly.toDouble(&ok);
    if (!ok)
        return 0;
    const QString up = text.toUpper();
    qint64 mul = 1;
    for (int i = kUnitCount - 1; i >= 0; i--) {
        if (up.contains(QLatin1String(kUnits[i]))) {
            mul = qint64(std::pow(1024.0, i));
            break;
        }
    }
    return qint64(num * double(mul));
}