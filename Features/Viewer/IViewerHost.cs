namespace KillerPDF.Features
{
    /// <summary>
    /// What a document viewer needs from the window around it.
    ///
    /// EXTENDS IShellServices, per the family rule in that file - the shell implements Window /
    /// Loc / SetStatus once, not once per feature. Those three cover the viewer's two heaviest
    /// call groups on their own (Loc 70 uses, SetStatus 68) plus the modal-dialog owner that
    /// TextEditing and Links need for KillerDialog (3 uses).
    ///
    /// DERIVED FROM MEASUREMENT, not guessed: every member here came from grepping what the nine
    /// files bound for the viewer control (Viewport, Zoom, Annotations, Selection, TextEditing,
    /// Crop, Links, Forms, PageSelection) actually reach for on MainWindow today. Use counts are
    /// noted per member so the cost of each is visible.
    ///
    /// That audit split the coupling into three groups, and only the first belongs here:
    ///
    ///  A. HOST SERVICES - chrome and app-level services the viewer asks for. This interface.
    ///
    ///  B. PER-DOCUMENT STATE - _doc (120 uses), _currentFile (33), _annotations (56),
    ///     _renderDims (34), _pageRotations (11), _undoStack (4). These are NOT host services:
    ///     they already ride in DocumentSession, which tab switching swaps by reference. The
    ///     viewer will hold its own active session and read them from it. Routing them through
    ///     the host would be a mistake that undoes the session design.
    ///
    ///  C. PageList - 84 uses, the single biggest coupling, and a DESIGN DECISION rather than a
    ///     mechanical one. The sidebar's page-thumbnail list is window chrome, but the viewer
    ///     drives it constantly (selection sync, scroll-to-page). With two panes there is still
    ///     ONE sidebar, so it has to follow the FOCUSED pane. The viewer therefore must not touch
    ///     PageList directly; it raises the notifications below and the window decides whether
    ///     this viewer is the focused one before acting. Getting this wrong is how the two panes
    ///     would end up fighting over the sidebar.
    /// </summary>
    internal interface IViewerHost : IShellServices
    {
        // ── Chrome the viewer updates (group A) ─────────────────────────────────────────────
        /// <summary>Mark the active document dirty (unsaved changes). 32 uses. Stays a host
        /// service even though dirtiness is per-document, because it also drives window chrome -
        /// the tab's dirty dot and the title bar.</summary>
        void MarkDirty();

        // PushUndo is deliberately NOT here, despite 9 uses. Undo is per-document state (group B):
        // _undoStack rides in DocumentSession, so the viewer pushes onto the session it is showing
        // rather than asking the window. It was in this interface briefly and the compiler caught
        // it - UndoEntry is a private nested record struct on MainWindow, and widening it plus its
        // UndoKind enum just to satisfy the signature would have been the wrong fix for a member
        // that should not have been here. (2026-08-01.)

        /// <summary>The editing tool in force. 23 uses. One toolbar serves both panes, so this
        /// stays window-level rather than per-viewer.</summary>
        EditTool CurrentTool { get; }

        /// <summary>Switch tools - Crop uses this to drop back to Select when it finishes. 2 uses.</summary>
        void SetTool(EditTool tool);

        // ── Notifications, so the window can update chrome for the FOCUSED viewer only ───────
        // These replace the viewer poking at PageList / ZoomBox / PageLabel / StatusText itself.
        /// <summary>This viewer scrolled or paged to a different page.</summary>
        void ViewerPageChanged(int pageIndex);

        /// <summary>This viewer's zoom or fit mode changed (updates the zoom box). 16 uses of
        /// ZoomBox today.</summary>
        void ViewerZoomChanged(double zoomLevel);

        /// <summary>This viewer took focus - the window repoints the sidebar, page list and
        /// status line at it, and moves the accent halo.</summary>
        void ViewerFocused();
    }
}
