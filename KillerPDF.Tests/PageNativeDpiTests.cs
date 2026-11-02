using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PageNativeDpiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-native-dpi-" + Guid.NewGuid().ToString("N"));

    public PageNativeDpiTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public void EachPageKeepsItsOwnImagesWholeNumberResolutionAndABlankPageTakesTheFallback()
    {
        PdfImage image = PdfImage.FromBitonal(500, 500, new byte[250000]);
        string path = Path.Combine(_folder, "scans.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 340, 340))
            .AddPage(720, 720, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 720, 720))
            .AddBlankPage()
            .Build());

        Dictionary<int, double> dpis = PdfEngineDocumentSession.Open(path).NativeDpis([0, 1, 2], null, 150);

        Assert.Equal(106, dpis[0]);
        Assert.Equal(50, dpis[1]);
        Assert.Equal(150, dpis[2]);
    }

    // R2-015: a canceled export stops reading pages instead of parsing every one first.
    [Fact]
    public void ACanceledReadStopsBeforeTheNextPage()
    {
        PdfImage image = PdfImage.FromBitonal(500, 500, new byte[250000]);
        string path = Path.Combine(_folder, "canceled.pdf");
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < 5; page++)
            builder.AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 340, 340));
        File.WriteAllBytes(path, builder.Build());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Dictionary<int, double> dpis = PdfEngineDocumentSession.Open(path).NativeDpis([0, 1, 2, 3, 4], null, 150, cancel.Token);

        Assert.Empty(dpis);
    }

    [Fact]
    public void ReadingPagesReportsItsProgress()
    {
        PdfImage image = PdfImage.FromBitonal(500, 500, new byte[250000]);
        string path = Path.Combine(_folder, "progress.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 340, 340))
            .AddPage(340, 340, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 340, 340))
            .Build());
        var seen = new List<(int Done, int Total)>();

        PdfEngineDocumentSession.Open(path).NativeDpis([0, 1], null, 150, default, (done, total) => seen.Add((done, total)));

        Assert.Equal([(1, 2), (2, 2)], seen);
    }

    // R2-016: reading one heavy page does not hold up a question about another page.
    [Fact]
    public void ALightPageAnswersWhileAHeavyPageIsStillBeingRead()
    {
        string path = Path.Combine(_folder, "heavy.pdf");
        File.WriteAllBytes(path, HeavyThenLight());
        Assert.True(HeavyReadMilliseconds(path) > 400, "The heavy page must take long enough to overlap.");
        PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long heavyDone = 0;

        Task heavy = Task.Run(() => { session.PageImagePixels(0); heavyDone = clock.ElapsedMilliseconds; });
        Thread.Sleep(100);
        session.PageImagePixels(1);
        long lightDone = clock.ElapsedMilliseconds;
        heavy.GetAwaiter().GetResult();

        Assert.True(lightDone < heavyDone, $"The light page took until {lightDone} ms and the heavy page until {heavyDone} ms.");
    }

    private static long HeavyReadMilliseconds(string path)
    {
        PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(path);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        session.PageImagePixels(0);
        return clock.ElapsedMilliseconds;
    }

    private static byte[] HeavyThenLight()
    {
        var content = new System.Text.StringBuilder();
        for (int line = 0; line < 600_000; line++) content.Append("1 0 0 rg 0 0 1 1 re f\n");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var text = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int table = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }
}
