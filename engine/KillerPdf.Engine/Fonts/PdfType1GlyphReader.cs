using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Fonts;

// Adobe Type 1 Font Format, sections 6 through 8: eexec and charstring decryption,
// outline operators, subroutines, flex, and accented-character composition.
internal sealed class PdfType1GlyphReader
{
    private readonly Dictionary<string, byte[]> _glyphs = new(StringComparer.Ordinal);
    private readonly Dictionary<int, byte[]> _subrs = [];
    private readonly Dictionary<string, PdfGlyphBounds?> _bounds = new(StringComparer.Ordinal);
    private readonly string[] _standardNames = PdfFontTables.EncodingNames("StandardEncoding")!;
    private int _lenIv = 4;
    private double _xx = 1, _xy, _yx, _yy = 1, _tx, _ty;
    internal string[]? EncodingNames { get; private set; }

    internal static PdfType1GlyphReader? TryRead(byte[] data, int length1, int length2)
    {
        try
        {
            if (length1 <= 0 || length1 >= data.Length) return null;
            if (length2 <= 0) length2 = data.Length - length1;
            if (length2 > data.Length - length1) return null;
            var result = new PdfType1GlyphReader();
            result.ReadMatrix(data.AsMemory(0, length1));
            result.ReadEncoding(data.AsMemory(0, length1));
            byte[] encrypted = data.AsSpan(length1, length2).ToArray();
            if (encrypted.Take(16).All(b => Hex(b) >= 0 || IsSpace(b)))
            {
                var digits = encrypted.Where(b => !IsSpace(b)).ToArray();
                if (digits.Length % 2 != 0) return null;
                encrypted = new byte[digits.Length / 2];
                for (int i = 0; i < encrypted.Length; i++)
                {
                    int a = Hex(digits[i * 2]), b = Hex(digits[i * 2 + 1]);
                    if (a < 0 || b < 0) return null;
                    encrypted[i] = (byte)((a << 4) | b);
                }
            }
            byte[] plain = Decrypt(encrypted, 55665, 4);
            result.ReadPrograms(plain);
            return result._glyphs.Count == 0 ? null : result;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    internal PdfGlyphBounds? GetBounds(string name) => Bounds(name, 0);

    private PdfGlyphBounds? Bounds(string name, int depth)
    {
        if (_bounds.TryGetValue(name, out var cached)) return cached;
        if (depth >= 16 || !_glyphs.TryGetValue(name, out byte[]? program)) return null;
        try
        {
            var state = new Outline(this, depth);
            state.Run(DecodeProgram(program), 0);
            var bounds = state.Result();
            _bounds[name] = bounds;
            return bounds;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or NotSupportedException)
        {
            _bounds[name] = null;
            return null;
        }
    }

    private byte[] DecodeProgram(byte[] program) => _lenIv < 0 ? program : Decrypt(program, 4330, _lenIv);

    private static byte[] Decrypt(byte[] cipher, int seed, int skip)
    {
        if (skip < 0 || skip > cipher.Length) throw new FormatException("Truncated Type1 encrypted program.");
        byte[] plain = new byte[cipher.Length - skip];
        for (int i = 0; i < cipher.Length; i++)
        {
            byte value = (byte)(cipher[i] ^ (seed >> 8));
            seed = ((cipher[i] + seed) * 52845 + 22719) & 65535;
            if (i >= skip) plain[i - skip] = value;
        }
        return plain;
    }

    private void ReadMatrix(ReadOnlyMemory<byte> header)
    {
        var tokens = new PdfTokenizer(header);
        while (true)
        {
            var token = tokens.Read();
            if (token.Kind == PdfTokenKind.EndOfInput) return;
            if (token.Kind != PdfTokenKind.Name || token.ValueAsLatin1() != "FontMatrix") continue;
            if (tokens.Read().Kind != PdfTokenKind.ArrayStart) return;
            double[] values = new double[6];
            for (int i = 0; i < 6; i++)
                if (!double.TryParse(tokens.Read().ValueAsLatin1(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || !double.IsFinite(values[i])) return;
            _xx = values[0] * 1000; _xy = values[1] * 1000;
            _yx = values[2] * 1000; _yy = values[3] * 1000;
            _tx = values[4] * 1000; _ty = values[5] * 1000;
            return;
        }
    }

    private void ReadEncoding(ReadOnlyMemory<byte> header)
    {
        var tokens = new PdfTokenizer(header);
        bool reading = false;
        PdfToken previous = default, beforePrevious = default, thirdPrevious = default;
        while (true)
        {
            var token = tokens.Read();
            if (token.Kind == PdfTokenKind.EndOfInput) return;
            string value = token.ValueAsLatin1();
            if (token.Kind == PdfTokenKind.Name && value == "Encoding")
            {
                reading = true;
                var initial = tokens.Read();
                EncodingNames = initial.Kind == PdfTokenKind.Integer
                    ? [.. Enumerable.Repeat(".notdef", 256)]
                    : PdfFontTables.EncodingNames(initial.ValueAsLatin1());
            }
            else if (reading)
            {
                if (value == "def") return;
                if (value == "put" && previous.Kind == PdfTokenKind.Name
                    && beforePrevious.Kind == PdfTokenKind.Integer && thirdPrevious.ValueAsLatin1() == "dup"
                    && int.TryParse(beforePrevious.ValueAsLatin1(), out int code) && code is >= 0 and < 256)
                {
                    EncodingNames ??= [.. Enumerable.Repeat(".notdef", 256)];
                    EncodingNames[code] = previous.ValueAsLatin1();
                }
            }
            thirdPrevious = beforePrevious;
            beforePrevious = previous;
            previous = token;
        }
    }

    private void ReadPrograms(byte[] source)
    {
        var tokens = new PdfTokenizer(source);
        PdfToken previous = default, beforePrevious = default;
        bool charStrings = false;
        int seen = 0;
        while (true)
        {
            int next = tokens.Position;
            while (next < source.Length)
            {
                if (IsSpace(source[next])) { next++; continue; }
                if (source[next] != '%') break;
                while (next < source.Length && source[next] is not 10 and not 13) next++;
            }
            PdfToken token;
            if (next + 2 < source.Length && source[next] == '-' && source[next + 1] == '|'
                && IsSpace(source[next + 2]))
            {
                token = new PdfToken(PdfTokenKind.Keyword, next, 2, source.AsMemory(next, 2));
                tokens.SetRawPosition(next + 2);
            }
            else token = tokens.Read();
            if (token.Kind == PdfTokenKind.EndOfInput || token.ValueAsLatin1() == "closefile") return;
            if (++seen > 1_000_000) throw new FormatException("Type1 program token limit exceeded.");
            string value = token.ValueAsLatin1();
            if (token.Kind == PdfTokenKind.Name && value == "CharStrings") charStrings = true;
            if (previous.Kind == PdfTokenKind.Name && previous.ValueAsLatin1() == "lenIV"
                && token.Kind == PdfTokenKind.Integer)
            {
                _lenIv = int.Parse(value, CultureInfo.InvariantCulture);
                if (_lenIv is < -1 or > 32) throw new FormatException("Invalid Type1 lenIV.");
            }
            if (token.Kind == PdfTokenKind.Keyword && value is "RD" or "-|" && previous.Kind == PdfTokenKind.Integer)
            {
                int length = int.Parse(previous.ValueAsLatin1(), CultureInfo.InvariantCulture);
                int start = tokens.Position;
                if (start >= source.Length || !IsSpace(source[start])) throw new FormatException("Missing Type1 binary separator.");
                if (source[start++] == 13 && start < source.Length && source[start] == 10) start++;
                if (length < 0 || length > source.Length - start) throw new FormatException("Truncated Type1 charstring.");
                byte[] program = source.AsSpan(start, length).ToArray();
                if (charStrings && beforePrevious.Kind == PdfTokenKind.Name)
                {
                    if (_glyphs.Count >= 65536) throw new FormatException("Type1 glyph limit exceeded.");
                    _glyphs[beforePrevious.ValueAsLatin1()] = program;
                }
                else if (!charStrings && beforePrevious.Kind == PdfTokenKind.Integer)
                {
                    int index = int.Parse(beforePrevious.ValueAsLatin1(), CultureInfo.InvariantCulture);
                    if (index is < 0 or > 65535) throw new FormatException("Invalid Type1 subroutine index.");
                    _subrs[index] = program;
                }
                tokens.SetRawPosition(start + length);
            }
            beforePrevious = previous;
            previous = token;
        }
    }

    private static int Hex(byte b) => b is >= 48 and <= 57 ? b - 48 : b is >= 65 and <= 70 ? b - 55 : b is >= 97 and <= 102 ? b - 87 : -1;
    private static bool IsSpace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private sealed class Outline(PdfType1GlyphReader font, int glyphDepth)
    {
        private readonly List<double> _stack = [];
        private readonly Stack<double> _other = [];
        private readonly List<(double X, double Y)> _flex = [];
        private double _x, _y, _sideBearing;
        private double _left = double.PositiveInfinity, _bottom = double.PositiveInfinity;
        private double _right = double.NegativeInfinity, _top = double.NegativeInfinity;
        private bool _flexing;
        private int _operations;

        internal void Run(byte[] bytes, int depth)
        {
            if (depth > 32) throw new FormatException("Type1 subroutine nesting limit exceeded.");
            for (int i = 0; i < bytes.Length;)
            {
                if (++_operations > 100000) throw new FormatException("Type1 outline operation limit exceeded.");
                byte op = bytes[i++];
                if (op >= 32)
                {
                    double number;
                    if (op <= 246) number = op - 139;
                    else if (op <= 250) { Require(bytes, i, 1); number = (op - 247) * 256 + bytes[i++] + 108; }
                    else if (op <= 254) { Require(bytes, i, 1); number = -(op - 251) * 256 - bytes[i++] - 108; }
                    else { Require(bytes, i, 4); number = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i, 4)); i += 4; }
                    Push(number);
                    continue;
                }
                switch (op)
                {
                    case 1: case 3: _stack.Clear(); break;
                    case 4: Move(0, Pop()); _stack.Clear(); break;
                    case 5:
                        for (int j = 0; j + 1 < _stack.Count; j += 2) Line(_stack[j], _stack[j + 1]);
                        _stack.Clear(); break;
                    case 6: case 7:
                        for (int j = 0; j < _stack.Count; j++)
                        {
                            bool horizontal = (j % 2 == 0) == (op == 6);
                            Line(horizontal ? _stack[j] : 0, horizontal ? 0 : _stack[j]);
                        }
                        _stack.Clear(); break;
                    case 8:
                        for (int j = 0; j + 5 < _stack.Count; j += 6)
                            Curve(_stack[j], _stack[j + 1], _stack[j + 2], _stack[j + 3], _stack[j + 4], _stack[j + 5]);
                        _stack.Clear(); break;
                    case 9: _stack.Clear(); break; // Type1 closepath preserves the current point.
                    case 10:
                        int subr = checked((int)Pop());
                        if (!font._subrs.TryGetValue(subr, out var subroutine)) throw new FormatException("Missing Type1 subroutine.");
                        Run(font.DecodeProgram(subroutine), depth + 1); break;
                    case 11: return;
                    case 12:
                        Require(bytes, i, 1); Escape(bytes[i++]); break;
                    case 13:
                        Need(2); _sideBearing = _stack[0]; _x = _sideBearing; _y = 0; _stack.Clear(); break;
                    case 14: return;
                    case 21: Need(2); Move(_stack[^2], _stack[^1]); _stack.Clear(); break;
                    case 22: Move(Pop(), 0); _stack.Clear(); break;
                    case 30: case 31:
                        Need(4);
                        if (op == 30) Curve(0, _stack[0], _stack[1], _stack[2], _stack[3], 0);
                        else Curve(_stack[0], 0, _stack[1], _stack[2], 0, _stack[3]);
                        _stack.Clear(); break;
                    default: throw new NotSupportedException($"Type1 outline operator {op} is not supported.");
                }
            }
        }

        private void Escape(byte op)
        {
            switch (op)
            {
                case 0: case 1: case 2: _stack.Clear(); return;
                case 6:
                    Need(5);
                    int baseCode = checked((int)_stack[^2]), accentCode = checked((int)_stack[^1]);
                    if (baseCode is < 0 or > 255 || accentCode is < 0 or > 255) throw new FormatException("Invalid Type1 accent code.");
                    AddBox(font.Bounds(font._standardNames[baseCode], glyphDepth + 1), 0, 0);
                    AddBox(font.Bounds(font._standardNames[accentCode], glyphDepth + 1), _stack[1] - _stack[0] + _sideBearing, _stack[2]);
                    _stack.Clear(); return;
                case 7: Need(4); _sideBearing = _stack[0]; _x = _stack[0]; _y = _stack[1]; _stack.Clear(); return;
                case 12: double divisor = Pop(), dividend = Pop(); Push(dividend / divisor); return;
                case 16:
                    int subr = checked((int)Pop()), count = checked((int)Pop());
                    if (count < 0 || count > _stack.Count) throw new FormatException("Invalid Type1 OtherSubr arguments.");
                    double[] args = [.. _stack.GetRange(_stack.Count - count, count)];
                    _stack.RemoveRange(_stack.Count - count, count);
                    if (subr == 1) { _flexing = true; _flex.Clear(); _flex.Add((_x, _y)); }
                    else if (subr == 2) { if (_flexing) _flex.Add((_x, _y)); }
                    else if (subr == 0)
                    {
                        _flexing = false;
                        if (_flex.Count >= 8)
                        {
                            var p = _flex.Skip(_flex.Count - 6).ToArray();
                            _x = _flex[0].X; _y = _flex[0].Y;
                            CurveTo(p[0], p[1], p[2]); CurveTo(p[3], p[4], p[5]);
                        }
                        else foreach (var (X, Y) in _flex) Include(X, Y);
                        _other.Push(_y); _other.Push(_x);
                    }
                    else if (subr == 3 && args.Length > 0) _other.Push(args[0]);
                    else throw new NotSupportedException("Unsupported Type1 OtherSubr.");
                    return;
                case 17: if (_other.Count == 0) throw new FormatException("Type1 OtherSubr stack underflow."); Push(_other.Pop()); return;
                case 33: Need(2); _x = _stack[^2]; _y = _stack[^1]; _stack.Clear(); return;
                default: throw new NotSupportedException($"Type1 escaped operator {op} is not supported.");
            }
        }

        private void Move(double dx, double dy) { _x += dx; _y += dy; }
        private void Line(double dx, double dy) { Include(_x, _y); _x += dx; _y += dy; Include(_x, _y); }
        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            var a = (_x + dx1, _y + dy1);
            var b = (a.Item1 + dx2, a.Item2 + dy2);
            var c = (b.Item1 + dx3, b.Item2 + dy3);
            CurveTo(a, b, c);
        }
        private void CurveTo((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            var (X, Y) = Transform(_x, _y); var p1 = Transform(a.X, a.Y);
            var p2 = Transform(b.X, b.Y); var p3 = Transform(c.X, c.Y);
            IncludeTransformed(X, Y); IncludeTransformed(p3.X, p3.Y);
            foreach (double t in Roots(X, p1.X, p2.X, p3.X).Concat(Roots(Y, p1.Y, p2.Y, p3.Y)))
                IncludeTransformed(Bezier(X, p1.X, p2.X, p3.X, t), Bezier(Y, p1.Y, p2.Y, p3.Y, t));
            _x = c.X; _y = c.Y;
        }
        private static IEnumerable<double> Roots(double p0, double p1, double p2, double p3)
        {
            double a = -p0 + 3 * p1 - 3 * p2 + p3, b = 2 * (p0 - 2 * p1 + p2), c = p1 - p0;
            if (Math.Abs(a) < 1e-12) { if (Math.Abs(b) > 1e-12) { double t = -c / b; if (t > 0 && t < 1) yield return t; } yield break; }
            double discriminant = b * b - 4 * a * c;
            if (discriminant < 0) yield break;
            double root = Math.Sqrt(discriminant);
            double first = (-b + root) / (2 * a), second = (-b - root) / (2 * a);
            if (first > 0 && first < 1) yield return first;
            if (second > 0 && second < 1) yield return second;
        }
        private static double Bezier(double a, double b, double c, double d, double t)
        {
            double u = 1 - t;
            return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        }
        private (double X, double Y) Transform(double x, double y) => (x * font._xx + y * font._yx + font._tx, x * font._xy + y * font._yy + font._ty);
        private void Include(double x, double y) { var (X, Y) = Transform(x, y); IncludeTransformed(X, Y); }
        private void IncludeTransformed(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new FormatException("Nonfinite Type1 outline coordinate.");
            _left = Math.Min(_left, x); _bottom = Math.Min(_bottom, y); _right = Math.Max(_right, x); _top = Math.Max(_top, y);
        }
        private void AddBox(PdfGlyphBounds? box, double dx, double dy)
        {
            if (box is not { } b) return;
            IncludeTransformed(b.Left + dx * font._xx + dy * font._yx, b.Bottom + dx * font._xy + dy * font._yy);
            IncludeTransformed(b.Right + dx * font._xx + dy * font._yx, b.Top + dx * font._xy + dy * font._yy);
        }
        internal PdfGlyphBounds? Result() => double.IsFinite(_left) ? new PdfGlyphBounds(_left, _bottom, _right, _top) : null;
        private void Push(double value) { if (_stack.Count >= 96 || !double.IsFinite(value)) throw new FormatException("Invalid Type1 operand stack."); _stack.Add(value); }
        private double Pop() { Need(1); double value = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); return value; }
        private void Need(int count) { if (_stack.Count < count) throw new FormatException("Type1 operand stack underflow."); }
        private static void Require(byte[] bytes, int offset, int count) { if (count > bytes.Length - offset) throw new FormatException("Truncated Type1 operand."); }
    }
}
