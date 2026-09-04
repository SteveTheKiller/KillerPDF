using System.Text;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads and writes Acrobat-compatible XFDF form field data without executing actions.</summary>
public static class PdfXfdfFormData
{
    private const string NamespaceName = "http://ns.adobe.com/xfdf/";
    private const long MaximumCharacters = 16 * 1024 * 1024;

    /// <summary>Reads field values and the optional source PDF reference from XFDF bytes.</summary>
    public static PdfFormDataSet Read(ReadOnlyMemory<byte> source)
    {
        if (source.IsEmpty) throw new ArgumentException("XFDF data cannot be empty.", nameof(source));
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
            IgnoreComments = true
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement root = document.Root ?? throw new InvalidOperationException("The XFDF document has no root element.");
        if (root.Name.LocalName != "xfdf" || root.Name.NamespaceName != NamespaceName)
            throw new InvalidOperationException("The document is not an XFDF document.");
        var fields = new List<PdfFormDataField>();
        XElement? fieldRoot = root.Elements().FirstOrDefault(element => element.Name.LocalName == "fields");
        if (fieldRoot is not null)
            foreach (XElement field in fieldRoot.Elements().Where(element => element.Name.LocalName == "field"))
                ReadField(field, null, fields, 0);
        var annotations = new List<PdfFormDataAnnotation>();
        XElement? annotationRoot = root.Elements().FirstOrDefault(element => element.Name.LocalName == "annots");
        if (annotationRoot is not null)
            foreach (XElement annotation in annotationRoot.Elements())
                annotations.Add(ReadAnnotation(annotation));
        string? sourcePath = root.Elements().FirstOrDefault(element => element.Name.LocalName == "f")
            ?.Attribute("href")?.Value;
        bool containsJavaScript = root.Descendants().Any(element =>
            element.Name.LocalName is "javascript" or "script");
        return new PdfFormDataSet
        {
            SourcePdfPath = sourcePath,
            Fields = Array.AsReadOnly(fields.ToArray()),
            Annotations = Array.AsReadOnly(annotations.ToArray()),
            ContainsJavaScript = containsJavaScript
        };
    }

