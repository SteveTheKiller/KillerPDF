using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>The picture placements read for one document's pages, and the pictures extracted from them. A placement
/// belongs to the strict parse it was read from, so the cache lives and dies with that parse. The cached placements
/// and the reader keep the parse reachable until the document lets it go (or the cache is released), and then
/// everything read from it is dropped together.</summary>
internal sealed class PlacementReadCache
{
    internal const int MaximumPages = 8;
    internal const int MaximumAssets = 8;

    private readonly Lock _sync = new();
    private PdfWorkingDocument? _subscribed;
    private WeakReference<PdfWorkingDocument>? _owner;
    private WeakReference<PdfDocument>? _parse;
    private PdfPageContentReader? _reader;
    private readonly Dictionary<int, IReadOnlyList<PdfImagePlacementSelection>?> _pages = [];
    // A refused picture is remembered as null, so a repeat press does not extract it again.
    private readonly Dictionary<(int Page, int Ordinal), PdfImageAsset?> _assets = [];

    /// <summary>How many extraction results are remembered, for tests.</summary>
    internal int AssetsRemembered { get { lock (_sync) return _assets.Count; } }

    /// <summary>How many pages' placement reads are remembered, for tests.</summary>
    internal int PagesRemembered { get { lock (_sync) return _pages.Count; } }

    /// <summary>Whether the cache still holds a document to hear its drops from, for tests.</summary>
    internal bool FollowsADocument { get { lock (_sync) return _subscribed is not null; } }

    internal IReadOnlyList<PdfImagePlacementSelection>? PlacementsForPage(PdfWorkingDocument doc, int page)
    {
        lock (_sync)
        {
            Follow(doc);
            IReadOnlyList<PdfImagePlacementSelection>? read;
            try
            {
                if (_pages.TryGetValue(page, out var cached) && HoldsParse(doc))
                {
                    doc.TouchContentCopy();
                    return cached;
                }
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
            if (_assets.TryGetValue((page, ordinal), out var known))
            {
                doc.TouchContentCopy();
                return known;
            }
            // The parse is gone (the document let it go between the page read and now): this is not a refusal, so
            // nothing is remembered and the next page read starts again.
            if (_parse is null || !_parse.TryGetTarget(out var source)) return null;
            doc.TouchContentCopy();
            PdfImageAsset? asset;
            try
            {
                // The placement was read from the parse the cache still follows; extract against that same parse.
                asset = PdfImagePlacementExtractor.Extract(source, image);
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

    /// <summary>True when extraction refused this picture (a format the editor cannot keep, or a PDF that forbids
    /// copying content), as opposed to a picture not asked for yet or a parse that was dropped before asking.</summary>
    internal bool WasRefused(int page, int ordinal)
    {
        lock (_sync) return _assets.TryGetValue((page, ordinal), out var asset) && asset is null;
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
        // A document that is already closed raises no more drops; Close sets the flag before it raises, so a close
        // racing this subscribe is caught either here or by the handler.
        if (doc.IsClosed)
        {
            doc.ContentCopyDropped -= OnContentCopyDropped;
            _subscribed = null;
        }
    }

    // Runs on the document's idle timer, or when it closes: the parse is gone, so what was read from it goes with it.
    // A closed document can raise no more drops, so the cache stops following it and no longer holds it. The owner is
    // weak and stays, so a later read of the same closed document does not subscribe to it again.
    private void OnContentCopyDropped()
    {
        lock (_sync)
        {
            ClearAll();
            if (_subscribed is { IsClosed: true } closed)
            {
                closed.ContentCopyDropped -= OnContentCopyDropped;
                _subscribed = null;
            }
        }
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
        // Held for exactly the cache's lifetime: ClearReads and ClearAll drop it with the placements read through it.
        if (_reader is not null) return _reader;
        var reader = new PdfPageContentReader(source);
        _reader = reader;
        return reader;
    }
}
