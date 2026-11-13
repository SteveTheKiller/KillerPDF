using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

/// <summary>
/// Display colors for unprofiled process inks on the byte render path, built from one working
/// CMYK profile so every surface shows what managed color and proof views show.
/// </summary>
/// <remarks>
/// Each ink is evaluated exactly through the profile, with the same intent and the same clip and
/// encode steps as managed frames: directly for lookup-table profiles, through a shared cache
/// for other profile shapes. The inverse for RGB paint entering
/// ink fits inks on a 17-point RGB grid against a 17-point sampling of the same profile.
/// </remarks>
internal sealed class PdfCmykDisplayTable
{
    private const int Points = 17;
    private const int StrideK = 3, StrideY = Points * 3, StrideM = Points * Points * 3, StrideC = Points * Points * Points * 3;
    private const int CacheBits = 16;
    private static readonly AsyncLocal<PdfCmykDisplayTable?> Active = new();
    private static readonly Lock Sync = new();
    private static readonly List<PdfCmykDisplayTable> Recent = [];
    private static readonly Lazy<PdfCmykDisplayTable> BuiltIn = new(() => For(PdfCmykViewingDefault.SharedProfile));

    private readonly PdfColorProfile _profile;
    private readonly KillerPdf.Engine.Authoring.PdfRenderingIntent _intent;
    // Packed (ink << 32) | valid flag | 0xRRGGBB, replaced whole so readers never see a torn entry.
    // Used only for profiles without the lookup-table fast path.
    private readonly long[] _cache = new long[1 << CacheBits];
    private readonly Lazy<int[]> _grid;
    private readonly Lazy<byte[]> _inverse;
    private long _fitIterations;

    private PdfCmykDisplayTable(PdfColorProfile profile)
    {
        ProfileSha256 = profile.ContentSha256;
        _profile = profile;
        _intent = profile.WorkingCmykForwardIntent
            ?? throw new NotSupportedException("The working CMYK profile has no usable forward transform.");
        _grid = new(BuildGrid);
        _inverse = new(() => ShippedInverse(ProfileSha256) ?? BuildInverse());
        _forward = profile.ForwardTransform(_intent);
        _byteInput = _forward?.PrepareByteInput();
    }

    // CMYK lookup-table profiles (the bundled default among them) evaluate 8-bit inks through input
    // curves resolved once per value and an exact byte quantizer, with no cache; other shapes use
    // the profile's general evaluation and the cache.
    private readonly PdfIccProfileTransform? _forward;
    private readonly PdfIccLut.ByteInput? _byteInput;

    // Smallest positive linear value whose clipped, encoded and rounded byte is at least k + 1.
    private static readonly double[] EncodeThresholds = BuildEncodeThresholds();

    private static double[] BuildEncodeThresholds()
    {
        var thresholds = new double[255];
        long high = BitConverter.DoubleToInt64Bits(2d);
        for (int level = 1; level <= 255; level++)
        {
            long low = 0, upper = high;
            while (upper - low > 1)
            {
                long middle = low + (upper - low) / 2;
                if (Channel(PdfPageRenderer.EncodeSrgb(BitConverter.Int64BitsToDouble(middle))) >= level) upper = middle;
                else low = middle;
            }
            thresholds[level - 1] = BitConverter.Int64BitsToDouble(upper);
        }
        return thresholds;
    }

    // For each of 16,384 equal steps of linear light in 0..1, the byte at the step's start, so
    // Quantize only checks the thresholds inside one step.
    private const int QuantizeSteps = 16384;
    private static readonly byte[] StepStart = BuildStepStart();

    private static byte[] BuildStepStart()
    {
        var start = new byte[QuantizeSteps + 1];
        for (int step = 0; step <= QuantizeSteps; step++)
        {
            double linear = step / (double)QuantizeSteps;
            int level = 0;
            while (level < 255 && linear >= EncodeThresholds[level]) level++;
            start[step] = (byte)level;
        }
        return start;
    }

    private static readonly double[] PcsToLinear = BuildPcsToLinear();

    private static double[] BuildPcsToLinear()
    {
        var matrix = new double[9];
        for (int column = 0; column < 3; column++)
        {
            var (red, green, blue) = PdfPageRenderer.PreciseConnectionToLinearRgb(column == 0 ? 1 : 0, column == 1 ? 1 : 0, column == 2 ? 1 : 0);
            matrix[column] = red; matrix[3 + column] = green; matrix[6 + column] = blue;
        }
        return matrix;
    }

