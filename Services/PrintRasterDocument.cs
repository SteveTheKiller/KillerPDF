using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

/// <summary>Preflights all print rasters on disk and retains at most two decoded pages.</summary>
internal sealed class PrintRasterDocument : DocumentPaginator, IDisposable
{
    private readonly ProcessTempFiles files = new(Path.Combine(Path.GetTempPath(), "KillerPDF-print"));
    private readonly List<(string Path, int Width, int Height)> pages = [];
    private readonly LinkedList<(int Index, BitmapSource Bitmap)> cache = [];
    private readonly int copies;
    private bool disposed;

    internal PrintRasterDocument(int copies)
    {
        this.copies = copies;
        files.CleanupStale();
    }

    internal void Add(BitmapSource bitmap)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string path = files.CreatePath("raster");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) encoder.Save(output);
        pages.Add((path, bitmap.PixelWidth, bitmap.PixelHeight));
    }

    internal bool Landscape => pages.Count(page => page.Width > page.Height) * 2 > pages.Count;
    public override bool IsPageCountValid => true;
    public override int PageCount => checked(pages.Count * copies);
    public override Size PageSize { get; set; }
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pageNumber < 0 || pageNumber >= PageCount) return DocumentPage.Missing;
        int index = pageNumber % pages.Count;
        var node = cache.First;
        while (node is not null && node.Value.Index != index) node = node.Next;
        BitmapSource bitmap;
        if (node is not null)
        {
            bitmap = node.Value.Bitmap;
            cache.Remove(node);
        }
        else
        {
            if (cache.Count == 2) cache.RemoveLast();
            // OnLoad closes the file before returning, allowing cleanup after a cancelled job.
            // A unique URI also distinguishes images in the XPS serializer's resource cache.
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(pages[index].Path, UriKind.Absolute);
            image.EndInit();
            bitmap = image;
            bitmap.Freeze();
        }
        cache.AddFirst((index, bitmap));
        // Every page fits its sheet, turning when its orientation differs from the sheet's (#482).
        var placement = PrintPagePlacement.Place(bitmap.PixelWidth, bitmap.PixelHeight,
            PageSize.Width, PageSize.Height, PrintPagePlacement.FitToPage, 100, autoRotate: true);
        BitmapSource drawn = placement.Rotated ? PrintPagePlacement.Turn(bitmap) : bitmap;
        var size = new Size(placement.Width, placement.Height);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
            drawing.DrawImage(drawn, new Rect(new Point((PageSize.Width - size.Width) / 2,
                (PageSize.Height - size.Height) / 2), size));
        // The serializer wraps this visual in a FixedPage. Returning a FixedPage would nest it.
        return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cache.Clear();
        files.Dispose();
    }
}
