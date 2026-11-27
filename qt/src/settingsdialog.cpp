#include "settingsdialog.h"

#include "lang.h"
#include "paintutil.h"

#include <QApplication>
#include <QCheckBox>
#include <QColorDialog>
#include <QComboBox>
#include <QCoreApplication>
#include <QDoubleSpinBox>
#include <QFontComboBox>
#include <QFormLayout>
#include <QGridLayout>
#include <QGroupBox>
#include <QHBoxLayout>
#include <QLabel>
#include <QLineEdit>
#include <QMouseEvent>
#include <QPainter>
#include <QPushButton>
#include <QSettings>
#include <QSlider>
#include <QSpinBox>
#include <QTabWidget>
#include <QVBoxLayout>

#include <climits>

namespace {

constexpr int WinW = 520, WinH = 440, FooterH = 46;

const char *const kRowKeys[] = {"conn", "model", "signal", "rate", "plan", "traffic", "hosts",
                                "battery", "msisdn", "ip", "uptime", "sms", "clock"};
const char *const kColorKeys[] = {"fg", "bg", "accent", "warn", "bad"};
const char *const kColorLbl[] = {"color.fg", "color.bg", "color.accent", "color.warn", "color.bad"};

const QString kDarkStyle = QStringLiteral(
    "QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox, QFontComboBox { background:#ffffff;"
    " border:1px solid #A0A0A0; padding:3px 4px; min-height:18px; }"
    "QPushButton { background:#FDFDFD; border:1px solid #BBBBBB; padding:4px 14px; min-height:18px; }"
    "QPushButton:hover { background:#E5F1FB; }"
    "QPushButton:pressed { background:#CCE4F7; }"
    "QTabWidget::pane { border:1px solid #D6D6D6; background:#ffffff; top:-1px; }"
    "QTabBar::tab { background:#F0F0F0; color:#444444; padding:6px 16px; border:1px solid #D6D6D6;"
    " border-bottom:0; margin-right:2px; }"
    "QTabBar::tab:selected { background:#ffffff; color:#1b1b1b; }"
    "QGroupBox { border:1px solid #D6D6D6; margin-top:10px; padding-top:8px; }"
    "QGroupBox::title { subcontrol-origin: margin; left:10px; padding:0 4px; color:#444444; }"
    "QCheckBox { spacing:6px; }"
    "QLabel { color:#202020; }");

QString styleKeyName(int i) { return QString::fromLatin1(kColorKeys[i]); }

void writeAutoStart(bool on)
{
#ifdef Q_OS_WIN
    QSettings s(QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
                QSettings::NativeFormat);
    if (on) {
        QString path = QCoreApplication::applicationFilePath();
        path.replace(QLatin1Char('/'), QLatin1Char('\\'));
        s.setValue(QStringLiteral("HwWidget"), QStringLiteral("\"") + path + QStringLiteral("\""));
    } else {
        s.remove(QStringLiteral("HwWidget"));
    }
#else
    Q_UNUSED(on)
#endif
}

} // namespace

// ---------------------------------------------------------------- StyleCard

StyleCard::StyleCard(const QString &style, const Cfg &cfg, QWidget *parent)
    : QWidget(parent), style_(style), cfg_(cfg)
{
    setFixedSize(226, 212);
    setCursor(Qt::PointingHandCursor);
}

void StyleCard::setSelected(bool sel)
{
    selected_ = sel;
    update();
}

void StyleCard::setConfig(const Cfg &cfg)
{
    cfg_ = cfg;
    update();
}

void StyleCard::mousePressEvent(QMouseEvent *e)
{
    if (e->button() == Qt::LeftButton)
        emit picked(style_);
}

void StyleCard::enterEvent(QEnterEvent *e)
{
    Q_UNUSED(e)
    hover_ = true;
    update();
}

void StyleCard::leaveEvent(QEvent *e)
{
    Q_UNUSED(e)
    hover_ = false;
    update();
}

