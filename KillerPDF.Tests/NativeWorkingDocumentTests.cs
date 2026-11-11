using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeWorkingDocumentTests
{
    // Closing a book (closing its tab or opening another book over it) drops the layout ahead queued for it, so the
    // closed book is collected even while the shared layout-ahead thread is still busy, and lays out no more chapters.
    [Fact]
    public void ClosingAnEpubLetsItBeCollectedWhileItsLayoutAheadWaits()
    {
        var layoutAhead = typeof(PdfReadSnapshot).Assembly.GetType("KillerPdf.Engine.Formats.Epub.EpubLayoutAhead")!;
        using var hold = (IDisposable)layoutAhead.GetMethod("Hold",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
        var (book, closed) = OpenReadAndClose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(book.TryGetTarget(out _), "A closed book stayed in memory behind its queued layout ahead.");
        Assert.True(closed, "A closed book laid a chapter out again.");
    }

    // The queue holds the book's chapter cache, and through its layout function the package, fonts and caches; that
    // cache is watched, since the snapshot object alone can be collected either way.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference<object> Book, bool LayoutRefused) OpenReadAndClose()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        string filler = string.Concat(Enumerable.Repeat("<p>AAAA AAA AAAAA AA AAAA AAA</p>", 30));
        var owner = NativeWorkingDocument.OpenEpub(NativeSearchServiceTests.Chapters(filler, filler, filler, filler),
            new EpubLayoutOptions(200, 160, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 });
        var snapshot = (PdfReadSnapshot)typeof(NativeWorkingDocument).GetField("_snapshot",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;
        int last = snapshot.PageCount - 1;
        using (var lease = owner.CaptureReadRevision().Rent())
            // Reading a page lays its chapter out and queues its neighbor to be laid out ahead.
            Assert.NotEmpty(lease.ReadNativeTextPage(last / 2).Regions);

        owner.Close();
        owner.Close();

        bool refused = false;
        try { _ = snapshot.ReadTextPage(last); }
        catch (ObjectDisposedException) { refused = true; }
        const System.Reflection.BindingFlags members = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object layout = typeof(PdfReadSnapshot).GetProperty("EpubLayout", members)!.GetValue(snapshot)!;
        object chapters = layout.GetType().GetProperty("ChapterPages", members)!.GetValue(layout)!;
        return (new WeakReference<object>(chapters), refused);
    }

    [Fact]
    public void NativeEpubOwnerRetainsLinkRegionsAndHonorsItsLifetime()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        using var owner = NativeWorkingDocument.OpenEpub(Book("<p><a href='#target'>A</a></p><div id='target'/>"),
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 });
        var link = Assert.Single(owner.ReadLinkRegions(0));
        Assert.Equal("EPUB/text.xhtml#target", link.Target);
        Assert.Equal(3, link.Bounds.Left, 6);
        Assert.Equal(33, link.Bounds.Top, 6);
        Assert.Equal(0, owner.ResolveNavigationTarget(link.Target).PageIndex);
        Assert.Throws<OperationCanceledException>(() => owner.ReadLinkRegions(0, new CancellationToken(true)));
        owner.Close();
        Assert.Throws<ObjectDisposedException>(() => owner.ReadLinkRegions(0));
    }

    [Fact]
    public void DjvuConversionHonorsCancellationCloseAndFormatBeforeStartingWork()
    {
        using var owner = NativeWorkingDocument.OpenDjvu(DjvuPage());
        using var revision = owner.CaptureReadRevision();
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => owner.ConvertDjvuToPdf(canceled));
        Assert.Same(revision, owner.CaptureReadRevision());
        owner.Close();
        Assert.Throws<ObjectDisposedException>(() => owner.ConvertDjvuToPdf());
        Assert.Throws<OperationCanceledException>(() => owner.ConvertDjvuToPdf(canceled));

        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        byte[] original = Book();
        using var epub = NativeWorkingDocument.OpenEpub(original,
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 });
        using var epubRevision = epub.CaptureReadRevision();
        Assert.Throws<NotSupportedException>(() => epub.ConvertDjvuToPdf());
        Assert.Throws<OperationCanceledException>(() => epub.ConvertDjvuToPdf(canceled));
        Assert.Equal(PdfDocumentFormat.Epub, epub.Format);
        Assert.Same(epubRevision, epub.CaptureReadRevision());
        using var copy = new MemoryStream();
        epub.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Throws<ArgumentNullException>(() => NativePdfConversion.WriteNewDjvu(null!, null!, null!));
        Assert.Throws<OperationCanceledException>(() => NativePdfConversion.WriteNewDjvu(null!, null!, null!, canceled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DjvuPublicationCannotReuseSourcePathThroughCaseOrRelativeAliases(bool originalExists)
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-DjvuConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string originalPath = Path.Combine(folder, "original.PDF");
            string alias = Path.Combine(folder, ".", "ORIGINAL.pdf");
            byte[] original = DjvuPage();
            if (originalExists) File.WriteAllBytes(originalPath, original);
            using var owner = NativeWorkingDocument.OpenDjvu(original);
            using var revision = owner.CaptureReadRevision();

            Assert.Throws<ArgumentException>(() => NativePdfConversion.WriteNewDjvu(owner, originalPath, alias));

            Assert.Equal(originalExists ? 1 : 0, Directory.GetFiles(folder).Length);
            if (originalExists) Assert.Equal(original, File.ReadAllBytes(originalPath));
            Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
            Assert.Same(revision, owner.CaptureReadRevision());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("converted.djvu")]
    [InlineData("converted.txt")]
    [InlineData("converted")]
    public void DjvuPublicationRequiresAPdfDestination(string outputName)
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-DjvuConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var owner = NativeWorkingDocument.OpenDjvu(DjvuPage());

            Assert.Throws<ArgumentException>(() => NativePdfConversion.WriteNewDjvu(owner,
                Path.Combine(folder, "original.djvu"), Path.Combine(folder, outputName)));

            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("canceled")]
    [InlineData("closed")]
    [InlineData("wrong-format")]
    [InlineData("navigation")]
    [InlineData("malformed")]
    public void DjvuPublicationPreservesOriginalAndPublishesOnlyACompletePdf(string failure)
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-DjvuConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] original = failure switch
            {
                "wrong-format" => Book(),
                "navigation" => Convert.FromHexString(
                    "41542654464F524D00000064444A564D4449524D0000001181000100000052FFFFF8BF9E1FCC877DC900" +
                    "4E41564D00000020FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B" +
                    "464F524D00000016444A5655494E464F0000000A000200011A002C011601"),
                _ => DjvuPage()
            };
            if (failure == "malformed") original[42] = 0;
            using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
                "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
            using var fontBytes = new MemoryStream();
            resource.CopyTo(fontBytes);
            using var owner = failure == "wrong-format" ? NativeWorkingDocument.OpenEpub(original,
                new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes.ToArray())) { FontSize = 12, Margin = 4 })
                : NativeWorkingDocument.OpenDjvu(original);
            string originalPath = Path.Combine(folder, failure == "wrong-format" ? "original.epub" : "original.djvu");
            string destination = Path.Combine(folder, "converted.pdf");
            byte[] priorOutput = "Existing output must survive."u8.ToArray();
            File.WriteAllBytes(originalPath, original);
            if (failure == "existing") File.WriteAllBytes(destination, priorOutput);
            if (failure == "closed") owner.Close();
            IReadOnlyList<KillerPdf.Engine.Formats.Djvu.DjvuDiagnostic> notices = [];
            Action publish = () => notices = NativePdfConversion.WriteNewDjvu(owner, originalPath, destination,
                new CancellationToken(failure == "canceled"));

            switch (failure)
            {
                case "existing": Assert.Throws<IOException>(publish); break;
                case "canceled": Assert.Throws<OperationCanceledException>(publish); break;
                case "closed": Assert.Throws<ObjectDisposedException>(publish); break;
                case "navigation":
                case "malformed":
                    publish();
                    // The outline fixture contains page metadata without an image; both cases keep
                    // that unreadable page's position as a labeled placeholder.
                    Assert.Contains(notices, notice => notice.Code == "PageNotConverted");
                    Assert.Single(PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(destination))));
                    break;
                default: Assert.Throws<NotSupportedException>(publish); break;
            }

            Assert.Equal(original, File.ReadAllBytes(originalPath));
            if (failure == "existing") Assert.Equal(priorOutput, File.ReadAllBytes(destination));
            else if (failure is not ("navigation" or "malformed")) Assert.False(File.Exists(destination));
            Assert.Equal(failure is "existing" or "navigation" or "malformed" ? 2 : 1,
                Directory.GetFiles(folder).Length);
            if (failure != "closed")
            {
                using var copy = new MemoryStream();
                owner.WriteCopyTo(copy);
                Assert.Equal(original, copy.ToArray());
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ExplicitDjvuPublicationCreatesSeparatePdfAndPreservesNativeSession()
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-DjvuConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string originalPath = Path.Combine(folder, "original.djvu");
            string destination = Path.Combine(folder, "converted.PDF");
            byte[] original = DjvuPage();
            File.WriteAllBytes(originalPath, original);
            using var owner = NativeWorkingDocument.OpenDjvu(original);
            using var revision = owner.CaptureReadRevision();
            using var lease = revision.Rent();
            var geometry = owner.GetNativePageGeometry(0);
            string identity = owner.ContentIdentity;
            float[] pixels = lease.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels.ToArray();

            NativePdfConversion.WriteNewDjvu(owner, originalPath, destination);

            var pdf = PdfDocument.Open(File.ReadAllBytes(destination));
            var paper = Assert.Single(PdfPageInformation.Read(pdf));
            Assert.Equal(.48, paper.PhysicalWidth, 6);
            Assert.Equal(.72, paper.PhysicalHeight, 6);
            Assert.Equal(new byte[] { 255, 255, 255, 0, 255, 0 },
                new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(2, 3)).Pixels.ToArray().Where((_, i) => i % 4 == 0));
            Assert.Equal(original, File.ReadAllBytes(originalPath));
            Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
            Assert.Equal(identity, owner.ContentIdentity);
            Assert.Same(revision, owner.CaptureReadRevision());
            Assert.Same(geometry, owner.GetNativePageGeometry(0));
            Assert.Equal(pixels, lease.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels.ToArray());
            using var copy = new MemoryStream();
            owner.WriteCopyTo(copy);
            Assert.Equal(original, copy.ToArray());
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ExplicitDjvuConversionPreservesLiteralPixelsNaturalSizeAndNativeSession()
    {
        byte[] source = DjvuPage();
        byte[] original = source.ToArray();
        using var owner = NativeWorkingDocument.OpenDjvu(source);
        using var revision = owner.CaptureReadRevision();
        using var lease = revision.Rent();
        string identity = owner.ContentIdentity;
        var geometry = owner.GetNativePageGeometry(0);
        float[] nativePixels = lease.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels.ToArray();
        Assert.Equal(new float[] { 1, 1, 1, 0, 1, 0 }, nativePixels.Where((_, i) => i % 4 == 0));
        Array.Clear(source);

        var pdf = PdfDocument.Open(owner.ConvertDjvuToPdf());

        var paper = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal(.48, paper.PhysicalWidth, 6);
        Assert.Equal(.72, paper.PhysicalHeight, 6);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(2, 3));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        byte[] expected = [255, 255, 255, 0, 255, 0];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(new byte[] { expected[i], expected[i], expected[i], 255 }, pixels.AsSpan(i * 4, 4).ToArray());
        Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
        Assert.Equal(identity, owner.ContentIdentity);
        Assert.Equal(1, owner.PageCount);
        Assert.Same(revision, owner.CaptureReadRevision());
        Assert.Same(geometry, owner.GetNativePageGeometry(0));
        Assert.Equal(.72, geometry.WidthPoints, 6);
        Assert.Equal(.48, geometry.HeightPoints, 6);
        Assert.Equal(nativePixels, lease.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels.ToArray());
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
    }

    [Theory]
    [InlineData("existing")]
    [InlineData("canceled")]
    [InlineData("closed")]
    [InlineData("unsupported")]
    public void FailedEpubPublicationPreservesTheSourceAndAnyExistingDestination(string failure)
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-NativeConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string originalPath = Path.Combine(folder, failure == "unsupported" ? "original.djvu" : "original.epub");
            string destination = Path.Combine(folder, "converted.PDF");
            byte[] original = failure == "unsupported" ? DjvuPage() : Book();
            byte[] priorOutput = "Existing output must survive."u8.ToArray();
            File.WriteAllBytes(originalPath, original);
            using var owner = failure == "unsupported" ? NativeWorkingDocument.OpenDjvu(original)
                : NativeWorkingDocument.OpenEpub(original,
                    new EpubLayoutOptions(80, 48, font) { FontSize = 12, Margin = 4 });
            if (failure == "existing") File.WriteAllBytes(destination, priorOutput);
            if (failure == "closed") owner.Close();
            Action publish = () => NativePdfConversion.WriteNewEpub(owner, originalPath, destination,
                new EpubPdfConversionOptions(font), new CancellationToken(failure == "canceled"));

            switch (failure)
            {
                case "existing": Assert.Throws<IOException>(publish); break;
                case "canceled": Assert.Throws<OperationCanceledException>(publish); break;
                case "closed": Assert.Throws<ObjectDisposedException>(publish); break;
                case "unsupported": Assert.Throws<NotSupportedException>(publish); break;
            }

            Assert.Equal(original, File.ReadAllBytes(originalPath));
            if (failure == "existing") Assert.Equal(priorOutput, File.ReadAllBytes(destination));
            else Assert.False(File.Exists(destination));
            Assert.Equal(failure == "existing" ? 2 : 1, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("converted.epub")]
    [InlineData("converted.txt")]
    [InlineData("converted")]
    public void EpubPublicationRequiresAPdfDestination(string outputName)
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-NativeConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var owner = NativeWorkingDocument.OpenEpub(Book(),
                new EpubLayoutOptions(80, 48, font) { FontSize = 12, Margin = 4 });

            Assert.Throws<ArgumentException>(() => NativePdfConversion.WriteNewEpub(owner,
                Path.Combine(folder, "original.epub"), Path.Combine(folder, outputName),
                new EpubPdfConversionOptions(font)));

            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void EpubPublicationCannotReuseTheSourcePathEvenWhenTheOriginalWasRemoved()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-NativeConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string originalPath = Path.Combine(folder, "original.epub");
            using var owner = NativeWorkingDocument.OpenEpub(Book(),
                new EpubLayoutOptions(80, 48, font) { FontSize = 12, Margin = 4 });

            Assert.Throws<ArgumentException>(() => NativePdfConversion.WriteNewEpub(owner, originalPath,
                originalPath, new EpubPdfConversionOptions(font)));

            Assert.Empty(Directory.GetFiles(folder));
            Assert.Equal(PdfDocumentFormat.Epub, owner.Format);
            Assert.Equal(1, owner.PageCount);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ExplicitEpubConversionPublishesSeparatePdfAndPreservesNativeSession()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF-NativeConversion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string originalPath = Path.Combine(folder, "original.epub");
            string destination = Path.Combine(folder, "converted.pdf");
            byte[] original = Book();
            File.WriteAllBytes(originalPath, original);
            using var owner = NativeWorkingDocument.OpenEpub(File.ReadAllBytes(originalPath),
                new EpubLayoutOptions(80, 48, font) { FontSize = 12, Margin = 4 });
            using var revision = owner.CaptureReadRevision();
            string identity = owner.ContentIdentity;

            NativePdfConversion.WriteNewEpub(owner, originalPath, destination, new EpubPdfConversionOptions(font));

            var pdf = PdfDocument.Open(File.ReadAllBytes(destination));
            Assert.Equal("A", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
            var paper = Assert.Single(PdfPageInformation.Read(pdf));
            Assert.InRange(paper.PhysicalWidth, 595.274, 595.277);
            Assert.InRange(paper.PhysicalHeight, 841.888, 841.892);
            Assert.Equal(original, File.ReadAllBytes(originalPath));
            Assert.Equal(PdfDocumentFormat.Epub, owner.Format);
            Assert.Equal(identity, owner.ContentIdentity);
            Assert.Same(revision, owner.CaptureReadRevision());
            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void EpubConversionRejectsDjvuWithoutChangingItsNativeContent()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        byte[] original = DjvuPage();
        using var owner = NativeWorkingDocument.OpenDjvu(original);
        using var revision = owner.CaptureReadRevision();
        using var lease = revision.Rent();
        float[] nativePixels = lease.RenderFrame(0, new PdfRenderOptions(3, 2)).Pixels.ToArray();

        Assert.Throws<NotSupportedException>(() => owner.ConvertEpubToPdf(new EpubPdfConversionOptions(font)));

        Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
        Assert.Same(revision, owner.CaptureReadRevision());
        Assert.Equal(nativePixels, lease.RenderFrame(0, new PdfRenderOptions(3, 2)).Pixels.ToArray());
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Throws<OperationCanceledException>(() => owner.ConvertEpubToPdf(null!, new CancellationToken(true)));
    }

    [Fact]
    public void ExplicitEpubConversionCannotStartAfterNativeSessionCloses()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        using var owner = NativeWorkingDocument.OpenEpub(Book(),
            new EpubLayoutOptions(80, 48, font) { FontSize = 12, Margin = 4 });
        owner.Close();

        Assert.Throws<ObjectDisposedException>(() => owner.ConvertEpubToPdf(new EpubPdfConversionOptions(font)));
    }

    [Fact]
    public void ExplicitEpubConversionUsesSeparatePaperAndPreservesOpenNativeSession()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        resource.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        byte[] source = Book();
        byte[] original = source.ToArray();
        using var owner = NativeWorkingDocument.OpenEpub(source,
            new EpubLayoutOptions(80, 48, font) { FontSize = 12, Margin = 4 });
        using var revision = owner.CaptureReadRevision();
        using var lease = revision.Rent();
        string identity = owner.ContentIdentity;
        var geometry = owner.GetNativePageGeometry(0);
        float[] nativePixels = lease.RenderFrame(0, new PdfRenderOptions(160, 144)).Pixels.ToArray();
        Array.Clear(source);

        byte[] converted = owner.ConvertEpubToPdf(new EpubPdfConversionOptions(font)
        {
            PageWidthPoints = 200, PageHeightPoints = 300, MarginPoints = 20
        });

        var pdf = PdfDocument.Open(converted);
        var paper = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal(200, paper.PhysicalWidth);
        Assert.Equal(300, paper.PhysicalHeight);
        Assert.Equal("A", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(PdfDocumentFormat.Epub, owner.Format);
        Assert.Equal(identity, owner.ContentIdentity);
        Assert.Same(revision, owner.CaptureReadRevision());
        Assert.Same(geometry, owner.GetNativePageGeometry(0));
        Assert.Equal(60, geometry.WidthPoints);
        Assert.Equal(36, geometry.HeightPoints);
        Assert.Equal(nativePixels, lease.RenderFrame(0, new PdfRenderOptions(160, 144)).Pixels.ToArray());
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
    }

    [Fact]
    public void NativeOwnerResolvesLocalDestinationsAndHonorsCloseAndCancellation()
    {
        using var owner = NativeWorkingDocument.OpenDjvu(DjvuPage());

        Assert.Equal(new PdfReadNavigationDestination(0, 0, 0), owner.ResolveNavigationTarget("#1"));

        Assert.Throws<InvalidDataException>(() => owner.ResolveNavigationTarget("#2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => owner.ResolveNavigationTarget("#1", 1));
        owner.Close();
        Assert.Throws<ObjectDisposedException>(() => owner.ResolveNavigationTarget("#1"));
        Assert.Throws<OperationCanceledException>(() => owner.ResolveNavigationTarget(null!, -1, new CancellationToken(true)));
    }

    [Fact]
    public void NativeOwnerReturnsCapturedOutlineAndRejectsNewReadsAfterClose()
    {
        byte[] source = Convert.FromHexString(
            "41542654464F524D00000064444A564D4449524D0000001181000100000052FFFFF8BF9E1FCC877DC900" +
            "4E41564D00000020FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B" +
            "464F524D00000016444A5655494E464F0000000A000200011A002C011601");
        byte[] original = source.ToArray();
        using var owner = NativeWorkingDocument.OpenDjvu(source);
        Array.Clear(source);

        var root = Assert.Single(owner.ReadNavigation());

        Assert.Equal("R\u00e9sum\u00e9", root.Label);
        Assert.Equal("#1", root.Target);
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
        owner.Close();
        Assert.Throws<ObjectDisposedException>(() => owner.ReadNavigation());
        Assert.Throws<OperationCanceledException>(() => owner.ReadNavigation(new CancellationToken(true)));
        Assert.Equal("#1", root.Target);
    }

    [Fact]
    public void NativeEpubOwnerCopiesOriginalPackageAndRendersItsOwnedSourceWithPortableFont()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")
            ?? throw new InvalidOperationException("The portable font fixture is missing.");
        byte[] fontBytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(fontBytes);
        var options = new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes))
        {
            FontSize = 12, Margin = 4, TextColor = new PdfRgbColor(.5001, .5001, .5001)
        };
        byte[] source = Book();
        byte[] original = source.ToArray();
        var direct = PdfReadSnapshot.OpenEpub(original, options);
        using var owner = NativeWorkingDocument.OpenEpub(source, options);
        Array.Clear(source);
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Equal(PdfDocumentFormat.Epub, owner.Format);
        Assert.Equal(direct.SourceSha256, owner.ContentIdentity);
        Assert.Equal(1, owner.PageCount);
        var chapter = Assert.Single(owner.ReadNavigation());
        Assert.Equal("EPUB/text.xhtml", chapter.Target);
        Assert.Equal(new PdfReadNavigationDestination(0, 4, 4), owner.ResolveNavigationTarget(chapter.Target));
        using var revision = owner.CaptureReadRevision();
        Assert.Equal(PdfDocumentFormat.Epub, revision.Format);
        Assert.Equal(owner.ContentIdentity, revision.ContentIdentity);
        Assert.Equal(60, revision.GetPageGeometry(0).Width);
        Assert.Equal(36, revision.GetPageGeometry(0).Height);
        using var lease = revision.Rent();
        var renderOptions = new PdfRenderOptions(160, 144);
        PdfRenderedFrame frame = lease.RenderFrame(0, renderOptions);
        PdfRenderedFrame expected = direct.RenderPage(0, renderOptions);
        Assert.Equal(160, frame.Width);
        Assert.Equal(144, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(expected.Pixels.ToArray(), frame.Pixels.ToArray());
        Assert.Contains(frame.Pixels.ToArray(), channel => channel < .99f);
        Assert.Throws<ArgumentNullException>(() => NativeWorkingDocument.OpenEpub(original, null!));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            NativeWorkingDocument.OpenEpub(original, null!, new CancellationToken(true)));
        Assert.Throws<InvalidDataException>(() => NativeWorkingDocument.OpenEpub(Array.Empty<byte>(), options));
    }

    [Fact]
    public void NativeOwnerPropagatesMalformedContainerAndOriginalCodecDiagnoses()
    {
        Assert.Throws<InvalidDataException>(() => NativeWorkingDocument.OpenDjvu(Array.Empty<byte>()));
        byte[] unsupported = DjvuPage();
        unsupported[28] = 99;
        using (var owner = NativeWorkingDocument.OpenDjvu(unsupported))
        using (var revision = owner.CaptureReadRevision())
        using (var lease = revision.Rent())
        {
            Assert.Throws<NotSupportedException>(() => lease.RenderFrame(0, new PdfRenderOptions(2, 3)));
            using var copy = new MemoryStream();
            owner.WriteCopyTo(copy);
            Assert.Equal(unsupported, copy.ToArray());
        }
        byte[] malformed = DjvuPage();
        malformed[42] = 0;
        using var malformedOwner = NativeWorkingDocument.OpenDjvu(malformed);
        using var malformedRevision = malformedOwner.CaptureReadRevision();
        using var malformedLease = malformedRevision.Rent();
        var error = Assert.Throws<InvalidDataException>(() => malformedLease.RenderFrame(0, new PdfRenderOptions(2, 3)));
        Assert.Equal("Invalid Smmr MMR header.", error.Message);
        using var retained = new MemoryStream();
        malformedOwner.WriteCopyTo(retained);
        Assert.Equal(malformed, retained.ToArray());
    }

    [Fact]
    public void NativeOwnerRejectsOversizedSourceBeforeCopyingAndChecksCancellationFirst()
    {
        byte[] oversized = new byte[65 * 1024 * 1024];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => NativeWorkingDocument.OpenDjvu(oversized));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Rejected source allocated {allocated} bytes.");
        Assert.ThrowsAny<OperationCanceledException>(() =>
            NativeWorkingDocument.OpenDjvu(oversized, new CancellationToken(true)));
    }

    [Fact]
    public void SourceCopyHonorsCancellationAndStreamDiagnosesWithoutChangingTheOwner()
    {
        byte[] original = LargeDjvuPage();
        using var owner = NativeWorkingDocument.OpenDjvu(original);
        using var revision = owner.CaptureReadRevision();
        Assert.Throws<ArgumentNullException>(() => owner.WriteCopyTo(null!));
        using var readOnly = new MemoryStream(Array.Empty<byte>(), writable: false);
        Assert.Throws<ArgumentException>(() => owner.WriteCopyTo(readOnly));
        var canceled = new CancellationToken(true);
        Assert.ThrowsAny<OperationCanceledException>(() => owner.WriteCopyTo(null!, canceled));
        Assert.ThrowsAny<OperationCanceledException>(() => NativeWorkingDocument.OpenDjvu(Array.Empty<byte>(), canceled));
        using var failed = new CopyProbeStream(fail: true);
        Assert.Throws<IOException>(() => owner.WriteCopyTo(failed));
        using var caller = new CancellationTokenSource();
        using var interrupted = new CopyProbeStream(caller.Cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => owner.WriteCopyTo(interrupted, caller.Token));
        Assert.InRange(interrupted.MaximumWriteBytes, 1, 65536);
        Assert.InRange(interrupted.Length, 1, original.Length - 1);
        using var complete = new CopyProbeStream();
        owner.WriteCopyTo(complete);
        Assert.InRange(complete.MaximumWriteBytes, 1, 65536);
        Assert.Equal(original, complete.ToArray());
        Assert.Same(revision, owner.CaptureReadRevision());
        using var lease = revision.Rent();
        Assert.Equal(0f, lease.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
    }

    [Fact]
    public void NativeOwnerRecapturesRetiredReadsAndClosesWithoutInterruptingAcquiredCopy()
    {
        byte[] original = LargeDjvuPage();
        using var owner = NativeWorkingDocument.OpenDjvu(original);
        using var first = owner.CaptureReadRevision();
        Assert.Same(first, owner.CaptureReadRevision());
        using var oldLease = first.Rent();
        first.Dispose();
        using var second = owner.CaptureReadRevision();
        Assert.NotSame(first, second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        Assert.Same(second, owner.CaptureReadRevision());
        Assert.ThrowsAny<OperationCanceledException>(() => oldLease.RenderFrame(0, new PdfRenderOptions(2, 3)));
        Assert.Throws<ObjectDisposedException>(() => first.Rent());
        using var held = second.Rent();
        Assert.Equal(0f, held.RenderFrame(0, new PdfRenderOptions(2, 3)).Pixels[20]);
        using var acquiredCopy = new CopyProbeStream(owner.Close);
        owner.WriteCopyTo(acquiredCopy);
        Assert.Equal(original, acquiredCopy.ToArray());
        Assert.Throws<ObjectDisposedException>(() => owner.CaptureReadRevision());
        using var rejected = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() => owner.WriteCopyTo(rejected));
        Assert.Throws<ObjectDisposedException>(() => second.Rent());
        Assert.ThrowsAny<OperationCanceledException>(() => held.RenderFrame(0, new PdfRenderOptions(2, 3)));
        Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
        Assert.Equal(1, owner.PageCount);
        Assert.Equal(second.ContentIdentity, owner.ContentIdentity);
        owner.Close();
        owner.Dispose();
    }

    [Fact]
    public void NativeOwnerPreservesOriginalDjvuBytesAndRendersCapturedIntrinsicOrientation()
    {
        byte[] source = DjvuPage();
        byte[] original = source.ToArray();
        using var owner = NativeWorkingDocument.OpenDjvu(source);
        Array.Clear(source);
        using var copy = new MemoryStream();
        owner.WriteCopyTo(copy);
        Assert.Equal(original, copy.ToArray());
        Assert.Equal(PdfDocumentFormat.Djvu, owner.Format);
        Assert.Equal(1, owner.PageCount);
        using var revision = owner.CaptureReadRevision();
        Assert.Equal(PdfDocumentFormat.Djvu, revision.Format);
        Assert.Equal(owner.ContentIdentity, revision.ContentIdentity);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), owner.ContentIdentity);
        Assert.Equal(1, revision.PageCount);
        var geometry = revision.GetPageGeometry(0);
        Assert.Equal(.72, geometry.Width);
        Assert.Equal(.48, geometry.Height);
        Assert.Equal(90, geometry.Rotation);
        using var lease = revision.Rent();
        PdfRenderedFrame frame = lease.RenderFrame(0, new PdfRenderOptions(2, 3));
        Assert.Equal(2, frame.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        float[] expected = [1, 1, 1, 0, 1, 0];
        for (int pixel = 0; pixel < 6; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
                Assert.Equal(expected[pixel], frame.Pixels[pixel * 4 + channel]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    internal static byte[] Book(string body = "<p>A</p>")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-owner</dc:identifier><dc:title>Native owner</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native owner</title></head><body>{body}</body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native owner</a></li></ol></nav></body></html>
                """);
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }

    private sealed class CopyProbeStream(Action? afterWrite = null, bool fail = false) : Stream
    {
        private readonly MemoryStream _output = new();
        internal int MaximumWriteBytes { get; private set; }
        internal byte[] ToArray() => _output.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _output.Length;
        public override long Position { get => _output.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (fail) throw new IOException("Synthetic stream failure.");
            MaximumWriteBytes = Math.Max(MaximumWriteBytes, buffer.Length);
            _output.Write(buffer);
            afterWrite?.Invoke();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _output.Dispose();
            base.Dispose(disposing);
        }
    }

    private static byte[] LargeDjvuPage() => [.. "AT&T"u8,
        .. Chunk("FORM", [.. DjvuPage()[12..],
            .. Chunk("TXTa", [2, 0, 0, .. Enumerable.Repeat((byte)'A', 131072), 1])])];

    internal static byte[] DjvuPage() => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 0x2c, 1, 22, 5]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];

    // DjvuPage with a TXTa text layer of raw bytes: with one word zone over the whole text when zoned, or none.
    internal static byte[] DjvuPageWithText(byte[] text, bool zoned)
    {
        byte[] zone = new byte[17];
        zone[0] = 6;
        // Left, bottom, width and height of the 3 x 2 page, and the text start, stored with the 0x8000 offset.
        int[] values = [0, 0, 3, 2, 0];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(zone.AsSpan(1 + index * 2), (ushort)(values[index] + 0x8000));
        zone[13] = (byte)text.Length;
        return [.. "AT&T"u8,
            .. Chunk("FORM", [.. "DJVU"u8,
                .. Chunk("INFO", [0, 3, 0, 2, 26, 0, 0x2c, 1, 22, 5]),
                .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80]),
                .. Chunk("TXTa", [0, 0, (byte)text.Length, .. text, .. (zoned ? (byte[])[1, .. zone] : [])])])];
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }
}
