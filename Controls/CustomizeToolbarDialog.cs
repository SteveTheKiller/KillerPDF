using KillerPDF.Shell;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF.Controls;

// Customize toolbar: the toolbar's buttons in order, with a marker where the left side ends and
// the right side begins, beside the More menu (the overflow chevron). Buttons move between the two
// lists and up and down by button, double-click or drag, and separators can be added and removed.
// Nothing changes until OK.
internal sealed class CustomizeToolbarDialog : Window
{
    private enum EntryKind { Button, Separator, Space }

    private sealed record Entry(EntryKind Kind, MainWindow.ToolbarUnit? Unit = null);

    private readonly ListBox _bar = new();
    private readonly ListBox _more = new();
    private readonly Button _toMore;
    private readonly Button _toBar;
    private readonly Button _up;
    private readonly Button _down;
    private readonly Button _addSeparator;
    private readonly Button _removeSeparator;
    private readonly IReadOnlyDictionary<string, MainWindow.ToolbarUnit> _units;
    private readonly Func<string, double, FrameworkElement> _glyph;
    private readonly Func<string, string> _label;
    private ListBox _active;
    private Point _dragStart;
    private ListBoxItem? _dragItem;
    private Brush? _buttonRowHover;

    internal MainWindow.ToolbarLayout? Result { get; private set; }

