using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Presentation;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

// #478: Apply puts the watermark into the page content, under the annotations the file already holds. The viewer
// drew an active watermark on its overlay over a page bitmap that already had those annotations in it, so a saved
// opaque Square showed under the watermark until Apply put it back on top. The viewer now renders the pages a stamp
// spec covers from a copy of the document with the stamps burned in the way Apply burns them.
public sealed class ViewerStampLayeringTests
{
    private const double PageWidth = 400, PageHeight = 300;

    // A high zoom bucket: the stamped page is as large and as sharp as the page bitmap without stamps, and every pixel,
    // the whole page, its float frame and a detail tile, is the one Apply's output gives.
    [Fact]
    public void CoveredPagesRenderAsApplyMakesThemAtAHighZoomBucket()
    {
        RunSta(() => WithStampedDocument((path, applied, burn) =>
        {
            const int Bucket = 4096;
            using var plainLease = new PdfBackgroundRenderCache().Capture(path, 0, null).Rent();
            using var viewerLease = new PdfBackgroundRenderCache().Capture(path, 0, null, burn).Rent();
            using var appliedLease = new PdfBackgroundRenderCache().Capture(applied, 0, null).Rent();

            KillerPDF.Services.PdfRenderedPage plain = plainLease.Render(0, Bucket, Bucket);
            KillerPDF.Services.PdfRenderedPage viewed = viewerLease.Render(0, Bucket, Bucket);
            KillerPDF.Services.PdfRenderedPage want = appliedLease.Render(0, Bucket, Bucket);
            Assert.Equal((plain.Width, plain.Height), (viewed.Width, viewed.Height));
            Assert.Equal(Bucket, Math.Max(viewed.Width, viewed.Height));
            Assert.Equal(want.Pixels, viewed.Pixels);

            BitmapSource page = Bitmap(viewed.Width, viewed.Height, viewed.Pixels);
            Assert.True(IsRed(Pixel(page, 0.5, 0.5)), $"the saved Square should sit over the watermark, got {Pixel(page, 0.5, 0.5)}");
            Assert.True(IsBlue(Pixel(page, 0.29, 0.5)), "the uncovered watermark should show in the page bitmap");

            Assert.Equal(appliedLease.RenderFrame(0, Bucket, Bucket, CancellationToken.None).Pixels.ToArray(),
                viewerLease.RenderFrame(0, Bucket, Bucket, CancellationToken.None).Pixels.ToArray());
            Assert.Equal(appliedLease.RenderRegion(0, Bucket, Bucket, 1024, 768, 1024, 768).Pixels,
                viewerLease.RenderRegion(0, Bucket, Bucket, 1024, 768, 1024, 768).Pixels);
            var region = VisibleRenderRegion.Create(viewed.Width, viewed.Height, 0, 0.25, 0.25, 0.75, 0.75)!.Value;
            Assert.Equal(appliedLease.RenderFrameRegion(0, Bucket, Bucket, region, CancellationToken.None).Pixels.ToArray(),
                viewerLease.RenderFrameRegion(0, Bucket, Bucket, region, CancellationToken.None).Pixels.ToArray());
        }));
    }

    // With a proof profile on, and with a monitor profile, the stamped page goes through the same conversion the page
    // bitmap gets, so the saved marks' colors are the ones Apply's output shows under that conversion.
    [Fact]
    public void CoveredPagesGetTheProofAndMonitorConversionOfThePageBitmap()
    {
        // A text watermark: the float renderer the proof and monitor paths use does not draw image paint yet, and a
        // page holding one falls back to the plain bitmap the same way Apply's output does.
        WithStampedDocument(textWatermark: true, test: (path, applied, burn) =>
        {
            using var viewerLease = new PdfBackgroundRenderCache().Capture(path, 0, null, burn).Rent();
            using var appliedLease = new PdfBackgroundRenderCache().Capture(applied, 0, null).Rent();
            using var plainLease = new PdfBackgroundRenderCache().Capture(path, 0, null).Rent();
            foreach (var (proof, monitor) in new (PdfDocumentProofSettings?, PdfSdrDisplayConverter?)[]
                { (Proof(), null), (null, PdfViewingFrameConversionTests.Monitor()) })
            {
                PdfViewingPixels want = View(appliedLease, proof, monitor);
                PdfViewingPixels got = View(viewerLease, proof, monitor);
                Assert.False(got.ProofUnavailable);
                Assert.Null(got.DisplayFallbackOperation);
                Assert.Equal((want.Width, want.Height), (got.Width, got.Height));
                Assert.Equal(want.Pixels, got.Pixels);
                Assert.NotEqual(View(plainLease, proof, monitor).Pixels, got.Pixels);
                // The conversion did run: the proofed or converted page differs from the plain one somewhere.
                Assert.NotEqual(View(plainLease, null, null).Pixels, View(plainLease, proof, monitor).Pixels);
            }
        });
    }

