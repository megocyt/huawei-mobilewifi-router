#include "device.h"

#include "cfg.h"
#include "format.h"
#include "lang.h"

#include <QCryptographicHash>
#include <QDate>
#include <QDateTime>
#include <QElapsedTimer>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QMessageAuthenticationCode>
#include <QNetworkProxy>
#include <QPasswordDigestor>
#include <QRandomGenerator>
#include <QSslSocket>
#include <QTcpSocket>
#include <QUrl>
#include <QXmlStreamReader>

#include <algorithm>

namespace {

const QSet<QString> &sessionDead()
{
    static const QSet<QString> s = {QStringLiteral("100003"), QStringLiteral("125002"),
                                    QStringLiteral("125003")};
    return s;
}

/// 只有设备侧限流/凭证类错误码才拉长退避
const QSet<QString> &rateLimitCodes()
{
    static const QSet<QString> s = {QStringLiteral("108003"), QStringLiteral("108005"),
                                    QStringLiteral("108006"), QStringLiteral("125001")};
    return s;
}

/// 会话位被后台/上次 logout 作废时回这两个码，需清 cookie 重开页面再试
const QSet<QString> &deadSessionCodes()
{
    static const QSet<QString> s = {QStringLiteral("125002"), QStringLiteral("125003")};
    return s;
}

QString strv(const QVariantMap &d, const QString &key)
{
    const auto it = d.constFind(key);
    if (it == d.constEnd())
        return QString();
    const QVariant &v = it.value();
    if (v.typeId() == QMetaType::Bool)
        return v.toBool() ? QStringLiteral("true") : QStringLiteral("false");
    if (v.typeId() == QMetaType::QVariantMap || v.typeId() == QMetaType::QVariantList)
        return QString();
    return v.toString();
}

qint64 lngv(const QVariantMap &d, const QString &key)
{
    bool ok = false;
    const qint64 v = strv(d, key).trimmed().toLongLong(&ok);
    return ok ? v : 0;
}

QVariantList listv(const QVariantMap &d, const QString &key)
{
    const auto it = d.constFind(key);
    if (it == d.constEnd())
        return QVariantList();
    const QVariant v = it.value();
    if (v.typeId() == QMetaType::QVariantList)
        return v.toList();
    if (!v.isValid() || v.isNull())
        return QVariantList();
    return QVariantList{v};
}

QVariantMap sub(const QVariantMap &d, const QString &key)
{
    const auto it = d.constFind(key);
    if (it == d.constEnd())
        return QVariantMap();
    return it.value().toMap();
}

QVariantMap errMap(const QString &msg)
{
    QVariantMap d;
    d.insert(QStringLiteral("__code"), QStringLiteral("local"));
    d.insert(QStringLiteral("__msg"), msg);
    return d;
}

/// 空字符串表示成功，否则是给界面看的失败文案
QString failText(const QVariantMap &d)
{
    const QString code = strv(d, QStringLiteral("__code"));
    if (code.isEmpty())
        return QString();
    QString msg = strv(d, QStringLiteral("__msg"));
    if (msg.isEmpty())
        msg = Device::describeCode(code);
    return msg + QStringLiteral("（") + code + QStringLiteral("）");
}

void insertMerged(QVariantMap &m, const QString &k, const QVariant &v)
{
    const auto it = m.find(k);
    if (it == m.end()) {
        m.insert(k, v);
        return;
    }
    QVariantList list;
    if (it.value().typeId() == QMetaType::QVariantList)
        list = it.value().toList();
    else
        list.append(it.value());
    list.append(v);
    it.value() = list;
}

QVariant parseXmlElement(QXmlStreamReader &r);

/// 复刻 cs 的 NodeToDict：子元素重复出现时合并成列表
QVariantMap nodeToMap(QXmlStreamReader &r)
{
    QVariantMap m;
    while (!r.atEnd()) {
        const QXmlStreamReader::TokenType tok = r.readNext();
        if (tok == QXmlStreamReader::StartElement)
            insertMerged(m, r.name().toString(), parseXmlElement(r));
        else if (tok == QXmlStreamReader::EndElement)
            break;
    }
    return m;
}

QVariant parseXmlElement(QXmlStreamReader &r)
{
    QVariantMap nested;
    QString text;
    bool hasNested = false;
    while (!r.atEnd()) {
        const QXmlStreamReader::TokenType tok = r.readNext();
        if (tok == QXmlStreamReader::StartElement) {
            hasNested = true;
            insertMerged(nested, r.name().toString(), parseXmlElement(r));
        } else if (tok == QXmlStreamReader::Characters || tok == QXmlStreamReader::EntityReference) {
            text += r.text().toString();
        } else if (tok == QXmlStreamReader::EndElement) {
            break;
        }
    }
    return hasNested ? QVariant(nested) : QVariant(text);
}

QVariantMap parseXml(const QString &text)
{
    QXmlStreamReader r(text);
    while (!r.atEnd()) {
        if (r.readNext() == QXmlStreamReader::StartElement)
            return nodeToMap(r);
    }
    return QVariantMap();
}

QString valueToXml(const QVariant &v)
{
    switch (v.typeId()) {
    case QMetaType::Bool:
        return v.toBool() ? QStringLiteral("true") : QStringLiteral("false");
    case QMetaType::Int:
    case QMetaType::LongLong:
    case QMetaType::UInt:
    case QMetaType::ULongLong:
        return v.toString();
    case QMetaType::Double: {
        const double d = v.toDouble();
        if (d == double(qint64(d)))
            return QString::number(qint64(d));
        return trimNumber(d, 4);
    }
    default:
        return v.toString();
    }
}

QByteArray httpDechunk(const QByteArray &in)
{
    QByteArray out;
    int i = 0;
    while (i < in.size()) {
        const int eol = in.indexOf("\r\n", i);
        if (eol < 0)
            break;
        QByteArray sz = in.mid(i, eol - i).trimmed();
        const int semi = sz.indexOf(';');
        if (semi >= 0)
            sz = sz.left(semi);
        bool ok = false;
        const int n = sz.toInt(&ok, 16);
        if (!ok || n <= 0)
            break;
        i = eol + 2;
        if (i + n > in.size()) {
            out += in.mid(i);
            break;
        }
        out += in.mid(i, n);
        i += n + 2;
    }
    return out;
}

QString hexOf(const QByteArray &b) { return QString::fromLatin1(b.toHex()); }

QByteArray hmacSha256(const QByteArray &key, const QByteArray &msg)
{
    return QMessageAuthenticationCode::hash(msg, key, QCryptographicHash::Sha256);
}

/// 与设备自带 emui-crypto.js 等价：CryptoJS.HmacSHA256(message, key) 的参数顺序与
/// RFC 5802 相反，因此 HMAC 的 key/msg 都按“反”的传。
void scram(const QString &pwd, const QString &saltHex, int iterations, const QString &first,
           const QString &server, QString *proof, QString *signature)
{
    const QByteArray salt = QByteArray::fromHex(saltHex.toLatin1());
    const QByteArray sp = QPasswordDigestor::deriveKeyPbkdf2(QCryptographicHash::Sha256,
                                                             pwd.toUtf8(), salt, iterations, 32);
    const QByteArray auth = (first + QLatin1Char(',') + server + QLatin1Char(',') + server).toUtf8();
    const QByteArray clientKey = hmacSha256("Client Key", sp);
    const QByteArray storedKey = QCryptographicHash::hash(clientKey, QCryptographicHash::Sha256);
    const QByteArray clientSig = hmacSha256(auth, storedKey);
    QByteArray p(clientKey.size(), 0);
    for (int i = 0; i < p.size(); ++i)
        p[i] = char(clientKey.at(i) ^ clientSig.at(i));
    const QByteArray serverKey = hmacSha256("Server Key", sp);
    const QByteArray s = hmacSha256(auth, serverKey);
    *proof = hexOf(p);
    *signature = hexOf(s);
}

QString newNonce()
{
    QByteArray b(32, 0);
    for (int i = 0; i < b.size(); ++i)
        b[i] = char(QRandomGenerator::system()->bounded(256));
    return hexOf(b);
}

/// 只保留数字、负号与小数点（复刻 Device.Num）
QString numericOnly(const QString &raw)
{
    QString out;
    for (const QChar ch : raw)
        if (ch.isDigit() || ch == QLatin1Char('-') || ch == QLatin1Char('.'))
            out.append(ch);
    return out.isEmpty() ? QStringLiteral("-") : out;
}

/// RSRP 质量等级（设备侧文案，与 cs 一致不翻译）
QString levelOf(const QString &rsrpText)
{
    bool ok = false;
    const double v = rsrpText.toDouble(&ok);
    if (!ok)
        return QStringLiteral("-");
    if (v >= -80)
        return QStringLiteral("优");
    if (v >= -90)
        return QStringLiteral("良");
    if (v >= -100)
        return QStringLiteral("中");
    if (v >= -110)
        return QStringLiteral("差");
    return QStringLiteral("极差");
}

QString mapNet(const QVector<QPair<QString, QString>> &tbl, const QString &key)
{
    for (const auto &row : tbl)
        if (row.first == key)
            return row.second;
    return key;
}

QString decodeSms(const QString &s)
{
    if (s.isEmpty())
        return s;
    QString out = s;
    out.remove(QStringLiteral("&#x0D;"));
    out.remove(QLatin1Char('\r'));
    out.replace(QStringLiteral("&#x2F;"), QStringLiteral("/"));
    out.replace(QStringLiteral("&quot;"), QStringLiteral("\""));
    out.replace(QStringLiteral("&apos;"), QStringLiteral("'"));
    out.replace(QStringLiteral("&lt;"), QStringLiteral("<"));
    out.replace(QStringLiteral("&gt;"), QStringLiteral(">"));
    out.replace(QStringLiteral("&amp;"), QStringLiteral("&"));
    return out;
}

bool isActiveField(const QVariantMap &h, const QString &key)
{
    const auto it = h.constFind(key);
    if (it == h.constEnd() || !it.value().isValid())
        return false;
    if (it.value().typeId() == QMetaType::Bool)
        return it.value().toBool();
    const QString s = it.value().toString().toLower();
    return s == QLatin1String("true") || s == QLatin1String("1");
}

} // namespace

