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
        if ((flags & ((1L << 12) | (1L << 24))) != 0
            || (kind == "/Ch" && (flags & (1L << 17)) == 0))
            return Unsupported("multiline, comb, or list-box layout");
        if (saved is null) return Unsupported("missing saved appearance geometry");
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
        if (width <= 4 || height <= 2) return Unsupported("empty field interior");
        if (size == 0) size = Math.Min(height - 2, advance > 0 ? (width - 4) / advance : height - 2);
        int alignment = Field("Q") is PdfInteger q ? (int)q.Value : 0;
        double x = alignment switch
        {
            1 => Math.Max(2, (width - advance * size) / 2),
            2 => Math.Max(2, width - advance * size - 2),
            _ => 2
        };
        double y = Math.Max(1, (height - (extraction.Ascent + extraction.Descent) * size / 1000) / 2);
        PdfContentInstruction I(string op, params PdfObject[] values) => new(op, 0, values);
        PdfReal R(double value) => new(value);
        var replacement = new List<PdfContentInstruction>
        {
            I("q"), I("re", R(left + 1), R(bottom + 1), R(width - 2), R(height - 2)),
            I("W"), I("n"), I("BT")
        };
        // Only text and color defaults belong inside the regenerated text object.
        replacement.AddRange(defaultInstructions.Where(item => item.Operator is
            "g" or "rg" or "k" or "Tc" or "Tw" or "Tz" or "TL" or "Tr" or "Ts"));
        replacement.Add(I("Tf", fontName, R(size)));
        replacement.Add(I("Tm", R(1), R(0), R(0), R(1), R(left + x), R(bottom + y)));
        replacement.Add(I("Tj", new PdfString(encoded.ToArray(), PdfStringForm.Hexadecimal)));
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
            return saved;
        }
    }
}
