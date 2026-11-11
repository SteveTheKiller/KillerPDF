using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using Xunit.Abstractions;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubPdfConversionMemoryTests(ITestOutputHelper output)
{
    private static EpubDocument LongBook(int chapters, bool pictures)
    {
        string manifest = "";
        var resources = new List<(string, byte[])>();
        for (int image = 0; pictures && image < 3; image++)
        {
            manifest += $"<item id=\"p{image}\" href=\"images/p{image}.png\" media-type=\"image/png\"/>";
            resources.Add(($"images/p{image}.png", EpubImageCompatibilityTests.Png(300 + image * 20, 200, 2)));
        }
        var source = new Book { ExtraManifest = manifest };
        foreach (var resource in resources) source.Resources.Add(resource);
        for (int chapter = 0; chapter < chapters; chapter++)
        {
            string picture = pictures ? $"<p><img src='../images/p{chapter % 3}.png' alt=''/></p>" : "";
            source.Chapters.Add(Chapter(picture + string.Concat(Enumerable.Repeat(
                "<p>Chapter " + chapter + " carries on with plain words for the page layout to wrap and break.</p>", 40))));
        }
        return EpubDocument.Open(source.Build());
    }

    private static TrueTypeFont Liberation()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream("KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return TrueTypeFont.Load(bytes.ToArray());
    }

    private static EpubPdfConversionOptions Conversion() => new(Liberation())
    {
        PageWidthPoints = 300, PageHeightPoints = 300, MarginPoints = 10, FontSizePoints = 9
    };

    [Fact]
    public void TheLaidOutBookIsReleasedBeforeThePdfIsSerialized()
    {
        // The layout holds the decoded-picture cache and every chapter's page index; once each page is written and
        // the bookmarks are placed, nothing needs them, so the serialization peak must not sit on top of them.
        var book = LongBook(30, pictures: true);
        bool aliveAtBuild = true;
        EpubDocument.BuildProbe = layout =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            aliveAtBuild = layout.IsAlive;
        };
        try { Assert.NotEmpty(book.ConvertToPdf(Conversion())); }
        finally { EpubDocument.BuildProbe = null; }
        Assert.False(aliveAtBuild, "The laid-out book was still reachable when the PDF began to serialize.");
    }

    [Theory]
    // Re-pinned once when converted page content became Flate-compressed (EpubPdfContentCompressionTests
    // checks the content and text); any other change to these bytes is unintended.
    [InlineData(true, 12, "9C8DF97B821EC1A38E586763C6A762CFDC36A82BC166DCEC88123F058B2581CC")]
    [InlineData(false, 12, "9BF861E565CB9296E30BB53D552239668758818BB15307F3A4016EB309F482B3")]
    public void ConversionOfAMultiChapterBookKeepsItsExactBytes(bool pictures, int chapters, string expected)
    {
        // Pinned from the conversion before the memory work: same pages, text, pictures and bookmarks, byte for byte.
        byte[] converted = LongBook(chapters, pictures).ConvertToPdf(Conversion());
        string hash = Convert.ToHexString(SHA256.HashData(converted));
        output.WriteLine($"Pages: {new PdfPageContentReader(PdfDocument.Open(converted)).PageCount}; bytes: {converted.Length}; sha256: {hash}");
        Assert.Equal(expected, hash);
    }
}
