using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPDF.Services;

/// <summary>
/// #478: Apply and save put the stamps (watermark and page number) into the page content, under the page's
/// markup: the burn draws the watermark and number before the session markup, and a viewer paints the file's own
/// annotations over the content. The Stamp preview showed the page with all of that already in its bitmap, so
/// stamps drawn over the bitmap covered a filled Shapes rectangle or a saved Square that Apply then drew over them.
/// The preview now has a page layer, the stamps, and a transparent markup layer on top, which this writes.
/// Highlighter marks, from the session or saved in the file, stay in the page layer: they darken what is under
/// them (Multiply), which a transparent layer cannot do, so on top they would lift black text toward the
/// highlight color. Only where a highlighter overlaps a stamp does the preview differ from Apply, by drawing the
/// stamp untinted.
/// </summary>
internal static class StampPreviewMarkup
{
    private static readonly PdfName AnnotsName = new("Annots"u8);
    private static readonly PdfName SubtypeName = new("Subtype"u8);
    private static readonly PdfName KidsName = new("Kids"u8);
    private static readonly PdfName PagesName = new("Pages"u8);
    private static readonly PdfName RootName = new("Root"u8);

    /// <summary>The preview's layers, bottom to top: the page, the stamps, then the page's markup.</summary>
    internal static void Stack(Panel host, UIElement page, UIElement stamps, UIElement markup)
    {
        host.Children.Add(page);
        host.Children.Add(stamps);
        host.Children.Add(markup);
    }

    /// <summary>True for session markup the preview keeps in the page layer, under the stamps.</summary>
    internal static bool StaysInPage(PageAnnotation annotation) =>
        annotation is HighlightAnnotation highlight && PdfBurn.IsHighlighterMark(highlight);

    /// <summary>
    /// Writes the preview's two layers for one page of <paramref name="clean"/> (the saved working copy):
    /// <paramref name="pageLayer"/> is the page with only its highlighter marks, saved and from the session, and
    /// <paramref name="markupLayer"/> is the page with no content, its other saved annotations and form fields,
    /// and the rest of the session markup burned in. Returns false, writing nothing, when the page has no markup,
    /// and the clean file is then the whole preview page.
    /// </summary>
    internal static bool WriteLayers(string clean, string pageLayer, string markupLayer, int pageIndex,
        IReadOnlyDictionary<int, List<PageAnnotation>> annotations, IReadOnlyDictionary<int, (int w, int h)> renderDims,
        IReadOnlyDictionary<int, int>? rotations)
    {
        List<PageAnnotation> session = annotations.TryGetValue(pageIndex, out var list) ? list : [];
        var document = PdfDocument.Open(File.ReadAllBytes(clean));
        (PdfIndirectReference reference, PdfDictionary page) = Page(document, pageIndex);
        List<PdfObject> saved = page.TryGetValue(AnnotsName, out PdfObject value) && Resolve(document, value) is PdfArray array
            ? [.. array] : [];
        if (saved.Count == 0 && session.Count == 0) return false;

        List<PdfObject> under = saved.Where(annotation => IsSavedHighlight(document, annotation)).ToList();
        List<PdfObject> over = saved.Where(annotation => !IsSavedHighlight(document, annotation)).ToList();

        File.WriteAllBytes(pageLayer, WithAnnotations(document, reference, page, under));
        List<PageAnnotation> marks = session.Where(StaysInPage).ToList();
        if (marks.Count > 0)
            PdfEngineBurn.Burn(pageLayer, new Dictionary<int, List<PageAnnotation>> { [pageIndex] = marks }, renderDims,
                null, pageIndex, rotations);

        byte[] annotated = WithAnnotations(document, reference, page, over);
        File.WriteAllBytes(markupLayer, new PdfIncrementalPageEditor(PdfDocument.Open(annotated))
            .SetPageContent(pageIndex, ReadOnlyMemory<byte>.Empty).Build());
        List<PageAnnotation> rest = session.Where(annotation => !StaysInPage(annotation)).ToList();
        if (rest.Count > 0)
            PdfEngineBurn.Burn(markupLayer, new Dictionary<int, List<PageAnnotation>> { [pageIndex] = rest }, renderDims,
                null, pageIndex, rotations, forRasterization: true);
        return true;
    }

    /// <summary>A page of <paramref name="path"/> with a transparent background, turned by the in-app rotation as
    /// the preview's page bitmap is.</summary>
    internal static BitmapSource RenderLayer(string path, int pageIndex, int maximumPixels, int rotation)
    {
        using var session = PdfPageRenderSession.OpenEngineFirst(path, maximumPixels, maximumPixels, null);
        PdfRenderedPage rendered = session.RenderPage(pageIndex, transparentBackground: true);
        byte[] pixels = rendered.Pixels;
        int width = rendered.Width, height = rendered.Height;
        if (rotation % 360 != 0) (pixels, width, height) = BitmapHelpers.RotateBitmap(pixels, width, height, rotation);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsSavedHighlight(PdfDocument document, PdfObject annotation) =>
        Resolve(document, annotation) is PdfDictionary dictionary
        && dictionary.TryGetValue(SubtypeName, out PdfObject subtype)
        && Resolve(document, subtype) is PdfName name && name.ValueAsLatin1() == "Highlight";

    private static byte[] WithAnnotations(PdfDocument document, PdfIndirectReference reference, PdfDictionary page,
        List<PdfObject> keep)
    {
        IEnumerable<KeyValuePair<PdfName, PdfObject>> entries = page.Where(entry => !entry.Key.Equals(AnnotsName));
        if (keep.Count > 0)
            entries = entries.Append(new KeyValuePair<PdfName, PdfObject>(AnnotsName, new PdfArray(keep)));
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries)).Build();
    }

    // The page dictionary at a document-order index, found by walking the page tree from the catalog.
    private static (PdfIndirectReference Reference, PdfDictionary Page) Page(PdfDocument document, int pageIndex)
    {
        if (!document.CrossReferences.TryGetTrailerValue(RootName, out PdfObject root)
            || Resolve(document, root) is not PdfDictionary catalog
            || !catalog.TryGetValue(PagesName, out PdfObject pages))
            throw new InvalidDataException("The PDF has no page tree.");
        int remaining = pageIndex, visited = 0;
        var active = new HashSet<int>();
        (PdfIndirectReference, PdfDictionary)? found = null;
        bool Visit(PdfObject node, int depth)
        {
            if (depth > 256 || ++visited > 1_000_000 || node is not PdfIndirectReference reference
                || !active.Add(reference.ObjectNumber) || document.Resolve(reference) is not PdfDictionary dictionary)
                throw new InvalidDataException("The page tree is invalid.");
            try
            {
                if (dictionary.TryGetValue(KidsName, out PdfObject kids))
                {
                    if (Resolve(document, kids) is not PdfArray children)
                        throw new InvalidDataException("The page tree is invalid.");
                    foreach (PdfObject child in children)
                        if (Visit(child, depth + 1)) return true;
                    return false;
                }
                if (remaining-- > 0) return false;
                found = (reference, dictionary);
                return true;
            }
            finally { active.Remove(reference.ObjectNumber); }
        }
        if (!Visit(pages, 0) || found is not { } page) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return page;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;
}
