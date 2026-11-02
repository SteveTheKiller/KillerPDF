using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Printing and image export continue past a page that cannot be drawn exactly and name it
// afterwards; the command line still refuses such a page unless --allow-incomplete is passed.
public sealed class IncompletePagePublicationTests
{
    [Fact]
    public void PrintAndExportKeepAnIncompletePageAndNameIt()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddPage(120, 80, "1 0 0 rg 4 4 60 40 re f"u8.ToArray())
                .AddPage(120, 80, "1 0 0 rg 4 4 60 40 re f q /Missing Do Q"u8.ToArray())
                .AddPage(120, 80, "BT /Helvetica 14 Tf 10 30 Td (Recovered) Tj ET"u8.ToArray())
                .Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 1.0, null);
            var log = new IncompletePageLog();
            for (int page = 0; page < 3; page++)
            {
                PdfRenderedPage rendered = session.RenderPage(page, requireComplete: false);
                Assert.Contains(rendered.Pixels, component => component < 128);
                log.Note(page, rendered);
            }
            Assert.Equal([1], log.Pages);
            Assert.Throws<InvalidOperationException>(() => session.RenderPage(1));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 0 }, "1")]
    [InlineData(new[] { 1, 4, 5, 6, 9 }, "2, 5-7, 10")]
    [InlineData(new[] { 2, 3 }, "3-4")]
    public void PagesAreNamedOneBasedWithRunsCollapsed(int[] pageIndexes, string expected)
    {
        Assert.Equal(expected, IncompletePageLog.Describe(pageIndexes));
    }

    [Fact]
    public void GuiPrintAndExportContinueAndWarnWhileTheCommandLineStaysStrict()
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!;
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts]));

        string rasterize = Read("Services", "PdfRasterize.cs");
        Assert.Equal(2, Count(rasterize, "requireComplete: incomplete is null, cancellationToken: ct"));
        Assert.Equal(2, Count(rasterize, "incomplete?.Note("));
        Assert.DoesNotContain("rendered.RequireComplete(", rasterize);

        string files = Read("Shell", "FileOperations.cs");
        Assert.Equal(2, Count(files, "WarnIncompletePages(incomplete);"));
        Assert.Contains("Loc(\"Str_Export_IncompletePages\")", files);
        Assert.Contains("renderSession.RenderPage(i, requireComplete: false)", files);
        Assert.Contains("preview.FinishLoading(incomplete.Pages)", files);

        string preview = Read("Controls", "PrintPreviewWindow.cs");
        Assert.Contains("renderSession.RenderPage(idx, requireComplete: false,", preview);
        Assert.Contains("Str_Print_IncompletePages", preview);
        // The print warning names only pages that will print and is announced to screen readers.
        Assert.Contains("_incompletePages.Where(printed.Contains)", preview);
        Assert.Contains("AutomationLiveSetting.Polite", preview);
        Assert.Contains("AutomationEvents.LiveRegionChanged", preview);
        // Typing a range announces only the settled warning, not each intermediate range.
        Assert.Equal(1, Count(preview, "AutomationEvents.LiveRegionChanged"));
        Assert.Contains("_announceWarning.Tick +=", preview);
        string label = preview[preview.IndexOf("private void UpdateRenderLabel()", StringComparison.Ordinal)..];
        label = label[..label.IndexOf("\n        }", StringComparison.Ordinal)];
        Assert.DoesNotContain("RaiseAutomationEvent", label);
        Assert.Contains("_announceWarning.Start();", label);

        foreach (string cli in new[] { "CliRunner.cs", "CliRunner.Pages.cs" })
            Assert.DoesNotContain("requireComplete: false", Read("Features", "Cli", cli));
        // to-image, flatten and print opt in only through --allow-incomplete, and name the pages.
        string runner = Read("Features", "Cli", "CliRunner.cs");
        Assert.Equal(3, Count(runner, "bool allowIncomplete = options.ContainsKey(\"--allow-incomplete\");"));
        Assert.Equal(3, Count(runner, "requireComplete: !allowIncomplete"));
        Assert.Equal(3, Count(runner, "CliReportIncomplete(incomplete, con);"));
        Assert.DoesNotContain("rendered.RequireComplete(", runner);
        // batch-render keeps the page's log row OK for gate baselines but marks and counts it.
        string batch = Read("Features", "Cli", "BatchRenderRunner.cs");
        Assert.Contains("row.Incomplete = PdfRenderCompleteness.Classify(rendered.Diagnostics)", batch);
        Assert.Contains("tally.Add(row.Status, row.Incomplete);", batch);
        Assert.Contains("BatchRenderTally.ConsoleStatus(row.Status, row.Incomplete)", batch);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        for (int index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
