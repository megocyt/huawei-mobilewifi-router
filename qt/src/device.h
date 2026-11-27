#pragma once
#include <QByteArray>
#include <QMap>
#include <QMutex>
#include <QString>
#include <QStringList>
#include <QVariantMap>
#include <QVector>

/// 上一代终端（/api/system/HostInfo 的一条）
struct Host
{
    QString name, mac, ip, band, brand;
    bool active = false;
    QString ip6, ifType, addrSource, ssid, vendorId, lastAccess;
    bool guest = false, local = false;
    qint64 rxK = 0, txK = 0;   // 累计流量（KB）
    qint64 assocSec = 0;       // 本次在线时长（秒）
};

struct SmsItem
{
    QString index, phone, date, content;
    bool unread = false;
};

/// 一次采样的结果，界面只认这个结构。
struct Snap
{
    bool ok = false;
    QString err;
    bool loggedIn = false;
    qint64 ts = 0;

    QString net = QStringLiteral("-");
    QString conn;
    bool connected = false;
    QString carrier;
    int sigBars = 0, sigMax = 5;
    QString rsrp = QStringLiteral("-"), sinr = QStringLiteral("-"), rsrq = QStringLiteral("-");
    QString rsrpLevel = QStringLiteral("-");
    qint64 rateDown = 0, rateUp = 0;
    qint64 dayUsed = 0, monthDown = 0, monthUp = 0;
    qint64 planLimit = 0, planUsed = 0, planRemain = 0;
    double planPercent = -1, planDayPercent = -1;
    QString planLimitRaw = QStringLiteral("-"), planStartDay = QStringLiteral("1"),
            planThreshold = QStringLiteral("90");
    int planDaysLeft = -1;
    bool planOverThreshold = false, planOverLimit = false;
    int hostsActive = 0, hostsTotal = 0;
    int smsUnread = 0, smsInbox = 0;
    QString battery = QStringLiteral("-");
    bool charging = false;
    QString wanIp = QStringLiteral("-");
    QString wanIpv6 = QStringLiteral("-");
    QString msisdn = QStringLiteral("-");
    qint64 uptime = 0;
    QString model = QStringLiteral("-");
    QVector<Host> hosts;
};

/// 华为随身 WiFi WebUI 客户端：SCRAM-SHA-256 登录 + XML 请求体 + 一次性 CSRF token。
/// 同步实现（QTcpSocket 每请求新建），可在任意线程调用；外部用 mutex() 串行化。
class Device
{
public:
    explicit Device(const QString &baseUrl, const QString &user, const QString &password);

    QString baseUrl;
    QString user;
    QString password;

    QString errText;
    QString lastNetErr;

    bool login(bool force);
    void forgetSession();
    void logout();
    Snap sample();
    int waitSeconds() const;

    QVector<SmsItem> smsAll(QString *error);
    QString markRead(const QStringList &indexes);
    QString deleteSms(const QStringList &indexes);

    static QString describeCode(const QString &code);

    /// 串行化入口：短信窗等后台线程用 tryLock(20~30s)，失败即“设备忙”
    QMutex &mutex() { return mtx; }

private:
    struct Reply
    {
        int status = -1;
        QByteArray body;
    };

    Reply request(const QString &path, const QByteArray &xmlBody, bool bodyPresent, bool sendToken);
    QVariantMap get(const QString &path);
    QVariantMap post(const QString &path, const QVariantMap &body);
    QVariantMap postInner(const QString &path, const QVariantMap &body, bool retry);
    bool refreshToken();
    bool tryScramLogin(QString *code, QString *why);
    void block(const QString &why, const QString &code);
    void fillCore(Snap &s, const QVariantMap &status, const QVariantMap &traffic,
                  const QVariantMap &signal);
    void fillHosts(Snap &s, const QVariantMap &d);
    void fillPlan(Snap &s, const QVariantMap &limit, const QVariantMap &month);
    QString smsAction(const QString &path, const QStringList &indexes);

    QMap<QString, QString> cookies;
    QString token;
    bool loggedIn = false;
    qint64 blockedUntil = 0;
    int fails = 0;
    QMutex mtx;
};

/// XML / JSON 响应统一转成同构字典（QVariantMap / QVariantList）
QVariantMap parseAnyBody(const QByteArray &body);
/// 字典 → XML 片段（复刻 C# ToXml，顺序保持插入顺序）
QString mapToXml(const QVariantMap &d);