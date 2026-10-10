using System.Text.Json;

namespace FreeMon;

/// <summary>
/// Localization. Source language is English: L.T("English text") / L.F("Format {0}", x) for the interface,
/// L.R / L.RF for reports (separate report language). Translations are embedded resources
/// "lang/&lt;code&gt;.json" = { "English text": "Translation" }; a missing translation shows the English text.
/// </summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("uk", "Українська") };

    static Dictionary<string, string> _ui = new();
    static Dictionary<string, string> _rep = new();

    public static string Language { get; private set; } = "en";
    public static string ReportLanguage { get; private set; } = "en";

    public static string SystemLanguage =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "uk" ? "uk" : "en";

    /// <summary>Known language code; empty or unknown = language of the operating system.</summary>
    public static string Normalize(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        return Languages.Any(l => l.Code == c) ? c : SystemLanguage;
    }

    public static void SetLanguage(string? code)
    {
        Language = Normalize(code);
        _ui = Load(Language);
    }

    public static void SetReportLanguage(string? code)
    {
        ReportLanguage = string.IsNullOrWhiteSpace(code) ? Language : Normalize(code);
        _rep = ReportLanguage == Language ? _ui : Load(ReportLanguage);
    }

    static Dictionary<string, string> Load(string code)
    {
        if (code == "en") return new();
        try
        {
            using var s = typeof(L).Assembly.GetManifestResourceStream($"lang/{code}.json");
            if (s is not null) return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Localization " + code, ex);
        }
        return new();
    }

    public static string T(string en) => _ui.TryGetValue(en, out var t) && t.Length > 0 ? t : en;

    public static string F(string en, params object?[] args) => string.Format(T(en), args);

    public static string R(string en) => _rep.TryGetValue(en, out var t) && t.Length > 0 ? t : en;

    public static string RF(string en, params object?[] args) => string.Format(R(en), args);
}

/// <summary>Network policy text (Ukrainian and English, shown together on purpose).</summary>
public static class NetworkPolicy
{
    public const string Uk =
        "МЕРЕЖЕВА ПОЛІТИКА FREEMON\n\n" +
        "FreeMon встановлює мережеві з'єднання лише з адресами, які задав користувач:\n" +
        "  • пристрої зі списку моніторингу — пінг (ICMP) і перевірки TCP, HTTP(S), HTTP JSON (наприклад, датчики Home Assistant), DNS;\n" +
        "  • системний DNS-сервер — лише щоб перетворити на IP-адреси імена пристроїв, заданих іменем;\n" +
        "  • канали сповіщень, налаштовані адміністратором, — SMTP-сервер і адреси вебхуків.\n" +
        "Підключення до пристроїв з меню правого кліку (SSH, веб) відкриваються у вашому браузері чи SSH-клієнті: " +
        "з'єднання встановлює та програма, а не FreeMon. Трасування маршруту (MTR) виконує системна утиліта (mtr, tracepath, " +
        "traceroute, pathping або tracert) до адреси вибраного пристрою.\n\n" +
        "FreeMon не перевіряє оновлень, не надсилає телеметрії, статистики чи звітів про збої й нічого не завантажує з інтернету. " +
        "Шрифти та іконки вбудовані в програму. HTTP(S)-запити йдуть напряму на задану адресу: без системного проксі, " +
        "без переадресацій на інші адреси, без завантаження сертифікатів чи списків відкликаних сертифікатів.\n\n" +
        "Сповіщення в канали. Якщо канал веде на загальнодоступний сервіс (Telegram, публічний сервер ntfy, зовнішня пошта тощо), " +
        "назви пристроїв, IP-адреси та стан вашої мережі передаються за межі периметра організації й зберігаються на серверах третіх сторін.\n\n" +
        "ЗАСТЕРЕЖЕННЯ. Автор не додавав у FreeMon коду, який передавав би дані кудись, крім перелічених вище адрес. " +
        "Це твердження стосується лише вихідного коду FreeMon у тому вигляді, в якому його опублікував автор, " +
        "і програми, зібраної з цього коду без змін. На змінений код або збірку, зроблену з невідомого коду, воно не поширюється. " +
        "FreeMon використовує сторонні бібліотеки (вкладка «Сторонні компоненти»): автор не використовує в них мережевих функцій, " +
        "крім потрібних для перелічених вище з'єднань, але не може гарантувати відсутність інших мережевих звернень у коді третіх сторін.\n\n" +
        "РОЗРОБКА ЗА ДОПОМОГОЮ ШІ. Код FreeMon написано з допомогою інструментів штучного інтелекту під керівництвом автора. " +
        "Автор перевіряв роботу програми, але не може гарантувати, що згенерований код не містить помилок або мережевих звернень, " +
        "не описаних у цій політиці. Автор не має наміру використовувати і свідомо не використовує жодних мережевих звернень, " +
        "крім описаних вище. Вихідний код відкритий — його може перевірити будь-хто.\n\n" +
        "ЗА БУДЬ-ЯКИХ ОБСТАВИН, ВІДПОВІДНО ДО ЛІЦЕНЗІЇ MIT: ПРОГРАМНЕ ЗАБЕЗПЕЧЕННЯ НАДАЄТЬСЯ «ЯК Є», БЕЗ БУДЬ-ЯКИХ ГАРАНТІЙ, " +
        "ЯВНИХ ЧИ НЕЯВНИХ, ЗОКРЕМА ГАРАНТІЙ ПРИДАТНОСТІ ДО ПРОДАЖУ, ВІДПОВІДНОСТІ ПЕВНІЙ МЕТІ ТА НЕПОРУШЕННЯ ПРАВ. " +
        "У ЖОДНОМУ РАЗІ АВТОРИ ЧИ ВЛАСНИКИ АВТОРСЬКИХ ПРАВ НЕ НЕСУТЬ ВІДПОВІДАЛЬНОСТІ ЗА БУДЬ-ЯКІ ПРЕТЕНЗІЇ, ЗБИТКИ ЧИ ІНШУ " +
        "ВІДПОВІДАЛЬНІСТЬ — ЗА ДОГОВОРОМ, ВНАСЛІДОК ПРАВОПОРУШЕННЯ ЧИ ІНАКШЕ, — ЩО ВИНИКАЮТЬ ІЗ ПРОГРАМНОГО ЗАБЕЗПЕЧЕННЯ, " +
        "ЙОГО ВИКОРИСТАННЯ АБО ІНШИХ ДІЙ З НИМ ЧИ У ЗВ'ЯЗКУ З НИМ. ЮРИДИЧНУ СИЛУ МАЄ АНГЛІЙСЬКИЙ ТЕКСТ ЛІЦЕНЗІЇ (LICENSE.txt).";

