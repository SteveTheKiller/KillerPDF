using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;

namespace KillerPDF.Services;

/// <summary>The keys of open tabs that read their file under its own encryption. A signed PDF that
/// needs a password cannot be stripped without clearing its signatures, so its tab reads the original
/// read-only, and every reader of that file, the working document, the render session and the text
/// and form readers, opens it here with no password. A key lasts as long as the tab that holds it.</summary>
internal static class HeldEncryptions
{
    private static readonly List<WeakReference<PdfEncryptionSnapshot>> _held = [];
    private static readonly Lock _sync = new();

    /// <summary>True while any tab may still hold a key, so a reader with nothing to try skips the work.</summary>
    internal static bool Any
    {
        get { lock (_sync) return _held.Count > 0; }
    }

    internal static void Hold(PdfEncryptionSnapshot encryption)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        lock (_sync)
        {
            _held.RemoveAll(reference => !reference.TryGetTarget(out PdfEncryptionSnapshot? held) || ReferenceEquals(held, encryption));
            _held.Add(new WeakReference<PdfEncryptionSnapshot>(encryption));
        }
    }

    /// <summary>Opens bytes under a held key that fits their encryption, or returns null when none does.</summary>
    internal static PdfDocument? TryOpen(ReadOnlyMemory<byte> source)
    {
        PdfEncryptionSnapshot[] keys;
        lock (_sync)
        {
            _held.RemoveAll(reference => !reference.TryGetTarget(out _));
            keys = [.. _held.Select(reference => reference.TryGetTarget(out PdfEncryptionSnapshot? held) ? held : null).OfType<PdfEncryptionSnapshot>()];
        }
        foreach (PdfEncryptionSnapshot key in keys)
        {
            try { return PdfDocument.OpenWithEncryption(source, key); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        return null;
    }
}
