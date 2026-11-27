#include "cfg.h"

#include "lang.h"

#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSaveFile>
#include <QStandardPaths>

#include <climits>

QStringList Cfg::rowKeys()
{
    return {QStringLiteral("conn"),   QStringLiteral("model"),   QStringLiteral("signal"),
            QStringLiteral("rate"),   QStringLiteral("plan"),    QStringLiteral("traffic"),
            QStringLiteral("hosts"),  QStringLiteral("battery"), QStringLiteral("msisdn"),
            QStringLiteral("ip"),     QStringLiteral("uptime"),  QStringLiteral("sms"),
            QStringLiteral("clock")};
}

Cfg::Cfg()
{
    static const QStringList defaultOn = {QStringLiteral("conn"),   QStringLiteral("model"),
                                          QStringLiteral("signal"), QStringLiteral("rate"),
                                          QStringLiteral("plan"),   QStringLiteral("hosts"),
                                          QStringLiteral("battery"), QStringLiteral("msisdn")};
    for (const QString &k : rowKeys())
        rows[k] = defaultOn.contains(k);
}

QString Cfg::cfgDir()
{
    // 与 cs / python 两版共用同一目录：%APPDATA%\HWiFiWidget
    const QString appdata = qEnvironmentVariable("APPDATA");
    if (!appdata.isEmpty())
        return QDir(appdata).filePath(QStringLiteral("HWiFiWidget"));
    return QDir(QStandardPaths::writableLocation(QStandardPaths::GenericConfigLocation))
        .filePath(QStringLiteral("HWiFiWidget"));
}

QString Cfg::cfgPath() { return QDir(cfgDir()).filePath(QStringLiteral("config.json")); }
QString Cfg::logPath() { return QDir(cfgDir()).filePath(QStringLiteral("error.log")); }

static QString str(const QJsonObject &o, const char *k, const QString &def)
{
    const QJsonValue v = o.value(QLatin1String(k));
    return v.isString() ? v.toString() : def;
}

static double num(const QJsonObject &o, const char *k, double def)
{
    const QJsonValue v = o.value(QLatin1String(k));
    return v.isDouble() ? v.toDouble() : def;
}

static bool boolean(const QJsonObject &o, const char *k, bool def)
{
    const QJsonValue v = o.value(QLatin1String(k));
    return v.isBool() ? v.toBool() : def;
}

Cfg Cfg::load()
{
    Cfg c;
    QFile f(cfgPath());
    if (!f.open(QIODevice::ReadOnly)) {
        c.password = qEnvironmentVariable("HWIFI_PASSWORD");
        return c;
    }
    const QJsonDocument doc = QJsonDocument::fromJson(f.readAll());
    f.close();
    if (!doc.isObject()) {
        c.password = qEnvironmentVariable("HWIFI_PASSWORD");
        return c;
    }
    const QJsonObject o = doc.object();
    c.host = str(o, "host", c.host);
    c.user = str(o, "user", c.user);
    c.password = str(o, "password", QString());
    c.language = str(o, "language", c.language);
    const QString st = str(o, "style", c.style);
    c.style = st == QLatin1String("mini") ? QStringLiteral("mini") : QStringLiteral("full");
    c.interval = num(o, "interval", c.interval);
    c.alpha = num(o, "alpha", c.alpha);
    c.fontFam = str(o, "font_family", c.fontFam);
    c.fontSize = int(num(o, "font_size", c.fontSize));
    c.fg = str(o, "fg", c.fg);
    c.bg = str(o, "bg", c.bg);
    c.accent = str(o, "accent", c.accent);
    c.warn = str(o, "warn", c.warn);
    c.bad = str(o, "bad", c.bad);
    c.topmost = boolean(o, "topmost", c.topmost);
    c.borderless = boolean(o, "borderless", c.borderless);
    c.locked = boolean(o, "locked", c.locked);
    c.clickThrough = boolean(o, "click_through", c.clickThrough);
    c.clickThroughBorder = boolean(o, "click_through_border", c.clickThroughBorder);
    c.autoStart = boolean(o, "auto_start", c.autoStart);
    c.showTitle = boolean(o, "show_title", c.showTitle);
    c.savePassword = boolean(o, "save_password", c.savePassword);

    const QJsonValue rowsVal = o.value(QStringLiteral("rows"));
    if (rowsVal.isObject()) {
        const QJsonObject ro = rowsVal.toObject();
        for (auto it = ro.begin(); it != ro.end(); ++it)
            if (c.rows.contains(it.key()))
                c.rows[it.key()] = it.value().toBool();
    }
    const QJsonValue pos = o.value(QStringLiteral("pos"));
    if (pos.isArray()) {
        const QJsonArray a = pos.toArray();
        if (a.size() >= 2) {
            c.posX = int(a.at(0).toDouble());
            c.posY = int(a.at(1).toDouble());
        }
    }

    if (c.password.isEmpty())
        c.password = qEnvironmentVariable("HWIFI_PASSWORD");
    if (!Lang::isKnown(c.language))
        c.language = QStringLiteral("zh-CN");
    return c;
}

