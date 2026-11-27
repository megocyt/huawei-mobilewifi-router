#pragma once
#include <QFont>
#include <QString>
#include <QWidget>

/// 终端行悬停浮层（白底无边框小窗，对应 cs 的 HostsPopup）
class HostsPopup : public QWidget
{
public:
    explicit HostsPopup(QWidget *parent = nullptr);

    void showContent(const QString &text, const QPoint &screenPos, const QFont &font);
    void hidePopup();

protected:
    void paintEvent(QPaintEvent *e) override;
    bool event(QEvent *e) override;

private:
    QString content_;
    QFont font_;
};