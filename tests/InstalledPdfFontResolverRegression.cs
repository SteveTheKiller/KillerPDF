using KillerPdf.Engine.Fonts;
using KillerPDF.Services;

int failures = 0;
foreach (var (name, family, bold, italic) in new[]
{
    ("Helvetica", "Arial", false, false),
    ("Helvetica-Bold", "Arial", true, false),
    ("Helvetica-Oblique", "Arial", false, true),
    ("Helvetica-BoldOblique", "Arial", true, true),
    ("Times-Roman", "Times New Roman", false, false),
    ("Times-Bold", "Times New Roman", true, false),
    ("Times-Italic", "Times New Roman", false, true),
    ("Times-BoldItalic", "Times New Roman", true, true),
    ("TimesNewRomanPS-BdMT", "Times New Roman", true, false)
})
    Check($"Standard alias {name} preserves installed family and style", () =>
    {
        InstalledFontCatalog.Faces[family] = [7];
        Require(new InstalledPdfFontResolver().Resolve(Request(name))?[0] == 7);
        Require(InstalledFontCatalog.Calls.Last() == family);
        Require(InstalledFontCatalog.Styles.Last() == (bold, italic));
    });
Check("Installed Helvetica takes precedence over Arial", () =>
{
    InstalledFontCatalog.Faces["Helvetica"] = [8];
    InstalledFontCatalog.Faces["Arial"] = [7];
    Require(new InstalledPdfFontResolver().Resolve(Request("Helvetica"))?[0] == 8);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["Helvetica"]));
});
Check("Missing standard aliases retain bundled fallback", () =>
{
    Require(new InstalledPdfFontResolver().Resolve(Request("Times-Roman")) is null);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["Times Roman", "Times New Roman"]));
});
Check("Distinct Times families do not use the standard alias", () =>
{
    InstalledFontCatalog.Faces["Times New Roman"] = [7];
    Require(new InstalledPdfFontResolver().Resolve(Request("TimesCustom")) is null);
});
foreach (var (name, bold, italic) in new[]
{
    ("Courier", false, false), ("Courier-Bold", true, false),
    ("Courier-Oblique", false, true), ("Courier-BoldOblique", true, true)
})
    Check($"Missing {name} uses matching Courier New style", () =>
    {
        InstalledFontCatalog.Faces["Courier New"] = [3];
        Require(new InstalledPdfFontResolver().Resolve(Request(name))?[0] == 3);
        Require(InstalledFontCatalog.Calls.SequenceEqual(["Courier", "Courier New"]));
        Require(InstalledFontCatalog.Styles.All(style => style == (bold, italic)));
    });
Check("Installed Courier takes precedence over Courier New", () =>
{
    InstalledFontCatalog.Faces["Courier"] = [4];
    InstalledFontCatalog.Faces["Courier New"] = [3];
    Require(new InstalledPdfFontResolver().Resolve(Request("Courier"))?[0] == 4);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["Courier"]));
});
foreach (var (name, bold, italic) in new[]
{
    ("CourierNew", false, false), ("CourierNewPSMT", false, false),
    ("CourierNewPS-BoldMT", true, false), ("CourierNewPS-ItalicMT", false, true),
    ("CourierNewPS-BoldItalicMT", true, true), ("couriernew", false, false),
    ("CourierNewPS-BdMT", true, false), ("ABCDEF+CourierNew", false, false)
})
    Check($"PostScript alias {name} resolves to the matching Courier New face", () =>
    {
        InstalledFontCatalog.Faces["Courier New"] = [3];
        Require(new InstalledPdfFontResolver().Resolve(Request(name))?[0] == 3);
        Require(InstalledFontCatalog.Calls.Last() == "Courier New");
        Require(InstalledFontCatalog.Styles.Last() == (bold, italic));
    });
Check("An installed CourierNew family retains precedence", () =>
{
    InstalledFontCatalog.Faces["CourierNew"] = [4];
    InstalledFontCatalog.Faces["Courier New"] = [3];
    Require(new InstalledPdfFontResolver().Resolve(Request("CourierNew"))?[0] == 4);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["CourierNew"]));
});
Check("Missing Courier and Courier New retain bundled fallback", () =>
{
    Require(new InstalledPdfFontResolver().Resolve(Request("Courier")) is null);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["Courier", "Courier New"]));
});
Check("Distinct Courier families do not use the standard-font alias", () =>
{
    InstalledFontCatalog.Faces["Courier New"] = [3];
    Require(new InstalledPdfFontResolver().Resolve(Request("CourierCustom")) is null);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["CourierCustom"]));
});
Check("Installed requested font takes precedence", () =>
{
    InstalledFontCatalog.Faces["NotoColorEmoji"] = [1];
    InstalledFontCatalog.Faces["Segoe UI Emoji"] = [2];
    Require(new InstalledPdfFontResolver().Resolve(Request("NotoColorEmoji"))![0] == 1);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["NotoColorEmoji"]));
});
Check("Missing emoji font uses installed fallback", () =>
{
    InstalledFontCatalog.Faces["Segoe UI Emoji"] = [2];
    Require(new InstalledPdfFontResolver().Resolve(Request("NotoColorEmoji"))?[0] == 2);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["NotoColorEmoji", "Segoe UI Emoji"]));
});
Check("Unrelated fonts do not use emoji fallback", () =>
{
    InstalledFontCatalog.Faces["Segoe UI Emoji"] = [2];
    Require(new InstalledPdfFontResolver().Resolve(Request("MissingSans")) is null);
    Require(InstalledFontCatalog.Calls.SequenceEqual(["MissingSans"]));
});
Check("Unavailable fallback remains unavailable", () =>
{
    Require(new InstalledPdfFontResolver().Resolve(Request("NotoColorEmoji")) is null);
});
Check("Resolved fallback is cached", () =>
{
    InstalledFontCatalog.Faces["Segoe UI Emoji"] = [2];
    var resolver = new InstalledPdfFontResolver();
    byte[]? first = resolver.Resolve(Request("NotoColorEmoji"));
    Require(first is not null);
    Require(ReferenceEquals(first, resolver.Resolve(Request("NotoColorEmoji"))));
    Require(InstalledFontCatalog.Calls.Count == 2);
});
return failures == 0 ? 0 : 1;

void Check(string name, Action test)
{
    InstalledFontCatalog.Faces.Clear();
    InstalledFontCatalog.Calls.Clear();
    InstalledFontCatalog.Styles.Clear();
    try { test(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL: {name}: {exception.Message}"); }
}
static void Require(bool condition)
{
    if (!condition) throw new InvalidOperationException("Unexpected font resolution.");
}
static PdfFontRequest Request(string name) => new(name, "Adobe", "Identity", false);

namespace KillerPDF.Services
{
    internal static class InstalledFontCatalog
    {
        internal static readonly Dictionary<string, byte[]> Faces = new(StringComparer.OrdinalIgnoreCase);
        internal static readonly List<string> Calls = [];
        internal static readonly List<(bool Bold, bool Italic)> Styles = [];
        internal static byte[]? FaceBytes(string family, bool bold, bool italic)
        {
            Calls.Add(family);
            Styles.Add((bold, italic));
            return Faces.GetValueOrDefault(family);
        }
    }
}
