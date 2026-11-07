using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Formats.Djvu;

namespace KillerPdf.Engine.Documents;

/// <summary>Provides a captured native outline label, opaque target and immutable child entries.</summary>
public sealed class PdfReadNavigationEntry
{
    internal PdfReadNavigationEntry(string label, string target, IEnumerable<PdfReadNavigationEntry> children)
    {
        Label = label;
        Target = target;
        Children = Array.AsReadOnly(children.ToArray());
    }
    /// <summary>Gets the original outline label.</summary>
    public string Label { get; }
    /// <summary>Gets the native target without executing or fetching it.</summary>
    public string Target { get; }
    /// <summary>Gets the owned, ordered child entries.</summary>
    public IReadOnlyList<PdfReadNavigationEntry> Children { get; }
}

/// <summary>Identifies a native page and top-left position in intrinsically oriented native page units.</summary>
public sealed record PdfReadNavigationDestination(int PageIndex, double Left, double Top);

/// <summary>A local native hyperlink with bounds in unrotated bottom-left PDF points.</summary>
public sealed record PdfReadLinkRegion(PdfContentBounds Bounds, string Target);

public sealed partial class PdfReadSnapshot
{
    /// <summary>Reads supported native hyperlink regions without executing or fetching their targets.</summary>
    public IReadOnlyList<PdfReadLinkRegion> ReadLinkRegions(int pageIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var geometry = GetPageGeometry(pageIndex);
        if (_layout is null)
            throw new NotSupportedException("Native hyperlink regions are not implemented for this format.");
        double scaleX = 72 / geometry.DpiX, scaleY = 72 / geometry.DpiY;
        var regions = new List<PdfReadLinkRegion>();
        foreach (var placement in _layout.Pages[pageIndex].LinkRuns(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            regions.Add(new(new PdfContentBounds(placement.Left * scaleX,
                (geometry.Height - placement.Top - placement.Height) * scaleY,
                (placement.Left + placement.Width) * scaleX,
                (geometry.Height - placement.Top) * scaleY), placement.LinkTarget!));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(regions.ToArray());
    }

    private readonly IReadOnlyList<PdfReadNavigationEntry> _navigation = Array.AsReadOnly(Array.Empty<PdfReadNavigationEntry>());
    private readonly NavigationFailure? _navigationFailure;
    private sealed record NavigationFailure(string Message, bool Unsupported);

    /// <summary>Reads the outline captured from the original native source without fetching its targets.</summary>
    public IReadOnlyList<PdfReadNavigationEntry> ReadNavigation(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_navigationFailure is { } failure)
        {
            if (failure.Unsupported) throw new NotSupportedException(failure.Message);
            throw new InvalidDataException(failure.Message);
        }
        return _navigation;
    }

    /// <summary>Resolves a native target without fetching resources. Relative DjVu targets require a current page.</summary>
    public PdfReadNavigationDestination ResolveNavigationTarget(string target, int? currentPageIndex = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        if (currentPageIndex is int current && (current < 0 || current >= PageCount))
            throw new ArgumentOutOfRangeException(nameof(currentPageIndex));
        if (_layout is not null)
        {
            var destination = _layout.ResolveNavigationTarget(target, cancellationToken);
            return new(destination.PageIndex, destination.Left, destination.Top);
        }
        if (_djvu is not null)
            return new(_djvu.ResolveNavigationTarget(target, currentPageIndex, cancellationToken), 0, 0);
        throw new NotSupportedException("Native target resolution is not implemented for this format.");
    }

    private static (IReadOnlyList<PdfReadNavigationEntry> Entries, NavigationFailure? Failure) CaptureNavigation(
        Func<IEnumerable<PdfReadNavigationEntry>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var entries = Array.AsReadOnly(read().ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            return (entries, null);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (Array.AsReadOnly(Array.Empty<PdfReadNavigationEntry>()),
                new NavigationFailure(error.Message[..Math.Min(error.Message.Length, 512)], error is NotSupportedException));
        }
    }

    private static PdfReadNavigationEntry CaptureEntry(EpubNavigationEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(entry.Label, entry.Target, entry.Children.Select(child => CaptureEntry(child, cancellationToken)));
    }

    private static PdfReadNavigationEntry CaptureEntry(DjvuNavigationEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(entry.Label, entry.Target, entry.Children.Select(child => CaptureEntry(child, cancellationToken)));
    }
}
