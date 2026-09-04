namespace KillerPdf.Engine.Documents;

/// <summary>Extracts portable AcroForm values for FDF or XFDF export.</summary>
public static class PdfFormDataExporter
{
    /// <summary>Extracts every exportable terminal field in document order.</summary>
    public static PdfFormDataSet Export(PdfDocument document, string? sourcePdfPath = null,
        bool includeNoExportFields = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        int pageCount = PdfDocumentInformation.Read(document).PageCount;
        var fields = new List<PdfFormDataField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, pageIndex))
            {
                if (!seen.Add(widget.FieldName) || !includeNoExportFields && (widget.Flags & 4) != 0)
                    continue;
                fields.Add(new PdfFormDataField
                {
                    Name = widget.FieldName,
                    MappingName = string.IsNullOrEmpty(widget.MappingName) ? null : widget.MappingName,
                    Values = Values(widget.Values, widget.Value),
                    DefaultValues = Values(widget.DefaultValues, widget.DefaultValue)
                });
            }
        }
        return new PdfFormDataSet
        {
            SourcePdfPath = sourcePdfPath,
            Fields = Array.AsReadOnly(fields.ToArray())
        };
    }

    private static IReadOnlyList<string> Values(IReadOnlyList<string> values, string scalar) =>
        values.Count > 0 ? Array.AsReadOnly(values.ToArray()) : [scalar];
}
