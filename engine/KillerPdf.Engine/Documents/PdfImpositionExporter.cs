using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates imposed sheet PDFs from planned source-page sequences.</summary>
public static class PdfImpositionExporter
{
    /// <summary>
    /// Writes overlapping regions of one source page as simplex poster sheets at full size.
    /// Source annotations are not copied.
    /// </summary>
    public static byte[] BuildPoster(
        PdfDocument source, int sourcePageIndex,
        double sheetWidth, double sheetHeight, double margin = 0, double overlap = 0,
        bool includeCropMarks = false, bool includeRegistrationMarks = false,
        bool includeColorBars = false, bool includePageInformation = false,
        PdfImpositionSourceBox sourceBox = PdfImpositionSourceBox.Crop)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(sheetWidth) || sheetWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheetWidth));
        if (!double.IsFinite(sheetHeight) || sheetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheetHeight));
        if (!double.IsFinite(margin) || margin < 0
            || margin * 2 >= sheetWidth || margin * 2 >= sheetHeight)
            throw new ArgumentOutOfRangeException(nameof(margin));
        if (!Enum.IsDefined(sourceBox))
            throw new ArgumentOutOfRangeException(nameof(sourceBox));
        IReadOnlyList<PdfPageBoxInformation> pages = PdfPageBoxInformation.Read(source);
        if (sourcePageIndex < 0 || sourcePageIndex >= pages.Count)
            throw new ArgumentOutOfRangeException(nameof(sourcePageIndex));
        PdfPageBoxBounds selected = sourceBox switch
        {
            PdfImpositionSourceBox.Crop => pages[sourcePageIndex].CropBox,
            PdfImpositionSourceBox.Bleed => pages[sourcePageIndex].BleedBox,
            PdfImpositionSourceBox.Trim => pages[sourcePageIndex].TrimBox,
            PdfImpositionSourceBox.Art => pages[sourcePageIndex].ArtBox,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceBox))
        };
        double tileWidth = sheetWidth - margin * 2;
        double tileHeight = sheetHeight - margin * 2;
        IReadOnlyList<PdfPosterTile> tiles = PdfImpositionPlanner.PlanPosterTiles(
            selected.Right - selected.Left, selected.Top - selected.Bottom,
            tileWidth, tileHeight, overlap);
        var builder = new PdfDocumentBuilder().SetViewerPreferences(
            new PdfViewerPreferences { Duplex = PdfDuplexMode.Simplex });
        foreach (PdfPosterTile _ in tiles) builder.AddBlankPage(sheetWidth, sheetHeight);
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(builder.Build()));
        for (int outputPage = 0; outputPage < tiles.Count; outputPage++)
        {
            PdfPosterTile tile = tiles[outputPage];
            PdfContentBounds relative = tile.SourceBounds;
            var absolute = new PdfContentBounds(
                relative.Left + selected.Left, relative.Bottom + selected.Bottom,
                relative.Right + selected.Left, relative.Top + selected.Bottom);
            editor.AppendImportedPageContent(outputPage, source, sourcePageIndex,
                1, 0, 0, 1, margin - absolute.Left, margin - absolute.Bottom, absolute);
            AppendPosterMarks(editor, outputPage, tile, sheetWidth, sheetHeight,
                margin, includeCropMarks, includeRegistrationMarks,
                includeColorBars, includePageInformation);
        }
        return editor.Build();
    }

    /// <summary>
    /// Writes one output page per planned sheet side, preserving source page content and resources.
    /// Source annotations are not copied.
    /// </summary>
    public static byte[] Build(
        PdfDocument source, IReadOnlyList<PdfImposedSheetSide> sides,
        int columns, int rows, double sheetWidth, double sheetHeight,
        double margin = 0, double gutter = 0, bool rotateToFit = true,
        double creepPerSheet = 0, bool includeCropMarks = false,
        bool includeRegistrationMarks = false, bool includeFoldMarks = false,
        bool includeColorBars = false, bool includePageInformation = false,
        PdfImpositionSourceBox sourceBox = PdfImpositionSourceBox.Crop,
        PdfImpositionBindingEdge bindingEdge = PdfImpositionBindingEdge.Long)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sides);
        if (sides.Count == 0)
            throw new ArgumentException(
                "At least one imposed sheet side is required.", nameof(sides));
        if (!Enum.IsDefined(sourceBox))
            throw new ArgumentOutOfRangeException(nameof(sourceBox));
        if (!Enum.IsDefined(bindingEdge))
            throw new ArgumentOutOfRangeException(nameof(bindingEdge));

        IReadOnlyList<PdfPageBoxInformation> pageBoxes =
            PdfPageBoxInformation.Read(source);
        PdfContentBounds[] sourceBounds = [.. pageBoxes.Select(page =>
        {
            PdfPageBoxBounds box = sourceBox switch
            {
                PdfImpositionSourceBox.Crop => page.CropBox,
                PdfImpositionSourceBox.Bleed => page.BleedBox,
                PdfImpositionSourceBox.Trim => page.TrimBox,
                PdfImpositionSourceBox.Art => page.ArtBox,
                _ => throw new ArgumentOutOfRangeException(nameof(sourceBox))
            };
            return new PdfContentBounds(box.Left, box.Bottom, box.Right, box.Top);
        })];
        PdfPageBox importedBox = sourceBox switch
        {
            PdfImpositionSourceBox.Crop => PdfPageBox.Crop,
            PdfImpositionSourceBox.Bleed => PdfPageBox.Bleed,
            PdfImpositionSourceBox.Trim => PdfPageBox.Trim,
            PdfImpositionSourceBox.Art => PdfPageBox.Art,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceBox))
        };

        bool duplex = sides.Any(side => side.Face == PdfImposedSheetFace.Back);
        var builder = new PdfDocumentBuilder().SetViewerPreferences(new PdfViewerPreferences
        {
            Duplex = duplex
                ? bindingEdge == PdfImpositionBindingEdge.Long
                    ? PdfDuplexMode.DuplexFlipLongEdge
                    : PdfDuplexMode.DuplexFlipShortEdge
                : PdfDuplexMode.Simplex
        });
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
                PdfContentBounds sourcePageBounds = sourceBounds[placement.SourcePageIndex];
                PdfContentBounds target = placement.SheetBounds;
                double scale = placement.Scale;
                if (placement.Rotation == 0)
                {
                    editor.AppendImportedPageContent(outputPage, source,
                        placement.SourcePageIndex,
                        scale, 0, 0, scale,
                        target.Left - sourcePageBounds.Left * scale,
                        target.Bottom - sourcePageBounds.Bottom * scale, importedBox);
                }
                else
                {
                    editor.AppendImportedPageContent(outputPage, source,
                        placement.SourcePageIndex,
                        0, scale, -scale, 0,
                        target.Left + sourcePageBounds.Top * scale,
                        target.Bottom - sourcePageBounds.Left * scale, importedBox);
                }
            }
            if (includeCropMarks || includeRegistrationMarks || includeFoldMarks
                || includeColorBars || includePageInformation)
            {
                var marks = new PdfContentStreamBuilder().SetStrokeGray(0).SetLineWidth(0.25);
                if (includeCropMarks)
                    foreach (PdfImposedPlacement placement in placements)
                        foreach (PdfImpositionMark mark
                            in PdfImpositionPlanner.PlanCropMarks(placement))
                            marks.MoveTo(mark.StartX, mark.StartY)
                                .LineTo(mark.EndX, mark.EndY).Stroke();
                if (includeRegistrationMarks)
                    foreach (PdfImpositionRegistrationMark mark
                        in PdfImpositionPlanner.PlanRegistrationMarks(sheetWidth, sheetHeight))
                    {
                        double half = mark.CrosshairLength / 2;
                        marks.MoveTo(mark.CenterX - half, mark.CenterY)
                            .LineTo(mark.CenterX + half, mark.CenterY).Stroke()
                            .MoveTo(mark.CenterX, mark.CenterY - half)
                            .LineTo(mark.CenterX, mark.CenterY + half).Stroke()
                            .Circle(mark.CenterX, mark.CenterY, mark.Radius).Stroke();
                    }
                if (includeFoldMarks)
                {
                    const double length = 6;
                    double cellWidth = (sheetWidth - margin * 2
                        - gutter * (columns - 1)) / columns;
                    double cellHeight = (sheetHeight - margin * 2
                        - gutter * (rows - 1)) / rows;
                    for (int column = 1; column < columns; column++)
                    {
                        double x = margin + column * cellWidth
                            + (column - 0.5) * gutter;
                        marks.MoveTo(x, 0).LineTo(x, length).Stroke()
                            .MoveTo(x, sheetHeight - length)
                            .LineTo(x, sheetHeight).Stroke();
                    }
                    for (int row = 1; row < rows; row++)
                    {
                        double y = margin + row * cellHeight + (row - 0.5) * gutter;
                        marks.MoveTo(0, y).LineTo(length, y).Stroke()
                            .MoveTo(sheetWidth - length, y)
                            .LineTo(sheetWidth, y).Stroke();
                    }
                }
                if (includeColorBars)
                {
                    const double size = 6;
                    double x = Math.Max(2, margin);
                    foreach ((double C, double M, double Y, double K) color in new[]
                    {
                        (1d, 0d, 0d, 0d), (0d, 1d, 0d, 0d),
                        (0d, 0d, 1d, 0d), (0d, 0d, 0d, 1d)
                    })
                    {
                        marks.SetFillCmyk(color.C, color.M, color.Y, color.K)
                            .Rectangle(x, 2, size, size).Fill();
                        x += size;
                    }
                    marks.SetStrokeGray(0);
                }
                if (includePageInformation)
                {
                    string face = sides[outputPage].Face == PdfImposedSheetFace.Front
                        ? "front" : "back";
                    marks.SetFillGray(0).BeginText()
                        .SetFont(PdfStandardFont.Helvetica, 6)
                        .MoveText(Math.Max(2, margin), sheetHeight - 8)
                        .ShowLatin1Text($"Sheet {sides[outputPage].SheetIndex + 1} {face}")
                        .EndText();
                }
                editor.AppendPageArtifact(outputPage, sheetWidth, sheetHeight, marks);
            }
        }
        return editor.Build();
    }

    private static void AppendPosterMarks(
        PdfIncrementalPageEditor editor, int outputPage, PdfPosterTile tile,
        double sheetWidth, double sheetHeight, double margin,
        bool includeCropMarks, bool includeRegistrationMarks,
        bool includeColorBars, bool includePageInformation)
    {
        if (!includeCropMarks && !includeRegistrationMarks
            && !includeColorBars && !includePageInformation)
            return;
        var marks = new PdfContentStreamBuilder().SetStrokeGray(0).SetLineWidth(0.25);
        if (includeCropMarks)
        {
            var placement = new PdfImposedPlacement(0, 0,
                new PdfContentBounds(margin, margin,
                    margin + tile.SourceBounds.Right - tile.SourceBounds.Left,
                    margin + tile.SourceBounds.Top - tile.SourceBounds.Bottom), 1, 0);
            foreach (PdfImpositionMark mark in PdfImpositionPlanner.PlanCropMarks(placement))
                marks.MoveTo(mark.StartX, mark.StartY)
                    .LineTo(mark.EndX, mark.EndY).Stroke();
        }
        if (includeRegistrationMarks)
            foreach (PdfImpositionRegistrationMark mark
                in PdfImpositionPlanner.PlanRegistrationMarks(sheetWidth, sheetHeight))
            {
                double half = mark.CrosshairLength / 2;
                marks.MoveTo(mark.CenterX - half, mark.CenterY)
                    .LineTo(mark.CenterX + half, mark.CenterY).Stroke()
                    .MoveTo(mark.CenterX, mark.CenterY - half)
                    .LineTo(mark.CenterX, mark.CenterY + half).Stroke()
                    .Circle(mark.CenterX, mark.CenterY, mark.Radius).Stroke();
            }
        if (includeColorBars)
        {
            const double size = 6;
            double x = Math.Max(2, margin);
            foreach ((double C, double M, double Y, double K) color in new[]
            {
                (1d, 0d, 0d, 0d), (0d, 1d, 0d, 0d),
                (0d, 0d, 1d, 0d), (0d, 0d, 0d, 1d)
            })
            {
                marks.SetFillCmyk(color.C, color.M, color.Y, color.K)
                    .Rectangle(x, 2, size, size).Fill();
                x += size;
            }
            marks.SetStrokeGray(0);
        }
        if (includePageInformation)
            marks.SetFillGray(0).BeginText()
                .SetFont(PdfStandardFont.Helvetica, 6)
                .MoveText(Math.Max(2, margin), sheetHeight - 8)
                .ShowLatin1Text(
                    $"Tile {tile.TileIndex + 1} row {tile.Row + 1} column {tile.Column + 1}")
                .EndText();
        editor.AppendPageArtifact(outputPage, sheetWidth, sheetHeight, marks);
    }
}