void StyleCard::paintEvent(QPaintEvent *)
{
    QPainter p(this);
    p.setRenderHint(QPainter::Antialiasing, true);
    const QRectF card(0.5, 0.5, width() - 1, height() - 1);
    p.setPen(QPen(selected_ ? QColor(0, 120, 212) : (hover_ ? QColor(150, 150, 150) : QColor(200, 200, 200)),
                  selected_ ? 2 : 1));
    p.setBrush(Qt::white);
    p.drawRoundedRect(card, 4, 4);

    const Pal pal = Pal::from(cfg_);
    if (style_ == QLatin1String("mini")) {
        const QRectF thumb(width() / 2.0 - 75, 46, 150, 24);
        QLinearGradient g(thumb.topLeft(), thumb.bottomLeft());
        g.setColorAt(0, pal.bgLo());
        g.setColorAt(1, pal.bg);
        p.setPen(Qt::NoPen);
        p.setBrush(g);
        p.drawRoundedRect(thumb, 5, 5);
        p.setBrush(pal.warn);
        p.drawRect(QRectF(thumb.left() + 10, thumb.top() + 10, 8, 3));
        p.setBrush(pal.accent);
        p.drawRect(QRectF(thumb.left() + 20, thumb.top() + 8, 8, 5));
        QFont f = font();
        f.setPointSizeF(7.5);
        p.setFont(f);
        p.setPen(pal.fg);
        p.drawText(QRectF(thumb.left() + 36, thumb.top(), 60, thumb.height()), Qt::AlignLeft | Qt::AlignVCenter,
                   QStringLiteral("1.2 MB/s"));
    } else {
        const QRectF thumb(width() / 2.0 - 79, 28, 158, 104);
        QLinearGradient g(thumb.topLeft(), thumb.bottomLeft());
        g.setColorAt(0, pal.bgLo());
        g.setColorAt(1, pal.bg);
        p.setPen(Qt::NoPen);
        p.setBrush(g);
        p.drawRoundedRect(thumb, 8, 8);
        // 标题行 + 状态徽章
        QFont f = font();
        f.setPointSizeF(7.0);
        p.setFont(f);
        p.setPen(pal.titleFg());
        p.drawText(QRectF(thumb.left() + 10, thumb.top() + 6, 90, 12), Qt::AlignLeft | Qt::AlignVCenter,
                   QStringLiteral("HUAWEI WiFi"));
        p.setBrush(pal.accent);
        p.drawEllipse(QPointF(thumb.right() - 16, thumb.top() + 12), 2.5, 2.5);
        // 数据行
        for (int i = 0; i < 6; ++i) {
            const double y = thumb.top() + 28 + i * 12;
            p.setBrush(pal.muted());
            p.drawRect(QRectF(thumb.left() + 10, y, 22, 4));
            p.setBrush(i == 0 ? pal.accent : pal.fg);
            p.drawRect(QRectF(thumb.right() - 20 - (30 + (i % 3) * 12), y, 30 + (i % 3) * 12, 4));
        }
    }

    // 标题 / 描述 / 对勾
    QFont tf = font();
    tf.setPointSizeF(9.5);
    tf.setBold(true);
    p.setFont(tf);
    p.setPen(QColor(32, 32, 32));
    const QString name = style_ == QLatin1String("mini") ? Lang::t(QStringLiteral("style.mini"))
                                                         : Lang::t(QStringLiteral("style.full"));
    p.drawText(QRectF(12, 140, width() - 24, 20), Qt::AlignLeft | Qt::AlignVCenter, name);
    QFont df = font();
    df.setPointSizeF(8.0);
    p.setFont(df);
    p.setPen(QColor(120, 120, 120));
    const QString desc = style_ == QLatin1String("mini") ? Lang::t(QStringLiteral("style.mini.desc"))
                                                         : Lang::t(QStringLiteral("style.full.desc"));
    p.drawText(QRectF(12, 162, width() - 24, 34), Qt::AlignLeft | Qt::AlignTop | Qt::TextWordWrap, desc);

    if (selected_) {
        const QRectF badge(width() - 34, 8, 22, 22);
        p.setPen(Qt::NoPen);
        p.setBrush(QColor(0, 120, 212));
        p.drawEllipse(badge);
        p.setPen(QPen(Qt::white, 2));
        p.drawLine(QPointF(badge.left() + 6, badge.center().y()),
                   QPointF(badge.center().x(), badge.bottom() - 6));
        p.drawLine(QPointF(badge.center().x(), badge.bottom() - 6),
                   QPointF(badge.right() - 6, badge.top() + 7));
    }
}

