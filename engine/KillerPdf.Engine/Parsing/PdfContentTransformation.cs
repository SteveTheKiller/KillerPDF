using KillerPdf.Engine.Objects;
using System.Text;

namespace KillerPdf.Engine.Parsing;

/// <summary>A finite affine matrix for direct page-content transformations.</summary>
public readonly record struct PdfContentTransformMatrix
{
    /// <summary>Creates an affine matrix in PDF operand order.</summary>
    public PdfContentTransformMatrix(double a, double b, double c, double d, double e, double f)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)
            || !double.IsFinite(d) || !double.IsFinite(e) || !double.IsFinite(f))
            throw new ArgumentOutOfRangeException(nameof(a), "Content transformation values must be finite.");
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    /// <summary>Gets the horizontal scale or rotation value.</summary>
    public double A { get; }
    /// <summary>Gets the vertical skew or rotation value.</summary>
    public double B { get; }
    /// <summary>Gets the horizontal skew or rotation value.</summary>
    public double C { get; }
    /// <summary>Gets the vertical scale or rotation value.</summary>
    public double D { get; }
    /// <summary>Gets the horizontal translation.</summary>
    public double E { get; }
    /// <summary>Gets the vertical translation.</summary>
    public double F { get; }

    /// <summary>Creates a translation.</summary>
    public static PdfContentTransformMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    /// <summary>Creates independent horizontal and vertical scaling.</summary>
    public static PdfContentTransformMatrix Scale(double x, double y)
    {
        if (x == 0 || y == 0) throw new ArgumentOutOfRangeException(nameof(x), "Content scale cannot be zero.");
        return new(x, 0, 0, y, 0, 0);
    }
    /// <summary>Creates counterclockwise rotation around the origin.</summary>
    public static PdfContentTransformMatrix Rotation(double degrees)
    {
        if (!double.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        double radians = degrees * Math.PI / 180;
        double cosine = Math.Cos(radians), sine = Math.Sin(radians);
        return new(cosine, sine, -sine, cosine, 0, 0);
    }
}

/// <summary>A finite rectangular clipping boundary in page coordinates.</summary>
public readonly record struct PdfContentClipRectangle
{
    /// <summary>Creates a clipping rectangle with positive dimensions.</summary>
    public PdfContentClipRectangle(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width)
            || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width),
                "Content clipping coordinates must be finite and dimensions must be positive.");
        X = x; Y = y; Width = width; Height = height;
    }

    /// <summary>Gets the left coordinate.</summary>
    public double X { get; }
    /// <summary>Gets the bottom coordinate.</summary>
    public double Y { get; }
    /// <summary>Gets the width.</summary>
    public double Width { get; }
    /// <summary>Gets the height.</summary>
    public double Height { get; }
}

