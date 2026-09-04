using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>A value type inferred from imported merge records.</summary>
public enum PdfDataRecordFieldKind
{
    /// <summary>No record contains a value.</summary>
    Empty,
    /// <summary>Every populated value is Boolean.</summary>
    Boolean,
    /// <summary>Every populated value is numeric.</summary>
    Number,
    /// <summary>Every populated value is a date or date and time.</summary>
    Date,
    /// <summary>Every populated value is general text.</summary>
    Text,
    /// <summary>Populated values contain more than one inferred type.</summary>
    Mixed
}

/// <summary>Describes one imported merge field without retaining its values.</summary>
public sealed record PdfDataRecordField(
    string Name, PdfDataRecordFieldKind Kind, int NonEmptyValueCount,
    bool HasMissingValues);

/// <summary>Reads tabular records for data-driven PDF generation.</summary>
public static class PdfDataRecordReader
{
    private const long MaximumWorkbookPartBytes = 64 * 1024 * 1024;

    /// <summary>Inspects imported headers and value types without retaining field values.</summary>
    public static IReadOnlyList<PdfDataRecordField> Inspect(
        IEnumerable<IReadOnlyDictionary<string, string?>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        IReadOnlyDictionary<string, string?>[] selected = [.. records];
        if (selected.Length > 1_000_000)
            throw new ArgumentException(
                "Merge data contains too many records.", nameof(records));
        var names = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string?> record in selected)
        {
            if (record is null)
                throw new ArgumentException(
                    "Merge data contains a null record.", nameof(records));
            foreach (string name in record.Keys)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException(
                        "Merge field names cannot be empty.", nameof(records));
                if (known.Add(name)) names.Add(name);
            }
        }
        var result = new PdfDataRecordField[names.Count];
        for (int fieldIndex = 0; fieldIndex < names.Count; fieldIndex++)
        {
            string name = names[fieldIndex];
            var kinds = new HashSet<PdfDataRecordFieldKind>();
            int populated = 0;
            bool missing = false;
            foreach (IReadOnlyDictionary<string, string?> record in selected)
            {
                if (!record.TryGetValue(name, out string? value)
                    || string.IsNullOrWhiteSpace(value))
                {
                    missing = true;
                    continue;
                }
                populated++;
                kinds.Add(Infer(value));
            }
            PdfDataRecordFieldKind kind = kinds.Count switch
            {
                0 => PdfDataRecordFieldKind.Empty,
                1 => kinds.Single(),
                _ => PdfDataRecordFieldKind.Mixed
            };
            result[fieldIndex] = new PdfDataRecordField(name, kind, populated, missing);
        }
        return Array.AsReadOnly(result);
    }

    /// <summary>Reads records from CSV text whose first row contains field names.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromCsv(
        string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        List<string[]> rows = ParseCsv(csv);
        if (rows.Count == 0) return [];
        string[] headers = rows[0];
        if (headers.Length == 0 || headers.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("CSV field names cannot be empty.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != headers.Length)
            throw new FormatException("CSV field names must be unique.");
        var records = new List<IReadOnlyDictionary<string, string?>>(rows.Count - 1);
        foreach (string[] row in rows.Skip(1))
        {
            if (row.Length > headers.Length)
                throw new FormatException("A CSV record contains more values than the header row.");
            var record = new Dictionary<string, string?>(
                headers.Length, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Length; index++)
                record.Add(headers[index], index < row.Length ? row[index] : null);
            records.Add(record);
        }
        return Array.AsReadOnly(records.ToArray());
    }

    /// <summary>Reads records from a JSON array of objects containing scalar values.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromJson(
        string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("Merge JSON must contain an array of records.");
        var records = new List<IReadOnlyDictionary<string, string?>>();
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new FormatException("Each merge JSON record must be an object.");
            var record = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name)
                    || !record.TryAdd(property.Name, Scalar(property.Value)))
                    throw new FormatException(
                        "Merge JSON field names must be nonempty and unique.");
            }
            records.Add(record);
        }
        return Array.AsReadOnly(records.ToArray());
    }

    /// <summary>Reads one merge record from Forms Data Format field data.</summary>
    public static IReadOnlyDictionary<string, string?> FromFdf(ReadOnlyMemory<byte> fdf) =>
        FromFormData(PdfFdfFormData.Read(fdf), "FDF");

    /// <summary>Reads one merge record from XML Forms Data Format field data.</summary>
    public static IReadOnlyDictionary<string, string?> FromXfdf(ReadOnlyMemory<byte> xfdf) =>
        FromFormData(PdfXfdfFormData.Read(xfdf), "XFDF");

    /// <summary>Reads records from a selected worksheet in an Office Open XML workbook.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromXlsx(
        ReadOnlyMemory<byte> xlsx, string? sheetName = null)
    {
        if (xlsx.Length == 0) throw new FormatException("The XLSX package is empty.");
        if (xlsx.Length > MaximumWorkbookPartBytes)
            throw new FormatException("The XLSX package exceeds the supported size limit.");
        using var archive = new ZipArchive(
            new MemoryStream(xlsx.ToArray(), writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count > 1_000)
            throw new FormatException("The XLSX package contains too many parts.");
        long expandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.Length > MaximumWorkbookPartBytes - expandedBytes)
                throw new FormatException("The XLSX package exceeds the expanded size limit.");
            expandedBytes += entry.Length;
        }
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        XDocument workbook = ReadXml(archive, "xl/workbook.xml");
        XElement[] sheets = [.. workbook.Descendants(spreadsheet + "sheet")];
        XElement sheet = sheetName is null
            ? sheets.FirstOrDefault() ?? throw new FormatException("The XLSX workbook has no worksheets.")
            : sheets.FirstOrDefault(item => string.Equals((string?)item.Attribute("name"),
                sheetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"The XLSX workbook has no sheet named '{sheetName}'.");
        string relationshipId = (string?)sheet.Attribute(relationships + "id")
            ?? throw new FormatException("The XLSX worksheet has no relationship.");
        XDocument workbookRelationships = ReadXml(archive, "xl/_rels/workbook.xml.rels");
        XElement relationship = workbookRelationships.Descendants(packageRelationships + "Relationship")
            .SingleOrDefault(item => (string?)item.Attribute("Id") == relationshipId)
            ?? throw new FormatException("The XLSX worksheet relationship was not found.");
        string target = (string?)relationship.Attribute("Target")
            ?? throw new FormatException("The XLSX worksheet relationship has no target.");
        string worksheetPath = WorkbookTarget(target);
        string[] sharedStrings = archive.GetEntry("xl/sharedStrings.xml") is null ? []
            : [.. ReadXml(archive, "xl/sharedStrings.xml")
                .Descendants(spreadsheet + "si")
                .Select(item => string.Concat(item.Descendants(spreadsheet + "t")
                    .Select(text => text.Value)))];
        XDocument worksheet = ReadXml(archive, worksheetPath);
        var rows = new List<string[]>();
        foreach (XElement row in worksheet.Descendants(spreadsheet + "row"))
        {
            var values = new SortedDictionary<int, string?>();
            int implicitColumn = 0;
            foreach (XElement cell in row.Elements(spreadsheet + "c"))
            {
                int column = CellColumn((string?)cell.Attribute("r"), implicitColumn);
                if (!values.TryAdd(column, CellValue(cell, spreadsheet, sharedStrings)))
                    throw new FormatException("An XLSX row contains a duplicate cell.");
                implicitColumn = column + 1;
            }
            if (values.Count == 0) continue;
            string[] output = new string[values.Keys.Max() + 1];
            foreach ((int column, string? value) in values) output[column] = value ?? string.Empty;
            rows.Add(output);
        }
        if (rows.Count == 0) return [];
        string[] headers = rows[0];
        if (headers.Length == 0 || headers.Any(string.IsNullOrWhiteSpace))
            throw new FormatException("XLSX field names cannot be empty.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new FormatException("XLSX field names must be unique.");
        var records = new List<IReadOnlyDictionary<string, string?>>(rows.Count - 1);
        foreach (string[] row in rows.Skip(1))
        {
            var record = new Dictionary<string, string?>(headers.Length, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Length; index++)
                record.Add(headers[index], index < row.Length ? row[index] : null);
            records.Add(record);
        }
        return Array.AsReadOnly(records.ToArray());
    }

    private static XDocument ReadXml(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path)
            ?? throw new FormatException($"The XLSX package is missing '{path}'.");
        if (entry.Length > MaximumWorkbookPartBytes)
            throw new FormatException($"The XLSX part '{path}' exceeds the supported size limit.");
        using Stream stream = entry.Open();
        try { return XDocument.Load(stream, LoadOptions.None); }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException)
        {
            throw new FormatException($"The XLSX part '{path}' is malformed.", error);
        }
    }

    private static IReadOnlyDictionary<string, string?> FromFormData(
        PdfFormDataSet data, string format)
    {
        if (data.ContainsJavaScript)
            throw new FormatException($"{format} merge data cannot contain scripts.");
        var record = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !record.TryAdd(field.Name,
                    field.Values.Count switch
                    {
                        0 => null,
                        1 => field.Values[0],
                        _ => throw new FormatException(
                            $"{format} merge field '{field.Name}' has multiple values.")
                    }))
                throw new FormatException(
                    $"{format} merge field names must be nonempty and unique.");
        }
        return record;
    }

    private static string WorkbookTarget(string target)
    {
        string normalized = target.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
            throw new FormatException("The XLSX worksheet target is unsafe.");
        return normalized.StartsWith("xl/", StringComparison.Ordinal)
            ? normalized : "xl/" + normalized;
    }

    private static int CellColumn(string? reference, int fallback)
    {
        if (reference is null) return fallback;
        int column = 0;
        int length = 0;
        foreach (char character in reference)
        {
            if (!char.IsAsciiLetter(character)) break;
            column = checked(column * 26 + char.ToUpperInvariant(character) - 'A' + 1);
            length++;
        }
        if (length == 0 || column is <= 0 or > 16_384)
            throw new FormatException("An XLSX cell has an invalid reference.");
        return column - 1;
    }

    private static string? CellValue(
        XElement cell, XNamespace spreadsheet, IReadOnlyList<string> sharedStrings)
    {
        string? type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(spreadsheet + "t").Select(text => text.Value));
        string? value = cell.Element(spreadsheet + "v")?.Value;
        if (value is null) return null;
        if (type == "s")
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index < 0 || index >= sharedStrings.Count)
                throw new FormatException("An XLSX cell has an invalid shared-string index.");
            return sharedStrings[index];
        }
        return type == "b" ? value switch
        {
            "0" => "false",
            "1" => "true",
            _ => throw new FormatException("An XLSX Boolean cell has an invalid value.")
        } : value;
    }

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            => value.GetRawText(),
        _ => throw new FormatException(
            "Merge JSON values must be strings, numbers, booleans, or null.")
    };

    private static PdfDataRecordFieldKind Infer(string value)
    {
        if (bool.TryParse(value, out _)) return PdfDataRecordFieldKind.Boolean;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            return PdfDataRecordFieldKind.Number;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out _))
            return PdfDataRecordFieldKind.Date;
        return PdfDataRecordFieldKind.Text;
    }

    private static List<string[]> ParseCsv(string csv)
    {
        if (csv.Length == 0) return [];
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        bool afterQuote = false;
        for (int index = 0; index < csv.Length; index++)
        {
            char character = csv[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                }
                else field.Append(character);
                continue;
            }
            if (afterQuote && character is not (',' or '\r' or '\n'))
                throw new FormatException("A quoted CSV value has trailing characters.");
            if (character == ',' || character is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                afterQuote = false;
                if (character != ',')
                {
                    if (character == '\r' && index + 1 < csv.Length
                        && csv[index + 1] == '\n') index++;
                    rows.Add(row.ToArray());
                    row.Clear();
                }
            }
            else if (character == '"' && field.Length == 0)
                quoted = true;
            else field.Append(character);
        }
        if (quoted) throw new FormatException("A quoted CSV value is not closed.");
        if (field.Length > 0 || row.Count > 0 || csv[^1] == ',')
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }
}