    // The byte of an approximate linear value, and whether it is far enough from every byte
    // boundary that the exact value is certain to give the same byte.
    private const double BoundaryMargin = 1e-9;

    // A step holds at most one threshold (the steepest encode slope is under 0.25 bytes per step),
    // so one comparison finds the byte; the margin test also rejects any lookup that missed.
    private static bool Quantize(double linear, out uint level)
    {
        double[] thresholds = EncodeThresholds;
        if (!(linear > 0)) { level = 0; return thresholds[0] - linear > BoundaryMargin; }
        if (linear >= 1) { level = 255; return linear - thresholds[254] >= BoundaryMargin; }
        int found = StepStart[(int)(linear * QuantizeSteps)];
        if (found < 255 && linear >= thresholds[found]) found++;
        level = (uint)found;
        return (found == 0 || linear - thresholds[found - 1] >= BoundaryMargin)
            && (found == 255 || thresholds[found] - linear > BoundaryMargin);
    }

    // Exactly Channel(EncodeSrgb(linear)): the count of thresholds at or below the value.
    private static uint Quantize(double linear)
    {
        if (!(linear > 0)) return 0;
        if (linear >= 1) return 255;
        double[] thresholds = EncodeThresholds;
        // One step lower than the product suggests, in case it rounded up across a step boundary.
        int level = StepStart[Math.Max(0, (int)(linear * QuantizeSteps) - 1)];
        while (level < 255 && linear >= thresholds[level]) level++;
        return (uint)level;
    }

    private static uint Channel(double encoded) =>
        (uint)Math.Round(Math.Clamp(encoded, 0, 1) * 255, MidpointRounding.AwayFromZero);

    // Linear light at 17 points per ink, the smooth forward model the inverse fit inverts.
    private int[] BuildGrid()
    {
        var grid = new int[Points * Points * Points * Points * 3];
        Span<double> device = stackalloc double[4];
        Span<double> xyz = stackalloc double[3];
        int offset = 0;
        for (int c = 0; c < Points; c++)
            for (int m = 0; m < Points; m++)
                for (int y = 0; y < Points; y++)
                    for (int k = 0; k < Points; k++)
                    {
                        device[0] = c / 16d; device[1] = m / 16d; device[2] = y / 16d; device[3] = k / 16d;
                        _profile.ToXyz(device, xyz, _intent);
                        var rgb = PdfPageRenderer.PreciseConnectionToRgb(xyz[0], xyz[1], xyz[2]);
                        grid[offset++] = Linear(rgb.Red);
                        grid[offset++] = Linear(rgb.Green);
                        grid[offset++] = Linear(rgb.Blue);
                    }
        return grid;

        static int Linear(double encoded)
        {
            double linear = Math.Abs(encoded) <= 0.04045 ? encoded / 12.92
                : Math.Sign(encoded) * Math.Pow((Math.Abs(encoded) + 0.055) / 1.055, 2.4);
            return (int)Math.Round(Math.Clamp(linear, -4, 4) * LinearOne);
        }
    }

    /// <summary>Gets the SHA256 content identity of the profile this table was built from.</summary>
    internal string ProfileSha256 { get; }

    /// <summary>Gets how many Gauss-Newton steps the RGB to ink fit has run for this table, across all of its grid points.</summary>
    internal long FitIterations => Interlocked.Read(ref _fitIterations);

    /// <summary>Runs the RGB to ink fit now if it has not run, so the first RGB paint into ink finds it done.</summary>
    internal void PrepareRgbInverse() => _ = _inverse.Value;

    // The bundled profile's fitted RGB to ink table ships with the engine, so no process pays for its fit: the
    // resource holds the uppercase hex SHA256 of the profile it was fitted for, then the table.
    internal const string ShippedInverseResource = "KillerPdf.Engine.ColorProfiles.CGATS001Compat-v2-micro.rgb-inverse";
    private const int InverseLength = 17 * 17 * 17 * 4;

    /// <summary>Gets the shipped RGB to ink table for a profile, or null when none ships for its content.</summary>
    internal static byte[]? ShippedInverse(string profileSha256)
    {
        using var stream = typeof(PdfCmykDisplayTable).Assembly.GetManifestResourceStream(ShippedInverseResource);
        if (stream is null || stream.Length != 64 + InverseLength) return null;
        var data = new byte[64 + InverseLength];
        stream.ReadExactly(data);
        return System.Text.Encoding.ASCII.GetString(data, 0, 64) == profileSha256 ? data[64..] : null;
    }