/// <summary>An RGB device color whose components are between zero and one.</summary>
public readonly record struct PdfDeviceRgbColor
{
    /// <summary>Creates a validated RGB device color.</summary>
    public PdfDeviceRgbColor(double red, double green, double blue)
    {
        if (!IsComponent(red) || !IsComponent(green) || !IsComponent(blue))
            throw new ArgumentOutOfRangeException(nameof(red),
                "Device RGB components must be finite values between zero and one.");
        Red = red; Green = green; Blue = blue;
    }

    /// <summary>Gets the red component.</summary>
    public double Red { get; }
    /// <summary>Gets the green component.</summary>
    public double Green { get; }
    /// <summary>Gets the blue component.</summary>
    public double Blue { get; }

    private static bool IsComponent(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}

/// <summary>Rewrites selected instructions or transforms a complete decoded content stream.</summary>
public static class PdfContentTransformation
{
    /// <summary>Replaces exact Latin-1 byte sequences in Tj and TJ text operands.</summary>
    public static IReadOnlyList<PdfContentInstruction> ReplaceLatin1Text(
        IEnumerable<PdfContentInstruction> instructions,
        IReadOnlyDictionary<string, string> replacements,
        out int replacementCount)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Any(item => item.Key.Length == 0
            || item.Key.Any(character => character > 0xff)
            || item.Value.Any(character => character > 0xff)))
            throw new ArgumentException(
                "Latin-1 text replacements require nonempty Latin-1 keys and values.",
                nameof(replacements));
        var encoded = replacements.Select(item => (
            Find: Encoding.Latin1.GetBytes(item.Key),
            Replace: Encoding.Latin1.GetBytes(item.Value))).ToArray();
        PdfContentInstruction[] source = instructions.ToArray();
        var result = source.ToArray();
        replacementCount = 0;
        for (int index = 0; index < result.Length; index++)
        {
            PdfContentInstruction instruction = result[index];
            if (instruction.Operator == "Tj" && instruction.Operands is [PdfString text])
            {
                PdfString changed = Replace(text, encoded, ref replacementCount);
                if (!ReferenceEquals(changed, text))
                    result[index] = new PdfContentInstruction("Tj", instruction.Offset, [changed]);
            }
            else if (instruction.Operator == "TJ"
                && instruction.Operands is [PdfArray array])
            {
                PdfObject[] items = array.ToArray();
                bool changed = false;
                for (int itemIndex = 0; itemIndex < items.Length; itemIndex++)
                {
                    if (items[itemIndex] is not PdfString arrayText) continue;
                    PdfString replacement = Replace(arrayText, encoded, ref replacementCount);
                    if (ReferenceEquals(replacement, arrayText)) continue;
                    items[itemIndex] = replacement;
                    changed = true;
                }
                if (changed)
                    result[index] = new PdfContentInstruction("TJ", instruction.Offset,
                        [new PdfArray(items)]);
            }
        }
        return Array.AsReadOnly(result);

        static PdfString Replace(PdfString text,
            IReadOnlyList<(byte[] Find, byte[] Replace)> replacements,
            ref int count)
        {
            byte[] current = text.Bytes.ToArray();
            bool changed = false;
            foreach ((byte[] find, byte[] replacement) in replacements)
            {
                using var output = new MemoryStream();
                int start = 0;
                for (int index = 0; index <= current.Length - find.Length;)
                {
                    if (!current.AsSpan(index, find.Length).SequenceEqual(find))
                    {
                        index++;
                        continue;
                    }
                    output.Write(current, start, index - start);
                    output.Write(replacement);
                    index += find.Length;
                    start = index;
                    count++;
                    changed = true;
                }
                if (start == 0) continue;
                output.Write(current, start, current.Length - start);
                current = output.ToArray();
            }
            return changed ? new PdfString(current, text.Form) : text;
        }
    }

    /// <summary>Removes selected complete text objects while preserving surrounding instructions.</summary>
    public static IReadOnlyList<PdfContentInstruction> RemoveTextObjects(
        IEnumerable<PdfContentInstruction> instructions,
        IEnumerable<int> textObjectIndexes)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(textObjectIndexes);
        PdfContentInstruction[] source = instructions.ToArray();
        IReadOnlyList<(int Start, int End)> ranges = TextObjectRanges(source);

        int[] requested = textObjectIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= ranges.Count)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected text-object indexes must be valid and unique.",
                nameof(textObjectIndexes));
        foreach (int requestedIndex in requested)
        {
            (int rangeStart, int rangeEnd) = ranges[requestedIndex];
            ValidateContainedScopes(source, rangeStart + 1, rangeEnd);
        }
        Dictionary<int, int> removed = requested.Select(index => ranges[index])
            .ToDictionary(range => range.Start, range => range.End);
        var result = new List<PdfContentInstruction>(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            if (removed.TryGetValue(index, out int end))
            {
                index = end;
                continue;
            }
            result.Add(source[index]);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Transforms selected complete text objects without changing other content.</summary>
    public static IReadOnlyList<PdfContentInstruction> TransformTextObjects(
        IEnumerable<PdfContentInstruction> instructions,
        IEnumerable<int> textObjectIndexes, PdfContentTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(textObjectIndexes);
        PdfContentInstruction[] source = instructions.ToArray();
        IReadOnlyList<(int Start, int End)> ranges = TextObjectRanges(source);
        int[] requested = textObjectIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= ranges.Count)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected text-object indexes must be valid and unique.",
                nameof(textObjectIndexes));
        foreach (int requestedIndex in requested)
        {
            (int start, int end) = ranges[requestedIndex];
            ValidateContainedScopes(source, start + 1, end);
        }
        Dictionary<int, int> selected = requested.Select(index => ranges[index])
            .ToDictionary(range => range.Start, range => range.End);
        var result = new List<PdfContentInstruction>(source.Length + selected.Count * 3);
        for (int index = 0; index < source.Length; index++)
        {
            if (!selected.TryGetValue(index, out int end))
            {
                result.Add(source[index]);
                continue;
            }
            result.Add(new PdfContentInstruction("q", 0, []));
            result.Add(new PdfContentInstruction("cm", 0,
            [
                new PdfReal(matrix.A), new PdfReal(matrix.B),
                new PdfReal(matrix.C), new PdfReal(matrix.D),
                new PdfReal(matrix.E), new PdfReal(matrix.F)
            ]));
            result.AddRange(source[index..(end + 1)]);
            result.Add(new PdfContentInstruction("Q", 0, []));
            index = end;
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Sets the rendering mode inside selected complete text objects.</summary>
    public static IReadOnlyList<PdfContentInstruction> SetTextObjectRenderingMode(
        IEnumerable<PdfContentInstruction> instructions,
        IEnumerable<int> textObjectIndexes, int renderingMode)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(textObjectIndexes);
        if (renderingMode is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(renderingMode),
                "Text rendering mode must be between zero and seven.");
        PdfContentInstruction[] source = instructions.ToArray();
        IReadOnlyList<(int Start, int End)> ranges = TextObjectRanges(source);
        int[] requested = textObjectIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= ranges.Count)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected text-object indexes must be valid and unique.",
                nameof(textObjectIndexes));
        HashSet<int> selected = requested.ToHashSet();
        var result = new List<PdfContentInstruction>(source.Length + selected.Count);
        for (int objectIndex = 0; objectIndex < ranges.Count; objectIndex++)
        {
            (int start, int end) = ranges[objectIndex];
            int priorEnd = objectIndex == 0 ? 0 : ranges[objectIndex - 1].End + 1;
            result.AddRange(source[priorEnd..start]);
            result.Add(source[start]);
            if (!selected.Contains(objectIndex))
            {
                result.AddRange(source[(start + 1)..(end + 1)]);
                continue;
            }
            bool replaced = false;
            for (int index = start + 1; index < end; index++)
            {
                PdfContentInstruction instruction = source[index];
                if (instruction.Operator != "Tr")
                {
                    result.Add(instruction);
                    continue;
                }
                if (instruction.Operands is not [PdfInteger] and not [PdfReal])
                    throw new FormatException("A text rendering instruction has invalid operands.");
                result.Add(new PdfContentInstruction("Tr", instruction.Offset,
                    [new PdfInteger(renderingMode)]));
                replaced = true;
            }
            if (!replaced)
                result.Insert(result.Count - (end - start - 1),
                    new PdfContentInstruction("Tr", 0, [new PdfInteger(renderingMode)]));
            result.Add(source[end]);
        }
        if (ranges.Count == 0) result.AddRange(source);
        else result.AddRange(source[(ranges[^1].End + 1)..]);
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Removes selected complete painted paths without changing clipping state.</summary>
    public static IReadOnlyList<PdfContentInstruction> RemovePaintedPaths(
        IEnumerable<PdfContentInstruction> instructions,
        IEnumerable<int> pathIndexes)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(pathIndexes);
        PdfContentInstruction[] source = instructions.ToArray();
        var ranges = new List<(int Start, int End)>();
        int start = -1;
        bool clipping = false;
        for (int index = 0; index < source.Length; index++)
        {
            string operation = source[index].Operator;
            if (operation is "m" or "l" or "c" or "v" or "y" or "h" or "re")
            {
                if (start < 0) start = index;
                continue;
            }
            if (operation is "W" or "W*")
            {
                if (start < 0)
                    throw new FormatException(
                        "A content stream clips without constructing a path.");
                clipping = true;
                continue;
            }
            if (operation is not ("S" or "s" or "f" or "F" or "f*"
                    or "B" or "B*" or "b" or "b*" or "n"))
                continue;
            if (start < 0)
                throw new FormatException(
                    "A content stream paints or ends a path that was not constructed.");
            if (clipping)
                throw new NotSupportedException(
                    "Painted path removal does not support clipping paths.");
            ranges.Add((start, index));
            start = -1;
            clipping = false;
        }
        if (start >= 0)
            throw new FormatException("A content stream contains an unfinished path.");

        int[] requested = pathIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= ranges.Count)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected painted-path indexes must be valid and unique.", nameof(pathIndexes));
        Dictionary<int, int> removed = requested.Select(index => ranges[index])
            .ToDictionary(range => range.Start, range => range.End);
        var result = new List<PdfContentInstruction>(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            if (removed.TryGetValue(index, out int end))
            {
                index = end;
                continue;
            }
            result.Add(source[index]);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Transforms selected complete painted paths without changing clipping state.</summary>
    public static IReadOnlyList<PdfContentInstruction> TransformPaintedPaths(
        IEnumerable<PdfContentInstruction> instructions,
        IEnumerable<int> pathIndexes,
        PdfContentTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(pathIndexes);
        PdfContentInstruction[] source = instructions.ToArray();
        var ranges = new List<(int Start, int End, bool Clipping)>();
        int start = -1;
        bool clipping = false;
        for (int index = 0; index < source.Length; index++)
        {
            string operation = source[index].Operator;
            if (operation is "m" or "l" or "c" or "v" or "y" or "h" or "re")
            {
                if (start < 0) start = index;
                continue;
            }
            if (operation is "W" or "W*")
            {
                if (start < 0)
                    throw new FormatException(
                        "A content stream clips without constructing a path.");
                clipping = true;
                continue;
            }
            if (operation is not ("S" or "s" or "f" or "F" or "f*"
                    or "B" or "B*" or "b" or "b*" or "n"))
                continue;
            if (start < 0)
                throw new FormatException(
                    "A content stream paints or ends a path that was not constructed.");
            ranges.Add((start, index, clipping));
            start = -1;
            clipping = false;
        }
        if (start >= 0)
            throw new FormatException("A content stream contains an unfinished path.");

        int[] requested = pathIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= ranges.Count)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected painted-path indexes must be valid and unique.", nameof(pathIndexes));
        if (requested.Any(index => ranges[index].Clipping))
            throw new NotSupportedException(
                "Painted path transformation does not support clipping paths.");
        Dictionary<int, int> selected = requested.Select(index => ranges[index])
            .ToDictionary(range => range.Start, range => range.End);
        var result = new List<PdfContentInstruction>(source.Length + selected.Count * 3);
        for (int index = 0; index < source.Length; index++)
        {
            if (!selected.TryGetValue(index, out int end))
            {
                result.Add(source[index]);
                continue;
            }
            result.Add(new PdfContentInstruction("q", 0, []));
            result.Add(new PdfContentInstruction("cm", 0,
            [
                new PdfReal(matrix.A), new PdfReal(matrix.B),
                new PdfReal(matrix.C), new PdfReal(matrix.D),
                new PdfReal(matrix.E), new PdfReal(matrix.F)
            ]));
            result.AddRange(source[index..(end + 1)]);
            result.Add(new PdfContentInstruction("Q", 0, []));
            index = end;
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static void ValidateContainedScopes(
        IReadOnlyList<PdfContentInstruction> source, int start, int end)
    {
        int graphics = 0, marked = 0, compatibility = 0;
        for (int index = start; index < end; index++)
        {
            switch (source[index].Operator)
            {
                case "q": graphics++; break;
                case "Q" when --graphics < 0:
                    throw new FormatException(
                        "A removed text object closes an outer graphics-state scope.");
                case "BMC" or "BDC": marked++; break;
                case "EMC" when --marked < 0:
                    throw new FormatException(
                        "A removed text object closes an outer marked-content scope.");
                case "BX": compatibility++; break;
                case "EX" when --compatibility < 0:
                    throw new FormatException(
                        "A removed text object closes an outer compatibility scope.");
            }
        }
        if (graphics != 0 || marked != 0 || compatibility != 0)
            throw new FormatException(
                "A removed text object contains an unclosed graphics, marked-content, or compatibility scope.");
    }

    /// <summary>Removes or replaces zero-based instructions while preserving every untouched instruction.</summary>
    public static IReadOnlyList<PdfContentInstruction> Rewrite(
        IEnumerable<PdfContentInstruction> instructions,
        IReadOnlyDictionary<int, PdfContentInstruction?> changes)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(changes);
        PdfContentInstruction[] source = instructions.ToArray();
        if (changes.Keys.Any(index => index < 0 || index >= source.Length))
            throw new ArgumentOutOfRangeException(nameof(changes), "A changed instruction index is outside the stream.");
        var result = new List<PdfContentInstruction>(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            if (!changes.TryGetValue(index, out PdfContentInstruction? replacement))
                result.Add(source[index]);
            else if (replacement is not null)
                result.Add(replacement);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Wraps all existing content in an isolated affine transformation.</summary>
    public static IReadOnlyList<PdfContentInstruction> TransformAll(
        IEnumerable<PdfContentInstruction> instructions, PdfContentTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        PdfContentInstruction[] source = instructions.ToArray();
        var result = new List<PdfContentInstruction>(source.Length + 3)
        {
            new("q", 0, []),
            new("cm", 0, [new PdfReal(matrix.A), new PdfReal(matrix.B), new PdfReal(matrix.C),
                new PdfReal(matrix.D), new PdfReal(matrix.E), new PdfReal(matrix.F)])
        };
        result.AddRange(source);
        result.Add(new PdfContentInstruction("Q", 0, []));
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Wraps a contiguous instruction range in an isolated affine transformation.</summary>
    public static IReadOnlyList<PdfContentInstruction> TransformRange(
        IEnumerable<PdfContentInstruction> instructions,
        int startIndex, int count,
        PdfContentTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        PdfContentInstruction[] source = instructions.ToArray();
        if (startIndex < 0 || count <= 0 || startIndex > source.Length - count)
            throw new ArgumentOutOfRangeException(nameof(startIndex),
                "The transformed instruction range is outside the stream.");
        var result = new List<PdfContentInstruction>(source.Length + 3);
        result.AddRange(source.Take(startIndex));
        result.Add(new PdfContentInstruction("q", 0, []));
        result.Add(new PdfContentInstruction("cm", 0,
            [
                new PdfReal(matrix.A), new PdfReal(matrix.B),
                new PdfReal(matrix.C), new PdfReal(matrix.D),
                new PdfReal(matrix.E), new PdfReal(matrix.F)
            ]));
        result.AddRange(source.Skip(startIndex).Take(count));
        result.Add(new PdfContentInstruction("Q", 0, []));
        result.AddRange(source.Skip(startIndex + count));
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Transforms selected placements of one image or Form XObject resource.</summary>
    public static IReadOnlyList<PdfContentInstruction> TransformXObjectPlacements(
        IEnumerable<PdfContentInstruction> instructions,
        PdfName resourceName,
        IEnumerable<int> occurrenceIndexes,
        PdfContentTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(occurrenceIndexes);
        if (resourceName.Bytes.IsEmpty)
            throw new ArgumentException("An XObject resource name is required.",
                nameof(resourceName));
        PdfContentInstruction[] source = instructions.ToArray();
        int[] placements = [.. source.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Operator == "Do"
                && item.instruction.Operands is [PdfName name]
                && name.Equals(resourceName))
            .Select(item => item.index)];
        int[] requested = occurrenceIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= placements.Length)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected XObject occurrence indexes must be valid and unique.",
                nameof(occurrenceIndexes));
        var selected = requested.Select(index => placements[index]).ToHashSet();
        var result = new List<PdfContentInstruction>(source.Length + selected.Count * 3);
        for (int index = 0; index < source.Length; index++)
        {
            if (!selected.Contains(index))
            {
                result.Add(source[index]);
                continue;
            }
            result.Add(new PdfContentInstruction("q", 0, []));
            result.Add(new PdfContentInstruction("cm", 0,
            [
                new PdfReal(matrix.A), new PdfReal(matrix.B),
                new PdfReal(matrix.C), new PdfReal(matrix.D),
                new PdfReal(matrix.E), new PdfReal(matrix.F)
            ]));
            result.Add(source[index]);
            result.Add(new PdfContentInstruction("Q", 0, []));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Removes selected placements of one image or Form XObject resource.</summary>
    public static IReadOnlyList<PdfContentInstruction> RemoveXObjectPlacements(
        IEnumerable<PdfContentInstruction> instructions,
        PdfName resourceName,
        IEnumerable<int> occurrenceIndexes)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(occurrenceIndexes);
        if (resourceName.Bytes.IsEmpty)
            throw new ArgumentException("An XObject resource name is required.",
                nameof(resourceName));
        PdfContentInstruction[] source = instructions.ToArray();
        int[] placements = [.. source.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Operator == "Do"
                && item.instruction.Operands is [PdfName name]
                && name.Equals(resourceName))
            .Select(item => item.index)];
        int[] requested = occurrenceIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= placements.Length)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                "Selected XObject occurrence indexes must be valid and unique.",
                nameof(occurrenceIndexes));
        HashSet<int> removed = requested.Select(index => placements[index]).ToHashSet();
        return Array.AsReadOnly(source.Where((_, index) => !removed.Contains(index)).ToArray());
    }

    /// <summary>Replaces selected placements of one image or Form XObject with another resource.</summary>
    public static IReadOnlyList<PdfContentInstruction> SubstituteXObjectPlacements(
        IEnumerable<PdfContentInstruction> instructions,
        PdfName resourceName, IEnumerable<int> occurrenceIndexes,
        PdfName replacementResourceName)
    {
        ArgumentNullException.ThrowIfNull(replacementResourceName);
        if (replacementResourceName.Bytes.IsEmpty)
            throw new ArgumentException(
                "A replacement XObject resource name is required.",
                nameof(replacementResourceName));
        PdfContentInstruction[] source = ValidateNamedPlacements(
            instructions, resourceName, occurrenceIndexes, "Do", "XObject",
            out HashSet<int> selected);
        PdfContentInstruction[] result = source.ToArray();
        foreach (int index in selected)
            result[index] = new PdfContentInstruction(
                "Do", source[index].Offset, [replacementResourceName]);
        return Array.AsReadOnly(result);
    }

    /// <summary>Clips selected placements of one image or Form XObject resource.</summary>
    public static IReadOnlyList<PdfContentInstruction> ClipXObjectPlacements(
        IEnumerable<PdfContentInstruction> instructions,
        PdfName resourceName, IEnumerable<int> occurrenceIndexes,
        PdfContentClipRectangle rectangle, bool evenOdd = false)
    {
        PdfContentInstruction[] source = ValidateNamedPlacements(
            instructions, resourceName, occurrenceIndexes, "Do", "XObject",
            out HashSet<int> selected);
        var result = new List<PdfContentInstruction>(source.Length + selected.Count * 5);
        for (int index = 0; index < source.Length; index++)
        {
            if (!selected.Contains(index))
            {
                result.Add(source[index]);
                continue;
            }
            result.Add(new PdfContentInstruction("q", 0, []));
            result.Add(new PdfContentInstruction("re", 0,
            [
                new PdfReal(rectangle.X), new PdfReal(rectangle.Y),
                new PdfReal(rectangle.Width), new PdfReal(rectangle.Height)
            ]));
            result.Add(new PdfContentInstruction(evenOdd ? "W*" : "W", 0, []));
            result.Add(new PdfContentInstruction("n", 0, []));
            result.Add(source[index]);
            result.Add(new PdfContentInstruction("Q", 0, []));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Transforms selected placements of one shading resource.</summary>
    public static IReadOnlyList<PdfContentInstruction> TransformShadingPlacements(
        IEnumerable<PdfContentInstruction> instructions,
        PdfName resourceName, IEnumerable<int> occurrenceIndexes,
        PdfContentTransformMatrix matrix)
    {
        PdfContentInstruction[] source = ValidateNamedPlacements(
            instructions, resourceName, occurrenceIndexes, "sh", "shading",
            out HashSet<int> selected);
        var result = new List<PdfContentInstruction>(source.Length + selected.Count * 3);
        for (int index = 0; index < source.Length; index++)
        {
            if (!selected.Contains(index))
            {
                result.Add(source[index]);
                continue;
            }
            result.Add(new PdfContentInstruction("q", 0, []));
            result.Add(new PdfContentInstruction("cm", 0,
            [
                new PdfReal(matrix.A), new PdfReal(matrix.B),
                new PdfReal(matrix.C), new PdfReal(matrix.D),
                new PdfReal(matrix.E), new PdfReal(matrix.F)
            ]));
            result.Add(source[index]);
            result.Add(new PdfContentInstruction("Q", 0, []));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Removes selected placements of one shading resource.</summary>
    public static IReadOnlyList<PdfContentInstruction> RemoveShadingPlacements(
        IEnumerable<PdfContentInstruction> instructions,
        PdfName resourceName, IEnumerable<int> occurrenceIndexes)
    {
        PdfContentInstruction[] source = ValidateNamedPlacements(
            instructions, resourceName, occurrenceIndexes, "sh", "shading",
            out HashSet<int> selected);
        return Array.AsReadOnly(source.Where((_, index) => !selected.Contains(index)).ToArray());
    }

    private static PdfContentInstruction[] ValidateNamedPlacements(
        IEnumerable<PdfContentInstruction> instructions, PdfName resourceName,
        IEnumerable<int> occurrenceIndexes, string operation, string kind,
        out HashSet<int> selected)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(resourceName);
        ArgumentNullException.ThrowIfNull(occurrenceIndexes);
        if (resourceName.Bytes.IsEmpty)
            throw new ArgumentException($"A {kind} resource name is required.",
                nameof(resourceName));
        PdfContentInstruction[] source = instructions.ToArray();
        int[] placements = [.. source.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Operator == operation
                && item.instruction.Operands is [PdfName name]
                && name.Equals(resourceName))
            .Select(item => item.index)];
        int[] requested = occurrenceIndexes.ToArray();
        if (requested.Any(index => index < 0 || index >= placements.Length)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                $"Selected {kind} occurrence indexes must be valid and unique.",
                nameof(occurrenceIndexes));
        selected = requested.Select(index => placements[index]).ToHashSet();
        return source;
    }

    /// <summary>Clips a contiguous instruction range to a rectangle without changing surrounding content.</summary>
    public static IReadOnlyList<PdfContentInstruction> ClipRange(
        IEnumerable<PdfContentInstruction> instructions,
        int startIndex, int count,
        PdfContentClipRectangle rectangle,
        bool evenOdd = false)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        PdfContentInstruction[] source = instructions.ToArray();
        if (startIndex < 0 || count <= 0 || startIndex > source.Length - count)
            throw new ArgumentOutOfRangeException(nameof(startIndex),
                "The clipped instruction range is outside the stream.");
        var result = new List<PdfContentInstruction>(source.Length + 5);
        result.AddRange(source.Take(startIndex));
        result.Add(new PdfContentInstruction("q", 0, []));
        result.Add(new PdfContentInstruction("re", 0,
            [new PdfReal(rectangle.X), new PdfReal(rectangle.Y),
                new PdfReal(rectangle.Width), new PdfReal(rectangle.Height)]));
        result.Add(new PdfContentInstruction(evenOdd ? "W*" : "W", 0, []));
        result.Add(new PdfContentInstruction("n", 0, []));
        result.AddRange(source.Skip(startIndex).Take(count));
        result.Add(new PdfContentInstruction("Q", 0, []));
        result.AddRange(source.Skip(startIndex + count));
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>
    /// Recolors explicit device-gray, device-RGB, and device-CMYK settings in a selected range.
    /// Pattern, spot-color, color-space selection, transparency, and overprint instructions remain unchanged.
    /// </summary>
    public static IReadOnlyList<PdfContentInstruction> RecolorDeviceRange(
        IEnumerable<PdfContentInstruction> instructions,
        int startIndex, int count,
        PdfDeviceRgbColor color,
        bool fill = true, bool stroke = true)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        PdfContentInstruction[] source = instructions.ToArray();
        if (startIndex < 0 || count <= 0 || startIndex > source.Length - count)
            throw new ArgumentOutOfRangeException(nameof(startIndex),
                "The recolored instruction range is outside the stream.");
        if (!fill && !stroke)
            throw new ArgumentException(
                "At least one of fill or stroke must be selected.", nameof(fill));

        PdfObject[] operands =
        [
            new PdfReal(color.Red),
            new PdfReal(color.Green),
            new PdfReal(color.Blue)
        ];
        var result = source.ToArray();
        for (int index = startIndex; index < startIndex + count; index++)
        {
            string operation = result[index].Operator;
            bool replaceFill = fill && operation is "g" or "rg" or "k";
            bool replaceStroke = stroke && operation is "G" or "RG" or "K";
            if (replaceFill || replaceStroke)
                result[index] = new PdfContentInstruction(
                    replaceFill ? "rg" : "RG", result[index].Offset, operands);
        }
        return Array.AsReadOnly(result);
    }

    /// <summary>Changes existing text font-size settings in a selected instruction range.</summary>
    public static IReadOnlyList<PdfContentInstruction> ResizeTextRange(
        IEnumerable<PdfContentInstruction> instructions,
        int startIndex, int count, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize),
                "Text font size must be finite and positive.");
        PdfContentInstruction[] source = instructions.ToArray();
        if (startIndex < 0 || count <= 0 || startIndex > source.Length - count)
            throw new ArgumentOutOfRangeException(nameof(startIndex),
                "The resized text instruction range is outside the stream.");

        var result = source.ToArray();
        for (int index = startIndex; index < startIndex + count; index++)
        {
            PdfContentInstruction instruction = result[index];
            if (instruction.Operator != "Tf") continue;
            if (instruction.Operands.Count != 2 || instruction.Operands[0] is not PdfName)
                throw new FormatException("A text font instruction has invalid operands.");
            result[index] = new PdfContentInstruction("Tf", instruction.Offset,
                [instruction.Operands[0], new PdfReal(fontSize)]);
        }
        return Array.AsReadOnly(result);
    }

    /// <summary>Changes existing text font resource selections in a selected instruction range.</summary>
    public static IReadOnlyList<PdfContentInstruction> SubstituteFontRange(
        IEnumerable<PdfContentInstruction> instructions,
        int startIndex, int count, PdfName fontResource)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(fontResource);
        if (fontResource.Bytes.IsEmpty)
            throw new ArgumentException("A font resource name is required.", nameof(fontResource));
        PdfContentInstruction[] source = instructions.ToArray();
        if (startIndex < 0 || count <= 0 || startIndex > source.Length - count)
            throw new ArgumentOutOfRangeException(nameof(startIndex),
                "The substituted text instruction range is outside the stream.");

        var result = source.ToArray();
        for (int index = startIndex; index < startIndex + count; index++)
        {
            PdfContentInstruction instruction = result[index];
            if (instruction.Operator != "Tf") continue;
            if (instruction.Operands.Count != 2 || instruction.Operands[0] is not PdfName
                || instruction.Operands[1] is not PdfInteger and not PdfReal)
                throw new FormatException("A text font instruction has invalid operands.");
            result[index] = new PdfContentInstruction("Tf", instruction.Offset,
                [fontResource, instruction.Operands[1]]);
        }
        return Array.AsReadOnly(result);
    }

    private static IReadOnlyList<(int Start, int End)> TextObjectRanges(
        IReadOnlyList<PdfContentInstruction> source)
    {
        var ranges = new List<(int Start, int End)>();
        int start = -1;
        for (int index = 0; index < source.Count; index++)
        {
            if (source[index].Operator == "BT")
            {
                if (start >= 0)
                    throw new FormatException("A content stream contains nested text objects.");
                start = index;
            }
            else if (source[index].Operator == "ET")
            {
                if (start < 0)
                    throw new FormatException("A content stream closes a text object that was not opened.");
                ranges.Add((start, index));
                start = -1;
            }
        }
        if (start >= 0)
            throw new FormatException("A content stream contains an unclosed text object.");
        return ranges;
    }
}
