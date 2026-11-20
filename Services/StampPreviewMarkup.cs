using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

namespace KillerPDF.Services;

/// <summary>
/// #478: Apply and save put the stamps (watermark and page number) on the page and the markup over them: the
/// viewer paints the stamp layer before the annotations, and the burn draws the watermark and number before the
/// markup. The Stamp preview showed the page with its markup burned in, so stamps drawn over that bitmap covered
/// markup such as a filled Shapes rectangle that Apply then drew over the stamps. The preview now shows the page,
/// the stamps, and the markup in a transparent layer on top, which this builds.
/// Highlighter marks stay in the page layer: they darken what is under them (Multiply), which a transparent layer
/// cannot do, so on top they would lift black text toward the highlight color. Only where a highlighter mark
/// overlaps a stamp does the preview differ from Apply, by drawing the stamp untinted.
/// </summary>
internal static class StampPreviewMarkup
{
    /// <summary>The preview's layers, bottom to top: the page, the stamps, then the page's markup.</summary>
    internal static void Stack(Panel host, UIElement page, UIElement stamps, UIElement markup)
    {
        host.Children.Add(page);
        host.Children.Add(stamps);
        host.Children.Add(markup);
    }

    /// <summary>True for markup the preview keeps in the page layer, under the stamps.</summary>
    internal static bool StaysInPage(PageAnnotation annotation) =>
        annotation is HighlightAnnotation highlight && PdfBurn.IsHighlighterMark(highlight);

    /// <summary>Writes <paramref name="target"/> as <paramref name="clean"/> with only the page's highlighter marks
    /// burned in, the page layer under the stamps. Returns false, writing nothing, when the page has none, and
    /// the clean file is then the page layer.</summary>
    internal static bool BurnPageLayer(string clean, string target, int pageIndex,
        IReadOnlyDictionary<int, List<PageAnnotation>> annotations, IReadOnlyDictionary<int, (int w, int h)> renderDims,
        IReadOnlyDictionary<int, int>? rotations)
    {
        if (!annotations.TryGetValue(pageIndex, out var list)) return false;
        List<PageAnnotation> marks = list.Where(StaysInPage).ToList();
        if (marks.Count == 0) return false;
        File.Copy(clean, target, overwrite: true);
        PdfEngineBurn.Burn(target, new Dictionary<int, List<PageAnnotation>> { [pageIndex] = marks }, renderDims,
            null, pageIndex, rotations);
        return true;
    }

    /// <summary>The page's markup, less its highlighter marks, alone on a transparent page of the page's visual
    /// size, drawn by the same burn that saves it, or null when there is none to draw.</summary>
    internal static BitmapSource? Render(IReadOnlyList<PageAnnotation>? markup, (int w, int h)? renderDims,
        double visualWidth, double visualHeight, int maximumPixels)
    {
        List<PageAnnotation> layer = markup?.Where(annotation => !StaysInPage(annotation)).ToList() ?? [];
        if (layer.Count == 0 || visualWidth <= 0 || visualHeight <= 0) return null;
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-stampmarkup-{Guid.NewGuid():N}.pdf");
        try
        {
            // A blank page in the page's visual orientation, so the markup (kept in visual space) needs no turn.
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(visualWidth, visualHeight).Build());
            var annotations = new Dictionary<int, List<PageAnnotation>> { [0] = layer };
            var dims = new Dictionary<int, (int w, int h)>();
            if (renderDims is { } size) dims[0] = size;
            PdfEngineBurn.Burn(path, annotations, dims, rotations: new Dictionary<int, int> { [0] = 0 },
                forRasterization: true);

            double scale = maximumPixels / Math.Max(visualWidth, visualHeight);
            int width = Math.Max(1, (int)Math.Round(visualWidth * scale));
            int height = Math.Max(1, (int)Math.Round(visualHeight * scale));
            var renderer = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path)));
            var rendered = renderer.Render(0, new PdfRenderOptions(width, height, transparentBackground: true)
                { CacheResult = false });
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                rendered.Pixels.ToArray(), checked(width * 4));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
