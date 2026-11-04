using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class TableExportSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(string appPath, string appXaml, string folder, bool officeExport = false, bool grainThemes = false, bool dialogText = false, bool sidebarActions = false, bool paragraphEdit = false, bool accessibilityReview = false, bool formFieldEditing = false, bool guidedTasks = false, bool signedCopy = false, bool renderRecovery = false, bool toolbarAvailability = false, bool windowLifecycle = false, bool windowSession = false, string windowSessionCase = "--verify-window-session", string? updateRestartState = null, bool referenceRepairNotice = false, bool reductionRepairNotice = false)
    {
        bool visibleNative = windowSession && (windowSessionCase is "--verify-native-reading" or "--verify-native-reading-continuous" or "--verify-native-reading-grid" or "--verify-native-reading-grid-switch" or "--verify-native-reading-epub" or "--verify-native-reading-epub-continuous" or "--verify-native-reading-epub-grid-switch" or "--verify-shortcut-hints"
            || windowSessionCase.StartsWith("--verify-page-navigation-", StringComparison.Ordinal));
        if (visibleNative && !new[] { "SN01", "SN02" }.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Visible native hint acceptance may run only on SN01 or SN02.");
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke")
            && !(windowSession && Environment.GetCommandLineArgs().Contains("--session-isolation")))
            throw new InvalidOperationException("Startup smoke isolation is required.");
        if (Directory.Exists(folder)) throw new IOException("Use a fresh evidence directory.");
        Directory.CreateDirectory(folder);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        if (accessibilityReview)
        {
            _ = assembly.GetType("KillerPDF.AccessibilityReviewDialog", true);
        }
        Type dialogType = assembly.GetType("KillerPDF.TableExportDialog", true)!;
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, updateRestartState ?? Path.Combine(folder, "data"));
        if (!visibleNative && (accessibilityReview || formFieldEditing || guidedTasks || signedCopy || renderRecovery || toolbarAvailability || windowLifecycle || windowSession))
            AccessibilityReviewSmoke.ConfigureOffscreen(assembly);
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        var hostType = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TableExportSmokeHost"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Host")
            .DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        hostType.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder startup = hostType.DefineMethod("OnStartup",
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        hostType.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        if (windowSessionCase.StartsWith("--verify-session-ending", StringComparison.Ordinal))
        {
            MethodBuilder sessionEnding = hostType.DefineMethod("SimulateSessionEnding", MethodAttributes.Public,
                typeof(void), [typeof(SessionEndingCancelEventArgs)]);
            ILGenerator body = sessionEnding.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldarg_1);
            body.Emit(OpCodes.Call, appType.GetMethod("OnSessionEnding", Members)!);
            body.Emit(OpCodes.Ret);
        }
        var app = (Application)Activator.CreateInstance(hostType.CreateType()!)!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
        foreach (XAttribute declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        string assemblyName = assembly.GetName().Name!;
        // Whole attribute values: "clr-namespace:KillerPDF" is also the start of KillerPDF.Controls.
        foreach (string name in new[] { "KillerPDF", "KillerPDF.Controls", "KillerFamily.Layout" })
            xaml = xaml.Replace("\"clr-namespace:" + name + "\"", "\"clr-namespace:" + name + ";assembly=" + assemblyName + "\"", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/" + assemblyName + ";component/") });
        if (!visibleNative && (accessibilityReview || formFieldEditing || guidedTasks || signedCopy || renderRecovery || toolbarAvailability || windowLifecycle || windowSession))
        {
            var hiddenMessages = new Style(typeof(Window));
            hiddenMessages.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
            hiddenMessages.Setters.Add(new Setter(Window.ShowInTaskbarProperty, false));
            hiddenMessages.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
            app.Resources[typeof(Window)] = hiddenMessages;
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Window? owner = null;
        try
        {
            // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
            assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
            foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + service, true)!.GetMethod("Initialize", Members)!.Invoke(null, null);
            if (visibleNative)
            {
                // This isolated host suppresses startup. Initialize the real presentation preference as startup does.
                var read = appType.GetMethod("GetSetting", Members)!.CreateDelegate<Func<string, string?>>();
                var save = appType.GetMethod("SetSetting", Members)!.CreateDelegate<Action<string, string>>();
                assembly.GetType("KillerFamily.Layout.ShortcutHints", true)!.GetMethod("Initialize")!.Invoke(null, [read, save]);
            }
            bool consumeUpdate = windowSessionCase == "--verify-update-restart-consume";
            if (consumeUpdate)
                appType.GetMethod("LoadPendingUpdateRestart", Members)!.Invoke(null, null);
            if (windowSession && !consumeUpdate)
            {
                MethodInfo setting = appType.GetMethod("SetSetting", Members)!;
                setting.Invoke(null, ["RememberChoiceLocked", "1"]);
                setting.Invoke(null, ["RememberOpenFiles", windowSessionCase.StartsWith("--verify-page-navigation-sidebar-", StringComparison.Ordinal) ? "0" : "1"]);
                setting.Invoke(null, ["CheckForUpdatesOnStartup", "0"]);
                setting.Invoke(null, ["CloseToTray", "0"]);
            }
            if (windowSessionCase == "--verify-update-empty-recents")
                StartupLayoutSmoke.PrepareRecent(assembly, appPath, Path.Combine(folder, "data"), folder);
            owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            if (visibleNative)
            {
                owner.Left = SystemParameters.WorkArea.Left + 32;
                owner.Top = SystemParameters.WorkArea.Top + 32;
                owner.Width = Math.Min(1100, SystemParameters.WorkArea.Width - 64);
                owner.Height = Math.Min(760, SystemParameters.WorkArea.Height - 64);
            }
            else if (!consumeUpdate) { owner.Left = -10000; owner.Top = -10000; }
            owner.ShowInTaskbar = false; owner.ShowActivated = visibleNative;
            owner.Show();
            if (windowSessionCase == "--verify-native-reading") return NativeReadingSmoke.Run(owner, appPath, folder);
            if (windowSessionCase == "--verify-native-reading-epub") return NativeReadingSmoke.Run(owner, appPath, folder, epub: true);
            if (windowSessionCase == "--verify-native-reading-epub-continuous") return NativeReadingSmoke.Run(owner, appPath, folder, continuous: true, epub: true);
            if (windowSessionCase == "--verify-native-reading-epub-grid-switch") return NativeReadingSmoke.Run(owner, appPath, folder, grid: true, gridSwitch: true, epub: true);
            if (windowSessionCase == "--verify-native-reading-continuous") return NativeReadingSmoke.Run(owner, appPath, folder, continuous: true);
            if (windowSessionCase == "--verify-native-reading-grid") return NativeReadingSmoke.Run(owner, appPath, folder, grid: true);
            if (windowSessionCase == "--verify-native-reading-grid-switch") return NativeReadingSmoke.Run(owner, appPath, folder, grid: true, gridSwitch: true);
            if (windowSessionCase == "--verify-about-balanced") return StartupLayoutSmoke.About(owner, folder);
            if (windowSessionCase == "--verify-update-empty-recents") return StartupLayoutSmoke.Recent(owner, folder);
            if (windowSessionCase == "--verify-update-restart-stage")
                return UpdateRestartSmoke.Stage(owner, assembly, appPath, folder, updateRestartState!);
            if (windowSessionCase == "--verify-update-restart-early-exit")
                return UpdateRestartSmoke.Stage(owner, assembly, appPath, folder, updateRestartState!, earlyExit: true);
            if (consumeUpdate)
                return UpdateRestartSmoke.Consume(owner, assembly, appPath, folder, updateRestartState!);
            if (windowSessionCase == "--verify-session-ending-controls")
                return SessionEndingControlsSmoke.Run(owner, assembly, appPath, folder);
            if (windowSessionCase.StartsWith("--verify-session-ending", StringComparison.Ordinal))
                return SessionEndingSmoke.Run(owner, assembly, appPath, folder, windowSessionCase);
            if (windowSessionCase == "--verify-update-window-state")
                return UpdateWindowSmoke.Run(owner, assembly, appPath, folder);
            if (windowSessionCase == "--verify-print-visibility")
                return PrintVisibilitySmoke.Run(owner, appPath, folder);
            if (windowSessionCase == "--verify-window-collection")
                return WindowCollectionSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-layer-isolation")
                return LayerStateSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-validate-profile")
                return MacroProfileSmoke.Run(owner, folder);
            if (windowSessionCase.StartsWith("--verify-validate-profile-case-", StringComparison.Ordinal))
                return MacroProfileSmoke.RunCase(owner, folder, windowSessionCase);
            if (windowSessionCase == "--verify-form-format")
                return FormCalculationSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-form-status")
                return FormCalculationSmoke.RunStatus(owner, folder);
            if (windowSessionCase == "--verify-form-total")
                return FormCalculationSmoke.RunTotal(owner, folder);
            if (windowSessionCase == "--verify-delete-nearest")
                return PageDeletionSmoke.Run(owner, folder);
            if (windowSessionCase.StartsWith("--verify-delete-nearest-case-", StringComparison.Ordinal))
                return PageDeletionSmoke.Run(owner, folder, windowSessionCase["--verify-delete-nearest-case-".Length..]);
            if (windowSessionCase == "--verify-watch-readiness")
                return WatchReadinessSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-watch-job-failure")
                return WatchReadinessSmoke.Run(owner, folder, failedJob: true);
            if (windowSessionCase == "--verify-watch-unsupported-step")
                return WatchReadinessSmoke.Run(owner, folder, unsupportedStep: true);
            if (windowSessionCase == "--verify-paragraph-refusal")
                return ParagraphRefusalSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-search-prerequisites")
                return ToolSearchPrerequisitesSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-search-certificates")
                return ToolSearchPrerequisitesSmoke.Run(owner, folder, certificates: true);
            if (windowSessionCase == "--verify-search-scan")
                return ToolSearchPrerequisitesSmoke.Run(owner, folder, creation: "scan");
            if (windowSessionCase == "--verify-search-office")
                return ToolSearchPrerequisitesSmoke.Run(owner, folder, creation: "office");
            if (windowSessionCase == "--verify-shortcut-hints")
                return ShortcutHintsSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation")
                return PageNavigationSmoke.Run(owner, folder);
            if (windowSessionCase is "--verify-page-navigation-options" or "--verify-page-navigation-restored")
                return PageNavigationOptionsSmoke.Run(owner, folder, updateRestartState ?? Path.Combine(folder, "data"),
                    restored: windowSessionCase == "--verify-page-navigation-restored");
            if (windowSessionCase == "--verify-page-navigation-modes")
                return PageNavigationModesSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-sidebar")
                return PageNavigationSidebarSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-box-selection")
                return ThumbnailBoxSelectionSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-box-controls")
                return ThumbnailBoxSelectionSmoke.Run(owner, folder, controls: true);
            if (windowSessionCase == "--verify-page-navigation-box-gutter")
                return ThumbnailBoxGutterSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-box-scroll")
                return ThumbnailBoxGutterSmoke.Run(owner, folder, scroll: true);
            if (windowSessionCase == "--verify-page-navigation-box-strip")
                return ThumbnailBoxGutterSmoke.Run(owner, folder, scroll: true, strip: true);
            if (windowSessionCase == "--verify-page-navigation-box-scale")
                return ThumbnailBoxGutterSmoke.Run(owner, folder, scroll: true, maximumScale: true);
            if (windowSessionCase == "--verify-page-navigation-box-actions")
                return ThumbnailBoxGutterSmoke.Run(owner, folder, scroll: true, actions: true);
            if (windowSessionCase == "--verify-page-navigation-box-switch")
                return ThumbnailBoxGutterSmoke.Run(owner, folder, scroll: true, strip: true, switchDocument: true);
            if (windowSessionCase == "--verify-page-navigation-drag-controls")
                return ThumbnailDragControlsSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-document-scrollbar")
                return DocumentScrollbarSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-picker-navigation-recovery")
                return PickerNavigationSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-document-scrollbar-variants")
                return DocumentScrollbarSmoke.RunVariants(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-tooltip-cursor")
                return TooltipCursorSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-tooltip-matrix")
                return TooltipCursorSmoke.RunMatrix(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-tooltip-placement-controls")
                return TooltipCursorSmoke.RunPlacementControls(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-caption-theme")
                return CaptionThemeSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-orientation-write-failure")
                return OrientationFailureSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-certificate-restriction")
                return CertificateRestrictionSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-restricted-form-fill")
                return RestrictedFormFillSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-form-workflow-failure")
                return FormWorkflowFailureSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-form-delete-recreate")
                return FormWorkflowFailureSmoke.Run(owner, folder, deletion: true);
            if (windowSessionCase == "--verify-page-navigation-replace-write-failure")
                return ReplacementFailureSmoke.Run(owner, folder, reopen: false);
            if (windowSessionCase == "--verify-page-navigation-replace-reopen-failure")
                return ReplacementFailureSmoke.Run(owner, folder, reopen: true);
            if (windowSessionCase == "--verify-page-navigation-replace-fallback-history")
                return ReplacementFailureSmoke.RunFallback(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-replace-occurrence")
                return ReplacementOccurrenceSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-replace-normalized-occurrence")
                return ReplacementOccurrenceSmoke.Run(owner, folder, normalized: true);
            if (windowSessionCase == "--verify-page-navigation-current-search")
                return CurrentDocumentSearchSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-current-search-fields")
                return CurrentSearchFieldsSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-form-replacement-limits")
                return FormReplacementRulesSmoke.Run(owner, folder, calculation: false);
            if (windowSessionCase == "--verify-page-navigation-form-replacement-calculation")
                return FormReplacementRulesSmoke.Run(owner, folder, calculation: true);
            if (windowSessionCase == "--verify-page-navigation-certified-form-save")
                return CertifiedFormSaveSmoke.Run(owner, appPath, folder);
            if (windowSessionCase == "--verify-page-navigation-print-copies")
                return PrintMemorySmoke.Run(owner, appPath, folder, 10, copies: 2, subset: true);
            if (windowSessionCase.StartsWith("--verify-page-navigation-print-memory-", StringComparison.Ordinal))
                return PrintMemorySmoke.Run(owner, appPath, folder, int.Parse(windowSessionCase["--verify-page-navigation-print-memory-".Length..]));
            if (windowSessionCase == "--verify-page-navigation-cli-image-publication")
                return CliOutputSmoke.Run(appPath, folder, merge: false);
            if (windowSessionCase == "--verify-page-navigation-cli-merge-input")
                return CliOutputSmoke.Run(appPath, folder, merge: true);
            if (windowSessionCase == "--verify-page-navigation-cli-plate-names")
                return CliOutputSmoke.RunPlates(appPath, folder);
            if (windowSessionCase == "--verify-page-navigation-cli-password-orientation")
                return CliOutputSmoke.RunOrientation(appPath, folder);
            if (windowSessionCase == "--verify-page-navigation-cli-replacement-outcomes")
                return CliReplacementSmoke.Run(appPath, folder);
            if (windowSessionCase == "--verify-page-navigation-physical-output")
                return PhysicalOutputSmoke.Run(appPath, folder);
            if (windowSessionCase == "--verify-page-navigation-physical-transform")
                return PhysicalTransformSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-preview-failure")
                return PreviewFailureSmoke.Run(owner, appPath, folder);
            if (windowSessionCase == "--verify-page-navigation-preview-com")
                return PreviewComSmoke.Run(owner, appPath, folder);
            if (windowSessionCase == "--verify-page-navigation-preview-explorer")
                return PreviewComSmoke.Run(owner, appPath, folder, explorer: true);
            if (windowSessionCase.StartsWith("--verify-page-navigation-sidebar-", StringComparison.Ordinal))
                return SidebarPersistenceSmoke.Run(owner, folder, windowSessionCase["--verify-page-navigation-sidebar-".Length..]);
            if (windowSessionCase == "--verify-page-navigation-image-overwrite-consent")
                return ImageExportWorkflowSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-image-pending-forms")
                return ImageExportWorkflowSmoke.RunForms(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-ocr-pending-snapshot")
                return ImageExportWorkflowSmoke.RunForms(owner, folder, ocr: true);
            if (windowSessionCase == "--verify-page-navigation-ocr-native-searchable")
                return OcrOutputSmoke.Run(owner, folder, convert: false);
            if (windowSessionCase == "--verify-page-navigation-ocr-native-convert")
                return OcrOutputSmoke.Run(owner, folder, convert: true);
            if (windowSessionCase == "--verify-page-navigation-ocr-native-cancel")
                return OcrOutputSmoke.Run(owner, folder, convert: false, cancel: true);
            if (windowSessionCase == "--verify-page-navigation-other-pane-save")
                return OtherPaneSaveSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-dialog-buttons-theme")
                return CaptionThemeSmoke.RunButtonTransitions(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-dialog-button-actions")
                return CaptionThemeSmoke.RunButtonTransitions(owner, folder, invokeActions: true);
            if (windowSessionCase == "--verify-page-navigation-caption-frame-theme")
                return CaptionThemeSmoke.Run(owner, folder, frameGeometry: true);
            if (windowSessionCase == "--verify-page-navigation-caption-close")
                return CaptionThemeSmoke.RunCloseControls(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-caption-pointer-close")
                return CaptionThemeSmoke.RunCloseControls(owner, folder, nativePointer: true);
            if (windowSessionCase == "--verify-page-navigation-caption-keyboard-close")
                return CaptionThemeSmoke.RunCloseControls(owner, folder, nativeKeyboard: true);
            if (windowSessionCase == "--verify-page-navigation-caption-frame-grain")
                return CaptionThemeSmoke.Run(owner, folder, frameGeometry: true, grainCheck: true);
            if (windowSessionCase == "--verify-page-navigation-tooltip-large-cursor")
                return TooltipCursorSmoke.RunLargeCursor(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-transitions")
                return PageNavigationTransitionsSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-layout")
                return PageNavigationLayoutSmoke.Run(owner, folder);
            if (windowSessionCase == "--verify-page-navigation-toolbar")
                return PageNavigationLayoutSmoke.Run(owner, folder, verifyToolbar: true);
            if (windowSessionCase == "--verify-page-navigation-toolbar-menus")
                return PageNavigationLayoutSmoke.Run(owner, folder, verifyToolbar: true, verifyMenus: true);
            if (windowSessionCase.StartsWith("--verify-page-navigation-focus-", StringComparison.Ordinal))
                return PageNavigationModesSmoke.Run(owner, folder, focusArm: windowSessionCase["--verify-page-navigation-focus-".Length..]);
            if (windowSessionCase.StartsWith("--verify-page-straighten-", StringComparison.Ordinal))
                return DocumentStateSmoke.Run(owner, assembly, appPath, folder, windowSessionCase);
            if (windowSessionCase.StartsWith("--verify-file-insertion-", StringComparison.Ordinal))
                return DocumentStateSmoke.Run(owner, assembly, appPath, folder, windowSessionCase);
            if (windowSessionCase is "--verify-page-reorder-failure" or "--verify-page-transform-form-failure")
                return DocumentStateSmoke.Run(owner, assembly, appPath, folder, windowSessionCase);
            if (windowSessionCase is "--verify-page-delete-failure" or "--verify-page-transform-failure" or "--verify-page-transform-cancel" or "--verify-page-transform-save-control" or "--verify-page-transform-success" or "--verify-range-extract-pending" or "--verify-page-straighten-failure")
                return DocumentStateSmoke.Run(owner, assembly, appPath, folder, windowSessionCase);
            if (windowSessionCase is "--verify-export-loss-review" or "--verify-export-loss-mixed" or "--verify-export-loss-continue" or "--verify-export-lossless")
                return ExportLossReviewSmoke.Run(owner, folder, windowSessionCase);
            if (windowSessionCase is "--verify-save-history" or "--verify-active-editor-close" or "--verify-annotation-history" or "--verify-form-history" or "--verify-stamp-isolation" or "--verify-failed-save-history" or "--verify-stamp-history" or "--verify-stamp-tabs" or "--verify-stamp-panes" or "--verify-stamp-tearout" or "--verify-page-edit-failure" or "--verify-layout-pending" or "--verify-merge-pending" or "--verify-layouts-pending" or "--verify-stamp-saveall" or "--verify-active-editor-close-b")
                return DocumentStateSmoke.Run(owner, assembly, appPath, folder, windowSessionCase);
            if (windowSession) return WindowSessionSmoke.Run(owner, assembly, appPath, folder, windowSessionCase);
            if (windowLifecycle) return WindowLifecycleSmoke.Run(owner, assembly, appPath, folder);
            if (toolbarAvailability) return ToolbarAvailabilitySmoke.Run(owner, appPath, folder);
            if (renderRecovery) return RenderRecoverySmoke.Run(owner, appPath, folder);
            if (referenceRepairNotice) return ReferenceRepairNoticeSmoke.Run(owner, assembly, appPath, folder);
            if (reductionRepairNotice) return ReductionRepairNoticeSmoke.Run(owner, assembly, appPath, folder);
            if (signedCopy) return SignedCopySmoke.Run(owner, assembly, appPath, folder);
            if (guidedTasks) return GuidedTasksSmoke.Run(owner, assembly, appPath, folder);
            if (formFieldEditing) return FormFieldEditingSmoke.Run(owner, appPath, folder);
            if (accessibilityReview) return AccessibilityReviewSmoke.Run(owner, assembly, appPath, folder);
            if (paragraphEdit) return ParagraphEditSmoke.Run(owner, assembly, appPath, folder);
            if (sidebarActions) return SidebarActionsSmoke.Run(owner, folder);
            if (dialogText) return DialogTextSmoke.Run(owner, assembly, folder);
            if (grainThemes) return GrainThemeSmoke.Run(owner, assembly, folder);
            VerifySidebarContextMenu(owner);
            if (officeExport) return OfficeExportSmoke.Run(owner, assembly, appPath, folder);
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            byte[] fixture = Fixture();
            string originalHash = Convert.ToHexString(SHA256.HashData(fixture));
            Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
            object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(fixture)])!;
            object renderer = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Rendering.PdfPageRenderer", true)!, [document, null])!;
            object options = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Rendering.PdfRenderOptions", true)!,
                [400, 400, false, false, false, 512L * 1024 * 1024])!;
            object rendered = renderer.GetType().GetMethod("Render")!.Invoke(renderer, [0, options, CancellationToken.None])!;
            byte[] pixels = ((ReadOnlyMemory<byte>)rendered.GetType().GetProperty("Pixels")!.GetValue(rendered)!).ToArray();
            var preview = BitmapSource.Create(400, 400, 96, 96, PixelFormats.Bgra32, null, pixels, 1600);
            preview.Freeze();
            Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
            MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", Members)!;
            int count = 0;
            foreach (string theme in Enum.GetNames(themeType))
            {
                apply.Invoke(null, [Enum.Parse(themeType, theme)]);
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    var dialog = (Window)Activator.CreateInstance(dialogType, Members, null,
                        [owner, document, 0, preview, 0, scale], null)!;
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Left = -10000; dialog.Top = -10000; dialog.ShowInTaskbar = false; dialog.ShowActivated = false;
                    try
                    {
                        dialog.Show();
                        Pump();
                        if (scale == 1)
                        {
                            Capture(dialog, Path.Combine(folder, theme + "-initial.png"));
                            if (dialog.ActualWidth > 700 || dialog.ActualHeight > 650)
                                throw new InvalidOperationException("Initial table dialog has excessive empty space.");
                            FrameworkElement[] actions = [Find<ComboBox>(dialog, "TableExportFormat"),
                                Find<Button>(dialog, "TableExportCancel"), Find<Button>(dialog, "TableExportSave")];
                            Rect[] bounds = actions.Select(control => control.TransformToAncestor(dialog).TransformBounds(new Rect(control.RenderSize))).ToArray();
                            if (bounds.Max(rect => rect.Top) - bounds.Min(rect => rect.Top) > 1
                                || bounds.Max(rect => rect.Bottom) - bounds.Min(rect => rect.Bottom) > 1)
                                throw new InvalidOperationException("Export format and buttons are not aligned.");
                        }
                        dialog.Width = scale == 1 ? 900 : 640; dialog.Height = scale == 1 ? 740 : 480;
                        Pump();
                        Button export = Find<Button>(dialog, "TableExportSave");
                        if (export.IsEnabled) throw new InvalidOperationException("Export must wait for a reviewed table.");
                        Find<TextBox>(dialog, "TableAreaLeft").Text = "101";
                        Click(Find<Button>(dialog, "TableDetect"));
                        if (export.IsEnabled) throw new InvalidOperationException("Invalid selection enabled export.");
                        Find<TextBox>(dialog, "TableAreaLeft").Text = "0";
                        Click(Find<Button>(dialog, "TableDetect"));
                        Wait(() => export.IsEnabled);
                        TextBox cell = Find<TextBox>(dialog, "TableCell_0_0");
                        if (cell.Text != "0012") throw new InvalidOperationException("Identifier was not preserved.");
                        cell.Text = "=reviewed";
                        Capture(dialog, Path.Combine(folder, theme + "-" + scale + ".png"));
                        Click(export);
                        object table = dialogType.GetProperty("Result", Members)!.GetValue(dialog)
                            ?? throw new InvalidOperationException("Accepted review did not return a table.");
                        Type formatType = engine.GetType("KillerPdf.Engine.Documents.PdfTableExportFormat", true)!;
                        MethodInfo serialize = engine.GetType("KillerPdf.Engine.Documents.PdfTableExport", true)!.GetMethod("Export")!;
                        foreach (string format in new[] { "Xlsx", "Csv" })
                        {
                            byte[] output = (byte[])serialize.Invoke(null, [table, Enum.Parse(formatType, format), CancellationToken.None])!;
                            string target = Path.Combine(folder, theme + "-" + scale + "." + format.ToLowerInvariant());
                            Type atomic = assembly.GetType("KillerPDF.Services.AtomicFileOutput", true)!;
                            atomic.GetMethod("Write", Members, [typeof(string), typeof(ReadOnlyMemory<byte>), typeof(CancellationToken)])!
                                .Invoke(null, [target, new ReadOnlyMemory<byte>(output), CancellationToken.None]);
                            if (format == "Xlsx")
                            {
                                using var zip = ZipFile.OpenRead(target);
                                using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                                XDocument sheet = XDocument.Load(stream);
                                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                                if (sheet.Descendants(ns + "f").Any()
                                    || sheet.Descendants(ns + "c").First().Element(ns + "is")?.Element(ns + "t")?.Value != "=reviewed")
                                    throw new InvalidOperationException("Reviewed text was not saved safely to XLSX.");
                            }
                            else if (!File.ReadAllText(target).Contains("'=reviewed", StringComparison.Ordinal))
                                throw new InvalidOperationException("Reviewed CSV text was not neutralized.");
                        }
                        count++;
                    }
                    finally { dialog.Close(); }
                }
            }
            if (Convert.ToHexString(SHA256.HashData(fixture)) != originalHash)
                throw new InvalidOperationException("The source changed during export.");
            apply.Invoke(null, [Enum.Parse(themeType, "Dark")]);
            var failures = new List<string>();
            void Check(string name, Action action)
            {
                try { action(); File.WriteAllText(Path.Combine(folder, name + ".txt"), "PASS " + name); }
                catch (Exception error)
                {
                    failures.Add(name + ": " + error.Message);
                    File.WriteAllText(Path.Combine(folder, name + ".txt"), error.ToString());
                }
            }
            void LayoutCase(string name, double scale, int width, int height) =>
                Check(name, () =>
                {
                    var dialog = (Window)Activator.CreateInstance(dialogType, Members, null,
                        [owner, document, 0, preview, 0, scale], null)!;
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Left = -10000; dialog.Top = -10000; dialog.ShowInTaskbar = false; dialog.ShowActivated = false;
                    try
                    {
                        dialog.Show(); dialog.Width = width; dialog.Height = height; Pump();
                        Capture(dialog, Path.Combine(folder, name + ".png"));
                        AssertVisible(dialog, Find<Button>(dialog, "TableExportSave"));
                        AssertVisible(dialog, Find<Button>(dialog, "TableExportCancel"));
                        AssertVisible(dialog, Find<ComboBox>(dialog, "TableExportFormat"));
                        ScrollViewer scroll = Descendants<ScrollViewer>(dialog).Single(view => view.Content is StackPanel);
                        if (scroll.ViewportHeight < 24)
                            throw new InvalidOperationException($"Review viewport is only {scroll.ViewportHeight:F2} logical pixels high.");
                        Button detect = Find<Button>(dialog, "TableDetect");
                        detect.BringIntoView(); Pump(); AssertVisible(dialog, detect);
                        Click(detect); Wait(() => Find<Button>(dialog, "TableExportSave").IsEnabled);
                        TextBox firstCell = Find<TextBox>(dialog, "TableCell_0_0");
                        firstCell.BringIntoView(); Pump(); AssertVisible(dialog, firstCell);
                    }
                    finally { dialog.Close(); Pump(); }
                });
            LayoutCase("geometry-640x480", 1.5, 640, 480);
            LayoutCase("geometry-480x360", 2.5, 480, 360);
            Type localeType = assembly.GetType("KillerPDF.Services.Locale", true)!;
            MethodInfo applyLocale = assembly.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply", Members)!;
            try
            {
                foreach (string locale in Enum.GetNames(localeType))
                {
                    applyLocale.Invoke(null, [Enum.Parse(localeType, locale)]);
                    LayoutCase("locale-" + locale, 1.5, 640, 480);
                }
            }
            finally { applyLocale.Invoke(null, [Enum.Parse(localeType, "EnUS")]); }
            string sourcePath = Path.Combine(folder, "workflow-source.pdf");
            File.WriteAllBytes(sourcePath, fixture);
            Check("menu-to-xlsx", () => RunWorkflow(owner, sourcePath, Path.Combine(folder, "workflow.xlsx"), "Xlsx", FlowMode.Save));
            Check("menu-to-csv", () => RunWorkflow(owner, sourcePath, Path.Combine(folder, "workflow.csv"), "Csv", FlowMode.Save));
            Check("review-cancel", () => RunWorkflow(owner, sourcePath, Path.Combine(folder, "review-cancel.xlsx"), "Xlsx", FlowMode.CancelReview));
            Check("picker-cancel", () => RunWorkflow(owner, sourcePath, Path.Combine(folder, "picker-cancel.xlsx"), "Xlsx", FlowMode.CancelPicker));
            Check("source-switch", () => RunWorkflow(owner, sourcePath, Path.Combine(folder, "source-switch.xlsx"), "Xlsx", FlowMode.SwitchSource));
            string disguisedSource = Path.Combine(folder, "report.csv");
            File.WriteAllBytes(disguisedSource, fixture);
            Check("source-protection", () => RunWorkflow(owner, disguisedSource, disguisedSource, "Csv", FlowMode.ProtectSource));
            if (failures.Count > 0)
            {
                File.WriteAllLines(Path.Combine(folder, "failures.txt"), failures);
                throw new InvalidOperationException(string.Join("\n", failures));
            }
            string result = $"PASS {count} dialog cases, 2 geometry cases, 19 locale cases and 6 real menu-to-picker workflows; app SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            if (!app.Dispatcher.HasShutdownStarted)
            {
                var closing = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
                var accepted = new HashSet<Window>();
                closing.Tick += (_, _) =>
                {
                    string quit = (string)app.FindResource("Str_Btn_Quit");
                    string discard = (string)app.FindResource("Str_Btn_DontSave");
                    foreach (Window window in app.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                    {
                        Button? button = Descendants<Button>(window).FirstOrDefault(candidate => Equals(candidate.Content, quit)
                            || (formFieldEditing || guidedTasks || signedCopy || renderRecovery || toolbarAvailability
                                || accessibilityReview || paragraphEdit) && Equals(candidate.Content, discard));
                        if (button is not null && accepted.Add(window)) Click(button);
                    }
                };
                closing.Start();
                try { owner?.Close(); }
                finally { closing.Stop(); app.Shutdown(); }
            }
        }
    }

    private static void VerifySidebarContextMenu(Window owner)
    {
        FrameworkElement sidebar = FindName<FrameworkElement>(owner, "SidebarBorder");
        sidebar.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
            Environment.TickCount, System.Windows.Input.MouseButton.Right)
        { RoutedEvent = UIElement.MouseRightButtonUpEvent });
        Pump();
        ContextMenu menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<DependencyObject>()
            .SelectMany(Descendants<ContextMenu>).Single(candidate => candidate.IsOpen);
        try
        {
            if (menu.Placement != System.Windows.Controls.Primitives.PlacementMode.MousePoint)
                throw new InvalidOperationException("Sidebar right-click menu is not placed at the cursor.");
        }
        finally { menu.IsOpen = false; Pump(); }
    }

    private enum FlowMode { Save, CancelReview, CancelPicker, SwitchSource, ProtectSource }

    private static void RunWorkflow(Window owner, string source, string destination, string format, FlowMode mode)
    {
        byte[] original = File.ReadAllBytes(source);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        Pump();
        int stage = 0;
        bool done = false;
        Exception? failure = null;
        string? reportedError = null;
        var watch = Stopwatch.StartNew();
        var events = new List<string>();
        var handledMessages = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        void CloseTransientWindows()
        {
            foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray())
                window.Close();
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(45))
                    throw new TimeoutException($"Menu-to-save workflow timed out at stage {stage}.");
                Window[] windows = Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray();
                if (stage == 0)
                {
                    stage = 1; events.Add("Opened Save menu");
                    Click(FindName<Button>(owner, "SaveMenuBtn"));
                    return;
                }
                if (stage == 1)
                {
                    string label = (string)Application.Current.FindResource("Str_TableExport_Title");
                    MenuItem? item = PresentationSource.CurrentSources.Cast<PresentationSource>()
                        .Select(source => source.RootVisual).OfType<DependencyObject>()
                        .SelectMany(Descendants<MenuItem>).FirstOrDefault(candidate => Equals(candidate.Header, label));
                    if (item is null) return;
                    stage = 2; events.Add("Invoked rendered Export table menu item");
                    if (ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu) menu.IsOpen = false;
                    owner.Dispatcher.BeginInvoke(new Action(() => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))));
                    return;
                }
                Window? review = windows.FirstOrDefault(window => AutomationProperties.GetAutomationId(window) == "TableExportDialog");
                Window? picker = windows.FirstOrDefault(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                if (stage == 2 && review is not null)
                {
                    if (mode == FlowMode.CancelReview)
                    {
                        stage = 6; events.Add("Canceled table review"); Click(Find<Button>(review, "TableExportCancel"));
                    }
                    else
                    {
                        stage = 3; events.Add("Selected complete table area");
                        if (mode == FlowMode.SwitchSource)
                        {
                            string replacement = Path.Combine(Path.GetDirectoryName(destination)!, "replacement-source.pdf");
                            File.WriteAllBytes(replacement, original);
                            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [replacement]);
                            events.Add("Opened a different document during table review");
                        }
                        Find<TextBox>(review, "TableAreaLeft").Text = "5";
                        Find<TextBox>(review, "TableAreaTop").Text = "5";
                        Find<TextBox>(review, "TableAreaWidth").Text = "90";
                        Find<TextBox>(review, "TableAreaHeight").Text = "90";
                        Click(Find<Button>(review, "TableDetect"));
                    }
                    return;
                }
                if (stage == 3 && review is not null && Find<Button>(review, "TableExportSave").IsEnabled)
                {
                    if (Find<TextBox>(review, "TableCell_0_0").Text != "0012")
                        throw new InvalidOperationException("Real workflow lost the leading-zero identifier.");
                    Find<TextBox>(review, "TableCell_0_1").Text = "=workflow";
                    Find<ComboBox>(review, "TableExportFormat").SelectedIndex = format == "Csv" ? 1 : 0;
                    stage = 4; events.Add("Accepted corrected table review as " + format);
                    Click(Find<Button>(review, "TableExportSave"));
                    return;
                }
                if (stage == 4 && picker is not null)
                {
                    if (mode == FlowMode.SwitchSource)
                        throw new InvalidOperationException("A source switch during review still opened the save picker.");
                    FindName<TextBox>(picker, "FileNameBox").Text = destination;
                    if (mode == FlowMode.CancelPicker)
                    {
                        stage = 6; events.Add("Canceled save picker");
                        string cancel = (string)Application.Current.FindResource("Str_Btn_Cancel");
                        Click(Descendants<Button>(picker).Single(button => Equals(button.Content, cancel)));
                    }
                    else
                    {
                        stage = 5; events.Add("Accepted output destination " + destination);
                        owner.Dispatcher.BeginInvoke(new Action(() => Click(FindName<Button>(picker, "AcceptButton"))));
                    }
                    return;
                }
                if (stage == 4 && mode == FlowMode.SwitchSource && review is null && picker is null && owner.IsEnabled)
                {
                    done = true; timer.Stop(); return;
                }
                if (stage >= 5)
                {
                    Window? message = windows.FirstOrDefault(window => window != review && window != picker);
                    if (message is not null)
                    {
                        if (!handledMessages.Add(message)) return;
                        // The kit file picker asks "Replace", the app's own prompts "Yes".
                        string yes = (string)Application.Current.FindResource("Str_Btn_Yes");
                        string replace = (string)Application.Current.FindResource("Str_Btn_Replace");
                        Button? confirm = Descendants<Button>(message).FirstOrDefault(button => Equals(button.Content, yes) || Equals(button.Content, replace));
                        if (confirm is not null)
                        {
                            if (mode != FlowMode.ProtectSource) throw new InvalidOperationException("Unexpected overwrite confirmation.");
                            events.Add("Confirmed replacement of owned synthetic source fixture"); Click(confirm);
                        }
                        else
                        {
                            reportedError = string.Join("\n", Descendants<TextBlock>(message).Select(block => block.Text));
                            events.Add("Application message: " + reportedError);
                            Button? accept = Descendants<Button>(message).FirstOrDefault(button => button.IsDefault);
                            if (accept is not null) Click(accept); else message.Close();
                        }
                        return;
                    }
                    if (review is not null || picker is not null || !owner.IsEnabled) return;
                    if (mode == FlowMode.Save && !File.Exists(destination) && reportedError is null) return;
                    if (mode == FlowMode.ProtectSource && reportedError is null
                        && File.ReadAllBytes(source).AsSpan().SequenceEqual(original)) return;
                    done = true; timer.Stop();
                }
            }
            catch (Exception error)
            {
                failure = error; done = true; timer.Stop(); CloseTransientWindows();
            }
        };
        timer.Start();
        try
        {
            Wait(() => done, 50);
            if (failure is not null) throw failure;
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Export overwrote the source PDF bytes.");
            if (mode == FlowMode.ProtectSource)
            {
                string expected = (string)Application.Current.FindResource("Str_Merge_SourceOutput");
                if (reportedError?.Contains(expected, StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Source preservation was not accompanied by the expected source-protection error: " + reportedError);
            }
            else if (reportedError is not null) throw new InvalidOperationException(reportedError);
            else if (mode != FlowMode.Save)
            {
                if (File.Exists(destination)) throw new InvalidOperationException("Canceled export wrote an output file.");
            }
            else if (format == "Xlsx")
            {
                using var zip = ZipFile.OpenRead(destination);
                using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                XDocument sheet = XDocument.Load(stream);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                string[] cells = sheet.Descendants(ns + "c").Select(cell => cell.Element(ns + "is")?.Element(ns + "t")?.Value ?? "").ToArray();
                if (sheet.Descendants(ns + "f").Any() || !cells.SequenceEqual(new[] { "0012", "=workflow", "42", "Test" }))
                    throw new InvalidOperationException("Real XLSX output did not retain the reviewed cells as text.");
            }
            else
            {
                string text = File.ReadAllText(destination);
                if (!text.Contains("0012", StringComparison.Ordinal) || !text.Contains("'=workflow", StringComparison.Ordinal))
                    throw new InvalidOperationException("Real CSV output lost an identifier or formula neutralization.");
            }
        }
        finally
        {
            timer.Stop(); CloseTransientWindows(); Pump();
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(destination)!, "flow-" + mode + "-" + format + ".log"), events);
        }
    }

    private static T FindName<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).Single(element => element.Name == name);

    private static void AssertVisible(Window window, FrameworkElement element)
    {
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        var available = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0 || !available.Contains(bounds))
            throw new InvalidOperationException($"{AutomationProperties.GetAutomationId(element)} is outside the window: {bounds}; window {available}.");
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(element); ancestor is FrameworkElement parent && parent != window;
            ancestor = VisualTreeHelper.GetParent(parent))
        {
            Geometry? geometry = VisualTreeHelper.GetClip(parent);
            if (geometry is not null || parent.ClipToBounds)
            {
                Rect localClip = geometry?.Bounds ?? new Rect(parent.RenderSize);
                Rect clip = parent.TransformToAncestor(window).TransformBounds(localClip);
                // Fractional app scaling can round an edge by half a window DIP.
                clip.Inflate(0.5, 0.5);
                if (!clip.Contains(bounds))
                    throw new InvalidOperationException($"{AutomationProperties.GetAutomationId(element)} is clipped by {parent.GetType().Name}: {bounds}; clip {clip}.");
            }
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> condition, int timeoutSeconds = 20)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { Pump(); if (watch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) throw new TimeoutException("Table workflow did not become ready."); Thread.Sleep(10); }
    }
    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants<T>(root).Single(item => AutomationProperties.GetAutomationId(item) == id);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }
    internal static byte[] Fixture(bool malformedDate = false, bool sidewaysText = false)
    {
        string content = sidewaysText ? "BT /F1 12 Tf 0 1 -1 0 150 25 Tm (SIDEWAYS ORIENTATION CONTROL) Tj ET\n"
            : "20 20 160 160 re 100 20 m 100 180 l 20 100 m 180 100 l S\nBT /F1 12 Tf 30 150 Td (0012) Tj ET\nBT /F1 12 Tf 110 150 Td (Name) Tj ET\nBT /F1 12 Tf 30 70 Td (42) Tj ET\nBT /F1 12 Tf 110 70 Td (Test) Tj ET\n";
        List<string> objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        if (malformedDate) objects.Add("<< /CreationDate (NOT_A_DATE) >>");
        string info = malformedDate ? $" /Info {objects.Count} 0 R" : "";
        var pdf = new StringBuilder("%PDF-1.7\n"); var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length; pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R{info} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
