using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// When the native annotation path refuses a page, the save draws the markup into the pages so it still succeeds.
// The markup can no longer be edited after that, so the save says so instead of leaving it to be found on reopening.
public sealed class BurnFlattenedMarkupTests
{
    [Fact]
    public void ASaveThatFallsBackToPageContentReportsThatTheMarkupWasFlattened()
    {
        // The page's annotation list holds an annotation written directly in the array, which the native
        // annotation editor refuses to extend.
        string path = Path.Combine(Path.GetTempPath(), $"kp-flattened-markup-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Build(
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                    + "/Annots [<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] >>] >>"
            ]));
            PdfEngineBurn.FlattenedMarkup = false;
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation
                {
                    PageIndex = 0, Position = new Point(10, 10), Width = 120, Height = 30,
                    Content = "Flattened", FontName = "Segoe UI", FontSize = 12
                }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 200) });

            Assert.True(PdfEngineBurn.FlattenedMarkup, "The save drew the markup into the page without saying so.");
            Assert.DoesNotContain("/FreeText", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ASaveThatKeepsTheMarkupEditableReportsNothing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-flattened-markup-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Build(
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"
            ]));
            PdfEngineBurn.FlattenedMarkup = true;
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation
                {
                    PageIndex = 0, Position = new Point(10, 10), Width = 120, Height = 30,
                    Content = "Editable", FontName = "Segoe UI", FontSize = 12
                }]
            }, new Dictionary<int, (int w, int h)> { [0] = (200, 200) });

            Assert.False(PdfEngineBurn.FlattenedMarkup);
            Assert.Contains("/FreeText", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Only the refusals the two native paths throw take the fallback. A missing argument or a disposed object is a
    // fault in the save itself; flattening every page's markup over it would hide the fault and cost the markup.
    [Fact]
    public void OnlyTheNativeRefusalsTakeTheFlatteningFallback()
    {
        Assert.True(PdfEngineBurn.EditingBatchRefused(new ArgumentException("Invalid overlay batch size.")));
        Assert.True(PdfEngineBurn.NativeAnnotationRefused(
            new InvalidOperationException("Page 1 /Annots contains a direct annotation entry.")));

        Assert.False(PdfEngineBurn.EditingBatchRefused(new ArgumentNullException("document")));
        Assert.False(PdfEngineBurn.EditingBatchRefused(new NullReferenceException()));
        Assert.False(PdfEngineBurn.NativeAnnotationRefused(new ObjectDisposedException("document")));
        Assert.False(PdfEngineBurn.NativeAnnotationRefused(new NullReferenceException()));
    }

    // SOURCE PIN: the main window is not compiled into this project. Save and Save As read the flag after their burn
    // and the shared save tail adds the note once.
    [Fact]
    public void SaveAndSaveAsAddTheFlattenedNoteToTheSavedStatus()
    {
        string files = ShellSource.Read("Shell", "FileOperations.cs");
        Assert.Equal(2, CountOf(files, "_lastSaveFlattenedMarkup = PdfEngineBurn.FlattenedMarkup;"));
        string tail = ShellSource.Between(files, "private void ReportSaved(", "private string WithFieldsThatKeptTheirValue(");
        Assert.Contains("if (_lastSaveFlattenedMarkup) status += \" \" + Loc(\"Str_Save_MarkupFlattened\");", tail);
    }

    // SOURCE PIN: both fallbacks ask the two predicates above, so a bare catch type cannot come back unnoticed.
    [Fact]
    public void BothFallbacksAreFilteredByTheRefusalPredicates()
    {
        string burn = ShellSource.Read("Services", "PdfEngineBurn.cs");
        Assert.Equal(1, CountOf(burn, "catch (Exception error) when (EditingBatchRefused(error))"));
        Assert.Equal(1, CountOf(burn, "catch (Exception error) when (NativeAnnotationRefused(error))"));
        Assert.Equal(2, CountOf(burn, "FlattenedMarkup = true;"));
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
            at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    internal static byte[] Build(string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
