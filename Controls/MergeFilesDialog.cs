using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;

namespace KillerPDF;

/// <summary>
/// The Merge dialog (#495): the files to merge, in order. Files and folders come from Add files and Add folder or from
/// a drop from File Explorer; a row is dragged to a new place or moved with Move up and Move down, and Delete removes it.
/// Where the merge builds a new file, Arrange pages shows every page of those files, which can be dragged into any
/// order or taken out before merging.
/// </summary>
internal sealed class MergeFilesDialog : Window
{
    private const string RowFormat = "KillerPDF.MergeRow";
    private const string PageFormat = "KillerPDF.MergePages";
    private const int ThumbWidth = 88, ThumbHeight = 112;
    private readonly MergeFileList _files = new();
    private readonly ListBox _list = new() { AllowDrop = true, SelectionMode = SelectionMode.Single };
    private readonly TextBlock _hint;
    private readonly Button _merge;
    private Point? _pressAt;
    private int _pressRow = -1;

    // The page view, offered only where the merge builds a new file and so can take a page order.
    private readonly bool _offerPages;
    private readonly MergePageArrangement _arrangement = new();
    private readonly HashSet<string> _arranged = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _grid = new() { AllowDrop = true, SelectionMode = SelectionMode.Extended };
    private readonly TextBlock _pageHint;
    private readonly Grid? _filesWell, _pagesWell;
    private readonly TextBlock? _pageTip;
    private readonly Button? _showPages, _showFiles;
    private bool _showingPages;
    private Point? _pagePressAt;
    private int _pagePressTile = -1;
    // A press on a tile of a several-tile selection keeps the selection so a drag takes all of it; let go without a
    // drag and only that tile stays selected, as a plain click does.
    private int _collapseToTile = -1;
    private PageDropMarker? _dropMarker;

    // Page counts and thumbnails load off the UI thread, one file at a time, once the page view is first shown.
    private readonly CancellationTokenSource _loading = new();
    private Task _loader = Task.CompletedTask;
    private bool _loadStarted, _closed;
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private MergePage[]? _draggedPages;
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MergePage, ImageSource> _thumbs = [];
    private readonly Dictionary<MergePage, Image> _tileImages = [];

    internal IReadOnlyList<string> Paths => _files.Paths;

    /// <summary>True when the dialog closed through Open separately, which only a drop offers.</summary>
    internal bool OpenSeparately { get; private set; }

    /// <summary>The page order to merge in, or null when it is every page of the files in file order.</summary>
    internal IReadOnlyList<MergePage>? PageOrder =>
        !_offerPages || _arrangement.IsPlainOrder(_files.Paths) ? null : [.. _arrangement.Pages];

    // A row can show another path than the file it merges: the open document goes in as a saved
    // snapshot but is listed under its own name and folder.
    private readonly IReadOnlyDictionary<string, string> _labels;

    internal MergeFilesDialog(Window owner, IEnumerable<string>? initial = null, bool offerOpenSeparately = false,
        IReadOnlyDictionary<string, string>? labels = null, bool offerPages = false)
    {
        _labels = labels ?? new Dictionary<string, string>();
        _offerPages = offerPages;
        Title = "KillerPDF - " + L("Str_MergeFiles_Title");
        Width = offerPages ? 600 : 520; Height = 500; MinWidth = 420; MinHeight = 320;
        UseLayoutRounding = true;
        AllowDrop = true;
        DialogFrame.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _merge = UiKit.Make(L("Str_MergeFiles_Merge"), accent: true);
        _merge.IsDefault = true;
        _merge.Click += (_, _) => { if (CanMerge) DialogResult = true; };
        // A drop of several files opens this dialog directly, so the other answer to the drop lives here too.
        var separate = UiKit.Make(L("Str_Drop_Separate"), accent: false);
        separate.Click += (_, _) => { if (_files.Paths.Count > 0) { OpenSeparately = true; DialogResult = true; } };
        Button[] confirm = offerOpenSeparately ? [cancel, separate, _merge] : [cancel, _merge];
        DockPanel footer;
        if (offerPages)
        {
            _showPages = DialogParts.LabeledAction("Str_MergeFiles_ShowPages", 0xE8A9, (_, _) => ShowPages(true));
            _showFiles = DialogParts.LabeledAction("Str_MergeFiles_ShowFiles", 0xE8FD, (_, _) => ShowPages(false));
            _showFiles.Visibility = Visibility.Collapsed;
            footer = DialogParts.Footer(confirm, _showPages, _showFiles);
        }
        else footer = DialogParts.Footer(confirm);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        DockPanel header = DialogParts.Header(L("Str_MergeFiles_Title"),
            DialogParts.IconAction("Str_MergeFiles_AddFiles", 0xE8E5, (_, _) => AddFiles()),
            DialogParts.IconAction("Str_MergeFiles_AddFolder", 0xE8B7, (_, _) => AddFolder()),
            DialogParts.IconAction("Str_Ctx_BmMoveUp", 0xE74A, (_, _) => MoveSelected(-1)),
            DialogParts.IconAction("Str_Ctx_BmMoveDown", 0xE74B, (_, _) => MoveSelected(1)),
            DialogParts.IconAction("Str_MergeFiles_Remove", 0xE74D, (_, _) => RemoveSelected()));
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);

