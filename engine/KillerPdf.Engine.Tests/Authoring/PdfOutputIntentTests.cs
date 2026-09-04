using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfOutputIntentTests
{
    [Theory]
    [InlineData("GRAY", 1)]
    [InlineData("RGB ", 3)]
    [InlineData("XYZ ", 3)]
    [InlineData("Lab ", 3)]
    [InlineData("Luv ", 3)]
    [InlineData("YCbr", 3)]
    [InlineData("Yxy ", 3)]
    [InlineData("HSV ", 3)]
    [InlineData("HLS ", 3)]
    [InlineData("CMY ", 3)]
    [InlineData("3CLR", 3)]
    [InlineData("CMYK", 4)]
    [InlineData("4CLR", 4)]
    public void IccProfile_ReadsSupportedComponentCounts(string colorSpace, int expectedComponents)
    {
        PdfIccProfile profile = PdfIccProfile.Load(BuildProfile(colorSpace));

        Assert.Equal(expectedComponents, profile.ComponentCount);
        Assert.Equal(colorSpace.TrimEnd(), profile.ColorSpace);
    }

    [Fact]
    public void SetOutputIntent_WritesCatalogIntentAndEmbeddedProfile()
    {
        byte[] bytes = BuildProfile("RGB ");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetOutputIntent(PdfIccProfile.Load(bytes), "sRGB IEC61966-2.1",
                registryName: "http://www.color.org")
            .AddBlankPage()
            .Build());
        var catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        var intents = Assert.IsType<PdfArray>(catalog[Name("OutputIntents")]);
        var intent = ResolveDictionary(document, intents[0]);
        var profile = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(intent[Name("DestOutputProfile")])));

        Assert.Equal("GTS_PDFA1", Assert.IsType<PdfName>(intent[Name("S")]).ValueAsLatin1());
        Assert.Equal(3, Assert.IsType<PdfInteger>(profile.Dictionary[Name("N")]).Value);
        Assert.Equal(bytes, profile.EncodedData.ToArray());
    }

    [Fact]
    public void OutputIntentInspectionReturnsValidatedColorManagementDetails()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetOutputIntent(PdfIccProfile.Load(BuildProfile("CMYK")), "FOGRA39",
                "ISO Coated v2", "http://www.color.org", "Press proofing")
            .AddBlankPage()
            .Build());

        PdfOutputIntentInformation intent = Assert.Single(
            PdfOutputIntentInspection.Inspect(document));

        Assert.Equal("GTS_PDFA1", intent.Subtype);
        Assert.Equal("FOGRA39", intent.OutputConditionIdentifier);
        Assert.Equal("ISO Coated v2", intent.OutputCondition);
        Assert.Equal("http://www.color.org", intent.RegistryName);
        Assert.Equal("Press proofing", intent.Information);
        Assert.Equal("CMYK", intent.Profile.ColorSpace);
        Assert.Equal(4, intent.Profile.ComponentCount);
    }

    [Fact]
    public void IccProfile_RejectsMissingSignatureAndUnsupportedColourSpace()
    {
        byte[] missing = BuildProfile("RGB ");
        missing.AsSpan(36, 4).Clear();

        Assert.Throws<FormatException>(() => PdfIccProfile.Load(missing));
        Assert.Throws<NotSupportedException>(() => PdfIccProfile.Load(BuildProfile("LAB ")));
    }

    [Fact]
    public void IccProfile_ValidatesTagTableRangesAndUniqueSignatures()
    {
        byte[] valid = BuildProfile("RGB ", 152);
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(128, 4), 1);
        "desc"u8.CopyTo(valid.AsSpan(132, 4));
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(136, 4), 144);
        BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(140, 4), 8);
        "text"u8.CopyTo(valid.AsSpan(144, 4));
        Assert.Equal(152, PdfIccProfile.Load(valid).Data.Length);

        byte[] truncatedTable = BuildProfile("RGB ");
        BinaryPrimitives.WriteUInt32BigEndian(truncatedTable.AsSpan(128, 4), 1);
        Assert.Throws<FormatException>(() => PdfIccProfile.Load(truncatedTable));

        byte[] outOfRange = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(outOfRange.AsSpan(136, 4), 148);
        Assert.Throws<FormatException>(() => PdfIccProfile.Load(outOfRange));

        byte[] duplicate = BuildProfile("RGB ", 164);
        BinaryPrimitives.WriteUInt32BigEndian(duplicate.AsSpan(128, 4), 2);
        for (int entry = 0; entry < 2; entry++)
        {
            "desc"u8.CopyTo(duplicate.AsSpan(132 + entry * 12, 4));
            BinaryPrimitives.WriteUInt32BigEndian(
                duplicate.AsSpan(136 + entry * 12, 4), 156);
            BinaryPrimitives.WriteUInt32BigEndian(
                duplicate.AsSpan(140 + entry * 12, 4), 8);
        }
        Assert.Throws<FormatException>(() => PdfIccProfile.Load(duplicate));
    }

    [Fact]
    public void PdfA4Mode_WritesIdentificationXmpAndOmitsInformationDictionary()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "PDF/A-4" })
            .SetOutputIntent(PdfIccProfile.Load(BuildProfile("RGB ")), "Test RGB")
            .EnablePdfA4Conformance()
            .AddBlankPage()
            .Build());
        var catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        var metadata = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Metadata")])));
        string xmp = Encoding.UTF8.GetString(metadata.EncodedData.Span);

        Assert.False(document.Trailer.ContainsKey(Name("Info")));
        Assert.Contains("pdfaid:part", xmp);
        Assert.Contains(">4<", xmp);
        Assert.Contains("pdfaid:rev", xmp);
        Assert.Contains(">2020<", xmp);
    }

    [Fact]
    public void PdfA4Mode_RequiresMetadataAndOutputIntent()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new PdfDocumentBuilder().EnablePdfA4Conformance().AddBlankPage().Build());
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata()).EnablePdfA4Conformance().AddBlankPage().Build());
    }

    [Fact]
    public void PdfA4fMode_WritesConformanceIdentificationAndAssociatedAttachment()
    {
        byte[] payload = "PDF/A-4f attachment"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "PDF/A-4f" })
            .SetOutputIntent(PdfIccProfile.Load(BuildProfile("RGB ")), "Test RGB")
            .EnablePdfA4fConformance()
            .AddBlankPage()
            .AddAttachment("evidence.txt", payload, "text/plain",
                relationship: PdfAssociatedFileRelationship.Data)
            .Build());
        var catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        var metadata = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Metadata")])));
        string xmp = Encoding.UTF8.GetString(metadata.EncodedData.Span);
        var associatedFiles = Assert.IsType<PdfArray>(catalog[Name("AF")]);
        var fileSpecification = ResolveDictionary(document, associatedFiles[0]);
        var embeddedFiles = Assert.IsType<PdfDictionary>(fileSpecification[Name("EF")]);
        var embedded = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(embeddedFiles[Name("UF")])));

        Assert.Contains("pdfaid:conformance", xmp);
        Assert.Contains(">F<", xmp);
        Assert.Equal("Data", Assert.IsType<PdfName>(
            fileSpecification[Name("AFRelationship")]).ValueAsLatin1());
        Assert.Equal(payload, embedded.EncodedData.ToArray());
    }

    [Fact]
    public void PdfA4eMode_WritesEngineeringConformanceIdentification()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "PDF/A-4e" })
            .SetOutputIntent(PdfIccProfile.Load(BuildProfile("RGB ")), "Test RGB")
            .EnablePdfA4eConformance()
            .AddBlankPage()
            .AddAttachment("engineering.txt", "payload"u8.ToArray(), "text/plain")
            .Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfStream metadata = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[Name("Metadata")])));
        string xmp = Encoding.UTF8.GetString(metadata.EncodedData.Span);

        Assert.Contains("pdfaid:conformance", xmp);
        Assert.Contains(">E<", xmp);
        Assert.Single(Assert.IsType<PdfArray>(catalog[Name("AF")]));
    }

    [Fact]
    public void PdfA4Mode_RejectsKnownNonConformingAuthoringFeatures()
    {
        static PdfDocumentBuilder Ready() => new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata())
            .SetOutputIntent(PdfIccProfile.Load(BuildProfile("RGB ")), "Test RGB")
            .EnablePdfA4Conformance()
            .AddBlankPage();

        Assert.Throws<InvalidOperationException>(() =>
            Ready().AddTextField(0, "name", 0, 0, 100, 20).Build());
        Assert.Throws<InvalidOperationException>(() =>
            Ready().AddAttachment("data.txt", ReadOnlyMemory<byte>.Empty).Build());
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata())
            .SetOutputIntent(PdfIccProfile.Load(BuildProfile("RGB ")), "Test RGB")
            .EnablePdfA4Conformance()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12).ShowLatin1Text("No").EndText())
            .Build());
    }

    private static byte[] BuildProfile(string colorSpace, int size = 132)
    {
        byte[] result = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)size);
        Encoding.ASCII.GetBytes(colorSpace).CopyTo(result, 16);
        "acsp"u8.CopyTo(result.AsSpan(36, 4));
        return result;
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
