#pragma once
#include <QColor>
#include <QMap>
#include <QString>
#include <QStringList>

/// 悬浮窗配置。存储于 %APPDATA%\HWiFiWidget\config.json，键名与 cs/src/Config.cs
/// 完全一致，三个实现可互相接管同一份配置。
struct Cfg
{
    static QStringList rowKeys();
    static QString cfgDir();
    static QString cfgPath();
    static QString logPath();

    QString host = QStringLiteral("192.168.8.1");
    QString user = QStringLiteral("admin");
    QString password;
    QString language = QStringLiteral("zh-CN");      // zh-CN | en | zh-TW | ja | ru | ko
    QString style = QStringLiteral("full");          // full | mini
    double interval = 3.0;
    double alpha = 0.88;
    QString fontFam = QStringLiteral("Microsoft YaHei UI");
    int fontSize = 12;
    QString fg = QStringLiteral("#e6edf3");
    QString bg = QStringLiteral("#0d1117");
    QString accent = QStringLiteral("#3fb950");
    QString warn = QStringLiteral("#d29922");
    QString bad = QStringLiteral("#f85149");
    QMap<QString, bool> rows;
    bool topmost = true;
    bool borderless = true;
    bool locked = false;
    bool clickThrough = false;
    bool clickThroughBorder = true;
    bool autoStart = false;
    bool showTitle = true;
    bool savePassword = true;
    int posX = INT_MIN, posY = INT_MIN;

    Cfg();

    static Cfg load();
    void save() const;
    static void logError(const QString &text);

    /// 按 fg/bg/accent/warn/bad 取当前配色
    QColor color(const QString &key) const;
    static QColor parseColor(const QString &hex);
    static QString toHex(const QColor &c);

    bool rowOn(const QString &key) const { return rows.value(key, false); }
};

/// 把 C# 的 {0}/{1} 占位符替换为参数
QString fmt(const QString &pattern, const QStringList &args);
inline QString fmt1(const QString &pattern, const QString &a) { return fmt(pattern, {a}); }