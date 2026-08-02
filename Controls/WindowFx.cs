using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace KillerPDF
{

    // Fade a window out on close: cancel the first close, animate opacity to 0, then close for real.
    // DialogResult is set before Closing fires, so it survives the deferral.
    internal static class WindowFx
    {
        public const int FadeMs = 150;

        public static void EnableFadeClose(Window w, int ms = FadeMs)
        {
            bool fading = false;
            bool readyToClose = false;
            w.Closing += (s, e) =>
            {
                if (readyToClose) return;  // our own post-fade Close - let it through
                e.Cancel = true;           // hold off the real close until the fade finishes
                if (fading) return;        // already fading - ignore repeat triggers
                fading = true;
                var anim = new DoubleAnimation(w.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                anim.Completed += (_, _) => { readyToClose = true; w.Close(); };
                w.BeginAnimation(UIElement.OpacityProperty, anim);
            };
        }
    }
}
