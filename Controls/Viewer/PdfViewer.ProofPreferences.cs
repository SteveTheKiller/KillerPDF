using System.IO;
using KillerPdf.Engine.ColorManagement;
using KillerPDF.Services;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    internal readonly record struct ProofPreferenceTarget(PdfDocumentProofView View, string Path, string SourceSha256);

    internal string? ProofPreferenceNoticeKeyExt => _active?.ProofPreferenceNoticeKey;

    internal ProofPreferenceTarget? CaptureProofPreferenceTargetExt() =>
        _active is { ProofPreferenceSource: { } source } session
            ? new(session.ProofView, source.Path, source.SourceSha256) : null;

    internal void RestoreProofPreferencesExt(string path, string sourceSha256)
    {
        if (_active is not { } session) return;
        try
        {
            string fullPath = Path.GetFullPath(path);
            session.ProofPreferenceSource = (fullPath, sourceSha256);
            var target = new ProofPreferenceTarget(session.ProofView, fullPath, sourceSha256);
            if (session.PreserveInitialProofOnLoad)
            {
                SaveProofPreferencesExt(target);
                return;
            }
            var restored = new DocumentProofStore().Restore(fullPath, sourceSha256, new ProofProfileLibrary());
            session.ProofView.Document.Proof = restored.Settings;
            session.PreviousProofPreferences = restored.PreviousForReview;
            session.ProofPreferenceNoticeKey = null;
        }
        catch (Exception error) when (IsProofPreferenceFailure(error))
        {
            session.ProofPreferenceNoticeKey = "Str_CM_PreferencesLoadFailed";
        }
        finally
        {
            session.PreserveInitialProofOnLoad = false;
        }
    }

    internal void SaveProofPreferencesExt(ProofPreferenceTarget target)
    {
        var session = _sessions.FirstOrDefault(item => ReferenceEquals(item.ProofView, target.View)
            && item.ProofPreferenceSource is { } source
            && source.Path == target.Path && source.SourceSha256 == target.SourceSha256);
        if (session is null) return;
        try
        {
            new DocumentProofStore().Save(target.Path, target.SourceSha256, target.View.Document.Proof);
            session.ProofPreferenceNoticeKey = null;
            session.PreviousProofPreferences = null;
        }
        catch (Exception error) when (IsProofPreferenceFailure(error))
        {
            session.ProofPreferenceNoticeKey = "Str_CM_PreferencesSaveFailed";
        }
    }

    private static bool IsProofPreferenceFailure(Exception error) => error is IOException
        or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException
        or InvalidOperationException or System.Security.SecurityException;
}