QVariantMap parseAnyBody(const QByteArray &body)
{
    if (body.isEmpty())
        return QVariantMap();
    const QByteArray t = body.trimmed();
    if (t.startsWith('[') || t.startsWith('{')) {
        QJsonParseError perr{};
        const QJsonDocument doc = QJsonDocument::fromJson(t, &perr);
        if (perr.error == QJsonParseError::NoError) {
            if (doc.isArray()) {
                QVariantMap wrap;
                wrap.insert(QStringLiteral("__list"), doc.array().toVariantList());
                return wrap;
            }
            if (doc.isObject())
                return doc.object().toVariantMap();
        }
        return QVariantMap();
    }
    return parseXml(QString::fromUtf8(body));
}

QString mapToXml(const QVariantMap &d)
{
    QString out;
    for (auto it = d.cbegin(); it != d.cend(); ++it) {
        const QString &k = it.key();
        const QVariant &v = it.value();
        if (v.typeId() == QMetaType::QVariantMap) {
            out += QLatin1Char('<') + k + QLatin1Char('>') + mapToXml(v.toMap()) + QLatin1String("</")
                   + k + QLatin1Char('>');
        } else if (v.typeId() == QMetaType::QVariantList) {
            const QVariantList list = v.toList();
            for (const QVariant &o : list)
                out += QLatin1Char('<') + k + QLatin1Char('>') + valueToXml(o) + QLatin1String("</") + k
                       + QLatin1Char('>');
        } else {
            out += QLatin1Char('<') + k + QLatin1Char('>') + valueToXml(v) + QLatin1String("</") + k
                   + QLatin1Char('>');
        }
    }
    return out;
}

