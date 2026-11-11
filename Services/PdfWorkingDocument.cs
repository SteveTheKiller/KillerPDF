using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Engine-validated serialized working state for one open desktop document.</summary>
internal sealed class PdfWorkingDocument : IDisposable
{
    private readonly byte[] _source;
    private readonly OpenedProtection? _key;
    private PdfDocument? _parsedDocument;
    private readonly Lock _readSync = new();
    private PdfCapturedReadRevision? _readRevision;
    private readonly Lock _contentCopySync = new();
    private (PdfDocument? Document, System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure)? _contentCopy;
    private bool _closed;

    /// <summary>How long the strict parse for picture copies is kept once nothing has asked for it.</summary>
    internal static readonly TimeSpan ContentCopyIdleLimit = TimeSpan.FromMinutes(2);

    /// <summary>The clock and timers behind the idle drop. Tests swap it before the first strict parse.</summary>
    internal TimeProvider ContentCopyClock { get; set; } = TimeProvider.System;
    private long _contentCopyUsedAt;
    private ITimer? _contentCopyTimer;

    /// <summary>Raised, outside the document's locks and possibly on a timer thread, after the idle strict parse is
    /// let go, so whatever was read from that parse can be let go with it. It is also raised once when the document
    /// closes, whether or not a parse was still held, so a listener can let go of the document itself.</summary>
    internal event Action? ContentCopyDropped;

    /// <summary>True while a strict parse is held for picture copies; a cached refusal holds nothing.</summary>
    internal bool HoldsContentCopy { get { lock (_contentCopySync) return _contentCopy is { Document: not null }; } }

