using System.Text;

namespace KillerPdf.Engine.Documents;

/// <summary>Recognizes one BGRA image with optional character constraints.</summary>
public delegate PdfOcrResult PdfOcrRasterRecognition(
    ReadOnlyMemory<byte> bgra, int width, int height,
    string? characterWhitelist, CancellationToken cancellationToken);

/// <summary>Coordinates page and field OCR without depending on a recognition backend.</summary>
public static class PdfOcrFormRecognizer
{
    /// <summary>Recognizes a page using form widgets mapped to its rendered pixels.</summary>
    public static PdfOcrResult Recognize(PdfOcrRasterRecognition recognizer,
        ReadOnlyMemory<byte> bgra, int width, int height,
        IReadOnlyList<PdfFormWidgetInfo> widgets, int additionalRotation = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(widgets);
        IReadOnlyList<PdfOcrFormRegion> regions = PdfOcrFormLayout.MapRegions(
            widgets, width, height, additionalRotation);
        return Recognize(recognizer, bgra, width, height, regions, cancellationToken);
    }

    /// <summary>Recognizes a page while applying form-region constraints and normalization.</summary>
    public static PdfOcrResult Recognize(PdfOcrRasterRecognition recognizer,
        ReadOnlyMemory<byte> bgra, int width, int height,
        IReadOnlyList<PdfOcrFormRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(regions);
        ValidateRegions(regions, width, height);
        PdfOcrResult full = recognizer(
            bgra, width, height, null, cancellationToken);
        if (regions.Count == 0) return full;

        var words = full.Words.Where(word => !regions.Any(region =>
            Contains(region, (word.Left + word.Right) / 2,
                (word.Top + word.Bottom) / 2))).ToList();
        foreach (PdfOcrFormRegion region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string text, float confidence) = region.IsComb
                ? RecognizeComb(recognizer, bgra, width, height, region,
                    cancellationToken)
                : RecognizeField(recognizer, bgra, width, height, region,
                    cancellationToken);
            if (text.Length == 0) continue;
            if (region.ChoiceValues.Count > 0)
                text = PdfOcrFormLayout.NormalizeChoice(text, region.ChoiceValues);
            if (region.MaximumLength > 0 && text.Length > region.MaximumLength)
                text = text[..region.MaximumLength];
            words.Add(new PdfOcrPixelWord(text, confidence,
                region.Left, region.Top, region.Right, region.Bottom));
        }
        return PdfOcrResult.FromWords(words);
    }

    private static (string Text, float Confidence) RecognizeField(
        PdfOcrRasterRecognition recognizer, ReadOnlyMemory<byte> source,
        int sourceWidth, int sourceHeight, PdfOcrFormRegion region,
        CancellationToken cancellationToken)
    {
        PdfOcrBgraImage crop = PdfOcrImagePreprocessor.CropBgra(
            source, sourceWidth, sourceHeight, region.Left, region.Top,
            region.Right - region.Left, region.Bottom - region.Top,
            cancellationToken);
        PdfOcrResult field = recognizer(crop.Pixels, crop.Width, crop.Height,
            region.CharacterWhitelist, cancellationToken);
        return (string.Join(" ", field.Words.Select(word => word.Text)).Trim(),
            field.MeanConfidence);
    }

    private static (string Text, float Confidence) RecognizeComb(
        PdfOcrRasterRecognition recognizer, ReadOnlyMemory<byte> source,
        int sourceWidth, int sourceHeight, PdfOcrFormRegion region,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder(region.MaximumLength);
        double confidence = 0;
        int recognized = 0;
        for (int cell = 0; cell < region.MaximumLength; cell++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int left = region.Left + (int)Math.Round(
                (double)(region.Right - region.Left) * cell / region.MaximumLength);
            int right = region.Left + (int)Math.Round(
                (double)(region.Right - region.Left) * (cell + 1)
                / region.MaximumLength);
            var cellRegion = region with
            {
                Left = left,
                Right = Math.Max(left + 1, right),
                IsComb = false
            };
            (string value, float cellConfidence) = RecognizeField(
                recognizer, source, sourceWidth, sourceHeight, cellRegion,
                cancellationToken);
            if (value.Length == 0) continue;
            text.Append(value[0]);
            confidence += cellConfidence;
            recognized++;
        }
        return (text.ToString(), recognized == 0
            ? 0 : (float)(confidence / recognized));
    }

    private static bool Contains(PdfOcrFormRegion region, int x, int y) =>
        x >= region.Left && x <= region.Right
        && y >= region.Top && y <= region.Bottom;

    private static void ValidateRegions(
        IReadOnlyList<PdfOcrFormRegion> regions, int width, int height)
    {
        for (int index = 0; index < regions.Count; index++)
        {
            PdfOcrFormRegion region = regions[index]
                ?? throw new ArgumentException("An OCR form region is null.", nameof(regions));
            if (region.Left < 0 || region.Top < 0
                || region.Right <= region.Left || region.Bottom <= region.Top
                || region.Right > width || region.Bottom > height)
                throw new ArgumentOutOfRangeException(nameof(regions),
                    "OCR form regions must be contained by the source image.");
            if (region.MaximumLength < 0)
                throw new ArgumentOutOfRangeException(nameof(regions),
                    "OCR form maximum lengths cannot be negative.");
            if (region.ChoiceValues is null)
                throw new ArgumentException(
                    "OCR form choice values cannot be null.", nameof(regions));
            if (region.IsComb && (region.MaximumLength == 0
                || region.MaximumLength > region.Right - region.Left))
                throw new ArgumentOutOfRangeException(nameof(regions),
                    "OCR comb fields require one or more pixels for every character cell.");
        }
    }
}