    internal CustomizeToolbarDialog(Window owner, IReadOnlyList<MainWindow.ToolbarUnit> units,
        MainWindow.ToolbarLayout layout, MainWindow.ToolbarLayout defaults,
        Func<string, double, FrameworkElement> glyph, Func<string, string> label)
    {
        _units = units.ToDictionary(unit => unit.Id);
        _glyph = glyph;
        _label = label;
        _active = _bar;
        Title = "KillerPDF - " + L("Str_CustomizeToolbar_Title");
        Width = 640;
        Height = 560;
        MinWidth = 560;
        MinHeight = 420;
        DialogFrame.Configure(this, owner, resizable: true);

        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16), LastChildFill = true };

        var hint = new TextBlock
        {
            Text = L("Str_CustomizeToolbar_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);

        var reset = UiKit.Make(L("Str_CustomizeToolbar_Reset"), accent: false);
        reset.Click += (_, _) => Load(defaults);
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = UiKit.Make(L("Str_Btn_OK"), accent: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { Result = BuildLayout(); DialogResult = true; Close(); };
        // One footer row: Reset on the left, Cancel and OK on the right.
        DockPanel buttons = DialogParts.Footer([cancel, ok], reset);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);

        // The moves between and within the lists are a strip of icons between them, named in their tooltips.
        _toMore = SideButton("Str_CustomizeToolbar_ToMore", 0xE72A, () => MoveAcross(_bar, _more));
        _toBar = SideButton("Str_CustomizeToolbar_ToBar", 0xE72B, () => MoveAcross(_more, _bar));
        _up = SideButton("Str_Ctx_BmMoveUp", 0xE74A, () => MoveSelected(-1));
        _down = SideButton("Str_Ctx_BmMoveDown", 0xE74B, () => MoveSelected(+1));
        _addSeparator = SideButton("Str_CustomizeToolbar_AddSeparator", 0xE710, AddSeparator);
        _removeSeparator = SideButton("Str_CustomizeToolbar_RemoveSeparator", 0xE711, RemoveSeparator);
        var side = new StackPanel { Margin = new Thickness(8, 26, 8, 0) };
        foreach (Button button in new[] { _toMore, _toBar })
            side.Children.Add(button);
        side.Children.Add(new Border { Height = 14 });
        foreach (Button button in new[] { _up, _down })
            side.Children.Add(button);
        side.Children.Add(new Border { Height = 14 });
        foreach (Button button in new[] { _addSeparator, _removeSeparator })
            side.Children.Add(button);

        var lists = new Grid();
        lists.ColumnDefinitions.Add(new ColumnDefinition());
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lists.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement barColumn = ListColumn("Str_CustomizeToolbar_Toolbar", _bar);
        FrameworkElement moreColumn = ListColumn("Str_CustomizeToolbar_More", _more);
        Grid.SetColumn(side, 1);
        Grid.SetColumn(moreColumn, 2);
        lists.Children.Add(barColumn);
        lists.Children.Add(side);
        lists.Children.Add(moreColumn);
        body.Children.Add(lists);

        Content = DialogFrame.Frame(this, owner, Title, () => { DialogResult = false; Close(); }, body);
        Load(layout);
        ThemeManager.ThemeChanged += UpdateRowHover;
        Closed += (_, _) => ThemeManager.ThemeChanged -= UpdateRowHover;
    }

    private void UpdateRowHover()
    {
        _buttonRowHover = null;
        if (ThemeManager.Current != Theme.SE98 && !SystemParameters.HighContrast
            && TryFindResource("PaneBrush") is SolidColorBrush pane
            && TryFindResource("TextBrush") is SolidColorBrush text
            && TryFindResource("RowHoverBrush") is SolidColorBrush hover
            && pane.Color.A == 255 && text.Color.A == 255 && hover.Color.A == 255)
        {
            Color color = VisibleRowHover(pane.Color, text.Color, hover.Color);
            if (color != hover.Color)
            {
                _buttonRowHover = new SolidColorBrush(color);
                _buttonRowHover.Freeze();
            }
        }
        foreach (ListBox list in new[] { _bar, _more })
            foreach (ListBoxItem item in list.Items)
            {
                item.Resources.Remove("RowHoverBrush");
                if (_buttonRowHover is not null && item.Tag is Entry { Kind: EntryKind.Button })
                    item.Resources["RowHoverBrush"] = _buttonRowHover;
            }
    }

    // Keep a neutral hover distinguishable from the grained pane without reducing label contrast.
    private static Color VisibleRowHover(Color pane, Color text, Color hover)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                double c = value / 255d;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
        static double Contrast(Color a, Color b) =>
            (Math.Max(Luminance(a), Luminance(b)) + 0.05) / (Math.Min(Luminance(a), Luminance(b)) + 0.05);
        if (Contrast(pane, hover) >= 1.5) return hover;
        for (double blend = 0.02; blend <= 0.3; blend += 0.02)
        {
            byte Mix(byte background, byte foreground) => (byte)Math.Round(background + (foreground - background) * blend);
            Color candidate = Color.FromRgb(Mix(pane.R, text.R), Mix(pane.G, text.G), Mix(pane.B, text.B));
            if (Contrast(text, candidate) < 4.5) break;
            if (Contrast(pane, candidate) >= 1.5) return candidate;
        }
        return hover;
    }

    private static Button SideButton(string key, int glyph, Action click)
    {
        Button button = DialogParts.IconAction(key, glyph, (_, _) => click());
        button.Margin = new Thickness(0, 0, 0, 4);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        return button;
    }

    private FrameworkElement ListColumn(string headerKey, ListBox list)
    {
        var column = new DockPanel();
        DockPanel header = DialogParts.Header(L(headerKey));
        DockPanel.SetDock(header, Dock.Top);
        column.Children.Add(header);

        list.Padding = new Thickness(2, 4, 2, 4);
        list.AllowDrop = true;
        // Rows fit the list's width: long captions wrap or trim instead of scrolling sideways.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(list, L(headerKey));
        list.GotKeyboardFocus += (_, _) => Activate(list);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not null) Activate(list);
            SyncButtons();
        };
        list.MouseDoubleClick += (_, e) =>
        {
            if (ItemUnder(list, e.OriginalSource) is null) return;
            if (ReferenceEquals(list, _bar)) MoveAcross(_bar, _more);
            else MoveAcross(_more, _bar);
        };
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragItem = ItemUnder(list, e.OriginalSource);
        };
        list.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
            Vector moved = e.GetPosition(this) - _dragStart;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            ListBoxItem item = _dragItem;
            _dragItem = null;
            DragDrop.DoDragDrop(item, item, DragDropEffects.Move);
        };
        list.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetData(typeof(ListBoxItem)) is ListBoxItem dragged && CanDrop(dragged, list)
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        list.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(ListBoxItem)) is not ListBoxItem dragged || !CanDrop(dragged, list)) return;
            ListBox source = (ListBox)ItemsControl.ItemsControlFromItemContainer(dragged);
            ListBoxItem? target = ItemUnder(list, e.OriginalSource);
            int index = target is null ? list.Items.Count : list.Items.IndexOf(target);
            if (target is not null && e.GetPosition(target).Y > target.ActualHeight / 2) index++;
            int from = source.Items.IndexOf(dragged);
            source.Items.Remove(dragged);
            if (ReferenceEquals(source, list) && from < index) index--;
            list.Items.Insert(Math.Clamp(index, 0, list.Items.Count), dragged);
            list.SelectedItem = dragged;
            Activate(list);
            SyncButtons();
        };
        // The lists sit on framed panes in the pane color with the family drop shadow, like Transform's page.
        // The well keeps its see-through list template; its own tint and edge give way to the pane's.
        Grid well = DialogParts.Well(list);
        foreach (UIElement part in well.Children)
            if (!ReferenceEquals(part, list)) part.Visibility = Visibility.Collapsed;
        var pane = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = UiKit.RadCard,
            Margin = new Thickness(0, 0, 0, 8),
            ClipToBounds = true,
            Child = well
        };
        pane.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
        pane.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
        column.Children.Add(UiKit.PaneWithShadow(pane));
        return column;
    }

    // Separators and the side marker belong to the toolbar list only.
    private bool CanDrop(ListBoxItem dragged, ListBox target) =>
        ReferenceEquals(target, _bar) || (dragged.Tag is Entry { Kind: EntryKind.Button });

    private static ListBoxItem? ItemUnder(ListBox list, object source)
    {
        for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, list);
             node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is ListBoxItem item) return item;
        return null;
    }

    private void Activate(ListBox list)
    {
        if (ReferenceEquals(_active, list)) return;
        _active = list;
        (ReferenceEquals(list, _bar) ? _more : _bar).SelectedItem = null;
        SyncButtons();
    }

    private void Load(MainWindow.ToolbarLayout layout)
    {
        _bar.Items.Clear();
        _more.Items.Clear();
        foreach (string id in layout.Left) AddBarEntry(id);
        _bar.Items.Add(Row(new Entry(EntryKind.Space)));
        foreach (string id in layout.Right) AddBarEntry(id);
        foreach (string id in layout.More)
            if (_units.TryGetValue(id, out var unit)) _more.Items.Add(Row(new Entry(EntryKind.Button, unit)));
        _active = _bar;
        SyncButtons();
        UpdateRowHover();
    }

    private void AddBarEntry(string id)
    {
        if (id == MainWindow.ToolbarSeparatorToken) _bar.Items.Add(Row(new Entry(EntryKind.Separator)));
        else if (_units.TryGetValue(id, out var unit)) _bar.Items.Add(Row(new Entry(EntryKind.Button, unit)));
    }

    private ListBoxItem Row(Entry entry)
    {
        FrameworkElement content;
        switch (entry.Kind)
        {
            case EntryKind.Button:
            {
                // The toolbar glyphs bind their strokes to the button around them, so the row
                // hosts each one in an inert button.
                var host = new Button
                {
                    Content = _glyph(entry.Unit!.Glyph, 14),
                    Width = 24,
                    Focusable = false,
                    IsHitTestVisible = false,
                    Template = InertTemplate()
                };
                host.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                var text = new TextBlock
                {
                    Text = _label(entry.Unit.LabelKey),
                    FontFamily = UiKit.UiFont,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var row = new DockPanel();
                DockPanel.SetDock(host, Dock.Left);
                row.Children.Add(host);
                row.Children.Add(text);
                content = row;
                break;
            }
            case EntryKind.Space:
            {
                // The side marker: an accent rule above and below a caption that wraps to the
                // list's width, so a long translation never runs off the row.
                var caption = new TextBlock
                {
                    Text = L("Str_CustomizeToolbar_Space"),
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 3),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                };
                caption.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                var band = new Border { BorderThickness = new Thickness(0, 1, 0, 1), Child = caption };
                band.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
                content = band;
                break;
            }
            default:
            {
                var grid = new Grid { MinHeight = 18 };
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                foreach (int column in new[] { 0, 2 })
                {
                    var line = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Center };
                    line.SetResourceReference(Shape.FillProperty, "CardBorderBrush");
                    Grid.SetColumn(line, column);
                    grid.Children.Add(line);
                }
                var caption = new TextBlock
                {
                    Text = L("Str_CustomizeToolbar_Separator"),
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                Grid.SetColumn(caption, 1);
                grid.Children.Add(caption);
                content = grid;
                break;
            }
        }
        var item = new ListBoxItem
        {
            Content = content,
            Tag = entry,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        if (_buttonRowHover is not null && entry.Kind == EntryKind.Button)
            item.Resources["RowHoverBrush"] = _buttonRowHover;
        return item;
    }

    private static ControlTemplate InertTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        return new ControlTemplate(typeof(Button)) { VisualTree = presenter };
    }

    private Entry? SelectedEntry => (_active.SelectedItem as ListBoxItem)?.Tag as Entry;

    private void SyncButtons()
    {
        Entry? entry = SelectedEntry;
        bool inBar = ReferenceEquals(_active, _bar);
        int index = _active.SelectedIndex;
        _toMore.IsEnabled = inBar && entry?.Kind == EntryKind.Button;
        _toBar.IsEnabled = !inBar && entry is not null;
        _up.IsEnabled = entry is not null && index > 0;
        _down.IsEnabled = entry is not null && index >= 0 && index < _active.Items.Count - 1;
        _addSeparator.IsEnabled = inBar;
        _removeSeparator.IsEnabled = inBar && entry?.Kind == EntryKind.Separator;
    }

    /// <summary>Moves the selected button to the other list: into the More menu at the end, or
    /// onto the toolbar just above the side marker, the end of the left side.</summary>
    private void MoveAcross(ListBox from, ListBox to)
    {
        if (from.SelectedItem is not ListBoxItem { Tag: Entry { Kind: EntryKind.Button } } item) return;
        int index = from.SelectedIndex;
        from.Items.Remove(item);
        if (ReferenceEquals(to, _bar))
        {
            int space = _bar.Items.Cast<ListBoxItem>().ToList().FindIndex(row => row.Tag is Entry { Kind: EntryKind.Space });
            _bar.Items.Insert(space < 0 ? _bar.Items.Count : space, item);
        }
        else to.Items.Add(item);
        if (from.Items.Count > 0) from.SelectedIndex = Math.Min(index, from.Items.Count - 1);
        to.SelectedItem = item;
        Activate(to);
        to.ScrollIntoView(item);
        SyncButtons();
    }

    private void MoveSelected(int step)
    {
        if (_active.SelectedItem is not ListBoxItem item) return;
        int index = _active.SelectedIndex, target = index + step;
        if (target < 0 || target >= _active.Items.Count) return;
        _active.Items.RemoveAt(index);
        _active.Items.Insert(target, item);
        _active.SelectedIndex = target;
        _active.ScrollIntoView(item);
        SyncButtons();
    }

    private void AddSeparator()
    {
        int index = _bar.SelectedIndex < 0 ? _bar.Items.Count : _bar.SelectedIndex + 1;
        ListBoxItem item = Row(new Entry(EntryKind.Separator));
        _bar.Items.Insert(index, item);
        _bar.SelectedItem = item;
        SyncButtons();
    }

    private void RemoveSeparator()
    {
        if (_bar.SelectedItem is not ListBoxItem { Tag: Entry { Kind: EntryKind.Separator } } item) return;
        int index = _bar.SelectedIndex;
        _bar.Items.Remove(item);
        if (_bar.Items.Count > 0) _bar.SelectedIndex = Math.Min(index, _bar.Items.Count - 1);
        SyncButtons();
    }

    private MainWindow.ToolbarLayout BuildLayout()
    {
        var left = new List<string>();
        var rightSide = new List<string>();
        List<string> current = left;
        foreach (ListBoxItem row in _bar.Items)
        {
            switch (row.Tag)
            {
                case Entry { Kind: EntryKind.Space }: current = rightSide; break;
                case Entry { Kind: EntryKind.Separator }: current.Add(MainWindow.ToolbarSeparatorToken); break;
                case Entry { Kind: EntryKind.Button, Unit: { } unit }: current.Add(unit.Id); break;
            }
        }
        var more = _more.Items.Cast<ListBoxItem>()
            .Select(row => (row.Tag as Entry)?.Unit?.Id).OfType<string>().ToList();
        return new MainWindow.ToolbarLayout(left, rightSide, more);
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
