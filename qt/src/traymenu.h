#pragma once
#include <QIcon>
#include <QObject>

class QMenu;
class QSystemTrayIcon;
class QTimer;

/// 应用图标：优先用 qrc 内嵌的多尺寸 HwWidget.ico（Fluent 风格透明底信号弧，
/// 与 cs 版 TrayIcon.MakeFor 同一份资源），资源缺失时回退到透明底三色弧。
/// 不再画深色背景方块 —— Win11 托盘/任务栏按透明底图标显示。
QIcon appIcon();
/// 暗色右键菜单观感（对应 cs 的 DarkMenuRenderer）
void styleDarkMenu(QMenu *menu);

/// 托盘图标：单击显隐、双击设置。NotifyIcon 式消歧——双击前会先到一次单击，
/// 因此单击延迟 mouseDoubleClickInterval()+50ms 才生效，期间收到双击就取消。
class TrayIcon : public QObject
{
    Q_OBJECT
public:
    explicit TrayIcon(QObject *parent = nullptr);
    ~TrayIcon() override;

    void setMenu(QMenu *menu);
    void setToolTip(const QString &tip);
    void showMessage(const QString &title, const QString &text);
    void show();
    void hide();

signals:
    void clicked();
    void doubleClicked();

private slots:
    void onActivated(int reason);

private:
    QSystemTrayIcon *tray_ = nullptr;
    QTimer *clickTimer_ = nullptr;
    QMenu *menu_ = nullptr;
    qint64 lastDoubleClick_ = 0;
};