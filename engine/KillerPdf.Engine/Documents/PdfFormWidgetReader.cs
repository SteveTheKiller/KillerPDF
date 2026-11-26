using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads page widgets with inherited AcroForm field state.</summary>
public sealed class PdfFormWidgetReader
{
    private readonly PdfDocument _document;
    private readonly PdfPageTree _tree;

    /// <summary>Creates a widget reader with one page-tree snapshot for an authenticated document.</summary>
    public PdfFormWidgetReader(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _tree = PdfPageTree.Read(document);
    }

    /// <summary>Reads a page using the reader's captured page tree.</summary>
    public IReadOnlyList<PdfFormWidgetInfo> Read(int pageIndex) => ReadPageCore(_document, _tree, pageIndex);

    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName SubtypeName = Name("Subtype");
    private static readonly PdfName WidgetName = Name("Widget");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName ParentName = Name("Parent");
    private static readonly PdfName FieldTypeName = Name("FT");
    private static readonly PdfName PartialName = Name("T");
    private static readonly PdfName TooltipName = Name("TU");
    private static readonly PdfName MappingName = Name("TM");
    private static readonly PdfName ValueName = Name("V");
    private static readonly PdfName DefaultValueName = Name("DV");
    private static readonly PdfName DefaultAppearanceName = Name("DA");
    private static readonly PdfName AlignmentName = Name("Q");
    private static readonly PdfName FlagsName = Name("Ff");
    private static readonly PdfName AnnotationFlagsName = Name("F");
    private static readonly PdfName MaximumLengthName = Name("MaxLen");
    private static readonly PdfName OptionsName = Name("Opt");
    private static readonly PdfName AppearanceName = Name("AP");
    private static readonly PdfName ActionName = Name("A");
    private static readonly PdfName AppearanceStateName = Name("AS");
    private static readonly PdfName NormalAppearanceName = Name("N");
    private static readonly PdfName AppearanceCharacteristicsName = Name("MK");
    private static readonly PdfName BackgroundColorName = Name("BG");
    private static readonly PdfName BorderColorName = Name("BC");
    private static readonly PdfName BorderStyleName = Name("BS");
    private static readonly PdfName LegacyBorderName = Name("Border");
    private static readonly PdfName CropBoxName = Name("CropBox");
    private static readonly PdfName MediaBoxName = Name("MediaBox");
    private static readonly PdfName RotateName = Name("Rotate");
    private static readonly PdfName DefaultStyleName = Name("DS");
    private static readonly PdfName ResourcesName = Name("Resources");
    private static readonly PdfName FontName = Name("Font");
    private static readonly PdfName BaseFontName = Name("BaseFont");
    private static readonly PdfName DescendantFontsName = Name("DescendantFonts");
    private static readonly PdfName FontDescriptorName = Name("FontDescriptor");
    private static readonly PdfName FontFileName = Name("FontFile");
    private static readonly PdfName FontFile2Name = Name("FontFile2");
    private static readonly PdfName FontFile3Name = Name("FontFile3");

    /// <summary>Reads all valid form widgets on one zero-based page.</summary>
    public static IReadOnlyList<PdfFormWidgetInfo> ReadPage(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ReadPageCore(document, PdfPageTree.Read(document), pageIndex);
    }