// ---------------------------------------------------------------- SettingsDialog

SettingsDialog::SettingsDialog(const Cfg &cfg, QWidget *parent) : LightWindow(parent), cur_(cfg)
{
    setWindowTitle(Lang::t(QStringLiteral("win.title")));
    resize(WinW, WinH);
    setFixedSize(WinW, WinH);
    setStyleSheet(kDarkStyle);
    body()->setGeometry(1, TitleH, WinW - 2, WinH - TitleH - 1);
    buildUi();
}

void SettingsDialog::updateSwatch(QPushButton *b, const QString &hex)
{
    if (!b)
        return;
    QColor c(hex);
    if (!c.isValid())
        c = QColor(200, 200, 200);
    b->setStyleSheet(QStringLiteral("QPushButton { background:%1; border:1px solid #888888; }").arg(hex));
    b->setText(QString());
}

void SettingsDialog::buildUi()
{
    const auto kids = body()->findChildren<QWidget *>(QString(), Qt::FindDirectChildrenOnly);
    for (QWidget *w : kids)
        delete w;

    tabs_ = new QTabWidget(body());
    // 左右边距 12，与页脚按钮、终端/短信窗的表格对齐
    tabs_->setGeometry(12, 8, body()->width() - 24, WinH - TitleH - FooterH - 16);
    tabs_->addTab(buildConnPage(), Lang::t(QStringLiteral("tab.conn")));
    tabs_->addTab(buildStylePage(), Lang::t(QStringLiteral("tab.style")));
    tabs_->addTab(buildLookPage(), Lang::t(QStringLiteral("tab.look")));
    tabs_->addTab(buildRowsPage(), Lang::t(QStringLiteral("tab.rows")));

    auto *reset = new QPushButton(Lang::t(QStringLiteral("btn.reset")), body());
    // 页脚几何统一按 body 尺寸算：三窗同一套 12px 边距 + 28px 按钮高，
    // 不再出现 WinW/WinH 混用导致按钮与标签页右边缘对不齐
    const int bw = body()->width();
    const int footerTop = body()->height() - FooterH + 8;
    reset->setGeometry(12, footerTop, 96, Light::BtnH);
    reset->setStyleSheet(Light::flatBtnQss());
    connect(reset, &QPushButton::clicked, this, [this] { resetPage(tabs_ ? tabs_->currentIndex() : 0); });

    auto *cancel = new QPushButton(Lang::t(QStringLiteral("btn.cancel")), body());
    cancel->setGeometry(bw - 12 - 84 - 8 - 84, footerTop, 84, Light::BtnH);
    cancel->setStyleSheet(Light::flatBtnQss());
    connect(cancel, &QPushButton::clicked, this, [this] {
        Lang::setLang(cur_.language);
        reject();
    });

    auto *save = new QPushButton(Lang::t(QStringLiteral("btn.save")), body());
    save->setGeometry(bw - 12 - 84, footerTop, 84, Light::BtnH);
    save->setStyleSheet(Light::primaryBtnQss());
    connect(save, &QPushButton::clicked, this, &SettingsDialog::onSave);

    auto *line = new QWidget(body());
    line->setGeometry(0, body()->height() - FooterH, bw, 1);
    line->setStyleSheet(QStringLiteral("background:#DADADA;"));
    line->lower();

    // 重建时父窗口已可见，新建的子控件默认是隐藏的（Qt 只在父 show() 时连带显示），
    // 这里逐个显式 show()，否则重建后界面一片空白。
    const auto fresh = body()->findChildren<QWidget *>(QString(), Qt::FindDirectChildrenOnly);
    for (QWidget *w : fresh)
        w->show();
    body()->update();
}

void SettingsDialog::rebuildUi()
{
    // buildUi() 会删除所有子控件，其中包括发信号的控件；必须等当前信号
    // 派发结束后再重建，否则会删除正在执行的对象。
    // update() 用来重画标题栏（标题文字随语言变化，子控件刷新盖不到它）。
    QMetaObject::invokeMethod(this, [this] { buildUi(); update(); }, Qt::QueuedConnection);
}

