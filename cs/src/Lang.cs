using System;
using System.Collections.Generic;

namespace HwWidget
{
    /// <summary>多语言支持：简体中文 / 英语 / 繁体中文 / 日语 / 俄语 / 韩语，默认简体中文。
    /// 用法：L.T("key")，占位符用 string.Format(L.T(key), x)。新增语言只需在 Options 加一列并给每条补词。</summary>
    static class L
    {
        public static string Lang = "zh-CN";          // zh-CN | en | zh-TW | ja | ru | ko

        // 每项 { 显示名, 语言代码 }；列顺序须与 Dict 内各词条数组一致
        public static readonly string[][] Options =
        {
            new[] { "简体中文", "zh-CN" },
            new[] { "English", "en" },
            new[] { "繁體中文", "zh-TW" },
            new[] { "日本語", "ja" },
            new[] { "Русский", "ru" },
            new[] { "한국어", "ko" },
        };

        /// <summary>语言代码 → 列索引（0..5），未匹配回退 0。</summary>
        public static int IndexOf(string code)
        {
            for (int i = 0; i < Options.Length; i++)
                if (Options[i][1] == code) return i;
            return 0;
        }

        static int Idx() { return IndexOf(Lang); }

        public static string T(string key)
        {
            string[] v;
            if (!Dict.TryGetValue(key, out v)) return key;
            int i = Idx();
            return i < v.Length ? v[i] : v[0];
        }

        /// <summary>运营商名不翻译，保持设备返回原样，仅去掉“中国”前缀。</summary>
        public static string Carrier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("中国", "").Trim();
        }