Device::Device(const QString &baseUrl, const QString &user, const QString &password)
    : baseUrl(baseUrl), user(user), password(password)
{
}

// ---------------------------------------------------------------- 低层 HTTP

Device::Reply Device::request(const QString &path, const QByteArray &xmlBody, bool bodyPresent,
                              bool sendToken)
{
    Reply rep;
    QString full = baseUrl;
    while (full.endsWith(QLatin1Char('/')))
        full.chop(1);
    full += path;
    const QUrl u(full);
    const bool tls = u.scheme().compare(QLatin1String("https"), Qt::CaseInsensitive) == 0;
    const QString host = u.host();
    const int port = u.port(tls ? 443 : 80);
    if (host.isEmpty()) {
        rep.status = -2;
        rep.body = QByteArrayLiteral("invalid host");
        return rep;
    }
    QByteArray target = u.path(QUrl::FullyEncoded).toUtf8();
    if (target.isEmpty())
        target = "/";
    if (u.hasQuery())
        target += '?' + u.query(QUrl::FullyEncoded).toUtf8();

    QString hostHeader = host;
    if (port != (tls ? 443 : 80))
        hostHeader += QLatin1Char(':') + QString::number(port);

    QByteArray req;
    req += (bodyPresent ? "POST " : "GET ") + target + " HTTP/1.1\r\n";
    req += "Host: " + hostHeader.toUtf8() + "\r\n";
    req += "User-Agent: Mozilla/5.0 (hw-widget)\r\n";
    req += "Accept: application/json, text/javascript, */*; q=0.01\r\n";
    req += "X-Requested-With: XMLHttpRequest\r\n";
    req += "_ResponseSource: Broswer\r\n";
    if (path.contains(QLatin1String("monitoring/status")))
        req += "Update-Cookie: UpdateCookie\r\n";
    if (!cookies.isEmpty()) {
        QStringList parts;
        for (auto it = cookies.cbegin(); it != cookies.cend(); ++it)
            parts.append(it.key() + QLatin1Char('=') + it.value());
        req += "Cookie: " + parts.join(QStringLiteral("; ")).toUtf8() + "\r\n";
    }
    if (sendToken && !token.isEmpty())
        req += "__RequestVerificationToken: " + token.toUtf8() + "\r\n";
    if (bodyPresent) {
        req += "Content-Type: application/x-www-form-urlencoded; charset=UTF-8\r\n";
        req += "Content-Length: " + QByteArray::number(xmlBody.size()) + "\r\n";
    }
    req += "Connection: close\r\n\r\n";
    if (bodyPresent)
        req += xmlBody;

    QTcpSocket tcp;
    QSslSocket ssl;
    QAbstractSocket *sock = &tcp;
    if (tls) {
        ssl.setPeerVerifyMode(QSslSocket::VerifyNone);
        sock = &ssl;
    }
    sock->setProxy(QNetworkProxy::NoProxy);
    sock->connectToHost(host, quint16(port));
    if (!sock->waitForConnected(8000)) {
        rep.status = -1;
        rep.body = sock->errorString().toUtf8();
        sock->abort();
        return rep;
    }
    if (tls) {
        QSslSocket *s = static_cast<QSslSocket *>(sock);
        if (!s->waitForEncrypted(8000)) {
            rep.status = -1;
            rep.body = s->errorString().toUtf8();
            sock->abort();
            return rep;
        }
    }
    sock->write(req);
    sock->waitForBytesWritten(4000);

    QByteArray resp;
    QElapsedTimer timer;
    timer.start();
    while (timer.elapsed() < 15000) {
        if (sock->bytesAvailable() == 0) {
            if (sock->state() == QAbstractSocket::UnconnectedState)
                break;
            sock->waitForReadyRead(300);
        }
        const QByteArray chunk = sock->readAll();
        if (!chunk.isEmpty())
            resp += chunk;
        else if (sock->state() == QAbstractSocket::UnconnectedState)
            break;
    }
    sock->abort();

    const int hdrEnd = resp.indexOf("\r\n\r\n");
    if (hdrEnd < 0) {
        rep.status = -2;
        rep.body = resp;
        return rep;
    }
    const QByteArray head = resp.left(hdrEnd);
    QByteArray body = resp.mid(hdrEnd + 4);
    const QList<QByteArray> lines = head.split('\n');
    if (lines.isEmpty()) {
        rep.status = -2;
        return rep;
    }
    const QList<QByteArray> statusParts = lines.at(0).trimmed().split(' ');
    rep.status = statusParts.size() >= 2 ? statusParts.at(1).toInt() : -2;

    bool chunked = false;
    for (int i = 1; i < lines.size(); ++i) {
        const QByteArray line = lines.at(i).trimmed();
        const QByteArray lower = line.toLower();
        if (lower.startsWith("transfer-encoding:") && lower.contains("chunked"))
            chunked = true;
        else if (lower.startsWith("set-cookie:")) {
            const QString sc = QString::fromUtf8(line.mid(11).trimmed());
            const QString pair = sc.split(QLatin1Char(';')).value(0);
            const int eq = pair.indexOf(QLatin1Char('='));
            if (eq > 0)
                cookies.insert(pair.left(eq).trimmed(), pair.mid(eq + 1).trimmed());
        }
    }
    if (chunked)
        body = httpDechunk(body);
    rep.body = body;
    return rep;
}

