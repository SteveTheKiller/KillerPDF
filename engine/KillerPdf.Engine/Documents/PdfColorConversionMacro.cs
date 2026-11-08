using System.Globalization;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Supported color modes for editable PDF macro output.</summary>
public enum PdfMacroColorMode
{
    /// <summary>Convert colors to grayscale.</summary>
    Grayscale,
    /// <summary>Convert colors to black or white using a threshold.</summary>
    BlackAndWhite
}

/// <summary>Color settings that retain PDF text and interactive fields.</summary>
public sealed record PdfMacroColorOptions(PdfMacroColorMode Mode, int Threshold = 160);

/// <summary>Converts PDF colors without replacing pages with images.</summary>
public static partial class PdfColorConversionMacro
{
    /// <summary>Creates a typed color conversion step.</summary>
    public static PdfMacroStep ConvertStep(PdfMacroColorMode mode, int threshold = 160)
    {
        Validate(new(mode, threshold));
        return new(PdfMacroOperation.ConvertColor,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>
            {
                ["mode"] = mode.ToString(),
                ["threshold"] = threshold.ToString(CultureInfo.InvariantCulture)
            }));
    }

    /// <summary>Reads and validates one color conversion step.</summary>
    public static PdfMacroColorOptions ReadOptions(PdfMacroStep step)
    {
        if (step.Operation != PdfMacroOperation.ConvertColor || step.Settings is not { Count: 2 }
            || !step.Settings.TryGetValue("mode", out string? modeText)
            || !Enum.TryParse(modeText, out PdfMacroColorMode mode)
            || !step.Settings.TryGetValue("threshold", out string? thresholdText)
            || !int.TryParse(thresholdText, NumberStyles.None, CultureInfo.InvariantCulture, out int threshold))
            throw new ArgumentException("Color conversion requires a mode and threshold.", nameof(step));
        var options = new PdfMacroColorOptions(mode, threshold);
        Validate(options);
        return options;
    }

    /// <summary>Converts supported page colors while retaining PDF content.</summary>
    public static PdfMacroOperationResult Execute(PdfMacroStep step, ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default) =>
        Execute(step, source, values, null, cancellationToken);

    /// <summary>
    /// Converts supported page colors while retaining PDF content, reading untagged CMYK through the
    /// chosen working CMYK profile, or the built-in one when it is null.
    /// </summary>
    public static PdfMacroOperationResult Execute(PdfMacroStep step, ReadOnlyMemory<byte> source,
        IReadOnlyDictionary<string, string> values, KillerPdf.Engine.ColorManagement.PdfColorProfile? workingCmykProfile,
        CancellationToken cancellationToken = default)
    {
        if (workingCmykProfile is null) return Convert(step, source, cancellationToken);
        using var working = KillerPdf.Engine.Rendering.PdfCmykDisplayTable.Use(workingCmykProfile);
        return Convert(step, source, cancellationToken);
    }

    private static PdfMacroOperationResult Convert(PdfMacroStep step, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfMacroColorOptions options = ReadOptions(step);
        PdfDocument document = PdfDocument.Open(source);
        var reader = new PdfPageContentReader(document);
        var tree = PdfPageTree.Read(document);
        var editor = new PdfIncrementalPageEditor(document);
        for (int page = 0; page < reader.PageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            editor.SetPageContent(page, PdfContentStreamWriter.Write(ConvertContent(
                reader.ReadInstructions(page, cancellationToken), options, cancellationToken,
                colorSpaces: ReadDeviceColorSpaces(document, tree.Pages[page].InheritedValues.GetValueOrDefault(Name("Resources"))))));
        }
        byte[] output = editor.Build();
        cancellationToken.ThrowIfCancellationRequested();
        return new(ConvertAppearances(PdfDocument.Open(output), options, cancellationToken));
    }

    private static IReadOnlyList<PdfContentInstruction> ConvertContent(
        IReadOnlyList<PdfContentInstruction> instructions, PdfMacroColorOptions options, CancellationToken token,
        bool inheritColorSpace = false, IReadOnlyDictionary<string, int>? colorSpaces = null)
    {
        var output = new List<PdfContentInstruction>(instructions.Count);
        if (!inheritColorSpace && options.Mode == PdfMacroColorMode.BlackAndWhite && options.Threshold == 0)
        {
            output.Add(new PdfContentInstruction("g", 0, [new PdfInteger(1)]));
            output.Add(new PdfContentInstruction("G", 0, [new PdfInteger(1)]));
        }
        int fillComponents = inheritColorSpace ? 0 : 1, strokeComponents = inheritColorSpace ? 0 : 1;
        var states = new Stack<(int Fill, int Stroke)>();
        foreach (PdfContentInstruction instruction in instructions)
        {
            token.ThrowIfCancellationRequested();
            if (instruction.Operator == "q")
            {
                if (states.Count >= 1024) throw new NotSupportedException("The graphics-state stack is too deeply nested.");
                states.Push((fillComponents, strokeComponents));
            }
            else if (instruction.Operator == "Q")
            {
                if (states.Count == 0) throw new InvalidOperationException("The graphics-state stack is unbalanced.");
                (fillComponents, strokeComponents) = states.Pop();
            }
            if (instruction.Operator is "cs" or "CS")
            {
                if (instruction.Operands.Count != 1 || instruction.Operands[0] is not PdfName colorSpace)
                    throw new InvalidOperationException("A color-space selection is invalid.");
                int selected = colorSpace.ValueAsLatin1() switch
                {
                    "DeviceGray" => 1, "DeviceRGB" => 3, "DeviceCMYK" => 4,
                    string alias when colorSpaces is not null && colorSpaces.TryGetValue(alias, out int count) => count,
                    _ => throw new NotSupportedException("The selected color space is not yet supported for editable conversion.")
                };
                if (instruction.Operator == "CS") strokeComponents = selected;
                else fillComponents = selected;
                byte initial = selected == 4 ? CmykGray(0, 0, 0, 255) : (byte)0;
                if (options.Mode == PdfMacroColorMode.BlackAndWhite) initial = initial >= options.Threshold ? (byte)255 : (byte)0;
                output.Add(new PdfContentInstruction(instruction.Operator == "CS" ? "G" : "g",
                    instruction.Offset, [new PdfReal(initial / 255d)]));
                continue;
            }
            int components = instruction.Operator switch { "rg" or "RG" => 3, "g" or "G" => 1, "k" or "K" => 4, _ => 0 };
            bool stroke = char.IsUpper(instruction.Operator[0]);
            if (instruction.Operator is "sc" or "scn" or "SC" or "SCN")
            {
                components = stroke ? strokeComponents : fillComponents;
                if (components == 0) components = instruction.Operands.Count;
                if (components is not (1 or 3 or 4) || instruction.Operands.Any(value => value is PdfName))
                    throw new NotSupportedException("Pattern and non-device colors cannot yet be converted without changing their appearance.");
            }
            if (components == 0)
            {
                if (instruction.Operator is "sh" or "BI")
                    throw new NotSupportedException($"Color conversion does not yet preserve '{instruction.Operator}' content.");
                output.Add(instruction);
                continue;
            }
            if (instruction.Operands.Count != components)
                throw new InvalidOperationException("A color operator has the wrong number of components.");
            if (stroke) strokeComponents = components;
            else fillComponents = components;
            byte gray = components switch
            {
                1 => Byte(Number(instruction.Operands[0])),
                3 => Gray(Byte(Number(instruction.Operands[0])), Byte(Number(instruction.Operands[1])), Byte(Number(instruction.Operands[2]))),
                _ => CmykGray(Byte(Number(instruction.Operands[0])), Byte(Number(instruction.Operands[1])),
                    Byte(Number(instruction.Operands[2])), Byte(Number(instruction.Operands[3])))
            };
            if (options.Mode == PdfMacroColorMode.BlackAndWhite) gray = gray >= options.Threshold ? (byte)255 : (byte)0;
            output.Add(new PdfContentInstruction(stroke ? "G" : "g",
                instruction.Offset, [new PdfReal(gray / 255d)]));
        }
        return output.AsReadOnly();
    }

    // ISO 32000 DeviceRGB to DeviceGray weights 0.3, 0.59 and 0.11, rounded half up.
    private static byte Gray(byte red, byte green, byte blue) => (byte)((red * 30 + green * 59 + blue * 11 + 50) / 100);
    private static IReadOnlyDictionary<string, int> ReadDeviceColorSpaces(PdfDocument document, PdfObject? resources)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Resolve(resources) is PdfDictionary dictionary
            && Resolve(dictionary.GetValueOrDefault(Name("ColorSpace"))) is PdfDictionary spaces)
            foreach ((PdfName name, PdfObject value) in spaces)
                if (Resolve(value) is PdfName device)
                {
                    int count = device.ValueAsLatin1() switch { "DeviceGray" => 1, "DeviceRGB" => 3, "DeviceCMYK" => 4, _ => 0 };
                    if (count != 0) result.Add(name.ValueAsLatin1(), count);
                }
        return result;

        PdfObject? Resolve(PdfObject? value)
        {
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 64) throw new InvalidOperationException("A color resource reference chain is too deeply nested.");
                value = document.Resolve(reference);
            }
            return value;
        }
    }
    private static byte CmykGray(byte cyan, byte magenta, byte yellow, byte black)
    {
        // The working CMYK profile's display color, the one every view shows for this ink.
        uint rgb = KillerPdf.Engine.Rendering.PdfDeviceCmyk.ToRgb(cyan | (uint)magenta << 8 | (uint)yellow << 16 | (uint)black << 24);
        return Gray((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new InvalidOperationException("A color component is not numeric.")
    };

    private static void Validate(PdfMacroColorOptions options)
    {
        if (!Enum.IsDefined(options.Mode) || options.Threshold is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(options), "Color mode and threshold must be valid.");
    }
}