    private static PdfViewingPixels View(PdfBackgroundRenderCache.Lease lease, PdfDocumentProofSettings? proof,
        PdfSdrDisplayConverter? monitor) => PdfViewingFrameConversion.Render(
            requested => lease.RenderFrame(0, 900, 900, CancellationToken.None, requested),
            () => lease.Render(0, 900, 900), proof, monitor, CancellationToken.None);

    // The float renderer behind proofing, monitor conversion and HDR presentation cannot draw every placement of a
    // burned picture yet, so a picture watermark keeps the overlay stamps there and the page keeps its proof and monitor
    // conversion; a text watermark and page numbers render from the stamped copy on those paths too.
    [Fact]
    public void PictureWatermarksKeepTheOverlayStampsWhereThePageDrawsFloatFrames()
    {
        WithStampedDocument((path, applied, burn) =>
        {
            using (var appliedLease = new PdfBackgroundRenderCache().Capture(applied, 0, null).Rent())
                Assert.Throws<NotSupportedException>(() => appliedLease.RenderFrame(0, 300, 300, CancellationToken.None, Proof()));
            Assert.Same(burn, burn.ForRender(floatFrames: false));
            Assert.Null(burn.ForRender(floatFrames: true));

            var request = new PdfBackgroundRenderCache().Capture(path, 0, null, burn.ForRender(floatFrames: true));
            Assert.False(request.StampsInPage(0));
            using var viewerLease = request.Rent();
            using var plainLease = new PdfBackgroundRenderCache().Capture(path, 0, null).Rent();
            foreach (var (proof, monitor) in new (PdfDocumentProofSettings?, PdfSdrDisplayConverter?)[]
                { (Proof(), null), (null, PdfViewingFrameConversionTests.Monitor()) })
            {
                PdfViewingPixels got = View(viewerLease, proof, monitor);
                Assert.False(got.ProofUnavailable);
                Assert.Null(got.DisplayFallbackOperation);
                Assert.Equal(View(plainLease, proof, monitor).Pixels, got.Pixels);
            }
        });
        WithStampedDocument(textWatermark: true, test: (_, _, burn) =>
            Assert.Same(burn, burn.ForRender(floatFrames: true)));
    }

    // A page turned in the app: the stamps sit where save puts them on the turned page, which burns them for the turn
    // and then writes the turn into the file.
    [Fact]
    public void TurnedCoveredPagesShowTheStampsWhereSavePutsThem()
    {
        WithStampedDocument(pageNumber: true, test: (path, _, unturned) =>
        {
            var turns = new Dictionary<int, int> { [0] = 90 };
            var burn = ViewerStampBurn.Create(path, unturned.Spec, turns);
            string saved = Path.Combine(Path.GetDirectoryName(path)!, "turned.pdf");
            File.Copy(path, saved);
            PdfEngineBurn.Burn(saved, new Dictionary<int, List<PageAnnotation>>(), new Dictionary<int, (int w, int h)>(),
                unturned.Spec, null, turns);
            PdfEngineIntegration.ApplyPageRotations(saved, turns);
            try
            {
                using var savedLease = new PdfBackgroundRenderCache().Capture(saved, 0, null).Rent();
                using var viewerLease = new PdfBackgroundRenderCache().Capture(path, 0, null, burn).Rent();
                using var unturnedLease = new PdfBackgroundRenderCache().Capture(path, 0, null, unturned).Rent();
                KillerPDF.Services.PdfRenderedPage want = savedLease.Render(0, 900, 900);
                KillerPDF.Services.PdfRenderedPage page = viewerLease.Render(0, 900, 900);
                var (shown, width, height) = BitmapHelpers.RotateBitmap(page.Pixels, page.Width, page.Height, 90);
                Assert.Equal((want.Width, want.Height), (width, height));
                Assert.True(Differing(want.Pixels, shown) < 50,
                    $"{Differing(want.Pixels, shown)} pixels of the turned page differ from the saved file");
                KillerPDF.Services.PdfRenderedPage wrong = unturnedLease.Render(0, 900, 900);
                var (misplaced, _, _) = BitmapHelpers.RotateBitmap(wrong.Pixels, wrong.Width, wrong.Height, 90);
                Assert.True(Differing(want.Pixels, misplaced) > 500,
                    "the page number burned for the upright page should land elsewhere on the turned one");
            }
            finally { PdfPageRenderSession.ForgetDocument(saved); }
        });

        static int Differing(byte[] a, byte[] b)
        {
            int count = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (Math.Abs(a[i] - b[i]) > 48 || Math.Abs(a[i + 1] - b[i + 1]) > 48 || Math.Abs(a[i + 2] - b[i + 2]) > 48)
                    count++;
            return count;
        }
    }

