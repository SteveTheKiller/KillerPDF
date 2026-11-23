using System.Globalization;

namespace KillerPDF.PreviewHandler;

/// <summary>
/// The pane's few words in KillerPDF's languages, picked from the Windows display language
/// because the preview runs inside the shell rather than the app.
/// </summary>
internal static class PreviewStrings
{
    internal enum Key { FitWidth, FitPage, ZoomOut, ZoomIn, Open, Protected, Failed, Incomplete, Retry }

    private static readonly Dictionary<string, string[]> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en-US"] = ["Fit width", "Fit page", "Zoom out", "Zoom in", "Open in KillerPDF",
            "This PDF is password protected. Open it in KillerPDF to enter the password.", "This PDF could not be previewed."],
        ["bn"] = ["প্রস্থে মানান", "পৃষ্ঠায় মানান", "ছোট করুন", "বড় করুন", "KillerPDF-এ খুলুন",
            "এই PDF পাসওয়ার্ড দিয়ে সুরক্ষিত। পাসওয়ার্ড দিতে এটি KillerPDF-এ খুলুন।", "এই PDF-এর প্রিভিউ দেখানো যায়নি।"],
        ["cs-CZ"] = ["Přizpůsobit šířce", "Přizpůsobit stránce", "Oddálit", "Přiblížit", "Otevřít v KillerPDF",
            "Toto PDF je chráněno heslem. Otevřete ho v KillerPDF a zadejte heslo.", "Náhled tohoto PDF nelze zobrazit."],
        ["de-DE"] = ["Breite anpassen", "Seite anpassen", "Verkleinern", "Vergrößern", "In KillerPDF öffnen",
            "Diese PDF ist kennwortgeschützt. Öffnen Sie sie in KillerPDF, um das Kennwort einzugeben.",
            "Für diese PDF kann keine Vorschau angezeigt werden."],
        ["es"] = ["Ajustar al ancho", "Ajustar a la página", "Alejar", "Acercar", "Abrir en KillerPDF",
            "Este PDF está protegido con contraseña. Ábralo en KillerPDF para escribir la contraseña.",
            "No se puede mostrar la vista previa de este PDF."],
        ["fr-FR"] = ["Ajuster à la largeur", "Ajuster à la page", "Zoom arrière", "Zoom avant", "Ouvrir dans KillerPDF",
            "Ce PDF est protégé par un mot de passe. Ouvrez-le dans KillerPDF pour saisir le mot de passe.",
            "Impossible d'afficher l'aperçu de ce PDF."],
        ["hu-HU"] = ["Szélességhez igazítás", "Oldalhoz igazítás", "Kicsinyítés", "Nagyítás", "Megnyitás a KillerPDF-ben",
            "Ez a PDF jelszóval védett. Nyissa meg a KillerPDF-ben a jelszó megadásához.",
            "Ennek a PDF-nek nem jeleníthető meg az előnézete."],
        ["it-IT"] = ["Adatta alla larghezza", "Adatta alla pagina", "Riduci", "Ingrandisci", "Apri in KillerPDF",
            "Questo PDF è protetto da password. Aprilo in KillerPDF per inserire la password.",
            "Impossibile visualizzare l'anteprima di questo PDF."],
        ["ja-JP"] = ["幅に合わせる", "ページに合わせる", "縮小", "拡大", "KillerPDF で開く",
            "この PDF はパスワードで保護されています。KillerPDF で開いてパスワードを入力してください。",
            "この PDF をプレビューできませんでした。"],
        ["kk-KZ"] = ["Ені бойынша", "Бет бойынша", "Кішірейту", "Үлкейту", "KillerPDF-те ашу",
            "Бұл PDF құпиясөзбен қорғалған. Құпиясөзді енгізу үшін оны KillerPDF-те ашыңыз.",
            "Бұл PDF-ті алдын ала қарау мүмкін болмады."],
        ["ko-KR"] = ["너비에 맞춤", "페이지에 맞춤", "축소", "확대", "KillerPDF에서 열기",
            "이 PDF는 암호로 보호되어 있습니다. 암호를 입력하려면 KillerPDF에서 여세요.",
            "이 PDF를 미리 볼 수 없습니다."],
        ["nb-NO"] = ["Tilpass bredde", "Tilpass side", "Zoom ut", "Zoom inn", "Åpne i KillerPDF",
            "Denne PDF-filen er passordbeskyttet. Åpne den i KillerPDF for å skrive inn passordet.",
            "Kan ikke forhåndsvise denne PDF-filen."],
        ["pl-PL"] = ["Dopasuj do szerokości", "Dopasuj do strony", "Pomniejsz", "Powiększ", "Otwórz w KillerPDF",
            "Ten plik PDF jest chroniony hasłem. Otwórz go w KillerPDF, aby wpisać hasło.",
            "Nie można wyświetlić podglądu tego pliku PDF."],
        ["pt-BR"] = ["Ajustar à largura", "Ajustar à página", "Reduzir", "Ampliar", "Abrir no KillerPDF",
            "Este PDF é protegido por senha. Abra-o no KillerPDF para digitar a senha.",
            "Não foi possível visualizar este PDF."],
        ["ru-RU"] = ["По ширине", "По странице", "Уменьшить", "Увеличить", "Открыть в KillerPDF",
            "Этот PDF защищен паролем. Откройте его в KillerPDF, чтобы ввести пароль.",
            "Не удалось показать предварительный просмотр этого PDF."],
        ["sl-SI"] = ["Prilagodi širini", "Prilagodi strani", "Pomanjšaj", "Povečaj", "Odpri v KillerPDF",
            "Ta PDF je zaščiten z geslom. Odprite ga v KillerPDF, da vnesete geslo.", "Predogleda tega PDF-ja ni bilo mogoče prikazati."],
        ["sv-SE"] = ["Anpassa till bredd", "Anpassa till sida", "Zooma ut", "Zooma in", "Öppna i KillerPDF",
            "Den här PDF-filen är lösenordsskyddad. Öppna den i KillerPDF för att ange lösenordet.",
            "Den här PDF-filen kunde inte förhandsgranskas."],
        ["tr-TR"] = ["Genişliğe sığdır", "Sayfaya sığdır", "Uzaklaştır", "Yakınlaştır", "KillerPDF'de aç",
            "Bu PDF parola korumalı. Parolayı girmek için KillerPDF'de açın.", "Bu PDF önizlenemedi."],
        ["uk-UA"] = ["За шириною", "За сторінкою", "Зменшити", "Збільшити", "Відкрити в KillerPDF",
            "Цей PDF захищено паролем. Відкрийте його в KillerPDF, щоб ввести пароль.",
            "Не вдалося показати попередній перегляд цього PDF."],
        ["vi-VN"] = ["Vừa chiều rộng", "Vừa trang", "Thu nhỏ", "Phóng to", "Mở trong KillerPDF",
            "PDF này được bảo vệ bằng mật khẩu. Hãy mở trong KillerPDF để nhập mật khẩu.", "Không thể xem trước PDF này."],
        ["zh-CN"] = ["适合宽度", "适合页面", "缩小", "放大", "在 KillerPDF 中打开",
            "此 PDF 受密码保护。请在 KillerPDF 中打开并输入密码。", "无法预览此 PDF。"],
        ["zh-TW"] = ["符合寬度", "符合頁面", "縮小", "放大", "在 KillerPDF 中開啟",
            "此 PDF 受密碼保護。請在 KillerPDF 中開啟並輸入密碼。", "無法預覽此 PDF。"],
    };

    private static readonly Dictionary<string, string[]> PageProblems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en-US"] = ["This page could not be displayed completely.", "Retry"],
        ["bn"] = ["এই পৃষ্ঠাটি সম্পূর্ণভাবে দেখানো যায়নি।", "আবার চেষ্টা করুন"],
        ["cs-CZ"] = ["Tuto stránku nelze zobrazit úplně.", "Zkusit znovu"],
        ["de-DE"] = ["Diese Seite konnte nicht vollständig angezeigt werden.", "Erneut versuchen"],
        ["es"] = ["No se pudo mostrar esta página por completo.", "Reintentar"],
        ["fr-FR"] = ["Cette page n’a pas pu être affichée entièrement.", "Réessayer"],
        ["hu-HU"] = ["Ez az oldal nem jeleníthető meg teljes egészében.", "Újra"],
        ["it-IT"] = ["Non è stato possibile visualizzare completamente questa pagina.", "Riprova"],
        ["ja-JP"] = ["このページを完全に表示できませんでした。", "再試行"],
        ["kk-KZ"] = ["Бұл бетті толық көрсету мүмкін болмады.", "Қайталау"],
        ["ko-KR"] = ["이 페이지를 완전히 표시할 수 없습니다.", "다시 시도"],
        ["nb-NO"] = ["Denne siden kunne ikke vises fullstendig.", "Prøv igjen"],
        ["pl-PL"] = ["Nie można wyświetlić tej strony w całości.", "Spróbuj ponownie"],
        ["pt-BR"] = ["Não foi possível exibir esta página por completo.", "Tentar novamente"],
        ["ru-RU"] = ["Не удалось полностью отобразить эту страницу.", "Повторить"],
        ["sl-SI"] = ["Te strani ni bilo mogoče prikazati v celoti.", "Poskusi znova"],
        ["sv-SE"] = ["Den här sidan kunde inte visas helt.", "Försök igen"],
        ["tr-TR"] = ["Bu sayfa tam olarak görüntülenemedi.", "Yeniden dene"],
        ["uk-UA"] = ["Не вдалося повністю відобразити цю сторінку.", "Повторити"],
        ["vi-VN"] = ["Không thể hiển thị đầy đủ trang này.", "Thử lại"],
        ["zh-CN"] = ["无法完整显示此页面。", "重试"],
        ["zh-TW"] = ["無法完整顯示此頁面。", "重試"]
    };

    private static readonly string Language = Resolve(CultureInfo.CurrentUICulture);
    private static readonly string[] Current = Tables[Language];

    internal static string Get(Key key) => key >= Key.Incomplete
        ? PageProblems[Language][(int)key - (int)Key.Incomplete] : Current[(int)key];

    internal static string Resolve(CultureInfo culture)
    {
        string name = culture.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-TW" : "zh-CN";
        if (Tables.ContainsKey(name)) return name;
        string language = culture.TwoLetterISOLanguageName;
        foreach (string candidate in Tables.Keys)
            if (candidate.Equals(language, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
                return candidate;
        return "en-US";
    }
}
