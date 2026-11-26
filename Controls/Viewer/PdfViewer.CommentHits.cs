using System.IO;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // Review comments the PDF carries (a note's icon, a highlight saved by another app) are part of the rendered page,
    // not the markup list, so a right-click finds them by their rectangles. The comments are read once per saved
    // version of the file and kept until the file changes.
    public partial class PdfViewer
    {
        private (string Path, DateTime Written, long Length, IReadOnlyList<PdfCommentInfo> Comments)? _commentHits;

        /// <summary>The topmost review comment from the file under a canvas point, or null.</summary>
        internal PdfCommentInfo? CommentAtExt(int pageIndex, Point point)
        {
            // EPUB and DjVu books carry no PDF comments.
            if (_currentFile is null || NativeDocumentRef is not null || !TryCanvasFrame(pageIndex, out var frame)) return null;
            var page = EnsureEngineDocumentSession().Pages[pageIndex];
            const double pad = 2;
            foreach (PdfCommentInfo comment in PageComments(pageIndex).Reverse())
            {
                if (comment.Bounds is not { } b) continue;
                // The annotation rectangle is in the page's own space; the canvas starts at the crop box corner.
                Rect area = RedactionGeometry.PageToCanvas(b.Left - page.Left, b.Bottom - page.Bottom, b.Right - page.Left,
                    b.Top - page.Bottom, frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation);
                area.Inflate(pad, pad);
                if (area.Contains(point)) return comment;
            }
            return null;
        }

        // A popup is the note's open window, not something drawn on the page, so it is never a hit. A link or a form
        // field with a tooltip text has its own menu.
        private IEnumerable<PdfCommentInfo> PageComments(int pageIndex)
        {
            string path = _currentFile!;
            var file = new FileInfo(path);
            if (!file.Exists) return [];
            if (_commentHits is not { } cached || cached.Path != path
                || cached.Written != file.LastWriteTimeUtc || cached.Length != file.Length)
            {
                var all = new List<PdfCommentInfo>();
                void Walk(PdfCommentThread thread)
                {
                    all.Add(thread.Comment);
                    foreach (PdfCommentThread reply in thread.Replies) Walk(reply);
                }
                try
                {
                    foreach (PdfCommentThread thread in PdfEngineIntegration.ReadCommentThreads(path, _protection)) Walk(thread);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // A file whose comments cannot be read still gets the page card.
                    all.Clear();
                }
                cached = (path, file.LastWriteTimeUtc, file.Length, all);
                _commentHits = cached;
            }
            return cached.Comments.Where(comment => comment.PageIndex == pageIndex
                && comment.Subtype is not ("Popup" or "Link" or "Widget"));
        }
    }
}
