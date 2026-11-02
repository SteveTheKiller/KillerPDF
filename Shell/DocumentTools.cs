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
    // The Document tools menu, built for both the Save menu's submenu and the Ctrl+Shift+D menu that
    // opens at the Save button. Each tool shows its chord in the gesture column, and pressing the
    // tool's letter while either menu is open runs it. The letters live in ShortcutTable, which the
    // shortcuts overlay reads too, so the three cannot disagree.
    private List<Control> BuildDocumentToolsItems()
    {
        if (_doc is null)
            return
            [
                DocumentToolItem("Str_DocumentTools_Macros", OpenMacroDialog, "\uE945"),
                DocumentToolItem("Str_CertCatalog_Title", OpenCertificateCatalog, "\uEB95"),
            ];
        var comparison = DocumentToolItem("Str_DocumentTools_StructuralComparison",
            ShowStructuralComparison, "\uE8AB");
        comparison.IsEnabled = IsSplit
            && !string.IsNullOrWhiteSpace(Viewer.CurrentFilePathExt)
            && !string.IsNullOrWhiteSpace(ViewerB.CurrentFilePathExt);
        return
        [
            DocumentToolItem("Str_DocumentTools_Preflight", OpenPreflightTools, "\uE9D9"),
            DocumentToolItem("Str_DocumentTools_PrintProduction", () => ShowDocumentReport(Loc("Str_DocumentTools_PrintProduction"),
                (path, protection) => PdfEngineIntegration.InspectPrintProduction(path, protection).ToText()), "\uE7C3"),
            DocumentToolItem("Str_DocumentTools_Separations", ShowSeparationPreview, "\uE790"),
            DocumentToolItem("Str_DocumentTools_Accessibility", OpenAccessibilityTools, "\uE776"),
            DocumentToolItem("Str_DocumentTools_Signatures", () => ShowDocumentReport(Loc("Str_DocumentTools_Signatures"),
                (path, protection) => PdfEngineIntegration.InspectSignatures(path, protection).ToText()), "\uE8D7"),
            DocumentToolItem("Str_CertCatalog_Title", OpenCertificateCatalog, "\uEB95"),
            DocumentToolItem("Str_DocumentTools_Layers", () => ShowDocumentReport(Loc("Str_DocumentTools_Layers"),
                (path, protection) => PdfEngineIntegration.InspectLayers(path, protection).ToText()), "\uE8A1"),
            DocumentToolItem("Str_DocumentTools_EditLayers", EditLayers, "\uE70F"),
            DocumentToolItem("Str_DocumentTools_Portfolio", () => ShowDocumentReport(Loc("Str_DocumentTools_Portfolio"),
                PdfEngineIntegration.InspectPortfolio), "\uE8B7"),
            DocumentToolItem("Str_DocumentTools_EditPortfolio", EditPortfolio, "\uE70F"),
            DocumentToolItem("Str_DocumentTools_EditPortfolioStructure", EditPortfolioStructure, "\uE70F"),
            DocumentToolItem("Str_DocumentTools_EditAttachments", EditAttachments, "\uE8B7"),
            DocumentToolItem("Str_DocumentTools_Comments", EditComments, "\uE90A"),
            DocumentToolItem("Str_DocumentTools_Measurements", ShowMeasurementReport, "\uED5E"),
            DocumentToolItem("Str_DocumentTools_NavigationAudit", AuditNavigation, "\uE9D9"),
            DocumentToolItem("Str_DocumentTools_AutoBookmarks", GenerateBookmarks, "\uE8FD"),
            DocumentToolItem("Str_DocumentTools_PageLabels", EditPageLabels, "\uE70F"),
            DocumentToolItem("Str_DocumentTools_InitialView", EditInitialView, "\uE7B3"),
            comparison,
            new Separator(),
            DocumentToolItem("Str_Guided_Title", OpenGuidedTasks, "\uE945"),
            DocumentToolItem("Str_DocumentTools_Macros", OpenMacroDialog, "\uE945"),
            DocumentToolItem("Str_DocumentTools_DataMerge", RunDataMerge, "\uE8F1"),
            DocumentToolItem("Str_DocumentTools_FormData", OpenFormDataTools, "\uE8A5"),
            DocumentToolItem("Str_DocumentTools_BookmarkData", OpenBookmarkDataTools, "\uE8A4"),
            DocumentToolItem("Str_DocumentTools_ProductionLayouts", OpenProductionTools, "\uE7C3"),
            DocumentToolItem("Str_DocumentTools_InsertToc", InsertTableOfContents, "\uE8FD"),
        ];
    }

    private MenuItem DocumentToolItem(string labelKey, Action run, string glyph)
    {
        var item = MakeMenuItem(Loc(labelKey), (_, _) => run(),
            GestureFor("CtrlShift:D") + ", " + ShortcutTable.DocumentToolKey(labelKey), glyph);
        item.Tag = ShortcutTable.DocumentToolKey(labelKey);
        return item;
    }

    private MenuItem BuildDocumentToolsMenu()
    {
        var menu = new MenuItem
        {
            Header = Loc("Str_DocumentTools"),
            InputGestureText = GestureFor("CtrlShift:D"),
            Icon = new Glyph
            {
                Text = "\uE9D2",
                FontFamily = UiKit.IconFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        foreach (Control item in BuildDocumentToolsItems())
            menu.Items.Add(item);
        menu.PreviewKeyDown += DocumentToolsMenu_PreviewKeyDown;
        return menu;
    }

    // Ctrl+Shift+D: the Document tools as their own menu, anchored to the Save menu button they
    // normally sit under. With that button folded away, the menu centers on the window instead.
    private void OpenDocumentToolsMenu()
    {
        var menu = MakeThemedMenu();
        foreach (Control item in BuildDocumentToolsItems())
            menu.Items.Add(item);
        menu.PreviewKeyDown += DocumentToolsMenu_PreviewKeyDown;
        if (SaveMenuBtn.IsVisible)
        {
            menu.PlacementTarget = SaveMenuBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
        else
        {
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
        }
        menu.Opened += (_, _) => menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsEnabled)?.Focus();
        menu.IsOpen = true;
    }

    // A letter key while a Document tools menu is open runs the tool with that letter. Shift and
    // Ctrl may still be held from the chord; Alt is left to the menu's own handling.
    private static void DocumentToolsMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ItemsControl owner || e.Key < Key.A || e.Key > Key.Z
            || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            return;
        char letter = (char)('A' + (e.Key - Key.A));
        MenuItem? match = owner.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag is char c && c == letter);
        if (match is null) return;
        e.Handled = true;
        if (!match.IsEnabled) return;
        // Close the whole menu first, the way a click does, then run the tool.
        DependencyObject? node = owner;
        while (node is not null and not System.Windows.Controls.ContextMenu)
            node = LogicalTreeHelper.GetParent(node);
        if (node is System.Windows.Controls.ContextMenu root) root.IsOpen = false;
        match.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, match));
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
        try
        {
            snapshot = CreateOutputSnapshot();
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
                : string.Format(Loc("Str_DataMerge_Complete"),
                    report.SucceededRecords, report.FailedRecords));
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