    private PdfWorkingDocument(byte[] source, bool isReadOnly, OpenedProtection? protection)
    {
        PdfDocument parsed = PdfEngineIntegration.OpenForReading(source);
        // Only the tab that holds the key reads a file its empty password does not open, and the
        // parse it gets is shared with that tab alone.
        if (parsed.IsEncrypted && !parsed.IsDecrypted && protection?.OpenHeld(source) is PdfDocument held)
        {
            parsed = held;
            _key = protection;
        }
        if (!parsed.CanReadPageContent)
            throw new InvalidOperationException("This encrypted PDF requires a password.");
        _parsedDocument = parsed;
        _source = source;
        ContentIdentity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source));
        PageCount = PdfPageInformation.Read(parsed).Count;
        IsReadOnly = isReadOnly;
    }

    internal int PageCount { get; }
    internal string ContentIdentity { get; }
    internal bool IsReadOnly { get; }
    internal PdfDocument? ParsedDocument => _parsedDocument;

    /// <summary>The strict parse that picture selection copies from. The source never changes, so one
    /// parse (or its refusal) serves every click on this document instead of parsing it again each time.</summary>
    internal PdfDocument? OpenStrictForContentCopy()
    {
        bool droppedOverdue = false;
        PdfDocument? result;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure;
        // Its own lock: a first strict parse of a large file never holds up render revisions.
        lock (_contentCopySync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed), this);
            // The timer normally drops an idle copy; this covers one that is overdue by the time it is asked for.
            if (_contentCopy is { Document: not null } && IdleFor() >= ContentCopyIdleLimit)
            {
                _contentCopy = null;
                droppedOverdue = true;
            }
            if (_contentCopy is null)
            {
                try
                {
                    var document = PdfDocument.Open(_source);
                    _contentCopy = (document.IsEncrypted ? null : document, null);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    _contentCopy = (null, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error));
                }
                // Only a held parse is dropped when idle. A cached refusal costs nothing to keep.
                if (_contentCopy.Value.Document is not null)
                {
                    if (_contentCopyTimer is null)
                        _contentCopyTimer = ContentCopyClock.CreateTimer(_ => DropIdleContentCopy(), null,
                            ContentCopyIdleLimit, Timeout.InfiniteTimeSpan);
                    else _contentCopyTimer.Change(ContentCopyIdleLimit, Timeout.InfiniteTimeSpan);
                }
            }
            _contentCopyUsedAt = ContentCopyClock.GetTimestamp();
            failure = _contentCopy.Value.Failure;
            result = _contentCopy.Value.Document;
        }
        // Outside the lock, like the timer's drop: whatever was read from the overdue parse goes with it.
        if (droppedOverdue) ContentCopyDropped?.Invoke();
        failure?.Throw();
        return result;
    }

    /// <summary>Counts a cached read of what the strict parse produced as a use of that parse, so the idle drop
    /// does not discard a parse the user is still working from. Does nothing when no parse is held.</summary>
    internal void TouchContentCopy()
    {
        lock (_contentCopySync)
        {
            if (_contentCopy is { Document: not null } && !Volatile.Read(ref _closed))
                _contentCopyUsedAt = ContentCopyClock.GetTimestamp();
        }
    }

    private TimeSpan IdleFor() => ContentCopyClock.GetElapsedTime(_contentCopyUsedAt);

    // Runs on the timer: drops the parse if it has gone unused for the whole limit, else waits out the rest of it.
    private void DropIdleContentCopy()
    {
        bool dropped = false;
        lock (_contentCopySync)
        {
            if (Volatile.Read(ref _closed) || _contentCopy is not { Document: not null }) return;
            TimeSpan idle = IdleFor();
            if (idle >= ContentCopyIdleLimit) { _contentCopy = null; dropped = true; }
            else _contentCopyTimer?.Change(ContentCopyIdleLimit - idle, Timeout.InfiniteTimeSpan);
        }
        // Outside the lock: a listener takes its own lock, which another thread may hold while asking for the parse.
        if (dropped) ContentCopyDropped?.Invoke();
    }

    internal PdfCapturedReadRevision CaptureReadRevision()
    {
        lock (_readSync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_readRevision is { IsClosed: false } revision) return revision;
            return _readRevision = new PdfCapturedReadRevision(
                _parsedDocument ?? PdfEngineIntegration.OpenForReading(_source, _key), ContentIdentity);
        }
    }

    internal void ReleaseParsedDocument(PdfDocument document) =>
        Interlocked.CompareExchange(ref _parsedDocument, null, document);

    /// <summary>Opens a tab's file. The protection is the tab's own: a file read where it lies, still
    /// encrypted, opens only under the key that protection holds.</summary>
    internal static PdfWorkingDocument Open(string path, OpenedProtection? protection, bool isReadOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo? info;
        long ticks = 0, size = -1;
        try
        {
            info = new FileInfo(path);
            ticks = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            info = null;
        }
        byte[] source = File.ReadAllBytes(path);
        var working = new PdfWorkingDocument(source, isReadOnly, protection);
        bool registered = false;
        if (info is not null && working._parsedDocument is { CanReadPageContent: true } parsed
            && size == source.LongLength)
        {
            try
            {
                info.Refresh();
                if (info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == ticks)
                {
                    PdfPageRenderSession.RegisterDocument(info.FullName, ticks, size, working, parsed, working._key);
                    registered = true;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The working document is still usable if the file changes during registration.
            }
        }
        if (!registered) Interlocked.Exchange(ref working._parsedDocument, null);
        return working;
    }

    internal void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, _source);
    }

    /// <summary>Writes the document with no encryption, for a copy that is thrown away after one use,
    /// such as the print copy. A tab that reads its encrypted original is decrypted under the key it
    /// opened with, its empty password for an owner-only file or the key it holds for a signed file
    /// that needs a password. Such a copy must never be published or put beside the original.</summary>
    internal void SavePlainThrowawayCopy(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfEngineIntegration.OpenForReading(_source, _key);
        if (!document.IsEncrypted) Save(path);
        else PdfEngineIntegration.WritePlainThrowawayCopy(document, path);
    }

    internal void Save(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(_source);
    }

    /// <summary>The document's bookmark tree, read from the working bytes as they are. Saving them into a memory
    /// stream first copied the file about three times, which a large scan left behind for the next collection.</summary>
    internal IReadOnlyList<PdfBookmarkInfo> ReadBookmarks(OpenedProtection? protection) =>
        PdfEngineIntegration.ReadBookmarks(_source, protection);

    /// <summary>Registers a byte-identical copy written by <see cref="Save(string)"/> with the parse
    /// this document already holds, so rendering from the copy does not parse the file again.</summary>
    internal void RegisterCopy(string path)
    {
        if (_parsedDocument is not { CanReadPageContent: true } parsed) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length == _source.LongLength)
                PdfPageRenderSession.RegisterDocument(info.FullName, info.LastWriteTimeUtc.Ticks, info.Length,
                    this, parsed, _key);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unregistered copy still renders; it is parsed on first use instead.
        }
    }

    /// <summary>True once the document has been closed; a closed document can no longer raise a content copy drop.</summary>
    internal bool IsClosed => Volatile.Read(ref _closed);

    internal void Close()
    {
        PdfCapturedReadRevision? revision;
        lock (_readSync)
        {
            if (_closed) return;
            Volatile.Write(ref _closed, true);
            revision = _readRevision;
            _readRevision = null;
            Interlocked.Exchange(ref _parsedDocument, null);
        }
        lock (_contentCopySync)
        {
            _contentCopy = null;
            _contentCopyTimer?.Dispose();
            _contentCopyTimer = null;
        }
        revision?.Dispose();
        // Outside both locks, as in DropIdleContentCopy: a listener holding a read of the parse, or the document itself,
        // lets it go. The _closed guard above makes this run once.
        ContentCopyDropped?.Invoke();
    }
    public void Dispose() => Close();
}