    /// <summary>Writes field values and an optional source PDF reference as UTF-8 XFDF.</summary>
    public static byte[] Write(PdfFormDataSet data, IEnumerable<string> fieldNames)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Write(data.SelectFields(fieldNames));
    }

    /// <summary>Writes selected fields and annotations from selected pages as UTF-8 XFDF.</summary>
    public static byte[] Write(PdfFormDataSet data, IEnumerable<string> fieldNames,
        IEnumerable<int> annotationPageIndexes)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Write(data.SelectFields(fieldNames).SelectAnnotationPages(annotationPageIndexes));
    }

    /// <summary>Writes all field values and an optional source PDF reference as UTF-8 XFDF.</summary>
    public static byte[] Write(PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);
        XNamespace xfdf = NamespaceName;
        var fields = new XElement(xfdf + "fields");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
                throw new ArgumentException("An XFDF field name cannot be empty.", nameof(data));
            if (!names.Add(field.Name))
                throw new ArgumentException($"The XFDF data contains duplicate field '{field.Name}'.", nameof(data));
            var element = new XElement(xfdf + "field", new XAttribute("name", field.Name));
            foreach (string value in field.Values)
            {
                if (value is null) throw new ArgumentException("XFDF values cannot contain null.", nameof(data));
                element.Add(new XElement(xfdf + "value", value));
            }
            fields.Add(element);
        }
        var root = new XElement(xfdf + "xfdf", new XAttribute(XNamespace.Xmlns + "xfdf", xfdf));
        if (data.SourcePdfPath is not null)
            root.Add(new XElement(xfdf + "f", new XAttribute("href", data.SourcePdfPath)));
        root.Add(fields);
        if (data.Annotations.Count > 0)
        {
            var annotations = new XElement(xfdf + "annots");
            foreach (PdfFormDataAnnotation annotation in data.Annotations)
                annotations.Add(WriteAnnotation(xfdf, annotation));
            root.Add(annotations);
        }
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var output = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            NewLineChars = "\n",
            OmitXmlDeclaration = false
        })) document.Save(writer);
        return output.ToArray();
    }

    private static PdfFormDataAnnotation ReadAnnotation(XElement element)
    {
        int page = IntegerAttribute(element, "page");
        if (page < 0) throw new InvalidOperationException("An XFDF annotation page cannot be negative.");
        double[] rectangle = RequiredAttribute(element, "rect").Split(',')
            .Select(value => ParseFiniteDouble(value, "rectangle")).ToArray();
        if (rectangle.Length != 4 || rectangle[2] < rectangle[0] || rectangle[3] < rectangle[1])
            throw new InvalidOperationException("An XFDF annotation rectangle is invalid.");
        double? opacity = OptionalDoubleAttribute(element, "opacity");
        if (opacity is < 0 or > 1)
            throw new InvalidOperationException("An XFDF annotation opacity must be between zero and one.");
        string? contents = element.Elements().FirstOrDefault(child => child.Name.LocalName == "contents")?.Value;
        return new PdfFormDataAnnotation
        {
            Subtype = element.Name.LocalName,
            PageIndex = page,
            Rectangle = Array.AsReadOnly(rectangle),
            Name = Attribute(element, "name"),
            Contents = contents,
            Author = Attribute(element, "title"),
            Subject = Attribute(element, "subject"),
            Color = Attribute(element, "color"),
            Opacity = opacity,
            CreationDate = Attribute(element, "creationdate"),
            ModifiedDate = Attribute(element, "date"),
            ReplyToName = Attribute(element, "inreplyto")
        };
    }

    private static XElement WriteAnnotation(XNamespace xfdf, PdfFormDataAnnotation annotation)
    {
        if (string.IsNullOrWhiteSpace(annotation.Subtype))
            throw new ArgumentException("An XFDF annotation subtype cannot be empty.", nameof(annotation));
        if (annotation.PageIndex < 0 || annotation.Rectangle.Count != 4
            || annotation.Rectangle.Any(value => !double.IsFinite(value))
            || annotation.Rectangle[2] < annotation.Rectangle[0]
            || annotation.Rectangle[3] < annotation.Rectangle[1])
            throw new ArgumentException("An XFDF annotation has invalid page or rectangle data.", nameof(annotation));
        if (annotation.Opacity is < 0 or > 1 || annotation.Opacity is double.NaN)
            throw new ArgumentException("An XFDF annotation opacity must be between zero and one.", nameof(annotation));
        var element = new XElement(xfdf + annotation.Subtype,
            new XAttribute("page", annotation.PageIndex),
            new XAttribute("rect", string.Join(",", annotation.Rectangle.Select(FormatDouble))));
        AddAttribute(element, "name", annotation.Name);
        AddAttribute(element, "title", annotation.Author);
        AddAttribute(element, "subject", annotation.Subject);
        AddAttribute(element, "color", annotation.Color);
        if (annotation.Opacity is double opacity)
            element.Add(new XAttribute("opacity", FormatDouble(opacity)));
        AddAttribute(element, "creationdate", annotation.CreationDate);
        AddAttribute(element, "date", annotation.ModifiedDate);
        AddAttribute(element, "inreplyto", annotation.ReplyToName);
        if (annotation.Contents is not null)
            element.Add(new XElement(xfdf + "contents", annotation.Contents));
        return element;
    }

    private static string RequiredAttribute(XElement element, string name) =>
        Attribute(element, name) ?? throw new InvalidOperationException($"An XFDF annotation has no {name} attribute.");
    private static string? Attribute(XElement element, string name) => element.Attribute(name)?.Value;
    private static int IntegerAttribute(XElement element, string name) =>
        int.TryParse(RequiredAttribute(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : throw new InvalidOperationException($"An XFDF annotation {name} is not an integer.");
    private static double? OptionalDoubleAttribute(XElement element, string name) =>
        Attribute(element, name) is string value ? ParseFiniteDouble(value, name) : null;
    private static double ParseFiniteDouble(string value, string name) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result : throw new InvalidOperationException($"An XFDF annotation {name} is not a finite number.");
    private static string FormatDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static void AddAttribute(XElement element, string name, string? value)
    {
        if (value is not null) element.Add(new XAttribute(name, value));
    }

    private static void ReadField(XElement element, string? parent,
        ICollection<PdfFormDataField> output, int depth)
    {
        if (depth >= 256) throw new InvalidOperationException("The XFDF field hierarchy is too deep.");
        string localName = element.Attribute("name")?.Value
            ?? throw new InvalidOperationException("An XFDF field has no name.");
        if (localName.Length == 0) throw new InvalidOperationException("An XFDF field name is empty.");
        string fullName = parent is null ? localName : parent + "." + localName;
        string[] values = [.. element.Elements()
            .Where(child => child.Name.LocalName is "value" or "value-richtext")
            .Select(child => child.Value)];
        if (values.Length > 0)
            output.Add(new PdfFormDataField { Name = fullName, Values = Array.AsReadOnly(values) });
        foreach (XElement child in element.Elements().Where(child => child.Name.LocalName == "field"))
            ReadField(child, fullName, output, depth + 1);
    }
}
