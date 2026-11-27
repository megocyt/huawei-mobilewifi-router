#include "hostsdialog.h"

#include "format.h"
#include "lang.h"
#include "widgetwindow.h"

#include <QHeaderView>
#include <QLabel>
#include <QPushButton>
#include <QStyleFactory>
#include <QTableWidget>

#include <algorithm>

namespace {
constexpr int WinW = 760, WinH = 470, FooterH = 46;
// 内容区（body）高度：减去自绘标题栏与 1px 边框，页脚按钮与表格都按它定位
constexpr int BodyH = WinH - 32 - 1;
constexpr int BodyW = WinW - 2;
constexpr int FooterTop = BodyH - FooterH;    // 页脚上沿（表格不能越过这条线）
constexpr int TableTop = 42;                  // 工具行（刷新 + 计数）下方
constexpr int ContentH = FooterTop - TableTop - 8; // 表格底边停在页脚上方 8px
const QColor kOnline(37, 128, 66);
const QColor kOffline(150, 155, 160);

constexpr int kColCount = 16;
const int kColWidths[kColCount] = {64, 168, 124, 236, 156, 104, 78, 84, 92, 132, 110, 110, 110, 160, 60, 150};
const char *const kColKeys[kColCount] = {
    "hosts.col.status", "hosts.col.name",     "hosts.col.ip",       "hosts.col.ip6",
    "hosts.col.mac",    "hosts.col.brand",    "hosts.col.band",     "hosts.col.iftype",
    "hosts.col.addrsrc", "hosts.col.ssid",    "hosts.col.rx",       "hosts.col.tx",
    "hosts.col.onlinetime", "hosts.col.lastaccess", "hosts.col.guest", "hosts.col.clientid"};
} // namespace

HostsDialog::HostsDialog(WidgetWindow *owner, QWidget *parent) : LightWindow(parent), owner_(owner)
{
    setWindowTitle(Lang::t(QStringLiteral("hosts.title")));
    resize(WinW, WinH);
    setFixedSize(WinW, WinH);

    body()->setGeometry(1, TitleH, WinW - 2, WinH - TitleH - 1);

    refresh_ = new QPushButton(Lang::t(QStringLiteral("menu.refresh")), body());
    refresh_->setGeometry(12, 7, 88, Light::BtnH);
    refresh_->setStyleSheet(Light::flatBtnQss());
    connect(refresh_, &QPushButton::clicked, this, &HostsDialog::reload);

    count_ = new QLabel(body());
    // 右边缘与表格对齐（body 右边距 12），与刷新按钮同一行、同高居中
    count_->setGeometry(110, 7, BodyW - 12 - 110, Light::BtnH);
    count_->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
    count_->setStyleSheet(QStringLiteral("color:#787878;background:transparent;"));

    table_ = new QTableWidget(0, kColCount, body());
    table_->setGeometry(12, TableTop, BodyW - 24, ContentH);
    table_->setEditTriggers(QAbstractItemView::NoEditTriggers);
    table_->setSelectionBehavior(QAbstractItemView::SelectRows);
    table_->setSelectionMode(QAbstractItemView::SingleSelection);
    table_->setFocusPolicy(Qt::NoFocus); // 与短信窗一致：不要当前单元格的黑色焦点框
    table_->setShowGrid(true);
    table_->setWordWrap(false);
    table_->verticalHeader()->setVisible(false);
    table_->horizontalHeader()->setSectionsClickable(true);
    table_->horizontalHeader()->setHighlightSections(false);
    table_->setStyleSheet(QStringLiteral(
        "QTableWidget { background:#ffffff; border:1px solid #C8C8C8; gridline-color:#E4E4E4; }"
        "QHeaderView::section { background:#F0F0F0; color:#444444; border:0; border-right:1px solid #D8D8D8;"
        " border-bottom:1px solid #D8D8D8; padding:4px 6px; }"
        "QTableWidget::item:selected { background:#CCE8FF; color:#202020; }"));
    QStringList heads;
    for (int i = 0; i < kColCount; ++i) {
        heads << Lang::t(QLatin1String(kColKeys[i]));
        table_->setColumnWidth(i, kColWidths[i]);
    }
    table_->setHorizontalHeaderLabels(heads);
    connect(table_->horizontalHeader(), &QHeaderView::sectionClicked, this, &HostsDialog::onColumnClicked);

    closeBtn_ = new QPushButton(Lang::t(QStringLiteral("sms.close")), body());
    closeBtn_->setGeometry(BodyW - 12 - 84, FooterTop + 8, 84, Light::BtnH);
    closeBtn_->setStyleSheet(Light::primaryBtnQss());
    connect(closeBtn_, &QPushButton::clicked, this, &HostsDialog::close);

    reload();
}

QString HostsDialog::dash(const QString &s) { return s.isEmpty() ? QStringLiteral("-") : s; }