bool Device::refreshToken()
{
    const Reply r = request(QStringLiteral("/api/webserver/token?_ResponseSource=Broswer"),
                            QByteArray(), false, false);
    const QVariantMap d = parseAnyBody(r.body);
    const QString tok = strv(d, QStringLiteral("token"));
    if (tok.size() < 33)
        return false;
    token = tok.mid(32);
    return true;
}

QVariantMap Device::get(const QString &path)
{
    const QString p = path + (path.contains(QLatin1Char('?')) ? QStringLiteral("&")
                                                             : QStringLiteral("?"))
                      + QStringLiteral("_ResponseSource=Broswer");
    const Reply r = request(p, QByteArray(), false, false);
    if (r.status < 0)
        lastNetErr = QString::fromUtf8(r.body);
    QVariantMap d = parseAnyBody(r.body);
    const QString code = strv(d, QStringLiteral("code"));
    if (!code.isEmpty()) {
        // 只有会话类错误才重登；100002（接口不支持）之类不能触发重登，
        // 否则会反复登录踩到 108005 限流
        if (sessionDead().contains(code))
            loggedIn = false;
        d.insert(QStringLiteral("__code"), code);
    }
    return d;
}

QVariantMap Device::post(const QString &path, const QVariantMap &body)
{
    return postInner(path, body, true);
}

QVariantMap Device::postInner(const QString &path, const QVariantMap &body, bool retry)
{
    if (token.isEmpty() && !refreshToken())
        return errMap(Lang::t(QStringLiteral("err.csrf")));
    const QString xml = QStringLiteral("<?xml version=\"1.0\" encoding=\"UTF-8\"?><request>")
                        + mapToXml(body) + QStringLiteral("</request>");
    const Reply r = request(path, xml.toUtf8(), true, true);
    token.clear();   // 一次性，用完即废
    QVariantMap d = parseAnyBody(r.body);
    const QString code = strv(d, QStringLiteral("code"));
    if (code.isEmpty())
        return d;
    if (retry && (code == QLatin1String("100006") || code == QLatin1String("100003"))) {
        refreshToken();
        return postInner(path, body, false);
    }
    if (sessionDead().contains(code))
        loggedIn = false;
    d.insert(QStringLiteral("__code"), code);
    return d;
}