    /// <summary>Fits a profile's RGB to ink table from scratch, ignoring any shipped table and every shared one.</summary>
    internal static byte[] FitRgbInverse(PdfColorProfile profile) => new PdfCmykDisplayTable(profile).BuildInverse();

    /// <summary>Gets the table for the render in progress, or the built-in profile's table.</summary>
    internal static PdfCmykDisplayTable Current => Active.Value ?? BuiltIn.Value;

    /// <summary>Gets the table built from a profile, reusing a recent one with the same content.</summary>
    internal static PdfCmykDisplayTable For(PdfColorProfile profile)
    {
        lock (Sync)
        {
            int index = Recent.FindIndex(table => table.ProfileSha256 == profile.ContentSha256);
            if (index >= 0)
            {
                var found = Recent[index];
                Recent.RemoveAt(index);
                Recent.Insert(0, found);
                return found;
            }
        }
        var built = new PdfCmykDisplayTable(profile);
        lock (Sync)
        {
            if (Recent.Find(table => table.ProfileSha256 == built.ProfileSha256) is { } raced) return raced;
            Recent.Insert(0, built);
            if (Recent.Count > 4) Recent.RemoveAt(Recent.Count - 1);
            return built;
        }
    }

    /// <summary>Makes a profile's table current for this render and its parallel work, until disposed.</summary>
    internal static Scope Use(PdfColorProfile profile)
    {
        var previous = Active.Value;
        Active.Value = For(profile);
        return new Scope(previous);
    }

    internal readonly struct Scope(PdfCmykDisplayTable? previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }

    /// <summary>Counts the display conversions of the calling thread, so tests can bound them.</summary>
    [ThreadStatic] internal static int ConversionCountOnThisThread;

    /// <summary>Converts packed C, M, Y, K bytes (low to high) to packed 0x00RRGGBB display color.</summary>
    internal uint ToRgb(uint ink)
    {
        ConversionCountOnThisThread++;
        if (_byteInput is { } input)
        {
            Span<double> xyz = stackalloc double[3];
            _forward!.BytesToXyz(input, ink, xyz);
            // One combined matrix lands within about 1e-15 of the step-by-step adaptation. Only a
            // value that close to a byte boundary needs the exact steps to decide its byte.
            double x = xyz[0], y = xyz[1], z = xyz[2];
            double red = PcsToLinear[0] * x + PcsToLinear[1] * y + PcsToLinear[2] * z;
            double green = PcsToLinear[3] * x + PcsToLinear[4] * y + PcsToLinear[5] * z;
            double blue = PcsToLinear[6] * x + PcsToLinear[7] * y + PcsToLinear[8] * z;
            if (Quantize(red, out uint r) & Quantize(green, out uint g) & Quantize(blue, out uint b))
                return r << 16 | g << 8 | b;
            (red, green, blue) = PdfPageRenderer.PreciseConnectionToLinearRgb(x, y, z);
            return Quantize(red) << 16 | Quantize(green) << 8 | Quantize(blue);
        }
        int slot = (int)((ink * 2654435761u) >> (32 - CacheBits));
        long entry = Volatile.Read(ref _cache[slot]);
        if ((entry & 0x1000000) != 0 && (uint)(entry >> 32) == ink) return (uint)entry & 0xFFFFFF;
        uint rgb = Evaluate(ink);
        Volatile.Write(ref _cache[slot], (long)ink << 32 | 0x1000000 | rgb);
        return rgb;
    }

    // The managed result for one ink: profile to D50 XYZ, D65 sRGB, clip, encode, round.
    internal uint Evaluate(uint ink)
    {
        Span<double> device = [(byte)ink / 255d, (byte)(ink >> 8) / 255d, (byte)(ink >> 16) / 255d, (byte)(ink >> 24) / 255d];
        Span<double> xyz = stackalloc double[3];
        _profile.ToXyz(device, xyz, _intent);
        var rgb = PdfPageRenderer.PreciseConnectionToRgb(xyz[0], xyz[1], xyz[2]);
        return Channel(rgb.Red) << 16 | Channel(rgb.Green) << 8 | Channel(rgb.Blue);
    }

