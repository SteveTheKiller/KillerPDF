using System.IO;
using System.IO.Compression;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfZipExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kp-zip-" + Guid.NewGuid().ToString("N"));
    public PdfZipExportTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedArchiveContainsTheOriginalPdf(bool existing)
    {
        string source = Path.Combine(_root, "source.pdf"), destination = Path.Combine(_root, "output.zip");
        File.WriteAllText(source, "Original PDF bytes");
        if (existing) File.WriteAllText(destination, "Previous archive bytes");
        PdfZipExport.Write(source, destination);
        using var archive = ZipFile.OpenRead(destination);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("source.pdf", entry.FullName);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("Original PDF bytes", reader.ReadToEnd());
        Assert.Equal("Original PDF bytes", File.ReadAllText(source));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void CancelledExportPreservesExistingArchive()
    {
        string source = Path.Combine(_root, "source.pdf"), destination = Path.Combine(_root, "output.zip");
        File.WriteAllText(source, "Original PDF bytes");
        File.WriteAllText(destination, "Previous archive bytes");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfZipExport.Write(source, destination, cancellation.Token));
        Assert.Equal("Previous archive bytes", File.ReadAllText(destination));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void LockedDestinationPreservesBothFiles()
    {
        string source = Path.Combine(_root, "source.pdf"), destination = Path.Combine(_root, "output.zip");
        File.WriteAllText(source, "Original PDF bytes");
        File.WriteAllText(destination, "Previous archive bytes");
        using (var held = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => PdfZipExport.Write(source, destination));
        Assert.Equal("Previous archive bytes", File.ReadAllText(destination));
        Assert.Equal("Original PDF bytes", File.ReadAllText(source));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void ExportCannotReplaceItsOwnSource()
    {
        string source = Path.Combine(_root, "source.pdf");
        File.WriteAllText(source, "Original PDF bytes");
        Assert.Throws<ArgumentException>(() => PdfZipExport.Write(source, Path.Combine(_root, ".", "SOURCE.pdf")));
        Assert.Equal("Original PDF bytes", File.ReadAllText(source));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void UnreadableSourcePreservesPreviousArchive()
    {
        string source = Path.Combine(_root, "source.pdf"), destination = Path.Combine(_root, "output.zip");
        File.WriteAllText(source, "PDF source");
        File.WriteAllText(destination, "Previous archive bytes");
        using (var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => PdfZipExport.Write(source, destination));
        Assert.Equal("Previous archive bytes", File.ReadAllText(destination));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }
}
