using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerPDF.Services;

namespace KillerPDF;

/// <summary>
/// The Merge dialog (#495): the files to merge, in order. Files and folders come from Add files and Add folder or from
/// a drop from File Explorer; a row is dragged to a new place or moved with Move up and Move down, and Delete removes it.
/// </summary>
internal sealed class MergeFilesDialog : Window
{
    private const string RowFormat = "KillerPDF.MergeRow";
    private readonly MergeFileList _files = new();
    private readonly ListBox _list = new() { AllowDrop = true, SelectionMode = SelectionMode.Single };
    private readonly TextBlock _hint;
    private readonly Button _merge;
    private Point? _pressAt;
    private int _pressRow = -1;

    internal IReadOnlyList<string> Paths => _files.Paths;

    /// <summary>True when the dialog closed through Open separately, which only a drop offers.</summary>
    internal bool OpenSeparately { get; private set; }

    // A row can show another path than the file it merges: the open document goes in as a saved
    // snapshot but is listed under its own name and folder.
    private readonly IReadOnlyDictionary<string, string> _labels;

    internal MergeFilesDialog(Window owner, IEnumerable<string>? initial = null, bool offerOpenSeparately = false,
        IReadOnlyDictionary<string, string>? labels = null)
    {
        _labels = labels ?? new Dictionary<string, string>();
        Title = "KillerPDF - " + L("Str_MergeFiles_Title");
        Width = 520; Height = 500; MinWidth = 420; MinHeight = 320;
        UseLayoutRounding = true;
        AllowDrop = true;
        DialogFrame.Configure(this, owner);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _merge = UiKit.Make(L("Str_MergeFiles_Merge"), accent: true);
        _merge.IsDefault = true;
        _merge.Click += (_, _) => { if (_files.Paths.Count > 0) DialogResult = true; };
        // A drop of several files opens this dialog directly, so the other answer to the drop lives here too.
        var separate = UiKit.Make(L("Str_Drop_Separate"), accent: false);
        separate.Click += (_, _) => { if (_files.Paths.Count > 0) { OpenSeparately = true; DialogResult = true; } };
        DockPanel footer = DialogParts.Footer(offerOpenSeparately ? [cancel, separate, _merge] : [cancel, _merge]);
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

        _hint = new TextBlock
        {
            Text = L("Str_MergeFiles_DropHint"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24), IsHitTestVisible = false
        };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
        System.Windows.Automation.AutomationProperties.SetName(_list, L("Str_MergeFiles_Title"));
        body.Children.Add(DialogParts.Well(_list, _hint));

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
        DragOver += OnDragOver;
        Drop += OnDrop;

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
        if (initial is not null) _files.Add(initial);
        Refresh(_files.Paths.Count > 0 ? 0 : -1);
        Loaded += (_, _) => _list.Focus();
    }

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
        _files.Add(paths);
        Refresh(_files.Paths.Count > before ? before : _list.SelectedIndex);
    }

    private void MoveSelected(int delta)
    {
        int from = _list.SelectedIndex;
        if (from < 0) return;
        int to = Math.Clamp(from + delta, 0, _files.Paths.Count - 1);
        _files.Move(from, to);
        Refresh(to);
    }

    private void RemoveSelected()
    {
        int index = _list.SelectedIndex;
        if (index < 0) return;
        _files.RemoveAt(index);
        Refresh(Math.Min(index, _files.Paths.Count - 1));
    }

    private void Refresh(int select)
    {
        _list.Items.Clear();
        foreach (string file in _files.Paths)
        {
            string path = _labels.TryGetValue(file, out string? shown) ? shown : file;
            var name = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis };
            var folder = new TextBlock { Text = Path.GetDirectoryName(path), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            // The folder line takes the row's own foreground, faded, so it stays readable on the selection fill.
            folder.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
            });
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
        _merge.IsEnabled = _files.Paths.Count > 0;
    }

    private int RowAt(Point point)
    {
        if (_list.InputHitTest(point) is not DependencyObject hit) return -1;
        while (hit is not null and not ListBoxItem) hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
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
        e.Effects = e.Data.GetDataPresent(RowFormat) ? DragDropEffects.Move
            : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(RowFormat) is int from)
        {
            // Below the last row moves to the end; let go outside the list and nothing moves.
            Point at = e.GetPosition(_list);
            if (at.X < 0 || at.Y < 0 || at.X > _list.ActualWidth || at.Y > _list.ActualHeight) return;
            int target = RowAt(at);
            int to = target < 0 ? _files.Paths.Count - 1 : target;
            _files.Move(from, to);
            Refresh(Math.Clamp(to, 0, _files.Paths.Count - 1));
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddAndSelect(dropped);
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
