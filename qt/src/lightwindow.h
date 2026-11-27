#pragma once
#include <QColor>
#include <QDialog>
#include <QPoint>
#include <QRect>
#include <QWidget>

/// Win32 经典浅色观感（取值与 cs/src/Win32Controls.cs 的 Pal 一致）
namespace Light
{
QColor accent();
QColor accentLight();
QColor titleBack();
QColor stripBack();
QColor windowBorder();
QColor btnFace();
QColor btnHover();
QColor btnPress();
QColor btnBorder();
QColor editBorder();
QColor groupBorder();
QColor text();
QColor gray();

/// 三窗（设置/终端/短信）共用的按钮外观。原生 Windows 样式带 5px 内边距，
/// 12pt 中文会被裁掉，所以统一用 QSS 接管；高度固定 BtnH，靠 padding 定内容。
QString flatBtnQss();
QString primaryBtnQss();
constexpr int BtnH = 28;
} // namespace Light

/// 无边框浅色窗基类：自绘奶油色标题栏（高 32）+ 关闭按钮 + 1px 边框，
/// 内容放在 body() 里（坐标原点即标题栏下方）。设置/终端/短信三窗共用。
class LightWindow : public QDialog
{
    Q_OBJECT
public:
    explicit LightWindow(QWidget *parent = nullptr);

    QWidget *body() const { return body_; }

    /// Win11 圆角 + 描边（设置/终端/短信窗用 ROUND(2) + #DADADA 描边）
    static void applyRoundFrame(QWidget *w);
    /// 干掉系统沿窗口边缘补画的 1px 亮边（三窗共用）
    static bool filterNative(const QByteArray &eventType, void *message, qintptr *result);

    /// 是/否确认框。Qt 自带 Yes/No 的文案来自 Qt 翻译文件，本项目没装 QTranslator，
    /// 直接 QMessageBox::question 会露出英文按钮，所以按钮文案一律走 Lang。
    static bool askYesNo(QWidget *parent, const QString &title, const QString &text);
    /// 单按钮提示框（同样走 Lang 的“确定”）
    static void tell(QWidget *parent, const QString &title, const QString &text);

protected:
    void paintEvent(QPaintEvent *e) override;
    void mousePressEvent(QMouseEvent *e) override;
    void mouseMoveEvent(QMouseEvent *e) override;
    void mouseReleaseEvent(QMouseEvent *e) override;
    void showEvent(QShowEvent *e) override;
    bool nativeEvent(const QByteArray &eventType, void *message, qintptr *result) override;

    static constexpr int TitleH = 32;

private:
    QWidget *body_ = nullptr;
    QRect closeRect_;
    bool dragging_ = false;
    bool closeHover_ = false;
    QPoint dragOff_;
};