using System.IO;
using KillerPDF.Services;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    internal CurrentSearchSnapshot CaptureCurrentSearchSnapshot()
    {
        CommitActiveTextBox();
        var text = new List<SearchTextSource>();
        var covers = new List<SearchTextSource>();
        var unreadable = new List<int>();
        var session = EnsureEngineDocumentSession();
        var pages = session.Pages;
        foreach (var (pageIndex, annotations) in _annotations)
        {
            if (pageIndex < 0 || pageIndex >= pages.Count) continue;
            EnsureRenderDims(pageIndex, pages[pageIndex].Width, pages[pageIndex].Height);
            if (!TryCanvasFrame(pageIndex, out var frame)) continue;
            foreach (CoverAnnotation cover in annotations.OfType<CoverAnnotation>())
            {
                var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(cover.Bounds,
                    frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation);
                covers.Add(new SearchTextSource(pageIndex, left, bottom, right, top, "", cover.HistoryId));
            }
            foreach (TextAnnotation annotation in annotations.OfType<TextAnnotation>())
            {
                var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(AnnotBounds(annotation),
                    frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation);
                var footprint = AnnotBounds(annotation);
                if (annotation.PairId.Length > 0)
                    foreach (var cover in annotations.OfType<CoverAnnotation>().Where(cover => cover.PairId == annotation.PairId))
                        footprint.Union(cover.Bounds);
                var (redactLeft, redactBottom, redactRight, redactTop) = RedactionGeometry.CanvasToPage(footprint,
                    frame.PdfWidth, frame.PdfHeight, frame.RenderWidth, frame.RenderHeight, frame.Rotation);
                text.Add(new SearchTextSource(pageIndex, left, bottom, right, top, annotation.Content, annotation.HistoryId)
                    { RedactionBounds = new(redactLeft, redactBottom, redactRight, redactTop) });
            }
        }
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            try
            {
                foreach (var field in session.ReadFormWidgets(pageIndex, forDisplay: true))
                {
                    bool isText = field.FieldKind == KillerPdf.Engine.Documents.PdfFormFieldKind.Text;
                    if (!isText && field.FieldKind != KillerPdf.Engine.Documents.PdfFormFieldKind.Choice) continue;
                    string value;
                    if (isText) value = _formTextValues.TryGetValue(field.FieldName, out string? pending) ? pending : field.Value;
                    else
                    {
                        IReadOnlyList<string> selected = _formMultiChoiceValues.TryGetValue(field.FieldName, out var multi) ? multi
                            : _formChoiceValues.TryGetValue(field.FieldName, out string? single) ? [single]
                            : field.Values.Count > 0 ? field.Values : [field.Value];
                        value = string.Join("\n", selected.Select(item => field.Options.FirstOrDefault(option => option.ExportValue == item)?.DisplayValue ?? item));
                    }
                    bool password = isText && (field.Flags & 8192) != 0;
                    text.Add(new SearchTextSource(pageIndex, field.Left - field.PageBoxLeft, field.Bottom - field.PageBoxBottom,
                        field.Right - field.PageBoxLeft, field.Top - field.PageBoxBottom, value, Guid.Empty, field.FieldName,
                        Editable: isText && !password && (field.Flags & 1) == 0)
                        { WidgetId = $"field:{pageIndex}:{field.AnnotationIndex}", Findable = !password, MaximumLength = field.MaximumLength });
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException)) { unreadable.Add(pageIndex); }
        }
        return new CurrentSearchSnapshot(File.ReadAllBytes(_currentFile!), text.AsReadOnly())
            { Covers = covers.AsReadOnly(), UnreadablePages = unreadable.AsReadOnly(), Protection = _protection };
    }
}
