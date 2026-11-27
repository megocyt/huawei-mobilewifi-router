#pragma once
#include <QIcon>
#include <QObject>

class QMenu;
class QSystemTrayIcon;
class QTimer;

/// 托盘/窗口图标（QPainter 绘制，与 cs 的 TrayIcon.Make 同风格）
QIcon makeAppIcon(int size);
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