QString Device::describeCode(const QString &code)
{
    static const QVector<QPair<QString, const char *>> tbl = {
        {QStringLiteral("100002"), "e.100002"}, {QStringLiteral("100003"), "e.100003"},
        {QStringLiteral("100005"), "e.100005"}, {QStringLiteral("100006"), "e.100006"},
        {QStringLiteral("108005"), "e.108005"}, {QStringLiteral("108006"), "e.108006"},
        {QStringLiteral("113055"), "e.113055"}, {QStringLiteral("125001"), "e.125001"},
        {QStringLiteral("125002"), "e.125002"}, {QStringLiteral("125003"), "e.125003"},
    };
    for (const auto &row : tbl)
        if (row.first == code)
            return Lang::t(QLatin1String(row.second));
    return Lang::t(QStringLiteral("e.other"));
}

// ---------------------------------------------------------------- 登录

bool Device::login(bool force)
{
    if (loggedIn && !force)
        return true;
    if (QDateTime::currentMSecsSinceEpoch() < blockedUntil)
        return false;
    if (cookies.isEmpty())
        request(QStringLiteral("/"), QByteArray(), false, false);   // 拿 SessionID cookie

    const QVariantMap st = get(QStringLiteral("/api/user/state-login"));
    if (strv(st, QStringLiteral("State")) == QLatin1String("0")
        && !strv(st, QStringLiteral("userlevel")).isEmpty() && !force) {
        loggedIn = true;
        return true;
    }
    for (int attempt = 1; attempt <= 2; ++attempt) {
        QString code, why;
        if (tryScramLogin(&code, &why)) {
            loggedIn = true;
            fails = 0;
            errText.clear();
            return true;
        }
        // 会话死：清 cookie + 重新拿 SessionID，再走一次 SCRAM
        if (attempt == 1 && deadSessionCodes().contains(code)) {
            forgetSession();
            request(QStringLiteral("/"), QByteArray(), false, false);
            continue;
        }
        block(why, code);
        return false;
    }
    return false;
}

bool Device::tryScramLogin(QString *code, QString *why)
{
    code->clear();
    why->clear();
    if (!refreshToken()) {
        *code = QStringLiteral("local");
        *why = Lang::t(QStringLiteral("err.token"));
        return false;
    }
    const QString first = newNonce();
    QVariantMap chBody;
    chBody.insert(QStringLiteral("username"), user);
    chBody.insert(QStringLiteral("firstnonce"), first);
    chBody.insert(QStringLiteral("mode"), 1);
    const QVariantMap ch = post(QStringLiteral("/api/user/challenge_login"), chBody);
    *code = strv(ch, QStringLiteral("__code"));
    if (!code->isEmpty()) {
        *why = failText(ch);
        return false;
    }
    const QString salt = strv(ch, QStringLiteral("salt"));
    const QString serverNonce = strv(ch, QStringLiteral("servernonce"));
    bool iterOk = false;
    const int iter = strv(ch, QStringLiteral("iterations")).toInt(&iterOk);
    if (!iterOk || salt.size() < 8 || serverNonce.size() < 8) {
        *code = QStringLiteral("local");
        *why = Lang::t(QStringLiteral("err.challenge"));
        return false;
    }
    QString proof, expect;
    scram(password, salt, iter, first, serverNonce, &proof, &expect);
    if (!refreshToken()) {
        *code = QStringLiteral("local");
        *why = Lang::t(QStringLiteral("err.token"));
        return false;
    }
    QVariantMap auBody;
    auBody.insert(QStringLiteral("clientproof"), proof);
    auBody.insert(QStringLiteral("finalnonce"), serverNonce);
    const QVariantMap au = post(QStringLiteral("/api/user/authentication_login"), auBody);
    *code = strv(au, QStringLiteral("__code"));
    if (!code->isEmpty()) {
        *why = failText(au);
        return false;
    }
    const QString gotSig = strv(au, QStringLiteral("serversignature"));
    if (!gotSig.isEmpty() && gotSig.compare(expect, Qt::CaseInsensitive) != 0) {
        *code = QStringLiteral("local");
        *why = Lang::t(QStringLiteral("err.signature"));
        return false;
    }
    return true;
}

