using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF.Controls;

/// <summary>A comb text field: one character drawn in each printed cell over the real editor, whose
/// own text stays transparent so the raw string never shows on top of the cells. The characters are
/// drawn at the editor's font size and redrawn when it changes, so the size stepper reaches them.</summary>
internal sealed class CombTextBox : Grid
{
    // Bound to the editor's FontSize. A binding, unlike a property descriptor handler, holds no
    // reference from a static table, so a comb dropped with its page overlay is collected.
    private static readonly DependencyProperty CharacterSizeProperty = DependencyProperty.Register(
        nameof(CharacterSize), typeof(double), typeof(CombTextBox),
        new PropertyMetadata(12.0, (owner, _) => ((CombTextBox)owner).RefreshCharacters()));
    // The editor's font, weight and slant, bound the same way, so a style given to the field reaches the cells.
    private static readonly DependencyProperty CharacterFamilyProperty = DependencyProperty.Register(
        nameof(CharacterFamily), typeof(FontFamily), typeof(CombTextBox),
        new PropertyMetadata(SystemFonts.MessageFontFamily, (owner, _) => ((CombTextBox)owner).RefreshCharacters()));
    private static readonly DependencyProperty CharacterWeightProperty = DependencyProperty.Register(
        nameof(CharacterWeight), typeof(FontWeight), typeof(CombTextBox),
        new PropertyMetadata(FontWeights.Normal, (owner, _) => ((CombTextBox)owner).RefreshCharacters()));
    private static readonly DependencyProperty CharacterStyleProperty = DependencyProperty.Register(
        nameof(CharacterStyle), typeof(FontStyle), typeof(CombTextBox),
        new PropertyMetadata(FontStyles.Normal, (owner, _) => ((CombTextBox)owner).RefreshCharacters()));
    private static readonly DependencyProperty CharacterDecorationsProperty = DependencyProperty.Register(
        nameof(CharacterDecorations), typeof(TextDecorationCollection), typeof(CombTextBox),
        new PropertyMetadata(null, (owner, _) => ((CombTextBox)owner).RefreshCharacters()));
    private Brush _characterBrush = Brushes.Black;

