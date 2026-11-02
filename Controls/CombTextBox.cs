using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF.Controls;

/// <summary>A comb text field: one character drawn in each printed cell over the real editor, whose
/// own text stays transparent so the raw string never shows on top of the cells.</summary>
internal sealed class CombTextBox : Grid
{
    private readonly Canvas _characters = new() { IsHitTestVisible = false };
    private readonly int _cellCount;
    private readonly double _fontSize;

    internal TextBox Editor { get; }

    internal CombTextBox(TextBox editor, int cellCount, double fontSize)
    {
        Editor = editor;
        _cellCount = cellCount;
        _fontSize = fontSize;
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
                FontSize = _fontSize,
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(character,
                CombFieldLayout.CellLeft(ActualWidth, _cellCount, index));
            Canvas.SetTop(character, Math.Max(0, (ActualHeight - _fontSize * 1.25) / 2));
            _characters.Children.Add(character);
        }
    }
}