QWidget *SettingsDialog::buildConnPage()
{
    auto *page = new QWidget;
    auto *v = new QVBoxLayout(page);
    v->setContentsMargins(12, 12, 12, 12);
    v->setSpacing(10);

    auto *devGroup = new QGroupBox(Lang::t(QStringLiteral("grp.device")), page);
    auto *form = new QFormLayout(devGroup);
    form->setContentsMargins(12, 8, 12, 12);
    form->setSpacing(8);
    host_ = new QLineEdit(cur_.host, devGroup);
    user_ = new QLineEdit(cur_.user, devGroup);
    pwd_ = new QLineEdit(cur_.password, devGroup);
    pwd_->setEchoMode(QLineEdit::Password);
    interval_ = new QDoubleSpinBox(devGroup);
    interval_->setRange(1.0, 3600.0);
    interval_->setDecimals(1);
    interval_->setSingleStep(0.5);
    interval_->setValue(cur_.interval);
    form->addRow(Lang::t(QStringLiteral("lbl.host")), host_);
    form->addRow(Lang::t(QStringLiteral("lbl.user")), user_);
    form->addRow(Lang::t(QStringLiteral("lbl.pwd")), pwd_);
    form->addRow(Lang::t(QStringLiteral("lbl.interval")), interval_);
    v->addWidget(devGroup);

    auto *secGroup = new QGroupBox(Lang::t(QStringLiteral("grp.security")), page);
    auto *sv = new QVBoxLayout(secGroup);
    sv->setContentsMargins(12, 8, 12, 12);
    savePwd_ = new QCheckBox(Lang::t(QStringLiteral("chk.savepwd")), secGroup);
    savePwd_->setChecked(cur_.savePassword);
    sv->addWidget(savePwd_);
    v->addWidget(secGroup);

    auto *row = new QHBoxLayout;
    row->setSpacing(8);
    row->addWidget(new QLabel(Lang::t(QStringLiteral("lbl.language")), page));
    lang_ = new QComboBox(page);
    const auto opts = Lang::options();
    int idx = 0;
    for (int i = 0; i < opts.size(); ++i) {
        lang_->addItem(opts.at(i).first, opts.at(i).second);
        if (opts.at(i).second == cur_.language)
            idx = i;
    }
    lang_->setCurrentIndex(idx);
    connect(lang_, QOverload<int>::of(&QComboBox::currentIndexChanged), this, [this](int) {
        if (!lang_)
            return;
        // 与 cs 一致：先 SyncToCfg，再切语言并重建整个界面（所见即所得）
        syncToCfg();
        cur_.language = lang_->currentData().toString();
        Lang::setLang(cur_.language);
        setWindowTitle(Lang::t(QStringLiteral("win.title")));
        rebuildUi();
    });
    row->addWidget(lang_, 1);
    v->addLayout(row);

    autoStart_ = new QCheckBox(Lang::t(QStringLiteral("chk.autostart")), page);
    autoStart_->setChecked(cur_.autoStart);
    v->addWidget(autoStart_);
    v->addStretch(1);
    return page;
}

QWidget *SettingsDialog::buildStylePage()
{
    auto *page = new QWidget;
    auto *v = new QVBoxLayout(page);
    v->setContentsMargins(12, 12, 12, 12);
    v->setSpacing(8);

    auto *tip = new QLabel(Lang::t(QStringLiteral("tip.stylepick")), page);
    tip->setStyleSheet(QStringLiteral("color:#787878;"));
    v->addWidget(tip);

    auto *row = new QHBoxLayout;
    row->setSpacing(16);
    cardFull_ = new StyleCard(QStringLiteral("full"), cur_, page);
    cardMini_ = new StyleCard(QStringLiteral("mini"), cur_, page);
    cardFull_->setSelected(cur_.style != QLatin1String("mini"));
    cardMini_->setSelected(cur_.style == QLatin1String("mini"));
    row->addWidget(cardFull_);
    row->addWidget(cardMini_);
    v->addLayout(row);

    const auto pick = [this](const QString &style) {
        cur_.style = style;
        cardFull_->setSelected(style != QLatin1String("mini"));
        cardMini_->setSelected(style == QLatin1String("mini"));
    };
    connect(cardFull_, &StyleCard::picked, this, pick);
    connect(cardMini_, &StyleCard::picked, this, pick);
    v->addStretch(1);
    return page;
}