void Cfg::save() const
{
    QJsonObject o;
    o.insert(QStringLiteral("host"), host);
    o.insert(QStringLiteral("user"), user);
    o.insert(QStringLiteral("password"), savePassword ? password : QString());
    o.insert(QStringLiteral("language"), language);
    o.insert(QStringLiteral("style"), style);
    o.insert(QStringLiteral("interval"), interval);
    o.insert(QStringLiteral("alpha"), qRound(alpha * 100) / 100.0);
    o.insert(QStringLiteral("font_family"), fontFam);
    o.insert(QStringLiteral("font_size"), fontSize);
    o.insert(QStringLiteral("fg"), fg);
    o.insert(QStringLiteral("bg"), bg);
    o.insert(QStringLiteral("accent"), accent);
    o.insert(QStringLiteral("warn"), warn);
    o.insert(QStringLiteral("bad"), bad);
    QJsonObject rowsObj;
    for (const QString &k : rowKeys())
        rowsObj.insert(k, rows.value(k, false));
    o.insert(QStringLiteral("rows"), rowsObj);
    o.insert(QStringLiteral("topmost"), topmost);
    o.insert(QStringLiteral("borderless"), borderless);
    o.insert(QStringLiteral("locked"), locked);
    o.insert(QStringLiteral("click_through"), clickThrough);
    o.insert(QStringLiteral("click_through_border"), clickThroughBorder);
    o.insert(QStringLiteral("auto_start"), autoStart);
    o.insert(QStringLiteral("show_title"), showTitle);
    o.insert(QStringLiteral("save_password"), savePassword);
    if (posX == INT_MIN) {
        o.insert(QStringLiteral("pos"), QJsonValue::Null);
    } else {
        QJsonArray a;
        a.append(posX);
        a.append(posY);
        o.insert(QStringLiteral("pos"), a);
    }

    QDir().mkpath(cfgDir());
    QSaveFile f(cfgPath());
    if (!f.open(QIODevice::WriteOnly)) {
        logError(QStringLiteral("cannot write %1").arg(cfgPath()));
        return;
    }
    f.write(QJsonDocument(o).toJson(QJsonDocument::Indented));
    f.commit();
}

void Cfg::logError(const QString &text)
{
    QDir().mkpath(cfgDir());
    QFile f(logPath());
    if (!f.open(QIODevice::Append | QIODevice::Text))
        return;
    const QString line = QStringLiteral("[%1] %2\n")
                             .arg(QDateTime::currentDateTime().toString(QStringLiteral("yyyy-MM-dd HH:mm:ss")),
                                  text);
    f.write(line.toUtf8());
}

QColor Cfg::parseColor(const QString &hex)
{
    QColor c(hex.trimmed());
    return c.isValid() ? c : QColor(220, 220, 220);
}

QString Cfg::toHex(const QColor &c)
{
    return QStringLiteral("#%1%2%3")
        .arg(c.red(), 2, 16, QLatin1Char('0'))
        .arg(c.green(), 2, 16, QLatin1Char('0'))
        .arg(c.blue(), 2, 16, QLatin1Char('0'));
}

QColor Cfg::color(const QString &key) const
{
    if (key == QLatin1String("accent"))
        return parseColor(accent);
    if (key == QLatin1String("warn"))
        return parseColor(warn);
    if (key == QLatin1String("bad"))
        return parseColor(bad);
    if (key == QLatin1String("bg"))
        return parseColor(bg);
    return parseColor(fg);
}

QString fmt(const QString &pattern, const QStringList &args)
{
    QString out = pattern;
    for (int i = 0; i < args.size(); ++i)
        out.replace(QStringLiteral("{%1}").arg(i), args.at(i));
    return out;
}