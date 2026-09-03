using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPDF.Services;
using Xunit;
using ContentDocument = KillerPDF.Services.PdfContentDocument;

namespace KillerPDF.Tests;

public sealed class PdfEngineIntegrationTests
{
    [Fact]
    public void AddTextField_CreatesUniquelyNamedEditableWidgets()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-field-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());

            string first = PdfEngineIntegration.AddTextField(path, 0, 72, 600, 200, 24);
            string second = PdfEngineIntegration.AddTextField(path, 0, 72, 540, 200, 48);

            Assert.Equal("answer_001", first);
            Assert.Equal("answer_002", second);
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
            IReadOnlyList<PdfFormWidgetInfo> widgets =
                PdfEngineIntegration.ReadPageFormWidgets(document, 0);
            Assert.Equal(2, widgets.Count);
            Assert.Contains(widgets, widget => widget.FieldName == first);
            Assert.Contains(widgets, widget => widget.FieldName == second);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadBookmarks_ProvidesSidebarHierarchyWithoutPdfSharpObjects()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddNamedDestination("section", 1, PdfDestination.FitWidth(700))
            .AddBookmark("Chapter", 0, options: new PdfBookmarkOptions
            {
                Style = PdfBookmarkStyle.Bold,
                Color = new PdfRgbColor(0.2, 0.4, 0.6)
            })
            .AddNamedDestinationBookmark("Section", "section", 1)
            .Build();

        PdfBookmarkInfo chapter = Assert.Single(PdfEngineIntegration.ReadBookmarks(source));

        Assert.Equal("Chapter", chapter.Title);
        Assert.Equal(PdfBookmarkStyle.Bold, chapter.Style);
        Assert.Equal(new PdfRgbColor(0.2, 0.4, 0.6), chapter.Color);
        PdfBookmarkInfo section = Assert.Single(chapter.Children);
        Assert.Equal("section", section.NamedDestination);
        Assert.Equal(1, section.DestinationPageIndex);
    }

    [Fact]
    public void ReplaceBookmarks_WritesEditedHierarchyThroughEngine()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"killerpdf-bookmarks-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage().AddBlankPage()
                .AddNamedDestination("section", 1, PdfDestination.FitWidth(700))
                .AddBookmark("Old chapter", 0)
                .AddNamedDestinationBookmark("Section", "section", 1)
                .Build());
            IReadOnlyList<PdfBookmarkInfo> original =
                PdfEngineIntegration.ReadBookmarks(File.ReadAllBytes(path));
            PdfBookmarkInfo changed = original[0] with
            {
                Title = "Renamed chapter",
                Style = PdfBookmarkStyle.Italic,
                IsOpen = false
            };

            PdfEngineIntegration.ReplaceBookmarks(path, [changed]);

            PdfBookmarkInfo result = Assert.Single(
                PdfEngineIntegration.ReadBookmarks(File.ReadAllBytes(path)));
            Assert.Equal("Renamed chapter", result.Title);
            Assert.Equal(PdfBookmarkStyle.Italic, result.Style);
            Assert.False(result.IsOpen);
            PdfBookmarkInfo child = Assert.Single(result.Children);
            Assert.Equal("section", child.NamedDestination);
            Assert.Equal(1, child.DestinationPageIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadPageLinks_ResolvesViewerTargetsAndAnnotationIndices()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"killerpdf-links-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage(300, 400).AddBlankPage(300, 400)
                .AddUriLink(0, 10, 20, 80, 15, "https://example.com")
                .AddPageLink(0, 100, 40, 50, 20, 1)
                .Build());

            IReadOnlyList<PdfLinkInfo> links = PdfEngineIntegration.ReadPageLinks(path, 0);

            Assert.Equal(2, links.Count);
            Assert.Equal("https://example.com/", links[0].Uri);
            Assert.Equal(0, links[0].AnnotationIndex);
            Assert.Equal(1, links[1].DestinationPageIndex);
            Assert.Equal(1, links[1].AnnotationIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadPageFormWidgets_ProvidesInteractiveViewerState()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400)
            .AddTextField(0, "contact.email", 20, 300, 180, 24, "a@example.com", 10,
                new PdfTextFieldOptions { MaximumLength = 120 })
            .AddComboBoxOptions(0, "region", 20, 250, 140, 24,
            [
                new PdfChoiceOption("us", "United States"),
                new PdfChoiceOption("ca", "Canada")
            ], "ca")
            .Build());

        IReadOnlyList<PdfFormWidgetInfo> widgets =
            PdfEngineIntegration.ReadPageFormWidgets(document, 0);

        Assert.Equal(2, widgets.Count);
        PdfFormWidgetInfo text = widgets.Single(widget => widget.FieldName == "contact.email");
        Assert.Equal(PdfFormFieldKind.Text, text.FieldKind);
        Assert.Equal("a@example.com", text.Value);
        Assert.Equal(120, text.MaximumLength);
        PdfFormWidgetInfo choice = widgets.Single(widget => widget.FieldName == "region");
        Assert.Equal("ca", choice.Value);
        Assert.Equal("Canada", choice.Options[1].DisplayValue);
    }

    [Fact]
    public void CreateBlankDocument_AuthorsOneA4Page()
    {
        byte[] result = PdfEngineIntegration.CreateBlankDocument();

        IReadOnlyList<PdfPageInformation> pages =
            PdfPageInformation.Read(PdfDocument.Open(result));
        Assert.Single(pages);
        Assert.Equal(595, pages[0].Width);
        Assert.Equal(842, pages[0].Height);
    }

    [Fact]
    public void RebuildDocument_PreservesPagesAndOptionallyStripsRotations()
    {
        string input = Path.Combine(Path.GetTempPath(),
            $"killerpdf-rebuild-input-{Guid.NewGuid():N}.pdf");
        string preserved = Path.Combine(Path.GetTempPath(),
            $"killerpdf-rebuild-preserved-{Guid.NewGuid():N}.pdf");
        string stripped = Path.Combine(Path.GetTempPath(),
            $"killerpdf-rebuild-stripped-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfDocument authored = PdfDocument.Open(new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Repair fixture" })
                .AddBlankPage(200, 300).AddBlankPage(400, 500).Build());
            File.WriteAllBytes(input, new PdfIncrementalPageEditor(authored)
                .SetRotation(0, 90).SetRotation(1, 270).Build());

            PdfEngineIntegration.RebuildDocument(input, preserved);
            PdfEngineIntegration.RebuildDocument(input, stripped, stripRotations: true);

            IReadOnlyList<PdfPageInformation> preservedPages =
                PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(preserved)));
            IReadOnlyList<PdfPageInformation> strippedPages =
                PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(stripped)));
            Assert.Equal([90, 270], preservedPages.Select(page => page.Rotation));
            Assert.Equal([0, 0], strippedPages.Select(page => page.Rotation));
            Assert.Equal([200d, 400d], preservedPages.Select(page => page.Width));
        }
        finally
        {
            foreach (string path in new[] { input, preserved, stripped })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ResaveDocument_WritesDeterministicEngineOutput()
    {
        string input = Path.Combine(Path.GetTempPath(),
            $"killerpdf-resave-input-{Guid.NewGuid():N}.pdf");
        string first = Path.Combine(Path.GetTempPath(),
            $"killerpdf-resave-first-{Guid.NewGuid():N}.pdf");
        string second = Path.Combine(Path.GetTempPath(),
            $"killerpdf-resave-second-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Batch fixture" })
                .AddBlankPage(200, 300).Build());

            PdfEngineIntegration.ResaveDocument(input, first);
            PdfEngineIntegration.ResaveDocument(input, second);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
            IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(
                PdfDocument.Open(File.ReadAllBytes(first)));
            Assert.Single(pages);
            Assert.Equal(200, pages[0].Width);
            Assert.Equal(300, pages[0].Height);
        }
        finally
        {
            foreach (string path in new[] { input, first, second })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemoveAnnotation_RemovesOnlySelectedNativeAnnotation()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"killerpdf-remove-annotation-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage(200, 300)
                .AddUriLink(0, 10, 10, 50, 20, "https://example.com/first")
                .AddUriLink(0, 10, 40, 50, 20, "https://example.com/second")
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.RemoveAnnotation(path, 0, 0);

            byte[] result = File.ReadAllBytes(path);
            PdfDocument reopened = PdfDocument.Open(result);
            PdfArray annotations = Assert.IsType<PdfArray>(
                Page(reopened, 0)[new PdfName("Annots"u8)]);
            Assert.Single(annotations);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemapRotationsAfterPageTurns_UpdatesOnlyDistinctSelectedPages()
    {
        var rotations = new Dictionary<int, int>
        {
            [0] = 0,
            [1] = 90,
            [2] = 270,
        };

        PdfEngineIntegration.RemapRotationsAfterPageTurns(
            rotations, [0, 2, 2], 90);

        Assert.Equal(90, rotations[0]);
        Assert.Equal(90, rotations[1]);
        Assert.Equal(0, rotations[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfEngineIntegration.RemapRotationsAfterPageTurns(
                rotations, [3], 90));
    }

    [Fact]
    public void StripLinkAppearances_MakesLinksInvisibleAndPreservesTargets()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"killerpdf-strip-links-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage(200, 300)
                .AddUriLink(0, 10, 10, 80, 20, "https://example.com/",
                    new PdfLinkAppearance(borderWidth: 2,
                        color: new PdfRgbColor(1, 0, 0)))
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.StripLinkAppearances(path);

            byte[] result = File.ReadAllBytes(path);
            PdfDocument reopened = PdfDocument.Open(result);
            PdfArray annotations = Assert.IsType<PdfArray>(
                Page(reopened, 0)[new PdfName("Annots"u8)]);
            PdfDictionary link = Assert.IsType<PdfDictionary>(reopened.Resolve(
                Assert.IsType<PdfIndirectReference>(Assert.Single(annotations))));
            PdfDictionary borderStyle = Assert.IsType<PdfDictionary>(link[new PdfName("BS"u8)]);
            PdfArray border = Assert.IsType<PdfArray>(link[new PdfName("Border"u8)]);
            Assert.Equal(0, Assert.IsType<PdfInteger>(borderStyle[new PdfName("W"u8)]).Value);
            Assert.All(border, value => Assert.Equal(0, Assert.IsType<PdfInteger>(value).Value));
            Assert.False(link.ContainsKey(new PdfName("C"u8)));
            Assert.False(link.ContainsKey(new PdfName("AP"u8)));
            Assert.True(link.ContainsKey(new PdfName("A"u8)));
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AddSearchableTextLayers_WritesExtractableMultiscriptUnicode()
    {
        string input = Path.Combine(Path.GetTempPath(),
            $"killerpdf-ocr-input-{Guid.NewGuid():N}.pdf");
        string output = Path.Combine(Path.GetTempPath(),
            $"killerpdf-ocr-output-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage(300, 400).Build();
            File.WriteAllBytes(input, source);
            var words = new[]
            {
                new PdfEngineIntegration.SearchableWord("Hello", 10, 10, 80, 30),
                new PdfEngineIntegration.SearchableWord("বাংলা", 10, 40, 100, 65),
                new PdfEngineIntegration.SearchableWord("日本語", 10, 75, 100, 100),
                new PdfEngineIntegration.SearchableWord("中文", 10, 110, 80, 135),
            };

            int count = PdfEngineIntegration.AddSearchableTextLayers(
                input, output,
                [new PdfEngineIntegration.SearchablePage(300, 400, words)]);

            Assert.Equal(4, count);
            Assert.True(File.ReadAllBytes(output).AsSpan(0, source.Length).SequenceEqual(source));
            using ContentDocument extracted = ContentDocument.Open(output);
            string text = extracted.GetPage(1).Text;
            Assert.Contains("Hello", text);
            Assert.Contains("বাংলা", text);
            Assert.Contains("日本語", text);
            Assert.Contains("中文", text);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void AddSearchableTextLayers_HandlesEveryNativePageRotation()
    {
        string input = Path.Combine(Path.GetTempPath(),
            $"killerpdf-ocr-rotated-input-{Guid.NewGuid():N}.pdf");
        string output = Path.Combine(Path.GetTempPath(),
            $"killerpdf-ocr-rotated-output-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfDocument authored = PdfDocument.Open(new PdfDocumentBuilder()
                .AddBlankPage(300, 400).AddBlankPage(300, 400)
                .AddBlankPage(300, 400).AddBlankPage(300, 400).Build());
            byte[] source = new PdfIncrementalPageEditor(authored)
                .SetRotation(0, 0).SetRotation(1, 90)
                .SetRotation(2, 180).SetRotation(3, 270).Build();
            File.WriteAllBytes(input, source);
            var layers = Enumerable.Range(0, 4).Select(index =>
                new PdfEngineIntegration.SearchablePage(
                    index % 2 == 0 ? 300 : 400,
                    index % 2 == 0 ? 400 : 300,
                    [new PdfEngineIntegration.SearchableWord(
                        $"Rotation{index}", 10, 10, 120, 35)])).ToArray();

            Assert.Equal(4, PdfEngineIntegration.AddSearchableTextLayers(
                input, output, layers));

            using ContentDocument extracted = ContentDocument.Open(output);
            for (int index = 0; index < 4; index++)
                Assert.Contains($"Rotation{index}", extracted.GetPage(index + 1).Text);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void ApplyFormValues_WritesAllDesktopFieldTypesInOneRevision()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-forms-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage()
                .AddBlankPage()
                .AddTextField(0, "customer.name", 20, 20, 140, 24, "Original")
                .AddComboBoxOptions(0, "customer.country", 20, 60, 140, 24, [
                    new PdfChoiceOption("US", "United States"),
                    new PdfChoiceOption("CA", "Canada")], "US")
                .AddCheckBox(0, "customer.approved", 20, 100, 20, 20)
                .AddRadioGroup("customer.plan", [
                    new PdfRadioButtonOption(0, 20, 140, 20, 20, "Free"),
                    new PdfRadioButtonOption(1, 20, 20, 20, 20, "Pro")], "Free")
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string> { ["customer.name"] = "Updated" },
                new Dictionary<string, string> { ["customer.country"] = "CA" },
                new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, bool> { ["customer.approved"] = true },
                new Dictionary<string, string> { ["customer.plan"] = "/Pro" },
                new Dictionary<string, double> { ["customer.name"] = 7.5 }));

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            Assert.Equal(2, PdfDocumentInformation.Read(PdfDocument.Open(result)).PageCount);
            string syntax = System.Text.Encoding.Latin1.GetString(result);
            Assert.Contains("/V /Pro", syntax);
            Assert.Contains("7.5 Tf", syntax);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyFormValues_WritesABatchOfOnlyMultiSelectValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-multi-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage()
                .AddMultiSelectListBox(0, "customer.colours", 20, 20, 140, 96,
                    ["Red", "Green", "Blue", "Amber"], ["Green", "Blue"])
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["customer.colours"] = new[] { "Red", "Amber" }
                },
                new Dictionary<string, bool>(),
                new Dictionary<string, string>(),
                new Dictionary<string, double>()));

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfFormWidgetInfo widget = Assert.Single(
                PdfEngineIntegration.ReadPageFormWidgets(PdfDocument.Open(result), 0),
                candidate => candidate.FieldName == "customer.colours");
            Assert.Equal(new[] { "Red", "Amber" }, widget.Values);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("don’t")]
    [InlineData("a—b")]
    [InlineData("€50")]
    [InlineData("日本")]
    public void ApplyFormValues_EmbedsFontForUnicodeTextValues(string value)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-unicode-form-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddTextField(0, "customer.name", 20, 20, 180, 28, "Original")
                .Build());

            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string> { ["customer.name"] = value },
                new Dictionary<string, string>(),
                new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
                new Dictionary<string, string>(), new Dictionary<string, double>()));

            PdfFormWidgetInfo field = Assert.Single(PdfFormWidgetReader.ReadPage(
                PdfDocument.Open(File.ReadAllBytes(path)), 0));
            Assert.Equal(value, field.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyFormValues_EmbedsFontForUnicodeChoiceValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-unicode-choice-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddComboBox(0, "customer.currency", 20, 20, 180, 28,
                    ["Dollar", "Other"], "Dollar", editable: true)
                .Build());

            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string>(),
                new Dictionary<string, string> { ["customer.currency"] = "€50" },
                new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
                new Dictionary<string, string>(), new Dictionary<string, double>()));

            PdfFormWidgetInfo field = Assert.Single(PdfFormWidgetReader.ReadPage(
                PdfDocument.Open(File.ReadAllBytes(path)), 0));
            Assert.Equal("€50", field.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemoveEncryption_WritesPasswordFreeDocumentWithPreservedMetadata()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), $"killerpdf-encrypted-{Guid.NewGuid():N}.pdf");
        string destinationPath = Path.Combine(Path.GetTempPath(), $"killerpdf-decrypted-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(sourcePath, new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Preserved" })
                .SetPasswordEncryption(new PdfPasswordEncryptionOptions
                {
                    UserPassword = "user",
                    OwnerPassword = "owner"
                })
                .AddBlankPage()
                .Build());

            PdfEngineIntegration.RemoveEncryption(sourcePath, destinationPath, "owner");

            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(destinationPath));
            Assert.False(document.IsEncrypted);
            Assert.Equal("Preserved", PdfDocumentInformation.Read(document).Title);
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    [Fact]
    public void CreateZeroRotationCopy_PreservesSourcePrefixAndClearsEveryRotation()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), $"killerpdf-rotated-{Guid.NewGuid():N}.pdf");
        string destinationPath = Path.Combine(Path.GetTempPath(), $"killerpdf-render-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] unrotated = new PdfDocumentBuilder()
                .AddBlankPage()
                .AddBlankPage()
                .Build();
            byte[] source = new PdfIncrementalPageEditor(PdfDocument.Open(unrotated))
                .SetRotation(0, 90)
                .SetRotation(1, 270)
                .Build();
            File.WriteAllBytes(sourcePath, source);

            PdfEngineIntegration.CreateZeroRotationCopy(sourcePath, destinationPath);

            byte[] result = File.ReadAllBytes(destinationPath);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            Assert.Contains("/Rotate 0", System.Text.Encoding.ASCII.GetString(result));
            Assert.Equal(2, PdfDocumentInformation.Read(PdfDocument.Open(result)).PageCount);
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    [Fact]
    public void MergeDocuments_PreservesFirstPrefixAndImportsCompleteDocuments()
    {
        byte[] first = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddBookmark("First", 0)
            .Build();
        byte[] second = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddBlankPage()
            .AddBookmark("Second", 1)
            .Build();

        byte[] merged = PdfEngineIntegration.MergeDocuments([first, second]);

        Assert.True(merged.AsSpan(0, first.Length).SequenceEqual(first));
        Assert.Equal(3, PdfDocumentInformation.Read(PdfDocument.Open(merged)).PageCount);
    }

    [Fact]
    public void MergeFiles_ComposesPdfAndImageInputsInOriginalOrder()
    {
        string pdfPath = Path.Combine(Path.GetTempPath(), $"killerpdf-merge-{Guid.NewGuid():N}.pdf");
        string imagePath = Path.Combine(Path.GetTempPath(), $"killerpdf-merge-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(pdfPath, new PdfDocumentBuilder()
                .AddPage(200, 300, ReadOnlyMemory<byte>.Empty)
                .AddBookmark("PDF page", 0)
                .Build());
            using (var bitmap = new System.Drawing.Bitmap(40, 20))
            {
                bitmap.SetResolution(72, 72);
                using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap);
                graphics.Clear(System.Drawing.Color.CornflowerBlue);
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
            }

            byte[] merged = PdfEngineIntegration.MergeFiles([pdfPath, imagePath]);

            PdfDocument document = PdfDocument.Open(merged);
            byte[] pdfSource = File.ReadAllBytes(pdfPath);
            Assert.True(merged.AsSpan(0, pdfSource.Length).SequenceEqual(pdfSource));
            Assert.Equal(2, PdfDocumentInformation.Read(document).PageCount);
            string syntax = System.Text.Encoding.Latin1.GetString(merged);
            Assert.Contains("/Subtype /Image", syntax);
            Assert.Contains("/Outlines", syntax);
        }
        finally
        {
            if (File.Exists(pdfPath)) File.Delete(pdfPath);
            if (File.Exists(imagePath)) File.Delete(imagePath);
        }
    }

    [Fact]
    public void CreateRasterDocument_AuthorsBgraPagesWithRequestedPointSizes()
    {
        byte[] result = PdfEngineIntegration.CreateRasterDocument([
            new PdfEngineIntegration.RasterPage(2, 1, 144, 72,
                new byte[] { 30, 20, 10, 255, 60, 50, 40, 128 }),
            new PdfEngineIntegration.RasterPage(1, 1, 72, 144,
                new byte[] { 90, 80, 70, 255 })]);

        Assert.Equal(2, PdfDocumentInformation.Read(PdfDocument.Open(result)).PageCount);
        string syntax = System.Text.Encoding.Latin1.GetString(result);
        Assert.Contains("/MediaBox [0 0 144 72]", syntax);
        Assert.Contains("/MediaBox [0 0 72 144]", syntax);
        Assert.Contains("/SMask", syntax);
    }

    [Fact]
    public void CreateRasterDocument_EmbedsSuppliedJpegWithoutRecompression()
    {
        BitmapSource bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr24,
            null, new byte[] { 0, 0, 255, 0, 255, 0 }, 6);
        var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] jpeg = stream.ToArray();

        byte[] result = PdfEngineIntegration.CreateRasterDocument([
            new PdfEngineIntegration.RasterPage(2, 1, 144, 72,
                ReadOnlyMemory<byte>.Empty, jpeg)]);

        string syntax = System.Text.Encoding.Latin1.GetString(result);
        Assert.Contains("/Filter /DCTDecode", syntax);
        Assert.True(result.AsSpan().IndexOf(jpeg) >= 0);
    }

    [Fact]
    public void CreateRasterDocument_StoresBitonalPagesAsOneBitWithoutAlphaMask()
    {
        byte[] result = PdfEngineIntegration.CreateRasterDocument([
            new PdfEngineIntegration.RasterPage(2, 1, 144, 72,
                new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 },
                Bitonal: true)]);

        string syntax = System.Text.Encoding.Latin1.GetString(result);
        Assert.Contains("/BitsPerComponent 1", syntax);
        Assert.Contains("/ColorSpace /DeviceGray", syntax);
        Assert.DoesNotContain("/SMask", syntax);
    }

    [Fact]
    public void CreateRasterDocument_StoresGrayscalePagesWithOneColorComponent()
    {
        byte[] result = PdfEngineIntegration.CreateRasterDocument([
            new PdfEngineIntegration.RasterPage(2, 1, 144, 72,
                new byte[] { 24, 24, 24, 255, 208, 208, 208, 255 },
                Grayscale: true)]);

        string syntax = System.Text.Encoding.Latin1.GetString(result);
        Assert.Contains("/BitsPerComponent 8", syntax);
        Assert.Contains("/ColorSpace /DeviceGray", syntax);
        Assert.DoesNotContain("/SMask", syntax);
    }

    [Fact]
    public void MergeReadableFiles_SkipsInvalidFolderImportEntries()
    {
        string validPath = Path.Combine(Path.GetTempPath(), $"killerpdf-readable-{Guid.NewGuid():N}.pdf");
        string invalidPath = Path.Combine(Path.GetTempPath(), $"killerpdf-invalid-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(validPath, new PdfDocumentBuilder().AddBlankPage().Build());
            File.WriteAllText(invalidPath, "not a PDF");

            byte[] result = PdfEngineIntegration.MergeReadableFiles([invalidPath, validPath]);

            Assert.Equal(1, PdfDocumentInformation.Read(PdfDocument.Open(result)).PageCount);
        }
        finally
        {
            if (File.Exists(validPath)) File.Delete(validPath);
            if (File.Exists(invalidPath)) File.Delete(invalidPath);
        }
    }

    [Fact]
    public void ExtractPages_UsesRequestedPageOrderAndReturnsIndependentDocument()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 200, ReadOnlyMemory<byte>.Empty)
            .AddPage(100, 300, ReadOnlyMemory<byte>.Empty)
            .AddPage(100, 400, ReadOnlyMemory<byte>.Empty)
            .Build();

        byte[] extracted = PdfEngineIntegration.ExtractPages(source, [2, 0]);
        Assert.Equal(2, PdfDocumentInformation.Read(PdfDocument.Open(extracted)).PageCount);
    }

    [Fact]
    public void SplitPages_ReturnsOneValidDocumentPerPage()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 200, ReadOnlyMemory<byte>.Empty)
            .AddPage(100, 300, ReadOnlyMemory<byte>.Empty)
            .AddPage(100, 400, ReadOnlyMemory<byte>.Empty)
            .Build();

        IReadOnlyList<byte[]> pages = PdfEngineIntegration.SplitPages(source);

        Assert.Equal(3, pages.Count);
        Assert.All(pages, page => Assert.Equal(
            1, PdfDocumentInformation.Read(PdfDocument.Open(page)).PageCount));
    }

    [Fact]
    public void ValidateDocument_RejectsSourceWithoutTrailer()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-invalid-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllText(path, "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n");

            Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.ValidateDocument(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DuplicatePage_DeepCopiesPageAtFollowingPosition()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-duplicate-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage(100, 200)
                .AddBlankPage(300, 400)
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.DuplicatePage(path, 0);

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfDocument reopened = PdfDocument.Open(result);
            Assert.Equal(3, PageCount(reopened));
            Assert.Equal([0d, 0d, 100d, 200d], PageMediaBox(reopened, 0));
            Assert.Equal([0d, 0d, 100d, 200d], PageMediaBox(reopened, 1));
            Assert.Equal([0d, 0d, 300d, 400d], PageMediaBox(reopened, 2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemapRotationsAfterPageDuplication_CopiesSourceRotation()
    {
        var rotations = new Dictionary<int, int> { [0] = 90, [1] = 270 };

        PdfEngineIntegration.RemapRotationsAfterPageDuplication(rotations, 0);

        Assert.Equal(new Dictionary<int, int> { [0] = 90, [1] = 90, [2] = 270 }, rotations);
    }

    [Fact]
    public void ReplacePage_ImportsReplacementAtSamePositionAndKeepsPageCount()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-replace-{Guid.NewGuid():N}.pdf");
        string replacementPath = Path.Combine(Path.GetTempPath(), $"killerpdf-replacement-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage(100, 200).SetPageRotation(0, 90)
                .AddBlankPage(200, 300).SetPageRotation(1, 180)
                .AddBlankPage(300, 400).SetPageRotation(2, 270)
                .Build();
            File.WriteAllBytes(path, source);
            File.WriteAllBytes(replacementPath, new PdfDocumentBuilder()
                .AddBlankPage(612, 792).SetPageRotation(0, 90)
                .Build());

            PdfEngineIntegration.ReplacePage(path, 1, replacementPath);

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfDocument reopened = PdfDocument.Open(result);
            Assert.Equal(3, PageCount(reopened));
            Assert.Equal([0d, 0d, 100d, 200d], PageMediaBox(reopened, 0));
            Assert.Equal([0d, 0d, 612d, 792d], PageMediaBox(reopened, 1));
            Assert.Equal(0, PageRotation(reopened, 1));
            Assert.Equal([0d, 0d, 300d, 400d], PageMediaBox(reopened, 2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(replacementPath)) File.Delete(replacementPath);
        }
    }

    [Fact]
    public void RemapRotationsAfterPageReplacement_ResetsOnlyReplacementPage()
    {
        var rotations = new Dictionary<int, int> { [0] = 90, [1] = 180, [2] = 270 };

        PdfEngineIntegration.RemapRotationsAfterPageReplacement(rotations, 1);

        Assert.Equal(new Dictionary<int, int> { [0] = 90, [1] = 0, [2] = 270 }, rotations);
    }

    [Fact]
    public void ReplacePages_SwapsBatchInOneStablePageOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-replace-pages-{Guid.NewGuid():N}.pdf");
        string first = Path.Combine(Path.GetTempPath(), $"killerpdf-replace-first-{Guid.NewGuid():N}.pdf");
        string second = Path.Combine(Path.GetTempPath(), $"killerpdf-replace-second-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage(100, 200).AddBlankPage(200, 300)
                .AddBlankPage(300, 400).AddBlankPage(400, 500).Build());
            File.WriteAllBytes(first, new PdfDocumentBuilder().AddBlankPage(610, 710).Build());
            File.WriteAllBytes(second, new PdfDocumentBuilder().AddBlankPage(620, 720).Build());

            PdfEngineIntegration.ReplacePages(path,
                new Dictionary<int, string> { [1] = first, [3] = second });

            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(path));
            Assert.Equal([100d, 610d, 300d, 620d], Enumerable.Range(0, 4)
                .Select(index => PageMediaBox(reopened, index)[2]));
            var rotations = new Dictionary<int, int> { [0] = 90, [1] = 180, [2] = 270, [3] = 90 };
            PdfEngineIntegration.RemapRotationsAfterPageReplacements(rotations, [1, 3]);
            Assert.Equal([90, 0, 270, 0], Enumerable.Range(0, 4)
                .Select(index => rotations[index]));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    [Fact]
    public void SetTextFieldBackground_PersistsColorAndPreservesValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-form-color-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage()
                .AddTextField(0, "customer.name", 20, 20, 140, 24, "Original")
                .Build());

            PdfEngineIntegration.SetTextFieldBackground(
                path, "customer.name", "Original",
                System.Windows.Media.Color.FromRgb(0x22, 0x88, 0xCC), null);

            PdfFormWidgetInfo field = Assert.Single(PdfFormWidgetReader.ReadPage(
                PdfDocument.Open(File.ReadAllBytes(path)), 0));
            Assert.Equal("Original", field.Value);
            Assert.NotNull(field.BackgroundColor);
            Assert.Equal(0x22 / 255d, field.BackgroundColor.Value.Red, 6);
            Assert.Equal(0x88 / 255d, field.BackgroundColor.Value.Green, 6);
            Assert.Equal(0xCC / 255d, field.BackgroundColor.Value.Blue, 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReplacePagesAndCompact_RemovesSupersededImageData()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-compact-pages-{Guid.NewGuid():N}.pdf");
        string replacement = Path.Combine(Path.GetTempPath(), $"killerpdf-compact-replacement-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] rgba = new byte[512 * 512 * 4];
            Random.Shared.NextBytes(rgba);
            byte[] source = new PdfDocumentBuilder().AddPage(612, 792,
                new PdfContentStreamBuilder().DrawImage(
                    PdfImage.FromRgba(512, 512, rgba), 0, 0, 612, 792)).Build();
            File.WriteAllBytes(path, source);
            File.WriteAllBytes(replacement, new PdfDocumentBuilder().AddBlankPage(612, 792).Build());

            PdfEngineIntegration.ReplacePagesAndCompact(path,
                new Dictionary<int, string> { [0] = replacement });

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.Length < source.Length / 10);
            Assert.Equal(1, PageCount(PdfDocument.Open(result)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(replacement)) File.Delete(replacement);
        }
    }

    [Fact]
    public void ReplaceAllPagesAndCompact_DoesNotRetainAnyOriginalPageImages()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-all-pages-{Guid.NewGuid():N}.pdf");
        string first = Path.Combine(Path.GetTempPath(), $"killerpdf-all-first-{Guid.NewGuid():N}.pdf");
        string second = Path.Combine(Path.GetTempPath(), $"killerpdf-all-second-{Guid.NewGuid():N}.pdf");
        try
        {
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Rgb24,
                null, new byte[] { 255, 0, 0, 0, 0, 255 }, 6);
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var jpegStream = new MemoryStream();
            encoder.Save(jpegStream);
            byte[] jpeg = jpegStream.ToArray();
            byte[] source = new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Keep me" })
                .AddPage(612, 792, new PdfContentStreamBuilder().DrawImage(
                    PdfImage.FromJpeg(jpeg), 0, 0, 612, 792))
                .AddPage(612, 792, new PdfContentStreamBuilder().DrawImage(
                    PdfImage.FromJpeg(jpeg), 0, 0, 612, 792))
                .Build();
            File.WriteAllBytes(path, source);
            byte[] bitonal = PdfEngineIntegration.CreateRasterDocument([
                new PdfEngineIntegration.RasterPage(2, 1, 612, 792,
                    new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, Bitonal: true)]);
            File.WriteAllBytes(first, bitonal);
            File.WriteAllBytes(second, bitonal);

            PdfEngineIntegration.ReplaceAllPagesAndCompact(path, [first, second]);

            byte[] result = File.ReadAllBytes(path);
            PdfDocumentInformation information = PdfDocumentInformation.Read(PdfDocument.Open(result));
            Assert.Equal(2, information.PageCount);
            Assert.Equal("Keep me", information.Title);
            string syntax = System.Text.Encoding.Latin1.GetString(result);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
                syntax, @"/Subtype\s*/Image").Count);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
                syntax, @"/BitsPerComponent\s+1\b").Count);
            Assert.DoesNotContain("/DCTDecode", syntax);
            Assert.Equal([0d, 0d, 612d, 792d], PageMediaBox(PdfDocument.Open(result), 0));
            Assert.Equal([0d, 0d, 612d, 792d], PageMediaBox(PdfDocument.Open(result), 1));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    [Fact]
    public void SanitizeRasterizedBookmarks_DropsInvalidParentsAndPromotesValidChildren()
    {
        var validChild = new PdfBookmarkInfo
        {
            ObjectNumber = 2,
            Generation = 0,
            Title = "Valid child",
            IsOpen = false,
            Style = PdfBookmarkStyle.Regular,
            DestinationPageIndex = 1,
            NamedDestination = "old-name",
            Destination = PdfDestination.FitPage(),
            Children = []
        };
        var invalidParent = new PdfBookmarkInfo
        {
            ObjectNumber = 1,
            Generation = 0,
            Title = "01.jpg",
            IsOpen = true,
            Style = PdfBookmarkStyle.Regular,
            Children = [validChild]
        };

        PdfBookmarkInfo result = Assert.Single(
            PdfEngineIntegration.SanitizeRasterizedBookmarks([invalidParent], 2));

        Assert.Equal("Valid child", result.Title);
        Assert.Equal(1, result.DestinationPageIndex);
        Assert.Null(result.NamedDestination);
        Assert.Empty(result.Children);
    }

    [Fact]
    public void ExtractPages_WritesSelectedOrderWithEffectiveRotations()
    {
        string sourcePath = Path.Combine(Path.GetTempPath(), $"killerpdf-extract-source-{Guid.NewGuid():N}.pdf");
        string destinationPath = Path.Combine(Path.GetTempPath(), $"killerpdf-extract-output-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(sourcePath, new PdfDocumentBuilder()
                .AddBlankPage(100, 200)
                .AddBlankPage(200, 300)
                .AddBlankPage(300, 400)
                .Build());

            PdfEngineIntegration.ExtractPages(sourcePath, destinationPath, [2, 0],
                new Dictionary<int, int> { [0] = 90, [1] = 180, [2] = 270 });

            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(destinationPath));
            Assert.Equal(2, PageCount(reopened));
            Assert.Equal([0d, 0d, 300d, 400d], PageMediaBox(reopened, 0));
            Assert.Equal(270, PageRotation(reopened, 0));
            Assert.Equal([0d, 0d, 100d, 200d], PageMediaBox(reopened, 1));
            Assert.Equal(90, PageRotation(reopened, 1));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    [Fact]
    public void AppendDocuments_MergesCompleteSourcesAndNormalizesStoredRotations()
    {
        string targetPath = Path.Combine(Path.GetTempPath(), $"killerpdf-merge-target-{Guid.NewGuid():N}.pdf");
        string sourcePath = Path.Combine(Path.GetTempPath(), $"killerpdf-merge-source-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(targetPath, new PdfDocumentBuilder().AddBlankPage(100, 200).Build());
            File.WriteAllBytes(sourcePath, new PdfDocumentBuilder()
                .AddBlankPage(200, 300).SetPageRotation(0, 90)
                .AddBlankPage(300, 400).SetPageRotation(1, 270)
                .Build());
            var imports = new[]
            {
                new PdfEngineIntegration.ImportedDocument(sourcePath, [90, 270])
            };

            PdfEngineIntegration.AppendDocuments(targetPath, imports);

            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(targetPath));
            Assert.Equal(3, PageCount(reopened));
            Assert.Equal([0d, 0d, 200d, 300d], PageMediaBox(reopened, 1));
            Assert.Equal(0, PageRotation(reopened, 1));
            Assert.Equal([0d, 0d, 300d, 400d], PageMediaBox(reopened, 2));
            Assert.Equal(0, PageRotation(reopened, 2));

            var rotations = new Dictionary<int, int> { [0] = 180 };
            PdfEngineIntegration.RemapRotationsAfterDocumentAppend(rotations, imports);
            Assert.Equal(new Dictionary<int, int>
            {
                [0] = 180, [1] = 90, [2] = 270
            }, rotations);
        }
        finally
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
        }
    }

    [Fact]
    public void InsertDocuments_AddsSourcesAtRequestedPositionAndShiftsRotations()
    {
        string targetPath = Path.Combine(Path.GetTempPath(), $"killerpdf-insert-doc-target-{Guid.NewGuid():N}.pdf");
        string sourcePath = Path.Combine(Path.GetTempPath(), $"killerpdf-insert-doc-source-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(targetPath, new PdfDocumentBuilder()
                .AddBlankPage(100, 200).AddBlankPage(400, 500).Build());
            File.WriteAllBytes(sourcePath, new PdfDocumentBuilder()
                .AddBlankPage(200, 300).AddBlankPage(300, 400).Build());
            var imports = new[]
            {
                new PdfEngineIntegration.ImportedDocument(sourcePath, [90, 270])
            };

            PdfEngineIntegration.InsertDocuments(targetPath, imports, 1);

            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(targetPath));
            Assert.Equal([100d, 200d, 300d, 400d], Enumerable.Range(0, 4)
                .Select(index => PageMediaBox(reopened, index)[2]));
            var rotations = new Dictionary<int, int> { [0] = 0, [1] = 180 };
            PdfEngineIntegration.RemapRotationsAfterDocumentInsertion(rotations, imports, 1);
            Assert.Equal([0, 90, 270, 180], Enumerable.Range(0, 4)
                .Select(index => rotations[index]));
        }
        finally
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
        }
    }

    [Fact]
    public void InsertBlankPage_AddsA4PageAtRequestedPosition()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-insert-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage(100, 200).SetPageRotation(0, 90)
                .AddBlankPage(300, 400).SetPageRotation(1, 270)
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.InsertBlankPage(path, 1, 595, 842);

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfDocument reopened = PdfDocument.Open(result);
            Assert.Equal(3, PageCount(reopened));
            Assert.Equal([0d, 0d, 100d, 200d], PageMediaBox(reopened, 0));
            Assert.Equal([0d, 0d, 595d, 842d], PageMediaBox(reopened, 1));
            Assert.Equal([0d, 0d, 300d, 400d], PageMediaBox(reopened, 2));
            Assert.Equal(270, PageRotation(reopened, 2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemapRotationsAfterPageInsertion_ShiftsPagesAndAddsZeroRotation()
    {
        var rotations = new Dictionary<int, int>
        {
            [0] = 90,
            [1] = 180,
            [2] = 270
        };

        PdfEngineIntegration.RemapRotationsAfterPageInsertion(rotations, 1);

        Assert.Equal(new Dictionary<int, int>
        {
            [0] = 90,
            [1] = 0,
            [2] = 180,
            [3] = 270
        }, rotations);
    }

    [Fact]
    public void MovePage_ReordersPagesAndPreservesTheirRotation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-move-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage(100, 200).SetPageRotation(0, 90)
                .AddBlankPage(200, 300).SetPageRotation(1, 180)
                .AddBlankPage(300, 400).SetPageRotation(2, 270)
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.MovePage(path, 0, 2);

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfDocument reopened = PdfDocument.Open(result);
            Assert.Equal([0d, 0d, 200d, 300d], PageMediaBox(reopened, 0));
            Assert.Equal(180, PageRotation(reopened, 0));
            Assert.Equal([0d, 0d, 300d, 400d], PageMediaBox(reopened, 1));
            Assert.Equal(270, PageRotation(reopened, 1));
            Assert.Equal([0d, 0d, 100d, 200d], PageMediaBox(reopened, 2));
            Assert.Equal(90, PageRotation(reopened, 2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemapRotationsAfterPageMove_MovesRotationWithPage()
    {
        var rotations = new Dictionary<int, int>
        {
            [0] = 90,
            [1] = 180,
            [2] = 270,
            [3] = 0
        };

        PdfEngineIntegration.RemapRotationsAfterPageMove(rotations, 0, 2);

        Assert.Equal(new Dictionary<int, int>
        {
            [0] = 180,
            [1] = 270,
            [2] = 90,
            [3] = 0
        }, rotations);
    }

    [Fact]
    public void MovePages_ReordersDiscontiguousSelectionAsOrderedBlock()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-move-pages-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage(100, 200).AddBlankPage(200, 300).AddBlankPage(300, 400)
                .AddBlankPage(400, 500).AddBlankPage(500, 600).Build());

            IReadOnlyList<int> selected = PdfEngineIntegration.MovePages(path, [1, 3], 5);

            Assert.Equal([3, 4], selected);
            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(path));
            Assert.Equal(100, PageMediaBox(reopened, 0)[2]);
            Assert.Equal(300, PageMediaBox(reopened, 1)[2]);
            Assert.Equal(500, PageMediaBox(reopened, 2)[2]);
            Assert.Equal(200, PageMediaBox(reopened, 3)[2]);
            Assert.Equal(400, PageMediaBox(reopened, 4)[2]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RemapRotationsAfterPageMoves_FollowsBatchOrder()
    {
        var rotations = new Dictionary<int, int>
        {
            [0] = 0, [1] = 90, [2] = 180, [3] = 270, [4] = 0
        };

        PdfEngineIntegration.RemapRotationsAfterPageMoves(rotations, [1, 3], 5);

        Assert.Equal([0, 180, 0, 90, 270],
            Enumerable.Range(0, rotations.Count).Select(index => rotations[index]));
    }

    [Fact]
    public void RemovePages_DeletesSelectedPagesAndPreservesRetainedRotations()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-delete-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage(100, 200).SetPageRotation(0, 90)
                .AddBlankPage(200, 300).SetPageRotation(1, 180)
                .AddBlankPage(300, 400).SetPageRotation(2, 270)
                .AddBlankPage(400, 500)
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.RemovePages(path, [2, 0]);

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfDocument reopened = PdfDocument.Open(result);
            Assert.Equal(2, PageCount(reopened));
            Assert.Equal([0d, 0d, 200d, 300d], PageMediaBox(reopened, 0));
            Assert.Equal(180, PageRotation(reopened, 0));
            Assert.Equal([0d, 0d, 400d, 500d], PageMediaBox(reopened, 1));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RemapRotationsAfterPageRemoval_DropsDeletedEntriesAndRenumbersSurvivors()
    {
        var rotations = new Dictionary<int, int>
        {
            [0] = 90,
            [1] = 180,
            [2] = 270,
            [3] = 0,
            [4] = 90
        };

        PdfEngineIntegration.RemapRotationsAfterPageRemoval(rotations, [3, 1]);

        Assert.Equal(new Dictionary<int, int>
        {
            [0] = 90,
            [1] = 270,
            [2] = 90
        }, rotations);
    }

    [Fact]
    public void ApplyCropBoxes_WritesMatchingCropAndTrimBoxesIncrementally()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-crop-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage(200, 300)
                .SetPageRotation(0, 90)
                .AddBlankPage(400, 500)
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.ApplyCropBoxes(path,
                new Dictionary<int, PdfEngineIntegration.PageRectangle?>
                {
                    [0] = new(10, 20, 150, 240),
                    [1] = new(25, 30, 300, 400)
                });

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfDocument reopened = PdfDocument.Open(result);
            Assert.Equal([10d, 20d, 160d, 260d], PageBox(reopened, 0, "CropBox"));
            Assert.Equal([10d, 20d, 160d, 260d], PageBox(reopened, 0, "TrimBox"));
            Assert.Equal(90, PageRotation(reopened, 0));
            Assert.Equal([25d, 30d, 325d, 430d], PageBox(reopened, 1, "CropBox"));
            Assert.Equal([25d, 30d, 325d, 430d], PageBox(reopened, 1, "TrimBox"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyCropBoxes_WithNullRectangleRemovesCropAndTrimBoxes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-crop-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage(200, 300)
                .SetPageBox(0, PdfPageBox.Crop, 10, 10, 180, 280)
                .SetPageBox(0, PdfPageBox.Trim, 20, 20, 160, 260)
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.ApplyCropBoxes(path,
                new Dictionary<int, PdfEngineIntegration.PageRectangle?> { [0] = null });

            PdfDocument reopened = PdfDocument.Open(File.ReadAllBytes(path));
            PdfDictionary page = Page(reopened, 0);
            Assert.False(page.ContainsKey(new PdfName("CropBox"u8)));
            Assert.False(page.ContainsKey(new PdfName("TrimBox"u8)));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyPageRotations_WritesFinalIncrementalRotationRevision()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-rotation-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder()
                .AddBlankPage()
                .AddBlankPage()
                .Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.ApplyPageRotations(path, new Dictionary<int, int>
            {
                [0] = 90,
                [1] = 270
            });

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            PdfDocument reopened = PdfDocument.Open(result);
            Assert.Equal(90, PageRotation(reopened, 0));
            Assert.Equal(270, PageRotation(reopened, 1));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyDocumentMetadata_WritesCompleteMetadataAndPreservesPrefix()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-metadata-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
            File.WriteAllBytes(path, source);
            var metadata = new PdfDocumentMetadata
            {
                Title = "Updated title",
                Author = "Steve",
                Subject = "The KillerPDF.Engine",
                Keywords = "PDF 2.0, PDF/A",
                Creator = "KillerPDF",
                Producer = "Original producer",
                Language = "en-US",
                CreationDate = new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.FromHours(-7)),
                ModificationDate = new DateTimeOffset(2026, 8, 24, 11, 12, 13, TimeSpan.Zero),
                Trapped = PdfTrappedStatus.False
            };

            PdfEngineIntegration.ApplyDocumentMetadata(path, metadata);

            byte[] result = File.ReadAllBytes(path);
            Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
            var info = PdfDocumentInformation.Read(PdfDocument.Open(result));
            Assert.Equal(metadata.Title, info.Title);
            Assert.Equal(metadata.Author, info.Author);
            Assert.Equal(metadata.Subject, info.Subject);
            Assert.Equal(metadata.Keywords, info.Keywords);
            Assert.Equal(metadata.Creator, info.Creator);
            Assert.Equal(metadata.Producer, info.Producer);
            Assert.Equal(metadata.Language, info.Language);
            Assert.Equal(metadata.CreationDate, info.CreationDate);
            Assert.Equal(metadata.ModificationDate, info.ModificationDate);
            Assert.Equal(metadata.Trapped, info.Trapped);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyPageRotations_WithNoApplicationRotations_LeavesFileUntouched()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-rotation-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.ApplyPageRotations(path, new Dictionary<int, int>());

            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ApplyPageRotations_WithInvalidPageIndex_PreservesOriginalFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-rotation-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
            File.WriteAllBytes(path, source);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PdfEngineIntegration.ApplyPageRotations(path, new Dictionary<int, int> { [1] = 90 }));

            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ClearInvalidatedSignatures_LeavesUnsignedFileByteIdentical()
    {
        string path = Path.Combine(Path.GetTempPath(),
            $"killerpdf-signature-cleanup-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
            File.WriteAllBytes(path, source);

            PdfEngineIntegration.ClearInvalidatedSignatures(path);

            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static long PageRotation(PdfDocument document, int pageIndex)
    {
        return Assert.IsType<PdfInteger>(Page(document, pageIndex)[new PdfName("Rotate"u8)]).Value;
    }

    private static double[] PageBox(PdfDocument document, int pageIndex, string name)
    {
        PdfName key = name switch
        {
            "CropBox" => new PdfName("CropBox"u8),
            "TrimBox" => new PdfName("TrimBox"u8),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        PdfArray box = Assert.IsType<PdfArray>(Page(document, pageIndex)[key]);
        return [.. box.Select(item => item switch
        {
            PdfInteger integer => (double)integer.Value,
            PdfReal real => real.Value,
            _ => throw new Xunit.Sdk.XunitException("Page box contains a nonnumeric value.")
        })];
    }

    private static double[] PageMediaBox(PdfDocument document, int pageIndex)
    {
        PdfArray box = Assert.IsType<PdfArray>(
            Page(document, pageIndex)[new PdfName("MediaBox"u8)]);
        return [.. box.Select(item => item switch
        {
            PdfInteger integer => (double)integer.Value,
            PdfReal real => real.Value,
            _ => throw new Xunit.Sdk.XunitException("Media box contains a nonnumeric value.")
        })];
    }

    private static int PageCount(PdfDocument document)
    {
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(document.Trailer[new PdfName("Root"u8)])));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)])));
        return checked((int)Assert.IsType<PdfInteger>(pages[new PdfName("Count"u8)]).Value);
    }

    private static PdfDictionary Page(PdfDocument document, int pageIndex)
    {
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(document.Trailer[new PdfName("Root"u8)])));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)])));
        PdfArray kids = Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)]);
        return Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(kids[pageIndex])));
    }
}
