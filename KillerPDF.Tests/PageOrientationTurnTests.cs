using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Fix Page Orientation compares the rotation that shows a page's text upright against the
/// rotation the page shows now. Text is read in content space, before /Rotate, so the upright
/// rotation is absolute; the page's current rotation is the in-app turn once one is captured and
/// the file's own /Rotate before that. The window read an uncaptured page as 0, so a freshly
/// opened scan with /Rotate 90 that already read upright was turned once more to 180 (BF-S006).
/// </summary>
public sealed class PageOrientationTurnTests
{
    [Fact]
    public void ANativelyRotatedPageThatReadsUprightNeedsNoTurn()
    {
        string file = Path.Combine(Path.GetTempPath(), $"killerpdf-orientation-{Guid.NewGuid():N}.pdf");
        try
        {
            // Text running up the content-space page: on a page shown with /Rotate 90 it reads upright.
            File.WriteAllBytes(file, Pdf(90, "BT /F1 14 Tf 0 1 -1 0 150 40 Tm (Upright text that reads along the page) Tj ET"));
            int? upright = UprightFromText(file);
            Assert.Equal(90, upright);

            PdfEngineDocumentSession session = PdfEngineDocumentSession.Open(file, null);
            Assert.Equal(90, session.Pages[0].Rotation);

            // Nothing captured yet, as right after opening: the file's /Rotate is the current rotation.
            var captured = new Dictionary<int, int>();
            int current = captured.TryGetValue(0, out int stored) ? stored : session.Pages[0].Rotation;
            Assert.Equal(0, PageGeometry.TurnBetween(current, upright!.Value));
            // Reading the uncaptured page as 0 is what turned it a second time.
            Assert.Equal(90, PageGeometry.TurnBetween(0, upright.Value));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void TheWindowFallsBackToTheFilesRotationBeforeRotationsAreCaptured()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "PageArrangement.cs"));
        Match method = Regex.Match(source, @"private int CurrentPageRotation\(int page\)(?<body>.*?)\r?\n\r?\n", RegexOptions.Singleline);
        Assert.True(method.Success, "CurrentPageRotation was not found.");
        string body = method.Groups["body"].Value;
        Assert.DoesNotContain("? rotation : 0;", body);
        Assert.Contains("EnsureEngineDocumentSession()", body);
        Assert.Contains(".Pages[page].Rotation", body);
    }

    private static int? UprightFromText(string file)
    {
        using var content = PdfContentDocument.Open(file, null);
        List<(double, double, int)> baselines = [.. content.GetPage(1).GetWords()
            .Where(word => word.Letters.Count > 0)
            .Select(word =>
            {
                var first = word.Letters[0];
                var last = word.Letters[^1];
                return (last.EndBaseLine.X - first.StartBaseLine.X,
                    last.EndBaseLine.Y - first.StartBaseLine.Y, word.Letters.Count);
            })];
        Assert.NotEmpty(baselines);
        return PageGeometry.UprightRotation(baselines);
    }

    private static byte[] Pdf(int rotation, string content)
    {
        const string resources = "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Rotate {rotation} {resources} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
