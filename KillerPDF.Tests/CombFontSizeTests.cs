using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// R6-004: a comb field draws its characters itself, so a new font size from the size stepper must
// redraw them, in the box being edited and in every other box of the same name.
public sealed class CombFontSizeTests
{
    private static CombTextBox Comb(string text)
    {
        var editor = new TextBox { Width = 120, Height = 24, FontSize = 10, Text = text };
        var comb = new CombTextBox(editor, 6, 10);
        comb.Measure(new Size(120, 24));
        comb.Arrange(new Rect(0, 0, 120, 24));
        comb.UpdateLayout();   // raises SizeChanged, which draws the cells
        return comb;
    }

    private static double[] DrawnSizes(CombTextBox comb) =>
        [.. comb.Children.OfType<Canvas>().Single().Children.OfType<TextBlock>().Select(character => character.FontSize)];

    [Fact]
    public void ANewFontSizeRedrawsEveryBoxOfARepeatedCombField()
    {
        RunSta(() =>
        {
            CombTextBox edited = Comb("1234"), twin = Comb("1234");
            var marks = new RepeatedFieldMarks();
            marks.AddText(0, "zip", edited.Editor);
            marks.AddText(1, "zip", twin.Editor);
            Assert.Equal([10.0, 10, 10, 10], DrawnSizes(edited));

            // What the size stepper does: the edited box takes the size, then every box of the name.
            edited.Editor.FontSize = 14;
            marks.ShowFontSize("zip", 14, edited.Editor);

            Assert.Equal([14.0, 14, 14, 14], DrawnSizes(edited));
            Assert.Equal([14.0, 14, 14, 14], DrawnSizes(twin));
        });
    }

    [Fact]
    public void TypingAfterASizeChangeKeepsTheNewSize()
    {
        RunSta(() =>
        {
            CombTextBox comb = Comb("12");
            comb.Editor.FontSize = 16;
            comb.Editor.Text = "123";

            Assert.Equal([16.0, 16, 16], DrawnSizes(comb));
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
