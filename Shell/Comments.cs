using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // Comments (#288) from the Document tools panel: read, edit, answer and delete the PDF's own
    // review comments, and add notes to the current page.
    public partial class MainWindow
    {
        private void EditComments() => EditComments(addNew: false);

        // focus: the comment a right-click card was opened on, selected when the window opens; reply starts an answer to it.
        private void EditComments(bool addNew, PdfCommentInfo? focus = null, bool reply = false)
        {
            if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
            {
                KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
                return;
            }

            CommitActiveTextBox();
            if (ReadCommentThreads() is not { } threads) return;

            string author = Environment.UserName;
            var dialog = new Controls.CommentsDialog(this, threads, _currentPage, author,
                page => ActiveViewer.NavigateToPageExt(page));
            if (addNew) dialog.AddComment();
            else if (focus is not null && dialog.Select(focus) && reply) dialog.Reply();
            dialog.ShowDialog();
            if (!dialog.Saved) return;
            SaveCommentEdits(dialog.Edits, author);
        }

        private IReadOnlyList<PdfCommentThread>? ReadCommentThreads()
        {
            try { return PdfEngineIntegration.ReadCommentThreads(_currentFile!, _protection); }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Comments_ReadFailed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        // One save, one undo step, for the Comments window and the comment card alike.
        private void SaveCommentEdits(IReadOnlyList<CommentEdit> edits, string author)
        {
            if (edits.Count == 0 || !EnsureEditable()) return;
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path => PdfEngineIntegration.ApplyCommentEdits(path, edits, author),
                    documentUndo: documentUndo);
                SetStatus(Loc("Str_Comments_Updated"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Comments_Failed") + "\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // The right-click card for a review comment the PDF carries: Edit and Reply open the Comments window on it, Copy
        // Text takes its text, and Delete in the header removes it with its replies, as the window's Delete does.
        private void AddCommentCard(PdfCommentInfo comment)
        {
            if (TryFindResource("CardMenu") is Style card) _ctxMenu.Style = card;
            AddNavRow(_ctxMenu, comment.PageIndex);
            string author = string.IsNullOrWhiteSpace(comment.Author) ? Loc("Str_Comments_NoAuthor") : comment.Author;
            string meta = author + " · " + string.Format(Loc("Str_PageLabel"), comment.PageIndex + 1);
            bool canChange = _doc is not null && _currentFile is not null;
            var header = WithHeaderAction(ObjectCardHeader(Loc("Str_Annot_Comment"), meta, null),
                NavRowButton(_ctxMenu, "\uE74D", "Str_Ctx_DeleteSel", null, () => DeleteComment(comment), canChange));
            _ctxMenu.Items.Add(CardSlot(header, new Thickness(4, 2, 4, 6)));
            // A reply points at its comment by name, so a comment saved without one cannot take replies.
            CardTile[] actions =
            [
                new("\uE70F", Loc("Str_Ctx_Edit"), null, () => EditComments(addNew: false, focus: comment), false, canChange),
                new("\uE97A", Loc("Str_Comments_Reply"), null, () => EditComments(addNew: false, focus: comment, reply: true), false,
                    canChange && !string.IsNullOrEmpty(comment.Name)),
                new("\uE8C8", Loc("Str_Ctx_CopyText"), null, () => CopyCommentText(comment), false, comment.Contents.Length > 0),
            ];
            var strip = TileGroups(_ctxMenu, actions);
            _ctxMenu.Items.Add(CardSlot(strip, new Thickness(0)));
            FocusFirstActionFromKeyboard(_ctxMenu, strip);
        }

        private void CopyCommentText(PdfCommentInfo comment)
        {
            try { Clipboard.SetText(comment.Contents); } catch (System.Runtime.InteropServices.ExternalException) { return; }
            SetStatus(Loc("Str_St_Copied"));
        }

        private void DeleteComment(PdfCommentInfo comment)
        {
            if (_currentFile is null || ReadCommentThreads() is not { } threads) return;
            var gone = new List<CommentEdit>();
            void Forget(PdfCommentThread thread)
            {
                gone.Add(new CommentRemoval(thread.Comment));
                foreach (PdfCommentThread reply in thread.Replies) Forget(reply);
            }
            bool Find(IEnumerable<PdfCommentThread> level)
            {
                foreach (PdfCommentThread thread in level)
                {
                    if (thread.Comment.PageIndex == comment.PageIndex && thread.Comment.AnnotationIndex == comment.AnnotationIndex)
                    {
                        Forget(thread);
                        return true;
                    }
                    if (Find(thread.Replies)) return true;
                }
                return false;
            }
            if (!Find(threads)) return;
            SaveCommentEdits(gone, Environment.UserName);
        }
    }
}
