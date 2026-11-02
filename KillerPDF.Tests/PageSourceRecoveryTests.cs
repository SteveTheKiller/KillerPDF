using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Merge and the form handlers read pages from a file the viewer already shows, so they read it the
// way the viewer does (TX-005, TX-012).
public sealed class PageSourceRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-page-source-" + Guid.NewGuid().ToString("N"));

    public PageSourceRecoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ReadPageInformation_ReadsAFileOnlyRecoveryCanOpen()
    {
        string path = Write("damaged.pdf", DamagedTwoPagePdf());

        IReadOnlyList<PdfPageInformation> pages = PdfEngineIntegration.ReadPageInformation(path, null);

        Assert.Equal(2, pages.Count);
        Assert.Equal(200, pages[0].Width);
    }

    [Fact]
    public void ValidateDocument_AcceptsAFileOnlyRecoveryCanOpen()
    {
        string path = Write("damaged.pdf", DamagedTwoPagePdf());

        PdfEngineIntegration.ValidateDocument(path);
    }

    [Fact]
    public void ValidateDocument_StillRefusesAFileNoReaderCanOpen()
    {
        string path = Write("junk.pdf", Encoding.ASCII.GetBytes("This is not a PDF."));

        Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.ValidateDocument(path));
    }

    // Merge offers a repair only when the file fails this check, so a file that passes it must also import (F3-005).
    [Fact]
    public void ValidateImportable_RefusesARecoveredFileTheImportWouldRefuse()
    {
        string path = Write("no-mediabox.pdf", RecoveredPageWithoutMediaBox());
        string target = Write("target.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        PdfEngineIntegration.ValidateDocument(path);
        Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.InsertDocuments(target,
            [new PdfEngineIntegration.ImportedDocument(path, [0])], 1));

        Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.ValidateImportable(path));
    }

    [Fact]
    public void ValidateImportable_AcceptsARecoveredFileTheImportAccepts()
    {
        PdfEngineIntegration.ValidateImportable(Write("damaged.pdf", DamagedTwoPagePdf()));
    }

    [Fact]
    public void ValidateImportable_AcceptsAFileTheStrictParserOpens()
    {
        PdfEngineIntegration.ValidateImportable(Write("valid.pdf", new PdfDocumentBuilder().AddBlankPage().Build()));
    }

    [Fact]
    public void ValidateImportable_StillRefusesAFileNoReaderCanOpen()
    {
        string path = Write("junk.pdf", Encoding.ASCII.GetBytes("This is not a PDF."));

        Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.ValidateImportable(path));
    }

    // One page with no /MediaBox anywhere, which the viewer shows as US Letter and the page copy refuses,
    // behind a startxref that points past the end so only recovery opens it.
    private static byte[] RecoveredPageWithoutMediaBox()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R >>"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        for (int index = 0; index < objects.Length; index++)
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        text.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n999999\n%%EOF\n");
        byte[] bytes = Encoding.Latin1.GetBytes(text.ToString());
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(bytes));
        return bytes;
    }

    // A valid two-page file whose startxref points past the end, which the strict parser refuses
    // and compatibility recovery rebuilds. The asserts keep the fixture honest.
    private static byte[] DamagedTwoPagePdf()
    {
        byte[] valid = new PdfDocumentBuilder().AddBlankPage(200, 300).AddBlankPage(300, 200).Build();
        string text = Encoding.Latin1.GetString(valid);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int numberStart = text.IndexOf('\n', marker) + 1;
        int numberEnd = text.IndexOf('\n', numberStart);
        byte[] damaged = Encoding.Latin1.GetBytes(
            text[..numberStart] + (valid.Length + 5000).ToString() + text[numberEnd..]);
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(damaged));
        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.OpenWithCompatibilityRecovery(damaged)).Count);
        return damaged;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