        _hint = Hint();
        _pageHint = Hint();
        System.Windows.Automation.AutomationProperties.SetName(_list, L("Str_MergeFiles_Title"));
        _filesWell = DialogParts.Well(_list, _hint);
        if (offerPages)
        {
            _pageTip = new TextBlock
            {
                Text = L("Str_MergeFiles_PageHint"), TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Margin = new Thickness(2, 6, 2, 0), Visibility = Visibility.Collapsed
            };
            _pageTip.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            DockPanel.SetDock(_pageTip, Dock.Bottom);
            body.Children.Add(_pageTip);

            var tiles = new FrameworkElementFactory(typeof(WrapPanel));
            _grid.ItemsPanel = new ItemsPanelTemplate(tiles);
            ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Disabled);
            System.Windows.Automation.AutomationProperties.SetName(_grid, L("Str_MergeFiles_ShowPages"));
            _pagesWell = DialogParts.Well(_grid, _pageHint);
            _pagesWell.Visibility = Visibility.Collapsed;
            var wells = new Grid();
            wells.Children.Add(_filesWell);
            wells.Children.Add(_pagesWell);
            body.Children.Add(wells);
        }
        else body.Children.Add(_filesWell);

        _list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pressAt = e.GetPosition(_list);
            _pressRow = RowAt(e.GetPosition(_list));
        };
        _list.PreviewMouseMove += StartRowDrag;
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; }
        };
        _grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _pagePressAt = e.GetPosition(_grid);
            _pagePressTile = TileAt(e.GetPosition(_grid));
            _collapseToTile = -1;
            if (_pagePressTile >= 0 && Keyboard.Modifiers == ModifierKeys.None && _grid.SelectedItems.Count > 1
                && _grid.SelectedItems.Contains(_grid.Items[_pagePressTile]))
            {
                // The list would narrow the selection to this tile before the drag starts. (#495)
                _collapseToTile = _pagePressTile;
                _grid.Focus();
                e.Handled = true;
            }
        };
        _grid.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (_collapseToTile >= 0 && _collapseToTile < _grid.Items.Count)
            {
                _grid.SelectedItems.Clear();
                _grid.SelectedIndex = _collapseToTile;
            }
            _collapseToTile = -1;
        };
        _grid.PreviewMouseMove += StartPageDrag;
        _grid.PreviewKeyDown += OnGridKey;
        DragOver += OnDragOver;
        DragLeave += (_, _) => _dropMarker?.Hide();
        Drop += OnDrop;
        // The loader holds the files open, and the open document's snapshot is deleted once this closes.
        Closed += (_, _) => StopLoading();

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        if (initial is not null) _files.Add(initial);
        Refresh(_files.Paths.Count > 0 ? 0 : -1);
        Loaded += (_, _) => _list.Focus();
    }

    private static TextBlock Hint()
    {
        var hint = new TextBlock
        {
            Text = L("Str_MergeFiles_DropHint"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24), IsHitTestVisible = false
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
        return hint;
    }

    private bool CanMerge => _files.Paths.Count > 0 && (!_offerPages || _arrangement.Pages.Count > 0);

    private void AddFiles()
    {
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
        {
            Filter = L("Str_Filter_Pdf") + "|*.pdf", Title = L("Str_Dlg_SelectMerge"), Multiselect = true
        };
        if (picker.ShowDialog(this) == true) AddAndSelect(picker.FileNames);
    }

    private void AddFolder()
    {
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Folder)
        {
            Title = L("Str_MergeFiles_AddFolder"), AcceptHighlightedFolder = true
        };
        if (picker.ShowDialog(this) == true && !string.IsNullOrEmpty(picker.FileName)) AddAndSelect([picker.FileName]);
    }

    private void AddAndSelect(IEnumerable<string> paths)
    {
        int before = _files.Paths.Count;
        int pagesBefore = _arrangement.Pages.Count;
        _files.Add(paths);
        Refresh(_files.Paths.Count > before ? before : _list.SelectedIndex);
        if (_showingPages && _arrangement.Pages.Count > pagesBefore) SelectTiles(pagesBefore, _arrangement.Pages.Count - pagesBefore);
    }

    private void MoveSelected(int delta)
    {
        if (_showingPages) { StepPages(delta); return; }
        int from = _list.SelectedIndex;
        if (from < 0) return;
        int to = Math.Clamp(from + delta, 0, _files.Paths.Count - 1);
        _files.Move(from, to);
        _arrangement.FollowFileOrder(_files.Paths);
        Refresh(to);
    }

    private void RemoveSelected()
    {
        if (_showingPages) { RemovePages(); return; }
        int index = _list.SelectedIndex;
        if (index < 0) return;
        _files.RemoveAt(index);
        Refresh(Math.Min(index, _files.Paths.Count - 1));
    }

    private void Refresh(int select)
    {
        SyncPages();
        _list.Items.Clear();
        foreach (string file in _files.Paths)
        {
            string path = Shown(file);
            var name = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis };
            var folder = new TextBlock { Text = Path.GetDirectoryName(path), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            // The folder line takes the row's own foreground, faded, so it stays readable on the selection fill.
            folder.SetBinding(TextBlock.ForegroundProperty, RowForeground());
            folder.Opacity = 0.8;
            var row = new StackPanel { Margin = new Thickness(2, 2, 2, 2) };
            row.Children.Add(name);
            row.Children.Add(folder);
            var item = new ListBoxItem { Content = row, ToolTip = path };
            System.Windows.Automation.AutomationProperties.SetName(item, Path.GetFileName(path));
            _list.Items.Add(item);
        }
        _list.SelectedIndex = select;
        if (select >= 0) _list.ScrollIntoView(_list.Items[select]);
        _hint.Visibility = _files.Paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_showingPages) RefreshPages([]);
        _merge.IsEnabled = CanMerge;
    }

    private static System.Windows.Data.Binding RowForeground() => new("Foreground")
    {
        RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
    };

    private string Shown(string file) => _labels.TryGetValue(file, out string? shown) ? shown : file;

    // Files added to the list put their pages at the end of the page order; files taken out take theirs with them.
    private void SyncPages()
    {
        if (!_offerPages) return;
        foreach (string gone in _arranged.Where(path => !_files.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _arrangement.RemoveFile(gone);
            _arranged.Remove(gone);
            // Taken out and added back, a file loads again, because its count left with it.
            _queued.Remove(gone);
        }
        foreach (string file in _files.Paths)
        {
            if (!_arranged.Add(file)) continue;
            _arrangement.AddFile(file, null);
            if (_loadStarted) Queue(file);
        }
    }

    // ----- Page view ---------------------------------------------------------------------

    private void ShowPages(bool show)
    {
        if (!_offerPages || _showingPages == show) return;
        _showingPages = show;
        _filesWell!.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _pagesWell!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _pageTip!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _showPages!.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _showFiles!.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            // Pages need the room a few tiles across take.
            if (Width < 760) Width = 760;
            if (Height < 600) Height = 600;
            if (!_loadStarted)
            {
                _loadStarted = true;
                foreach (string file in _files.Paths) Queue(file);
            }
            RefreshPages([]);
            _grid.Focus();
        }
        else _list.Focus();
    }

    private void RefreshPages(IReadOnlyCollection<int> select)
    {
        _grid.Items.Clear();
        _tileImages.Clear();
        IReadOnlyList<MergePage> pages = _arrangement.Pages;
        foreach (MergePage page in pages) _grid.Items.Add(Tile(page));
        foreach (int index in select.Where(i => i >= 0 && i < pages.Count)) _grid.SelectedItems.Add(_grid.Items[index]);
        if (select.Count > 0 && select.Min() is int first && first < pages.Count) _grid.ScrollIntoView(_grid.Items[first]);
        _pageHint.Visibility = pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _merge.IsEnabled = CanMerge;
    }

    private void SelectTiles(int start, int count) => RefreshPages([.. Enumerable.Range(start, count)]);

    private ListBoxItem Tile(MergePage page)
    {
        string name = Path.GetFileName(Shown(page.Path));
        bool whole = page.Page == MergePage.WholeFile;
        var picture = new Grid { Width = ThumbWidth, Height = ThumbHeight };
        if (_thumbs.TryGetValue(page, out ImageSource? thumb) || !whole)
        {
            var image = new Image { Source = thumb, Stretch = Stretch.Uniform };
            picture.Children.Add(image);
            if (!whole) _tileImages[page] = image;
        }
        else
        {
            // A file still loading, or one whose pages cannot be read here (a protected or damaged file), merges whole.
            TextBlock glyph = DialogParts.Glyph(_unreadable.Contains(page.Path) ? 0xE72E : 0xE8A5, 28);
            glyph.Opacity = 0.6;
            picture.Children.Add(glyph);
        }
        var frame = new Border { Child = picture, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 4) };
        frame.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        string label = whole ? L("Str_MergeFiles_WholeFile") : string.Format(L("Str_PageLabel"), page.Page + 1);
        var caption = new TextBlock { Text = label, FontSize = 11, TextAlignment = TextAlignment.Center };
        var file = new TextBlock { Text = name, FontSize = 11, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8 };
        file.SetBinding(TextBlock.ForegroundProperty, RowForeground());
        var stack = new StackPanel { Width = ThumbWidth + 2, Margin = new Thickness(4) };
        stack.Children.Add(frame);
        stack.Children.Add(caption);
        stack.Children.Add(file);
        string spoken = whole ? name + ", " + label : string.Format(L("Str_MergeFiles_PageTile"), name, page.Page + 1);
        var item = new ListBoxItem { Content = stack, ToolTip = spoken };
        System.Windows.Automation.AutomationProperties.SetName(item, spoken);
        return item;
    }

    private int[] SelectedTiles() =>
        [.. _grid.SelectedItems.Cast<object>().Select(item => _grid.Items.IndexOf(item)).Where(i => i >= 0).Order()];

    private void StepPages(int delta)
    {
        int[] selected = SelectedTiles();
        if (selected.Length == 0) return;
        int landing = _arrangement.Step(selected, delta);
        SelectTiles(landing, selected.Length);
    }

    private void RemovePages()
    {
        int[] selected = SelectedTiles();
        if (selected.Length == 0) return;
        _arrangement.RemoveAt(selected);
        int next = Math.Min(selected[0], _arrangement.Pages.Count - 1);
        RefreshPages(next >= 0 ? [next] : []);
    }

    private void OnGridKey(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete) { RemovePages(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Left or Key.Up) { StepPages(-1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Right or Key.Down) { StepPages(1); e.Handled = true; }
    }

    private int TileAt(Point point)
    {
        if (_grid.InputHitTest(point) is not DependencyObject hit) return -1;
        while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return hit is ListBoxItem item ? _grid.Items.IndexOf(item) : -1;
    }

    // A tile dragged past the system drag distance takes the selection with it when it is part of it.
    private void StartPageDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pagePressAt is not { } start || _pagePressTile < 0) return;
        Vector moved = e.GetPosition(_grid) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        int tile = _pagePressTile;
        _pagePressAt = null;
        _collapseToTile = -1;
        int[] selected = SelectedTiles();
        int[] moving = selected.Contains(tile) ? selected : [tile];
        // The drag holds the pages, not their places: a page count can arrive while it runs and open a whole
        // file up into its pages, which moves every place after it.
        _draggedPages = [.. moving.Select(index => _arrangement.Pages[index])];
        try { DragDrop.DoDragDrop(_grid, new DataObject(PageFormat, moving), DragDropEffects.Move); }
        finally { _draggedPages = null; _dropMarker?.Hide(); }
    }

    // Where a drop on the page view goes, worked out from the tiles' bounds so the space between tiles counts. (#495)
    private MergeDropTarget DropTarget(Point at)
    {
        var tiles = new List<Rect>(_grid.Items.Count);
        foreach (ListBoxItem item in _grid.Items)
            tiles.Add(item.TransformToAncestor(_grid).TransformBounds(new Rect(item.RenderSize)));
        return MergeDropSlot.Find(tiles, at);
    }

    private bool OverGrid(Point spot) =>
        _showingPages && spot.X >= 0 && spot.Y >= 0 && spot.X <= _grid.ActualWidth && spot.Y <= _grid.ActualHeight;

    // The line between tiles where a drop goes, the same look as the Outlines panel's drop line.
    private void ShowDropMarker(MergeDropTarget target)
    {
        if (_dropMarker is null)
        {
            if (AdornerLayer.GetAdornerLayer(_grid) is not AdornerLayer layer) return;
            _dropMarker = new PageDropMarker(_grid);
            layer.Add(_dropMarker);
        }
        double top = Math.Max(0, target.Top), bottom = Math.Min(_grid.ActualHeight, target.Bottom);
        if (_arrangement.Pages.Count == 0 || bottom <= top) { _dropMarker.Hide(); return; }
        _dropMarker.Show(target.X, top, bottom, TryFindResource("PrimaryBrush") as Brush ?? Brushes.SteelBlue);
    }

    private sealed class PageDropMarker : Adorner
    {
        private double _x, _top, _bottom;
        private Brush _brush = Brushes.SteelBlue;

        internal PageDropMarker(UIElement grid) : base(grid)
        {
            IsHitTestVisible = false;
            Visibility = Visibility.Collapsed;
        }

        internal void Show(double x, double top, double bottom, Brush brush)
        {
            (_x, _top, _bottom, _brush) = (x, top, bottom, brush);
            Visibility = Visibility.Visible;
            InvalidateVisual();
        }

        internal void Hide() => Visibility = Visibility.Collapsed;

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(_brush, null, new Rect(_x - 1, _top + 3, 2, Math.Max(0, _bottom - _top - 6)));
            dc.DrawEllipse(_brush, null, new Point(_x, _top + 3), 3, 3);
        }
    }

    private void Queue(string path)
    {
        if (!_queued.Add(path) || _closed) return;
        CancellationToken ct = _loading.Token;
        // LazyCancellation keeps each link waiting for the one before it, so closing waits for the load that is running.
        _loader = _loader.ContinueWith(_ => LoadFile(path, ct), ct, TaskContinuationOptions.LazyCancellation,
            TaskScheduler.Default);
    }

    // Runs on the thread pool and never waits on the UI thread, so closing can wait for it.
    private void LoadFile(string path, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        PdfPageRenderSession session;
        try { session = PdfPageRenderSession.OpenEngineFirst(path, ThumbWidth, ThumbHeight, null); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Post(() => { _unreadable.Add(path); if (_showingPages) RefreshPages(SelectedTiles()); });
            return;
        }
        using (session)
        {
            int count = session.PageCount;
            Post(() => OnPageCount(path, count));
            for (int page = 0; page < count && !ct.IsCancellationRequested; page++)
            {
                try
                {
                    var rendered = session.RenderFittedPage(page, ThumbWidth, ThumbHeight, cancellationToken: ct);
                    var bitmap = BitmapSource.Create(rendered.Width, rendered.Height, 96, 96, PixelFormats.Bgra32, null,
                        rendered.Pixels.ToArray(), rendered.Width * 4);
                    bitmap.Freeze();
                    var key = new MergePage(path, page);
                    Post(() => OnThumb(key, bitmap));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }   // that tile stays blank
            }
        }
    }

    private void Post(Action action) =>
        Dispatcher.BeginInvoke(() => { if (!_closed) action(); });

    private void OnPageCount(string path, int count)
    {
        if (_arrangement.HasCount(path)) return;
        // The selection keeps to the same pages while a file's placeholder turns into its pages.
        MergePage[] selected = [.. SelectedTiles().Select(index => _arrangement.Pages[index])];
        _arrangement.ExpandFile(path, count);
        if (!_showingPages) { _merge.IsEnabled = CanMerge; return; }
        var pages = _arrangement.Pages;
        RefreshPages([.. selected.Select(page => IndexOf(pages, page)).Where(index => index >= 0)]);
    }

    private static int IndexOf(IReadOnlyList<MergePage> pages, MergePage page)
    {
        for (int index = 0; index < pages.Count; index++) if (pages[index] == page) return index;
        return -1;
    }

    private void OnThumb(MergePage page, ImageSource bitmap)
    {
        _thumbs[page] = bitmap;
        if (_tileImages.TryGetValue(page, out Image? image)) image.Source = bitmap;
    }

    private void StopLoading()
    {
        _closed = true;
        _loading.Cancel();
        try { _loader.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }

    // ----- File rows ---------------------------------------------------------------------

    private int RowAt(Point point)
    {
        if (_list.InputHitTest(point) is not DependencyObject hit) return -1;
        while (hit is not null and not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        return hit is ListBoxItem item ? _list.Items.IndexOf(item) : -1;
    }

    // A row dragged past the system drag distance moves to where it is dropped.
    private void StartRowDrag(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressAt is not { } start || _pressRow < 0) return;
        Vector moved = e.GetPosition(_list) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        int row = _pressRow;
        _pressAt = null;
        DragDrop.DoDragDrop(_list, new DataObject(RowFormat, row), DragDropEffects.Move);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(RowFormat) || e.Data.GetDataPresent(PageFormat) ? DragDropEffects.Move
            : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Point spot = e.GetPosition(_grid);
        bool pagesOrFiles = e.Data.GetDataPresent(PageFormat) || e.Data.GetDataPresent(DataFormats.FileDrop);
        if (pagesOrFiles && OverGrid(spot)) ShowDropMarker(DropTarget(spot));
        else _dropMarker?.Hide();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropMarker?.Hide();
        if (e.Data.GetDataPresent(PageFormat))
        {
            if (_draggedPages is null) return;
            Point spot = e.GetPosition(_grid);
            if (!OverGrid(spot)) return;
            int slot = DropTarget(spot).Index;
            int[] pages = _arrangement.IndicesOf(_draggedPages);
            int landing = _arrangement.MoveTo(pages, slot);
            if (landing >= 0) SelectTiles(landing, pages.Length);
            return;
        }
        if (e.Data.GetData(RowFormat) is int from)
        {
            // Below the last row moves to the end; let go outside the list and nothing moves.
            Point at = e.GetPosition(_list);
            if (at.X < 0 || at.Y < 0 || at.X > _list.ActualWidth || at.Y > _list.ActualHeight) return;
            int target = RowAt(at);
            int to = target < 0 ? _files.Paths.Count - 1 : target;
            _files.Move(from, to);
            _arrangement.FollowFileOrder(_files.Paths);
            Refresh(Math.Clamp(to, 0, _files.Paths.Count - 1));
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            // On the page view, the new files' pages go where they were dropped, not at the end. (#495)
            Point spot = e.GetPosition(_grid);
            if (OverGrid(spot)) InsertDropped(dropped, DropTarget(spot).Index);
            else AddAndSelect(dropped);
        }
    }

    private void InsertDropped(IEnumerable<string> paths, int slot)
    {
        int before = _files.Paths.Count;
        _files.Add(paths);
        int at = Math.Clamp(slot, 0, _arrangement.Pages.Count), count = 0;
        foreach (string file in _files.Paths.Skip(before))
        {
            if (!_arranged.Add(file)) continue;
            int added = _arrangement.InsertFile(file, null, at + count);
            count += added;
            if (_loadStarted) Queue(file);
        }
        Refresh(_files.Paths.Count > before ? before : _list.SelectedIndex);
        if (count > 0) SelectTiles(at, count);
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
