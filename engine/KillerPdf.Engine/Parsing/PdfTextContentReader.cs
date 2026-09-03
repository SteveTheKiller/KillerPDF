using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Parsing;

/// <summary>Interprets horizontal text placement using explicitly resolved extraction fonts.</summary>
/// <remarks>
/// Input must be decoded page content. Nested forms, external graphics states, inline images,
/// and vertical fonts require further resource resolution and are not supported here yet.
/// Results preserve content order; reading-order grouping and glyph outlines are separate work.
/// </remarks>
public static class PdfTextContentReader
{
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        internal static Matrix Identity => new(1, 0, 0, 1, 0, 0);
        internal PdfPoint Point(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
        internal Matrix Then(Matrix m) => new(
            m.A * A + m.C * B, m.B * A + m.D * B,
            m.A * C + m.C * D, m.B * C + m.D * D,
            m.A * E + m.C * F + m.E, m.B * E + m.D * F + m.F);
        internal Matrix Move(double x, double y) => new Matrix(1, 0, 0, 1, x, y).Then(this);
    }

    private sealed record State(Matrix Ctm, string? Font = null, double Size = 0,
        double CharacterSpacing = 0, double WordSpacing = 0, double Scale = 1,
        double Leading = 0, double Rise = 0);

    /// <summary>Reads character baselines with a bound on output size.</summary>
    public static IReadOnlyList<PdfTextPlacement> Read(ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, PdfExtractionFont> fonts, int maximumCharacters = 1_000_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var result = new List<PdfTextPlacement>();
        var stack = new Stack<State>();
        var state = new State(Matrix.Identity);
        Matrix text = Matrix.Identity, line = Matrix.Identity;
        bool insideText = false;
        int compatibility = 0;

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
            foreach (var character in font.Unicode.Decode(bytes.Bytes.Span))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Count >= maximumCharacters) throw new FormatException("Extracted character limit exceeded.");
                double width = font.GetWidth(character.Code) / 1000 * state.Size * state.Scale;
                Matrix transform = text.Then(state.Ctm);
                result.Add(new PdfTextPlacement(character.Text, character.Code, state.Font, state.Size,
                    transform.Point(0, state.Rise), transform.Point(width, state.Rise)));
                double spacing = state.CharacterSpacing;
                if (character.ByteLength == 1 && character.Code == 32) spacing += state.WordSpacing;
                text = text.Move(width + spacing * state.Scale, 0);
            }
        }

        foreach (var instruction in PdfContentStreamReader.Read(source, cancellationToken: cancellationToken))
        {
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
                            text = text.Move(-adjustment / 1000 * state.Size * state.Scale, 0);
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
                case "BMC": case "BDC": case "EMC": case "MP": case "DP":
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
        if (insideText || stack.Count != 0 || compatibility != 0) throw new FormatException("Unterminated content state.");
        return result.AsReadOnly();
    }
}
