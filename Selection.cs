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
        // Selection
        // ============================================================

        // Resolve the active theme's "SelectionAccent" color: a per-theme color picked to stay
        // readable on the white PDF page (Accent is white in several themes, and AccentBorder is a
        // pale cream that washes out on white). Falls back to brand green.
        private Color AccentColor()
            => TryFindResource("SelectionAccent") is SolidColorBrush b ? b.Color : Color.FromRgb(30, 165, 76);
        private SolidColorBrush AccentBrush(byte alpha = 255)
        {
            var c = AccentColor();
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        // A darker shade of the accent, used for a cover's selection chrome and its in-edit outline so a
        // cover reads as distinct from the lighter accent on the text box stacked over it.
        private SolidColorBrush DarkerAccentBrush(byte alpha = 255)
        {
            var c = AccentColor();
            return new SolidColorBrush(Color.FromArgb(alpha, (byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)));
        }

        private void SelectAllText()
        {
            if (_currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);
                _selectedText = WordsToText(page.GetWords());
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    SetStatus("No text found on this page");
                    return;
                }
                Clipboard.SetText(_selectedText);
                // Visual feedback: highlight entire canvas
                ClearTextSelection();
                _selectRect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(30, 74, 130, 255)),
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 74, 130, 255)),
                    StrokeThickness = 1,
                    Width = _annotationCanvas.Width,
                    Height = _annotationCanvas.Height,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_selectRect, 0);
                Canvas.SetTop(_selectRect, 0);
                _annotationCanvas.Children.Add(_selectRect);
                SetStatus($"Selected all text - copied to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Select all error: {ex.Message}");
            }
        }

        private void CopySelectedText()
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                Clipboard.SetText(_selectedText);
                SetStatus($"Copied to clipboard");
            }
            else
            {
                SetStatus("No text selected - drag to select text");
            }
        }

        private void ClearTextSelection()
        {
            if (_selectRect is not null)
            {
                _activeCanvas.Children.Remove(_selectRect);
                _selectRect = null;
            }
            CancelTextDrag();                // cancel any in-progress flowing drag + free its capture
            ClearTextSelectionHighlight();   // and drop the word/char highlight rects + forget the range
            _selectedText = null;
        }

        private void ExtractTextFromRegion(int pageIdx, Rect canvasBounds)
        {
            if (_currentFile is null || pageIdx < 0) return;
            if (!_renderDims.ContainsKey(pageIdx)) return;

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1); // PdfPig is 1-based

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = pdfW / renderW;
                double sy = pdfH / renderH;

                // Convert canvas rect to PDF coordinates (flip Y - PDF origin is bottom-left)
                double pdfLeft = canvasBounds.Left * sx;
                double pdfRight = canvasBounds.Right * sx;
                double pdfTop = pdfH - (canvasBounds.Top * sy);
                double pdfBottom = pdfH - (canvasBounds.Bottom * sy);
                // pdfTop > pdfBottom because of Y flip
                double pdfMinY = Math.Min(pdfTop, pdfBottom);
                double pdfMaxY = Math.Max(pdfTop, pdfBottom);

                var words = page.GetWords()
                    .Where(w =>
                    {
                        var bb = w.BoundingBox;
                        double cx = (bb.Left + bb.Right) / 2;
                        double cy = (bb.Bottom + bb.Top) / 2;
                        return cx >= pdfLeft && cx <= pdfRight && cy >= pdfMinY && cy <= pdfMaxY;
                    })
                    .ToList();

                if (words.Count == 0)
                {
                    SetStatus("No text found in selection");
                    ClearTextSelection();
                    return;
                }

                _selectedText = WordsToText(words);

                Clipboard.SetText(_selectedText);
                int wordCount = words.Count;
                SetStatus($"Copied {wordCount} word(s) to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Text extraction error: {ex.Message}");
                ClearTextSelection();
            }
        }
    }
}
