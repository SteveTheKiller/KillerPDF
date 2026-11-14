using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeDiagnosticsTests
{
    // Stand-in wording, so the rows show which localized key and arguments each line uses.
    private static readonly Dictionary<string, string> Wording = new()
    {
        ["Str_Epub_Diagnostics_Chapter"] = "Chapter {0}",
        ["Str_Epub_Diagnostics_ChapterRefused"] = "Not shown: {0}",
        ["Str_Epub_Diagnostics_ItemOmitted"] = "Left out: {0}",
        ["Str_Epub_Diagnostics_Ignored"] = "Ignored style rules: {0}",
        ["Str_Epub_Diagnostics_Reason"] = "Why: {0}",
        ["Str_Epub_Reason_Scripting"] = "scripts",
        ["Str_Epub_Reason_UnsupportedImageFormat"] = "image format",
        ["Str_Epub_Reason_MissingGlyph"] = "missing glyph"
    };

    [Fact]
    public void AChapterThatKeepsItsTextIsNotSaidToHaveLeftSomethingOut()
    {
        // U+2720 (the Maltese cross Ulysses prints) is in no font the test layout has, so the chapter shows a
        // missing-glyph box and keeps everything else; the row names the reason without claiming an item was left out.
        using var owner = NativeWorkingDocument.OpenEpub(Book("<p>A ✠ B</p>"),
            NativeOpenTests.Options() with { IsolateChapterFailures = true });
        var row = Assert.Single(NativeDocumentDiagnostics.Build(owner, key => Wording[key]));
        Assert.Equal("Chapter 1", row.Heading);
        Assert.Equal(new[] { "Why: missing glyph" }, row.Lines);
        Assert.Contains("U+2720", NativeDocumentDiagnostics.Report([row]));
    }

    [Fact]
    public async Task TheDiagnosticsSayTheBookIsStillBeingCountedUntilItsLastPageIsKnown()
    {
        using var gate = new ManualResetEventSlim();
        using var owner = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(),
            NativeProgressiveOpenTests.Gated(gate), inBackground: true);
        static string Loc(string key) => key == "Str_Epub_Diagnostics_Counting" ? "counting {0} of {1}" : key;

        string? counting = NativeDocumentDiagnostics.CountingNote(owner, Loc);
        gate.Set();
        await owner.Pagination.WaitAsync(TimeSpan.FromSeconds(20));

        // Chapter 0 of 4 is paginated when the open returns; once the last page is known the note goes.
        Assert.Equal("counting 1 of 4", counting);
        Assert.Null(NativeDocumentDiagnostics.CountingNote(owner, Loc));
    }

    [Fact]
    public void EveryOmissionReasonHasLocalizedWordingInEveryLocale()
    {
        // Each engine reason maps to its own key, present and non-empty in all 20 string files.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Strings", "en-US.xaml"))) root = root.Parent;
        Assert.NotNull(root);
        var files = Directory.GetFiles(Path.Combine(root.FullName, "Strings"), "*.xaml");
        Assert.Equal(20, files.Length);
        foreach (var reason in Enum.GetValues<KillerPdf.Engine.Formats.Epub.EpubOmissionReason>())
        {
            string key = NativeDocumentDiagnostics.ReasonKey(reason);
            Assert.Equal("Str_Epub_Reason_" + reason, key);
            foreach (string file in files)
                Assert.Matches("x:Key=\"" + key + "\">[^<]+</sys:String>", File.ReadAllText(file));
        }
    }

    [Fact]
    public void DiagnosticsListWhatEachChapterLostInReadingOrder()
    {
        // Chapter 1 keeps its text but loses a GIF, a format the reader cannot show; chapter 2 uses
        // scripting, which stays a chapter refusal; chapter 3 lays out with one invalid color
        // declaration ignored, as CSS requires; chapter 4 lays out whole.
        using var owner = NativeWorkingDocument.OpenEpub(Book(
            "<p>A<img src='pic.gif' alt=''/></p>", "<p onclick='go()'>A</p>",
            "<p style='color: nonsense'>A</p>", "<p>A</p>"),
            NativeOpenTests.Options() with { IsolateChapterFailures = true });

        Assert.True(owner.HasOmissions);
        var rows = NativeDocumentDiagnostics.Build(owner, key => Wording[key]);

        Assert.Equal(new[] { "Chapter 1", "Chapter 2", "Chapter 3" }, rows.Select(row => row.Heading));
        Assert.Equal(new[] { "Ignored style rules: 1" }, rows[2].Lines);
        Assert.Equal(2, rows[0].Lines.Count);
        // The engine names the chapter in Href and the omitted image in its message.
        Assert.Equal("Left out: EPUB/c1.xhtml", rows[0].Lines[0]);
        // The panel shows the localized reason; the engine's English message stays in the copied report.
        // The omitted item's path sits next to its localized reason.
        Assert.Equal("Why: image format (EPUB/pic.gif)", rows[0].Lines[1]);
        Assert.Equal(2, rows[1].Lines.Count);
        Assert.Equal("Not shown: EPUB/c2.xhtml", rows[1].Lines[0]);
        Assert.Equal("Why: scripts", rows[1].Lines[1]);
        string report = NativeDocumentDiagnostics.Report(rows);
        Assert.Contains("format is not supported: EPUB/pic.gif", report);
        Assert.Contains("Why: image format", report);
        Assert.Contains("Scripted EPUB XHTML text is unsupported.", report);
    }

    [Fact]
    public void AWholeBookHasNoOmissionsAndNoRows()
    {
        using var owner = NativeWorkingDocument.OpenEpub(Book("<p>A</p>"),
            NativeOpenTests.Options() with { IsolateChapterFailures = true });
        Assert.False(owner.HasOmissions);
        Assert.Empty(NativeDocumentDiagnostics.Build(owner, key => Wording[key]));
    }

    [Fact]
    public void IgnoredStyleRulesAloneAreListedWithoutCountingAsOmissions()
    {
        using var owner = NativeWorkingDocument.OpenEpub(Book("<p style='color: nonsense'>A</p>"),
            NativeOpenTests.Options() with { IsolateChapterFailures = true });
        Assert.False(owner.HasOmissions);
        var row = Assert.Single(NativeDocumentDiagnostics.Build(owner, key => Wording[key]));
        Assert.Equal("Chapter 1", row.Heading);
        Assert.Equal(new[] { "Ignored style rules: 1" }, row.Lines);
    }

    [Fact]
    public void RowsFollowReadingOrderSoANonLinearChapterComesLast()
    {
        // Chapter 1 is linear="no", so the reader appends it after chapters 2 and 3. It loses a GIF;
        // chapter 2 uses scripting and is refused. The rows follow the pages, keeping spine numbers.
        using var owner = NativeWorkingDocument.OpenEpub(Book([0],
            "<p>A<img src='pic.gif' alt=''/></p>", "<p onclick='go()'>A</p>", "<p>A</p>"),
            NativeOpenTests.Options() with { IsolateChapterFailures = true });
        var rows = NativeDocumentDiagnostics.Build(owner, key => Wording[key]);
        Assert.Equal(new[] { "Chapter 2", "Chapter 1" }, rows.Select(row => row.Heading));
        Assert.Equal("Left out: EPUB/c1.xhtml", rows[1].Lines[0]);
    }

    internal static byte[] Book(params string[] chapters) => Book([], chapters);

    internal static byte[] Book(int[] nonLinear, params string[] chapters)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            string items = string.Concat(chapters.Select((_, index) =>
                $"<item id=\"c{index + 1}\" href=\"c{index + 1}.xhtml\" media-type=\"application/xhtml+xml\"/>"));
            string spine = string.Concat(chapters.Select((_, index) => nonLinear.Contains(index)
                ? $"<itemref idref=\"c{index + 1}\" linear=\"no\"/>" : $"<itemref idref=\"c{index + 1}\"/>"));
            Add("EPUB/book.opf", $"""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:diagnostics</dc:identifier><dc:title>Diagnostics</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-08T00:00:00Z</meta></metadata>
                  <manifest>{items}<item id="pic" href="pic.gif" media-type="image/gif"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine>{spine}</spine>
                </package>
                """);
            for (int index = 0; index < chapters.Length; index++)
                Add($"EPUB/c{index + 1}.xhtml", $"""
                    <html xmlns="http://www.w3.org/1999/xhtml"><head><title>C</title></head><body>{chapters[index]}</body></html>
                    """);
            Add("EPUB/pic.gif", "GIF89a");
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="c1.xhtml">C</a></li></ol></nav></body></html>
                """);
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
