using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// R6-007: every KillerDialog takes the app's scale, not only the message box. The windows here are
// built the way each kind of dialog builds its own and never shown.
public sealed class DialogScaleTests
{
    private const double Scale = 2.5;

    private static double Drawn(FrameworkElement content) =>
        content.LayoutTransform is ScaleTransform transform && transform.ScaleX == transform.ScaleY ? transform.ScaleX : 1;

    // The password, text and lines prompts and the two-check prompts have a fixed width.
    [Fact]
    public void AFixedWidthPromptGrowsWithTheScale()
    {
        RunSta(() =>
        {
            var prompt = new Window { Width = 380, SizeToContent = SizeToContent.Height };
            var content = new Grid();

            DialogScale.Apply(prompt, content, Scale);

            Assert.Equal(Scale, Drawn(content));
            Assert.Equal(380 * Scale, prompt.Width);
        });
    }

    // The choice dialogs, Ask to Save among them, size to their content between a minimum and a
    // maximum width, so the limits grow with the scale or the scaled content is cut off.
    [Fact]
    public void AChoiceDialogsWidthLimitsGrowWithTheScale()
    {
        RunSta(() =>
        {
            var choices = new Window { MinWidth = 380, MaxWidth = 760, SizeToContent = SizeToContent.WidthAndHeight };
            var content = new Grid();

            DialogScale.Apply(choices, content, Scale);

            Assert.Equal(Scale, Drawn(content));
            Assert.True(double.IsNaN(choices.Width));
            double screen = SystemParameters.WorkArea.Width;
            Assert.Equal(Math.Min(380 * Scale, screen), choices.MinWidth);
            Assert.Equal(Math.Min(760 * Scale, screen), choices.MaxWidth);
        });
    }

    [Fact]
    public void AtTheNormalSizeNothingChanges()
    {
        RunSta(() =>
        {
            var prompt = new Window { Width = 380, MinWidth = 300, MaxWidth = 700 };
            var content = new Grid();

            DialogScale.Apply(prompt, content, 1);

            Assert.Same(Transform.Identity, content.LayoutTransform);
            Assert.Equal((380.0, 300.0, 700.0), (prompt.Width, prompt.MinWidth, prompt.MaxWidth));
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