    private readonly Canvas _characters = new() { IsHitTestVisible = false };
    private readonly int _cellCount;
    // The editor's own caret and selection follow its transparent proportional text, not the cells,
    // so both are hidden and drawn here at the cell they belong to.
    private readonly Brush _caretBrush;
    private readonly Brush _selectionBrush;
    private readonly System.Windows.Shapes.Rectangle _caret = new() { Width = 1.5, IsHitTestVisible = false };
    private readonly System.Windows.Threading.DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };

    internal TextBox Editor { get; }

    private double CharacterSize => (double)GetValue(CharacterSizeProperty);
    private FontFamily CharacterFamily => (FontFamily)GetValue(CharacterFamilyProperty);
    private FontWeight CharacterWeight => (FontWeight)GetValue(CharacterWeightProperty);
    private FontStyle CharacterStyle => (FontStyle)GetValue(CharacterStyleProperty);
    private TextDecorationCollection? CharacterDecorations => (TextDecorationCollection?)GetValue(CharacterDecorationsProperty);

    /// <summary>The color the cells' characters are drawn in.</summary>
    internal Brush CharacterBrush
    {
        get => _characterBrush;
        set { _characterBrush = value ?? Brushes.Black; RefreshCharacters(); }
    }

    /// <summary>Wraps the editor. <paramref name="fontSize"/> is the size the field starts at.</summary>
    internal CombTextBox(TextBox editor, int cellCount, double fontSize)
    {
        Editor = editor;
        _cellCount = cellCount;
        Editor.FontSize = fontSize;
        SetBinding(CharacterSizeProperty, new System.Windows.Data.Binding(nameof(TextBox.FontSize)) { Source = editor });
        SetBinding(CharacterFamilyProperty, new System.Windows.Data.Binding(nameof(TextBox.FontFamily)) { Source = editor });
        SetBinding(CharacterWeightProperty, new System.Windows.Data.Binding(nameof(TextBox.FontWeight)) { Source = editor });
        SetBinding(CharacterStyleProperty, new System.Windows.Data.Binding(nameof(TextBox.FontStyle)) { Source = editor });
        SetBinding(CharacterDecorationsProperty, new System.Windows.Data.Binding(nameof(TextBox.TextDecorations)) { Source = editor });
        Width = editor.Width;
        Height = editor.Height;
        _cellWidth = editor.Width / cellCount;
        Width = _cellWidth * Cells;
        Background = Brushes.Transparent;
        Editor.Width = double.NaN;
        Editor.Height = double.NaN;
        Editor.Foreground = Brushes.Transparent;
        _caretBrush = Editor.CaretBrush ?? Brushes.Black;
        _selectionBrush = Editor.SelectionBrush ?? SystemColors.HighlightBrush;
        Editor.CaretBrush = Brushes.Transparent;
        Editor.SelectionOpacity = 0;
        _caret.Fill = _caretBrush;
        _blink.Tick += (_, _) => _caret.Visibility =
            _caret.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
        Children.Add(Editor);
        Children.Add(_characters);
        // A value longer than the printed cells runs on in cells of the same width, as the save writes it.
        Editor.TextChanged += (_, _) => { Width = _cellWidth * Cells; RefreshCharacters(); };
        Editor.SelectionChanged += (_, _) => RefreshCharacters();
        Editor.IsKeyboardFocusedChanged += (_, _) => RefreshCharacters();
        // The editor's own hit testing follows its hidden text, so the mouse is mapped to cells here:
        // a click puts the caret at the cell, a drag selects the cells it crosses, Shift+click extends
        // from the caret, and a double-click selects everything.
        Editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            int cell = CellAt(e);
            Editor.Focus();
            if (e.ClickCount > 1) Editor.SelectAll();
            else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                int anchor = Math.Min(Editor.SelectionStart, Editor.Text.Length);
                int target = Math.Min(cell >= anchor ? cell + 1 : cell, Editor.Text.Length);
                Editor.Select(Math.Min(anchor, target), Math.Abs(target - anchor));
            }
            else
            {
                Editor.CaretIndex = Math.Min(cell, Editor.Text.Length);
                _dragAnchor = cell;
                Editor.CaptureMouse();
            }
            e.Handled = true;
        };
        Editor.PreviewMouseMove += (_, e) =>
        {
            if (_dragAnchor is not int anchor || e.LeftButton != MouseButtonState.Pressed) return;
            int cell = CellAt(e);
            if (cell != anchor || Editor.SelectionLength > 0) SelectCells(anchor, cell);
            e.Handled = true;
        };
        Editor.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragAnchor is null) return;
            _dragAnchor = null;
            Editor.ReleaseMouseCapture();
            e.Handled = true;
        };
        Editor.LostMouseCapture += (_, _) => _dragAnchor = null;
        SizeChanged += (_, _) => RefreshCharacters();
        RefreshCharacters();
    }

    private int? _dragAnchor;
    private readonly double _cellWidth;

    // The printed cells, and one more of the same width for each character past them.
    private int Cells => Math.Max(_cellCount, Editor.Text.Length);

    private int CellAt(MouseEventArgs e) =>
        CombFieldLayout.CellIndexAt(e.GetPosition(Editor).X, Math.Max(1, _cellWidth * Cells), Cells);

    // Selects the cells from one to the other, both included, as far as the text reaches.
    private void SelectCells(int from, int to)
    {
        int start = Math.Min(Math.Min(from, to), Editor.Text.Length);
        int end = Math.Min(Math.Max(from, to) + 1, Editor.Text.Length);
        if (end > start) Editor.Select(start, end - start);
        else Editor.CaretIndex = start;
    }

    private void RefreshCharacters()
    {
        _characters.Children.Clear();
        _blink.Stop();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double fontSize = CharacterSize;
        double cellWidth = _cellWidth;
        if (Editor.IsKeyboardFocused)
        {
            int start = Math.Min(Editor.SelectionStart, Editor.Text.Length);
            int end = Math.Min(Editor.SelectionStart + Editor.SelectionLength, Editor.Text.Length);
            if (end > start)
            {
                var selection = new System.Windows.Shapes.Rectangle
                {
                    Width = (end - start) * cellWidth, Height = ActualHeight,
                    Fill = _selectionBrush, Opacity = 0.35, IsHitTestVisible = false
                };
                Canvas.SetLeft(selection, start * cellWidth);
                _characters.Children.Add(selection);
            }
            else
            {
                // The caret stands just inside the left edge of the cell the next character goes to,
                // past the printed cells too, since the value runs on in cells of the same width.
                int index = Math.Min(Editor.CaretIndex, Editor.Text.Length);
                double left = index * cellWidth + 2;
                _caret.Height = Math.Min(ActualHeight, fontSize * 1.2);
                Canvas.SetLeft(_caret, left);
                Canvas.SetTop(_caret, (ActualHeight - _caret.Height) / 2);
                _caret.Visibility = Visibility.Visible;
                _characters.Children.Add(_caret);
                _blink.Start();
            }
        }
        for (int index = 0; index < Editor.Text.Length; index++)
        {
            // The editor's font, as an ordinary text field shows its own, and the line is centred on its
            // capitals as that field's is (TextCapCentering), so both kinds of field show the same
            // face at the same height.
            var character = new TextBlock
            {
                Text = Editor.Text[index].ToString(),
                Width = cellWidth,
                FontFamily = CharacterFamily,
                FontSize = fontSize,
                FontWeight = CharacterWeight,
                FontStyle = CharacterStyle,
                TextDecorations = CharacterDecorations,
                Foreground = _characterBrush,
                TextAlignment = TextAlignment.Center
            };
            _characters.Children.Add(character);
            character.Measure(new Size(cellWidth, double.PositiveInfinity));
            Canvas.SetLeft(character, index * cellWidth);
            Canvas.SetTop(character, (ActualHeight - character.DesiredSize.Height) / 2
                - TextCapCentering.Lift(character.FontFamily, fontSize));
        }
    }
}
