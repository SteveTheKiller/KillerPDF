using System.IO;

namespace KillerPDF.Services;

/// <summary>The working copy of an encrypted file and what the tab must remember about it.</summary>
internal sealed record ProtectedOpen(bool Stripped, bool StartsDirty, OpenedProtection Protection);

/// <summary>The two ends of a protected document's life in the app: the decrypted copy it is edited in,
/// and the file a save puts back. Every save goes out through <see cref="Publish"/>.</summary>
internal static class ProtectedDocumentFiles
{
    /// <summary>Writes the decrypted working copy of a file that opens with no password and reads its
    /// protection, off the UI thread. An untouched file has nothing to save, so only a page rebuild,
    /// which can drop what the file held, starts out unsaved.</summary>
    internal static ProtectedOpen PrepareWorkingCopy(string sourcePath, string workingPath,
        Func<string, string, bool> rebuildPages)
    {
        OpenedProtection protection = OpenedProtection.Capture(sourcePath, null);
        bool lossless = PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(sourcePath, workingPath);
        bool rebuilt = !lossless && rebuildPages(sourcePath, workingPath);
        return new ProtectedOpen(lossless || rebuilt, rebuilt, protection);
    }

    /// <summary>Writes a smaller copy of a finished document, which keeps the file's protection as a
    /// save does. Returns what the size report needs, with the size of the file delivered: the
    /// encryption writes each object on its own, so a sealed copy is not the size of the plain one.</summary>
    internal static (long Before, long After, int Resampled, IReadOnlyList<KillerPdf.Engine.Writing.PdfReferenceRepair> Repairs) ReduceSize(
        string finishedPath, string destinationPath, string workingPath,
        KillerPdf.Engine.Writing.PdfImageDownsampleOptions? images, CancellationToken cancellation,
        OpenedProtection protection)
    {
        if (!protection.KeepsEncryption)
            return PdfEngineIntegration.ReduceFileSize(finishedPath, destinationPath, images, cancellation);
        var reduced = PdfEngineIntegration.ReduceFileSize(finishedPath, workingPath, images, cancellation);
        protection.Seal(workingPath, destinationPath);
        return reduced with { After = new FileInfo(destinationPath).Length };
    }

    /// <summary>Puts a finished document at its destination, under the file's own encryption when the
    /// tab keeps it. Returns true when it did.</summary>
    internal static bool Publish(string finishedPath, string destinationPath, OpenedProtection protection)
    {
        if (!protection.KeepsEncryption)
        {
            PdfEngineIntegration.ReplaceFileAtomically(finishedPath, destinationPath);
            return false;
        }
        protection.Seal(finishedPath, destinationPath);
        return true;
    }
}
