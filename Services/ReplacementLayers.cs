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

    /// <summary>The marks a copy takes: everything selected, and the other layer of each replacement in it, linked or
    /// not, so a pasted replacement is the whole object rather than an orphaned text box. The list follows the
    /// page's stack order, because a paste adds marks in list order and a cover added after its text hides it.
    /// Marks that aren't on the page lead, in selection order. (#467)</summary>
    public static IReadOnlyList<PageAnnotation> CopyMembers(IEnumerable<PageAnnotation> selection, IReadOnlyList<PageAnnotation> page)
    {
        var copied = new List<PageAnnotation>();
        foreach (var item in selection)
        {
            if (!copied.Contains(item)) copied.Add(item);
            if (FindPair(item, page) is { } pair)
                foreach (PageAnnotation layer in new PageAnnotation[] { pair.Cover, pair.Text })
                    if (!copied.Contains(layer)) copied.Add(layer);
        }
        int StackIndex(PageAnnotation item)
        {
            for (int index = 0; index < page.Count; index++)
                if (ReferenceEquals(page[index], item)) return index;
            return -1;
        }
        return [.. copied.OrderBy(StackIndex)];
    }

    /// <summary>The members of a selection free to move. A layer locked on its own stays put while its partner
    /// moves; locking the whole replacement holds both. (#467)</summary>
    public static IReadOnlyList<PageAnnotation> MovableMembers(IEnumerable<PageAnnotation> selection) =>
        [.. selection.Distinct().Where(CanMove)];

    /// <summary>
    /// The marks a move of this selection takes. A linked replacement moves as one; a replacement layer locked on its
    /// own hands the move to its other layer, linked or not, so with the text box's hand off its cover moves; locked
    /// layers stay put. An unlinked layer that is free moves alone. (#467)
    /// </summary>
    public static IReadOnlyList<PageAnnotation> MoveMembers(IEnumerable<PageAnnotation> selection, IReadOnlyList<PageAnnotation> page)
    {
        var moving = new List<PageAnnotation>();
        foreach (var item in selection)
        {
            foreach (var member in SelectionMembers(item, page).DefaultIfEmpty(item))
                if (!moving.Contains(member)) moving.Add(member);
            if (!CanMove(item) && FindPair(item, page) is { } pair)
            {
                PageAnnotation partner = ReferenceEquals(item, pair.Text) ? pair.Cover : pair.Text;
                if (!moving.Contains(partner)) moving.Add(partner);
            }
        }
        return MovableMembers(moving);
    }

    /// <summary>Whether a hidden layer of a replacement still draws a faint outline on screen and takes a click, so
    /// it can be found and shown again. It stays out of saves, prints and reading. (#467)</summary>
    public static bool ShowsWhenHidden(PageAnnotation annotation) => HasPairState(annotation) && !IsVisible(annotation);

    private static bool HasPairState(PageAnnotation annotation) =>
        annotation is TextAnnotation or CoverAnnotation && !string.IsNullOrEmpty(annotation.PairId);
}
