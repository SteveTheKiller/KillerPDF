using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>Open, Save or Folder. Picked at construction; changes the accept button and the
    /// rules. Folder hides the name and type fields, labels the button Select, and returns a
    /// folder as FileName.</summary>
    public enum FileDialogMode { Open, Save, Folder }

    /// <summary>
    /// Themed stand-in for Microsoft.Win32.OpenFileDialog / SaveFileDialog. Same chrome, places
    /// rail, view modes and sortable columns as FolderPickerDialog (row styles shared from
    /// Controls.xaml), plus a file name box and a filter combo.
    ///
    /// The property surface mirrors the Win32 dialogs on purpose - Title, Filter, FilterIndex,
    /// FileName, InitialDirectory, DefaultExt, AddExtension, OverwritePrompt, CheckFileExists -
    /// so adopting it at a call site is a one-word change:
    ///
    ///     var dlg = new FileDialog(FileDialogMode.Save) { Title = ..., Filter = ..., FileName = ... };
    ///     if (dlg.ShowDialog(owner) == true) Use(dlg.FileName);
    ///
    /// Multiselect matches Explorer's Ctrl and Shift selection behavior in Open mode.
    /// </summary>
    public partial class FileDialog : Window
    {
        // ── Win32-compatible surface ─────────────────────────────────────────────

        /// <summary>Win32 filter syntax: "Desc|*.a;*.b|Other|*.c". Empty means every file.</summary>
        public string Filter { get; set; } = "";

        /// <summary>1-based, like the Win32 dialogs. Out of range is clamped.</summary>
        public int FilterIndex { get; set; } = 1;

        /// <summary>Seeded with a suggested name; on OK, the full chosen path.</summary>
        public string FileName { get; set; } = "";

        public string InitialDirectory { get; set; } = "";

        /// <summary>Appended on save when the typed name has no extension. No leading dot needed.</summary>
        public string DefaultExt { get; set; } = "";

        public bool AddExtension { get; set; } = true;

        /// <summary>Keeps the seeded name fixed while the user picks only the folder: the name box
        /// is read only, picking a file does not replace it, and a type switch leaves it alone.
        /// Off by default.</summary>
        public bool IsFileNameReadOnly { get; set; }
        /// <summary>
        /// Append the active filter's extension even when the typed name contains another dot.
        /// Use this for format-locked exports such as flattened PDF output.
        /// </summary>
        public bool RequireFilterExtension { get; set; }

        /// <summary>Save mode: confirm before replacing an existing file.</summary>
        public bool OverwritePrompt { get; set; } = true;

        /// <summary>Open mode: refuse to return a path that does not exist.</summary>
        public bool CheckFileExists { get; set; } = true;

        /// <summary>Refuse a path whose DIRECTORY does not exist. On by default, matching the
        /// Win32 dialogs - the picker never creates folder trees on the user's behalf, so turning
        /// this off only means the caller has agreed to handle a missing directory itself.</summary>
        public bool CheckPathExists { get; set; } = true;

        /// <summary>Open mode only: let the user pick several files at once (Ctrl/Shift click, or
        /// a drag over the list). Ignored in Save mode, where "several names" is meaningless.
        /// Set it BEFORE ShowDialog - the list's selection mode is applied there.</summary>
        public bool Multiselect { get; set; }

        /// <summary>Folder mode only: list folders alone instead of folders and the files that
        /// pass the filter. Off by default.</summary>
        public bool HideFilesInFolderMode { get; set; }

        /// <summary>Folder mode only: Explorer-style picking. A highlighted subfolder is the answer
        /// (otherwise the folder being shown), Enter anywhere accepts, and the chosen folder is
        /// remembered as the next starting point. Off by default: the folder being shown is the
        /// answer and nothing is remembered.</summary>
        public bool AcceptHighlightedFolder { get; set; }

        /// <summary>Show a live preview pane for image-selection workflows. The pane is opt-in so
        /// ordinary Open and Save dialogs keep their compact layout.</summary>
        public bool ShowImagePreview { get; set; }

        /// <summary>The kit's older name for <see cref="ShowImagePreview"/>, kept for its callers.</summary>
        public bool ShowPreview { get => ShowImagePreview; set => ShowImagePreview = value; }

        /// <summary>Every path chosen. Always populated on success, so a caller can read this
        /// whether or not it asked for Multiselect - single selection yields one entry, matching
        /// the Win32 dialogs' FileNames. FileName remains the first of them.</summary>
        public string[] FileNames { get; private set; } = [];

        /// <summary>True when the files were accepted with Shift held (Shift+Enter, or Shift and the Open button) in
        /// Open mode: the caller opens them in a new window instead of the one that asked (KillerPDF #513).</summary>
        public bool OpenInNewWindow { get; private set; }

        // ── internals ────────────────────────────────────────────────────────────

        private readonly FileDialogMode _mode;

        public ObservableCollection<PickerPlace> Places  { get; } = [];
        public ObservableCollection<PickerEntry> Entries { get; } = [];

        private readonly List<PickerEntry> _raw = [];
        private string _currentDir = string.Empty;
        private readonly List<string> _startCandidates = new List<string>();

        /// <summary>The existence checks the accept rules use. They run on a background thread
        /// under a timeout, so a dead network path cannot freeze the dialog.</summary>
        public Func<string, bool> FolderExistsProbe { get; set; } = Directory.Exists;
        public Func<string, bool> FileExistsProbe { get; set; } = File.Exists;

        private string? NextStartCandidate()
        {
            if (_startCandidates.Count == 0) return null;
            string next = _startCandidates[0];
            _startCandidates.RemoveAt(0);
            return next;
        }

        /// <summary>A folder remembered from an earlier run, or null when it cannot name a folder at all
        /// (empty, relative, or holding characters no path can carry). Whether it still exists is the
        /// listing's question.</summary>
        internal static string? UsableRemembered(string? remembered)
        {
            if (string.IsNullOrWhiteSpace(remembered)) return null;
            return IsMalformedTyped(remembered!) || !IsFullyQualified(remembered!) ? null : remembered;
        }

        /// <summary>A start folder the caller seeded: it must name a folder outright (a drive or share
        /// path), not one relative to the current directory.</summary>
        internal static bool IsUsableSeedDirectory(string? path) => UsableRemembered(path) != null;

        /// <summary>The folders that exist, each probed on its own worker and under its own timeout, so one
        /// dead network entry does not hide the healthy ones.</summary>
        internal static async Task<List<string>> ExistingFoldersAsync(IEnumerable<string> folders, Func<string, bool> probe, TimeSpan timeout)
        {
            var all = folders.ToList();
            var checks = all.Select(async folder =>
            {
                try { return await OnStaWorker(() => probe(folder), timeout, ProbeKey(probe, folder)); }
                catch (Exception) { return false; }
            }).ToList();
            bool[] exists = await Task.WhenAll(checks);
            return all.Where((_, index) => exists[index]).ToList();
        }

        private static string ProbeKey(Func<string, bool> probe, string path) =>
            probe.Method.DeclaringType?.FullName + "." + probe.Method.Name + ":" + path;

        private enum Presence { No, Yes, Unknown }

        /// <summary>Whether a path exists, as a probe answers it. A probe that throws or times out has not
        /// answered: that is Unknown, with the reason in the system's words, never a quiet "no".</summary>
        private async Task<(Presence State, string Reason)> CheckAsync(Func<string, bool> probe, string path)
        {
            if (string.IsNullOrEmpty(path)) return (Presence.No, "");
            try { return (await OnStaWorker(() => probe(path), ProbeTimeout, ProbeKey(probe, path)) ? Presence.Yes : Presence.No, ""); }
            catch (TimeoutException) { return (Presence.Unknown, new System.ComponentModel.Win32Exception(1460).Message); }
            catch (Exception error) { return (Presence.Unknown, error.Message); }
        }

        private async Task<bool> ProbeAsync(Func<string, bool> probe, string path)
            => (await CheckAsync(probe, path)).State == Presence.Yes;

        /// <summary>Whether the dialog is closing or closed. Work that resumes after an await checks this
        /// first: a cancel or a close that landed meanwhile must not become an accept, a navigation or a
        /// prompt owned by a window that is gone.</summary>
        private bool _closing;

        /// <summary>A typed name or path, resolved against the folder shown. "C:shot.png" and
        /// "\shot.png" are rooted but not fully qualified; they must not fall back on the
        /// process's current directory.</summary>
        private string ResolveTyped(string typed)
        {
            // Written by hand: .NET Framework apps share this file and lack the two-argument GetFullPath.
            if (typed.Length == 0 || _currentDir.Length == 0) return typed;
            if (typed.StartsWith(@"\\") || typed.StartsWith("//")) return typed;
            string combined = typed;
            try
            {
                if (typed.Length >= 2 && char.IsLetter(typed[0]) && typed[1] == ':')
                {
                    if (typed.Length >= 3 && (typed[2] == '\\' || typed[2] == '/')) return typed;
                    // "C:shot.png": the shown folder when it is on that drive, otherwise that drive's root.
                    bool sameDrive = _currentDir.Length >= 2 && _currentDir[1] == ':'
                        && char.ToUpperInvariant(_currentDir[0]) == char.ToUpperInvariant(typed[0]);
                    combined = Path.Combine(sameDrive ? _currentDir : typed.Substring(0, 2) + @"\", typed.Substring(2));
                }
                else if (typed[0] == '\\' || typed[0] == '/')
                    combined = Path.Combine(Path.GetPathRoot(_currentDir) ?? _currentDir, typed.TrimStart('\\', '/'));
                else combined = Path.Combine(_currentDir, typed);
                return Path.GetFullPath(combined);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { return combined; }
        }

        /// <summary>Whether typed text holds a character or a name length no path can carry. The
        /// checks run before the text is combined with the folder, because Path.Combine throws on
        /// these on .NET Framework.</summary>
        internal static bool IsMalformedTyped(string typed)
        {
            string rest = typed.StartsWith(@"\\?\") ? typed.Substring(4) : typed;
            for (int index = 0; index < rest.Length; index++)
            {
                char c = rest[index];
                if (c < ' ' || c == '"' || c == '<' || c == '>' || c == '|') return true;
                if (c == ':' && !(index == 1 && char.IsLetter(rest[0]))) return true;
            }
            foreach (string part in rest.Split('\\', '/'))
                if (part.Length > 255) return true;
            return false;
        }

        /// <summary>The names in quoted multiselect text such as "a.txt" "b.txt", or null when the
        /// text is not made only of quoted names.</summary>
        private static string[]? QuotedNames(string typed)
        {
            if (typed.IndexOf('"') < 0) return null;
            var matches = Regex.Matches(typed, "\"([^\"]*)\"");
            if (matches.Count == 0 || Regex.Replace(typed, "\"[^\"]*\"", "").Trim().Length != 0) return null;
            return matches.Cast<Match>().Select(x => x.Groups[1].Value).Where(x => x.Length > 0).ToArray();
        }

        private string _selectionText = "";   // what the name box showed for the highlighted file(s)

        /// <summary>The name box text for highlighted files: the one name, or a quoted list of several.</summary>
        internal static string SelectionNameText(IReadOnlyList<string> fileNames) =>
            fileNames.Count == 1 ? fileNames[0] : string.Join(" ", fileNames.Select(name => "\"" + name + "\""));

        /// <summary>True while the name box is empty or still shows what the highlighted files put there.</summary>
        internal static bool SelectionAnswers(string typed, string selectionText)
        {
            string t = typed.Trim();
            return t.Length == 0 || string.Equals(t, selectionText, StringComparison.Ordinal);
        }

        private static bool IsFullyQualified(string path)
        {
            if (path.StartsWith(@"\\") || path.StartsWith("//")) return true;
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        }

        private void RejectTyped()
        {
            SelMeta.Text = Loc("Str_Dlg_InvalidPath");
            FileNameBox.Focus();
        }
        private bool _navigating;
        private bool _built;                 // suppresses filter events during construction
        private int  _viewMode;              // 0 list, 1 icons, 2 details
        private int  _sortKey;               // 0 name, 1 size, 2 modified
        private bool _sortAsc = true;
        private int _imagePreviewGeneration;

        private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
        };

        // Per-filter-entry patterns, parallel to FilterCombo's items. Empty list = show all.
        private readonly List<string[]> _filterPatterns = [];
        private int _declaredFilters;   // how many entries of the combo are the caller's own

        private static readonly string ArrowUp   = ((char)0xE70E).ToString();
        private static readonly string ArrowDown = ((char)0xE70D).ToString();

        // ── Tree / pinned places / recents / hidden state ────────────────────────
        public ObservableCollection<FolderNode> TreeRoots { get; } = [];
        private bool _treeSyncing;   // tree selection navigates, navigation selects: no ping-pong
        private bool _showHidden;

        private const string ShowHiddenKey = "FileDlgShowHidden";
        private const string RecentsKey    = "FileDlgRecents";
        private const string PinnedKey     = "FileDlgPinned";
        private const string PlacesHKey    = "FileDlgPlacesH";
        private const string LastOpenKey   = "FileDlgLastOpenDir";
        private const string LastSaveKey   = "FileDlgLastSaveDir";
        private const string SortKeyKey    = "FileDlgSortKey";
        private const string SortAscKey    = "FileDlgSortAsc";

        // Image pickers (every caller with ShowImagePreview) remember their own folder, separate
        // from the document open/save memory. One shared key meant Insert Image always started
        // wherever the last PDF was opened from, never where the user last picked an image.
        private string LastDirKey =>
            (_mode == FileDialogMode.Open ? LastOpenKey : LastSaveKey) + (ShowImagePreview ? "Img" : "");
        private const int    RecentsMax    = 12;

        // Guards the fade-then-close re-entry below. Without it OnClosing would cancel forever.
        private bool _fadingOut;

        /// <summary>The result Accept wants, held until the window is actually allowed to close.
        /// Null means cancel (the X, Escape, the Cancel button - none of them set it).</summary>
        private bool? _pendingResult;

        /// <summary>Fades the dialog out before it actually closes. The first pass cancels the
        /// close and runs the fade; the second sees the flag and lets it through.
        ///
        /// The result CANNOT be assigned before the fade. Assigning Window.DialogResult is itself a
        /// close request, so it lands in this handler, which cancels that close - and WPF resets
        /// DialogResult to null whenever a close is canceled. Accept therefore records what it
        /// wants in _pendingResult and the assignment happens in the fade's completion callback,
        /// where nothing will cancel it. Assigning DialogResult there also closes the window, which
        /// is why that branch does not call Close() as well.</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_fadingOut)
            {
                _fadingOut = true;
                _closing = true;
                e.Cancel = true;
                // The result is fixed here: an accept still waiting on an existence check finds _closing
                // set when it resumes and does nothing, so a cancel can never turn into an OK.
                bool? result = _pendingResult;
                // Stop folder listing now, not after the fade, so a slow listing cannot land on a closing dialog.
                CloseDirectoryListing();
                var completion = new FadeCompletion(() =>
                {
                    _fadeDone = true;
                    if (result.HasValue) DialogResult = result;   // this closes it
                    else Close();                                  // cancel path
                });
                Anim.FadeOut(RootFade, completion.Complete);
                // A lost Completed callback (animation suppressed, element out of the tree) would leave the
                // dialog open and refusing every close, so a timer finishes it a little after the fade.
                completion.ArmFallback(Dispatcher);
                return;
            }
            // A second close request while the fade runs waits for it instead of cutting it short.
            if (!_fadeDone) { e.Cancel = true; return; }
            base.OnClosing(e);
        }

        private bool _fadeDone;

        public FileDialog(FileDialogMode mode = FileDialogMode.Open)
        {
            _mode = mode;
            InitializeComponent();
            Closed += (_, _) => CloseDirectoryListing();
            if (!DialogChrome.SetTitleIcon(TitleIcon)) TitleIcon.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => Anim.FadeIn(RootFade);
            SourceInitialized += (_, _) => KillerFamily.Windowing.WorkAreaFit.Fit(this);

            // Size and placement remembered separately from the folder picker: this dialog is a
            // different shape and sharing the keys would make each one fight the other.
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (double.TryParse(Services.ThemeManager.GetSetting("FileDlgW"),
                        System.Globalization.NumberStyles.Float, ci, out double w) &&
                    double.TryParse(Services.ThemeManager.GetSetting("FileDlgH"),
                        System.Globalization.NumberStyles.Float, ci, out double h))
                {
                    if (!double.IsNaN(w) && !double.IsInfinity(w) && w > 0) Width = Math.Max(MinWidth, w);
                    if (!double.IsNaN(h) && !double.IsInfinity(h) && h > 0) Height = Math.Max(MinHeight, h);
                }
                if (double.TryParse(Services.ThemeManager.GetSetting("FileDlgX"),
                        System.Globalization.NumberStyles.Float, ci, out double x) &&
                    double.TryParse(Services.ThemeManager.GetSetting("FileDlgY"),
                        System.Globalization.NumberStyles.Float, ci, out double y))
                {
                    if (!double.IsNaN(x) && !double.IsInfinity(x) && !double.IsNaN(y) && !double.IsInfinity(y))
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = x;
                        Top  = y;
                    }
                }
                if (double.TryParse(Services.ThemeManager.GetSetting(PlacesHKey),
                        System.Globalization.NumberStyles.Float, ci, out double ph) && ph >= 56)
                    PlacesRow.Height = new GridLength(Math.Min(ph, 600));
            }
            catch { /* registry unavailable - defaults are fine */ }

            _showHidden = Services.ThemeManager.GetSetting(ShowHiddenKey) == "1";
            FolderNode.ShowHidden = _showHidden;
            ApplyShowHiddenButton();
            if (int.TryParse(Services.ThemeManager.GetSetting(SortKeyKey), out int savedSortKey)
                && savedSortKey is >= 0 and <= 2)
                _sortKey = savedSortKey;
            _sortAsc = Services.ThemeManager.GetSetting(SortAscKey) is not string savedSortAsc
                || savedSortAsc != "0";

            Closing += (_, _) =>
            {
                try
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    Services.ThemeManager.SetSetting("FileDlgW", ActualWidth.ToString(ci));
                    Services.ThemeManager.SetSetting("FileDlgH", ActualHeight.ToString(ci));
                    Services.ThemeManager.SetSetting("FileDlgX", Left.ToString(ci));
                    Services.ThemeManager.SetSetting("FileDlgY", Top.ToString(ci));
                    Services.ThemeManager.SetSetting(PlacesHKey, PlacesRow.ActualHeight.ToString(ci));
                }
                catch { /* not worth failing the close */ }
            };

            // NO DwmChrome calls, deliberately. On an AllowsTransparency window the DWM corner
            // preference makes DWM composite its own rounded frame around the WINDOW rect - the
            // transparent 10px halo included - and SetThemeBorder tints it: that WAS the gray
            // band. The other four dialogs are AllowsTransparency with no DWM calls and have
            // never shown one; the card draws its own border and shadow, so DWM has nothing to
            // add. The WM_ERASEBKGND hook that lived here went too - a layered window is rendered
            // via UpdateLayeredWindow and never receives it. (2026-07-30, fifth attempt)
        }

        /// <summary>
        /// Sets the owner and shows modally. Everything that depends on Filter / FileName /
        /// InitialDirectory is wired HERE rather than in the constructor, because callers set
        /// those as object-initializer properties after construction.
        /// </summary>
        public bool? ShowDialog(Window? owner)
        {
            if (owner != null && owner.IsVisible) Owner = owner;

            AcceptButton.Content = Loc(_mode switch
            {
                FileDialogMode.Save   => "Str_Btn_Save",
                FileDialogMode.Folder => "Str_Btn_Select",
                _                     => "Str_Btn_Open",
            });
            if (_mode == FileDialogMode.Folder)
            {
                FileFields.Visibility = Visibility.Collapsed;
                AcceptButton.IsDefault = AcceptHighlightedFolder;
            }
            ConfigureImagePreview();
            // Extended, not Multiple: Extended is the Explorer behavior (plain click replaces the
            // selection, Ctrl adds, Shift ranges). Multiple toggles on every click, which feels
            // broken to anyone who has used a file dialog before.
            FileList.SelectionMode = Multiselect && _mode == FileDialogMode.Open
                ? SelectionMode.Extended
                : SelectionMode.Single;

            // Open mode has nothing to name, so the box is for typing/filtering a path, not a
            // new file. It stays visible: typing an exact name is faster than hunting for it.
            BuildFilters();
            BuildPlaces();
            PlacesList.ItemsSource = Places;
            FileList.ItemsSource   = Entries;
            InitTree();
            ApplyView();

            // A seeded FileName can be a bare name ("export.ics"), a full path, or empty.
            // The first of these that lists becomes the start folder. The listing runs off the UI
            // thread, so a dead network path cannot freeze the dialog before it shows.
            string seedName = "";
            _startCandidates.Clear();
            if (!string.IsNullOrWhiteSpace(FileName))
            {
                if (FileName.IndexOfAny(['\\', '/']) >= 0)
                {
                    var d = Path.GetDirectoryName(FileName);
                    // A relative folder would resolve against whatever the process's current directory is.
                    if (IsUsableSeedDirectory(d)) _startCandidates.Add(d!);
                    seedName = Path.GetFileName(FileName);
                }
                else seedName = FileName;
            }
            if (IsUsableSeedDirectory(InitialDirectory)) _startCandidates.Add(InitialDirectory);
            string? remembered = UsableRemembered(Services.ThemeManager.GetSetting(LastDirKey));
            if (!string.IsNullOrWhiteSpace(remembered)) _startCandidates.Add(remembered!);
            _startCandidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            _built = true;
            NavigateTo(NextStartCandidate()!);
            FileNameBox.Text = seedName;
            FileNameBox.IsReadOnly = IsFileNameReadOnly;

            // Save: preselect the stem so typing replaces the name but keeps the extension
            // visible. Open: caret at the end. Folder: there is no name box, so the button.
            if (_mode == FileDialogMode.Folder) AcceptButton.Focus();
            else FileNameBox.Focus();
            if (_mode == FileDialogMode.Save && seedName.Length > 0)
            {
                int dot = seedName.LastIndexOf('.');
                FileNameBox.Select(0, dot > 0 ? dot : seedName.Length);
            }
            else FileNameBox.CaretIndex = FileNameBox.Text.Length;

            return ShowDialog();
        }

        // ── Filters ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses Win32 filter syntax into the combo plus a parallel pattern list. A malformed
        /// filter (odd number of segments) degrades to "all files" rather than throwing - a bad
        /// filter string should not stop someone opening a file.
        /// </summary>
        private void BuildFilters()
        {
            FilterCombo.Items.Clear();
            _filterPatterns.Clear();

            var parts = (Filter ?? "").Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                var label = parts[i].Trim();
                var pats  = parts[i + 1].Split(';')
                                        .Select(p => p.Trim())
                                        .Where(p => p.Length > 0)
                                        .ToArray();
                if (label.Length == 0 || pats.Length == 0) continue;
                FilterCombo.Items.Add(label);
                _filterPatterns.Add(pats);
            }

            if (FilterCombo.Items.Count == 0)
            {
                FilterCombo.Items.Add(Loc("Str_Dlg_AllFiles"));
                _filterPatterns.Add(["*.*"]);
            }

            _declaredFilters = FilterCombo.Items.Count;
            int idx = FilterIndex - 1;
            FilterCombo.SelectedIndex = idx >= 0 && idx < FilterCombo.Items.Count ? idx : 0;
            FilterLabel.Visibility = FilterCombo.Visibility =
                FilterCombo.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_built) return;
            FilterIndex = FilterIndexAfterSelection(FilterCombo.SelectedIndex, _declaredFilters, FilterIndex);

            // Save mode follows the Win32 dialogs: switching the type swaps the typed name's
            // extension - but only when the current one belongs to another entry of THIS filter.
            // An extension the user typed by hand is theirs and is left alone.
            if (_mode == FileDialogMode.Save && !IsFileNameReadOnly)
            {
                string  name   = FileNameBox.Text?.Trim() ?? "";
                // Path.GetExtension throws on characters no path can carry; such a name keeps its
                // text and gets the invalid-path message instead of an extension swap.
                if (name.Length > 0 && IsMalformedTyped(name.Trim('"'))) { RejectTyped(); ApplySort(); return; }
                if (SwapTypedExtension(name, ActiveFilterExt(), AllFilterExts()) is string swapped)
                    FileNameBox.Text = swapped;
            }

            ApplySort();
        }

        /// <summary>
        /// The typed name with its extension swapped for <paramref name="newExt"/>, or null to leave
        /// it as typed: there is no extension, it already matches, or the user typed one that no
        /// entry of this filter names.
        /// </summary>
        internal static string? SwapTypedExtension(string typed, string? newExt, System.Collections.Generic.IEnumerable<string> filterExts)
        {
            // A pasted name keeps its quotes, and a quote is a character Path refuses.
            string name = typed.Trim().Trim('"');
            string cur  = name.Length == 0 ? "" : Path.GetExtension(name);
            if (newExt == null || cur.Length == 0 || cur.Equals(newExt, StringComparison.OrdinalIgnoreCase)
                || !filterExts.Contains(cur, StringComparer.OrdinalIgnoreCase)) return null;
            return Path.ChangeExtension(name, newExt);
        }

        /// <summary>
        /// The active filter entry's own extension (".csv"), or null when its first pattern is a
        /// wildcard-any or a multi-pattern catch-all that names no single extension.
        /// </summary>
        private string? ActiveFilterExt()
        {
            int i = FilterCombo.SelectedIndex;
            if (i < 0 || i >= _filterPatterns.Count) return null;
            string p = _filterPatterns[i][0];
            if (p.Length > 2 && p.StartsWith("*.") && p.IndexOfAny(['*', '?'], 2) < 0)
                return p[1..];
            return null;
        }

        /// <summary>Every concrete extension the filter list names, for the swap test above.</summary>
        private IEnumerable<string> AllFilterExts()
        {
            foreach (var pats in _filterPatterns)
                foreach (var p in pats)
                    if (p.Length > 2 && p.StartsWith("*.") && p.IndexOfAny(['*', '?'], 2) < 0)
                        yield return p[1..];
        }

        /// <summary>True when the name passes the active filter. Folders are never filtered out.</summary>
        private bool PassesFilter(PickerEntry en)
        {
            if (en.IsFolder) return true;
            if (_mode == FileDialogMode.Folder && HideFilesInFolderMode) return false;
            int i = FilterCombo.SelectedIndex;
            if (i < 0 || i >= _filterPatterns.Count) return true;
            var pats = _filterPatterns[i];
            return pats.Any(p => p == "*.*" || p == "*" || WildcardMatch(en.Name, p));
        }

        /// <summary>Case-insensitive glob. Anchored, so "*.ics" does not match "a.icsx".</summary>
        private static bool WildcardMatch(string name, string pattern)
        {
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // ── Quick places (pinned + drives) ───────────────────────────────────────

        /// <summary>
        /// Pinned folders first (persisted, user-editable via right-click), then the ready
        /// drives. Drives are enumerated live every build - they come and go with USB sticks -
        /// and are not pinned, so they carry no remove menu.
        /// </summary>
        private void BuildPlaces()
        {
            int build = ++_placesBuild;
            _ = FillPlacesAsync(build, PinnedPaths());
        }

        private int _placesBuild;

        /// <summary>
        /// Drive readiness and folder existence are probed on a worker: a dropped mapped drive or an
        /// unreachable pinned share blocks for the network timeout, and on the UI thread that would
        /// keep the dialog from ever appearing. The list fills in when the probe is done, and a newer
        /// build supersedes an older one.
        /// </summary>
        private async Task FillPlacesAsync(int build, List<string> pinnedPaths)
        {
            List<(string? Label, string Path, bool Pinned)> found;
            // A probe that fails or hangs still leaves the pinned folders to choose from.
            try
            {
                // Collecting the names touches no pinned folder. Each folder's existence is then
                // checked on its own worker under its own timeout, so one dead share drops only itself.
                var candidates = await OnStaWorker(() => CollectPlaces(pinnedPaths), ProbeTimeout, "places");
                found = await KeepExistingPlacesAsync(candidates, PlaceExists, ProbeTimeout);
            }
            catch { found = pinnedPaths.Select(path => ((string?)null, path, true)).ToList(); }
            if (build != _placesBuild) return;

            Places.Clear();
            foreach (var place in found)
                Places.Add(new PickerPlace(place.Label ?? LabelFor(place.Path), place.Path, place.Pinned));
            SyncPlacesSelection();
        }

        /// <summary>The candidates whose folder has to be checked (Check) are kept only when it exists, each
        /// on its own worker under its own timeout; the rest are kept as they are. Order is preserved.</summary>
        internal static async Task<List<(string? Label, string Path, bool Pinned)>> KeepExistingPlacesAsync(
            List<(string? Label, string Path, bool Pinned, bool Check)> candidates, Func<string, bool> exists, TimeSpan timeout)
        {
            var checkedPaths = await ExistingFoldersAsync(candidates.Where(c => c.Check).Select(c => c.Path), exists, timeout);
            var keep = new HashSet<string>(checkedPaths, StringComparer.OrdinalIgnoreCase);
            return candidates.Where(c => !c.Check || keep.Contains(c.Path))
                             .Select(c => (c.Label, c.Path, c.Pinned)).ToList();
        }

        /// <summary>Runs on a worker, so it touches no WPF state; a null label means "name it from the path".
        /// Pinned folders and drive roots come back marked for an existence check, which the caller does.</summary>
        private static List<(string? Label, string Path, bool Pinned, bool Check)> CollectPlaces(List<string> pinnedPaths)
        {
            var found = new List<(string? Label, string Path, bool Pinned, bool Check)>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in pinnedPaths)
                if (added.Add(p.TrimEnd('\\'))) found.Add((null, p, true, true));

            foreach (var place in ExplorerQuickAccessPlaces())
                if (added.Add(place.Path.TrimEnd('\\'))) found.Add((place.Label.Length > 0 ? place.Label : null, place.Path, false, false));

            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    string label;
                    try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel.Trim(); }
                    catch { label = d.DriveType.ToString(); }
                    if (added.Add(d.RootDirectory.FullName.TrimEnd('\\')))
                        found.Add(($"{d.Name.TrimEnd('\\')}  {label}", d.RootDirectory.FullName, false, true));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return found;
        }

        private static bool PlaceExists(string path) => !string.IsNullOrEmpty(path) && Directory.Exists(path);

        /// <summary>
        /// Runs <paramref name="work"/> on its own background thread in a single-threaded apartment:
        /// the Shell.Application COM object behind Quick Access is apartment-bound, and a pool
        /// thread would marshal it back to the UI thread, which is the thread being kept free.
        /// </summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

        // Workers still running, by caller-chosen key. A probe of a dead share can block for minutes
        // after its caller gave up at the timeout; asking again for the same thing while it is still
        // blocked waits on that worker instead of starting one more thread that blocks the same way.
        private static readonly Dictionary<string, Task> InFlight = new Dictionary<string, Task>();

        public static Task<T> OnStaWorker<T>(Func<T> work, TimeSpan timeout, string? key = null)
        {
            string? slot = key == null ? null : typeof(T).FullName + "|" + key;
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (InFlight)
            {
                if (slot != null && InFlight.TryGetValue(slot, out var running))
                    return WithTimeout((Task<T>)running, timeout);
                if (slot != null) InFlight[slot] = done.Task;
            }
            var thread = new System.Threading.Thread(() =>
            {
                T result = default!;
                Exception? failure = null;
                try { result = work(); }
                catch (Exception error) { failure = error; }
                // Forgotten before the result is published, so a caller that sees it finish and asks
                // again gets a fresh probe.
                if (slot != null) lock (InFlight) InFlight.Remove(slot);
                if (failure != null) done.SetException(failure); else done.SetResult(result);
            }) { IsBackground = true };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            return WithTimeout(done.Task, timeout);
        }

        private static async Task<T> WithTimeout<T>(Task<T> work, TimeSpan timeout)
        {
            if (await Task.WhenAny(work, Task.Delay(timeout)) != work) throw new TimeoutException();
            return await work;
        }

        /// <summary>
        /// Explorer's Quick Access entries, read through the Shell.Application COM object.
        ///
        /// Returns a materialized list rather than an iterator so the whole body can sit inside a
        /// real try/catch: a yield-return method is only allowed a finally, so every failure here
        /// used to escape into BuildPlaces and take the Open dialog down with it. Each call below
        /// is late-bound COM and can fail outright - Wine and CrossOver register the
        /// Shell.Application ProgID but implement no NameSpace, so the ProgID null check passes
        /// and the binder then throws RuntimeBinderException. Quick Access is a convenience, so
        /// any failure just drops it and leaves the pinned folders and the drives.
        /// </summary>
        private static List<(string Label, string Path)> ExplorerQuickAccessPlaces()
        {
            const string QuickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
            var places = new List<(string Label, string Path)>();
            object? shell = null, folder = null, items = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return places;
                shell = Activator.CreateInstance(type);
                folder = ((dynamic)shell!).NameSpace(QuickAccess);
                if (folder == null) return places;
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items).Count;
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = ((dynamic)items).Item(i);
                        if (item == null) continue;
                        dynamic quickItem = item;
                        if (!Convert.ToBoolean(quickItem.IsFolder)) continue;
                        string path = Convert.ToString(quickItem.Path) ?? "";
                        string name = Convert.ToString(quickItem.Name) ?? "";
                        if (Directory.Exists(path)) places.Add((name, path));
                    }
                    catch { /* one unreadable entry must not lose the rest */ }
                    finally { if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
                }
            }
            catch { /* no shell, or a shell without Quick Access: keep whatever was read */ }
            finally
            {
                if (items != null && Marshal.IsComObject(items)) Marshal.FinalReleaseComObject(items);
                if (folder != null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
            return places;
        }

        /// <summary>
        /// The persisted pin list. First run (key absent, null) seeds the five standard folders;
        /// an EMPTY stored value means the user unpinned everything and must stay empty.
        /// </summary>
        private static List<string> PinnedPaths()
        {
            string? saved = Services.ThemeManager.GetSetting(PinnedKey);
            if (saved != null)
                return [.. saved.Split('|').Where(s => s.Length > 0)];

            return [.. new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            }.Where(p => !string.IsNullOrEmpty(p))];
        }

        /// <summary>Localized label for the five standard folders, plain folder name otherwise.</summary>
        private static string LabelFor(string path)
        {
            string p = path.TrimEnd('\\');
            bool Is(string other) => other.Length > 0 &&
                p.Equals(other.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))  return Loc("Str_QA_Home");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)))      return Loc("Str_QA_Desktop");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))  return Loc("Str_QA_Documents");
            if (Is(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")))
                                                                                       return Loc("Str_QA_Downloads");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)))   return Loc("Str_QA_Pictures");

            var name = Path.GetFileName(p);
            return name.Length == 0 ? p : name;
        }

        private void PinPlace(string path)
        {
            var list = PinnedPaths();
            if (list.Any(p => p.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(path);
            Services.ThemeManager.SetSetting(PinnedKey, string.Join("|", list));
            BuildPlaces();
            SyncPlacesSelection();
        }

        private PickerPlace? _placesMenuPlace;

        private void Places_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _placesMenuPlace = ItemUnder<PickerPlace>(e.OriginalSource as DependencyObject);
            // Drives are dynamic, not pinned - nothing to remove; empty space likewise.
            if (_placesMenuPlace is not { Pinned: true }) e.Handled = true;
        }

        private void UnpinPlace_Click(object sender, RoutedEventArgs e)
        {
            if (_placesMenuPlace is not { Pinned: true } pl) return;
            var list = PinnedPaths()
                .Where(p => !p.TrimEnd('\\').Equals(pl.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .ToList();
            Services.ThemeManager.SetSetting(PinnedKey, string.Join("|", list));
            BuildPlaces();
            SyncPlacesSelection();
        }

        private PickerEntry? _filesMenuEntry;

        private void Files_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _filesMenuEntry = ItemUnder<PickerEntry>(e.OriginalSource as DependencyObject);
            if (_filesMenuEntry is not { IsFolder: true }) e.Handled = true;   // only folders pin
        }

        private void FilePin_Click(object sender, RoutedEventArgs e)
        {
            if (_filesMenuEntry is { IsFolder: true } en) PinPlace(en.FullPath);
        }

        /// <summary>Marks the place matching the current folder, or clears the marker.</summary>
        private void SyncPlacesSelection()
        {
            bool was = _navigating;
            _navigating = true;
            PlacesList.SelectedItem = _currentDir.Length == 0 ? null : Places.FirstOrDefault(p =>
                p.Path.TrimEnd('\\').Equals(_currentDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            _navigating = was;
        }

        /// <summary>The row model under a right-click, resolved by walking up to the ListBoxItem.</summary>
        private static T? ItemUnder<T>(DependencyObject? d) where T : class
        {
            while (d != null)
            {
                if (d is ListBoxItem lbi) return lbi.DataContext as T;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private static string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        // ── Navigation ───────────────────────────────────────────────────────────

        private void NavigateTo(string dir) => DirectoryListingTask = NavigateDirectoryAsync(dir);
        private void RecoverTo(string dir, int attempt) => DirectoryListingTask = NavigateCoreAsync(dir, attempt);

        private static DateTime SafeTime(Func<DateTime> get)
        {
            try { return get(); } catch { return DateTime.MinValue; }
        }

        private static long SafeLen(FileInfo fi)
        {
            try { return fi.Length; } catch { return 0; }
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            // After a recovery walk gave up, Up continues from where it stopped, not from the vanished folder.
            if (_walkStopped != null)
            {
                if (ParentOf(_walkStopped) is { } above) RecoverTo(above, 1);
                return;
            }
            var parent = Directory.GetParent(_currentDir);
            if (parent != null) NavigateTo(parent.FullName);
        }

        private void Places_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (PinHoldsSelection(_pinDragPlace, Mouse.LeftButton == MouseButtonState.Pressed)) return;
            if (PlacesList.SelectedItem is PickerPlace p) NavigateTo(p.Path);
        }

        // ── Folder tree (ported from KillerShell, see Controls/FolderTree.cs) ────

        /// <summary>Ready drives only - an empty optical drive or a dropped mapping would sit
        /// there as a node that throws the moment anyone touches it. The drives are probed on a
        /// worker, because a dropped mapping can block for the network timeout, and the roots fill
        /// in when it is done.</summary>
        private void InitTree()
        {
            if (TreeRoots.Count > 0 || _treeStarted) return;
            _treeStarted = true;
            FolderTreeCtl.ItemsSource = TreeRoots;
            _ = FillTreeRootsAsync();

            // Edge fades follow the scroll position (KillerShell TreePanel.cs). ScrollChanged is
            // handled at the TreeView rather than dug out of its template: it bubbles, so the
            // inner ScrollViewer is reached without needing to have found it first. Loaded and
            // SizeChanged cover the passes where nothing scrolled but the extent moved.
            FolderTreeCtl.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => { SyncTreeEdgeFades(); SyncTreeFade(); }));
            FolderTreeCtl.SizeChanged += (_, _) => { SyncTreeEdgeFades(); SyncTreeFade(); };
            FolderTreeCtl.Loaded      += (_, _) => { SyncTreeEdgeFades(); SyncTreeFade(); };

            // The places list gets the same treatment (2026-07-30). No scrollbar lift:
            // horizontal scrolling is disabled on it.
            PlacesList.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => SyncPlacesEdgeFades()));
            PlacesList.SizeChanged += (_, _) => SyncPlacesEdgeFades();
            PlacesList.Loaded      += (_, _) => SyncPlacesEdgeFades();
        }

        private bool _treeStarted;

        private async Task FillTreeRootsAsync()
        {
            List<FolderNode> roots;
            try { roots = await OnStaWorker(ReadyDriveNodes, ProbeTimeout, "drives"); }
            catch { _treeStarted = false; return; }
            foreach (var root in roots) TreeRoots.Add(root);
            // The first listing may have finished before the roots existed, leaving nothing revealed.
            if (_currentDir.Length > 0) _ = RevealInTree(_currentDir, _navigationVersion);
        }

        /// <summary>Runs on a worker; FolderNode is plain data, so the nodes can be built there.</summary>
        private static List<FolderNode> ReadyDriveNodes()
        {
            var roots = new List<FolderNode>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (IOException) { return roots; }

            foreach (var d in drives)
            {
                bool ready;
                try { ready = d.IsReady; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (ready) roots.Add(new FolderNode(d));
            }
            return roots;
        }

        /// <summary>Places-list twin of SyncTreeEdgeFades: same ramp, same rules.</summary>
        private void SyncPlacesEdgeFades()
        {
            var sv = FindDescendant<ScrollViewer>(PlacesList);
            if (sv == null) return;

            PlacesFadeTop.Opacity    = Ramp(sv.VerticalOffset, PlacesFadeTop.Height, 18);
            PlacesFadeBottom.Opacity = Ramp(sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset,
                                            PlacesFadeBottom.Height, 22);
        }

        /// <summary>
        /// Fade each edge only while there is something PAST it, ramped over the fade's own
        /// height: none at the very top, none at the very bottom, full in between. A proportional
        /// ramp rather than a flip - at one pixel of scroll it is one pixel's worth of fade, so
        /// neither edge ever pops. (KillerShell TreePanel.SyncTreeEdgeFades, verbatim.)
        /// </summary>
        private void SyncTreeEdgeFades()
        {
            var sv = FindDescendant<ScrollViewer>(FolderTreeCtl);
            if (sv == null) return;

            TreeFadeTop.Opacity    = Ramp(sv.VerticalOffset, TreeFadeTop.Height, 18);
            TreeFadeBottom.Opacity = Ramp(sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset,
                                          TreeFadeBottom.Height, 22);
        }

        // Height is NaN until the border has been laid out, hence the fallback. Scaled by the theme's
        // EdgeFadeOpacity, as every other edge fade in the kit is: 98SE states 0. Its pane is white
        // and its scrollbar wider than the fades' right inset, so a fade there was a solid white
        // block painted over the bottom of the places scrollbar.
        private double Ramp(double distance, double height, double fallback)
        {
            double h = double.IsNaN(height) || height <= 0 ? fallback : height;
            double strength = TryFindResource("EdgeFadeOpacity") is double fade ? fade : 1;
            return Math.Min(1, Math.Max(0, distance) / h) * strength;
        }

        /// <summary>
        /// Keep the bottom edge fade sitting on the tree's last visible ROW rather than on the
        /// horizontal scrollbar underneath it. The bar's real height is measured, not taken from
        /// SystemParameters - the themed template is not the system metric. Base 4 is the tree's
        /// own bottom margin. (KillerShell TreePanel.SyncTreeFade, adapted.)
        /// </summary>
        private void SyncTreeFade()
        {
            var sv = FindDescendant<ScrollViewer>(FolderTreeCtl);
            double lift = 0;

            if (sv != null && sv.ComputedHorizontalScrollBarVisibility == Visibility.Visible)
            {
                var bar = FindHorizontalBar(sv);
                lift = bar?.ActualHeight ?? SystemParameters.HorizontalScrollBarHeight;
            }

            var m = TreeFadeBottom.Margin;
            double want = 4 + lift;
            if (Math.Abs(m.Bottom - want) < 0.5) return;     // no churn on every layout pass
            TreeFadeBottom.Margin = new Thickness(m.Left, m.Top, m.Right, want);
        }

        private void FolderTree_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = FindDescendant<ScrollViewer>(FolderTreeCtl);
            if (sv is null) return;

            bool horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (horizontal)
            {
                if (sv.ScrollableWidth <= 0) return;
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.5);
            }
            else
            {
                if (sv.ScrollableHeight <= 0) return;
                sv.ScrollToVerticalOffset(sv.VerticalOffset - KillerFamily.Controls.WheelScroll.Step(e.Delta, SystemParameters.WheelScrollLines, sv.ViewportHeight));
            }
            e.Handled = true;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T hit) return hit;
                var deeper = FindDescendant<T>(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // FindDescendant takes the FIRST match of a type, and a ScrollViewer has two scrollbars,
        // so the orientation has to be checked rather than assumed.
        private static System.Windows.Controls.Primitives.ScrollBar? FindHorizontalBar(DependencyObject root)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is System.Windows.Controls.Primitives.ScrollBar sb &&
                    sb.Orientation == Orientation.Horizontal) return sb;
                var deeper = FindHorizontalBar(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // TreeViewItem.Expanded is attached at the TreeView, so this fires for every node at any
        // depth - which is the point: one handler drives the whole lazy load.
        private async void FolderTree_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not TreeViewItem tvi) return;
            if (tvi.DataContext is not FolderNode node) return;
            await node.LoadChildrenAsync();
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_treeSyncing) return;
            if (e.NewValue is not FolderNode node) return;
            if (string.IsNullOrEmpty(node.Path)) return;   // the placeholder, mid-load
            // The tree follows the listing, so its node for the folder already shown is no request to list it again.
            // Only while that listing is available and idle: after a failed or unfinished one the node is a retry.
            if (IsDirectoryListingAvailable && !IsDirectoryListingBusy
                && string.Equals(node.Path.TrimEnd('\\'), _currentDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            NavigateTo(node.Path);
        }

        private FolderNode? _treeMenuNode;

        private void FolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _treeMenuNode = e.CursorLeft < 0
                ? NodeUnder(Keyboard.FocusedElement as DependencyObject)
                    ?? NodeUnder(e.OriginalSource as DependencyObject)
                    ?? FolderTreeCtl.SelectedItem as FolderNode
                : NodeUnder(e.OriginalSource as DependencyObject)
                    ?? NodeUnder(Mouse.DirectlyOver as DependencyObject);
            // Drives are already in places; empty space has nothing to pin.
            if (_treeMenuNode == null || _treeMenuNode.IsDrive) e.Handled = true;
        }

        private void TreePin_Click(object sender, RoutedEventArgs e)
        {
            if (_treeMenuNode is { IsDrive: false } n && !string.IsNullOrEmpty(n.Path))
                PinPlace(n.Path);
        }

        private static FolderNode? NodeUnder(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is TreeViewItem tvi) return tvi.DataContext as FolderNode;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>
        /// Points the tree at a folder reached from somewhere else - places, the path box, a
        /// double-click. Expands the chain of ANCESTORS and selects the folder; the destination's
        /// own expander is left exactly as the user had it (KillerShell's rule - forcing it
        /// collapsed the branch under the cursor and the whole tree jumped).
        /// </summary>
        private async Task RevealInTree(string folder, long version)
        {
            if (string.IsNullOrEmpty(folder)) return;

            string full;
            try { full = Path.GetFullPath(folder); }
            catch { return; }

            var root = TreeRoots.FirstOrDefault(
                r => full.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase));
            if (root == null) return;

            var segments = RelativeSegments(root.Path, full).ToList();

            var current = root;
            if (segments.Count > 0)
            {
                await current.LoadChildrenAsync();
                if (version != _navigationVersion) return;   // a newer folder is being shown
                current.IsExpanded = true;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                var next = current.Children.FirstOrDefault(
                    c => string.Equals(c.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                if (next == null) return;   // hidden by the filter, or gone since the listing

                current = next;
                if (i == segments.Count - 1) break;

                await current.LoadChildrenAsync();   // needed to match the NEXT segment
                if (version != _navigationVersion) return;
                current.IsExpanded = true;
            }

            _treeSyncing = true;
            current.IsSelected = true;
            _treeSyncing = false;
        }

        private static string[] RelativeSegments(string rootPath, string fullPath)
        {
            string rest = fullPath[rootPath.Length..];
            return rest.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                              StringSplitOptions.RemoveEmptyEntries);
        }

        // ── Recent locations ─────────────────────────────────────────────────────

        private static List<string> LoadRecents()
            => [.. (Services.ThemeManager.GetSetting(RecentsKey) ?? "")
               .Split('|').Where(s => s.Length > 0)];

        private static void RecordRecent(string dir)
        {
            var list = LoadRecents();
            list.RemoveAll(p => p.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            list.Insert(0, dir);
            if (list.Count > RecentsMax) list.RemoveRange(RecentsMax, list.Count - RecentsMax);
            Services.ThemeManager.SetSetting(RecentsKey, string.Join("|", list));
        }

        // Recents are probed for a couple of seconds at most, and a click while a probe is still out does nothing.
        private bool _recentsProbing;
        internal static readonly TimeSpan RecentsDeadline = TimeSpan.FromSeconds(2);

        /// <summary>The recent folders that answered in time, or null when a probe is already running.</summary>
        internal async Task<List<string>?> ProbeRecentsAsync()
        {
            if (_recentsProbing) return null;
            _recentsProbing = true;
            try { return await ExistingFoldersAsync(LoadRecents(), FolderExistsProbe, RecentsDeadline); }
            finally { _recentsProbing = false; }
        }

        private async void RecentsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Stale entries (unplugged drive, deleted folder) are filtered at open rather than
            // scrubbed from the store - the drive may be back tomorrow.
            var list = await ProbeRecentsAsync();
            if (list == null || list.Count == 0 || _closing) return;

            _navigating = true;              // rebinding must not raise a navigation
            RecentsList.ItemsSource = list;
            RecentsList.SelectedItem = null;
            _navigating = false;
            RecentsPopup.IsOpen = true;
        }

        private void RecentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (RecentsList.SelectedItem is not string dir) return;
            RecentsPopup.IsOpen = false;
            NavigateTo(dir);
        }

        // ── Hidden / dot files ───────────────────────────────────────────────────

        private void ShowHidden_Click(object sender, RoutedEventArgs e)
        {
            _showHidden = !_showHidden;
            Services.ThemeManager.SetSetting(ShowHiddenKey, _showHidden ? "1" : "0");
            FolderNode.ShowHidden = _showHidden;
            ApplyShowHiddenButton();

            if (_currentDir.Length > 0) NavigateTo(_currentDir);

            // Re-enumerate loaded tree branches in place, keeping expansion (FolderTree.cs).
            foreach (var r in TreeRoots.ToList()) _ = r.RefreshAsync();
        }

        private void ApplyShowHiddenButton()
        {
            // E7B3 eye at rest, E890 while showing - KillerShell's build-proven pair
            // (ViewOptions.cs). Codepoints, never literal PUA glyphs (family rule).
            ShowHiddenBtn.Content = ((char)(_showHidden ? 0xE890 : 0xE7B3)).ToString();
            ShowHiddenBtn.Tag     = _showHidden ? "on" : null;
        }

        private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (FileList.SelectedItem is PickerEntry en)
            {
                // Selecting a FILE fills the name box - that is the value being chosen. Selecting
                // a folder does not: it is a navigation target, and overwriting the typed name
                // with a folder name would lose what the user was in the middle of typing.
                // Several files highlighted show as the quoted list the box also accepts as input.
                var highlighted = Multiselect && _mode == FileDialogMode.Open
                    ? FileList.SelectedItems.OfType<PickerEntry>().Where(x => !x.IsFolder).Select(x => x.Name).ToList()
                    : null;
                if (highlighted != null && highlighted.Count > 1)
                {
                    _selectionText = SelectionNameText(highlighted);
                    if (!IsFileNameReadOnly) FileNameBox.Text = _selectionText;
                }
                else if (!en.IsFolder && !IsFileNameReadOnly)
                {
                    _selectionText = en.Name;
                    FileNameBox.Text = en.Name;
                }
                SelName.Text = en.Name;
                SelMeta.Text = en.IsFolder ? en.ModifiedLabel : $"{en.SizeLabel}  |  {en.ModifiedLabel}";
                UpdateImagePreview(en);
            }
            else
            {
                UpdateInfoSummary();
                UpdateImagePreview(null);
            }
        }

        private void ConfigureImagePreview()
        {
            if (!ShowImagePreview)
            {
                ImagePreviewHost.Visibility = Visibility.Collapsed;
                ImagePreviewGapColumn.Width = new GridLength(0);
                ImagePreviewColumn.Width = new GridLength(0);
                return;
            }

            ImagePreviewHost.Visibility = Visibility.Visible;
            ImagePreviewGapColumn.Width = new GridLength(8);
            ImagePreviewColumn.Width = new GridLength(230);

            // A preview should not consume the file list. Only image pickers grow; the remembered
            // size remains the user's starting point and is still clamped to the work area.
            MinWidth = 780;
            Width = Math.Min(SystemParameters.WorkArea.Width, Math.Max(Width, 880));
        }

        private async void UpdateImagePreview(PickerEntry? entry)
        {
            int generation = ++_imagePreviewGeneration;
            ImagePreview.Source = null;
            ImagePreviewPlaceholder.Visibility = Visibility.Visible;

            if (!ShowImagePreview || entry == null || entry.IsFolder ||
                !PreviewImageExtensions.Contains(Path.GetExtension(entry.FullPath)))
                return;

            string path = entry.FullPath;
            try
            {
                // BitmapCacheOption.OnLoad closes the file after decoding, and DecodePixelWidth
                // caps memory for camera images while retaining enough detail for this pane.
                var bitmap = await Task.Run(() =>
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    image.DecodePixelWidth = 900;
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.EndInit();
                    image.Freeze();
                    return image;
                });

                if (generation != _imagePreviewGeneration) return;
                ImagePreview.Source = bitmap;
                ImagePreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Corrupt, unsupported, or transiently unavailable image: leave the neutral
                // placeholder visible and keep the picker fully usable.
            }
        }

        private void Files_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            // A double-click on the scroll bars or the blank space under the last row is raised by
            // the list too, and must not open whatever row is still highlighted.
            if (ItemUnder<PickerEntry>(e.OriginalSource as DependencyObject) == null) return;
            if (FileList.SelectedItem is not PickerEntry en) return;
            if (en.IsFolder) NavigateTo(en.FullPath);
            else Accept();
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var typed = PathBox.Text?.Trim().Trim('"');
            e.Handled = true;
            if (string.IsNullOrEmpty(typed)) return;
            if (IsMalformedTyped(typed!)) { SelMeta.Text = Loc("Str_Dlg_InvalidPath"); return; }
            string resolved = ResolveTyped(typed!);
            // A drive-relative or unresolvable entry would otherwise be looked up against the process's folder.
            if (!IsFullyQualified(resolved)) { SelMeta.Text = Loc("Str_Dlg_NoSuchFolder"); return; }
            NavigateTo(resolved);
        }

        // A second Enter while a slow folder probe (a network or USB path) is still out does nothing.
        private readonly Services.SingleFlight _nameEnter = new();

        private async void FileNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await _nameEnter.RunAsync(AcceptTypedNameAsync);
        }

        private async Task AcceptTypedNameAsync()
        {
            var typed = FileNameBox.Text?.Trim() ?? "";

            // Several quoted names are a multiselect answer, not a path.
            var quoted = Multiselect && _mode == FileDialogMode.Open ? QuotedNames(typed) : null;
            if (quoted != null && quoted.Length > 1) { Accept(); return; }
            if (IsMalformedTyped(typed.Trim('"'))) { RejectTyped(); return; }

            // A directory typed into the name box navigates instead of accepting - matches the
            // Win32 dialogs, and is how people paste a path in.
            string unquoted = typed.Trim('"');
            if (unquoted.Length > 0)
            {
                var asDir = ResolveTyped(unquoted);
                bool isDirectory = await ProbeAsync(FolderExistsProbe, asDir);
                if (_closing) return;
                if (isDirectory) { NavigateTo(asDir); FileNameBox.Clear(); return; }
            }

            if (TryApplyWildcard(typed)) return;

            Accept();
        }

        /// <summary>A wildcard retargets the listing rather than naming a file: it becomes the first
        /// filter and the name box is cleared. False when <paramref name="typed"/> holds no wildcard.</summary>
        private bool TryApplyWildcard(string typed)
        {
            if (typed.IndexOfAny(['*', '?']) < 0) return false;
            // After the caller's own filters, never before them: FilterIndex numbers those, and a
            // caller that picks its export format by FilterIndex must not see it shift.
            var (slot, replace) = TypedFilterSlot(_declaredFilters, FilterCombo.Items.Count);
            if (replace) { _filterPatterns[slot] = [typed]; FilterCombo.Items[slot] = typed; }
            else { _filterPatterns.Add([typed]); FilterCombo.Items.Add(typed); }
            FilterCombo.SelectedIndex = slot;
            FileNameBox.Clear();
            return true;
        }

        /// <summary>Where a typed wildcard's entry goes: right after the declared filters, replacing the
        /// previous typed one when there is already one.</summary>
        internal static (int Slot, bool Replace) TypedFilterSlot(int declaredCount, int itemCount) =>
            (declaredCount, itemCount > declaredCount);

        /// <summary>The caller-visible FilterIndex (1-based) after the combo changes: only the caller's own
        /// filters move it. A typed wildcard is a view of the listing, not one of the caller's choices.</summary>
        internal static int FilterIndexAfterSelection(int selectedIndex, int declaredCount, int current) =>
            selectedIndex >= 0 && selectedIndex < declaredCount ? selectedIndex + 1 : current;

        private void UpdateInfoSummary()
        {
            int folders = _raw.Count(x => x.IsFolder);
            int shown   = Entries.Count(x => !x.IsFolder);
            var leaf    = Path.GetFileName(_currentDir.TrimEnd('\\'));
            SelName.Text = leaf.Length == 0 ? _currentDir : leaf;
            SelMeta.Text = string.Format(Loc("Str_Sum_Counts"), folders, shown);
            if (!IsDirectoryListingComplete && !string.IsNullOrWhiteSpace(_listingError)) SelMeta.Text += " | " + _listingError;
        }

        // ── View modes ───────────────────────────────────────────────────────────

        private void ViewList_Click(object sender, RoutedEventArgs e)    => SetView(0);
        private void ViewIcons_Click(object sender, RoutedEventArgs e)   => SetView(1);
        private void ViewDetails_Click(object sender, RoutedEventArgs e) => SetView(2);

        private void SetView(int mode)
        {
            _viewMode = mode;
            ApplyView();
        }

        /// <summary>
        /// The three views differ in panel, template AND scroll direction - that last one is the
        /// part that is easy to miss. List view wraps into columns and scrolls sideways, which only
        /// works if vertical scrolling is DISABLED: an enabled vertical ScrollViewer hands the panel
        /// infinite height, so a vertical WrapPanel never wraps and you get one tall column.
        /// </summary>
        private void ApplyView()
        {
            Resources["PickerIconSize"] = (double)_pickerIconSize;
            Resources["PickerIconContentWidth"] = (double)Math.Max(84, _pickerIconSize + 24);
            Resources["PickerIconTileWidth"] = (double)Math.Max(96, _pickerIconSize + 36);
            Resources["PickerIconTileHeight"] = (double)(_pickerIconSize + 44);
            switch (_viewMode)
            {
                case 1:  // icons: grid, wraps across, scrolls down
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelIconGrid");
                    FileList.ItemTemplate = (DataTemplate)FindResource("IconTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    break;

                case 2:  // details: one row per entry, scrolls down
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelStack");
                    FileList.ItemTemplate = (DataTemplate)FindResource("DetailsTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    break;

                default: // list: columns of small icons, scrolls RIGHT
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelListCols");
                    FileList.ItemTemplate = (DataTemplate)FindResource("RowTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    break;
            }

            DetailsHeader.Visibility = _viewMode == 2 ? Visibility.Visible : Visibility.Collapsed;
            ViewListBtn.Tag    = _viewMode == 0 ? "on" : null;
            ViewIconsBtn.Tag   = _viewMode == 1 ? "on" : null;
            ViewDetailsBtn.Tag = _viewMode == 2 ? "on" : null;
        }

        /// <summary>
        /// List view wraps entries into columns and scrolls horizontally. A normal mouse wheel
        /// only asks WPF to scroll vertically, which is disabled in this view, so translate the
        /// wheel delta to the horizontal scrollbar. Icon and details views remain vertical.
        /// </summary>
        private int _pickerIconSize = 32;
        private int _pickerWheelDelta;
        private static readonly int[] PickerIconSizes = [16, 32, 48, 96];

        private void ChangePickerView(int delta)
        {
            _pickerWheelDelta += delta;
            int steps = _pickerWheelDelta / 120;
            _pickerWheelDelta %= 120;
            if (steps == 0) return;
            int current = _viewMode == 2 ? 0 : _viewMode == 0 ? 1 :
                Array.IndexOf(PickerIconSizes, _pickerIconSize) + 2;
            int next = Math.Max(0, Math.Min(PickerIconSizes.Length + 1, current + steps));
            if (next >= 2) _pickerIconSize = PickerIconSizes[next - 2];
            SetView(next == 0 ? 2 : next == 1 ? 0 : 1);
        }

        private void FileList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                ChangePickerView(e.Delta);
                e.Handled = true;
                return;
            }
            _pickerWheelDelta = 0;
            if (_viewMode != 0) return;
            var sv = FindDescendant<ScrollViewer>(FileList);
            if (sv is null) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        // ── Sorting ──────────────────────────────────────────────────────────────

        private void SortName_Click(object sender, RoutedEventArgs e)     => SetSort(0);
        private void SortSize_Click(object sender, RoutedEventArgs e)     => SetSort(1);
        private void SortModified_Click(object sender, RoutedEventArgs e) => SetSort(2);

        private void SortButton_Click(object sender, RoutedEventArgs e)
        {
            SortMenu.PlacementTarget = SortButton;
            SortMenu.IsOpen = true;
        }

        private void SetSort(int key)
        {
            if (_sortKey == key) _sortAsc = !_sortAsc;
            else { _sortKey = key; _sortAsc = key == 0; }
            Services.ThemeManager.SetSetting(SortKeyKey, _sortKey.ToString());
            Services.ThemeManager.SetSetting(SortAscKey, _sortAsc ? "1" : "0");
            ApplySort();
        }

        /// <summary>
        /// Rebuilds Entries from _raw: filter applied, folders always before files, then the
        /// active sort key. Folders-first is not a sort key - it is the frame the sort runs in.
        /// </summary>
        private void ApplySort() => _ = ApplySortAsync();

        private async Task ApplySortAsync()
        {
            var visible = _raw.Where(PassesFilter);

            IOrderedEnumerable<PickerEntry> ordered = _sortKey switch
            {
                1 => _sortAsc ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.SizeBytes)
                              : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.SizeBytes),
                2 => _sortAsc ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.Modified)
                              : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.Modified),
                _ => _sortAsc ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                              : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase),
            };

            if (!await PublishListingRowsAsync(ordered.ToArray())) return;

            NameArrow.Text = _sortKey == 0 ? (_sortAsc ? ArrowUp : ArrowDown) : "";
            SizeArrow.Text = _sortKey == 1 ? (_sortAsc ? ArrowUp : ArrowDown) : "";
            ModArrow.Text  = _sortKey == 2 ? (_sortAsc ? ArrowUp : ArrowDown) : "";
            SortNameItem.IsChecked = _sortKey == 0;
            SortSizeItem.IsChecked = _sortKey == 1;
            SortModifiedItem.IsChecked = _sortKey == 2;
            SortButton.Tag = "on";

            EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Accept / cancel ──────────────────────────────────────────────────────

        private void OK_Click(object sender, RoutedEventArgs e) => Accept();

        /// <summary>
        /// Resolves the name box to a full path and applies the mode's rules. Anything that fails
        /// leaves the dialog OPEN with focus back in the name box - a file dialog that closes on a
        /// bad name and makes you start over is the worst outcome.
        /// </summary>
        private async void Accept()
        {
            // Read as the key or click arrives: the accept below may wait on a slow folder probe.
            OpenInNewWindow = PickerAcceptRule.OpensInNewWindow((Keyboard.Modifiers & ModifierKeys.Shift) != 0, _mode == FileDialogMode.Open);
            // A name no path can carry is refused with its message straight away: that needs no
            // listing, so a folder still loading (after a filter change, say) must not swallow it.
            if (_mode != FileDialogMode.Folder && !_accepting)
            {
                string raw = FileNameBox.Text?.Trim() ?? "";
                bool quotedList = Multiselect && _mode == FileDialogMode.Open && QuotedNames(raw) is { Length: > 1 };
                if (!quotedList && raw.Trim('"').Length > 0 && IsMalformedTyped(raw.Trim('"'))) { RejectTyped(); return; }
            }
            // Accept stays off while a listing loads or an accept is already running, so a double
            // press or an Enter during the overwrite prompt cannot run it twice.
            if (!PickerAcceptRule.CanAccept(IsDirectoryListingBusy, IsDirectoryListingAvailable, _accepting, _mode == FileDialogMode.Folder, FileNameBox.Text)) return;
            _accepting = true;
            AcceptButton.IsEnabled = false;
            try { await AcceptCoreAsync(); }
            finally
            {
                _accepting = false;
                if (_pendingResult != true && !_closing) RefreshAcceptEnabled();
            }
        }

        private bool _accepting;

        // Accept is on exactly when pressing it would do something.
        private void RefreshAcceptEnabled()
            => AcceptButton.IsEnabled = PickerAcceptRule.CanAccept(IsDirectoryListingBusy, IsDirectoryListingAvailable, _accepting, _mode == FileDialogMode.Folder, FileNameBox.Text);

        // Accept depends on the typed name while the folder listing is unavailable, so it is
        // re-evaluated as the name changes, not only when the listing settles.
        private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (AcceptButton != null) RefreshAcceptEnabled();
        }

        private void ReportUnanswered(string reason)
        {
            SelMeta.Text = reason;
            FileNameBox.Focus();
        }

        private async Task AcceptCoreAsync()
        {
            if (_mode == FileDialogMode.Folder)
            {
                string folder = AcceptHighlightedFolder && FileList.SelectedItem is PickerEntry { IsFolder: true } selected
                    ? selected.FullPath : _currentDir;
                var folderState = await CheckAsync(FolderExistsProbe, folder);
                if (_closing) return;
                if (folderState.State == Presence.Unknown) { SelMeta.Text = folderState.Reason; return; }
                if (folderState.State == Presence.No) { SelMeta.Text = Loc("Str_Dlg_NoSuchFolder"); return; }
                FileName  = folder;
                FileNames = [folder];
                if (AcceptHighlightedFolder) RememberAcceptedDirectory(folder);
                _pendingResult = true;   // applied after the fade - see OnClosing
                Close();
                return;
            }
            // Multiselect: several files highlighted wins outright. The name box shows a quoted
            // list in that state and is not worth re-parsing - the selection IS the answer. A
            // single highlighted file falls through to the normal path so every rule below
            // (extension, existence) still applies.
            if (Multiselect && _mode == FileDialogMode.Open)
            {
                var picked = FileList.SelectedItems.OfType<PickerEntry>()
                                                   .Where(x => !x.IsFolder)
                                                   .Select(x => x.FullPath)
                                                   .ToArray();
                // The selection answers only while the box still shows it; a name the user typed over it wins.
                if (picked.Length > 1 && (IsFileNameReadOnly || SelectionAnswers(FileNameBox.Text ?? "", _selectionText)))
                {
                    FileNames = picked;
                    FileName  = picked[0];
                    RememberAcceptedDirectory();
                    _pendingResult = true;   // applied after the fade - see OnClosing
                    Close();
                    return;
                }
            }

            var rawTyped = FileNameBox.Text?.Trim() ?? "";
            var quoted = Multiselect && _mode == FileDialogMode.Open ? QuotedNames(rawTyped) : null;
            if (quoted != null && quoted.Length > 1)
            {
                var named = new string[quoted.Length];
                for (int index = 0; index < quoted.Length; index++)
                {
                    if (IsMalformedTyped(quoted[index])) { RejectTyped(); return; }
                    named[index] = ResolveTyped(quoted[index]);
                }
                if (CheckFileExists)
                {
                    var all = await AllExistAsync(named);
                    if (_closing) return;
                    if (all.State == Presence.Unknown) { ReportUnanswered(all.Reason); return; }
                    if (all.State == Presence.No)
                    {
                        SelMeta.Text = Loc("Str_Dlg_NoSuchFile");
                        FileNameBox.Focus();
                        return;
                    }
                }
                FileNames = named;
                FileName  = named[0];
                RememberAcceptedDirectory();
                _pendingResult = true;   // applied after the fade - see OnClosing
                Close();
                return;
            }

            var typed = rawTyped.Trim('"');
            if (TryApplyWildcard(typed)) return;
            if (typed.Length > 0 && IsMalformedTyped(typed)) { RejectTyped(); return; }
            if (typed.Length == 0)
            {
                // Nothing typed but a file is highlighted: take that.
                if (FileList.SelectedItem is PickerEntry sel && !sel.IsFolder) typed = sel.Name;
                else { FileNameBox.Focus(); return; }
            }

            var full = ResolveTyped(typed);

            if (_mode == FileDialogMode.Save)
            {
                // The extension follows the ACTIVE filter, so picking "CSV files" in the type
                // combo is enough to get a .csv - DefaultExt only decides when the filter names
                // no single extension (a wildcard or a multi-pattern entry).
                if (AddExtension)
                {
                    string? ext = ActiveFilterExt();
                    if (ext == null && !string.IsNullOrEmpty(DefaultExt))
                        ext = DefaultExt.StartsWith(".") ? DefaultExt : "." + DefaultExt;
                    full = SaveFileNamePolicy.ApplyExtension(full, ext, AddExtension, RequireFilterExtension);
                }

                // The directory must exist; we do not silently create trees on the user's behalf.
                var dir = Path.GetDirectoryName(full);
                if (CheckPathExists)
                {
                    var dirState = await CheckAsync(FolderExistsProbe, dir ?? "");
                    if (_closing) return;
                    if (dirState.State == Presence.Unknown) { ReportUnanswered(dirState.Reason); return; }
                    if (dirState.State == Presence.No)
                    {
                        SelMeta.Text = Loc("Str_Dlg_NoSuchFolder");
                        FileNameBox.Focus();
                        return;
                    }
                }

                // An overwrite check that cannot be answered is not "nothing there": Save stops rather than replace unasked.
                var overwriteState = OverwritePrompt ? await CheckAsync(FileExistsProbe, full) : (State: Presence.No, Reason: "");
                if (_closing) return;
                if (overwriteState.State == Presence.Unknown) { ReportUnanswered(overwriteState.Reason); return; }
                if (overwriteState.State == Presence.Yes)
                {
                    // The app's own themed ConfirmDialog, the same prompt every other question in
                    // the app uses, rather than a system MessageBox.
                    var confirm = new ConfirmDialog(
                        string.Format(Loc("Str_Dlg_OverwriteMsg"), Path.GetFileName(full)),
                        "",
                        Loc("Str_Btn_Replace"),
                        Loc("Str_Btn_Cancel")) { Owner = this };
                    // Enter keeps the existing file, as Windows' own replace prompt does.
                    confirm.DefaultToCancel();
                    confirm.ShowDialog();
                    if (_closing) return;
                    if (!confirm.Confirmed) { FileNameBox.Focus(); return; }
                }
            }
            else
            {
                if (CheckFileExists)
                {
                    var fileState = await CheckAsync(FileExistsProbe, full);
                    if (_closing) return;
                    if (fileState.State == Presence.Unknown) { ReportUnanswered(fileState.Reason); return; }
                    if (fileState.State == Presence.No)
                    {
                        SelMeta.Text = Loc("Str_Dlg_NoSuchFile");
                        FileNameBox.Focus();
                        FileNameBox.SelectAll();
                        return;
                    }
                }
            }

            FileName = full;
            FileNames = [full];   // always populated on success, so callers can read either
            RememberAcceptedDirectory();
            _pendingResult = true;   // applied after the fade - see OnClosing
            Close();
        }

        private async Task<(Presence State, string Reason)> AllExistAsync(string[] paths)
        {
            foreach (string path in paths)
            {
                var one = await CheckAsync(FileExistsProbe, path);
                if (one.State != Presence.Yes) return one;
            }
            return (Presence.Yes, "");
        }

        // _currentDir only changes after its listing succeeded, so it needs no existence check here.
        private void RememberAcceptedDirectory(string? accepted = null)
        {
            string remembered = string.IsNullOrEmpty(accepted) ? _currentDir : accepted!;
            if (remembered.Length > 0) Services.ThemeManager.SetSetting(LastDirKey, remembered);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Handled, or the press bubbles on to Resize_MouseDown AFTER DragMove's modal loop
            // returns - by then the button is UP, and WM_NCLBUTTONDOWN with no button held puts
            // Windows into its sticky keyboard-style size loop: the window chases the mouse,
            // resizing, until a click. (2026-07-30)
            e.Handled = true;
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { }   // the button was already up
        }

        // Enter on the file list opens a highlighted folder, or accepts the highlighted files like Open (and with
        // Shift, into a new window). Caught on the way down: the Extended-selection list handles Enter and
        // Shift+Enter itself, so OnKeyDown never sees them.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter && FileList.IsKeyboardFocusWithin && FileList.SelectedItems.Count > 0)
            {
                e.Handled = true;
                if (PickerAcceptRule.ListEnterOpensFolder(FileList.SelectedItems.Count, FileList.SelectedItem is PickerEntry { IsFolder: true }))
                    NavigateTo(((PickerEntry)FileList.SelectedItem).FullPath);
                else Accept();
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            if (HandleNewFolderKey(e)) { e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        // ---- edge resize, done by hand ----
        //
        // This dialog carries no shell:WindowChrome - on an AllowsTransparency window it fills its
        // own non-client area and that paints as a flat band around the card. So the 10px halo does
        // the job instead: work out which edge the pointer is in and hand the drag to Windows with
        // WM_NCLBUTTONDOWN, exactly as Shell/Chrome.cs does for the main window's corner grip.
        // Windows then runs its own resize loop, so this gets the real snapping and live preview
        // rather than a hand-rolled approximation. (2026-07-30)

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        /// <summary>Width of the grab band, matching the ResizeBorderThickness WindowChrome used.</summary>
        private const double ResizeEdge = 8;

        /// <summary>Which edge the pointer is in, or 0 for none.</summary>
        private int HitTestEdge(Point p)
        {
            bool left   = p.X <= ResizeEdge;
            bool right  = p.X >= ActualWidth  - ResizeEdge;
            bool top    = p.Y <= ResizeEdge;
            bool bottom = p.Y >= ActualHeight - ResizeEdge;

            if (top && left)     return HTTOPLEFT;
            if (top && right)    return HTTOPRIGHT;
            if (bottom && left)  return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left)            return HTLEFT;
            if (right)           return HTRIGHT;
            if (top)             return HTTOP;
            if (bottom)          return HTBOTTOM;
            return 0;
        }

        private void Resize_MouseMove(object sender, MouseEventArgs e)
        {
            Cursor = HitTestEdge(e.GetPosition(this)) switch
            {
                HTLEFT or HTRIGHT           => Cursors.SizeWE,
                HTTOP or HTBOTTOM           => Cursors.SizeNS,
                HTTOPLEFT or HTBOTTOMRIGHT  => Cursors.SizeNWSE,
                HTTOPRIGHT or HTBOTTOMLEFT  => Cursors.SizeNESW,
                _                           => Cursors.Arrow,
            };
        }

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Only a press on the halo Grid ITSELF may start a resize. Every press on the card
            // bubbles up here too (with OriginalSource somewhere in the card's tree), and a stale
            // bubbled press must never reach WM_NCLBUTTONDOWN - see TitleBar_MouseLeftButtonDown.
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            int ht = HitTestEdge(e.GetPosition(this));
            if (ht == 0) return;
            e.Handled = true;
            SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                        WM_NCLBUTTONDOWN, new IntPtr(ht), IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
