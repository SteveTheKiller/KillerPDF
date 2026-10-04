using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace KillerPDF.Controls
{
    public partial class FileDialog
    {
        private PickerPlace? _pinDragPlace;
        private Point _pinDragStart;
        private bool _pinDragging;
        private bool _pinNavigatingWas;

        private void Places_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _pinDragPlace = ItemUnder<PickerPlace>(e.OriginalSource as DependencyObject) is { Pinned: true } p ? p : null;
            _pinDragStart = e.GetPosition(PlacesList);
            _pinDragging = false;
        }

        private void Places_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_pinDragPlace == null || e.LeftButton != MouseButtonState.Pressed) return;
            Point now = e.GetPosition(PlacesList);
            if (!_pinDragging)
            {
                if (Math.Abs(now.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _pinDragging = true;
                _pinNavigatingWas = _navigating;
                _navigating = true;
                PlacesList.CaptureMouse();
                DragCursors.BeginDrag();
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
            if (_pinDragging)
            {
                e.Handled = true;
                SavePinOrder();
            }
            EndPinDrag();
        }

        private void Places_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_pinDragging) SavePinOrder();
            EndPinDrag();
        }

        private void EndPinDrag()
        {
            bool captured = _pinDragging;
            _pinDragPlace = null;
            _pinDragging = false;
            if (!captured) return;
            if (PlacesList.IsMouseCaptured) PlacesList.ReleaseMouseCapture();
            _navigating = _pinNavigatingWas;
            SyncPlacesSelection();
            DragCursors.EndDrag();
        }

        private void SavePinOrder()
        {
            var shown = Places.Where(p => p.Pinned).Select(p => p.Path).ToList();
            var hidden = PinnedPaths().Where(p => !shown.Any(s =>
                s.TrimEnd('\\').Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
            App.SetSetting(PinnedKey, string.Join("|", shown.Concat(hidden)));
        }
    }
}
