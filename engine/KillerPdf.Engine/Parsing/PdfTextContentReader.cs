using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Parsing;

/// <summary>Interprets text placement using explicitly resolved extraction fonts.</summary>
/// <remarks>
/// Input must be decoded page content. Nested forms, external graphics states, and inline images
/// require page resource resolution before interpretation. Results preserve content order.
/// </remarks>
public static class PdfTextContentReader
{
    internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        internal static Matrix Identity => new(1, 0, 0, 1, 0, 0);
        internal PdfPoint Point(double x, double y)
        {
            double px = A * x + C * y + E, py = B * x + D * y + F;
            if (!double.IsFinite(px) || !double.IsFinite(py)) throw new FormatException("Nonfinite content coordinates.");
            return new(px, py);
        }
        internal Matrix Then(Matrix m)
        {
            var result = new Matrix(m.A * A + m.C * B, m.B * A + m.D * B,
                m.A * C + m.C * D, m.B * C + m.D * D,
                m.A * E + m.C * F + m.E, m.B * E + m.D * F + m.F);
            if (!double.IsFinite(result.A) || !double.IsFinite(result.B) || !double.IsFinite(result.C) ||
                !double.IsFinite(result.D) || !double.IsFinite(result.E) || !double.IsFinite(result.F))
                throw new FormatException("Nonfinite content transformation.");
            return result;
        }
        internal Matrix Move(double x, double y) => new Matrix(1, 0, 0, 1, x, y).Then(this);
    }

    private sealed record State(Matrix Ctm, string? Font = null, double Size = 0,
        double CharacterSpacing = 0, double WordSpacing = 0, double Scale = 1,
        double Leading = 0, double Rise = 0);

    /// <summary>Reads character baselines with a bound on output size.</summary>
    public static IReadOnlyList<PdfTextPlacement> Read(ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, PdfExtractionFont> fonts, int maximumCharacters = 1_000_000,
        CancellationToken cancellationToken = default)
        => ReadInstructions(PdfContentStreamReader.Read(source, cancellationToken: cancellationToken), fonts,
            maximumCharacters, cancellationToken);

    internal static IReadOnlyList<PdfTextPlacement> ReadInstructions(IEnumerable<PdfContentInstruction> instructions,
        IReadOnlyDictionary<string, PdfExtractionFont> fonts, int maximumCharacters = 1_000_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var result = new List<PdfTextPlacement>();
        var stack = new Stack<State>();
        var marked = new Stack<(int Start, PdfString? Replacement)>();
        var state = new State(Matrix.Identity);
        Matrix text = Matrix.Identity, line = Matrix.Identity;
        bool insideText = false;
        int compatibility = 0;
        long emittedCharacters = 0;

        void AccountText(string value)
        {
            emittedCharacters += value.Length;
            if (emittedCharacters > maximumCharacters) throw new FormatException("Extracted character limit exceeded.");
        }

        void RequireText()
        {
            if (!insideText) throw new FormatException("Text positioning or display requires a text object.");
        }
        void MoveLine(double x, double y)
        {
            RequireText();
            text = line = line.Move(x, y);
        }
        void Show(PdfObject value)
        {
            RequireText();
            if (value is not PdfString bytes) throw new FormatException("Text display requires a string.");
            if (state.Font is null || !fonts.TryGetValue(state.Font, out var font))
                throw new NotSupportedException("Text uses an unresolved font resource.");
            foreach (var character in font.Decode(bytes.Bytes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Count >= maximumCharacters) throw new FormatException("Extracted character limit exceeded.");
                AccountText(character.Text);
                double width = font.GetWidth(character.Code) / 1000 * state.Size * state.Scale;
                var vertical = font.GetVerticalMetrics(character.Code);
                double advanceY = font.IsVertical ? vertical.Advance / 1000 * state.Size : 0;
                double originX = font.IsVertical ? -vertical.OriginX / 1000 * state.Size * state.Scale : 0;
                double originY = font.IsVertical ? -vertical.OriginY / 1000 * state.Size : 0;
                Matrix transform = text.Then(state.Ctm);
                var glyph = font.GetGlyphBounds(character.Code);
                double left = (glyph?.Left ?? 0) / 1000 * state.Size * state.Scale + originX;
                double right = (glyph?.Right ?? font.GetWidth(character.Code)) / 1000 * state.Size * state.Scale + originX;
                double bottom = (glyph?.Bottom ?? font.Descent) / 1000 * state.Size + state.Rise + originY;
                double top = (glyph?.Top ?? font.Ascent) / 1000 * state.Size + state.Rise + originY;
                var corners = new[] { transform.Point(left, bottom), transform.Point(right, bottom),
                    transform.Point(left, top), transform.Point(right, top) };
                var sizeOrigin = transform.Point(0, 0);
                var sizeEnd = transform.Point(0, state.Size);
                double pointSize = Math.Sqrt(Math.Pow(sizeEnd.X - sizeOrigin.X, 2) + Math.Pow(sizeEnd.Y - sizeOrigin.Y, 2));
                if (!double.IsFinite(pointSize)) throw new FormatException("Nonfinite extracted font size.");
                result.Add(new PdfTextPlacement(character.Text, character.Code, state.Font, state.Size,
                    transform.Point(0, state.Rise), transform.Point(font.IsVertical ? 0 : width, state.Rise + advanceY))
                {
                    FontName = font.FontName,
                    PointSize = pointSize,
                    Bounds = new PdfContentBounds(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y))
                });
                double spacing = state.CharacterSpacing;
                if (character.ByteLength == 1 && character.Code == 32) spacing += state.WordSpacing;
                text = font.IsVertical ? text.Move(0, advanceY + spacing)
                    : text.Move(width + spacing * state.Scale, 0);
            }
        }

        foreach (var instruction in instructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = instruction.Operands;
            void Arity(int count)
            {
                if (args.Count != count) throw new FormatException($"Invalid operand count for {instruction.Operator}.");
            }
            double Number(int index) => args[index] switch
            {
                PdfInteger integer => integer.Value,
                PdfReal real => real.Value,
                _ => throw new FormatException("Expected a numeric text or graphics operand.")
            };
            switch (instruction.Operator)
            {
                case "q":
                    Arity(0);
                    if (stack.Count >= 256) throw new FormatException("Graphics state nesting limit exceeded.");
                    stack.Push(state); break;
                case "Q":
                    Arity(0);
                    if (!stack.TryPop(out var restored)) throw new FormatException("Unbalanced graphics state restore.");
                    state = restored; break;
                case "cm":
                    Arity(6);
                    state = state with { Ctm = new Matrix(Number(0), Number(1), Number(2), Number(3), Number(4), Number(5)).Then(state.Ctm) }; break;
                case "BT":
                    Arity(0);
                    if (insideText) throw new FormatException("Nested text objects are invalid.");
                    insideText = true; text = line = Matrix.Identity; break;
                case "ET": Arity(0); RequireText(); insideText = false; break;
                case "Tf":
                    Arity(2);
                    if (args[0] is not PdfName name) throw new FormatException("Font resource must be a name.");
                    state = state with { Font = name.ValueAsLatin1(), Size = Number(1) }; break;
                case "Tc": Arity(1); state = state with { CharacterSpacing = Number(0) }; break;
                case "Tw": Arity(1); state = state with { WordSpacing = Number(0) }; break;
                case "Tz": Arity(1); state = state with { Scale = Number(0) / 100 }; break;
                case "TL": Arity(1); state = state with { Leading = Number(0) }; break;
                case "Ts": Arity(1); state = state with { Rise = Number(0) }; break;
                case "Tr":
                    Arity(1);
                    if (args[0] is not PdfInteger mode || mode.Value is < 0 or > 7) throw new FormatException("Invalid text rendering mode.");
                    break;
                case "Tm":
                    Arity(6); RequireText();
                    text = line = new Matrix(Number(0), Number(1), Number(2), Number(3), Number(4), Number(5)); break;
                case "Td": Arity(2); MoveLine(Number(0), Number(1)); break;
                case "TD": Arity(2); state = state with { Leading = -Number(1) }; MoveLine(Number(0), Number(1)); break;
                case "T*": Arity(0); MoveLine(0, -state.Leading); break;
                case "Tj": Arity(1); Show(args[0]); break;
                case "'": Arity(1); MoveLine(0, -state.Leading); Show(args[0]); break;
                case "\"":
                    Arity(3); state = state with { WordSpacing = Number(0), CharacterSpacing = Number(1) };
                    MoveLine(0, -state.Leading); Show(args[2]); break;
                case "TJ":
                    Arity(1); RequireText();
                    if (args[0] is not PdfArray array) throw new FormatException("TJ requires an array.");
                    foreach (var item in array)
                    {
                        if (item is PdfString) Show(item);
                        else
                        {
                            double adjustment = item switch { PdfInteger n => n.Value, PdfReal n => n.Value,
                                _ => throw new FormatException("Invalid TJ array item.") };
                            bool vertical = state.Font is not null && fonts.TryGetValue(state.Font, out var currentFont) && currentFont.IsVertical;
                            text = vertical ? text.Move(0, -adjustment / 1000 * state.Size)
                                : text.Move(-adjustment / 1000 * state.Size * state.Scale, 0);
                        }
                    }
                    break;
                case "Do": case "gs":
                    throw new NotSupportedException("External graphics and form resources are not resolved yet.");
                case "BX": Arity(0); compatibility++; break;
                case "EX":
                    Arity(0);
                    if (compatibility == 0) throw new FormatException("Unbalanced compatibility section.");
                    compatibility--; break;
                case "BMC": case "BDC":
                    Arity(instruction.Operator == "BMC" ? 1 : 2);
                    if (marked.Count >= 256) throw new FormatException("Marked content nesting limit exceeded.");
                    PdfString? replacement = null;
                    if (args.Count == 2 && args[1] is PdfDictionary properties &&
                        properties.TryGetValue(new PdfName("ActualText"u8), out var actual) && actual is PdfString actualText)
                        replacement = actualText;
                    marked.Push((result.Count, replacement));
                    break;
                case "EMC":
                    Arity(0);
                    if (!marked.TryPop(out var section)) throw new FormatException("Unbalanced marked content.");
                    if (section.Replacement is not null && !marked.Any(m => m.Replacement is not null) && result.Count > section.Start)
                    {
                        if (section.Replacement.Bytes.Length > 4L * (maximumCharacters - emittedCharacters) + 3)
                            throw new FormatException("ActualText exceeds the extracted character limit.");
                        var replacementText = PdfUnicodeEncoding.DecodeTextString(section.Replacement.Bytes.Span, "Marked content ActualText");
                        AccountText(replacementText);
                        var first = result[section.Start];
                        var last = result[^1];
                        var bounds = PdfContentBounds.Union(result.Skip(section.Start).Select(p => p.Bounds));
                        result.RemoveRange(section.Start, result.Count - section.Start);
                        if (replacementText.Length > 0)
                            result.Add(first with { Text = replacementText, Bounds = bounds, AdvanceEnd = last.AdvanceEnd });
                    }
                    break;
                case "MP": case "DP":
                case "m": case "l": case "c": case "v": case "y": case "h": case "re":
                case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*": case "n":
                case "W": case "W*": case "w": case "J": case "j": case "M": case "d": case "ri": case "i":
                case "CS": case "cs": case "SC": case "SCN": case "sc": case "scn":
                case "G": case "g": case "RG": case "rg": case "K": case "k": case "sh":
                    break;
                default:
                    if (compatibility == 0) throw new NotSupportedException($"Unsupported content operator {instruction.Operator}.");
                    break;
            }
        }
        if (insideText || stack.Count != 0 || compatibility != 0 || marked.Count != 0) throw new FormatException("Unterminated content state.");
        return result.AsReadOnly();
    }
}
