using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>The picture placements read for one document's pages, and the pictures extracted from them. A placement
/// belongs to the strict parse it was read from, so the cache lives and dies with that parse: when the document
/// lets its idle parse go, everything read from it goes too and nothing here keeps the parse reachable.</summary>
internal sealed class PlacementReadCache
{
    internal const int MaximumPages = 8;
    internal const int MaximumAssets = 8;

    private readonly Lock _sync = new();
    private PdfWorkingDocument? _subscribed;
    private WeakReference<PdfWorkingDocument>? _owner;
    private WeakReference<PdfDocument>? _parse;
    private WeakReference<PdfPageContentReader>? _reader;
    private readonly Dictionary<int, IReadOnlyList<PdfImagePlacementSelection>?> _pages = [];
    // A refused picture is remembered as null, so a repeat press does not extract it again.
    private readonly Dictionary<(int Page, int Ordinal), PdfImageAsset?> _assets = [];

    internal IReadOnlyList<PdfImagePlacementSelection>? PlacementsForPage(PdfWorkingDocument doc, int page)
    {
        lock (_sync)
        {
            Follow(doc);
            IReadOnlyList<PdfImagePlacementSelection>? read;
            try
            {
                if (_pages.TryGetValue(page, out var cached) && HoldsParse(doc)) return cached;
                var source = doc.OpenStrictForContentCopy();
                if (source is null) return null;
                if (!IsCurrentParse(source)) ClearReads(source);
                read = ReaderFor(source).ReadImagePlacements(page);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
            {
                // This document's page is one the reader refuses; remember that so a click does not parse it again.
                read = null;
            }
            if (_pages.Count >= MaximumPages)
            {
                _pages.Clear();
                _assets.Clear();
            }
            _pages[page] = read;
            return read;
        }
    }

    internal PdfImageAsset? AssetForPlacement(PdfWorkingDocument doc, int page, int ordinal, PdfImagePlacementSelection image)
    {
        lock (_sync)
        {
            if (_assets.TryGetValue((page, ordinal), out var known)) return known;
            PdfImageAsset? asset;
            try
            {
                // The placement was read from the parse the cache still follows; extract against that same parse.
                asset = _parse is not null && _parse.TryGetTarget(out var source)
                    ? PdfImagePlacementExtractor.Extract(source, image) : null;
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or FormatException
                or ArgumentException or KillerPdf.Engine.Filters.PdfFilterException)
            {
                asset = null;
            }
            if (_assets.Count >= MaximumAssets) _assets.Clear();
            _assets[(page, ordinal)] = asset;
            return asset;
        }
    }

    /// <summary>Drops every placement read, extracted asset and the reader behind them.</summary>
    internal void Release()
    {
        lock (_sync)
        {
            if (_subscribed is not null) _subscribed.ContentCopyDropped -= OnContentCopyDropped;
            _subscribed = null;
            _owner = null;
            ClearAll();
        }
    }

    // Placement reads are cached per page for one document instance; another document starts an empty cache.
    private void Follow(PdfWorkingDocument doc)
    {
        if (_owner is not null && _owner.TryGetTarget(out var owner) && ReferenceEquals(owner, doc)) return;
        if (_subscribed is not null) _subscribed.ContentCopyDropped -= OnContentCopyDropped;
        ClearAll();
        _owner = new WeakReference<PdfWorkingDocument>(doc);
        _subscribed = doc;
        doc.ContentCopyDropped += OnContentCopyDropped;
    }

    // Runs on the document's idle timer: the parse is gone, so what was read from it goes with it.
    private void OnContentCopyDropped()
    {
        lock (_sync) ClearAll();
    }

    private bool IsCurrentParse(PdfDocument source) =>
        _parse is not null && _parse.TryGetTarget(out var current) && ReferenceEquals(current, source);

    private bool HoldsParse(PdfWorkingDocument doc) => doc.HoldsContentCopy && _parse is not null && _parse.TryGetTarget(out _);

    private void ClearReads(PdfDocument source)
    {
        _pages.Clear();
        _assets.Clear();
        _reader = null;
        _parse = new WeakReference<PdfDocument>(source);
    }

    private void ClearAll()
    {
        _pages.Clear();
        _assets.Clear();
        _reader = null;
        _parse = null;
    }

    private PdfPageContentReader ReaderFor(PdfDocument source)
    {
        if (_reader is not null && _reader.TryGetTarget(out var reader)) return reader;
        reader = new PdfPageContentReader(source);
        _reader = new WeakReference<PdfPageContentReader>(reader);
        return reader;
    }
}
