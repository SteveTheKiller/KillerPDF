namespace KillerPdf.Engine.Documents;

/// <summary>A category of optional-content layer change.</summary>
public enum PdfLayerChangeKind
{
    /// <summary>A layer exists only in the changed document.</summary>
    Added,
    /// <summary>A layer exists only in the original document.</summary>
    Removed,
    /// <summary>The layer's initial visibility changed.</summary>
    Visibility,
    /// <summary>The layer's locked state changed.</summary>
    Lock,
    /// <summary>The layer's print or export visibility changed.</summary>
    Usage,
    /// <summary>Optional-content configuration metadata or ordering changed.</summary>
    Configuration
}

/// <summary>One optional-content difference between two documents.</summary>
public sealed record PdfLayerChange(PdfLayerChangeKind Kind, string Name);

/// <summary>A deterministic comparison of layer registration and configuration state.</summary>
public sealed class PdfLayerComparison
{
    private PdfLayerComparison(IEnumerable<PdfLayerChange> changes) =>
        Changes = Array.AsReadOnly(changes.ToArray());

    /// <summary>Gets changes in layer-name and category order.</summary>
    public IReadOnlyList<PdfLayerChange> Changes { get; }

    /// <summary>Gets whether any layer state changed.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Compares registered layers and optional-content configurations by stable names.</summary>
    public static PdfLayerComparison Compare(PdfDocument original, PdfDocument changed)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(changed);
        PdfOptionalContentInfo before = PdfOptionalContentReader.Read(original);
        PdfOptionalContentInfo after = PdfOptionalContentReader.Read(changed);
        Dictionary<string, PdfOptionalContentGroupInfo> left = before.Groups
            .ToDictionary(group => group.Name, StringComparer.Ordinal);
        Dictionary<string, PdfOptionalContentGroupInfo> right = after.Groups
            .ToDictionary(group => group.Name, StringComparer.Ordinal);
        var changes = new List<PdfLayerChange>();
        foreach (string name in left.Keys.Union(right.Keys, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal))
        {
            if (!left.TryGetValue(name, out PdfOptionalContentGroupInfo? oldGroup))
            {
                changes.Add(new(PdfLayerChangeKind.Added, name));
                continue;
            }
            if (!right.TryGetValue(name, out PdfOptionalContentGroupInfo? newGroup))
            {
                changes.Add(new(PdfLayerChangeKind.Removed, name));
                continue;
            }
            if (oldGroup.IsInitiallyVisible != newGroup.IsInitiallyVisible)
                changes.Add(new(PdfLayerChangeKind.Visibility, name));
            if (oldGroup.IsLocked != newGroup.IsLocked)
                changes.Add(new(PdfLayerChangeKind.Lock, name));
            if (oldGroup.IsVisibleWhenPrinting != newGroup.IsVisibleWhenPrinting
                || oldGroup.IsVisibleWhenExporting != newGroup.IsVisibleWhenExporting)
                changes.Add(new(PdfLayerChangeKind.Usage, name));
        }
        if (!ConfigurationSignature(before).SequenceEqual(ConfigurationSignature(after)))
            changes.Add(new(PdfLayerChangeKind.Configuration, "Optional content"));
        return new PdfLayerComparison(changes);
    }

    private static IEnumerable<string> ConfigurationSignature(PdfOptionalContentInfo info)
    {
        Dictionary<int, string> names = info.Groups.ToDictionary(
            group => group.ObjectNumber, group => group.Name);
        return info.Configurations.Select(configuration => string.Join('|',
            configuration.IsDefault, configuration.Name, configuration.Creator,
            configuration.BaseState,
            string.Join(',', configuration.VisibleGroupObjectNumbers
                .Select(number => names[number]).Order(StringComparer.Ordinal)),
            string.Join(',', configuration.LockedGroupObjectNumbers
                .Select(number => names[number]).Order(StringComparer.Ordinal)),
            string.Join(',', configuration.DisplayOrderGroupObjectNumbers
                .Select(number => names[number]))));
    }
}
