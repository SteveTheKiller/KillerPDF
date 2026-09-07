using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private PdfStream? RequestedFieldAppearance(PdfDictionary widget, PdfStream? saved,
        PdfDictionary pageResources, ISet<string> diagnostics, CancellationToken cancellationToken)
    {
        PdfStream? originalSaved = saved;
        if (!_tree.Catalog.TryGetValue(Name("AcroForm"), out PdfObject? formValue)
            || Resolve(formValue) is not PdfDictionary form
            || !form.TryGetValue(Name("NeedAppearances"), out PdfObject? needValue)
            || Resolve(needValue) is not PdfBoolean { Value: true }) return saved;

        var inherited = new Dictionary<PdfName, PdfObject>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        PdfDictionary? node = widget;
        while (node is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(node) || visited.Count > 256)
                return Unsupported("cyclic or excessive field inheritance");
            foreach (var entry in node) inherited.TryAdd(entry.Key, entry.Value);
            node = node.TryGetValue(Name("Parent"), out PdfObject? parent)
                ? Resolve(parent) as PdfDictionary : null;
        }
        PdfObject? Field(string key) => inherited.TryGetValue(Name(key), out PdfObject? value)
            ? Resolve(value) : null;
        string? kind = Field("FT") is PdfName fieldType ? fieldType.ToString() : null;
        if (kind is not "/Tx" and not "/Ch") return saved;
        long flags = Field("Ff") is PdfInteger flagValue ? flagValue.Value : 0;
        if ((flags & (1L << 12)) != 0
            || (kind == "/Ch" && (flags & (1L << 17)) == 0))
            return Unsupported("multiline or list-box layout");
        int combCells = 0;
        if (kind == "/Tx" && (flags & (1L << 24)) != 0)
        {
            if (Field("MaxLen") is not PdfInteger { Value: > 0 and <= 32768 } maximumLength)
                return Unsupported("missing, invalid, or excessive comb cell count");
            combCells = (int)maximumLength.Value;
        }
        if (saved is null)
        {
            if (widget.TryGetValue(Name("F"), out PdfObject? visibility)
                && Resolve(visibility) is PdfInteger annotationFlags
                && (annotationFlags.Value & 35) != 0) return null;
            saved = MissingFieldAppearance(widget);
            if (saved is null) return Unsupported("missing or unsupported appearance geometry");
        }
        if (Field("V") is not PdfString textValue) return saved;
        string text = PdfUnicodeEncoding.DecodeTextString(textValue.Bytes.Span, "A form field value");
        if (text.Length > 32768) return Unsupported("field text limit");
        if (kind == "/Ch" && Field("Opt") is PdfArray choices)
        {
            foreach (PdfObject optionValue in choices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Resolve(optionValue) is PdfArray { Count: 2 } pair
                    && Resolve(pair[0]) is PdfString export
                    && Resolve(pair[1]) is PdfString display
                    && PdfUnicodeEncoding.DecodeTextString(export.Bytes.Span, "A choice export") == text)
                {
                    text = PdfUnicodeEncoding.DecodeTextString(display.Bytes.Span, "A choice label");
                    break;
                }
            }
        }
        if (text.Length > 32768) return Unsupported("field text limit");
        if ((flags & (1L << 13)) != 0) text = new string('*', text.EnumerateRunes().Count());
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length == 0 && originalSaved is null)
        {
            diagnostics.Add("A requested empty form-field appearance was regenerated.");
            return saved;
        }
        PdfObject? defaultAppearance = Field("DA");
        if (defaultAppearance is null && form.TryGetValue(Name("DA"), out PdfObject? formAppearance))
            defaultAppearance = Resolve(formAppearance);
        if (defaultAppearance is not PdfString da) return Unsupported("missing default appearance");
        if (da.Bytes.Length > 65536) return Unsupported("default appearance limit");
        IReadOnlyList<PdfContentInstruction> defaultInstructions = PdfContentStreamReader.Read(
            da.Bytes, cancellationToken: cancellationToken);
        PdfContentInstruction? fontInstruction = defaultInstructions.LastOrDefault(item => item.Operator == "Tf");
        if (fontInstruction is null || fontInstruction.Operands.Count != 2
            || fontInstruction.Operands[0] is not PdfName fontName)
            return Unsupported("missing default font");
        double size = Number(fontInstruction.Operands[1]);
        if (size < 0) return Unsupported("negative default font size");

        var resourceEntries = new Dictionary<PdfName, PdfObject>();
        if (saved.Dictionary.TryGetValue(Name("Resources"), out PdfObject? savedResources)
            && Resolve(savedResources) is PdfDictionary existingResources)
            foreach (var entry in existingResources) resourceEntries[entry.Key] = entry.Value;
        else if (!saved.Dictionary.ContainsKey(Name("Resources")))
            foreach (var entry in pageResources) resourceEntries[entry.Key] = entry.Value;
        var fonts = new Dictionary<PdfName, PdfObject>();
        if (resourceEntries.TryGetValue(Name("Font"), out PdfObject? existingFonts)
            && Resolve(existingFonts) is PdfDictionary existingFontDictionary)
            foreach (var entry in existingFontDictionary) fonts[entry.Key] = entry.Value;
        PdfObject? fontValue = null;
        if (form.TryGetValue(Name("DR"), out PdfObject? defaultResources)
            && Resolve(defaultResources) is PdfDictionary resources
            && resources.TryGetValue(Name("Font"), out PdfObject? defaultFonts)
            && Resolve(defaultFonts) is PdfDictionary defaultFontDictionary)
            defaultFontDictionary.TryGetValue(fontName, out fontValue);
        if (fontValue is null) fonts.TryGetValue(fontName, out fontValue);
        if (fontValue is null || Resolve(fontValue) is not PdfDictionary font)
            return Unsupported("unresolved default font");
        int fontIndex = 0;
        do { fontName = Name($"KpFieldFont{fontIndex++}"); } while (fonts.ContainsKey(fontName));
        fonts[fontName] = fontValue;
        if (IsName(font, "Subtype", "Type0")) return Unsupported("composite-font text encoding");
        var extraction = ReadFont(font);
        var encoding = new Dictionary<string, byte>(StringComparer.Ordinal);
        for (int code = 0; code < 256; code++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = extraction.Decode(new byte[] { (byte)code });
            if (decoded.Count == 1 && decoded[0].Text.Length > 0 && decoded[0].Text != "\uFFFD")
                encoding.TryAdd(decoded[0].Text, (byte)code);
        }
        var encoded = new List<byte>(text.Length);
        double advance = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (combCells > 0 && encoded.Count == combCells)
            {
                diagnostics.Add("A comb field value exceeded MaxLen; only the declared cells were rendered.");
                break;
            }
            if (!encoding.TryGetValue(rune.ToString(), out byte code))
                return Unsupported("unmapped field character");
            encoded.Add(code);
            advance += extraction.GetWidth(code) / 1000;
        }
        if (!saved.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boundsValue))
            return Unsupported("missing saved appearance bounds");
        PdfArray bounds = ResolveArray(boundsValue, 4, "Field appearance bounds");
        double left = Number(Resolve(bounds[0])), bottom = Number(Resolve(bounds[1]));
        double width = Number(Resolve(bounds[2])) - left, height = Number(Resolve(bounds[3])) - bottom;
        PdfDictionary? border = widget.TryGetValue(Name("BS"), out PdfObject? borderValue)
            ? Resolve(borderValue) as PdfDictionary : null;
        double inset = border is not null && border.TryGetValue(Name("W"), out PdfObject? borderWidth)
            ? Number(Resolve(borderWidth)) : 1;
        if (inset < 0) return Unsupported("negative border width");
        if (border is not null && NameValue(border, "S") is "B" or "I") inset *= 2;
        double interiorWidth = width - 2 * inset, interiorHeight = height - 2 * inset;
        if (interiorWidth <= 0 || interiorHeight <= 0) return Unsupported("empty field interior");
        if (size == 0) size = Math.Min(interiorHeight,
            combCells > 0 && encoded.Count > 0
                ? interiorWidth / combCells / Math.Max(0.001, encoded.Max(code => extraction.GetWidth(code) / 1000))
                : advance > 0 ? interiorWidth / advance : interiorHeight);
        int alignment = Field("Q") is PdfInteger q ? (int)q.Value : 0;
        double x = alignment switch
        {
            1 => Math.Max(inset, (width - advance * size) / 2),
            2 => Math.Max(inset, width - advance * size - inset),
            _ => inset
        };
        double y = Math.Max(1, (height - (extraction.Ascent + extraction.Descent) * size / 1000) / 2);
        PdfContentInstruction I(string op, params PdfObject[] values) => new(op, 0, values);
        PdfReal R(double value) => new(value);
        var replacement = new List<PdfContentInstruction>
        {
            I("q"), I("re", R(left + inset), R(bottom + inset), R(interiorWidth), R(interiorHeight)),
            I("W"), I("n"), I("BT")
        };
        // Only text and color defaults belong inside the regenerated text object.
        replacement.AddRange(defaultInstructions.Where(item => item.Operator is
            "g" or "rg" or "k" or "Tc" or "Tw" or "Tz" or "TL" or "Tr" or "Ts"));
        replacement.Add(I("Tf", fontName, R(size)));
        if (combCells > 0)
        {
            double cellWidth = interiorWidth / combCells;
            int firstCell = alignment switch
            {
                1 => (combCells - encoded.Count) / 2,
                2 => combCells - encoded.Count,
                _ => 0
            };
            for (int index = 0; index < encoded.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte code = encoded[index];
                double glyphWidth = extraction.GetWidth(code) * size / 1000;
                double cellX = left + inset + (firstCell + index + 0.5) * cellWidth - glyphWidth / 2;
                replacement.Add(I("Tm", R(1), R(0), R(0), R(1), R(cellX), R(bottom + y)));
                replacement.Add(I("Tj", new PdfString(new byte[] { code }, PdfStringForm.Hexadecimal)));
            }
        }
        else
        {
            replacement.Add(I("Tm", R(1), R(0), R(0), R(1), R(left + x), R(bottom + y)));
            replacement.Add(I("Tj", new PdfString(encoded.ToArray(), PdfStringForm.Hexadecimal)));
        }
        replacement.Add(I("ET"));
        replacement.Add(I("Q"));
        IReadOnlyList<PdfContentInstruction> original = ReadStreamInstructions(saved, cancellationToken);
        var output = new List<PdfContentInstruction>();
        int markedDepth = 0;
        bool replaced = false;
        foreach (PdfContentInstruction instruction in original)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool begins = instruction.Operator is "BMC" or "BDC";
            if (markedDepth > 0)
            {
                if (begins) markedDepth++;
                if (instruction.Operator == "EMC") markedDepth--;
                continue;
            }
            if (begins && instruction.Operands.Count > 0
                && instruction.Operands[0] is PdfName tag && tag.Equals(Name("Tx")))
            {
                if (!replaced) output.AddRange(replacement);
                replaced = true;
                markedDepth = 1;
            }
            else output.Add(instruction);
        }
        if (!replaced || markedDepth != 0) return Unsupported("missing or unbalanced text appearance section");
        resourceEntries[Name("Font")] = new PdfDictionary(fonts);
        var dictionary = new Dictionary<PdfName, PdfObject>(saved.Dictionary);
        dictionary.Remove(Name("Filter"));
        dictionary.Remove(Name("DecodeParms"));
        dictionary.Remove(Name("Length"));
        dictionary[Name("Resources")] = new PdfDictionary(resourceEntries);
        diagnostics.Add("A requested form-field text appearance was regenerated.");
        return new PdfStream(new PdfDictionary(dictionary), PdfContentStreamWriter.Write(output));

        PdfStream? Unsupported(string reason)
        {
            diagnostics.Add($"Requested form-field appearance regeneration is not implemented for {reason}.");
            return originalSaved;
        }
    }

    private PdfStream? MissingFieldAppearance(PdfDictionary widget)
    {
        if (!widget.TryGetValue(Name("Rect"), out PdfObject? rectangleValue)) return null;
        PdfArray rectangle = ResolveArray(rectangleValue, 4, "Widget rectangle");
        double width = Number(Resolve(rectangle[2])) - Number(Resolve(rectangle[0]));
        double height = Number(Resolve(rectangle[3])) - Number(Resolve(rectangle[1]));
        if (width <= 0 || height <= 0) return null;
        PdfDictionary? characteristics = widget.TryGetValue(Name("MK"), out PdfObject? mk)
            ? Resolve(mk) as PdfDictionary : null;
        long rotation = characteristics is not null
            && characteristics.TryGetValue(Name("R"), out PdfObject? rotationValue)
            && Resolve(rotationValue) is PdfInteger angle ? angle.Value % 360 : 0;
        rotation = (rotation + 360) % 360;
        if (rotation % 90 != 0) return null;
        double fieldWidth = rotation is 90 or 270 ? height : width;
        double fieldHeight = rotation is 90 or 270 ? width : height;
        PdfReal R(double value) => new(value);
        PdfArray A(params double[] values) => new(values.Select(value => (PdfObject)R(value)));
        PdfContentInstruction I(string op, params PdfObject[] values) => new(op, 0, values);
        var instructions = new List<PdfContentInstruction>();
        PdfArray? ColorArray(string key) => characteristics is not null
            && characteristics.TryGetValue(Name(key), out PdfObject? color)
            ? Resolve(color) as PdfArray : null;
        PdfContentInstruction? ColorInstruction(PdfArray? color, bool stroke)
        {
            if (color is null || color.Count == 0) return null;
            string op = color.Count switch
            {
                1 => stroke ? "G" : "g",
                3 => stroke ? "RG" : "rg",
                4 => stroke ? "K" : "k",
                _ => throw new FormatException("Widget appearance color is invalid.")
            };
            return new PdfContentInstruction(op, 0,
                color.Select(value => (PdfObject)R(Number(Resolve(value)))));
        }
        if (ColorInstruction(ColorArray("BG"), false) is PdfContentInstruction background)
        {
            instructions.Add(I("q"));
            instructions.Add(background);
            instructions.Add(I("re", R(0), R(0), R(fieldWidth), R(fieldHeight)));
            instructions.Add(I("f"));
            instructions.Add(I("Q"));
        }
        if (ColorInstruction(ColorArray("BC"), true) is PdfContentInstruction borderColor)
        {
            PdfDictionary? border = widget.TryGetValue(Name("BS"), out PdfObject? bs)
                ? Resolve(bs) as PdfDictionary : null;
            double borderWidth = border is not null && border.TryGetValue(Name("W"), out PdfObject? bw)
                ? Number(Resolve(bw)) : 1;
            string style = border is null ? "S" : NameValue(border, "S") ?? "S";
            if (borderWidth < 0 || style is not "S" and not "D" and not "U") return null;
            if (borderWidth > 0)
            {
                instructions.Add(I("q"));
                instructions.Add(borderColor);
                instructions.Add(I("w", R(borderWidth)));
                if (style == "D")
                {
                    PdfObject pattern = border is not null
                        && border.TryGetValue(Name("D"), out PdfObject? dash) ? Resolve(dash) : A(3);
                    instructions.Add(I("d", pattern, R(0)));
                }
                double inset = borderWidth / 2;
                if (style == "U")
                {
                    instructions.Add(I("m", R(0), R(inset)));
                    instructions.Add(I("l", R(fieldWidth), R(inset)));
                }
                else
                    instructions.Add(I("re", R(inset), R(inset),
                        R(Math.Max(0, fieldWidth - borderWidth)), R(Math.Max(0, fieldHeight - borderWidth))));
                instructions.Add(I("S"));
                instructions.Add(I("Q"));
            }
        }
        instructions.Add(I("BMC", Name("Tx")));
        instructions.Add(I("EMC"));
        PdfArray matrix = rotation switch
        {
            90 => A(0, 1, -1, 0, width, 0),
            180 => A(-1, 0, 0, -1, width, height),
            270 => A(0, -1, 1, 0, 0, height),
            _ => A(1, 0, 0, 1, 0, 0)
        };
        return new PdfStream(new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [Name("Subtype")] = Name("Form"),
            [Name("BBox")] = A(0, 0, fieldWidth, fieldHeight),
            [Name("Matrix")] = matrix
        }), PdfContentStreamWriter.Write(instructions));
    }
}
