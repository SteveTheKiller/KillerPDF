namespace KillerPDF.Services;

/// <summary>The optional true-zoom extension to the existing seven-field document view record.</summary>
internal static class DocumentViewportState
{
    internal static string AppendTrueZoom(string legacyFields, double? trueZoom) =>
        trueZoom is { } value && double.IsFinite(value) && value > 0
            ? legacyFields + "|" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            : legacyFields;

    internal static double? ReadTrueZoom(IReadOnlyList<string> fields, double legacyZoom, bool legacyContinuous)
    {
        if (fields.Count >= 8 && double.TryParse(fields[7], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value > 0)
            return value;
        return legacyContinuous && double.IsFinite(legacyZoom) && legacyZoom > 0 ? legacyZoom : null;
    }
}