    // The night view keeps pictures as they are; the burned picture watermark is a picture in the stamped page, as it
    // is in Apply's output, so it stays un-inverted the same way.
    [Fact]
    public void TheNightViewKeepsTheBurnedPictureAsItKeepsItInApplysOutput()
    {
        RunSta(() => WithStampedDocument((path, applied, burn) =>
        {
            BitmapHelpers.FracRect[] want;
            using (var content = PdfContentDocument.Open(applied, null)) want = PdfImages.GetFracRects(content, 0);
            BitmapHelpers.FracRect[] kept = burn.ReadContent(content => PdfImages.GetFracRects(content, 0));
            Assert.Equal(want, kept);
            Assert.NotEmpty(kept);

            using var viewerLease = new PdfBackgroundRenderCache().Capture(path, 0, null, burn).Rent();
            using var appliedLease = new PdfBackgroundRenderCache().Capture(applied, 0, null).Rent();
            KillerPDF.Services.PdfRenderedPage viewed = viewerLease.Render(0, 900, 900);
            KillerPDF.Services.PdfRenderedPage applyPage = appliedLease.Render(0, 900, 900);
            BitmapHelpers.InvertBgraInPlaceExcept(viewed.Pixels, viewed.Width, viewed.Height, kept);
            BitmapHelpers.InvertBgraInPlaceExcept(applyPage.Pixels, applyPage.Width, applyPage.Height, want);
            Assert.Equal(applyPage.Pixels, viewed.Pixels);
            Assert.True(IsBlue(Pixel(Bitmap(viewed.Width, viewed.Height, viewed.Pixels), 0.29, 0.5)),
                "the burned picture should stay as it is in the night view");
        }));
    }

