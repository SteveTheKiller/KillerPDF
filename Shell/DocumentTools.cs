using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    /// <summary>One tool in the Document tools panel. <see cref="Key"/> is its Ctrl+Shift+D letter, or null for a
    /// tool with none (Convert to PDF, offered for a book).</summary>
    private sealed record DocumentTool(string LabelKey, string Glyph, Action Run, char? Key, bool Enabled = true);

    // The Document tools, for the sidebar panel (Alt+7, Ctrl+Shift+D) and Search Tools. Each tool names its chord in
    // its tooltip, and pressing the tool's letter while the panel has the keyboard runs it. The letters live in
    // ShortcutTable, which the shortcuts overlay reads too, so the three cannot disagree. A book and an empty
    // window get only the tools that work for them.
    private List<DocumentTool> DocumentToolsForState()
    {
        if (ActiveViewer.NativeDocumentRef is not null)
            return
            [
                new DocumentTool("Str_NativePdf_Convert", "\uE8AB", ConvertNativeDocumentToPdf, null),
                DocumentToolItem("Str_CM_Title", OpenColorManagement, "\uE790"),
                DocumentToolItem("Str_DocumentTools_Macros", OpenMacroDialog, "\uE945"),
                DocumentToolItem("Str_CertCatalog_Title", OpenCertificateCatalog, "\uEB95"),
            ];
        if (_doc is null)
            return
            [
                DocumentToolItem("Str_DocumentTools_Macros", OpenMacroDialog, "\uE945"),
                DocumentToolItem("Str_CertCatalog_Title", OpenCertificateCatalog, "\uEB95"),
            ];
        var comparison = DocumentToolItem("Str_DocumentTools_StructuralComparison",
            ShowStructuralComparison, "\uE8AB") with
            {
                Enabled = IsSplit
                    && !string.IsNullOrWhiteSpace(Viewer.CurrentFilePathExt)
                    && !string.IsNullOrWhiteSpace(ViewerB.CurrentFilePathExt)
            };
        return
        [
            DocumentToolItem("Str_DocumentTools_Preflight", OpenPreflightTools, "\uE9D9"),
            DocumentToolItem("Str_CM_Title", OpenColorManagement, "\uE790"),
            DocumentToolItem("Str_DocumentTools_PrintProduction", () => ShowDocumentReport(Loc("Str_DocumentTools_PrintProduction"),
                (path, protection) => PdfEngineIntegration.InspectPrintProduction(path, protection).ToText()), "\uE749"),
            DocumentToolItem("Str_DocumentTools_Separations", ShowSeparationPreview, "\uE8B9"),
            DocumentToolItem("Str_DocumentTools_Accessibility", OpenAccessibilityTools, "\uE776"),
            DocumentToolItem("Str_DocumentTools_Signatures", () => ShowDocumentReport(Loc("Str_DocumentTools_Signatures"),
                (path, protection) => PdfEngineIntegration.InspectSignatures(path, protection).ToText()), "\uE8D7"),
            DocumentToolItem("Str_CertCatalog_Title", OpenCertificateCatalog, "\uEB95"),
            DocumentToolItem("Str_DocumentTools_Layers", () => ShowDocumentReport(Loc("Str_DocumentTools_Layers"),
                (path, protection) => PdfEngineIntegration.InspectLayers(path, protection).ToText()), "\uE81E"),
            DocumentToolItem("Str_DocumentTools_EditLayers", EditLayers, "\uE81E"),
            DocumentToolItem("Str_DocumentTools_Portfolio", () => ShowDocumentReport(Loc("Str_DocumentTools_Portfolio"),
                PdfEngineIntegration.InspectPortfolio), "\uE7B8"),
            DocumentToolItem("Str_DocumentTools_EditPortfolio", EditPortfolio, "\uE7B8"),
            DocumentToolItem("Str_DocumentTools_EditPortfolioStructure", EditPortfolioStructure, "\uE80A"),
            DocumentToolItem("Str_DocumentTools_EditAttachments", EditAttachments, "\uE723"),
            DocumentToolItem("Str_DocumentTools_Comments", EditComments, "\uE90A"),
            DocumentToolItem("Str_DocumentTools_Measurements", ShowMeasurementReport, "\uED5E"),
            DocumentToolItem("Str_DocumentTools_NavigationAudit", AuditNavigation, "\uE71B"),
            DocumentToolItem("Str_DocumentTools_AutoBookmarks", GenerateBookmarks, "\uE728"),
            DocumentToolItem("Str_DocumentTools_PageLabels", EditPageLabels, "\uE8EC"),
            DocumentToolItem("Str_DocumentTools_InitialView", EditInitialView, "\uE7B3"),
            comparison,
            DocumentToolItem("Str_Guided_Title", OpenGuidedTasks, "\uE9D5"),
            DocumentToolItem("Str_DocumentTools_Macros", OpenMacroDialog, "\uE945"),
            DocumentToolItem("Str_DocumentTools_DataMerge", RunDataMerge, "\uE8F1"),
            DocumentToolItem("Str_DocumentTools_FormData", OpenFormDataTools, "\uE8A1"),
            DocumentToolItem("Str_DocumentTools_BookmarkData", OpenBookmarkDataTools, "\uE943"),
            DocumentToolItem("Str_DocumentTools_ProductionLayouts", OpenProductionTools, "\uE8A9"),
            DocumentToolItem("Str_DocumentTools_InsertToc", InsertTableOfContents, "\uE8FD"),
        ];
    }

    // The edit tools show what they edit with a pen badge on it, instead of all showing the same bare pen.
    private static readonly HashSet<string> EditToolKeys = new(StringComparer.Ordinal)
    {
        "Str_DocumentTools_EditLayers", "Str_DocumentTools_EditPortfolio", "Str_DocumentTools_EditPortfolioStructure",
        "Str_DocumentTools_EditAttachments", "Str_DocumentTools_PageLabels",
    };

    private DocumentTool DocumentToolItem(string labelKey, Action run, string glyph)
        => new(labelKey, glyph, run, ShortcutTable.DocumentToolKey(labelKey));

    // The built panel: its scroller, its rows, and what it was built from. Pane focus refreshes the panel on every
    // click, so a refresh that changes nothing leaves the rows, and the keyboard focus on them, where they are.
    private ScrollViewer? _documentToolsScroll;
    private string? _documentToolsShape;
    private readonly List<(Button Button, DocumentTool Tool)> _documentToolsRows = [];
    // Set by Ctrl+Shift+D until the next key, which may still have Ctrl and Shift held from the chord.
    private bool _documentToolsChordPressed;

    /// <summary>
    /// Ctrl+Shift+D: shows the Document tools panel and puts the keyboard on its first tool, so the tool's letter,
    /// Enter or Space runs it. Unlike the rail button, pressing it again with the panel open keeps the panel.
    /// </summary>
    private void ShowDocumentToolsPanel()
    {
        if (_sidebarCollapsed || _sidebarPanel != SidebarPanel.DocumentTools)
            OpenSidebarPanel(SidebarPanel.DocumentTools);
        else
            RefreshDocumentToolsPanel();
        _documentToolsChordPressed = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)FocusDocumentTools);
    }

    // The first tool that can run takes the keyboard, so the tool letters reach HandleDocumentToolsKey. While the
    // sidebar is still sliding open the row may not take focus yet, so it is tried once more when the window idles.
    private void FocusDocumentTools() => FocusDocumentTools(retry: true);

    private void FocusDocumentTools(bool retry)
    {
        if (_sidebarPanel != SidebarPanel.DocumentTools || _documentToolsScroll is null) return;
        Button? first = _documentToolsRows.Select(row => row.Button).FirstOrDefault(button => button.IsEnabled);
        if (first is not null && !first.Focus() && retry)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                (Action)(() => FocusDocumentTools(retry: false)));
    }

    /// <summary>
    /// Builds the Document tools panel for what is open, when it is the open panel: the tools under their section
    /// headings, in as many columns as the panel's width allows. Nothing is rebuilt when the tools are what the
    /// panel already shows, unless <paramref name="force"/> asks (a language or shortcut change renames the chords).
    /// </summary>
    private void RefreshDocumentToolsPanel(bool force = false)
    {
        if (_sidebarPanel != SidebarPanel.DocumentTools || SidebarListPanel is null) return;
        List<DocumentTool> tools = DocumentToolsForState();
        string shape = string.Join("|", tools.Select(tool => tool.LabelKey + (tool.Enabled ? "+" : "-")));
        if (!force && shape == _documentToolsShape && _documentToolsScroll is { } built
            && ReferenceEquals(built.Parent, SidebarListPanel))
            return;   // the actions read what is open when they run, so the built rows stay correct
        bool hadKeyboard = _documentToolsScroll?.IsKeyboardFocusWithin == true;
        _documentToolsShape = shape;
        _documentToolsRows.Clear();
        SidebarListPanel.Children.Clear();
        _sidebarPanelTree = null;

        var sections = new StackPanel { Margin = new Thickness(6, 0, 6, 8) };
        var grids = new List<System.Windows.Controls.Primitives.UniformGrid>();
        var byKey = tools.ToDictionary(tool => tool.LabelKey, StringComparer.Ordinal);
        foreach (var (titleKey, keys) in DocumentToolsLayout.Group(tools.Select(tool => tool.LabelKey)))
        {
            var heading = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                Margin = new Thickness(6, 6, 6, 2),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            heading.SetResourceReference(TextBlock.TextProperty, titleKey);
            heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            sections.Children.Add(heading);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (string key in keys) grid.Children.Add(DocumentToolRow(byKey[key]));
            sections.Children.Add(grid);
            grids.Add(grid);
        }

        // Tiles wrap into columns across the width and the panel scrolls down only when the window is too short
        // for them. Every section shares the column count, so the tiles line up from one section to the next.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Not focusable: a click on the panel's empty space must not take the editor's letter keys.
            Focusable = false,
            Content = sections
        };
        System.Windows.Automation.AutomationProperties.SetName(scroll, Loc("Str_Rail_DocumentTools"));
        void Fit()
        {
            double width = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            int columns = DocumentToolsLayout.Columns(width - sections.Margin.Left - sections.Margin.Right);
            foreach (var grid in grids)
                if (grid.Columns != columns) grid.Columns = columns;
        }
        scroll.SizeChanged += (_, _) => Fit();
        scroll.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) Fit(); };
        scroll.LostKeyboardFocus += (_, _) =>
        {
            if (!scroll.IsKeyboardFocusWithin) _documentToolsChordPressed = false;
        };
        SidebarListPanel.Children.Add(scroll);
        _documentToolsScroll = scroll;
        Fit();
        if (hadKeyboard)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)FocusDocumentTools);
    }

    // One tool: its glyph and name (DocumentToolRowBtn), with the name and its chord in the tooltip (the family
    // shortcut rule). The name follows the language through its resource; the tooltip is rebuilt on a change.
    private Button DocumentToolRow(DocumentTool tool)
    {
        var row = new Button { Content = tool.Glyph, IsEnabled = tool.Enabled };
        row.SetResourceReference(StyleProperty, "DocumentToolRowBtn");
        row.SetResourceReference(TagProperty, tool.LabelKey);
        if (EditToolKeys.Contains(tool.LabelKey)) ToolBadge.SetGlyph(row, char.ConvertFromUtf32(0xE70F));
        System.Windows.Automation.AutomationProperties.SetAutomationId(row, tool.LabelKey);
        string name = Loc(tool.LabelKey);
        row.ToolTip = tool.Key is char key
            ? KillerFamily.Layout.ShortcutHints.WithHint(name, DocumentToolGestureText(key, tool.LabelKey))
            : name;
        row.Click += (_, _) =>
        {
            _documentToolsChordPressed = false;
            // A click leaves the keyboard where it was before, as the menu this panel replaced did, so the
            // single-letter editor tools (T, S, C...) stay the window's. Only a panel entered from the keyboard
            // (Ctrl+Shift+D, Tab, the arrows) keeps the tool letters.
            if (InputManager.Current.MostRecentInputDevice is MouseDevice or StylusDevice && row.IsKeyboardFocused)
            {
                Keyboard.ClearFocus();
                Focus();
            }
            tool.Run();
        };
        _documentToolsRows.Add((row, tool));
        return row;
    }

    /// <summary>
    /// Keys while the Document tools panel has the keyboard: a tool's letter runs it, Enter and Space run the focused
    /// tool, and the arrows move between tools. The window's own keys (single-letter tools, page turning, panning)
    /// would otherwise take them first.
    /// </summary>
    private bool HandleDocumentToolsKey(KeyEventArgs e)
    {
        if (_sidebarPanel != SidebarPanel.DocumentTools || _documentToolsScroll is not { IsKeyboardFocusWithin: true })
        {
            _documentToolsChordPressed = false;
            return false;
        }
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift) return false;
        bool chordPressed = _documentToolsChordPressed;
        _documentToolsChordPressed = false;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key is Key.Enter or Key.Space)
        {
            if (Keyboard.FocusedElement is not Button focused
                || !_documentToolsRows.Any(row => ReferenceEquals(row.Button, focused))) return false;
            e.Handled = true;
            if (!e.IsRepeat && focused.IsEnabled)
                focused.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, focused));
            return true;
        }
        if (modifiers == ModifierKeys.None && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var direction = key switch
            {
                Key.Left => FocusNavigationDirection.Left,
                Key.Right => FocusNavigationDirection.Right,
                Key.Up => FocusNavigationDirection.Up,
                _ => FocusNavigationDirection.Down,
            };
            if (Keyboard.FocusedElement is UIElement from) from.MoveFocus(new TraversalRequest(direction));
            e.Handled = true;
            return true;
        }
        if (!KeyboardShortcutsEnabled || DocumentToolsLayout.ChordKey(key, modifiers, chordPressed) is not char letter)
            return false;
        var match = _documentToolsRows.FirstOrDefault(row => row.Tool.Key == letter);
        if (match.Button is null) return false;
        e.Handled = true;
        if (!e.IsRepeat && match.Button.IsEnabled)
            match.Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, match.Button));
        return true;
    }

    private async void RunDataMerge()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        if (!EnsureMayWrite(PdfOutput.Printable, continueAfterUnlock: true)) return;

        var dataDialog = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Title = Loc("Str_DataMerge_SelectData"),
            Filter = Loc("Str_DataMerge_DataFiles") + "|*.csv;*.json;*.xlsx|"
                + Loc("Str_Dlg_AllFiles") + "|*.*"
        };
        if (dataDialog.ShowDialog(this) != true) return;

        IReadOnlyList<IReadOnlyDictionary<string, string?>> records;
        try
        {
            records = PdfDataMergeWorkflow.LoadRecords(dataDialog.FileName);
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DataMerge_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (records.Count == 0)
        {
            KillerDialog.Show(this, Loc("Str_DataMerge_NoRecords"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile) + "-merged.pdf";
        var outputDialog = new Controls.FileDialog(Controls.FileDialogMode.Save)
        {
            Title = Loc("Str_DataMerge_SelectOutput"),
            Filter = Loc("Str_Filter_Pdf") + "|*.pdf",
            DefaultExt = "pdf",
            FileName = stem
        };
        if (outputDialog.ShowDialog(this) != true) return;

        CommitActiveTextBox();
        byte[] templateBytes;
        PdfDataMergePlan plan;
        PdfDataMergeOutputMode outputMode;
        string? snapshot = null;
        int skippedPictures = 0;
        try
        {
            SavedCopy templateCopy = CreateOutputSnapshot();
            snapshot = templateCopy.Path;
            skippedPictures = templateCopy.SkippedPictures;
            templateBytes = File.ReadAllBytes(snapshot);
            var template = PdfDocument.Open(templateBytes);
            var settings = new DataMergeDialog(this, template, records, Path.GetFileName(outputDialog.FileName));
            if (settings.ShowDialog() != true || settings.Profile is null) return;
            plan = PdfDataMergeWorkflow.CreatePlan(template, records, Path.GetFileName(outputDialog.FileName), settings.Profile);
            outputMode = settings.OutputMode;
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DataMerge_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { if (snapshot is not null) File.Delete(snapshot); }

        string outputDirectory = Path.GetDirectoryName(outputDialog.FileName)!;
        string[] destinations = plan.OutputFileNames.Select(name => Path.GetFullPath(Path.Combine(outputDirectory, name))).ToArray();
        if (destinations.Any(path => string.Equals(path, Path.GetFullPath(_currentFile), StringComparison.OrdinalIgnoreCase)
            || (_originalFile is not null && string.Equals(path, Path.GetFullPath(_originalFile), StringComparison.OrdinalIgnoreCase))))
        {
            KillerDialog.Show(this, Loc("Str_Merge_SourceOutput")); return;
        }
        if (destinations.Length == 0) { KillerDialog.Show(this, Loc("Str_Repair_NoChanges")); return; }
        var approvedOverwrites = destinations.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int collisions = plan.OutputFileNames.Count(name =>
            File.Exists(Path.Combine(outputDirectory, name)));
        if (collisions > 0 && KillerDialog.Show(this,
                string.Format(Loc("Str_DataMerge_Overwrite"), collisions), "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        string previewText = string.Join("\n", plan.Profile.Mappings.Select(mapping => mapping.SourceField + " > " + mapping.TargetField))
            + "\n" + string.Join("\n", plan.Profile.ImageMappings.Select(mapping => mapping.SourceField + " > "
                + Loc("Str_Repair_Figure") + " (" + RepairPageLabel(mapping.PageIndex) + ")"))
            + "\n\n" + string.Join("\n", destinations);
        var review = new ImportReviewDialog(this, Loc("Str_DocumentTools_DataMerge"), previewText, null, Loc("Str_Btn_Run"));
        if (review.ShowDialog() != true) return;

        var busy = ShowBusyOverlay(Loc("Str_DataMerge_Processing"));
        CancellationToken token = BeginCancellableOp(Loc("Str_DocumentTools_DataMerge"));
        try
        {
            PdfDataMergeBatchReport report = await Task.Run(() => PdfDataMergeWorkflow.RunToDirectory(
                templateBytes, plan, outputDirectory, outputMode, token,
                value => PdfDataMergeWorkflow.LoadImage(Path.GetFullPath(value, Path.GetDirectoryName(dataDialog.FileName)!)),
                approvedOverwrites));

            HideBusyOverlay(busy);
            SetStatus(token.IsCancellationRequested
                ? Loc("Str_DataMerge_Canceled")
                : Services.PicturesSkippedNote.Append(string.Format(Loc("Str_DataMerge_Complete"),
                    report.SucceededRecords, report.FailedRecords), skippedPictures, Loc("Str_Save_PicturesSkipped")));
            new DocumentReportDialog(this, Loc("Str_DocumentTools_DataMerge"),
                report.ToText()).ShowDialog();
        }
        catch (OperationCanceledException)
        {
            HideBusyOverlay(busy);
            SetStatus(Loc("Str_DataMerge_Canceled"));
        }
        catch (Exception ex)
        {
            HideBusyOverlay(busy);
            KillerDialog.Show(this, Loc("Str_DataMerge_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndCancellableOp(token);
        }
    }

    private void InsertTableOfContents()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            var initialPlan = PdfEngineIntegration.PlanTableOfContents(_currentFile, 6, _protection);
            if (initialPlan.Entries.Count == 0)
            {
                KillerDialog.Show(this, Loc("Str_DocumentTools_TocNeedsBookmarks"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new TableOfContentsDialog(this, initialPlan.Entries.Count);
            if (dialog.ShowDialog() != true) return;

            var plan = PdfEngineIntegration.PlanTableOfContents(_currentFile, dialog.MaximumDepth, _protection);
            const int rowsPerPage = 40;
            int insertedPages = Math.Max(1, (plan.Entries.Count + rowsPerPage - 1) / rowsPerPage);
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            var annotationBackup = _annotations.ToDictionary(pair => pair.Key, pair => pair.Value);
            try
            {
                PageAnnotationInsertion.Shift(_annotations, 0, insertedPages);
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path =>
                        PdfEngineIntegration.InsertTableOfContents(
                            path, dialog.ContentsTitle, dialog.MaximumDepth),
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageInsertion(
                            rotations, 0, insertedPages),
                    selectedPageAfterReload: 0,
                    documentUndo: documentUndo);
            }
            catch
            {
                _annotations.Clear();
                foreach (KeyValuePair<int, List<PageAnnotation>> pair in annotationBackup)
                {
                    foreach (PageAnnotation annotation in pair.Value)
                        annotation.PageIndex = pair.Key;
                    _annotations[pair.Key] = pair.Value;
                }
                throw;
            }
            SetStatus(string.Format(Loc("Str_DocumentTools_TocInserted"), insertedPages));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_TocFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditLayers()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            var layers = PdfEngineIntegration.InspectLayers(_currentFile, _protection);
            if (layers.Groups.Count == 0)
            {
                KillerDialog.Show(this, Loc("Str_DocumentTools_NoLayers"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new LayerEditorDialog(this, layers);
            if (dialog.ShowDialog() != true) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                    PdfEngineIntegration.ApplyLayerEdits(path, dialog.Edits),
                documentUndo: documentUndo);
            SetStatus(Loc("Str_DocumentTools_LayersUpdated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_LayersFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditInitialView()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            var information = KillerPdf.Engine.Documents.PdfDocumentInformation.Read(
                EnsureEngineDocumentSession().Document);
            var dialog = new InitialViewDialog(
                this, information.InitialView, information.PageCount);
            if (dialog.ShowDialog() != true || dialog.InitialView is null) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                    PdfEngineIntegration.ApplyInitialView(path, dialog.InitialView),
                documentUndo: documentUndo);
            SetStatus(Loc("Str_InitialView_Updated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_InitialView_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditPortfolio()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            PdfEngineIntegration.PortfolioEditorState state =
                PdfEngineIntegration.ReadPortfolioEditorState(_currentFile, _protection);
            var dialog = new PortfolioEditorDialog(
                this, state.Collection,
                [.. state.Attachments.Select(attachment => attachment.FileName)]);
            if (dialog.ShowDialog() != true) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                {
                    if (dialog.RemoveMetadata)
                        PdfEngineIntegration.ClearPortfolioMetadata(path);
                    else
                        PdfEngineIntegration.ApplyPortfolioPresentation(
                            path, dialog.View, dialog.InitialDocument);
                },
                documentUndo: documentUndo);
            SetStatus(Loc("Str_Portfolio_Updated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_Portfolio_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditPortfolioStructure()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            PdfEngineIntegration.PortfolioEditorState state =
                PdfEngineIntegration.ReadPortfolioEditorState(_currentFile, _protection);
            var dialog = new PortfolioStructureDialog(
                this, state.Collection, state.Attachments);
            if (dialog.ShowDialog() != true) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path => PdfEngineIntegration.ApplyPortfolioStructure(
                    path, dialog.Fields, dialog.Sort, dialog.Folders, dialog.ItemValues),
                documentUndo: documentUndo);
            SetStatus(Loc("Str_Portfolio_StructureUpdated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_Portfolio_StructureFailed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditAttachments()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            IReadOnlyList<PdfEngineIntegration.AttachmentEditorItem> attachments =
                PdfEngineIntegration.ReadAttachmentEditorItems(_currentFile, _protection);
            if (attachments.Count == 0)
            {
                KillerDialog.Show(this, Loc("Str_Attachment_None"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new AttachmentEditorDialog(this, attachments);
            if (dialog.ShowDialog() != true) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                    PdfEngineIntegration.ApplyAttachmentEdits(path, dialog.Edits),
                documentUndo: documentUndo);
            RefreshSidebarPanel();   // the panel lists the files, so it must not keep showing a removed one
            SetStatus(Loc("Str_Attachment_Updated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_Attachment_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AuditNavigation()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            IReadOnlyList<PdfNavigationFinding> findings =
                PdfEngineIntegration.InspectNavigation(_currentFile, _protection);
            bool hasUnsafeLinks = findings.Any(finding =>
                finding.Code == PdfNavigationFindingCode.LinkUnsafeUri);
            bool hasUnresolvedLinks = findings.Any(finding =>
                finding.Code == PdfNavigationFindingCode.LinkUnresolvedDestination);
            var dialog = new NavigationRepairDialog(
                this, PdfEngineIntegration.FormatNavigationAudit(_currentFile, _protection),
                hasUnsafeLinks, hasUnresolvedLinks);
            if (dialog.ShowDialog() != true) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path => PdfEngineIntegration.RepairNavigationLinks(
                    path, dialog.RemoveUnsafeLinks, dialog.RemoveUnresolvedLinks),
                documentUndo: documentUndo);
            SetStatus(Loc("Str_Navigation_Updated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_Navigation_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GenerateBookmarks()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            var detection = new BookmarkDetectionDialog(this);
            if (detection.ShowDialog() != true || detection.Options is null) return;
            IReadOnlyList<PdfBookmarkProposal> proposals =
                PdfEngineIntegration.DetectBookmarkHeadings(_currentFile, detection.Options, _protection);
            if (proposals.Count == 0)
            {
                KillerDialog.Show(this, Loc(PdfEngineIntegration.HasTextLayer(_currentFile, _protection)
                        ? "Str_AutoBookmarks_NoneFound" : "Str_AutoBookmarks_NoText"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var review = new BookmarkReviewDialog(this, proposals);
            if (review.ShowDialog() != true) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                    PdfEngineIntegration.ApplyBookmarkProposals(path, review.Proposals),
                documentUndo: documentUndo);
            SetStatus(string.Format(Loc("Str_AutoBookmarks_Added"),
                review.Proposals.Count(item =>
                    item.Decision == PdfBookmarkProposalDecision.Accepted)));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_AutoBookmarks_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditPageLabels()
    {
        if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        try
        {
            int pageCount = PdfDocumentInformation.Read(
                EnsureEngineDocumentSession().Document).PageCount;
            var dialog = new PageLabelsDialog(this, pageCount);
            if (dialog.ShowDialog() != true) return;
            if (!EnsureEditable()) return;
            UndoEntry? documentUndo = CaptureDocumentUndo();
            SaveTempAndReload(
                keepAnnotations: true,
                finalizeSavedFile: path =>
                    PdfEngineIntegration.ApplyPageLabels(path, dialog.Ranges),
                documentUndo: documentUndo);
            SetStatus(Loc("Str_PageLabels_Updated"));
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_PageLabels_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowStructuralComparison()
    {
        // A page-text line still open in either pane (focus parked in the text settings bar keeps
        // it uncommitted) is written to the backing file first, so the report compares what is on
        // screen rather than the bytes from before the edit (BF-U010).
        Viewer.CommitActiveTextBoxExt();
        ViewerB.CommitActiveTextBoxExt();
        string? left = Viewer.CurrentFilePathExt;
        string? right = ViewerB.CurrentFilePathExt;
        if (!IsSplit || string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_ComparisonNeedsTwo"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            string report = PdfEngineIntegration.CompareStructure(
                left, Viewer.ProtectionRef, right, ViewerB.ProtectionRef).ToText();
            new DocumentReportDialog(this,
                Loc("Str_DocumentTools_StructuralComparison"), report).ShowDialog();
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // A report reads the tab's file under the tab's protection, so a read-only original reads too.
    private void ShowDocumentReport(string title, Func<string, OpenedProtection, string> createReport)
    {
        if (_doc is null)
        {
            KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
            return;
        }

        CommitActiveTextBox();
        string? path = _currentFile ?? _originalFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_Failed"),
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            new DocumentReportDialog(this, title, createReport(path, _protection)).ShowDialog();
        }
        catch (Exception ex)
        {
            KillerDialog.Show(this, Loc("Str_DocumentTools_Failed") + "\n" + ex.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