QWidget *SettingsDialog::buildGeneralSub()
{
    auto *page = new QWidget;
    auto *v = new QVBoxLayout(page);
    v->setContentsMargins(12, 12, 12, 12);
    v->setSpacing(8);

    auto *grp = new QGroupBox(Lang::t(QStringLiteral("grp.font")), page);
    auto *form = new QFormLayout(grp);
    form->setContentsMargins(12, 8, 12, 12);
    form->setSpacing(8);
    fontSize_ = new QSpinBox(grp);
    fontSize_->setRange(8, 24);
    fontSize_->setValue(cur_.fontSize);
    fontFam_ = new QFontComboBox(grp);
    fontFam_->setCurrentFont(QFont(cur_.fontFam));
    form->addRow(Lang::t(QStringLiteral("lbl.fontsize")), fontSize_);
    form->addRow(Lang::t(QStringLiteral("lbl.font")), fontFam_);
    v->addWidget(grp);

    auto *alphaRow = new QHBoxLayout;
    alphaRow->setSpacing(8);
    alphaRow->addWidget(new QLabel(Lang::t(QStringLiteral("lbl.opacity")), page));
    alpha_ = new QSlider(Qt::Horizontal, page);
    alpha_->setRange(25, 100);
    alpha_->setValue(int(cur_.alpha * 100));
    alpha_->setTickPosition(QSlider::TicksBelow);
    alpha_->setTickInterval(25);
    alphaRow->addWidget(alpha_, 1);
    auto *val = new QLabel(QString::number(int(cur_.alpha * 100)) + QStringLiteral("%"), page);
    val->setMinimumWidth(40);
    connect(alpha_, &QSlider::valueChanged, this, [this, val](int v) {
        val->setText(QString::number(v) + QStringLiteral("%"));
        cur_.alpha = v / 100.0;
    });
    alphaRow->addWidget(val);
    v->addLayout(alphaRow);

    auto *reset = new QPushButton(Lang::t(QStringLiteral("btn.reset")), page);
    reset->setStyleSheet(Light::flatBtnQss());
    connect(reset, &QPushButton::clicked, this, [this] {
        applyDefaults(cur_, QStringLiteral("general"));
        if (fontSize_)
            fontSize_->setValue(cur_.fontSize);
        if (fontFam_)
            fontFam_->setCurrentFont(QFont(cur_.fontFam));
        if (alpha_)
            alpha_->setValue(int(cur_.alpha * 100));
    });
    auto *btnRow = new QHBoxLayout;
    btnRow->addStretch(1);
    btnRow->addWidget(reset);
    v->addLayout(btnRow);
    v->addStretch(1);
    return page;
}

QWidget *SettingsDialog::buildColorSub()
{
    auto *page = new QWidget;
    auto *v = new QVBoxLayout(page);
    v->setContentsMargins(12, 12, 12, 12);
    v->setSpacing(8);

    auto *grp = new QGroupBox(Lang::t(QStringLiteral("grp.color")), page);
    auto *grid = new QGridLayout(grp);
    grid->setContentsMargins(12, 8, 12, 12);
    grid->setHorizontalSpacing(12);
    grid->setVerticalSpacing(8);
    for (int i = 0; i < 5; ++i) {
        auto *lbl = new QLabel(Lang::t(QLatin1String(kColorLbl[i])), grp);
        auto *btn = new QPushButton(grp);
        btn->setFixedSize(64, 24);
        btn->setCursor(Qt::PointingHandCursor);
        const QString key = styleKeyName(i);
        updateSwatch(btn, cur_.color(key).name());
        connect(btn, &QPushButton::clicked, this, [this, key, btn] { pickColor(key, btn); });
        swatches_[i] = btn;
        grid->addWidget(lbl, i, 0);
        grid->addWidget(btn, i, 1, Qt::AlignLeft);
    }
    v->addWidget(grp);

    auto *reset = new QPushButton(Lang::t(QStringLiteral("btn.reset")), page);
    reset->setStyleSheet(Light::flatBtnQss());
    connect(reset, &QPushButton::clicked, this, [this] {
        applyDefaults(cur_, QStringLiteral("color"));
        if (swatches_[0]) {
            for (int i = 0; i < 5; ++i)
                updateSwatch(swatches_[i], cur_.color(styleKeyName(i)).name());
        }
    });
    auto *btnRow = new QHBoxLayout;
    btnRow->addStretch(1);
    btnRow->addWidget(reset);
    v->addLayout(btnRow);
    v->addStretch(1);
    return page;
}

