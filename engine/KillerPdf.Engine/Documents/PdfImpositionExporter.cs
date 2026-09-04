using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates imposed sheet PDFs from planned source-page sequences.</summary>
public static class PdfImpositionExporter
{
    /// <summary>
    /// Writes one output page per planned sheet side, preserving source page content and resources.
    /// Source annotations are not copied.
    /// </summary>
    public static byte[] Build(
        PdfDocument source, IReadOnlyList<PdfImposedSheetSide> sides,
        int columns, int rows, double sheetWidth, double sheetHeight,
        double margin = 0, double gutter = 0, bool rotateToFit = true,
        double creepPerSheet = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sides);
        if (sides.Count == 0)
            throw new ArgumentException(
                "At least one imposed sheet side is required.", nameof(sides));

        IReadOnlyList<PdfPageBoxInformation> pageBoxes =
            PdfPageBoxInformation.Read(source);
        PdfContentBounds[] sourceBounds = [.. pageBoxes.Select(page =>
            new PdfContentBounds(page.CropBox.Left, page.CropBox.Bottom,
                page.CropBox.Right, page.CropBox.Top))];

        var builder = new PdfDocumentBuilder();
        foreach (PdfImposedSheetSide _ in sides)
            builder.AddBlankPage(sheetWidth, sheetHeight);
        byte[] seed = builder.Build();
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(seed));

        for (int outputPage = 0; outputPage < sides.Count; outputPage++)
        {
            IReadOnlyList<PdfImposedPlacement> placements =
                PdfImpositionPlanner.PlaceOnSheet(sides[outputPage], columns, rows,
                    sheetWidth, sheetHeight, sourceBounds, margin, gutter, rotateToFit);
            if (creepPerSheet != 0)
                placements = PdfImpositionPlanner.ApplyCreep(
                    sides[outputPage], placements, columns, creepPerSheet);
            foreach (PdfImposedPlacement placement in placements)
            {
                PdfContentBounds sourceBox = sourceBounds[placement.SourcePageIndex];
                PdfContentBounds target = placement.SheetBounds;
                double scale = placement.Scale;
                if (placement.Rotation == 0)
                {
                    editor.AppendImportedPageContent(outputPage, source,
                        placement.SourcePageIndex,
                        scale, 0, 0, scale,
                        target.Left - sourceBox.Left * scale,
                        target.Bottom - sourceBox.Bottom * scale);
                }
                else
                {
                    editor.AppendImportedPageContent(outputPage, source,
                        placement.SourcePageIndex,
                        0, scale, -scale, 0,
                        target.Left + sourceBox.Top * scale,
                        target.Bottom - sourceBox.Left * scale);
                }
            }
        }
        return editor.Build();
    }
}