    private const int LinearOne = 65536;

    /// <summary>Converts an RGB paint entering ink to packed C, M, Y, K bytes for this profile.</summary>
    internal uint FromRgb(byte red, byte green, byte blue)
    {
        byte[] inverse = _inverse.Value;
        double r = red * (16d / 255), g = green * (16d / 255), b = blue * (16d / 255);
        int ri = Math.Min((int)r, 15), gi = Math.Min((int)g, 15), bi = Math.Min((int)b, 15);
        r -= ri; g -= gi; b -= bi;
        double cyan = 0, magenta = 0, yellow = 0, black = 0;
        for (int dr = 0; dr < 2; dr++)
            for (int dg = 0; dg < 2; dg++)
                for (int db = 0; db < 2; db++)
                {
                    int offset = (((ri + dr) * 17 + gi + dg) * 17 + bi + db) * 4;
                    double weight = (dr == 0 ? 1 - r : r) * (dg == 0 ? 1 - g : g) * (db == 0 ? 1 - b : b);
                    cyan += inverse[offset] * weight;
                    magenta += inverse[offset + 1] * weight;
                    yellow += inverse[offset + 2] * weight;
                    black += inverse[offset + 3] * weight;
                }
        return (uint)((byte)Math.Round(cyan) | (byte)Math.Round(magenta) << 8
            | (byte)Math.Round(yellow) << 16 | (byte)Math.Round(black) << 24);
    }

    // Continuous forward conversion on 0..255 inks, the function the inverse fit inverts.
    private void Forward(ReadOnlySpan<double> ink, Span<double> rgb)
    {
        Span<int> cell = stackalloc int[4];
        Span<double> fraction = stackalloc double[4];
        for (int channel = 0; channel < 4; channel++)
        {
            double position = Math.Clamp(ink[channel], 0, 255) * 16 / 255;
            cell[channel] = Math.Min((int)position, Points - 2);
            fraction[channel] = position - cell[channel];
        }
        rgb.Clear();
        int[] grid = _grid.Value;
        int origin = cell[0] * StrideC + cell[1] * StrideM + cell[2] * StrideY + cell[3] * StrideK;
        for (int corner = 0; corner < 16; corner++)
        {
            double weight = ((corner & 8) != 0 ? fraction[0] : 1 - fraction[0]) * ((corner & 4) != 0 ? fraction[1] : 1 - fraction[1])
                * ((corner & 2) != 0 ? fraction[2] : 1 - fraction[2]) * ((corner & 1) != 0 ? fraction[3] : 1 - fraction[3]);
            int offset = origin + ((corner >> 3) & 1) * StrideC + ((corner >> 2) & 1) * StrideM
                + ((corner >> 1) & 1) * StrideY + (corner & 1) * StrideK;
            for (int channel = 0; channel < 3; channel++) rgb[channel] += weight * grid[offset + channel] / (double)LinearOne;
        }
        // The fit targets encoded color. Light outside the display range is not clipped here, so
        // inks whose color clips keep a slope toward the displayable result instead of a flat
        // plateau where the fit stalls (dark greens near red 0 stopped up to 16 codes short).
        for (int channel = 0; channel < 3; channel++)
        {
            double linear = rgb[channel];
            rgb[channel] = 255 * (linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055);
        }
    }

