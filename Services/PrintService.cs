using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using System.Windows.Xps.Serialization;
using System.Windows.Documents.Serialization;
using System.Windows.Xps.Packaging;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    public sealed class PrintService
    {
        private const double DipPerInch = 96.0;
        private const double PointPerInch = 72.0;
        private const double RenderDpi = 300.0;

        public void Print(
            Window owner,
            PdfDocument document,
            string currentPath,
            bool includeAnnotations,
            Action drawAnnotationsOnDocument,
            Action<string> reloadDocument,
            Action<string> setStatus)
        {
            string? restorePath = null;
            try
            {
                var pageSizes = GetPageSizes(document);
                string printablePath = CreateScratchPath("print");

                if (includeAnnotations)
                {
                    string cleanPath = CreateScratchPath("clean");
                    document.Save(cleanPath);
                    restorePath = cleanPath;
                    drawAnnotationsOnDocument();
                    document.Save(printablePath);
                }
                else if (!TrySavePrintableCopy(document, printablePath))
                {
                    printablePath = currentPath;
                }

                PrintPreparedPdf(owner, printablePath, pageSizes, setStatus);
            }
            finally
            {
                if (restorePath is not null)
                    reloadDocument(restorePath);
            }
        }

        private static bool TrySavePrintableCopy(PdfDocument document, string path)
        {
            try
            {
                document.Save(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<Size> GetPageSizes(PdfDocument document)
        {
            var sizes = new List<Size>(document.PageCount);
            for (int i = 0; i < document.PageCount; i++)
                sizes.Add(new Size(document.Pages[i].Width.Point, document.Pages[i].Height.Point));
            return sizes;
        }

        private static void PrintPreparedPdf(Window owner, string pdfPath, IReadOnlyList<Size> pageSizes, Action<string> setStatus)
        {
            owner.Dispatcher.VerifyAccess();
            var dialog = new PrintDialog
            {
                PageRangeSelection = PageRangeSelection.AllPages,
                UserPageRangeEnabled = true,
                MinPage = 1,
                MaxPage = (uint)pageSizes.Count
            };

            bool? accepted = dialog.ShowDialog();
            if (accepted != true)
            {
                setStatus("Print canceled");
                return;
            }

            var printQueue = dialog.PrintQueue;
            if (printQueue is null)
            {
                setStatus("No printer selected");
                return;
            }

            var printTicket = dialog.PrintTicket ?? printQueue.DefaultPrintTicket ?? new PrintTicket();
            if (!printTicket.CopyCount.HasValue || printTicket.CopyCount.Value < 1)
                printTicket.CopyCount = 1;

            var selectedPages = GetSelectedPages(dialog, pageSizes.Count);
            if (selectedPages.Count == 0)
            {
                setStatus("No pages selected for printing");
                return;
            }

            ApplyOrientation(printTicket, pageSizes[selectedPages[0]]);

            var fixedDocument = BuildFixedDocument(pdfPath, pageSizes, selectedPages, dialog, printTicket);
            var writer = PrintQueue.CreateXpsDocumentWriter(printQueue);
            writer.WritingCompleted += (sender, args) => owner.Dispatcher.BeginInvoke(new Action(() => OnWritingCompleted(args, setStatus)));
            writer.WriteAsync(fixedDocument, printTicket);
            setStatus("Print job queued");
        }

        private static List<int> GetSelectedPages(PrintDialog dialog, int pageCount)
        {
            int first = 1;
            int last = pageCount;

            if (dialog.PageRangeSelection == PageRangeSelection.UserPages)
            {
                first = Math.Max(1, dialog.PageRange.PageFrom);
                last = Math.Min(pageCount, dialog.PageRange.PageTo);
            }

            var selected = new List<int>();
            for (int page = first; page <= last; page++)
                selected.Add(page - 1);
            return selected;
        }

        private static void ApplyOrientation(PrintTicket printTicket, Size sourcePageSize)
        {
            printTicket.PageOrientation = sourcePageSize.Width > sourcePageSize.Height
                ? PageOrientation.Landscape
                : PageOrientation.Portrait;
        }

        private static FixedDocument BuildFixedDocument(
            string pdfPath,
            IReadOnlyList<Size> pageSizes,
            IReadOnlyList<int> selectedPages,
            PrintDialog dialog,
            PrintTicket printTicket)
        {
            var mediaSize = GetMediaSize(dialog, printTicket);
            var pageLayouts = selectedPages
                .Select(pageIndex => CreatePageLayout(pageSizes[pageIndex], mediaSize))
                .ToList();

            var fixedDocument = new FixedDocument();
            // Render at RenderDpi of each page's native size; Image.Stretch=Fill scales to fit the layout.
            // Using the (dimOne, dimTwo) viewport overload requires dimOne <= dimTwo and breaks for landscape pages.
            using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(RenderDpi / PointPerInch));

            for (int i = 0; i < selectedPages.Count; i++)
            {
                var layout = pageLayouts[i];
                var bitmap = RenderPage(docReader, selectedPages[i]);
                var page = CreateFixedPage(bitmap, layout);
                fixedDocument.Pages.Add(new PageContent { Child = page });
            }

            return fixedDocument;
        }

        private static Size GetMediaSize(PrintDialog dialog, PrintTicket printTicket)
        {
            double width = printTicket.PageMediaSize?.Width ?? dialog.PrintableAreaWidth;
            double height = printTicket.PageMediaSize?.Height ?? dialog.PrintableAreaHeight;

            if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
            {
                width = 8.5 * DipPerInch;
                height = 11.0 * DipPerInch;
            }

            return new Size(width, height);
        }

        private static PageLayout CreatePageLayout(Size sourcePageSize, Size mediaSize)
        {
            bool sourceLandscape = sourcePageSize.Width > sourcePageSize.Height;
            bool mediaLandscape = mediaSize.Width > mediaSize.Height;
            double pageWidth = mediaSize.Width;
            double pageHeight = mediaSize.Height;

            if (sourceLandscape != mediaLandscape)
            {
                pageWidth = mediaSize.Height;
                pageHeight = mediaSize.Width;
            }

            double sourceWidthDip = sourcePageSize.Width / PointPerInch * DipPerInch;
            double sourceHeightDip = sourcePageSize.Height / PointPerInch * DipPerInch;
            double scale = Math.Min(pageWidth / sourceWidthDip, pageHeight / sourceHeightDip);
            double contentWidth = sourceWidthDip * scale;
            double contentHeight = sourceHeightDip * scale;

            return new PageLayout(
                pageWidth,
                pageHeight,
                contentWidth,
                contentHeight,
                (pageWidth - contentWidth) / 2.0,
                (pageHeight - contentHeight) / 2.0);
        }

        private static BitmapSource RenderPage(IDocReader docReader, int pageIndex)
        {
            using var pageReader = docReader.GetPageReader(pageIndex);
            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();
            var rawBytes = pageReader.GetImage();
            if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                throw new InvalidOperationException($"Could not render page {pageIndex + 1} for printing.");

            var bitmap = new WriteableBitmap(width, height, RenderDpi, RenderDpi, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        private static FixedPage CreateFixedPage(BitmapSource bitmap, PageLayout layout)
        {
            var page = new FixedPage
            {
                Width = layout.PageWidth,
                Height = layout.PageHeight
            };

            var image = new Image
            {
                Source = bitmap,
                Width = layout.ContentWidth,
                Height = layout.ContentHeight,
                Stretch = Stretch.Fill
            };

            FixedPage.SetLeft(image, layout.Left);
            FixedPage.SetTop(image, layout.Top);
            page.Children.Add(image);
            page.Measure(new Size(layout.PageWidth, layout.PageHeight));
            page.Arrange(new Rect(new Size(layout.PageWidth, layout.PageHeight)));
            page.UpdateLayout();
            return page;
        }

        private static string CreateScratchPath(string purpose) =>
            Path.Combine(Path.GetTempPath(), $"tdpdf_{purpose}_{Guid.NewGuid():N}.pdf");

        private static void OnWritingCompleted(WritingCompletedEventArgs args, Action<string> setStatus)
        {
            if (args.Cancelled)
            {
                setStatus("Print canceled");
            }
            else if (args.Error is not null)
            {
                setStatus($"Print failed: {args.Error.Message}");
            }
            else
            {
                setStatus("Print job sent");
            }
        }

        private sealed class PageLayout
        {
            public PageLayout(double pageWidth, double pageHeight, double contentWidth, double contentHeight, double left, double top)
            {
                PageWidth = pageWidth;
                PageHeight = pageHeight;
                ContentWidth = contentWidth;
                ContentHeight = contentHeight;
                Left = left;
                Top = top;
            }

            public double PageWidth { get; }
            public double PageHeight { get; }
            public double ContentWidth { get; }
            public double ContentHeight { get; }
            public double Left { get; }
            public double Top { get; }
        }
    }
}
