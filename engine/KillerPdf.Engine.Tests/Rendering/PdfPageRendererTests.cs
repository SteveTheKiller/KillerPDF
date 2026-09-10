using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_UnknownImageFilterDoesNotPaintEncodedBytes(bool arrayFilter)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 2, new PdfContentStreamBuilder()
                .SetFillRgb(0, 1, 0).Rectangle(0, 0, 4, 2).Fill()
                .DrawImage(PdfImage.FromGray(2, 2, new byte[] { 0, 0, 0, 0 }),
                    1, 0, 2, 2)).Build());
        PdfObject filter = arrayFilter ? new PdfArray([Name("XXXDecode")]) : Name("XXXDecode");
        PdfDocument malformed = AddImageDictionaryEntry(source, "Filter", filter, [0]);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(
            PdfDocumentWriter.Write(malformed));

        PdfRenderedPage page = new PdfPageRenderer(recovered).Render(0,
            new PdfRenderOptions(4, 2, includeAnnotations: false, includeFormFields: false));
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, x, y));
        Assert.Contains(page.Diagnostics, diagnostic => diagnostic.Contains("/XXXDecode"));
    }

    [Theory]
    [InlineData(PdfTextRenderingMode.Fill, "B")]
    [InlineData(PdfTextRenderingMode.Fill, "BA")]
    [InlineData(PdfTextRenderingMode.Clip, "B")]
    [InlineData(PdfTextRenderingMode.Clip, "BA")]
    public void Render_RecoveryIsolatesUndecodableType3Glyphs(PdfTextRenderingMode mode, string text)
    {
        var content = new PdfContentStreamBuilder().SaveState().SetFillRgb(1, 0, 0)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextRenderingMode(mode).ShowLatin1Text(text).EndText();
        if (mode == PdfTextRenderingMode.Clip) content.Rectangle(0, 0, 30, 10).Fill();
        content.RestoreState().SetFillRgb(0, 0, 1).Rectangle(25, 0, 5, 10).Fill();
        PdfDocument source = AddType3TriangleFont(PdfDocument.Open(
            new PdfDocumentBuilder().AddPage(30, 10, content).Build()));
        PdfPageTree tree = PdfPageTree.Read(source);
        PdfDictionary page = ResolveDictionary(source, tree.Pages[0].Reference);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(Assert.Single(fonts).Value);
        PdfDictionary font = ResolveDictionary(source, reference);
        PdfDictionary procs = Assert.IsType<PdfDictionary>(font[Name("CharProcs")]);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference bad = update.AddObject(new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Filter"), Name("ASCII85Decode"))]), "v~>"u8));
        var replacements = new Dictionary<PdfName, PdfObject>
        {
            [Name("CharProcs")] = new PdfDictionary(procs.Append(new(Name("B"), bad))),
            [Name("Encoding")] = new PdfDictionary([new(Name("Differences"),
                new PdfArray([new PdfInteger(65), Name("A"), Name("B")]))]),
            [Name("LastChar")] = new PdfInteger(66),
            [Name("Widths")] = Reals(1000, 1000)
        };
        var changed = new PdfDictionary(font.Where(entry => !replacements.ContainsKey(entry.Key))
            .Concat(replacements));
        byte[] bytes = update.ReplaceObject(reference.ObjectNumber, changed).Build();
        var options = new PdfRenderOptions(30, 10, includeAnnotations: false, includeFormFields: false);
        Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() =>
            new PdfPageRenderer(PdfDocument.Open(bytes)).Render(0, options));
        var renderer = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(bytes));
        PdfRenderedPage result = renderer.Render(0, options);
        Assert.Equal([255, 255, 255, 255], Pixel(result, 5, 5));
        Assert.Equal(text == "BA" ? new byte[] { 0, 0, 255, 255 } : [255, 255, 255, 255],
            Pixel(result, 15, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(result, 27, 5));
        Assert.Contains("An undecodable Type 3 glyph was omitted.", result.Diagnostics);
        Assert.Equal(result.Pixels.ToArray(), renderer.Render(0, options).Pixels.ToArray());
    }

    [Theory]
    [InlineData("/bad j")]
    [InlineData("null j")]
    [InlineData("(1) j")]
    public void Render_RecoveryPreservesJoinAfterNonnumericOperand(string operation)
    {
        const string prefix = "1 j 8 w ";
        const string suffix = "10 10 m 40 80 l 45 10 l S";
        byte[] Make(string middle) => new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes(prefix + middle + " " + suffix)).Build();
        var options = new PdfRenderOptions(200, 200);
        Assert.Throws<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(Make(operation)))
            .Render(0, options));
        var actual = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(Make(operation)))
            .Render(0, options);
        var expected = new PdfPageRenderer(PdfDocument.Open(Make(""))).Render(0, options);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains("An invalid line-join operation was ignored.", actual.Diagnostics);
    }

    [Theory]
    [InlineData("/bad 3 Td")]
    [InlineData("3 /bad Td")]
    [InlineData("/bad 3 TD")]
    [InlineData("3 /bad TD")]
    public void Render_RecoveryPreservesTextPositionAndLeadingAfterNonnumericOperand(string operation)
    {
        const string prefix = "BT /Helvetica 12 Tf 1 0 0 1 10 60 Tm 8 TL ";
        const string suffix = " (A) Tj T* (B) Tj ET";
        byte[] Make(string middle) => new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes(prefix + middle + suffix)).Build();
        var options = new PdfRenderOptions(200, 200);
        Assert.Throws<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(Make(operation)))
            .Render(0, options));
        var actual = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(Make(operation)))
            .Render(0, options);
        var expected = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(Make("")))
            .Render(0, options);
        Assert.Contains((byte)0, expected.Pixels.ToArray());
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains("An invalid text-position operation was ignored.", actual.Diagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(257)]
    [InlineData(65536)]
    public void MultiplyCoverage_PreservesEveryBytePairAndSpanBoundaries(int chunk)
    {
        var first = new byte[65536 + 3];
        var second = new byte[65536 + 5];
        var output = new byte[65536 + 7];
        Array.Fill(output, (byte)173);
        for (int i = 0; i < 65536; i++)
        {
            first[i + 1] = (byte)(i >> 8);
            second[i + 2] = (byte)i;
        }
        for (int start = 0; start < 65536; start += chunk)
        {
            int count = Math.Min(chunk, 65536 - start);
            PdfPageRenderer.MultiplyCoverage(first.AsSpan(start + 1, count),
                second.AsSpan(start + 2, count), output.AsSpan(start + 3, count));
        }
        for (int i = 0; i < 65536; i++)
            Assert.Equal((byte)Math.Round((i >> 8) * (i & 255) / 255.0), output[i + 3]);
        Assert.All(output.Take(3), value => Assert.Equal((byte)173, value));
        Assert.All(output.Skip(65536 + 3), value => Assert.Equal((byte)173, value));
    }

    [Theory]
    [InlineData(0, 0, 32, 32, false)]
    [InlineData(0, 0, 32, 32, true)]
    [InlineData(7, 4, 13, 19, false)]
    [InlineData(7, 4, 13, 19, true)]
    [InlineData(-1, 6, 10, 12, false)]
    [InlineData(-1, 6, 10, 12, true)]
    [InlineData(29, 29, 2, 2, false)]
    [InlineData(29, 29, 2, 2, true)]
    public void Render_RectangularClipCropsAntialiasedCoverageExactly(
        int left, int bottom, int width, int height, bool rectangleFirst)
    {
        PdfRenderedPage Render(bool rectangularClip)
        {
            var content = new PdfContentStreamBuilder();
            void RectangleClip() => content.Rectangle(left, bottom, width, height).Clip().EndPath();
            if (rectangularClip && rectangleFirst) RectangleClip();
            content.MoveTo(2.25, 3.5).LineTo(27.75, 8.25)
                .LineTo(11.5, 28.75).ClosePath().Clip().EndPath();
            if (rectangularClip && !rectangleFirst) RectangleClip();
            content.SetFillRgb(.2, .4, .8).Rectangle(0, 0, 32, 32).Fill();
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(32, 32, content).Build());
            return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(32, 32));
        }
        PdfRenderedPage original = Render(false), cropped = Render(true);
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        {
            bool inside = x >= left && x < left + width
                && y >= 32 - bottom - height && y < 32 - bottom;
            Assert.Equal(inside ? Pixel(original, x, y) : [255, 255, 255, 255],
                Pixel(cropped, x, y));
        }
        Assert.Empty(original.Diagnostics);
        Assert.Empty(cropped.Diagnostics);
    }

    [Theory]
    [InlineData(false, "0.0000000000008", 8)]
    [InlineData(true, "0.0000000000008", 8)]
    [InlineData(false, "0.000000000008", 80)]
    [InlineData(true, "0.000000000008", 80)]
    public void Render_TinyTransformedDashesMatchOrdinaryCoordinates(bool phase, string end, int width)
    {
        PdfRenderedPage Render(string content)
        {
            var document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(width, 1, Encoding.ASCII.GetBytes(content)).Build());
            var page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(width, 1));
            Assert.Empty(page.Diagnostics);
            return page;
        }
        var expected = Render($"0 w [1 1] {(phase ? "1" : "0")} d 0 0.5 m {width} 0.5 l S");
        var actual = Render("10000000000000 0 0 1 0 0 cm 0 w "
            + $"[0.0000000000001 0.0000000000001] {(phase ? "0.0000000000001" : "0")} d "
            + $"0 0.5 m {end} 0.5 l S");
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Fact]
    public void Render_ExtremelyDenseDashesReachExpansionLimit()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 1,
            "0 w [0.0000000000001 0.0000000000001] 0 d 0 0.5 m 8 0.5 l S"u8.ToArray()).Build());
        var error = Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(8, 1)));
        Assert.Contains("Line dash expansion limit", error.Message);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(.00001, false)]
    [InlineData(.00001, true)]
    [InlineData(.25, false)]
    [InlineData(.25, true)]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(-2, false)]
    [InlineData(-2, true)]
    public void Render_RectangleCoverageMatchesSubdividedPolygon(double offset, bool clip)
    {
        foreach (bool evenOdd in new[] { false, true })
        {
            PdfRenderedPage Render(bool subdivided)
            {
                var content = new PdfContentStreamBuilder().SetFillRgb(.2, .4, .8);
                if (!subdivided) content.Rectangle(offset, offset, 32, 32);
                else
                {
                    content.MoveTo(offset, offset).LineTo(offset + 16, offset)
                        .LineTo(offset + 32, offset).LineTo(offset + 32, offset + 32)
                        .LineTo(offset, offset + 32).ClosePath();
                }
                if (clip)
                {
                    if (evenOdd) content.ClipEvenOdd();
                    else content.Clip();
                    content.Rectangle(0, 0, 32, 32).Fill();
                }
                else if (evenOdd) content.FillEvenOdd();
                else content.Fill();
                PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(32, 32, content).Build());
                return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(32, 32));
            }
            PdfRenderedPage fast = Render(false), reference = Render(true);
            Assert.Equal(reference.Pixels.ToArray(), fast.Pixels.ToArray());
            Assert.Empty(fast.Diagnostics);
            Assert.Empty(reference.Diagnostics);
        }
    }

    [Theory]
    [InlineData(.1)]
    [InlineData(.33)]
    [InlineData(.5)]
    [InlineData(.66)]
    public void Render_TranslucentFillPreservesEveryOpaqueBackdropValue(double opacity)
    {
        var content = new PdfContentStreamBuilder();
        for (int value = 0; value < 256; value++)
            content.SetFillGray(value / 255d).Rectangle(value, 0, 1, 32).Fill();
        content.SetOpacity(opacity).SetFillRgb(.2, .4, .8).Rectangle(0, 0, 256, 32).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(256, 32, content).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(256, 32, includeAnnotations: false, includeFormFields: false));
        double alpha = opacity * 255 / 255d;
        double outputAlpha = alpha + (1 - alpha);
        for (int y = 0; y < 32; y++)
        for (int value = 0; value < 256; value++)
        {
            byte Blend(byte source) => (byte)Math.Round(Math.Clamp(
                ((1 - alpha) * (value / 255d) + alpha * (source / 255d)) / outputAlpha, 0, 1) * 255);
            Assert.Equal([Blend(204), Blend(102), Blend(51), 255], Pixel(rendered, value, y));
        }
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(33, false)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(31, true)]
    [InlineData(32, true)]
    [InlineData(33, true)]
    public void Render_OpaqueRunsPreserveChannelsAndAdjacentTransparentPixels(int runLength, bool clipped)
    {
        int width = runLength + 6;
        var content = new PdfContentStreamBuilder();
        if (clipped) content.Rectangle(1, 0, width - 2, 4).Clip();
        content.SetFillRgb(1, 0, .5).Rectangle(2, 1, runLength, 2).Fill()
            .SetFillRgb(0, 1, 0).Rectangle(3, 1, 1, 1).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, 4, content).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(width, 4, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < width; x++)
        {
            byte[] expected = x == 3 && y == 2 ? [0, 255, 0, 255]
                : x >= 2 && x < runLength + 2 && y is 1 or 2 ? [128, 0, 255, 255]
                : [255, 255, 255, 0];
            Assert.Equal(expected, Pixel(rendered, x, y));
        }
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_NegativeDashPhaseUsesPdf20CycleInsteadOfAbsoluteValue(bool extended, bool recovery)
    {
        const string path = "2 w 0 50 m 100 50 l S";
        PdfDocument actual;
        if (extended)
            actual = AddStrokeGraphicsState("/Test gs " + path, "D",
                new PdfArray([new PdfArray([new PdfInteger(10), new PdfInteger(5),
                    new PdfInteger(60), new PdfInteger(50)]), new PdfInteger(-20)]), recovery);
        else
        {
            byte[] bytes = new PdfDocumentBuilder().AddPage(100, 100,
                Encoding.ASCII.GetBytes("[10 5 60 50] -20 d " + path)).Build();
            actual = recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
        }
        PdfDocument expected = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes("[10 5 60 50] 230 d " + path)).Build());
        var options = new PdfRenderOptions(100, 100, includeAnnotations: false, includeFormFields: false);
        PdfRenderedPage page = new PdfPageRenderer(actual).Render(0, options);
        Assert.Empty(page.Diagnostics);
        Assert.Equal(new PdfPageRenderer(expected).Render(0, options).Pixels.ToArray(), page.Pixels.ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 5, 50));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(page, 25, 50));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(page, 50, 50));
    }

    [Theory]
    [InlineData("LW", "2 w")]
    [InlineData("LC", "1 J")]
    [InlineData("LJ", "2 j")]
    [InlineData("ML", "1 M")]
    [InlineData("D", "[2 6] -3 d")]
    public void Render_GraphicsStateStrokeSettingsMatchDirectOperators(string key, string direct)
    {
        const string path = "20 20 m 50 80 l 55 20 l S";
        PdfObject value = key switch
        {
            "LW" or "LJ" => new PdfInteger(2),
            "D" => new PdfArray([new PdfArray([new PdfInteger(2), new PdfInteger(6)]),
                new PdfInteger(-3)]),
            _ => new PdfInteger(1)
        };
        PdfDocument actual = AddStrokeGraphicsState(
            $"q 10 w /Test gs {path} Q 10 10 m 90 10 l S", key, value);
        PdfDocument expected = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes($"q 10 w {direct} {path} Q 10 10 m 90 10 l S")).Build());
        var options = new PdfRenderOptions(200, 200, includeAnnotations: false, includeFormFields: false);
        PdfRenderedPage rendered = new PdfPageRenderer(actual).Render(0, options);
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new PdfPageRenderer(expected).Render(0, options).Pixels.ToArray(),
            rendered.Pixels.ToArray());
    }

    [Theory]
    [InlineData("LW")]
    [InlineData("LC")]
    [InlineData("LJ")]
    [InlineData("ML")]
    [InlineData("D")]
    public void Render_InvalidGraphicsStateStrokeSettingsKeepStrictValidation(string key)
    {
        const string content = "10 w /Test gs 20 20 m 50 80 l 55 20 l S";
        PdfObject invalid = key switch
        {
            "D" => new PdfArray([new PdfArray([new PdfInteger(0)]), new PdfInteger(0)]),
            "ML" => new PdfReal(0.3),
            _ => new PdfInteger(-1)
        };
        PdfDocument strict = AddStrokeGraphicsState(content, key, invalid);
        var options = new PdfRenderOptions(200, 200, includeAnnotations: false, includeFormFields: false);
        Assert.Throws<FormatException>(() => new PdfPageRenderer(strict).Render(0, options));
        PdfDocument recovered = AddStrokeGraphicsState(content, key, invalid, recovery: true);
        PdfRenderedPage actual = new PdfPageRenderer(recovered).Render(0, options);
        Assert.NotEmpty(actual.Diagnostics);
        string expectedContent = content.Replace("/Test gs", key == "ML" ? "1 M" : "");
        PdfDocument expected = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes(expectedContent)).Build());
        Assert.Equal(new PdfPageRenderer(expected).Render(0, options).Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfDocument AddStrokeGraphicsState(string content, string key, PdfObject value,
        bool recovery = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTree tree = PdfPageTree.Read(source);
        var reference = tree.Pages[0].Reference;
        PdfDictionary page = ResolveDictionary(source, reference);
        var resources = new PdfDictionary([new(Name("ExtGState"),
            new PdfDictionary([new(Name("Test"), new PdfDictionary([new(Name(key), value)]))]))]);
        var changed = new PdfDictionary(page.Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), resources)));
        byte[] bytes = new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, changed).Build();
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }

    [Fact]
    public void Render_DecodesJbig2SymbolDictionaryContextReuse()
    {
        byte[] pdf = Convert.FromBase64String(
            "JVBERi0xLjQKJbW2CgoxIDAgb2JqCjw8CiAgL1R5cGUgL0NhdGFsb2cKICAvUGFnZXMgMiAwIFIKPj4KZW5kb2JqCgoyIDAgb2JqCjw8CiAgL1R5cGUgL1BhZ2VzCiAgL0tpZHMgWzMgMCBSXQogIC9Db3VudCAxCj4+CmVuZG9iagoKMyAwIG9iago8PAogIC9UeXBlIC9QYWdlCiAgL1BhcmVudCAyIDAgUgogIC9NZWRpYUJveCBbMCAwIDM5OSA0MDBdCiAgL0NvbnRlbnRzIDQgMCBSCiAgL1Jlc291cmNlcyA8PAogICAgL1hPYmplY3QgPDwKICAgICAgL0ltIDUgMCBSCiAgICA+PgogID4+Cj4+CmVuZG9iagoKNCAwIG9iago8PC9MZW5ndGggMjU+PgpzdHJlYW0KMzk5IDAgMCA0MDAgMCAwIGNtCi9JbSBEbwplbmRzdHJlYW0KZW5kb2JqCgo1IDAgb2JqCjw8CiAgL0xlbmd0aCA0NTQKICAvVHlwZSAvWE9iamVjdAogIC9TdWJ0eXBlIC9JbWFnZQogIC9XaWR0aCAzOTkKICAvSGVpZ2h0IDQwMAogIC9Db2xvclNwYWNlIC9EZXZpY2VHcmF5CiAgL0ZpbHRlciAvSkJJRzJEZWNvZGUKICAvQml0c1BlckNvbXBvbmVudCAxCj4+CnN0cmVhbQoAAAAAMAABAAAAEwAAAY8AAAGQAAAAAAAAAAABAAAAAAABAAEBAAAAOAIAA//9/wL+/v4AAAABAAAAASeDXjClUJAwSe6SGT00AMVhNLTpjv5bXmz0gcy/fy0SLqcpV/+sAAAAAgAjAQEAAAAqAQAD//3/Av7+/gAAAAEAAAABJ4NRqghtsBLrYYOQULeuW74Xq8xWb/+sAAAAAwAjAQEAAABWAwAD//3/Av7+/gAAAAEAAAABFM+Ee8gfZcaqDEQ9NdMJ4/uRbgIm84ozZtY2tsZqanJ+8q0px+16fhNomknpCh/lTqgwEYn7SfMKNUL/Cv6GQ8SU/6wAAAAEAGEBAgMBAAAAjAEAA//9/wL+/v4AAAAEAAAAAQ6C6s2APeK0+6f8jziDL4ksCMa2hl28d1frZXNZHlqv4CboyxqjZpfmKvzrA/q4mRXvdIzvLac3/t72YqugosWGJEIrzvkK4/S5vzZ4Rq1tEsIyENTFABrE4agJ5fXiP/y7iVTVa6RfRvd7Wh53TwOnD4v6Lx51H/+sAAAABQcgBAEAAAAnAAABjwAAAZAAAAAAAAAAAAAAGAAAAASI+tjT5Giw9PsdtXqP9/+sCmVuZHN0cmVhbQplbmRvYmoKCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzYgZiAKMDAwMDAwMDAxNCAwMDAwMCBuIAowMDAwMDAwMDY4IDAwMDAwIG4gCjAwMDAwMDAxMzIgMDAwMDAgbiAKMDAwMDAwMDI4OCAwMDAwMCBuIAowMDAwMDAwMzYyIDAwMDAwIG4gCgp0cmFpbGVyCjw8CiAgL1NpemUgNgogIC9Sb290IDEgMCBSCj4+CnN0YXJ0eHJlZgoxMDAzCiUlRU9GCg==");
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(pdf);

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(399, 400, includeAnnotations: false, includeFormFields: false));

        Assert.Empty(page.Diagnostics);
        Assert.Contains(page.Pixels.ToArray().Chunk(4), pixel =>
            pixel[0] != 255 || pixel[1] != 255 || pixel[2] != 255);
    }

    [Theory]
    [InlineData("0.25 0 0 sc", 64)]
    [InlineData("0.25 0 0 scn", 64)]
    [InlineData("sc", 128)]
    [InlineData("scn", 128)]
    [InlineData("0.25 0 0 SC", 64)]
    [InlineData("0.25 0 0 SCN", 64)]
    [InlineData("SC", 128)]
    [InlineData("SCN", 128)]
    public void Render_RecoversColorOperandCountWithoutChangingStrictRendering(string operation, byte gray)
    {
        bool stroke = operation.Contains('S');
        string content = stroke
            ? $"0.5 G 4 w {operation} 0 2 m 4 2 l S"
            : $"0.5 g {operation} 0 0 4 4 re f";
        byte[] bytes = new PdfDocumentBuilder().AddPage(4, 4,
            Encoding.ASCII.GetBytes(content)).Build();
        var options = new PdfRenderOptions(4, 4, includeAnnotations: false, includeFormFields: false);
        Assert.Throws<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(bytes)).Render(0, options));

        PdfRenderedPage page = new PdfPageRenderer(
            PdfDocument.OpenWithCompatibilityRecovery(bytes)).Render(0, options);
        Assert.Equal(new byte[] { gray, gray, gray, 255 }, Pixel(page, 2, 2));
        Assert.NotEmpty(page.Diagnostics);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("null")]
    [InlineData("integer")]
    [InlineData("array")]
    public void Render_RecoversInvalidPageResourcesForIndependentContent(string kind)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, "1 0 0 rg 2 2 4 4 re f"u8.ToArray()).Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        var reference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = ResolveDictionary(source, reference);
        PdfObject resources = kind switch
        {
            "stream" => page[Name("Contents")],
            "null" => PdfNull.Instance,
            "integer" => new PdfInteger(1),
            _ => new PdfArray([])
        };
        var changed = new PdfDictionary(page.Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), resources)));
        byte[] bytes = new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber, changed).Build();
        Assert.Throws<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(bytes)));
        var renderer = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(bytes));
        var options = new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false);
        PdfRenderedPage rendered = renderer.Render(0, options);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(rendered, 4, 6));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(rendered, 0, 0));
        Assert.Contains("Invalid page resources were treated as empty.", rendered.Diagnostics);
        Assert.Equal(rendered.Pixels.ToArray(), renderer.Render(0, options).Pixels.ToArray());
    }

    [Fact]
    public void Render_BlankPageProducesOpaqueWhiteBgra()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(10, 20).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 4, includeAnnotations: false, includeFormFields: false));

        Assert.Equal(2, page.Width);
        Assert.Equal(4, page.Height);
        Assert.Equal(32, page.Pixels.Length);
        Assert.All(page.Pixels.ToArray().Chunk(4), pixel =>
            Assert.Equal([255, 255, 255, 255], pixel));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_TransformsAndFillsRgbRectangle()
    {
        byte[] content = "q 1 0 0 1 2 3 cm 1 0 0 rg 1 1 2 2 re f Q"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 3, 4));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 0));
    }

    [Fact]
    public void Render_ReusesParsedPageAtDifferentOutputSizes()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, "1 0 0 rg 0 0 5 10 re f"u8.ToArray()).Build());
        var renderer = new PdfPageRenderer(document);

        PdfRenderedPage small = renderer.Render(0,
            new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage large = renderer.Render(0,
            new PdfRenderOptions(20, 20, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(small, 2, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(small, 7, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(large, 4, 10));
        Assert.Equal([255, 255, 255, 255], Pixel(large, 14, 10));
    }

    [Fact]
    public void Render_ReusesExactCompletedRastersAndKeepsProfilesSeparate()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, "1 0 0 rg 0 0 5 10 re f"u8.ToArray()).Build());
        var renderer = new PdfPageRenderer(document);
        var baseOptions = new PdfRenderOptions(10, 10,
            includeAnnotations: false, includeFormFields: false);

        PdfRenderedPage first = renderer.Render(0, baseOptions);
        PdfRenderedPage repeated = renderer.Render(0, baseOptions);
        PdfRenderedPage transparent = renderer.Render(0, new PdfRenderOptions(10, 10,
            transparentBackground: true, includeAnnotations: false, includeFormFields: false));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Same(first, repeated);
        Assert.NotSame(first, transparent);
        Assert.Throws<OperationCanceledException>(() =>
            renderer.Render(0, baseOptions, canceled.Token));
    }

    [Fact]
    public void Render_BoundsParsedPageCache()
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < 40; page++) builder.AddBlankPage(10, 10);
        var renderer = new PdfPageRenderer(PdfDocument.Open(builder.Build()));

        for (int page = 0; page < 40; page++)
            renderer.Render(page, new PdfRenderOptions(1, 1,
                includeAnnotations: false, includeFormFields: false));
        object cache = typeof(PdfPageRenderer).GetField("_instructionCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        int count = (int)cache.GetType().GetProperty("Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;
        object renderCache = typeof(PdfPageRenderer).GetField("_renderCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        int renderCount = (int)renderCache.GetType().GetProperty("Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderCache)!;

        Assert.Equal(32, count);
        Assert.Equal(16, renderCount);
    }

    [Fact]
    public void OptionsRejectUnboundedPixelBuffers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRenderOptions(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfRenderOptions(PdfRenderOptions.MaximumDimension + 1, 1));
        Assert.Throws<ArgumentException>(() => new PdfRenderOptions(20_000, 20_000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfRenderOptions(1, 1, maximumPixelBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRenderOptions(
            1, 1, maximumPixelBytes: PdfRenderOptions.MaximumPixelBytes + 1));
        Assert.Throws<ArgumentException>(() =>
            new PdfRenderOptions(100, 100, maximumPixelBytes: 39_999));

        var bounded = new PdfRenderOptions(100, 100, maximumPixelBytes: 40_000);
        Assert.Equal(40_000, bounded.PixelByteLimit);
    }

    [Fact]
    public async Task Render_HonorsCancellationDuringRasterization()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, "0 0 100 100 re f"u8.ToArray()).Build());
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Task render = Task.Factory.StartNew(() =>
        {
            started.Set();
            new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(4096, 4096,
                    includeAnnotations: false, includeFormFields: false),
                cancellation.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        started.Wait();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
    }

    [Fact]
    public void Render_FillsGeneralPathAndStrokesCurve()
    {
        byte[] content = "0 1 0 rg 1 1 m 8 1 l 4 8 l h f 0 0 1 RG 1 w 1 9 m 3 5 7 5 9 9 c S"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(page, 4, 7));
        AssertNear([255, 0, 0, 255], Pixel(page, 1, 1), 64);
        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 9));
    }

    [Fact]
    public void Render_AcceptsEmptyPathPaintingOperatorsAsNoOps()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, "f F f* S s B B* b b*"u8.ToArray()).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_PreservesNonzeroAndEvenOddFillRules()
    {
        const string paths = "1 1 m 9 1 l 9 9 l 1 9 l h "
            + "3 3 m 7 3 l 7 7 l 3 7 l h ";
        PdfRenderedPage nonzero = Render(paths + "f");
        PdfRenderedPage evenOdd = Render(paths + "f*");

        Assert.Equal([0, 0, 0, 255], Pixel(nonzero, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(evenOdd, 5, 5));
        Assert.Equal([0, 0, 0, 255], Pixel(evenOdd, 2, 5));

        static PdfRenderedPage Render(string content)
        {
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(10, 10, Encoding.ASCII.GetBytes(content)).Build());
            return new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(10, 10,
                    includeAnnotations: false, includeFormFields: false));
        }
    }

    [Fact]
    public void Render_AppliesLineDashPatternPhaseAndTransform()
    {
        PdfDocument patternDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, "0 w [1 3] 0 d 0 0.5 m 8 0.5 l S"u8.ToArray()).Build());
        PdfDocument phaseDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, "0 w [1 3] 2 d 0 0.5 m 8 0.5 l S"u8.ToArray()).Build());
        PdfDocument negativePhaseDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, "0 w [1 3] -2 d 0 0.5 m 8 0.5 l S"u8.ToArray()).Build());
        PdfDocument transformDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1,
                "2 0 0 1 0 0 cm 0 w [1 1] 0 d 0 0.5 m 4 0.5 l S"u8.ToArray()).Build());

        PdfRenderedPage pattern = new PdfPageRenderer(patternDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage phase = new PdfPageRenderer(phaseDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage negativePhase = new PdfPageRenderer(negativePhaseDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage transformed = new PdfPageRenderer(transformDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(pattern, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(pattern, 2, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(pattern, 4, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(phase, 0, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(phase, 2, 0));
        Assert.Equal(phase.Pixels.ToArray(), negativePhase.Pixels.ToArray());
        Assert.Equal([255, 255, 255, 255], Pixel(transformed, 3, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(transformed, 4, 0));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Render_ZeroLengthDashesRetainCapsAndDirection(int cap, bool diagonal)
    {
        string path = diagonal ? "10 10 m 90 90 l S" : "10 50 m 90 50 l S";
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes($"6 w {cap} J [0 10] 0 d {path}")).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        int centerY = diagonal ? 89 : 49;
        Assert.Equal(cap == 0 ? new byte[] { 255, 255, 255, 255 } : new byte[] { 0, 0, 0, 255 },
            Pixel(rendered, 10, centerY));
        if (cap == 2 && diagonal)
        {
            Assert.InRange(Pixel(rendered, 13, 89)[0], 50, 100);
            Assert.InRange(Pixel(rendered, 12, 87)[0], 230, 255);
        }
        Assert.InRange(Pixel(rendered, diagonal ? 14 : 15, diagonal ? 85 : 49)[0], 230, 255);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(2, 18)]
    [InlineData(-2, 12)]
    [InlineData(10, 10)]
    public void Render_ZeroLengthDashesRespectPhaseAndRestartAtEachSubpath(int phase, int firstDot)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes($"2 w 1 J [0 10] {phase} d 10 50 m 90 50 l 10 20 m 90 20 l S")).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        foreach (int y in new[] { 49, 79 })
        {
            Assert.InRange(Pixel(rendered, firstDot, y)[0], 0, 80);
            Assert.InRange(Pixel(rendered, firstDot + 10, y)[0], 0, 80);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(rendered, firstDot + 5, y));
        }
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_TransformsStrokeWidthWithTheCurrentMatrix()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "q 0.1 0 0 0.1 0 0 cm 50 w 0 50 m 100 50 l S Q"))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 5, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 5, 9));
    }

    [Fact]
    public void Render_AppliesButtRoundAndProjectingLineCaps()
    {
        PdfRenderedPage butt = RenderCap(PdfLineCap.Butt);
        PdfRenderedPage round = RenderCap(PdfLineCap.Round);
        PdfRenderedPage square = RenderCap(PdfLineCap.ProjectingSquare);

        Assert.Equal([255, 255, 255, 255], Pixel(butt, 0, 1));
        AssertPainted(Pixel(round, 0, 1));
        AssertNear([255, 255, 255, 255], Pixel(round, 0, 0), 32);
        AssertPainted(Pixel(square, 0, 0));

        static PdfRenderedPage RenderCap(PdfLineCap cap)
        {
            var content = new PdfContentStreamBuilder()
                .SetLineWidth(2).SetLineCap(cap)
                .MoveTo(1.5, 1.5).LineTo(3.5, 1.5).Stroke();
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(5, 3, content).Build());
            return new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(5, 3,
                    includeAnnotations: false, includeFormFields: false));
        }
    }

    [Fact]
    public void Render_AppliesMiterRoundAndBevelLineJoins()
    {
        PdfRenderedPage miter = RenderJoin(PdfLineJoin.Miter, 10);
        PdfRenderedPage limitedMiter = RenderJoin(PdfLineJoin.Miter, 1);
        PdfRenderedPage round = RenderJoin(PdfLineJoin.Round, 10);
        PdfRenderedPage bevel = RenderJoin(PdfLineJoin.Bevel, 10);

        AssertPainted(Pixel(miter, 9, 1));
        Assert.Equal([255, 255, 255, 255], Pixel(limitedMiter, 9, 1));
        AssertPainted(Pixel(round, 9, 2));
        Assert.Equal([255, 255, 255, 255], Pixel(bevel, 9, 2));

        static PdfRenderedPage RenderJoin(PdfLineJoin join, double limit)
        {
            var content = new PdfContentStreamBuilder()
                .SetLineWidth(4).SetLineJoin(join).SetMiterLimit(limit)
                .MoveTo(4, 4).LineTo(10, 16).LineTo(16, 4).Stroke();
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(20, 20, content).Build());
            return new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(20, 20,
                    includeAnnotations: false, includeFormFields: false));
        }
    }

    [Fact]
    public void Render_ReportsUnsupportedOperatorsOutsideCompatibilitySections()
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "/Perceptual ri 1 i /Tag MP 1 2 d0 1 2 3 4 5 6 d1 "
            + "BX UnsupportedInsideCompatibility EX UnsupportedOutsideCompatibility");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain(page.Diagnostics,
            diagnostic => diagnostic.Contains("UnsupportedInsideCompatibility",
                StringComparison.Ordinal));
        Assert.Contains("Rendering operator UnsupportedOutsideCompatibility is not implemented.",
            page.Diagnostics);
    }

    [Fact]
    public void Render_IgnoresClosePathWithoutAnActiveSubpath()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, "h"u8.ToArray()).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain("Rendering operator h is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_PaintsNamedAndExplicitNonPatternColorSpaces()
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "/CS1 cs 1 0 0 sc 0 0 1 1 re f "
            + "/DeviceCMYK cs 1 0 0 0 scn 1 0 1 1 re f "
            + "/CS1 CS 0 0 1 SCN 0 w 0 1.5 m 2 1.5 l S");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 2, content).Build());
        PdfDocument document = AddPageColorSpaceResource(source, "CS1", Name("DeviceRGB"));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 2, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 1));
        Assert.Equal([239, 174, 0, 255], Pixel(page, 1, 1));
    }

    [Fact]
    public void Render_HonorsDefaultOptionalContentVisibility()
    {
        var hidden = new PdfOptionalContentGroup("Hidden", initiallyVisible: false);
        var visible = new PdfOptionalContentGroup("Visible", initiallyVisible: true);
        var content = new PdfContentStreamBuilder()
            .BeginOptionalContent(hidden)
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill()
            .EndMarkedContent()
            .BeginOptionalContent(visible)
            .SetFillRgb(0, 1, 0).Rectangle(1, 0, 1, 1).Fill()
            .EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 1, 0));
    }

    [Fact]
    public void Render_EvaluatesOptionalContentMembershipPoliciesAndExpressions()
    {
        PdfRenderedPage hiddenByPolicy = new PdfPageRenderer(
            OptionalContentMembershipDocument("/OCGs [5 0 R 6 0 R] /P /AllOn")).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage visibleByExpression = new PdfPageRenderer(
            OptionalContentMembershipDocument("/VE [/Or 5 0 R 6 0 R]")).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(hiddenByPolicy, 0, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(visibleByExpression, 0, 0));
    }

    [Fact]
    public void Render_CompatibilityRecoveryBoundsCyclicVisibilityExpressions()
    {
        PdfDocument strict = OptionalContentMembershipDocument(
            "/VE [/And 7 0 R 5 0 R]");
        Assert.Throws<FormatException>(() => new PdfPageRenderer(strict).Render(
            0, new PdfRenderOptions(2, 1,
                includeAnnotations: false, includeFormFields: false)));

        PdfDocument recovered = OptionalContentMembershipDocument(
            "/VE [/And 7 0 R 5 0 R]", compatibilityRecovery: true);
        PdfRenderedPage rendered = new PdfPageRenderer(recovered).Render(
            0, new PdfRenderOptions(2, 1,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(rendered, 0, 0));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_HonorsOptionalContentAttachedToXObjects()
    {
        PdfRenderedPage page = new PdfPageRenderer(OptionalContentXObjectDocument()).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
    }

    [Fact]
    public void Render_HonorsOptionalContentAttachedToAnnotations()
    {
        PdfRenderedPage visible = new PdfPageRenderer(
            OptionalContentAnnotationDocument(hidden: false)).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: true, includeFormFields: false));
        PdfRenderedPage hidden = new PdfPageRenderer(
            OptionalContentAnnotationDocument(hidden: true)).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: true, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(visible, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(hidden, 0, 0));
    }

    [Fact]
    public void Render_FillsWithColoredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetFillPattern(pattern).Rectangle(0, 0, 4, 1).Fill())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_TilingPatternUsesInitialSpaceAfterContentTransform()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 2, new PdfContentStreamBuilder()
                .Transform(2, 0, 0, 2, 0, 0)
                .SetFillPattern(pattern).Rectangle(0, 0, 4, 1).Fill())
            .Build());
        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(8, 2, includeAnnotations: false, includeFormFields: false));

        for (int x = 0; x < 8; x++)
            Assert.Equal(x % 2 == 0 ? new byte[] { 0, 0, 255, 255 }
                : new byte[] { 255, 255, 255, 255 }, Pixel(page, x, 0));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_FillsWithUncoloredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .Rectangle(0, 0, 1, 1).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetFillPattern(pattern, new PdfRgbColor(0, 1, 0))
                .Rectangle(0, 0, 4, 1).Fill())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_StrokesWithColoredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetStrokePattern(pattern).SetLineWidth(1)
                .MoveTo(0, 0.5).LineTo(4, 0.5).Stroke())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_StrokesWithUncoloredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .Rectangle(0, 0, 1, 1).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetStrokePattern(pattern, new PdfRgbColor(0, 0, 1)).SetLineWidth(1)
                .MoveTo(0, 0.5).LineTo(4, 0.5).Stroke())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_DecodesAndTransformsRgbImageXObject()
    {
        PdfImage image = PdfImage.FromRgb(2, 1, new byte[]
        {
            255, 0, 0,
            0, 255, 0
        });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(image, 2, 3, 6, 2))
            .Build());

        var renderer = new PdfPageRenderer(document);
        PdfRenderOptions options = new(10, 10,
            includeAnnotations: false, includeFormFields: false);
        PdfRenderedPage page = renderer.Render(0, options);
        PdfRenderedPage repeated = renderer.Render(0, options);
        object cache = typeof(PdfPageRenderer).GetField("_imageCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        int count = (int)cache.GetType().GetProperty("Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;

        Assert.Equal([0, 0, 255, 255], Pixel(page, 3, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 7, 6));
        Assert.Equal(page.Pixels.ToArray(), repeated.Pixels.ToArray());
        Assert.Equal(1, count);
        Assert.DoesNotContain("Image rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_DecodesEightBitGrayImages()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 32, 224 }), 0, 0, 2, 1))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([32, 32, 32, 255], Pixel(page, 0, 0));
        Assert.Equal([224, 224, 224, 255], Pixel(page, 1, 0));
        Assert.Empty(page.Diagnostics);
    }

    [Theory]
    [InlineData(2, new byte[] { 0x30 })]
    [InlineData(4, new byte[] { 0x0F })]
    [InlineData(16, new byte[] { 0x00, 0x00, 0xFF, 0xFF })]
    public void Render_DecodesPackedImageSampleDepths(int bits, byte[] samples)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 0, 0, 2, 1))
            .Build());
        PdfDocument document = AddImageDictionaryEntry(source, "BitsPerComponent",
            new PdfInteger(bits), Compress(samples));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            page.Diagnostics);
    }

    [Fact]
    public void Render_DecodesBaselineJpegImageXObjects()
    {
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDwyiiivw8/0oP/2Q==");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromJpeg(jpeg), 2, 3, 6, 2)).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] pixel = Pixel(page, 3, 6);
        Assert.InRange(pixel[2], 235, 245);
        Assert.InRange(pixel[1], 15, 25);
        Assert.InRange(pixel[0], 5, 15);
        Assert.Equal(255, pixel[3]);
        Assert.DoesNotContain(page.Diagnostics, diagnostic => diagnostic.StartsWith(
            "The image compression filter is not implemented.", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_DecodesJpegThumbnailsAtScaledIdctResolutions()
    {
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDwyiiivw8/0oP/2Q==");
        jpeg = [.. jpeg.AsSpan(0, 155), 20, 20, 20, .. jpeg.AsSpan(155)];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(1, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromJpeg(jpeg), 0, 0, 1, 1)).Build());
        var renderer = new PdfPageRenderer(document);

        PdfRenderedPage page = renderer.Render(0, new PdfRenderOptions(
            1, 1, includeAnnotations: false, includeFormFields: false));
        renderer.Render(0, new PdfRenderOptions(
            2, 2, includeAnnotations: false, includeFormFields: false));
        renderer.Render(0, new PdfRenderOptions(
            4, 4, includeAnnotations: false, includeFormFields: false));
        renderer.Render(0, new PdfRenderOptions(
            8, 8, includeAnnotations: false, includeFormFields: false));
        object cache = typeof(PdfPageRenderer).GetField("_imageCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        object usage = cache.GetType().GetField("_usage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;
        object[] decoded = ((System.Collections.IEnumerable)usage).Cast<object>()
            .Select(entry => entry.GetType().GetField("Item2")!.GetValue(entry)!)
            .ToArray();
        int[] decodedWidths = decoded.Select(image =>
            (int)image.GetType().GetProperty("Width")!.GetValue(image)!).Order().ToArray();
        int[] decodedHeights = decoded.Select(image =>
            (int)image.GetType().GetProperty("Height")!.GetValue(image)!).Order().ToArray();

        Assert.Equal([1, 2, 4, 8], decodedWidths);
        Assert.Equal([1, 2, 4, 8], decodedHeights);
        Assert.InRange(Pixel(page, 0, 0)[2], 235, 245);
        Assert.DoesNotContain(page.Diagnostics, diagnostic => diagnostic.StartsWith(
            "The image compression filter is not implemented.", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_CompatibilityRecoveryDecodesPngMislabeledAsJpeg()
    {
        byte[] png = PngRgba(2, 1, [255, 0, 0, 128, 0, 255, 0, 255]);
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 2, 1)).Build());
        byte[] malformedBytes = AddImageDictionaryEntryBytes(source, "Filter",
            Name("DCTDecode"), png);
        PdfDocument malformed = PdfDocument.Open(malformedBytes);
        PdfRenderOptions options = new(2, 1, transparentBackground: true,
            includeAnnotations: false, includeFormFields: false);

        PdfRenderedPage strict = new PdfPageRenderer(malformed).Render(0, options);
        PdfDocument recoveredDocument = PdfDocument.OpenWithCompatibilityRecovery(malformedBytes);
        PdfRenderedPage recovered = new PdfPageRenderer(recoveredDocument).Render(0, options);

        Assert.Contains(strict.Diagnostics, diagnostic => diagnostic.StartsWith(
            "The image compression filter is not implemented.", StringComparison.Ordinal));
        Assert.Empty(recovered.Diagnostics);
        Assert.Equal([0, 0, 255, 128], Pixel(recovered, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(recovered, 1, 0));
    }

    [Fact]
    public void Render_CompatibilityRecoveryIgnoresExtraImageSamples()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 2, 1)).Build());
        byte[] malformedBytes = AddImageDictionaryEntryBytes(source, "Filter",
            Name("FlateDecode"), Compress([
                255, 0, 0, 0, 255, 0,
                0, 0, 255]));
        PdfRenderOptions options = new(2, 1, includeAnnotations: false,
            includeFormFields: false);

        PdfRenderedPage strict = new PdfPageRenderer(
            PdfDocument.Open(malformedBytes)).Render(0, options);
        PdfRenderedPage recovered = new PdfPageRenderer(
            PdfDocument.OpenWithCompatibilityRecovery(malformedBytes)).Render(0, options);

        Assert.Contains(strict.Diagnostics, diagnostic => diagnostic.StartsWith(
            "The image compression filter is not implemented.", StringComparison.Ordinal));
        Assert.Empty(recovered.Diagnostics);
        Assert.Equal([0, 0, 255, 255], Pixel(recovered, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(recovered, 1, 0));
    }

    [Fact]
    public void Render_CompatibilityRecoveryUsesCompleteRowsFromShortImage()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 2, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(2, 2, new byte[12]), 0, 0, 2, 2)).Build());
        byte[] malformedBytes = AddImageDictionaryEntryBytes(source, "Filter",
            Name("FlateDecode"), Compress([255, 0, 0, 0, 255, 0]));
        PdfRenderOptions options = new(2, 2, includeAnnotations: false,
            includeFormFields: false);

        Assert.Throws<FormatException>(() => new PdfPageRenderer(
            PdfDocument.Open(malformedBytes)).Render(0, options));
        PdfRenderedPage recovered = new PdfPageRenderer(
            PdfDocument.OpenWithCompatibilityRecovery(malformedBytes)).Render(0, options);

        Assert.Empty(recovered.Diagnostics);
        Assert.Equal([0, 0, 255, 255], Pixel(recovered, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(recovered, 1, 1));
    }

    [Fact]
    public void Render_CompositesImageSoftMasks()
    {
        PdfImage image = PdfImage.FromRgba(2, 1, new byte[]
        {
            255, 0, 0, 128,
            0, 255, 0, 0
        });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(image, 2, 3, 6, 2))
            .Build());

        var renderer = new PdfPageRenderer(document);
        PdfRenderOptions options = new(10, 10, transparentBackground: true,
            includeAnnotations: false, includeFormFields: false);
        PdfRenderedPage page = renderer.Render(0, options);
        PdfRenderedPage repeated = renderer.Render(0, options);
        object cache = typeof(PdfPageRenderer).GetField("_imageCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        int count = (int)cache.GetType().GetProperty("Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;

        Assert.Equal([0, 0, 255, 128], Pixel(page, 3, 6));
        Assert.Equal([255, 255, 255, 0], Pixel(page, 7, 6));
        Assert.Equal(page.Pixels.ToArray(), repeated.Pixels.ToArray());
        Assert.Equal(2, count);
        Assert.DoesNotContain("The image soft mask is not implemented.", page.Diagnostics);
    }

    [Theory]
    [InlineData(2, new byte[] { 0x30 })]
    [InlineData(4, new byte[] { 0x0F })]
    [InlineData(16, new byte[] { 0x00, 0x00, 0xFF, 0xFF })]
    public void Render_CompositesPackedImageSoftMasks(int bits, byte[] samples)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgba(2, 1, new byte[]
                {
                    255, 0, 0, 255,
                    0, 255, 0, 255
                }), 0, 0, 2, 1))
            .Build());
        PdfDocument document = ReplaceImageSoftMask(source, bits, Compress(samples));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 0], Pixel(page, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 1, 0));
        Assert.DoesNotContain("The image soft mask is not implemented.", page.Diagnostics);
    }

    [Theory]
    [InlineData(1, new byte[] { 0x50 })]
    [InlineData(2, new byte[] { 0x33 })]
    [InlineData(4, new byte[] { 0x0F, 0x0F })]
    [InlineData(8, new byte[] { 0, 255, 0, 255 })]
    [InlineData(16, new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 })]
    public void Render_PreservesSoftMaskDetailBeyondImageResolution(int bits, byte[] samples)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgba(1, 1, new byte[] { 255, 0, 0, 128 }), 0, 0, 4, 1))
            .Build());
        PdfDocument document = ReplaceImageSoftMask(source, bits, Compress(samples), 4);

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 0], Pixel(page, 0, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([255, 255, 255, 0], Pixel(page, 2, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 3, 0));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_AveragesThinSoftMaskLinesWhenReducing()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(1, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgba(1, 1, new byte[] { 255, 0, 0, 128 }), 0, 0, 1, 1))
            .Build());
        PdfDocument document = ReplaceImageSoftMask(source, 1, Compress([0x44]), 8);
        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(1, 1, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 64], Pixel(page, 0, 0));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_OpaqueSoftMaskDoesNotShiftColorSamples()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgba(3, 1, new byte[]
                {
                    255, 0, 0, 128, 0, 255, 0, 128, 0, 0, 255, 128
                }), 0, 0, 4, 1))
            .Build());
        PdfDocument document = ReplaceImageSoftMask(source, 1, Compress([0xFF]), 8);
        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 3, 0));
        Assert.Empty(page.Diagnostics);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void Render_AveragesEightBitMaskRunsWithoutOverflow(int samplesPerCell)
    {
        byte[] samples = Enumerable.Range(0, samplesPerCell * 2)
            .Select(index => (byte)(index % 3 == 0 ? 255 : index * 17 % 256)).ToArray();
        int sum = samples.Skip(samplesPerCell).Sum(value => (int)value);
        byte expectedAlpha = (byte)((sum + samplesPerCell / 2) / samplesPerCell);
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(1, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgba(1, 1, new byte[] { 255, 0, 0, 128 }), 0, 0, 1, 1))
            .Build());
        PdfDocument document = ReplaceImageSoftMask(source, 8, Compress(samples), samples.Length);
        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(1, 1, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, expectedAlpha], Pixel(page, 0, 0));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_DecodesCcittFaxImageXObjects()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(8, 1, new byte[8]), 0, 0, 8, 1))
            .Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter",
            Name("CCITTFaxDecode"), [0x89, 0xC0]);
        PdfDocument bitDepth = AddImageDictionaryEntry(filtered, "BitsPerComponent",
            new PdfInteger(1));
        PdfDocument document = AddImageDictionaryEntry(bitDepth, "DecodeParms",
            new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(
                Name("Columns"), new PdfInteger(8))]));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(page, 3, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 6, 0));
        Assert.DoesNotContain(page.Diagnostics, diagnostic => diagnostic.StartsWith(
            "The image compression filter is not implemented.", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_AppliesImageDecodeArrays()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 10, 20, 30 }), 2, 3, 6, 2))
            .Build());
        var decode = new PdfArray(Enumerable.Range(0, 3).SelectMany(_ =>
            new PdfObject[] { new PdfInteger(1), new PdfInteger(0) }));
        PdfDocument document = AddImageDictionaryEntry(source, "Decode", decode);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([225, 235, 245, 255], Pixel(rendered, 3, 6));
    }

    [Fact]
    public void Render_AppliesImageColorKeyMasks()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(2, 1, new byte[] { 255, 0, 0, 0, 255, 0 }), 2, 3, 6, 2))
            .Build());
        var mask = new PdfArray(new PdfObject[]
        {
            new PdfInteger(250), new PdfInteger(255),
            new PdfInteger(0), new PdfInteger(5),
            new PdfInteger(0), new PdfInteger(5)
        });
        PdfDocument document = AddImageDictionaryEntry(source, "Mask", mask);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 0], Pixel(rendered, 3, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 7, 6));
    }

    [Fact]
    public void Render_AppliesExplicitImageMaskStreams()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(2, 1, new byte[]
                {
                    255, 0, 0,
                    0, 255, 0
                }), 0, 0, 2, 1))
            .Build());
        PdfDocument document = AddExplicitImageMask(source, [0b0100_0000]);

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 0], Pixel(page, 1, 0));
        Assert.DoesNotContain("Masked-image rendering is not implemented.", page.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_ExplicitImageMaskPreservesRowPaddingAndDecode(bool inverted)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(9, 2, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 0, 0, 9, 2)).Build());
        PdfDocument document = AddExplicitImageMask(source,
            [0b0101_0101, 0b0111_1111, 0b1010_1010, 0b1000_0000], 9, 2, inverted);
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(9, 2, transparentBackground: true) { CacheResult = false };
        PdfRenderedPage page = renderer.Render(0, options);
        Assert.Empty(page.Diagnostics);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 9; x++)
            {
                bool paints = ((x + y) % 2 == 0) != inverted;
                Assert.Equal(paints ? new byte[] { 0, 0, 255, 255 }
                    : [255, 255, 255, 0], Pixel(page, x, y));
            }
        Assert.Equal(page.Pixels.ToArray(), renderer.Render(0, options).Pixels.ToArray());
    }

    [Fact]
    public void Render_LargeExplicitImageMaskDoesNotExpandEverySourcePixel()
    {
        const int maskWidth = 2049, maskHeight = 1024;
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(1, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 0, 0, 1, 1)).Build());
        PdfDocument document = AddExplicitImageMask(source,
            new byte[((maskWidth + 7) / 8) * maskHeight], maskWidth, maskHeight);
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(16, 16);
        byte[] pixels = new byte[16 * 16 * 4];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var diagnostics = renderer.RenderInto(0, options, pixels);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Explicit image mask allocated {allocated} bytes.");
        Assert.Empty(diagnostics);
        for (int offset = 0; offset < pixels.Length; offset += 4)
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan(offset, 4).ToArray());
    }

    [Fact]
    public void Render_ResolvesIndexedImageColorSpaces()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 1 }), 2, 3, 6, 2))
            .Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 3, 6));
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 7, 6));
    }

    [Fact]
    public void Render_IgnoresOneTrailingByteInFilteredIndexedColorLookup()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 1 }), 2, 3, 6, 2))
            .Build());
        var lookup = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Filter"), Name("FlateDecode"))]),
            Compress([255, 0, 0, 0, 0, 255, 10]));
        var lookupUpdate = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference lookupReference = lookupUpdate.AddObject(lookup);
        source = PdfDocument.Open(lookupUpdate.Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1), lookupReference
        });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 3, 6));
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 7, 6));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ResolvesNamedImageColorSpaceResources()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 2, 3, 6, 2))
            .Build());
        PdfDocument named = AddImageDictionaryEntry(source, "ColorSpace", Name("ImageRgb"));
        PdfDocument document = AddPageColorSpaceResource(
            named, "ImageRgb", Name("DeviceRGB"));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_UsesIccImageAlternateColorSpaces()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 0, 255, 0 }), 2, 3, 6, 2))
            .Build());
        var profile = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(3)),
            new KeyValuePair<PdfName, PdfObject>(Name("Alternate"), Name("ImageRgb"))]), []);
        PdfDocument profiled = AddIccImageColorSpace(source, profile);
        PdfDocument document = AddPageColorSpaceResource(
            profiled, "ImageRgb", Name("DeviceRGB"));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsCalGrayImagesToSrgb()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(1, 1, new byte[] { 128 }), 2, 3, 6, 2))
            .Build());
        var parameters = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("WhitePoint"),
                Reals(0.95047, 1, 1.08883))]);
        var colorSpace = new PdfArray(new PdfObject[] { Name("CalGray"), parameters });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([188, 188, 188, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsCalRgbImagesToSrgb()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 128, 0, 0 }), 2, 3, 6, 2))
            .Build());
        var parameters = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("WhitePoint"),
                Reals(0.95047, 1, 1.08883)),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), Reals(
                0.4124564, 0.2126729, 0.0193339,
                0.3575761, 0.7151522, 0.119192,
                0.1804375, 0.072175, 0.9503041))]);
        var colorSpace = new PdfArray(new PdfObject[] { Name("CalRGB"), parameters });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 188, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsLabImagesUsingDefaultDecodeRanges()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 128, 128, 128 }), 2, 3, 6, 2))
            .Build());
        var parameters = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("WhitePoint"),
                Reals(0.95047, 1, 1.08883))]);
        var colorSpace = new PdfArray(new PdfObject[] { Name("Lab"), parameters });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] pixel = Pixel(rendered, 3, 6);
        Assert.All(pixel[..3], channel => Assert.InRange(channel, (byte)115, (byte)125));
        Assert.Equal(255, pixel[3]);
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesSeparationTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(1, 1, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Separation"), Name("SpotRed"), Name("DeviceRGB"), function
        });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 3, 6));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 7, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsAxialShadings()
    {
        var shading = new PdfAxialGradient(0, 0, 10, 0,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(1, 1, 1))
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([13, 13, 13, 255], Pixel(rendered, 0, 5));
        Assert.Equal([242, 242, 242, 255], Pixel(rendered, 9, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Theory]
    [InlineData(16, 0, false)]
    [InlineData(0, 16, false)]
    [InlineData(16, 16, false)]
    [InlineData(16, 0, true)]
    [InlineData(0, 16, true)]
    [InlineData(16, 16, true)]
    public void Render_AxialSamplesPreserveEveryPixelAcrossClippingAndRotation(
        double endX, double endY, bool rotate)
    {
        var shading = new PdfAxialGradient(0, 0, endX, endY,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(1, 1, 1))
        ]);
        var content = new PdfContentStreamBuilder().Rectangle(2, 3, 12, 10).Clip();
        if (rotate) content.Transform(0, 1, -1, 0, 16, 0);
        content.PaintShading(shading);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(16, 16, content).Build());
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(16, 16, includeAnnotations: false, includeFormFields: false));
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
        {
            double pageX = x + .5, pageY = 15.5 - y;
            double sourceX = rotate ? pageY : pageX;
            double sourceY = rotate ? 16 - pageX : pageY;
            double unit = (sourceX * endX + sourceY * endY) / (endX * endX + endY * endY);
            byte expected = x >= 2 && x < 14 && pageY >= 3 && pageY < 13
                ? (byte)Math.Round(unit * 255) : (byte)255;
            Assert.Equal([expected, expected, expected, 255], Pixel(rendered, x, y));
        }
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsAxialShadingsWithIndexedColors()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function)]);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 0, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 9, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_PaintsAndClipsShadingPatterns(bool transformContent)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                transformContent
                    ? "2 0 0 2 0 0 cm /Pattern cs /P1 scn 0 0 2.5 5 re f /Pattern CS /P1 SCN 0.5 w 3.5 0.5 1 4 re S"
                    : "/Pattern cs /P1 scn 0 0 5 10 re f /Pattern CS /P1 SCN 1 w 7 1 2 8 re S"))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))]);
        PdfDocument document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 2, 0));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 13, 255], Pixel(rendered, 2, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 5, 9));
        Assert.Equal([0, 0, 140, 255], Pixel(rendered, 7, 1));
        Assert.DoesNotContain("Pattern rendering is not implemented.", rendered.Diagnostics);
    }

    [Theory]
    [InlineData("CA", 191, 0)]
    [InlineData("ca", 0, 191)]
    public void Render_ShadingPatternUsesThePaintedObjectsOpacity(string key, byte strokeGreen, byte fillGreen)
    {
        PdfDocument source = AddStrokeGraphicsState(
            "/Test gs /Pattern cs /P1 scn 0 0 40 100 re f /Pattern CS /P1 SCN 10 w 70 0 m 70 100 l S",
            key, new PdfReal(0.25));
        var function = new PdfDictionary([
            new(Name("FunctionType"), new PdfInteger(2)),
            new(Name("Domain"), Reals(0, 1)),
            new(Name("C0"), Reals(1, 0, 0)),
            new(Name("C1"), Reals(1, 0, 0)),
            new(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new(Name("ShadingType"), new PdfInteger(2)),
            new(Name("ColorSpace"), Name("DeviceRGB")),
            new(Name("Coords"), Reals(0, 0, 100, 0)),
            new(Name("Function"), function)]);
        PdfDocument document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 0, 0));
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { strokeGreen, strokeGreen, 255, 255 }, Pixel(rendered, 70, 50));
        Assert.Equal(new byte[] { fillGreen, fillGreen, 255, 255 }, Pixel(rendered, 20, 50));
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public void Render_ShadingBackgroundIsPatternOnlyAndDoesNotAccumulateOpacity(bool pattern, bool innerOpacity, bool bounds, bool stroke)
    {
        string paint = pattern ? "/Pattern cs /P1 scn 10 10 80 80 re f"
            : "10 10 80 80 re W n /Sh1 sh";
        if (stroke) paint = "/Pattern CS /P1 SCN 80 w 50 10 m 50 90 l S";
        var source = AddStrokeGraphicsState("/Test gs " + paint, stroke ? "CA" : "ca", new PdfReal(0.5));
        var function = new PdfDictionary([
            new(Name("FunctionType"), new PdfInteger(2)), new(Name("Domain"), Reals(0, 1)),
            new(Name("C0"), Reals(1, 0, 0)), new(Name("C1"), Reals(1, 0, 0)),
            new(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new(Name("ShadingType"), new PdfInteger(2)), new(Name("ColorSpace"), Name("DeviceRGB")),
            new(Name("Coords"), Reals(30, 0, 70, 0)), new(Name("Function"), function),
            new(Name("Background"), Reals(0, 0, 1))]);
        if (bounds) shading = new PdfDictionary(shading.Append(new KeyValuePair<PdfName, PdfObject>(
            Name("BBox"), Reals(40, 20, 60, 80))));
        var parameters = innerOpacity ? new PdfDictionary([new(Name("ca"), new PdfReal(0.5))]) : null;
        var document = pattern ? AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 0, 0), parameters)
            : AddShadingResource(source, shading);
        var rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        int pale = innerOpacity ? 191 : 128;
        byte[] center = Pixel(rendered, 50, 50), outside = Pixel(rendered, bounds ? 35 : 20, 50);
        Assert.Equal(255, center[2]);
        Assert.InRange(center[0], pale - 1, pale + 1);
        Assert.InRange(center[1], pale - 1, pale + 1);
        Assert.Equal(255, outside[0]);
        Assert.InRange(outside[1], pattern ? pale - 1 : 255, pattern ? pale + 1 : 255);
        Assert.InRange(outside[2], pattern ? pale - 1 : 255, pattern ? pale + 1 : 255);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(rendered, 5, 50));
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(false, "ca", 191)]
    [InlineData(true, "ca", 191)]
    [InlineData(false, "CA", 128)]
    [InlineData(true, "CA", 128)]
    public void Render_ShadingPatternStateCombinesWithObjectOpacity(bool stroke, string key, byte green)
    {
        string paint = stroke ? "/Pattern CS /P1 SCN 10 w 50 0 m 50 100 l S"
            : "/Pattern cs /P1 scn 0 0 100 100 re f";
        PdfDocument source = AddStrokeGraphicsState("/Test gs " + paint,
            stroke ? "CA" : "ca", new PdfReal(0.5));
        var function = new PdfDictionary([
            new(Name("FunctionType"), new PdfInteger(2)), new(Name("Domain"), Reals(0, 1)),
            new(Name("C0"), Reals(1, 0, 0)), new(Name("C1"), Reals(1, 0, 0)),
            new(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new(Name("ShadingType"), new PdfInteger(2)), new(Name("ColorSpace"), Name("DeviceRGB")),
            new(Name("Coords"), Reals(0, 0, 100, 0)), new(Name("Function"), function)]);
        var parameters = new PdfDictionary([new(Name(key), new PdfReal(0.5))]);
        var document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 0, 0), parameters);
        var rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        byte[] pixel = Pixel(rendered, 50, 50);
        Assert.InRange(pixel[0], green, green + 1);
        Assert.InRange(pixel[1], green, green + 1);
        Assert.Equal(255, pixel[2]);
        Assert.Equal(255, pixel[3]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ShadingPatternBlendIsInternalAndDoesNotLeak()
    {
        PdfDocument source = AddStrokeGraphicsState(
            "0 0 1 rg 0 0 100 100 re f /Test gs /Pattern cs /P1 scn 0 0 40 100 re f "
            + "1 0 0 rg 60 0 40 100 re f", "BM", Name("Multiply"));
        var function = new PdfDictionary([
            new(Name("FunctionType"), new PdfInteger(2)), new(Name("Domain"), Reals(0, 1)),
            new(Name("C0"), Reals(1, 0, 0)), new(Name("C1"), Reals(1, 0, 0)),
            new(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new(Name("ShadingType"), new PdfInteger(2)), new(Name("ColorSpace"), Name("DeviceRGB")),
            new(Name("Coords"), Reals(0, 0, 100, 0)), new(Name("Function"), function)]);
        var parameters = new PdfDictionary([new(Name("BM"), Name("Screen"))]);
        var document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 0, 0), parameters);
        var rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(rendered, 20, 50));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(rendered, 80, 50));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ShadingPatternSoftMaskUsesPatternCoordinatesAndDoesNotLeak()
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            "/Pattern cs /P1 scn 0 0 100 100 re f 0 1 0 rg 80 0 20 100 re f"u8.ToArray()).Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        var mask = update.AddObject(new PdfStream(new PdfDictionary([
            new(Name("Type"), Name("XObject")), new(Name("Subtype"), Name("Form")),
            new(Name("BBox"), Reals(0, 0, 100, 100)), new(Name("Resources"), new PdfDictionary([])),
            new(Name("Group"), new PdfDictionary([
                new(Name("S"), Name("Transparency")), new(Name("CS"), Name("DeviceGray"))]))
        ]), "1 g 0 0 50 100 re f"u8.ToArray()));
        source = PdfDocument.Open(update.Build());
        var function = new PdfDictionary([
            new(Name("FunctionType"), new PdfInteger(2)), new(Name("Domain"), Reals(0, 1)),
            new(Name("C0"), Reals(1, 0, 0)), new(Name("C1"), Reals(1, 0, 0)),
            new(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new(Name("ShadingType"), new PdfInteger(2)), new(Name("ColorSpace"), Name("DeviceRGB")),
            new(Name("Coords"), Reals(0, 0, 100, 0)), new(Name("Function"), function)]);
        var parameters = new PdfDictionary([new(Name("SMask"), new PdfDictionary([
            new(Name("S"), Name("Luminosity")), new(Name("G"), mask)]))]);
        var document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 10, 0), parameters);
        var rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(rendered, 55, 50));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(rendered, 65, 50));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(rendered, 85, 50));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ResolvesNamedPatternColorSpaces()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "/CS1 cs /P1 scn 0 0 5 10 re f /CS2 CS /P1 SCN 1 w 7 1 2 8 re S"))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))]);
        PdfDocument document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 0, 0));
        document = AddPageColorSpaceResources(document,
            new KeyValuePair<PdfName, PdfObject>(Name("CS1"),
                new PdfArray(new PdfObject[] { Name("Pattern") })),
            new KeyValuePair<PdfName, PdfObject>(Name("CS2"),
                new PdfArray(new PdfObject[] { Name("Pattern"), Name("DeviceRGB") })));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 13, 255], Pixel(rendered, 0, 5));
        Assert.Equal([0, 0, 191, 255], Pixel(rendered, 7, 1));
        Assert.DoesNotContain("Pattern rendering is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsMultiStopAxialShadings()
    {
        var shading = new PdfAxialGradient(0, 0, 10, 0,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(0.5, new PdfRgbColor(1, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(1, 1, 1))
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 230, 255], Pixel(rendered, 4, 5));
        Assert.Equal([26, 26, 255, 255], Pixel(rendered, 5, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Theory]
    [InlineData(0.5, 0.5, true)]
    [InlineData(0, 1, true)]
    [InlineData(-0.1, 0.5, false)]
    [InlineData(0.5, 1.1, false)]
    public void Render_ValidatesStitchingFunctionBoundaries(
        double firstBound, double secondBound, bool valid)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        PdfDictionary Segment(double red) => new([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(red, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(red, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(3)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Functions"),
                new PdfArray(new PdfObject[] { Segment(0), Segment(0.5), Segment(1) })),
            new KeyValuePair<PdfName, PdfObject>(Name("Bounds"), Reals(firstBound, secondBound)),
            new KeyValuePair<PdfName, PdfObject>(Name("Encode"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]);
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0.5, 0, 9.5, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))]), []);
        PdfDocument document = AddShadingResource(source, shading);

        if (!valid)
        {
            Assert.Throws<FormatException>(() => new PdfPageRenderer(document).Render(
                0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false)));
            return;
        }

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal(firstBound == 0 ? new byte[] { 0, 0, 128, 255 }
            : new byte[] { 0, 0, 0, 255 }, Pixel(rendered, 4, 5));
        Assert.Equal(firstBound == 0 ? new byte[] { 0, 0, 128, 255 }
            : new byte[] { 0, 0, 255, 255 }, Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 9, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    public void Render_AppliesDeviceNTintsToExponentialShadings(int channels)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var tint = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"),
                Reals(Enumerable.Repeat(new double[] { 0, 1 }, channels).SelectMany(pair => pair).ToArray())),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1))]),
            Encoding.ASCII.GetBytes("{ " + channels + " 1 roll "
                + string.Concat(Enumerable.Repeat("pop ", channels - 1)) + "1 exch sub }"));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference tintReference = update.AddObject(tint);
        source = PdfDocument.Open(update.Build());
        var colorSpace = new PdfArray(new PdfObject[] {
            Name("DeviceN"), new PdfArray(Enumerable.Range(0, channels)
                .Select(index => (PdfObject)Name("Ink" + index)).ToArray()),
            Name("DeviceGray"), tintReference });
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(new double[channels])),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(Enumerable.Repeat(1d, channels).ToArray())),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0.5, 0, 9.5, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function)]), []);
        PdfRenderedPage rendered = new PdfPageRenderer(AddShadingResource(source, shading)).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 0, 5));
        Assert.Equal([170, 170, 170, 255], Pixel(rendered, 3, 5));
        Assert.Equal([0, 0, 0, 255], Pixel(rendered, 9, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ClipsAxialShadingsToTheirBounds()
    {
        var shading = new PdfAxialGradient(0, 0, 10, 0,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(0, 0, 0))
        ], bounds: new PdfShadingBounds(2, 2, 8, 8));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 5));
        Assert.Equal([0, 0, 0, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 8, 5));
    }

    [Fact]
    public void Render_PaintsRadialShadings()
    {
        var shading = new PdfRadialGradient(5, 5, 0, 5, 5, 5,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(1, 1, 1))
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([36, 36, 36, 255], Pixel(rendered, 5, 5));
        Assert.Equal([180, 180, 180, 255], Pixel(rendered, 8, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsRadialShadingsWithIndexedColors()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(3)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(5, 5, 0, 5, 5, 5)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function)]);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 9, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsFunctionShadingsWithTheirMatrixAndDomain()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            Encoding.ASCII.GetBytes("{ pop dup dup }") );
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(1)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), Reals(5, 0, 0, 5, 2, 3)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function)]);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 5));
        Assert.Equal([25, 25, 25, 255], Pixel(rendered, 2, 6));
        Assert.Equal([230, 230, 230, 255], Pixel(rendered, 6, 6));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsFunctionShadingsWithIndexedColors()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1))]),
            Encoding.ASCII.GetBytes("{ pop }"));
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(1)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), Reals(10, 0, 0, 10, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function)]);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 0, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 9, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsFunctionShadingsWithComponentFunctionArrays()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        PdfStream Component(string program) => new(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1))]),
            Encoding.ASCII.GetBytes(program));
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(1)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), Reals(10, 0, 0, 10, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), new PdfArray(new PdfObject[]
            {
                Component("{ pop }"), Component("{ exch pop }"), Component("{ pop 0.5 }")
            }))]);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 115, 115, 255], Pixel(rendered, 4, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsAndClipsTensorPatchShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "0 0 3 10 re 7 0 3 10 re W n /Sh1 sh")).Build());
        byte[] points =
        [
            0, 0, 85, 0, 170, 0, 255, 0,
            255, 85, 255, 170, 255, 255, 170, 255,
            85, 255, 0, 255, 0, 170, 0, 85,
            85, 85, 170, 85, 170, 170, 85, 170
        ];
        byte[] colors = Enumerable.Repeat(new byte[] { 255, 0, 0 }, 4)
            .SelectMany(value => value).ToArray();
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(7)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[] { 0 }.Concat(points).Concat(colors).ToArray());
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 2, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 8, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsCoonsPatchShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        byte[] boundary =
        [
            0, 0, 85, 0, 170, 0, 255, 0,
            255, 85, 255, 170, 255, 255, 170, 255,
            85, 255, 0, 255, 0, 170, 0, 85
        ];
        byte[] colors = Enumerable.Repeat(new byte[] { 0, 255, 0 }, 4)
            .SelectMany(value => value).ToArray();
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(6)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[] { 0 }.Concat(boundary).Concat(colors).ToArray());
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsPatchMeshesWithIndexedColors()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        byte[] boundary =
        [
            0, 0, 85, 0, 170, 0, 255, 0,
            255, 85, 255, 170, 255, 255, 170, 255,
            85, 255, 0, 255, 0, 170, 0, 85
        ];
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(6)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1))]),
            new byte[] { 0 }.Concat(boundary).Concat(new byte[] { 255, 255, 255, 255 }).ToArray());
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ContinuesTensorPatchMeshesAcrossSharedEdges()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        byte[] firstPoints =
        [
            0, 0, 43, 0, 85, 0, 128, 0,
            128, 43, 128, 85, 128, 128, 85, 128,
            43, 128, 0, 128, 0, 85, 0, 43,
            43, 43, 85, 43, 85, 85, 43, 85
        ];
        byte[] secondPoints =
        [
            170, 128, 213, 128, 255, 128, 255, 85,
            255, 43, 255, 0, 213, 0, 170, 0,
            170, 43, 213, 43, 213, 85, 170, 85
        ];
        byte[] firstColors = Enumerable.Repeat(new byte[] { 255, 0, 0 }, 4)
            .SelectMany(value => value).ToArray();
        byte[] secondColors = [0, 0, 255, 0, 0, 255];
        byte[] data = new byte[] { 0 }.Concat(firstPoints).Concat(firstColors)
            .Concat(new byte[] { 1 }).Concat(secondPoints).Concat(secondColors).ToArray();
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(7)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]), data);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] continuedPatch = Pixel(rendered, 9, 7);
        Assert.True(continuedPatch[0] > 200 && continuedPatch[2] < 50);
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void Render_OverlappingMeshTrianglesApplyOpacityOnce(int type, bool screen)
    {
        var placeholder = new PdfRadialGradient(5, 5, 0, 5, 5, 5,
            [new PdfGradientStop(0, new PdfRgbColor(1, 0, 0)),
             new PdfGradientStop(1, new PdfRgbColor(1, 0, 0))]);
        var content = new PdfContentStreamBuilder();
        if (screen) content.SetFillRgb(0, 0, 0).Rectangle(0, 0, 10, 10).Fill();
        content.SetBlendMode(screen ? PdfBlendMode.Screen : PdfBlendMode.Normal)
            .SetOpacity(0.5).PaintShading(placeholder);
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10, content).Build());
        byte[] triangle = [0, 0, 0, 255, 0, 0, 0, 255, 0, 255, 0, 0, 0, 0, 255, 255, 0, 0];
        if (type == 5)
            triangle = [0, 0, 255, 0, 0, 255, 0, 255, 0, 0,
                0, 255, 255, 0, 0, 255, 255, 255, 0, 0];
        if (type is 6 or 7)
        {
            byte[] points = [0, 0, 85, 0, 170, 0, 255, 0,
                255, 85, 255, 170, 255, 255, 170, 255,
                85, 255, 0, 255, 0, 170, 0, 85,
                85, 85, 170, 85, 170, 170, 85, 170];
            triangle = [0, .. points.Take(type == 6 ? 24 : 32),
                255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0];
        }
        var shading = new PdfStream(new PdfDictionary([
            new(Name("ShadingType"), new PdfInteger(type)),
            new(Name("VerticesPerRow"), new PdfInteger(2)),
            new(Name("ColorSpace"), Name("DeviceRGB")),
            new(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new(Name("BitsPerComponent"), new PdfInteger(8)),
            new(Name("BitsPerFlag"), new PdfInteger(8)),
            new(Name("Decode"), Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            [.. triangle, .. triangle]);
        PdfRenderedPage rendered = new PdfPageRenderer(AddShadingResource(source, shading)).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));
        Assert.Equal(screen ? [0, 0, 128, 255] : [128, 128, 255, 255], Pixel(rendered, 2, 7));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsFreeFormGouraudMeshShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[]
            {
                0, 0, 0, 255, 0, 0,
                0, 255, 0, 0, 255, 0,
                0, 0, 255, 0, 0, 255
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([64, 64, 128, 255], Pixel(rendered, 2, 7));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 8, 1));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsFreeFormMeshesWithIndexedColors()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1))]),
            new byte[]
            {
                0, 0, 0, 0,
                0, 255, 0, 255,
                0, 0, 255, 255
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 2, 7));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 8, 1));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ContinuesFreeFormGouraudMeshesAcrossEitherAvailableEdge()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[]
            {
                0, 0, 255, 255, 0, 0,
                0, 0, 0, 0, 255, 0,
                0, 128, 0, 0, 0, 255,
                1, 128, 255, 255, 255, 255,
                2, 255, 255, 255, 0, 0
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.NotEqual([255, 255, 255, 255], Pixel(rendered, 4, 5));
        Assert.NotEqual([255, 255, 255, 255], Pixel(rendered, 8, 1));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_EvaluatesGouraudMeshFunctionsAfterInterpolation()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 1, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(2))]);
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"), Reals(0, 10, 0, 10, 0, 1))]),
            new byte[]
            {
                0, 0, 0, 0,
                0, 255, 0, 255,
                0, 0, 255, 0
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] pixel = Pixel(rendered, 2, 7);
        Assert.InRange(pixel[0], 10, 30);
        Assert.Equal(pixel[0], pixel[1]);
        Assert.Equal(pixel[0], pixel[2]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsLatticeGouraudMeshShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(5)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("VerticesPerRow"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[]
            {
                0, 0, 255, 0, 0,
                255, 0, 0, 255, 0,
                0, 255, 0, 0, 255,
                255, 255, 255, 255, 255
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] lowerLeft = Pixel(rendered, 0, 9);
        byte[] upperRight = Pixel(rendered, 9, 0);
        Assert.True(lowerLeft[2] > lowerLeft[1] && lowerLeft[2] > lowerLeft[0]);
        Assert.True(upperRight[0] > 200 && upperRight[1] > 200 && upperRight[2] > 200);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsLatticeMeshesWithIndexedColors()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(5)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("VerticesPerRow"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1))]),
            new byte[]
            {
                0, 0, 0,
                255, 0, 255,
                0, 255, 0,
                255, 255, 255
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 0, 9));
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 9, 0));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_RejectsUnboundedLatticeMeshRowsBeforeAllocatingThem()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(5)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("VerticesPerRow"),
                new PdfInteger(int.MaxValue)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]), []);
        PdfDocument document = AddShadingResource(source, shading);

        Assert.Throws<FormatException>(() => new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderInto_CalculatorShadingAvoidsPerPixelArrays(bool componentFunctions)
    {
        const int size = 256;
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(size, size, "/Sh1 sh"u8.ToArray()).Build());
        PdfStream Function(string program, bool singleOutput) => new(new PdfDictionary([
            new(Name("FunctionType"), new PdfInteger(4)),
            new(Name("Domain"), Reals(0, 1)),
            new(Name("Range"), singleOutput ? Reals(0, 1) : Reals(0, 1, 0, 1, 0, 1))]),
            Encoding.ASCII.GetBytes(program));
        PdfObject function = componentFunctions
            ? new PdfArray([Function("{ }", true), Function("{ 1 exch sub }", true),
                Function("{ pop 0 }", true)])
            : Function("{ dup 1 exch sub 0 }", false);
        var shading = new PdfDictionary([
            new(Name("ShadingType"), new PdfInteger(3)),
            new(Name("ColorSpace"), Name("DeviceRGB")),
            new(Name("Coords"), Reals(128, 128, 0, 128, 128, size)),
            new(Name("Function"), function)]);
        var renderer = new PdfPageRenderer(AddShadingResource(source, shading));
        var options = new PdfRenderOptions(size, size,
            includeAnnotations: false, includeFormFields: false);
        byte[] output = new byte[size * size * 4];
        Assert.Empty(renderer.RenderInto(0, options, output));
        byte[] expected = output.ToArray();

        long before = GC.GetAllocatedBytesForCurrentThread();
        renderer.RenderInto(0, options, output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, output);
        Assert.Equal(new byte[] { 0, 254, 1, 255 }, output.AsSpan((128 * size + 128) * 4, 4).ToArray());
        Assert.True(allocated < size * size * 4,
            $"Calculator shading allocated {allocated} bytes for {size * size} pixels.");
    }

    [Fact]
    public void Render_PaintsThirtyTwoBitSampledShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(new PdfObject[] { new PdfInteger(2) })),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(32))]),
            new byte[12].Concat(Enumerable.Repeat(byte.MaxValue, 12)).ToArray());
        PdfDocument document = AddSampledAxialShadingResource(source, function);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([13, 13, 13, 255], Pixel(rendered, 0, 5));
        Assert.Equal([242, 242, 242, 255], Pixel(rendered, 9, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AcceptsOrderThreeSampledShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(new PdfObject[] { new PdfInteger(2) })),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Order"), new PdfInteger(3))]),
            new byte[] { 0, 0, 0, 255, 255, 255 });
        PdfDocument document = AddSampledAxialShadingResource(source, function);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([13, 13, 13, 255], Pixel(rendered, 0, 5));
        Assert.Equal([242, 242, 242, 255], Pixel(rendered, 9, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesSampledSeparationTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(new PdfObject[] { new PdfInteger(2) })),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(8))]),
            new byte[] { 255, 255, 255, 255, 0, 0 });
        PdfDocument document = AddSampledSeparationColorSpace(source, function);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 3, 6));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 7, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesOrderThreeMultidimensionalDeviceNTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(new PdfObject[] { new PdfInteger(2), new PdfInteger(2) })),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Order"), new PdfInteger(3))]),
            new byte[]
            {
                255, 255, 255, 255, 0, 0,
                0, 255, 0, 0, 0, 0
            });
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255, 0, 255, 0, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesNineChannelDeviceNTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"),
                Reals(Enumerable.Repeat(new[] { 0d, 1d }, 9)
                    .SelectMany(pair => pair).ToArray())),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(Enumerable.Repeat<PdfObject>(new PdfInteger(1), 9))),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(8))]),
            new byte[] { 255, 0, 0 });
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[18], componentCount: 9);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesCalculatorDeviceNTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ 1 index 1 cvr exch sub 3 1 roll 1 exch sub 3 1 roll }"u8);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 255, 255, 0, 255, 0, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesCalculatorCopyOperator()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ 2 copy }"u8);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255, 255, 0, 255, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesCalculatorLogicalAndBitwiseOperators()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ pop pop true false or false xor false ne { 1 } { 0 } ifelse "u8.ToArray()
                .Concat("true dup and false exch pop not 5 3 and 1 eq and "u8.ToArray())
                .Concat("{ 1 } { 0 } ifelse 1 3 bitshift 8 eq not not "u8.ToArray())
                .Concat("{ 1 } { 0 } ifelse }"u8.ToArray())
                .ToArray());
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesNestedCalculatorConditionalsAndAtan()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ atan 360 div dup 0 lt { pop 0 } { dup 1 gt { pop 1 } if } ifelse dup dup }"u8);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([64, 64, 64, 255, 0, 0, 0, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesCalculatorSeparationTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ dup 1 exch sub 0 }"u8);
        PdfDocument document = AddSampledSeparationColorSpace(source, function);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255, 0, 0, 255, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesSingleChannelExponentialDeviceNTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(1, 1, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 0, 255 }, componentCount: 1);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255, 0, 0, 255, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ExecutesType3GlyphProgramsAndAdvancesText()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("AA")
            .EndText();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());
        PdfDocument document = AddType3TriangleFont(source);

        var renderer = new PdfPageRenderer(document);
        PdfRenderOptions options = new(20, 10,
            includeAnnotations: false, includeFormFields: false);
        PdfRenderedPage rendered = renderer.Render(0, options);
        PdfRenderedPage repeated = renderer.Render(0, options);
        object cache = typeof(PdfPageRenderer).GetField("_streamInstructionCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        int count = (int)cache.GetType().GetProperty("Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 15, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.Equal(rendered.Pixels.ToArray(), repeated.Pixels.ToArray());
        Assert.Equal(1, count);
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesType3GlyphProgramsToTheClippingPath()
    {
        var content = new PdfContentStreamBuilder()
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextRenderingMode(PdfTextRenderingMode.Clip)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("A")
            .EndText()
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 10, 10)
            .Fill();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());
        PdfDocument document = AddType3TriangleFont(source);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
    }

    [Theory]
    [InlineData(PdfTextRenderingMode.Clip)]
    [InlineData(PdfTextRenderingMode.Fill)]
    public void Render_RecoveryPreservesClippingAndLaterContentWithCyclicGlyphs(PdfTextRenderingMode mode)
    {
        byte[] fontData = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, includeCompound: true);
        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(fontData.AsSpan(4));
        for (int index = 0; index < tableCount; index++)
        {
            int entry = 12 + index * 16;
            if (!fontData.AsSpan(entry, 4).SequenceEqual("glyf"u8)) continue;
            int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fontData.AsSpan(entry + 8)));
            BinaryPrimitives.WriteUInt16BigEndian(fontData.AsSpan(offset + 12), 1);
        }
        var content = new PdfContentStreamBuilder().SaveState().BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextRenderingMode(mode)
            .ShowLatin1Text("AA").EndText()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 20, 10).Fill().RestoreState()
            .SetFillRgb(0, 0, 1).Rectangle(15, 0, 5, 10).Fill();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 10, content).Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(Assert.Single(fonts).Value);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference file = update.AddObject(new PdfStream(new PdfDictionary([]), fontData));
        var descriptor = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FontFile2"), file)]);
        var font = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("TrueType")),
            new KeyValuePair<PdfName, PdfObject>(Name("BaseFont"), Name("Test")),
            new KeyValuePair<PdfName, PdfObject>(Name("Encoding"), Name("WinAnsiEncoding")),
            new KeyValuePair<PdfName, PdfObject>(Name("FontDescriptor"), descriptor)]);
        byte[] pdf = update.ReplaceObject(reference.ObjectNumber, font).Build();
        var options = new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false);
        Assert.ThrowsAny<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(pdf)).Render(0, options));
        var renderer = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(pdf));
        PdfRenderedPage result = renderer.Render(0, options);
        Assert.Equal(mode == PdfTextRenderingMode.Clip
            ? new byte[] { 255, 255, 255, 255 } : [0, 0, 255, 255], Pixel(result, 5, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(result, 17, 5));
        Assert.Contains("A cyclic or excessively nested text glyph was omitted.", result.Diagnostics);
        Assert.Equal(result.Pixels.ToArray(), renderer.Render(0, options).Pixels.ToArray());
    }

    [Fact]
    public void Render_FillsEmbeddedTrueTypeGlyphContours()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(font, 10)
            .SetCharacterSpacing(4)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowUnicodeText("AA")
            .EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 15, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_RestoresTextStateWithGraphicsState()
    {
        TrueTypeFont embedded = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("A")
            .EndText()
            .SaveState()
            .BeginText()
            .SetFont(embedded, 5)
            .ShowUnicodeText("A")
            .EndText()
            .RestoreState()
            .BeginText()
            .SetTextMatrix(1, 0, 0, 1, 10, 0)
            .ShowLatin1Text("AA")
            .EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(30, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(30, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain(rendered.Diagnostics,
            diagnostic => diagnostic.StartsWith("Text outlines for font ",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Render_ReusesParsedFontsAcrossRepeatedRenders()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .BeginText()
            .SetFont(font, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowUnicodeText("A")
            .EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());
        var renderer = new PdfPageRenderer(document);
        PdfRenderOptions options = new(10, 10,
            includeAnnotations: false, includeFormFields: false);

        PdfRenderedPage first = renderer.Render(0, options);
        PdfRenderedPage second = renderer.Render(0, options);
        object cache = typeof(PdfPageRenderer).GetField("_fontCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        int count = (int)cache.GetType().GetProperty("Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cache)!;
        object glyphCache = typeof(PdfPageRenderer).GetField("_glyphPathCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(renderer)!;
        int glyphCount = (int)glyphCache.GetType().GetProperty("Count",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(glyphCache)!;

        Assert.Equal(first.Pixels.ToArray(), second.Pixels.ToArray());
        Assert.Equal(1, count);
        Assert.Equal(1, glyphCount);
        Assert.DoesNotContain("A text glyph outline is not implemented.", second.Diagnostics);
    }

    [Theory]
    [InlineData(PdfStandardFont.Helvetica)]
    [InlineData(PdfStandardFont.HelveticaBold)]
    [InlineData(PdfStandardFont.HelveticaOblique)]
    [InlineData(PdfStandardFont.HelveticaBoldOblique)]
    [InlineData(PdfStandardFont.TimesRoman)]
    [InlineData(PdfStandardFont.TimesBold)]
    [InlineData(PdfStandardFont.TimesItalic)]
    [InlineData(PdfStandardFont.TimesBoldItalic)]
    [InlineData(PdfStandardFont.Courier)]
    [InlineData(PdfStandardFont.CourierBold)]
    [InlineData(PdfStandardFont.CourierOblique)]
    [InlineData(PdfStandardFont.CourierBoldOblique)]
    public void Render_UsesBundledOutlinesForOrdinaryStandardFonts(
        PdfStandardFont standardFont)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(standardFont, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("A A")
            .EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Contains(Enumerable.Range(0, rendered.Width * rendered.Height)
            .Select(index => rendered.Pixels.Span.Slice(index * 4, 4).ToArray()),
            pixel => pixel[0] < 128 && pixel[1] < 128 && pixel[2] > 200 && pixel[3] == 255);
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain(rendered.Diagnostics,
            diagnostic => diagnostic.StartsWith("Text outlines for font ",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Render_AcceptsNegativeFontSizes()
    {
        var content = new PdfContentStreamBuilder()
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(1, 0, 0, 1, 10, 10)
            .ShowLatin1Text("A")
            .EndText();
        byte[] source = new PdfDocumentBuilder().AddPage(20, 20, content).Build();
        string pdf = Encoding.Latin1.GetString(source)
            .Replace(" 10 Tf", " -1 Tf", StringComparison.Ordinal);
        PdfDocument document = PdfDocument.Open(Encoding.Latin1.GetBytes(pdf));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 20,
                includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_IgnoresInvisibleTextWithAnUnresolvedFont()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, Encoding.ASCII.GetBytes(
                "BT /Missing 12 Tf 3 Tr (hidden) Tj ET"))
            .Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(100, 100,
                includeAnnotations: false, includeFormFields: false));

        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesEmbeddedGlyphsToTheClippingPathAtEndText()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .BeginText()
            .SetFont(font, 10)
            .SetTextRenderingMode(PdfTextRenderingMode.Clip)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowUnicodeText("A")
            .EndText()
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 10, 10)
            .Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesVerticalOriginsAdvancesAndPositionAdjustments()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(font, 10)
            .SetTextMatrix(1, 0, 0, 1, 5, 20)
            .ShowPositionedUnicodeText(["A", "A"], [100])
            .EndText();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 20, content).Build());
        PdfDocument document = MakeEmbeddedFontVertical(source);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 20, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 15));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 10));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_FillsEmbeddedCffCubicGlyphContours()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("A")
            .EndText();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());
        PdfDocument document = AddCffCurveFont(source);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_FillsAndStrokesPathsAndSupportsCurveShorthands()
    {
        byte[] content = "1 0 0 rg 0 0 1 RG 1 w 2 2 4 4 re B 1 8 m 3 6 5 8 v 5 8 m 7 6 9 8 y S"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 4, 6));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 2, 7));
        Assert.NotEqual([255, 255, 255, 255], Pixel(page, 3, 3));
        Assert.NotEqual([255, 255, 255, 255], Pixel(page, 7, 3));
    }

    [Fact]
    public void Render_UsesCropOriginAndClockwisePageRotation()
    {
        byte[] content = "1 0 0 rg 10 5 5 5 re f"u8.ToArray();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 15, content).Build());
        PdfDocument document = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetCropBox(0, 10, 5, 10, 10)
            .SetRotation(0, 90)
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 2));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 7, 7));
    }

    [Fact]
    public void Render_IntersectsNestedGraphicsStateClippingPaths()
    {
        byte[] content = "2 2 6 6 re W n q 4 0 4 10 re W* n 1 0 0 rg 0 0 10 10 re f Q 0 0 1 rg 0 0 2 2 re f"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 8, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 8));
    }

    [Fact]
    public void Render_DecodesInlineRgbImages()
    {
        byte[] prefix = Encoding.ASCII.GetBytes(
            "q 4 0 0 2 2 3 cm BI /W 2 /H 1 /BPC 8 /CS /RGB ID ");
        byte[] suffix = Encoding.ASCII.GetBytes(" EI Q");
        byte[] content = [.. prefix, 255, 0, 0, 0, 255, 0, .. suffix];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 5, 6));
        Assert.DoesNotContain("Inline-image rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_DistinguishesNonzeroAndEvenOddCompoundFills()
    {
        byte[] content = "1 0 0 rg 1 1 8 8 re 3 3 4 4 re f 0 0 1 rg 11 1 8 8 re 13 3 4 4 re f*"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 15, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 12, 5));
    }

    [Fact]
    public void Render_CompositesGraphicsStateOpacityOverTheBackground()
    {
        var content = new PdfContentStreamBuilder()
            .SetOpacity(0.5)
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 10, 10)
            .Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage opaque = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage transparent = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 255, 255], Pixel(opaque, 5, 5));
        Assert.Equal([0, 0, 255, 128], Pixel(transparent, 5, 5));
    }

    [Fact]
    public void Render_CompositesOverlappingStrokeSegmentsOnce()
    {
        var content = new PdfContentStreamBuilder()
            .SetOpacity(0.5).SetLineWidth(2)
            .MoveTo(1, 1).LineTo(5, 5).LineTo(1, 5).LineTo(5, 1).Stroke();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(6, 6, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(6, 6,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 128, 255], Pixel(page, 3, 3));
    }

    [Theory]
    [InlineData(PdfBlendMode.Multiply, 0, 0, 0)]
    [InlineData(PdfBlendMode.Screen, 255, 0, 255)]
    [InlineData(PdfBlendMode.Darken, 0, 0, 0)]
    [InlineData(PdfBlendMode.Lighten, 255, 0, 255)]
    [InlineData(PdfBlendMode.Difference, 255, 0, 255)]
    [InlineData(PdfBlendMode.Exclusion, 255, 0, 255)]
    public void Render_CompositesSeparableBlendModes(
        PdfBlendMode blendMode, byte blue, byte green, byte red)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0, 0, 1).Rectangle(0, 0, 10, 10).Fill()
            .SetBlendMode(blendMode)
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 10, 10).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([blue, green, red, (byte)255], Pixel(rendered, 5, 5));
        Assert.DoesNotContain("Transparency blend-mode rendering is not implemented.",
            rendered.Diagnostics);
    }

    [Theory]
    [InlineData(PdfBlendMode.Overlay)]
    [InlineData(PdfBlendMode.ColorDodge)]
    [InlineData(PdfBlendMode.ColorBurn)]
    [InlineData(PdfBlendMode.HardLight)]
    [InlineData(PdfBlendMode.SoftLight)]
    public void Render_AcceptsRemainingSeparableBlendModes(PdfBlendMode blendMode)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0.2, 0.4, 0.6).Rectangle(0, 0, 10, 10).Fill()
            .SetBlendMode(blendMode)
            .SetFillRgb(0.8, 0.3, 0.1).Rectangle(0, 0, 10, 10).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain("Transparency blend-mode rendering is not implemented.",
            rendered.Diagnostics);
        Assert.Equal(255, Pixel(rendered, 5, 5)[3]);
    }

    [Theory]
    [InlineData(PdfBlendMode.ColorDodge, false, 1)]
    [InlineData(PdfBlendMode.ColorDodge, true, 1)]
    [InlineData(PdfBlendMode.ColorBurn, false, 1)]
    [InlineData(PdfBlendMode.ColorBurn, true, 1)]
    [InlineData(PdfBlendMode.ColorDodge, false, 0.5)]
    [InlineData(PdfBlendMode.ColorDodge, true, 0.5)]
    [InlineData(PdfBlendMode.ColorBurn, false, 0.5)]
    [InlineData(PdfBlendMode.ColorBurn, true, 0.5)]
    public void Render_DodgeAndBurnKeepBackdropEndpointsContinuous(PdfBlendMode mode, bool cmyk, double opacity)
    {
        byte[] Render(bool foreground)
        {
            var content = new PdfContentStreamBuilder();
            void Color(double value)
            {
                if (cmyk) content.SetFillCmyk(1 - value, 1 - value, 1 - value, 1 - value);
                else content.SetFillRgb(value, value, value);
            }
            Color(mode == PdfBlendMode.ColorBurn ? 1 : 0);
            content.Rectangle(0, 0, 3, 1).Fill();
            if (foreground)
            {
                content.SetGraphicsState(new PdfGraphicsState(fillOpacity: opacity, blendMode: mode));
                for (int x = 0; x < 3; x++)
                {
                    Color(x / 2d);
                    content.Rectangle(x, 0, 1, 1).Fill();
                }
            }
            if (cmyk)
                content = new PdfContentStreamBuilder().DrawForm(new PdfFormXObject(3, 1, content,
                    isolatedTransparencyGroup: true, transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Cmyk), 0, 0);
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1, content).Build());
            PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(3, 1));
            Assert.Empty(page.Diagnostics);
            return page.Pixels.ToArray();
        }
        Assert.Equal(Render(false), Render(true));
    }

    [Theory]
    [InlineData(PdfBlendMode.Hue, 102)]
    [InlineData(PdfBlendMode.Saturation, 102)]
    [InlineData(PdfBlendMode.Color, 102)]
    [InlineData(PdfBlendMode.Luminosity, 204)]
    public void Render_CompositesNonseparableBlendModes(
        PdfBlendMode blendMode, byte expected)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0.4, 0.4, 0.4).Rectangle(0, 0, 10, 10).Fill()
            .SetBlendMode(blendMode)
            .SetFillRgb(0.8, 0.8, 0.8).Rectangle(0, 0, 10, 10).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([expected, expected, expected, (byte)255], Pixel(rendered, 5, 5));
        Assert.DoesNotContain("Transparency blend-mode rendering is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesAlphaSoftMaskBackdropAndTransferFunction()
    {
        PdfDocument document = AddGraphicsSoftMask(
            "Alpha", 0.5, 1, 0.5, "0 0 5 10 re f");

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 2, 5));
        Assert.Equal([127, 127, 255, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesLuminositySoftMaskBackdropAndTransferFunction()
    {
        PdfDocument document = AddGraphicsSoftMask(
            "Luminosity", 0.25, 0.75, 1, "0 g 0 0 5 10 re f");

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([191, 191, 255, 255], Pixel(rendered, 2, 5));
        Assert.Equal([64, 64, 255, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsRgbSoftMaskBackdropToLuminosity()
    {
        PdfDocument document = AddGraphicsSoftMask(
            "Luminosity", 0, 1, 0, "0 g 0 0 5 10 re f",
            "DeviceRGB", [1, 0, 0]);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([178, 178, 255, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData("DeviceGray", new double[] { 1 })]
    [InlineData("DeviceRGB", new double[] { 1, 1, 1 })]
    [InlineData("DeviceCMYK", new double[] { 0, 0, 0, 0 })]
    public void Render_DeviceColorSpaceArrayMatchesNameInSoftMask(string colorSpace, double[] backdrop)
    {
        PdfDocument named = AddGraphicsSoftMask("Luminosity", 0, 1, 1,
            "0 g 0 0 5 10 re f", colorSpace, backdrop);
        PdfDocument array = AddGraphicsSoftMask("Luminosity", 0, 1, 1,
            "0 g 0 0 5 10 re f", colorSpace, backdrop, arrayColorSpace: true);
        var options = new PdfRenderOptions(10, 10);
        PdfRenderedPage expected = new PdfPageRenderer(named).Render(0, options);
        PdfRenderedPage actual = new PdfPageRenderer(array).Render(0, options);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Equal([255, 255, 255, 255], Pixel(actual, 2, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(actual, 7, 5));
        Assert.Empty(actual.Diagnostics);
    }

    [Theory]
    [InlineData("CalGray")]
    [InlineData("DeviceN")]
    public void Render_RejectsColorSpaceArraysWithoutRequiredParameters(string colorSpace)
    {
        PdfDocument document = AddGraphicsSoftMask("Luminosity", 0, 1, 1,
            "", colorSpace, arrayColorSpace: true);
        Assert.Throws<NotSupportedException>(() =>
            new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10)));
    }

    [Fact]
    public void Render_ExpandsNestedFormsWithScopedResourcesMatricesAndBounds()
    {
        var inner = new PdfFormXObject(4, 4, new PdfContentStreamBuilder()
            .SetOpacity(0.5)
            .SetFillRgb(1, 0, 0)
            .Rectangle(-2, -2, 8, 8)
            .Fill());
        var outer = new PdfFormXObject(8, 8,
            new PdfContentStreamBuilder().DrawForm(inner, 2, 2));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawForm(outer, 1, 1))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 255, 255], Pixel(page, 4, 4));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 2, 4));
        Assert.DoesNotContain("Form XObject rendering is not implemented.", page.Diagnostics);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" % empty form\n ", true)]
    [InlineData("0 0 5 5 re f", false)]
    public void Render_RecoversInvalidResourcesOnlyForEmptyForms(string formContent,
        bool recoverable)
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .SetFillRgb(1, 0, 0).Rectangle(0, 0, 10, 10).Fill()
                .DrawForm(form, 0, 0)
                .SetFillRgb(0, 0, 1).Rectangle(0, 0, 5, 10).Fill())
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference formReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(xObjects).Value);
        PdfStream formStream = Assert.IsType<PdfStream>(source.Resolve(formReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference invalidResources = update.AddObject(
            new PdfStream(new PdfDictionary([]), Array.Empty<byte>()));
        var dictionary = new PdfDictionary(formStream.Dictionary
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), invalidResources)));
        byte[] bytes = update.ReplaceObject(formReference.ObjectNumber,
            new PdfStream(dictionary, Encoding.ASCII.GetBytes(formContent))).Build();
        var options = new PdfRenderOptions(10, 10,
            includeAnnotations: false, includeFormFields: false);

        Assert.Throws<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(bytes))
            .Render(0, options));
        var renderer = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(bytes));
        if (!recoverable)
        {
            Assert.Throws<FormatException>(() => renderer.Render(0, options));
            return;
        }

        PdfRenderedPage rendered = renderer.Render(0, options);
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 2, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_CompositesIsolatedTransparencyFormAsOneGroup()
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 7, 10)
            .Fill()
            .SetFillRgb(0, 0, 1)
            .Rectangle(3, 0, 7, 10)
            .Fill());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .SetOpacity(0.5)
                .DrawForm(form, 0, 0))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference formReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(xObjects).Value);
        PdfStream formStream = Assert.IsType<PdfStream>(source.Resolve(formReference));
        var group = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("S"), Name("Transparency")),
            new KeyValuePair<PdfName, PdfObject>(Name("I"), new PdfBoolean(true))
        ]);
        var dictionary = new PdfDictionary(formStream.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Group"), group)));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(
            formReference.ObjectNumber,
            new PdfStream(dictionary, formStream.EncodedData.Span)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 255, 255], Pixel(rendered, 1, 5));
        Assert.Equal([255, 128, 128, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_AcceptsSingleObjectKnockoutTransparencyForm()
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 10, 10)
            .Fill());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawForm(form, 0, 0))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference formReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(xObjects).Value);
        PdfStream formStream = Assert.IsType<PdfStream>(source.Resolve(formReference));
        var group = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("S"), Name("Transparency")),
            new KeyValuePair<PdfName, PdfObject>(Name("K"), new PdfBoolean(true))
        ]);
        var dictionary = new PdfDictionary(formStream.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Group"), group)));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(
            formReference.ObjectNumber,
            new PdfStream(dictionary, formStream.EncodedData.Span)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_KnocksOutEarlierObjectsInIsolatedTransparencyForm()
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .SetOpacity(0.5)
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 7, 10)
            .Fill()
            .SetFillRgb(0, 0, 1)
            .Rectangle(3, 0, 7, 10)
            .Fill());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawForm(form, 0, 0))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference formReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(xObjects).Value);
        PdfStream formStream = Assert.IsType<PdfStream>(source.Resolve(formReference));
        var group = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("S"), Name("Transparency")),
            new KeyValuePair<PdfName, PdfObject>(Name("I"), new PdfBoolean(true)),
            new KeyValuePair<PdfName, PdfObject>(Name("K"), new PdfBoolean(true))
        ]);
        var dictionary = new PdfDictionary(formStream.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Group"), group)));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(
            formReference.ObjectNumber,
            new PdfStream(dictionary, formStream.EncodedData.Span)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([127, 127, 255, 255], Pixel(rendered, 1, 5));
        Assert.Equal([255, 127, 127, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_KnocksOutEarlierObjectsAgainstNonIsolatedBackdrop()
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .SetOpacity(0.5)
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 7, 10)
            .Fill()
            .SetFillRgb(0, 0, 1)
            .Rectangle(3, 0, 7, 10)
            .Fill());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .SetFillRgb(0, 1, 0)
                .Rectangle(0, 0, 10, 10)
                .Fill()
                .DrawForm(form, 0, 0))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference formReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(xObjects).Value);
        PdfStream formStream = Assert.IsType<PdfStream>(source.Resolve(formReference));
        var group = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("S"), Name("Transparency")),
            new KeyValuePair<PdfName, PdfObject>(Name("I"), new PdfBoolean(false)),
            new KeyValuePair<PdfName, PdfObject>(Name("K"), new PdfBoolean(true))
        ]);
        var dictionary = new PdfDictionary(formStream.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Group"), group)));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(
            formReference.ObjectNumber,
            new PdfStream(dictionary, formStream.EncodedData.Span)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 128, 128, 255], Pixel(rendered, 1, 5));
        Assert.Equal([128, 128, 0, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_CompositesNonIsolatedKnockoutGroupOpacity()
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 7, 10)
            .Fill()
            .SetFillRgb(0, 0, 1)
            .Rectangle(3, 0, 7, 10)
            .Fill());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .SetFillRgb(0, 1, 0)
                .Rectangle(0, 0, 10, 10)
                .Fill()
                .SetOpacity(0.5)
                .DrawForm(form, 0, 0))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference formReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(xObjects).Value);
        PdfStream formStream = Assert.IsType<PdfStream>(source.Resolve(formReference));
        var group = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("S"), Name("Transparency")),
            new KeyValuePair<PdfName, PdfObject>(Name("I"), new PdfBoolean(false)),
            new KeyValuePair<PdfName, PdfObject>(Name("K"), new PdfBoolean(true))
        ]);
        var dictionary = new PdfDictionary(formStream.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Group"), group)));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDocument document = PdfDocument.Open(update.ReplaceObject(
            formReference.ObjectNumber,
            new PdfStream(dictionary, formStream.EncodedData.Span)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 128, 128, 255], Pixel(rendered, 1, 5));
        Assert.Equal([128, 128, 0, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_SeparatesWidgetAppearanceInclusionFromPageContent()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(20, 20)
            .AddCheckBox(0, "approved", 5, 5, 10, 10, isChecked: true)
            .Build());
        var renderer = new PdfPageRenderer(document);

        PdfRenderedPage hidden = renderer.Render(0,
            new PdfRenderOptions(20, 20, includeAnnotations: true, includeFormFields: false));
        PdfRenderedPage shown = renderer.Render(0,
            new PdfRenderOptions(20, 20, includeAnnotations: false, includeFormFields: true));

        Assert.Equal([255, 255, 255, 255], Pixel(hidden, 5, 14));
        Assert.NotEqual([255, 255, 255, 255], Pixel(shown, 5, 14));
        Assert.DoesNotContain("Form-field rendering is not implemented.", shown.Diagnostics);
    }

    [Fact]
    public void Render_PaintsInlineStencilMasksWithTheCurrentFill()
    {
        byte[] prefix = Encoding.ASCII.GetBytes(
            "0 0 1 rg q 4 0 0 2 2 3 cm BI /W 2 /H 1 /IM true ID ");
        byte[] suffix = Encoding.ASCII.GetBytes(" EI Q");
        byte[] content = [.. prefix, 0b0100_0000, .. suffix];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 0, 255], Pixel(page, 2, 6));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 5, 6));
        Assert.DoesNotContain("Masked-image rendering is not implemented.", page.Diagnostics);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Render_ReducedStencilRetainsFineInkAndOpacity(bool inverted, bool clipped, bool translucent)
    {
        string clip = clipped ? "1 1 1 2 re W n " : "";
        string decode = inverted ? "[1 0]" : "[0 1]";
        PdfDocument document = AddStrokeGraphicsState(
            $"/Test gs 0 0 1 rg {clip}2 0 0 2 1 1 cm BI /W 8 /H 8 /IM true /D {decode} /F /AHx ID 5555555555555555> EI",
            "ca", new PdfReal(translucent ? 0.5 : 1));
        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        byte unpainted = translucent ? (byte)191 : (byte)127;
        Assert.Equal(new byte[] { 255, unpainted, unpainted, 255 }, Pixel(rendered, 1, 98));
        Assert.Equal(clipped ? new byte[] { 255, 255, 255, 255 }
            : new byte[] { 255, unpainted, unpainted, 255 }, Pixel(rendered, 2, 98));
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(2,
        "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAnmpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACMAAf+T34AQDF/fgBAJP9+AGAWxf8+0CAsX/9k=",
        200, 0, 0)]
    [InlineData(1,
        "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAoGpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACUAAf+T34AgC7KKf9+AGAWi3d+AEAk/z7QICxf/2Q==",
        0, 255, 0)]
    public void Render_UsesJpeg2000EmbeddedAlpha(
        int maskMode, string encoded, byte blue, byte green, byte red)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, new PdfContentStreamBuilder()
                .SetFillGray(0.5).Rectangle(0, 0, 20, 10).Fill()
                .DrawImage(PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 20, 10))
            .Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));
        PdfDocument document = AddImageDictionaryEntry(
            filtered, "SMaskInData", new PdfInteger(maskMode));

        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(20, 10,
            includeAnnotations: false, includeFormFields: false) { CacheResult = false };
        PdfRenderedPage rendered = renderer.Render(0, options);
        PdfRenderedPage repeated = renderer.Render(0, options);

        Assert.Equal([64, 64, 192, 255], Pixel(rendered, 2, 5));
        Assert.Equal([blue, green, red, (byte)255], Pixel(rendered, 15, 5));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(rendered.Pixels.ToArray(), repeated.Pixels.ToArray());
        Assert.Empty(repeated.Diagnostics);
    }

    [Fact]
    public void RenderInto_ReusesJpeg2000ColorAndAlphaPlanes()
    {
        const int size = 512;
        int[][] channels = [
            Enumerable.Repeat(127, size * size).ToArray(),
            Enumerable.Repeat(-128, size * size).ToArray(),
            Enumerable.Repeat(-128, size * size).ToArray(),
            new int[size * size]];
        var imageSource = new CoreJ2K.Util.InterleavedImageSource(
            size, size, 4, 8, new bool[4], channels);
        var parameters = new CoreJ2K.Configuration.J2KEncoderConfiguration()
            .WithLossless().ToParameterList();
        byte[] encoded = CoreJ2K.J2kImage.ToBytes(imageSource, parameters);
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(size, size, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromRgb(size, size, new byte[size * size * 3]),
                    0, 0, size, size)).Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"), encoded);
        PdfDocument document = AddImageDictionaryEntry(filtered, "SMaskInData", new PdfInteger(1));
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(size, size,
            includeAnnotations: false, includeFormFields: false);
        byte[] output = new byte[size * size * 4];
        Assert.Empty(renderer.RenderInto(0, options, output));
        byte[] expected = output.ToArray();

        long before = GC.GetAllocatedBytesForCurrentThread();
        renderer.RenderInto(0, options, output);
        renderer.RenderInto(0, options, output);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, output);
        Assert.Equal(new byte[] { 127, 127, 255, 255 }, output[..4]);
        Assert.True(allocated < size * size * 2,
            $"Repeated cached image paints allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_IgnoresJpeg2000OpacityUnlessRequested(bool explicitZero, bool inferColorSpace)
    {
        const string encoded =
            "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAoGpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACUAAf+T34AgC7KKf9+AGAWi3d+AEAk/z7QICxf/2Q==";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 20, 10))
            .Build());
        PdfDocument document = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));
        if (explicitZero)
            document = AddImageDictionaryEntry(document, "SMaskInData", new PdfInteger(0));
        if (inferColorSpace) document = RemoveImageDictionaryEntry(document, "ColorSpace");

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 2, 5));
        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 15, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void Render_RecoversJpeg2000DictionarySampleDepth(int dictionaryBits)
    {
        const string encoded =
            "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAoGpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACUAAf+T34AgC7KKf9+AGAWi3d+AEAk/z7QICxf/2Q==";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 20, 10))
            .Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));
        PdfDocument strict = AddImageDictionaryEntry(filtered, "BitsPerComponent", new PdfInteger(dictionaryBits));
        var options = new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false);
        Assert.Throws<FormatException>(() => new PdfPageRenderer(strict).Render(0, options));

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(PdfDocumentWriter.Write(strict));
        PdfRenderedPage rendered = new PdfPageRenderer(recovered).Render(0, options);
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 2, 5));
        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 15, 5));
        PdfDocument wrongSize = AddImageDictionaryEntry(strict, "Width", new PdfInteger(3));
        PdfDocument recoveredWrongSize = PdfDocument.OpenWithCompatibilityRecovery(
            PdfDocumentWriter.Write(wrongSize));
        Assert.Throws<FormatException>(() => new PdfPageRenderer(recoveredWrongSize).Render(0, options));
    }

    [Fact]
    public void Render_InfersJpeg2000ColorSpaceWithEmbeddedAlpha()
    {
        const string encoded =
            "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAnWpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACIAAf+T34AQDF/fgBAMX8+0CAGv34AQDF//2Q==";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .SetFillGray(0.5).Rectangle(0, 0, 10, 10).Fill()
                .DrawImage(PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 10, 10))
            .Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));
        PdfDocument masked = AddImageDictionaryEntry(
            filtered, "SMaskInData", new PdfInteger(2));
        PdfDocument withMatte = AddImageDictionaryEntry(
            masked, "Matte", Reals(0, 0, 1));
        PdfDocument withoutColorSpace = RemoveImageDictionaryEntry(withMatte, "ColorSpace");
        PdfDocument document = RemoveImageDictionaryEntry(
            withoutColorSpace, "BitsPerComponent");

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([192, 192, 192, 255], Pixel(rendered, 2, 5));
        Assert.Equal([128, 128, 128, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_DecodesJpeg2000AtTheRequiredDisplayResolution()
    {
        const string encoded =
            "AAAADGpQICANCocKAAAAHGZ0eXBqcDIgAAAAAGpwMiBqcHhianB4IAAAAB5ycmVxAfj4AAUAAYAABUAADCAAEhAANwgAAAAAAFhqcDJoAAAAFmloZHIAAADsAAAA7AABBwcBAAAAAA9jb2xyAQIBAAAADAAAABNwY2xyAAEEBwcHBwD//wAAAAAYY21hcAAAAQAAAAEBAAABAgAAAQMAAAC7anAyY/9P/1EAKQAAAAAA7AAAAOwAAAAAAAAAAAAAAOwAAADsAAAAAAAAAAAAAQcBAf9SAAwAAQABAAUDAwAB/1wAE0BASEhQSEhQSEhQSEhQSEhQ/5AACgAAAAAAFgAG/5PfgCgRUFSjb/+QAAoAAAAAAA8BBv+TgP+QAAoAAAAAAA8CBv+TgP+QAAoAAAAAAA8DBv+TgP+QAAoAAAAAAA8EBv+TgP+QAAoAAAAAAA8FBv+TgP/Z";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromGray(236, 236, new byte[236 * 236]),
                    0, 0, 10, 10))
            .Build());
        PdfDocument document = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static void AssertNear(byte[] expected, byte[] actual, int tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
            Assert.InRange(actual[index], Math.Max(0, expected[index] - tolerance),
                Math.Min(255, expected[index] + tolerance));
    }

    /// <summary>Asserts that anti-aliased painting touched an opaque pixel.</summary>
    private static void AssertPainted(byte[] pixel)
    {
        Assert.Equal(255, pixel[3]);
        Assert.True(pixel[0] < 224 || pixel[1] < 224 || pixel[2] < 224,
            $"Pixel [{pixel[0]}, {pixel[1]}, {pixel[2]}] was not painted.");
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(source);
        return output.ToArray();
    }

    private static byte[] PngRgba(int width, int height, byte[] samples)
    {
        using var filtered = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            filtered.WriteByte(0);
            filtered.Write(samples, y * width * 4, width * 4);
        }
        byte[] compressed = Compress(filtered.ToArray());
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed);
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream target, ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        target.Write(length);
        target.Write(type);
        target.Write(data);
        target.Write([0, 0, 0, 0]);
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));

    private static PdfDocument AddImageDictionaryEntry(
        PdfDocument source, string name, PdfObject value, byte[]? encodedData = null)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(reference));
        PdfName entryName = Name(name);
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(entryName)).Append(
            new KeyValuePair<PdfName, PdfObject>(entryName, value)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber,
                new PdfStream(dictionary, encodedData ?? image.EncodedData.ToArray())).Build());
    }

    private static byte[] AddImageDictionaryEntryBytes(
        PdfDocument source, string name, PdfObject value, byte[] encodedData)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(reference));
        PdfName entryName = Name(name);
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(entryName)).Append(
            new KeyValuePair<PdfName, PdfObject>(entryName, value)));
        return new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber,
                new PdfStream(dictionary, encodedData)).Build();
    }

    private static PdfDocument RemoveImageDictionaryEntry(PdfDocument source, string name)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(reference));
        PdfName entryName = Name(name);
        var dictionary = new PdfDictionary(
            image.Dictionary.Where(entry => !entry.Key.Equals(entryName)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber,
                new PdfStream(dictionary, image.EncodedData.ToArray())).Build());
    }

    private static PdfDocument ReplaceImageSoftMask(
        PdfDocument source, int bits, byte[] encodedData, int? width = null)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(xObjects[Name("Im1")])));
        PdfIndirectReference maskReference = Assert.IsType<PdfIndirectReference>(
            image.Dictionary[Name("SMask")]);
        PdfStream mask = Assert.IsType<PdfStream>(source.Resolve(maskReference));
        var dictionary = new PdfDictionary(mask.Dictionary
            .Where(entry => !entry.Key.Equals(Name("BitsPerComponent"))
                && !(width.HasValue && entry.Key.Equals(Name("Width"))))
            .Append(new KeyValuePair<PdfName, PdfObject>(
                Name("BitsPerComponent"), new PdfInteger(bits))));
        if (width.HasValue)
            dictionary = new PdfDictionary(dictionary.Append(new KeyValuePair<PdfName, PdfObject>(
                Name("Width"), new PdfInteger(width.Value))));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(maskReference.ObjectNumber, new PdfStream(dictionary, encodedData))
            .Build());
    }

    private static PdfDocument AddExplicitImageMask(PdfDocument source, byte[] samples,
        int width = 2, int height = 1, bool? inverted = null)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference imageReference = Assert.IsType<PdfIndirectReference>(
            xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(imageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var maskDictionary = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("XObject")),
            new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("Image")),
            new KeyValuePair<PdfName, PdfObject>(Name("Width"), new PdfInteger(width)),
            new KeyValuePair<PdfName, PdfObject>(Name("Height"), new PdfInteger(height)),
            new KeyValuePair<PdfName, PdfObject>(Name("ImageMask"), new PdfBoolean(true))
        ]);
        if (inverted is bool reverse)
            maskDictionary = new PdfDictionary(maskDictionary.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Decode"), new PdfArray([
                    new PdfInteger(reverse ? 1 : 0), new PdfInteger(reverse ? 0 : 1)]))));
        PdfIndirectReference maskReference = update.AddObject(new PdfStream(maskDictionary, samples));
        var dictionary = new PdfDictionary(image.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Mask"), maskReference)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, image.EncodedData.Span)).Build());
    }

    private static PdfDocument AddPageColorSpaceResource(
        PdfDocument source, string name, PdfObject value)
        => AddPageColorSpaceResources(source,
            new KeyValuePair<PdfName, PdfObject>(Name(name), value));

    private static PdfDocument AddGraphicsSoftMask(string subtype,
        double transferStart, double transferEnd, double backdrop, string maskContent,
        string groupColorSpace = "DeviceGray", double[]? backdropComponents = null,
        bool arrayColorSpace = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "q /Mask gs 1 0 0 rg 0 0 10 10 re f Q"))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference groupReference = update.AddObject(new PdfStream(
            new PdfDictionary([
                Entry("Type", Name("XObject")),
                Entry("Subtype", Name("Form")),
                Entry("BBox", Reals(0, 0, 10, 10)),
                Entry("Resources", new PdfDictionary([])),
                Entry("Group", new PdfDictionary([
                    Entry("Type", Name("Group")),
                    Entry("S", Name("Transparency")),
                    Entry("CS", arrayColorSpace ? new PdfArray([Name(groupColorSpace)]) : Name(groupColorSpace))
                ]))
            ]), Encoding.ASCII.GetBytes(maskContent)));
        var transfer = new PdfDictionary([
            Entry("FunctionType", new PdfInteger(2)),
            Entry("Domain", Reals(0, 1)),
            Entry("Range", Reals(0, 1)),
            Entry("N", new PdfInteger(1)),
            Entry("C0", Reals(transferStart)),
            Entry("C1", Reals(transferEnd))
        ]);
        var softMask = new PdfDictionary([
            Entry("S", Name(subtype)),
            Entry("G", groupReference),
            Entry("BC", Reals(backdropComponents ?? [backdrop])),
            Entry("TR", transfer)
        ]);
        var states = new PdfDictionary([Entry("Mask",
            new PdfDictionary([Entry("SMask", softMask)]))]);
        var updatedResources = new PdfDictionary(resources.Append(
            Entry("ExtGState", states)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", updatedResources)));
        return PdfDocument.Open(update.ReplaceObject(
            pageReference.ObjectNumber, updatedPage).Build());

        static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) =>
            new(Name(name), value);
    }

    private static PdfDocument AddPageColorSpaceResources(PdfDocument source,
        params KeyValuePair<PdfName, PdfObject>[] values)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var colorSpaces = new PdfDictionary(values);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("ColorSpace")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpaces)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDocument AddIccImageColorSpace(PdfDocument source, PdfStream profile)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference imageReference = Assert.IsType<PdfIndirectReference>(
            xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(imageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference profileReference = update.AddObject(profile);
        var colorSpace = new PdfArray(new PdfObject[] { Name("ICCBased"), profileReference });
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("ColorSpace")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, image.EncodedData.Span)).Build());
    }

    private static PdfDocument AddSampledSeparationColorSpace(
        PdfDocument source, PdfObject function)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference imageReference = Assert.IsType<PdfIndirectReference>(
            xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(imageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference functionReference = update.AddObject(function);
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Separation"), Name("SpotRed"), Name("DeviceRGB"), functionReference
        });
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("ColorSpace")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, image.EncodedData.Span)).Build());
    }

    private static PdfDocument AddDeviceNColorSpace(
        PdfDocument source, PdfObject function, byte[] imageSamples, int componentCount = 2)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference imageReference = Assert.IsType<PdfIndirectReference>(
            xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(imageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference functionReference = update.AddObject(function);
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("DeviceN"),
            new PdfArray(Enumerable.Range(0, componentCount)
                .Select(index => (PdfObject)Name($"Spot{index + 1}"))),
            Name("DeviceRGB"), functionReference
        });
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("ColorSpace"))
                && !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, imageSamples)).Build());
    }

    private static PdfDocument AddShadingResource(PdfDocument source, PdfObject shading)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject preparedShading = shading;
        if (shading is PdfDictionary dictionary
            && dictionary.TryGetValue(Name("Function"), out PdfObject? functionValue)
            && functionValue is PdfStream function)
        {
            PdfIndirectReference functionReference = update.AddObject(function);
            preparedShading = new PdfDictionary(dictionary
                .Where(entry => !entry.Key.Equals(Name("Function")))
                .Append(new KeyValuePair<PdfName, PdfObject>(
                    Name("Function"), functionReference)));
        }
        else if (shading is PdfDictionary arrayDictionary
            && arrayDictionary.TryGetValue(Name("Function"), out PdfObject? arrayValue)
            && arrayValue is PdfArray functions && functions.All(item => item is PdfStream))
        {
            var references = new PdfArray(functions.Select(update.AddObject));
            preparedShading = new PdfDictionary(arrayDictionary
                .Where(entry => !entry.Key.Equals(Name("Function")))
                .Append(new KeyValuePair<PdfName, PdfObject>(Name("Function"), references)));
        }
        PdfIndirectReference shadingReference = update.AddObject(preparedShading);
        var shadings = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Sh1"), shadingReference)
        ]);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("Shading")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Shading"), shadings)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        return PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDocument AddSampledAxialShadingResource(
        PdfDocument source, PdfStream function)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference functionReference = update.AddObject(function);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), functionReference),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))
        ]);
        PdfIndirectReference shadingReference = update.AddObject(shading);
        var shadings = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Sh1"), shadingReference)
        ]);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("Shading")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Shading"), shadings)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        return PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDocument AddShadingPatternResource(
        PdfDocument source, PdfDictionary shading, PdfArray matrix, PdfDictionary? parameters = null)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var pattern = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("Pattern")),
            new KeyValuePair<PdfName, PdfObject>(Name("PatternType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Shading"), shading),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), matrix)]);
        if (parameters is not null)
            pattern = new PdfDictionary(pattern.Append(new KeyValuePair<PdfName, PdfObject>(Name("ExtGState"), parameters)));
        var patterns = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("P1"), pattern)]);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("Pattern")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Pattern"), patterns)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        var update = new PdfIncrementalUpdateBuilder(source);
        return PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDocument AddType3TriangleFont(PdfDocument source)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        KeyValuePair<PdfName, PdfObject> fontEntry = Assert.Single(fonts);
        PdfIndirectReference fontReference = Assert.IsType<PdfIndirectReference>(fontEntry.Value);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference glyphReference = update.AddObject(new PdfStream(
            new PdfDictionary([]), "0 0 m 1000 0 l 500 1000 l h f"u8));
        var font = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("Font")),
            new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("Type3")),
            new KeyValuePair<PdfName, PdfObject>(Name("FontBBox"), Reals(0, 0, 1000, 1000)),
            new KeyValuePair<PdfName, PdfObject>(Name("FontMatrix"),
                Reals(0.001, 0, 0, 0.001, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("CharProcs"),
                new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(Name("A"), glyphReference)])),
            new KeyValuePair<PdfName, PdfObject>(Name("Encoding"),
                new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(Name("Differences"),
                        new PdfArray(new PdfObject[] { new PdfInteger(65), Name("A") }))])),
            new KeyValuePair<PdfName, PdfObject>(Name("FirstChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("LastChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("Widths"), Reals(1000)),
            new KeyValuePair<PdfName, PdfObject>(Name("Resources"), new PdfDictionary([]))]);
        return PdfDocument.Open(update.ReplaceObject(fontReference.ObjectNumber, font).Build());
    }

    private static PdfDocument AddCffCurveFont(PdfDocument source)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        PdfIndirectReference fontReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(fonts).Value);
        byte[] program = [.. PdfCffGlyphReaderTests.Numbers(0, 0), 21,
            .. PdfCffGlyphReaderTests.Numbers(0, 1000, 1000, 0, 0, -1000), 8, 14];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference fileReference = update.AddObject(new PdfStream(
            new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(
                Name("Subtype"), Name("Type1C"))]), PdfCffGlyphReaderTests.Build(program)));
        var descriptor = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("FontDescriptor")),
            new KeyValuePair<PdfName, PdfObject>(Name("FontName"), Name("KillerCff")),
            new KeyValuePair<PdfName, PdfObject>(Name("FontFile3"), fileReference)]);
        var font = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("Font")),
            new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("Type1")),
            new KeyValuePair<PdfName, PdfObject>(Name("BaseFont"), Name("KillerCff")),
            new KeyValuePair<PdfName, PdfObject>(Name("FirstChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("LastChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("Widths"), Reals(1000)),
            new KeyValuePair<PdfName, PdfObject>(Name("FontDescriptor"), descriptor)]);
        return PdfDocument.Open(update.ReplaceObject(fontReference.ObjectNumber, font).Build());
    }

    private static PdfDocument MakeEmbeddedFontVertical(PdfDocument source)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        PdfIndirectReference fontReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(fonts).Value);
        PdfDictionary font = Assert.IsType<PdfDictionary>(source.Resolve(fontReference));
        PdfArray descendants = Assert.IsType<PdfArray>(font[Name("DescendantFonts")]);
        PdfIndirectReference descendantReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(descendants));
        PdfDictionary descendant = Assert.IsType<PdfDictionary>(
            source.Resolve(descendantReference));
        var verticalFont = new PdfDictionary(font
            .Where(entry => !entry.Key.Equals(Name("Encoding")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Encoding"), Name("Identity-V"))));
        var verticalDescendant = new PdfDictionary(descendant
            .Where(entry => !entry.Key.Equals(Name("DW2")) && !entry.Key.Equals(Name("W2")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("DW2"), Reals(1000, -1000)))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("W2"),
                new PdfArray(new PdfObject[]
                {
                    new PdfInteger(1), Reals(-1000, 500, 1000)
                }))));
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(fontReference.ObjectNumber, verticalFont);
        update.ReplaceObject(descendantReference.ObjectNumber, verticalDescendant);
        return PdfDocument.Open(update.Build());
    }

    private static PdfDocument OptionalContentMembershipDocument(string membershipEntries,
        bool compatibilityRecovery = false)
    {
        const string content = "/OC /LayerSet BDC 0 0 2 1 re f EMC";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /OFF /ON [5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Properties << /LayerSet 7 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /OCG /Name (Visible) >>",
            "<< /Type /OCG /Name (Hidden) >>",
            $"<< /Type /OCMD {membershipEntries} >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        byte[] bytes = Encoding.Latin1.GetBytes(pdf.ToString());
        return compatibilityRecovery
            ? PdfDocument.OpenWithCompatibilityRecovery(bytes)
            : PdfDocument.Open(bytes);
    }

    private static PdfDocument OptionalContentXObjectDocument()
    {
        const string pageContent = "/Fm Do";
        const string formContent = "0 0 2 1 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] /D << /BaseState /OFF >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Fm 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>\nstream\n{pageContent}\nendstream",
            "<< /Type /OCG /Name (Hidden) >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 2 1] /OC 5 0 R /Length {Encoding.ASCII.GetByteCount(formContent)} >>\nstream\n{formContent}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfDocument OptionalContentAnnotationDocument(bool hidden)
    {
        const string appearanceContent = "0 0 2 1 re f";
        string optionalContent = hidden ? " /OC 5 0 R" : string.Empty;
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] /D << /BaseState /OFF >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [6 0 R] >>",
            "<< >>",
            "<< /Type /OCG /Name (Hidden) >>",
            $"<< /Type /Annot /Subtype /Square /Rect [0 0 2 1] /AP << /N 7 0 R >>{optionalContent} >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 2 1] /Length {Encoding.ASCII.GetByteCount(appearanceContent)} >>\nstream\n{appearanceContent}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfArray Reals(params double[] values) =>
        new(values.Select(value => (PdfObject)new PdfReal(value)));
}
