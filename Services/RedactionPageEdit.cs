using KillerPDF.Controls;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using System.IO;
using System.Windows.Media;

namespace KillerPDF.Services;

internal static class RedactionPageEdit
{
    internal static RedactionMarkSnapshot Apply(string workingPath, RedactionMarkSnapshot marks, Action<string> edit)
    {
        // Carry pending marks through the same page objects as the content being edited.
        PdfEngineIntegration.WriteRedactionMarks(workingPath, marks.Marks.Select(mark =>
            new PdfEngineIntegration.RedactionMarkData(mark.PageIndex, mark.Left, mark.Bottom, mark.Right, mark.Top,
                mark.OverlayText, mark.Fill is Color color
                    ? new PdfRgbColor(color.R / 255d, color.G / 255d, color.B / 255d) : null,
                mark.AnnotationFlags)).ToArray());
        edit(workingPath);
        PdfDocument output = PdfEngineIntegration.OpenForReading(File.ReadAllBytes(workingPath));
        var mapped = new List<RedactionMark>();
        foreach (var mark in PdfEngineIntegration.ReadRedactionMarks(output))
        {
            // Dividing a spread crops copies of its content. Keep the full extent so applying
            // either copy's mark also removes selected content hidden beyond its crop box.
            mapped.Add(new(mark.PageIndex, mark.Left, mark.Bottom, mark.Right, mark.Top, mark.OverlayText,
                mark.FillColor is { } fill ? Color.FromRgb(Component(fill.Red), Component(fill.Green), Component(fill.Blue)) : null,
                mark.AnnotationFlags));
        }
        return new(mapped, foundText: marks.FoundText);
    }

    private static byte Component(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
