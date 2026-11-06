using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenNativeFile(string path)
    {
        NativeWorkingDocument? opened = null;
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length > 64L << 20)
                    throw new InvalidDataException("Native document input exceeds the 64 MiB limit.");
                byte[] bytes = new byte[checked((int)source.Length)];
                source.ReadExactly(bytes);
                if (Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase))
                {
                    var face = PdfEngineIntegration.LoadTextFont("Segoe UI", "A", bold: false, italic: false)
                        ?? throw new NotSupportedException("Native EPUB fallback font is unavailable.");
                    opened = NativeWorkingDocument.OpenEpub(bytes, new EpubLayoutOptions(800, 1000, face.Font)
                    {
                        FontFamilyResolver = PdfEngineIntegration.ResolveEpubFontFamily
                    });
                }
                else opened = NativeWorkingDocument.OpenDjvu(bytes);
            }
            _doc?.Close();
            _doc = null;
            ActiveViewer.NativeDocumentRef?.Close();
            ActiveViewer.NativeDocumentRef = opened;
            CloseEngineDocumentSession();
            _currentFile = path;
            _originalFile = path;
            _publishedFile = null;
            _completeDocumentSource = null;
            _loadedRevision = null;
            _suggestedName = null;
            FileNameLabel.Text = Path.GetFileName(path);
            _openedFromProtected = false;
            _restrictedByCertificate = false;
            _restrictedFormFillable = false;
            _protection = OpenedProtection.None;
            _pageRotations.Clear();
            _renderDims.Clear();
            _navBack.Clear();
            _navForward.Clear();
            _undoStack.Clear();
            _redoStack.Clear();
            _annotations.Clear();
            _continuousLinks.Clear();
            Search.ClearPageResults();
            _docStampSpec = null;
            AcceptSavedCheckpoint();
            _viewMode = Enum.TryParse<ViewMode>(App.GetSetting("ViewMode"), out var preferredView)
                ? preferredView : ViewMode.Continuous;
            BootstrapDocumentView(0, autoFit: true);
            SetStatus(string.Format(Loc("Str_Opened"), Path.GetFileName(path), opened.PageCount));
            SyncSidebarToDocState(hasDoc: true, startup: false);
            ActiveViewer.CompleteTabLoadExt();
        }
        catch (Exception error)
        {
            opened?.Dispose();
            if (ReferenceEquals(ActiveViewer.NativeDocumentRef, opened))
                ActiveViewer.NativeDocumentRef = null;
            KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), error.Message),
                Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