        // 每项 { zh-CN, en, zh-TW, ja, ru, ko }
        static readonly Dictionary<string, string[]> Dict = new Dictionary<string, string[]>
        {
            // ---- 悬浮窗行标签
            { "lbl.network",  new[]{"网络", "Network", "網路", "ネットワーク", "Сеть", "네트워크"} },
            { "lbl.model",    new[]{"型号", "Model", "型號", "機種", "Модель", "기종"} },
            { "lbl.signal",   new[]{"信号", "Signal", "信號", "電波", "Сигнал", "신호"} },
            { "lbl.rate",     new[]{"速率", "Speed", "速率", "速度", "Скорость", "속도"} },
            { "lbl.plan",     new[]{"套餐", "Plan", "套餐", "プラン", "Тариф", "요금제"} },
            { "lbl.traffic",  new[]{"流量", "Traffic", "流量", "通信量", "Трафик", "데이터"} },
            { "lbl.hosts",    new[]{"终端", "Devices", "終端", "端末", "Устройства", "단말"} },
            { "lbl.battery",  new[]{"电量", "Battery", "電量", "バッテリ", "Батарея", "배터리"} },
            { "lbl.local",    new[]{"本机", "Local", "本機", "本機", "Локальный", "로컬"} },
            { "lbl.wan",      new[]{"WAN", "WAN", "WAN", "WAN", "WAN", "WAN"} },
            { "lbl.v6",       new[]{"IPv6", "IPv6", "IPv6", "IPv6", "IPv6", "IPv6"} },
            { "lbl.uptime",   new[]{"运行", "Uptime", "運行", "稼働", "Аптайм", "가동"} },
            { "lbl.sms",      new[]{"短信", "SMS", "簡訊", "SMS", "SMS", "문자"} },
            { "lbl.clock",    new[]{"时间", "Clock", "時間", "時刻", "Время", "시간"} },
            { "lbl.status",   new[]{"状态", "Status", "狀態", "状態", "Статус", "상태"} },
            { "lbl.detail",   new[]{"详情", "Details", "詳情", "詳細", "Подробно", "상세"} },
            { "lbl.num",      new[]{"号码", "Number", "號碼", "番号", "Номер", "번호"} },

            // ---- 值文案
            { "v.fail",       new[]{"读取失败", "Read failed", "讀取失敗", "読み取り失敗", "Ошибка чтения", "읽기 실패"} },
            { "v.connected",  new[]{"已连接", "Connected", "已連線", "接続済み", "Подключено", "연결됨"} },
            { "v.disconnected", new[]{"未连接", "Disconnected", "未連線", "未接続", "Не подключено", "연결 안 됨"} },
            { "v.charging",   new[]{"充电中", "Charging", "充電中", "充電中", "Зарядка", "충전 중"} },
            { "v.today",      new[]{"今日", "Today", "今日", "本日", "Сегодня", "오늘"} },
            { "v.month",      new[]{"本月", "Month", "本月", "今月", "Этот месяц", "이번 달"} },
            { "fmt.daysleft", new[]{"剩{0}天", "{0}d left", "剩{0}天", "残り{0}日", "Осталось {0} дн.", "{0}일 남음"} },
            { "fmt.smsunread", new[]{"未读 {0} 条", "{0} unread", "未讀 {0} 則", "未読 {0} 件", "{0} непрочит.", "읽지 않음 {0}건"} },
            { "fmt.retry",    new[]{"，{0} 秒后重试", ", retry in {0}s", "，{0} 秒後重試", "、{0}秒後に再試行", ", повтор через {0}с", ", {0}초 후 재시도"} },
            { "u.day",        new[]{"天", "d", "天", "日", "дн.", "일"} },
            { "u.hour",       new[]{"小时", "h", "小時", "時間", "ч", "시간"} },
            { "u.minute",     new[]{"分", "m", "分", "分", "мин", "분"} },
            { "u.second",     new[]{"秒", "s", "秒", "秒", "с", "초"} },

            // ---- 右键菜单 / 提示 / 托盘
            { "menu.toggle",  new[]{"显示 / 隐藏悬浮窗", "Show / Hide widget", "顯示 / 隱藏懸浮窗", "ウィジェット表示 / 非表示", "Показать / скрыть виджет", "위젯 표시 / 숨기기"} },
            { "menu.settings", new[]{"设置…", "Settings…", "設定…", "設定…", "Настройки…", "설정…"} },
            { "menu.refresh", new[]{"立即刷新", "Refresh now", "立即重新整理", "今すぐ更新", "Обновить сейчас", "지금 새로고침"} },
            { "menu.manage", new[]{"打开设备管理页面", "Open device admin page", "開啟裝置管理頁面", "端末管理ページを開く", "Открыть страницу администр.", "장치 관리 페이지 열기"} },
            { "menu.topon",   new[]{"保持置顶", "Keep on top", "保持在最上層", "最前面に維持", "Держать сверху", "항상 위에 유지"} },
            { "menu.topoff",  new[]{"取消置顶", "Unpin from top", "取消最上層", "最前面を解除", "Убрать сверху", "항상 위 해제"} },
            { "menu.lock",    new[]{"锁定位置", "Lock position", "鎖定位置", "位置をロック", "Закрепить позицию", "위치 잠금"} },
            { "menu.unlock",  new[]{"解锁位置", "Unlock position", "解除鎖定", "位置ロックを解除", "Открепить позицию", "위치 잠금 해제"} },
            { "menu.clickon", new[]{"穿透模式（点击可透）", "Click-through mode", "穿透模式（點擊可透）", "クリック貫通モード", "Режим прозрачности кликов", "클릭 통과 모드"} },
            { "menu.clickoff", new[]{"关闭穿透模式", "Exit click-through", "關閉穿透模式", "クリック貫通を解除", "Выключить прозрачность кликов", "클릭 통과 해제"} },
            { "menu.quit",    new[]{"退出", "Exit", "結束", "終了", "Выход", "끝내기"} },
            { "hint.alpha",   new[]{"背景透明度 {0}%", "Opacity {0}%", "背景透明度 {0}%", "背景の不透明度 {0}%", "Прозрачность {0}%", "배경 투명도 {0}%"} },
            { "hint.locked",  new[]{"位置已锁定", "Position locked", "位置已鎖定", "位置をロックしました", "Позиция закреплена", "위치가 잠겼습니다"} },
            { "hint.unlocked", new[]{"位置已解锁", "Position unlocked", "位置已解鎖", "位置ロックを解除しました", "Позиция откреплена", "위치 잠금이 해제되었습니다"} },
            { "hint.clickon", new[]{"已开启穿透模式：托盘图标右键可关闭",
                                    "Click-through on: right-click tray icon to disable",
                                    "已開啟穿透模式：於系統匣圖示右鍵可關閉",
                                    "クリック貫通を有効にしました：トレイアイコン右クリックで解除",
                                    "Прозрачность кликов вкл.: снимок через правый клик в трее",
                                    "클릭 통과 켜짐: 트레이 아이콘 우클릭으로 해제"} },
            { "hint.clickoff", new[]{"已关闭穿透模式", "Click-through off", "已關閉穿透模式", "クリック貫通を解除しました", "Прозрачность кликов выкл.", "클릭 통과 꺼짐"} },
            { "tray.title",   new[]{"华为随身 WiFi", "Huawei Mobile WiFi", "華為隨身 WiFi", "Huawei モバイル WiFi", "Huawei Mobile WiFi", "Huawei 모바일 WiFi"} },
            { "tray.balloon", new[]{"随身 WiFi 悬浮窗", "Mobile WiFi widget", "隨身 WiFi 懸浮窗", "モバイル WiFi ウィジェット", "Виджет Mobile WiFi", "모바일 WiFi 위젯"} },

            // ---- 设备协议层错误
            { "err.token",    new[]{"无法获取 token", "Cannot get token", "無法取得 token", "token を取得できません", "Не удалось получить token", "token을 가져올 수 없음"} },
            { "err.csrf",     new[]{"无法获取 CSRF token", "Cannot get CSRF token", "無法取得 CSRF token", "CSRF token を取得できません", "Не удалось получить CSRF token", "CSRF token을 가져올 수 없음"} },
            { "err.challenge", new[]{"登录挑战响应异常", "Login challenge response invalid", "登入挑戰回應異常", "ログインチャレンジ応答が不正", "Неверный ответ challenge входа", "로그인 챌린지 응답 오류"} },
            { "err.signature", new[]{"服务器签名校验不通过", "Server signature mismatch", "伺服器簽章驗證失敗", "サーバー署名の検証に失敗", "Несовпадение подписи сервера", "서버 서명 검증 실패"} },
            { "err.login",    new[]{"登录失败", "Login failed", "登入失敗", "ログインに失敗", "Ошибка входа", "로그인 실패"} },
            { "err.reboot",   new[]{"重启指令已下发", "Reboot command sent", "重啟指令已下發", "再起動コマンドを送信しました", "Команда перезагрузки отправлена", "재시작 명령을 보냈습니다"} },
            { "err.cleared",  new[]{"已清零历史流量统计", "Traffic history cleared", "已清零歷史流量統計", "通信量履歴をクリアしました", "История трафика очищена", "데이터 사용량 기록이 초기화됨"} },
            { "err.don",      new[]{"已开启移动数据", "Mobile data enabled", "已開啟行動數據", "モバイルデータを有効化", "Мобильные данные включены", "모바일 데이터 켜짐"} },
            { "err.doff",     new[]{"已关闭移动数据", "Mobile data disabled", "已關閉行動數據", "モバイルデータを無効化", "Мобильные данные выключены", "모바일 데이터 꺼짐"} },
            { "err.planset",  new[]{"套餐设置已保存", "Plan settings saved", "套餐設定已儲存", "プラン設定を保存しました", "Настройки тарифа сохранены", "요금제 설정이 저장됨"} },
            { "e.100002", new[]{"设备不支持该接口", "API not supported by device", "裝置不支援此介面", "端末がこのAPIに対応していません", "API не поддерживается устройством", "단말이 이 API를 지원하지 않음"} },
            { "e.100003", new[]{"需要登录（会话无效或已过期）", "Login required (session invalid or expired)", "需要登入（工作階段無效或已過期）", "ログインが必要（セッションが無効または期限切れ）", "Требуется вход (сессия недействительна или истекла)", "로그인 필요 (세션 무효 또는 만료)"} },
            { "e.100005", new[]{"请求参数错误", "Bad request parameters", "請求參數錯誤", "リクエストパラメータエラー", "Неверные параметры запроса", "요청 매개변수 오류"} },
            { "e.100006", new[]{"CSRF token 校验失败", "CSRF token validation failed", "CSRF token 驗證失敗", "CSRF token の検証に失敗", "Ошибка проверки CSRF token", "CSRF token 검증 실패"} },
            { "e.108005", new[]{"登录过于频繁，被设备限流", "Login rate limited by device", "登入過於頻繁，遭裝置限流", "ログインが頻繁すぎます（端末制限）", "Слишком частый вход, лимит устройства", "로그인 너무 빈번 (단말 제한)"} },
            { "e.108006", new[]{"认证失败（密码错误或挑战失效）", "Authentication failed (wrong password or stale challenge)", "認證失敗（密碼錯誤或挑戰失效）", "認証に失敗（パスワード誤りまたは challenge 失効）", "Ошибка аутентификации (неверный пароль или challenge)", "인증 실패 (비밀번호 오류 또는 챌린지 만료)"} },
            { "e.113055", new[]{"短信操作被拒（已读过 / 下标不存在）", "SMS action rejected (already read / index missing)", "簡訊操作被拒（已讀過 / 索引不存在）", "SMS操作が拒否（既読 / 指標なし）", "Действие SMS отклонено (прочитано / нет индекса)", "문자 작업 거부 (읽힘 / 인덱스 없음)"} },
            { "e.125001", new[]{"账号被锁定", "Account locked", "帳號被鎖定", "アカウントがロックされました", "Аккаунт заблокирован", "계정이 잠김"} },
            { "e.125002", new[]{"会话已注销", "Session logged out", "工作階段已註銷", "セッションがログアウトされました", "Сессия завершена", "세션 로그아웃됨"} },
            { "e.125003", new[]{"会话状态失效，需重开页面", "Session invalid, reload the page", "工作階段狀態失效，需重開頁面", "セッションが無効、ページの再読み込みが必要", "Сессия недействительна, перезагрузите страницу", "세션 무효, 페이지 새로고침 필요"} },
            { "e.other",  new[]{"设备返回错误", "Device error", "裝置返回錯誤", "端末がエラーを返しました", "Ошибка устройства", "단말 오류"} },

            // ---- 设置窗口
            { "tab.conn",     new[]{"连接", "Connection", "連線", "接続", "Подключение", "연결"} },
            { "tab.look",     new[]{"外观", "Appearance", "外觀", "外観", "Внешний вид", "모양"} },
            { "tab.rows",     new[]{"显示内容", "Content", "顯示內容", "表示項目", "Содержимое", "표시 내용"} },
            { "grp.device",   new[]{"设备连接", "Device connection", "裝置連線", "端末接続", "Подключение устройства", "장치 연결"} },
            { "grp.security", new[]{"安全", "Security", "安全性", "セキュリティ", "Безопасность", "보안"} },
            { "grp.font",     new[]{"字体", "Font", "字型", "フォント", "Шрифт", "글꼴"} },
            { "grp.color",    new[]{"颜色", "Colors", "顏色", "色", "Цвета", "색상"} },
            { "grp.behavior", new[]{"行为", "Behavior", "行為", "動作", "Поведение", "동작"} },
            { "lbl.host",     new[]{"管理地址", "Manage address", "管理位址", "管理アドレス", "Адрес администр.", "관리 주소"} },
            { "lbl.user",     new[]{"登录用户", "Username", "登入使用者", "ユーザー名", "Пользователь", "사용자 이름"} },
            { "lbl.pwd",      new[]{"登录密码", "Password", "登入密碼", "パスワード", "Пароль", "비밀번호"} },
            { "lbl.interval", new[]{"刷新秒数", "Interval (s)", "重新整理秒數", "更新間隔(秒)", "Интервал (с)", "갱신 (초)"} },
            { "lbl.opacity",  new[]{"透明度", "Opacity", "透明度", "不透明度", "Прозрачность", "투명도"} },
            { "lbl.fontsize", new[]{"字号", "Font size", "字級", "サイズ", "Размер", "글자 크기"} },
            { "lbl.font",     new[]{"字体", "Font", "字體", "フォント", "Шрифт", "글꼴"} },
            { "lbl.language", new[]{"语言", "Language", "語言", "言語", "Язык", "언어"} },
            { "chk.savepwd",  new[]{"把密码保存到本机配置文件", "Save password to config file", "將密碼儲存至本機設定檔", "パスワードを設定ファイルに保存", "Сохранить пароль в файл конфигурации", "비밀번호를 설정 파일에 저장"} },
            { "chk.top",      new[]{"置顶显示", "Always on top", "最上層顯示", "最前面に表示", "Всегда поверху", "항상 위에"} },
            { "chk.lock",     new[]{"锁定位置", "Lock position", "鎖定位置", "位置を固定", "Закрепить позицию", "위치 잠금"} },
            { "chk.border",   new[]{"无边框样式", "Borderless", "無邊框樣式", "枠なし表示", "Без рамки", "테두리 없음"} },
            { "chk.title",    new[]{"显示标题行", "Show title bar", "顯示標題列", "タイトルバーを表示", "Показать заголовок", "제목 표시줄 보기"} },
            { "btn.save",     new[]{"确定", "OK", "確定", "OK", "OK", "확인"} },
            { "btn.cancel",   new[]{"取消", "Cancel", "取消", "キャンセル", "Отмена", "취소"} },
            { "btn.reset",    new[]{"恢复默认", "Reset", "回復預設", "既定に戻す", "Сброс", "기본값 복원"} },
            { "win.title",    new[]{"悬浮窗设置", "Widget settings", "懸浮窗設定", "ウィジェット設定", "Настройки виджета", "위젯 설정"} },
            { "tip.now",      new[]{"保存后立即生效", "Takes effect right after saving", "儲存後立即生效", "保存後すぐに反映されます", "Применяется сразу после сохранения", "저장 후 즉시 적용"} },
            { "color.fg",     new[]{"文字", "Text", "文字", "文字", "Текст", "글자"} },
            { "color.bg",     new[]{"底色", "Background", "底色", "背景", "Фон", "배경"} },
            { "color.accent", new[]{"正常", "Normal", "正常", "正常", "Норма", "정상"} },
            { "color.warn",   new[]{"告警", "Warning", "警告", "警告", "Предупр.", "경고"} },
            { "color.bad",    new[]{"异常", "Error", "異常", "エラー", "Ошибка", "오류"} },
            { "app.running",  new[]{"悬浮窗已经在运行了（看系统托盘）",
                                    "The widget is already running (see system tray)",
                                    "懸浮窗已在執行中（看系統匣）",
                                    "ウィジェットは既に実行中です（トレイを確認）",
                                    "Виджет уже запущен (см. системный трей)",
                                    "위젯이 이미 실행 중입니다 (트레이 확인)"} },

            // ---- 显示内容项（设置页）
            { "row.conn",     new[]{"连接状态 / 运营商", "Connection / Carrier", "連線狀態 / 業者", "接続状態 / 通信事業者", "Соединение / Оператор", "연결 상태 / 통신사"} },
            { "row.model",    new[]{"设备型号", "Device model", "裝置型號", "端末機種", "Модель устройства", "장치 기종"} },
            { "row.signal",   new[]{"信号格 + RSRP/SINR", "Signal bars + RSRP/SINR", "信號格 + RSRP/SINR", "電波バー + RSRP/SINR", "Сигнал + RSRP/SINR", "신호 강도 + RSRP/SINR"} },
            { "row.rate",     new[]{"实时上下行速率", "Live up/down speed", "即時上下行速率", "実効速度（上り/下り）", "Скорость ↑/↓", "실시간 업/다운 속도"} },
            { "row.plan",     new[]{"套餐用量与剩余天数", "Plan usage & days left", "套餐用量與剩餘天數", "プラン使用量と残り日数", "Расход тарифа и дни", "요금제 사용량 및 남은 날"} },
            { "row.traffic",  new[]{"今日 / 本月流量", "Today / Month traffic", "今日 / 本月流量", "本日 / 今月の通信量", "Сегодня / за месяц", "오늘 / 이번 달 데이터"} },
            { "row.hosts",    new[]{"在线终端数", "Online devices", "線上終端數", "接続端末数", "Устройства онлайн", "온라인 단말 수"} },
            { "row.battery",  new[]{"电量与充电状态", "Battery & charging", "電量與充電狀態", "バッテリーと充電状態", "Батарея и зарядка", "배터리 및 충전 상태"} },
            { "row.msisdn",   new[]{"手机号", "Phone number", "手機號", "電話番号", "Номер телефона", "전화번호"} },
            { "row.ip",       new[]{"本机 IP / WAN IP", "Local IP / WAN IP", "本機 IP / WAN IP", "ローカル IP / WAN IP", "Локальный IP / WAN IP", "로컬 IP / WAN IP"} },
            { "row.uptime",   new[]{"设备运行时长", "Device uptime", "裝置執行時長", "端末の稼働時間", "Время работы устройства", "장치 가동 시간"} },
            { "row.sms",      new[]{"未读短信提醒", "Unread SMS alert", "未讀簡訊提醒", "未読SMS通知", "Непрочитанные SMS", "읽지 않은 문자 알림"} },
            { "row.clock",    new[]{"当前时间", "Current time", "目前時間", "現在時刻", "Текущее время", "현재 시간"} },
        };
    }
}
