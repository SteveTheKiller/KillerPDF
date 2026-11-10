namespace KillerPdf.Engine.Formats.Djvu;

/// <summary>A bilevel bitmap, one bit per pixel, most significant bit first, top row first, rows padded to bytes.</summary>
internal sealed class DjvuJb2Bitmap
{
    internal DjvuJb2Bitmap(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = (width + 7) / 8;
        Bits = new byte[checked(Stride * height)];
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal byte[] Bits { get; }

    internal static long StorageBytes(long width, long height) => (width + 7) / 8 * height;

    internal bool this[int x, int y] => (Bits[y * Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
}

/// <summary>The shapes a shared JB2 dictionary (Djbz) defines, in library order, inherited shapes first.</summary>
internal sealed class DjvuJb2Dictionary
{
    internal DjvuJb2Dictionary(IReadOnlyList<DjvuJb2Bitmap> symbols) => Symbols = symbols;

    internal IReadOnlyList<DjvuJb2Bitmap> Symbols { get; }
}

/// <summary>
/// Work and storage shared by one page's JB2 decode and the dictionaries it includes. Every pixel coded or drawn
/// costs one unit of work, and every symbol kept or being decoded is counted against the storage limit before it is
/// allocated, so no stream can make the decoder run or allocate without bound.
/// </summary>
internal sealed class DjvuJb2Budget
{
    /// <summary>The most bytes the decoder holds for symbols at once, beyond the page mask.</summary>
    internal const long StorageLimit = 32L * 1024 * 1024;

    internal DjvuJb2Budget(long pageArea) => _work = 32_000_000 + 4 * pageArea;

    private long _work;
    private long _storage = StorageLimit;
    private long _records = 4_000_000;

    internal void Work(long units)
    {
        _work -= units;
        if (_work < 0) throw new InvalidDataException("JB2 data exceeds the decoding work limit.");
    }

    internal void Store(long bytes)
    {
        _storage -= bytes;
        if (_storage < 0) throw new InvalidDataException("JB2 symbols exceed the 32 MiB storage limit.");
    }

    internal void Release(long bytes) => _storage += bytes;

    internal void Record()
    {
        if (--_records < 0) throw new InvalidDataException("JB2 data exceeds the record limit.");
    }

    /// <summary>The work, storage and records still available.</summary>
    internal (long Work, long Storage, long Records) Remaining => (_work, _storage, _records);

    /// <summary>
    /// Charges what an earlier decode of a shared dictionary cost, so a page that reuses that decode is held to the
    /// same limits as one that decodes the dictionary itself.
    /// </summary>
    internal void Charge(long work, long storage, long records)
    {
        Work(work);
        Store(storage);
        _records -= records;
        if (_records < 0) throw new InvalidDataException("JB2 data exceeds the record limit.");
    }
}

/// <summary>
/// JB2 bilevel decoding as November 2005 Appendix 2 describes it: one Z'-coded record stream per chunk, a symbol
/// library fed by directly and refinement coded shapes and optionally seeded by a shared dictionary, and shapes placed
/// at absolute positions or relative to the previous shape on the current text line.
/// </summary>
internal sealed class DjvuJb2Decoder
{
    private const int StartOfData = 0, NewMark = 1, NewMarkLibraryOnly = 2, NewMarkImageOnly = 3, MatchedRefine = 4,
        MatchedRefineLibraryOnly = 5, MatchedRefineImageOnly = 6, MatchedCopy = 7, NonMarkData = 8,
        RequiredDictOrReset = 9, Comment = 10, EndOfData = 11;
    private const int BigPositive = 262142, BigNegative = -262143;
    private const int MaximumCells = 262_144;

    /// <summary>
    /// The most the coder's contexts take: pixel contexts and, for numbers, a state byte and two child links per
    /// cell, counted twice for the moment a growing table is copied.
    /// </summary>
    internal const long ContextBytes = 3072 + 2L * MaximumCells * (1 + 2 * sizeof(int));

    // Integer contexts (Table 7 plus the dictionary size).
    private const int RecordTypeContext = 0, ImageSizeContext = 1, MatchIndexContext = 2, SymbolWidthContext = 3,
        SymbolHeightContext = 4, WidthDifferenceContext = 5, HeightDifferenceContext = 6, ColumnContext = 7,
        RowContext = 8, SameLineColumnContext = 9, SameLineRowContext = 10, NewLineColumnContext = 11,
        NewLineRowContext = 12, CommentLengthContext = 13, CommentOctetContext = 14, DictionarySizeContext = 15;

    private readonly DjvuZpDecoder _coder;
    private readonly byte[] _slot = new byte[1];
    private readonly byte[] _direct = new byte[1024];
    private readonly byte[] _refine = new byte[2048];
    private byte _refinementFlag, _offsetType;
    private readonly int[] _roots = new int[16];
    private byte[] _cellStates = new byte[4096];
    private int[] _left = new int[4096], _right = new int[4096];
    private int _cells = 1;
    private readonly DjvuJb2Budget _budget;
    private readonly CancellationToken _token;
    private readonly List<DjvuJb2Bitmap> _library = [];

    private DjvuJb2Decoder(byte[] data, DjvuJb2Budget budget, CancellationToken token)
    {
        _budget = budget;
        _token = token;
        // Bits past the end of the chunk read as 1; the coder refuses a stream that needs more than 32 of them.
        _coder = new DjvuZpDecoder(data, _slot, token);
    }

    /// <summary>Decodes a Djbz shared dictionary. <paramref name="inherited"/> supplies the dictionary it requires, if any.</summary>
    internal static DjvuJb2Dictionary DecodeDictionary(byte[] data, Func<DjvuJb2Dictionary?> inherited,
        DjvuJb2Budget budget, CancellationToken token)
    {
        var decoder = Start(data, budget, token);
        return Guard(() =>
        {
            decoder.Run(0, 0, inherited, null, null);
            return new DjvuJb2Dictionary(decoder._library.ToArray());
        });
    }

    /// <summary>
    /// Decodes an Sjbz page mask of the given size. <paramref name="blit"/>, when given, sees every shape placed on
    /// the page in blit order with its top left position.
    /// </summary>
    internal static DjvuJb2Bitmap DecodeImage(byte[] data, int width, int height, Func<DjvuJb2Dictionary?> inherited,
        DjvuJb2Budget budget, Action<DjvuJb2Bitmap, int, int>? blit, CancellationToken token)
    {
        var decoder = Start(data, budget, token);
        return Guard(() =>
        {
            var mask = new DjvuJb2Bitmap(width, height);
            decoder.Run(width, height, inherited, mask, blit);
            return mask;
        });
    }

    private static DjvuJb2Decoder Start(byte[] data, DjvuJb2Budget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Guard(() => new DjvuJb2Decoder(data, budget, token));
    }

    private static T Guard<T>(Func<T> decode)
    {
        try { return decode(); }
        catch (InvalidDataException error) when (!error.Message.StartsWith("JB2", StringComparison.Ordinal))
        {
            throw new InvalidDataException("JB2 data is truncated or malformed.", error);
        }
    }

    // Text-line state from 11.5.8, in the bottom-up, one-based coordinates the spec uses.
    private int _lastLeft, _lastRowLeft, _lastRowBottom, _lastRight, _lastBottom;
    private readonly int[] _baselines = new int[3];
    private int _baselinePosition;

    private void Run(int width, int height, Func<DjvuJb2Dictionary?> inherited, DjvuJb2Bitmap? mask,
        Action<DjvuJb2Bitmap, int, int>? blit)
    {
        bool started = false;
        int required = -1;
        while (true)
        {
            _token.ThrowIfCancellationRequested();
            _budget.Record();
            int type = Number(StartOfData, EndOfData, RecordTypeContext);
            if (!started && type is not (StartOfData or RequiredDictOrReset or Comment))
                throw new InvalidDataException("JB2 data must begin with a start of image record.");
            switch (type)
            {
                case StartOfData:
                {
                    if (started) throw new InvalidDataException("JB2 data has a second start of image record.");
                    int codedWidth = Number(0, BigPositive, ImageSizeContext);
                    int codedHeight = Number(0, BigPositive, ImageSizeContext);
                    // Only the value FALSE is defined for the eventual refinement flag; it changes nothing here.
                    Bit(ref _refinementFlag);
                    if (mask is null ? codedWidth != 0 || codedHeight != 0 : codedWidth != width || codedHeight != height)
                        throw new InvalidDataException("JB2 image size does not match the page.");
                    if (required > 0)
                    {
                        var dictionary = inherited()
                            ?? throw new InvalidDataException("JB2 data requires a shared dictionary the document does not contain.");
                        if (dictionary.Symbols.Count != required)
                            throw new InvalidDataException("JB2 shared dictionary size does not match the page.");
                        _library.AddRange(dictionary.Symbols);
                    }
                    _lastLeft = 1 + width;
                    _lastRowBottom = height;
                    _lastRowLeft = _lastRight = 0;
                    _lastBottom = height;
                    FillBaselines(height);
                    started = true;
                    break;
                }
                case RequiredDictOrReset:
                    if (started) ResetNumbers();
                    else
                    {
                        if (required >= 0) throw new InvalidDataException("JB2 data requires a dictionary twice.");
                        required = Number(0, BigPositive, DictionarySizeContext);
                    }
                    break;
                case Comment:
                {
                    int length = Number(0, BigPositive, CommentLengthContext);
                    _budget.Work(length);
                    for (int i = 0; i < length; i++) Number(0, 255, CommentOctetContext);
                    break;
                }
                case EndOfData:
                    return;
                case NewMark or NewMarkLibraryOnly or NewMarkImageOnly or NonMarkData:
                {
                    if (mask is null && type != NewMarkLibraryOnly)
                        throw new InvalidDataException("A JB2 dictionary may only add shapes to its library.");
                    var shape = Direct(Size(Number(0, BigPositive, SymbolWidthContext), Number(0, BigPositive, SymbolHeightContext)));
                    if (type == NonMarkData)
                    {
                        int left = Number(1, width, ColumnContext);
                        int top = Number(1, height, RowContext);
                        Place(mask!, blit, shape, left - 1, height - top);
                    }
                    else if (type != NewMarkLibraryOnly) PlaceRelative(mask!, blit, shape);
                    Keep(shape, type is NewMark or NewMarkLibraryOnly);
                    break;
                }
                case MatchedRefine or MatchedRefineLibraryOnly or MatchedRefineImageOnly:
                {
                    if (mask is null && type != MatchedRefineLibraryOnly)
                        throw new InvalidDataException("A JB2 dictionary may only add shapes to its library.");
                    var reference = Match();
                    int shapeWidth = reference.Width + Number(BigNegative, BigPositive, WidthDifferenceContext);
                    int shapeHeight = reference.Height + Number(BigNegative, BigPositive, HeightDifferenceContext);
                    var shape = Refine(Size(shapeWidth, shapeHeight), reference);
                    if (type != MatchedRefineLibraryOnly) PlaceRelative(mask!, blit, shape);
                    Keep(shape, type is MatchedRefine or MatchedRefineLibraryOnly);
                    break;
                }
                case MatchedCopy:
                {
                    if (mask is null) throw new InvalidDataException("A JB2 dictionary may only add shapes to its library.");
                    PlaceRelative(mask, blit, Match());
                    break;
                }
                default:
                    throw new InvalidDataException("JB2 record type is not defined.");
            }
        }
    }

    private DjvuJb2Bitmap Size(int width, int height)
    {
        // Sizes outside 0 through 65535 are refused, as by the reference decoder.
        if (width is < 0 or > 65535 || height is < 0 or > 65535)
            throw new InvalidDataException("JB2 symbol size is out of range.");
        _budget.Store(DjvuJb2Bitmap.StorageBytes(width, height));
        _budget.Work((long)width * height);
        return new DjvuJb2Bitmap(width, height);
    }

    // A shape for the library keeps only its black bounding box (11.5.10); others are released once placed.
    private void Keep(DjvuJb2Bitmap shape, bool library)
    {
        _budget.Release(DjvuJb2Bitmap.StorageBytes(shape.Width, shape.Height));
        if (!library) return;
        var trimmed = Trim(shape);
        _budget.Store(DjvuJb2Bitmap.StorageBytes(trimmed.Width, trimmed.Height));
        _library.Add(trimmed);
    }

    private DjvuJb2Bitmap Match()
    {
        if (_library.Count == 0) throw new InvalidDataException("JB2 data matches a shape in an empty library.");
        return _library[Number(0, _library.Count - 1, MatchIndexContext)];
    }

    private static DjvuJb2Bitmap Trim(DjvuJb2Bitmap shape)
    {
        int left = shape.Width, right = -1, top = shape.Height, bottom = -1;
        for (int y = 0; y < shape.Height; y++)
            for (int x = 0; x < shape.Width; x++)
            {
                if (!shape[x, y]) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        if (right < 0) return new DjvuJb2Bitmap(0, 0);
        if (left == 0 && top == 0 && right == shape.Width - 1 && bottom == shape.Height - 1) return shape;
        var trimmed = new DjvuJb2Bitmap(right - left + 1, bottom - top + 1);
        for (int y = 0; y < trimmed.Height; y++)
            for (int x = 0; x < trimmed.Width; x++)
                if (shape[x + left, y + top]) trimmed.Bits[y * trimmed.Stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        return trimmed;
    }

    // 11.5.9: each pixel's context is the ten pixels of Figure 2, two rows up (3), one row up (5) and to its left (2),
    // pixel 1 the most significant; pixels outside the bitmap are white.
    private DjvuJb2Bitmap Direct(DjvuJb2Bitmap shape)
    {
        int width = shape.Width;
        // Entry x + 2 holds column x, with white entries for the three columns past either edge the template reads.
        byte[] up2 = new byte[width + 5], up1 = new byte[width + 5], up0 = new byte[width + 5];
        for (int y = 0; y < shape.Height; y++)
        {
            _token.ThrowIfCancellationRequested();
            int context = (up2[1] << 9) | (up2[2] << 8) | (up2[3] << 7) | (up1[0] << 6) | (up1[1] << 5)
                | (up1[2] << 4) | (up1[3] << 3) | (up1[4] << 2) | (up0[0] << 1) | up0[1];
            for (int x = 0; x < width; x++)
            {
                int pixel = Bit(ref _direct[context]);
                up0[x + 2] = (byte)pixel;
                // The next column's context: shift left and bring in the new right-hand pixels of rows 1 and 2 up.
                context = ((context << 1) & 0x37a) | (up1[x + 5] << 2) | (up2[x + 4] << 7) | pixel;
            }
            Pack(up0, shape, y);
            (up2, up1, up0) = (up1, up0, up2);
            Array.Clear(up0);
        }
        return shape;
    }

    // 11.5.10: four pixels of the shape being coded (Figure 3a) and seven of the matching shape (Figure 3b), the
    // matching pixel 7 aligned with P. Centers are the leftmost of two central columns and the lowermost of two rows.
    private DjvuJb2Bitmap Refine(DjvuJb2Bitmap shape, DjvuJb2Bitmap reference)
    {
        int width = shape.Width;
        int columnShift = (reference.Width + 1) / 2 - (width + 1) / 2;
        int rowShift = reference.Height / 2 - shape.Height / 2;
        byte[] up1 = new byte[width + 4], up0 = new byte[width + 4];
        byte[] above = new byte[width + 4], level = new byte[width + 4], below = new byte[width + 4];
        Unpack(reference, rowShift - 1, columnShift, above);
        Unpack(reference, rowShift, columnShift, level);
        for (int y = 0; y < shape.Height; y++)
        {
            _token.ThrowIfCancellationRequested();
            Unpack(reference, y + rowShift + 1, columnShift, below);
            for (int x = 0; x < width; x++)
            {
                int c = x + 2;
                int context = (up1[c - 1] << 10) | (up1[c] << 9) | (up1[c + 1] << 8) | (up0[c - 1] << 7)
                    | (above[c] << 6) | (level[c - 1] << 5) | (level[c] << 4) | (level[c + 1] << 3)
                    | (below[c - 1] << 2) | (below[c] << 1) | below[c + 1];
                up0[c] = (byte)Bit(ref _refine[context]);
            }
            Pack(up0, shape, y);
            (up1, up0) = (up0, up1);
            Array.Clear(up0);
            (above, level, below) = (level, below, above);
        }
        return shape;
    }

    // Row y of the reference as bytes, entry x + 2 holding reference column x + columnShift; white outside.
    private static void Unpack(DjvuJb2Bitmap reference, int y, int columnShift, byte[] row)
    {
        Array.Clear(row);
        if (y < 0 || y >= reference.Height) return;
        for (int i = 0; i < row.Length; i++)
        {
            int x = i - 2 + columnShift;
            if (x >= 0 && x < reference.Width && reference[x, y]) row[i] = 1;
        }
    }

    private static void Pack(byte[] row, DjvuJb2Bitmap shape, int y)
    {
        int offset = y * shape.Stride;
        for (int x = 0; x < shape.Width; x++)
            if (row[x + 2] != 0) shape.Bits[offset + (x >> 3)] |= (byte)(0x80 >> (x & 7));
    }

    // 11.5.8, with rows numbered from the bottom: the first shape on a line is placed from the previous line's first
    // shape, later shapes from the previous shape's right edge and the median of the last three bottom edges.
    private void PlaceRelative(DjvuJb2Bitmap mask, Action<DjvuJb2Bitmap, int, int>? blit, DjvuJb2Bitmap shape)
    {
        int left, bottom;
        if (Bit(ref _offsetType) != 0)
        {
            left = _lastRowLeft + Number(BigNegative, BigPositive, NewLineColumnContext);
            int top = _lastRowBottom + Number(BigNegative, BigPositive, NewLineRowContext);
            bottom = top - shape.Height + 1;
            _lastLeft = _lastRowLeft = left;
            _lastRight = left + shape.Width - 1;
            _lastBottom = _lastRowBottom = bottom;
            FillBaselines(bottom);
        }
        else
        {
            left = _lastRight + Number(BigNegative, BigPositive, SameLineColumnContext);
            bottom = _lastBottom + Number(BigNegative, BigPositive, SameLineRowContext);
            _lastLeft = left;
            _lastRight = left + shape.Width - 1;
            _lastBottom = Baseline(bottom);
        }
        Place(mask, blit, shape, left - 1, mask.Height - (bottom - 1) - shape.Height);
    }

    private void FillBaselines(int bottom)
    {
        _baselines[0] = _baselines[1] = _baselines[2] = bottom;
        _baselinePosition = 0;
    }

    private int Baseline(int bottom)
    {
        if (++_baselinePosition == 3) _baselinePosition = 0;
        _baselines[_baselinePosition] = bottom;
        int a = _baselines[0], b = _baselines[1], c = _baselines[2];
        return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }

    // Draws a shape with its top left pixel at (left, top) in top-down page coordinates; black stays black.
    private void Place(DjvuJb2Bitmap mask, Action<DjvuJb2Bitmap, int, int>? blit, DjvuJb2Bitmap shape, int left, int top)
    {
        int x0 = Math.Max(0, left), x1 = (int)Math.Min(mask.Width, (long)left + shape.Width);
        int y0 = Math.Max(0, top), y1 = (int)Math.Min(mask.Height, (long)top + shape.Height);
        if (x0 < x1 && y0 < y1)
        {
            _budget.Work((long)(x1 - x0) * (y1 - y0));
            for (int y = y0; y < y1; y++)
            {
                _token.ThrowIfCancellationRequested();
                int sourceRow = (y - top) * shape.Stride, targetRow = y * mask.Stride;
                for (int x = x0; x < x1; x++)
                {
                    int sx = x - left;
                    if ((shape.Bits[sourceRow + (sx >> 3)] & (0x80 >> (sx & 7))) != 0)
                        mask.Bits[targetRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }
        blit?.Invoke(shape, left, top);
    }

    private int Bit(ref byte state)
    {
        _slot[0] = state;
        int bit = _coder.Bit(0);
        state = _slot[0];
        return bit;
    }

    // 11.2.5: phase 1 the sign, phase 2 the range of Table 8, phase 3 the value inside it, phase 4 the signed result.
    // Decisions the allowed range already settles are not coded. Each tree node has its own context.
    private int Number(int low, int high, int root)
    {
        bool negative = false;
        int cutoff = 0, phase = 1, range = -1;
        int parent = -1;
        bool right = false;
        while (range != 1)
        {
            int cell = parent < 0 ? _roots[root] : right ? _right[parent] : _left[parent];
            if (cell == 0)
            {
                cell = Allocate();
                if (parent < 0) _roots[root] = cell;
                else if (right) _right[parent] = cell;
                else _left[parent] = cell;
            }
            bool decision = low >= cutoff || (high >= cutoff && Bit(ref _cellStates[cell]) != 0);
            parent = cell;
            right = decision;
            switch (phase)
            {
                case 1:
                    negative = !decision;
                    if (negative)
                    {
                        int temp = -low - 1;
                        low = -high - 1;
                        high = temp;
                    }
                    phase = 2;
                    cutoff = 1;
                    break;
                case 2:
                    if (!decision)
                    {
                        phase = 3;
                        range = (cutoff + 1) / 2;
                        if (range == 1) cutoff = 0;
                        else cutoff -= range / 2;
                    }
                    else cutoff += cutoff + 1;
                    break;
                default:
                    range /= 2;
                    if (range != 1) cutoff += decision ? range / 2 : -(range / 2);
                    else if (!decision) cutoff--;
                    break;
            }
        }
        return negative ? -cutoff - 1 : cutoff;
    }

    private int Allocate()
    {
        if (_cells == MaximumCells) throw new InvalidDataException("JB2 data exceeds the number context limit.");
        if (_cells == _cellStates.Length)
        {
            Array.Resize(ref _cellStates, _cellStates.Length * 2);
            Array.Resize(ref _left, _left.Length * 2);
            Array.Resize(ref _right, _right.Length * 2);
        }
        return _cells++;
    }

    // A reset record after the start of image clears every context used for numbers.
    private void ResetNumbers()
    {
        Array.Clear(_roots);
        Array.Clear(_cellStates, 0, _cells);
        Array.Clear(_left, 0, _cells);
        Array.Clear(_right, 0, _cells);
        _cells = 1;
    }
}
