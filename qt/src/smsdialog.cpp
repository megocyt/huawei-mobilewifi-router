#include "smsdialog.h"

#include "lang.h"
#include "widgetwindow.h"

#include <QHeaderView>
#include <QLabel>
#include <QPlainTextEdit>
#include <QPushButton>
#include <QTableWidget>
#include <QThread>

#include <thread>

namespace {
constexpr int WinW = 660, WinH = 522, FooterH = 46;
// 内容区（body）高度：窗口高减去自绘标题栏与 1px 边框；页脚按钮坐标要按 body 算，
// 否则会掉到可视区外被裁掉
constexpr int BodyH = WinH - 32 - 1;
} // namespace

SmsDialog::SmsDialog(WidgetWindow *owner, QWidget *parent) : LightWindow(parent), owner_(owner)
{
    setWindowTitle(Lang::t(QStringLiteral("sms.title")));
    resize(WinW, WinH);
    setFixedSize(WinW, WinH);
    body()->setGeometry(1, TitleH, WinW - 2, WinH - TitleH - 1);

    pageLbl_ = new QLabel(body());
    pageLbl_->setGeometry(12, 10, 200, 22);
    pageLbl_->setStyleSheet(QStringLiteral("color:#202020;background:transparent;"));

    count_ = new QLabel(body());
    count_->setGeometry(210, 10, 220, 22);
    count_->setStyleSheet(QStringLiteral("color:#787878;background:transparent;"));

    prev_ = new QPushButton(QStringLiteral("◀"), body());
    prev_->setGeometry(436, 8, 32, 26);
    connect(prev_, &QPushButton::clicked, this, [this] {
        if (page_ <= 1)
            return;
        --page_;
        applyList();
    });
    next_ = new QPushButton(QStringLiteral("▶"), body());
    next_->setGeometry(472, 8, 32, 26);
    connect(next_, &QPushButton::clicked, this, [this] {
        const int pages = qMax(1, (items_.size() + perPage_ - 1) / perPage_);
        if (page_ >= pages)
            return;
        ++page_;
        applyList();
    });

    status_ = new QLabel(body());
    status_->setGeometry(510, 10, 140, 22);
    status_->setAlignment(Qt::AlignRight | Qt::AlignVCenter);
    status_->setStyleSheet(QStringLiteral("color:#B03030;background:transparent;"));

    table_ = new QTableWidget(0, 4, body());
    table_->setGeometry(12, 42, 372, WinH - 42 - FooterH - 12);
    table_->setEditTriggers(QAbstractItemView::NoEditTriggers);
    table_->setSelectionBehavior(QAbstractItemView::SelectRows);
    table_->setSelectionMode(QAbstractItemView::SingleSelection);
    table_->setShowGrid(true);
    table_->verticalHeader()->setVisible(false);
    table_->horizontalHeader()->setHighlightSections(false);
    table_->horizontalHeader()->setSectionsClickable(false);
    table_->setStyleSheet(QStringLiteral(
        "QTableWidget { background:#ffffff; border:1px solid #C8C8C8; gridline-color:#E4E4E4; }"
        "QHeaderView::section { background:#F0F0F0; color:#444444; border:0; border-right:1px solid #D8D8D8;"
        " border-bottom:1px solid #D8D8D8; padding:4px 6px; }"
        "QTableWidget::item:selected { background:#CCE8FF; color:#202020; }"));
    table_->setColumnCount(4);
    table_->setHorizontalHeaderLabels({Lang::t(QStringLiteral("sms.col.state")),
                                       Lang::t(QStringLiteral("sms.col.from")),
                                       Lang::t(QStringLiteral("sms.col.date")),
                                       Lang::t(QStringLiteral("sms.col.content"))});
    table_->setColumnWidth(0, 52);
    table_->setColumnWidth(1, 108);
    table_->setColumnWidth(2, 128);
    table_->setColumnWidth(3, 60);
    connect(table_, &QTableWidget::currentCellChanged, this,
            [this](int row, int, int, int) { fillDetail(row); });

    detail_ = new QPlainTextEdit(body());
    detail_->setGeometry(392, 42, WinW - 12 - 392, WinH - 42 - FooterH - 12);
    detail_->setReadOnly(true);
    detail_->setStyleSheet(QStringLiteral(
        "QPlainTextEdit { background:#FFFFFF; border:1px solid #C8C8C8; color:#202020; }"));

    markBtn_ = new QPushButton(Lang::t(QStringLiteral("sms.markread")), body());
    markBtn_->setGeometry(12, BodyH - FooterH + 10, 100, 26);
    connect(markBtn_, &QPushButton::clicked, this, [this] {
        const int row = table_->currentRow();
        if (row < 0)
            return;
        const int idx = (page_ - 1) * perPage_ + row;
        if (idx < 0 || idx >= items_.size())
            return;
        actAsync(true, {items_.at(idx).index}, 1);
    });

    delBtn_ = new QPushButton(Lang::t(QStringLiteral("sms.delete")), body());
    delBtn_->setGeometry(120, BodyH - FooterH + 10, 88, 26);
    connect(delBtn_, &QPushButton::clicked, this, [this] {
        const int row = table_->currentRow();
        if (row < 0)
            return;
        const int idx = (page_ - 1) * perPage_ + row;
        if (idx < 0 || idx >= items_.size())
            return;
        const QString text
            = fmt1(Lang::t(QStringLiteral("sms.confirm.del")), QString::number(1));
        if (!LightWindow::askYesNo(this, Lang::t(QStringLiteral("sms.delete")), text))
            return;
        actAsync(false, {items_.at(idx).index}, 1);
    });

    closeBtn_ = new QPushButton(Lang::t(QStringLiteral("sms.close")), body());
    closeBtn_->setGeometry(WinW - 12 - 84, BodyH - FooterH + 10, 84, 26);
    closeBtn_->setStyleSheet(QStringLiteral(
        "QPushButton { background:#0078D4; color:white; border:1px solid #0069BD; border-radius:3px; }"
        "QPushButton:hover { background:#1A86D9; }"
        "QPushButton:pressed { background:#0069BD; }"));
    connect(closeBtn_, &QPushButton::clicked, this, &SmsDialog::close);

    pageLbl_->setText(Lang::t(QStringLiteral("sms.loading")));
    loadAsync();
}

