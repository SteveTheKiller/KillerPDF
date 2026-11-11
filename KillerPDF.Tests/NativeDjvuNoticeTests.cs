using System.IO;
using KillerPdf.Engine.Formats.Djvu;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A DjVu document uses the book notice bar and its diagnostics dialog. Document notices that mean something may be
// missing or wrong raise it at open; a page whose text could not all be read raises it the first time that text is
// read (select, search or copy). Notices that only describe what the decoder supports never raise it.
public sealed class NativeDjvuNoticeTests
{
    private static readonly Dictionary<string, string> Wording = new()
    {
        ["Str_Djvu_Diagnostics_Document"] = "Whole document",
        ["Str_Djvu_Diagnostics_Page"] = "Page {0}",
        ["Str_Djvu_Reason_IncludedComponentUnsupported"] = "shared parts missing",
        ["Str_Djvu_Reason_PageVersionUnsupported"] = "newer version",
        ["Str_Djvu_Reason_TextEncodingInvalid"] = "unreadable characters"
    };

    // 0x41 0xB8: an A and a lone byte that is not UTF-8, as the djvu2spec reference edition stores its copyright sign.
    private static byte[] PageWithUnreadableText() => NativeWorkingDocumentTests.DjvuPageWithText([0x41, 0xb8], zoned: true);

    [Fact]
    public void ReadingAPageWhoseTextIsNotUtf8RaisesTheNoticeOnceNamingThePage()
    {
        using var owner = NativeWorkingDocument.OpenDjvu(PageWithUnreadableText());
        int changes = 0;
        owner.NoticesChanged += () => changes++;
        // The Smmr mask's rendering notice only describes the decoder, so the open raises nothing.
        Assert.False(owner.HasOmissions);

        using (var lease = owner.CaptureReadRevision().Rent())
        {
            lease.ReadNativeTextPage(0);
            lease.ReadNativeTextPage(0);
        }

        Assert.True(owner.HasOmissions);
        var notice = Assert.Single(owner.DjvuNotices);
        Assert.Equal("TextEncodingInvalid", notice.Code);
        Assert.Equal(0, notice.PageIndex);
        Assert.Equal(1, changes);
    }

    // Search reads a page's text without its regions; that read reports the page too.
    [Fact]
    public void SearchingAPageWhoseTextIsNotUtf8RaisesTheNotice()
    {
        using var owner = NativeWorkingDocument.OpenDjvu(PageWithUnreadableText());
        int changes = 0;
        owner.NoticesChanged += () => changes++;

        using (var lease = owner.CaptureReadRevision().Rent())
            Assert.Equal("A�", lease.ReadNativeText(0));

        Assert.True(owner.HasOmissions);
        Assert.Equal(0, Assert.Single(owner.DjvuNotices).PageIndex);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ValidPageTextRaisesNothing()
    {
        using var owner = NativeWorkingDocument.OpenDjvu(NativeWorkingDocumentTests.DjvuPageWithText("AB"u8.ToArray(), zoned: true));
        int changes = 0;
        owner.NoticesChanged += () => changes++;

        using (var lease = owner.CaptureReadRevision().Rent()) lease.ReadNativeTextPage(0);

        Assert.False(owner.HasOmissions);
        Assert.Empty(owner.DjvuNotices);
        Assert.Equal(0, changes);
    }

    // INFO minor version 27 is newer than the format reference's 26: rendering may not be exact, so the open raises
    // the notice. The dialog lists the whole document first, then each page in page order.
    [Fact]
    public void ANewerPageVersionRaisesTheNoticeAtOpenAndTheDialogListsTheDocumentThenThePage()
    {
        byte[] source = PageWithUnreadableText();
        source[28] = 27;
        using var owner = NativeWorkingDocument.OpenDjvu(source);

        Assert.True(owner.HasOmissions);
        using (var lease = owner.CaptureReadRevision().Rent()) lease.ReadNativeTextPage(0);
        var rows = NativeDocumentDiagnostics.Build(owner, key => Wording[key]);

        Assert.Equal(["Whole document", "Page 1"], rows.Select(row => row.Heading));
        Assert.Equal(["newer version"], rows[0].Lines);
        Assert.Equal(["unreadable characters"], rows[1].Lines);
        // The copied report keeps the engine's English diagnosis after the localized reason.
        string report = NativeDocumentDiagnostics.Report(rows);
        Assert.Contains("newer version (Page metadata uses a version newer", report);
        Assert.Contains("unreadable characters (Page 1 text is not valid UTF-8.", report);
    }

    // The engine raises one notice per unknown chunk name. The dialog's line is the same sentence for each, so the
    // row shows it once; the copied report keeps one line per chunk with the name.
    [Fact]
    public void SeveralUnknownChunksShowTheirReasonOnceAndTheReportNamesEachChunk()
    {
        byte[] source = PageWithUnreadableText();
        byte[] extra = [.. DjvuChunk("Zqa1", [1, 2]), .. DjvuChunk("Zqb2", [1, 2]), .. DjvuChunk("Zqc3", [1, 2])];
        byte[] grown = [.. source, .. extra];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(grown.AsSpan(8),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(grown.AsSpan(8)) + extra.Length);
        using var owner = NativeWorkingDocument.OpenDjvu(grown);
        var wording = new Dictionary<string, string>(Wording) { ["Str_Djvu_Reason_UnrecognizedChunkSkipped"] = "unknown part skipped" };

        var rows = NativeDocumentDiagnostics.Build(owner, key => wording[key]);

        var document = Assert.Single(rows, row => row.Heading == "Whole document");
        Assert.Equal(["unknown part skipped"], document.Lines);
        string report = NativeDocumentDiagnostics.Report(rows);
        foreach (string name in new[] { "Zqa1", "Zqb2", "Zqc3" }) Assert.Contains($"'{name}'", report);
    }

    private static byte[] DjvuChunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. System.Text.Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }

    [Fact]
    public void TheBarAndDialogUseDocumentWordingForDjvuAndBookWordingForEpub()
    {
        using var djvu = NativeWorkingDocument.OpenDjvu(PageWithUnreadableText());
        using var epub = NativeWorkingDocument.OpenEpub(NativeDiagnosticsTests.Book("<p>A</p>"),
            NativeOpenTests.Options() with { IsolateChapterFailures = true });

        Assert.Equal(new NativeNoticeWording("Str_Djvu_Diagnostics_Notice", "Str_Djvu_Diagnostics_Title",
            "Str_Djvu_Diagnostics_Intro", "Str_Djvu_Diagnostics_None"), NativeDocumentDiagnostics.WordingFor(djvu.Format));
        Assert.Equal(new NativeNoticeWording("Str_Epub_Diagnostics_Notice", "Str_Epub_Diagnostics_Title",
            "Str_Epub_Diagnostics_Intro", "Str_Epub_Diagnostics_None"), NativeDocumentDiagnostics.WordingFor(epub.Format));
    }

    [Fact]
    public void EveryDjvuNoticeKeyHasWordingInEveryLocale()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Strings", "en-US.xaml"))) root = root.Parent;
        Assert.NotNull(root);
        var files = Directory.GetFiles(Path.Combine(root.FullName, "Strings"), "*.xaml");
        Assert.Equal(20, files.Length);
        var wording = NativeDocumentDiagnostics.WordingFor(PdfDocumentFormatDjvu);
        string[] keys =
        [
            wording.Notice, wording.Title, wording.Intro, wording.None, "Str_Djvu_Diagnostics_Document",
            "Str_Djvu_Diagnostics_Page", .. NativeDocumentDiagnostics.RaisingDjvuCodes.Select(NativeDocumentDiagnostics.DjvuReasonKey)
        ];
        Assert.Equal(10, keys.Distinct().Count());
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string key in keys)
                Assert.Matches("x:Key=\"" + key + "\">[^<]+</sys:String>", text);
            Assert.Matches("x:Key=\"Str_Djvu_Diagnostics_Page\">[^<]*\\{0\\}[^<]*</sys:String>", text);
        }
    }

    private const KillerPdf.Engine.Documents.PdfDocumentFormat PdfDocumentFormatDjvu =
        KillerPdf.Engine.Documents.PdfDocumentFormat.Djvu;
}