void Device::block(const QString &why, const QString &code)
{
    loggedIn = false;
    errText = why;
    // 30s → 60s → 120s → 240s → 480s → 900s，首次 30s；仅限流码递增
    if (rateLimitCodes().contains(code)) {
        ++fails;
        const qint64 now = QDateTime::currentMSecsSinceEpoch();
        blockedUntil = now + std::min<qint64>(900000LL, 30000LL * (1LL << std::min(5, fails - 1)));
    }
}

void Device::forgetSession()
{
    cookies.clear();
    token.clear();
    loggedIn = false;
}

void Device::logout()
{
    if (!loggedIn)
        return;
    refreshToken();
    QVariantMap body;
    body.insert(QStringLiteral("Logout"), 1);
    post(QStringLiteral("/api/user/logout"), body);
    forgetSession();
}

int Device::waitSeconds() const
{
    const qint64 ms = blockedUntil - QDateTime::currentMSecsSinceEpoch();
    return ms <= 0 ? 0 : int(ms / 1000);
}

// ---------------------------------------------------------------- 采样

namespace {
const QVector<QPair<QString, QString>> &netEx()
{
    static const QVector<QPair<QString, QString>> t = {
        {QStringLiteral("0"), QStringLiteral("无服务")},   {QStringLiteral("1"), QStringLiteral("GSM")},
        {QStringLiteral("2"), QStringLiteral("GPRS")},     {QStringLiteral("3"), QStringLiteral("EDGE")},
        {QStringLiteral("41"), QStringLiteral("WCDMA")},   {QStringLiteral("42"), QStringLiteral("HSDPA")},
        {QStringLiteral("43"), QStringLiteral("HSUPA")},   {QStringLiteral("44"), QStringLiteral("HSPA")},
        {QStringLiteral("45"), QStringLiteral("HSPA+")},   {QStringLiteral("46"), QStringLiteral("DC-HSPA+")},
        {QStringLiteral("61"), QStringLiteral("TD-SCDMA")}, {QStringLiteral("101"), QStringLiteral("LTE")},
        {QStringLiteral("1011"), QStringLiteral("LTE-A")}, {QStringLiteral("111"), QStringLiteral("5G NR")},
    };
    return t;
}

const QVector<QPair<QString, QString>> &netT()
{
    static const QVector<QPair<QString, QString>> t = {
        {QStringLiteral("1"), QStringLiteral("GSM")},     {QStringLiteral("4"), QStringLiteral("WCDMA")},
        {QStringLiteral("7"), QStringLiteral("HSPA")},    {QStringLiteral("8"), QStringLiteral("TD-SCDMA")},
        {QStringLiteral("9"), QStringLiteral("HSPA+")},   {QStringLiteral("19"), QStringLiteral("LTE")},
        {QStringLiteral("20"), QStringLiteral("LTE/5G")},
    };
    return t;
}
} // namespace

Snap Device::sample()
{
    Snap s;
    s.ts = QDateTime::currentMSecsSinceEpoch();
    lastNetErr.clear();
    if (!login(false)) {
        s.loggedIn = false;
        s.err = errText.isEmpty() ? Lang::t(QStringLiteral("err.login")) : errText;
        const int w = waitSeconds();
        if (w > 0)
            s.err += fmt1(Lang::t(QStringLiteral("fmt.retry")), QString::number(w));
    }
    const QVariantMap st = get(QStringLiteral("/api/monitoring/status"));
    const QVariantMap traffic = get(QStringLiteral("/api/monitoring/traffic-statistics"));
    const QVariantMap signal = get(QStringLiteral("/api/device/signal"));
    const QVariantMap hosts = get(QStringLiteral("/api/system/HostInfo"));
    fillCore(s, st, traffic, signal);
    // 断网时 Login 可能仍返回 true，核心接口全空代表传输层失败，必须标 ok=false
    if (st.isEmpty() && traffic.isEmpty() && signal.isEmpty()) {
        s.ok = false;
        if (s.err.isEmpty())
            s.err = lastNetErr.isEmpty() ? Lang::t(QStringLiteral("err.net")) : lastNetErr;
    } else if (s.err.isEmpty()) {
        s.ok = true;
    }
    fillHosts(s, hosts);

    const QVariantMap plmn = get(QStringLiteral("/api/net/current-plmn"));
    s.carrier = strv(plmn, QStringLiteral("ShortName"));
    const QVariantMap month = get(QStringLiteral("/api/monitoring/month_statistics"));
    const QVariantMap limit = get(QStringLiteral("/api/monitoring/start_date"));
    s.monthDown = lngv(month, QStringLiteral("CurrentMonthDownload"));
    s.monthUp = lngv(month, QStringLiteral("CurrentMonthUpload"));
    s.dayUsed = lngv(month, QStringLiteral("CurrentDayUsed"));
    fillPlan(s, limit, month);

    const QVariantMap sms = get(QStringLiteral("/api/sms/sms-count"));
    s.smsUnread = int(lngv(sms, QStringLiteral("LocalUnread")));
    s.smsInbox = int(lngv(sms, QStringLiteral("LocalInbox")));

    const QVariantMap dev = get(QStringLiteral("/api/device/information"));
    s.wanIp = strv(dev, QStringLiteral("WanIPAddress"));
    s.wanIpv6 = strv(dev, QStringLiteral("WanIPv6Address"));
    s.msisdn = strv(dev, QStringLiteral("Msisdn"));
    s.uptime = lngv(dev, QStringLiteral("uptime"));
    s.model = strv(dev, QStringLiteral("DeviceName"));

    if (s.loggedIn && (s.rsrp == QLatin1String("-") || s.hostsTotal == 0))
        loggedIn = false;   // 会话被网页后台顶掉：下次采样重新登录
    return s;
}

