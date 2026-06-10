using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TDPdf.Controls
{
    /// <summary>
    /// A <see cref="WrapPanel"/> that is resilient to a long-standing WPF framework
    /// race in which the panel's internal <c>VisualCollection</c> is indexed out of
    /// range during a measure/arrange pass when children are added or removed from a
    /// Dispatcher continuation while a layout pass is already in flight.
    ///
    /// The base implementation can throw
    /// <see cref="ArgumentOutOfRangeException"/> from <c>VisualCollection.get_Item</c>
    /// (observed signature: <c>index ('2') must be less than '1'</c>). The exception is
    /// transient — the children collection is consistent once our own add/remove
    /// operations complete — so we swallow it and queue a fresh layout pass rather than
    /// letting it escape to <see cref="AppDomain.UnhandledException"/> and terminate the
    /// process. This is the page-grid panel (<c>PageContentPanel</c>) whose secondary
    /// pages are added/removed from async Dispatcher continuations.
    /// </summary>
    public sealed class SafeWrapPanel : WrapPanel
    {
        private Size _lastMeasure;

        protected override Size MeasureOverride(Size constraint)
        {
            try
            {
                _lastMeasure = base.MeasureOverride(constraint);
                return _lastMeasure;
            }
            catch (ArgumentOutOfRangeException)
            {
                QueueRelayout();
                return _lastMeasure;
            }
            catch (IndexOutOfRangeException)
            {
                QueueRelayout();
                return _lastMeasure;
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            try
            {
                return base.ArrangeOverride(finalSize);
            }
            catch (ArgumentOutOfRangeException)
            {
                QueueRelayout();
                return finalSize;
            }
            catch (IndexOutOfRangeException)
            {
                QueueRelayout();
                return finalSize;
            }
        }

        /// <summary>
        /// Re-runs layout once the children collection has settled. Background priority
        /// guarantees we run after the current (failed) layout pass and after any pending
        /// child add/remove operations queued on the Dispatcher.
        /// </summary>
        private void QueueRelayout()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
            }));
        }
    }
}
