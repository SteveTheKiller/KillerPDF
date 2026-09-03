using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;

namespace KillerPDF.Services;

/// <summary>Bridges completed application state into The KillerPDF.Engine during migration.</summary>
internal static class PdfEngineIntegration
{
    internal sealed record FormEdits(
        IReadOnlyDictionary<string, string> TextValues,
        IReadOnlyDictionary<string, string> ChoiceValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> MultiChoiceValues,
        IReadOnlyDictionary<string, bool> CheckBoxValues,
        IReadOnlyDictionary<string, string> RadioValues,
        IReadOnlyDictionary<string, double> TextFontSizes);

    /// <summary>Reads the complete bookmark hierarchy for the desktop sidebar.</summary>
    internal static IReadOnlyList<PdfBookmarkInfo> ReadBookmarks(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return PdfBookmarkReader.Read(PdfDocument.Open(source));
    }

    /// <summary>Reads native links from one page for viewer hit testing.</summary>
    internal static IReadOnlyList<PdfLinkInfo> ReadPageLinks(string path, int pageIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfLinkReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(path)), pageIndex);
    }

    /// <summary>Reads native links from an already parsed engine document.</summary>
    internal static IReadOnlyList<PdfLinkInfo> ReadPageLinks(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfLinkReader.ReadPage(document, pageIndex);
    }

    /// <summary>Reads interactive form widgets from an already parsed engine document.</summary>
    internal static IReadOnlyList<PdfFormWidgetInfo> ReadPageFormWidgets(
        PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfFormWidgetReader.ReadPage(document, pageIndex);
    }

    internal static IReadOnlyList<PdfFormWidgetInfo> ReadPageFormWidgets(
        string path, int pageIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ReadPageFormWidgets(PdfDocument.Open(File.ReadAllBytes(path)), pageIndex);
    }

    internal static IReadOnlyList<IReadOnlyList<PdfFormWidgetInfo>> ReadAllPageFormWidgets(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        int pageCount = PdfDocumentInformation.Read(document).PageCount;
        return
        [
            .. Enumerable.Range(0, pageCount).Select(index =>
                (IReadOnlyList<PdfFormWidgetInfo>)PdfFormWidgetReader.ReadPage(document, index))
        ];
    }

    /// <summary>Adds one editable AcroForm text field to an existing page.</summary>
    internal static string AddTextField(
        string path, int pageIndex, double x, double y, double width, double height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        int pageCount = PdfPageInformation.Read(document).Count;
        for (int index = 0; index < pageCount; index++)
            foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, index))
                if (!string.IsNullOrWhiteSpace(widget.FieldName))
                    existingNames.Add(widget.FieldName);

        int suffix = 1;
        string name;
        do name = $"answer_{suffix++:000}";
        while (existingNames.Contains(name));

        var appearance = new PdfFormFieldAppearanceStyle
        {
            BackgroundColor = new PdfRgbColor(1, 1, 1),
            BorderColor = new PdfRgbColor(1, 0, 0),
            TextColor = new PdfRgbColor(0, 0, 0),
            BorderWidth = 1
        };
        var options = new PdfTextFieldOptions { Multiline = height >= 32 };
        double initialFontSize = Math.Clamp(height * 0.5, 12, 24);
        byte[] result = new PdfIncrementalPageEditor(document).AddTextField(
            pageIndex, name, x, y, width, height, fontSize: initialFontSize,
            options: options,
            fieldMetadata: new PdfFormFieldMetadata
            {
                Tooltip = $"Answer {suffix - 1}"
            }, appearanceStyle: appearance).Build();
        ReplaceWithBuiltResult(path, result);
        return name;
    }

    /// <summary>Moves one native form widget while preserving its field and appearance.</summary>
    internal static void MoveFormWidget(
        string path, int objectNumber, int generation,
        double left, double bottom, double right, double top)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        byte[] result = new PdfIncrementalPageEditor(document)
            .SetFormWidgetRectangle(
                objectNumber, generation, left, bottom, right, top)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Removes one native form field and all of its widgets.</summary>
    internal static void RemoveFormField(string path, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        byte[] result = new PdfIncrementalPageEditor(document)
            .RemoveFormField(fieldName)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Changes one text field's background color while preserving its value.</summary>
    internal static void SetTextFieldBackground(
        string path, string fieldName, string value, System.Windows.Media.Color color,
        double? fontSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var background = new PdfRgbColor(
            color.R / 255d, color.G / 255d, color.B / 255d);
        byte[] result = new PdfIncrementalPageEditor(document)
            .SetTextFieldBackgroundColor(fieldName, value, background, fontSize: fontSize)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Replaces the document bookmark hierarchy as one engine revision.</summary>
    internal static void ReplaceBookmarks(string path, IReadOnlyList<PdfBookmarkInfo> bookmarks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bookmarks);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(document).ClearBookmarks();
        Add(bookmarks, 0);
        ReplaceWithBuiltResult(path, editor.Build());

        void Add(IReadOnlyList<PdfBookmarkInfo> items, int level)
        {
            foreach (PdfBookmarkInfo item in items)
            {
                var options = new PdfBookmarkOptions
                {
                    Style = item.Style,
                    Color = item.Color,
                    IsOpen = item.IsOpen,
                    Destination = item.Destination ?? PdfDestination.FitPage()
                };
                if (item.NamedDestination is not null)
                    editor.AddNamedDestinationBookmark(item.Title, item.NamedDestination, level, options);
                else if (item.DestinationPageIndex.HasValue)
                    editor.AddBookmark(item.Title, item.DestinationPageIndex.Value, level, options);
                else
                    throw new NotSupportedException(
                        $"Bookmark '{item.Title}' has no local page or named destination.");
                Add(item.Children, level + 1);
            }
        }
    }

    /// <summary>Applies a complete pending form-edit batch as one incremental revision.</summary>
    internal static void ApplyFormValues(string path, FormEdits edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.TextValues.Count == 0 && edits.ChoiceValues.Count == 0
            && edits.MultiChoiceValues.Count == 0
            && edits.CheckBoxValues.Count == 0 && edits.RadioValues.Count == 0)
            return;
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(document);
        var fonts = new Dictionary<string, TrueTypeFont>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<PdfFormWidgetInfo> widgets =
        [
            .. Enumerable.Range(
                0, PdfDocumentInformation.Read(document).PageCount)
                .SelectMany(pageIndex => PdfFormWidgetReader.ReadPage(document, pageIndex))
        ];
        foreach ((string name, string value) in edits.TextValues.OrderBy(item => item.Key))
            editor.SetTextFieldValue(name, value, EmbeddedFormFont(value, fonts), fontSize:
                edits.TextFontSizes.TryGetValue(name, out double size) ? size : null);
        foreach ((string name, string value) in edits.ChoiceValues.OrderBy(item => item.Key))
        {
            string appearanceText = widgets.FirstOrDefault(widget =>
                    widget.FieldKind == PdfFormFieldKind.Choice
                    && string.Equals(widget.FieldName, name, StringComparison.Ordinal))?
                .Options.FirstOrDefault(option =>
                    string.Equals(option.ExportValue, value, StringComparison.Ordinal))?
                .DisplayValue ?? value;
            editor.SetChoiceFieldValue(name, value, EmbeddedFormFont(appearanceText, fonts));
        }
        foreach ((string name, IReadOnlyList<string> values) in
                 edits.MultiChoiceValues.OrderBy(item => item.Key))
        {
            string appearanceText = string.Join(' ', values.Select(value => widgets
                .FirstOrDefault(widget => widget.FieldKind == PdfFormFieldKind.Choice
                    && string.Equals(widget.FieldName, name, StringComparison.Ordinal))?
                .Options.FirstOrDefault(option =>
                    string.Equals(option.ExportValue, value, StringComparison.Ordinal))?
                .DisplayValue ?? value));
            editor.SetChoiceFieldValues(name, values, EmbeddedFormFont(appearanceText, fonts));
        }
        foreach ((string name, bool value) in edits.CheckBoxValues.OrderBy(item => item.Key))
            editor.SetCheckBoxValue(name, value);
        foreach ((string name, string value) in edits.RadioValues.OrderBy(item => item.Key))
            editor.SetRadioButtonValue(name, value.TrimStart('/'));
        ReplaceWithBuiltResult(path, editor.Build());
    }

    private static TrueTypeFont? EmbeddedFormFont(
        string value, Dictionary<string, TrueTypeFont> cache)
    {
        if (!value.Any(character => character > byte.MaxValue)) return null;
        string family = FontCoverage.PickFamily("Segoe UI", value);
        if (cache.TryGetValue(family, out TrueTypeFont? font)) return font;
        byte[]? bytes = InstalledFontCatalog.RegularFaceBytes(family) ?? throw new InvalidOperationException(
                $"No installed font can preserve the Unicode form value using {family}.");
        try { font = TrueTypeFont.Load(bytes); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The installed font {family} could not be embedded for a Unicode form value.", ex);
        }
        cache.Add(family, font);
        return font;
    }

    /// <summary>Authenticates and fully rewrites a PDF without password encryption.</summary>
    internal static void RemoveEncryption(
        string sourcePath, string destinationPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(password);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(sourcePath), password);
        byte[] result = PdfDocumentWriter.Write(document,
            new PdfDocumentWriteOptions { RemoveEncryption = true });
        ReplaceWithBuiltResult(destinationPath, result);
    }

    /// <summary>Merges complete PDF documents while preserving the first document byte prefix.</summary>
    internal static byte[] MergeDocuments(IReadOnlyList<byte[]> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
            throw new ArgumentException("At least one PDF document is required.", nameof(sources));

        PdfDocument document = PdfDocument.Open(sources[0]);
        var editor = new PdfIncrementalPageEditor(document);
        for (int index = 1; index < sources.Count; index++)
            editor.AddImportedDocument(PdfDocument.Open(sources[index]));
        return editor.Build();
    }

    /// <summary>Rebuilds a complete document graph into a clean engine-authored page tree.</summary>
    internal static void RebuildDocument(
        string sourcePath, string destinationPath, bool stripRotations = false,
        bool preserveBookmarks = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty).AddImportedDocument(source);
        if (!preserveBookmarks)
            editor.ClearBookmarks();
        if (stripRotations)
            for (int pageIndex = 0; pageIndex < editor.PageCount; pageIndex++)
                editor.SetRotation(pageIndex, 0);
        ReplaceWithBuiltResult(destinationPath, editor.Build());
    }

    /// <summary>Performs a deterministic full-document resave through the engine writer.</summary>
    internal static void ResaveDocument(
        string sourcePath,
        string destinationPath,
        bool allowSignatureInvalidation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        ReplaceWithBuiltResult(destinationPath, PdfDocumentWriter.Write(source,
            new PdfDocumentWriteOptions
            {
                AllowSignatureInvalidation = allowSignatureInvalidation
            }));
    }

    /// <summary>Removes one native PDF annotation by its page-array index.</summary>
    internal static void RemoveAnnotation(string path, int pageIndex, int annotationIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument source = PdfDocument.Open(original);
        byte[] result = new PdfIncrementalAnnotationEditor(source)
            .RemoveAnnotationAt(pageIndex, annotationIndex)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Normalizes native links to invisible clickable regions through one revision.</summary>
    internal static void StripLinkAppearances(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalAnnotationEditor(source).StripLinkAppearances();
        if (!editor.HasChanges) return;
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Clears signature state invalidated by the application's rewritten output.</summary>
    internal static void ClearInvalidatedSignatures(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument source = PdfDocument.Open(original);
        byte[] result = PdfSignatureInvalidationWriter.ClearSignatureValues(source);
        if (result.AsSpan().SequenceEqual(original)) return;
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Repairs empty outlines and invalid direct crop boxes after serialization.</summary>
    internal static void RepairHarmlessSaveArtifacts(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] original = File.ReadAllBytes(path);
        byte[] result = PdfSaveSanitizer.RepairHarmlessArtifacts(PdfDocument.Open(original));
        if (result.AsSpan().SequenceEqual(original)) return;
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Proportionally normalizes selected pages into a compatible dimension range.</summary>
    internal static void NormalizePageDimensions(string path,
        IReadOnlyCollection<int> pageIndexes, double minimum, double maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument source = PdfDocument.Open(original);
        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            source, pageIndexes, minimum, maximum);
        if (result.AsSpan().SequenceEqual(original)) return;
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Merges PDF documents and image frames through one engine page tree.</summary>
    internal static byte[] MergeFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            throw new ArgumentException("At least one input file is required.", nameof(paths));

        bool firstIsPdf = string.Equals(Path.GetExtension(paths[0]), ".pdf",
            StringComparison.OrdinalIgnoreCase);
        PdfDocument target = firstIsPdf
            ? PdfDocument.Open(File.ReadAllBytes(paths[0]))
            : PdfDocument.Open(new PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(target);
        foreach (string path in paths.Skip(firstIsPdf ? 1 : 0))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (string.Equals(Path.GetExtension(path), ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                editor.AddImportedDocument(PdfDocument.Open(File.ReadAllBytes(path)));
                continue;
            }
            AppendImageFrames(editor, path);
        }
        return editor.Build();
    }

    /// <summary>Merges every readable PDF or image input and skips invalid entries.</summary>
    internal static byte[] MergeReadableFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty);
        foreach (string path in paths)
        {
            try
            {
                if (string.Equals(Path.GetExtension(path), ".pdf",
                        StringComparison.OrdinalIgnoreCase))
                    editor.AddImportedDocument(PdfDocument.Open(File.ReadAllBytes(path)));
                else
                    AppendImageFrames(editor, path);
            }
            catch
            {
                // Folder and archive imports deliberately retain every readable entry.
            }
        }
        if (editor.PageCount == 0)
            throw new InvalidOperationException("No readable PDF or image pages were found.");
        return editor.Build();
    }

    private static void AppendImageFrames(PdfIncrementalPageEditor editor, string path)
    {
        using DrawingImage source = DrawingImage.FromFile(path);
        var dimension = new System.Drawing.Imaging.FrameDimension(source.FrameDimensionsList[0]);
        int frameCount = Math.Max(1, source.GetFrameCount(dimension));
        bool useOriginalJpeg = frameCount == 1
            && Path.GetExtension(path) is string extension
            && (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
        byte[]? originalJpeg = useOriginalJpeg ? File.ReadAllBytes(path) : null;
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            source.SelectActiveFrame(dimension, frameIndex);
            int width = source.Width;
            int height = source.Height;
            double dpiX = source.HorizontalResolution;
            double dpiY = source.VerticalResolution;
            if (dpiX is < 24 or > 4800) dpiX = 96;
            if (dpiY is < 24 or > 4800) dpiY = 96;
            double pageWidth = width * 72.0 / dpiX;
            double pageHeight = height * 72.0 / dpiY;
            double shrink = Math.Min(1, 14400.0 / Math.Max(pageWidth, pageHeight));
            pageWidth *= shrink;
            pageHeight *= shrink;
            double grow = Math.Max(1, 3.0 / Math.Min(pageWidth, pageHeight));
            pageWidth *= grow;
            pageHeight *= grow;

            PdfImage image;
            if (originalJpeg is not null)
            {
                image = PdfImage.FromJpeg(originalJpeg);
            }
            else
            {
                using var bitmap = new DrawingBitmap(width, height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (DrawingGraphics graphics = DrawingGraphics.FromImage(bitmap))
                    graphics.DrawImage(source, 0, 0, width, height);
                byte[] rgba = CopyRgba(bitmap);
                image = PdfImage.FromRgba(width, height, rgba);
            }
            editor.AddPage(pageWidth, pageHeight,
                new PdfContentStreamBuilder().DrawImage(
                    image, 0, 0, pageWidth, pageHeight));
        }
    }

    private static byte[] CopyRgba(DrawingBitmap bitmap)
    {
        var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(rectangle,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            byte[] row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr rowAddress = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(rowAddress, row, 0, row.Length);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int source = x * 4;
                    int target = (y * bitmap.Width + x) * 4;
                    rgba[target] = row[source + 2];
                    rgba[target + 1] = row[source + 1];
                    rgba[target + 2] = row[source];
                    rgba[target + 3] = row[source + 3];
                }
            }
            return rgba;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    internal sealed record RasterPage(
        int PixelWidth, int PixelHeight, double PageWidth, double PageHeight,
        ReadOnlyMemory<byte> BgraPixels,
        ReadOnlyMemory<byte> JpegData = default,
        bool Bitonal = false,
        bool Grayscale = false);

    internal sealed record SearchableWord(
        string Text, int Left, int Top, int Right, int Bottom);

    internal sealed record SearchablePage(
        int PixelWidth, int PixelHeight, IReadOnlyList<SearchableWord> Words);

    /// <summary>Appends invisible, Unicode-mapped OCR text to every supplied page.</summary>
    internal static int AddSearchableTextLayers(
        string sourcePath, string destinationPath, IReadOnlyList<SearchablePage> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(pages);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        IReadOnlyList<PdfPageInformation> information = PdfPageInformation.Read(document);
        if (pages.Count != information.Count)
            throw new ArgumentException(
                "The OCR page count must match the PDF page count.", nameof(pages));

        var editor = new PdfIncrementalPageEditor(document);
        var fonts = new Dictionary<string, TrueTypeFont>(StringComparer.OrdinalIgnoreCase);
        int writtenWords = 0;
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            SearchablePage page = pages[pageIndex];
            if (page.PixelWidth <= 0 || page.PixelHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(pages),
                    "OCR pixel dimensions must be positive.");
            PdfPageInformation geometry = information[pageIndex];
            double displayWidth = geometry.Rotation is 90 or 270
                ? geometry.Height : geometry.Width;
            double displayHeight = geometry.Rotation is 90 or 270
                ? geometry.Width : geometry.Height;
            double scaleX = displayWidth / page.PixelWidth;
            double scaleY = displayHeight / page.PixelHeight;
            var content = new PdfContentStreamBuilder().SaveState();
            ApplyDisplayTransform(content, geometry);
            content.BeginText().SetTextRenderingMode(PdfTextRenderingMode.Invisible);
            int pageWords = 0;
            foreach (SearchableWord word in page.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                string family = FontCoverage.PickFamily("Segoe UI", word.Text);
                if (!fonts.TryGetValue(family, out TrueTypeFont? font))
                {
                    byte[]? bytes = InstalledFontCatalog.RegularFaceBytes(family);
                    if (bytes is null) continue;
                    try { font = TrueTypeFont.Load(bytes); }
                    catch { continue; }
                    fonts.Add(family, font);
                }
                if (!CanMap(font, word.Text)) continue;

                double height = Math.Max(1, (word.Bottom - word.Top) * scaleY);
                double width = Math.Max(1, (word.Right - word.Left) * scaleX);
                double naturalWidth = NaturalWidth(font, word.Text, height);
                double horizontalScale = naturalWidth > 0
                    ? Math.Clamp(width / naturalWidth * 100, 10, 1000)
                    : 100;
                content.SetFont(font, height)
                    .SetHorizontalTextScale(horizontalScale)
                    .SetTextMatrix(1, 0, 0, -1,
                        word.Left * scaleX,
                        word.Top * scaleY + height * 0.8)
                    .ShowUnicodeText(word.Text);
                pageWords++;
            }
            content.EndText().RestoreState();
            if (pageWords == 0) continue;
            editor.AppendPageContent(
                pageIndex, geometry.Width, geometry.Height, content);
            writtenWords += pageWords;
        }
        ReplaceWithBuiltResult(destinationPath, editor.Build());
        return writtenWords;
    }

    private static void ApplyDisplayTransform(
        PdfContentStreamBuilder content, PdfPageInformation page)
    {
        switch (page.Rotation)
        {
            case 0: content.Transform(1, 0, 0, -1, 0, page.Height); break;
            case 90: content.Transform(0, 1, 1, 0, 0, 0); break;
            case 180: content.Transform(-1, 0, 0, 1, page.Width, 0); break;
            case 270: content.Transform(0, -1, -1, 0, page.Width, page.Height); break;
            default: throw new InvalidOperationException("The PDF page rotation is unsupported.");
        }
    }

    private static bool CanMap(TrueTypeFont font, string text) =>
        text.EnumerateRunes().All(rune => font.GetGlyphId(rune.Value) != 0);

    private static double NaturalWidth(TrueTypeFont font, string text, double size) =>
        text.EnumerateRunes().Sum(rune =>
            font.GetPdfAdvanceWidth(font.GetGlyphId(rune.Value))) * size / 1000;

    /// <summary>Authors a flattened PDF from opaque or alpha-bearing PDFium BGRA pages.</summary>
    internal static byte[] CreateRasterDocument(IReadOnlyList<RasterPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
            throw new ArgumentException("At least one raster page is required.", nameof(pages));
        var builder = new PdfDocumentBuilder();
        foreach (RasterPage page in pages)
        {
            if (page.PixelWidth <= 0 || page.PixelHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(pages),
                    "Raster page dimensions must be positive.");
            PdfImage image;
            if (page.Bitonal || page.Grayscale && page.JpegData.IsEmpty)
            {
                int required = checked(page.PixelWidth * page.PixelHeight * 4);
                if (page.BgraPixels.Length != required)
                    throw new ArgumentException(
                        "A raster page does not contain the required BGRA pixel count.", nameof(pages));
                byte[] gray = new byte[checked(page.PixelWidth * page.PixelHeight)];
                ReadOnlySpan<byte> bgra = page.BgraPixels.Span;
                for (int pixel = 0; pixel < gray.Length; pixel++)
                    gray[pixel] = bgra[pixel * 4];
                image = page.Bitonal
                    ? PdfImage.FromBitonal(page.PixelWidth, page.PixelHeight, gray)
                    : PdfImage.FromGray(page.PixelWidth, page.PixelHeight, gray);
            }
            else if (!page.JpegData.IsEmpty)
            {
                image = PdfImage.FromJpeg(page.JpegData);
                if (image.Width != page.PixelWidth || image.Height != page.PixelHeight)
                    throw new ArgumentException(
                        "A raster page's JPEG dimensions do not match its declared dimensions.", nameof(pages));
            }
            else
            {
                int required = checked(page.PixelWidth * page.PixelHeight * 4);
                if (page.BgraPixels.Length != required)
                    throw new ArgumentException(
                        "A raster page does not contain the required BGRA pixel count.", nameof(pages));
                byte[] rgba = page.BgraPixels.ToArray();
                for (int pixel = 0; pixel < rgba.Length; pixel += 4)
                    (rgba[pixel], rgba[pixel + 2]) = (rgba[pixel + 2], rgba[pixel]);
                image = PdfImage.FromRgba(page.PixelWidth, page.PixelHeight, rgba);
            }
            builder.AddPage(page.PageWidth, page.PageHeight,
                new PdfContentStreamBuilder().DrawImage(
                    image, 0, 0, page.PageWidth, page.PageHeight));
        }
        return builder.Build();
    }

    /// <summary>Reads crop-aware page dimensions and native rotations.</summary>
    internal static IReadOnlyList<PdfPageInformation> ReadPageInformation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));
    }

    /// <summary>Authors a new one-page blank document.</summary>
    internal static byte[] CreateBlankDocument(double width = 595, double height = 842) =>
        new PdfDocumentBuilder().AddPage(width, height, ReadOnlyMemory<byte>.Empty).Build();

    /// <summary>Extracts selected pages into a new PDF in the supplied order.</summary>
    internal static byte[] ExtractPages(byte[] source, IReadOnlyList<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (pageIndices.Count == 0)
            throw new ArgumentException("At least one page is required.", nameof(pageIndices));

        PdfDocument sourceDocument = PdfDocument.Open(source);
        PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty);
        foreach (int pageIndex in pageIndices)
            editor.AddImportedPage(sourceDocument, pageIndex);
        return editor.Build();
    }

    /// <summary>Splits a PDF into one independently valid PDF per source page.</summary>
    internal static IReadOnlyList<byte[]> SplitPages(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        PdfDocument sourceDocument = PdfDocument.Open(source);
        int pageCount = new PdfIncrementalPageEditor(sourceDocument).PageCount;
        var results = new byte[pageCount][];
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
            results[pageIndex] = new PdfIncrementalPageEditor(empty)
                .AddImportedPage(sourceDocument, pageIndex)
                .Build();
        }
        return results;
    }

    internal readonly record struct PageRectangle(
        double X, double Y, double Width, double Height);

    internal sealed record ImportedDocument(
        string Path, IReadOnlyList<int> PageRotations);

    /// <summary>Validates that the engine can open a document for page-copy operations.</summary>
    internal static void ValidateDocument(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = new PdfIncrementalPageEditor(
            PdfDocument.Open(File.ReadAllBytes(path))).PageCount;
    }

    /// <summary>
    /// Writes the application's effective page rotations as the final incremental revision.
    /// The source file is replaced only after the engine has built the complete result.
    /// </summary>
    internal static void ApplyPageRotations(
        string path, IReadOnlyDictionary<int, int> rotations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rotations);
        if (rotations.Count == 0) return;

        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(document);
        foreach ((int pageIndex, int rotation) in rotations.OrderBy(item => item.Key))
            editor.SetRotation(pageIndex, rotation);

        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Creates a rendering copy with every native page rotation set to zero.</summary>
    internal static void CreateZeroRotationCopy(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        var editor = new PdfIncrementalPageEditor(document);
        for (int pageIndex = 0; pageIndex < editor.PageCount; pageIndex++)
            editor.SetRotation(pageIndex, 0);
        ReplaceWithBuiltResult(destinationPath, editor.Build());
    }

    /// <summary>Writes complete descriptive document metadata incrementally.</summary>
    internal static void ApplyDocumentMetadata(string path, PdfDocumentMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(metadata);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        byte[] result = new PdfIncrementalPageEditor(document)
            .SetMetadata(metadata)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>
    /// Writes visible crop and matching trim boundaries as the final incremental revision.
    /// A null rectangle removes both boundaries so the page falls back to its media box.
    /// </summary>
    internal static void ApplyCropBoxes(
        string path, IReadOnlyDictionary<int, PageRectangle?> crops)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(crops);
        if (crops.Count == 0) return;

        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(document);
        foreach ((int pageIndex, PageRectangle? crop) in crops.OrderBy(item => item.Key))
        {
            if (crop is PageRectangle box)
            {
                editor.SetCropBox(pageIndex, box.X, box.Y, box.Width, box.Height);
                editor.SetPageBox(pageIndex, PdfPageBox.Trim,
                    box.X, box.Y, box.Width, box.Height);
            }
            else
            {
                editor.ClearPageBox(pageIndex, PdfPageBox.Crop);
                editor.ClearPageBox(pageIndex, PdfPageBox.Trim);
            }
        }

        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Removes pages as one byte-preserving incremental revision.</summary>
    internal static void RemovePages(string path, IReadOnlyCollection<int> pageIndices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pageIndices);
        int[] removed = [.. pageIndices.Distinct().OrderByDescending(index => index)];
        if (removed.Length == 0) return;

        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        var editor = new PdfIncrementalPageEditor(document);
        foreach (int pageIndex in removed) editor.RemovePage(pageIndex);
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Renumbers application rotation state after pages are removed.</summary>
    internal static void RemapRotationsAfterPageRemoval(
        Dictionary<int, int> rotations, IReadOnlyCollection<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(pageIndices);
        int[] removed = [.. pageIndices.Distinct().OrderBy(index => index)];
        if (removed.Length == 0) return;

        var remapped = new Dictionary<int, int>();
        foreach ((int oldIndex, int rotation) in rotations.OrderBy(item => item.Key))
        {
            if (Array.BinarySearch(removed, oldIndex) >= 0) continue;
            int shift = removed.Count(index => index < oldIndex);
            remapped[oldIndex - shift] = rotation;
        }
        rotations.Clear();
        foreach ((int pageIndex, int rotation) in remapped)
            rotations[pageIndex] = rotation;
    }

    /// <summary>Moves one page to its final position in a byte-preserving revision.</summary>
    internal static void MovePage(string path, int sourceIndex, int destinationIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        byte[] result = new PdfIncrementalPageEditor(document)
            .MovePage(sourceIndex, destinationIndex)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Moves selected pages as one ordered block into an original-order insertion slot.</summary>
    internal static IReadOnlyList<int> MovePages(
        string path, IReadOnlyList<int> sourceIndices, int insertionIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sourceIndices);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(document);
        IReadOnlyList<int> target = PageOrderAfterMove(editor.PageCount, sourceIndices, insertionIndex);
        var current = Enumerable.Range(0, editor.PageCount).ToList();
        for (int destination = 0; destination < target.Count; destination++)
        {
            int source = current.IndexOf(target[destination]);
            if (source == destination) continue;
            editor.MovePage(source, destination);
            int page = current[source];
            current.RemoveAt(source);
            current.Insert(destination, page);
        }
        ReplaceWithBuiltResult(path, editor.Build());
        var selected = sourceIndices.Distinct().ToHashSet();
        return [.. target.Select((original, index) => (original, index))
            .Where(item => selected.Contains(item.original)).Select(item => item.index)];
    }

    internal static IReadOnlyList<int> PageOrderAfterMove(
        int pageCount, IReadOnlyList<int> sourceIndices, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(sourceIndices);
        if (insertionIndex < 0 || insertionIndex > pageCount)
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        int[] selected = [.. sourceIndices.Distinct().OrderBy(index => index)];
        if (selected.Length == 0 || selected.Any(index => index < 0 || index >= pageCount))
            throw new ArgumentOutOfRangeException(nameof(sourceIndices));
        var selectedSet = selected.ToHashSet();
        var remaining = Enumerable.Range(0, pageCount).Where(index => !selectedSet.Contains(index)).ToList();
        int adjustedSlot = insertionIndex - selected.Count(index => index < insertionIndex);
        remaining.InsertRange(adjustedSlot, selected);
        return remaining;
    }

    internal static void RemapRotationsAfterPageMoves(
        Dictionary<int, int> rotations, IReadOnlyList<int> sourceIndices, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        IReadOnlyList<int> order = PageOrderAfterMove(rotations.Count, sourceIndices, insertionIndex);
        int[] values = [.. Enumerable.Range(0, rotations.Count).Select(index => rotations[index])];
        rotations.Clear();
        for (int index = 0; index < order.Count; index++) rotations[index] = values[order[index]];
    }

    /// <summary>Moves rotation state with a reordered page.</summary>
    internal static void RemapRotationsAfterPageMove(
        Dictionary<int, int> rotations, int sourceIndex, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        if (sourceIndex == destinationIndex) return;
        var ordered = Enumerable.Range(0, rotations.Count)
            .Select(index => rotations[index])
            .ToList();
        int moved = ordered[sourceIndex];
        ordered.RemoveAt(sourceIndex);
        ordered.Insert(destinationIndex, moved);
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++)
            rotations[index] = ordered[index];
    }

    /// <summary>Inserts a blank page at its final zero-based position.</summary>
    internal static void InsertBlankPage(
        string path, int pageIndex, double width, double height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] source = File.ReadAllBytes(path);
        PdfDocument document = PdfDocument.Open(source);
        byte[] result = new PdfIncrementalPageEditor(document)
            .InsertBlankPage(pageIndex, width, height)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Creates a zero-rotation entry and shifts later page rotation state.</summary>
    internal static void RemapRotationsAfterPageInsertion(
        Dictionary<int, int> rotations, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        var ordered = Enumerable.Range(0, rotations.Count)
            .Select(index => rotations[index])
            .ToList();
        ordered.Insert(pageIndex, 0);
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++)
            rotations[index] = ordered[index];
    }

    /// <summary>Deep-copies one page directly after its source page.</summary>
    internal static void DuplicatePage(string path, int pageIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] sourceBytes = File.ReadAllBytes(path);
        PdfDocument target = PdfDocument.Open(sourceBytes);
        PdfDocument source = PdfDocument.Open(sourceBytes);
        byte[] result = new PdfIncrementalPageEditor(target)
            .InsertImportedPage(pageIndex + 1, source, pageIndex)
            .SetRotation(pageIndex + 1, 0)
            .Build();
        ReplaceWithBuiltResult(path, result);
    }

    /// <summary>Duplicates the source page's effective application rotation.</summary>
    internal static void RemapRotationsAfterPageDuplication(
        Dictionary<int, int> rotations, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        var ordered = Enumerable.Range(0, rotations.Count)
            .Select(index => rotations[index])
            .ToList();
        ordered.Insert(pageIndex + 1, ordered[pageIndex]);
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++)
            rotations[index] = ordered[index];
    }

    /// <summary>Replaces one page with the first page of an authored PDF.</summary>
    internal static void ReplacePage(string path, int pageIndex, string replacementPath)
        => ReplacePages(path, new Dictionary<int, string> { [pageIndex] = replacementPath });

    /// <summary>Replaces selected pages with the first pages of authored PDFs in one revision.</summary>
    internal static void ReplacePages(string path, IReadOnlyDictionary<int, string> replacements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0) return;
        try
        {
            ReplaceWithBuiltResult(path, Build(clearBookmarks: false));
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            ReplaceWithBuiltResult(path, Build(clearBookmarks: true));
        }

        byte[] Build(bool clearBookmarks)
        {
            PdfDocument target = PdfDocument.Open(File.ReadAllBytes(path));
            var editor = new PdfIncrementalPageEditor(target);
            if (clearBookmarks)
                editor.ClearBookmarks();
            foreach (var pair in replacements.OrderBy(pair => pair.Key))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(pair.Value);
                if (pair.Key < 0 || pair.Key >= editor.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(replacements));
                PdfDocument replacement = PdfDocument.Open(File.ReadAllBytes(pair.Value));
                if (new PdfIncrementalPageEditor(replacement).PageCount < 1)
                    throw new ArgumentException("A replacement document must contain a page.",
                        nameof(replacements));
                editor.RemovePage(pair.Key).InsertImportedPage(pair.Key, replacement, 0)
                    .SetRotation(pair.Key, 0);
            }
            return editor.Build();
        }
    }

    /// <summary>Replaces pages and removes the superseded page data from the saved file.</summary>
    internal static void ReplacePagesAndCompact(
        string path, IReadOnlyDictionary<int, string> replacements)
    {
        ReplacePages(path, replacements);
        try
        {
            RebuildDocument(path, path);
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            RebuildDocument(path, path, preserveBookmarks: false);
        }
    }

    /// <summary>Replaces every page from raster-only documents without retaining superseded resources.</summary>
    internal static void ReplaceAllPagesAndCompact(
        string path, IReadOnlyList<string> replacementPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(replacementPaths);
        if (replacementPaths.Count == 0) return;

        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(path));
        PdfDocumentInformation information = PdfDocumentInformation.Read(source);
        IReadOnlyList<PdfBookmarkInfo> bookmarks;
        try
        {
            bookmarks = PdfBookmarkReader.Read(source);
        }
        catch (Exception ex) when (IsBookmarkGraphFailure(ex))
        {
            bookmarks = [];
        }
        bookmarks = SanitizeRasterizedBookmarks(bookmarks, replacementPaths.Count);

        byte[] result = MergeDocuments([.. replacementPaths.Select(File.ReadAllBytes)]);
        ReplaceWithBuiltResult(path, result);
        ApplyDocumentMetadata(path, new PdfDocumentMetadata
        {
            Title = information.Title,
            Author = information.Author,
            Subject = information.Subject,
            Keywords = information.Keywords,
            Creator = information.Creator,
            Producer = information.Producer,
            Language = information.Language,
            CreationDate = information.CreationDate,
            ModificationDate = information.ModificationDate,
            Trapped = information.Trapped
        });
        if (bookmarks.Count > 0)
        {
            try
            {
                ReplaceBookmarks(path, bookmarks);
            }
            catch (Exception ex) when (IsBookmarkGraphFailure(ex))
            {
                // The rasterized pages are complete. A broken source outline must not discard them.
            }
        }
    }

    internal static List<PdfBookmarkInfo> SanitizeRasterizedBookmarks(
        IReadOnlyList<PdfBookmarkInfo> bookmarks, int pageCount)
    {
        var result = new List<PdfBookmarkInfo>();
        foreach (PdfBookmarkInfo bookmark in bookmarks)
        {
            List<PdfBookmarkInfo> children =
                SanitizeRasterizedBookmarks(bookmark.Children, pageCount);
            if (bookmark.DestinationPageIndex is int pageIndex
                && pageIndex >= 0 && pageIndex < pageCount)
            {
                result.Add(bookmark with
                {
                    NamedDestination = null,
                    Children = children
                });
            }
            else
            {
                result.AddRange(children);
            }
        }
        return result;
    }

    private static bool IsBookmarkGraphFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current.Message.Contains("bookmark", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Resets the replaced page's application rotation.</summary>
    internal static void RemapRotationsAfterPageReplacement(
        Dictionary<int, int> rotations, int pageIndex)
        => RemapRotationsAfterPageReplacements(rotations, [pageIndex]);

    internal static void RemapRotationsAfterPageReplacements(
        Dictionary<int, int> rotations, IReadOnlyList<int> pageIndices)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(pageIndices);
        foreach (int pageIndex in pageIndices.Distinct())
        {
            if (!rotations.ContainsKey(pageIndex))
                throw new ArgumentOutOfRangeException(nameof(pageIndices));
            rotations[pageIndex] = 0;
        }
    }

    /// <summary>Creates a new document from selected working-document pages.</summary>
    internal static void ExtractPages(
        string sourcePath, string destinationPath, IReadOnlyList<int> pageIndices,
        IReadOnlyDictionary<int, int> rotations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(pageIndices);
        ArgumentNullException.ThrowIfNull(rotations);
        if (pageIndices.Count == 0)
            throw new ArgumentException("At least one page must be extracted.", nameof(pageIndices));

        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        PdfDocument empty = PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty)
            .InsertImportedPages(0, source, pageIndices);
        for (int outputIndex = 0; outputIndex < pageIndices.Count; outputIndex++)
            editor.SetRotation(outputIndex,
                rotations.TryGetValue(pageIndices[outputIndex], out int rotation) ? rotation : 0);
        ReplaceWithBuiltResult(destinationPath, editor.Build());
    }

    /// <summary>Appends complete PDF documents and normalizes their rotations for the viewer.</summary>
    internal static void AppendDocuments(
        string path, IReadOnlyList<ImportedDocument> sources)
        => InsertDocuments(path, sources, int.MaxValue);

    /// <summary>Inserts complete PDF documents at a zero-based page position.</summary>
    internal static void InsertDocuments(
        string path, IReadOnlyList<ImportedDocument> sources, int insertionIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) return;

        PdfDocument target = PdfDocument.Open(File.ReadAllBytes(path));
        var editor = new PdfIncrementalPageEditor(target);
        int offset = insertionIndex == int.MaxValue ? editor.PageCount : insertionIndex;
        if (offset < 0 || offset > editor.PageCount)
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        foreach (ImportedDocument import in sources)
        {
            PdfDocument source = PdfDocument.Open(File.ReadAllBytes(import.Path));
            int count = new PdfIncrementalPageEditor(source).PageCount;
            if (import.PageRotations.Count != count)
                throw new ArgumentException(
                    "The imported rotation count must match the source page count.", nameof(sources));
            editor.InsertImportedPages(offset, source, [.. Enumerable.Range(0, count)]);
            for (int index = 0; index < count; index++)
                editor.SetRotation(offset + index, 0);
            offset += count;
        }
        ReplaceWithBuiltResult(path, editor.Build());
    }

    /// <summary>Appends imported page rotations to the application rotation map.</summary>
    internal static void RemapRotationsAfterDocumentAppend(
        Dictionary<int, int> rotations, IReadOnlyList<ImportedDocument> sources)
        => RemapRotationsAfterDocumentInsertion(rotations, sources, rotations.Count);

    internal static void RemapRotationsAfterDocumentInsertion(
        Dictionary<int, int> rotations, IReadOnlyList<ImportedDocument> sources, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(sources);
        if (insertionIndex < 0 || insertionIndex > rotations.Count)
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        var ordered = Enumerable.Range(0, rotations.Count).Select(index => rotations[index]).ToList();
        var inserted = new List<int>();
        foreach (ImportedDocument source in sources)
            foreach (int rotation in source.PageRotations)
                inserted.Add(((rotation % 360) + 360) % 360);
        ordered.InsertRange(insertionIndex, inserted);
        rotations.Clear();
        for (int index = 0; index < ordered.Count; index++) rotations[index] = ordered[index];
    }

    /// <summary>Turns selected application-managed pages without mutating the live PDF model.</summary>
    internal static void RemapRotationsAfterPageTurns(
        Dictionary<int, int> rotations, IReadOnlyList<int> pageIndices, int delta)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(pageIndices);
        if (delta % 90 != 0)
            throw new ArgumentOutOfRangeException(nameof(delta),
                "Page rotation changes must be multiples of 90 degrees.");
        foreach (int pageIndex in pageIndices.Distinct())
        {
            if (!rotations.TryGetValue(pageIndex, out int current))
                throw new ArgumentOutOfRangeException(nameof(pageIndices),
                    $"Page index {pageIndex} has no rotation state.");
            rotations[pageIndex] = ((current + delta) % 360 + 360) % 360;
        }
    }

    private static void ReplaceWithBuiltResult(string path, byte[] result)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, result);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
