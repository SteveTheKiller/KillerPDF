using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfObjectStreamRecoveryTests
{
    [Fact]
    public void RecoveryFindsCatalogWhenStartxrefSelectsRootlessCompanionStream()
    {
        string members = ObjectStream(4, "2 0 ", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        string source = Encoding.ASCII.GetString(Source(members));
        int companionOffset = source.IndexOf("xref\n", StringComparison.Ordinal);
        source = source.Insert(companionOffset,
            "6 0 obj << /Type /XRef /Size 8 /Index [7 1] /W [1 1 1] /Length 3 >> stream\n" +
            "\0\0\0\nendstream\nendobj\n");
        source = source.Replace("startxref\n0\n", $"startxref\n{companionOffset}\n", StringComparison.Ordinal);

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Encoding.ASCII.GetBytes(source));

        Assert.Single(PdfPageInformation.Read(recovered));
    }

    [Fact]
    public void RecoveryRebuildsCompressedPageTreeAfterBrokenXref()
    {
        byte[] bytes = Source(ObjectStream(4, "2 0 ", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>"));
        Assert.Throws<PdfSyntaxException>(() => PdfDocument.Open(bytes));

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.Single(PdfPageInformation.Read(recovered));
        Assert.IsType<PdfDictionary>(recovered.Resolve(2));
    }

    [Fact]
    public void RecoveryKeepsLaterDirectDefinitionAndOtherCompressedMembers()
    {
        string first = "<< /Type /Pages /Count 0 /Kids [] >> ";
        string stream = ObjectStream(4, $"2 0 5 {first.Length} ", first + "42");
        byte[] bytes = Source(stream + "2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n");

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.Single(PdfPageInformation.Read(recovered));
        Assert.Equal(42, Assert.IsType<PdfInteger>(recovered.Resolve(5)).Value);
    }

    [Fact]
    public void RecoveryKeepsLaterCompressedDefinition()
    {
        byte[] bytes = Source("2 0 obj null endobj\n" +
            ObjectStream(4, "2 0 ", "<< /Type /Pages /Count 1 /Kids [3 0 R] >>"));

        Assert.Single(PdfPageInformation.Read(PdfDocument.OpenWithCompatibilityRecovery(bytes)));
    }

    [Theory]
    [InlineData("2 0 2 3 ", "42 43", 2)]
    [InlineData("2 9 ", "42", 1)]
    [InlineData("2 0 5 0 ", "42", 2)]
    [InlineData("4 0 ", "42", 1)]
    [InlineData("2 0 ", "42", 1000001)]
    public void RecoveryDoesNotRegisterMalformedObjectStreams(string header, string body, int count)
    {
        byte[] bytes = Source(ObjectStream(4, header, body, count));

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.IsType<PdfNull>(recovered.Resolve(2));
    }

    [Fact]
    public void ABrokenObjectStreamIsReadOnceForAllItsEntries()
    {
        // 5,000 compressed entries point into one 8 MB object stream whose header is garbage.
        // Each lookup used to copy and parse the whole stream again.
        const int members = 5000;
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Add(string body) { offsets.Add(text.Length); text.Append(body); }
        Add("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        Add("2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj\n");
        Add("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >> endobj\n");
        string garbage = new('x', 8 * 1024 * 1024);
        Add($"4 0 obj << /Type /ObjStm /N {members} /First 0 /Length {garbage.Length} >> stream\n{garbage}\nendstream\nendobj\n");
        int xrefOffset = text.Length;
        int size = 6 + members;
        var rows = new List<byte> { 0, 0, 0, 0, 0, 0xFF, 0xFF };
        foreach (int offset in offsets.Append(xrefOffset))
            rows.AddRange([1, (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset, 0, 0]);
        for (int index = 0; index < members; index++)
            rows.AddRange([2, 0, 0, 0, 4, (byte)(index >> 8), (byte)index]);
        byte[] head = Encoding.ASCII.GetBytes(text +
            $"5 0 obj << /Type /XRef /Size {size} /Root 1 0 R /W [1 4 2] /Length {rows.Count} >> stream\n");
        byte[] tail = Encoding.ASCII.GetBytes($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery((byte[])[.. head, .. rows, .. tail]);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int number = 6; number < size; number++)
            Assert.IsType<PdfNull>(document.Resolve(number));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Resolving took {clock.Elapsed}.");
    }

    private static byte[] Source(string members) => Encoding.ASCII.GetBytes(
        "%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n" +
        "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >> endobj\n" +
        members + "xref\ntrailer << /Root 1 0 R /Size 6 >>\nstartxref\n0\n%%EOF\n");

    private static string ObjectStream(int number, string header, string body, int? count = null) =>
        $"{number} 0 obj << /Type /ObjStm /N {count ?? header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2} /First {header.Length} /Length {header.Length + body.Length} >> stream\n" +
        header + body + "\nendstream\nendobj\n";
}
