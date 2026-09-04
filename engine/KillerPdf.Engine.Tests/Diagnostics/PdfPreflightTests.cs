using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Diagnostics;

public sealed class PdfPreflightTests
{
    [Fact]
    public void ProfileRoundTripsAndRunsOnlySelectedChecks()
    {
        var profile = new PdfPreflightProfile("Language check",
            [PdfPreflightCheck.DocumentLanguage]);
        PdfPreflightProfile restored = PdfPreflightProfile.FromJson(profile.ToJson());
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        PdfPreflightReport report = PdfPreflightRunner.Run(source, restored);

        Assert.Equal("Language check", report.ProfileName);
        Assert.Equal(300, restored.MinimumImageDpi);
        PdfPreflightFinding finding = Assert.Single(report.Findings);
        Assert.Equal("Accessibility.MissingDocumentLanguage", finding.Code);
        Assert.DoesNotContain(report.Findings,
            item => item.Code.Contains("Structure", StringComparison.Ordinal));
        using JsonDocument json = JsonDocument.Parse(report.ToJson());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void LanguageCorrectionIsPreviewedAppliedAndVerified()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        PdfDocument document = PdfDocument.Open(source);

        PdfPreflightCorrectionPlan plan =
            PdfPreflightCorrectionPlan.SetDocumentLanguage(document, "en-US");
        byte[] correctedBytes = plan.Apply();
        PdfDocument corrected = PdfDocument.Open(correctedBytes);
        PdfPreflightCorrectionPlan unchanged =
            PdfPreflightCorrectionPlan.SetDocumentLanguage(corrected, "EN-us");

        Assert.Equal(PdfPreflightCorrectionKind.SetDocumentLanguage, plan.Kind);
        Assert.True(plan.ChangesDocument);
        Assert.Equal("en-US", PdfDocumentInformation.Read(corrected).Language);
        Assert.False(unchanged.ChangesDocument);
        Assert.Equal(correctedBytes, unchanged.Apply());
        Assert.Throws<ArgumentException>(() =>
            PdfPreflightCorrectionPlan.SetDocumentLanguage(document, "not_a_language"));
    }

    [Fact]
    public void InvalidProductionBoxCorrectionIsPreviewedAppliedAndVerified()
    {
        PdfDocument valid = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(200, 300).Build());
        (int ObjectNumber, PdfDictionary Dictionary) page = valid.CrossReferences.Values
            .Select(entry => (entry.ObjectNumber, Object: valid.Resolve(entry.ObjectNumber)))
            .Where(item => item.Object is PdfDictionary dictionary
                && dictionary.TryGetValue(new PdfName("Type"u8), out PdfObject? type)
                && type is PdfName name && name.ValueAsLatin1() == "Page")
            .Select(item => (item.ObjectNumber, (PdfDictionary)item.Object)).Single();
        PdfDictionary changedPage = new(page.Dictionary.Append(new(
            new PdfName("TrimBox"u8), new PdfArray([
                new PdfInteger(-5), new PdfInteger(0),
                new PdfInteger(200), new PdfInteger(300)]))));
        byte[] source = new PdfIncrementalUpdateBuilder(valid)
            .ReplaceObject(page.ObjectNumber, changedPage).Build();
        PdfDocument document = PdfDocument.Open(source);

        PdfPreflightCorrectionPlan plan =
            PdfPreflightCorrectionPlan.ClearProductionPageBox(
                document, 0, PdfPageBox.Trim);
        byte[] correctedBytes = plan.Apply();
        PdfDocument corrected = PdfDocument.Open(correctedBytes);
        PdfPreflightCorrectionPlan unchanged =
            PdfPreflightCorrectionPlan.ClearProductionPageBox(
                corrected, 0, PdfPageBox.Trim);

