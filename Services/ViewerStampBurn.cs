using System.IO;
using System.Reflection;
using EngineDocument = KillerPdf.Engine.Documents.PdfDocument;

namespace KillerPDF.Services;

/// <summary>
/// #478: the viewer's copy of a document with its active stamps put into the page content by the same burn Apply and
/// save run, held in memory. The viewer renders the pages the stamps cover from it, so the annotations the file holds
/// stay above the watermark and the page number exactly as they will after Apply, at every zoom, detail tile, color
/// conversion and display. Pages the stamps do not cover keep rendering from the document itself.
/// </summary>
internal sealed class ViewerStampBurn
{
    private readonly Lock _sync = new();
    private readonly byte[] _bytes;
    private readonly HashSet<int> _covered;
    private EngineDocument? _document;
    private PdfContentDocument? _content;

    private ViewerStampBurn(string sourcePath, StampSpec spec, IReadOnlyDictionary<int, int> rotations, byte[] bytes,
        HashSet<int> covered)
    {
        Id = Guid.NewGuid();
        SourcePath = sourcePath;
        Spec = spec;
        Rotations = rotations;
        _bytes = bytes;
        _covered = covered;
    }

    /// <summary>Tells this burn apart from every other one.</summary>
    internal Guid Id { get; }

    /// <summary>
    /// True when the float renderer behind proofing, monitor conversion and HDR presentation can draw the stamped
    /// pages. It cannot draw every placement of a burned picture yet (the image paint it refuses depends on the
    /// render size), so a picture watermark keeps the overlay stamps on those paths: a page bitmap that silently
    /// lost its proof or monitor conversion would show the saved marks in the wrong colors.
    /// </summary>
    internal bool DrawsInFloatFrames => !(Spec.WmEnabled && Spec.WmIsImage);

    /// <summary>This burn for a render that draws float frames when <paramref name="floatFrames"/> is true, or null
    /// when those renders keep the overlay stamps.</summary>
    internal ViewerStampBurn? ForRender(bool floatFrames) => floatFrames && !DrawsInFloatFrames ? null : this;

    /// <summary>The file the stamps were burned into.</summary>
    internal string SourcePath { get; }

    /// <summary>A copy of the stamp settings the burn used.</summary>
    internal StampSpec Spec { get; }

    /// <summary>The page turns the stamps were placed for.</summary>
    internal IReadOnlyDictionary<int, int> Rotations { get; }

    /// <summary>The pages that carry a watermark or a page number.</summary>
    internal IReadOnlyCollection<int> CoveredPages => _covered;

    internal bool Covers(int pageIndex) => _covered.Contains(pageIndex);

    /// <summary>The stamped document, parsed once on first use and shared by every render of it.</summary>
    internal EngineDocument Document
    {
        get
        {
            lock (_sync)
                return _document ??= EngineDocument.OpenWithCompatibilityRecovery(new MemoryStream(_bytes, writable: false));
        }
    }

    /// <summary>The stamped document's bytes. Never written to.</summary>
    internal byte[] Bytes => _bytes;

    /// <summary>Reads the stamped document's page content, parsed once and shared by every reader, one at a time.</summary>
    internal T ReadContent<T>(Func<PdfContentDocument, T> read)
    {
        lock (_sync)
            return read(_content ??= PdfContentDocument.Open(_bytes, null));
    }

    /// <summary>True when this burn was made from <paramref name="sourcePath"/> with these stamps and page turns.</summary>
    internal bool Matches(string sourcePath, StampSpec spec, IReadOnlyDictionary<int, int> rotations) =>
        string.Equals(SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) && SameSpec(Spec, spec)
        && SameRotations(Rotations, rotations);

    /// <summary>True when this burn was made from <paramref name="sourcePath"/> with these page turns, whatever the stamps.</summary>
    internal bool MatchesDocument(string sourcePath, IReadOnlyDictionary<int, int> rotations) =>
        string.Equals(SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase) && SameRotations(Rotations, rotations);

    /// <summary>
    /// Burns <paramref name="spec"/> into a temporary copy of <paramref name="sourcePath"/> the way Apply does, reads the
    /// result into memory and deletes the copy before returning. Runs off the UI thread.
    /// </summary>
    internal static ViewerStampBurn Create(string sourcePath, StampSpec spec, IReadOnlyDictionary<int, int> rotations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(spec);
        StampSpec copy = spec.Clone();
        Dictionary<int, int> turns = new(rotations);
        string temp = Path.Combine(Path.GetTempPath(), $"killerpdf-stamped-{Guid.NewGuid():N}.pdf");
        byte[] bytes;
        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            cancellationToken.ThrowIfCancellationRequested();
            PdfEngineBurn.Burn(temp, new Dictionary<int, List<PageAnnotation>>(), new Dictionary<int, (int w, int h)>(),
                copy, null, turns, cancellationToken: cancellationToken);
            bytes = File.ReadAllBytes(temp);
        }
        finally
        {
            // The copy holds the page content; it goes as soon as it is read.
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        int pageCount = KillerPdf.Engine.Documents.PdfPageInformation.Read(
            EngineDocument.OpenWithCompatibilityRecovery(new MemoryStream(bytes, writable: false))).Count;
        HashSet<int> covered = [];
        if (copy.NumbersEnabled) covered.UnionWith(PdfBurn.StampPageRange(copy.NumRange, pageCount));
        if (copy.WmEnabled) covered.UnionWith(PdfBurn.StampPageRange(copy.WmRange, pageCount));
        return new ViewerStampBurn(sourcePath, copy, turns, bytes, covered);
    }

    private static readonly FieldInfo[] SpecFields = typeof(StampSpec).GetFields(BindingFlags.Instance | BindingFlags.Public);

    /// <summary>True when two stamp settings draw the same stamps.</summary>
    internal static bool SameSpec(StampSpec a, StampSpec b) =>
        ReferenceEquals(a, b) || SpecFields.All(field => Equals(field.GetValue(a), field.GetValue(b)));

    private static bool SameRotations(IReadOnlyDictionary<int, int> a, IReadOnlyDictionary<int, int> b)
    {
        static int Turn(int value) => ((value % 360) + 360) % 360;
        foreach (var (page, turn) in a)
            if (Turn(turn) != Turn(b.TryGetValue(page, out int other) ? other : 0)) return false;
        foreach (var (page, turn) in b)
            if (Turn(turn) != Turn(a.TryGetValue(page, out int other) ? other : 0)) return false;
        return true;
    }
}
