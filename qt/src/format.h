#pragma once
#include <QString>

/// 字节数 → "1.2 MB"（i==0 时不带小数，规则与 cs/Device.cs HumanBytes 一致）
QString humanBytes(double bytes, int digits = 1);
/// "1.2 MB/s"
QString humanRate(double bps);
/// 秒 → "3天 4小时" / "12分 30秒"，单位词条走 Lang
QString humanDur(qint64 sec);
/// "10GB" / "1.5 gb" → 字节数，无法解析返回 0
qint64 parseSize(const QString &text);
/// 定点格式化并去掉末尾多余的 0（对应 C# 的 "0.####"）
QString trimNumber(double v, int digits);