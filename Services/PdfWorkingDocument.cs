using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Engine-validated serialized working state for one open desktop document.</summary>
internal sealed class PdfWorkingDocument : IDisposable
{
    private readonly byte[] _source;

    private PdfWorkingDocument(byte[] source, bool isReadOnly)
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);
        _source = source;
        PageCount = PdfPageInformation.Read(document).Count;
        IsReadOnly = isReadOnly;
    }

    internal int PageCount { get; }
    internal bool IsReadOnly { get; }

    internal static PdfWorkingDocument Open(string path, bool isReadOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PdfWorkingDocument(File.ReadAllBytes(path), isReadOnly);
    }

    internal void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, _source);
    }

    internal void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(_source);
    }

    // Preserve the instance lifecycle contract even though disposal currently owns no resources.
#pragma warning disable CA1822
    internal void Close() { }
#pragma warning restore CA1822
    public void Dispose() { }
}
