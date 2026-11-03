using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    // ═══════════════════════════════════════════════════════════
    //  FOLDER TREE  -  the file dialog's left pane, above places
    // ═══════════════════════════════════════════════════════════
    // Ported from KillerShell's FolderTree.cs (the family reference), minus what a modal file
    // dialog does not need: no demo mode, no expansion persistence (the dialog reveals the
    // current folder on open instead), no shell context menu suite.
    //
    // One node per folder, children loaded only when a node is actually expanded. A tree that
    // eagerly walked the disk would hang on the first drive with a deep tree on it. The lazy
    // load is the standard placeholder trick: every node that might have children gets a single
    // dummy child so WPF draws an expander arrow, and the real children replace it on first
    // expand. "Might have children" is deliberately optimistic - proving a folder empty costs
    // the very enumeration being deferred.
    public sealed class FolderNode : INotifyPropertyChanged
    {
        private static readonly FolderNode Placeholder = new("", "", false);

        /// <summary>Set by FileDialog from its persisted toggle, BEFORE the tree loads. Gates
        /// attribute-Hidden/System folders AND leading-dot names, same as the file list.</summary>
        internal static bool ShowHidden;

        public string Path { get; }
        public string Name { get; }

        // Drives get their own treatment: always expandable, never disappear mid-session, and
        // their label is "Local Disk (C:)" rather than a bare folder name.
        public bool IsDrive { get; }

        public ObservableCollection<FolderNode> Children { get; } = new();

        public FolderNode(string path, string name, bool mayHaveChildren)
        {
            Path = path;
            Name = name;
            if (mayHaveChildren) Children.Add(Placeholder);
        }

        public FolderNode(DriveInfo d)
        {
            Path    = d.RootDirectory.FullName;
            IsDrive = true;
            Name    = DriveLabel(d);
            Children.Add(Placeholder);
        }

        /// <summary>"Local Disk (C:)" style label, or the bare letter when the volume cannot be read.</summary>
        internal static string DriveLabel(DriveInfo d)
        {
            string letter = d.Name.TrimEnd('\\');
            try
            {
                // VolumeLabel throws on a drive that is not ready (empty optical, disconnected
                // share), which is exactly when we still want to show the letter.
                if (d.IsReady && !string.IsNullOrWhiteSpace(d.VolumeLabel))
                    return d.VolumeLabel + " (" + letter + ")";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return letter;
        }

        public bool IsLoaded { get; private set; }

        /// <summary>Lists a folder's subfolders. A test seam: tests swap in a slow or failing
        /// enumerator to exercise the tree without touching the disk.</summary>
        internal Func<string, List<FolderNode>> ChildEnumerator { get; set; } = EnumerateChildren;

        // One load or refresh at a time per node. A second caller waits for the first instead of
        // seeing IsLoaded already set over a still-empty child list.
        private readonly System.Threading.SemaphoreSlim _loadGate = new(1, 1);

        // A drive's REAL icon (USB, network, optical) via the real-path query; plain folders get
        // the shared generic folder icon so the tree never touches the disk per row.
        private ImageSource? _icon;
        private bool _iconRequested;
        public ImageSource? Icon
        {
            get
            {
                if (IsDrive && !_iconRequested)
                {
                    _iconRequested = true;
                    LoadIcon(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                }
                return _icon ?? Services.ShellIcons.Small(Path, true);
            }
        }

        private async void LoadIcon(System.Windows.Threading.Dispatcher dispatcher)
        {
            var image = await Task.Run(() => Services.ShellIcons.Place(Path)).ConfigureAwait(false);
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            await dispatcher.InvokeAsync(() => { _icon = image; Raise(nameof(Icon)); });
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; Raise(); } }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; Raise(); } }
        }

        /// <summary>
        /// Replaces the placeholder with the real subfolders. Enumeration happens off the UI
        /// thread - a slow or disconnected network drive would otherwise freeze the dialog for
        /// as long as the SMB timeout takes.
        /// </summary>
        public async Task LoadChildrenAsync()
        {
            await _loadGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (IsLoaded) return;
                string path = Path;
                List<FolderNode> kids = await Task.Run(() => ChildEnumerator(path)).ConfigureAwait(true);
                Children.Clear();
                foreach (var k in kids) Children.Add(k);
                IsLoaded = true;
            }
            finally { _loadGate.Release(); }
        }

        /// <summary>
        /// Re-enumerates this node's children in place, keeping whatever the user had open.
        /// Used when the show-hidden filter changes. Reconciled IN PLACE rather than cleared
        /// and refilled: Clear() removes the container holding the tree's selection, and WPF
        /// answers a lost selection by selecting the PARENT node - which would navigate the
        /// dialog up one folder on a toggle that has nothing to do with where you are.
        /// </summary>
        internal async Task RefreshAsync()
        {
            await _loadGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (!IsLoaded) return;

                string path = Path;
                var fresh = await Task.Run(() => ChildEnumerator(path)).ConfigureAwait(true);

                var byName = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in fresh) byName[n.Name] = n;

                for (int i = Children.Count - 1; i >= 0; i--)
                    if (!byName.ContainsKey(Children[i].Name)) Children.RemoveAt(i);

                var have = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in Children) have[c.Name] = c;

                for (int i = 0; i < fresh.Count; i++)
                {
                    if (have.TryGetValue(fresh[i].Name, out var existing))
                    {
                        // Anything the user has opened stays the SAME node object, so its own subtree
                        // and IsExpanded survive; only genuinely new entries get fresh nodes.
                        int at = Children.IndexOf(existing);
                        if (at != i) Children.Move(at, i);
                        await existing.RefreshAsync();   // its children are stale for the same reason
                    }
                    else Children.Insert(i, fresh[i]);
                }
            }
            finally { _loadGate.Release(); }
        }

        private static List<FolderNode> EnumerateChildren(string path)
        {
            var list = new List<FolderNode>();
            try
            {
                foreach (var d in new DirectoryInfo(path).EnumerateDirectories())
                {
                    // Same gate the file list applies: attribute Hidden/System AND leading-dot
                    // names, so the two panes never disagree about what exists. System is grouped
                    // with hidden rather than given its own switch - Explorer's separate option
                    // guards a handful of roots nobody browses to on purpose.
                    if (!ShowHidden)
                    {
                        var a = d.Attributes;
                        if ((a & FileAttributes.Hidden) != 0 || (a & FileAttributes.System) != 0) continue;
                        if (d.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    }

                    list.Add(new FolderNode(d.FullName, d.Name, mayHaveChildren: true));
                }
            }
            catch (UnauthorizedAccessException) { /* show what we can see */ }
            catch (IOException) { }

            list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
