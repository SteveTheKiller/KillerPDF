using System.Text;
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
        string? sourcePath = root.Elements().FirstOrDefault(element => element.Name.LocalName == "f")
            ?.Attribute("href")?.Value;
        bool containsJavaScript = root.Descendants().Any(element =>
            element.Name.LocalName is "javascript" or "script");
        return new PdfFormDataSet
        {
            SourcePdfPath = sourcePath,
            Fields = Array.AsReadOnly(fields.ToArray()),
            ContainsJavaScript = containsJavaScript
        };
    }

    /// <summary>Writes field values and an optional source PDF reference as UTF-8 XFDF.</summary>
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
