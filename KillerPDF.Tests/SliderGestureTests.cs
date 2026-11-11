using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The slider is built on an STA thread and never shown, so no window opens.
public sealed class SliderGestureTests
{
    // A drag across the replacement Blur slider is one change to undo, not one per tick: twenty
    // ticks must not push twenty page snapshots and evict the rest of the undo history.
    [Fact]
    public void OnlyTheFirstChangeOfADragStartsAGesture()
    {
        RunSta(() =>
        {
            var (slider, starts) = Tracked();

            for (int tick = 1; tick <= 20; tick++) slider.Value = tick * .05;

            Assert.Equal(1, starts());
        });
    }

    // Ending a drag, releasing the mouse or a key, or leaving the slider ends the gesture, so the
    // next change is its own undo step.
    [Fact]
    public void EachEndedGestureLetsTheNextChangeStartAnother()
    {
        RunSta(() =>
        {
            var (slider, starts) = Tracked();

            slider.Value = .1;
            slider.Value = .2;
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            slider.Value = .3;
            slider.Value = .4;
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
            slider.Value = .5;
            slider.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, slider, null)
                { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            slider.Value = .6;
            slider.Value = .7;

            Assert.Equal(4, starts());
        });
    }

    // The app's slider wheel handler (UiKit) is a class handler that changes the value and marks the wheel handled,
    // without focusing the slider or raising a mouse-up or key-up. Each wheel tick must still be its own undo step.
    [Fact]
    public void EachMouseWheelTickIsItsOwnGesture()
    {
        RunSta(() =>
        {
            EnsureWheelClassHandler();
            var (slider, starts) = Tracked();

            RaiseWheel(slider, 120);
            RaiseWheel(slider, 120);
            RaiseWheel(slider, -120);

            Assert.Equal(3, starts());
        });
    }

    private static readonly object WheelHandlerLock = new();
    private static bool _wheelHandlerRegistered;

    // The same shape as UiKit's class handler for every Slider.
    private static void EnsureWheelClassHandler()
    {
        lock (WheelHandlerLock)
        {
            if (_wheelHandlerRegistered) return;
            _wheelHandlerRegistered = true;
            EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler((sender, e) =>
                {
                    var s = (Slider)sender;
                    s.Value = Math.Max(s.Minimum, Math.Min(s.Maximum, s.Value + (e.Delta > 0 ? .1 : -.1)));
                    e.Handled = true;
                }));
        }
    }

    private static void RaiseWheel(Slider slider, int delta) =>
        slider.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });

    private static (Slider Slider, Func<int> Starts) Tracked()
    {
        var slider = new Slider { Minimum = 0, Maximum = 2 };
        var gesture = new SliderGesture(slider);
        int starts = 0;
        slider.ValueChanged += (_, _) => { if (gesture.Starts()) starts++; };
        return (slider, () => starts);
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
