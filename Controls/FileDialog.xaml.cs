using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerPDF.Controls
{
    /// <summary>Open or Save. Picked at construction; changes the accept button and the rules.</summary>
    public enum FileDialogMode { Open, Save }

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
    /// Multiselect is deliberately NOT implemented yet - nothing in the app needs it, and
    /// a half-working Multiselect is worse than an absent one. Add it when something wants it.
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

        /// <summary>Every path chosen. Always populated on success, so a caller can read this
        /// whether or not it asked for Multiselect - single selection yields one entry, matching
        /// the Win32 dialogs' FileNames. FileName remains the first of them.</summary>
        public string[] FileNames { get; private set; } = [];

        // ── internals ────────────────────────────────────────────────────────────

        private readonly FileDialogMode _mode;

        public ObservableCollection<PickerPlace> Places  { get; } = [];
        public ObservableCollection<PickerEntry> Entries { get; } = [];

        private readonly List<PickerEntry> _raw = [];
        private string _currentDir = string.Empty;
        private bool _navigating;
        private bool _built;                 // suppresses filter events during construction
        private int  _viewMode;              // 0 list, 1 icons, 2 details
        private int  _sortKey;               // 0 name, 1 size, 2 modified
        private bool _sortAsc = true;

        // Per-filter-entry patterns, parallel to FilterCombo's items. Empty list = show all.
        private readonly List<string[]> _filterPatterns = [];

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
        /// DialogResult to null whenever a close is cancelled. Accept therefore records what it
        /// wants in _pendingResult and the assignment happens in the fade's completion callback,
        /// where nothing will cancel it. Assigning DialogResult there also closes the window, which
        /// is why that branch does not call Close() as well.</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_fadingOut)
            {
                _fadingOut = true;
                e.Cancel = true;
                Anim.FadeOut(RootFade, () =>
                {
                    if (_pendingResult.HasValue) DialogResult = _pendingResult;   // this closes it
                    else Close();                                                 // cancel path
                });
                return;
            }
            base.OnClosing(e);
        }

        public FileDialog(FileDialogMode mode = FileDialogMode.Open)
        {
            _mode = mode;
            InitializeComponent();
            Loaded += (_, _) => Anim.FadeIn(RootFade);

            // Size and placement remembered separately from the folder picker: this dialog is a
            // different shape and sharing the keys would make each one fight the other.
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (double.TryParse(App.GetSetting("FileDlgW"),
                        System.Globalization.NumberStyles.Float, ci, out double w) &&
                    double.TryParse(App.GetSetting("FileDlgH"),
                        System.Globalization.NumberStyles.Float, ci, out double h))
                {
                    Width  = Math.Max(MinWidth,  Math.Min(w, SystemParameters.WorkArea.Width));
                    Height = Math.Max(MinHeight, Math.Min(h, SystemParameters.WorkArea.Height));
                }
                if (double.TryParse(App.GetSetting("FileDlgX"),
                        System.Globalization.NumberStyles.Float, ci, out double x) &&
                    double.TryParse(App.GetSetting("FileDlgY"),
                        System.Globalization.NumberStyles.Float, ci, out double y))
                {
                    var wa = SystemParameters.WorkArea;
                    if (x > wa.Left - Width + 80 && x < wa.Right - 80 &&
                        y > wa.Top - 20 && y < wa.Bottom - 80)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = x;
                        Top  = y;
                    }
                }
                if (double.TryParse(App.GetSetting(PlacesHKey),
                        System.Globalization.NumberStyles.Float, ci, out double ph) && ph >= 56)
                    PlacesRow.Height = new GridLength(Math.Min(ph, 600));
            }
            catch { /* registry unavailable - defaults are fine */ }

            _showHidden = App.GetSetting(ShowHiddenKey) == "1";
            FolderNode.ShowHidden = _showHidden;
            ApplyShowHiddenButton();

            Closing += (_, _) =>
            {
                try
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    App.SetSetting("FileDlgW", ActualWidth.ToString(ci));
                    App.SetSetting("FileDlgH", ActualHeight.ToString(ci));
                    App.SetSetting("FileDlgX", Left.ToString(ci));
                    App.SetSetting("FileDlgY", Top.ToString(ci));
                    App.SetSetting(PlacesHKey, PlacesRow.ActualHeight.ToString(ci));
                }
                catch { /* not worth failing the close */ }
            };

            // NO DwmChrome calls, deliberately. On an AllowsTransparency window the DWM corner
            // preference makes DWM composite its own rounded frame around the WINDOW rect - the
            // transparent 10px halo included - and SetThemeBorder tints it: that WAS the gray
            // band. The other four dialogs are AllowsTransparency with no DWM calls and have
            // never shown one; the card draws its own border and shadow, so DWM has nothing to
            // add. The WM_ERASEBKGND hook that lived here went too - a layered window is rendered
            // via UpdateLayeredWindow and never receives it. (Steve, 2026-07-30, fifth attempt.)
        }

        /// <summary>
        /// Sets the owner and shows modally. Everything that depends on Filter / FileName /
        /// InitialDirectory is wired HERE rather than in the constructor, because callers set
        /// those as object-initializer properties after construction.
        /// </summary>
        public bool? ShowDialog(Window? owner)
        {
            if (owner != null && owner.IsVisible) Owner = owner;

            HeadingText.Text    = Title ?? "";
            AcceptButton.Content = Loc(_mode == FileDialogMode.Save ? "Str_Btn_Save" : "Str_Btn_Open");
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
            string startDir = InitialDirectory;
            string seedName = "";
            if (!string.IsNullOrWhiteSpace(FileName))
            {
                if (FileName.IndexOfAny(['\\', '/']) >= 0)
                {
                    var d = Path.GetDirectoryName(FileName);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) startDir = d!;
                    seedName = Path.GetFileName(FileName);
                }
                else seedName = FileName;
            }
            if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir))
                startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            _built = true;
            NavigateTo(startDir);
            FileNameBox.Text = seedName;

            // Save: preselect the stem so typing replaces the name but keeps the extension
            // visible. Open: caret at the end.
            FileNameBox.Focus();
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

            int idx = FilterIndex - 1;
            FilterCombo.SelectedIndex = idx >= 0 && idx < FilterCombo.Items.Count ? idx : 0;
            FilterLabel.Visibility = FilterCombo.Visibility =
                FilterCombo.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_built) return;
            FilterIndex = FilterCombo.SelectedIndex + 1;

            // Save mode follows the Win32 dialogs: switching the type swaps the typed name's
            // extension - but only when the current one belongs to another entry of THIS filter.
            // An extension the user typed by hand is theirs and is left alone.
            if (_mode == FileDialogMode.Save)
            {
                string? newExt = ActiveFilterExt();
                string  name   = FileNameBox.Text?.Trim() ?? "";
                string  cur    = name.Length == 0 ? "" : Path.GetExtension(name);
                if (newExt != null && cur.Length > 0 &&
                    !cur.Equals(newExt, StringComparison.OrdinalIgnoreCase) &&
                    AllFilterExts().Contains(cur, StringComparer.OrdinalIgnoreCase))
                {
                    FileNameBox.Text = Path.ChangeExtension(name, newExt);
                }
            }

            ApplySort();
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
                return p.Substring(1);
            return null;
        }

        /// <summary>Every concrete extension the filter list names, for the swap test above.</summary>
        private IEnumerable<string> AllFilterExts()
        {
            foreach (var pats in _filterPatterns)
                foreach (var p in pats)
                    if (p.Length > 2 && p.StartsWith("*.") && p.IndexOfAny(['*', '?'], 2) < 0)
                        yield return p.Substring(1);
        }

        /// <summary>True when the name passes the active filter. Folders are never filtered out.</summary>
        private bool PassesFilter(PickerEntry en)
        {
            if (en.IsFolder) return true;
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
            Places.Clear();

            foreach (var p in PinnedPaths())
                AddPlace(LabelFor(p), p, pinned: true);

            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string label;
                try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel.Trim(); }
                catch { label = d.DriveType.ToString(); }
                AddPlace($"{d.Name.TrimEnd('\\')}  {label}", d.RootDirectory.FullName);
            }
        }

        /// <summary>
        /// The persisted pin list. First run (key absent, null) seeds the five standard folders;
        /// an EMPTY stored value means the user unpinned everything and must stay empty.
        /// </summary>
        private static List<string> PinnedPaths()
        {
            string? saved = App.GetSetting(PinnedKey);
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

        private void AddPlace(string label, string path, bool pinned = false)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Places.Add(new PickerPlace(label, path, pinned));
        }

        private void PinPlace(string path)
        {
            var list = PinnedPaths();
            if (list.Any(p => p.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(path);
            App.SetSetting(PinnedKey, string.Join("|", list));
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
            App.SetSetting(PinnedKey, string.Join("|", list));
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

        private void NavigateTo(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            _navigating = true;
            _currentDir  = dir;
            PathBox.Text = dir;
            _raw.Clear();

            try
            {
                // The toggle gates two things together: attribute Hidden/System AND leading-dot
                // names - the Unix convention is all over a Windows home folder (.gradle, .ssh)
                // and those carry no Hidden attribute. Same gate in the folder tree (FolderTree.cs).
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    DirectoryInfo info;
                    try { info = new DirectoryInfo(sub); } catch { continue; }
                    if (!_showHidden)
                    {
                        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (info.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    }
                    _raw.Add(new PickerEntry(info.Name, sub, true, 0, SafeTime(() => info.LastWriteTime)));
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(file); } catch { continue; }
                    if (!_showHidden)
                    {
                        if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (fi.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    }
                    _raw.Add(new PickerEntry(fi.Name, file, false, SafeLen(fi), SafeTime(() => fi.LastWriteTime)));
                }
            }
            catch { /* unauthorized / unreadable - show what we have */ }

            ApplySort();
            UpButton.IsEnabled = Directory.GetParent(dir) != null;
            UpdateInfoSummary();
            SyncPlacesSelection();
            _navigating = false;

            RecordRecent(dir);
            _ = RevealInTree(dir);
        }

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
            var parent = Directory.GetParent(_currentDir);
            if (parent != null) NavigateTo(parent.FullName);
        }

        private void Places_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (PlacesList.SelectedItem is PickerPlace p) NavigateTo(p.Path);
        }

        // ── Folder tree (ported from KillerShell, see Controls/FolderTree.cs) ────

        /// <summary>Ready drives only - an empty optical drive or a dropped mapping would sit
        /// there as a node that throws the moment anyone touches it.</summary>
        private void InitTree()
        {
            if (TreeRoots.Count > 0) return;
            FolderTreeCtl.ItemsSource = TreeRoots;

            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (IOException) { return; }

            foreach (var d in drives)
            {
                bool ready;
                try { ready = d.IsReady; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (ready) TreeRoots.Add(new FolderNode(d));
            }

            // Edge fades follow the scroll position (KillerShell TreePanel.cs). ScrollChanged is
            // handled at the TreeView rather than dug out of its template: it bubbles, so the
            // inner ScrollViewer is reached without needing to have found it first. Loaded and
            // SizeChanged cover the passes where nothing scrolled but the extent moved.
            FolderTreeCtl.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => { SyncTreeEdgeFades(); SyncTreeFade(); }));
            FolderTreeCtl.SizeChanged += (_, _) => { SyncTreeEdgeFades(); SyncTreeFade(); };
            FolderTreeCtl.Loaded      += (_, _) => { SyncTreeEdgeFades(); SyncTreeFade(); };

            // The places list gets the same treatment (Steve, 2026-07-30). No scrollbar lift:
            // horizontal scrolling is disabled on it.
            PlacesList.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => SyncPlacesEdgeFades()));
            PlacesList.SizeChanged += (_, _) => SyncPlacesEdgeFades();
            PlacesList.Loaded      += (_, _) => SyncPlacesEdgeFades();
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

        // Height is NaN until the border has been laid out, hence the fallback.
        private static double Ramp(double distance, double height, double fallback)
        {
            double h = double.IsNaN(height) || height <= 0 ? fallback : height;
            return Math.Min(1, Math.Max(0, distance) / h);
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
            NavigateTo(node.Path);
        }

        private FolderNode? _treeMenuNode;

        private void FolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _treeMenuNode = NodeUnder(Mouse.DirectlyOver as DependencyObject)
                         ?? NodeUnder(e.OriginalSource as DependencyObject);
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
        private async Task RevealInTree(string folder)
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
                current.IsExpanded = true;
            }

            _treeSyncing = true;
            current.IsSelected = true;
            _treeSyncing = false;
        }

        private static IEnumerable<string> RelativeSegments(string rootPath, string fullPath)
        {
            string rest = fullPath.Substring(rootPath.Length);
            return rest.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                              StringSplitOptions.RemoveEmptyEntries);
        }

        // ── Recent locations ─────────────────────────────────────────────────────

        private static List<string> LoadRecents()
            => [.. (App.GetSetting(RecentsKey) ?? "")
               .Split('|').Where(s => s.Length > 0)];

        private static void RecordRecent(string dir)
        {
            var list = LoadRecents();
            list.RemoveAll(p => p.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            list.Insert(0, dir);
            if (list.Count > RecentsMax) list.RemoveRange(RecentsMax, list.Count - RecentsMax);
            App.SetSetting(RecentsKey, string.Join("|", list));
        }

        private void RecentsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Stale entries (unplugged drive, deleted folder) are filtered at open rather than
            // scrubbed from the store - the drive may be back tomorrow.
            var list = LoadRecents().Where(Directory.Exists).ToList();
            if (list.Count == 0) return;

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
            App.SetSetting(ShowHiddenKey, _showHidden ? "1" : "0");
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
                if (!en.IsFolder) FileNameBox.Text = en.Name;
                SelName.Text = en.Name;
                SelMeta.Text = en.IsFolder ? en.ModifiedLabel : $"{en.SizeLabel}  |  {en.ModifiedLabel}";
            }
            else UpdateInfoSummary();
        }

        private void Files_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is not PickerEntry en) return;
            if (en.IsFolder) NavigateTo(en.FullPath);
            else Accept();
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var typed = PathBox.Text?.Trim();
            if (!string.IsNullOrEmpty(typed) && Directory.Exists(typed)) NavigateTo(typed!);
            e.Handled = true;
        }

        private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            var typed = FileNameBox.Text?.Trim() ?? "";

            // A directory typed into the name box navigates instead of accepting - matches the
            // Win32 dialogs, and is how people paste a path in.
            if (typed.Length > 0)
            {
                var asDir = Path.IsPathRooted(typed) ? typed : Path.Combine(_currentDir, typed);
                if (Directory.Exists(asDir)) { NavigateTo(asDir); FileNameBox.Clear(); return; }
            }

            // A wildcard retargets the listing rather than naming a file.
            if (typed.IndexOfAny(['*', '?']) >= 0)
            {
                _filterPatterns.Insert(0, [typed]);
                FilterCombo.Items.Insert(0, typed);
                FilterCombo.SelectedIndex = 0;
                FileNameBox.Clear();
                return;
            }

            Accept();
        }

        private void UpdateInfoSummary()
        {
            int folders = _raw.Count(x => x.IsFolder);
            int shown   = Entries.Count(x => !x.IsFolder);
            var leaf    = Path.GetFileName(_currentDir.TrimEnd('\\'));
            SelName.Text = leaf.Length == 0 ? _currentDir : leaf;
            SelMeta.Text = string.Format(Loc("Str_Sum_Counts"), folders, shown);
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

        // ── Sorting ──────────────────────────────────────────────────────────────

        private void SortName_Click(object sender, RoutedEventArgs e)     => SetSort(0);
        private void SortSize_Click(object sender, RoutedEventArgs e)     => SetSort(1);
        private void SortModified_Click(object sender, RoutedEventArgs e) => SetSort(2);

        private void SetSort(int key)
        {
            if (_sortKey == key) _sortAsc = !_sortAsc;
            else { _sortKey = key; _sortAsc = true; }
            ApplySort();
        }

        /// <summary>
        /// Rebuilds Entries from _raw: filter applied, folders always before files, then the
        /// active sort key. Folders-first is not a sort key - it is the frame the sort runs in.
        /// </summary>
        private void ApplySort()
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

            Entries.Clear();
            foreach (var e in ordered) Entries.Add(e);

            NameArrow.Text = _sortKey == 0 ? (_sortAsc ? ArrowUp : ArrowDown) : "";
            SizeArrow.Text = _sortKey == 1 ? (_sortAsc ? ArrowUp : ArrowDown) : "";
            ModArrow.Text  = _sortKey == 2 ? (_sortAsc ? ArrowUp : ArrowDown) : "";

            EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Accept / cancel ──────────────────────────────────────────────────────

        private void OK_Click(object sender, RoutedEventArgs e) => Accept();

        /// <summary>
        /// Resolves the name box to a full path and applies the mode's rules. Anything that fails
        /// leaves the dialog OPEN with focus back in the name box - a file dialog that closes on a
        /// bad name and makes you start over is the worst outcome.
        /// </summary>
        private void Accept()
        {
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
                if (picked.Length > 1)
                {
                    FileNames = picked;
                    FileName  = picked[0];
                    _pendingResult = true;   // applied after the fade - see OnClosing
                    Close();
                    return;
                }
            }

            var typed = FileNameBox.Text?.Trim().Trim('"') ?? "";
            if (typed.Length == 0)
            {
                // Nothing typed but a file is highlighted: take that.
                if (FileList.SelectedItem is PickerEntry sel && !sel.IsFolder) typed = sel.Name;
                else { FileNameBox.Focus(); return; }
            }

            var full = Path.IsPathRooted(typed) ? typed : Path.Combine(_currentDir, typed);

            if (_mode == FileDialogMode.Save)
            {
                // The extension follows the ACTIVE filter, so picking "CSV files" in the type
                // combo is enough to get a .csv - DefaultExt only decides when the filter names
                // no single extension (a wildcard or a multi-pattern entry).
                if (AddExtension && string.IsNullOrEmpty(Path.GetExtension(full)))
                {
                    string? ext = ActiveFilterExt();
                    if (ext == null && !string.IsNullOrEmpty(DefaultExt))
                        ext = DefaultExt.StartsWith(".") ? DefaultExt : "." + DefaultExt;
                    if (ext != null) full += ext;
                }

                // The directory must exist; we do not silently create trees on the user's behalf.
                var dir = Path.GetDirectoryName(full);
                if (CheckPathExists && (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)))
                {
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFolder");
                    FileNameBox.Focus();
                    return;
                }

                if (OverwritePrompt && File.Exists(full))
                {
                    // Killendar asks this through its own ConfirmDialog. KillerPDF already has a
                    // themed confirm (KillerDialog) that every other prompt in the app uses, so
                    // this goes through that instead of importing a second one - two dialogs that
                    // ask the same kind of question is exactly how a UI starts to look stitched
                    // together. Same question, same buttons, one implementation.
                    var answer = KillerDialog.Show(this,
                        string.Format(Loc("Str_Dlg_OverwriteMsg"), Path.GetFileName(full)),
                        "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) { FileNameBox.Focus(); return; }
                }
            }
            else
            {
                if (CheckFileExists && !File.Exists(full))
                {
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFile");
                    FileNameBox.Focus();
                    FileNameBox.SelectAll();
                    return;
                }
            }

            FileName = full;
            FileNames = [full];   // always populated on success, so callers can read either
            _pendingResult = true;   // applied after the fade - see OnClosing
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Handled, or the press bubbles on to Resize_MouseDown AFTER DragMove's modal loop
            // returns - by then the button is UP, and WM_NCLBUTTONDOWN with no button held puts
            // Windows into its sticky keyboard-style size loop: the window chases the mouse,
            // resizing, until a click. (Steve, 2026-07-30.)
            e.Handled = true;
            DragMove();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        // ---- edge resize, done by hand ----
        //
        // This dialog carries no shell:WindowChrome - on an AllowsTransparency window it fills its
        // own non-client area and that paints as a flat band around the card. So the 10px halo does
        // the job instead: work out which edge the pointer is in and hand the drag to Windows with
        // WM_NCLBUTTONDOWN, exactly as Shell/Chrome.cs does for the main window's corner grip.
        // Windows then runs its own resize loop, so this gets the real snapping and live preview
        // rather than a hand-rolled approximation. (Steve, 2026-07-30.)

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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
