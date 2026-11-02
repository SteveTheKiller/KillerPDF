using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfPrintVisibilityTests
{
    [Fact]
    public void InteractiveWidgetsExcludeHiddenAndPrintOnlyFieldsButRetainInspectionData()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100)
            .AddTextField(0, "visible", 10, 10, 80, 10, "Visible")
            .AddTextField(0, "hidden", 10, 30, 80, 10, "Hidden", 10,
                new PdfTextFieldOptions { Visibility = PdfFormFieldVisibility.Hidden })
            .AddTextField(0, "print.only", 10, 50, 80, 10, "Print", 10,
                new PdfTextFieldOptions { Visibility = PdfFormFieldVisibility.HiddenButPrintable })
            .AddTextField(0, "screen.only", 10, 70, 80, 10, "Screen", 10,
                new PdfTextFieldOptions { Visibility = PdfFormFieldVisibility.VisibleButDoesNotPrint })
            .Build());

        Assert.Equal(4, PdfEngineIntegration.ReadPageFormWidgets(document, 0).Count);
        Assert.Equal(new[] { "visible", "screen.only" },
            PdfEngineIntegration.ReadPageFormWidgets(document, 0, forDisplay: true)
                .Select(widget => widget.FieldName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrintOnlyFieldAppearsInPrintSessionButNotScreenSession(bool fitted)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-print-visibility-{Guid.NewGuid():N}.pdf");
        try
        {
            byte[] original = new PdfDocumentBuilder().AddBlankPage(100, 100)
                .AddTextField(0, "print.only", 10, 40, 80, 20, "PRINT ONLY", 10,
                    new PdfTextFieldOptions { Visibility = PdfFormFieldVisibility.HiddenButPrintable })
                .Build();
            File.WriteAllBytes(path, original);

            PdfRenderedPage Render(bool printing)
            {
                using var session = fitted
                    ? PdfPageRenderSession.OpenEngineFirst(path, 100, 100, null, forPrinting: printing)
                    : PdfPageRenderSession.OpenEngineFirst(path, 1.0, null, forPrinting: printing);
                return session.RenderPage(0);
            }
            PdfRenderedPage screen = Render(false);
            PdfRenderedPage print = Render(true);
            Assert.All(screen.Pixels, value => Assert.Equal((byte)255, value));
            Assert.Contains(print.Pixels, value => value < 128);
            Assert.Equal(screen.Width, print.Width);
            Assert.Equal(screen.Height, print.Height);
            Assert.Equal(screen.Pixels, Render(false).Pixels);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
