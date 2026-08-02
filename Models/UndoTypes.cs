using System.Collections.Generic;

namespace KillerPDF
{
    // The undo stack's entry type. Each entry is either an annotation removal or a full document
    // snapshot; AnnotationGroup removes a specific set in one step (a text edit = cover + text).
    //
    // TOP-LEVEL, not nested in MainWindow. The code that pushes undo entries - Annotations.cs and
    // TextEditing.cs - lives in KillerPDF.Controls, where a type nested in MainWindow only spells
    // as MainWindow.UndoEntry; that would mean qualifying roughly 30 call sites for no gain. As
    // top-level types in KillerPDF they resolve unqualified from the child namespace too.
    //
    // This also retires the CS0052 chain that made them internal in the first place: DocumentSession
    // had to be internal for the render cache, its UndoStack field is Stack<UndoEntry>, and a field
    // cannot be more accessible than its type.

    internal enum UndoKind { Annotation, Document, StampBatch, ClearAnnotations, AnnotationGroup, PageSnapshot }

    internal readonly record struct UndoEntry(
        UndoKind Kind,
        int PageIdx = -1,
        byte[]? DocBytes = null,
        bool WasDirty = false,
        int[]? Pages = null,
        PageAnnotation? Annot = null,
        Dictionary<int, List<PageAnnotation>>? AnnotSnapshot = null,
        List<PageAnnotation>? AnnotGroup = null);
}
