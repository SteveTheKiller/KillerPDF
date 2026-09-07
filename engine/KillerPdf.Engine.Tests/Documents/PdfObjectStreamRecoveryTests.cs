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

    private static byte[] Source(string members) => Encoding.ASCII.GetBytes(
        "%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n" +
        "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >> endobj\n" +
        members + "xref\ntrailer << /Root 1 0 R /Size 6 >>\nstartxref\n0\n%%EOF\n");

    private static string ObjectStream(int number, string header, string body, int? count = null) =>
        $"{number} 0 obj << /Type /ObjStm /N {count ?? header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2} /First {header.Length} /Length {header.Length + body.Length} >> stream\n" +
        header + body + "\nendstream\nendobj\n";
}
