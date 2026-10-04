using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRegistrationSpotOverprintTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 0.05, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 0.05, false)]
    [InlineData(1, 1, false)]
    [InlineData(0, 0, true)]
    [InlineData(0, 0.05, true)]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, true)]
    [InlineData(1, 0.05, true)]
    [InlineData(1, 1, true)]
    public void RegistrationColor_KnocksOutAllNamedSpotPlatesRegardlessOfOverprint(
        int overprintMode, double tint, bool fractional)
    {
        foreach (bool followWithWhiteImage in new[] { false, true })
        {
            PdfRenderedPage knockout = Render(tint, overprintMode, overprint: false,
                fractional, followWithWhiteImage);
            PdfRenderedPage overprint = Render(tint, overprintMode, overprint: true,
                fractional, followWithWhiteImage);

            Assert.Empty(knockout.Diagnostics);
            Assert.Empty(overprint.Diagnostics);
            Assert.NotEqual([255, 255, 255, 255], Pixel(knockout, 0));
            Assert.Equal(knockout.Pixels.ToArray(), overprint.Pixels.ToArray());
            if (followWithWhiteImage)
                Assert.Equal([255, 255, 255, 255], Pixel(overprint, 2));
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void RegistrationImage_KnocksOutAllNamedSpotPlatesRegardlessOfOverprint(
        bool indexed, int overprintMode)
    {
        double tint = indexed ? 0 : 0.05;
        foreach (bool followWithWhiteImage in new[] { false, true })
        {
            PdfRenderedPage knockout = Render(tint, overprintMode, overprint: false,
                fractional: false, followWithWhiteImage, registrationImage: true, indexed);
            PdfRenderedPage overprint = Render(tint, overprintMode, overprint: true,
                fractional: false, followWithWhiteImage, registrationImage: true, indexed);

            Assert.Empty(knockout.Diagnostics);
            Assert.Empty(overprint.Diagnostics);
            Assert.NotEqual([255, 255, 255, 255], Pixel(knockout, 0));
            Assert.Equal(knockout.Pixels.ToArray(), overprint.Pixels.ToArray());
            if (followWithWhiteImage)
                Assert.Equal([255, 255, 255, 255], Pixel(overprint, 2));
        }
    }

    private static PdfRenderedPage Render(double tint, int overprintMode, bool overprint,
        bool fractional, bool followWithWhiteImage, bool registrationImage = false,
        bool indexed = false)
    {
        string rectangle = fractional ? "1.5 0 2 1 re f " : "1 0 2 1 re f ";
        string whiteImage = followWithWhiteImage
            ? "q /WhiteOn gs 3 0 0 1 1 0 cm /White Do Q " : "";
        string registrationPaint = registrationImage
            ? "q /Registration gs 2 0 0 1 1 0 cm /Im Do Q "
            : $"q /Registration gs /All cs {tint.ToString(System.Globalization.CultureInfo.InvariantCulture)} scn "
                + rectangle + "Q ";
        string content = "q /SpotOn gs /MyRed cs 1 scn 0 0 5 1 re f "
            + "/MyBlue cs 1 scn 0 0 5 1 re f Q "
            + registrationPaint + whiteImage;
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(5, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference white = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))
        ]), new byte[4]));
        byte sample = (byte)Math.Round(tint * 255);
        PdfObject imageSpace = indexed
            ? new PdfArray([Name("Indexed"), Name("All"), new PdfInteger(0),
                new PdfString([sample], PdfStringForm.Hexadecimal)])
            : Name("All");
        PdfIndirectReference registration = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", imageSpace)
        ]), [indexed ? (byte)0 : sample]));
        PdfDictionary redTint = Tint(0.5, 0, 0, 0);
        PdfDictionary blueTint = Tint(0.5, 0.2, 0, 0);
        PdfDictionary Overprint(bool enabled, int mode) => new([
            Entry("OP", new PdfBoolean(enabled)), Entry("op", new PdfBoolean(enabled)),
            Entry("OPM", new PdfInteger(mode))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([
                Entry("MyRed", new PdfArray([
                    Name("Separation"), Name("MyRed"), Name("DeviceCMYK"), redTint
                ])),
                Entry("MyBlue", new PdfArray([
                    Name("Separation"), Name("MyBlue"), Name("DeviceCMYK"), blueTint
                ])),
                Entry("All", new PdfArray([
                    Name("Separation"), Name("All"), Name("DeviceCMYK"), Tint(1, 1, 1, 1)
                ]))
            ])),
            Entry("ExtGState", new PdfDictionary([
                Entry("SpotOn", Overprint(true, 0)),
                Entry("Registration", Overprint(overprint, overprintMode)),
                Entry("WhiteOn", Overprint(true, 0))
            ])),
            Entry("XObject", new PdfDictionary([
                Entry("White", white), Entry("Im", registration)
            ]))
        ]);
        PdfDictionary group = new([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(originalPage
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(5, 1, includeAnnotations: false, includeFormFields: false));
    }

    private static PdfDictionary Tint(double cyan, double magenta, double yellow, double black) =>
        new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)),
            Entry("C1", new PdfArray([
                new PdfReal(cyan), new PdfReal(magenta),
                new PdfReal(yellow), new PdfReal(black)
            ])),
            Entry("N", new PdfInteger(1))
        ]);

    private static byte[] Pixel(PdfRenderedPage page, int x) =>
        page.Pixels.Slice(x * 4, 4).ToArray();

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
