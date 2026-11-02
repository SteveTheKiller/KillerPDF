using System.IO;
using System.Security.Cryptography;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void ReviewAccessibility()
    {
        if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
        try
        {
            CommitActiveTextBox();
            bool canRepair = !_doc.IsReadOnly && !IsOwnerRestricted;
            // Inspect signatures before any serialization can alter their evidence.
            try { canRepair &= !PdfSignatureReader.Read(EnsureEngineDocumentSession().Document).Any(item => item.IsSigned); }
            catch (Exception error) when (error is not OutOfMemoryException) { canRepair = false; }
            Func<bool> origin = CaptureDocumentOrigin();
            string sourcePath = _currentFile;
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
            byte[] snapshot = Snapshot();
            var repairs = new Stack<UndoEntry>();
            bool Current() => origin() && File.Exists(sourcePath)
                && SHA256.HashData(File.ReadAllBytes(sourcePath)).AsSpan().SequenceEqual(sourceHash);
            void RefreshOrigin()
            {
                origin = CaptureDocumentOrigin(); sourcePath = _currentFile!;
                sourceHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
            }
            // The review reads the tab as it stands, typed form values included, under its protection.
            byte[] Snapshot()
            {
                CommitActiveTextBox();
                return AccessibilityReviewSource.Snapshot(_doc!, ActiveViewer.SnapshotFormValuesForPrint(),
                    _protection, App.MakeTempFile("exchange"));
            }
            byte[]? Apply(byte[] edited)
            {
                if (!canRepair || !Current()) return null;
                // Validate all output before the ordinary reload closes the current document.
                _ = PdfPageInformation.Read(PdfDocument.Open(edited));
                string before = App.MakeTempFile("accessibility");
                try
                {
                    File.WriteAllBytes(before, snapshot);
                    ApplyFormWorkflow(path => AtomicFileOutput.Write(path, edited), before);
                    if (ActiveViewer.UndoStackRef.TryPeek(out UndoEntry entry)) repairs.Push(entry);
                    RefreshOrigin();
                    return snapshot = Snapshot();
                }
                finally { File.Delete(before); }
            }
            byte[]? Undo()
            {
                if (!canRepair || !Current() || !repairs.TryPeek(out UndoEntry expected)
                    || !ActiveViewer.UndoStackRef.TryPeek(out UndoEntry actual) || actual != expected) return null;
                ActiveViewer.UndoClickExt(this, new RoutedEventArgs());
                repairs.Pop(); RefreshOrigin();
                return snapshot = Snapshot();
            }
            var dialog = new AccessibilityReviewDialog(this, snapshot, _appScale, canRepair,
                Apply, Undo, Current, new Dictionary<int, int>(_pageRotations), _protection);
            dialog.ShowDialog();
            if (!Current()) return;
            if (dialog.RequestedTool == "Str_Repair_ReadingOrder") RepairReadingOrder();
            else if (dialog.RequestedTool == "Str_Repair_TableHeader") RepairTableHeader();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { ShowExchangeError(error); }
    }
}
