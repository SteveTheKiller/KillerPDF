namespace KillerPdf.Engine.Documents;

/// <summary>Calculates deterministic source-page placement for imposed print sheets.</summary>
public static class PdfImpositionPlanner
{
    /// <summary>Plans sequential N-up sheets, inserting blank slots at the end.</summary>
    public static IReadOnlyList<PdfImposedSheetSide> PlanNUp(int pageCount, int columns, int rows,
        bool duplex = false)
    {
        if (pageCount < 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        int slotsPerSide = checked(columns * rows);
        int sideCount = pageCount == 0 ? 0 : (pageCount + slotsPerSide - 1) / slotsPerSide;
        var result = new List<PdfImposedSheetSide>(sideCount);
        for (int sideIndex = 0; sideIndex < sideCount; sideIndex++)
        {
            int sheetIndex = duplex ? sideIndex / 2 : sideIndex;
            PdfImposedSheetFace face = duplex && sideIndex % 2 == 1
                ? PdfImposedSheetFace.Back : PdfImposedSheetFace.Front;
            var slots = new int?[slotsPerSide];
            for (int slot = 0; slot < slots.Length; slot++)
            {
                int sourcePage = sideIndex * slotsPerSide + slot;
                slots[slot] = sourcePage < pageCount ? sourcePage : null;
            }
            result.Add(new PdfImposedSheetSide(sheetIndex, face, Array.AsReadOnly(slots)));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Plans repeated copies of one source page across N-up sheet sides.</summary>
    public static IReadOnlyList<PdfImposedSheetSide> PlanStepAndRepeat(
        int sourcePageIndex, int copyCount, int columns, int rows,
        bool duplex = false)
    {
        if (sourcePageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sourcePageIndex));
        if (copyCount < 0)
            throw new ArgumentOutOfRangeException(nameof(copyCount));
        IReadOnlyList<PdfImposedSheetSide> layout =
            PlanNUp(copyCount, columns, rows, duplex);
        return Array.AsReadOnly(layout.Select(side => side with
        {
            SourcePageIndices = Array.AsReadOnly(side.SourcePageIndices
                .Select(page => page.HasValue ? (int?)sourcePageIndex : null)
                .ToArray())
        }).ToArray());
    }

    /// <summary>Plans a caller-supplied page sequence with explicit blank slots.</summary>
    public static IReadOnlyList<PdfImposedSheetSide> PlanManual(
        int sourcePageCount, IReadOnlyList<int?> sequence,
        int slotsPerSide, bool duplex = false)
    {
        if (sourcePageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sourcePageCount));
        ArgumentNullException.ThrowIfNull(sequence);
        if (slotsPerSide <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotsPerSide));
        if (sequence.Any(page => page is < 0 || page >= sourcePageCount))
            throw new ArgumentOutOfRangeException(nameof(sequence),
                "A manual imposition page is outside the source document.");
        if (sequence.Count == 0) return [];
        int sideCount = (sequence.Count + slotsPerSide - 1) / slotsPerSide;
        var result = new List<PdfImposedSheetSide>(sideCount);
        for (int sideIndex = 0; sideIndex < sideCount; sideIndex++)
        {
            var slots = new int?[slotsPerSide];
            for (int slot = 0; slot < slots.Length; slot++)
            {
                int sequenceIndex = sideIndex * slotsPerSide + slot;
                if (sequenceIndex < sequence.Count)
                    slots[slot] = sequence[sequenceIndex];
            }
            result.Add(new PdfImposedSheetSide(
                duplex ? sideIndex / 2 : sideIndex,
                duplex && sideIndex % 2 == 1
                    ? PdfImposedSheetFace.Back : PdfImposedSheetFace.Front,
                Array.AsReadOnly(slots)));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Plans two-up saddle-stitched booklet signatures in left-to-right slot order.</summary>
    public static IReadOnlyList<PdfImposedSheetSide> PlanBooklet(int pageCount)
    {
        if (pageCount < 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (pageCount == 0) return [];
        int paddedCount = checked((pageCount + 3) / 4 * 4);
        var result = new List<PdfImposedSheetSide>(paddedCount / 2);
        int low = 0;
        int high = paddedCount - 1;
        for (int sheet = 0; low < high; sheet++)
        {
            result.Add(Side(sheet, PdfImposedSheetFace.Front, high--, low++));
            result.Add(Side(sheet, PdfImposedSheetFace.Back, low++, high--));
        }
        return Array.AsReadOnly(result.ToArray());

        PdfImposedSheetSide Side(int sheet, PdfImposedSheetFace face, int left, int right) =>
            new(sheet, face, Array.AsReadOnly<int?>(
                [left < pageCount ? left : null, right < pageCount ? right : null]));
    }

    /// <summary>Plans bounded saddle-stitched signatures without mixing pages between signatures.</summary>
    public static IReadOnlyList<PdfImposedSheetSide> PlanBookletSignatures(
        int pageCount, int maximumPagesPerSignature)
    {
        if (pageCount < 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (maximumPagesPerSignature <= 0 || maximumPagesPerSignature % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPagesPerSignature),
                "A booklet signature size must be a positive multiple of four pages.");
        var result = new List<PdfImposedSheetSide>();
        int sourceOffset = 0;
        int sheetOffset = 0;
        while (sourceOffset < pageCount)
        {
            int signaturePages = Math.Min(maximumPagesPerSignature, pageCount - sourceOffset);
            IReadOnlyList<PdfImposedSheetSide> signature = PlanBooklet(signaturePages);
            foreach (PdfImposedSheetSide side in signature)
                result.Add(side with
                {
                    SheetIndex = side.SheetIndex + sheetOffset,
                    SourcePageIndices = Array.AsReadOnly(side.SourcePageIndices
                        .Select(page => page.HasValue ? page + sourceOffset : null).ToArray())
                });
            sourceOffset += signaturePages;
            sheetOffset += signature.Count / 2;
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Plans overlapping poster tiles in top-to-bottom, left-to-right print order.</summary>
    public static IReadOnlyList<PdfPosterTile> PlanPosterTiles(
        double sourceWidth, double sourceHeight,
        double tileWidth, double tileHeight, double overlap = 0)
    {
        ValidateDimension(sourceWidth, nameof(sourceWidth));
        ValidateDimension(sourceHeight, nameof(sourceHeight));
        ValidateDimension(tileWidth, nameof(tileWidth));
        ValidateDimension(tileHeight, nameof(tileHeight));
        if (!double.IsFinite(overlap) || overlap < 0
            || overlap >= tileWidth || overlap >= tileHeight)
            throw new ArgumentOutOfRangeException(nameof(overlap));
        double horizontalStep = tileWidth - overlap;
        double verticalStep = tileHeight - overlap;
        int columns = TileCount(sourceWidth, tileWidth, horizontalStep);
        int rows = TileCount(sourceHeight, tileHeight, verticalStep);
        var result = new List<PdfPosterTile>(checked(columns * rows));
        for (int row = 0; row < rows; row++)
        {
            double top = sourceHeight - row * verticalStep;
            double bottom = Math.Max(0, top - tileHeight);
            for (int column = 0; column < columns; column++)
            {
                double left = column * horizontalStep;
                double right = Math.Min(sourceWidth, left + tileWidth);
                result.Add(new PdfPosterTile(result.Count, row, column,
                    new PdfContentBounds(left, bottom, right, top)));
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Fits one imposed side into a sheet grid with margins and gutters.</summary>
    public static IReadOnlyList<PdfImposedPlacement> PlaceOnSheet(
        PdfImposedSheetSide side, int columns, int rows,
        double sheetWidth, double sheetHeight,
        IReadOnlyList<PdfContentBounds> sourcePageBounds,
        double margin = 0, double gutter = 0, bool rotateToFit = true)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(sourcePageBounds);
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        ValidateDimension(sheetWidth, nameof(sheetWidth));
        ValidateDimension(sheetHeight, nameof(sheetHeight));
        if (!double.IsFinite(margin) || margin < 0)
            throw new ArgumentOutOfRangeException(nameof(margin));
        if (!double.IsFinite(gutter) || gutter < 0)
            throw new ArgumentOutOfRangeException(nameof(gutter));
        int slotCount = checked(columns * rows);
        if (side.SourcePageIndices.Count != slotCount)
            throw new ArgumentException("The sheet side must contain one source entry per grid slot.", nameof(side));
        double cellWidth = (sheetWidth - margin * 2 - gutter * (columns - 1)) / columns;
        double cellHeight = (sheetHeight - margin * 2 - gutter * (rows - 1)) / rows;
        if (cellWidth <= 0 || cellHeight <= 0)
            throw new ArgumentException("Margins and gutters leave no printable grid area.");
        var placements = new List<PdfImposedPlacement>();
        for (int slot = 0; slot < slotCount; slot++)
        {
            int? sourcePageIndex = side.SourcePageIndices[slot];
            if (!sourcePageIndex.HasValue) continue;
            if (sourcePageIndex.Value < 0 || sourcePageIndex.Value >= sourcePageBounds.Count)
                throw new ArgumentOutOfRangeException(nameof(sourcePageBounds),
                    "An imposed source page is outside the supplied page bounds.");
            PdfContentBounds source = sourcePageBounds[sourcePageIndex.Value];
            if (source.Width <= 0 || source.Height <= 0)
                throw new ArgumentException("Source page bounds must have positive dimensions.", nameof(sourcePageBounds));
            double normalScale = Math.Min(cellWidth / source.Width, cellHeight / source.Height);
            double rotatedScale = Math.Min(cellWidth / source.Height, cellHeight / source.Width);
            bool rotated = rotateToFit && rotatedScale > normalScale;
            double scale = rotated ? rotatedScale : normalScale;
            double placedWidth = (rotated ? source.Height : source.Width) * scale;
            double placedHeight = (rotated ? source.Width : source.Height) * scale;
            int row = slot / columns;
            int column = slot % columns;
            double cellLeft = margin + column * (cellWidth + gutter);
            double cellBottom = sheetHeight - margin - (row + 1) * cellHeight - row * gutter;
            double left = cellLeft + (cellWidth - placedWidth) / 2;
            double bottom = cellBottom + (cellHeight - placedHeight) / 2;
            placements.Add(new PdfImposedPlacement(slot, sourcePageIndex.Value,
                new PdfContentBounds(left, bottom, left + placedWidth, bottom + placedHeight),
                scale, rotated ? 90 : 0));
        }
        return Array.AsReadOnly(placements.ToArray());
    }

    private static int TileCount(double source, double tile, double step) =>
        source <= tile ? 1 : checked((int)Math.Ceiling((source - tile) / step) + 1);

    private static void ValidateDimension(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>One printable side of an imposed sheet.</summary>
public sealed record PdfImposedSheetSide(int SheetIndex, PdfImposedSheetFace Face,
    IReadOnlyList<int?> SourcePageIndices);

/// <summary>One source-page region assigned to a poster sheet.</summary>
public sealed record PdfPosterTile(int TileIndex, int Row, int Column, PdfContentBounds SourceBounds);

/// <summary>One fitted source page in sheet coordinates.</summary>
public sealed record PdfImposedPlacement(int SlotIndex, int SourcePageIndex,
    PdfContentBounds SheetBounds, double Scale, int Rotation);

/// <summary>The printable face of a physical sheet.</summary>
public enum PdfImposedSheetFace
{
    /// <summary>The front face.</summary>
    Front,
    /// <summary>The back face.</summary>
    Back
}
