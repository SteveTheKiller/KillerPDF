using System;
using System.Windows;
using System.Windows.Input;
using KillerPDF.Controls;

namespace KillerPDF
{
    public partial class MainWindow
    {
        internal bool IsOutsideWindow(MouseEventArgs e)
        {
            const double slack = 40;
            Point p = e.GetPosition(this);
            return p.X < -slack || p.X > ActualWidth + slack
                || p.Y < -slack || p.Y > ActualHeight + slack;
        }

        internal void TearOutTab(PdfViewer source, PdfViewer.DocumentSession session)
        {
            source.CaptureActiveIfAny();

            var detachedWindow = new MainWindow(session)
            {
                Width = ActualWidth,
                Height = ActualHeight,
                Left = Math.Max(0, Left + 32),
                Top = Math.Max(0, Top + 32),
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            source.DetachSessionExt(session);
            source.ApplyActiveSessionIfAny();
            source.RenderActiveSessionExt();
            source.RebuildTabStripExt();

            detachedWindow.Show();
            detachedWindow.Activate();
        }
    }
}
