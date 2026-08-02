using System.Threading;
using System.Threading.Tasks;

namespace KillerPDF.Features
{
    /// <summary>
    /// What OcrController needs from the window hosting it, beyond the shared shell services.
    ///
    /// Every member is a value, a plain string, or an intent ("put up the busy overlay"), never a
    /// control, so the controller holds no reference to a Border or a Dictionary of window state
    /// and can be driven by a stub in a test.
    /// </summary>
    internal interface IOcrHost : IShellServices
    {
        /// <summary>True while a document is open (both the live doc and its backing file).</summary>
        bool HasDocument { get; }

        /// <summary>Page count of the open document, 0 when none.</summary>
        int PageCount { get; }

        /// <summary>Path of the working (temp) file the renderer reads. Null when no document.</summary>
        string? CurrentFile { get; }

        /// <summary>Path of the file the user actually opened, for suggesting output names.
        /// Null for a document that never had an original (e.g. built from images).</summary>
        string? OriginalFile { get; }

        /// <summary>The page's in-memory rotation in degrees, 0 when untouched. The working file
        /// has /Rotate stripped, so renders must rotate the pixel buffer by this.</summary>
        int RotationFor(int pageIdx);

        /// <summary>Render-dim space of the page overlay (the (w,h) its canvas coordinates live
        /// in), for mapping a canvas rect onto the OCR bitmap. False when the page has not
        /// rendered yet.</summary>
        bool TryGetRenderDims(int pageIdx, out int w, out int h);

        /// <summary>The '+'-joined Tesseract language string, e.g. "eng" or "eng+spa".</summary>
        string OcrLanguageString { get; }

        /// <summary>Makes sure the selected language models are on disk, downloading behind a
        /// heads-up dialog if not. False when the user declined or the download failed.</summary>
        Task<bool> EnsureOcrModelsReadyAsync();

        /// <summary>Commits any annotation text box still being edited, so the saved snapshot
        /// matches what is on screen.</summary>
        void CommitActiveTextBox();

        /// <summary>Saves the live document to <paramref name="path"/>. Throws on failure.</summary>
        void SaveDocumentTo(string path);

        /// <summary>Registers the cancellable operation (Esc offers to cancel it) and puts up the
        /// busy overlay. Returns the token to thread through the work.</summary>
        CancellationToken BeginOp(string label, string busyMessage);

        /// <summary>Updates the busy overlay's message line (per-page progress). UI thread only.</summary>
        void SetBusyMessage(string message);

        /// <summary>Takes the busy overlay down - before any completion dialog, so the overlay is
        /// gone when the dialog appears. Safe to call when it is already down.</summary>
        void HideBusy();

        /// <summary>Disposes the cancellable-operation registration. Always called from finally.</summary>
        void EndOp();
    }
}
