using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfContentRedactionTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    [Theory]
    [InlineData("ActualText", false)]
    [InlineData("Alt", false)]
    [InlineData("E", false)]
    [InlineData("ActualText", true)]
    [InlineData("Alt", true)]
    [InlineData("E", true)]
    [InlineData("Contents", false)]
    [InlineData("Contents", true)]
    public void RedactionRemovesAffectedNamedPropertyDescriptions(string key, bool direct)
    {
        string property = $"<< /{key} (NAMEDPROPERTYSECRET) /MCID 0 >>";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive {(direct ? property : "6 0 R")} >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (VISIBLESECRET) Tj EMC ET"),
            property
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("VISIBLESECRET", new PdfPageContentReader(output).Read(0).Text);
        AssertDescriptionAbsent(output, "NAMEDPROPERTYSECRET");
    }

    [Fact]
    public void OpaqueResourceConsumersRequireRasterApprovalWhenNamedDescriptionsAreRedacted()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("/Opaque unknown BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            "<< /ActualText (NAMEDPROPERTYSECRET) >>"
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130));

        Assert.Equal([0], PdfContentRedaction.Analyze(document, [region]).RasterPages);
        Assert.Throws<InvalidOperationException>(() => PdfContentRedaction.Apply(document, [region]));
    }

    [Theory]
    [InlineData("ActualText", false)]
    [InlineData("Alt", false)]
    [InlineData("E", false)]
    [InlineData("ActualText", true)]
    [InlineData("Alt", true)]
    [InlineData("E", true)]
    [InlineData("Contents", false)]
    [InlineData("Contents", true)]
    public void RedactionRemovesDescriptionsOfRemovedVectors(string key, bool raster)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            $"/Figure << /{key} (VECTORSECRET) >> BDC 0 0 1 rg 20 20 10 10 re f EMC",
            "/MediaBox [0 0 100 100]"));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 40, 40))],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain(new PdfPageContentReader(output).Read(0).Paths,
            path => path.BoundingBox == new PdfContentBounds(20, 20, 30, 30));
        AssertDescriptionAbsent(output, "VECTORSECRET");
    }

    private static byte[] TaggedStamp(string element) => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [6 0 R] /StructParents 0 >>",
        Stream("BT /F1 12 Tf 20 300 Td (KEEP) Tj ET"),
        Helvetica,
        "<< /Type /Annot /Subtype /Stamp /Rect [100 100 200 200] /Contents (ANNOTSECRET) /P 3 0 R >>",
        "<< /Type /StructTreeRoot /K [8 0 R] >>",
        element,
        "<< /Type /StructElem /S /Annot /P 8 0 R /Pg 3 0 R /Alt (ALTSECRET account 1234) /ActualText (ACTUALSECRET) /K [<< /Type /OBJR /Pg 3 0 R /Obj 6 0 R >>] >>"
    ]);

    [Theory]
    [InlineData("<< /Type /StructElem /S /Annot /P 7 0 R /Pg 3 0 R /Alt (ALTSECRET account 1234) /ActualText (ACTUALSECRET) /K [<< /Type /OBJR /Pg 3 0 R /Obj 6 0 R >>] >>")]
    [InlineData("<< /Type /StructElem /S /Annot /P 7 0 R /Pg 3 0 R /Alt (ALTSECRET account 1234) /ActualText (ACTUALSECRET) /K << /Type /OBJR /Pg 3 0 R /Obj 6 0 R >> >>")]
    [InlineData("<< /Type /StructElem /S /Sect /P 7 0 R /Alt (ALTSECRET parent) /K [9 0 R] >>")]
    public void RemovedAnnotationTakesItsStructureDescriptionWithIt(string element)
    {
        PdfDocument document = PdfDocument.Open(TaggedStamp(element));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 210, 210))]);

        Assert.True(result.Verification.Succeeded);
        Assert.Equal(1, Assert.Single(result.Plan.Pages).RemovedAnnotations);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "ALTSECRET");
        AssertDescriptionAbsent(output, "ACTUALSECRET");
        AssertDescriptionAbsent(output, "ANNOTSECRET");
    }

    [Fact]
    public void VerificationRejectsAnEmptyStructureElementThatKeepsItsDescription()
    {
        byte[] output = CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            Stream("0 0 m"),
            "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /Annot /P 6 0 R /Pg 3 0 R /Alt (LEFTOVER) /K [] >>",
            "<< /Type /StructElem /S /Annot /P 6 0 R /Pg 4 0 R /Alt (ELSEWHERE) /K [] >>"
        ]);

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(output,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 50, 50))], [], expectMetadataCleared: false);

        PdfRedactionVerificationFinding finding = Assert.Single(report.Findings,
            item => item.Code == "StructureDescription");
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void AnEmptyStructureElementOnARedactedPageLosesItsDescriptionAndElsewhereKeepsIt()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            Stream("0 0 m"),
            "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /Figure /P 6 0 R /Pg 3 0 R /Alt (LEFTOVER) /K [] >>",
            "<< /Type /StructElem /S /Figure /P 6 0 R /Pg 4 0 R /Alt (ELSEWHERE) /K [] >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 50, 50))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "LEFTOVER");
        Assert.Contains("ELSEWHERE", Encoding.Latin1.GetString(output.Source.Span));
    }

    private static byte[] DeepStructure(int levels)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 >>",
            Stream("/P << /MCID 0 >> BDC BT /F1 12 Tf 100 100 Td (STRUCTSECRET) Tj ET EMC"),
            Helvetica,
            "<< /Type /StructTreeRoot /K [7 0 R] >>"
        };
        for (int level = 0; level < levels; level++)
        {
            string kids = level == levels - 1 ? "[0]" : $"[{8 + level} 0 R]";
            objects.Add($"<< /Type /StructElem /S /Div /P {(level == 0 ? 6 : 6 + level)} 0 R /Pg 3 0 R /ActualText (STRUCTSECRET{level}) /K {kids} >>");
        }
        return CreatePdf(objects);
    }

    [Theory]
    [InlineData(85)]
    [InlineData(86)]
    [InlineData(100)]
    [InlineData(200)]
    public void DeepStructureTreesAreCleanedAtEveryLevel(int levels)
    {
        PdfContentRedactionResult result = PdfContentRedaction.Apply(PdfDocument.Open(DeepStructure(levels)),
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 300, 120))]);

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "STRUCTSECRET");
    }

    [Fact]
    public void AStructureTreeTooDeepToCleanIsRefused()
    {
        var error = Assert.ThrowsAny<NotSupportedException>(() => PdfContentRedaction.Apply(
            PdfDocument.Open(DeepStructure(300)), [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 300, 120))]));

        Assert.Equal("The document structure is too deeply nested to redact safely.", error.Message);
    }

    private static void AssertDescriptionAbsent(PdfDocument document, string secret)
    {
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed))
                continue;
            Inspect(document.Resolve(entry.ObjectNumber));
        }

        void Inspect(PdfObject value)
        {
            switch (value)
            {
                case PdfString text:
                    Assert.DoesNotContain(secret, Encoding.Latin1.GetString(text.Bytes.Span));
                    break;
                case PdfDictionary dictionary:
                    foreach (PdfObject child in dictionary.Values) Inspect(child);
                    break;
                case PdfArray array:
                    foreach (PdfObject child in array) Inspect(child);
                    break;
                case PdfStream stream:
                    Inspect(stream.Dictionary);
                    Assert.DoesNotContain(secret, Encoding.Latin1.GetString(
                        KillerPdf.Engine.Filters.PdfStreamDecoder.Decode(stream, document.Resolve)));
                    break;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RedactionRemovesDescriptionsOfRemovedOrPaintedImages(bool partial, bool raster = false)
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "/Figure << /Alt (IMAGESECRET) >> BDC q 100 0 0 100 20 20 cm /Im1 Do Q EMC", 0));
        var bounds = partial ? new PdfContentBounds(20, 20, 70, 120)
            : new PdfContentBounds(10, 10, 130, 130);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, bounds)],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "IMAGESECRET");
        Assert.Equal(raster || partial ? 1 : 0, new PdfPageContentReader(output).Read(0).Images.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactionRemovesDescriptionsOfRemovedInlineImages(bool raster)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "/Figure << /Alt (INLINESECRET) >> BDC q 10 0 0 10 20 20 cm "
            + "BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q EMC",
            "/MediaBox [0 0 100 100]"));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 40, 40))],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "INLINESECRET");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactionPropagatesChangedFormContentToEnclosingDescriptions(bool ownResources)
    {
        const string resources = "/Font << /F1 4 0 R >> /Properties << /Sensitive 7 0 R >>";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} /XObject << /Fm 5 0 R >> >> /Contents 6 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (VISIBLESECRET) Tj EMC ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + (ownResources ? $"/Resources << {resources} >>" : "")),
            Stream("/Figure << /Alt (PARENTSECRET) >> BDC /Fm Do EMC"),
            "<< /ActualText (CHILDSECRET) >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "CHILDSECRET");
        AssertDescriptionAbsent(output, "PARENTSECRET");
    }

    [Fact]
    public void DefaultVerificationRejectsRetiredDescriptionsRetainedOutsideResources()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /RetainedDescription 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            "<< /ActualText (NAMEDPROPERTYSECRET) >>"
        ]));

        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedDescription");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedNamedDescriptionsSurviveOnlyForUnaffectedUses(bool affectedFirst)
    {
        const string affected = "/Span /Shared BDC BT /F1 12 Tf 20 100 Td (REMOVE) Tj ET EMC ";
        const string unaffected = "/Span /Shared BDC BT /F1 12 Tf 20 20 Td (KEEP) Tj ET EMC ";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Shared 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(affectedFirst ? affected + unaffected : unaffected + affected),
            "<< /ActualText (SHAREDDESCRIPTION) >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        PdfObject ResolveOutput(PdfObject value) => value is PdfIndirectReference reference
            ? output.Resolve(reference) : value;
        var catalog = (PdfDictionary)ResolveOutput(output.Trailer[new PdfName("Root"u8)]);
        var pages = (PdfDictionary)ResolveOutput(catalog[new PdfName("Pages"u8)]);
        var kids = (PdfArray)ResolveOutput(pages[new PdfName("Kids"u8)]);
        var page = (PdfDictionary)ResolveOutput(kids[0]);
        var stream = (PdfStream)ResolveOutput(page[new PdfName("Contents"u8)]);
        var instructions = PdfContentStreamReader.Read(KillerPdf.Engine.Filters.PdfStreamDecoder.Decode(stream));
        var marked = instructions.Where(instruction => instruction.Operator == "BDC").ToArray();
        Assert.Equal(2, marked.Length);
        Assert.Single(marked, instruction => instruction.Operands[1] is PdfName);
        var sanitized = Assert.IsType<PdfDictionary>(Assert.Single(marked,
            instruction => instruction.Operands[1] is PdfDictionary).Operands[1]);
        Assert.False(sanitized.ContainsKey(new PdfName("ActualText"u8)));
        var resources = (PdfDictionary)ResolveOutput(page[new PdfName("Resources"u8)]);
        var properties = (PdfDictionary)ResolveOutput(resources[new PdfName("Properties"u8)]);
        var description = (PdfDictionary)ResolveOutput(properties[new PdfName("Shared"u8)]);
        Assert.Equal("SHAREDDESCRIPTION", Encoding.Latin1.GetString(
            ((PdfString)description[new PdfName("ActualText"u8)]).Bytes.Span));
        Assert.Equal("SHAREDDESCRIPTION", new PdfPageContentReader(output).Read(0).Text.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactionRemovesDescriptionsOfRemovedShadings(bool raster)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/Figure << /Alt (SHADINGSECRET) >> BDC /S1 sh EMC"),
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true true] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 0, 100, 100))],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "SHADINGSECRET");
    }

    [Fact]
    public void RasterRedactionPreservesIndirectPropertyValuesWithoutKeepingDescriptions()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            "<< /ActualText (REFERENCESECRET) /Lang 7 0 R >>",
            "(en-US)"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "REFERENCESECRET");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultVerificationRequiresUnaffectedUseForInheritedDirectDescriptions(bool used)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive << /Alt (INHERITEDSECRET) >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] {(used ? "/Contents 7 0 R" : "")} >>",
            Stream("/Figure << /Alt (INHERITEDSECRET) >> BDC BT /F1 12 Tf 20 20 Td (KEEP) Tj ET EMC")
        ]));

        if (used)
        {
            PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
                [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);
            Assert.True(result.Verification.Succeeded);
            Assert.Equal("KEEP", new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(1).Text.Trim());
            return;
        }
        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedDescription");
    }

    [Theory]
    [InlineData("ActualText")]
    [InlineData("Alt")]
    [InlineData("E")]
    public void RemovedVectorsLoseStructureDescriptionsWhileUnaffectedSiblingsKeepTheirs(string key)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>",
            Stream("/Figure << /MCID 0 >> BDC 20 20 10 10 re f EMC /Figure << /MCID 1 >> BDC 70 70 10 10 re f EMC"),
            "<< /Type /StructTreeRoot /K [6 0 R 7 0 R] >>",
            $"<< /Type /StructElem /S /Figure /P 5 0 R /Pg 3 0 R /{key} (STRUCTURESECRET) /K 0 >>",
            "<< /Type /StructElem /S /Figure /P 5 0 R /Pg 3 0 R /Alt (KEPTDESCRIPTION) /K 1 >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 40, 40))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "STRUCTURESECRET");
        PdfObject ResolveOutput(PdfObject value) => value is PdfIndirectReference reference
            ? output.Resolve(reference) : value;
        var catalog = (PdfDictionary)ResolveOutput(output.Trailer[new PdfName("Root"u8)]);
        var structure = (PdfDictionary)ResolveOutput(catalog[new PdfName("StructTreeRoot"u8)]);
        var kids = (PdfArray)ResolveOutput(structure[new PdfName("K"u8)]);
        var kept = (PdfDictionary)ResolveOutput(kids[1]);
        Assert.Equal("KEPTDESCRIPTION", Encoding.Latin1.GetString(
            ((PdfString)kept[new PdfName("Alt"u8)]).Bytes.Span));
    }

    [Fact]
    public void PartialVectorRedactionCannotPublishRecoverableGeometryWithoutRasterApproval()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("1 0 0 rg 0 0 50 100 re f",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));
        PdfContentRedactionResult? result = null;

        Exception? error = Record.Exception(() => result = PdfContentRedaction.Apply(document, [region]));

        if (result is not null)
        {
            // Inspect original geometry independently of whether an opaque cover hides it.
            PdfPageContent output = new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0);
            Assert.DoesNotContain(output.Paths, path => path.BoundingBox == new PdfContentBounds(0, 0, 50, 100));
        }
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("without replacing them with an image", error.Message);
    }

    [Fact]
    public void StrokeWidthCanRequireRasterizationWhenTheCenterlineMissesTheRegion()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("20 w 0 20 m 100 20 l S",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));
        PdfRenderedPage original = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        Assert.True(IsBlack(original, 50, 72));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);

        Assert.Equal([0], plan.RasterPages);
        Assert.Contains("Vector", Assert.Single(plan.Pages).RasterReasons);
    }

    [Fact]
    public void ClipOnlyPathsOverlappingTheRegionDoNotForceRasterization()
    {
        // A page-sized clip rectangle (re W n) paints nothing, so it is not recoverable geometry.
        PdfDocument document = PdfDocument.Open(SinglePage(
            "q 0 0 100 100 re W n 1 0 0 rg 0 0 20 20 re f Q",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.Empty(plan.RasterPages);
        Assert.DoesNotContain("Vector", Assert.Single(plan.Pages).RasterReasons);
        Assert.Equal(PdfRedactionPageMethod.Content, Assert.Single(result.Plan.Pages).Method);
        Assert.True(result.Verification.Succeeded);
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(result.Document)).Render(0, new PdfRenderOptions(100, 100));
        Assert.True(IsBlack(rendered, 50, 50));
    }

    [Fact]
    public void VerifierRejectsVectorGeometryHiddenByAnOtherwiseValidCover()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "1 0 0 rg 0 0 50 100 re f q 0 0 0 rg 25 25 50 50 re f Q",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(document.Source, [region], []);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Findings, finding => finding.Code == "VectorInRegion");
    }

    [Fact]
    public void FullyCoveredVectorCoordinatesAreRemovedRatherThanLeftUnpainted()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("35 35 m 65 35 l 50 65 l h f",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfPageContent output = new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0);
        Assert.DoesNotContain(output.Paths, path => path.Segments.Any(segment => segment.Operator == "m"));
        Assert.Equal(PdfRedactionPageMethod.Content, Assert.Single(result.Plan.Pages).Method);
    }

    [Theory]
    [InlineData("0 0 50 100 re f")]
    [InlineData("0 50 m 25 100 75 0 100 50 c f")]
    [InlineData("0 0 100 100 re 40 40 20 20 re f*")]
    [InlineData("0 0 100 100 re W n 0 50 m 100 50 l S")]
    [InlineData("20 w 0 20 m 100 20 l S")]
    [InlineData("2 0 0 2 0 0 cm 10 w 0 10 m 50 10 l S")]
    [InlineData("0 0 50 100 re f", 90)]
    [InlineData("0 0 50 100 re f", 180)]
    [InlineData("0 0 50 100 re f", 270)]
    [InlineData("0 0 m 100 0 l 100 100 l 0 100 l h 20 20 m 20 80 l 80 80 l 80 20 l h f")]
    [InlineData("0 50 m 10 100 20 0 30 50 c 40 100 50 0 60 50 c 70 100 80 0 90 50 c 100 100 100 0 100 50 c 90 0 80 100 70 50 c 60 0 50 100 40 50 c 30 0 20 100 10 50 c 0 0 0 100 0 50 c h f")]
    public void ApprovedVectorFallbackRemovesOriginalPaths(string content, int rotation = 0)
    {
        PdfDocument document = PdfDocument.Open(SinglePage("1 0 0 rg 1 0 0 RG " + content,
            $"/MediaBox [0 0 100 100] /Rotate {rotation}"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));
        Assert.Equal([0], PdfContentRedaction.Analyze(document, [region]).RasterPages);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 });

        PdfDocument output = PdfDocument.Open(result.Document);
        PdfPageContent page = new PdfPageContentReader(output).Read(0);
        Assert.All(page.Paths, path => Assert.Equal(region.Bounds, path.BoundingBox));
        Assert.Single(page.Images);
        Assert.True(result.Verification.Succeeded);

        // Remove only the appended cover in a test copy before checking the remaining picture.
        var contents = (PdfIndirectReference)PdfPageTree.Read(output).Pages[0].Dictionary[
            new PdfName("Contents"u8)];
        var stream = (PdfStream)output.Resolve(contents);
        var instructions = PdfContentStreamReader.Read(output.DecodeStream(stream));
        Assert.Equal(new[] { "q", "rg", "re", "f", "Q" }, instructions.TakeLast(5).Select(op => op.Operator));
        byte[] uncoveredBytes = new PdfIncrementalUpdateBuilder(output).ReplaceObject(contents.ObjectNumber,
            new PdfStream(new PdfDictionary([]), PdfContentStreamWriter.Write(instructions.SkipLast(5)))).Build();
        PdfDocument uncovered = PdfDocument.Open(uncoveredBytes);
        Assert.Empty(new PdfPageContentReader(uncovered).Read(0).Paths);
        PdfRenderedPage rendered = new PdfPageRenderer(uncovered).Render(0, new PdfRenderOptions(100, 100));
        Assert.True(IsBlack(rendered, 50, 50));
        PdfRenderedPage original = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                if (x < 23 || x > 77 || y < 23 || y > 77)
                    Assert.Equal(Pixel(original, x, y), Pixel(rendered, x, y));
    }

    [Fact]
    public void RestoredStrokeWidthDoesNotForceUnnecessaryRasterization()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("q 100 w Q 0 10 m 100 10 l S",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        Assert.Empty(PdfContentRedaction.Analyze(document, [region]).RasterPages);
        Assert.True(PdfContentRedaction.Apply(document, [region]).Verification.Succeeded);
    }

    [Fact]
    public void RemovesOnlyTheGlyphsUnderTheRegionAndKeepsTheRestInPlace()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Account SECRET42 closed) Tj ET"));
        PdfRedactionRegion region = Region(document, 0, "SECRET42");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRET42"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Single(output.CrossReferences.Sections);
        Assert.True(result.Verification.Succeeded);
        PdfRedactionPageAnalysis page = Assert.Single(result.Plan.Pages);
        Assert.Equal(PdfRedactionPageMethod.Content, page.Method);
        Assert.Equal(8, page.RemovedGlyphs);
        string text = Letters(output, 0);
        Assert.Contains("Account", text);
        Assert.Contains("closed", text);
        Assert.DoesNotContain("SECRET", text);
        AssertKeptLettersUnmoved(document, output, 0, [region]);
        Assert.False(PdfContentRedaction.Verify(document.Source, [region], []).Succeeded);
    }

    [Fact]
    public void KeepsSpacingForKerningCharacterAndWordSpacingAndQuoteOperators()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 2 Tc 3 Tw 14 TL 50 600 Td [(Name: ) -120 (HIDDEN) 40 ( value)] TJ " +
            "(second HIDDEN line) ' 1 0.5 (third HIDDEN row) \" ET"));
        PdfRedactionRegion[] regions = [Region(document, 0, "HIDDEN", 0),
            Region(document, 0, "HIDDEN", 1), Region(document, 0, "HIDDEN", 2)];

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, regions,
            new PdfContentRedactionOptions { ProhibitedText = ["HIDDEN"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("HIDDEN", Letters(output, 0));
        Assert.Contains("value", Letters(output, 0));
        Assert.Contains("row", Letters(output, 0));
        AssertKeptLettersUnmoved(document, output, 0, regions);
    }

    [Fact]
    public void HandlesCroppedRotatedPagesInReaderCoordinates()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 14 Tf 150 400 Td (TOP SECRET DATA) Tj ET",
            "/MediaBox [0 0 612 792] /CropBox [100 50 500 700] /Rotate 90"));
        PdfRedactionRegion region = Region(document, 0, "SECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        string text = Letters(output, 0);
        Assert.Contains("TOP", text);
        Assert.Contains("DATA", text);
        Assert.DoesNotContain("SECRET", text);
        AssertKeptLettersUnmoved(document, output, 0, [region]);

        // The crop box is 400 by 650 points; a quarter turn shows it 650 wide and 400 tall.
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(650, 400));
        double cx = (region.Bounds.Left + region.Bounds.Right) / 2;
        double cy = (region.Bounds.Bottom + region.Bounds.Top) / 2;
        Assert.True(IsBlack(rendered, (int)cy, (int)cx));
        Assert.False(IsBlack(rendered, 10, 10));
    }

    [Fact]
    public void RedactionDoesNotRetainOriginalFormBytesThroughInheritedResources()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /XObject << /Fm1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            Helvetica,
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Inherited FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 4 0 R >> >>")
        ]));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("FORMSECRET", Letters(output, 0));
        Assert.Contains("Inherited", Letters(output, 0));
        PdfRedactionVerificationReport objectReport = PdfContentRedaction.Verify(
            result.Document, [region], [], ["FORMSECRET"]);
        Assert.True(objectReport.Succeeded, string.Join("; ", objectReport.Findings.Select(finding => finding.Message)));
    }

    [Fact]
    public void DropsOriginalFormsFromAnUnusedSiblingResource()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Fm1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 500] /Contents 7 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Inherited FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 8 0 R >> >>"),
            Stream(""),
            Helvetica
        ]));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain(output.CrossReferences.Values.Where(entry => entry.Type is
                PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed)
            .Select(entry => output.Resolve(entry.ObjectNumber)).OfType<PdfStream>(),
            stream => Encoding.Latin1.GetString(output.DecodeStream(stream, 1_000_000)).Contains("FORMSECRET"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void DropsOriginalImagesFromAnUnusedSiblingResource(bool partlyCovered, bool inherited)
    {
        // Inherited: the Pages node carries the image. Shared: both pages name one indirect XObject dictionary.
        string pages = inherited
            ? "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Im1 6 0 R >> >> >>"
            : "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>";
        string resources = inherited ? string.Empty : "/Resources << /XObject 9 0 R >>";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            pages,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] {resources} /Contents 5 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] {resources} /Contents 7 0 R >>",
            Stream(partlyCovered ? "q 300 0 0 300 50 50 cm /Im1 Do Q" : "q 20 0 0 20 100 100 cm /Im1 Do Q"),
            SecretImage("/SMask 8 0 R"),
            Stream(""),
            MaskImage(),
            "<< /Im1 6 0 R >>"
        ]));
        var region = new PdfRedactionRegion(0, partlyCovered
            ? new PdfContentBounds(60, 210, 120, 300) : new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(!output.CrossReferences.TryGetValue(6, out PdfCrossReferenceEntry original)
            || original.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed),
            "The original image is still in the document.");
    }

    // LibreOffice exports give every page one indirect /Resources dictionary. Page 2 draws only /B,
    // directly or through a form without resources of its own, so /A must leave its resources and /B stay.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DropsAnImageFromPagesSharingOneResourceDictionaryAndKeepsWhatTheyDraw(bool throughForm)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 7 0 R >>",
            Stream("q 20 0 0 20 100 100 cm /A Do Q"),
            SecretImage(""),
            Stream(throughForm ? "/Fm Do" : "q 400 0 0 400 0 0 cm /B Do Q"),
            "<< /XObject << /A 6 0 R /B 9 0 R /Fm 10 0 R >> >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\n"
                + Encoding.Latin1.GetString([0, 0, 255]) + "\nendstream",
            Stream("q 400 0 0 400 0 0 cm /B Do Q", "/Type /XObject /Subtype /Form /BBox [0 0 400 400]")
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.False(HasSourceImage(output));
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(1, new PdfRenderOptions(400, 400));
        (byte red, byte green, byte blue) = Pixel(rendered, 200, 200);
        Assert.True(blue > 200 && red < 50 && green < 50, "Page 2 no longer draws its own image.");
    }

    // The AcroForm defaults and form appearances often name the page's own resource dictionary.
    [Fact]
    public void DropsAnImageFromAnAcroFormDefaultResourceDictionaryThePageShares()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /DR 7 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 7 0 R /Contents 5 0 R >>",
            Helvetica,
            Stream("q 20 0 0 20 100 100 cm /A Do Q"),
            SecretImage(""),
            "<< /XObject << /A 6 0 R >> >>"
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        Assert.False(HasSourceImage(PdfDocument.Open(result.Document)));
    }

    // Only entries of dictionaries the rewrite can replace are dropped, so a form with resources of its
    // own that lists the original still leaves it in the file, and the check still refuses.
    [Fact]
    public void StillRefusesWhenAFormWithResourcesOfItsOwnListsTheOriginal()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /XObject << /A 6 0 R >> >> /Contents 5 0 R /Annots [7 0 R] >>",
            Helvetica,
            Stream("q 20 0 0 20 100 100 cm /A Do Q"),
            SecretImage(""),
            "<< /Type /Annot /Subtype /Stamp /Rect [200 200 300 300] /F 4 /AP << /N 8 0 R >> >>",
            Stream("", "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /XObject << /A 6 0 R >> >>")
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130));

        PdfRedactionVerificationException error = Assert.Throws<PdfRedactionVerificationException>(() =>
            PdfContentRedaction.Apply(document, [region]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedSourceStream");
    }

    [Fact]
    public void KeepsAnImageAnUntouchedPageSharingTheResourceDictionaryStillDraws()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 7 0 R >>",
            Stream("q 20 0 0 20 100 100 cm /A Do Q"),
            SecretImage(""),
            Stream("q 300 0 0 300 50 50 cm /A Do Q"),
            "<< /XObject << /A 6 0 R >> >>"
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal("A", Assert.Single(new PdfPageContentReader(output).Read(1).Images).ResourceName);
        Assert.Empty(new PdfPageContentReader(output).Read(0).Images);
    }

    // Alternating graphics states without q and Q used to rebuild a growing string for every one of them.
    [Fact]
    public void RedactsAPageThatAlternatesGraphicsStatesWithoutSlowingDown()
    {
        string states = string.Concat(Enumerable.Repeat("/G0 gs /G1 gs ", 50_000));
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /ExtGState << /G0 << /ca 1 >> /G1 << /ca 0.5 >> >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(states + "BT /F1 12 Tf 20 100 Td (LATESECRET) Tj ET")
        ]));
        PdfRedactionRegion region = Region(document, 0, "LATESECRET");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);
        clock.Stop();

        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("LATESECRET", Letters(PdfDocument.Open(result.Document), 0));
        // About a second alone. The suite runs in parallel, and the quadratic walk this guards took minutes.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Redaction took {clock.Elapsed.TotalSeconds:0.0} seconds.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesAnInvokedInheritedImageAndItsMaskOnAnUntouchedSibling(bool partlyCovered)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Im1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 7 0 R >>",
            Stream(partlyCovered ? "q 300 0 0 300 50 50 cm /Im1 Do Q" : "q 20 0 0 20 100 100 cm /Im1 Do Q"),
            SecretImage("/SMask 8 0 R"),
            Stream("q 300 0 0 300 50 50 cm /Im1 Do Q"),
            MaskImage()
        ]));
        var region = new PdfRedactionRegion(0, partlyCovered
            ? new PdfContentBounds(60, 210, 120, 300) : new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfExtractedImage sibling = Assert.Single(new PdfPageContentReader(output).Read(1).Images);
        Assert.Equal("Im1", sibling.ResourceName);
        Assert.Equal(PdfPageInformation.Read(document)[1], PdfPageInformation.Read(output)[1]);
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("type3")]
    [InlineData("appearance")]
    public void PreservesARetiredImageStillDrawnBySiblingPatternsGlyphsOrAppearances(string use)
    {
        // Page 1 drops the image; page 2 draws the same object through a tiling pattern, a Type 3 glyph or a stamp appearance.
        (string page, string content, string extra, string extra2) = use switch
        {
            "pattern" => ("/Resources << /Pattern << /P1 8 0 R >> >>",
                "q /Pattern cs /P1 scn 50 50 200 200 re f Q",
                Stream("q 40 0 0 40 0 0 cm /Im1 Do Q",
                    "/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 40 40] /XStep 40 /YStep 40 /Resources << /XObject << /Im1 6 0 R >> >>"),
                "<< >>"),
            "type3" => ("/Resources << /Font << /T3 8 0 R >> >>",
                "BT /T3 200 Tf 50 50 Td (a) Tj ET",
                "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /square 9 0 R >> "
                    + "/Encoding << /Type /Encoding /Differences [97 /square] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << /XObject << /Im1 6 0 R >> >> >>",
                Stream("1000 0 d0 q 1000 0 0 1000 0 0 cm /Im1 Do Q")),
            _ => ("/Annots [8 0 R]",
                "",
                "<< /Type /Annot /Subtype /Stamp /Rect [50 50 250 250] /F 4 /AP << /N 9 0 R >> >>",
                Stream("q 200 0 0 200 0 0 cm /Im1 Do Q",
                    "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /Im1 6 0 R >> >>"))
        };
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /XObject << /Im1 6 0 R >> >> /Contents 5 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] {page} /Contents 7 0 R >>",
            Stream("q 20 0 0 20 100 100 cm /Im1 Do Q"),
            SecretImage(""),
            Stream(content),
            extra,
            extra2
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(new PdfPageContentReader(output).Read(0).Images);
        Assert.True(HasSourceImage(output));
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(1, new PdfRenderOptions(400, 400, includeAnnotations: true));
        (byte red, byte green, byte blue) = Pixel(rendered, 100, 200);
        Assert.True(red < 250 || green < 250 || blue < 250, "Page 2 no longer draws the shared image.");
    }

    private static string SolidImage(byte red, byte green, byte blue)
    {
        var samples = new byte[48];
        for (int offset = 0; offset < samples.Length; offset += 3)
            (samples[offset], samples[offset + 1], samples[offset + 2]) = (red, green, blue);
        return "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 48 >>\nstream\n"
            + Encoding.Latin1.GetString(samples) + "\nendstream";
    }

    // Page one draws /A under the region, and the given page still needs it through a route of its own.
    private static void AssertSharedImageSurvives(List<string> objects, int page)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        (byte Red, byte Green, byte Blue) before = Pixel(new PdfPageRenderer(document).Render(page, new PdfRenderOptions(400, 400)), 200, 200);
        Assert.NotEqual((byte)255, before.Red);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130))]);

        Assert.True(result.Verification.Succeeded);
        Assert.Equal(before, Pixel(new PdfPageRenderer(PdfDocument.Open(result.Document))
            .Render(page, new PdfRenderOptions(400, 400)), 200, 200));
    }

    [Fact]
    public void AType3FontSelectedByAGraphicsStateStillDrawsItsSharedImage() => AssertSharedImageSurvives([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 7 0 R >>",
        Stream("q 20 0 0 20 100 100 cm /A Do Q"),
        SolidImage(0, 0, 255),
        Stream("/G0 gs BT 200 0 0 200 100 100 Tm (a) Tj ET"),
        "<< /XObject << /A 6 0 R >> /ExtGState << /G0 << /Font [9 0 R 1] >> >> >>",
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << /a 10 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1] >>",
        Stream("1 0 0 0 1 1 d1 q 1 0 0 1 0 0 cm /A Do Q")
    ], 1);

    [Fact]
    public void APatternWithoutResourcesIsWalkedForEveryPageThatBindsItsNames() => AssertSharedImageSurvives([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 12 0 R 4 0 R] /Count 3 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 9 0 R /Contents 7 0 R >>",
        Stream("q 20 0 0 20 100 100 cm /A Do Q"),
        SolidImage(0, 0, 255),
        Stream("/Pattern cs /P scn 0 0 400 400 re f"),
        "<< /XObject << /A 6 0 R /I 11 0 R >> /Pattern << /P 10 0 R >> >>",
        "<< /XObject << /I 6 0 R >> /Pattern << /P 10 0 R >> >>",
        Stream("q 100 0 0 100 0 0 cm /I Do Q", "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100"),
        SolidImage(0, 255, 0),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 8 0 R /Contents 7 0 R >>"
    ], 2);

    [Fact]
    public void AnEntryAnUnwalkedFormStillNamesStaysAndIsRefused()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /Unwalked 8 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 7 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources 7 0 R /Contents 9 0 R >>",
            Stream("q 20 0 0 20 100 100 cm /A Do Q"),
            SolidImage(0, 0, 255),
            "<< /XObject << /A 6 0 R >> >>",
            Stream("q 10 0 0 10 0 0 cm /A Do Q", "/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources 7 0 R"),
            Stream("0 0 m")
        ]));

        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130))]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedSourceStream");
    }

    // The stroke lands on the new text after it is written, so only a refusal keeps the text visible.
    [Theory]
    [InlineData("20 w 1 0 0 RG 40 240 m 180 240 l S", true)]
    [InlineData("60 w 1 0 0 RG 40 205 m 180 205 l S", true)]
    [InlineData("1 0 0 RG 10 w 40 240 m 180 240 l 180 150 l h B", true)]
    [InlineData("0 w 1 0 0 RG 40 240 m 180 240 l S", true)]
    [InlineData("0 w 1 0 0 RG 40 100 m 180 100 l S", false)]
    [InlineData("1 w 1 0 0 RG 40 100 m 180 100 l S", false)]
    // Some producers draw a vertical table rule as a very wide, very short line. Only the band it
    // really paints counts, not the square around the path and its width.
    [InlineData("500 w 1 0 0 RG 260 100 m 260.5 100 l S", false)]
    [InlineData("500 w 1 0 0 RG 60 100 m 60.5 100 l S", true)]
    public void AStrokeOverTheNewParagraphIsRefusedAndOneClearOfItIsNot(string after, bool refused)
    {
        PdfDocument source = PdfDocument.Open(SinglePage(
            $"BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET BT /F1 10 Tf 1 0 0 1 30 238 Tm (OLD next) Tj ET {after}",
            "/MediaBox [0 0 300 300]"));
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));

        Exception? error = Record.Exception(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 200, 262), "AAAA AAAA\nAAAA\nAAAA",
            KillerPdf.Engine.Fonts.PdfStandardFontSubstitutes.OcrTrainingFonts()[0], 10, 12, PdfParagraphAlignment.Left));

        if (refused) Assert.Contains("painted after", Assert.IsAssignableFrom<NotSupportedException>(error).Message);
        else Assert.Null(error);
    }

    private static string SecretImage(string entries) =>
        $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 {entries} /Length 12 >>\nstream\n"
            + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]) + "\nendstream";

    private static string MaskImage() =>
        "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>\nstream\n"
            + Encoding.Latin1.GetString([255, 255, 255, 255]) + "\nendstream";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesAnInvokedInheritedFormOnAnUntouchedSibling(bool softMask)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Fm1 6 0 R >> /ExtGState << /GS1 << /SMask << /S /Luminosity /G 6 0 R >> >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 500] /Contents 7 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Inherited FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Font << /F1 8 0 R >> >>"),
            Stream(softMask ? "q /GS1 gs 0 0 400 500 re f Q" : "q 1 0 0 1 40 200 cm /Fm1 Do Q"),
            Helvetica
        ]));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("FORMSECRET", Letters(output, 0));
        if (!softMask) Assert.Contains("FORMSECRET", Letters(output, 1));
        Assert.Equal(PdfPageInformation.Read(document)[1], PdfPageInformation.Read(output)[1]);
        AssertKeptLettersUnmoved(document, output, 1, []);
    }

    [Fact]
    public void RewritesSharedFormsOnlyForTheRedactedPage()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Shared FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 7 0 R >> >>"),
            Helvetica
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("FORMSECRET", Letters(output, 0));
        Assert.Contains("Shared", Letters(output, 0));
        Assert.Contains("FORMSECRET", Letters(output, 1));
        AssertKeptLettersUnmoved(document, output, 0, [region]);
    }

    [Fact]
    public void ImageFullyInsideARegionIsRemovedAndThePageStaysEditable()
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "BT /F1 12 Tf 72 700 Td (Caption) Tj ET q 20 0 0 20 100 100 cm /Im1 Do Q", rotate: 0));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130))]);

        PdfRedactionPageAnalysis page = Assert.Single(result.Plan.Pages);
        Assert.Equal(PdfRedactionPageMethod.Content, page.Method);
        Assert.Equal(1, page.RemovedImages);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(new PdfPageContentReader(output).Read(0).Images);
        Assert.False(HasSourceImage(output));
        Assert.Contains("Caption", Letters(output, 0));
    }

    [Fact]
    public void PartlyCoveredImageThatCannotBePaintedNeedsApprovalAndKeepsItsSearchableTextOutsideTheRegion()
    {
        // A color-key mask keeps the image out of pixel painting, so the page needs the image fallback.
        PdfDocument document = PdfDocument.Open(ImagePage(
            "q 300 0 0 300 50 50 cm /Im1 Do Q BT 3 Tr /F1 12 Tf 230 100 Td (SCANNED SSN 123) Tj ET",
            rotate: 90, imageEntries: "/Mask [0 0 0 0 0 0]"));
        PdfRedactionRegion region = Region(document, 0, "123");

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Equal([0], plan.RasterPages);
        Assert.Contains("Image", Assert.Single(plan.Pages).RasterReasons);
        Assert.Throws<InvalidOperationException>(() => PdfContentRedaction.Apply(document, [region]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72, ProhibitedText = ["123"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        PdfPageContent content = new PdfPageContentReader(output).Read(0);
        Assert.Equal(PdfContentRedaction.RedactionImageName, Assert.Single(content.Images).ResourceName);
        Assert.Contains("SCANNED", Letters(output, 0));
        Assert.DoesNotContain("123", Letters(output, 0));
        Assert.False(HasSourceImage(output));
        AssertKeptLettersUnmoved(document, output, 0, [region]);

        // Quarter turn: unrotated (x, y) shows at pixel (y, x) on this 400 by 400 page.
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(400, 400));
        double cx = (region.Bounds.Left + region.Bounds.Right) / 2;
        double cy = (region.Bounds.Bottom + region.Bounds.Top) / 2;
        Assert.True(IsBlack(rendered, (int)cy, (int)cx));
        (byte red, byte green, byte blue) = Pixel(rendered, 275, 125);
        Assert.True(red > 200 && green < 60 && blue < 60, $"Expected red, got {red},{green},{blue}.");
    }

    [Fact]
    public void PartlyCoveredImageHasOnlyItsCoveredPixelsPaintedAndThePageStaysText()
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "q 300 0 0 300 50 50 cm /Im1 Do Q BT /F1 12 Tf 60 30 Td (Caption) Tj ET", rotate: 0));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(60, 210, 120, 300));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Empty(plan.RasterPages);
        Assert.Equal(1, Assert.Single(plan.Pages).RedactedImages);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfExtractedImage image = Assert.Single(new PdfPageContentReader(output).Read(0).Images);
        Assert.StartsWith("KPRI", image.ResourceName);
        Assert.Contains("Caption", Letters(output, 0));
        // The top-left pixel was red and is now black; the other three keep their colors.
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(400, 400));
        Assert.True(IsBlack(rendered, 90, 150));
        (byte red, byte green, byte blue) = Pixel(rendered, 275, 125);
        Assert.True(green > 200 && red < 60 && blue < 60, $"Expected green, got {red},{green},{blue}.");
        (red, green, blue) = Pixel(rendered, 125, 275);
        Assert.True(blue > 200 && red < 60 && green < 60, $"Expected blue, got {red},{green},{blue}.");
        Assert.Contains(PdfContentRedaction.Verify(document.Source, [region], [], expectMetadataCleared: false).Findings,
            finding => finding.Code == "ImageInRegion");
    }

    [Fact]
    public void PaintsTheSoftMaskOpaqueUnderThePaintedPixels()
    {
        // The top-left pixel is fully transparent, so only an opaque mask lets the fill show there.
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 300 0 0 300 50 50 cm /Im1 Do Q"),
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R /Length 12 >>\nstream\n"
                + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]) + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [0 1] /Length 4 >>\nstream\n"
                + Encoding.Latin1.GetString([0, 255, 255, 255]) + "\nendstream"
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(60, 210, 120, 300));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        Assert.Empty(result.Plan.RasterPages);
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(result.Document))
            .Render(0, new PdfRenderOptions(400, 400));
        Assert.True(IsBlack(rendered, 90, 150));
        (byte red, byte green, byte blue) = Pixel(rendered, 275, 125);
        Assert.True(green > 200 && red < 60 && blue < 60, $"Expected green, got {red},{green},{blue}.");
    }

    [Fact]
    public void RemovesIntersectingAnnotationsMarkedReplacementTextAndMetadata()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td /Span << /ActualText (SECRETWORD) >> BDC (SECRETWORD) Tj EMC ( visible) Tj ET"),
            "<< /Type /Annot /Subtype /Link /Rect [70 690 160 715] /A << /S /URI /URI (https://secretlink.example) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [300 100 350 120] /A << /S /URI /URI (https://kept.example) >> >>",
            "<< /Title (Confidential title) >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects, info: 8));
        PdfRedactionRegion region = Region(document, 0, "SECRETWORD");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD", "secretlink", "Confidential title"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(1, Assert.Single(result.Plan.Pages).RemovedAnnotations);
        Assert.Contains("visible", Letters(output, 0));
        Assert.Single(PdfLinkReader.ReadPage(output, 0));
        Assert.False(output.Trailer.ContainsKey(new PdfName("Info"u8)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchFindsAndRemovesTextShownThroughFieldsAndAnnotations(bool cropped)
    {
        // Field values and FreeText notes are drawn from their own appearances, not the page content,
        // so a search of page words alone cannot see them.
        string crop = cropped ? "/CropBox [50 40 562 752]" : string.Empty;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {crop} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R 8 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (123-45-6789) /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /FreeText /Contents (Call SECRETNOTE today) /DA (/F1 10 Tf 0 g) /Rect [100 500 300 540] /F 4 >>",
            "<< /Type /Annot /Subtype /Text /Contents (Unrelated note) /Rect [400 100 420 120] >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        var ssn = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.SocialSecurityNumber };
        var note = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "SECRETNOTE" };
        Assert.Empty(PdfRedactionSearch.Find([page], ssn).Matches);
        Assert.Empty(PdfRedactionSearch.Find([page], note).Matches);

        PdfRedactionMatch field = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, ssn).Matches);
        PdfRedactionMatch freeText = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, note).Matches);
        Assert.Equal(PdfRedactionTargetKind.FormField, field.TargetKind);
        Assert.Equal(PdfRedactionTargetKind.Comment, freeText.TargetKind);
        PdfRedactionRegion[] regions = [new(field.PageIndex, field.Bounds), new(freeText.PageIndex, freeText.Bounds)];

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, regions,
            new PdfContentRedactionOptions { ProhibitedText = ["123-45-6789", "SECRETNOTE"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal("Unrelated note", Assert.Single(PdfCommentReader.Read(output)).Contents);
        Assert.Empty(PdfFormWidgetReader.ReadPage(output, 0));
        Assert.Contains("visible", Letters(output, 0));
    }

    // ---- Hostile structure stops as a refusal, not a stack overflow ------------------------------

    [Fact]
    public void AColorSpaceAliasLoopDoesNotOverflowTheStack()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ColorSpace << /A /B /B /A >> >> /Contents 4 0 R >>",
            Stream("BI /W 1 /H 1 /BPC 8 /CS /A ID x EI")]));

        Exception? failure = Record.Exception(() => PdfContentRedaction.Analyze(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 40, 40))]));

        Assert.True(failure is null or FormatException or NotSupportedException or InvalidOperationException);
    }

    // ---- What a redaction leaves behind ------------------------------------------------------------

    // The file's own unmatched q left the cover under a clip of the page's, so red stayed in the image.
    [Fact]
    public void APageImageIsCoveredEvenWhenTheContentNeverClosesItsGraphicsState()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "1 0 0 rg 20 20 20 20 re f 0 0 5 5 re W n q",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 50, 50));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(result.Document)).Render(0, new PdfRenderOptions(100, 100));
        (byte red, byte green, byte blue) = Pixel(rendered, 30, 70);
        Assert.True(red < 50 && green < 50 && blue < 50, $"The covered area shows {red},{green},{blue}.");
    }

    [Fact]
    public void ARemovedCommentIsNotKeptAliveByAReplyOutsideTheArea()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Highlight /Contents (PARENTCOMMENT) /Rect [100 100 150 120] /QuadPoints [100 120 150 120 100 100 150 100] >>",
            "<< /Type /Annot /Subtype /Text /Contents (Reply stays) /IRT 4 0 R /RT /R /Rect [10 10 30 30] >>"]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 160, 130))],
            new PdfContentRedactionOptions { ProhibitedText = ["PARENTCOMMENT"] });

        Assert.True(result.Verification.Succeeded);
        Assert.Equal("Reply stays", Assert.Single(PdfCommentReader.Read(PdfDocument.Open(result.Document))).Contents);
    }

    [Fact]
    public void AWholePageRedactionTakesWhatLiesPastTheCropBox()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [0 0 612 400] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 300 Td (VISIBLETEXT) Tj ET BT /F1 12 Tf 72 700 Td (CROPPEDAWAY) Tj ET")]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 0, 612, 400))]);

        Assert.True(result.Verification.Succeeded);
        string kept = string.Concat(new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0).Letters.Select(letter => letter.Value));
        Assert.DoesNotContain("CROPPEDAWAY", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("VISIBLETEXT", kept, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableAppearanceReportsItsPageWithoutLosingReadableAnnotationText(bool recovery)
    {
        string readable = "BT /F1 12 Tf 2 2 Td (READABLE) Tj ET";
        byte[] bytes = CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Annots [5 0 R 6 0 R] >>",
            Helvetica,
            "<< /Type /Annot /Subtype /Stamp /Rect [10 10 100 50] /AP << /N 7 0 R >> >>",
            "<< /Type /Annot /Subtype /Stamp /Rect [10 100 100 150] /MK << /CA (CAPTION) >> /AP << /N 8 0 R >> >>",
            Stream(readable, "/Type /XObject /Subtype /Form /BBox [0 0 100 40]"),
            Stream("BT /F1 12 Tf (SECRET) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 100 40] /Filter /Unknown")
        ]);
        PdfDocument document = recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
        foreach (string query in new[] { "READABLE", "CAPTION" })
        {
            PdfRedactionReview review = PdfRedactionSearch.FindInAnnotations(document,
                new PdfRedactionSearchOptions { Query = query });
            Assert.Single(review.Matches);
            Assert.Equal(new[] { 0 }, review.UnreadableAnnotationPages);
        }
        PdfRedactionReview noMatch = PdfRedactionSearch.FindInAnnotations(document,
            new PdfRedactionSearchOptions { Query = "SECRET" });
        Assert.Empty(noMatch.Matches);
        Assert.Equal(new[] { 0 }, noMatch.UnreadableAnnotationPages);
    }

    // A button caption, a signature's printed name and a stamp are drawn from the annotation, with no
    // value or contents to search, so a search of those alone reports nothing while the words stay shown.
    // A nested form with no resources of its own draws with the page's, as a viewer shows it.
    [Fact]
    public void SearchFindsAppearanceTextThatUsesThePagesFont()
    {
        string inner = "BT /F1 10 Tf 2 2 Td (SIGNEDBYJANE) Tj ET";
        string outer = "/N2 Do";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (sig) /Rect [100 500 300 540] /P 3 0 R /F 4 /AP << /N 7 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 40] /Resources << /XObject << /N2 8 0 R >> >> /Length {outer.Length} >>\nstream\n{outer}\nendstream",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 40] /Length {inner.Length} >>\nstream\n{inner}\nendstream"]));

        Assert.Single(PdfRedactionSearch.FindInAnnotations(document,
            new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "SIGNEDBYJANE" }).Matches);
    }

    [Theory]
    [InlineData("SECRETCAPTION")]
    [InlineData("SIGNEDBYJANE")]
    [InlineData("STAMPBYJOHN")]
    public void SearchFindsAndRemovesTextShownOnlyByAnAnnotationItself(string term)
    {
        static string Appearance(string text)
        {
            string content = $"BT /F1 10 Tf 2 2 Td ({text}) Tj ET";
            return $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 40] /Resources << /Font << /F1 4 0 R >> >> /Length {content.Length} >>\nstream\n{content}\nendstream";
        }
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R 8 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (go) /MK << /CA (SECRETCAPTION) >> /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (sig) /Rect [100 500 300 540] /P 3 0 R /F 4 /AP << /N 9 0 R >> >>",
            "<< /Type /Annot /Subtype /Stamp /Rect [100 400 300 440] /F 4 /AP << /N 10 0 R >> >>",
            Appearance("SIGNEDBYJANE"),
            Appearance("STAMPBYJOHN")]));
        var search = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = term };

        PdfRedactionMatch match = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, search).Matches);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [new(match.PageIndex, match.Bounds)],
            new PdfContentRedactionOptions { ProhibitedText = [term] });
        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain(term, Encoding.Latin1.GetString(result.Document.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void FieldRedactionDropsTheXfaPacketThatMirrorsTheFieldValue()
    {
        // A hybrid form keeps every field value in its XFA datasets as well, so the packet goes with the field.
        PdfDocument document = PdfDocument.Open(HybridFormPdf());
        var search = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "SECRETWORD" };
        PdfRedactionMatch field = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, search).Matches);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [new(field.PageIndex, field.Bounds)],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD"] });

        Assert.True(result.Verification.Succeeded);
        Assert.True(result.Plan.RemovesXfaForm);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.False(AcroForm(output).ContainsKey(new PdfName("XFA"u8)));
        Assert.DoesNotContain("SECRETWORD", Encoding.Latin1.GetString(PdfDocumentWriterRaw(output)));
    }

    [Fact]
    public void VerifierReportsAnXfaPacketLeftInTheOutput()
    {
        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(HybridFormPdf(),
            [new PdfRedactionRegion(0, new PdfContentBounds(100, 600, 300, 620))], [], expectMetadataCleared: false);

        Assert.Contains(report.Findings, finding => finding.Code == "XfaFormData");
    }

    private static byte[] HybridFormPdf() => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /XFA [(datasets) 7 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R] >>",
        Helvetica,
        Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (SECRETWORD) /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
        Stream("<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form><ssn>SECRETWORD</ssn></form></xfa:data></xfa:datasets>")
    ]);

    private static PdfDictionary AcroForm(PdfDocument document)
    {
        PdfObject acroForm = PdfPageTree.Read(document).Catalog[new PdfName("AcroForm"u8)];
        if (acroForm is PdfIndirectReference reference) acroForm = document.Resolve(reference);
        return Assert.IsType<PdfDictionary>(acroForm);
    }

    [Fact]
    public void RemovingOneWidgetOfAMultiWidgetFieldClearsTheValueOnItsParent()
    {
        // The value of a field with several widgets lives on the field, not on the widget that is removed.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            "<< /FT /Tx /T (ssn) /V (PARENTSECRET) /DV (PARENTSECRET) /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 3 0 R /Rect [140 690 260 712] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 3 0 R /Rect [300 100 400 120] >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["PARENTSECRET"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfFormWidgetInfo remaining = Assert.Single(PdfFormWidgetReader.ReadPage(output, 0));
        Assert.Equal("ssn", remaining.FieldName);
        Assert.Equal(string.Empty, remaining.Value);
        Assert.Equal(string.Empty, remaining.DefaultValue);
    }

    [Fact]
    public void RemovingOneWidgetOfARepeatedFieldLeavesNoValueInTheSiblingAppearances()
    {
        // The same field is drawn on both pages. Redacting the page 1 widget clears the shared value, and
        // the page 2 widget must not keep drawing it from its appearance streams.
        string Appearance(string text) => Stream($"BT /F1 12 Tf 2 4 Td ({text}) Tj ET",
            "/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /F1 10 0 R >> >>");
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 10 0 R >> >> /Contents 5 0 R /Annots [7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 10 0 R >> >> /Contents 9 0 R /Annots [8 0 R] >>",
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            "<< /FT /Tx /T (Name) /V (John Smith) /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 3 0 R /Rect [140 690 340 710] /AP << /N 11 0 R >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 4 0 R /Rect [100 700 300 720] /AP << /N 12 0 R /D 13 0 R >> >>",
            Stream("BT /F1 12 Tf 72 700 Td (Page two) Tj ET"),
            Helvetica,
            Appearance("John Smith"),
            Appearance("John Smith"),
            Appearance("John Smith")
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain(AllObjectText(output), text => text.Contains("John Smith", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("John Smith", Encoding.Latin1.GetString(result.Document.Span));
        // The page 2 occurrence stays as an empty field.
        PdfFormWidgetInfo sibling = Assert.Single(PdfFormWidgetReader.ReadPage(output, 1));
        Assert.Equal("Name", sibling.FieldName);
        Assert.Equal(string.Empty, sibling.Value);
    }

    [Fact]
    public void RemovingOneWidgetOfARadioGroupKeepsTheSiblingStateAppearancesAndTurnsItOff()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 10 0 R >> >> /Contents 5 0 R /Annots [7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 10 0 R >> >> /Contents 9 0 R /Annots [8 0 R] >>",
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            "<< /FT /Btn /Ff 32768 /T (Choice) /V /A /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 3 0 R /Rect [140 690 160 710] /AS /A /AP << /N << /A 11 0 R /Off 12 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 4 0 R /Rect [100 700 120 720] /AS /A /AP << /N << /A 13 0 R /Off 14 0 R >> >> >>",
            Stream("BT /F1 12 Tf 72 700 Td (Page two) Tj ET"),
            Helvetica,
            Stream("0 0 10 10 re f", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]"),
            Stream("0 0 20 20 re S", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]"),
            Stream("0 0 10 10 re f", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]"),
            Stream("0 0 20 20 re S", "/Type /XObject /Subtype /Form /BBox [0 0 20 20]")
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        PdfObject ResolveOutput(PdfObject value) => value is PdfIndirectReference reference
            ? output.Resolve(reference) : value;
        var sibling = Assert.IsType<PdfDictionary>(output.Resolve(8));
        Assert.Equal(new PdfName("Off"u8), ResolveOutput(sibling[new PdfName("AS"u8)]));
        var appearance = Assert.IsType<PdfDictionary>(ResolveOutput(sibling[new PdfName("AP"u8)]));
        var states = Assert.IsType<PdfDictionary>(ResolveOutput(appearance[new PdfName("N"u8)]));
        Assert.True(states.ContainsKey(new PdfName("Off"u8)));
        Assert.True(states.ContainsKey(new PdfName("A"u8)));
        var parent = Assert.IsType<PdfDictionary>(output.Resolve(6));
        Assert.False(parent.ContainsKey(new PdfName("V"u8)));
    }

    private static List<string> AllObjectText(PdfDocument document)
    {
        var found = new List<string>();
        foreach (PdfCrossReferenceEntry entry in document.CrossReferences.Values)
        {
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed)) continue;
            Walk(document.Resolve(entry.ObjectNumber), 0);
        }
        return found;

        void Walk(PdfObject value, int depth)
        {
            if (depth > 64) return;
            switch (value)
            {
                case PdfString text:
                    found.Add(PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "Object text"));
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array) Walk(item, depth + 1);
                    break;
                case PdfStream stream:
                    Walk(stream.Dictionary, depth + 1);
                    byte[] decoded = PdfStreamDecoder.Decode(stream, document.Resolve, 1 << 24);
                    found.Add(Encoding.Latin1.GetString(decoded));
                    found.Add(Encoding.BigEndianUnicode.GetString(decoded));
                    break;
                case PdfDictionary dictionary:
                    foreach (PdfObject item in dictionary.Values) Walk(item, depth + 1);
                    break;
            }
        }
    }

    [Theory]
    [InlineData("[0 0 0 0]")]
    [InlineData("[100 100 100 100]")]
    public void SearchProposesZeroSizeHiddenFieldsThroughAOnePointAreaThatRemovesThem(string rect)
    {
        // Hidden and calculated fields often carry an empty rectangle, and they hold values the page never shows.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (123-45-6789) /Rect {rect} /P 3 0 R /F 2 >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        var ssn = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.SocialSecurityNumber };

        PdfRedactionMatch field = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, ssn).Matches);

        Assert.Equal(PdfRedactionTargetKind.FormField, field.TargetKind);
        Assert.True(field.Bounds.Width > 0 && field.Bounds.Height > 0, "The proposed area must have a size.");
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [new(field.PageIndex, field.Bounds)],
            new PdfContentRedactionOptions { ProhibitedText = ["123-45-6789"] });
        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(PdfFormWidgetReader.ReadPage(output, 0));
        Assert.DoesNotContain("123-45-6789", Encoding.Latin1.GetString(PdfDocumentWriterRaw(output)));
    }

    [Fact]
    public void RemovesAnOrphanWidgetOfTheRedactedPageThatIsMissingFromAnnots()
    {
        // A widget that only the field tree knows cannot be placed, so it leaves with its value instead of blocking the page.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (orphan) /P 3 0 R /V (SECRETWORD) >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfObject fields = AcroForm(output)[new PdfName("Fields"u8)];
        if (fields is PdfIndirectReference reference) fields = output.Resolve(reference);
        Assert.Empty(Assert.IsType<PdfArray>(fields));
        Assert.DoesNotContain(PdfContentRedaction.Verify(result.Document, [region], []).Findings,
            finding => finding.Code == "FieldInRegion");
    }

    [Fact]
    public void SearchSkipsAndReportsAMalformedAnnotationInsteadOfFailing()
    {
        // A reversed rectangle names two opposite corners and is normalized; a nonstring /T is skipped and reported.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            "<< /Type /Annot /Subtype /FreeText /Contents (Call SECRETNOTE today) /DA (/F1 10 Tf 0 g) /Rect [300 540 100 500] /F 4 >>",
            "<< /Type /Annot /Subtype /Text /Contents (SECRETNOTE in a broken note) /T 5 /Rect [400 100 420 120] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [9 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Contents (Second page SECRETNOTE) /Rect [10 10 30 30] >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        var note = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "SECRETNOTE" };

        PdfRedactionReview review = PdfRedactionSearch.FindInAnnotations(document, note);

        Assert.Equal(["annotation:0:0", "annotation:1:0"], review.Matches.Select(match => match.Id));
        Assert.Equal(new PdfContentBounds(100, 500, 300, 540), review.Matches[0].Bounds);
        Assert.Equal([0], review.UnreadableAnnotationPages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Rect [70 690 160]")]
    [InlineData("/Rect [70 690 (wide) 715]")]
    [InlineData("/Rect 9 0 R")]
    public void RemovesAnnotationsWithoutAUsableRectangleFromARedactedPage(string rect)
    {
        // An annotation that cannot be placed cannot be shown to be clear of the area, so it leaves the page.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (SECRETWORD) Tj ( visible) Tj ET"),
            $"<< /Type /Annot /Subtype /Text {rect} /Contents (SECRETWORD note) /T (SECRETWORD author) >>",
            "<< /Type /Annot /Subtype /Link /Rect [300 100 350 120] /A << /S /URI /URI (https://kept.example) >> >>",
            "<< /Title (Plain) >>",
            "(not a rectangle)"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "SECRETWORD");

        Assert.Contains(PdfContentRedaction.Verify(document.Source, [region], [], expectMetadataCleared: false).Findings,
            finding => finding.Code == "AnnotationInRegion");
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD"] });

        Assert.True(result.Verification.Succeeded);
        Assert.Equal(1, Assert.Single(result.Plan.Pages).RemovedAnnotations);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Single(PdfLinkReader.ReadPage(output, 0));
        PdfObject annots = PdfPageTree.Read(output).Pages[0].Dictionary[new PdfName("Annots"u8)];
        if (annots is PdfIndirectReference reference) annots = output.Resolve(reference);
        Assert.Single(Assert.IsType<PdfArray>(annots));
    }

    [Fact]
    public void StripsAlternateTextFromTaggedContentAndRemovesCoveredFormFields()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /AcroForm << /Fields [8 0 R 9 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [8 0 R 9 0 R] >>",
            Helvetica,
            Stream("/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (Patient TAGSECRET) Tj ET EMC " +
                "/P << /MCID 1 >> BDC BT /F1 12 Tf 72 600 Td (Public line) Tj ET EMC"),
            "<< /Type /StructTreeRoot /K [7 0 R 10 0 R] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /Alt (Alt mentions TAGSECRET) /K 0 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (FIELDVALUE99) /Rect [140 690 260 712] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (other) /V (keep) /Rect [300 100 400 120] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /Alt (Kept description) /K 1 >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "TAGSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["TAGSECRET", "FIELDVALUE99"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Contains("Patient", Letters(output, 0));
        string raw = Encoding.Latin1.GetString(PdfDocumentWriterRaw(output));
        Assert.Contains("Kept description", raw);
        Assert.Contains("(keep)", raw);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(70)]
    public void RemovesCoveredWidgetsBeneathDeepFieldHierarchies(int ancestors)
    {
        PdfDocument document = PdfDocument.Open(DeepFieldPdf(ancestors));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        string raw = Encoding.Latin1.GetString(PdfDocumentWriterRaw(PdfDocument.Open(result.Document)));
        Assert.DoesNotContain("DEEPSECRET", raw);
        Assert.DoesNotContain("(a0)", raw);
    }

    [Fact]
    public void RefusesFieldHierarchiesTooDeepToPrune()
    {
        PdfDocument document = PdfDocument.Open(DeepFieldPdf(300));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        var error = Assert.Throws<InvalidOperationException>(() => PdfContentRedaction.Apply(document, [region]));
        Assert.Contains("too deep", error.Message);
    }

    [Fact]
    public void RedactsAFieldUnderADeepButAllowedHierarchy()
    {
        PdfDocument document = PdfDocument.Open(DeepFieldPdf(200));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "NAMEHERE");
    }

    [Fact]
    public void VerifierReportsAFieldWidgetLeftOverARedactedArea()
    {
        PdfDocument document = PdfDocument.Open(DeepFieldPdf(1));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(DeepFieldPdf(1), [region], []);

        Assert.Contains(report.Findings, finding => finding.Code == "FieldInRegion");
    }

    // A widget holding DEEPSECRET sits beneath a chain of nonterminal fields and over NAMEHERE.
    private static byte[] DeepFieldPdf(int ancestors)
    {
        int widget = 6 + ancestors;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [{widget} 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET")
        };
        for (int level = 0; level < ancestors; level++)
        {
            string parent = level == 0 ? string.Empty : $"/Parent {5 + level} 0 R ";
            objects.Add($"<< /T (a{level}) {parent}/Kids [{7 + level} 0 R] >>");
        }
        objects.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /Parent {widget - 1} 0 R /P 3 0 R /V (DEEPSECRET) /Rect [140 690 260 712] >>");
        return CreatePdf(objects);
    }

    [Theory]
    [InlineData("/Filter /KillerUnknownFilter", "HIDDENSECRET")]
    [InlineData("/Filter /FlateDecode", "HIDDENSECRET is not zlib")]
    public void ProhibitedTextVerificationFailsWhenARetainedStreamCannotBeInspected(string entries, string data)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /KillerExtra 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            Stream(data, entries)
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["HIDDENSECRET"] }));
        Assert.Contains(error.Report.Findings, finding => finding.Code == "ObjectInspectionFailure");
    }

    [Fact]
    public void ProhibitedTextVerificationFailsWhenAStreamExceedsTheDecodeBudget()
    {
        byte[] plain = Encoding.Latin1.GetBytes("HIDDENSECRET" + new string(' ', 8192));
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(plain);
        string data = Encoding.Latin1.GetString(packed.ToArray());
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /KillerExtra 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            Stream(data, "/Filter /FlateDecode")
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");
        int limit = PdfContentRedaction.InspectionDecodeLimit;
        PdfContentRedaction.InspectionDecodeLimit = 1024;
        try
        {
            var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document, [region],
                new PdfContentRedactionOptions { ProhibitedText = ["HIDDENSECRET"] }));
            Assert.Contains(error.Report.Findings, finding => finding.Code == "ObjectInspectionFailure"
                && finding.Message.Contains("limit", StringComparison.OrdinalIgnoreCase));
        }
        finally { PdfContentRedaction.InspectionDecodeLimit = limit; }
    }

    private static byte[] PdfDocumentWriterRaw(PdfDocument document) =>
        KillerPdf.Engine.Writing.PdfDocumentWriter.Write(document, new KillerPdf.Engine.Writing.PdfDocumentWriteOptions());

    [Fact]
    public void RejectsRegionsOutsideTheDocument()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("BT /F1 12 Tf 72 700 Td (x) Tj ET"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentRedaction.Analyze(document,
            [new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 10, 10))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentRedaction.Analyze(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 10, 20))]));
    }

    [Fact]
    public void CoveredTextGoesWhileDrawingsImagesAndNeighborsStay()
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "BT /F1 12 Tf 72 300 Td (OLDTEXT) Tj ET BT /F1 12 Tf 72 200 Td (Keep) Tj ET " +
            "q 0 0 1 rg 60 290 120 30 re f Q q 20 0 0 20 100 295 cm /Im1 Do Q", rotate: 0));
        PdfRedactionRegion region = Region(document, 0, "OLD");

        byte[]? updated = PdfContentRedaction.RemoveCoveredText(document, [region]);

        Assert.NotNull(updated);
        PdfDocument output = PdfDocument.Open(updated);
        Assert.True(output.CrossReferences.Sections.Count > 1, "the change is an incremental update");
        // Only glyphs centered under the cover go; the rest of the word keeps its place.
        Assert.Equal("TEXTKeep", Letters(output, 0));
        AssertKeptLettersUnmoved(document, output, 0, [region]);
        Assert.Single(new PdfPageContentReader(output).Read(0).Images);
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(400, 400));
        (byte red, byte green, byte blue) = Pixel(rendered, 65, 105);
        Assert.True(blue > 200 && red < 60 && green < 60, $"Expected the blue fill, got {red},{green},{blue}.");
    }

    [Fact]
    public void CoveredTextInsideAFormIsRemovedOnlyForThatPage()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Shared EARLIEREDIT text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 7 0 R >> >>"),
            Helvetica
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "EARLIEREDIT");

        PdfDocument output = PdfDocument.Open(PdfContentRedaction.RemoveCoveredText(document, [region])!);

        Assert.DoesNotContain("EARLIEREDIT", Letters(output, 0));
        Assert.Contains("Shared", Letters(output, 0));
        Assert.Contains("EARLIEREDIT", Letters(output, 1));
        AssertKeptLettersUnmoved(document, output, 0, [region]);
    }

    [Fact]
    public void NothingUnderTheCoverLeavesTheDocumentAlone()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("BT /F1 12 Tf 72 700 Td (Untouched) Tj ET"));

        Assert.Null(PdfContentRedaction.RemoveCoveredText(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(300, 100, 400, 150))]));
    }

    [Fact]
    public void FillsTheAreaWithItsColorAndDrawsTheOverlayTextAsRealText()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Account SECRET42 closed) Tj ET"));
        PdfRedactionRegion region = Region(document, 0, "SECRET42") with
        {
            OverlayText = "(b)(6)",
            FillColor = new PdfRgbColor(1, 0, 0)
        };

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRET42"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        string text = Letters(output, 0);
        Assert.Contains("(b)(6)", text);
        Assert.Contains("Account", text);
        Assert.DoesNotContain("SECRET", text);
        Assert.All(new PdfPageContentReader(output).Read(0).Letters
            .Where(letter => PdfContentRedaction.Overlaps(letter.BoundingBox, region.Bounds)),
            letter => Assert.Contains(letter.Value, "(b)(6)"));
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(612, 792));
        double cy = (region.Bounds.Bottom + region.Bounds.Top) / 2;
        (byte red, byte green, byte blue) = Pixel(rendered, (int)region.Bounds.Left + 1, 792 - (int)cy);
        Assert.True(red > 200 && green < 60 && blue < 60, $"Expected red, got {red},{green},{blue}.");
    }

    [Fact]
    public void OverlayTextReadsUprightOnRotatedPagesAndFallsBackToTheReasonCode()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 110 400 Td (HIDDEN) Tj ET", "/MediaBox [0 0 612 792] /Rotate 90"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(100, 300, 160, 500), ReasonCode: "(b)(7)(C)");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfExtractedLetter[] overlay = [.. new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0).Letters];
        Assert.Equal("(b)(7)(C)", string.Concat(overlay.Select(letter => letter.Value)));
        Assert.All(overlay, letter =>
        {
            Assert.Equal(PdfWritingDirection.BottomToTop, letter.WritingDirection);
            Assert.True(PdfContentRedaction.Overlaps(letter.BoundingBox, region.Bounds));
        });
    }

    [Fact]
    public void VerificationRejectsTextThatIsNotTheOverlayText()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Account SECRET42 closed) Tj ET"));
        PdfRedactionRegion region = Region(document, 0, "SECRET42") with { OverlayText = "(b)(6)" };

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(document.Source, [region], [],
            expectMetadataCleared: false);

        Assert.Contains(report.Findings, finding => finding.Code == "TextInRegion");
        Assert.True(PdfContentRedaction.SupportsOverlayText("Caf\u00e9 (b)(6)"));
        Assert.False(PdfContentRedaction.SupportsOverlayText("\u79d8\u5bc6"));
    }

    private static string GrayImage(string entries = "") => Stream(new string('\u00c8', 100 * 100),
        $"/Type /XObject /Subtype /Image /Width 100 /Height 100 /BitsPerComponent 8 /ColorSpace /DeviceGray {entries}");

    private static bool AnyStreamHasKey(PdfDocument document, string key)
    {
        foreach (var entry in document.CrossReferences.Values)
            if (entry.Type is PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed
                && document.Resolve(entry.ObjectNumber) is PdfStream stream
                && stream.Dictionary.ContainsKey(new PdfName(Encoding.ASCII.GetBytes(key)))) return true;
        return false;
    }

    [Theory]
    [InlineData("/Alternates [<< /Image 6 0 R /DefaultForPrinting true >>]")]
    [InlineData("/OPI << /2.0 << /Type /OPI /Version 2.0 /F 6 0 R >> >>")]
    [InlineData("/PieceInfo << /Illustrator << /Private 6 0 R >> >>")]
    [InlineData("/Metadata 6 0 R")]
    public void ARepaintedImageLeavesItsOriginalCopiesBehind(string entry)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 200 0 0 200 100 500 cm /Im Do Q"),
            GrayImage(entry + " /OC 7 0 R"),
            GrayImage("/OriginalCopy true"),
            "<< /Type /OCG /Name (Layer) >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(120, 520, 180, 580))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.Equal(1, Assert.Single(result.Plan.Pages).RedactedImages);
        Assert.False(AnyStreamHasKey(output, "OriginalCopy"), "A copy of the original image is still in the file.");
        Assert.True(AnyStreamHasKey(output, "OC"), "The image left its optional content group.");
    }

    [Theory]
    [InlineData("/PieceInfo << /Illustrator << /Private 7 0 R >> >>")]
    [InlineData("/Metadata 7 0 R")]
    [InlineData("/OPI << /2.0 << /Type /OPI /Version 2.0 /F 7 0 R >> >>")]
    public void ARewrittenFormLeavesItsPrivateDataBehind(string entry)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/Fm Do"),
            Stream("BT /F1 12 Tf 100 600 Td (SECRETTEXT) Tj ET\nBT /F1 12 Tf 100 300 Td (PUBLICTEXT) Tj ET",
                $"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 6 0 R >> >> {entry} /StructParent 4"),
            Helvetica,
            Stream("SECRETTEXT in the private editable copy")
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 620))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(output, "SECRETTEXT");
        Assert.True(AnyStreamHasKey(output, "StructParent"), "The form left the structure tree.");
    }

    [Fact]
    public void ARedactedWidgetTakesAValueInheritedFromAGrandparent()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [8 0 R 9 0 R] /Contents 4 0 R >>",
            Stream("0 0 m"),
            Helvetica,
            "<< /T (a) /FT /Tx /V (SECRETVALUE) /DV (SECRETDEFAULT) /RV (SECRETRICH) /Kids [7 0 R] >>",
            "<< /T (b) /Parent 6 0 R /Kids [8 0 R 9 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /Rect [100 600 200 620] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /Rect [100 300 200 320] >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 630))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(output, "SECRETVALUE");
        AssertDescriptionAbsent(output, "SECRETDEFAULT");
        AssertDescriptionAbsent(output, "SECRETRICH");
    }

    [Fact]
    public void AFieldEmptiedOfItsWidgetsTakesAValueInheritedFromItsParent()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [8 0 R 10 0 R] /Contents 4 0 R >>",
            Stream("0 0 m"),
            Helvetica,
            "<< /T (a) /FT /Tx /V (SECRETVALUE) /Kids [7 0 R 9 0 R] >>",
            "<< /T (b) /Parent 6 0 R /Kids [8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /Rect [100 600 200 620] >>",
            "<< /T (c) /Parent 6 0 R /Kids [10 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 9 0 R /Rect [100 300 200 320] >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 630))]);

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "SECRETVALUE");
    }

    private static byte[] DescribedElement(string entries, string extraObject = "null") => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        Stream("/P << /MCID 0 >> BDC BT /F1 12 Tf 100 600 Td (SECRETTEXT) Tj ET EMC\n/P << /MCID 1 >> BDC BT /F1 12 Tf 100 300 Td (PUBLICTEXT) Tj ET EMC"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] >>",
        $"<< /Type /StructElem /S /Table /P 6 0 R /Pg 3 0 R /K 0 {entries} >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 1 /T (KEPTTITLE) /A << /O /Table /Summary (KEPTSUMMARY) >> >>",
        extraObject
    ]);

    [Theory]
    [InlineData("/T (SECRETTITLE) /A << /O /Table /Summary (SECRETSUMMARY) >>", "null")]
    [InlineData("/T (SECRETTITLE) /A [<< /O /Layout /Placement /Block >> 0 << /O /Table /Summary (SECRETSUMMARY) >> 1]", "null")]
    [InlineData("/T (SECRETTITLE) /A 9 0 R", "<< /O /Table /Summary (SECRETSUMMARY) >>")]
    [InlineData("/T (SECRETTITLE) /A [9 0 R]", "<< /O /Table /Summary (SECRETSUMMARY) >>")]
    [InlineData("/T (SECRETTITLE) /A << /O /UserProperties /P [<< /N (Owner) /V (SECRETSUMMARY) >>] >>", "null")]
    [InlineData("/T (SECRETTITLE) /A [<< /O /UserProperties /P [<< /N (Owner) /V (SECRETSUMMARY) >>] >> 0]", "null")]
    public void AnElementWithRedactedContentLosesItsTitleAndAttributeText(string entries, string extraObject)
    {
        PdfDocument document = PdfDocument.Open(DescribedElement(entries, extraObject));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 620))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(output, "SECRETTITLE");
        AssertDescriptionAbsent(output, "SECRETSUMMARY");
        string kept = string.Concat(output.CrossReferences.Values.Where(entry => entry.Type is PdfCrossReferenceEntryType.InUse
            or PdfCrossReferenceEntryType.Compressed).Select(entry => output.Resolve(entry.ObjectNumber))
            .OfType<PdfDictionary>().Select(item => Encoding.Latin1.GetString(PdfObjectWriter.Write(item))));
        Assert.Contains("KEPTTITLE", kept, StringComparison.Ordinal);
        Assert.Contains("KEPTSUMMARY", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationRejectsAnEmptyStructureElementThatKeepsItsTitle()
    {
        byte[] output = CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            Stream("0 0 m"),
            "<< /Type /StructTreeRoot /K [6 0 R] >>",
            "<< /Type /StructElem /S /Table /P 5 0 R /Pg 3 0 R /T (LEFTOVER) /K [] >>"
        ]);

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(output,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 50, 50))], [], expectMetadataCleared: false);

        Assert.Contains(report.Findings, finding => finding.Code == "StructureDescription");
    }

    // A marked-content reference into a form names the form's stream, as forRemovePage.pdf does.
    private static byte[] TaggedForm(string kids) => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>",
        Stream("/Fm Do"),
        Stream("/P << /MCID 0 >> BDC BT /F1 12 Tf 100 600 Td (SECRETTEXT) Tj ET EMC\nBT /F1 12 Tf 100 300 Td (PUBLICTEXT) Tj ET",
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 6 0 R >> >>"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [8 0 R] >>",
        $"<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /Alt (SECRETALT) /K {kids} >>",
        "<< /MCID 0 /Pg 3 0 R /Stm 5 0 R /Type /MCR >>"
    ]);

    [Theory]
    [InlineData("<< /MCID 0 /Pg 3 0 R /Stm 5 0 R /Type /MCR >>")]
    [InlineData("[<< /MCID 0 /Pg 3 0 R /Stm 5 0 R /Type /MCR >>]")]
    [InlineData("[9 0 R]")]
    [InlineData("9 0 R")]
    public void ARedactionInATaggedFormFinishesWithTheReferenceDropped(string kids)
    {
        PdfDocument document = PdfDocument.Open(TaggedForm(kids));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 620))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.Contains("PUBLICTEXT", new PdfPageContentReader(output).Read(0).Text, StringComparison.Ordinal);
        AssertDescriptionAbsent(output, "SECRETTEXT");
        AssertDescriptionAbsent(output, "SECRETALT");
        Assert.False(output.CrossReferences.Values.Any(entry => entry.Type is PdfCrossReferenceEntryType.InUse
            or PdfCrossReferenceEntryType.Compressed && output.Resolve(entry.ObjectNumber) is PdfDictionary item
            && item.ContainsKey(new PdfName("Stm"u8))), "A reference to the original form is still in the file.");
    }

    [Fact]
    public void AnyOtherReferenceToARetiredFormStaysRefused()
    {
        // The open action names the form's stream, and nothing the redaction knows can vouch for that.
        byte[] source = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(TaggedForm("[]"))
            .Replace("/StructTreeRoot 7 0 R", "/OpenAction 5 0 R /StructTreeRoot 7 0 R"));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);

        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 620))]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedSourceStream");
    }

    private const string WordInRegion = "\nBT /F1 12 Tf 300 400 Td (SECRETTEXT) Tj ET";
    private static readonly PdfRedactionRegion WordRegion = new(0, new PdfContentBounds(290, 390, 400, 420));

    // A page with a word in the region, a pattern, a soft mask with nothing near the region, and a form.
    private static byte[] PathPage(string content) => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> /Pattern << /P 6 0 R >> " +
            "/ExtGState << /M << /SMask << /Type /Mask /S /Luminosity /G 7 0 R >> >> >> /XObject << /Fm 8 0 R >> >> /Contents 5 0 R >>",
        Helvetica,
        Stream(content + WordInRegion),
        Stream("0 0 1 rg 0 0 5 5 re f", "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >>"),
        Stream("1 g 0 0 10 10 re f", "/Type /XObject /Subtype /Form /BBox [0 0 10 10] /Group << /S /Transparency /CS /DeviceGray >> /Resources << >>"),
        Stream("0 0 612 792 re f", "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << >>")
    ]);

    private static string[] Reasons(string content)
    {
        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(PdfDocument.Open(PathPage(content)), [WordRegion]);
        return [.. Assert.Single(plan.Pages).RasterReasons];
    }

    [Theory]
    [InlineData("0 w 10 10 m 100 10 l S")]
    [InlineData("0 w 10 10 m 100 10 l 100 100 l s")]
    [InlineData("0 G 0 w 0 0 1 rg 10 10 50 50 re B")]
    [InlineData("0 0 m 0 w 10 10 m 100 10 l 50 50 l h S")]
    [InlineData("10 10 50 50 re 500 700 50 50 re f")]
    [InlineData("10 10 m 60 10 l 60 60 l h 500 700 m 550 700 l 550 750 l h f")]
    [InlineData("1 w 10 10 50 50 re 500 700 50 50 re S")]
    [InlineData("q 0 700 612 92 re W n 0.9 g 0 0 612 792 re f Q")]
    [InlineData("q 0 700 612 92 re W n 1 w 300 300 m 300 500 l S Q")]
    [InlineData("q 0 700 612 92 re W n /Fm Do Q")]
    [InlineData("q 0 700 612 92 re W n 10 10 m 600 10 l 600 780 l h f Q")]
    public void APathThatPaintsNothingInTheRegionNeedsNoPageImage(string content)
    {
        byte[] source = PathPage(content);
        Assert.Empty(Reasons(content));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(PdfDocument.Open(source), [WordRegion]);

        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("SECRETTEXT", new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0 w 280 400 m 410 400 l S")]
    [InlineData("0 w 280 395 m 410 395 l 410 500 l S")]
    [InlineData("0 w 280 200 m 350 400 l 500 200 l S")]
    [InlineData("10 10 50 50 re 280 395 200 10 re f")]
    [InlineData("500 700 50 50 re 350 380 20 30 re f")]
    [InlineData("10 10 m 60 10 l 60 60 l h 280 395 m 480 395 l 480 405 l h f")]
    [InlineData("1 w 10 10 50 50 re 280 395 100 10 re S")]
    [InlineData("q 0 300 612 150 re W n 0.9 g 350 380 20 30 re f Q")]
    public void APathThatReallyCrossesTheRegionStillAsks(string content) =>
        Assert.Equal(["Vector"], Reasons(content));

    [Fact]
    public void AHairlineInsideTheRegionIsRemovedLikeAnyOtherPath()
    {
        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(PdfDocument.Open(PathPage("0 w 300 395 m 380 395 l S")), [WordRegion]);

        PdfRedactionPageAnalysis page = Assert.Single(plan.Pages);
        Assert.Equal(PdfRedactionPageMethod.Content, page.Method);
        Assert.Equal(1, page.RemovedPaths);
    }

    [Fact]
    public void APathClippedAwayButWhollyInTheRegionStillLeaves()
    {
        const string content = "q 0 700 612 92 re W n 300 395 50 10 re f Q";

        PdfContentRedactionResult result = PdfContentRedaction.Apply(PdfDocument.Open(PathPage(content)), [WordRegion]);

        Assert.Equal(1, Assert.Single(result.Plan.Pages).RemovedPaths);
        Assert.DoesNotContain(new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0).Paths,
            path => path.BoundingBox == new PdfContentBounds(300, 395, 350, 405));
    }

    [Theory]
    [InlineData("0.9 g 0 0 612 792 re f")]
    [InlineData("0.9 g 0 0 612 792 re F")]
    [InlineData("0.9 g 0 0 612 792 re f*")]
    [InlineData("0.9 g 0 0 m 612 0 l 612 792 l 0 792 l h f")]
    [InlineData("0.9 g 0 0 m 612 0 l 612 792 l 0 792 l f")]
    [InlineData("0.9 g 0 0 m 612 0 l 612 792 l 0 792 l 0 0 l f")]
    [InlineData("q 0.5 0 0 0.5 0 0 cm 0.9 g 0 0 1224 1584 re f Q")]
    [InlineData("q 0 1 -1 0 612 0 cm 0.9 g 0 0 792 612 re f Q")]
    [InlineData("/DeviceCMYK cs 0 0 0 0.1 sc 0 0 612 792 re f")]
    [InlineData("0.9 g q 100 100 412 592 re W n 0 0 612 792 re f Q")]
    [InlineData("0.9 g /Fm Do")]
    public void APlainBackgroundRectangleNeedsNoPageImage(string content)
    {
        byte[] source = PathPage(content);
        Assert.Empty(Reasons(content));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(PdfDocument.Open(source), [WordRegion]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("SECRETTEXT", new PdfPageContentReader(output).Read(0).Text, StringComparison.Ordinal);
        Assert.Equal(0, Assert.Single(result.Plan.Pages).RemovedPaths);
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(612, 792));
        Assert.True(IsBlack(rendered, 340, 792 - 405), "The cover is missing.");
        Assert.False(IsBlack(rendered, 150, 792 - 150), "The background is gone.");
    }

    [Theory]
    [InlineData("/Pattern cs /P scn 0 0 612 792 re f")]
    [InlineData("/Pattern cs /P scn /Fm Do")]
    [InlineData("/M gs 0.9 g 0 0 612 792 re f")]
    [InlineData("/M gs 0.9 g /Fm Do")]
    [InlineData("2 w 0 0 612 792 re S")]
    [InlineData("2 w 0 0 612 792 re s")]
    [InlineData("0.9 g 2 w 0 0 612 792 re B")]
    [InlineData("0.9 g 2 w 0 0 612 792 re b*")]
    [InlineData("0 w 0 0 612 792 re S")]
    public void DetailInTheFillOrAtAnEdgeStillAsks(string content) =>
        Assert.Equal(["Vector"], Reasons(content));

    [Theory]
    [InlineData("0.9 g 0 0 350 792 re f")]
    [InlineData("0.9 g 350 0 262 792 re f")]
    [InlineData("0.9 g 0 0 612 400 re f")]
    [InlineData("0.9 g 295 395 50 500 re f")]
    [InlineData("0.9 g q 1 1 -1 1 306 -100 cm 0 0 500 500 re f Q")]
    [InlineData("0.9 g 0 0 612 792 re 10 10 20 20 re f")]
    [InlineData("0.9 g 0 0 m 612 0 l 612 792 l 0 792 l 306 400 l f")]
    [InlineData("0.9 g q 0 0 350 792 re W n 0 0 612 792 re f Q")]
    [InlineData("0.9 g q 0 0 m 612 0 l 306 792 l h W n 0 0 612 792 re f Q")]
    [InlineData("0.9 g q BT 7 Tr /F1 12 Tf 300 400 Td (X) Tj ET 0 0 612 792 re f Q")]
    [InlineData("0.9 g 0 0 612 792 re W f")]
    public void ARectangleThatDoesNotHoldTheRegionStillAsks(string content) =>
        Assert.Contains("Vector", Reasons(content));

    [Fact]
    public void ARectangleHoldingOneRegionAndCrossingAnotherStillAsks()
    {
        PdfDocument document = PdfDocument.Open(PathPage("0.9 g 0 300 612 200 re f"));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document,
            [WordRegion, new PdfRedactionRegion(0, new PdfContentBounds(100, 490, 200, 510))]);

        Assert.Equal(["Vector"], Assert.Single(plan.Pages).RasterReasons);
    }

    private static void AssertKeptLettersUnmoved(PdfDocument before, PdfDocument after, int pageIndex,
        IReadOnlyList<PdfRedactionRegion> regions)
    {
        var expected = new PdfPageContentReader(before).Read(pageIndex).Letters
            .Where(letter => !regions.Any(region => PdfContentRedaction.Overlaps(letter.BoundingBox, region.Bounds)))
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToArray();
        var actual = new PdfPageContentReader(after).Read(pageIndex).Letters
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static PdfRedactionRegion Region(PdfDocument document, int pageIndex, string word, int occurrence = 0)
    {
        IReadOnlyList<PdfExtractedLetter> letters = new PdfPageContentReader(document).Read(pageIndex).Letters;
        string text = string.Concat(letters.Select(letter => letter.Value));
        int start = -1;
        for (int index = 0; index <= occurrence; index++)
            start = text.IndexOf(word, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{word}' was not found.");
        var selected = new List<PdfExtractedLetter>();
        int position = 0;
        foreach (PdfExtractedLetter letter in letters)
        {
            if (position < start + word.Length && position + letter.Value.Length > start) selected.Add(letter);
            position += letter.Value.Length;
        }
        return new PdfRedactionRegion(pageIndex, new PdfContentBounds(
            selected.Min(letter => letter.BoundingBox.Left), selected.Min(letter => letter.BoundingBox.Bottom),
            selected.Max(letter => letter.BoundingBox.Right), selected.Max(letter => letter.BoundingBox.Top)));
    }

    private static string Letters(PdfDocument document, int pageIndex) =>
        string.Concat(new PdfPageContentReader(document).Read(pageIndex).Letters.Select(letter => letter.Value));

    private static bool HasSourceImage(PdfDocument document)
    {
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse
                or PdfCrossReferenceEntryType.Compressed)) continue;
            if (document.Resolve(entry.ObjectNumber) is PdfStream stream
                && stream.Dictionary.TryGetValue(new PdfName("Width"u8), out PdfObject? width)
                && width is PdfInteger { Value: 2 }) return true;
        }
        return false;
    }

    private static (byte Red, byte Green, byte Blue) Pixel(PdfRenderedPage page, int x, int y)
    {
        int offset = (y * page.Width + x) * 4;
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static bool IsBlack(PdfRenderedPage page, int x, int y)
    {
        (byte red, byte green, byte blue) = Pixel(page, x, y);
        return red < 40 && green < 40 && blue < 40;
    }

    private static byte[] SinglePage(string content, string pageEntries = "/MediaBox [0 0 612 792]") =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R {pageEntries} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content)
        ]);

    private static byte[] ImagePage(string content, int rotate, string imageEntries = "") =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Rotate {rotate} /Resources << /Font << /F1 4 0 R >> /XObject << /Im1 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content),
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 {imageEntries} /Length 12 >>\nstream\n"
                + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]) + "\nendstream"
        ]);

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] CreatePdf(List<string> objects, int? info = null)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        string infoEntry = info is int number ? $" /Info {number} 0 R" : string.Empty;
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{infoEntry} >>\n")
            .Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
