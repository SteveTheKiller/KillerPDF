using System.Collections;
using System.Windows.Controls;

namespace KillerPDF.Controls
{
    /// <summary>The sidebar thumbnail list and the total above it, moved together.
    ///
    /// They describe one document between them, so they are set in one place. They used to be set
    /// apart: the only writer of a real total was BootstrapDocumentView, on the render path, which
    /// a pane switch and a temp reload both skip on purpose. That left the sidebar listing one
    /// document's pages under another document's count.
    ///
    /// The count comes off the bound list rather than off the document, so the number cannot
    /// disagree with the thumbnails it sits above, whichever pane is in context.</summary>
    internal sealed class SidebarPageBinding(ItemsControl list, TextBlock total)
    {
        /// <summary>What the total reads with no document behind it. The two teardown paths in
        /// Shell/FileOperations.cs and PdfViewer.Tabs.cs still write this inline.</summary>
        internal const string Empty = "/ -";

        /// <param name="items">The page list, or null with no document.</param>
        /// <param name="provisional">True while a book is still being paginated: the total reads "/ 340+" and
        /// settles when the last page is known.</param>
        internal void Show(IList? items, bool provisional = false)
        {
            // Re-binding the same list rebuilds every container, so only assign on a real change.
            if (!ReferenceEquals(list.ItemsSource, items)) list.ItemsSource = items;
            total.Text = items is null ? Empty : Total(items.Count, provisional);
        }

        /// <summary>The page total as the footer shows it.</summary>
        internal static string Total(int count, bool provisional) => provisional ? $"/ {count}+" : $"/ {count}";
    }
}
