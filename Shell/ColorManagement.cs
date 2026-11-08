using KillerPDF.Controls;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenColorManagement()
    {
        var viewer = ActiveViewer;
        var view = viewer.DocumentProofView;
        if (view is null) return;
        var proof = view.CaptureProof();
        var target = viewer.CaptureProofPreferenceTargetExt();
        var previous = proof.Stamp.IsComparison ? null : viewer.PreviousProofPreferencesExt;
        var dialog = new ColorManagementDialog(this, proof.Settings, viewer.ProofPreferenceNoticeKeyExt, previous);
        if (dialog.ShowDialog() != true || dialog.AppliedSettings is not { } settings) return;
        if (dialog.WorkingCmykChanged)
        {
            // Every render reads the new choice once; pages drawn with the old one are dropped
            // and in-flight renders are cancelled by the re-render below.
            KillerPDF.Services.PdfPageRenderSession.InvalidateWorkingCmyk();
            viewer.FlushAllRenderCachesExt();
        }
        if (proof.Stamp.IsComparison) view.ComparisonOverride = settings;
        else
        {
            view.Document.Proof = settings;
            if (target is { } captured) viewer.SaveProofPreferencesExt(captured);
        }
        viewer.RenderActiveSessionExt();
        // Thumbnails drawn with the previous working CMYK profile no longer match; redraw them.
        if (dialog.WorkingCmykChanged) RestorePageListForActivePane();
        if (viewer.ProofPreferenceNoticeKeyExt is { } notice) SetStatus(Loc(notice));
    }
}
