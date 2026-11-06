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
        var dialog = new ColorManagementDialog(this, proof.Settings);
        if (dialog.ShowDialog() != true || dialog.AppliedSettings is not { } settings) return;
        if (proof.Stamp.IsComparison) view.ComparisonOverride = settings;
        else view.Document.Proof = settings;
        viewer.RenderActiveSessionExt();
    }
}