void Device::fillCore(Snap &s, const QVariantMap &status, const QVariantMap &traffic,
                      const QVariantMap &signal)
{
    s.connected = strv(status, QStringLiteral("ConnectionStatus")) == QLatin1String("901");
    s.conn = s.connected ? Lang::t(QStringLiteral("v.connected"))
                         : Lang::t(QStringLiteral("v.disconnected"));
    s.net = mapNet(netEx(), strv(status, QStringLiteral("CurrentNetworkTypeEx")));
    if (s.net == strv(status, QStringLiteral("CurrentNetworkTypeEx")))
        s.net = mapNet(netT(), strv(status, QStringLiteral("CurrentNetworkType")));
    bool ok = false;
    s.sigBars = strv(status, QStringLiteral("SignalIcon")).toInt(&ok);
    if (!ok)
        s.sigBars = 0;
    s.sigMax = strv(status, QStringLiteral("maxsignal")).toInt(&ok);
    if (!ok)
        s.sigMax = 5;
    s.rsrp = numericOnly(strv(signal, QStringLiteral("rsrp")));
    s.sinr = numericOnly(strv(signal, QStringLiteral("sinr")));
    s.rsrq = numericOnly(strv(signal, QStringLiteral("rsrq")));
    s.rsrpLevel = levelOf(s.rsrp);
    s.rateDown = lngv(traffic, QStringLiteral("CurrentDownloadRate"));
    s.rateUp = lngv(traffic, QStringLiteral("CurrentUploadRate"));
    const QString bp = strv(status, QStringLiteral("BatteryPercent"));
    s.battery = bp.isEmpty() ? QStringLiteral("-") : bp + QStringLiteral("%");
    s.charging = strv(status, QStringLiteral("BatteryStatus")) == QLatin1String("1");
}

void Device::fillHosts(Snap &s, const QVariantMap &d)
{
    const QVariantList arr = listv(d, QStringLiteral("__list"));
    for (const QVariant &o : arr) {
        const QVariantMap h = o.toMap();
        if (h.isEmpty())
            continue;
        Host host;
        host.name = strv(h, QStringLiteral("ActualName"));
        if (host.name.isEmpty())
            host.name = strv(h, QStringLiteral("HostName"));
        host.mac = strv(h, QStringLiteral("MACAddress"));
        host.ip = strv(h, QStringLiteral("IPAddress"));
        host.band = strv(h, QStringLiteral("Frequency"));
        host.brand = strv(h, QStringLiteral("DevBrands"));
        if (host.brand.isEmpty())
            host.brand = strv(h, QStringLiteral("VendorClassID"));
        host.active = isActiveField(h, QStringLiteral("Active"))
                      || isActiveField(h, QStringLiteral("WlanActive"));
        host.ip6 = strv(h, QStringLiteral("IPv6Address"));
        host.ifType = strv(h, QStringLiteral("InterfaceType"));
        host.addrSource = strv(h, QStringLiteral("AddressSource"));
        host.ssid = strv(h, QStringLiteral("SsidName"));
        host.vendorId = strv(h, QStringLiteral("VendorClassID"));
        host.guest = isActiveField(h, QStringLiteral("IsGuest"));
        host.local = isActiveField(h, QStringLiteral("IsLocalDevice"));
        host.rxK = lngv(h, QStringLiteral("RxKBytes"));
        host.txK = lngv(h, QStringLiteral("TxKBytes"));
        host.assocSec = lngv(h, QStringLiteral("AssociatedTime"));
        const QString ar = strv(h, QStringLiteral("AccessRecord"));
        const int hash = ar.indexOf(QLatin1Char('#'));
        host.lastAccess = (hash >= 0 ? ar.left(hash) : ar).trimmed();
        s.hosts.append(host);
        ++s.hostsTotal;
        if (host.active)
            ++s.hostsActive;
    }
}

