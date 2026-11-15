using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace KillerPDF.Controls
{
    // ── Reordering pinned places ─────────────────────────────────────────────
    //
    // A pinned place can be dragged up or down among the other pinned places; Quick Access
    // entries and drives are not the user's list and stay where they are. The row moves live
    // under the pointer, and the new order is saved on release. Pins whose folder is missing
    // right now are not shown, so they keep their place at the end of the saved list instead
    // of being dropped.
    public partial class FileDialog
    {
        private PickerPlace? _pinDragPlace;
        private Point _pinDragStart;
        private bool _pinDragging;

        private void Places_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            EndPinDrag();
            _pinDragPlace = ItemUnder<PickerPlace>(e.OriginalSource as DependencyObject) is { Pinned: true } p ? p : null;
            _pinDragStart = e.GetPosition(PlacesList);
            _pinDragging = false;
        }

        /// <summary>
        /// True while the button is down on a pinned place, so the list's own selection change does
        /// not navigate; the release does that, or the drag reorders instead. Tied to the button
        /// itself, so a release somewhere else cannot leave navigation held off.
        /// </summary>
        internal static bool PinHoldsSelection(PickerPlace? pressed, bool buttonDown) => pressed != null && buttonDown;

        private void Places_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pinDragPlace == null || e.LeftButton != MouseButtonState.Pressed) return;
            Point now = e.GetPosition(PlacesList);
            if (!_pinDragging)
            {
                if (Math.Abs(now.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _pinDragging = true;
                // A ListBox selects whatever row a held button passes over, and selecting a place
                // navigates. Hold navigation off for the whole drag and re-mark the current folder
                // when it ends.
                PlacesList.CaptureMouse();
            }

            var target = ItemUnder<PickerPlace>(PlacesList.InputHitTest(now) as DependencyObject);
            if (target is { Pinned: true } && !ReferenceEquals(target, _pinDragPlace))
            {
                int from = Places.IndexOf(_pinDragPlace), to = Places.IndexOf(target);
                if (from >= 0 && to >= 0)
                    Places.Move(from, to);
            }
            e.Handled = true;
        }

        private void Places_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            PickerPlace? clicked = _pinDragPlace;
            bool dragged = _pinDragging;
            if (_pinDragging)
            {
                e.Handled = true;
                SavePinOrder();
            }
            EndPinDrag();
            if (!dragged && clicked != null) NavigateTo(clicked.Path);
        }

        private void Places_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (PlacesList.IsMouseCaptured) return;
            if (_pinDragging) SavePinOrder();
            EndPinDrag();
        }

        private void EndPinDrag()
        {
            bool captured = _pinDragging;
            bool pending = _pinDragPlace != null;
            _pinDragPlace = null;
            _pinDragging = false;
            if (!pending) return;
            if (captured && PlacesList.IsMouseCaptured) PlacesList.ReleaseMouseCapture();
            SyncPlacesSelection();
        }

        private void SavePinOrder()
        {
            var shown = Places.Where(p => p.Pinned).Select(p => p.Path).ToList();
            var hidden = PinnedPaths().Where(p => !shown.Any(s =>
                s.TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            Services.ThemeManager.SetSetting(PinnedKey, string.Join("|", shown.Concat(hidden)));
        }
    }
}