        Assert.Equal(PdfPreflightCorrectionKind.ClearProductionPageBox, plan.Kind);
        Assert.Equal(0, plan.PageIndex);
        Assert.Equal(PdfPageBox.Trim, plan.PageBox);
        Assert.True(plan.ChangesDocument);
        Assert.DoesNotContain(PdfPreflightRunner.Run(correctedBytes,
                new PdfPreflightProfile("Page boxes", [PdfPreflightCheck.PageBoxes])).Findings,
            finding => finding.Code == "PageBoxes.TrimOutsideMediaBox");
        Assert.False(unchanged.ChangesDocument);
        Assert.Equal(correctedBytes, unchanged.Apply());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPreflightCorrectionPlan.ClearProductionPageBox(
                document, 0, PdfPageBox.Crop));
    }

    [Fact]
    public void MissingMetadataCorrectionPreservesExistingDocumentInformation()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Author = "KillerPDF",
                Subject = "Press proof",
                Keywords = "proof, press",
                Creator = "Source application",
                Producer = "Source producer"
            }).AddBlankPage().Build());

        PdfPreflightCorrectionPlan plan =
            PdfPreflightCorrectionPlan.SetDocumentMetadata(
                document, PdfPreflightMetadataField.Title, "Production proof");
        PdfDocument corrected = PdfDocument.Open(plan.Apply());
        PdfDocumentInformation information = PdfDocumentInformation.Read(corrected);

        Assert.Equal(PdfPreflightCorrectionKind.SetDocumentMetadata, plan.Kind);
        Assert.Equal(PdfPreflightMetadataField.Title, plan.MetadataField);
        Assert.True(plan.ChangesDocument);
        Assert.Equal("Production proof", information.Title);
        Assert.Equal("KillerPDF", information.Author);
        Assert.Equal("Press proof", information.Subject);
        Assert.Equal("proof, press", information.Keywords);
        Assert.Equal("Source application", information.Creator);
        Assert.Equal("Source producer", information.Producer);
        Assert.Throws<InvalidOperationException>(() =>
            PdfPreflightCorrectionPlan.SetDocumentMetadata(
                corrected, PdfPreflightMetadataField.Title, "Replacement"));
        Assert.False(PdfPreflightCorrectionPlan.SetDocumentMetadata(
            corrected, PdfPreflightMetadataField.Title, "Production proof").ChangesDocument);
    }

    [Fact]
    public void TaggedProfileReportsBothRequiredCatalogDeclarations()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        var profile = new PdfPreflightProfile("Tagged PDF",
            [PdfPreflightCheck.TaggedStructure]);

        PdfPreflightReport report = PdfPreflightRunner.Run(source, profile);

        Assert.Equal([
            "Accessibility.MissingStructureTree",
            "Accessibility.DocumentNotMarked"
        ], report.Findings.Select(finding => finding.Code));
    }

    [Fact]
    public void FormAccessibilityReportsOnlyFieldsWithoutDescriptions()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "undocumented", 10, 10, 100, 20)
            .AddTextField(0, "documented", 10, 40, 100, 20, fieldMetadata:
                new PdfFormFieldMetadata { Tooltip = "Account name" }).Build();
        var profile = new PdfPreflightProfile("Accessible forms",
            [PdfPreflightCheck.FormAccessibility]);

        PdfPreflightFinding finding = Assert.Single(
            PdfPreflightRunner.Run(source, profile).Findings);

        Assert.Equal("Accessibility.MissingFormFieldDescription", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
        Assert.Contains("undocumented", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkAccessibilityCanRunWithoutTaggedStructureFindings()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddUriLink(0, 10, 10, 100, 20, "https://example.test/missing")
            .AddUriLink(0, 10, 40, 100, 20, "https://example.test/described",
                contents: "Documentation").Build();
        var profile = new PdfPreflightProfile("Accessible links",
            [PdfPreflightCheck.LinkAccessibility]);

        PdfPreflightFinding finding = Assert.Single(
            PdfPreflightRunner.Run(source, profile).Findings);

        Assert.Equal("Accessibility.MissingLinkDescription", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
        Assert.DoesNotContain("MissingStructureTree", finding.Code,
            StringComparison.Ordinal);
        Assert.Contains("link annotation", finding.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneralProfileReportsStructuralDamageWithoutThrowing()
    {
        PdfPreflightReport report = PdfPreflightRunner.Run(
            "not a pdf"u8.ToArray(), PdfPreflightProfile.General);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings,
            finding => finding.Code == "Structural.InvalidHeader");
    }

    [Fact]
    public void TextReportIncludesProfileResultSeverityAndSourceLocation()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(10, 10, 20, 20).Fill();
        var profile = new PdfPreflightProfile("Color review",
            [PdfPreflightCheck.ColorUsage]);

        string report = PdfPreflightRunner.Run(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build(), profile).ToText();

        Assert.Contains("Preflight profile: Color review", report);
        Assert.Contains("Result: Passed", report);
        Assert.Contains("[Warning] ColorUsage.DeviceRgb | Page 1:", report);
    }

    [Fact]
    public void PrintProductionChecksPageBoxesAndOutputIntent()
    {
        byte[] withoutIntent = new PdfDocumentBuilder().AddBlankPage(200, 300).Build();
        byte[] withIntent = new PdfDocumentBuilder()
            .SetOutputIntent(PdfIccProfile.Load(Profile()), "Test RGB")
            .AddBlankPage(200, 300).Build();

        PdfPreflightReport missing = PdfPreflightRunner.Run(
            withoutIntent, PdfPreflightProfile.PrintProduction);
        PdfPreflightReport complete = PdfPreflightRunner.Run(
            withIntent, PdfPreflightProfile.PrintProduction);

        Assert.Contains(missing.Findings, finding => finding.Code == "OutputIntent.Missing");
        Assert.DoesNotContain(complete.Findings,
            finding => finding.Code.StartsWith("PageBoxes.", StringComparison.Ordinal)
                || finding.Code.StartsWith("OutputIntent.", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOutputIntentCorrectionIsPreviewedAppliedAndVerified()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIccProfile profile = PdfIccProfile.Load(Profile());

        PdfPreflightCorrectionPlan plan = PdfPreflightCorrectionPlan.SetOutputIntent(
            document, profile, "Test RGB");
        byte[] correctedBytes = plan.Apply();
        PdfOutputIntentInformation saved = Assert.Single(
            PdfOutputIntentInspection.Inspect(PdfDocument.Open(correctedBytes)));

        Assert.Equal(PdfPreflightCorrectionKind.SetOutputIntent, plan.Kind);
        Assert.True(plan.ChangesDocument);
        Assert.Equal("Test RGB", plan.OutputConditionIdentifier);
        Assert.Equal("Test RGB", saved.OutputConditionIdentifier);
        Assert.Equal(profile.Data.ToArray(), saved.Profile.Data.ToArray());
        Assert.DoesNotContain(PdfPreflightRunner.Run(correctedBytes,
                new PdfPreflightProfile("Output intent", [PdfPreflightCheck.OutputIntent])).Findings,
            finding => finding.Code.StartsWith("OutputIntent.", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() =>
            PdfPreflightCorrectionPlan.SetOutputIntent(
                PdfDocument.Open(correctedBytes), profile, "Replacement"));
    }

    [Fact]
    public void OutputIntentCheckRejectsMalformedIccProfileBytes()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .SetOutputIntent(PdfIccProfile.Load(Profile()), "Test RGB")
            .AddBlankPage().Build());
        int profileObject = original.CrossReferences.Values
            .Where(entry => entry.Type == PdfCrossReferenceEntryType.InUse)
            .Select(entry => (entry.ObjectNumber, Object: original.Resolve(entry.ObjectNumber)))
            .Where(item => item.Object is PdfStream stream
                && stream.Dictionary.ContainsKey(new PdfName("N"u8)))
            .Select(item => item.ObjectNumber).Single();
        byte[] malformed = new PdfIncrementalUpdateBuilder(original).ReplaceObject(
            profileObject,
            new PdfStream(new PdfDictionary([
                new(new PdfName("N"u8), new PdfInteger(3))]), "not an ICC profile"u8)).Build();
        var profile = new PdfPreflightProfile("Output intent", [PdfPreflightCheck.OutputIntent]);

        PdfPreflightFinding finding = Assert.Single(
            PdfPreflightRunner.Run(malformed, profile).Findings);

        Assert.Equal("OutputIntent.Invalid", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Error, finding.Severity);
    }

    [Fact]
    public void PageBoxCheckReportsExplicitProductionBoxesOutsideMediaBox()
    {
        PdfDocument original = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 300).Build());
        (int ObjectNumber, PdfDictionary Dictionary) page = original.CrossReferences.Values
            .Select(entry => (entry.ObjectNumber, Object: original.Resolve(entry.ObjectNumber)))
            .Where(item => item.Object is PdfDictionary dictionary
                && dictionary.TryGetValue(new PdfName("Type"u8), out PdfObject? type)
                && type is PdfName name && name.ValueAsLatin1() == "Page")
            .Select(item => (item.ObjectNumber, (PdfDictionary)item.Object)).Single();
        PdfDictionary changedPage = new(page.Dictionary.Append(new(
            new PdfName("TrimBox"u8), new PdfArray([
                new PdfInteger(-5), new PdfInteger(0),
                new PdfInteger(200), new PdfInteger(300)]))));
        byte[] source = new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(page.ObjectNumber, changedPage).Build();
        var profile = new PdfPreflightProfile("Page boxes", [PdfPreflightCheck.PageBoxes]);

        PdfPreflightFinding finding = Assert.Single(
            PdfPreflightRunner.Run(source, profile).Findings);

        Assert.Equal("PageBoxes.TrimOutsideMediaBox", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Error, finding.Severity);
        Assert.Equal(0, finding.PageIndex);
        Assert.Equal(page.ObjectNumber, finding.ObjectNumber);
    }

    [Fact]
    public void DeclaredConformanceAcceptsEngineAuthoredPdfA4()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "PDF/A-4" })
            .SetOutputIntent(PdfIccProfile.Load(Profile()), "Test RGB")
            .EnablePdfA4Conformance()
            .AddBlankPage()
            .Build();
        var profile = new PdfPreflightProfile("Declared conformance",
            [PdfPreflightCheck.ConformanceDeclarations]);

        PdfPreflightReport report = PdfPreflightRunner.Run(source, profile);

        Assert.Empty(report.Findings);
        Assert.Contains("conformanceDeclarations", profile.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredPdfXChecksUniversalBoundariesBeforeReportingUnsupportedValidation()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "PDF/X sample" })
            .AddBlankPage()
            .Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(original.Resolve(
            Assert.IsType<PdfIndirectReference>(original.Trailer[new PdfName("Root"u8)])));
        PdfIndirectReference metadataReference = Assert.IsType<PdfIndirectReference>(
            catalog[new PdfName("Metadata"u8)]);
        PdfStream metadata = Assert.IsType<PdfStream>(original.Resolve(metadataReference));
        byte[] xmp = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:pdfx=\"http://ns.adobe.com/pdfx/1.3/\"><pdfx:GTS_PDFXVersion>PDF/X-4</pdfx:GTS_PDFXVersion></rdf:Description></rdf:RDF></x:xmpmeta>");
        byte[] source = new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(metadataReference.ObjectNumber,
                new PdfStream(metadata.Dictionary, xmp))
            .Build();
        var profile = new PdfPreflightProfile("Declared PDF/X",
            [PdfPreflightCheck.ConformanceDeclarations]);

        PdfPreflightReport report = PdfPreflightRunner.Run(source, profile);

        Assert.Equal([
            "Conformance.PdfX.OutputIntent.Missing",
            "Conformance.PdfXValidationUnavailable"
        ], report.Findings.Select(finding => finding.Code));
        Assert.Equal(PdfDiagnosticSeverity.Error, report.Findings[0].Severity);
        Assert.Equal(PdfDiagnosticSeverity.Unsupported, report.Findings[1].Severity);
        Assert.Equal(metadataReference.ObjectNumber, report.Findings[1].ObjectNumber);
    }

    [Fact]
    public void ImageResolutionUsesTheProfileThresholdAndPageIndex()
    {
        PdfImage image = PdfImage.FromRgb(100, 100, new byte[30_000]);
        byte[] source = new PdfDocumentBuilder().AddPage(300, 300,
            new PdfContentStreamBuilder().DrawImage(image, 20, 30, 144, 72)).Build();
        var profile = new PdfPreflightProfile("Newsprint",
            [PdfPreflightCheck.ImageResolution], minimumImageDpi: 75);

        PdfPreflightProfile restored = PdfPreflightProfile.FromJson(profile.ToJson());
        PdfPreflightReport report = PdfPreflightRunner.Run(source, restored);

        Assert.Equal(75, restored.MinimumImageDpi);
        PdfPreflightFinding finding = Assert.Single(report.Findings);
        Assert.Equal("ImageResolution.BelowMinimum", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Warning, finding.Severity);
        Assert.Equal(0, finding.PageIndex);
        Assert.Contains("50 by 100 DPI", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorUsageAppliesShareableMaximumInkCoverage()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillCmyk(0.8, 0.8, 0.8, 0.8)
            .Rectangle(10, 10, 50, 50).Fill();
        var profile = new PdfPreflightProfile("Ink limit",
            [PdfPreflightCheck.ColorUsage], maximumInkCoveragePercent: 310);

        PdfPreflightProfile restored = PdfPreflightProfile.FromJson(profile.ToJson());
        PdfPreflightFinding finding = Assert.Single(PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build(), restored).Findings);

        Assert.Equal(310, restored.MaximumInkCoveragePercent);
        Assert.Equal("ColorUsage.InkCoverageAboveMaximum", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Warning, finding.Severity);
        Assert.Equal(0, finding.PageIndex);
        Assert.Contains("320% total ink", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FontEmbeddingFindsUnembeddedFontsAndAcceptsEmbeddedFonts()
    {
        var standardContent = new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12).ShowLatin1Text("A").EndText();
        TrueTypeFont embeddedFont = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        var embeddedContent = new PdfContentStreamBuilder()
            .BeginText().SetFont(embeddedFont, 12).ShowUnicodeText("A").EndText();
        var profile = new PdfPreflightProfile("Embedded fonts",
            [PdfPreflightCheck.FontEmbedding]);

        PdfPreflightReport standard = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, standardContent).Build(), profile);
        PdfPreflightReport embedded = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, embeddedContent).Build(), profile);

        PdfPreflightFinding finding = Assert.Single(standard.Findings);
        Assert.Equal("FontEmbedding.NotEmbedded", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.Contains("Helvetica", finding.Message, StringComparison.Ordinal);
        Assert.Empty(embedded.Findings);
    }

    [Fact]
    public void TransparencyFindsOpacityAndBlendModes()
    {
        var content = new PdfContentStreamBuilder()
            .SetGraphicsState(new PdfGraphicsState(
                fillOpacity: 0.5, blendMode: PdfBlendMode.Multiply))
            .Rectangle(10, 10, 50, 50).Fill();
        var profile = new PdfPreflightProfile("Transparency",
            [PdfPreflightCheck.Transparency]);

        PdfPreflightReport report = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build(), profile);

        PdfPreflightFinding finding = Assert.Single(report.Findings);
        Assert.Equal("Transparency.GraphicsState", finding.Code);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void ColorUsageFindsDeviceRgbAndOverprint()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .SetGraphicsState(new PdfGraphicsState(fillOverprint: true))
            .Rectangle(10, 10, 50, 50).Fill();
        var profile = new PdfPreflightProfile("Color usage",
            [PdfPreflightCheck.ColorUsage]);

        PdfPreflightReport report = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build(), profile);

        Assert.Equal(["ColorUsage.DeviceRgb", "ColorUsage.Overprint"],
            report.Findings.Select(finding => finding.Code));
        Assert.All(report.Findings, finding => Assert.Equal(0, finding.PageIndex));
    }

    [Fact]
    public void ColorUsageValidatesEmbeddedIccProfileBytes()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillIccColor(PdfIccProfile.Load(Profile()), 0.1, 0.2, 0.3)
            .Rectangle(10, 10, 50, 50).Fill();
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(original.Resolve(
            Assert.IsType<PdfIndirectReference>(original.Trailer[new PdfName("Root"u8)])));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(original.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)])));
        PdfArray kids = Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(original.Resolve(
            Assert.IsType<PdfIndirectReference>(Assert.Single(kids))));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(
            page[new PdfName("Resources"u8)]);
        PdfDictionary spaces = Assert.IsType<PdfDictionary>(
            resources[new PdfName("ColorSpace"u8)]);
        PdfArray colorSpace = Assert.IsType<PdfArray>(Assert.Single(spaces).Value);
        PdfIndirectReference profileReference = Assert.IsType<PdfIndirectReference>(colorSpace[1]);
        PdfStream profile = Assert.IsType<PdfStream>(original.Resolve(profileReference));
        var invalidProfile = new PdfStream(profile.Dictionary, new byte[132]);
        byte[] source = new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(profileReference.ObjectNumber, invalidProfile).Build();
        var preflight = new PdfPreflightProfile("Color usage",
            [PdfPreflightCheck.ColorUsage]);

        PdfPreflightFinding finding = Assert.Single(
            PdfPreflightRunner.Run(source, preflight).Findings);

        Assert.Equal("ColorUsage.InvalidIccProfile", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
    }

    [Fact]
    public void OptionalContentCheckReportsEffectiveLayerStateAndObject()
    {
        var layer = new PdfOptionalContentGroup("Measurements", initiallyVisible: false,
            visibleWhenPrinting: true, visibleWhenExporting: false);
        byte[] source = new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().BeginOptionalContent(layer)
                .Rectangle(10, 10, 20, 20).Stroke().EndMarkedContent()).Build();
        var profile = new PdfPreflightProfile("Layers", [PdfPreflightCheck.OptionalContent]);

        PdfPreflightFinding finding = Assert.Single(PdfPreflightRunner.Run(source, profile).Findings);

        Assert.Equal("OptionalContent.LayerState", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Information, finding.Severity);
        Assert.Contains("visible=False", finding.Message, StringComparison.Ordinal);
        Assert.Contains("print=on", finding.Message, StringComparison.Ordinal);
        Assert.Contains("export=off", finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.ObjectNumber);
    }

    [Fact]
    public void DocumentMetadataReportsMissingFieldsAndAcceptsCompleteMetadata()
    {
        var profile = new PdfPreflightProfile("Document metadata",
            [PdfPreflightCheck.DocumentMetadata]);
        byte[] missing = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] complete = new PdfDocumentBuilder().SetMetadata(new PdfDocumentMetadata
        {
            Title = "Production proof",
            Author = "KillerPDF",
            Subject = "Preflight sample",
            Keywords = "preflight, proof"
        }).AddBlankPage().Build();

        PdfPreflightReport report = PdfPreflightRunner.Run(missing, profile);

        Assert.Equal([
            "Metadata.MissingTitle",
            "Metadata.MissingAuthor",
            "Metadata.MissingSubject",
            "Metadata.MissingKeywords"
        ], report.Findings.Select(finding => finding.Code));
        Assert.Equal(PdfDiagnosticSeverity.Warning, report.Findings[0].Severity);
        Assert.All(report.Findings.Skip(1), finding =>
            Assert.Equal(PdfDiagnosticSeverity.Information, finding.Severity));
        Assert.Empty(PdfPreflightRunner.Run(complete, profile).Findings);
    }

    [Fact]
    public void AttachmentSafetyReportsExecutableNamesAndPayloadSignaturesOnce()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("installer.exe", "plain text"u8.ToArray())
            .AddAttachment("notes.txt", new byte[] { (byte)'M', (byte)'Z', 0, 0 })
            .AddFileAttachmentAnnotation(0, 20, 20, 24, "notes.txt")
            .Build();

        PdfPreflightReport report = PdfPreflightRunner.Run(
            source, PdfPreflightProfile.Attachments);

        Assert.Equal([
            "Attachment.ExecutableFileName",
            "Attachment.ExecutableContent"
        ], report.Findings.Select(finding => finding.Code));
        Assert.Equal(PdfDiagnosticSeverity.Warning, report.Findings[0].Severity);
        Assert.Equal(PdfDiagnosticSeverity.Error, report.Findings[1].Severity);
        Assert.False(report.Passed);
        Assert.All(report.Findings, finding => Assert.NotNull(finding.ObjectNumber));
    }

    [Fact]
    public void AttachmentSafetyReportsEncryptedZipFamilyPayloads()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("protected.docx",
                new byte[] { (byte)'P', (byte)'K', 3, 4, 20, 0, 1, 0 })
            .Build();

        PdfPreflightFinding finding = Assert.Single(PdfPreflightRunner.Run(
            source, PdfPreflightProfile.Attachments).Findings);

        Assert.Equal("Attachment.EncryptedContent", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void MeasurementCheckReportsValidCalibrationScaleAndLocation()
    {
        var measurement = new PdfMeasurementProfile("Site plan", 0.125, "ft", 3);
        byte[] source = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddLine(0, new PdfPoint(20, 30), new PdfPoint(120, 30),
                contents: "12.500 ft", measurement: measurement)
            .Build();
        var profile = new PdfPreflightProfile("Measurements",
            [PdfPreflightCheck.MeasurementAnnotations]);

        PdfPreflightFinding finding = Assert.Single(
            PdfPreflightRunner.Run(source, profile).Findings);

        Assert.Equal("Measurement.Calibration", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Information, finding.Severity);
        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
        Assert.Contains("0.125 ft per PDF point, precision 3", finding.Message,
            StringComparison.Ordinal);
    }

    private static byte[] Profile()
    {
        byte[] result = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(result, 16);
        "acsp"u8.CopyTo(result.AsSpan(36, 4));
        return result;
    }
}
