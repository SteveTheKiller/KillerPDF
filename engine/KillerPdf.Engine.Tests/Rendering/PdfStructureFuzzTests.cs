using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

/// <summary>
/// A small seeded fuzzer for hostile structure: forms that draw each other, patterns that paint
/// with themselves, broken cross-references and cut-off files. The hangs found in review all came
/// from shapes like these. Every case must open, or fail to open, and render, or fail to render,
/// within a deadline; an exception is fine, a hang is not. The seeds are fixed, so a failure names
/// a case that repeats on every run.
/// </summary>
public sealed class PdfStructureFuzzTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    public static TheoryData<int> Seeds()
    {
        var seeds = new TheoryData<int>();
        for (int seed = 0; seed < 40; seed++) seeds.Add(seed);
        return seeds;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AMutatedDocumentOpensAndRendersOrFailsWithinItsDeadline(int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < 8; round++)
        {
            byte[] bytes = Mutate(Seed(random), random);
            Exception? hung = RunWithin(bytes);
            Assert.True(hung is null, $"Seed {seed} round {round} did not finish within {Deadline.TotalSeconds} s.");
        }
    }

    // Null when the case finished, whatever it threw; an exception object when it did not.
    private static Exception? RunWithin(byte[] bytes)
    {
        using var cancel = new CancellationTokenSource(Deadline);
        var done = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                PdfDocument document;
                try { document = PdfDocument.Open(bytes); }
                catch (Exception) { document = PdfDocument.OpenWithCompatibilityRecovery(bytes); }
                var renderer = new PdfPageRenderer(document);
                for (int page = 0; page < 2; page++)
                    try { renderer.Render(page, new PdfRenderOptions(32, 32) { CacheResult = false }, cancel.Token); }
                    catch (Exception) { }
            }
            catch (Exception) { }
            finally { done.Set(); }
        }, 64 * 1024 * 1024) { IsBackground = true };
        worker.Start();
        // The renderer is handed the deadline too, so this extra margin only catches work that
        // ignores cancellation.
        return done.Wait(Deadline + TimeSpan.FromSeconds(10)) ? null : new TimeoutException();
    }

    private static byte[] Seed(Random random) => (random.Next(4)) switch
    {
        0 => Pdf(FormChain(random.Next(1, 30), random.Next(1, 4))),
        1 => Pdf(SelfPattern()),
        2 => Pdf(PlainText()),
        _ => Pdf(FormCycle()),
    };

    private static byte[] Mutate(byte[] source, Random random)
    {
        string text = Encoding.Latin1.GetString(source);
        int mutations = random.Next(1, 5);
        for (int i = 0; i < mutations; i++)
        {
            switch (random.Next(7))
            {
                case 0: // point a reference somewhere else
                    text = ReplaceOne(text, " 0 R", $" 0 R {random.Next(1, 12)} 0 R", random);
                    break;
                case 1: // draw the form more times
                    text = ReplaceOne(text, "/F Do", string.Join(" ", Enumerable.Repeat("/F Do", random.Next(2, 9))), random);
                    break;
                case 2: // break the cross-reference offsets
                    text = ReplaceOne(text, "0000000", random.Next(10).ToString(System.Globalization.CultureInfo.InvariantCulture) + "000000", random);
                    break;
                case 3: // lose the xref section entirely
                    int xref = text.LastIndexOf("xref", StringComparison.Ordinal);
                    if (xref > 0) text = text[..xref] + "%%EOF\n";
                    break;
                case 4: // cut the file short
                    text = text[..random.Next(text.Length / 2, text.Length)];
                    break;
                case 5: // flip bytes
                    var chars = text.ToCharArray();
                    for (int flip = 0; flip < 8; flip++) chars[random.Next(chars.Length)] = (char)random.Next(256);
                    text = new string(chars);
                    break;
                default: // a wrong stream length
                    text = ReplaceOne(text, "/Length ", "/Length 9" , random);
                    break;
            }
        }
        return Encoding.Latin1.GetBytes(text);
    }

    private static string ReplaceOne(string text, string find, string replacement, Random random)
    {
        var hits = new List<int>();
        for (int at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + 1, StringComparison.Ordinal))
            hits.Add(at);
        if (hits.Count == 0) return text;
        int pick = hits[random.Next(hits.Count)];
        return text[..pick] + replacement + text[(pick + find.Length)..];
    }

    private static string[] FormChain(int depth, int fanOut)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 16 16] /Resources << /XObject << /F 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/F Do"),
        };
        for (int level = 0; level <= depth; level++)
        {
            string content = level == depth ? "0 0 1 1 re f" : string.Join(" ", Enumerable.Repeat("/F Do", fanOut));
            string resources = level == depth ? "" : $"/XObject << /F {level + 6} 0 R >>";
            objects.Add(Stream(content, $"/Type /XObject /Subtype /Form /BBox [0 0 16 16] /Resources << {resources} >>"));
        }
        return [.. objects];
    }

    private static string[] FormCycle() =>
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 16 16] /Resources << /XObject << /F 5 0 R >> >> /Contents 4 0 R >>",
        Stream("/F Do"),
        Stream("/F Do /F Do", "/Type /XObject /Subtype /Form /BBox [0 0 16 16] /Resources << /XObject << /F 6 0 R >> >>"),
        Stream("/F Do /F Do", "/Type /XObject /Subtype /Form /BBox [0 0 16 16] /Resources << /XObject << /F 5 0 R >> >>"),
    ];

    private static string[] SelfPattern() =>
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 16 16] /Resources << /Pattern << /P 5 0 R >> >> /Contents 4 0 R >>",
        Stream("/Pattern cs /P scn 0 0 16 16 re f"),
        Stream("/Pattern cs /P scn 0 0 1 1 re f",
            "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1 1] /XStep 0.01 /YStep 0.01 /Resources << /Pattern << /P 5 0 R >> >>"),
    ];

    private static string[] PlainText() =>
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        Stream("BT /F1 12 Tf 10 50 Td (fuzz) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ];

    private static byte[] Pdf(string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";
}
