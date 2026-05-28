using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace TDPdf.Services
{
    internal sealed class PdfDocumentService
    {
        public Task<PdfOpenResult> OpenAsync(string path, string? password, CancellationToken cancellationToken)
        {
            return Task.Run(() => OpenCore(path, password, cancellationToken), cancellationToken);
        }

        public Task SaveAsync(Action saveAction, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                saveAction();
                cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken);
        }

        public Task<PdfDocument> OpenPdfSharpAsync(string path, PdfDocumentOpenMode mode, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = PdfReader.Open(path, mode);
                cancellationToken.ThrowIfCancellationRequested();
                return document;
            }, cancellationToken);
        }

        public Task SaveFlattenedAsync(string sourcePath, string destinationPath, IReadOnlyList<PdfPageSize> pageSizes, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pageCount = pageSizes.Count;
                int maxW = 1, maxH = 1;
                for (int i = 0; i < pageCount; i++)
                {
                    int pw = (int)(pageSizes[i].WidthPoint * 150 / 72.0);
                    int ph = (int)(pageSizes[i].HeightPoint * 150 / 72.0);
                    if (pw > maxW) maxW = pw;
                    if (ph > maxH) maxH = ph;
                }

                using (var outDoc = new PdfDocument())
                using (var docReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(maxW, maxH)))
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (var pageReader = docReader.GetPageReader(i))
                        {
                            var bgra = pageReader.GetImage();
                            int rw = pageReader.GetPageWidth();
                            int rh = pageReader.GetPageHeight();
                            if (bgra == null || bgra.Length == 0 || rw <= 0 || rh <= 0) continue;

                            var pngBytes = EncodeBgraToPng(bgra, rw, rh);
                            var newPage = outDoc.AddPage();
                            newPage.Width = pageSizes[i].WidthPoint;
                            newPage.Height = pageSizes[i].HeightPoint;
                            using (var xi = XImage.FromStream(() => new MemoryStream(pngBytes)))
                            using (var gfx = XGraphics.FromPdfPage(newPage))
                            {
                                gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
                            }
                        }
                    }

                    outDoc.Save(destinationPath);
                }
            }, cancellationToken);
        }

        public Task<IReadOnlyList<BitmapSource?>> RenderThumbnailsAsync(string path, int pageCount, CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<BitmapSource?>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var thumbnails = new List<BitmapSource?>(pageCount);
                using (var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(256, 256)))
                {
                    for (int i = 0; i < pageCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        thumbnails.Add(RenderPageBitmap(docReader, i));
                    }
                }
                return thumbnails;
            }, cancellationToken);
        }

        public Task<PdfRenderResult> RenderPageAsync(string path, int pageIndex, int dpiX, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int safeDpiX = Math.Max(1, dpiX);
                double renderScale = safeDpiX / 96.0;
                int renderMax = Math.Max(1, (int)Math.Round(1536 * renderScale));
                using (var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(renderMax, renderMax)))
                using (var pageReader = docReader.GetPageReader(pageIndex))
                {
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();
                    var rawBytes = pageReader.GetImage();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                    {
                        return new PdfRenderResult(null, width, height, 0, 0);
                    }

                    // Round DIP dims once and derive bitmap DPI from them so the displayed
                    // canvas width is exactly dipW/dipH DIPs. Without this, PDFium's per-zoom
                    // pixel-rounding makes pixel/renderScale drift by ±1 DIP between zoom
                    // levels and placed annotations creep on every re-render.
                    int dipW = Math.Max(1, (int)Math.Round(width / renderScale));
                    int dipH = Math.Max(1, (int)Math.Round(height / renderScale));
                    double bitmapDpiX = 96.0 * width / dipW;
                    double bitmapDpiY = 96.0 * height / dipH;

                    var bitmap = new WriteableBitmap(width, height, bitmapDpiX, bitmapDpiY, PixelFormats.Bgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);
                    bitmap.Freeze();
                    return new PdfRenderResult(bitmap, width, height, dipW, dipH);
                }
            }, cancellationToken);
        }

        private static PdfOpenResult OpenCore(string path, string? password, CancellationToken cancellationToken)
        {
            PdfDocument? document = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (password is null)
                {
                    try
                    {
                        document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
                        PreloadDocnet(path, cancellationToken);
                        return new PdfOpenResult(document, path, path, false);
                    }
                    catch (Exception ex) when (IsOwnerPasswordException(ex))
                    {
                        document?.Close();
                        document = PdfReader.Open(path, PdfDocumentOpenMode.ReadOnly);
                        PreloadDocnet(path, cancellationToken);
                        return new PdfOpenResult(document, path, path, true);
                    }
                }

                document = PdfReader.Open(path, password ?? string.Empty, PdfDocumentOpenMode.Modify);
                var tempDec = Path.Combine(Path.GetTempPath(), $"tdpdf_dec_{Guid.NewGuid():N}.pdf");
                document.Save(tempDec);
                document.Close();
                document = PdfReader.Open(tempDec, PdfDocumentOpenMode.Modify);
                PreloadDocnet(tempDec, cancellationToken);
                return new PdfOpenResult(document, path, tempDec, false);
            }
            catch
            {
                document?.Close();
                throw;
            }
        }

        private static void PreloadDocnet(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (DocLib.Instance.GetDocReader(path, new PageDimensions(256, 256)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private static BitmapSource? RenderPageBitmap(dynamic docReader, int pageIndex)
        {
            try
            {
                using (var pageReader = docReader.GetPageReader(pageIndex))
                {
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();
                    var raw = pageReader.GetImage();
                    if (width <= 0 || height <= 0 || raw == null || raw.Length == 0) return null;

                    var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, width, height), raw, width * 4, 0);
                    bitmap.Freeze();
                    return bitmap;
                }
            }
            catch
            {
                return null;
            }
        }


        private static byte[] EncodeBgraToPng(byte[] bgra, int width, int height)
        {
            using (var bitmap = new DrawingBitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var rect = new DrawingRectangle(0, 0, width, height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(bgra, 0, data.Scan0, Math.Min(bgra.Length, Math.Abs(data.Stride) * height));
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
        }

        internal static bool IsOwnerPasswordException(Exception ex) =>
            ex.Message.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal sealed class PdfOpenResult
    {
        public PdfOpenResult(PdfDocument document, string displayPath, string workingPath, bool openedReadOnly)
        {
            Document = document;
            DisplayPath = displayPath;
            WorkingPath = workingPath;
            OpenedReadOnly = openedReadOnly;
        }

        public PdfDocument Document { get; }
        public string DisplayPath { get; }
        public string WorkingPath { get; }
        public bool OpenedReadOnly { get; }
    }

    internal sealed class PdfPageSize
    {
        public PdfPageSize(double widthPoint, double heightPoint)
        {
            WidthPoint = widthPoint;
            HeightPoint = heightPoint;
        }

        public double WidthPoint { get; }
        public double HeightPoint { get; }
    }

    internal sealed class PdfRenderResult
    {
        public PdfRenderResult(BitmapSource? bitmap, int width, int height, int dipWidth, int dipHeight)
        {
            Bitmap = bitmap;
            Width = width;
            Height = height;
            DipWidth = dipWidth;
            DipHeight = dipHeight;
        }

        public BitmapSource? Bitmap { get; }
        public int Width { get; }
        public int Height { get; }
        public int DipWidth { get; }
        public int DipHeight { get; }
    }
}
