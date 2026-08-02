using System.Diagnostics;
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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Dirty / unsaved-change tracking
        // ============================================================

        private void MarkDirty(bool dirty = true)
        {
            _isDirty = dirty;
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
                    // Saved / clean: just a normal toolbar icon (no colour). The orange above is the only
                    // signal, reserved for "you have unsaved changes".
                    _saveAsBtnRef.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                    _saveAsBtnRef.Effect = null;
                }
            }
        }

        // Cryptographic certificate signing (the real digital signature, not the drawn stamp tool).
        private void OpenSignDialog()
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile))
            {
                KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
                return;
            }
            // Sign the user's real document, not the temp working copy. Operations like print/crop/repair
            // repoint _currentFile at a temp (e.g. "...printfixed...") while _originalFile keeps the real
            // path - which is the name the user expects to see and the file Save targets.
            new SignDocumentDialog(this, _originalFile ?? _currentFile!).ShowDialog();
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
            overlay.Cursor = Cursors.SizeAll;
            overlay.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
            };
            if (ShortcutOverlay?.Parent is Grid host)
            {
                if (host.RowDefinitions.Count > 0) Grid.SetRowSpan(overlay, host.RowDefinitions.Count);
                host.Children.Add(overlay);
            }
            else
            {
                RootClipGrid?.Children.Add(overlay);
            }
            return overlay;
        }

        private static void HideBusyOverlay(Border overlay)
            => (overlay.Parent as Panel)?.Children.Remove(overlay);

        // RenderToPng lives in Services/BitmapHelpers.cs (KillerUI refactor), with the other
        // raw-bitmap helpers.
    }
}
