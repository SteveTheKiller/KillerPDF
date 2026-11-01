using System.Diagnostics;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Dirty / unsaved-change tracking
        // ============================================================

        private long _documentEditRevision;

        private void AcceptSavedCheckpoint()
        {
            ActiveViewer.CaptureSavedCheckpoint(_docStampSpec);
            MarkDirty(false);
        }

        private void MarkDirty(bool dirty = true)
            => MarkViewerDirty(ActiveViewer, dirty);

        private void MarkViewerDirty(PdfViewer viewer, bool dirty)
        {
            _documentEditRevision++;
            viewer.IsDirtyRef = dirty;
            viewer.NotifyReadingTextChanged();
            if (viewer.ActiveSessionExt is { } session)
            {
                session.IsDirty = dirty;
                session.RefreshTabLabel();
            }
            ScheduleToolbarAvailabilityRefresh();
            // A delayed editor commit still belongs to its pane after focus has moved.
            // Shared window controls continue to describe the focused document.
            if (!ReferenceEquals(ActiveViewer, viewer)) return;
            UpdateStampIndicator();
            ScheduleSidebarPanelRefresh();   // measurements listed in the Comments panel may have changed
            if (_saveAsBtnRef != null)
            {
                if (dirty)
                {
                    // Deeper orange = unsaved. The old #FFA500 washed out on the light theme's white
                    // toolbar; this reads on light and dark. A soft dark halo (ShadowDepth 0) outlines
                    // the glyph so it pops on light backgrounds and stays invisible on dark ones.
                    _saveAsBtnRef.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x73, 0x00));
                    _saveAsBtnRef.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 5,
                        ShadowDepth = 0,
                        Opacity = 0.55
                    };
                }
                else
                {
                    // Saved / clean: just a normal toolbar icon (no color). The orange above is the only
                    // signal, reserved for "you have unsaved changes".
                    _saveAsBtnRef.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                    _saveAsBtnRef.Effect = null;
                }
            }
        }

        // Clean copy and signing use complete bytes captured at open or successful save. The
        // editable copy may omit overlay measurements, and the external file may have changed.
        private string? UnchangedDocumentBytes()
        {
            if (_isDirty || string.IsNullOrEmpty(_currentFile)
                || !ActiveViewer.MatchesSavedCheckpoint(_docStampSpec)) return null;
            return _completeDocumentSource?.Path;
        }

        // Cryptographic certificate signing (the real digital signature, not the drawn stamp tool).
        private void OpenSignDialog()
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile))
            {
                KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
                return;
            }
            CommitActiveTextBox();
            string suggestedSource = _originalFile ?? _currentFile!;
            string? snapshot = null;
            try
            {
                if (UnchangedDocumentBytes() is not { } unchanged)
                    snapshot = WriteSavedCopyToTemp();
                else
                {
                    // Preserve existing signatures on an unchanged document without rewriting it.
                    snapshot = App.MakeTempFile("signsource");
                    File.Copy(unchanged, snapshot, overwrite: true);
                }
                new SignDocumentDialog(this, snapshot, suggestedSource, _suggestedName).ShowDialog();
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Sign_Failed") + "\n" + ex.Message,
                    Loc("Str_Sign_Name"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (snapshot is not null && File.Exists(snapshot)) File.Delete(snapshot);
            }
        }

        // ---- generic busy overlay (indeterminate spinner) for blocking background work ----

        /// <summary>
        /// Dims the window and shows a spinning ring plus a message while a background task runs.
        /// Returned Border is passed to HideBusyOverlay when the work completes.
        /// </summary>
        private Border ShowBusyOverlay(string message)
        {
            var spinner = new System.Windows.Shapes.Ellipse
            {
                Width = 34,
                Height = 34,
                Stroke = AccentBrush(),
                StrokeThickness = 3,
                StrokeDashArray = [5.5, 3.5], // dashed ring reads as "spinning"
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rot = new RotateTransform();
            spinner.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
                { RepeatBehavior = RepeatBehavior.Forever });

            var text = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontFamily = UiKit.UiFont,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(spinner);
            panel.Children.Add(text);

            var overlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 0x12, 0x12, 0x12)),
                Child = panel
            };
            Panel.SetZIndex(overlay, 10050); // above the Settings/Shortcuts/About overlays

            // Cover the whole window for a uniform dim, but let the user drag the window by pressing
            // anywhere on the overlay - so a long operation (e.g. repair) doesn't trap the window in place.
            overlay.Cursor = DragCursors.Open;
            overlay.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                // DragMove blocks for the whole move, so the override brackets it rather than
                // being cleared from a button-up handler that never runs.
                DragCursors.BeginDrag();
                try { DragMove(); } catch { } finally { DragCursors.EndDrag(); }
            };
            if (ShortcutsOverlay?.Parent is Grid host)
            {
                if (host.RowDefinitions.Count > 0) Grid.SetRowSpan(overlay, host.RowDefinitions.Count);
                host.Children.Add(overlay);
            }
            else
            {
                RootGrid?.Children.Add(overlay);
            }
            return overlay;
        }

        private static void HideBusyOverlay(Border overlay)
            => (overlay.Parent as Panel)?.Children.Remove(overlay);

        // RenderToPng lives in Services/BitmapHelpers.cs (KillerUI refactor), with the other
        // raw-bitmap helpers.
    }
}
