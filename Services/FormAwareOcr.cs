using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

internal static class FormAwareOcr
{
    internal static PdfOcrResult Recognize(
        OcrService service, byte[] bgra, int width, int height,
        IReadOnlyList<PdfFormWidgetInfo> widgets, int additionalRotation = 0)
    {
        PdfOcrResult full = service.RecognizeBgra(bgra, width, height);
        IReadOnlyList<PdfOcrFormRegion> regions = PdfOcrFormLayout.MapRegions(
            widgets, width, height, additionalRotation);
        if (regions.Count == 0) return full;

        var words = full.Words.Where(word => !regions.Any(region =>
            Contains(region, (word.Left + word.Right) / 2, (word.Top + word.Bottom) / 2))).ToList();
        foreach (PdfOcrFormRegion region in regions)
        {
            string text;
            float fieldConfidence;
            if (region.IsComb)
            {
                (text, fieldConfidence) = RecognizeComb(service, bgra, width, region);
            }
            else
            {
                byte[] crop = Crop(bgra, width, region, out int cropWidth, out int cropHeight);
                PdfOcrResult field = service.RecognizeBgra(
                    crop, cropWidth, cropHeight, region.CharacterWhitelist);
                text = string.Join(" ", field.Words.Select(word => word.Text)).Trim();
                fieldConfidence = field.MeanConfidence;
            }
            if (text.Length == 0) continue;
            if (region.ChoiceValues.Count > 0)
                text = PdfOcrFormLayout.NormalizeChoice(text, region.ChoiceValues);
            if (region.MaximumLength > 0 && text.Length > region.MaximumLength)
                text = text[..region.MaximumLength];
            words.Add(new PdfOcrPixelWord(text, fieldConfidence,
                region.Left, region.Top, region.Right, region.Bottom));
        }
        return PdfOcrResult.FromWords(words);
    }

    private static bool Contains(PdfOcrFormRegion region, int x, int y) =>
        x >= region.Left && x <= region.Right && y >= region.Top && y <= region.Bottom;

    private static byte[] Crop(byte[] source, int sourceWidth, PdfOcrFormRegion region,
        out int width, out int height)
    {
        width = region.Right - region.Left;
        height = region.Bottom - region.Top;
        var result = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            Array.Copy(source, ((region.Top + row) * sourceWidth + region.Left) * 4,
                result, row * width * 4, width * 4);
        return result;
    }

    private static (string text, float confidence) RecognizeComb(
        OcrService service, byte[] source, int sourceWidth, PdfOcrFormRegion region)
    {
        var text = new System.Text.StringBuilder(region.MaximumLength);
        double confidence = 0;
        int recognized = 0;
        for (int cell = 0; cell < region.MaximumLength; cell++)
        {
            int left = region.Left + (int)Math.Round(
                (double)(region.Right - region.Left) * cell / region.MaximumLength);
            int right = region.Left + (int)Math.Round(
                (double)(region.Right - region.Left) * (cell + 1) / region.MaximumLength);
            var cellRegion = region with { Left = left, Right = Math.Max(left + 1, right), IsComb = false };
            byte[] crop = Crop(source, sourceWidth, cellRegion, out int width, out int height);
            PdfOcrResult result = service.RecognizeBgra(
                crop, width, height, region.CharacterWhitelist);
            string value = string.Concat(result.Words.Select(word => word.Text)).Trim();
            if (value.Length == 0) continue;
            text.Append(value[0]);
            confidence += result.MeanConfidence;
            recognized++;
        }
        return (text.ToString(), recognized == 0 ? 0 : (float)(confidence / recognized));
    }

}
