using System;
using System.Windows.Threading;

namespace KillerPDF.Controls
{
    /// <summary>Runs a fade's completion once, from the animation's Completed callback or, when that
    /// callback is lost, from a timer that fires a little after the fade should have ended.
    /// Animation callbacks do not arrive when animation is suppressed or the element has left the
    /// tree, and a dialog waiting on one stays open and refuses every close.</summary>
    internal sealed class FadeCompletion
    {
        /// <summary>How long after the fade starts the timer finishes it: the fade plus a margin the real callback comfortably beats.</summary>
        internal static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(Anim.FadeMs + 250);

        private readonly Action _done;
        private DispatcherTimer? _timer;
        private bool _completed;

        internal FadeCompletion(Action done) { _done = done; }

        internal bool IsCompleted => _completed;

        internal void Complete()
        {
            if (_completed) return;
            _completed = true;
            _timer?.Stop();
            _timer = null;
            _done();
        }

        internal void ArmFallback(Dispatcher dispatcher)
        {
            if (_completed) return;
            _timer = new DispatcherTimer(Fallback, DispatcherPriority.Normal, (_, _) => Complete(), dispatcher);
            _timer.Start();
        }
    }
}
