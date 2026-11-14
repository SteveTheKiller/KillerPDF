using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The Digital Signature window is two compact columns: certificate and signer details on the left,
// the visible signature and the output file on the right. Built and measured, never shown.
public sealed class SignDocumentDialogLayoutTests
{
    // The smallest common laptop screen.
    private const double SmallScreenWidth = 1366;

    [Fact]
    public void TheColumnsSitSideBySideAndTheTallerOneSetsTheHeight()
    {
        RunSta(() =>
        {
            var left = new Border { Height = 400 };
            var right = new Border { Height = 300 };

            Grid columns = SignDialogLayout.Columns(left, right);
            columns.Measure(new Size(SignDialogLayout.ContentWidth, double.PositiveInfinity));
            columns.Arrange(new Rect(columns.DesiredSize));

            Assert.Equal(400, columns.DesiredSize.Height);
            Assert.Equal(0, left.TranslatePoint(new Point(), columns).X);
            Assert.Equal(0, right.TranslatePoint(new Point(), columns).Y);
            Assert.True(right.TranslatePoint(new Point(), columns).X >= left.ActualWidth,
                "the right column overlaps the left one");
            Assert.Equal(left.ActualWidth, right.ActualWidth);
        });
    }

    // Tab follows the children, so the left column has to come before the right one.
    [Fact]
    public void TabReachesTheLeftColumnBeforeTheRightOne()
    {
        RunSta(() =>
        {
            var left = new Border();
            var right = new Border();

            Grid columns = SignDialogLayout.Columns(left, right);

            Assert.True(columns.Children.IndexOf(left) < columns.Children.IndexOf(right));
        });
    }

    [Fact]
    public void TheWindowIsNarrowerThanASmallLaptopScreen()
    {
        Assert.True(SignDialogLayout.OpeningWidth < SmallScreenWidth,
            $"the window opens {SignDialogLayout.OpeningWidth} wide");
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
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
