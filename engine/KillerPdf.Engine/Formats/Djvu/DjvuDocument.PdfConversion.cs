using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Converts supported text-free bitonal pages and local bookmarks to a separate PDF.</summary>
    public byte[] ConvertToPdf(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var navigation = ReadNavigation(cancellationToken);
        if (Pages.Count == 0)
            throw new NotSupportedException("DjVu PDF conversion requires supported native pages.");
        var builder = new PdfDocumentBuilder();
        for (int pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
        {
            if (ReadText(pageIndex, cancellationToken).Length != 0)
                throw new NotSupportedException("DjVu PDF conversion does not yet preserve text.");
            var frame = RenderPage(pageIndex, cancellationToken);
            var form = _pageForms[pageIndex];
            int budget = 65_535;
            var chunks = ReadChunks(_data, form.Offset + 4, form.Offset + form.Length,
                cancellationToken, ref budget);
            if (!chunks.Any(chunk => chunk.Id == 0x536d6d72))
                throw new NotSupportedException("DjVu PDF conversion currently supports only bitonal Smmr pages.");
            var samples = new byte[frame.Width * frame.Height];
            for (int y = 0; y < frame.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < frame.Width; x++)
                {
                    int index = y * frame.Width + x;
                    samples[index] = frame.Pixels[index * 4] == 0 ? (byte)0 : (byte)255;
                }
            }
            double width = frame.Width * 72d / Pages[pageIndex].Dpi;
            double height = frame.Height * 72d / Pages[pageIndex].Dpi;
            var image = PdfImage.FromBitonal(frame.Width, frame.Height, samples);
            builder.AddPage(width, height, new PdfContentStreamBuilder().DrawImage(image, 0, 0, width, height));
        }
        AddBookmarks(navigation, 0);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = builder.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        void AddBookmarks(IReadOnlyList<DjvuNavigationEntry> entries, int level)
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int destination = ResolveNavigationTarget(entry.Target, cancellationToken: cancellationToken);
                builder.AddBookmark(entry.Label, destination, level);
                AddBookmarks(entry.Children, level + 1);
            }
        }
    }
}
