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
}

/// <summary>One printable side of an imposed sheet.</summary>
public sealed record PdfImposedSheetSide(int SheetIndex, PdfImposedSheetFace Face,
    IReadOnlyList<int?> SourcePageIndices);

/// <summary>The printable face of a physical sheet.</summary>
public enum PdfImposedSheetFace
{
    /// <summary>The front face.</summary>
    Front,
    /// <summary>The back face.</summary>
    Back
}