    public const string En =
        "FREEMON NETWORK POLICY\n\n" +
        "FreeMon opens network connections only to addresses configured by the user:\n" +
        "  • monitored devices — ping (ICMP) and TCP, HTTP(S), HTTP JSON (e.g. Home Assistant sensors), DNS checks;\n" +
        "  • the system DNS server — only to resolve devices that are configured by host name;\n" +
        "  • notification channels configured by the administrator — the SMTP server and webhook URLs.\n" +
        "Device connections from the right-click menu (SSH, web) open in your browser or SSH client: " +
        "that program makes the connection, not FreeMon. Route tracing (MTR) is done by a system tool (mtr, tracepath, " +
        "traceroute, pathping or tracert) towards the address of the selected device.\n\n" +
        "FreeMon does not check for updates, does not send telemetry, statistics or crash reports and downloads nothing from the Internet. " +
        "Fonts and icons are embedded. HTTP(S) requests go directly to the configured address: no system proxy, " +
        "no redirects to other addresses, no downloading of certificates or certificate revocation lists.\n\n" +
        "Notification channels. If a channel points to a public service (Telegram, a public ntfy server, external e-mail, etc.), " +
        "device names, IP addresses and the state of your network leave your organisation's perimeter and are stored on third-party servers.\n\n" +
        "DISCLAIMER. The author has not added any code to FreeMon that sends data anywhere other than the addresses listed above. " +
        "This statement applies only to the FreeMon source code as published by the author and to a program built from that code without modification. " +
        "It does not apply to modified code or to builds made from unknown sources. " +
        "FreeMon uses third-party libraries (see \"Third-party components\"): the author does not use their network functions " +
        "beyond what is needed for the connections listed above, but cannot guarantee that third-party code makes no other network requests.\n\n" +
        "AI-ASSISTED DEVELOPMENT. FreeMon's code was written with the help of artificial intelligence tools under the author's direction. " +
        "The author has tested the program but cannot guarantee that the generated code is free of errors or of network requests " +
        "not described in this policy. The author has no intention of using, and does not knowingly use, any network requests " +
        "other than those described above. The source code is open, so anyone can review it.\n\n" +
        "IN ANY CASE, UNDER THE MIT LICENSE: THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, " +
        "INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. " +
        "IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN " +
        "ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER " +
        "DEALINGS IN THE SOFTWARE.";
}

public static class EncryptionPolicy
{
    public static string Notice => L.T(NoticeEn);

