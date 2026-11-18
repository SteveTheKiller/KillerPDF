using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenFormDataTools() => OpenDocumentToolCard("Str_DocumentTools_FormData",
        ("Str_Exchange_ExportForms", () => ExportExchangeData(bookmarks: false)),
        ("Str_Exchange_ImportForms", () => ImportExchangeData(bookmarks: false)),
        ("Str_FormTools_Recognize", RecognizeFormFields),
        ("Str_FormTools_ConvertXfa", ConvertXfaForm),
        ("Str_FormTools_Flatten", FlattenFormFields));

    private void OpenBookmarkDataTools() => OpenDocumentToolCard("Str_DocumentTools_BookmarkData",
        ("Str_Exchange_ExportBookmarks", () => ExportExchangeData(bookmarks: true)),
        ("Str_Exchange_ImportBookmarks", () => ImportExchangeData(bookmarks: true)));

    // Form data, Bookmark data and Preflight are a handful of commands each. They were small windows of big buttons
    // with a Close button; now each is a card of tiles under its own icon in the Document tools panel, three across,
    // the tile look of the page and OCR cards. Picking a tile, a click outside or Esc closes it. Opened from Search
    // Tools or its chord letter with the panel shut, the panel opens first and the card opens under the tool there.
    private void OpenDocumentToolCard(string toolKey, params (string Label, Action Run)[] commands)
    {
        if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
        if (DocumentToolButton(toolKey) is { } anchor)
        {
            ShowDocumentToolCard(toolKey, anchor, commands);
            return;
        }
        if (_sidebarCollapsed || _sidebarPanel != SidebarPanel.DocumentTools) OpenSidebarPanel(SidebarPanel.DocumentTools);
        else RefreshDocumentToolsPanel();
        bool byKeyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            (Action)(() => ShowDocumentToolCard(toolKey, DocumentToolButton(toolKey), commands, byKeyboard)));
    }

    // The tool's own icon in the Document tools panel, when the panel shows it.
    private Button? DocumentToolButton(string toolKey) =>
        _documentToolsRows.Where(row => row.Tool.LabelKey == toolKey && row.Button.IsVisible).Select(row => row.Button).FirstOrDefault();

    private void ShowDocumentToolCard(string toolKey, Button? anchor, (string Label, Action Run)[] commands, bool? byKeyboard = null)
    {
        if (_doc is null) return;
        // The card does not hold the window, so a tile acts only on the document it was opened for.
        var isSourceCurrent = CaptureDocumentOrigin();
        bool keyboard = byKeyboard ?? InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        var menu = MakeThemedMenu();
        if (TryFindResource("CardMenu") is Style card) menu.Style = card;
        System.Windows.Automation.AutomationProperties.SetName(menu, Loc(toolKey));
        var tiles = QuickStrip(menu, 3, [.. commands.Select(command => new CardTile(
            Controls.CommandLauncher.GlyphFor(command.Label), Loc(command.Label), null,
            () => { if (isSourceCurrent()) command.Run(); }, false, true))]);
        menu.Items.Add(CardSlot(tiles, new Thickness(0)));
        FocusFirstActionFromKeyboard(menu, tiles);
        if (anchor is not null)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            // While the card is open its icon takes no clicks, so the click that closes the card does not open it
            // again at once (the Redact bar's Mark pages flyout does the same).
            anchor.IsHitTestVisible = false;
        }
        else
        {
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        }
        menu.Closed += (_, _) =>
        {
            if (anchor is null) return;
            anchor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => anchor.IsHitTestVisible = true));
            // Opened from the keyboard, the keyboard goes back to the tool, unless the picked command already moved
            // it (a dialog it opened).
            if (keyboard && (Keyboard.FocusedElement is not DependencyObject focused
                || ReferenceEquals(focused, menu) || menu.IsAncestorOf(focused)))
                anchor.Focus();
        };
        menu.IsOpen = true;
    }

    private void OpenExchangeCommands(string titleKey, params (string Label, Action Run)[] commands)
    {
        if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
        var isSourceCurrent = CaptureDocumentOrigin();
        var dialog = new DocumentCommandDialog(this, Loc(titleKey),
            commands.Select(command => (Loc(command.Label), Controls.CommandLauncher.GlyphFor(command.Label), command.Run)).ToArray());
        if (dialog.ShowDialog() == true && isSourceCurrent()) dialog.Command?.Invoke();
    }

    // Output copies compose editable overlays and document side state. Structural edits keep
    // those separate through CreateExchangeSnapshot so reloading cannot burn them twice.
    // Production layouts and Data Merge rebuild pages from their content and do not carry
    // annotations, so KillerPDF's own text and markup are drawn into the page here, as 1.9.2 did,
    // with replaced text still removed; a save keeps added text as editable annotations instead.
    private SavedCopy CreateOutputSnapshot()
    {
        CommitActiveTextBox();
        return WriteSavedCopyToTemp(flattenEditing: true);
    }

    private string CreateExchangeSnapshot()
    {
        CommitActiveTextBox();
        string path = App.MakeTempFile("exchange");
        try
        {
            _doc!.Save(path);
            WriteFormValuesToDocument(path);
            return path;
        }
        catch { File.Delete(path); throw; }
    }

    private void ExportExchangeData(bool bookmarks)
    {
        if (_doc is null || !EnsureMayWrite(PdfOutput.Data)) return;
        string title = Loc(bookmarks ? "Str_Exchange_ExportBookmarks" : "Str_Exchange_ExportForms");
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = title,
            Filter = bookmarks ? "JSON|*.json" : "XFDF|*.xfdf|FDF|*.fdf|JSON|*.json",
            DefaultExt = bookmarks ? "json" : "xfdf",
            FileName = Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile ?? "document")
                + (bookmarks ? "-bookmarks.json" : "-forms.xfdf")
        };
        if (picker.ShowDialog(this) != true) return;
        string destination = picker.FileName;
        if (bookmarks && !Path.GetExtension(destination).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            destination += ".json";
            if (File.Exists(destination) && KillerDialog.Show(this,
                    string.Format(Loc("Str_Dlg_OverwriteMsg"), Path.GetFileName(destination)), title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        string? snapshot = null;
        try
        {
            snapshot = CreateExchangeSnapshot();
            // The snapshot holds the tab's own bytes, so it reads under the tab's protection.
            PdfDocument document = PdfEngineIntegration.OpenForReading(File.ReadAllBytes(snapshot), _protection);
            byte[] bytes = bookmarks
                ? Encoding.UTF8.GetBytes(PdfBookmarkExchange.ToJson(document, indented: true))
                : PdfEngineIntegration.ExportFormData(document, Path.GetExtension(destination),
                    Path.GetFileName(_originalFile));
            PdfEngineIntegration.WriteExchangeFile(destination, bytes);
            SetStatus(string.Format(Loc("Str_St_Saved"), Path.GetFileName(destination)));
        }
        catch (Exception ex) { ShowExchangeError(ex); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
    }

    private void ImportExchangeData(bool bookmarks)
    {
        if (_doc is null || !EnsureEditable()) return;
        var isSourceCurrent = CaptureDocumentOrigin();
        string title = Loc(bookmarks ? "Str_Exchange_ImportBookmarks" : "Str_Exchange_ImportForms");
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Title = title,
            Filter = bookmarks ? "JSON|*.json" : "FDF / XFDF|*.fdf;*.xfdf"
        };
        if (picker.ShowDialog(this) != true || !isSourceCurrent()) return;
        string? snapshot = null;
        try
        {
            snapshot = CreateExchangeSnapshot();
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(snapshot));
            string? json = bookmarks ? File.ReadAllText(picker.FileName) : null;
            PdfFormDataSet? data = bookmarks ? null : PdfEngineIntegration.ReadFormData(
                File.ReadAllBytes(picker.FileName), Path.GetExtension(picker.FileName));
            string report;
            if (bookmarks)
            {
                // Build the replacement in memory first so malformed input cannot reach the working copy.
                PdfDocument preview = PdfDocument.Open(PdfBookmarkExchange.Import(document, json!, replaceExisting: true));
                report = string.Join(Environment.NewLine, BookmarkTitles(PdfBookmarkReader.Read(preview)));
            }
            else
            {
                PdfFormDataImportReport preview = PdfFormDataImporter.CreateReport(document, data!);
                report = string.Format(Loc("Str_Exchange_Preview"), preview.ApplicableFieldCount,
                    preview.BlockedFieldCount, data!.Annotations.Count)
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, preview.Fields.Select(field =>
                        (field.Status == PdfFormDataMatchStatus.Matched ? "✓ " : "× ") + field.FieldName));
                if (preview.ApplicableFieldCount == 0 && data.Annotations.Count == 0)
                {
                    new DocumentReportDialog(this, title, report).ShowDialog();
                    return;
                }
            }
            var review = new ImportReviewDialog(this, title, report,
                Loc(bookmarks ? "Str_Exchange_ReplaceBookmarks" : "Str_Exchange_Flatten"));
            if (review.ShowDialog() != true || !isSourceCurrent()) return;
            UndoEntry? undo = CaptureDocumentUndo();
            if (!bookmarks && undo is { } captured)
                undo = captured with { DocBytes = File.ReadAllBytes(snapshot) };
            SaveTempAndReload(keepAnnotations: true, documentUndo: undo, finalizeSavedFile: path =>
            {
                if (bookmarks) PdfEngineIntegration.ApplyBookmarkData(path, json!, review.Option);
                else
                {
                    WriteFormValuesToDocument(path);
                    PdfEngineIntegration.ApplyFormData(path, data!, review.Option);
                }
            }, onPublished: bookmarks ? null : () =>
            {
                // The typed values are now in the imported file, so the overlays go only once it is live.
                ActiveViewer.FormTextValuesRef.Clear();
                ActiveViewer.FormChoiceValuesRef.Clear();
                ActiveViewer.FormMultiChoiceValuesRef.Clear();
                ActiveViewer.FormCheckValuesRef.Clear();
                ActiveViewer.FormRadioValuesRef.Clear();
                ActiveViewer.FormFontSizesRef.Clear();
                ActiveViewer.FormTextStylesRef.Clear();
            });
            MarkDirty();
        }
        catch (Exception ex) { ShowExchangeError(ex); }
        finally { if (snapshot is not null) File.Delete(snapshot); }
    }

    private static IEnumerable<string> BookmarkTitles(IEnumerable<PdfBookmarkInfo> bookmarks, int level = 0)
    {
        foreach (PdfBookmarkInfo bookmark in bookmarks)
        {
            yield return new string(' ', level * 2) + bookmark.Title;
            foreach (string child in BookmarkTitles(bookmark.Children, level + 1)) yield return child;
        }
    }

    private void ShowExchangeError(Exception error) => KillerDialog.Show(this,
        Loc("Str_DocumentTools_Failed") + "\n" + error.Message,
        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
}
