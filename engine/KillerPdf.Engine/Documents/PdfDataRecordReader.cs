using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads tabular records for data-driven PDF generation.</summary>
public static class PdfDataRecordReader
{
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

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            => value.GetRawText(),
        _ => throw new FormatException(
            "Merge JSON values must be strings, numbers, booleans, or null.")
    };

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
