using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Controls;

/// <summary>
/// Remembers the named face inside one tab container, so asking for it on every layout pass does not walk the tab's
/// visual tree each time. A different container, or a face that a rebuilt template has detached from the container,
/// is searched for again. A container with no such element yet is not remembered, so it is searched again until its
/// template has been applied.
/// </summary>
internal sealed class TabFaceCache(string name)
{
    FrameworkElement? container;
    FrameworkElement? face;

    /// <summary>How many tree searches have run, for tests.</summary>
    internal int Searches { get; private set; }

    internal FrameworkElement? Find(FrameworkElement? tab)
    {
        if (tab is null)
        {
            container = face = null;
            return null;
        }
        if (ReferenceEquals(tab, container) && face is not null && face.IsDescendantOf(tab)) return face;
        Searches++;
        container = tab;
        face = Search(tab);
        return face;
    }

    FrameworkElement? Search(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var childName } element && childName == name) return element;
            if (Search(child) is { } found) return found;
        }
        return null;
    }
}
