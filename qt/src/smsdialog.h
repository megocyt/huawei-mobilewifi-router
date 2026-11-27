#pragma once
#include "device.h"
#include "lightwindow.h"

class QLabel;
class QPlainTextEdit;
class QPushButton;
class QTableWidget;
class WidgetWindow;

/// 短信管理窗：10 条/页、详情只读、"标记已读"/"删除" 在后台线程执行，
/// 抢不到设备锁时提示"设备忙"而不是卡住界面。
class SmsDialog : public LightWindow
{
    Q_OBJECT
public:
    explicit SmsDialog(WidgetWindow *owner, QWidget *parent = nullptr);

private:
    void loadAsync();
    void applyList();
    void fillDetail(int row);
    void actAsync(bool markRead, const QStringList &indexes, int count);
    void updatePager();
    void setBusy(bool busy);

    WidgetWindow *owner_;
    QTableWidget *table_ = nullptr;
    QPlainTextEdit *detail_ = nullptr;
    QLabel *pageLbl_ = nullptr;
    QLabel *count_ = nullptr;
    QLabel *status_ = nullptr;
    QPushButton *prev_ = nullptr;
    QPushButton *next_ = nullptr;
    QPushButton *markBtn_ = nullptr;
    QPushButton *delBtn_ = nullptr;
    QPushButton *closeBtn_ = nullptr;

    QVector<SmsItem> items_;
    int page_ = 1;
    int perPage_ = 10;
    int selectedRow_ = -1;
    int unread_ = 0, total_ = 0;
    bool busy_ = false;
};