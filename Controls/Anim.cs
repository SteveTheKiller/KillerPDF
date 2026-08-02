using System.Windows;
using System.Windows.Media.Animation;

namespace KillerPDF.Controls
{
    // Shared fade used across the whole app so every surface - the main window, dialogs and
    // flyouts - fades in with the same timing and easing. Ported with the file picker from
    // Killendar (the family reference); keep the timings identical across the apps.
    internal static class Anim
    {
        // Standard fade duration in milliseconds, shared by all surfaces.
        public const int FadeMs = 150;

        // Fades an element's opacity from 0 to 1 over FadeMs with an ease-out curve.
        public static void FadeIn(UIElement element)
        {
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        /// <summary>Fades an element out to 0 and calls <paramref name="done"/> when it lands.
        /// EaseIn mirrors FadeIn's EaseOut, so the surface accelerates away as smoothly as it
        /// arrived. Windows use this to fade before actually closing (see FileDialog.OnClosing);
        /// without it a dialog that fades in vanishes instantly, which reads as a glitch.</summary>
        public static void FadeOut(UIElement element, Action done)
        {
            var a = new DoubleAnimation(element.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            // Completed fires even if the value is already 0, so the callback cannot be stranded.
            a.Completed += (_, _) => done?.Invoke();
            element.BeginAnimation(UIElement.OpacityProperty, a);
        }

        /// <summary>Fade plus a horizontal glide from dx px to rest (negative dx = in from
        /// the left). Used by the rail flyouts so they read as sliding out of the rail.</summary>
        public static void SlideInX(UIElement element, double dx)
        {
            var tt = new System.Windows.Media.TranslateTransform(dx, 0);
            element.RenderTransform = tt;
            FadeIn(element);
            var a = new DoubleAnimation(dx, 0, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            a.Completed += (_, _) => element.RenderTransform = null;
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, a);
        }
    }
}
