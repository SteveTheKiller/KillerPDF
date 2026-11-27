using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// One action shows one icon on every surface (toolbar, overflow, page menu, cards, sidebar menus), and an icon never
// depicts a different action. The menus need a window to build, so each surface's own source line is read and its
// glyph compared with the surface it must match; no codepoint is written twice here.
public sealed class MenuIconConsistencyTests
{
    private static string Source(string folder, string file) => ShellSource.Read(folder, file);

    private static string Toolbar => Source("Shell", "ToolbarLayout.cs");
    private static string PageMenu => Source("Shell", "ContextMenu.cs");
    private static string PageCard => Source("Shell", "PageCard.cs");
    private static string MarkupCard => Source("Shell", "AnnotationCard.cs");
    private static string OcrMenu => Source("Shell", "Ocr.cs");
    private static string Window => Source("Shell", "MainWindow.xaml");

    // The glyph on each line that holds the anchor (an escape or a literal private-use character), or "" when that
    // line carries none. span reads that many lines after the anchor's line too.
    private static List<string> Glyphs(string text, string anchor, int span = 0)
    {
        var lines = text.Split('\n');
        var found = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(anchor, StringComparison.Ordinal)) continue;
            string glyph = "";
            for (int j = i; j <= Math.Min(lines.Length - 1, i + span) && glyph.Length == 0; j++)
                glyph = FirstGlyph(lines[j]);
            found.Add(glyph);
        }
        Assert.True(found.Count > 0, "No line carries " + anchor);
        return found;
    }

    private static string FirstGlyph(string line)
    {
        var escape = Regex.Match(line, @"\\u([0-9A-Fa-f]{4})");
        var literal = Regex.Match(line, "[-]");
        if (escape.Success && (!literal.Success || escape.Index < literal.Index)) return char.ConvertFromUtf32(Convert.ToInt32(escape.Groups[1].Value, 16));
        return literal.Success ? literal.Value : "";
    }

    // The one glyph an action shows in a file: it may appear more than once (a card's spare tile repeats a menu row),
    // but always as the same glyph.
    private static string Only(string text, string anchor, int span = 0)
    {
        var all = Glyphs(text, anchor, span);
        Assert.Single(all.Distinct());
        return all[0];
    }

    // The overflow row in the window's XAML: the first glyph written after the button's name.
    private static string OverflowGlyph(string buttonName)
    {
        string xaml = Window;
        int at = xaml.IndexOf($"x:Name=\"{buttonName}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "Missing " + buttonName);
        var match = Regex.Match(xaml[at..], "Text=\"&#x([0-9A-Fa-f]{4});\"");
        Assert.True(match.Success, "No glyph after " + buttonName);
        return char.ConvertFromUtf32(Convert.ToInt32(match.Groups[1].Value, 16));
    }

    private static string Show(string glyph) => glyph.Length == 0 ? "(none)" : "U+" + char.ConvertToUtf32(glyph, 0).ToString("X4");

    private static void AllShow(string expected, string what, params (string Surface, List<string> Glyphs)[] surfaces)
    {
        Assert.True(expected.Length > 0, what + ": the reference surface has no icon");
        foreach (var (surface, glyphs) in surfaces)
            foreach (string glyph in glyphs)
                Assert.True(glyph == expected, $"{what} on {surface} shows {Show(glyph)} but its toolbar or reference surface shows {Show(expected)}");
    }

    [Fact]
    public void StampPagesShowsTheStampTool()
        => AllShow(Only(Toolbar, "\"Str_Lbl_Stamp\""), "Stamp Pages",
            ("page menu", Glyphs(PageMenu, "Loc(\"Str_Ctx_StampPages\")")),
            ("page card", Glyphs(PageCard, "Loc(\"Str_Ctx_StampPages\")")),
            ("overflow", [OverflowGlyph("MiStamp")]));

    [Fact]
    public void RedactSelectedTextShowsTheRedactTool()
        => AllShow(Only(Toolbar, "\"Str_Lbl_Redact\""), "Redact",
            ("page menu text strip", Glyphs(PageMenu, "Loc(\"Str_Lbl_Redact\")")),
            ("overflow", [OverflowGlyph("MiRedact")]));

    [Fact]
    public void OcrPageShowsTheOcrButtonOnEverySurface()
        => AllShow(Only(Toolbar, "\"Str_Lbl_Ocr\""), "OCR Page",
            ("page menu", Glyphs(PageMenu, "Loc(\"Str_Ctx_OcrPage\")")),
            ("page card", Glyphs(PageCard, "Loc(\"Str_Ctx_OcrPage\")")),
            ("OCR dropdown", Glyphs(OcrMenu, "Loc(\"Str_Ctx_OcrPage\")")));

    [Fact]
    public void ExtractPagesShowsTheExtractToolOnTheCard()
        => AllShow(Only(Toolbar, "\"Str_Lbl_Extract\""), "Extract Page(s)",
            ("page card", Glyphs(PageCard, "Loc(\"Str_Ctx_ExtractPages\")")),
            ("overflow", [OverflowGlyph("MiExtract")]));

    [Fact]
    public void InsertPagesOnTheCardShowsTheToolbarMergeGlyph()
        // Insert Pages (#529) took the Merge tile's place and still merges, at the page you pick.
        => AllShow(Only(Toolbar, "\"Str_Lbl_Merge\""), "Merge",
            ("page card", Glyphs(PageCard, "Loc(\"Str_Ctx_InsertPages\")")),
            ("overflow", [OverflowGlyph("MiMerge")]));

    [Fact]
    public void InsertBlankPageOnTheCardShowsTheBlankPageGlyph()
        => AllShow(Only(PageMenu, "Loc(\"Str_Ctx_AddBlankPage\")"), "Insert blank page",
            ("page card", Glyphs(PageCard, "Loc(\"Str_Ctx_InsertBlank\")")),
            ("toolbar New", [Only(Toolbar, "\"Str_Lbl_New\"")]));

    [Fact]
    public void EditParagraphShowsOneGlyphOnTheMenuAndTheCard()
        => AllShow(Glyphs(PageMenu, "Loc(\"Str_Paragraph_Title\")")[0], "Edit paragraph",
            ("page menu", Glyphs(PageMenu, "Loc(\"Str_Paragraph_Title\")")),
            ("page card", Glyphs(PageCard, "Loc(\"Str_Paragraph_Title\")")));

    [Fact]
    public void RasterizeRegionDoesNotShareItsGlyphWithExportTable()
    {
        AllShow(Glyphs(PageMenu, "Loc(\"Str_Pages_RasterizeRegion\")")[0], "Rasterize region",
            ("page menu", Glyphs(PageMenu, "Loc(\"Str_Pages_RasterizeRegion\")")),
            ("page card", Glyphs(PageCard, "Loc(\"Str_Pages_RasterizeRegion\")")));
        string rasterize = Only(PageCard, "Loc(\"Str_Pages_RasterizeRegion\")");
        foreach (string table in Glyphs(PageMenu, "Loc(\"Str_TableExport_Title\")").Concat(Glyphs(PageCard, "Loc(\"Str_TableExport_Title\")")))
            Assert.NotEqual(table, rasterize);
    }

    [Fact]
    public void GrayAreaMenuRowsCarryTheGlyphTheirSiblingsShow()
    {
        string divide = Only(PageCard, "Loc(\"Str_Pages_DivideSpreads\")");
        AllShow(divide, "Divide spreads", ("page menu", Glyphs(PageMenu, "Loc(\"Str_Pages_DivideSpreads\")")));
        AllShow(Glyphs(PageMenu, "Loc(\"Str_Ctx_Paste\")")[0], "Paste",
            ("page menu", Glyphs(PageMenu, "Loc(\"Str_Ctx_Paste\")")),
            ("markup card", Glyphs(MarkupCard, "Loc(\"Str_Ctx_Paste\")")));
        AllShow(Glyphs(PageMenu, "Loc(\"Str_Ctx_SelectAll\")")[0], "Select All", ("page menu", Glyphs(PageMenu, "Loc(\"Str_Ctx_SelectAll\")")));
        Assert.True(Glyphs(PageMenu, "Loc(\"Str_Ctx_FullScreen\")")[0].Length > 0, "Full Screen shows no icon");
        Assert.True(Only(PageMenu, "Loc(\"Str_View_BookMode\")").Length > 0, "Book layout shows no icon");
    }

    [Fact]
    public void StraightenShowsOneGlyphThatIsNotTheRedoArrow()
    {
        string redo = Only(Toolbar, "\"Str_Ctx_Redo\"");
        var surfaces = new (string Surface, List<string> Glyphs)[]
        {
            ("page menu", Glyphs(PageMenu, "Loc(\"Str_Pages_Straighten\")")),
            ("page card", Glyphs(PageCard, "Loc(\"Str_Pages_Straighten\")")),
        };
        AllShow(surfaces[1].Glyphs[0], "Straighten", surfaces);
        foreach (var (surface, glyphs) in surfaces)
            foreach (string glyph in glyphs)
                Assert.True(glyph != redo, $"Straighten on {surface} shows the Redo arrow {Show(glyph)}");
    }

    [Fact]
    public void UngroupAndUnpairKeepTheirOwnPictures()
    {
        // Ungroup reuses the Group glyph (the two never share a card); Unpair is the minus Remove from group uses.
        AllShow(Only(MarkupCard, "Loc(\"Str_Ctx_Group\")"), "Ungroup", ("markup card", Glyphs(MarkupCard, "Loc(\"Str_Ctx_Ungroup\")")));
        AllShow(Only(MarkupCard, "Loc(\"Str_Ctx_RemoveFromGroup\")"), "Unpair", ("markup card", Glyphs(MarkupCard, "Loc(\"Str_Ctx_Unpair\")")));
    }
}
