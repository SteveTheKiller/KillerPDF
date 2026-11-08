using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

namespace KillerPDF.Services;

/// <summary>The page Extract Image selects from: the engine's own sRGB render of the base page, without
/// annotations, form fields, monitor correction or proofing, turned to the orientation the viewer shows.
/// Nothing about the document's proof or the display reaches it, so the extracted pixels are the same
/// whatever the view shows.</summary>
internal static class PageExtractionSource
{
    // 150 dpi, held to about four million pixels so a large page stays inside the selection's working budget.
    internal const double Dpi = 150;
    internal const double MaximumPixels = 4_000_000;

    /// <summary>Returns the frame and its straight BGRA32 bytes.</summary>
    internal static (PdfRenderedFrame Frame, byte[] Bgra) Render(string file, OpenedProtection? protection,
        int pageIndex, PdfPageInformation page, int rotation)
    {
        double scale = Dpi / 72;
        double area = page.Width * page.Height * scale * scale;
        if (area > MaximumPixels) scale *= Math.Sqrt(MaximumPixels / area);
        using var session = PdfPageRenderSession.OpenEngineFirst(file, scale, protection);
        var rendered = session.RenderBasePage(pageIndex);
        (byte[] pixels, int width, int height) = (rendered.Pixels.ToArray(), rendered.Width, rendered.Height);
        (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, rotation - page.Rotation);
        var floats = new float[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            floats[i * 4] = pixels[i * 4 + 2] / 255f;
            floats[i * 4 + 1] = pixels[i * 4 + 1] / 255f;
            floats[i * 4 + 2] = pixels[i * 4] / 255f;
            floats[i * 4 + 3] = pixels[i * 4 + 3] / 255f;
        }
        return (new PdfRenderedFrame(width, height, floats, PdfFrameColorEncoding.SrgbD65), pixels);
    }
}
