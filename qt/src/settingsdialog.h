#pragma once
#include "cfg.h"
#include "lightwindow.h"

#include <QFont>
#include <QMap>

class QCheckBox;
class QComboBox;
class QDoubleSpinBox;
class QFontComboBox;
class QLineEdit;
class QPushButton;
class QSlider;
class QSpinBox;
class QTabWidget;

/// 样式缩略图卡片：按当前配色真实预览 full / mini 两种形态
class StyleCard : public QWidget
{
    Q_OBJECT
public:
    StyleCard(const QString &style, const Cfg &cfg, QWidget *parent = nullptr);

    QString style() const { return style_; }
    void setSelected(bool sel);
    void setConfig(const Cfg &cfg);

signals:
    void picked(const QString &style);

protected:
    void paintEvent(QPaintEvent *e) override;
    void mousePressEvent(QMouseEvent *e) override;
    void enterEvent(QEnterEvent *e) override;
    void leaveEvent(QEvent *e) override;

private:
    QString style_;
    Cfg cfg_;
    bool selected_ = false;
    bool hover_ = false;
};

/// 设置窗口：四页签（连接 / 样式 / 外观（常规·颜色·行为子页）/ 显示内容），
/// 语言切换即时重建界面；保存后由悬浮窗统一落盘并重启设备会话。
class SettingsDialog : public LightWindow
{
    Q_OBJECT
public:
    explicit SettingsDialog(const Cfg &cfg, QWidget *parent = nullptr);

    Cfg result() const { return cur_; }
    bool saved() const { return saved_; }

private:
    void buildUi();
    /// 从控件自身的信号回调里重建界面会删掉调用者自身，统一延后到事件循环
    void rebuildUi();
    void syncToCfg();
    void resetPage(int page);
    void applyDefaults(Cfg &c, const QString &scope);

    QWidget *buildConnPage();
    QWidget *buildStylePage();
    QWidget *buildLookPage();
    QWidget *buildRowsPage();
    QWidget *buildGeneralSub();
    QWidget *buildColorSub();
    QWidget *buildBehaviorSub();

    void onSave();
    void pickColor(const QString &key, QPushButton *swatch);
    static void updateSwatch(QPushButton *b, const QString &hex);

    Cfg cur_;
    bool saved_ = false;
    QTabWidget *tabs_ = nullptr;

    QLineEdit *host_ = nullptr;
    QLineEdit *user_ = nullptr;
    QLineEdit *pwd_ = nullptr;
    QDoubleSpinBox *interval_ = nullptr;
    QCheckBox *savePwd_ = nullptr;
    QCheckBox *autoStart_ = nullptr;
    QComboBox *lang_ = nullptr;

    StyleCard *cardFull_ = nullptr;
    StyleCard *cardMini_ = nullptr;

    QSpinBox *fontSize_ = nullptr;
    QFontComboBox *fontFam_ = nullptr;
    QSlider *alpha_ = nullptr;
    QPushButton *swatches_[5] = {nullptr, nullptr, nullptr, nullptr, nullptr};
    QCheckBox *top_ = nullptr;
    QCheckBox *borderless_ = nullptr;
    QCheckBox *locked_ = nullptr;
    QCheckBox *showTitle_ = nullptr;
    QCheckBox *ctBorder_ = nullptr;

    QMap<QString, QCheckBox *> rowBoxes_;
};