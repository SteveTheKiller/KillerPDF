using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace KillerPdf.Engine.Fonts;

/// <summary>Bounded CFF1 Type 2 outline geometry for extraction, independent of text decoding.</summary>
public sealed class PdfCffGlyphReader
{
    private readonly byte[] _data;
    private readonly ReadOnlyMemory<byte>[] _glyphs, _global;
    private readonly ReadOnlyMemory<byte>[][] _local;
    private readonly int[] _fd, _charset;
    private readonly double[][] _matrices;
    private readonly string[] _strings;
    private readonly bool _cid;
    private readonly Dictionary<int, PdfGlyphBounds?> _cache = [];
    private readonly Dictionary<int, PdfGlyphOutline?> _outlineCache = [];
    private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> _cids = [];

    /// <summary>Reads a standalone CFF1 or OpenType CFF table; returns null for unsupported or malformed data.</summary>
    public static PdfCffGlyphReader? TryRead(ReadOnlyMemory<byte> source)
    {
        if (source.Length > 64 * 1024 * 1024) return null;
        try { return new PdfCffGlyphReader(source); }
        catch (Exception e) when (e is FormatException or NotSupportedException or OverflowException or ArgumentException)
        { return null; }
    }

    /// <summary>Finds a name-keyed glyph, returning -1 when absent.</summary>
    public int FindGlyph(string name) => _names.GetValueOrDefault(name, -1);
    /// <summary>Finds a CID-keyed glyph, returning -1 when absent.</summary>
    public int FindCid(uint cid) => _cids.GetValueOrDefault(cid, -1);
    /// <summary>Gets the number of glyph programs.</summary>
    public int GlyphCount => _glyphs.Length;
    /// <summary>Gets a name-keyed glyph name, or null for CID glyphs and unsupported names.</summary>
    public string? GetGlyphName(int glyph) => glyph >= 0 && glyph < _charset.Length && !_cid ? Sid(_charset[glyph]) : null;
    /// <summary>Gets outline bounds in thousandths of text space, or null for empty or unsupported outlines.</summary>
    public PdfGlyphBounds? GetBounds(int glyph)
    {
        if (glyph < 0 || glyph >= _glyphs.Length) return null;
        if (_cache.TryGetValue(glyph, out var cached)) return cached;
        PdfGlyphBounds? result;
        try { result = new Interpreter(_global, _local[_fd[glyph]], _matrices[_fd[glyph]]).Read(_glyphs[glyph]); }
        catch (Exception e) when (e is FormatException or NotSupportedException or OverflowException or ArgumentException)
        { result = null; }
        _cache[glyph] = result;
        return result;
    }

    /// <summary>Gets outline contours in thousandths of text space, or null for missing or unsupported outlines.</summary>
    public PdfGlyphOutline? GetOutline(int glyph)
    {
        if (glyph < 0 || glyph >= _glyphs.Length) return null;
        if (_outlineCache.TryGetValue(glyph, out var cached)) return cached;
        PdfGlyphOutline? result;
        try
        {
            PdfGlyphOutline outline = new Interpreter(
                _global, _local[_fd[glyph]], _matrices[_fd[glyph]])
                .ReadOutline(_glyphs[glyph]);
            result = glyph == 0 && outline.Contours.Count == 0 ? null : outline;
        }
        catch (Exception e) when (e is FormatException or NotSupportedException
            or OverflowException or ArgumentException)
        { result = null; }
        _outlineCache[glyph] = result;
        return result;
    }

