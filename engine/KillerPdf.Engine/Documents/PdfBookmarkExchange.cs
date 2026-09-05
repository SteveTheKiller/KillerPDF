using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Exports and imports portable bookmark hierarchies as JSON.</summary>
public static partial class PdfBookmarkExchange
{
    private static readonly PdfBookmarkCompactJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfBookmarkIndentedJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Exports the document's bookmark hierarchy as stable JSON.</summary>
    public static string ToJson(PdfDocument document, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfBookmarkExchangeDocument exchange = new(1,
            [.. PdfBookmarkReader.Read(document).Select(Export)]);
        return JsonSerializer.Serialize(exchange, indented
            ? IndentedJson.PdfBookmarkExchangeDocument
            : CompactJson.PdfBookmarkExchangeDocument);
    }

    /// <summary>Imports a JSON bookmark hierarchy, optionally replacing existing bookmarks.</summary>
    public static byte[] Import(PdfDocument document, string json, bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PdfBookmarkExchangeDocument exchange;
        try
        {
            exchange = JsonSerializer.Deserialize(
                json, CompactJson.PdfBookmarkExchangeDocument)
                ?? throw new InvalidOperationException("The bookmark JSON document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The bookmark JSON document is invalid.", exception);
        }
        if (exchange.SchemaVersion != 1)
            throw new NotSupportedException(
                $"Bookmark JSON schema version {exchange.SchemaVersion} is not supported.");
        if (exchange.Bookmarks is null)
            throw new InvalidOperationException("The bookmark JSON document has no bookmark list.");

        var editor = new PdfIncrementalPageEditor(document);
        if (replaceExisting) editor.ClearBookmarks();
        Add(exchange.Bookmarks, 0);
        return editor.Build();

        void Add(IReadOnlyList<PdfBookmarkExchangeItem> bookmarks, int level)
        {
            foreach (PdfBookmarkExchangeItem bookmark in bookmarks)
            {
                if (bookmark is null || string.IsNullOrWhiteSpace(bookmark.Title))
                    throw new InvalidOperationException("Every imported bookmark needs a title.");
                if (bookmark.PageIndex is not int pageIndex)
                    throw new InvalidOperationException(
                        $"Bookmark '{bookmark.Title}' has no resolved page destination.");
                var options = new PdfBookmarkOptions
                {
                    IsOpen = bookmark.IsOpen,
                    Style = bookmark.Style,
                    Color = bookmark.Color is null ? null : new PdfRgbColor(
                        bookmark.Color.Red, bookmark.Color.Green, bookmark.Color.Blue),
                    Destination = RestoreDestination(bookmark.Destination)
                };
                editor.AddBookmark(bookmark.Title, pageIndex, level, options);
                Add(bookmark.Children ?? [], level + 1);
            }
        }
    }

    private static PdfBookmarkExchangeItem Export(PdfBookmarkInfo bookmark) => new(
        bookmark.ObjectNumber,
        bookmark.Generation,
        bookmark.Title,
        bookmark.IsOpen,
        bookmark.Style,
        bookmark.Color is PdfRgbColor color
            ? new PdfBookmarkExchangeColor(color.Red, color.Green, color.Blue) : null,
        bookmark.DestinationPageIndex,
        bookmark.NamedDestination,
        bookmark.Destination is null ? null : new PdfBookmarkExchangeDestination(
            bookmark.Destination.Kind, [.. bookmark.Destination.Values]),
        [.. bookmark.Children.Select(Export)]);

    private static PdfDestination RestoreDestination(PdfBookmarkExchangeDestination? destination)
    {
        if (destination is null) return PdfDestination.FitPage();
        IReadOnlyList<double?> values = destination.Values
            ?? throw new InvalidOperationException("A bookmark destination has no value list.");
        return destination.Kind switch
        {
            PdfDestinationKind.Xyz when values.Count == 3 =>
                PdfDestination.At(values[0], values[1], values[2]),
            PdfDestinationKind.Fit when values.Count == 0 => PdfDestination.FitPage(),
            PdfDestinationKind.FitH when values.Count == 1 => PdfDestination.FitWidth(values[0]),
            PdfDestinationKind.FitV when values.Count == 1 => PdfDestination.FitHeight(values[0]),
            PdfDestinationKind.FitR when values is [double left, double bottom, double right, double top] =>
                PdfDestination.FitRectangle(left, bottom, right, top),
            PdfDestinationKind.FitB when values.Count == 0 => PdfDestination.FitBoundingBox(),
            PdfDestinationKind.FitBH when values.Count == 1 =>
                PdfDestination.FitBoundingBoxWidth(values[0]),
            PdfDestinationKind.FitBV when values.Count == 1 =>
                PdfDestination.FitBoundingBoxHeight(values[0]),
            _ => throw new InvalidOperationException(
                $"A {destination.Kind} bookmark destination has invalid values.")
        };
    }

    private static JsonSerializerOptions JsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        options.Converters.Add(
            new JsonStringEnumConverter<PdfBookmarkStyle>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(
            new JsonStringEnumConverter<PdfDestinationKind>(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PdfBookmarkExchangeDocument(
        int SchemaVersion, IReadOnlyList<PdfBookmarkExchangeItem>? Bookmarks);

    private sealed record PdfBookmarkExchangeItem(
        int ObjectNumber,
        int Generation,
        string Title,
        bool IsOpen,
        PdfBookmarkStyle Style,
        PdfBookmarkExchangeColor? Color,
        int? PageIndex,
        string? NamedDestination,
        PdfBookmarkExchangeDestination? Destination,
        IReadOnlyList<PdfBookmarkExchangeItem>? Children);

    private sealed record PdfBookmarkExchangeColor(double Red, double Green, double Blue);

    private sealed record PdfBookmarkExchangeDestination(
        PdfDestinationKind Kind, IReadOnlyList<double?>? Values);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(PdfBookmarkExchangeDocument))]
    private sealed partial class PdfBookmarkCompactJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(PdfBookmarkExchangeDocument))]
    private sealed partial class PdfBookmarkIndentedJsonContext : JsonSerializerContext;
}