    private static IReadOnlyList<PdfFormWidgetInfo> ReadPageCore(PdfDocument document, PdfPageTree tree, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = tree.Pages[pageIndex];
        (double boxLeft, double boxBottom, double boxWidth, double boxHeight) = PageBox(document, page);
        int rotation = PageRotation(document, page);
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotationsValue)) return [];
        PdfArray annotations = Resolve(document, annotationsValue, "A page /Annots value") as PdfArray
            ?? throw new InvalidOperationException("A page /Annots value is not an array.");
        var result = new List<PdfFormWidgetInfo>();
        for (int annotationIndex = 0; annotationIndex < annotations.Count; annotationIndex++)
        {
            PdfObject rawWidget = annotations[annotationIndex];
            if (Resolve(document, rawWidget, "A page annotation") is not PdfDictionary widget
                || !widget.TryGetValue(SubtypeName, out PdfObject? subtypeValue)
                || Resolve(document, subtypeValue, "An annotation subtype") is not PdfName subtype
                || !subtype.Equals(WidgetName)) continue;
            if (!widget.TryGetValue(RectName, out PdfObject? rectangleValue)
                || Resolve(document, rectangleValue, "A widget /Rect value") is not PdfArray rectangle
                || rectangle.Count != 4) continue;
            double x1 = Number(document, rectangle[0], "A widget rectangle coordinate");
            double y1 = Number(document, rectangle[1], "A widget rectangle coordinate");
            double x2 = Number(document, rectangle[2], "A widget rectangle coordinate");
            double y2 = Number(document, rectangle[3], "A widget rectangle coordinate");

            PdfName? fieldType = null;
            string value = string.Empty;
            string defaultValue = string.Empty;
            string tooltip = string.Empty;
            string mappingName = string.Empty;
            IReadOnlyList<string> values = [];
            IReadOnlyList<string> defaultValues = [];
            string defaultAppearance = string.Empty;
            string? defaultStyle = null;
            PdfTextFieldAlignment alignment = PdfTextFieldAlignment.Left;
            bool hasAlignment = false;
            long flags = 0;
            int maximumLength = 0;
            List<PdfFormChoiceInfo> options = [];
            var nameParts = new List<string>();
            PdfDictionary? node = widget;
            var parentReferences = new HashSet<(int, int)>();
            for (int depth = 0; node is not null; depth++)
            {
                if (depth >= 256)
                    throw new InvalidOperationException("An AcroForm widget parent chain is too deep.");
                if (fieldType is null && node.TryGetValue(FieldTypeName, out PdfObject? typeValue))
                    fieldType = Resolve(document, typeValue, "An AcroForm /FT value") as PdfName
                        ?? throw new InvalidOperationException("An AcroForm /FT value is not a name.");
                if (node.TryGetValue(PartialName, out PdfObject? nameValue))
                {
                    PdfString partial = Resolve(document, nameValue, "An AcroForm /T value") as PdfString
                        ?? throw new InvalidOperationException("An AcroForm /T value is not a string.");
                    string decoded = PdfUnicodeEncoding.DecodeTextString(
                        partial.Bytes.Span, "An AcroForm /T value");
                    if (decoded.Length > 0) nameParts.Add(decoded);
                }
                if (tooltip.Length == 0 && node.TryGetValue(TooltipName, out PdfObject? tooltipValue))
                {
                    PdfString text = Resolve(document, tooltipValue, "An AcroForm /TU value") as PdfString
                        ?? throw new InvalidOperationException("An AcroForm /TU value is not a string.");
                    tooltip = PdfUnicodeEncoding.DecodeTextString(
                        text.Bytes.Span, "An AcroForm /TU value");
                }
                if (mappingName.Length == 0
                    && node.TryGetValue(MappingName, out PdfObject? mappingValue))
                {
                    PdfString text = Resolve(document, mappingValue,
                        "An AcroForm /TM value") as PdfString
                        ?? throw new InvalidOperationException(
                            "An AcroForm /TM value is not a string.");
                    mappingName = PdfUnicodeEncoding.DecodeTextString(
                        text.Bytes.Span, "An AcroForm /TM value");
                }
                if (values.Count == 0 && node.TryGetValue(ValueName, out PdfObject? currentValue))
                {
                    values = FieldValues(document, currentValue);
                    value = values.Count > 0 ? values[0] : string.Empty;
                }
                if (defaultValues.Count == 0
                    && node.TryGetValue(DefaultValueName, out PdfObject? resetValue))
                {
                    defaultValues = FieldValues(document, resetValue);
                    defaultValue = defaultValues.Count > 0 ? defaultValues[0] : string.Empty;
                }
                if (defaultAppearance.Length == 0
                    && node.TryGetValue(DefaultAppearanceName, out PdfObject? appearanceValue))
                {
                    PdfString appearance = Resolve(document, appearanceValue,
                        "An AcroForm /DA value") as PdfString
                        ?? throw new InvalidOperationException("An AcroForm /DA value is not a string.");
                    defaultAppearance = PdfUnicodeEncoding.DecodeTextString(
                        appearance.Bytes.Span, "An AcroForm /DA value");
                }
                if (defaultStyle is null && node.TryGetValue(DefaultStyleName, out PdfObject? styleValue)
                    && Resolve(document, styleValue, "An AcroForm /DS value") is PdfString styleText)
                    defaultStyle = PdfUnicodeEncoding.DecodeTextString(styleText.Bytes.Span, "An AcroForm /DS value");
                if (!hasAlignment && node.TryGetValue(AlignmentName, out PdfObject? alignmentValue))
                {
                    long rawAlignment = Integer(document, alignmentValue, "An AcroForm /Q value");
                    if (rawAlignment is < 0 or > 2)
                        throw new InvalidOperationException("An AcroForm /Q value is out of range.");
                    alignment = (PdfTextFieldAlignment)rawAlignment;
                    hasAlignment = true;
                }
                if (flags == 0 && node.TryGetValue(FlagsName, out PdfObject? flagsValue))
                    flags = Integer(document, flagsValue, "An AcroForm /Ff value");
                if (maximumLength == 0
                    && node.TryGetValue(MaximumLengthName, out PdfObject? lengthValue))
                {
                    long length = Integer(document, lengthValue, "An AcroForm /MaxLen value");
                    if (length is < 0 or > int.MaxValue)
                        throw new InvalidOperationException("An AcroForm /MaxLen value is out of range.");
                    maximumLength = (int)length;
                }
                if (options.Count == 0 && node.TryGetValue(OptionsName, out PdfObject? optionsValue))
                    options = ReadOptions(document, optionsValue);
                if (!node.TryGetValue(ParentName, out PdfObject? parentValue)) break;
                if (parentValue is PdfIndirectReference parentReference
                    && !parentReferences.Add((parentReference.ObjectNumber, parentReference.Generation)))
                    throw new InvalidOperationException("An AcroForm widget parent chain contains a cycle.");
                node = Resolve(document, parentValue, "An AcroForm widget /Parent value") as PdfDictionary
                    ?? throw new InvalidOperationException("An AcroForm widget /Parent value is not a dictionary.");
            }
            if (fieldType is null || nameParts.Count == 0) continue;
            nameParts.Reverse();
            (bool underline, bool strikeThrough) = Editing.PdfIncrementalPageEditor.TextDecorationsOf(defaultStyle);
            (int objectNumber, int generation) = rawWidget is PdfIndirectReference reference
                ? (reference.ObjectNumber, reference.Generation) : (0, 0);
            (double borderWidth, PdfFormFieldBorderStyle borderStyle) = WidgetBorder(document, widget);
            result.Add(new PdfFormWidgetInfo
            {
                PageIndex = pageIndex,
                AnnotationIndex = annotationIndex,
                ObjectNumber = objectNumber,
                Generation = generation,
                FieldName = string.Join('.', nameParts),
                Tooltip = tooltip,
                MappingName = mappingName,
                FieldKind = FieldKind(fieldType),
                Flags = flags,
                AnnotationFlags = widget.TryGetValue(AnnotationFlagsName,
                    out PdfObject? annotationFlagsValue)
                    ? Integer(document, annotationFlagsValue, "A widget /F value") : 0,
                Value = value,
                Values = values,
                DefaultValue = defaultValue,
                DefaultValues = defaultValues,
                DefaultAppearance = defaultAppearance,
                AppearanceFontName = EmbeddedAppearanceFont(document, widget),
                Underline = underline,
                StrikeThrough = strikeThrough,
                MarkedTransparent = HasEmptyBackground(document, widget),
                Alignment = alignment,
                BackgroundColor = WidgetColor(
                    document, widget, BackgroundColorName, "background"),
                BorderColor = WidgetColor(
                    document, widget, BorderColorName, "border"),
                BorderWidth = borderWidth,
                BorderStyle = borderStyle,
                MaximumLength = maximumLength,
                OnValue = ButtonOnValue(document, widget),
                HasAction = widget.ContainsKey(ActionName),
                HasAppearanceState = widget.ContainsKey(AppearanceStateName),
                Options = options,
                Left = Math.Min(x1, x2), Bottom = Math.Min(y1, y2),
                Right = Math.Max(x1, x2), Top = Math.Max(y1, y2),
                PageBoxLeft = boxLeft, PageBoxBottom = boxBottom,
                PageBoxWidth = boxWidth, PageBoxHeight = boxHeight,
                PageRotation = rotation
            });
        }
        return result;
    }

    // The first font of the normal appearance's resources that carries its own font program. An
    // appearance that cannot be read is treated as using no embedded font: this is display detail
    // and must never stop a form from being read.
    private static string EmbeddedAppearanceFont(PdfDocument document, PdfDictionary widget)
    {
        try
        {
            if (!widget.TryGetValue(AppearanceName, out PdfObject? appearanceValue)
                || Resolve(document, appearanceValue, "A widget /AP value") is not PdfDictionary appearance
                || !appearance.TryGetValue(NormalAppearanceName, out PdfObject? normalValue)
                || Resolve(document, normalValue, "A widget normal appearance") is not PdfStream normal
                || !normal.Dictionary.TryGetValue(ResourcesName, out PdfObject? resourcesValue)
                || Resolve(document, resourcesValue, "An appearance /Resources value") is not PdfDictionary resources
                || !resources.TryGetValue(FontName, out PdfObject? fontsValue)
                || Resolve(document, fontsValue, "An appearance /Font value") is not PdfDictionary fonts)
                return string.Empty;
            foreach (KeyValuePair<PdfName, PdfObject> entry in fonts)
            {
                if (Resolve(document, entry.Value, "An appearance font") is not PdfDictionary font
                    || !font.TryGetValue(BaseFontName, out PdfObject? baseFontValue)
                    || Resolve(document, baseFontValue, "A font /BaseFont value") is not PdfName baseFont)
                    continue;
                PdfDictionary? described = font;
                if (font.TryGetValue(DescendantFontsName, out PdfObject? descendantsValue))
                    described = Resolve(document, descendantsValue, "A font /DescendantFonts value") is PdfArray { Count: > 0 } descendants
                        ? Resolve(document, descendants[0], "A descendant font") as PdfDictionary
                        : null;
                if (described is null
                    || !described.TryGetValue(FontDescriptorName, out PdfObject? descriptorValue)
                    || Resolve(document, descriptorValue, "A /FontDescriptor value") is not PdfDictionary descriptor
                    || !(descriptor.ContainsKey(FontFile2Name) || descriptor.ContainsKey(FontFile3Name)
                        || descriptor.ContainsKey(FontFileName)))
                    continue;
                string name = baseFont.ValueAsLatin1();
                int plus = name.IndexOf('+');
                return plus >= 0 && plus + 1 < name.Length ? name[(plus + 1)..] : name;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        return string.Empty;
    }

    private static bool HasEmptyBackground(PdfDocument document, PdfDictionary widget)
    {
        try
        {
            return widget.TryGetValue(AppearanceCharacteristicsName, out PdfObject? characteristicsValue)
                && Resolve(document, characteristicsValue, "A widget /MK value") is PdfDictionary characteristics
                && characteristics.TryGetValue(BackgroundColorName, out PdfObject? colorValue)
                && Resolve(document, colorValue, "A widget background color") is PdfArray { Count: 0 };
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    // The widget's /BS width and style, or the width in a legacy /Border array. A value the reader cannot
    // use keeps the PDF default of a 1-point solid border.
    private static (double Width, PdfFormFieldBorderStyle Style) WidgetBorder(PdfDocument document, PdfDictionary widget)
    {
        double width = 1;
        PdfFormFieldBorderStyle style = PdfFormFieldBorderStyle.Solid;
        try
        {
            if (widget.TryGetValue(BorderStyleName, out PdfObject? bsValue)
                && Resolve(document, bsValue, "A widget /BS value") is PdfDictionary bs)
            {
                if (bs.TryGetValue(Name("W"), out PdfObject? widthValue)
                    && Number(document, widthValue, "A widget border width") is var w
                    && double.IsFinite(w) && w >= 0)
                    width = w;
                if (bs.TryGetValue(Name("S"), out PdfObject? styleValue)
                    && Resolve(document, styleValue, "A widget border style") is PdfName name)
                    style = name.ValueAsLatin1() switch
                    {
                        "D" => PdfFormFieldBorderStyle.Dashed,
                        "B" => PdfFormFieldBorderStyle.Beveled,
                        "I" => PdfFormFieldBorderStyle.Inset,
                        "U" => PdfFormFieldBorderStyle.Underline,
                        _ => PdfFormFieldBorderStyle.Solid
                    };
            }
            else if (widget.TryGetValue(LegacyBorderName, out PdfObject? borderValue)
                && Resolve(document, borderValue, "A widget /Border value") is PdfArray { Count: >= 3 } border
                && Number(document, border[2], "A widget border width") is var legacy
                && double.IsFinite(legacy) && legacy >= 0)
                width = legacy;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        return (width, style);
    }

    private static PdfRgbColor? WidgetColor(
        PdfDocument document, PdfDictionary widget, PdfName colorName, string description)
    {
        if (!widget.TryGetValue(AppearanceCharacteristicsName, out PdfObject? characteristicsValue)
            || Resolve(document, characteristicsValue, "A widget /MK value") is not PdfDictionary characteristics
            || !characteristics.TryGetValue(colorName, out PdfObject? colorValue)
            || Resolve(document, colorValue, $"A widget {description} color") is not PdfArray color
            || color.Count is not (1 or 3)) return null;
        double red = Number(document, color[0], $"A widget {description} color component");
        double green = color.Count == 1 ? red
            : Number(document, color[1], $"A widget {description} color component");
        double blue = color.Count == 1 ? red
            : Number(document, color[2], $"A widget {description} color component");
        if (red is < 0 or > 1 || green is < 0 or > 1 || blue is < 0 or > 1) return null;
        return new PdfRgbColor(red, green, blue);
    }

    private static List<PdfFormChoiceInfo> ReadOptions(PdfDocument document, PdfObject value)
    {
        PdfArray array = Resolve(document, value, "An AcroForm /Opt value") as PdfArray
            ?? throw new InvalidOperationException("An AcroForm /Opt value is not an array.");
        var result = new List<PdfFormChoiceInfo>();
        foreach (PdfObject optionValue in array)
        {
            PdfObject option = Resolve(document, optionValue, "An AcroForm choice option");
            if (option is PdfString single)
            {
                string decoded = PdfUnicodeEncoding.DecodeTextString(single.Bytes.Span,
                    "An AcroForm choice option");
                result.Add(new PdfFormChoiceInfo { ExportValue = decoded, DisplayValue = decoded });
            }
            else if (option is PdfArray pair && pair.Count >= 2
                && Resolve(document, pair[0], "An AcroForm choice export value") is PdfString export
                && Resolve(document, pair[1], "An AcroForm choice display value") is PdfString display)
                result.Add(new PdfFormChoiceInfo
                {
                    ExportValue = PdfUnicodeEncoding.DecodeTextString(
                        export.Bytes.Span, "An AcroForm choice export value"),
                    DisplayValue = PdfUnicodeEncoding.DecodeTextString(
                        display.Bytes.Span, "An AcroForm choice display value")
                });
        }
        return result;
    }

    private static string ButtonOnValue(PdfDocument document, PdfDictionary widget)
    {
        if (!widget.TryGetValue(AppearanceName, out PdfObject? appearanceValue)
            || Resolve(document, appearanceValue, "A widget /AP value") is not PdfDictionary appearance
            || !appearance.TryGetValue(NormalAppearanceName, out PdfObject? normalValue)
            || Resolve(document, normalValue, "A widget /AP /N value") is not PdfDictionary states)
            return "/Yes";
        PdfName? state = states.Keys.FirstOrDefault(key => key.ValueAsLatin1() != "Off");
        return state is null ? "/Yes" : "/" + state.ValueAsLatin1();
    }

    private static IReadOnlyList<string> FieldValues(PdfDocument document, PdfObject value)
    {
        PdfObject resolved = Resolve(document, value, "An AcroForm /V value");
        if (resolved is PdfArray array)
            return [.. array.Select(item => ScalarFieldValue(document, item)).Where(item => item.Length > 0)];
        string scalar = ScalarFieldValue(document, resolved);
        return scalar.Length == 0 ? [] : [scalar];
    }

    private static string ScalarFieldValue(PdfDocument document, PdfObject value) =>
        Resolve(document, value, "An AcroForm /V value") switch
        {
            PdfString text => PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "An AcroForm /V value"),
            PdfName name => "/" + name.ValueAsLatin1(),
            PdfInteger integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PdfReal real => real.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => string.Empty
        };

    private static PdfFormFieldKind FieldKind(PdfName type) => type.ValueAsLatin1() switch
    {
        "Tx" => PdfFormFieldKind.Text,
        "Btn" => PdfFormFieldKind.Button,
        "Ch" => PdfFormFieldKind.Choice,
        "Sig" => PdfFormFieldKind.Signature,
        _ => PdfFormFieldKind.Unknown
    };

    private static (double Left, double Bottom, double Width, double Height) PageBox(
        PdfDocument document, PdfPageTreeEntry page)
    {
        PdfObject value = page.InheritedValues.TryGetValue(CropBoxName, out PdfObject? crop)
            ? crop : page.InheritedValues.TryGetValue(MediaBoxName, out PdfObject? media)
                ? media : throw new InvalidOperationException("A widget page has no effective page box.");
        PdfArray box = Resolve(document, value, "A widget page box") as PdfArray
            ?? throw new InvalidOperationException("A widget page box is not an array.");
        if (box.Count != 4) throw new InvalidOperationException("A widget page box has an invalid length.");
        double x1 = Number(document, box[0], "A page-box coordinate");
        double y1 = Number(document, box[1], "A page-box coordinate");
        double x2 = Number(document, box[2], "A page-box coordinate");
        double y2 = Number(document, box[3], "A page-box coordinate");
        double width = Math.Abs(x2 - x1), height = Math.Abs(y2 - y1);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("A widget page box is degenerate.");
        return (Math.Min(x1, x2), Math.Min(y1, y2), width, height);
    }

    private static int PageRotation(PdfDocument document, PdfPageTreeEntry page)
    {
        if (!page.InheritedValues.TryGetValue(RotateName, out PdfObject? value)) return 0;
        long rotation = Integer(document, value, "A widget page /Rotate value");
        int normalized = (int)(((rotation % 360) + 360) % 360);
        if (normalized % 90 != 0)
            throw new InvalidOperationException("A widget page rotation is not a multiple of 90 degrees.");
        return normalized;
    }

    private static long Integer(PdfDocument document, PdfObject value, string description) =>
        Resolve(document, value, description) is PdfInteger integer ? integer.Value
        : throw new InvalidOperationException($"{description} is not an integer.");

    private static double Number(PdfDocument document, PdfObject value, string description) =>
        Resolve(document, value, description) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => throw new InvalidOperationException($"{description} is not numeric.")
        };

    private static PdfObject Resolve(PdfDocument document, PdfObject value, string description)
    {
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
