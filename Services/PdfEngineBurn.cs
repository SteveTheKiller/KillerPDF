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
using EngineImage = KillerPdf.Engine.Authoring.PdfImage;
using WpfPoint = System.Windows.Point;

namespace KillerPDF.Services;

/// <summary>Burns KillerPDF markup into typed engine page content.</summary>
internal static class PdfEngineBurn
{
    internal static void Burn(
        string path,
        IReadOnlyDictionary<int, List<PageAnnotation>> annotations,
        IReadOnlyDictionary<int, (int w, int h)> renderDims,
        StampSpec? stamps = null,
        int? onlyPage = null,
        IReadOnlyDictionary<int, int>? rotations = null,
        bool forRasterization = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (annotations.Values.Any(items => items.Count > 0))
            PdfEngineIntegration.StripLinkAppearances(path);
        var document = PdfDocument.Open(File.ReadAllBytes(path));
        var pages = PdfPageInformation.Read(document);
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
            if (onlyPage.HasValue && pageIndex != onlyPage.Value) continue;
            bool hasAnnotations = annotations.TryGetValue(pageIndex, out var pageAnnotations)
                && pageAnnotations.Count > 0 && renderDims.ContainsKey(pageIndex);
            bool hasNumber = numberPages.Contains(pageIndex);
            bool hasWatermark = watermarkPages.Contains(pageIndex);
            if (!hasAnnotations && !hasNumber && !hasWatermark) continue;

            PdfPageInformation page = pages[pageIndex];
            int rotation = rotations?.TryGetValue(pageIndex, out int overrideRotation) == true
                ? NormalizeRotation(overrideRotation) : page.Rotation;
            double visualWidth = rotation is 90 or 270 ? page.Height : page.Width;
            double visualHeight = rotation is 90 or 270 ? page.Width : page.Height;
            var content = new PdfContentStreamBuilder().SaveState();
            ApplyVisualTransform(content, page.Width, page.Height, rotation);

            if (hasWatermark) DrawWatermark(content, stamps!, watermark, visualWidth, visualHeight, fonts);
            if (hasNumber) DrawNumber(content, stamps!, pageIndex, firstNumberPage, pages.Count,
                visualWidth, visualHeight, fonts);
            if (hasAnnotations)
            {
                var (renderWidth, renderHeight) = renderDims[pageIndex];
                if (renderWidth > 0 && renderHeight > 0)
                {
                    double sx = visualWidth / renderWidth;
                    double sy = visualHeight / renderHeight;
                    foreach (PageAnnotation annotation in pageAnnotations!)
                        DrawAnnotation(content, annotation, sx, sy, fonts);
                }
            }

            content.RestoreState();
            if (forRasterization)
                editor.AppendPageArtifact(pageIndex, page.Width, page.Height, content);
            else
                editor.AppendPageContent(pageIndex, page.Width, page.Height, content);
        }

        Replace(path, editor.Build());
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
        }
    }

    private static void DrawText(PdfContentStreamBuilder content, TextAnnotation text,
        double sx, double sy, Dictionary<string, TrueTypeFont> fonts)
    {
        double x = text.Position.X * sx, y = text.Position.Y * sy;
        double width = Math.Max(1, text.Width * sx), height = Math.Max(1, text.Height * sy);
        if (text.HasFill) FillRect(content, x, y, width, height,
            text.BgR, text.BgG, text.BgB, text.BgA);
        if (string.IsNullOrEmpty(text.Content)) return;
        TrueTypeFont? font = Font(fonts, text.FontName, text.Content, text.Bold, text.Italic);
        if (font is null) return;
        double size = Math.Max(1, text.FontSize * sy);
        double padX = 2 * sx, padY = 2 * sy;
        double lineHeight = size * 1.2;
        double characterSpacing = text.LetterSpacing * sy;
        var lines = Wrap(text.Content, font, size, Math.Max(1, width - 2 * padX), characterSpacing);
        content.SaveState().SetFillRgb(text.ColorR / 255d, text.ColorG / 255d, text.ColorB / 255d)
            .SetOpacity(text.ColorA / 255d).BeginText().SetFont(font, size)
            .SetCharacterSpacing(characterSpacing);
        // WPF positions glyphs at the typeface's own baseline, which is not necessarily one em below
        // the top of the TextBlock. Assuming a full em here made saved text jump vertically even
        // though the overlay looked correctly placed before save (#273).
        double baseline = y + padY + BaselineRatio(text.FontName, text.Bold, text.Italic) * size;
        foreach (string line in lines)
        {
            if (baseline > y + height) break;
            content.SetTextMatrix(1, 0, 0, -1, x + padX, baseline).ShowUnicodeText(line);
            double lineWidth = Measure(font, line, size, characterSpacing);
            if (text.Underline) DrawRuleAfterText(content, x + padX, baseline + size * .12, lineWidth, size);
            if (text.Strike) DrawRuleAfterText(content, x + padX, baseline - size * .3, lineWidth, size);
            baseline += lineHeight;
        }
        content.EndText().RestoreState();
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

    private static List<string> Wrap(string text, TrueTypeFont font, double size, double width,
        double characterSpacing = 0)
    {
        var result = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = "";
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(font, candidate, size, characterSpacing) > width)
                {
                    result.Add(line);
                    line = "";
                }
                if (line.Length == 0 && Measure(font, word, size, characterSpacing) > width)
                {
                    var fragment = new StringBuilder();
                    foreach (Rune rune in word.EnumerateRunes())
                    {
                        string next = fragment + rune.ToString();
                        if (fragment.Length > 0 && Measure(font, next, size, characterSpacing) > width)
                        {
                            result.Add(fragment.ToString());
                            fragment.Clear();
                        }
                        fragment.Append(rune.ToString());
                    }
                    line = fragment.ToString();
                }
                else if (line.Length == 0) line = word;
                else line = candidate;
            }
            result.Add(line);
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

    private static void ApplyVisualTransform(PdfContentStreamBuilder content, double width,
        double height, int rotation)
    {
        switch (rotation)
        {
            case 0: content.Transform(1, 0, 0, -1, 0, height); break;
            case 90: content.Transform(0, 1, 1, 0, 0, 0); break;
            case 180: content.Transform(-1, 0, 0, 1, width, 0); break;
            case 270: content.Transform(0, -1, -1, 0, width, height); break;
        }
    }

    private static int NormalizeRotation(int rotation) => ((rotation % 360) + 360) % 360;

    private static void Replace(string path, byte[] result)
    {
        string fullPath = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { File.WriteAllBytes(temporary, result); File.Move(temporary, fullPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
