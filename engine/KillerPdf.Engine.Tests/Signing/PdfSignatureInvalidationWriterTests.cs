using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfSignatureInvalidationWriterTests
{
    [Fact]
    public void ClearSignatureValues_PreservesEmptyFieldAndAllowsRewrite()
    {
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            _ => [0x30, 0x00],
            new PdfSignatureOptions
            {
                CertificationPermission =
                    PdfSignatureCertificationPermission.FormFillingAndSignatures
            });

        byte[] cleared = PdfSignatureInvalidationWriter.ClearSignatureValues(
            PdfDocument.Open(signed));
        PdfDocument reopened = PdfDocument.Open(cleared);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(reopened));

        Assert.False(signature.IsSigned);
        Assert.Null(PdfSignatureReader.ReadCertificationPermission(reopened));
        Assert.True(cleared.AsSpan(0, signed.Length).SequenceEqual(signed));
        Assert.NotEmpty(PdfDocumentWriter.Write(reopened));
    }

    // #505: Acrobat draws an unsigned signature field as its own empty box and ignores the widget's appearance, so a
    // visible signature whose value goes becomes a stamp that keeps the signer's name and date on the page.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AVisibleSignatureKeepsItsAppearanceAsAStamp(bool rewrite)
    {
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            _ => [0x30, 0x00],
            new PdfSignatureOptions { VisibleAppearance = new PdfSignatureAppearance { Text = "Signed by Test" } });
        PdfDocument original = PdfDocument.Open(signed);
        PdfDictionary widget = OnlyAnnotation(original);

        byte[] cleared = rewrite
            ? PdfDocumentWriter.Write(original, new PdfDocumentWriteOptions
                { AllowSignatureInvalidation = true, ReplaceDanglingReferences = true })
            : PdfSignatureInvalidationWriter.ClearSignatureValues(original);
        PdfDocument reopened = PdfDocument.Open(cleared);
        PdfDictionary stamp = OnlyAnnotation(reopened);

        Assert.Empty(PdfSignatureReader.Read(reopened));
        Assert.Equal("Stamp", Assert.IsType<PdfName>(stamp[N("Subtype")]).ValueAsLatin1());
        Assert.False(stamp.ContainsKey(N("FT")));
        Assert.False(stamp.ContainsKey(N("V")));
        Assert.Equal(Hex(widget[N("Rect")]), Hex(stamp[N("Rect")]));
        Assert.Equal(Hex(widget[N("F")]), Hex(stamp[N("F")]));
        Assert.Equal(Appearance(original, widget), Appearance(reopened, stamp));
        if (!rewrite) Assert.True(cleared.AsSpan(0, signed.Length).SequenceEqual(signed));
    }

    private static PdfDictionary OnlyAnnotation(PdfDocument document)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        var annotations = Assert.IsType<PdfArray>(Deref(document, tree.Pages[0].Dictionary[N("Annots")]));
        return Assert.IsType<PdfDictionary>(Deref(document, Assert.Single(annotations)));
    }

    private static string Appearance(PdfDocument document, PdfDictionary annotation)
    {
        var appearances = Assert.IsType<PdfDictionary>(Deref(document, annotation[N("AP")]));
        var normal = Assert.IsType<PdfStream>(Deref(document, appearances[N("N")]));
        return Convert.ToHexString(normal.EncodedData.Span);
    }

    private static PdfObject Deref(PdfDocument document, PdfObject value)
    {
        while (value is PdfIndirectReference reference) value = document.Resolve(reference);
        return value;
    }

    private static string Hex(PdfObject value) => Convert.ToHexString(PdfObjectWriter.Write(value));
    private static PdfName N(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));

    [Fact]
    public void ClearSignatureValues_ReturnsUnchangedDocumentWithoutSignatureState()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        Assert.Equal(source, PdfSignatureInvalidationWriter.ClearSignatureValues(
            PdfDocument.Open(source)));
    }
}
