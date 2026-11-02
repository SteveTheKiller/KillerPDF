using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// R4-003: the Measurements report's Save CSV and Save JSON write a file, which is a data export, so
// the gate answers before the save dialog opens. Reading the report on screen and Copy stay free.
public sealed class GatedReportSaveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-report-save-" + Guid.NewGuid().ToString("N"));

    public GatedReportSaveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void ARefusedExportAsksForNoPathAndWritesNothing()
    {
        string path = Path.Combine(_folder, "measurements.csv");
        bool asked = false;

        string? saved = GatedReportSave.Save(() => false, () => { asked = true; return path; }, () => "Distance,12");

        Assert.Null(saved);
        Assert.False(asked);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AnAllowedExportWritesTheReportWhereTheUserChose()
    {
        string path = Path.Combine(_folder, "measurements.json");

        string? saved = GatedReportSave.Save(() => true, () => path, () => "[]");

        Assert.Equal(path, saved);
        Assert.Equal("[]", File.ReadAllText(path));
    }

    [Fact]
    public void ACanceledSaveDialogWritesNothing() =>
        Assert.Null(GatedReportSave.Save(() => true, () => null, () => throw new InvalidOperationException("Nothing to write.")));
}
