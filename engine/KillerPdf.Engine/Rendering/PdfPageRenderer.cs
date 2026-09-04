using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Rendering;

/// <summary>Renders PDF page content through the engine-owned CPU raster pipeline.</summary>
public sealed class PdfPageRenderer
{
    private readonly PdfDocument _document;
    private readonly PdfPageContentReader _content;
    private readonly IReadOnlyList<PdfPageInformation> _pages;

    /// <summary>Creates a renderer for an immutable document.</summary>
    public PdfPageRenderer(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        if (!document.IsDecrypted)
            throw new InvalidOperationException("Authenticate the document before rendering pages.");
        _content = new PdfPageContentReader(document);
        _pages = PdfPageInformation.Read(document);
    }

    /// <summary>Renders the currently supported page operators into BGRA32 pixels.</summary>
    public PdfRenderedPage Render(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        cancellationToken.ThrowIfCancellationRequested();

        byte background = options.TransparentBackground ? (byte)0 : (byte)255;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(
            checked(options.Width * options.Height * 4));
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = background;
        }

        PdfPageInformation page = _pages[pageIndex];
        double scaleX = options.Width / page.Width;
        double scaleY = options.Height / page.Height;
        var state = new GraphicsState(Matrix.Identity, Color.Black);
        var stack = new Stack<GraphicsState>();
        Rect? rectangle = null;
        var diagnostics = new HashSet<string>();
        foreach (PdfContentInstruction instruction in _content.ReadInstructions(
            pageIndex, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PdfObject> values = instruction.Operands;
            switch (instruction.Operator)
            {
                case "q":
                    stack.Push(state);
                    break;
                case "Q":
                    state = stack.Count == 0 ? state : stack.Pop();
                    rectangle = null;
                    break;
                case "cm" when values.Count == 6:
                    state = state with { Transform = Matrix.From(values).Then(state.Transform) };
                    break;
                case "g" when values.Count == 1:
                    state = state with { Fill = Color.Gray(Number(values[0])) };
                    break;
                case "rg" when values.Count == 3:
                    state = state with
                    {
                        Fill = Color.Rgb(Number(values[0]), Number(values[1]), Number(values[2]))
                    };
                    break;
                case "k" when values.Count == 4:
                    state = state with
                    {
                        Fill = Color.Cmyk(Number(values[0]), Number(values[1]),
                            Number(values[2]), Number(values[3]))
                    };
                    break;
                case "re" when values.Count == 4:
                    rectangle = TransformRectangle(state.Transform, Number(values[0]),
                        Number(values[1]), Number(values[2]), Number(values[3]));
                    break;
                case "f" or "F" or "f*" when rectangle.HasValue:
                    Fill(pixels, options.Width, options.Height, scaleX, scaleY,
                        rectangle.Value, state.Fill);
                    rectangle = null;
                    break;
                case "n":
                    rectangle = null;
                    break;
                case "BT" or "ET" or "Tf" or "Tm" or "Td" or "TD" or "T*"
                    or "Tc" or "Tw" or "Tz" or "TL" or "Tr" or "Ts" or "Tj" or "TJ"
                    or "'" or "\"":
                    diagnostics.Add("Text rendering is not implemented.");
                    break;
                case "Do" or "BI":
                    diagnostics.Add("Image rendering is not implemented.");
                    break;
            }
        }
        if (options.IncludeAnnotations)
            diagnostics.Add("Annotation rendering is not implemented.");
        if (options.IncludeFormFields)
            diagnostics.Add("Form-field rendering is not implemented.");
        return new PdfRenderedPage(options.Width, options.Height, pixels, diagnostics);
    }

    private static void Fill(byte[] pixels, int width, int height, double scaleX,
        double scaleY, Rect rectangle, Color color)
    {
        int left = Math.Clamp((int)Math.Floor(rectangle.Left * scaleX), 0, width);
        int right = Math.Clamp((int)Math.Ceiling(rectangle.Right * scaleX), 0, width);
        int top = Math.Clamp(height - (int)Math.Ceiling(rectangle.Top * scaleY), 0, height);
        int bottom = Math.Clamp(height - (int)Math.Floor(rectangle.Bottom * scaleY), 0, height);
        for (int y = top; y < bottom; y++)
        {
            int offset = (y * width + left) * 4;
            for (int x = left; x < right; x++, offset += 4)
            {
                pixels[offset] = color.Blue;
                pixels[offset + 1] = color.Green;
                pixels[offset + 2] = color.Red;
                pixels[offset + 3] = 255;
            }
        }
    }

    private static Rect TransformRectangle(Matrix matrix, double x, double y,
        double width, double height)
    {
        Point[] points =
        [
            matrix.Apply(x, y), matrix.Apply(x + width, y),
            matrix.Apply(x, y + height), matrix.Apply(x + width, y + height)
        ];
        return new Rect(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));
    }

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new FormatException("A rendering operand is not numeric.")
    };

    private readonly record struct GraphicsState(Matrix Transform, Color Fill);
    private readonly record struct Point(double X, double Y);
    private readonly record struct Rect(double Left, double Bottom, double Right, double Top);
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        internal static Matrix Identity => new(1, 0, 0, 1, 0, 0);
        internal static Matrix From(IReadOnlyList<PdfObject> values) => new(
            Number(values[0]), Number(values[1]), Number(values[2]),
            Number(values[3]), Number(values[4]), Number(values[5]));
        internal Matrix Then(Matrix next) => new(
            A * next.A + B * next.C, A * next.B + B * next.D,
            C * next.A + D * next.C, C * next.B + D * next.D,
            E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);
        internal Point Apply(double x, double y) =>
            new(x * A + y * C + E, x * B + y * D + F);
    }

    private readonly record struct Color(byte Red, byte Green, byte Blue)
    {
        internal static Color Black => new(0, 0, 0);
        internal static Color Gray(double gray) => Rgb(gray, gray, gray);
        internal static Color Rgb(double red, double green, double blue) =>
            new(Channel(red), Channel(green), Channel(blue));
        internal static Color Cmyk(double cyan, double magenta, double yellow, double black) =>
            Rgb(1 - Math.Min(1, cyan + black), 1 - Math.Min(1, magenta + black),
                1 - Math.Min(1, yellow + black));
        private static byte Channel(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }
}
