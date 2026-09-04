using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>The displayed direction in which a scanned spread is divided.</summary>
public enum PdfSpreadDivisionDirection
{
    /// <summary>Creates the displayed left page followed by the displayed right page.</summary>
    Vertical,
    /// <summary>Creates the displayed top page followed by the displayed bottom page.</summary>
    Horizontal
}

/// <summary>One reviewed spread division expressed in displayed page coordinates.</summary>
public sealed record PdfSpreadDivisionRequest(
    int PageIndex, PdfSpreadDivisionDirection Direction, double Position = 0.5);

/// <summary>Maps one source spread to its two final output pages.</summary>
public sealed record PdfSpreadDivisionMapping(
    int SourcePageIndex, int FirstOutputPageIndex, int SecondOutputPageIndex);

/// <summary>The source-preserving result of dividing selected scanned spreads.</summary>
public sealed record PdfSpreadDivisionResult(
    ReadOnlyMemory<byte> Document, IReadOnlyList<PdfSpreadDivisionMapping> Mappings);

/// <summary>Divides selected scanned spreads into ordered page pairs.</summary>
public static class PdfSpreadDivider
{
    /// <summary>
    /// Divides each selected page at a displayed position from the left or top edge.
    /// Unselected pages retain their order and each selected page is followed by its second half.
    /// </summary>
    public static PdfSpreadDivisionResult Divide(
        PdfDocument document, IEnumerable<PdfSpreadDivisionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(requests);
        PdfSpreadDivisionRequest[] values = requests.Select(request => request
            ?? throw new ArgumentException(
                "A spread division request cannot be null.", nameof(requests))).ToArray();
        if (values.Length == 0)
            throw new ArgumentException(
                "At least one spread division request is required.", nameof(requests));

        IReadOnlyList<PdfPageBoxInformation> boxes = PdfPageBoxInformation.Read(document);
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var seenPages = new HashSet<int>();
        foreach (PdfSpreadDivisionRequest request in values)
        {
            if (request.PageIndex < 0 || request.PageIndex >= pages.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(requests), "A spread division page index is outside the document.");
            if (!seenPages.Add(request.PageIndex))
                throw new ArgumentException(
                    "A page can be divided only once per operation.", nameof(requests));
            if (!Enum.IsDefined(request.Direction))
                throw new ArgumentOutOfRangeException(
                    nameof(requests), "A spread division direction is not defined.");
            if (!double.IsFinite(request.Position)
                || request.Position <= 0 || request.Position >= 1)
                throw new ArgumentOutOfRangeException(
                    nameof(requests), "A spread division position must be between zero and one.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var editor = new PdfIncrementalPageEditor(document);
        foreach (PdfSpreadDivisionRequest request in values
                     .OrderByDescending(request => request.PageIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageBoxBounds crop = boxes[request.PageIndex].CropBox;
            int rotation = pages[request.PageIndex].Rotation;
            (PdfPageBoxBounds first, PdfPageBoxBounds second) = DivideBox(
                crop, rotation, request.Direction, request.Position);

            editor.InsertImportedPage(request.PageIndex + 1, document, request.PageIndex);
            SetOutputBox(editor, request.PageIndex, first);
            SetOutputBox(editor, request.PageIndex + 1, second);
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = editor.Build();
        PdfSpreadDivisionMapping[] mappings = values
            .OrderBy(request => request.PageIndex)
            .Select((request, selectedIndex) => new PdfSpreadDivisionMapping(
                request.PageIndex, request.PageIndex + selectedIndex,
                request.PageIndex + selectedIndex + 1))
            .ToArray();
        return new PdfSpreadDivisionResult(output, Array.AsReadOnly(mappings));
    }

    private static void SetOutputBox(
        PdfIncrementalPageEditor editor, int pageIndex, PdfPageBoxBounds box)
    {
        editor.SetMediaBox(pageIndex, box.Left, box.Bottom, box.Width, box.Height)
            .SetCropBox(pageIndex, box.Left, box.Bottom, box.Width, box.Height)
            .ClearPageBox(pageIndex, PdfPageBox.Bleed)
            .ClearPageBox(pageIndex, PdfPageBox.Trim)
            .ClearPageBox(pageIndex, PdfPageBox.Art);
    }

    private static (PdfPageBoxBounds First, PdfPageBoxBounds Second) DivideBox(
        PdfPageBoxBounds box, int rotation, PdfSpreadDivisionDirection direction,
        double position)
    {
        bool splitX = direction == PdfSpreadDivisionDirection.Vertical
            ? rotation is 0 or 180
            : rotation is 90 or 270;
        bool reverse = direction == PdfSpreadDivisionDirection.Vertical
            ? rotation is 180 or 270
            : rotation is 0 or 270;
        if (splitX)
        {
            double split = reverse
                ? box.Right - box.Width * position
                : box.Left + box.Width * position;
            var low = new PdfPageBoxBounds(box.Left, box.Bottom, split, box.Top);
            var high = new PdfPageBoxBounds(split, box.Bottom, box.Right, box.Top);
            return reverse ? (high, low) : (low, high);
        }
        else
        {
            double split = reverse
                ? box.Top - box.Height * position
                : box.Bottom + box.Height * position;
            var low = new PdfPageBoxBounds(box.Left, box.Bottom, box.Right, split);
            var high = new PdfPageBoxBounds(box.Left, split, box.Right, box.Top);
            return reverse ? (high, low) : (low, high);
        }
    }
}
