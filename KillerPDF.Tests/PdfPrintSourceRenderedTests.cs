using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfPrintSourceRenderedTests
{
    [Theory]
    [InlineData(72, false)]
    [InlineData(300, false)]
    [InlineData(72, true)]
    [InlineData(300, true)]
    public void PendingPrintCopyMatchesFreshlyReopenedSavedFieldOutput(int dpi, bool sizeOnly)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-print-pixels-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] original = new PdfDocumentBuilder().AddBlankPage()
                .AddTextField(0, "customer.name", 20, 20, 140, 24, "Original")
                .AddComboBoxOptions(0, "customer.country", 20, 60, 140, 24,
                    [new PdfChoiceOption("US", "United States"), new PdfChoiceOption("CA", "Canada")], "US")
                .AddCheckBox(0, "customer.approved", 20, 100, 20, 20).Build();
            string working = Path.Combine(directory, "working.pdf");
            string pendingPrint = Path.Combine(directory, "pending-print.pdf");
            string saved = Path.Combine(directory, "saved.pdf");
            string sameSessionPrint = Path.Combine(directory, "same-session-print.pdf");
            File.WriteAllBytes(working, original);
            var edits = new PdfEngineIntegration.FormEdits(
                sizeOnly ? new Dictionary<string, string>() : new Dictionary<string, string> { ["customer.name"] = "Updated" },
                sizeOnly ? new Dictionary<string, string>() : new Dictionary<string, string> { ["customer.country"] = "CA" },
                new Dictionary<string, IReadOnlyList<string>>(),
                sizeOnly ? new Dictionary<string, bool>() : new Dictionary<string, bool> { ["customer.approved"] = true },
                new Dictionary<string, string>(), sizeOnly ? new Dictionary<string, double> { ["customer.name"] = 7.5 }
                    : new Dictionary<string, double>());

            Assert.True(PdfPrintSource.HasValues(edits));
            PdfPrintSource.Create(working, pendingPrint, PdfPrintSource.Snapshot(edits));
            File.Copy(working, saved);
            PdfEngineIntegration.ApplyFormValues(saved, edits);
            PdfPrintSource.Create(working, sameSessionPrint, PdfPrintSource.Snapshot(edits));

            PdfRenderedPage Raster(string path)
            {
                using var session = PdfPageRenderSession.OpenEngineFirst(path, dpi / 72.0, null);
                return session.RenderPage(0);
            }
            PdfRenderedPage before = Raster(working);
            PdfRenderedPage pending = Raster(pendingPrint);
            PdfRenderedPage sameSession = Raster(sameSessionPrint);
            PdfRenderedPage reopened = Raster(saved);
            Assert.Equal(reopened.Width, pending.Width);
            Assert.Equal(reopened.Height, pending.Height);
            Assert.True(pending.Pixels.AsSpan().SequenceEqual(reopened.Pixels));
            Assert.True(sameSession.Pixels.AsSpan().SequenceEqual(reopened.Pixels));
            Assert.False(before.Pixels.AsSpan().SequenceEqual(reopened.Pixels));

            double scale = dpi / 72.0;
            foreach (var (x, y, width, height) in new[] { (20, 20, 140, 24), (20, 60, 140, 24), (20, 100, 20, 20) })
            {
                if (sizeOnly && y != 20) continue;
                bool changed = false;
                for (int row = (int)Math.Ceiling((792 - y - height + 2) * scale); row < (int)Math.Floor((792 - y - 2) * scale); row++)
                for (int column = (int)Math.Ceiling((x + 2) * scale); column < (int)Math.Floor((x + width - 2) * scale); column++)
                {
                    int offset = (row * reopened.Width + column) * 4;
                    changed |= !before.Pixels.AsSpan(offset, 4).SequenceEqual(reopened.Pixels.AsSpan(offset, 4));
                }
                Assert.True(changed, $"Field interior at {x},{y} must show its edited value.");
            }
            Assert.Equal(original, File.ReadAllBytes(working));
            if (sizeOnly) Assert.Equal(7.5, edits.TextFontSizes["customer.name"]);
            else
            {
                Assert.Equal("Updated", edits.TextValues["customer.name"]);
                Assert.True(edits.CheckBoxValues["customer.approved"]);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
