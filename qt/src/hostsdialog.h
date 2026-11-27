#pragma once
#include "device.h"
#include "lightwindow.h"

class QLabel;
class QPushButton;
class QTableWidget;
class WidgetWindow;

/// 终端列表窗口：16 列只读表格，数据取自悬浮窗已轮询的快照（不重访设备，避免单会话顶号）。
/// 在线行加粗深绿、离线灰；在线优先稳定排序；表头点击可排序。
class HostsDialog : public LightWindow
{
    Q_OBJECT
public:
    explicit HostsDialog(WidgetWindow *owner, QWidget *parent = nullptr);

private:
    void reload();
    void render();
    void onColumnClicked(int col);
    static int defaultCmp(const Host &a, const Host &b);
    static int cmp(const Host &a, const Host &b, int col);
    static QString dash(const QString &s);
    static QString ifType(const QString &t);

    WidgetWindow *owner_;
    QTableWidget *table_ = nullptr;
    QLabel *count_ = nullptr;
    QPushButton *refresh_ = nullptr;
    QPushButton *closeBtn_ = nullptr;
    QVector<Host> list_;
    int active_ = 0, total_ = 0;
    int sortCol_ = -1;
    bool sortAsc_ = true;
};