QString HostsDialog::ifType(const QString &t)
{
    if (t.isEmpty())
        return QStringLiteral("-");
    if (t.compare(QLatin1String("Wireless"), Qt::CaseInsensitive) == 0)
        return Lang::t(QStringLiteral("hosts.wireless"));
    if (t.compare(QLatin1String("Ethernet"), Qt::CaseInsensitive) == 0
        || t.compare(QLatin1String("Wired"), Qt::CaseInsensitive) == 0)
        return Lang::t(QStringLiteral("hosts.ethernet"));
    return t;
}

void HostsDialog::reload()
{
    list_ = owner_ ? owner_->hostsList(&active_, &total_) : QVector<Host>();
    render();
}

void HostsDialog::onColumnClicked(int col)
{
    if (col == sortCol_)
        sortAsc_ = !sortAsc_;
    else {
        sortCol_ = col;
        sortAsc_ = true;
    }
    render();
}

int HostsDialog::defaultCmp(const Host &a, const Host &b)
{
    if (a.active != b.active)
        return a.active ? -1 : 1;
    return QString::compare(a.name, b.name);
}

int HostsDialog::cmp(const Host &a, const Host &b, int c)
{
    switch (c) {
    case 0:
        return int(a.active) - int(b.active);
    case 1:
        return QString::compare(a.name, b.name);
    case 2:
        return QString::compare(a.ip, b.ip);
    case 3:
        return QString::compare(a.ip6, b.ip6);
    case 4:
        return QString::compare(a.mac, b.mac);
    case 5:
        return QString::compare(a.brand, b.brand);
    case 6:
        return QString::compare(a.band, b.band);
    case 7:
        return QString::compare(a.ifType, b.ifType);
    case 8:
        return QString::compare(a.addrSource, b.addrSource);
    case 9:
        return QString::compare(a.ssid, b.ssid);
    case 10:
        return a.rxK < b.rxK ? -1 : (a.rxK > b.rxK ? 1 : 0);
    case 11:
        return a.txK < b.txK ? -1 : (a.txK > b.txK ? 1 : 0);
    case 12:
        return a.assocSec < b.assocSec ? -1 : (a.assocSec > b.assocSec ? 1 : 0);
    case 13:
        return QString::compare(a.lastAccess, b.lastAccess);
    case 14:
        return int(a.guest) - int(b.guest);
    case 15:
        return QString::compare(a.vendorId, b.vendorId);
    default:
        return 0;
    }
}

void HostsDialog::render()
{
    QVector<Host> data = list_;
    std::stable_sort(data.begin(), data.end(), [this](const Host &a, const Host &b) {
        int r = sortCol_ < 0 ? defaultCmp(a, b) : cmp(a, b, sortCol_);
        if (sortCol_ >= 0 && !sortAsc_)
            r = -r;
        if (r == 0)
            r = defaultCmp(a, b);
        return r < 0;
    });

    table_->setRowCount(data.size());
    for (int row = 0; row < data.size(); ++row) {
        const Host &h = data.at(row);
        const QString cells[kColCount] = {
            h.active ? Lang::t(QStringLiteral("hosts.online")) : Lang::t(QStringLiteral("hosts.offline")),
            dash(h.name),
            dash(h.ip),
            dash(h.ip6),
            dash(h.mac),
            dash(h.brand),
            dash(h.band),
            ifType(h.ifType),
            dash(h.addrSource),
            dash(h.ssid),
            h.rxK > 0 ? humanBytes(double(h.rxK) * 1024.0, 1) : QStringLiteral("-"),
            h.txK > 0 ? humanBytes(double(h.txK) * 1024.0, 1) : QStringLiteral("-"),
            (h.active && h.assocSec > 0) ? humanDur(h.assocSec) : QStringLiteral("-"),
            dash(h.lastAccess),
            h.guest ? Lang::t(QStringLiteral("hosts.yes")) : Lang::t(QStringLiteral("hosts.no")),
            dash(h.vendorId)};
        for (int col = 0; col < kColCount; ++col) {
            auto *item = new QTableWidgetItem(cells[col]);
            item->setForeground(h.active ? kOnline : kOffline);
            if (h.active) {
                QFont f = item->font();
                f.setBold(true);
                item->setFont(f);
            }
            table_->setItem(row, col, item);
        }
    }
    count_->setText(data.isEmpty()
                        ? Lang::t(QStringLiteral("hosts.empty"))
                        : fmt(Lang::t(QStringLiteral("hosts.count")),
                              {QString::number(active_), QString::number(total_)}));

    // 表头排序箭头
    for (int i = 0; i < kColCount; ++i)
        table_->horizontalHeaderItem(i)->setText(
            QString::fromLatin1(kColKeys[i]).isEmpty() ? QString() : Lang::t(QLatin1String(kColKeys[i])));
    if (sortCol_ >= 0 && sortCol_ < kColCount) {
        const QString base = Lang::t(QLatin1String(kColKeys[sortCol_]));
        table_->horizontalHeaderItem(sortCol_)
            ->setText(base + (sortAsc_ ? QStringLiteral("  ▲") : QStringLiteral("  ▼")));
    }
}