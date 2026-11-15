using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

// ============================================================
// SHARED FADE / SLIDE
//
// One place for the timing and easing every surface animates with, so the main window, the
// dialogs, the overlays and the rail flyouts all appear the same way. An app that hand-rolls a
// DoubleAnimation per call site ends up with four durations and three easings, which reads as
// four different products.
//
// THIS IS THE CANONICAL COPY (consolidated 2026-08-08). Every app carries a byte-identical
// copy of this file - only the namespace line differs - so a diff against this file IS the
// drift check. It replaced five copies that had each grown a different subset: the kit shipped
// no fade-out at all, so KillerNotes invented FadeOutAndClose and KillerPDF invented
// FadeOut(element, done) independently, while KillerScan, KillerShell and Killendar closed
// every dialog with no fade. When something here needs to change, change it HERE first, then
// re-copy into every app.
//
// COPY THIS FILE INTO THE APP and change the namespace - it is a plain static helper with no
// dependencies. Usage:
//
//     Loaded += (_, _) => Anim.FadeIn(RootBorder);      // a dialog, on its root Border
//     Anim.SlideInX(flyout, -12);                       // a rail flyout, gliding out of the rail
//
// The dialog's root Border must start at Opacity="0" in XAML, or the first frame paints solid
// before the animation takes over and the fade is a flicker rather than a fade.
//
// Two fade-outs, for two shapes of close:
//   - FadeOutAndClose(window, ref flag): call from an OnClosing override; it cancels that close,
//     fades the whole window, then closes for real. The default for a Window.
//   - FadeOut(element, done): fades a named element and runs a callback. For a dialog that must
//     hold its DialogResult until after the fade: assigning DialogResult is itself a close
//     request, and WPF resets DialogResult to null whenever a close is canceled, so such a
//     dialog records the result, fades, and assigns it in the callback (see KillerPDF's
//     FileDialog.OnClosing).
// ============================================================
namespace KillerPDF.Controls
{
    internal static class Anim
    {
        /// <summary>Standard fade duration in milliseconds, shared by all surfaces.</summary>
        public const int FadeMs = 150;

        /// <summary>Fades an element's opacity from 0 to 1 over FadeMs with an ease-out curve.</summary>
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
        /// arrived. Windows use this to fade before actually closing; without it a dialog that
        /// fades in vanishes instantly, which reads as a glitch.</summary>
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

        /// <summary>
        /// Fades a window out and then closes it for real. Call from an OnClosing override, or
        /// wire it to a close button; it returns true if it took over the close, in which case
        /// the caller must cancel this one and do nothing else.
        ///
        /// Driven per composition frame rather than with a DoubleAnimation, matching the palette
        /// fade in each app's ThemeManager and for the same reason: Timeline-based animation is
        /// suppressed outright in some environments (remote sessions, "show animations in
        /// Windows" turned off) and fails silently when it is, which reads as a window that
        /// vanishes instead of fading. A per-frame opacity write always runs.
        /// </summary>
        public static bool FadeOutAndClose(Window window, ref bool alreadyFaded)
        {
            if (alreadyFaded || !window.IsLoaded || window.Opacity <= 0.01) return false;
            alreadyFaded = true;

            // Release FadeIn's animation FIRST. It is a DoubleAnimation with the default
            // FillBehavior.HoldEnd, so it keeps holding Opacity after it finishes - and a held
            // animation outranks a local value, which means every per-frame write below would be
            // silently discarded and the window would sit at full opacity until the timer closed it.
            window.BeginAnimation(UIElement.OpacityProperty, null);
            window.Opacity = 1;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            double from = window.Opacity;
            void Tick(object? sender, EventArgs e)
            {
                double t = clock.Elapsed.TotalMilliseconds / FadeMs;
                if (t >= 1)
                {
                    CompositionTarget.Rendering -= Tick;
                    window.Opacity = 0;
                    // Off the render callback before closing: tearing the window down inside a
                    // Rendering handler reenters composition.
                    //
                    // Hand foreground back to the owner BEFORE the teardown. This close is
                    // deferred to a dispatcher callback with no input message behind it, and
                    // Win32 is free to ignore the activation it would otherwise do for us when
                    // an owned window is destroyed. With an owner chain (main window -> modeless
                    // pad -> modal dialog) nothing reclaimed foreground on the way back out and
                    // the MAIN window sank behind other applications. Activating first means the
                    // window being destroyed is not the foreground one, so there is nothing to
                    // hand off. For a modal child the owner is Win32-disabled (ShowDialog
                    // disables the thread's windows without touching WPF's IsEnabled, so it
                    // cannot be tested for here) and Activate is a harmless no-op; WPF's own
                    // dialog teardown re-enables and reactivates that case.
                    window.Dispatcher.BeginInvoke(new Action(() => FinishFadeClose(window)));
                    return;
                }
                window.Opacity = from * (1 - t * t);   // quadratic ease-in, mirrors FadeIn
            }
            CompositionTarget.Rendering += Tick;
            return true;
        }

        /// <summary>The close that follows the fade. A close the window's own OnClosing cancels
        /// (an unsaved-changes prompt answered Cancel) leaves the window open, so it comes back at
        /// full opacity instead of sitting there invisible.</summary>
        internal static void FinishFadeClose(Window window)
        {
            Window? owner = window.Owner;
            if (owner != null && owner.IsVisible) owner.Activate();
            bool closed = false;
            EventHandler onClosed = (_, _) => closed = true;
            window.Closed += onClosed;
            try { window.Close(); }
            finally { window.Closed -= onClosed; }
            if (!closed) window.Opacity = 1;
        }

        /// <summary>Clears the glide's transform when it lands, unless a newer glide has already
        /// replaced it on the same element.</summary>
        internal static void EndSlide(UIElement element, Transform mine)
        {
            if (ReferenceEquals(element.RenderTransform, mine)) element.RenderTransform = null;
        }

        /// <summary>Fade plus a horizontal glide from dx px to rest (negative dx = in from
        /// the left). Used by the rail flyouts so they read as sliding out of the rail.</summary>
        public static void SlideInX(UIElement element, double dx)
        {
            var tt = new TranslateTransform(dx, 0);
            element.RenderTransform = tt;
            FadeIn(element);
            var a = new DoubleAnimation(dx, 0, new Duration(TimeSpan.FromMilliseconds(FadeMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            // Clear the transform when it lands: a RenderTransform left in place on a laid-out
            // element is a permanent extra composition layer for no benefit.
            a.Completed += (_, _) => EndSlide(element, tt);
            tt.BeginAnimation(TranslateTransform.XProperty, a);
        }
    }
}
