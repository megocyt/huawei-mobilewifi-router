#pragma once
#include <QPair>
#include <QString>
#include <QStringList>
#include <QVector>

/// 多语言支持。词条从 :/lang.json 读取（由 cs/src/Lang.cs 的 170 条词条原样移植），
/// 列顺序固定 zh-CN, en, zh-TW, ja, ru, ko，默认简体中文。
class Lang
{
public:
    /// 语言代码 → 列索引（0..5），未匹配回退 0
    static int indexOf(const QString &code);
    static bool isKnown(const QString &code);

    static void setLang(const QString &code);
    static QString lang();

    /// 取词条；缺失时返回 key 本身，占位符用 fmt() 替换
    static QString t(const QString &key);

    /// { 显示名, 语言代码 } 列表，用于设置窗下拉
    static QVector<QPair<QString, QString>> options();

    /// 运营商名不翻译，仅去掉“中国”前缀
    static QString carrier(const QString &name);

private:
    static void ensureLoaded();
};