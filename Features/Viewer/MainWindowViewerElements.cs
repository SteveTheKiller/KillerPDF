using System.Windows.Controls;
using KillerPDF.Controls;

namespace KillerPDF
{
    // The element names MainWindow.xaml used to generate, now that the document pane is a control
    // (Controls/PdfViewer.xaml).
    //
    // Same trick as the ViewerState forwarding: the names stay, the storage moves. Every
    // existing call site - DocPaneBorder.CornerRadius in Tabs.cs, DocPaneShadow.Visibility in
    // FullScreen.cs, PagePreviewPanel and MarqueeLayer across the render and annotation code -
    // compiles untouched.
    //
    // Get-only is correct here: callers mutate the ELEMENT (its margin, radius, visibility), never
    // rebind the reference.
    //
    // NOTE the one thing that could NOT be forwarded: the card's MARGIN. It lives on the control
    // now, not on PaneBorder, because the control is what the layout positions. ApplySidebarSide
    // and ApplyFullScreen set ActiveViewer.Margin directly.
    public partial class MainWindow
    {
        private Border DocPaneShadow => ActiveViewer.PaneShadowBorder;
        private Border DocPaneBorder => ActiveViewer.PaneCardBorder;
        private Grid DocPaneContent => ActiveViewer.ContentHost;
        // 7 bare uses in Tabs.cs and Viewport.cs. Note this is a SEPARATE member from the
        // underscore-prefixed _pageContentGrid, which forwards to ViewerState - the code uses both
        // spellings, so both have to resolve.
        private Grid PageContentGrid => ActiveViewer.PageGrid;
        private ScrollViewer PagePreviewPanel => ActiveViewer.PreviewScroller;
        private Border DropZone => ActiveViewer.DropSurface;
        private Border RecentFilesBox => ActiveViewer.RecentBox;
        private ItemsControl RecentFilesList => ActiveViewer.RecentList;
        private Canvas MarqueeLayer => ActiveViewer.Marquee;
        private Border DocSurfacePad => ActiveViewer.SurfacePad;
        private System.Windows.Media.ImageBrush GrainBrush => ActiveViewer.Grain;
    }
}
