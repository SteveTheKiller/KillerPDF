using KillerPdf.Engine.Fonts;
using KillerPDF.Services;

int failures = 0;
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
