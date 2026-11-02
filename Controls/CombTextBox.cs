using System.Windows;
using System.Windows.Controls;
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

    private readonly Canvas _characters = new() { IsHitTestVisible = false };
    private readonly int _cellCount;

    internal TextBox Editor { get; }

    private double CharacterSize => (double)GetValue(CharacterSizeProperty);

    /// <summary>Wraps the editor. <paramref name="fontSize"/> is the size the field starts at.</summary>
    internal CombTextBox(TextBox editor, int cellCount, double fontSize)
    {
        Editor = editor;
        _cellCount = cellCount;
        Editor.FontSize = fontSize;
        SetBinding(CharacterSizeProperty, new System.Windows.Data.Binding(nameof(TextBox.FontSize)) { Source = editor });
        Width = editor.Width;
        Height = editor.Height;
        Background = Brushes.Transparent;
        Editor.Width = double.NaN;
        Editor.Height = double.NaN;
        Editor.Foreground = Brushes.Transparent;
        Children.Add(Editor);
        Children.Add(_characters);
        Editor.TextChanged += (_, _) => RefreshCharacters();
        Editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            int cell = CombFieldLayout.CellIndexAt(
                e.GetPosition(Editor).X, Math.Max(1, ActualWidth), _cellCount);
            Editor.Focus();
            Editor.CaretIndex = Math.Min(cell, Editor.Text.Length);
            e.Handled = true;
        };
        SizeChanged += (_, _) => RefreshCharacters();
        RefreshCharacters();
    }

    private void RefreshCharacters()
    {
        _characters.Children.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double fontSize = CharacterSize;
        double cellWidth = ActualWidth / _cellCount;
        int count = Math.Min(Editor.Text.Length, _cellCount);
        for (int index = 0; index < count; index++)
        {
            var character = new TextBlock
            {
                Text = Editor.Text[index].ToString(),
                Width = cellWidth,
                Height = ActualHeight,
                FontFamily = new FontFamily("Consolas"),
                FontSize = fontSize,
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(character,
                CombFieldLayout.CellLeft(ActualWidth, _cellCount, index));
            Canvas.SetTop(character, Math.Max(0, (ActualHeight - fontSize * 1.25) / 2));
            _characters.Children.Add(character);
        }
    }
}
