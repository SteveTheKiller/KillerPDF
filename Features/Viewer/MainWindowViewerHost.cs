using KillerPDF.Features;

namespace KillerPDF
{
    // MainWindow's half of IViewerHost.
    //
    // Explicit implementation throughout, matching Shell/About.cs: MainWindow's own members are
    // private, and a private member cannot implement an interface. Forwarding explicitly satisfies
    // the contract without widening anything to public - a change nobody asked for.
    //
    // IShellServices (Window / Loc / SetStatus) is already implemented in Shell/About.cs and is
    // inherited through IViewerHost, so it is deliberately NOT repeated here.
    public partial class MainWindow : IViewerHost
    {
        void IViewerHost.MarkDirty() => MarkDirty();

        EditTool IViewerHost.CurrentTool => _currentTool;

        void IViewerHost.SetTool(EditTool tool) => SetTool(tool);

        // ---- Focused-viewer notifications ----------------------------------------------------
        // Single pane today, so these just drive the existing chrome directly. When there are two,
        // each body gains a "is the caller the focused viewer?" guard - the sidebar, page list and
        // status line follow focus rather than whichever pane happened to update last. Keeping the
        // calls routed through here means that guard lands in three known places instead of being
        // hunted through 84 PageList call sites. (BACKLOG.md, group C.)

        void IViewerHost.ViewerPageChanged(int pageIndex)
        {
            // Direct assignment, because that is what the 84 existing call sites do - there is no
            // SyncPageListSelection helper today. The guard avoids re-entering the selection
            // handler when the list already agrees.
            if (pageIndex < 0 || PageList is null) return;
            if (PageList.SelectedIndex != pageIndex) PageList.SelectedIndex = pageIndex;
        }

        // SyncZoomBox reads the current zoom itself rather than taking one, so the parameter is
        // unused today. It stays in the signature because with two panes the window has to know
        // WHICH viewer's zoom changed before deciding whether the toolbar box should follow.
        void IViewerHost.ViewerZoomChanged(double zoomLevel) => SyncZoomBox();

        void IViewerHost.ViewerFocused()
        {
            // Nothing to do while there is one viewer. With two panes this moves the accent halo
            // here and repoints the sidebar at the caller.
        }
    }
}
