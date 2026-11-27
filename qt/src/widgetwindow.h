#pragma once
#include "cfg.h"
#include "device.h"

#include <QFont>
#include <QMutex>
#include <QPoint>
#include <QRect>
#include <QVector>
#include <QWidget>

#include <atomic>
#include <thread>
class QMenu;
class QTimer;
class QWaitCondition;
class TrayIcon;
class HostsPopup;
class SettingsDialog;
class SmsDialog;
class HostsDialog;
struct Snap;

/// 悬浮窗主界面：无边框圆角卡片，全部 QPainter 自绘，
/// 支持拖动 / 滚轮调透明度 / 右键菜单 / 托盘，功能与 cs/src/WidgetForm.cs 对齐。
class WidgetWindow : public QWidget
{
    Q_OBJECT
public:
    enum Kind { Text, Signal, Plan, Battery };

    struct Row
    {
        QString key, label, value;
        QColor color;
        Kind kind = Text;
        double frac = 0;
        int bars = 0, barMax = 5;
        bool mono = false;
        QString tail;
        bool charging = false;
        bool eye = false;
    };

    explicit WidgetWindow(QWidget *parent = nullptr);
    ~WidgetWindow() override;

    // ---- 子窗使用的设备门面：均在后台线程调用，抢锁失败即返回“设备忙”
    QVector<SmsItem> smsQueryAll(QString *err);
    QString smsMarkRead(const QStringList &idx);
    QString smsDelete(const QStringList &idx);
    void smsCounts(int *unread, int *total);
    QVector<Host> hostsList(int *active, int *total);
    void notifyResample();
    /// 命令行入口（--panel settings|hosts|sms）：直接打开对应子窗，便于无交互验证
    void openPanel(const QString &name);

    const Cfg &cfg() const { return cfg_; }

protected:
    void paintEvent(QPaintEvent *e) override;
    void mousePressEvent(QMouseEvent *e) override;
    void mouseMoveEvent(QMouseEvent *e) override;
    void mouseReleaseEvent(QMouseEvent *e) override;
    void mouseDoubleClickEvent(QMouseEvent *e) override;
    void wheelEvent(QWheelEvent *e) override;
    void leaveEvent(QEvent *e) override;
    void contextMenuEvent(QContextMenuEvent *e) override;
    void closeEvent(QCloseEvent *e) override;
    void showEvent(QShowEvent *e) override;
    bool nativeEvent(const QByteArray &eventType, void *message, qintptr *result) override;

private:
    // ---- 构建与绘制
    void buildFonts();
    void computeLabelWidth();
    QVector<Row> buildRows() const;
    void relayout();
    void relayoutMini();
    void keepOnScreen();
    void paintFull(QPainter &p);
    void paintMini(QPainter &p);
    void drawRow(QPainter &p, const Row &r, int &y, const QRect &rc);
    QString rowKeyAt(int y) const;
    QString buildHostsTooltip() const;

    // ---- 窗口外观
    void applyChrome();
    void applyClickThrough();
    void applyDwmFrame();

    // ---- 交互与菜单
    void doCmd(const QString &cmd);
    void buildMenu();
    void rebuildMenu();
    QMenu *menu() const { return menu_; }
    void showSettings();
    void showSms();
    void showHosts();
    void onSettingsClosed(SettingsDialog *dlg);
    void applyAlphaStep(int delta);
    void savePos();
    void quit();
    void toggleVisible();
    void checkHostsHover(const QPoint &p);
    void hideHostsTip();

    // ---- 采集
    void pollLoop();
    void onSnap(const Snap &s, const QString &err);
    void updateTrayTip();
    Device *newDevice() const;

    Cfg cfg_;
    Device *dev_ = nullptr;
    QMutex devMutex_;

    Snap snap_;
    bool haveSnap_ = false;
    QString err_, lastErr_, hint_;
    QVector<Row> rows_;
    QVector<int> rowTops_;
    int hostsRowIdx_ = -1;
    int rowH_ = 18, labelW_ = 60;
    int miniColW_ = 78; // 迷你条速率列宽：按当前字号算一次，之后固定
    QString appliedLang_;

    // 卡片内所有文字共用一个字号（fValue_），不再为放得下而临时缩字
    QFont fLabel_, fValue_, fMono_;
    QPixmap logo_;

    QPoint dragOff_;
    QPoint pressPt_;
    QString pressRow_;
    bool dragging_ = false, moved_ = false;
    bool revealed_ = false;
    QRect eyeRect_;

    QTimer *clockTimer_ = nullptr;
    QTimer *hintTimer_ = nullptr;
    QTimer *saveTimer_ = nullptr;
    QTimer *ctTimer_ = nullptr;
    QMenu *menu_ = nullptr;
    TrayIcon *tray_ = nullptr;
    HostsPopup *hostsPopup_ = nullptr;
    SettingsDialog *settings_ = nullptr;
    SmsDialog *smsDlg_ = nullptr;
    HostsDialog *hostsDlg_ = nullptr;

    std::thread poll_;
    std::atomic<bool> stopPoll_{false};
    std::atomic<int> intervalMs_{3000};
    QMutex wakeMutex_;
    QWaitCondition *wake_ = nullptr;
    bool wakeFlag_ = false;
    bool quitting_ = false;
    bool dwmPending_ = false;
};