// The Attachments dialog addresses each file by its name-tree key, so a row removes or edits exactly that
// file even when keys and listed names cross, repeat or differ only in case (F3-002).
public sealed class AttachmentEditKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-attachment-keys-" + Guid.NewGuid().ToString("N"));

    public AttachmentEditKeyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string, int> EveryRowOfEveryShape()
    {
        var data = new TheoryData<string, int>();
        foreach (string shape in new[] { "cross", "duplicate", "case" })
            foreach (int row in new[] { 0, 1 })
                data.Add(shape, row);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRowOfEveryShape))]
    public void ApplyAttachmentEdits_RemovesExactlyTheRemovedRow(string shape, int removedRow)
    {
        string path = Write(Shape(shape));
        var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);
        Assert.Equal(2, items.Count);

        PdfEngineIntegration.ApplyAttachmentEdits(path,
            [.. items.Select((item, row) => item with { Remove = row == removedRow })]);

        PdfAttachmentInfo kept = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path))));
        Assert.Equal(items[1 - removedRow].Key, kept.NameTreeKey);
        Assert.Equal(items[1 - removedRow].FileName, kept.FileName);
        Assert.Equal(1 - removedRow == 0 ? "PAYLOAD1" : "PAYLOAD2", Encoding.ASCII.GetString(kept.Data.Span));
    }

    [Theory]
    [MemberData(nameof(EveryRowOfEveryShape))]
    public void ApplyAttachmentEdits_ChangesOnlyTheDescriptionOfTheEditedRow(string shape, int editedRow)
    {
        string path = Write(Shape(shape));
        var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);

        PdfEngineIntegration.ApplyAttachmentEdits(path,
            [.. items.Select((item, row) => row == editedRow ? item with { Description = "Edited" } : item)]);

        // The edited file is rewritten under its own key, and the order of entries in the tree is not part of the result.
        PdfAttachmentInfo[] saved = [.. items.Select(item => PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path)))
            .Single(file => file.NameTreeKey == item.Key))];
        Assert.Equal(items.Select(item => item.FileName), saved.Select(item => item.FileName));
        Assert.Equal("Edited", saved[editedRow].Description);
        Assert.NotEqual("Edited", saved[1 - editedRow].Description);
        Assert.Equal(["PAYLOAD1", "PAYLOAD2"], saved.Select(item => Encoding.ASCII.GetString(item.Data.Span)));
    }

    [Fact]
    public void ApplyAttachmentEdits_SwapsListedNamesWhenKeysDifferFromThem()
    {
        string path = Write(Shape("cross"));
        var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);

        PdfEngineIntegration.ApplyAttachmentEdits(path,
            [items[0] with { FileName = "Z" }, items[1] with { FileName = "Y" }]);

        PdfAttachmentInfo[] saved = [.. PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path)))
            .OrderBy(item => item.NameTreeKey, StringComparer.Ordinal)];
        Assert.Equal(["Y", "Z"], saved.Select(item => item.NameTreeKey));
        Assert.Equal(["Y", "Z"], saved.Select(item => item.FileName));
        Assert.Equal(["PAYLOAD2", "PAYLOAD1"], saved.Select(item => Encoding.ASCII.GetString(item.Data.Span)));
    }

    [Fact]
    public void ApplyAttachmentEdits_RefusesANewNameAnotherRowAlreadyHas()
    {
        string path = Write(Shape("cross"));
        byte[] before = File.ReadAllBytes(path);
        var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);

        Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.ApplyAttachmentEdits(path,
            [items[0] with { FileName = "z" }, items[1]]));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ApplyAttachmentEdits_RefusesRowsWhoseFilesChangedSinceTheyWereRead()
    {
        string path = Write(Shape("duplicate"));
        var items = PdfEngineIntegration.ReadAttachmentEditorItems(path, null);
        File.WriteAllBytes(path, Shape("cross"));

        Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.ApplyAttachmentEdits(path, items));
    }

    [Fact]
    public void ApplyAttachmentEdits_MatchesARowWithoutAKeyByItsUniqueOriginalName()
    {
        string path = Write(Shape("cross"));

        PdfEngineIntegration.ApplyAttachmentEdits(path,
        [
            new("Y", "Y", null, "application/octet-stream", PdfAssociatedFileRelationship.Data, 8, Remove: true),
            new("Z", "Z", null, "application/octet-stream", PdfAssociatedFileRelationship.Data, 8)
        ]);

        PdfAttachmentInfo kept = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(File.ReadAllBytes(path))));
        Assert.Equal("PAYLOAD2", Encoding.ASCII.GetString(kept.Data.Span));
        Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.ApplyAttachmentEdits(Write(Shape("duplicate")),
        [
            new("dup.txt", "dup.txt", null, "application/octet-stream", PdfAssociatedFileRelationship.Data, 8, Remove: true),
            new("dup.txt", "dup.txt", null, "application/octet-stream", PdfAssociatedFileRelationship.Data, 8)
        ]));
    }

    [Fact]
    public void FindAttachmentEditorItem_PicksTheNamedFileAndRefusesAmbiguousNames()
    {
        var crossed = PdfEngineIntegration.ReadAttachmentEditorItems(Write(Shape("cross")), null);
        var repeated = PdfEngineIntegration.ReadAttachmentEditorItems(Write(Shape("duplicate")), null);
        var cased = PdfEngineIntegration.ReadAttachmentEditorItems(Write(Shape("case")), null);

        Assert.Equal("X", PdfEngineIntegration.FindAttachmentEditorItem(crossed, "Y", out bool crossedAmbiguous)?.Key);
        Assert.False(crossedAmbiguous);
        Assert.Null(PdfEngineIntegration.FindAttachmentEditorItem(repeated, "dup.txt", out bool repeatedAmbiguous));
        Assert.True(repeatedAmbiguous);
        Assert.Equal("a.txt", PdfEngineIntegration.FindAttachmentEditorItem(cased, "a.txt", out _)?.Key);
        Assert.Equal("A.txt", PdfEngineIntegration.FindAttachmentEditorItem(cased, "A.txt", out _)?.Key);
        Assert.Equal("k2", PdfEngineIntegration.FindAttachmentEditorItem(repeated, "k2", out _)?.Key);
        Assert.Null(PdfEngineIntegration.FindAttachmentEditorItem(repeated, "missing.txt", out bool missingAmbiguous));
        Assert.False(missingAmbiguous);
    }

    // Two files per shape. Keys are written in byte order, so row 0 holds PAYLOAD1 and row 1 PAYLOAD2.
    private static byte[] Shape(string shape) => shape switch
    {
        // Key X lists Y and key Y lists Z, so each file's key is the other file's listed name.
        "cross" => Keyed(("X", "Y", "PAYLOAD1"), ("Y", "Z", "PAYLOAD2")),
        "duplicate" => Keyed(("k1", "dup.txt", "PAYLOAD1"), ("k2", "dup.txt", "PAYLOAD2")),
        "case" => Keyed(("A.txt", "A.txt", "PAYLOAD1"), ("a.txt", "a.txt", "PAYLOAD2")),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static byte[] Keyed(params (string Key, string Listed, string Payload)[] items)
    {
        var objects = new List<string>
        {
            "",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"
        };
        var names = new StringBuilder();
        foreach ((string key, string listed, string payload) in items.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            int specification = objects.Count + 1;
            objects.Add($"<< /Type /Filespec /F ({listed}) /UF ({listed}) /EF << /F {specification + 1} 0 R >> >>");
            objects.Add($"<< /Type /EmbeddedFile /Length {payload.Length} >>\nstream\n{payload}\nendstream");
            names.Append($"({key}) {specification} 0 R ");
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [" + names + "] >> >> >>";
        var text = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private string Write(byte[] bytes)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

// Saving Document Info writes the whole descriptive block back, so a value the reader could not interpret
// must be carried over unless the user typed a replacement (F3-003), and unreadable XMP must not stop the
// save (F3-013).
public sealed class DocumentInfoPreservationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-docinfo-keep-" + Guid.NewGuid().ToString("N"));

    public DocumentInfoPreservationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private const string UnreadableInfo =
        "/Title (Real title) /Author (Original) /Subject <FEFFD800> /CreationDate (March 3 2021) /Trapped /Maybe /Producer (Acme)";

    [Fact]
    public void Read_ListsTheEntriesItCouldNotInterpret()
    {
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(RawPdf(UnreadableInfo)));

        Assert.Null(info.Subject);
        Assert.Null(info.CreationDate);
        Assert.Null(info.Trapped);
        Assert.Equal(["CreationDate", "Subject", "Trapped"], info.UnreadableKeys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void Read_ListsNothingWhenEveryEntryIsReadable()
    {
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(RawPdf(
            "/Title (Real title) /CreationDate (D:20210303120000Z) /Trapped /True")));

        Assert.Empty(info.UnreadableKeys);
    }

    [Theory]
    [InlineData("D:20210303120000+01", 1, 0)]
    [InlineData("D:20210303120000+01'", 1, 0)]
    [InlineData("D:20210303120000-05'30'", -5, -30)]
    public void Read_AcceptsADateWhoseOffsetHasNoMinutes(string text, int hours, int minutes)
    {
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(RawPdf($"/CreationDate ({text})")));

        Assert.Equal(new DateTimeOffset(2021, 3, 3, 12, 0, 0, new TimeSpan(hours, minutes, 0)), info.CreationDate);
        Assert.Empty(info.UnreadableKeys);
    }

    [Fact]
    public void ApplyDocumentMetadata_KeepsUnreadableEntriesTheUserDidNotTouch()
    {
        string path = Write(RawPdf(UnreadableInfo));
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));
        var edited = new PdfDocumentMetadata { Title = info.Title, Author = "Changed", Subject = "", Keywords = "", Creator = "", Producer = info.Producer };

        PdfEngineIntegration.ApplyDocumentMetadata(path, PdfEngineIntegration.CompleteDocumentMetadata(info, edited));

        PdfDictionary saved = InfoOf(File.ReadAllBytes(path));
        Assert.Equal("Changed", Text(saved, "Author"));
        Assert.Equal("Real title", Text(saved, "Title"));
        Assert.Equal("Acme", Text(saved, "Producer"));
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0xD8, 0x00 }, Assert.IsType<PdfString>(saved[new PdfName("Subject"u8)]).Bytes.ToArray());
        Assert.Equal("March 3 2021", Text(saved, "CreationDate"));
        Assert.Equal("Maybe", Assert.IsType<PdfName>(saved[new PdfName("Trapped"u8)]).ValueAsLatin1());
    }

    [Fact]
    public void ApplyDocumentMetadata_ReplacesAnUnreadableValueTheUserTypedOver()
    {
        string path = Write(RawPdf(UnreadableInfo));
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));
        var edited = new PdfDocumentMetadata { Title = info.Title, Author = info.Author, Subject = "Typed subject", Producer = info.Producer };

        PdfEngineIntegration.ApplyDocumentMetadata(path, PdfEngineIntegration.CompleteDocumentMetadata(info, edited));

        PdfDictionary saved = InfoOf(File.ReadAllBytes(path));
        Assert.Equal("Typed subject", Text(saved, "Subject"));
        Assert.Equal("March 3 2021", Text(saved, "CreationDate"));
    }

    [Fact]
    public void ApplyDocumentMetadata_KeepsALanguageTheWriterCannotValidate()
    {
        string path = Write(RawPdf("/Title (T)", catalogExtra: "/Lang (not_a_tag)"));
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));
        var edited = new PdfDocumentMetadata { Title = "T2", Producer = info.Producer };

        PdfEngineIntegration.ApplyDocumentMetadata(path, PdfEngineIntegration.CompleteDocumentMetadata(info, edited));

        PdfDocument saved = PdfDocument.Open(File.ReadAllBytes(path));
        Assert.Equal("T2", Text(InfoOf(File.ReadAllBytes(path)), "Title"));
        Assert.Equal("not_a_tag", PdfDocumentInformation.Read(saved).Language);
    }

    [Theory]
    [InlineData("this is <not xml")]
    [InlineData("")]
    [InlineData("<a/>")]
    public void ApplyDocumentMetadata_ReplacesAnXmpPacketItCannotUse(string packet)
    {
        string path = Write(RawPdf("/Title (Old)", metadata: packet));
        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));
        Assert.Equal("Old", info.Title);
        var edited = new PdfDocumentMetadata { Title = "New", Author = "Writer", Producer = info.Producer };

        PdfEngineIntegration.ApplyDocumentMetadata(path, PdfEngineIntegration.CompleteDocumentMetadata(info, edited));

        PdfDocumentInformation saved = PdfDocumentInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));
        Assert.Equal("New", saved.Title);
        Assert.Equal("Writer", saved.Author);
    }

    private static PdfDictionary InfoOf(byte[] bytes)
    {
        PdfDocument document = PdfDocument.Open(bytes);
        PdfObject value = document.Trailer[new PdfName("Info"u8)];
        return Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);
    }

    private static string Text(PdfDictionary info, string key)
    {
        byte[] bytes = Assert.IsType<PdfString>(info[new PdfName(Encoding.ASCII.GetBytes(key))]).Bytes.ToArray();
        return bytes is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2) : Encoding.Latin1.GetString(bytes);
    }

    // One blank page, an /Info with the given entries and, optionally, an XMP packet with the given text.
    private static byte[] RawPdf(string info, string catalogExtra = "", string? metadata = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalogExtra + (metadata is null ? "" : " /Metadata 5 0 R") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< " + info + " >>"
        };
        if (metadata is not null)
            objects.Add($"<< /Type /Metadata /Subtype /XML /Length {metadata.Length} >>\nstream\n{metadata}\nendstream");
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 4 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private string Write(byte[] bytes)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