    // Damped Gauss-Newton fit of inks for each point of a 17-point RGB grid, starting from a
    // gray-component estimate and keeping the best quantized candidate. This is an
    // approximation, especially for RGB colors outside the profile's gamut.
    private byte[] BuildInverse()
    {
        var inverse = new byte[17 * 17 * 17 * 4];
        Span<double> x = stackalloc double[4], best = stackalloc double[4], proposal = stackalloc double[4];
        Span<double> actual = stackalloc double[3], target = stackalloc double[3], residual = stackalloc double[3];
        Span<double> low = stackalloc double[3], high = stackalloc double[3], probe = stackalloc double[4];
        Span<double> jacobian = stackalloc double[12], normal = stackalloc double[9], solved = stackalloc double[3];
        for (int index = 0; index < 17 * 17 * 17; index++)
        {
            target[0] = index / 289 * 255 / 16d;
            target[1] = index / 17 % 17 * 255 / 16d;
            target[2] = index % 17 * 255 / 16d;
            double light = Math.Max(target[0], Math.Max(target[1], target[2]));
            for (int channel = 0; channel < 3; channel++)
                x[channel] = (light - target[channel]) * 255 / Math.Max(light, 1);
            x[3] = 255 - light;
            x.CopyTo(best);
            double bestError = Error(x, target, actual);
            // A second start at full black with added process ink reaches rich darks the
            // gray-component start misses, while keeping the darkness in black ink.
            for (int start = 0; start < 2; start++)
            {
            if (start == 1)
            {
                for (int channel = 0; channel < 3; channel++)
                    x[channel] = Math.Min(255, (light - target[channel]) * 255 / Math.Max(light, 1) + (255 - light) / 2);
                x[3] = 255;
                LimitInk(x);
                double error = Error(x, target, actual);
                if (error < bestError)
                {
                    bestError = error;
                    x.CopyTo(best);
                }
            }
            for (int iteration = 0; iteration < 80; iteration++)
            {
                Interlocked.Increment(ref _fitIterations);
                Forward(x, actual);
                for (int component = 0; component < 4; component++)
                {
                    x.CopyTo(probe);
                    probe[component] = Math.Max(0, x[component] - 2);
                    double lowValue = probe[component];
                    Forward(probe, low);
                    probe[component] = Math.Min(255, x[component] + 2);
                    double span = probe[component] - lowValue;
                    Forward(probe, high);
                    for (int row = 0; row < 3; row++) jacobian[row * 4 + component] = (high[row] - low[row]) / span;
                }
                for (int row = 0; row < 3; row++) residual[row] = target[row] - actual[row];
                // step = J^T (J J^T + 0.1 I)^-1 residual
                for (int row = 0; row < 3; row++)
                    for (int column = 0; column < 3; column++)
                    {
                        double sum = row == column ? .1 : 0;
                        for (int component = 0; component < 4; component++)
                            sum += jacobian[row * 4 + component] * jacobian[column * 4 + component];
                        normal[row * 3 + column] = sum;
                    }
                Solve3(normal, residual, solved);
                for (int component = 0; component < 4; component++)
                {
                    double step = 0;
                    for (int row = 0; row < 3; row++) step += jacobian[row * 4 + component] * solved[row];
                    proposal[component] = Math.Clamp(x[component] + Math.Clamp(step, -24, 24), 0, 255);
                }
                LimitInk(proposal);
                double error = Error(proposal, target, actual);
                if (error < bestError)
                {
                    bestError = error;
                    proposal.CopyTo(best);
                }
                proposal.CopyTo(x);
            }
            }
            for (int component = 0; component < 4; component++)
                inverse[index * 4 + component] = (byte)Math.Round(best[component]);
        }
        return inverse;

        double Error(ReadOnlySpan<double> ink, ReadOnlySpan<double> goal, Span<double> scratch)
        {
            Forward(ink, scratch);
            double sum = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                double difference = scratch[channel] - goal[channel];
                sum += difference * difference;
            }
            return sum;
        }
    }

    // CGATS TR 001 (SWOP) limits total ink to 300 percent. Separations above it are not printable
    // and, as a heavy rich black, blend and overprint far darker in CMYK groups than the color
    // they stand for, so the fit keeps black ink and reduces cyan, magenta and yellow evenly.
    private const double TotalInkLimit = 3 * 255;

    private static void LimitInk(Span<double> ink)
    {
        double process = ink[0] + ink[1] + ink[2];
        double allowed = Math.Max(0, TotalInkLimit - ink[3]);
        if (process <= allowed) return;
        double scale = allowed / process;
        for (int channel = 0; channel < 3; channel++) ink[channel] *= scale;
    }

    // Cramer's rule for the symmetric, damped 3 x 3 normal system.
    private static void Solve3(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> x)
    {
        double det = Determinant(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]);
        x[0] = Determinant(b[0], a[1], a[2], b[1], a[4], a[5], b[2], a[7], a[8]) / det;
        x[1] = Determinant(a[0], b[0], a[2], a[3], b[1], a[5], a[6], b[2], a[8]) / det;
        x[2] = Determinant(a[0], a[1], b[0], a[3], a[4], b[1], a[6], a[7], b[2]) / det;

        static double Determinant(double p, double q, double r, double s, double t, double u, double v, double w, double z) =>
            p * (t * z - u * w) - q * (s * z - u * v) + r * (s * w - t * v);
    }
}
