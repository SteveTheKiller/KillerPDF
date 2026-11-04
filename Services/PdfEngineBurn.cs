using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;
using EngineImage = KillerPdf.Engine.Authoring.PdfImage;
using WpfPoint = System.Windows.Point;

namespace KillerPDF.Services;

internal sealed record PdfTextWatermarkOptions(string Text, double FontSize = 64,
    double Opacity = .25, double RotationDegrees = 45);

/// <summary>Burns KillerPDF markup into typed engine page content.</summary>
internal static class PdfEngineBurn
{
    internal static PdfMacroStep WatermarkStep(PdfTextWatermarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Text) || options.Text.Length > 4096
            || options.Text.Any(char.IsControl))
            throw new ArgumentException("Watermark text must be a nonempty single line of at most 4096 characters.");
        if (!double.IsFinite(options.FontSize) || options.FontSize is <= 0 or > 1000
            || !double.IsFinite(options.Opacity) || options.Opacity is <= 0 or > 1
            || !double.IsFinite(options.RotationDegrees) || options.RotationDegrees is < -360 or > 360)
            throw new ArgumentOutOfRangeException(nameof(options), "Watermark size, opacity or rotation is outside its supported range.");
        return new(PdfMacroOperation.Watermark, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = options.Text,
            ["fontSize"] = options.FontSize.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["opacity"] = options.Opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["rotation"] = options.RotationDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    internal static PdfTextWatermarkOptions ReadWatermarkStep(PdfMacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.Watermark || step.Settings is null
            || !step.Settings.TryGetValue("text", out string? text)
            || step.Settings.Keys.Any(key => key is not ("text" or "fontSize" or "opacity" or "rotation")))
            throw new ArgumentException("The text watermark settings are invalid.", nameof(step));
        double Number(string key, double fallback) => step.Settings.TryGetValue(key, out string? value)
            ? double.Parse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture) : fallback;
        var options = new PdfTextWatermarkOptions(text, Number("fontSize", 64),
            Number("opacity", .25), Number("rotation", 45));
        _ = WatermarkStep(options);
        return options;
    }

    /// <summary>Names the working file the watermark macro writes the document to. The app points this at
    /// its session temp folder, whose files a crash cannot leave behind in the shared temp folder; the
    /// default serves a host without one.</summary>
    internal static Func<string, string> MacroTempFile { get; set; } =
        tag => Path.Combine(Path.GetTempPath(), $"killerpdf_{tag}_{Guid.NewGuid():N}.pdf");

    internal static PdfMacroOperationResult ExecuteWatermarkMacro(PdfMacroStep step,
        ReadOnlyMemory<byte> source, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfTextWatermarkOptions options = ReadWatermarkStep(step);
        if (Font(new Dictionary<string, TrueTypeFont>(), "Segoe UI", options.Text, true, false) is null)
            throw new InvalidOperationException("No usable font is available for the watermark text.");
        string path = MacroTempFile("watermark");
        try
        {
            File.WriteAllBytes(path, source.ToArray());
            Burn(path, new Dictionary<int, List<PageAnnotation>>(),
                new Dictionary<int, (int, int)>(), new StampSpec
                {
                    WmEnabled = true, WmText = options.Text, WmFontPt = options.FontSize,
                    WmOpacity = options.Opacity, WmAngle = options.RotationDegrees
                }, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfMacroOperationResult(File.ReadAllBytes(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    internal static void Burn(
        string path,
        IReadOnlyDictionary<int, List<PageAnnotation>> annotations,
        IReadOnlyDictionary<int, (int w, int h)> renderDims,
        StampSpec? stamps = null,
        int? onlyPage = null,
        IReadOnlyDictionary<int, int>? rotations = null,
        bool forRasterization = false,
        bool measurementsAsAnnotations = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (annotations.Values.Any(items => items.Count > 0))
            PdfEngineIntegration.StripLinkAppearances(path);
        var document = PdfDocument.Open(File.ReadAllBytes(path));
        var pages = PdfPageInformation.Read(document);
        if (!forRasterization
            && RemoveReplacedText(document, pages, annotations, renderDims, onlyPage, rotations) is { } cleaned)
        {
            document = PdfDocument.Open(cleaned);
            pages = PdfPageInformation.Read(document);
        }
        var editor = new PdfIncrementalPageEditor(document);
        var fonts = new Dictionary<string, TrueTypeFont>(StringComparer.OrdinalIgnoreCase);
        var watermark = stamps is { WmEnabled: true, WmIsImage: true }
            ? LoadImageFile(stamps.WmImagePath, 1) : null;
        HashSet<int> numberPages = stamps is { NumbersEnabled: true }
            ? [.. PdfBurn.StampPageRange(stamps.NumRange, pages.Count)] : [];
        HashSet<int> watermarkPages = stamps is { WmEnabled: true }
            ? [.. PdfBurn.StampPageRange(stamps.WmRange, pages.Count)] : [];
        int firstNumberPage = numberPages.Count == 0 ? 0 : numberPages.Min();

        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onlyPage.HasValue && pageIndex != onlyPage.Value) continue;
            bool hasAnnotations = annotations.TryGetValue(pageIndex, out var pageAnnotations)
                && pageAnnotations.Count > 0;
            bool hasNumber = numberPages.Contains(pageIndex);
            bool hasWatermark = watermarkPages.Contains(pageIndex);
            if (!hasAnnotations && !hasNumber && !hasWatermark) continue;

            PdfPageInformation page = pages[pageIndex];
            int rotation = rotations?.TryGetValue(pageIndex, out int overrideRotation) == true
                ? NormalizeRotation(overrideRotation) : page.Rotation;
            double visualWidth = rotation is 90 or 270 ? page.Height : page.Width;
            double visualHeight = rotation is 90 or 270 ? page.Width : page.Height;
            var content = new PdfContentStreamBuilder().SaveState();
            ApplyVisualTransform(content, page.Left, page.Bottom, page.Width, page.Height, rotation);

            if (hasWatermark) DrawWatermark(content, stamps!, watermark, visualWidth, visualHeight, fonts);
            if (hasNumber) DrawNumber(content, stamps!, pageIndex, firstNumberPage, pages.Count,
                visualWidth, visualHeight, fonts);
            if (hasAnnotations)
            {
                var (renderWidth, renderHeight) = AnnotationSpace(renderDims, pageIndex, visualWidth, visualHeight);
                double sx = visualWidth / renderWidth;
                double sy = visualHeight / renderHeight;
                foreach (PageAnnotation annotation in pageAnnotations!)
                {
                    // A save writes measurements as real measurement annotations instead
                    // (PdfEngineIntegration.WriteMeasurements); prints and exports draw them in.
                    if (measurementsAsAnnotations && annotation is MeasurementAnnotation) continue;
                    DrawAnnotation(content, annotation, sx, sy, fonts);
                }
            }

            content.RestoreState();
            if (forRasterization)
                editor.AppendPageArtifact(pageIndex, page.Width, page.Height, content);
            else
            {
                var descriptions = new List<string>();
                if (hasAnnotations)
                    foreach (PageAnnotation annotation in pageAnnotations!)
                        descriptions.Add(annotation is TextAnnotation text && !string.IsNullOrWhiteSpace(text.Content)
                            ? text.Content : annotation is SignatureAnnotation ? "Signature" : "Markup");
                if (hasNumber) descriptions.Add("Page number");
                if (hasWatermark) descriptions.Add("Watermark");
                editor.AppendPageDescribedContent(pageIndex, page.Width, page.Height, content,
                    string.Join("\n", descriptions));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = editor.Build();
        cancellationToken.ThrowIfCancellationRequested();
        Replace(path, result);
    }

    // An in-place edit replaces a line of the page's own text: its cover hides the old glyphs and its
    // text draws the new ones. Saving removes the hidden glyphs as well, so the old words cannot be
    // found or copied and are not read back by the next Edit Text on that line. A cover that lets the
    // page show through (partly erased or translucent) keeps the text under it.
    private static byte[]? RemoveReplacedText(PdfDocument document, IReadOnlyList<PdfPageInformation> pages,
        IReadOnlyDictionary<int, List<PageAnnotation>> annotations,
        IReadOnlyDictionary<int, (int w, int h)> renderDims, int? onlyPage, IReadOnlyDictionary<int, int>? rotations)
    {
        var regions = new List<PdfRedactionRegion>();
        foreach ((int pageIndex, List<PageAnnotation> items) in annotations)
        {
            if (pageIndex < 0 || pageIndex >= pages.Count || onlyPage.HasValue && pageIndex != onlyPage.Value)
                continue;
            var edits = new HashSet<string>(items.OfType<TextAnnotation>()
                .Where(text => text.PairId.Length > 0).Select(text => text.PairId));
            if (edits.Count == 0) continue;
            PdfPageInformation page = pages[pageIndex];
            int rotation = rotations?.TryGetValue(pageIndex, out int overrideRotation) == true
                ? NormalizeRotation(overrideRotation) : page.Rotation;
            double visualWidth = rotation is 90 or 270 ? page.Height : page.Width;
            double visualHeight = rotation is 90 or 270 ? page.Width : page.Height;
            var (renderWidth, renderHeight) = AnnotationSpace(renderDims, pageIndex, visualWidth, visualHeight);
            foreach (CoverAnnotation cover in items.OfType<CoverAnnotation>())
            {
                if (!edits.Contains(cover.PairId) || cover.GetColor().A < 255 || cover.Erases is { Count: > 0 })
                    continue;
                var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(cover.DrawRect(),
                    page.Width, page.Height, renderWidth, renderHeight, rotation);
                if (right > left && top > bottom)
                    regions.Add(new PdfRedactionRegion(pageIndex, new PdfContentBounds(left, bottom, right, top)));
            }
        }
        if (regions.Count == 0) return null;
        try { return PdfContentRedaction.RemoveCoveredText(document, regions); }
        catch (Exception error) when (error is FormatException or InvalidOperationException
            or NotSupportedException or ArgumentException)
        {
            // The cover still hides the old line, so the save goes ahead without the cleanup.
            return null;
        }
    }

    /// <summary>
    /// The annotation coordinate space of one page: its recorded render size when it has been drawn,
    /// otherwise the canonical size the viewer lays it out at. Rotation remaps and the burn both use
    /// this, so annotations on a page that was not drawn since the last reload stay in one frame.
    /// </summary>
    internal static (int Width, int Height) AnnotationSpace(
        IReadOnlyDictionary<int, (int w, int h)> renderDims, int pageIndex, double visualWidth, double visualHeight)
        => renderDims.TryGetValue(pageIndex, out var stored) && stored.w > 0 && stored.h > 0
            ? (stored.w, stored.h) : CanonicalRenderDimensions(visualWidth, visualHeight);

    internal static (int Width, int Height) CanonicalRenderDimensions(double width, double height)
    {
        double longest = Math.Max(1, Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(2048 * width / longest)),
            Math.Max(1, (int)Math.Round(2048 * height / longest)));
    }

    private static void DrawAnnotation(PdfContentStreamBuilder content, PageAnnotation annotation,
        double sx, double sy, Dictionary<string, TrueTypeFont> fonts)
    {
        switch (annotation)
        {
            case TextAnnotation text: DrawText(content, text, sx, sy, fonts); break;
            case HighlightAnnotation highlight: DrawHighlight(content, highlight, sx, sy); break;
            case InkAnnotation ink: DrawInk(content, ink, sx, sy); break;
            case SignatureAnnotation signature: DrawSignature(content, signature, sx, sy); break;
            case ImageAnnotation image: DrawImage(content, image.ImageData, image.Position,
                image.SourceWidth * image.Scale, image.SourceHeight * image.Scale, sx, sy); break;
            case MeasurementAnnotation measurement: DrawMeasurement(content, measurement, sx, sy, fonts); break;
        }
    }

    // The same picture the page overlay shows: the measured lines, the arc of an angle, and the
    // value on a white box at the label point.
    private static void DrawMeasurement(PdfContentStreamBuilder content, MeasurementAnnotation measurement,
        double sx, double sy, Dictionary<string, TrueTypeFont> fonts)
    {
        if (!MeasurementGeometry.IsUsable(measurement)) return;
        var points = measurement.Points;
        content.SaveState().SetStrokeRgb(measurement.ColorR / 255d, measurement.ColorG / 255d, measurement.ColorB / 255d)
            .SetOpacity(measurement.ColorA / 255d, measurement.ColorA / 255d)
            .SetLineWidth(Math.Max(.01, measurement.StrokeWidth * sy))
            .SetLineCap(PdfLineCap.Round).SetLineJoin(PdfLineJoin.Round)
            .MoveTo(points[0].X * sx, points[0].Y * sy);
        foreach (WpfPoint point in points.Skip(1)) content.LineTo(point.X * sx, point.Y * sy);
        if (measurement.Kind == MeasurementKind.Area
            || (measurement.Kind == MeasurementKind.Perimeter && measurement.Closed))
            content.ClosePath();
        content.Stroke();
        foreach (var (a, b) in MeasurementGeometry.DistanceTicks(measurement, sy))
            content.MoveTo(a.X * sx, a.Y * sy).LineTo(b.X * sx, b.Y * sy).Stroke();
        if (measurement.Kind == MeasurementKind.Angle)
        {
            List<WpfPoint> arc = MeasurementGeometry.ArcPoints(measurement);
            if (arc.Count > 1)
            {
                content.MoveTo(arc[0].X * sx, arc[0].Y * sy);
                foreach (WpfPoint point in arc.Skip(1)) content.LineTo(point.X * sx, point.Y * sy);
                content.Stroke();
            }
        }
        content.RestoreState();

        string label = MeasurementGeometry.Label(measurement, sy, System.Globalization.CultureInfo.CurrentCulture);
        TrueTypeFont? font = Font(fonts, "Arial", label, false, false);
        if (font is null) return;
        double size = Math.Max(1, measurement.LabelSize * sy);
        WpfPoint center = measurement.LabelCenter ?? MeasurementGeometry.AutoLabelCenter(measurement);
        center = new WpfPoint(center.X * sx, center.Y * sy);
        double width = Measure(font, label, size);
        Rect box = MeasurementGeometry.LabelBox(center, width, size);
        content.SaveState().SetOpacity(measurement.ColorA / 255d)
            .SetFillRgb(1, 1, 1).Rectangle(box.X, box.Y, box.Width, box.Height).Fill()
            .SetFillRgb(measurement.ColorR / 255d, measurement.ColorG / 255d, measurement.ColorB / 255d)
            .BeginText().SetFont(font, size).SetTextMatrix(1, 0, 0, -1, center.X - width / 2, center.Y + size * 0.36)
            .ShowUnicodeText(label).EndText().RestoreState();
    }

    private static void DrawText(PdfContentStreamBuilder content, TextAnnotation text,
        double sx, double sy, Dictionary<string, TrueTypeFont> fonts)
    {
        int turn = TextTurn.Normalize(text.Rotation);
        if (turn == 0)
        {
            DrawTextBox(content, text, text.Position.X * sx, text.Position.Y * sy,
                Math.Max(1, text.Width * sx), Math.Max(1, text.Height * sy), sx, sy, fonts);
            return;
        }
        // Turned text draws in its own frame, turned about the canvas point where its first line
        // starts, the same way the canvas lays it out.
        WpfPoint origin = text.Position + TextTurn.OriginOffset(turn, text.Width, text.Height);
        var (a, b, c, d) = TextTurn.Matrix(turn);
        content.SaveState().Transform(a, b, c, d, origin.X * sx, origin.Y * sy);
        DrawTextBox(content, text, 0, 0, Math.Max(1, text.Width * sy), Math.Max(1, text.Height * sy), sy, sy, fonts);
        content.RestoreState();
    }

    private static void DrawTextBox(PdfContentStreamBuilder content, TextAnnotation text,
        double x, double y, double width, double height, double sx, double sy,
        Dictionary<string, TrueTypeFont> fonts, bool allowBlur = true, bool invisible = false)
    {
        if (allowBlur && text.HasFill) FillRect(content, x, y, width, height,
            text.BgR, text.BgG, text.BgB, text.BgA);
        if (string.IsNullOrEmpty(text.Content)) return;
        TrueTypeFont? font = Font(fonts, text.FontName, text.Content, text.Bold, text.Italic);
        if (font is null) return;
        double blur = text.PairId.Length > 0 && double.IsFinite(text.BlurRadius)
            ? Math.Clamp(text.BlurRadius * sy, 0, 8) : 0;
        if (allowBlur && blur > 0)
        {
            DrawBlurredText(content, text, x, y, width, height, sx, sy, blur, fonts);
            DrawTextBox(content, text, x, y, width, height, sx, sy, fonts, allowBlur: false, invisible: true);
            return;
        }
        double size = Math.Max(1, text.FontSize * sy);
        double padX = 2 * sx, padY = 2 * sy;
        double lineHeight = size * 1.2;
        double characterSpacing = text.LetterSpacing * sy;
        var lines = Wrap(text.Content, font, size, Math.Max(1, width - 2 * padX), characterSpacing);
        content.SaveState().SetFillRgb(text.ColorR / 255d, text.ColorG / 255d, text.ColorB / 255d)
            .SetOpacity(text.ColorA / 255d).BeginText().SetFont(font, size)
            .SetTextRenderingMode(invisible ? PdfTextRenderingMode.Invisible : PdfTextRenderingMode.Fill)
            .SetCharacterSpacing(characterSpacing);
        // WPF positions glyphs at the typeface's own baseline, which is not necessarily one em below
        // the top of the TextBlock. Assuming a full em here made saved text jump vertically even
        // though the overlay looked correctly placed before save (#273).
        double baseline = y + padY + BaselineRatio(text.FontName, text.Bold, text.Italic) * size;
        foreach ((string paragraph, int start, string line) in lines)
        {
            if (baseline > y + height) break;
            double lineWidth = Measure(font, line, size, characterSpacing);
            var runs = BidiLine.VisualRuns(paragraph, start, line.Length);
            if (runs.Count == 1 && !runs[0].RightToLeft)
                content.SetTextMatrix(1, 0, 0, -1, x + padX, baseline).ShowUnicodeText(line);
            else
            {
                // Hebrew, and any numbers or Latin inside it, lands run by run where the text box shows
                // it (#435). Right-to-left runs are written in reading order from their right edge, and
                // a mirrored bracket still copies as the bracket that was typed.
                double cursor = x + padX;
                foreach (BidiRun run in runs)
                {
                    if (run.RightToLeft)
                    {
                        // Each text element is placed on its own, so spacing falls between elements,
                        // not between a letter and its marks.
                        var elements = new List<string>();
                        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(run.Text);
                        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
                        var glyphs = elements.Select(element => BidiLine.Mirrored(element) is var mirrored
                            && mirrored != element && font.GetGlyphId(mirrored[0]) != 0 ? mirrored : element).ToList();
                        double runWidth = glyphs.Sum(glyph => Measure(font, glyph, size))
                            + Math.Max(0, glyphs.Count - 1) * characterSpacing;
                        content.SetCharacterSpacing(0);
                        double right = cursor + runWidth;
                        for (int index = 0; index < elements.Count; index++)
                        {
                            right -= Measure(font, glyphs[index], size);
                            content.SetTextMatrix(1, 0, 0, -1, right, baseline);
                            if (ReferenceEquals(glyphs[index], elements[index])) content.ShowUnicodeText(elements[index]);
                            else content.ShowUnicodeText(glyphs[index], elements[index]);
                            right -= characterSpacing;
                        }
                        content.SetCharacterSpacing(characterSpacing);
                        cursor += runWidth + characterSpacing;
                    }
                    else
                    {
                        content.SetTextMatrix(1, 0, 0, -1, cursor, baseline).ShowUnicodeText(run.Text);
                        cursor += Measure(font, run.Text, size, characterSpacing) + characterSpacing;
                    }
                }
            }
            if (!invisible && text.Underline) DrawRuleAfterText(content, x + padX, baseline + size * .12, lineWidth, size);
            if (!invisible && text.Strike) DrawRuleAfterText(content, x + padX, baseline - size * .3, lineWidth, size);
            baseline += lineHeight;
        }
        content.EndText().RestoreState();
    }

    private static void DrawBlurredText(PdfContentStreamBuilder content, TextAnnotation text,
        double x, double y, double width, double height, double sx, double sy, double blur,
        Dictionary<string, TrueTypeFont> fonts)
    {
        double margin = Math.Ceiling(blur * 3 + Math.Max(1, text.FontSize * sy) * .5);
        double layerWidth = width + 2 * margin, layerHeight = height + 2 * margin;
        double density = Math.Min(600d / 72, Math.Min(4096 / layerWidth, 4096 / layerHeight));
        density = Math.Min(density, Math.Sqrt(1_048_576d / (layerWidth * layerHeight)));
        int pixelWidth = Math.Max(1, (int)Math.Floor(layerWidth * density));
        int pixelHeight = Math.Max(1, (int)Math.Floor(layerHeight * density));
        var layer = new PdfContentStreamBuilder().SaveState().Transform(1, 0, 0, -1, 0, layerHeight);
        DrawTextBox(layer, text, margin, margin, width, height, sx, sy, fonts, allowBlur: false);
        layer.RestoreState();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(layerWidth, layerHeight, layer).Build());
        var rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(pixelWidth, pixelHeight, transparentBackground: true) { CacheResult = false });
        float[] alpha = new float[pixelWidth * pixelHeight];
        for (int pixel = 0; pixel < alpha.Length; pixel++) alpha[pixel] = rendered.Pixels.Span[pixel * 4 + 3];
        float[] temporary = new float[alpha.Length];
        BlurAlpha(alpha, temporary, pixelWidth, pixelHeight, blur * pixelWidth / layerWidth, horizontal: true);
        BlurAlpha(temporary, alpha, pixelWidth, pixelHeight, blur * pixelHeight / layerHeight, horizontal: false);
        byte[] rgba = new byte[alpha.Length * 4];
        for (int pixel = 0; pixel < alpha.Length; pixel++)
        {
            rgba[pixel * 4] = text.ColorR;
            rgba[pixel * 4 + 1] = text.ColorG;
            rgba[pixel * 4 + 2] = text.ColorB;
            rgba[pixel * 4 + 3] = (byte)Math.Clamp(Math.Round(alpha[pixel]), 0, 255);
        }
        content.SaveState().DrawImage(EngineImage.FromRgba(pixelWidth, pixelHeight, rgba),
            x - margin, y - margin + layerHeight, layerWidth, -layerHeight).RestoreState();
    }

    private static void BlurAlpha(float[] source, float[] destination, int width, int height,
        double sigma, bool horizontal)
    {
        if (sigma < .01)
        {
            source.CopyTo(destination, 0);
            return;
        }
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        double[] kernel = new double[radius * 2 + 1];
        double total = 0;
        for (int index = -radius; index <= radius; index++)
        {
            double weight = Math.Exp(-index * index / (2 * sigma * sigma));
            kernel[index + radius] = weight;
            total += weight;
        }
        for (int index = 0; index < kernel.Length; index++) kernel[index] /= total;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double value = 0;
                for (int offset = -radius; offset <= radius; offset++)
                {
                    int sampleX = horizontal ? x + offset : x;
                    int sampleY = horizontal ? y : y + offset;
                    if ((uint)sampleX < (uint)width && (uint)sampleY < (uint)height)
                        value += source[sampleY * width + sampleX] * kernel[offset + radius];
                }
                destination[y * width + x] = (float)value;
            }
    }

    private static void DrawRuleAfterText(PdfContentStreamBuilder content, double x, double y,
        double width, double size)
    {
        content.EndText().SetLineWidth(Math.Max(.5, size / 16)).MoveTo(x, y).LineTo(x + width, y)
            .Stroke().BeginText();
    }

    private static void DrawHighlight(PdfContentStreamBuilder content, HighlightAnnotation highlight,
        double sx, double sy)
    {
        var color = highlight.GetColor();
        Rect rect = highlight.DrawRect();
        content.SaveState().SetFillRgb(color.R / 255d, color.G / 255d, color.B / 255d)
            .SetOpacity(color.A / 255d);
        if (highlight is not CoverAnnotation && highlight.Style == HighlightStyle.Fill)
            content.SetBlendMode(PdfBlendMode.Multiply);
        if (PdfBurn.HighlightEraseGeometry(highlight) is { } geometry)
        {
            foreach (var figure in geometry.GetFlattenedPathGeometry().Figures)
            {
                content.MoveTo(figure.StartPoint.X * sx, figure.StartPoint.Y * sy);
                foreach (var segment in figure.Segments)
                    if (segment is System.Windows.Media.PolyLineSegment poly)
                        foreach (WpfPoint point in poly.Points) content.LineTo(point.X * sx, point.Y * sy);
                    else if (segment is System.Windows.Media.LineSegment line)
                        content.LineTo(line.Point.X * sx, line.Point.Y * sy);
                content.ClosePath();
            }
            content.Fill();
        }
        else content.Rectangle(rect.X * sx, rect.Y * sy, rect.Width * sx, rect.Height * sy).Fill();
        content.RestoreState();
    }

    private static void DrawInk(PdfContentStreamBuilder content, InkAnnotation ink, double sx, double sy)
    {
        if (ink.Points.Count < 2) return;
        content.SaveState();
        if (ink.HasFill)
        {
            content.SetFillRgb(ink.FillR / 255d, ink.FillG / 255d, ink.FillB / 255d)
                .SetOpacity(ink.FillA / 255d).MoveTo(ink.Points[0].X * sx, ink.Points[0].Y * sy);
            foreach (WpfPoint point in ink.Points.Skip(1)) content.LineTo(point.X * sx, point.Y * sy);
            content.ClosePath().FillEvenOdd();
        }
        content.SetStrokeRgb(ink.ColorR / 255d, ink.ColorG / 255d, ink.ColorB / 255d)
            .SetOpacity(1, ink.ColorA / 255d).SetLineWidth(Math.Max(.01, ink.StrokeWidth * sx))
            .SetLineCap(PdfLineCap.Round).SetLineJoin(PdfLineJoin.Round)
            .MoveTo(ink.Points[0].X * sx, ink.Points[0].Y * sy);
        foreach (WpfPoint point in ink.Points.Skip(1)) content.LineTo(point.X * sx, point.Y * sy);
        content.Stroke().RestoreState();
    }

    private static void DrawSignature(PdfContentStreamBuilder content, SignatureAnnotation signature,
        double sx, double sy)
    {
        if (signature.ImageData is not null)
        {
            DrawImage(content, signature.ImageData, signature.Position,
                signature.SourceWidth * signature.Scale, signature.SourceHeight * signature.Scale, sx, sy);
            return;
        }
        if (signature.Vectors is { Count: > 0 } vectors)
        {
            DrawSignatureVectors(content, signature, vectors, sx, sy);
            return;
        }
        content.SaveState().SetStrokeRgb(0, 0, 0)
            .SetLineWidth(Math.Max(.01, signature.StrokeWidth * signature.Scale * sx))
            .SetLineCap(PdfLineCap.Round).SetLineJoin(PdfLineJoin.Round);
        foreach (var stroke in signature.Strokes)
        {
            if (stroke.Count < 2) continue;
            content.MoveTo((signature.Position.X + stroke[0].X * signature.Scale) * sx,
                (signature.Position.Y + stroke[0].Y * signature.Scale) * sy);
            foreach (WpfPoint point in stroke.Skip(1))
                content.LineTo((signature.Position.X + point.X * signature.Scale) * sx,
                    (signature.Position.Y + point.Y * signature.Scale) * sy);
            content.Stroke();
        }
        content.RestoreState();
    }

    // An SVG signature (#398) goes into the page as vector outlines, in the signature's own
    // units under one transform, so it stays sharp at any zoom.
    private static void DrawSignatureVectors(PdfContentStreamBuilder content, SignatureAnnotation signature,
        List<SignatureVector> vectors, double sx, double sy)
    {
        content.SaveState().Transform(signature.Scale * sx, 0, 0, signature.Scale * sy,
            signature.Position.X * sx, signature.Position.Y * sy);
        foreach (SignatureVector vector in vectors)
        {
            List<VectorSegment> segments;
            try { segments = SvgPathData.Parse(vector.Data); }
            catch (FormatException) { continue; }
            bool fill = vector.Fill is uint fillColor && fillColor >> 24 > 0;
            bool stroke = vector.Stroke is uint strokeColor && strokeColor >> 24 > 0 && vector.StrokeWidth > 0;
            if (segments.Count == 0 || !fill && !stroke) continue;
            content.SaveState();
            uint fillArgb = vector.Fill ?? 0, strokeArgb = vector.Stroke ?? 0;
            if (fill) content.SetFillRgb(Channel(fillArgb, 16), Channel(fillArgb, 8), Channel(fillArgb, 0));
            if (stroke)
            {
                content.SetStrokeRgb(Channel(strokeArgb, 16), Channel(strokeArgb, 8), Channel(strokeArgb, 0))
                    .SetLineWidth(vector.StrokeWidth)
                    .SetLineCap(Enum.IsDefined((PdfLineCap)vector.LineCap) ? (PdfLineCap)vector.LineCap : PdfLineCap.Butt)
                    .SetLineJoin(Enum.IsDefined((PdfLineJoin)vector.LineJoin) ? (PdfLineJoin)vector.LineJoin : PdfLineJoin.Miter);
            }
            double fillAlpha = fill ? Channel(fillArgb, 24) : 1, strokeAlpha = stroke ? Channel(strokeArgb, 24) : 1;
            if (fillAlpha < 1 || strokeAlpha < 1) content.SetOpacity(fillAlpha, strokeAlpha);
            foreach (VectorSegment segment in segments)
                switch (segment.Verb)
                {
                    case VectorVerb.Move: content.MoveTo(segment.X, segment.Y); break;
                    case VectorVerb.Line: content.LineTo(segment.X, segment.Y); break;
                    case VectorVerb.Cubic:
                        content.CurveTo(segment.X1, segment.Y1, segment.X2, segment.Y2, segment.X, segment.Y);
                        break;
                    default: content.ClosePath(); break;
                }
            if (fill && stroke)
            {
                if (vector.EvenOdd) content.FillAndStrokeEvenOdd(); else content.FillAndStroke();
            }
            else if (fill)
            {
                if (vector.EvenOdd) content.FillEvenOdd(); else content.Fill();
            }
            else content.Stroke();
            content.RestoreState();
        }
        content.RestoreState();
    }

    private static double Channel(uint argb, int shift) => ((argb >> shift) & 0xFF) / 255d;

    private static void DrawImage(PdfContentStreamBuilder content, string data, WpfPoint position,
        double width, double height, double sx, double sy)
    {
        try
        {
            EngineImage? image = LoadImage(Convert.FromBase64String(data));
            double scaledHeight = height * sy;
            if (image is not null) content.DrawImage(image, position.X * sx,
                position.Y * sy + scaledHeight, width * sx, -scaledHeight);
        }
        catch { }
    }

    private static void DrawNumber(PdfContentStreamBuilder content, StampSpec spec, int pageIndex,
        int firstPage, int total, double pageWidth, double pageHeight,
        Dictionary<string, TrueTypeFont> fonts)
    {
        int number = spec.StartNumber + Math.Max(0, pageIndex - firstPage);
        string text = (string.IsNullOrEmpty(spec.Format) ? "{n}" : spec.Format)
            .Replace("{n}", number.ToString()).Replace("{N}", total.ToString());
        TrueTypeFont? font = Font(fonts, "Segoe UI", text, false, false);
        if (font is null || text.Length == 0) return;
        double size = Math.Max(1, spec.NumFontPt), width = Measure(font, text, size), height = size * 1.2;
        double mx = pageWidth * .05, my = pageHeight * .04;
        int horizontal = spec.NumPosH;
        double x, y;
        if (horizontal < 0)
        {
            double customX = spec.NumMirror && pageIndex % 2 == 1 ? 1 - spec.NumCustomX : spec.NumCustomX;
            x = customX * pageWidth - width / 2; y = spec.NumCustomY * pageHeight - height / 2;
        }
        else
        {
            if (spec.NumMirror && horizontal != 1 && pageIndex % 2 == 1) horizontal = 2 - horizontal;
            x = horizontal == 0 ? mx : horizontal == 2 ? pageWidth - width - mx : (pageWidth - width) / 2;
            y = spec.NumPosV == 0 ? my : spec.NumPosV == 1 ? (pageHeight - height) / 2 : pageHeight - height - my;
        }
        DrawSingleLine(content, font, size, text, x, y + size, spec.NumColor.R, spec.NumColor.G, spec.NumColor.B, 255);
    }

    private static void DrawWatermark(PdfContentStreamBuilder content, StampSpec spec, EngineImage? image,
        double pageWidth, double pageHeight, Dictionary<string, TrueTypeFont> fonts)
    {
        double width, height;
        TrueTypeFont? font = null;
        if (spec.WmIsImage)
        {
            if (image is null) return;
            width = pageWidth * .5 * spec.WmScale;
            height = width * image.Height / Math.Max(1d, image.Width);
        }
        else
        {
            if (string.IsNullOrEmpty(spec.WmText)) return;
            font = Font(fonts, spec.WmFont, spec.WmText, true, false);
            if (font is null) return;
            height = Math.Max(1, spec.WmFontPt) * 1.2;
            width = Measure(font, spec.WmText, Math.Max(1, spec.WmFontPt));
        }
        double mx = pageWidth * .05, my = pageHeight * .04;
        double cx = spec.WmPosH < 0 ? spec.WmCustomX * pageWidth
            : spec.WmPosH == 0 ? mx + width / 2 : spec.WmPosH == 2 ? pageWidth - mx - width / 2 : pageWidth / 2;
        double cy = spec.WmPosH < 0 ? spec.WmCustomY * pageHeight
            : spec.WmPosV == 0 ? my + height / 2 : spec.WmPosV == 1 ? pageHeight / 2 : pageHeight - my - height / 2;
        double radians = -spec.WmAngle * Math.PI / 180;
        content.SaveState().Transform(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), cx, cy);
        if (image is not null) content.SetOpacity(spec.WmOpacity).DrawImage(image, -width / 2, -height / 2, width, height);
        else DrawSingleLine(content, font!, Math.Max(1, spec.WmFontPt), spec.WmText,
            -width / 2, Math.Max(1, spec.WmFontPt) / 2, spec.WmColor.R, spec.WmColor.G, spec.WmColor.B,
            (byte)Math.Clamp(spec.WmOpacity * 255, 0, 255));
        content.RestoreState();
    }

    private static void DrawSingleLine(PdfContentStreamBuilder content, TrueTypeFont font, double size,
        string text, double x, double baseline, byte r, byte g, byte b, byte alpha) =>
        content.SaveState().SetFillRgb(r / 255d, g / 255d, b / 255d).SetOpacity(alpha / 255d)
            .BeginText().SetFont(font, size).SetTextMatrix(1, 0, 0, -1, x, baseline)
            .ShowUnicodeText(text).EndText().RestoreState();

    private static void FillRect(PdfContentStreamBuilder content, double x, double y, double width,
        double height, byte r, byte g, byte b, byte alpha) => content.SaveState()
            .SetFillRgb(r / 255d, g / 255d, b / 255d).SetOpacity(alpha / 255d)
            .Rectangle(x, y, width, height).Fill().RestoreState();

    private static TrueTypeFont? Font(Dictionary<string, TrueTypeFont> cache, string wanted,
        string text, bool bold, bool italic)
    {
        string family = FontCoverage.PickFamily(string.IsNullOrWhiteSpace(wanted) ? "Segoe UI" : wanted, text);
        string key = $"{family}|{bold}|{italic}";
        if (cache.TryGetValue(key, out TrueTypeFont? found)) return found;
        byte[]? bytes = InstalledFontCatalog.FaceBytes(family, bold, italic)
            ?? InstalledFontCatalog.RegularFaceBytes(family);
        if (bytes is null) return null;
        try { found = TrueTypeFont.Load(bytes); }
        catch { return null; }
        cache[key] = found;
        return found;
    }

    // Each wrapped line with its hard paragraph and where it starts there: bidi directions are
    // resolved over the whole paragraph, as the text box does, and only reordering is per line.
    private static List<(string Paragraph, int Start, string Line)> Wrap(string text, TrueTypeFont font,
        double size, double width, double characterSpacing = 0)
    {
        var result = new List<(string, int, string)>();
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = "";
            int lineStart = 0, wordStart = 0;
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(font, candidate, size, characterSpacing) > width)
                {
                    result.Add((paragraph, lineStart, line));
                    line = "";
                }
                if (line.Length == 0 && Measure(font, word, size, characterSpacing) > width)
                {
                    var fragment = new StringBuilder();
                    int fragmentStart = wordStart;
                    foreach (Rune rune in word.EnumerateRunes())
                    {
                        string next = fragment + rune.ToString();
                        if (fragment.Length > 0 && Measure(font, next, size, characterSpacing) > width)
                        {
                            result.Add((paragraph, fragmentStart, fragment.ToString()));
                            fragmentStart += fragment.Length;
                            fragment.Clear();
                        }
                        fragment.Append(rune.ToString());
                    }
                    line = fragment.ToString();
                    lineStart = fragmentStart;
                }
                else if (line.Length == 0) { line = word; lineStart = wordStart; }
                else line = candidate;
                wordStart += word.Length + 1;
            }
            result.Add((paragraph, lineStart, line));
        }
        return result;
    }

    private static double Measure(TrueTypeFont font, string text, double size) => text.EnumerateRunes()
        .Sum(rune => font.GetPdfAdvanceWidth(font.GetGlyphId(rune.Value))) * size / 1000;

    private static double Measure(TrueTypeFont font, string text, double size, double characterSpacing)
    {
        int count = text.EnumerateRunes().Count();
        return Measure(font, text, size) + Math.Max(0, count - 1) * characterSpacing;
    }

    internal static double BaselineRatio(string family, bool bold, bool italic)
    {
        try
        {
            var typeface = new System.Windows.Media.Typeface(
                new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family),
                italic ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal,
                bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out System.Windows.Media.GlyphTypeface glyph)
                && double.IsFinite(glyph.Baseline) && glyph.Baseline is > .5 and < 1.5)
                return glyph.Baseline;
        }
        catch { }
        return .8;
    }

    private static EngineImage? LoadImageFile(string? path, double opacity)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return LoadImage(File.ReadAllBytes(path), opacity); } catch { return null; }
    }

    private static EngineImage? LoadImage(byte[] bytes, double opacity = 1)
    {
        using var stream = new MemoryStream(bytes);
        using var source = Image.FromStream(stream);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.DrawImage(source, 0, 0, bitmap.Width, bitmap.Height);
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData locked = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[Math.Abs(locked.Stride) * locked.Height];
            Marshal.Copy(locked.Scan0, bgra, 0, bgra.Length);
            byte[] rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int sourceOffset = y * Math.Abs(locked.Stride) + x * 4;
                    int target = (y * bitmap.Width + x) * 4;
                    rgba[target] = bgra[sourceOffset + 2]; rgba[target + 1] = bgra[sourceOffset + 1];
                    rgba[target + 2] = bgra[sourceOffset];
                    rgba[target + 3] = (byte)(bgra[sourceOffset + 3] * Math.Clamp(opacity, 0, 1));
                }
            return EngineImage.FromRgba(bitmap.Width, bitmap.Height, rgba);
        }
        finally { bitmap.UnlockBits(locked); }
    }

    private static void ApplyVisualTransform(PdfContentStreamBuilder content, double x, double y,
        double width, double height, int rotation)
    {
        switch (rotation)
        {
            case 0: content.Transform(1, 0, 0, -1, x, y + height); break;
            case 90: content.Transform(0, 1, 1, 0, x, y); break;
            case 180: content.Transform(-1, 0, 0, 1, x + width, y); break;
            case 270: content.Transform(0, -1, -1, 0, x + width, y + height); break;
        }
    }

    private static int NormalizeRotation(int rotation) => ((rotation % 360) + 360) % 360;

    private static void Replace(string path, byte[] result)
        => AtomicFileOutput.Write(path, result);
}