void Device::fillPlan(Snap &s, const QVariantMap &limit, const QVariantMap &month)
{
    Q_UNUSED(month)
    s.planStartDay = strv(limit, QStringLiteral("StartDay"));
    s.planThreshold = strv(limit, QStringLiteral("MonthThreshold"));
    s.planLimitRaw = strv(limit, QStringLiteral("DataLimit"));
    s.planLimit = parseSize(s.planLimitRaw);
    s.planUsed = s.monthDown + s.monthUp;
    if (s.planLimit > 0) {
        s.planRemain = std::max<qint64>(0, s.planLimit - s.planUsed);
        s.planPercent = std::round(s.planUsed * 1000.0 / double(s.planLimit)) / 10.0;
        bool tok = false;
        const double thr = s.planThreshold.toDouble(&tok);
        s.planOverThreshold = tok && s.planPercent >= thr;
        s.planOverLimit = s.planPercent >= 100;
    }
    const QDate now = QDate::currentDate();
    const int start = std::max(1, int(lngv(limit, QStringLiteral("StartDay"))));
    const int dim = now.daysInMonth();
    const int cur = now.day() >= start ? now.day() : now.day() + dim;
    s.planDayPercent = std::round(std::min(100.0, cur * 100.0 / dim) * 10) / 10.0;
    s.planDaysLeft = std::max(0, dim - cur);
}

// ---------------------------------------------------------------- 短信

QVector<SmsItem> Device::smsAll(QString *error)
{
    if (error)
        error->clear();
    QVector<SmsItem> list;
    if (!login(false)) {
        if (error)
            *error = errText;
        return list;
    }

    // 1) 枚举全部会话号码
    QStringList phones;
    QSet<QString> phoneSeen;
    for (int cp = 1; cp <= 40; ++cp) {
        QVariantMap body;
        body.insert(QStringLiteral("pageindex"), cp);
        body.insert(QStringLiteral("readcount"), 20);
        const QVariantMap r = post(QStringLiteral("/api/sms/sms-list-contact"), body);
        if (!failText(r).isEmpty())
            break;
        const QVariantList arr = listv(sub(r, QStringLiteral("messages")), QStringLiteral("message"));
        if (arr.isEmpty())
            break;
        for (const QVariant &o : arr) {
            const QVariantMap m = o.toMap();
            const QString ph = strv(m, QStringLiteral("phone"));
            if (!ph.isEmpty() && !phoneSeen.contains(ph)) {
                phoneSeen.insert(ph);
                phones.append(ph);
            }
        }
        if (arr.size() < 20)
            break;
    }

    // 2) 逐个号码取其会话内全部短信，按 index 去重
    QSet<QString> idxSeen;
    for (const QString &ph : phones) {
        for (int tp = 1; tp <= 40; ++tp) {
            QVariantMap body;
            body.insert(QStringLiteral("phone"), ph);
            body.insert(QStringLiteral("pageindex"), tp);
            body.insert(QStringLiteral("readcount"), 20);
            const QVariantMap r = post(QStringLiteral("/api/sms/sms-list-phone"), body);
            if (!failText(r).isEmpty())
                break;
            const QVariantList arr = listv(sub(r, QStringLiteral("messages")), QStringLiteral("message"));
            if (arr.isEmpty())
                break;
            int fresh = 0;
            for (const QVariant &o : arr) {
                const QVariantMap m = o.toMap();
                const QString index = strv(m, QStringLiteral("index"));
                if (!index.isEmpty() && idxSeen.contains(index))
                    continue;   // 分页重叠 → 跳过已取
                if (!index.isEmpty())
                    idxSeen.insert(index);
                ++fresh;
                SmsItem item;
                item.index = index;
                item.phone = strv(m, QStringLiteral("phone"));
                item.date = strv(m, QStringLiteral("date"));
                item.content = decodeSms(strv(m, QStringLiteral("content"))).trimmed();
                item.unread = strv(m, QStringLiteral("smstat")) == QLatin1String("0");
                list.append(item);
            }
            if (arr.size() < 20 || fresh == 0)
                break;   // 末页或全旧 → 该会话结束
        }
    }

    std::stable_sort(list.begin(), list.end(), [](const SmsItem &a, const SmsItem &b) {
        return QString::compare(b.date, a.date, Qt::CaseSensitive) < 0;
    });
    return list;
}

QString Device::markRead(const QStringList &indexes)
{
    return smsAction(QStringLiteral("/api/sms/set-read"), indexes);
}

QString Device::deleteSms(const QStringList &indexes)
{
    return smsAction(QStringLiteral("/api/sms/delete-sms"), indexes);
}

QString Device::smsAction(const QString &path, const QStringList &indexes)
{
    if (!login(false))
        return errText;
    QVariantList list;
    for (const QString &i : indexes)
        list.append(i);
    QVariantMap body;
    body.insert(QStringLiteral("Index"), list);
    const QVariantMap d = post(path, body);
    const QString fail = failText(d);
    return fail.isEmpty() ? QStringLiteral("ok") : fail;
}