    // A page the stamp spec does not cover keeps exactly the pixels it had, and without a stamp spec the viewer's
    // renders keep today's options.
    [Fact]
    public void PagesTheStampsDoNotCoverRenderExactlyAsBefore()
    {
        WithStampedDocument((path, _, burn) =>
        {
            Assert.True(burn.Covers(0));
            Assert.False(burn.Covers(1));
            var request = new PdfBackgroundRenderCache().Capture(path, 0, null, burn);
            Assert.False(request.StampsInPage(1));
            using var plainLease = new PdfBackgroundRenderCache().Capture(path, 0, null).Rent();
            using var viewerLease = request.Rent();
            Assert.Equal(plainLease.Render(1, 900, 900).Pixels, viewerLease.Render(1, 900, 900).Pixels);
            Assert.Equal(plainLease.RenderFrame(1, 900, 900, CancellationToken.None).Pixels.ToArray(),
                viewerLease.RenderFrame(1, 900, 900, CancellationToken.None).Pixels.ToArray());

            KillerPDF.Services.PdfRenderedPage viewer = plainLease.Render(0, 900, 900);
            var before = new PdfRenderOptions(viewer.Width, viewer.Height, false, includeAnnotations: true,
                includeFormFields: false)
            { IncludeSignedSignatureFields = true, IncludePushButtonFields = true, IncludeRedactionMarks = false };
            var engine = new PdfPageRenderer(KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(path)))
                .Render(0, before);
            Assert.Equal(engine.Pixels.ToArray(), viewer.Pixels);
        });
    }

    // The overlay's stamps hide in the same step the stamped page bitmap takes the old one's place, and come back in
    // the same step an unstamped one replaces it, so translucent marks never show twice and never go missing.
    [Fact]
    public void TheOverlaysStampsSwapInTheSameStepAsThePageBitmap()
    {
        RunSta(() =>
        {
            WithStampedDocument((path, _, burn) =>
            {
                var request = new PdfBackgroundRenderCache().Capture(path, 0, null, burn);
                BitmapSource unstamped = Bitmap(4, 3, new byte[4 * 3 * 4]);
                BitmapSource stamped = Bitmap(4, 3, new byte[4 * 3 * 4]);
                StampedPageBitmaps.Note(request, 0, stamped);
                BitmapSource uncovered = Bitmap(4, 3, new byte[4 * 3 * 4]);
                StampedPageBitmaps.Note(request, 1, uncovered);
                Assert.False(StampedPageBitmaps.IsStamped(unstamped));
                Assert.True(StampedPageBitmaps.IsStamped(stamped));
                Assert.False(StampedPageBitmaps.IsStamped(uncovered));

                var page = new Image { Source = unstamped };
                UIElement[] stamps = [new TextBlock { Text = "1" }, new Image()];
                StampedPageBitmaps.ShowStamps(page, stamps);
                Assert.All(stamps, element => Assert.Equal(Visibility.Visible, element.Visibility));

                page.Source = stamped;
                StampedPageBitmaps.ShowStamps(page, stamps);
                Assert.All(stamps, element => Assert.Equal(Visibility.Hidden, element.Visibility));

                page.Source = unstamped;
                StampedPageBitmaps.ShowStamps(page, stamps);
                Assert.All(stamps, element => Assert.Equal(Visibility.Visible, element.Visibility));
            });
        });

        // Every place a page bitmap is put in place follows it with the swap, and turning the stamps on or off draws
        // the pages again without clearing them first.
        string viewer = ViewerFolder();
        string viewport = File.ReadAllText(Path.Combine(viewer, "PdfViewer.Viewport.cs"));
        Assert.Contains("pageImg.Source  = bmp;\r\n            ShowStampElementsFor(slotGrid);", viewport.ReplaceLineEndings("\r\n"));
        Assert.Contains("exImg.Source = bitmap;\r\n                ShowStampElementsFor(exGrid);", viewport.ReplaceLineEndings("\r\n"));
        string annotations = File.ReadAllText(Path.Combine(viewer, "PdfViewer.Annotations.cs"));
        Assert.Contains("RenderStamps(pageIndex);   // stamp layer (page numbers / watermark) sits beneath annotations\r\n            KeepStampElements(_activeCanvas, firstStamp);",
            annotations.ReplaceLineEndings("\r\n"));
        string layering = File.ReadAllText(Path.Combine(viewer, "PdfViewer.StampLayering.cs"));
        string repaint = layering[layering.IndexOf("private void RepaintStampPages()", StringComparison.Ordinal)..];
        repaint = repaint[..repaint.IndexOf("private void KeepStampElements(", StringComparison.Ordinal)];
        Assert.DoesNotContain("Source = null", repaint);
        Assert.DoesNotContain("RepaintPixelsExt", layering);
    }

    // The burn runs off the UI thread on a temporary copy, which holds page content and is gone once it is read.
    [Fact]
    public void TheStampedCopyLeavesNoTemporaryFileBehind()
    {
        WithStampedDocument((path, _, burn) =>
        {
            string[] before = Directory.GetFiles(Path.GetTempPath(), "killerpdf-stamped-*.pdf");
            var again = ViewerStampBurn.Create(path, burn.Spec, new Dictionary<int, int>());
            Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "killerpdf-stamped-*.pdf"));
            Assert.Equal(burn.Bytes, again.Bytes);
            Assert.True(again.Matches(path, burn.Spec.Clone(), new Dictionary<int, int> { [1] = 360 }));
            Assert.False(again.Matches(path, burn.Spec, new Dictionary<int, int> { [0] = 90 }));
            var moved = burn.Spec.Clone();
            moved.WmOpacity = 0.5;
            Assert.False(again.Matches(path, moved, new Dictionary<int, int>()));
        });
    }

    // A page bitmap captured outside the stamp-aware request would bring the saved annotations back under the
    // stamps, or drop the stamps, on whichever view path it served.
    [Fact]
    public void EveryViewerPageRenderFollowsTheStampedCopy()
    {
        foreach (string file in Directory.GetFiles(ViewerFolder(), "*.cs"))
        {
            string source = File.ReadAllText(file);
            int captures = source.Split("_backgroundRenderCache.Capture(").Length - 1;
            Assert.True(captures == (Path.GetFileName(file) == "PdfViewer.StampLayering.cs" ? 1 : 0),
                $"{Path.GetFileName(file)} captures {captures} page render requests outside CaptureRenderRequest");
            Assert.DoesNotContain("ImageRectsFor(currentFile,", source);
            Assert.DoesNotContain("ImageRectsFor(file,", source);
        }
    }

    private static string ViewerFolder() =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!, "Controls", "Viewer");

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    // A two-page document with the saved annotations on page one and a centered blue image watermark on page one
    // only, its stamped copy as the viewer makes it, and the file Apply makes from the same spec.
    private static void WithStampedDocument(Action<string, string, ViewerStampBurn> test, bool textWatermark = false,
        bool pageNumber = false)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"killerpdf-wm478-viewer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string image = Path.Combine(folder, "logo.png");
            File.WriteAllBytes(image, SolidPng(200, 150, Colors.Blue));
            var spec = pageNumber
                ? new StampSpec
                {
                    NumbersEnabled = true, NumPosH = 2, NumPosV = 2, NumFontPt = 36, NumColor = Colors.Blue, NumRange = "1",
                }
                : textWatermark
                ? new StampSpec
                {
                    WmEnabled = true, WmIsImage = false, WmText = "DRAFT", WmFontPt = 96, WmColor = Colors.Blue,
                    WmAngle = 0, WmOpacity = 0.5, WmPosH = 1, WmPosV = 1, WmRange = "1",
                }
                : new StampSpec
                {
                    WmEnabled = true, WmIsImage = true, WmImagePath = image, WmScale = 1.0,
                    WmAngle = 0, WmOpacity = 1, WmPosH = 1, WmPosV = 1, WmRange = "1",
                };
            string path = Path.Combine(folder, "saved.pdf");
            File.WriteAllBytes(path, SavedPages());
            string applied = Path.Combine(folder, "applied.pdf");
            File.Copy(path, applied);
            PdfEngineBurn.Burn(applied, new Dictionary<int, List<PageAnnotation>>(),
                new Dictionary<int, (int w, int h)> { [0] = (2048, 1536) }, spec);
            var burn = ViewerStampBurn.Create(path, spec, new Dictionary<int, int>());
            test(path, applied, burn);
        }
        finally
        {
            PdfPageRenderSession.ForgetDocument(Path.Combine(folder, "saved.pdf"));
            PdfPageRenderSession.ForgetDocument(Path.Combine(folder, "applied.pdf"));
            Directory.Delete(folder, recursive: true);
        }
    }

    private static PdfContentStreamBuilder Ink() => new PdfContentStreamBuilder().SaveState().SetFillRgb(0, 0, 0)
        .Rectangle(20, 250, 80, 30).Fill().RestoreState();

    // Page one: black page text at the top left with a saved highlight over it, a saved opaque red Square over the
    // page center, inside the centered watermark's 100..300 x 75..225 pt, and a half-opaque green Square on bare
    // paper at the bottom right, which must show once, not twice. Page two: the same text, no stamps.
    private static byte[] SavedPages() => new KillerPdf.Engine.Editing.PdfIncrementalAnnotationEditor(
            KillerPdf.Engine.Documents.PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(PageWidth, PageHeight, Ink()).AddPage(PageWidth, PageHeight, Ink()).Build()))
        .AddRectangle(0, 140, 105, 120, 90, new PdfRgbColor(1, 0, 0), new PdfRgbColor(1, 0, 0))
        .AddHighlight(0, 10, 245, 60, 40)
        .AddRectangle(0, 300, 20, 80, 60, new PdfRgbColor(0, 0.6, 0), new PdfRgbColor(0, 0.6, 0), opacity: 0.5)
        .AddRectangle(1, 140, 105, 120, 90, new PdfRgbColor(1, 0, 0), new PdfRgbColor(1, 0, 0))
        .Build();

    private static PdfDocumentProofSettings Proof() => new(enabled: true,
        printerProfile: PdfColorProfile.Load(KillerPdf.Engine.Tests.ColorManagement.SyntheticCmykPrinter.Create()),
        gamutWarnings: false, options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric,
            BlackPointCompensation = false, SimulatePaper = false, SimulateBlack = false });

    private static BitmapSource Bitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Pixel(BitmapSource source, double fx, double fy)
    {
        byte[] pixel = new byte[4];
        source.CopyPixels(new Int32Rect((int)(source.PixelWidth * fx), (int)(source.PixelHeight * fy), 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static bool IsRed(Color color) => color.R > 200 && color.G < 60 && color.B < 60;
    private static bool IsBlue(Color color) => color.B > 200 && color.R < 60 && color.G < 60;

    private static byte[] SolidPng(int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        var source = BitmapSource.Create(width, height, 72, 72, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