    private PdfCffGlyphReader(ReadOnlyMemory<byte> source)
    {
        if (source.Length >= 12 && source.Span[..4].SequenceEqual("OTTO"u8))
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(source.Span[4..]);
            if (count > (source.Length - 12) / 16) throw Bad();
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                var table = source.Span.Slice(12 + i * 16, 16);
                if (!table[..4].SequenceEqual("CFF "u8)) continue;
                int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table[8..]));
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table[12..]));
                source = source.Slice(offset, length); found = true; break;
            }
            if (!found) throw Bad();
        }
        _data = source.ToArray();
        if (_data.Length < 4 || _data[0] != 1 || _data[2] < 4) throw Bad();
        int position = _data[2];
        var names = Index(ref position);
        var top = Index(ref position);
        if (names.Length != 1 || top.Length != 1) throw new NotSupportedException("CFF font sets are not supported.");
        _strings = [.. Index(ref position).Select(s => Encoding.Latin1.GetString(s.Span))];
        _global = Index(ref position);
        var dict = Dict(top[0]);
        if (Value(dict, 1206, 2) != 2) throw new NotSupportedException("Only Type 2 CFF charstrings are supported.");
        _cid = dict.ContainsKey(1230);
        position = Int(Value(dict, 17, -1));
        _glyphs = Index(ref position);
        if (_glyphs.Length == 0) throw Bad();
        _charset = Charset(Int(Value(dict, 15, 0)), _glyphs.Length);
        for (int i = 0; i < _charset.Length; i++)
        {
            if (_cid) _cids.TryAdd((uint)_charset[i], i);
            else if (Sid(_charset[i]) is string name) _names.TryAdd(name, i);
        }
        var topMatrix = Matrix(dict, [0.001, 0, 0, 0.001, 0, 0]);
        if (_cid)
        {
            position = Int(Value(dict, 1236, -1));
            var fontDicts = Index(ref position);
            if (fontDicts.Length is 0 or > 256) throw Bad();
            _local = new ReadOnlyMemory<byte>[fontDicts.Length][];
            _matrices = new double[fontDicts.Length][];
            for (int i = 0; i < fontDicts.Length; i++)
            {
                var fd = Dict(fontDicts[i]);
                _local[i] = Subrs(fd);
                _matrices[i] = Multiply(Matrix(fd, [1, 0, 0, 1, 0, 0]), topMatrix);
            }
            _fd = FdSelect(Int(Value(dict, 1237, -1)), _glyphs.Length, fontDicts.Length);
        }
        else
        {
            _local = [Subrs(dict)]; _matrices = [topMatrix]; _fd = new int[_glyphs.Length];
        }
    }

    private ReadOnlyMemory<byte>[] Subrs(Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(18, out var location)) return [];
        if (location.Length != 2) throw Bad();
        int length = Int(location[0]), offset = Int(location[1]);
        var privateDict = Dict(Slice(offset, length));
        if (!privateDict.ContainsKey(19)) return [];
        int position = checked(offset + Int(Value(privateDict, 19, 0)));
        return Index(ref position);
    }

    private ReadOnlyMemory<byte>[] Index(ref int position)
    {
        int count = U16(ref position);
        if (count == 0) return [];
        int size = Byte(ref position);
        if (size is < 1 or > 4) throw Bad();
        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            uint value = 0;
            for (int j = 0; j < size; j++) value = (value << 8) | Byte(ref position);
            offsets[i] = checked((int)value - 1);
            if (offsets[i] < 0 || (i > 0 && offsets[i] < offsets[i - 1])) throw Bad();
        }
        if (offsets[0] != 0) throw Bad();
        var bytes = Slice(position, offsets[^1]);
        var result = new ReadOnlyMemory<byte>[count];
        for (int i = 0; i < count; i++) result[i] = bytes[offsets[i]..offsets[i + 1]];
        position += offsets[^1];
        return result;
    }

    private int[] Charset(int position, int count)
    {
        var result = new int[count];
        if (position == 0 && !_cid)
        {
            if (count > 229) throw Bad();
            for (int i = 1; i < count; i++) result[i] = i;
            return result;
        }
        if (position is 0 or 1 or 2) throw new NotSupportedException("Predefined Expert CFF charsets are not supported.");
        int format = Byte(ref position), glyph = 1;
        if (format is < 0 or > 2) throw Bad();
        while (glyph < count)
        {
            int first = U16(ref position);
            int left = format == 0 ? 0 : format == 1 ? Byte(ref position) : U16(ref position);
            if (left >= count - glyph || first + left > 65535) throw Bad();
            for (int i = 0; i <= left; i++) result[glyph++] = first + i;
        }
        return result;
    }

    private int[] FdSelect(int position, int count, int fdCount)
    {
        var result = new int[count];
        int format = Byte(ref position);
        if (format == 0)
            for (int i = 0; i < count; i++) result[i] = Byte(ref position);
        else if (format == 3)
        {
            int ranges = U16(ref position), first = U16(ref position);
            if (ranges == 0 || first != 0) throw Bad();
            for (int i = 0; i < ranges; i++)
            {
                int fd = Byte(ref position), next = U16(ref position);
                if (next <= first || next > count) throw Bad();
                Array.Fill(result, fd, first, next - first); first = next;
            }
            if (first != count) throw Bad();
        }
        else throw Bad();
        if (result.Any(fd => fd >= fdCount)) throw Bad();
        return result;
    }

    private static Dictionary<int, double[]> Dict(ReadOnlyMemory<byte> source)
    {
        var bytes = source.Span;
        var result = new Dictionary<int, double[]>();
        var stack = new List<double>();
        int position = 0;
        while (position < bytes.Length)
        {
            int value = bytes[position++];
            if (value >= 28)
            {
                stack.Add(Number(bytes, ref position, value, false));
                if (stack.Count > 48) throw Bad();
                continue;
            }
            int op = value == 12 ? 1200 + Take(bytes, ref position) : value;
            result[op] = [.. stack]; stack.Clear();
        }
        if (stack.Count != 0) throw Bad();
        return result;
    }

    private static double Number(ReadOnlySpan<byte> bytes, ref int position, int value, bool charstring)
    {
        if (value is >= 32 and <= 246) return value - 139;
        if (value is >= 247 and <= 250) return (value - 247) * 256 + Take(bytes, ref position) + 108;
        if (value is >= 251 and <= 254) return -(value - 251) * 256 - Take(bytes, ref position) - 108;
        if (value == 28) return (short)((Take(bytes, ref position) << 8) | Take(bytes, ref position));
        if ((!charstring && value == 29) || (charstring && value == 255))
        {
            int n = (Take(bytes, ref position) << 24) | (Take(bytes, ref position) << 16)
                | (Take(bytes, ref position) << 8) | Take(bytes, ref position);
            return charstring ? n / 65536.0 : n;
        }
        if (!charstring && value == 30)
        {
            var text = new StringBuilder();
            while (text.Length < 64)
            {
                int pair = Take(bytes, ref position);
                foreach (int nibble in new[] { pair >> 4, pair & 15 })
                {
                    if (nibble == 15)
                    {
                        if (!double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                            || !double.IsFinite(number)) throw Bad();
                        return number;
                    }
                    text.Append(nibble switch { <= 9 => nibble.ToString(CultureInfo.InvariantCulture),
                        10 => ".", 11 => "E", 12 => "E-", 14 => "-", _ => throw Bad() });
                }
            }
        }
        throw Bad();
    }

    private string? Sid(int sid) => sid < Standard.Length ? Standard[sid]
        : sid >= 391 && sid - 391 < _strings.Length ? _strings[sid - 391] : null;
    private static double Value(Dictionary<int, double[]> dict, int op, double fallback) =>
        dict.TryGetValue(op, out var args) ? args.Length == 1 ? args[0] : throw Bad() : fallback;
    private static double[] Matrix(Dictionary<int, double[]> dict, double[] fallback) =>
        dict.TryGetValue(1207, out var args) ? args.Length == 6 ? args : throw Bad() : fallback;
    private static double[] Multiply(double[] a, double[] b) =>
        [b[0]*a[0]+b[2]*a[1], b[1]*a[0]+b[3]*a[1], b[0]*a[2]+b[2]*a[3], b[1]*a[2]+b[3]*a[3],
            b[0]*a[4]+b[2]*a[5]+b[4], b[1]*a[4]+b[3]*a[5]+b[5]];
    private static int Int(double value) => value >= 0 && value <= int.MaxValue && Math.Truncate(value) == value ? (int)value : throw Bad();
    private byte Byte(ref int position) => Take(_data, ref position);
    private int U16(ref int position) => (Byte(ref position) << 8) | Byte(ref position);
    private ReadOnlyMemory<byte> Slice(int offset, int length) => offset >= 0 && length >= 0 && offset <= _data.Length - length
        ? _data.AsMemory(offset, length) : throw Bad();
    private static byte Take(ReadOnlySpan<byte> bytes, ref int position) => position < bytes.Length ? bytes[position++] : throw Bad();
    private static FormatException Bad() => new("Invalid or unsupported CFF outline data.");

    private static readonly string[] Standard = (".notdef space exclam quotedbl numbersign dollar percent ampersand quoteright parenleft parenright asterisk plus comma hyphen period slash zero one two three four five six seven eight nine colon semicolon less equal greater question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft backslash bracketright asciicircum underscore quoteleft a b c d e f g h i j k l m n o p q r s t u v w x y z braceleft bar braceright asciitilde exclamdown cent sterling fraction yen florin section currency quotesingle quotedblleft guillemotleft guilsinglleft guilsinglright fi fl endash dagger daggerdbl periodcentered paragraph bullet quotesinglbase quotedblbase quotedblright guillemotright ellipsis perthousand questiondown grave acute circumflex tilde macron breve dotaccent dieresis ring cedilla hungarumlaut ogonek caron emdash AE ordfeminine Lslash Oslash OE ordmasculine ae dotlessi lslash oslash oe germandbls onesuperior logicalnot mu trademark Eth onehalf plusminus Thorn onequarter divide brokenbar degree thorn threequarters twosuperior registered minus eth multiply threesuperior copyright Aacute Acircumflex Adieresis Agrave Aring Atilde Ccedilla Eacute Ecircumflex Edieresis Egrave Iacute Icircumflex Idieresis Igrave Ntilde Oacute Ocircumflex Odieresis Ograve Otilde Scaron Uacute Ucircumflex Udieresis Ugrave Yacute Ydieresis Zcaron aacute acircumflex adieresis agrave aring atilde ccedilla eacute ecircumflex edieresis egrave iacute icircumflex idieresis igrave ntilde oacute ocircumflex odieresis ograve otilde scaron uacute ucircumflex udieresis ugrave yacute ydieresis zcaron").Split(' ');

    private sealed class Interpreter(ReadOnlyMemory<byte>[] global, ReadOnlyMemory<byte>[] local, double[] matrix)
    {
        private readonly List<double> _stack = [];
        private readonly double[] _transient = new double[32];
        private readonly List<PdfGlyphContour> _contours = [];
        private List<PdfGlyphPoint>? _contour;
        private double _x, _y, _left = double.PositiveInfinity, _bottom = double.PositiveInfinity,
            _right = double.NegativeInfinity, _top = double.NegativeInfinity;
        private int _steps, _hints;
        private bool _width;
        internal PdfGlyphBounds? Read(ReadOnlyMemory<byte> program)
        {
            if (!Run(program, 0)) throw Bad();
            return double.IsFinite(_left) ? new(_left, _bottom, _right, _top) : null;
        }
        internal PdfGlyphOutline ReadOutline(ReadOnlyMemory<byte> program)
        {
            if (!Run(program, 0)) throw Bad();
            FinishContour();
            return new PdfGlyphOutline(_contours.AsReadOnly());
        }
        private bool Run(ReadOnlyMemory<byte> program, int depth)
        {
            if (depth > 10) throw Bad();
            var bytes = program.Span;
            int position = 0;
            while (position < bytes.Length)
            {
                if (++_steps > 100_000) throw Bad();
                int op = Take(bytes, ref position);
                if (op == 28 || op >= 32)
                { Push(Number(bytes, ref position, op, true)); continue; }
                if (op == 12) { Escape(Take(bytes, ref position)); continue; }
                switch (op)
                {
                    case 1: case 3: case 18: case 23: case 19: case 20:
                        if (!_width && _stack.Count % 2 == 1) _stack.RemoveAt(0);
                        _width = true;
                        if (_stack.Count % 2 != 0) throw Bad();
                        _hints += _stack.Count / 2; _stack.Clear();
                        if (_hints > 96) throw Bad();
                        if (op is 19 or 20)
                        { position += (_hints + 7) / 8; if (position > bytes.Length) throw Bad(); }
                        break;
                    case 4: case 21: case 22:
                        int move = op == 21 ? 2 : 1;
                        if (!_width && _stack.Count == move + 1) _stack.RemoveAt(0);
                        _width = true; Require(move);
                        FinishContour();
                        _x += op == 4 ? 0 : _stack[0]; _y += op == 22 ? 0 : _stack[^1];
                        Finite(_x); Finite(_y); _stack.Clear(); break;
                    case 5:
                        Multiple(2);
                        for (int i = 0; i < _stack.Count; i += 2) Line(_stack[i], _stack[i + 1]);
                        _stack.Clear(); break;
                    case 6: case 7:
                        if (_stack.Count == 0) throw Bad();
                        bool horizontal = op == 6;
                        foreach (double amount in _stack) { Line(horizontal ? amount : 0, horizontal ? 0 : amount); horizontal = !horizontal; }
                        _stack.Clear(); break;
                    case 8:
                        Multiple(6);
                        for (int i = 0; i < _stack.Count; i += 6) CurveAt(i);
                        _stack.Clear(); break;
                    case 24:
                        if (_stack.Count < 8 || (_stack.Count - 2) % 6 != 0) throw Bad();
                        for (int i = 0; i < _stack.Count - 2; i += 6) CurveAt(i);
                        Line(_stack[^2], _stack[^1]); _stack.Clear(); break;
                    case 25:
                        if (_stack.Count < 8 || (_stack.Count - 6) % 2 != 0) throw Bad();
                        for (int i = 0; i < _stack.Count - 6; i += 2) Line(_stack[i], _stack[i + 1]);
                        CurveAt(_stack.Count - 6); _stack.Clear(); break;
                    case 26: case 27: case 30: case 31:
                        Alternating(op); _stack.Clear(); break;
                    case 10: case 29:
                        var subrs = op == 10 ? local : global;
                        int index = checked((int)Pop()) + (subrs.Length < 1240 ? 107 : subrs.Length < 33900 ? 1131 : 32768);
                        if (index < 0 || index >= subrs.Length) throw Bad();
                        if (Run(subrs[index], depth + 1)) return true;
                        break;
                    case 11: if (depth == 0) throw Bad(); return false;
                    case 14:
                        if (!_width && _stack.Count is 1 or 5) _stack.RemoveAt(0);
                        if (_stack.Count != 0) throw new NotSupportedException("CFF seac outlines require component composition.");
                        return true;
                    default: throw new NotSupportedException($"Unsupported Type 2 operator {op}.");
                }
            }
            throw Bad();
        }
        private void Alternating(int op)
        {
            int count = _stack.Count, i = 0;
            if (count < 4 || count % 4 is not (0 or 1)) throw Bad();
            if (op is 26 or 27)
            {
                double first = count % 4 == 1 ? _stack[i++] : 0;
                while (i < count)
                {
                    if (op == 26) Curve(first, _stack[i], _stack[i+1], _stack[i+2], 0, _stack[i+3]);
                    else Curve(_stack[i], first, _stack[i+1], _stack[i+2], _stack[i+3], 0);
                    first = 0; i += 4;
                }
            }
            else
            {
                bool horizontal = op == 31;
                while (i + 3 < count)
                {
                    bool extra = count - i == 5;
                    double last = extra ? _stack[i+4] : 0;
                    if (horizontal) Curve(_stack[i], 0, _stack[i+1], _stack[i+2], last, _stack[i+3]);
                    else Curve(0, _stack[i], _stack[i+1], _stack[i+2], _stack[i+3], last);
                    i += extra ? 5 : 4; horizontal = !horizontal;
                }
            }
        }
        private void Escape(int op)
        {
            double a, b;
            switch (op)
            {
                case 3: b=Pop(); a=Pop(); Push(a!=0 && b!=0 ? 1:0); return;
                case 4: b=Pop(); a=Pop(); Push(a!=0 || b!=0 ? 1:0); return;
                case 5: Push(Pop()==0 ? 1:0); return;
                case 9: Push(Math.Abs(Pop())); return;
                case 10: b=Pop(); Push(Pop()+b); return;
                case 11: b=Pop(); Push(Pop()-b); return;
                case 12: b=Pop(); Push(Pop()/b); return;
                case 14: Push(-Pop()); return;
                case 15: b=Pop(); Push(Pop()==b ? 1:0); return;
                case 18: Pop(); return;
                case 20: int put=Int(Pop()); if(put>=32)throw Bad(); _transient[put]=Pop(); return;
                case 21: int get=Int(Pop()); if(get>=32)throw Bad(); Push(_transient[get]); return;
                case 22: b=Pop(); a=Pop(); double s2=Pop(), s1=Pop(); Push(a<=b?s1:s2); return;
                case 23: throw new NotSupportedException("Randomized Type 2 outlines have no fixed geometry.");
                case 24: b=Pop(); Push(Pop()*b); return;
                case 26: Push(Math.Sqrt(Pop())); return;
                case 27: if(_stack.Count==0)throw Bad(); Push(_stack[^1]); return;
                case 28: b=Pop(); a=Pop(); Push(b); Push(a); return;
                case 29: int index=Math.Max(0,checked((int)Pop())); if(index>=_stack.Count)throw Bad(); Push(_stack[^(index+1)]); return;
                case 30:
                    int rotation=checked((int)Pop()), n=Int(Pop());
                    if(n>_stack.Count)throw Bad(); if(n==0)return; rotation=((rotation%n)+n)%n;
                    var values=_stack.GetRange(_stack.Count-n,n);
                    for(int j=0;j<n;j++)_stack[_stack.Count-n+(j+rotation)%n]=values[j]; return;
                case 34:
                    Require(7); Curve(_stack[0],0,_stack[1],_stack[2],_stack[3],0);
                    Curve(_stack[4],0,_stack[5],-_stack[2],_stack[6],0); break;
                case 35: Require(13); CurveAt(0); CurveAt(6); break;
                case 36:
                    Require(9); Curve(_stack[0],_stack[1],_stack[2],_stack[3],_stack[4],0);
                    Curve(_stack[5],0,_stack[6],_stack[7],_stack[8],-(_stack[1]+_stack[3]+_stack[7])); break;
                case 37:
                    Require(11); double dx=0,dy=0;
                    for(int i=0;i<10;i+=2){dx+=_stack[i];dy+=_stack[i+1];}
                    CurveAt(0); Curve(_stack[6],_stack[7],_stack[8],_stack[9],Math.Abs(dx)>Math.Abs(dy)?_stack[10]:-dx,
                        Math.Abs(dx)>Math.Abs(dy)?-dy:_stack[10]); break;
                default: throw new NotSupportedException($"Unsupported Type 2 escaped operator {op}.");
            }
            _stack.Clear();
        }
        private void Push(double value) { Finite(value); if(_stack.Count>=48)throw Bad(); _stack.Add(value); }
        private double Pop() { if(_stack.Count==0)throw Bad(); double value=_stack[^1];_stack.RemoveAt(_stack.Count-1);return value; }
        private void Require(int count) { if(_stack.Count!=count)throw Bad(); }
        private void Multiple(int count) { if(_stack.Count==0 || _stack.Count%count!=0)throw Bad(); }
        private static void Finite(double value) { if(!double.IsFinite(value) || Math.Abs(value)>1e12)throw Bad(); }
        private (double X,double Y) Transform(double x,double y)
        {
            double tx=(matrix[0]*x+matrix[2]*y+matrix[4])*1000, ty=(matrix[1]*x+matrix[3]*y+matrix[5])*1000;
            Finite(tx);Finite(ty);return(tx,ty);
        }
        private void Point((double X,double Y) p) { _left=Math.Min(_left,p.X);_right=Math.Max(_right,p.X);_bottom=Math.Min(_bottom,p.Y);_top=Math.Max(_top,p.Y); }
        private void Line(double dx,double dy)
        {
            EnsureContour();
            Point(Transform(_x,_y));
            _x += dx; _y += dy;
            var end = Transform(_x, _y);
            Point(end);
            _contour?.Add(new PdfGlyphPoint(end.X, end.Y, true));
        }
        private void CurveAt(int i) => Curve(_stack[i],_stack[i+1],_stack[i+2],_stack[i+3],_stack[i+4],_stack[i+5]);
        private void Curve(double dx1,double dy1,double dx2,double dy2,double dx3,double dy3)
        {
            EnsureContour();
            var p0=Transform(_x,_y);_x+=dx1;_y+=dy1;var (X, Y) = Transform(_x,_y);
            _x+=dx2;_y+=dy2;var p2=Transform(_x,_y);_x+=dx3;_y+=dy3;var p3=Transform(_x,_y);
            _contour?.Add(new PdfGlyphPoint(X, Y, false, true));
            _contour?.Add(new PdfGlyphPoint(p2.X, p2.Y, false, true));
            _contour?.Add(new PdfGlyphPoint(p3.X, p3.Y, true));
            Point(p0);Point(p3);Extrema(p0.X,X,p2.X,p3.X);Extrema(p0.Y,Y,p2.Y,p3.Y);
            void Extrema(double v0,double v1,double v2,double v3)
            {
                double a=-v0+3*v1-3*v2+v3,b=2*(v0-2*v1+v2),c=v1-v0;
                if(Math.Abs(a)<1e-12){if(Math.Abs(b)>1e-12)At(-c/b);return;}
                double d=b*b-4*a*c;if(d<0)return;double root=Math.Sqrt(d);At((-b+root)/(2*a));At((-b-root)/(2*a));
            }
            void At(double t)
            {
                if(t<=0 || t>=1)return;double u=1-t;
                Point((u*u*u*p0.X+3*u*u*t*X+3*u*t*t*p2.X+t*t*t*p3.X,
                    u*u*u*p0.Y+3*u*u*t*Y+3*u*t*t*p2.Y+t*t*t*p3.Y));
            }
        }
        private void EnsureContour()
        {
            if (_contour is not null) return;
            var start = Transform(_x, _y);
            _contour = [new PdfGlyphPoint(start.X, start.Y, true)];
        }
        private void FinishContour()
        {
            if (_contour is { Count: > 1 })
                _contours.Add(new PdfGlyphContour(_contour.AsReadOnly()));
            _contour = null;
        }
    }
}
