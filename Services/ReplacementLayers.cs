namespace KillerPDF.Services;

public enum ReplacementLayer { Group, Cover, Text }

public sealed record ReplacementPair(TextAnnotation Text, CoverAnnotation Cover);

public static class ReplacementLayers
{
    public static bool CanMoveTogether(IEnumerable<PageAnnotation> members)
    {
        bool found = false;
        foreach (var member in members)
        {
            found = true;
            if (!CanMove(member)) return false;
        }
        return found;
    }

    public static void SetLinked(ReplacementPair pair, bool linked) =>
        pair.Text.PairLinked = pair.Cover.PairLinked = linked;

    /// <summary>Whether a row of the layer controls carries the link button. The link belongs to the pair, not to
    /// one layer, so only one row shows it.</summary>
    public static bool ShowsLinkToggle(ReplacementLayer layer) => layer == ReplacementLayer.Group;

    /// <summary>Returns the lead first, followed by connected members in page stack order.</summary>
    public static IReadOnlyList<PageAnnotation> SelectionMembers(PageAnnotation lead, IReadOnlyList<PageAnnotation> page)
    {
        if (!page.Any(item => ReferenceEquals(item, lead))) return [];
        var members = new HashSet<PageAnnotation> { lead };
        var pending = new Queue<PageAnnotation>();
        pending.Enqueue(lead);
        while (pending.TryDequeue(out var current))
        {
            if (!string.IsNullOrEmpty(current.GroupId))
                foreach (var item in page)
                    if (item.PageIndex == lead.PageIndex && item.GroupId == current.GroupId && members.Add(item))
                        pending.Enqueue(item);

            var pair = FindPair(current, page);
            if (pair is not { Text.PairLinked: true, Cover.PairLinked: true }) continue;
            PageAnnotation partner = ReferenceEquals(current, pair.Text) ? pair.Cover : pair.Text;
            if (members.Add(partner)) pending.Enqueue(partner);
        }

        var ordered = new List<PageAnnotation> { lead };
        foreach (var item in page)
            if (!ReferenceEquals(item, lead) && members.Remove(item)) ordered.Add(item);
        return ordered;
    }

    public static ReplacementPair? FindPair(PageAnnotation lead, IReadOnlyList<PageAnnotation> page)
    {
        if (!HasPairState(lead) || !page.Any(item => ReferenceEquals(item, lead)))
            return null;

        TextAnnotation? text = null;
        CoverAnnotation? cover = null;
        foreach (var item in page.Distinct())
        {
            if (item.PageIndex != lead.PageIndex || item.PairId != lead.PairId)
                continue;
            if (item is TextAnnotation candidateText)
            {
                if (text != null) return null;
                text = candidateText;
            }
            else if (item is CoverAnnotation candidateCover)
            {
                if (cover != null) return null;
                cover = candidateCover;
            }
        }
        return text != null && cover != null ? new ReplacementPair(text, cover) : null;
    }

    public static void SetMovementAllowed(ReplacementPair pair, ReplacementLayer layer, bool allowed)
    {
        switch (layer)
        {
            case ReplacementLayer.Group:
                pair.Text.PairMovementLocked = pair.Cover.PairMovementLocked = !allowed;
                break;
            case ReplacementLayer.Cover:
                pair.Cover.IsMovementLocked = !allowed;
                break;
            case ReplacementLayer.Text:
                pair.Text.IsMovementLocked = !allowed;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layer));
        }
    }

    public static bool CanMove(PageAnnotation annotation) =>
        !annotation.IsMovementLocked && (!HasPairState(annotation) || !annotation.PairMovementLocked);

    public static void SetVisibility(ReplacementPair pair, ReplacementLayer layer, bool visible)
    {
        switch (layer)
        {
            case ReplacementLayer.Group:
                pair.Text.PairVisible = pair.Cover.PairVisible = visible;
                break;
            case ReplacementLayer.Cover:
                pair.Cover.IsVisible = visible;
                break;
            case ReplacementLayer.Text:
                pair.Text.IsVisible = visible;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layer));
        }
    }

    public static bool IsVisible(PageAnnotation annotation) =>
        annotation.IsVisible && (!HasPairState(annotation) || annotation.PairVisible);

    private static bool HasPairState(PageAnnotation annotation) =>
        annotation is TextAnnotation or CoverAnnotation && !string.IsNullOrEmpty(annotation.PairId);
}