QWidget *SettingsDialog::buildBehaviorSub()
{
    auto *page = new QWidget;
    auto *v = new QVBoxLayout(page);
    v->setContentsMargins(12, 12, 12, 12);
    v->setSpacing(8);

    auto *grp = new QGroupBox(Lang::t(QStringLiteral("grp.behavior")), page);
    auto *gv = new QVBoxLayout(grp);
    gv->setContentsMargins(12, 8, 12, 12);
    top_ = new QCheckBox(Lang::t(QStringLiteral("chk.top")), grp);
    top_->setChecked(cur_.topmost);
    borderless_ = new QCheckBox(Lang::t(QStringLiteral("chk.border")), grp);
    borderless_->setChecked(cur_.borderless);
    locked_ = new QCheckBox(Lang::t(QStringLiteral("chk.lock")), grp);
    locked_->setChecked(cur_.locked);
    showTitle_ = new QCheckBox(Lang::t(QStringLiteral("chk.title")), grp);
    showTitle_->setChecked(cur_.showTitle);
    ctBorder_ = new QCheckBox(Lang::t(QStringLiteral("chk.ctborder")), grp);
    ctBorder_->setChecked(cur_.clickThroughBorder);
    gv->addWidget(top_);
    gv->addWidget(borderless_);
    gv->addWidget(locked_);
    gv->addWidget(showTitle_);
    gv->addWidget(ctBorder_);
    v->addWidget(grp);

    auto *reset = new QPushButton(Lang::t(QStringLiteral("btn.reset")), page);
    reset->setStyleSheet(Light::flatBtnQss());
    connect(reset, &QPushButton::clicked, this, [this] {
        applyDefaults(cur_, QStringLiteral("behavior"));
        if (top_)
            top_->setChecked(cur_.topmost);
        if (borderless_)
            borderless_->setChecked(cur_.borderless);
        if (locked_)
            locked_->setChecked(cur_.locked);
        if (showTitle_)
            showTitle_->setChecked(cur_.showTitle);
        if (ctBorder_)
            ctBorder_->setChecked(cur_.clickThroughBorder);
    });
    auto *btnRow = new QHBoxLayout;
    btnRow->addStretch(1);
    btnRow->addWidget(reset);
    v->addLayout(btnRow);
    v->addStretch(1);
    return page;
}

QWidget *SettingsDialog::buildLookPage()
{
    auto *page = new QWidget;
    auto *v = new QVBoxLayout(page);
    v->setContentsMargins(0, 0, 0, 0);

    auto *sub = new QTabWidget(page);
    sub->addTab(buildGeneralSub(), Lang::t(QStringLiteral("sub.general")));
    sub->addTab(buildColorSub(), Lang::t(QStringLiteral("grp.color")));
    sub->addTab(buildBehaviorSub(), Lang::t(QStringLiteral("grp.behavior")));
    v->addWidget(sub);
    return page;
}

QWidget *SettingsDialog::buildRowsPage()
{
    auto *page = new QWidget;
    auto *v = new QVBoxLayout(page);
    v->setContentsMargins(12, 12, 12, 12);

    auto *grid = new QGridLayout;
    grid->setHorizontalSpacing(18);
    grid->setVerticalSpacing(8);
    rowBoxes_.clear();
    int i = 0;
    for (const char *key : kRowKeys) {
        const QString k = QLatin1String(key);
        auto *cb = new QCheckBox(Lang::t(QStringLiteral("row.") + k), page);
        cb->setChecked(cur_.rowOn(k));
        rowBoxes_.insert(k, cb);
        grid->addWidget(cb, i % 7, i / 7);
        ++i;
    }
    v->addLayout(grid);
    v->addStretch(1);
    return page;
}