void SmsDialog::setBusy(bool busy)
{
    busy_ = busy;
    markBtn_->setEnabled(!busy);
    delBtn_->setEnabled(!busy);
    prev_->setEnabled(!busy);
    next_->setEnabled(!busy);
}

void SmsDialog::loadAsync()
{
    if (!owner_)
        return;
    setBusy(true);
    status_->setText(Lang::t(QStringLiteral("sms.loading")));
    std::thread([this] {
        QString err;
        const QVector<SmsItem> list = owner_->smsQueryAll(&err);
        QMetaObject::invokeMethod(
            this,
            [this, list, err] {
                items_ = list;
                if (!err.isEmpty())
                    status_->setText(err);
                else
                    status_->clear();
                page_ = 1;
                applyList();
                setBusy(false);
            },
            Qt::QueuedConnection);
    }).detach();
}

void SmsDialog::applyList()
{
    const int start = (page_ - 1) * perPage_;
    const int end = qMin(items_.size(), start + perPage_);
    table_->setRowCount(qMax(0, end - start));
    for (int i = start; i < end; ++i) {
        const SmsItem &m = items_.at(i);
        const int row = i - start;
        auto *st = new QTableWidgetItem(m.unread ? QStringLiteral("●") : QStringLiteral("○"));
        st->setForeground(m.unread ? QColor(0, 120, 212) : QColor(150, 155, 160));
        table_->setItem(row, 0, st);
        auto *from = new QTableWidgetItem(m.phone);
        auto *date = new QTableWidgetItem(m.date);
        auto *text = new QTableWidgetItem(m.content.simplified());
        if (m.unread) {
            for (QTableWidgetItem *it : {from, date, text}) {
                QFont f = it->font();
                f.setBold(true);
                it->setFont(f);
            }
        }
        table_->setItem(row, 1, from);
        table_->setItem(row, 2, date);
        table_->setItem(row, 3, text);
    }
    detail_->clear();
    if (!items_.isEmpty()) {
        table_->setCurrentCell(0, 0);
        fillDetail(0);
    }
    updatePager();
}

void SmsDialog::fillDetail(int row)
{
    const int idx = (page_ - 1) * perPage_ + row;
    if (row < 0 || idx < 0 || idx >= items_.size()) {
        detail_->clear();
        return;
    }
    const SmsItem &m = items_.at(idx);
    detail_->setPlainText(QStringLiteral("%1\n%2\n\n%3")
                              .arg(m.phone, m.date, m.content));
}

void SmsDialog::updatePager()
{
    const int pages = qMax(1, (items_.size() + perPage_ - 1) / perPage_);
    if (page_ > pages)
        page_ = pages;
    pageLbl_->setText(fmt(Lang::t(QStringLiteral("sms.page")),
                       {QString::number(page_), QString::number(pages)}));
    if (items_.isEmpty()) {
        count_->setText(Lang::t(QStringLiteral("sms.empty")));
    } else {
        int unread = 0;
        for (const SmsItem &m : items_)
            if (m.unread)
                ++unread;
        unread_ = unread;
        total_ = items_.size();
        count_->setText(fmt(Lang::t(QStringLiteral("sms.count")),
                            {QString::number(unread_), QString::number(total_)}));
    }
}

void SmsDialog::actAsync(bool markRead, const QStringList &indexes, int count)
{
    if (!owner_)
        return;
    setBusy(true);
    status_->setText(Lang::t(QStringLiteral("sms.loading")));
    const int cnt = count;
    std::thread([this, markRead, indexes, cnt] {
        const QString r = markRead ? owner_->smsMarkRead(indexes) : owner_->smsDelete(indexes);
        QMetaObject::invokeMethod(
            this,
            [this, r, cnt] {
                if (r == QLatin1String("ok")) {
                    status_->clear();
                    owner_->notifyResample();
                    loadAsync();
                } else {
                    status_->setText(r.isEmpty()
                                         ? fmt1(Lang::t(QStringLiteral("sms.opfail")),
                                                QString::number(cnt))
                                         : r);
                    setBusy(false);
                }
            },
            Qt::QueuedConnection);
    }).detach();
}