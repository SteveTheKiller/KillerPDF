using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// #467: with the bar floating, the click that closes More settings landed on the menu's popup. The dock walked from
// the popup up into the bar, took the press for a drag on empty bar space and captured the mouse. Taking the capture
// from the popup raised LostMouseCapture on the popup, which bubbled to the dock and cleared the drag, so the release
// never let the mouse go and every later click in the window went to the dock.
public sealed class ToolOptionsDockPopupPressTests
{
    [Fact]
    public void APressInsideABarMenuIsNotADrag() => RunSta(() =>
    {
        var bar = new ToolOptionsBar(EditTool.Text, "Text options", "More settings");
        var dock = new ToolOptionsDock { Floating = true, Bar = bar };
        var menu = FindPopup(bar)?.Child;
        Assert.NotNull(menu);

        var press = Press(Mouse.PreviewMouseDownEvent, UIElement.PreviewMouseLeftButtonDownEvent);
        menu!.RaiseEvent(press);

        Assert.False(press.Handled);
    });

    [Fact]
    public void ACaptureLostInsideTheBarDoesNotEndTheDrag() => RunSta(() =>
    {
        var bar = new ToolOptionsBar(EditTool.Text, "Text options", "More settings");
        var dock = new ToolOptionsDock { Floating = true, Bar = bar };
        int changes = 0;
        dock.FloatingStateChanged += (_, _) => changes++;

        var press = Press(Mouse.PreviewMouseDownEvent, UIElement.PreviewMouseLeftButtonDownEvent);
        bar.RaiseEvent(press);
        Assert.True(press.Handled);
        // Another element in the bar losing the mouse is not the dock losing it.
        bar.OverflowButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        bar.RaiseEvent(Press(Mouse.PreviewMouseUpEvent, UIElement.PreviewMouseLeftButtonUpEvent));

        Assert.Equal(1, changes);
    });

    // The tunneling event the mouse raises; WPF turns it into PreviewMouseLeftButtonDown/Up on each element.
    private static MouseButtonEventArgs Press(RoutedEvent tunnel, RoutedEvent _) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = tunnel };

    private static Popup? FindPopup(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup popup) return popup;
            if (FindPopup(child) is { } found) return found;
        }
        return null;
    }

    private static void RunSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
