#include "lang.h"

#include <QFile>
#include <QHash>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>

namespace {

struct Table
{
    QStringList codes;
    QVector<QPair<QString, QString>> options;
    QHash<QString, QStringList> dict;
    bool loaded = false;
};

Table &table()
{
    static Table t;
    return t;
}

QString gLang = QStringLiteral("zh-CN");

} // namespace

void Lang::ensureLoaded()
{
    Table &t = table();
    if (t.loaded)
        return;
    t.loaded = true;
    QFile f(QStringLiteral(":/lang.json"));
    if (!f.open(QIODevice::ReadOnly))
        return;
    const QJsonDocument doc = QJsonDocument::fromJson(f.readAll());
    f.close();
    if (!doc.isObject())
        return;
    const QJsonObject root = doc.object();

    const QJsonArray opts = root.value(QStringLiteral("options")).toArray();
    for (const QJsonValue &v : opts) {
        const QJsonArray pair = v.toArray();
        if (pair.size() >= 2)
            t.options.append({pair.at(0).toString(), pair.at(1).toString()});
    }
    if (t.options.isEmpty()) {
        // 兜底，保证设置窗下拉不为空
        t.options = {{QStringLiteral("简体中文"), QStringLiteral("zh-CN")},
                     {QStringLiteral("English"), QStringLiteral("en")}};
    }
    for (const auto &o : t.options)
        t.codes.append(o.second);

    const QJsonObject dict = root.value(QStringLiteral("dict")).toObject();
    for (auto it = dict.begin(); it != dict.end(); ++it) {
        const QJsonArray arr = it.value().toArray();
        QStringList row;
        row.reserve(arr.size());
        for (const QJsonValue &v : arr)
            row.append(v.toString());
        t.dict.insert(it.key(), row);
    }
}

int Lang::indexOf(const QString &code)
{
    ensureLoaded();
    const int i = table().codes.indexOf(code);
    return i < 0 ? 0 : i;
}

bool Lang::isKnown(const QString &code)
{
    ensureLoaded();
    return table().codes.contains(code);
}

void Lang::setLang(const QString &code)
{
    ensureLoaded();
    gLang = isKnown(code) ? code : QStringLiteral("zh-CN");
}

QString Lang::lang() { return gLang; }

QString Lang::t(const QString &key)
{
    ensureLoaded();
    const auto it = table().dict.constFind(key);
    if (it == table().dict.constEnd())
        return key;
    const QStringList &row = it.value();
    const int i = indexOf(gLang);
    if (row.isEmpty())
        return key;
    return i < row.size() ? row.at(i) : row.at(0);
}

QVector<QPair<QString, QString>> Lang::options()
{
    ensureLoaded();
    return table().options;
}

QString Lang::carrier(const QString &name)
{
    if (name.isEmpty())
        return QString();
    QString s = name;
    s.remove(QStringLiteral("中国"));
    return s.trimmed();
}