    const string NoticeEn =
        "• Settings, the device list and notification channel secrets are stored encrypted. " +
        "The key is bound to this computer (for \"only for me\" installations also to the user account).\n" +
        "• The data folder cannot be copied to another computer: it cannot be decrypted there. To move data use " +
        "a backup (Settings → Backups) or device list export and import.\n" +
        "• After reinstalling the operating system or replacing the computer the data cannot be recovered without a backup.\n" +
        "• Make backups regularly — the easiest way is to turn on automatic backups.\n" +
        "• The backup password is not stored anywhere: if you forget it, the backup cannot be opened.";

    public const string Uk =
        "ПОЛІТИКА ШИФРУВАННЯ FREEMON\n\n" +
        "Що захищено\n" +
        "  • Конфігурація (список пристроїв, налаштування) і секрети каналів сповіщень (адреси вебхуків, токени, паролі SMTP) " +
        "зберігаються зашифрованими (AES-256-GCM). Зміна зашифрованого файлу поза програмою виявляється, і програма повертається " +
        "до останньої коректної копії.\n" +
        "  • Журнал перевірок щогодини запечатується. Зміна, видалення чи додавання записів після запечатування виявляється " +
        "під час формування звітів.\n" +
        "  • Якщо задано пароль адміністратора, конфігурація і печатки журналу додатково засвідчуються ключем, похідним від цього пароля. " +
        "Непомітно змінити засвідчені дані без пароля неможливо.\n" +
        "  • Кожен PDF-звіт і сирий журнал мають код перевірки. «Історія та звіти → Перевірити звіт» підтверджує, що звіт " +
        "сформовано цією установкою FreeMon, і показує справжні показники за період.\n\n" +
        "Ключ і прив'язка\n" +
        "  • Ключ шифрування створюється під час першого запуску і зберігається захищеним засобами операційної системи, " +
        "з прив'язкою до цього комп'ютера (у варіанті «лише для мене» — ще й до облікового запису).\n" +
        "  • Копія теки даних на іншому комп'ютері не розшифровується. Перевстановлення ОС або заміна комп'ютера без резервної копії " +
        "означають втрату даних.\n\n" +
        "Резервні копії\n" +
        "  • Повна резервна копія містить налаштування, секрети каналів, ключ і — за вибором — журнал. Вона зашифрована паролем копії, " +
        "який ніде не зберігається.\n" +
        "  • Автоматичні копії робляться без введення пароля, але відкрити їх можна лише з паролем копій.\n" +
        "  • Експорт та імпорт списку пристроїв (CSV) — окремо, без шифрування: CSV не містить секретів каналів.\n\n" +
        "Межі захисту\n" +
        "  • Захист призначений запобігати випадковим і неавторизованим змінам та виявляти підробку даних і звітів. " +
        "Він не замінює обмеження прав доступу в операційній системі: користувачам, яким не можна змінювати дані FreeMon, " +
        "не надавайте прав адміністратора ОС і доступу до теки даних.\n" +
        "  • Пароль адміністратора — найсильніша частина захисту: використовуйте надійний пароль і не передавайте його.";

    public const string En =
        "FREEMON ENCRYPTION POLICY\n\n" +
        "What is protected\n" +
        "  • The configuration (device list, settings) and notification channel secrets (webhook URLs, tokens, SMTP passwords) " +
        "are stored encrypted (AES-256-GCM). Any change of an encrypted file outside the program is detected and the program " +
        "falls back to the last valid copy.\n" +
        "  • The check log is sealed every hour. Changing, deleting or adding records after sealing is detected when reports are made.\n" +
        "  • If an administrator password is set, the configuration and log seals are additionally certified with a key derived from it. " +
        "Certified data cannot be changed unnoticed without the password.\n" +
        "  • Every PDF report and raw log carries a verification code. \"History and reports → Verify report\" confirms that the report " +
        "was produced by this FreeMon installation and shows the actual figures for the period.\n\n" +
        "Key and binding\n" +
        "  • The encryption key is created on first start and stored protected by the operating system, bound to this computer " +
        "(and to the user account for \"only for me\" installations).\n" +
        "  • A copy of the data folder cannot be decrypted on another computer. Reinstalling the OS or replacing the computer " +
        "without a backup means data loss.\n\n" +
        "Backups\n" +
        "  • A full backup contains the settings, channel secrets, the key and — optionally — the log. It is encrypted with the backup " +
        "password, which is not stored anywhere.\n" +
        "  • Automatic backups are made without entering the password, but can only be opened with the backup password.\n" +
        "  • Device list export and import (CSV) is separate and not encrypted: the CSV contains no channel secrets.\n\n" +
        "Limits\n" +
        "  • The protection is meant to prevent accidental and unauthorised changes and to detect forged data and reports. " +
        "It does not replace operating system access control: do not give OS administrator rights or access to the data folder " +
        "to users who must not change FreeMon data.\n" +
        "  • The administrator password is the strongest part of the protection: use a strong password and do not share it.";
}
