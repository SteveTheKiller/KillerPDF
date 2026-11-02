using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    // #456: when WPF's render thread fails the window goes black for good. App saves every
    // unsaved tab aside through here, restarts, and the new window reopens them unsaved.
    public partial class MainWindow
    {
        /// <summary>Lines the restart lists when it cannot read the recovery manifest.</summary>
        internal IReadOnlyList<string> RecoveryNotes { get; private set; } = [];

        /// <summary>
        /// Writes each unsaved document in every document window to one recovery folder and
        /// remembers the open tabs. Returns the manifest, or null when nothing needed saving.
        /// Nothing here draws, so it still works after the render thread has failed.
        /// </summary>
        internal string? PrepareRenderFailureRecovery()
        {
            var recovered = new List<RecoveredDocument>();
            string? folder = null;
            bool folderUnavailable = false;
            var unrecovered = new List<Controls.PdfViewer.DocumentSession>();
            MainWindow[] windows = Application.Current.Windows.OfType<MainWindow>().ToArray();
            foreach (MainWindow window in windows) CaptureWindow(window);

            void CaptureWindow(MainWindow window)
            {
                Controls.PdfViewer activeViewer = window.ActiveViewer;
                try
                {
                    foreach (Controls.PdfViewer pane in new[] { window.Viewer, window.ViewerB })
                    {
                        // One pane that cannot capture its live edits must not stop the other pane's
                        // unsaved documents from being written.
                        Controls.PdfViewer.PendingTextRecovery? pending = null;
                        try { pending = pane.CapturePendingTextForRecovery(); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashReporter.Capture(ex, "Recovery pending text"); }
                        var pendingSession = pane.ActiveSessionExt;
                        try { pane.CaptureActiveIfAny(); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashReporter.Capture(ex, "Recovery tab capture"); }
                        foreach (var session in pane.SessionsRef.Where(s => s.Doc is not null
                            && (s.IsDirty || pending is not null && ReferenceEquals(s, pendingSession))).ToList())
                        {
                            if (folder is null && !folderUnavailable)
                            {
                                folder = RenderFailureRecovery.CreateFolder(RenderFailureRecovery.FolderRoots(
                                    AppDataPaths.UserRoot, AppDataPaths.LocalRoot, Path.GetTempPath()));
                                folderUnavailable = folder is null;
                            }
                            if (folder is null)
                            {
                                unrecovered.Add(session);
                                continue;
                            }
                            // A protected document reads from a decrypted copy, which must not reach the
                            // recovery folder: it goes there under its own encryption or not at all. Its
                            // original is untouched on disk.
                            RecoveryRoute route = RenderFailureRecovery.Route(
                                session.Doc?.IsReadOnly == true, session.ProtectedSource, session.Protection);
                            if (route == RecoveryRoute.NotWritten)
                            {
                                unrecovered.Add(session);
                                continue;
                            }
                            string name = session.SuggestedName ?? Path.GetFileName(session.OriginalFile ?? "document.pdf");
                            string copy = RenderFailureRecovery.CopyPath(folder, name, recovered.Count);
                            string working = Path.ChangeExtension(copy, ".working.pdf");
                            string state = Path.ChangeExtension(copy, ".state.json");
                            var edit = ReferenceEquals(session, pendingSession) ? pending : null;
                            var backupErrors = new List<string>();
                            // Preserve what is available before attempting the full save pipeline. The
                            // working copy and the state file are plain, so a protected tab writes neither.
                            bool plain = route == RecoveryRoute.Plain;
                            if (plain)
                                try { session.Doc!.Save(working); }
                                catch (Exception ex) when (ex is not OutOfMemoryException) { backupErrors.Add(ex.Message); }
                            window.ActiveViewer = pane;
                            try
                            {
                                pane.WithSessionLive(session, () =>
                                {
                                    if (plain)
                                        try { window.WriteRecoveryState(state, session, edit); }
                                        catch (Exception ex) when (ex is not OutOfMemoryException) { backupErrors.Add(ex.Message); }
                                    string snapshot = pane.WriteRecoverySnapshot(edit, () => window.WriteSavedCopyToTemp());
                                    RenderFailureRecovery.WriteCopy(snapshot, copy, route, session.Protection);
                                });
                                recovered.Add(new RecoveredDocument(session.OriginalFile, copy, session.SuggestedName,
                                    session.LoadedRevision, Protected: session.ProtectedSource));
                                // The unedited working copy and state exist only for a failed copy; beside a
                                // complete one they could be opened by mistake.
                                foreach (string backup in new[] { working, state })
                                    try { File.Delete(backup); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException && !plain)
                            {
                                CrashReporter.Capture(ex, "Recovery of a protected document");
                                unrecovered.Add(session);
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                App.RetainRenderRecoveryTemps(null);
                                backupErrors.Insert(0, ex.Message);
                                recovered.Add(new RecoveredDocument(session.OriginalFile, working, session.SuggestedName,
                                    session.LoadedRevision, string.Join("\n", backupErrors), File.Exists(state) ? state : null));
                            }
                        }
                    }
                }
                finally { window.ActiveViewer = activeViewer; }
            }
            if (folderUnavailable)
            {
                // Nowhere to write copies: keep the session's working files for a manual recovery.
                App.RetainRenderRecoveryTemps(null);
                CrashReporter.Capture(new IOException("No recovery folder could be created."), "Render recovery folder");
            }

            // The snapshots are written, so a line being edited can give the tool its own look back. The
            // settings saved next would otherwise keep the page's ink as the default for new text.
            foreach (MainWindow window in windows)
            foreach (Controls.PdfViewer pane in new[] { window.Viewer, window.ViewerB })
                try { pane.RestoreTextToolDefaultsForExit(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { CrashReporter.Capture(ex, "Recovery text defaults"); }

            // The open tabs are remembered first, so a restart with only clean tabs reopens them
            // instead of the set left by the last normal close.
            try { SaveWindowSettings(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { CrashReporter.Capture(ex, "Recovery session settings"); }

            if (unrecovered.Count > 0)
            {
                // With nowhere to write copies, the restart must not report that the documents simply
                // reopened. Their lines go to the restarted process on its command line, which needs no
                // disk. A manifest in the retained session folder also lists them when it can be
                // written, and either way the restart points at the session's working files there.
                string untitled = Loc("Str_Outline_Untitled"), noFolder = Loc("Str_Dlg_RenderRecoveryNoFolder");
                RecoveryNotes = [.. unrecovered.Select(session =>
                    RenderFailureRecovery.LostNote(session.OriginalFile, session.SuggestedName, untitled, noFolder))];
                List<RecoveredDocument> lost = [.. unrecovered.Select((session, index) => new RecoveredDocument(
                    session.OriginalFile, Path.Combine(App.TempDir, $"unrecovered-{index + 1:00}.pdf"),
                    RenderFailureRecovery.LostName(session.OriginalFile, session.SuggestedName, untitled),
                    session.LoadedRevision, noFolder))];
                string unavailable = Path.Combine(App.TempDir, "recovery.json");
                try { unavailable = RenderFailureRecovery.WriteManifest(App.TempDir, lost); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { CrashReporter.Capture(ex, "Render recovery manifest"); }
                App.RetainRenderRecoveryTemps(unavailable);
                return unavailable;
            }
            if (folder is null || recovered.Count == 0) return null;
            // If the manifest cannot be written, for example because the copies filled the disk, the
            // restart still names the copies and points at the folder that holds them. They cannot
            // reopen without it, so their originals stay in the restored tabs.
            RecoveryNotes = [.. recovered.Select(document => Path.GetFileName(document.RecoveredPath))];
            string manifest;
            try { manifest = RenderFailureRecovery.WriteManifest(folder, recovered); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CrashReporter.Capture(ex, "Render recovery manifest");
                string missing = Path.Combine(folder, "recovery.json");
                App.RetainRenderRecoveryTemps(missing);
                return missing;
            }
            if (recovered.Any(document => document.RecoveryError is not null)) App.RetainRenderRecoveryTemps(manifest);

            // Recovered tabs come back from the manifest, so once it is written their originals must
            // not also reopen beside them through the normal session restore.
            try
            {
                var originals = new HashSet<string>(recovered.Select(d => d.OriginalPath ?? string.Empty), StringComparer.OrdinalIgnoreCase);
                foreach (string key in new[] { "OpenTabs", "OpenTabsB" })
                    if (App.GetSetting(key) is { Length: > 0 } saved)
                    {
                        string[] keep = [.. saved.Split('|').Where(path => !originals.Contains(path))];
                        if (keep.Length > 0) App.SetSetting(key, string.Join("|", keep)); else App.RemoveSetting(key);
                    }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { CrashReporter.Capture(ex, "Recovery session settings"); }
            return manifest;
        }

        private void WriteRecoveryState(string path, Controls.PdfViewer.DocumentSession session,
            Controls.PdfViewer.PendingTextRecovery? pending)
        {
            var options = new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            AtomicFileOutput.Write(path, JsonSerializer.SerializeToUtf8Bytes(new
            {
                session.OriginalFile, session.CurrentFile, session.SuggestedName, session.LoadedRevision,
                PendingText = pending,
                Annotations = session.Annotations.SelectMany(pair => pair.Value.Select(annotation => new
                {
                    Type = annotation.GetType().Name,
                    Value = JsonSerializer.SerializeToElement(annotation, annotation.GetType(), options)
                })),
                RenderDimensions = session.RenderDims.Select(pair => new { Page = pair.Key, Width = pair.Value.w, Height = pair.Value.h }),
                session.PageRotations, session.FormTextValues, session.FormChoiceValues,
                session.FormMultiChoiceValues, session.FormCheckValues, session.FormRadioValues,
                session.FormFontSizes, session.RedactionMarks,
                Stamps = JsonSerializer.SerializeToElement(_docStampSpec, new JsonSerializerOptions { IncludeFields = true })
            }, options));
        }

        /// <summary>
        /// Lab check for the recovery path, which a real failure (sleep, driver reset) cannot be
        /// summoned for: with KILLERPDF_SIMULATE_RENDER_FAILURE=1 the window raises the genuine
        /// render-thread error five seconds after it opens. Never set in normal use.
        /// </summary>
        private void ArmSimulatedRenderFailure()
        {
            if (App.RestartedAfterRenderFailure
                || Environment.GetEnvironmentVariable("KILLERPDF_SIMULATE_RENDER_FAILURE") != "1") return;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                throw new System.Runtime.InteropServices.COMException("UCEERR_RENDERTHREADFAILURE (simulated)",
                    RenderFailureRecovery.RenderThreadFailureHResult);
            };
            timer.Start();
        }

        // Protected copies whose open has not finished, by the path they open from.
        private static readonly Dictionary<string, RecoveredDocument> ProtectedRecoveries =
            new(StringComparer.OrdinalIgnoreCase);

        // The copy holds the edits; saving goes back to the file they were made to. A partial working
        // copy must be reviewed and saved separately, and a protected original takes only a tab that
        // still holds its protection, so nothing plain can land on it.
        private void ReturnRecoveredTabHome(RecoveredDocument document)
        {
            bool hasHome = document.RecoveryError is null && document.OriginalPath is { Length: > 0 } original
                && Directory.Exists(Path.GetDirectoryName(original))
                && RenderFailureRecovery.MayReturnHome(document, _protection);
            _originalFile = hasHome ? document.OriginalPath : null;
            _suggestedName = hasHome ? null : document.SuggestedName ?? Path.GetFileName(document.RecoveredPath);
            _loadedRevision = hasHome ? document.LoadedRevision : null;
            _publishedFile = null;
            FileNameLabel.Text = Path.GetFileName(document.OriginalPath ?? _suggestedName ?? document.RecoveredPath);
            MarkDirty(true);
            ActiveViewer.CaptureActiveIfAny();
        }

        // Called where a protected open finishes, so a recovered copy gets the tab state an open cannot give it.
        private void CompleteProtectedRecovery(string openedPath)
        {
            if (!ProtectedRecoveries.Remove(openedPath, out RecoveredDocument? document)) return;
            ReturnRecoveredTabHome(document);
            RebuildTabStrip();
        }

        /// <summary>Reopens documents recovered after a render failure as unsaved changes to their files.</summary>
        private void OpenRenderRecovery()
        {
            ArmSimulatedRenderFailure();
            if (!App.RestartedAfterRenderFailure) return;
            App.RestartedAfterRenderFailure = false;
            string? manifest = App.RenderRecoveryManifest;
            IReadOnlyList<RecoveredDocument> documents = manifest is null ? [] : RenderFailureRecovery.ReadManifest(manifest);
            var incomplete = new List<string>();
            int reopened = 0;
            if (manifest is not null && documents.Count == 0)
            {
                // Without a readable manifest, the documents are still listed from the command line.
                if (App.RenderRecoveryNotes.Count > 0) incomplete.AddRange(App.RenderRecoveryNotes);
                else incomplete.Add(Path.GetFileName(manifest));
            }
            foreach (RecoveredDocument document in documents)
            {
                // One line per document: a copy already listed with its errors is not listed again.
                string name = RenderFailureRecovery.DocumentName(document);
                if (document.RecoveryError is not null) incomplete.Add(RenderFailureRecovery.ProblemNote(document));
                try
                {
                    if (!File.Exists(document.RecoveredPath))
                    {
                        if (document.RecoveryError is null) incomplete.Add(name);
                        continue;
                    }
                    // A protected copy opens like any protected file, on a background thread or after
                    // its password, so it finds its way home when that open finishes.
                    if (document.Protected) ProtectedRecoveries[document.RecoveredPath] = document;
                    ActiveViewer.OpenInNewTabExt(document.RecoveredPath);
                    if (document.Protected)
                    {
                        reopened++;
                        continue;
                    }
                    if (_doc is null || !string.Equals(_originalFile, document.RecoveredPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (document.RecoveryError is null) incomplete.Add(name);
                        continue;
                    }
                    ReturnRecoveredTabHome(document);
                    reopened++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (document.RecoveryError is null) incomplete.Add(name + ": " + ex.Message);
                }
            }
            RebuildTabStrip();
            // With nothing reopened there are no recovered copies to review. Each document keeps its
            // own line, even when two share a name.
            KillerDialog.Show(this, incomplete.Count > 0
                    ? string.Format(Loc(reopened > 0 ? "Str_Dlg_RenderRecoveryIncomplete" : "Str_Dlg_RenderRecoveryNotReopened"),
                        RenderFailureRecovery.JoinNotes(incomplete), Path.GetDirectoryName(manifest))
                    : reopened > 0
                    ? string.Format(Loc("Str_Dlg_RenderRecovered"), Path.GetDirectoryName(manifest))
                    : Loc("Str_Dlg_RenderRestarted"),
                Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK,
                incomplete.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
    }
}
