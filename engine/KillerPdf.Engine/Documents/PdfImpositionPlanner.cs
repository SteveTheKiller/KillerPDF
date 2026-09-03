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
