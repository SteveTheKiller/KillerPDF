using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Signing;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private bool CanSaveCertifiedFormFill()
    {
        if (_doc is null || _currentFile is null || _annotations.Values.Any(list => list.Count > 0)
            || HasActiveStamps || ActiveViewer.RedactionMarkCountExt > 0 || _formFontSizes.Count > 0
            || ActiveViewer.ActiveTextBoxRef is not null)
            return false;
        try
        {
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            var permission = PdfSignatureReader.ReadCertificationPermission(session.Document);
            if (permission is not (PdfSignatureCertificationPermission.FormFillingAndSignatures
                or PdfSignatureCertificationPermission.FormFillingSignaturesAndAnnotations)) return false;
            if (_pageRotations.Any(pair => pair.Key < 0 || pair.Key >= session.Pages.Count
                || pair.Value != session.Pages[pair.Key].Rotation)) return false;
            var signatures = PdfSignatureReader.Read(session.Document);
            if (!signatures.Any(signature => signature.IsSigned && signature.IsCertificationSignature)) return false;
            foreach (var signature in signatures.Where(signature => signature.IsSigned))
            {
                var integrity = PdfSignatureVerifier.VerifyIntegrity(session.Document, signature);
                if (!integrity.IsStructurallyValid || !integrity.IsCryptographicallyValid) return false;
            }
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private bool TrySaveCertifiedFormFill(string destination, bool openCopy = false, bool confirmOtherSignedTarget = false)
    {
        // The working bytes of a protected file are decrypted, so its save goes through the encryption.
        if (_protection.KeepsEncryption || !CanSaveCertifiedFormFill()) return false;
        string? source = _currentFile;
        if (source is null) return false;
        if (confirmOtherSignedTarget && !string.Equals(destination, _originalFile, StringComparison.OrdinalIgnoreCase)
            && !ConfirmSignatureRemoval(destination)) return true;

        string? candidate = null;
        try
        {
            candidate = App.MakeTempFile("certified-form");
            PdfEngineIntegration.FillRestrictedForm(source, candidate, ActiveViewer.SnapshotFormValuesForPrint());
            // Validate the completed candidate before replacing the destination. The editor enforces
            // certification and logical-field locks while keeping the signed revision byte for byte.
            using (var reopened = PdfWorkingDocument.Open(candidate)) { }
            var complete = CompleteDocumentSource.Capture(candidate);
            SaveExactCopy(candidate, destination, openCopy);
            if (!openCopy) _completeDocumentSource = complete;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            KillerDialog.Show(this, Loc("Str_Err_SaveFailed") + "\n" + error.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (candidate is not null)
                try { File.Delete(candidate); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return true;
    }
}