void SettingsDialog::pickColor(const QString &key, QPushButton *swatch)
{
    const QColor init(cur_.color(key));
    const QColor c = QColorDialog::getColor(init, this, Lang::t(QStringLiteral("grp.color")));
    if (!c.isValid())
        return;
    const QString hex = Cfg::toHex(c);
    if (key == QLatin1String("fg"))
        cur_.fg = hex;
    else if (key == QLatin1String("bg"))
        cur_.bg = hex;
    else if (key == QLatin1String("accent"))
        cur_.accent = hex;
    else if (key == QLatin1String("warn"))
        cur_.warn = hex;
    else
        cur_.bad = hex;
    updateSwatch(swatch, hex);
    if (cardFull_)
        cardFull_->setConfig(cur_);
    if (cardMini_)
        cardMini_->setConfig(cur_);
}

void SettingsDialog::syncToCfg()
{
    if (host_)
        cur_.host = host_->text().trimmed();
    if (user_)
        cur_.user = user_->text().trimmed();
    if (pwd_)
        cur_.password = pwd_->text();
    if (interval_)
        cur_.interval = interval_->value();
    if (savePwd_)
        cur_.savePassword = savePwd_->isChecked();
    if (autoStart_)
        cur_.autoStart = autoStart_->isChecked();
    if (fontSize_)
        cur_.fontSize = fontSize_->value();
    if (fontFam_)
        cur_.fontFam = fontFam_->currentFont().family();
    if (alpha_)
        cur_.alpha = alpha_->value() / 100.0;
    if (top_)
        cur_.topmost = top_->isChecked();
    if (borderless_)
        cur_.borderless = borderless_->isChecked();
    if (locked_)
        cur_.locked = locked_->isChecked();
    if (showTitle_)
        cur_.showTitle = showTitle_->isChecked();
    if (ctBorder_)
        cur_.clickThroughBorder = ctBorder_->isChecked();
    for (auto it = rowBoxes_.cbegin(); it != rowBoxes_.cend(); ++it)
        cur_.rows[it.key()] = it.value()->isChecked();
}

void SettingsDialog::applyDefaults(Cfg &c, const QString &scope)
{
    const Cfg d;
    if (scope == QLatin1String("general")) {
        c.fontSize = d.fontSize;
        c.fontFam = d.fontFam;
        c.alpha = d.alpha;
    } else if (scope == QLatin1String("color")) {
        c.fg = d.fg;
        c.bg = d.bg;
        c.accent = d.accent;
        c.warn = d.warn;
        c.bad = d.bad;
    } else if (scope == QLatin1String("behavior")) {
        c.topmost = d.topmost;
        c.borderless = d.borderless;
        c.locked = d.locked;
        c.showTitle = d.showTitle;
        c.clickThroughBorder = d.clickThroughBorder;
    }
}

void SettingsDialog::resetPage(int page)
{
    syncToCfg();
    // 页签顺序：0 连接 / 1 样式 / 2 外观 / 3 显示内容
    if (page == 0) {
        const Cfg d;
        cur_.host = d.host;
        cur_.user = d.user;
        cur_.password = QString();
        cur_.interval = d.interval;
        cur_.savePassword = d.savePassword;
        cur_.autoStart = d.autoStart;
        rebuildUi();
    } else if (page == 1) {
        cur_.style = Cfg().style;
        rebuildUi();
    } else if (page == 2) {
        applyDefaults(cur_, QStringLiteral("general"));
        applyDefaults(cur_, QStringLiteral("color"));
        applyDefaults(cur_, QStringLiteral("behavior"));
        rebuildUi();
    } else {
        cur_ = Cfg();
        rebuildUi();
    }
}

void SettingsDialog::onSave()
{
    syncToCfg();
    Lang::setLang(cur_.language);
    saved_ = true;
    writeAutoStart(cur_.autoStart);
    accept();
}