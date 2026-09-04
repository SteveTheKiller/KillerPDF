using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>
/// Adds annotations to existing pages through a byte-preserving incremental revision.
/// Original page contents and every source byte remain untouched.
/// </summary>
public sealed class PdfIncrementalAnnotationEditor
{
    private static readonly PdfName AnnotsName = new("Annots"u8);
    private static readonly PdfName NamesName = new("Names"u8);
    private static readonly PdfName DestsName = new("Dests"u8);
    private static readonly PdfName EmbeddedFilesName = new("EmbeddedFiles"u8);
    private static readonly PdfName StructTreeRootName = new("StructTreeRoot"u8);
    private static readonly PdfName StructureKidsName = new("K"u8);
    private static readonly PdfName ParentTreeName = new("ParentTree"u8);
    private static readonly PdfName ParentTreeNextKeyName = new("ParentTreeNextKey"u8);
    private static readonly PdfName NamespacesName = new("Namespaces"u8);
    private static readonly PdfName VersionName = new("Version"u8);
    private static readonly PdfName MetadataName = new("Metadata"u8);

    private readonly PdfDocument _document;
    private readonly PdfPageTree _tree;
    private readonly IReadOnlyList<PdfPageTreeEntry> _pages;
    private readonly List<PendingAnnotation> _annotations = [];
    private readonly List<PendingRemoval> _removals = [];
    private readonly List<PendingAnnotationUpdate> _updates = [];

    /// <summary>Initializes a byte-preserving annotation editor for an opened document.</summary>
    public PdfIncrementalAnnotationEditor(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _tree = PdfPageTree.Read(document);
        _pages = _tree.Pages;
    }

    /// <summary>Gets the number of pages available for annotation editing.</summary>
    public int PageCount => _pages.Count;

    /// <summary>Gets whether this editor contains pending annotation changes.</summary>
    public bool HasChanges => _annotations.Count > 0 || _removals.Count > 0 || _updates.Count > 0;

    /// <summary>Removes visual styling from every indirect link annotation.</summary>
    public PdfIncrementalAnnotationEditor StripLinkAppearances()
    {
        for (int pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            PdfPageTreeEntry page = _pages[pageIndex];
            if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotsValue)
                || ResolveValue(annotsValue,
                    $"Page {pageIndex + 1} /Annots value") is not PdfArray annotations)
                continue;
            for (int annotationIndex = 0; annotationIndex < annotations.Count; annotationIndex++)
            {
                ResolvedValue resolved = ResolveWithIdentity(annotations[annotationIndex],
                    $"Page {pageIndex + 1} annotation {annotationIndex + 1}");
                if (resolved.FinalReference is not PdfIndirectReference reference
                    || resolved.Value is not PdfDictionary annotation
                    || !annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
                    || ResolveValue(subtypeValue,
                        $"Page {pageIndex + 1} annotation subtype") is not PdfName subtype
                    || subtype.ValueAsLatin1() != "Link")
                    continue;
                _updates.Add(new PendingAnnotationUpdate(
                    pageIndex, $"annotation {annotationIndex + 1}", reference,
                    annotation, false, null, false, null, true));
            }
        }
        return this;
    }

    /// <summary>Removes the uniquely named annotation from a page.</summary>
    public PdfIncrementalAnnotationEditor RemoveAnnotation(
        int pageIndex, string name)
    {
        ValidatePage(pageIndex);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "An annotation name is required.", nameof(name));
        if (_removals.Any(value => string.Equals(
                value.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException(
                $"Annotation '{name}' is already scheduled for removal.", nameof(name));
        PendingRemoval removal = FindNamedAnnotation(pageIndex, name);
        EnsureNotPendingUpdate(removal);
        _removals.Add(removal);
        return this;
    }

    /// <summary>Removes an annotation by its zero-based position in the page annotation array.</summary>
    public PdfIncrementalAnnotationEditor RemoveAnnotationAt(
        int pageIndex, int annotationIndex)
    {
        ValidatePage(pageIndex);
        PdfPageTreeEntry page = _pages[pageIndex];
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotsValue)
            || ResolveValue(annotsValue,
                $"Page {pageIndex + 1} /Annots value") is not PdfArray annotations)
            throw new ArgumentOutOfRangeException(nameof(annotationIndex));
        if ((uint)annotationIndex >= (uint)annotations.Count)
            throw new ArgumentOutOfRangeException(nameof(annotationIndex));
        PendingRemoval removal = ReadRemovalTarget(
            pageIndex, annotations[annotationIndex],
            $"annotation {annotationIndex + 1}");
        if (removal.Dictionary.TryGetValue(Name("Subtype"),
                out PdfObject? subtypeValue)
            && ResolveValue(subtypeValue,
                $"Page {pageIndex + 1} annotation subtype")
                is PdfName subtype
            && subtype.ValueAsLatin1() == "Popup")
            throw new InvalidOperationException(
                "A popup annotation must be removed through its parent annotation.");
        if (_removals.Any(value => SameIdentity(
                value.Reference, removal.Reference)))
            throw new ArgumentException(
                "The annotation is already scheduled for removal.",
                nameof(annotationIndex));
        EnsureNotPendingUpdate(removal);
        _removals.Add(removal);
        return this;
    }

    /// <summary>Changes or clears the contents of a uniquely named annotation.</summary>
    public PdfIncrementalAnnotationEditor SetAnnotationContents(
        int pageIndex, string name, string? contents)
    {
        PendingRemoval target = FindUpdateTarget(pageIndex, name);
        AddContentsUpdate(target, contents, nameof(name));
        return this;
    }

    /// <summary>Changes or clears the lifecycle metadata of a uniquely named annotation.</summary>
    public PdfIncrementalAnnotationEditor SetAnnotationMetadata(
        int pageIndex, string name, PdfAnnotationMetadata? metadata)
    {
        PendingRemoval target = FindUpdateTarget(pageIndex, name);
        AddMetadataUpdate(target, metadata, nameof(name));
        return this;
    }

    /// <summary>Changes or clears annotation contents by page-array position.</summary>
    public PdfIncrementalAnnotationEditor SetAnnotationContentsAt(
        int pageIndex, int annotationIndex, string? contents)
    {
        PendingRemoval target = FindIndexedUpdateTarget(pageIndex, annotationIndex);
        AddContentsUpdate(target, contents, nameof(annotationIndex));
        return this;
    }

    /// <summary>Changes or clears annotation lifecycle metadata by page-array position.</summary>
    public PdfIncrementalAnnotationEditor SetAnnotationMetadataAt(
        int pageIndex, int annotationIndex, PdfAnnotationMetadata? metadata)
    {
        PendingRemoval target = FindIndexedUpdateTarget(pageIndex, annotationIndex);
        AddMetadataUpdate(target, metadata, nameof(annotationIndex));
        return this;
    }

    /// <summary>Changes the standard icon of a file-attachment annotation.</summary>
    public PdfIncrementalAnnotationEditor SetFileAttachmentIconAt(
        int pageIndex, int annotationIndex, PdfFileAttachmentIcon icon)
    {
        if (!Enum.IsDefined(icon))
            throw new ArgumentOutOfRangeException(nameof(icon));
        PendingRemoval target = FindIndexedUpdateTarget(pageIndex, annotationIndex);
        if (!target.Dictionary.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
            || ResolveValue(subtypeValue,
                $"Annotation '{target.Name}' subtype") is not PdfName subtype
            || subtype.ValueAsLatin1() != "FileAttachment")
            throw new InvalidOperationException(
                $"Annotation '{target.Name}' is not a file-attachment annotation.");
        if (_updates.Any(value => SameIdentity(value.Reference, target.Reference)
                && value.UpdateFileAttachmentIcon))
            throw new ArgumentException(
                $"Annotation '{target.Name}' already has a pending icon update.",
                nameof(annotationIndex));
        _updates.Add(new PendingAnnotationUpdate(target.PageIndex, target.Name,
            target.Reference, target.Dictionary, false, null, false, null, false,
            UpdateFileAttachmentIcon: true, FileAttachmentIcon: icon));
        return this;
    }

    /// <summary>Changes a link annotation to open an absolute URI.</summary>
    public PdfIncrementalAnnotationEditor SetLinkUriAt(
        int pageIndex, int annotationIndex, string uri)
    {
        PendingRemoval target = FindIndexedUpdateTarget(pageIndex, annotationIndex);
        EnsureLinkAnnotation(target);
        AddLinkTargetUpdate(target, PendingLinkTarget.Uri,
            PdfLinkAnnotationFactory.ValidateUri(uri), null, nameof(annotationIndex));
        return this;
    }

    /// <summary>Changes a link annotation to target a page in this document.</summary>
    public PdfIncrementalAnnotationEditor SetLinkDestinationAt(
        int pageIndex, int annotationIndex, int destinationPageIndex,
        PdfDestination? destination = null)
    {
        ValidatePage(destinationPageIndex);
        PendingRemoval target = FindIndexedUpdateTarget(pageIndex, annotationIndex);
        EnsureLinkAnnotation(target);
        AddLinkTargetUpdate(target, PendingLinkTarget.Page, null,
            (destinationPageIndex, destination ?? PdfDestination.FitPage()),
            nameof(annotationIndex));
        return this;
    }

    /// <summary>Changes a link annotation to target an existing named destination.</summary>
    public PdfIncrementalAnnotationEditor SetLinkNamedDestinationAt(
        int pageIndex, int annotationIndex, string destinationName)
    {
        if (string.IsNullOrWhiteSpace(destinationName)
            || !HasNamedDestination(destinationName))
            throw new ArgumentException(
                "The named destination has not been defined.", nameof(destinationName));
        PendingRemoval target = FindIndexedUpdateTarget(pageIndex, annotationIndex);
        EnsureLinkAnnotation(target);
        AddLinkTargetUpdate(target, PendingLinkTarget.Named,
            destinationName, null, nameof(annotationIndex));
        return this;
    }

    /// <summary>Adds a text note with optional popup, reply, and workflow state.</summary>
    public PdfIncrementalAnnotationEditor AddTextNote(
        int pageIndex, double x, double y, string contents,
        PdfRgbColor? color = null, bool open = false, double size = 24,
        PdfAnnotationMetadata? annotationMetadata = null,
        PdfTextNoteIcon icon = PdfTextNoteIcon.Note,
        PdfTextNoteState? state = null, string? name = null,
        string? inReplyTo = null,
        PdfAnnotationReplyType replyType = PdfAnnotationReplyType.Reply,
        PdfAnnotationPopup? popup = null)
    {
        ValidatePage(pageIndex);
        ArgumentNullException.ThrowIfNull(contents);
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(y, nameof(y));
        if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!Enum.IsDefined(icon)) throw new ArgumentOutOfRangeException(nameof(icon));
        if (state is not null && !Enum.IsDefined(state.Value))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (!Enum.IsDefined(replyType))
            throw new ArgumentOutOfRangeException(nameof(replyType));
        Dictionary<string, PdfIndirectReference> existingNames =
            ExistingAnnotationNames();
        foreach (PendingRemoval removal in _removals)
            existingNames.Remove(removal.Name);
        IEnumerable<string> pendingNames = _annotations.OfType<PendingTextNote>()
            .Where(note => note.Name is not null).Select(note => note.Name!);
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException(
                    "Annotation names cannot be empty.", nameof(name));
            if (existingNames.ContainsKey(name)
                || pendingNames.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"A text-note annotation named '{name}' already exists.",
                    nameof(name));
        }
        if (inReplyTo is not null)
        {
            if (string.IsNullOrWhiteSpace(inReplyTo))
                throw new ArgumentException(
                    "Reply targets cannot be empty.", nameof(inReplyTo));
            if (!existingNames.ContainsKey(inReplyTo)
                && !pendingNames.Contains(inReplyTo, StringComparer.Ordinal))
                throw new ArgumentException(
                    $"The reply target '{inReplyTo}' must name an existing or earlier text-note annotation.",
                    nameof(inReplyTo));
        }
        else if (replyType != PdfAnnotationReplyType.Reply)
            throw new ArgumentException(
                "A grouped reply requires an annotation target.", nameof(replyType));
        _annotations.Add(new PendingTextNote(
            pageIndex, x, y, size, contents, color ?? PdfRgbColor.NoteYellow,
            open, annotationMetadata, icon, state, name, inReplyTo,
            replyType, popup));
        return this;
    }

    /// <summary>Adds a highlight over a rectangular text region.</summary>
    public PdfIncrementalAnnotationEditor AddHighlight(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 0.35,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Highlight, pageIndex, x, y, width, height,
            contents, color ?? PdfRgbColor.Yellow, opacity, annotationMetadata);

    /// <summary>Adds a highlight over one or more text quadrilaterals.</summary>
    public PdfIncrementalAnnotationEditor AddHighlight(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 0.35,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Highlight, pageIndex, quads,
            contents, color ?? PdfRgbColor.Yellow, opacity, annotationMetadata);

    /// <summary>Adds an underline over a rectangular text region.</summary>
    public PdfIncrementalAnnotationEditor AddUnderline(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Underline, pageIndex, x, y, width, height,
            contents, color ?? new PdfRgbColor(0, 0.35, 0.9), opacity,
            annotationMetadata);

    /// <summary>Adds an underline over one or more text quadrilaterals.</summary>
    public PdfIncrementalAnnotationEditor AddUnderline(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Underline, pageIndex, quads,
            contents, color ?? new PdfRgbColor(0, 0.35, 0.9), opacity,
            annotationMetadata);

    /// <summary>Adds a strikeout over a rectangular text region.</summary>
    public PdfIncrementalAnnotationEditor AddStrikeOut(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.StrikeOut, pageIndex, x, y, width, height,
            contents, color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity,
            annotationMetadata);

    /// <summary>Adds a strikeout over one or more text quadrilaterals.</summary>
    public PdfIncrementalAnnotationEditor AddStrikeOut(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.StrikeOut, pageIndex, quads,
            contents, color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity,
            annotationMetadata);

    /// <summary>Adds a squiggly underline over a rectangular text region.</summary>
    public PdfIncrementalAnnotationEditor AddSquiggly(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Squiggly, pageIndex, x, y, width, height,
            contents, color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity,
            annotationMetadata);

    /// <summary>Adds a squiggly underline over one or more text quadrilaterals.</summary>
    public PdfIncrementalAnnotationEditor AddSquiggly(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Squiggly, pageIndex, quads,
            contents, color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity,
            annotationMetadata);

    /// <summary>Adds a free-text annotation with an embedded font and optional callout.</summary>
    public PdfIncrementalAnnotationEditor AddFreeText(
        int pageIndex, double x, double y, double width, double height,
        string contents, TrueTypeFont font, double fontSize = 12,
        PdfRgbColor? textColor = null, PdfRgbColor? fillColor = null,
        PdfRgbColor? borderColor = null, double borderWidth = 1, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null,
        PdfTextAlignment alignment = PdfTextAlignment.Left,
        IReadOnlyList<double>? dashPattern = null,
        PdfFreeTextIntent intent = PdfFreeTextIntent.FreeText,
        IReadOnlyList<PdfPoint>? calloutLine = null,
        PdfLineEndingStyle calloutEnding = PdfLineEndingStyle.OpenArrow)
    {
        ValidatePage(pageIndex);
        ValidateRectangle(x, y, width, height);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(font);
        if (!double.IsFinite(fontSize) || fontSize <= 0) throw new ArgumentOutOfRangeException(nameof(fontSize));
        ValidateStroke(borderWidth, opacity);
        if (!Enum.IsDefined(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (!Enum.IsDefined(intent))
            throw new ArgumentOutOfRangeException(nameof(intent));
        if (!Enum.IsDefined(calloutEnding))
            throw new ArgumentOutOfRangeException(nameof(calloutEnding));
        if (intent == PdfFreeTextIntent.Callout
            && calloutLine?.Count is not (2 or 3))
            throw new ArgumentException(
                "Callout free text requires two or three callout points.",
                nameof(calloutLine));
        if (intent != PdfFreeTextIntent.Callout && calloutLine is not null)
            throw new ArgumentException(
                "Callout points require callout free-text intent.",
                nameof(calloutLine));
        double[]? dash = ValidateDashPattern(dashPattern);
        ValidateDrawableText(font, contents, nameof(contents));
        _annotations.Add(new PendingFreeText(
            pageIndex, x, y, width, height, contents, font, fontSize,
            textColor ?? new PdfRgbColor(0, 0, 0), fillColor,
            borderColor ?? new PdfRgbColor(0, 0, 0), borderWidth, opacity,
            annotationMetadata, alignment, dash, intent,
            calloutLine?.ToArray(), calloutEnding));
        return this;
    }

    /// <summary>Adds a line annotation with optional endpoint symbols and dimension intent.</summary>
    public PdfIncrementalAnnotationEditor AddLine(
        int pageIndex, PdfPoint start, PdfPoint end, PdfRgbColor? color = null,
        double lineWidth = 1, double opacity = 1, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null,
        PdfLineEndingStyle startEnding = PdfLineEndingStyle.None,
        PdfLineEndingStyle endEnding = PdfLineEndingStyle.None,
        PdfRgbColor? interiorColor = null,
        PdfLineAnnotationIntent? intent = null,
        PdfMeasurementProfile? measurement = null)
    {
        ValidatePage(pageIndex);
        ValidateStroke(lineWidth, opacity);
        double[]? dash = ValidateDashPattern(dashPattern);
        if (!Enum.IsDefined(startEnding))
            throw new ArgumentOutOfRangeException(nameof(startEnding));
        if (!Enum.IsDefined(endEnding))
            throw new ArgumentOutOfRangeException(nameof(endEnding));
        if (intent is not null && !Enum.IsDefined(intent.Value))
            throw new ArgumentOutOfRangeException(nameof(intent));
        if (measurement is not null)
        {
            if (intent is not null && intent != PdfLineAnnotationIntent.Dimension)
                throw new ArgumentException(
                    "A measurement profile requires dimension intent.", nameof(intent));
            intent = PdfLineAnnotationIntent.Dimension;
        }
        if (start == end) throw new ArgumentException("A line must have two distinct endpoints.", nameof(end));
        _annotations.Add(new PendingLine(
            pageIndex, start, end, color ?? new PdfRgbColor(0, 0, 0),
            lineWidth, opacity, contents, annotationMetadata, dash,
            startEnding, endEnding, interiorColor, intent, measurement));
        return this;
    }

    /// <summary>Adds a rectangle annotation with optional fill and dashed border.</summary>
    public PdfIncrementalAnnotationEditor AddRectangle(
        int pageIndex, double x, double y, double width, double height,
        PdfRgbColor? strokeColor = null, PdfRgbColor? fillColor = null,
        double lineWidth = 1, double opacity = 1, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null)
        => AddShape(PendingShapeType.Square, pageIndex, x, y, width, height,
            strokeColor, fillColor, lineWidth, opacity, contents, annotationMetadata,
            dashPattern);

    /// <summary>Adds an ellipse annotation with optional fill and dashed border.</summary>
    public PdfIncrementalAnnotationEditor AddEllipse(
        int pageIndex, double x, double y, double width, double height,
        PdfRgbColor? strokeColor = null, PdfRgbColor? fillColor = null,
        double lineWidth = 1, double opacity = 1, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null)
        => AddShape(PendingShapeType.Circle, pageIndex, x, y, width, height,
            strokeColor, fillColor, lineWidth, opacity, contents, annotationMetadata,
            dashPattern);

    /// <summary>Adds a polyline annotation with optional endpoint symbols and intent.</summary>
    public PdfIncrementalAnnotationEditor AddPolyline(
        int pageIndex, IReadOnlyList<PdfPoint> vertices, PdfRgbColor? color = null,
        double lineWidth = 1, double opacity = 1, string? contents = null,
        PdfLineEndingStyle startEnding = PdfLineEndingStyle.None,
        PdfLineEndingStyle endEnding = PdfLineEndingStyle.None,
        IReadOnlyList<double>? dashPattern = null,
        PdfRgbColor? interiorColor = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        PdfVertexAnnotationIntent? intent = null,
        PdfMeasurementProfile? measurement = null)
        => AddVertex(pageIndex, vertices, false, color, null, lineWidth,
            opacity, contents, startEnding, endEnding, dashPattern,
            interiorColor, annotationMetadata, intent, measurement);

    /// <summary>Adds an editable three-point angle measurement with a calculated label.</summary>
    public PdfIncrementalAnnotationEditor AddAngleMeasurement(
        int pageIndex, PdfPoint first, PdfPoint vertex, PdfPoint second,
        PdfRgbColor? color = null, double lineWidth = 1, double opacity = 1,
        int precision = 1, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null)
    {
        if (precision is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(precision));
        double angle = PdfMeasurement.Angle(
            new PdfMeasurementPoint(first.X, first.Y),
            new PdfMeasurementPoint(vertex.X, vertex.Y),
            new PdfMeasurementPoint(second.X, second.Y));
        string label = contents ?? angle.ToString($"F{precision}", CultureInfo.InvariantCulture) + " deg";
        return AddPolyline(pageIndex, [first, vertex, second], color,
            lineWidth, opacity, label, dashPattern: dashPattern,
            annotationMetadata: annotationMetadata,
            intent: PdfVertexAnnotationIntent.Dimension);
    }

    /// <summary>Adds an editable closed-path perimeter measurement with a calculated label.</summary>
    public PdfIncrementalAnnotationEditor AddPerimeterMeasurement(
        int pageIndex, IReadOnlyList<PdfPoint> vertices, PdfMeasurementProfile measurement,
        PdfRgbColor? color = null, double lineWidth = 1, double opacity = 1,
        string? contents = null, PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(measurement);
        PdfMeasurementPoint[] points = [.. vertices.Select(point =>
            new PdfMeasurementPoint(point.X, point.Y))];
        double perimeter = PdfMeasurement.Perimeter(measurement, points);
        string label = contents ?? perimeter.ToString(
            $"F{measurement.Precision}", CultureInfo.InvariantCulture)
            + " " + measurement.UnitSymbol;
        PdfPoint[] closed = vertices.Count > 0 && vertices[0] != vertices[^1]
            ? [.. vertices, vertices[0]] : [.. vertices];
        return AddPolyline(pageIndex, closed, color, lineWidth, opacity, label,
            dashPattern: dashPattern, annotationMetadata: annotationMetadata,
            intent: PdfVertexAnnotationIntent.Dimension, measurement: measurement);
    }

    /// <summary>Adds an editable polygon area measurement with a calculated label.</summary>
    public PdfIncrementalAnnotationEditor AddAreaMeasurement(
        int pageIndex, IReadOnlyList<PdfPoint> vertices, PdfMeasurementProfile measurement,
        PdfRgbColor? strokeColor = null, PdfRgbColor? fillColor = null,
        double lineWidth = 1, double opacity = 1, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(measurement);
        double area = PdfMeasurement.Area(measurement, [.. vertices.Select(point =>
            new PdfMeasurementPoint(point.X, point.Y))]);
        string label = contents ?? area.ToString(
            $"F{measurement.Precision}", CultureInfo.InvariantCulture)
            + " " + measurement.UnitSymbol + "^2";
        return AddPolygon(pageIndex, vertices, strokeColor, fillColor, lineWidth,
            opacity, label, dashPattern, annotationMetadata,
            PdfVertexAnnotationIntent.Dimension, measurement);
    }

    /// <summary>Adds a polygon annotation with optional fill, dash style, and intent.</summary>
    public PdfIncrementalAnnotationEditor AddPolygon(
        int pageIndex, IReadOnlyList<PdfPoint> vertices,
        PdfRgbColor? strokeColor = null, PdfRgbColor? fillColor = null,
        double lineWidth = 1, double opacity = 1, string? contents = null,
        IReadOnlyList<double>? dashPattern = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        PdfVertexAnnotationIntent? intent = null,
        PdfMeasurementProfile? measurement = null)
        => AddVertex(pageIndex, vertices, true, strokeColor, fillColor,
            lineWidth, opacity, contents, PdfLineEndingStyle.None,
            PdfLineEndingStyle.None, dashPattern, null, annotationMetadata, intent,
            measurement);

    /// <summary>Adds a single-path ink annotation.</summary>
    public PdfIncrementalAnnotationEditor AddInk(
        int pageIndex, IReadOnlyList<PdfPoint> points, PdfRgbColor? color = null,
        double lineWidth = 2, double opacity = 1, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null)
        => AddInk(pageIndex, [points], color, lineWidth, opacity, contents,
            annotationMetadata, dashPattern);

    /// <summary>Adds a multipath ink annotation.</summary>
    public PdfIncrementalAnnotationEditor AddInk(
        int pageIndex, IReadOnlyList<IReadOnlyList<PdfPoint>> strokes, PdfRgbColor? color = null,
        double lineWidth = 2, double opacity = 1, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        IReadOnlyList<double>? dashPattern = null)
    {
        ValidatePage(pageIndex);
        ArgumentNullException.ThrowIfNull(strokes);
        ValidateStroke(lineWidth, opacity);
        double[]? dash = ValidateDashPattern(dashPattern);
        if (strokes.Count == 0 || strokes.Any(stroke => stroke is null || stroke.Count < 2))
            throw new ArgumentException("Ink requires at least one stroke containing two points.", nameof(strokes));
        _annotations.Add(new PendingInk(
            pageIndex, [.. strokes.Select(stroke => stroke.ToArray())],
            color ?? new PdfRgbColor(0, 0, 0), lineWidth, opacity, contents,
            annotationMetadata, dash));
        return this;
    }

    /// <summary>Adds a semantic stamp annotation rendered with the supplied image.</summary>
    public PdfIncrementalAnnotationEditor AddImageStamp(
        int pageIndex, double x, double y, double width, double height,
        PdfImage image, string? contents = null,
        PdfAnnotationMetadata? annotationMetadata = null,
        PdfStampIcon icon = PdfStampIcon.Image)
    {
        ValidatePage(pageIndex);
        ValidateRectangle(x, y, width, height);
        ArgumentNullException.ThrowIfNull(image);
        if (!Enum.IsDefined(icon))
            throw new ArgumentOutOfRangeException(nameof(icon));
        _annotations.Add(new PendingImageStamp(
            pageIndex, x, y, width, height, image, contents,
            annotationMetadata, icon));
        return this;
    }

    /// <summary>Adds a caret annotation marking an insertion or replacement location.</summary>
    public PdfIncrementalAnnotationEditor AddCaret(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfCaretSymbol symbol = PdfCaretSymbol.None,
        PdfAnnotationMetadata? annotationMetadata = null)
    {
        ValidatePage(pageIndex);
        ValidateRectangle(x, y, width, height);
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        if (!Enum.IsDefined(symbol))
            throw new ArgumentOutOfRangeException(nameof(symbol));
        _annotations.Add(new PendingCaret(pageIndex, x, y, width, height,
            contents, color ?? new PdfRgbColor(0.1, 0.35, 0.9), opacity,
            symbol, annotationMetadata));
        return this;
    }

    /// <summary>Adds a caret annotation using the authoring-compatible method name.</summary>
    public PdfIncrementalAnnotationEditor AddCaretAnnotation(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfCaretSymbol symbol = PdfCaretSymbol.None,
        PdfAnnotationMetadata? annotationMetadata = null) =>
        AddCaret(pageIndex, x, y, width, height, contents, color, opacity,
            symbol, annotationMetadata);

    /// <summary>Adds a redaction review mark with optional replacement-text overlay.</summary>
    public PdfIncrementalAnnotationEditor AddRedactionMark(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? fillColor = null,
        PdfRgbColor? markColor = null, double opacity = 0.25,
        PdfAnnotationMetadata? annotationMetadata = null,
        string? overlayText = null, bool repeatOverlayText = false,
        PdfTextAlignment overlayAlignment = PdfTextAlignment.Center,
        double overlayFontSize = 10, TrueTypeFont? overlayFont = null)
    {
        ValidatePage(pageIndex);
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        if (overlayFont is null && overlayText is not null
            && overlayText.Any(character => character > 0x7F))
            throw new ArgumentException(
                "Baseline redaction overlay text supports ASCII characters.",
                nameof(overlayText));
        if (overlayText is not null && overlayFont is null && IsPdfA4())
            throw new InvalidOperationException(
                "PDF/A-4 redaction overlay text requires an embedded TrueType font.");
        if (overlayFont is not null && overlayText is not null)
            ValidateDrawableText(overlayFont, overlayText, nameof(overlayText));
        if (!Enum.IsDefined(overlayAlignment))
            throw new ArgumentOutOfRangeException(nameof(overlayAlignment));
        if (!double.IsFinite(overlayFontSize) || overlayFontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(overlayFontSize));
        var (X, Y, Width, Height) = PdfLinkAnnotationFactory.Bounds(values);
        _annotations.Add(new PendingRedaction(pageIndex, X, Y,
            Width, Height, values, contents,
            fillColor ?? new PdfRgbColor(0, 0, 0),
            markColor ?? new PdfRgbColor(0.85, 0.1, 0.1), opacity,
            annotationMetadata, overlayText, repeatOverlayText,
            overlayAlignment, overlayFontSize, overlayFont));
        return this;
    }

    private bool IsPdfA4()
    {
        if (!_tree.Catalog.TryGetValue(MetadataName, out PdfObject? metadataValue))
            return false;
        PdfObject resolved = ResolveValue(metadataValue,
            "The catalog /Metadata value");
        if (resolved is not PdfStream stream)
            throw new InvalidOperationException(
                "The catalog /Metadata value is not a stream.");
        byte[] decoded = PdfStreamDecoder.Decode(stream,
            reference => _document.Resolve(reference),
            maximumDecodedBytes: 32 * 1024 * 1024);
        XDocument xmp;
        try
        {
            xmp = XDocument.Parse(
                new UTF8Encoding(false, true).GetString(decoded),
                LoadOptions.PreserveWhitespace);
        }
        catch (Exception error) when (
            error is XmlException or DecoderFallbackException)
        {
            throw new InvalidOperationException(
                "The existing XMP metadata packet is not well-formed UTF-8 XML.",
                error);
        }
        XNamespace pdfa = "http://www.aiim.org/pdfa/ns/id/";
        return xmp.Descendants(pdfa + "part")
            .Any(value => value.Value.Trim() == "4");
    }

    /// <summary>Places an existing embedded file on a page as an attachment annotation.</summary>
    public PdfIncrementalAnnotationEditor AddFileAttachmentAnnotation(
        int pageIndex, double x, double y, double size, string fileName,
        string? contents = null,
        PdfFileAttachmentIcon icon = PdfFileAttachmentIcon.Paperclip,
        PdfRgbColor? color = null,
        PdfAnnotationMetadata? annotationMetadata = null)
    {
        ValidatePage(pageIndex);
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(y, nameof(y));
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException(
                "A file name is required.", nameof(fileName));
        if (!Enum.IsDefined(icon))
            throw new ArgumentOutOfRangeException(nameof(icon));
        PdfIndirectReference fileSpecification =
            FindEmbeddedFileSpecification(fileName);
        _annotations.Add(new PendingFileAttachment(
            pageIndex, x, y, size, fileName, contents, icon,
            color ?? new PdfRgbColor(0.2, 0.45, 0.85),
            annotationMetadata, fileSpecification));
        return this;
    }

    /// <summary>Places an existing embedded file using a concise attachment method name.</summary>
    public PdfIncrementalAnnotationEditor AddFileAttachment(
        int pageIndex, double x, double y, double size, string fileName,
        string? contents = null,
        PdfFileAttachmentIcon icon = PdfFileAttachmentIcon.Paperclip,
        PdfRgbColor? color = null,
        PdfAnnotationMetadata? annotationMetadata = null) =>
        AddFileAttachmentAnnotation(
            pageIndex, x, y, size, fileName, contents, icon, color,
            annotationMetadata);

    /// <summary>Adds a rectangular link that opens an absolute URI.</summary>
    public PdfIncrementalAnnotationEditor AddUriLink(
        int pageIndex, double x, double y, double width, double height, string uri,
        PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePage(pageIndex);
        ValidateRectangle(x, y, width, height);
        _annotations.Add(new PendingLink(
            pageIndex, x, y, width, height,
            appearance ?? new PdfLinkAppearance(),
            PendingLinkTarget.Uri, PdfLinkAnnotationFactory.ValidateUri(uri),
            null, null, annotationMetadata, contents));
        return this;
    }

    /// <summary>Adds a quadrilateral link that opens an absolute URI.</summary>
    public PdfIncrementalAnnotationEditor AddUriLink(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads, string uri,
        PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePage(pageIndex);
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        var (X, Y, Width, Height) = PdfLinkAnnotationFactory.Bounds(values);
        _annotations.Add(new PendingLink(
            pageIndex, X, Y, Width, Height,
            appearance ?? new PdfLinkAppearance(),
            PendingLinkTarget.Uri, PdfLinkAnnotationFactory.ValidateUri(uri),
            null, values, annotationMetadata, contents));
        return this;
    }

    /// <summary>Adds a rectangular link to a destination in this document.</summary>
    public PdfIncrementalAnnotationEditor AddPageLink(
        int pageIndex, double x, double y, double width, double height,
        int destinationPageIndex, PdfLinkAppearance? appearance = null,
        PdfDestination? destination = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePage(pageIndex);
        ValidatePage(destinationPageIndex);
        ValidateRectangle(x, y, width, height);
        _annotations.Add(new PendingLink(
            pageIndex, x, y, width, height,
            appearance ?? new PdfLinkAppearance(), PendingLinkTarget.Page,
            null, (destinationPageIndex, destination ?? PdfDestination.FitPage()),
            null, annotationMetadata, contents));
        return this;
    }

    /// <summary>Adds a quadrilateral link to a destination in this document.</summary>
    public PdfIncrementalAnnotationEditor AddPageLink(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        int destinationPageIndex, PdfLinkAppearance? appearance = null,
        PdfDestination? destination = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePage(pageIndex);
        ValidatePage(destinationPageIndex);
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        var (X, Y, Width, Height) = PdfLinkAnnotationFactory.Bounds(values);
        _annotations.Add(new PendingLink(
            pageIndex, X, Y, Width, Height,
            appearance ?? new PdfLinkAppearance(), PendingLinkTarget.Page,
            null, (destinationPageIndex, destination ?? PdfDestination.FitPage()),
            values, annotationMetadata, contents));
        return this;
    }

    /// <summary>Adds a rectangular link to an existing named destination.</summary>
    public PdfIncrementalAnnotationEditor AddNamedDestinationLink(
        int pageIndex, double x, double y, double width, double height,
        string destinationName, PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePage(pageIndex);
        ValidateRectangle(x, y, width, height);
        if (string.IsNullOrWhiteSpace(destinationName)
            || !HasNamedDestination(destinationName))
            throw new ArgumentException(
                "The named destination has not been defined.", nameof(destinationName));
        _annotations.Add(new PendingLink(
            pageIndex, x, y, width, height,
            appearance ?? new PdfLinkAppearance(), PendingLinkTarget.Named,
            destinationName, null, null, annotationMetadata, contents));
        return this;
    }

    /// <summary>Adds a quadrilateral link to an existing named destination.</summary>
    public PdfIncrementalAnnotationEditor AddNamedDestinationLink(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string destinationName, PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePage(pageIndex);
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        if (string.IsNullOrWhiteSpace(destinationName)
            || !HasNamedDestination(destinationName))
            throw new ArgumentException(
                "The named destination has not been defined.", nameof(destinationName));
        var (X, Y, Width, Height) = PdfLinkAnnotationFactory.Bounds(values);
        _annotations.Add(new PendingLink(
            pageIndex, X, Y, Width, Height,
            appearance ?? new PdfLinkAppearance(), PendingLinkTarget.Named,
            destinationName, null, values, annotationMetadata, contents));
        return this;
    }

    private PdfIncrementalAnnotationEditor AddShape(
        PendingShapeType type, int pageIndex, double x, double y, double width, double height,
        PdfRgbColor? strokeColor, PdfRgbColor? fillColor,
        double lineWidth, double opacity, string? contents,
        PdfAnnotationMetadata? metadata, IReadOnlyList<double>? dashPattern)
    {
        ValidatePage(pageIndex);
        ValidateRectangle(x, y, width, height);
        ValidateStroke(lineWidth, opacity);
        double[]? dash = ValidateDashPattern(dashPattern);
        _annotations.Add(new PendingShape(type, pageIndex, x, y, width, height,
            strokeColor ?? new PdfRgbColor(0, 0, 0), fillColor, lineWidth,
            opacity, contents, metadata, dash));
        return this;
    }

    private PdfIncrementalAnnotationEditor AddVertex(
        int pageIndex, IReadOnlyList<PdfPoint> vertices, bool closed,
        PdfRgbColor? strokeColor, PdfRgbColor? fillColor,
        double lineWidth, double opacity, string? contents,
        PdfLineEndingStyle startEnding, PdfLineEndingStyle endEnding,
        IReadOnlyList<double>? dashPattern, PdfRgbColor? interiorColor,
        PdfAnnotationMetadata? metadata, PdfVertexAnnotationIntent? intent,
        PdfMeasurementProfile? measurement)
    {
        ValidatePage(pageIndex);
        ArgumentNullException.ThrowIfNull(vertices);
        ValidateStroke(lineWidth, opacity);
        if (!Enum.IsDefined(startEnding))
            throw new ArgumentOutOfRangeException(nameof(startEnding));
        if (!Enum.IsDefined(endEnding))
            throw new ArgumentOutOfRangeException(nameof(endEnding));
        if (intent is not null && !Enum.IsDefined(intent.Value))
            throw new ArgumentOutOfRangeException(nameof(intent));
        if (!closed && intent == PdfVertexAnnotationIntent.Cloud)
            throw new ArgumentException(
                "Cloud intent is only valid for polygons.", nameof(intent));
        if (measurement is not null)
        {
            if (intent is not null && intent != PdfVertexAnnotationIntent.Dimension)
                throw new ArgumentException(
                    "A measurement profile requires dimension intent.", nameof(intent));
            intent = PdfVertexAnnotationIntent.Dimension;
        }
        double[]? dash = ValidateDashPattern(dashPattern);
        int minimum = closed ? 3 : 2;
        if (vertices.Count < minimum)
            throw new ArgumentException(
                $"{(closed ? "A polygon" : "A polyline")} requires at least {minimum} vertices.",
                nameof(vertices));
        PdfPoint[] values = [.. vertices];
        if (values.Zip(values.Skip(1)).All(pair => pair.First == pair.Second))
            throw new ArgumentException(
                "Vertex annotations require distinct points.", nameof(vertices));
        _annotations.Add(new PendingVertex(pageIndex, values, closed,
            strokeColor ?? new PdfRgbColor(0, 0, 0), fillColor, lineWidth,
            opacity, contents, startEnding, endEnding, dash, interiorColor,
            metadata, intent, measurement));
        return this;
    }

    private PdfIncrementalAnnotationEditor AddTextMarkup(
        PdfTextMarkupType type, int pageIndex, double x, double y, double width, double height,
        string? contents, PdfRgbColor color, double opacity,
        PdfAnnotationMetadata? metadata)
    {
        ValidatePage(pageIndex);
        ValidateRectangle(x, y, width, height);
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        return AddTextMarkup(type, pageIndex,
            [PdfTextQuad.FromRectangle(x, y, width, height)],
            contents, color, opacity, metadata);
    }

    private PdfIncrementalAnnotationEditor AddTextMarkup(
        PdfTextMarkupType type, int pageIndex,
        IReadOnlyList<PdfTextQuad> quads, string? contents,
        PdfRgbColor color, double opacity, PdfAnnotationMetadata? metadata)
    {
        ValidatePage(pageIndex);
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        var (X, Y, Width, Height) = PdfLinkAnnotationFactory.Bounds(values);
        _annotations.Add(new PendingTextMarkup(
            type, pageIndex, X, Y, Width, Height,
            values, contents, color, opacity, metadata));
        return this;
    }

    /// <summary>Validates pending annotation edits and appends one incremental revision.</summary>
    public byte[] Build(PdfIncrementalUpdateWriteOptions? options = null)
    {
        if (_annotations.Count == 0 && _removals.Count == 0 && _updates.Count == 0)
            throw new InvalidOperationException("The incremental annotation update is empty.");
        if (_document.PasswordAuthenticationRole == PdfPasswordAuthenticationRole.User
            && (_document.DeclaredPermissions is not PdfDocumentPermissions permissions
                || !permissions.AllowAnnotationModification))
            throw new InvalidOperationException(
                "The PDF user password does not permit annotation modification.");
        PdfSignatureCertificationPermission? certification =
            PdfSignatureReader.ReadCertificationPermission(_document);
        if (certification.HasValue
            && certification != PdfSignatureCertificationPermission.FormFillingSignaturesAndAnnotations)
            throw new InvalidOperationException(
                "The document certification signature prohibits annotation changes.");
        var update = new PdfIncrementalUpdateBuilder(_document);
        ApplyRequiredVersionUpgrade(update);
        ApplyAnnotationUpdates(update);
        var allocated = _annotations.Select(annotation => new AllocatedAnnotation(
            annotation, update.ReserveObject(), annotation is PendingLink
                ? null : update.ReserveObject(),
            annotation is PendingTextNote { Popup: not null }
                ? update.ReserveObject() : null)).ToArray();
        if (_removals.Count > 0)
            RemoveTaggedAnnotationStructure(update, _removals);
        IReadOnlyDictionary<int, long> structureParentKeys = allocated.Length == 0
            ? []
            : PrepareTaggedAnnotationStructure(update, allocated);
        Dictionary<TrueTypeFont, EditorFontBinding> fonts = AllocateFonts(update);
        PdfIndirectReference? baselineRedactionFont = _annotations
            .OfType<PendingRedaction>().Any(value =>
                value.OverlayText is not null && value.OverlayFont is null)
            ? update.AddObject(Dictionary(
                ("Type", Name("Font")), ("Subtype", Name("Type1")),
                ("BaseFont", Name("Helvetica")),
                ("Encoding", Name("WinAnsiEncoding"))))
            : null;
        Dictionary<PdfImage, PdfIndirectReference> images = AllocateImages(update);
        Dictionary<string, PdfIndirectReference> annotationNames =
            ExistingAnnotationNames();
        foreach (PendingRemoval removal in _removals)
            annotationNames.Remove(removal.Name);
        foreach (AllocatedAnnotation item in allocated)
            if (item.Definition is PendingTextNote { Name: not null } named)
                annotationNames.Add(named.Name, item.AnnotationReference);

        foreach (AllocatedAnnotation item in allocated)
        {
            PdfPageTreeEntry page = _pages[item.Definition.PageIndex];
            switch (item.Definition)
            {
                case PendingTextNote note:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(TextNoteDictionary(note, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!,
                            item.PopupReference, annotationNames), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!, TextNoteAppearance(note));
                    if (item.PopupReference is not null)
                        update.SetObject(item.PopupReference,
                            PopupDictionary(note, page.Reference,
                                item.AnnotationReference));
                    break;
                case PendingTextMarkup markup:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(TextMarkupDictionary(markup, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!, TextMarkupAppearance(markup));
                    break;
                case PendingFreeText freeText:
                    EditorFontBinding binding = fonts[freeText.Font];
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(FreeTextDictionary(freeText, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!, binding.Resource),
                            item, structureParentKeys));
                    update.SetObject(item.AppearanceReference!,
                        FreeTextAppearance(freeText, binding.Resource, binding.Type0Reference,
                            binding.Usage));
                    break;
                case PendingLine line:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(LineDictionary(line, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!, LineAppearance(line));
                    break;
                case PendingShape shape:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(ShapeDictionary(shape, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!, ShapeAppearance(shape));
                    break;
                case PendingVertex vertex:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(VertexDictionary(vertex, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!, VertexAppearance(vertex));
                    break;
                case PendingInk ink:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(InkDictionary(ink, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!, InkAppearance(ink));
                    break;
                case PendingImageStamp stamp:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(ImageStampDictionary(stamp, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!,
                        ImageStampAppearance(stamp, images[stamp.Image]));
                    break;
                case PendingCaret caret:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(CaretDictionary(caret, page.Reference,
                            item.AnnotationReference, item.AppearanceReference!), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!, CaretAppearance(caret));
                    break;
                case PendingRedaction redaction:
                    EditorFontBinding? redactionFont = redaction.OverlayFont is null
                        ? null : fonts[redaction.OverlayFont];
                    PdfIndirectReference? fontReference = redactionFont?.Type0Reference
                        ?? baselineRedactionFont;
                    PdfName? fontResource = redaction.OverlayText is null
                        ? null : redactionFont?.Resource ?? Name("Helv");
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(RedactionDictionary(redaction,
                            page.Reference, item.AnnotationReference,
                            item.AppearanceReference!, fontResource), item,
                            structureParentKeys));
                    update.SetObject(item.AppearanceReference!,
                        RedactionAppearance(redaction, fontResource,
                            fontReference, redactionFont?.Usage));
                    break;
                case PendingFileAttachment attachment:
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(FileAttachmentDictionary(
                            attachment, page.Reference, item.AnnotationReference,
                            item.AppearanceReference!), item, structureParentKeys));
                    update.SetObject(item.AppearanceReference!,
                        FileAttachmentAppearance(attachment));
                    break;
                case PendingLink link:
                    PdfObject target = link.Target switch
                    {
                        PendingLinkTarget.Uri => PdfLinkAnnotationFactory.UriAction(link.Name!),
                        PendingLinkTarget.Page => link.PageTarget!.Value.Destination.ToArray(
                            _pages[link.PageTarget.Value.PageIndex].Reference),
                        PendingLinkTarget.Named => UnicodeString(link.Name!),
                        _ => throw new InvalidOperationException("Unknown link target.")
                    };
                    update.SetObject(item.AnnotationReference,
                        WithStructureParent(PdfLinkAnnotationFactory.Create(
                            link.X, link.Y, link.Width, link.Height, page.Reference,
                            item.AnnotationReference, link.Appearance, target,
                            link.Target == PendingLinkTarget.Uri, link.Quads,
                            link.Metadata, link.Contents), item, structureParentKeys));
                    break;
                default:
                    throw new InvalidOperationException("Unknown annotation definition.");
            }
        }

        foreach (int pageIndex in allocated.Select(item => item.Definition.PageIndex)
                     .Concat(_removals.Select(item => item.PageIndex)).Distinct())
        {
            AllocatedAnnotation[] pageAdditions = [.. allocated.Where(
                item => item.Definition.PageIndex == pageIndex)];
            PendingRemoval[] pageRemovals = [.. _removals.Where(
                item => item.PageIndex == pageIndex)];
            AppendPageAnnotations(update, _pages[pageIndex], pageAdditions.SelectMany(item =>
                item.PopupReference is null
                    ? [(item.AnnotationReference, AllocatedAnnotationName(item))]
                    : new[]
                    {
                        (item.AnnotationReference, AllocatedAnnotationName(item)),
                        (item.PopupReference, $"KillerPDF-Popup-{item.PopupReference.ObjectNumber}")
                    }), pageRemovals);
        }
        return update.Build(options);
    }

    private static string AllocatedAnnotationName(AllocatedAnnotation item)
    {
        string kind = item.Definition switch
        {
            PendingTextNote => "Note",
            PendingTextMarkup markup => markup.Type.ToString(),
            PendingFreeText => "FreeText",
            PendingLine => "Line",
            PendingShape shape => shape.Type.ToString(),
            PendingVertex vertex => vertex.Closed ? "Polygon" : "PolyLine",
            PendingInk => "Ink",
            PendingImageStamp => "Image",
            PendingCaret => "Caret",
            PendingRedaction => "Redact",
            PendingFileAttachment => "FileAttachment",
            PendingLink => "Link",
            _ => throw new InvalidOperationException("Unknown annotation definition.")
        };
        return $"KillerPDF-{kind}-{item.AnnotationReference.ObjectNumber}";
    }

    private Dictionary<int, long> PrepareTaggedAnnotationStructure(
        PdfIncrementalUpdateBuilder update, IReadOnlyList<AllocatedAnnotation> annotations)
    {
        if (!_tree.Catalog.TryGetValue(StructTreeRootName, out PdfObject? rootValue))
            return [];
        ResolvedValue resolvedRoot = ResolveWithIdentity(rootValue,
            "The document structure-tree root");
        PdfDictionary root = resolvedRoot.Value as PdfDictionary
            ?? throw new InvalidOperationException(
                "The document structure-tree root is not a dictionary.");
        if (!root.TryGetValue(Name("Type"), out PdfObject? rootType))
            throw new InvalidOperationException(
                "The document structure-tree root has no /Type /StructTreeRoot value.");
        PdfObject resolvedRootType = ResolveValue(rootType,
            "The document structure-tree root /Type value");
        if (resolvedRootType is not PdfName rootTypeName
            || rootTypeName.ValueAsLatin1() != "StructTreeRoot")
            throw new InvalidOperationException(
                "The document structure-tree root has an invalid /Type value.");
        PdfIndirectReference rootReference;
        if (resolvedRoot.FinalReference is PdfIndirectReference indirectRoot)
            rootReference = indirectRoot;
        else
        {
            rootReference = FindStructureRootParentReference(root)
                ?? throw new NotSupportedException(
                    "A direct structure-tree root has no unambiguous indirect parent reference.");
            var catalogEntries = _tree.Catalog.ToDictionary(
                entry => entry.Key, entry => entry.Value);
            catalogEntries[StructTreeRootName] = rootReference;
            if (RequiredVersionOverride() is PdfVersion requiredVersion)
                catalogEntries[VersionName] = Name(requiredVersion.ToString());
            update.ReplaceObject(_tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries));
        }
        var documentTarget = FindDocumentStructureElement(root, rootReference, update);
        PdfIndirectReference documentElementReference = documentTarget.Reference;
        PdfDictionary documentElement = documentTarget.Dictionary;
        root = documentTarget.Root;
        bool documentElementIsNew = documentTarget.IsNew;
        PdfIndirectReference? namespaceReference = FindStructureNamespace(root);

        IReadOnlyList<PdfNumberTreeEntry> allExistingEntries =
            root.TryGetValue(ParentTreeName, out PdfObject? parentTreeValue)
                ? PdfNumberTree.Read(_document, parentTreeValue)
                : [];
        var removalReferences = _removals.Select(value =>
                (value.Reference.ObjectNumber, value.Reference.Generation))
            .ToHashSet();
        var removalKeys = _removals.Where(value =>
                value.Dictionary.TryGetValue(Name("StructParent"), out _))
            .Select(value =>
            {
                PdfObject keyValue = value.Dictionary[Name("StructParent")];
                return ResolveValue(keyValue,
                        $"Annotation '{value.Name}' /StructParent value")
                    is PdfInteger key && key.Value >= 0
                        ? key.Value
                        : throw new InvalidOperationException(
                            $"Annotation '{value.Name}' has an invalid structure-parent key.");
            }).ToHashSet();
        var removedStructureIdentities = new HashSet<
            (int ObjectNumber, int Generation)>();
        foreach (PdfNumberTreeEntry entry in allExistingEntries)
        {
            if (!removalKeys.Contains(entry.Key)
                && !ParentMappingTargetsRemoval(entry.Value)) continue;
            ResolvedValue resolvedElement = ResolveWithIdentity(entry.Value,
                $"The structure-tree ParentTree value for key {entry.Key}");
            if (resolvedElement.FinalReference is PdfIndirectReference reference)
                removedStructureIdentities.Add(
                    (reference.ObjectNumber, reference.Generation));
            removalKeys.Add(entry.Key);
        }
        IReadOnlyList<PdfNumberTreeEntry> existingEntries =
            [.. allExistingEntries.Where(entry => !removalKeys.Contains(entry.Key))];
        if (existingEntries.Any(entry => entry.Key < 0))
            throw new InvalidOperationException(
                "The structure-tree ParentTree contains a negative key.");
        foreach (PdfNumberTreeEntry entry in existingEntries)
            ValidateParentTreeValue(entry.Value,
                $"The structure-tree ParentTree value for key {entry.Key}");
        if (annotations.Count > PdfNumberTree.MaximumEntryCount - existingEntries.Count)
            throw new NotSupportedException(
                "The structure-tree ParentTree would contain too many entries.");
        long nextKey = root.TryGetValue(ParentTreeNextKeyName, out PdfObject? nextValue)
            ? (ResolveValue(nextValue,
                    "The structure-tree /ParentTreeNextKey value") as PdfInteger)?.Value
                ?? throw new InvalidOperationException(
                    "The structure-tree /ParentTreeNextKey is not an integer.")
            : existingEntries.Count == 0 ? 0 : checked(existingEntries.Max(entry => entry.Key) + 1);
        if (nextKey < 0)
            throw new InvalidOperationException(
                "The structure-tree /ParentTreeNextKey cannot be negative.");
        if (existingEntries.Count > 0)
            nextKey = Math.Max(nextKey, checked(existingEntries.Max(entry => entry.Key) + 1));

        var keys = new Dictionary<int, long>();
        var newStructureReferences = new List<PdfIndirectReference>();
        var parentNumbers = new List<PdfObject>();
        foreach (PdfNumberTreeEntry entry in existingEntries.OrderBy(entry => entry.Key))
        {
            parentNumbers.Add(new PdfInteger(entry.Key));
            parentNumbers.Add(entry.Value);
        }
        foreach (AllocatedAnnotation annotation in annotations)
        {
            string description = AnnotationDescription(annotation.Definition);
            if (string.IsNullOrWhiteSpace(description))
                throw new InvalidOperationException(
                    "Annotations added to a tagged PDF require descriptive contents.");
            PdfIndirectReference structureReference = update.ReserveObject();
            long key = nextKey;
            nextKey = checked(nextKey + 1);
            keys.Add(annotation.AnnotationReference.ObjectNumber, key);
            newStructureReferences.Add(structureReference);
            PdfIndirectReference pageReference = _pages[annotation.Definition.PageIndex].Reference;
            var entries = new List<(string Name, PdfObject Value)>
            {
                ("Type", Name("StructElem")),
                ("S", Name("Annot")),
                ("P", documentElementReference),
                ("Pg", pageReference),
                ("Alt", UnicodeString(description)),
                ("K", Dictionary(
                    ("Type", Name("OBJR")),
                    ("Pg", pageReference),
                    ("Obj", annotation.AnnotationReference)))
            };
            if (namespaceReference is not null)
                entries.Add(("NS", namespaceReference));
            update.SetObject(structureReference, Dictionary([.. entries]));
            parentNumbers.Add(new PdfInteger(key));
            parentNumbers.Add(structureReference);
        }

        PdfIndirectReference rebuiltParentTree = update.AddObject(
            Dictionary(("Nums", new PdfArray(parentNumbers))));
        var rootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        rootEntries[ParentTreeName] = rebuiltParentTree;
        rootEntries[ParentTreeNextKeyName] = new PdfInteger(nextKey);
        update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(rootEntries));

        var documentEntries = documentElement.ToDictionary(entry => entry.Key, entry => entry.Value);
        var kids = new List<PdfObject>();
        if (documentEntries.TryGetValue(StructureKidsName, out PdfObject? existingKids))
        {
            PdfObject resolvedKids = ResolveValue(existingKids,
                "The document structure element /K value");
            if (resolvedKids is PdfNull)
                throw new InvalidOperationException(
                    "The document structure element /K value contains a stale indirect reference.");
            if (resolvedKids is PdfArray array)
            {
                foreach (PdfObject kid in array)
                {
                    ValidateDocumentKid(kid);
                    ResolvedValue resolvedKid = ResolveWithIdentity(kid,
                        "The document structure element /K value");
                    if (resolvedKid.FinalReference is PdfIndirectReference reference
                        && removedStructureIdentities.Contains(
                            (reference.ObjectNumber, reference.Generation)))
                        continue;
                    kids.Add(kid);
                }
            }
            else
            {
                ValidateDocumentKid(existingKids);
                ResolvedValue resolvedKid = ResolveWithIdentity(existingKids,
                    "The document structure element /K value");
                if (resolvedKid.FinalReference is not PdfIndirectReference reference
                    || !removedStructureIdentities.Contains(
                        (reference.ObjectNumber, reference.Generation)))
                    kids.Add(existingKids);
            }
        }
        kids.AddRange(newStructureReferences);
        if (kids.Count == 0)
            documentEntries.Remove(StructureKidsName);
        else
            documentEntries[StructureKidsName] = kids.Count == 1
                ? kids[0] : new PdfArray(kids);
        if (documentElementIsNew)
            update.SetObject(documentElementReference, new PdfDictionary(documentEntries));
        else
            update.ReplaceObject(documentElementReference.ObjectNumber,
                new PdfDictionary(documentEntries));
        return keys;

        bool ParentMappingTargetsRemoval(PdfObject value)
        {
            PdfObject resolved = ResolveValue(value,
                "A ParentTree annotation mapping");
            if (resolved is not PdfDictionary element
                || !element.TryGetValue(Name("K"), out PdfObject? kidValue)
                || ResolveValue(kidValue,
                    "A ParentTree annotation mapping /K value")
                    is not PdfDictionary kid
                || !kid.TryGetValue(Name("Obj"), out PdfObject? objectValue))
                return false;
            return ResolveWithIdentity(objectValue,
                    "A ParentTree annotation mapping OBJR /Obj value")
                    .FinalReference is PdfIndirectReference reference
                && removalReferences.Contains(
                    (reference.ObjectNumber, reference.Generation));
        }

        void ValidateDocumentKid(PdfObject kid)
        {
            PdfObject resolved = ResolveValue(kid,
                "The document structure element /K value");
            if (resolved is PdfInteger mcid && mcid.Value >= 0) return;
            if (resolved is PdfDictionary dictionary)
            {
                if (dictionary.TryGetValue(Name("S"), out PdfObject? role))
                {
                    PdfObject resolvedRole = ResolveValue(role,
                        "A document structure child /S value");
                    if (resolvedRole is PdfName) return;
                }
                if (dictionary.TryGetValue(Name("MCID"), out PdfObject? markedContent))
                {
                    PdfObject resolvedMcid = ResolveValue(markedContent,
                        "A document structure child /MCID value");
                    if (resolvedMcid is PdfInteger dictionaryMcid
                        && dictionaryMcid.Value >= 0) return;
                }
                if (dictionary.TryGetValue(Name("Obj"), out PdfObject? referencedObject)
                    && referencedObject is PdfIndirectReference objectReference
                    && ResolveValue(objectReference,
                        "A document structure OBJR /Obj value") is PdfDictionary)
                    return;
            }
            throw new InvalidOperationException(
                "The document structure element /K value contains an invalid child.");
        }

        void ValidateParentTreeValue(PdfObject value, string description)
        {
            PdfObject resolved = ResolveValue(value, description);
            if (resolved is PdfArray array)
            {
                foreach (PdfObject item in array)
                {
                    if (item is PdfNull) continue;
                    if (item is not PdfIndirectReference itemReference
                        || ResolveValue(itemReference,
                            $"{description} array entry") is not PdfDictionary element)
                        throw new InvalidOperationException(
                            $"{description} array contains a non-structure-element entry.");
                    ValidateStructureElement(element, description);
                }
                return;
            }
            if (resolved is not PdfDictionary dictionary)
                throw new InvalidOperationException(
                    $"{description} is not a structure element or array.");
            ValidateStructureElement(dictionary, description);
        }

        void ValidateStructureElement(PdfDictionary element, string description)
        {
            if (!element.TryGetValue(Name("S"), out PdfObject? role))
                throw new InvalidOperationException(
                    $"{description} structure element has no /S role name.");
            PdfObject resolvedRole = ResolveValue(
                role, $"{description} structure element /S value");
            if (resolvedRole is not PdfName)
                throw new InvalidOperationException(
                    $"{description} structure element /S value is not a name.");
        }
    }

    private PdfIndirectReference? FindStructureRootParentReference(PdfDictionary root)
    {
        if (!root.TryGetValue(StructureKidsName, out PdfObject? kidsValue)) return null;
        PdfObject resolvedKids = ResolveValue(
            kidsValue, "The structure-tree root /K value");
        IEnumerable<PdfObject> kids = resolvedKids is PdfArray array ? array : [resolvedKids];
        PdfIndirectReference? result = null;
        foreach (PdfObject kid in kids)
        {
            PdfDictionary child = ResolveDictionary(kid,
                "A top-level structure element is not a dictionary.");
            if (!child.TryGetValue(Name("P"), out PdfObject? parent)
                || ResolveWithIdentity(parent,
                    "A top-level structure element /P value").FinalReference
                    is not PdfIndirectReference parentReference)
                return null;
            if (result is not null
                && (result.ObjectNumber != parentReference.ObjectNumber
                    || result.Generation != parentReference.Generation))
                return null;
            result = parentReference;
        }
        return result;
    }

    private (PdfIndirectReference Reference, PdfDictionary Dictionary,
        PdfDictionary Root, bool IsNew) FindDocumentStructureElement(
            PdfDictionary root, PdfIndirectReference rootReference,
            PdfIncrementalUpdateBuilder update)
    {
        if (!root.TryGetValue(StructureKidsName, out PdfObject? kidsValue))
            throw new InvalidOperationException("The structure-tree root has no children.");
        PdfObject resolvedKids = ResolveValue(
            kidsValue, "The structure-tree root /K value");
        PdfObject[] kids = resolvedKids is PdfArray array ? [.. array] : [resolvedKids];
        PdfIndirectReference? fallback = null;
        for (int index = 0; index < kids.Length; index++)
        {
            PdfObject kid = kids[index];
            ResolvedValue resolvedKid = ResolveWithIdentity(kid,
                "A top-level structure element");
            PdfDictionary dictionary = resolvedKid.Value as PdfDictionary
                ?? throw new InvalidOperationException(
                    "A top-level structure element is not a dictionary.");
            if (resolvedKid.FinalReference is PdfIndirectReference reference)
            {
                fallback ??= reference;
                if (!dictionary.TryGetValue(Name("P"), out PdfObject? parent)
                    || ResolveWithIdentity(parent,
                        "A top-level structure element /P value").FinalReference
                        is not PdfIndirectReference parentReference
                    || parentReference.ObjectNumber != rootReference.ObjectNumber
                    || parentReference.Generation != rootReference.Generation)
                    throw new InvalidOperationException(
                        "A top-level structure element has no reciprocal /P link to the structure-tree root.");
            }
            if (!dictionary.TryGetValue(Name("S"), out PdfObject? type))
                throw new InvalidOperationException(
                    "A top-level structure element has no /S role name.");
            PdfObject resolvedType = ResolveValue(
                type, "A top-level structure element /S value");
            PdfName name = resolvedType as PdfName
                ?? throw new InvalidOperationException(
                    "A top-level structure element /S value is not a name.");
            if (name.ValueAsLatin1() == "Document")
                return Target(kid, dictionary, index);
        }
        if (fallback is not null)
            return (fallback, ResolveDictionary(fallback,
                "The top-level structure element is not a dictionary."), root, false);
        if (kids.Length == 0)
            throw new InvalidOperationException("The structure-tree root has no children.");
        return Target(kids[0], ResolveDictionary(kids[0],
            "The top-level structure element is not a dictionary."), 0);

        (PdfIndirectReference, PdfDictionary, PdfDictionary, bool) Target(
            PdfObject value, PdfDictionary dictionary, int index)
        {
            if (ResolveWithIdentity(value,
                    "A top-level structure element").FinalReference
                is PdfIndirectReference reference)
                return (reference, dictionary, root, false);
            PdfIndirectReference? documentReference =
                FindStructureElementParentReference(dictionary);
            bool isNew = false;
            if (documentReference is null)
            {
                if (HasStructureElementParentReference(dictionary))
                    throw new NotSupportedException(
                        "A direct top-level structure element has ambiguous child parent references.");
                documentReference = update.ReserveObject();
                isNew = true;
            }
            var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
            entries[Name("P")] = rootReference;
            kids[index] = documentReference;
            var rootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
            rootEntries[StructureKidsName] = kids.Length == 1
                ? kids[0] : new PdfArray(kids);
            return (documentReference, new PdfDictionary(entries),
                new PdfDictionary(rootEntries), isNew);
        }
    }

    private PdfIndirectReference? FindStructureElementParentReference(PdfDictionary element)
    {
        if (!element.TryGetValue(StructureKidsName, out PdfObject? kidsValue)) return null;
        PdfIndirectReference? result = null;
        foreach (PdfObject kid in StructureKids(kidsValue))
        {
            PdfObject resolved = ResolveValue(
                kid, "A direct structure-element child");
            if (resolved is not PdfDictionary child) continue;
            if (!child.TryGetValue(Name("P"), out PdfObject? parent)
                || ResolveWithIdentity(parent,
                    "A direct structure-element child /P value").FinalReference
                    is not PdfIndirectReference parentReference)
                return null;
            if (result is not null
                && (result.ObjectNumber != parentReference.ObjectNumber
                    || result.Generation != parentReference.Generation))
                return null;
            result = parentReference;
        }
        return result;
    }

    private bool HasStructureElementParentReference(PdfDictionary element)
    {
        if (!element.TryGetValue(StructureKidsName, out PdfObject? kidsValue)) return false;
        return StructureKids(kidsValue).Any(kid =>
        {
            PdfObject resolved = ResolveValue(
                kid, "A direct structure-element child");
            return resolved is PdfDictionary child
                && child.TryGetValue(Name("P"), out PdfObject? parent)
                && ResolveWithIdentity(parent,
                    "A direct structure-element child /P value").FinalReference is not null;
        });
    }

    private PdfObject[] StructureKids(PdfObject value)
    {
        PdfObject resolved = ResolveValue(value,
            "A structure-element /K value");
        return resolved is PdfArray array ? [.. array] : [value];
    }

    private PdfIndirectReference? FindStructureNamespace(PdfDictionary root)
    {
        if (!root.TryGetValue(NamespacesName, out PdfObject? value)) return null;
        PdfObject resolved = ResolveValue(value,
            "The structure-tree /Namespaces value");
        PdfArray namespaces = resolved as PdfArray
            ?? throw new InvalidOperationException("The structure-tree /Namespaces value is not an array.");
        PdfIndirectReference? result = null;
        foreach (PdfObject namespaceValue in namespaces)
        {
            PdfIndirectReference reference = namespaceValue as PdfIndirectReference
                ?? throw new InvalidOperationException(
                    "A structure namespace is not an indirect reference.");
            PdfDictionary definition = ResolveDictionary(reference,
                "A structure namespace is not a dictionary.");
            if (!definition.TryGetValue(Name("Type"), out PdfObject? type))
                throw new InvalidOperationException(
                    "A structure namespace has no /Type /Namespace value.");
            PdfObject resolvedType = ResolveValue(
                type, "A structure namespace /Type value");
            if (resolvedType is not PdfName typeName
                || typeName.ValueAsLatin1() != "Namespace")
                throw new InvalidOperationException(
                    "A structure namespace has an invalid /Type value.");
            if (!definition.TryGetValue(Name("NS"), out PdfObject? uri))
                throw new InvalidOperationException(
                    "A structure namespace has no /NS string.");
            PdfObject resolvedUri = ResolveValue(
                uri, "A structure namespace /NS value");
            PdfString text = resolvedUri as PdfString
                ?? throw new InvalidOperationException(
                    "A structure namespace /NS value is not a string.");
            if (definition.TryGetValue(Name("Schema"), out PdfObject? schema)
                && ResolveValue(schema,
                    "A structure namespace /Schema value") is not PdfDictionary)
                throw new InvalidOperationException(
                    "A structure namespace /Schema value is not a dictionary.");
            if (DecodePdfString(text) != "http://iso.org/pdf2/ssn") continue;
            if (result is not null)
                throw new InvalidOperationException(
                    "The structure-tree /Namespaces array contains duplicate PDF 2.0 namespaces.");
            result = reference;
        }
        return result;
    }

    private static string DecodePdfString(PdfString value)
        => PdfUnicodeEncoding.DecodeTextString(
            value.Bytes.Span, "A structure namespace URI");

    private PdfDictionary ResolveDictionary(PdfObject value, string message) =>
        ResolveValue(value, message) as PdfDictionary
            ?? throw new InvalidOperationException(message);

    private PdfObject ResolveValue(PdfObject value, string description)
        => ResolveWithIdentity(value, description).Value;

    private ResolvedValue ResolveWithIdentity(PdfObject value, string description)
    {
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        PdfIndirectReference? finalReference = null;
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw new InvalidOperationException(
                    $"{description} is too deeply indirect.");
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException(
                    $"{description} contains an indirect-reference cycle.");
            finalReference = reference;
            value = _document.Resolve(reference);
        }
        return new ResolvedValue(value, finalReference);
    }

    private static PdfDictionary WithStructureParent(
        PdfDictionary dictionary, AllocatedAnnotation annotation,
        IReadOnlyDictionary<int, long> keys)
    {
        if (!keys.TryGetValue(annotation.AnnotationReference.ObjectNumber, out long key))
            return dictionary;
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("StructParent")] = new PdfInteger(key);
        return new PdfDictionary(entries);
    }

    private static string AnnotationDescription(PendingAnnotation annotation) => annotation switch
    {
        PendingTextNote value => value.Contents,
        PendingTextMarkup value => value.Contents ?? string.Empty,
        PendingFreeText value => value.Contents,
        PendingLine value => value.Contents ?? string.Empty,
        PendingShape value => value.Contents ?? string.Empty,
        PendingVertex value => value.Contents ?? string.Empty,
        PendingInk value => value.Contents ?? string.Empty,
        PendingImageStamp value => value.Contents ?? string.Empty,
        PendingCaret value => value.Contents ?? string.Empty,
        PendingRedaction value => value.Contents ?? string.Empty,
        PendingFileAttachment value => value.Contents ?? string.Empty,
        PendingLink value => value.Contents ?? string.Empty,
        _ => string.Empty
    };

    private Dictionary<TrueTypeFont, EditorFontBinding> AllocateFonts(PdfIncrementalUpdateBuilder update)
    {
        var result = new Dictionary<TrueTypeFont, EditorFontBinding>();
        int sequence = 0;
        IEnumerable<(TrueTypeFont Font, string Text)> textRuns =
            _annotations.OfType<PendingFreeText>()
                .Select(value => (value.Font, value.Contents))
                .Concat(_annotations.OfType<PendingRedaction>()
                    .Where(value => value.OverlayFont is not null
                        && value.OverlayText is not null)
                    .Select(value => (value.OverlayFont!, value.OverlayText!)));
        foreach (IGrouping<TrueTypeFont, (TrueTypeFont Font, string Text)> group in
            textRuns.GroupBy(value => value.Font))
        {
            var usage = new EmbeddedFontUsage(group.Key, new PdfName(
                Encoding.ASCII.GetBytes($"KpF{sequence + 1}")));
            foreach (FontGlyphMapping mapping in group.SelectMany(
                         value => group.Key.MapText(value.Text)))
            {
                if (mapping.UnicodeSequence is "\r" or "\n") continue;
                usage.AddMapping(mapping.Glyph, mapping.UnicodeSequence);
            }
            PdfIndirectReference type0 = update.ReserveObject();
            PdfIndirectReference cidFont = update.ReserveObject();
            PdfIndirectReference descriptor = update.ReserveObject();
            PdfIndirectReference fontFile = update.ReserveObject();
            PdfIndirectReference toUnicode = update.ReserveObject();
            PdfIndirectReference encoding = update.ReserveObject();
            EmbeddedTrueTypeFontObjects values = PdfEmbeddedTrueTypeFontFactory.Create(
                group.Key, usage.Mappings, type0, cidFont, descriptor, fontFile, toUnicode,
                encoding);
            update.SetObject(type0, values.Type0).SetObject(cidFont, values.CidFont)
                .SetObject(descriptor, values.Descriptor).SetObject(fontFile, values.FontFile)
                .SetObject(toUnicode, values.ToUnicode).SetObject(encoding, values.Encoding);
            result.Add(group.Key, new EditorFontBinding(
                new PdfName(Encoding.ASCII.GetBytes($"KpF{++sequence}")), type0, usage));
        }
        return result;
    }

    private Dictionary<PdfImage, PdfIndirectReference> AllocateImages(
        PdfIncrementalUpdateBuilder update)
    {
        var result = new Dictionary<PdfImage, PdfIndirectReference>();
        foreach (PdfImage image in _annotations.OfType<PendingImageStamp>()
            .Select(value => value.Image).Distinct())
            Add(image);
        return result;

        PdfIndirectReference Add(PdfImage image)
        {
            if (result.TryGetValue(image, out PdfIndirectReference? existing)) return existing;
            PdfIndirectReference reference = update.ReserveObject();
            result.Add(image, reference);
            PdfIndirectReference? softMask = image.SoftMask is null ? null : Add(image.SoftMask);
            update.SetObject(reference, PdfImageXObjectFactory.Create(image, softMask));
            return reference;
        }
    }

    private Dictionary<string, PdfIndirectReference> ExistingAnnotationNames()
    {
        var result = new Dictionary<string, PdfIndirectReference>(StringComparer.Ordinal);
        foreach (PdfPageTreeEntry page in _pages)
        {
            if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotsValue))
                continue;
            PdfObject resolvedAnnots = ResolveValue(annotsValue,
                $"Page {page.Index + 1} /Annots value");
            if (resolvedAnnots is not PdfArray annotations)
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots value is not an array.");
            foreach (PdfObject item in annotations)
            {
                ResolvedValue resolved = ResolveWithIdentity(item,
                    $"Page {page.Index + 1} annotation");
                if (resolved.FinalReference is not PdfIndirectReference reference
                    || resolved.Value is not PdfDictionary annotation
                    || !annotation.TryGetValue(Name("NM"), out PdfObject? nameValue))
                    continue;
                PdfObject resolvedName = ResolveValue(nameValue,
                    $"Page {page.Index + 1} annotation /NM value");
                if (resolvedName is not PdfString text)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /NM value is not a string.");
                string name = DecodePdfString(text);
                if (!result.TryAdd(name, reference))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} /Annots contains duplicate /NM values.");
            }
        }
        return result;
    }

    private PendingRemoval FindNamedAnnotation(int pageIndex, string name)
    {
        PdfPageTreeEntry page = _pages[pageIndex];
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotsValue)
            || ResolveValue(annotsValue,
                $"Page {pageIndex + 1} /Annots value") is not PdfArray annotations)
            throw new ArgumentException(
                $"Page {pageIndex + 1} has no annotation named '{name}'.",
                nameof(name));
        foreach (PdfObject item in annotations)
        {
            ResolvedValue resolved = ResolveWithIdentity(item,
                $"Page {pageIndex + 1} annotation");
            if (resolved.FinalReference is null
                || resolved.Value is not PdfDictionary annotation
                || !annotation.TryGetValue(Name("NM"), out PdfObject? nameValue)
                || ResolveValue(nameValue,
                    $"Page {pageIndex + 1} annotation /NM value")
                    is not PdfString text
                || !string.Equals(DecodePdfString(text), name,
                    StringComparison.Ordinal))
                continue;
            return ReadRemovalTarget(pageIndex, item, name);
        }
        throw new ArgumentException(
            $"Page {pageIndex + 1} has no annotation named '{name}'.",
            nameof(name));
    }

    private PendingRemoval FindUpdateTarget(int pageIndex, string name)
    {
        ValidatePage(pageIndex);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "An annotation name is required.", nameof(name));
        PendingRemoval target = FindNamedAnnotation(pageIndex, name);
        EnsureUpdateableAnnotation(target);
        if (_removals.Any(value => SameIdentity(value.Reference, target.Reference)))
            throw new InvalidOperationException(
                $"Annotation '{name}' is scheduled for removal and cannot be updated.");
        return target;
    }

    private PendingRemoval FindIndexedUpdateTarget(
        int pageIndex, int annotationIndex)
    {
        ValidatePage(pageIndex);
        PdfPageTreeEntry page = _pages[pageIndex];
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotsValue)
            || ResolveValue(annotsValue,
                $"Page {pageIndex + 1} /Annots value") is not PdfArray annotations
            || (uint)annotationIndex >= (uint)annotations.Count)
            throw new ArgumentOutOfRangeException(nameof(annotationIndex));
        PendingRemoval target = ReadRemovalTarget(pageIndex,
            annotations[annotationIndex], $"annotation {annotationIndex + 1}");
        EnsureUpdateableAnnotation(target);
        if (_removals.Any(value => SameIdentity(value.Reference, target.Reference)))
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} annotation {annotationIndex + 1} is scheduled for removal and cannot be updated.");
        return target;
    }

    private void EnsureUpdateableAnnotation(PendingRemoval target)
    {
        if (target.Dictionary.TryGetValue(Name("Subtype"),
                out PdfObject? subtypeValue)
            && ResolveValue(subtypeValue,
                $"Annotation '{target.Name}' subtype") is PdfName subtype
            && subtype.ValueAsLatin1() == "Popup")
            throw new InvalidOperationException(
                "A popup annotation must be updated through its parent annotation.");
    }

    private void EnsureLinkAnnotation(PendingRemoval target)
    {
        if (!target.Dictionary.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
            || ResolveValue(subtypeValue,
                $"Annotation '{target.Name}' subtype") is not PdfName subtype
            || subtype.ValueAsLatin1() != "Link")
            throw new InvalidOperationException(
                $"Annotation '{target.Name}' is not a link annotation.");
    }

    private void AddContentsUpdate(
        PendingRemoval target, string? contents, string parameterName)
    {
        if (_updates.Any(value => SameIdentity(value.Reference, target.Reference)
                && value.UpdateContents))
            throw new ArgumentException(
                $"Annotation '{target.Name}' already has a pending contents update.",
                parameterName);
        _updates.Add(new PendingAnnotationUpdate(target.PageIndex, target.Name,
            target.Reference, target.Dictionary, true, contents, false, null, false));
    }

    private void AddMetadataUpdate(
        PendingRemoval target, PdfAnnotationMetadata? metadata,
        string parameterName)
    {
        if (_updates.Any(value => SameIdentity(value.Reference, target.Reference)
                && value.UpdateMetadata))
            throw new ArgumentException(
                $"Annotation '{target.Name}' already has a pending metadata update.",
                parameterName);
        _updates.Add(new PendingAnnotationUpdate(target.PageIndex, target.Name,
            target.Reference, target.Dictionary, false, null, true, metadata, false));
    }

    private void AddLinkTargetUpdate(
        PendingRemoval target, PendingLinkTarget linkTarget, string? linkName,
        (int PageIndex, PdfDestination Destination)? pageTarget,
        string parameterName)
    {
        if (_updates.Any(value => SameIdentity(value.Reference, target.Reference)
                && value.UpdateLinkTarget))
            throw new ArgumentException(
                $"Annotation '{target.Name}' already has a pending link-target update.",
                parameterName);
        _updates.Add(new PendingAnnotationUpdate(target.PageIndex, target.Name,
            target.Reference, target.Dictionary, false, null, false, null, false,
            true, linkTarget, linkName, pageTarget));
    }

    private void EnsureNotPendingUpdate(PendingRemoval target)
    {
        if (_updates.Any(value => SameIdentity(value.Reference, target.Reference)))
            throw new InvalidOperationException(
                $"Annotation '{target.Name}' has a pending update and cannot be removed.");
    }

    private void ApplyAnnotationUpdates(PdfIncrementalUpdateBuilder update)
    {
        foreach (IGrouping<(int ObjectNumber, int Generation), PendingAnnotationUpdate> group
                 in _updates.GroupBy(value =>
                     (value.Reference.ObjectNumber, value.Reference.Generation)))
        {
            PendingAnnotationUpdate first = group.First();
            var entries = first.Dictionary.ToDictionary(
                entry => entry.Key, entry => entry.Value);
            foreach (PendingAnnotationUpdate change in group)
            {
                if (change.UpdateContents)
                {
                    entries.Remove(Name("Contents"));
                    if (change.Contents is not null)
                        entries[Name("Contents")] = UnicodeString(change.Contents);
                }
                if (change.UpdateMetadata)
                {
                    entries.Remove(Name("F"));
                    entries.Remove(Name("T"));
                    entries.Remove(Name("Subj"));
                    entries.Remove(Name("CreationDate"));
                    entries.Remove(Name("M"));
                    PdfAnnotationMetadata metadata = change.Metadata
                        ?? new PdfAnnotationMetadata();
                    entries[Name("F")] = new PdfInteger((long)metadata.Flags);
                    var values = new List<(string Name, PdfObject Value)>();
                    PdfLinkAnnotationFactory.AddMetadata(values, change.Metadata);
                    foreach ((string key, PdfObject value) in values)
                        entries[Name(key)] = value;
                }
                if (change.StripLinkAppearance)
                {
                    entries.Remove(Name("AP"));
                    entries.Remove(Name("C"));
                    entries[Name("BS")] = Dictionary(("W", new PdfInteger(0)));
                    entries[Name("Border")] = new PdfArray([
                        new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]);
                }
                if (change.UpdateFileAttachmentIcon)
                {
                    entries[Name("Name")] = Name(change.FileAttachmentIcon.ToString());
                    entries.Remove(Name("AP"));
                }
                if (change.UpdateLinkTarget)
                {
                    entries.Remove(Name("A"));
                    entries.Remove(Name("Dest"));
                    switch (change.LinkTarget)
                    {
                        case PendingLinkTarget.Uri:
                            entries[Name("A")] = PdfLinkAnnotationFactory.UriAction(
                                change.LinkName!);
                            break;
                        case PendingLinkTarget.Page:
                            entries[Name("Dest")] = change.PageTarget!.Value.Destination.ToArray(
                                _pages[change.PageTarget.Value.PageIndex].Reference);
                            break;
                        case PendingLinkTarget.Named:
                            entries[Name("Dest")] = UnicodeString(change.LinkName!);
                            break;
                        default:
                            throw new InvalidOperationException("Unknown link target.");
                    }
                }
            }
            update.ReplaceObject(first.Reference.ObjectNumber,
                new PdfDictionary(entries));
            PendingAnnotationUpdate? contentsChange = group.LastOrDefault(
                value => value.UpdateContents);
            if (contentsChange is not null)
                SynchronizeTaggedAnnotationDescription(update, first,
                    contentsChange.Contents);
        }
    }

    private void SynchronizeTaggedAnnotationDescription(
        PdfIncrementalUpdateBuilder update, PendingAnnotationUpdate target,
        string? contents)
    {
        if (!_tree.Catalog.TryGetValue(StructTreeRootName, out PdfObject? rootValue)
            || !target.Dictionary.TryGetValue(Name("StructParent"),
                out PdfObject? keyValue)) return;
        if (string.IsNullOrWhiteSpace(contents))
            throw new InvalidOperationException(
                $"Tagged annotation '{target.Name}' requires descriptive contents.");
        if (ResolveValue(keyValue,
                $"Annotation '{target.Name}' /StructParent value")
                is not PdfInteger key || key.Value < 0)
            throw new InvalidOperationException(
                $"Tagged annotation '{target.Name}' has an invalid structure-parent key.");
        PdfDictionary root = ResolveValue(rootValue,
            "The document structure-tree root") as PdfDictionary
            ?? throw new InvalidOperationException(
                "The document structure-tree root is not a dictionary.");
        if (!root.TryGetValue(ParentTreeName, out PdfObject? parentTreeValue))
            throw new InvalidOperationException("The tagged document has no ParentTree.");
        PdfNumberTreeEntry mapping = PdfNumberTree.Read(_document, parentTreeValue)
            .SingleOrDefault(entry => entry.Key == key.Value)
            ?? throw new InvalidOperationException(
                $"Tagged annotation '{target.Name}' has no ParentTree mapping.");
        ResolvedValue resolvedElement = ResolveWithIdentity(mapping.Value,
            $"Annotation '{target.Name}' ParentTree mapping");
        if (resolvedElement.FinalReference is not PdfIndirectReference elementReference
            || resolvedElement.Value is not PdfDictionary element)
            throw new InvalidOperationException(
                $"Tagged annotation '{target.Name}' ParentTree mapping is not an indirect structure element.");
        if (!element.TryGetValue(Name("K"), out PdfObject? kidValue)
            || ResolveValue(kidValue,
                $"Annotation '{target.Name}' structure element /K value")
                is not PdfDictionary kid
            || !kid.TryGetValue(Name("Obj"), out PdfObject? objectValue)
            || ResolveWithIdentity(objectValue,
                $"Annotation '{target.Name}' OBJR /Obj value").FinalReference
                is not PdfIndirectReference objectReference
            || !SameIdentity(objectReference, target.Reference))
            throw new InvalidOperationException(
                $"Annotation '{target.Name}' ParentTree mapping does not reference the annotation.");
        var entries = element.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("Alt")] = UnicodeString(contents);
        update.ReplaceObject(elementReference.ObjectNumber,
            new PdfDictionary(entries));
    }

    private PendingRemoval ReadRemovalTarget(
        int pageIndex, PdfObject value, string fallbackName)
    {
        ResolvedValue resolved = ResolveWithIdentity(value,
            $"Page {pageIndex + 1} {fallbackName}");
        if (resolved.FinalReference is not PdfIndirectReference reference
            || resolved.Value is not PdfDictionary annotation)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} {fallbackName} is not an indirect annotation dictionary.");
        string name = fallbackName;
        if (annotation.TryGetValue(Name("NM"), out PdfObject? nameValue))
        {
            PdfObject resolvedName = ResolveValue(nameValue,
                $"Page {pageIndex + 1} {fallbackName} /NM value");
            if (resolvedName is not PdfString text)
                throw new InvalidOperationException(
                    $"Page {pageIndex + 1} {fallbackName} /NM value is not a string.");
            name = DecodePdfString(text);
        }
        PdfIndirectReference? popup = null;
        if (annotation.TryGetValue(Name("Popup"), out PdfObject? popupValue))
        {
            ResolvedValue resolvedPopup = ResolveWithIdentity(popupValue,
                $"Annotation '{name}' /Popup value");
            if (resolvedPopup.FinalReference is not PdfIndirectReference popupReference
                || resolvedPopup.Value is not PdfDictionary popupDictionary)
                throw new InvalidOperationException(
                    $"Annotation '{name}' has an invalid popup reference.");
            if (!popupDictionary.TryGetValue(Name("Parent"), out PdfObject? parentValue)
                || ResolveWithIdentity(parentValue,
                    $"Annotation '{name}' popup /Parent value").FinalReference
                    is not PdfIndirectReference parentReference
                || !SameIdentity(parentReference, reference))
                throw new InvalidOperationException(
                    $"Annotation '{name}' popup has no reciprocal parent reference.");
            popup = popupReference;
        }
        return new PendingRemoval(
            pageIndex, name, reference, annotation, popup);
    }

    private static bool SameIdentity(
        PdfIndirectReference left, PdfIndirectReference right) =>
        left.ObjectNumber == right.ObjectNumber
        && left.Generation == right.Generation;

    private void RemoveTaggedAnnotationStructure(
        PdfIncrementalUpdateBuilder update,
        IReadOnlyList<PendingRemoval> removals)
    {
        if (!_tree.Catalog.TryGetValue(StructTreeRootName,
                out PdfObject? rootValue)) return;
        ResolvedValue resolvedRoot = ResolveWithIdentity(rootValue,
            "The document structure-tree root");
        PdfDictionary root = resolvedRoot.Value as PdfDictionary
            ?? throw new InvalidOperationException(
                "The document structure-tree root is not a dictionary.");
        PdfIndirectReference rootReference;
        if (resolvedRoot.FinalReference is PdfIndirectReference indirectRoot)
            rootReference = indirectRoot;
        else
        {
            rootReference = FindStructureRootParentReference(root)
                ?? throw new NotSupportedException(
                    "A direct structure-tree root has no unambiguous indirect parent reference.");
            var catalogEntries = _tree.Catalog.ToDictionary(
                entry => entry.Key, entry => entry.Value);
            catalogEntries[StructTreeRootName] = rootReference;
            update.ReplaceObject(_tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries));
        }
        if (!root.TryGetValue(ParentTreeName, out PdfObject? parentTreeValue))
            throw new InvalidOperationException(
                "The tagged document has no ParentTree.");
        IReadOnlyList<PdfNumberTreeEntry> parentEntries =
            PdfNumberTree.Read(_document, parentTreeValue);
        var removedKeys = new HashSet<long>();
        var parentChildren = new Dictionary<
            (int ObjectNumber, int Generation),
            (PdfIndirectReference Parent, HashSet<(int ObjectNumber, int Generation)> Children)>();
        foreach (PendingRemoval removal in removals)
        {
            PdfNumberTreeEntry? mapping;
            if (removal.Dictionary.TryGetValue(Name("StructParent"),
                    out PdfObject? keyValue))
            {
                if (ResolveValue(keyValue,
                        $"Annotation '{removal.Name}' /StructParent value")
                        is not PdfInteger key || key.Value < 0)
                    throw new InvalidOperationException(
                        $"Tagged annotation '{removal.Name}' has an invalid structure-parent key.");
                mapping = parentEntries.SingleOrDefault(
                        entry => entry.Key == key.Value)
                    ?? throw new InvalidOperationException(
                        $"Tagged annotation '{removal.Name}' has no ParentTree mapping.");
            }
            else
            {
                PdfNumberTreeEntry[] hiddenMappings = [.. parentEntries.Where(entry =>
                    ParentMappingReferences(entry.Value, removal.Reference))];
                if (hiddenMappings.Length == 0) continue;
                if (hiddenMappings.Length > 1)
                    throw new InvalidOperationException(
                        $"Tagged annotation '{removal.Name}' has multiple ParentTree mappings.");
                mapping = hiddenMappings[0];
            }
            ResolvedValue resolvedElement = ResolveWithIdentity(mapping.Value,
                $"Annotation '{removal.Name}' ParentTree mapping");
            if (resolvedElement.FinalReference is not PdfIndirectReference elementReference
                || resolvedElement.Value is not PdfDictionary element)
                throw new InvalidOperationException(
                    $"Annotation '{removal.Name}' ParentTree mapping is not an indirect structure element.");
            if (!element.TryGetValue(Name("K"), out PdfObject? kidValue)
                || ResolveValue(kidValue,
                    $"Annotation '{removal.Name}' structure element /K value")
                    is not PdfDictionary kid
                || !kid.TryGetValue(Name("Obj"), out PdfObject? objectValue)
                || ResolveWithIdentity(objectValue,
                    $"Annotation '{removal.Name}' OBJR /Obj value").FinalReference
                    is not PdfIndirectReference objectReference
                || !SameIdentity(objectReference, removal.Reference))
                throw new InvalidOperationException(
                    $"Annotation '{removal.Name}' ParentTree mapping does not reference the annotation.");
            if (!element.TryGetValue(Name("P"), out PdfObject? parentValue))
                throw new InvalidOperationException(
                    $"Annotation '{removal.Name}' structure element has no parent.");
            ResolvedValue resolvedParent = ResolveWithIdentity(parentValue,
                $"Annotation '{removal.Name}' structure parent");
            if (resolvedParent.FinalReference is not PdfIndirectReference parentReference
                || resolvedParent.Value is not PdfDictionary)
                throw new InvalidOperationException(
                    $"Annotation '{removal.Name}' structure parent is not indirect.");
            var parentIdentity = (parentReference.ObjectNumber,
                parentReference.Generation);
            if (!parentChildren.TryGetValue(parentIdentity, out var group))
            {
                group = (parentReference, []);
                parentChildren.Add(parentIdentity, group);
            }
            group.Children.Add((elementReference.ObjectNumber,
                elementReference.Generation));
            removedKeys.Add(mapping.Key);
        }
        var numbers = new List<PdfObject>();
        foreach (PdfNumberTreeEntry entry in parentEntries
                     .Where(entry => !removedKeys.Contains(entry.Key))
                     .OrderBy(entry => entry.Key))
        {
            numbers.Add(new PdfInteger(entry.Key));
            numbers.Add(entry.Value);
        }
        PdfIndirectReference rebuiltParentTree = update.AddObject(
            Dictionary(("Nums", new PdfArray(numbers))));
        var rootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        rootEntries[ParentTreeName] = rebuiltParentTree;
        update.ReplaceObject(rootReference.ObjectNumber,
            new PdfDictionary(rootEntries));

        foreach (var (Parent, Children) in parentChildren.Values)
        {
            PdfDictionary parent = (PdfDictionary)ResolveValue(Parent,
                "An annotation structure parent");
            var entries = parent.ToDictionary(entry => entry.Key, entry => entry.Value);
            if (!entries.TryGetValue(StructureKidsName, out PdfObject? kidsValue))
                throw new InvalidOperationException(
                    "An annotation structure parent has no /K value.");
            PdfObject resolvedKids = ResolveValue(kidsValue,
                "An annotation structure parent /K value");
            IEnumerable<PdfObject> kids = resolvedKids is PdfArray array
                ? array : [kidsValue];
            PdfObject[] retained = [.. kids.Where(kid =>
            {
                ResolvedValue resolvedKid = ResolveWithIdentity(kid,
                    "An annotation structure parent child");
                return resolvedKid.FinalReference is not PdfIndirectReference reference
                    || !Children.Contains((reference.ObjectNumber,
                        reference.Generation));
            })];
            if (retained.Length == 0)
                entries.Remove(StructureKidsName);
            else
                entries[StructureKidsName] = retained.Length == 1
                    ? retained[0] : new PdfArray(retained);
            update.ReplaceObject(Parent.ObjectNumber,
                new PdfDictionary(entries));
        }

        bool ParentMappingReferences(
            PdfObject value, PdfIndirectReference annotationReference)
        {
            PdfObject resolved = ResolveValue(value,
                "A ParentTree annotation mapping");
            if (resolved is not PdfDictionary element
                || !element.TryGetValue(Name("K"), out PdfObject? kidValue)
                || ResolveValue(kidValue,
                    "A ParentTree annotation mapping /K value")
                    is not PdfDictionary kid
                || !kid.TryGetValue(Name("Obj"), out PdfObject? objectValue))
                return false;
            return ResolveWithIdentity(objectValue,
                    "A ParentTree annotation mapping OBJR /Obj value")
                    .FinalReference is PdfIndirectReference reference
                && SameIdentity(reference, annotationReference);
        }
    }

    private void AppendPageAnnotations(
        PdfIncrementalUpdateBuilder update, PdfPageTreeEntry page,
        IEnumerable<(PdfIndirectReference Reference, string Name)> additions,
        IReadOnlyList<PendingRemoval>? removals = null)
    {
        var pending = additions.ToArray();
        var removed = (removals ?? []).SelectMany(value =>
                value.PopupReference is null
                    ? [value.Reference]
                    : new[] { value.Reference, value.PopupReference })
            .Select(reference => (reference.ObjectNumber, reference.Generation))
            .ToHashSet();
        var values = new List<PdfObject>();
        var annotationIdentities = new HashSet<(int ObjectNumber, int Generation)>();
        var annotationNames = new HashSet<string>(StringComparer.Ordinal);
        if (page.Dictionary.TryGetValue(AnnotsName, out PdfObject existing))
        {
            ResolvedValue resolvedArray = ResolveWithIdentity(existing,
                $"Page {page.Index + 1} /Annots value");
            PdfArray array = resolvedArray.Value as PdfArray
                ?? throw new InvalidOperationException($"Page {page.Index + 1} /Annots value is not an array.");
            PrepareExistingAnnotations(array);
            foreach (PdfObject annotation in array)
                ValidateExistingAnnotation(annotation);
            foreach (PdfObject annotation in array)
            {
                ResolvedValue resolved = ResolveWithIdentity(annotation,
                    $"Page {page.Index + 1} annotation value");
                PdfIndirectReference reference = resolved.FinalReference!;
                if (removed.Contains((reference.ObjectNumber, reference.Generation)))
                    continue;
                PdfDictionary dictionary = (PdfDictionary)resolved.Value;
                foreach (string relationship in new[] { "IRT", "Parent" })
                {
                    if (!dictionary.TryGetValue(Name(relationship),
                            out PdfObject? relationshipValue)) continue;
                    ResolvedValue target = ResolveWithIdentity(relationshipValue,
                        $"Page {page.Index + 1} annotation /{relationship} value");
                    if (target.FinalReference is PdfIndirectReference targetReference
                        && removed.Contains((targetReference.ObjectNumber,
                            targetReference.Generation)))
                        throw new InvalidOperationException(
                            $"Removing an annotation would orphan a retained /{relationship} relationship.");
                }
                values.Add(annotation);
            }
            foreach (PendingRemoval removal in removals ?? [])
                annotationNames.Remove(removal.Name);
            AddPendingAnnotations();
            if (resolvedArray.FinalReference is PdfIndirectReference arrayReference)
            {
                if (IsSharedAnnotationArray(arrayReference))
                {
                    PdfIndirectReference replacementArray = update.AddObject(
                        new PdfArray(values));
                    PdfDictionary replacementPage = new(page.Dictionary
                        .Where(entry => !entry.Key.Equals(AnnotsName))
                        .Append(new KeyValuePair<PdfName, PdfObject>(
                            AnnotsName, replacementArray)));
                    update.ReplaceObject(page.Reference.ObjectNumber, replacementPage);
                    return;
                }
                update.ReplaceObject(arrayReference.ObjectNumber, new PdfArray(values));
                return;
            }
        }
        else
            AddPendingAnnotations();
        var replacement = new PdfDictionary(page.Dictionary
            .Where(entry => !entry.Key.Equals(AnnotsName))
            .Append(new KeyValuePair<PdfName, PdfObject>(AnnotsName, new PdfArray(values))));
        update.ReplaceObject(page.Reference.ObjectNumber, replacement);

        void AddPendingAnnotations()
        {
            foreach ((PdfIndirectReference reference, string name) in pending)
            {
                if (!annotationNames.Add(name))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} already contains annotation /NM value '{name}'.");
                values.Add(reference);
            }
        }

        void PrepareExistingAnnotations(PdfArray annotations)
        {
            foreach (PdfObject annotation in annotations)
            {
                if (annotation is not PdfIndirectReference reference)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} /Annots contains a direct annotation entry.");
                ResolvedValue resolvedAnnotation = ResolveWithIdentity(reference,
                    $"Page {page.Index + 1} annotation value");
                PdfIndirectReference finalReference = resolvedAnnotation.FinalReference
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} /Annots contains a direct annotation entry.");
                if (!annotationIdentities.Add(
                        (finalReference.ObjectNumber, finalReference.Generation)))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} /Annots contains a duplicate annotation reference.");
            }
        }

        void ValidateExistingAnnotation(PdfObject value)
        {
            PdfIndirectReference reference = (PdfIndirectReference)value;
            ResolvedValue resolvedAnnotation = ResolveWithIdentity(reference,
                $"Page {page.Index + 1} annotation value");
            PdfIndirectReference annotationReference = resolvedAnnotation.FinalReference
                ?? throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots contains a direct annotation entry.");
            PdfDictionary annotation = resolvedAnnotation.Value as PdfDictionary
                ?? throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots contains a stale or non-dictionary entry.");
            PdfObject Resolve(PdfObject item) => ResolveValue(item,
                $"Page {page.Index + 1} annotation value");
            if (annotation.TryGetValue(Name("Type"), out PdfObject? type)
                && (Resolve(type) is not PdfName typeName
                    || typeName.ValueAsLatin1() != "Annot"))
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots contains an entry with an invalid /Type value.");
            if (!annotation.TryGetValue(Name("Subtype"), out PdfObject? subtype)
                || Resolve(subtype) is not PdfName)
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots contains an entry without a /Subtype name.");
            if (!annotation.TryGetValue(Name("Rect"), out PdfObject? rectangle)
                || Resolve(rectangle) is not PdfArray coordinates
                || coordinates.Count != 4
                || coordinates.Any(item => Resolve(item) is not (PdfInteger or PdfReal)))
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots contains an entry without a four-number /Rect array.");
            if (annotation.TryGetValue(Name("P"), out PdfObject? owner)
                && (ResolveWithIdentity(owner,
                        $"Page {page.Index + 1} annotation /P value").FinalReference
                        is not PdfIndirectReference ownerReference
                    || ResolveWithIdentity(page.Reference,
                        $"Page {page.Index + 1} reference").FinalReference
                        is not PdfIndirectReference pageReference
                    || ownerReference.ObjectNumber != pageReference.ObjectNumber
                    || ownerReference.Generation != pageReference.Generation))
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots contains an entry whose /P value identifies another page.");
            if (annotation.TryGetValue(Name("NM"), out PdfObject? nameValue))
            {
                PdfString name = Resolve(nameValue) as PdfString
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} /Annots contains an entry whose /NM value is not a string.");
                string decoded = PdfUnicodeEncoding.DecodeTextString(name.Bytes.Span,
                    $"Page {page.Index + 1} annotation /NM value");
                if (!annotationNames.Add(decoded))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} /Annots contains duplicate /NM values.");
            }
            foreach (string key in new[] { "Contents", "T", "Subj" })
            {
                if (!annotation.TryGetValue(Name(key), out PdfObject? textValue)) continue;
                PdfString text = Resolve(textValue) as PdfString
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /{key} value is not a string.");
                PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span,
                    $"Page {page.Index + 1} annotation /{key} value");
            }
            if (annotation.TryGetValue(Name("Lang"), out PdfObject? languageValue))
            {
                PdfString language = Resolve(languageValue) as PdfString
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /Lang value is not a string.");
                string tag = PdfUnicodeEncoding.DecodeTextString(language.Bytes.Span,
                    $"Page {page.Index + 1} annotation /Lang value");
                if (!PdfLanguageTag.IsValid(tag))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /Lang value is not a valid BCP 47 language tag.");
            }
            if (annotation.TryGetValue(Name("RC"), out PdfObject? richText)
                && Resolve(richText) is not (PdfString or PdfStream))
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} annotation /RC value is not a string or stream.");
            if (annotation.TryGetValue(Name("IT"), out PdfObject? intent)
                && Resolve(intent) is not PdfName)
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} annotation /IT value is not a name.");
            if (annotation.TryGetValue(Name("IRT"), out PdfObject? replyValue))
            {
                ResolvedValue resolvedReply = ResolveWithIdentity(replyValue,
                    $"Page {page.Index + 1} annotation /IRT value");
                if (resolvedReply.FinalReference is not PdfIndirectReference replyReference
                    || resolvedReply.Value is not PdfDictionary reply
                    || !reply.TryGetValue(Name("Subtype"), out PdfObject? replySubtype)
                    || Resolve(replySubtype) is not PdfName)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /IRT value is not an indirect typed annotation dictionary.");
                if (!annotationIdentities.Contains((replyReference.ObjectNumber,
                        replyReference.Generation)))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /IRT target is not registered on the page.");
            }
            if (annotation.TryGetValue(Name("RT"), out PdfObject? replyType))
            {
                string replyName = (Resolve(replyType) as PdfName)?.ValueAsLatin1()
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /RT value is not a name.");
                if (replyName is not ("R" or "Group"))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /RT value /{replyName} is not defined.");
            }
            bool hasState = annotation.TryGetValue(
                Name("State"), out PdfObject? annotationStateValue);
            bool hasStateModel = annotation.TryGetValue(
                Name("StateModel"), out PdfObject? stateModelValue);
            if (hasState || hasStateModel)
            {
                if (((PdfName)Resolve(subtype)).ValueAsLatin1() != "Text")
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation state is only defined for text annotations.");
                if (!hasState || Resolve(annotationStateValue!) is not PdfString stateString
                    || !hasStateModel || Resolve(stateModelValue!) is not PdfString modelString)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /State and /StateModel must both be strings.");
                string stateModel = PdfUnicodeEncoding.DecodeTextString(modelString.Bytes.Span,
                    $"Page {page.Index + 1} annotation /StateModel value");
                string state = PdfUnicodeEncoding.DecodeTextString(stateString.Bytes.Span,
                    $"Page {page.Index + 1} annotation /State value");
                bool validState = stateModel switch
                {
                    "Marked" => state is "Marked" or "Unmarked",
                    "Review" => state is "Accepted" or "Rejected" or "Cancelled"
                        or "Completed" or "None",
                    _ => false
                };
                if (!validState)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /State /{state} is not defined for /StateModel /{stateModel}.");
            }
            if (annotation.TryGetValue(Name("Popup"), out PdfObject? popupValue))
            {
                ResolvedValue resolvedPopup = ResolveWithIdentity(popupValue,
                    $"Page {page.Index + 1} annotation /Popup value");
                if (resolvedPopup.FinalReference is not PdfIndirectReference popupReference
                    || resolvedPopup.Value is not PdfDictionary popup
                    || !popup.TryGetValue(Name("Subtype"), out PdfObject? popupSubtype)
                    || Resolve(popupSubtype) is not PdfName popupSubtypeName
                    || popupSubtypeName.ValueAsLatin1() != "Popup")
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /Popup value is not an indirect popup annotation.");
                if (!annotationIdentities.Contains((popupReference.ObjectNumber,
                        popupReference.Generation)))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /Popup target is not registered on the page.");
                if (!popup.TryGetValue(Name("Parent"), out PdfObject? popupParent)
                    || ResolveWithIdentity(popupParent,
                        $"Page {page.Index + 1} popup /Parent value").FinalReference
                        is not PdfIndirectReference popupParentReference
                    || popupParentReference.ObjectNumber != annotationReference.ObjectNumber
                    || popupParentReference.Generation != annotationReference.Generation)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /Popup target does not link back through /Parent.");
            }
            string retainedSubtype = ((PdfName)Resolve(subtype)).ValueAsLatin1();
            if (retainedSubtype == "Popup")
            {
                if (!annotation.TryGetValue(Name("Parent"), out PdfObject? parentValue)
                    || ResolveWithIdentity(parentValue,
                        $"Page {page.Index + 1} popup /Parent value") is not
                        { FinalReference: PdfIndirectReference parentReference,
                          Value: PdfDictionary parent })
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} popup annotation has no indirect /Parent dictionary.");
                if (!annotationIdentities.Contains((parentReference.ObjectNumber,
                        parentReference.Generation)))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} popup /Parent is not registered on the page.");
                if (!parent.TryGetValue(Name("Popup"), out PdfObject? parentPopup)
                    || ResolveWithIdentity(parentPopup,
                        $"Page {page.Index + 1} annotation /Popup value").FinalReference
                        is not PdfIndirectReference parentPopupReference
                    || parentPopupReference.ObjectNumber != annotationReference.ObjectNumber
                    || parentPopupReference.Generation != annotationReference.Generation)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} popup /Parent does not link back through /Popup.");
            }
            foreach (string key in new[] { "M", "CreationDate" })
            {
                if (!annotation.TryGetValue(Name(key), out PdfObject? dateValue)) continue;
                if (Resolve(dateValue) is not PdfString date
                    || !PdfDateStringValidator.IsValid(date))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /{key} value is not a valid PDF date.");
            }
            if (annotation.TryGetValue(Name("F"), out PdfObject? flags)
                && (Resolve(flags) is not PdfInteger flagValue || flagValue.Value < 0))
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} annotation /F value is not a nonnegative integer.");
            if (annotation.TryGetValue(Name("CA"), out PdfObject? opacity))
            {
                double opacityValue = Resolve(opacity) switch
                {
                    PdfInteger integer => integer.Value,
                    PdfReal real => real.Value,
                    _ => double.NaN
                };
                if (!double.IsFinite(opacityValue) || opacityValue is < 0 or > 1)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /CA value is not a number from 0 through 1.");
            }
            if (annotation.TryGetValue(Name("C"), out PdfObject? colorValue))
            {
                PdfArray color = Resolve(colorValue) as PdfArray
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /C value is not an array.");
                if (color.Count is not (0 or 1 or 3 or 4)
                    || color.Any(item => !TryFiniteNumber(Resolve(item), out double component)
                        || component is < 0 or > 1))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /C value is not a valid color array.");
            }
            if (annotation.TryGetValue(Name("Border"), out PdfObject? borderValue))
            {
                PdfArray border = Resolve(borderValue) as PdfArray
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /Border value is not an array.");
                if (border.Count is not (3 or 4)
                    || Enumerable.Range(0, 3).Any(index =>
                        !TryFiniteNumber(Resolve(border[index]), out double number)
                        || number < 0))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /Border value has invalid radii or width.");
                if (border.Count == 4)
                {
                    PdfArray dash = Resolve(border[3]) as PdfArray
                        ?? throw new InvalidOperationException(
                            $"Page {page.Index + 1} annotation /Border dash value is not an array.");
                    if (dash.Any(item => !TryFiniteNumber(Resolve(item), out double number)
                            || number < 0)
                        || dash.Count > 0 && dash.All(item =>
                            TryFiniteNumber(Resolve(item), out double number) && number == 0))
                        throw new InvalidOperationException(
                            $"Page {page.Index + 1} annotation /Border dash array is invalid.");
                }
            }
            if (annotation.TryGetValue(Name("BS"), out PdfObject? borderStyleValue))
            {
                PdfDictionary borderStyle = Resolve(borderStyleValue) as PdfDictionary
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /BS value is not a dictionary.");
                if (borderStyle.TryGetValue(Name("W"), out PdfObject? width)
                    && (!TryFiniteNumber(Resolve(width), out double widthValue)
                        || widthValue < 0))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /BS /W value is not nonnegative.");
                if (borderStyle.TryGetValue(Name("S"), out PdfObject? style))
                {
                    string styleName = (Resolve(style) as PdfName)?.ValueAsLatin1()
                        ?? throw new InvalidOperationException(
                            $"Page {page.Index + 1} annotation /BS /S value is not a name.");
                    if (styleName is not ("S" or "D" or "B" or "I" or "U"))
                        throw new InvalidOperationException(
                            $"Page {page.Index + 1} annotation /BS /S value /{styleName} is not defined.");
                }
                if (borderStyle.TryGetValue(Name("D"), out PdfObject? dashValue))
                {
                    PdfArray dash = Resolve(dashValue) as PdfArray
                        ?? throw new InvalidOperationException(
                            $"Page {page.Index + 1} annotation /BS /D value is not an array.");
                    if (dash.Any(item => !TryFiniteNumber(Resolve(item), out double number)
                            || number < 0)
                        || dash.Count > 0 && dash.All(item =>
                            TryFiniteNumber(Resolve(item), out double number) && number == 0))
                        throw new InvalidOperationException(
                            $"Page {page.Index + 1} annotation /BS /D dash array is invalid.");
                }
            }
            if (annotation.TryGetValue(Name("QuadPoints"), out PdfObject? quadrilateralValue))
            {
                PdfArray points = Resolve(quadrilateralValue) as PdfArray
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /QuadPoints value is not an array.");
                if (points.Count == 0 || points.Count % 8 != 0
                    || points.Any(item => !TryFiniteNumber(Resolve(item), out _)))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /QuadPoints value is not a nonempty sequence of numeric quadrilaterals.");
            }
            if (annotation.TryGetValue(Name("StructParent"), out PdfObject? structureParent)
                && (Resolve(structureParent) is not PdfInteger parentKey
                    || parentKey.Value < 0))
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} annotation /StructParent value is not a nonnegative integer.");
            PdfName? appearanceState = null;
            if (annotation.TryGetValue(Name("AS"), out PdfObject? stateValue))
                appearanceState = Resolve(stateValue) as PdfName
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /AS value is not a name.");
            if (annotation.TryGetValue(Name("AP"), out PdfObject? appearanceValue))
            {
                PdfDictionary appearances = Resolve(appearanceValue) as PdfDictionary
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /AP value is not a dictionary.");
                if (!appearances.TryGetValue(Name("N"), out PdfObject? normalValue))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /AP dictionary has no normal /N appearance.");
                ValidateAppearance(normalValue, "normal /N");
                foreach (string key in new[] { "R", "D" })
                    if (appearances.TryGetValue(Name(key), out PdfObject? optionalAppearance))
                        ValidateAppearance(optionalAppearance, $"/{key}");
                if (appearanceState is not null
                    && Resolve(normalValue) is PdfDictionary normalStates
                    && !normalStates.ContainsKey(appearanceState))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation /AS value has no matching normal appearance state.");
            }

            void ValidateAppearance(PdfObject appearance, string description)
            {
                PdfObject resolvedAppearance = Resolve(appearance);
                if (resolvedAppearance is PdfStream stream)
                {
                    ValidateAppearanceStream(stream, description);
                    return;
                }
                if (resolvedAppearance is not PdfDictionary states || states.Count == 0
                    || states.Any(entry => Resolve(entry.Value) is not PdfStream))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {description} appearance is not a stream or nonempty state dictionary of streams.");
                foreach (var entry in states)
                    ValidateAppearanceStream((PdfStream)Resolve(entry.Value),
                        $"{description} /{entry.Key.ValueAsLatin1()}");
            }

            void ValidateAppearanceStream(PdfStream stream, string description)
            {
                PdfDictionary dictionary = stream.Dictionary;
                if (dictionary.TryGetValue(Name("Type"), out PdfObject? appearanceType)
                    && (Resolve(appearanceType) is not PdfName appearanceTypeName
                        || appearanceTypeName.ValueAsLatin1() != "XObject"))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {description} appearance has an invalid /Type value.");
                if (!dictionary.TryGetValue(Name("Subtype"), out PdfObject? appearanceSubtype)
                    || Resolve(appearanceSubtype) is not PdfName appearanceSubtypeName
                    || appearanceSubtypeName.ValueAsLatin1() != "Form")
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {description} appearance has no /Subtype /Form value.");
                if (!dictionary.TryGetValue(Name("BBox"), out PdfObject? boundsValue)
                    || Resolve(boundsValue) is not PdfArray bounds || bounds.Count != 4
                    || bounds.Any(item => !TryFiniteNumber(Resolve(item), out _)))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {description} appearance has no finite four-number /BBox.");
                if (dictionary.TryGetValue(Name("Matrix"), out PdfObject? matrixValue)
                    && (Resolve(matrixValue) is not PdfArray matrix || matrix.Count != 6
                        || matrix.Any(item => !TryFiniteNumber(Resolve(item), out _))))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {description} appearance has no finite six-number /Matrix.");
                if (dictionary.TryGetValue(Name("Resources"), out PdfObject? resources)
                    && Resolve(resources) is not PdfDictionary)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {description} appearance /Resources value is not a dictionary.");
            }

            static bool TryFiniteNumber(PdfObject item, out double number)
            {
                number = item switch
                {
                    PdfInteger integer => integer.Value,
                    PdfReal real => real.Value,
                    _ => double.NaN
                };
                return double.IsFinite(number);
            }
        }
    }

    private bool IsSharedAnnotationArray(PdfIndirectReference expected)
    {
        int matches = 0;
        foreach (PdfPageTreeEntry candidate in _pages)
        {
            if (!candidate.Dictionary.TryGetValue(AnnotsName, out PdfObject? value))
                continue;
            ResolvedValue resolved = ResolveWithIdentity(value,
                $"Page {candidate.Index + 1} /Annots value");
            if (resolved.FinalReference is not PdfIndirectReference reference
                || reference.ObjectNumber != expected.ObjectNumber
                || reference.Generation != expected.Generation)
                continue;
            matches++;
            if (matches > 1) return true;
        }
        return false;
    }

    private static PdfDictionary TextNoteDictionary(
        PendingTextNote note, PdfIndirectReference page, PdfIndirectReference annotation,
        PdfIndirectReference appearance, PdfIndirectReference? popup,
        Dictionary<string, PdfIndirectReference> annotationNames)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")), ("Subtype", Name("Text")),
            ("Rect", Rectangle(note.X, note.Y, note.Size, note.Size)),
            ("P", page), ("F", new PdfInteger((int)(note.Metadata?.Flags
                ?? PdfAnnotationFlags.Print))),
            ("Contents", UnicodeString(note.Contents)),
            ("NM", note.Name is null
                ? Latin1String($"KillerPDF-Note-{annotation.ObjectNumber}")
                : UnicodeString(note.Name)),
            ("Name", Name(PdfTextNoteIconNames.Name(note.Icon))),
            ("Open", new PdfBoolean(note.Open)),
            ("C", ColorArray(note.Color)),
            ("AP", Dictionary(("N", appearance)))
        };
        if (note.State is not null)
        {
            entries.Add(("State", Name(PdfTextNoteStateNames.State(note.State.Value))));
            entries.Add(("StateModel", Name(PdfTextNoteStateNames.Model(note.State.Value))));
        }
        if (note.InReplyTo is not null)
        {
            entries.Add(("IRT", annotationNames[note.InReplyTo]));
            entries.Add(("RT", Name(PdfAnnotationReplyTypeNames.Name(note.ReplyType))));
        }
        if (popup is not null) entries.Add(("Popup", popup));
        PdfLinkAnnotationFactory.AddMetadata(entries, note.Metadata);
        return Dictionary([.. entries]);
    }

    private static PdfDictionary PopupDictionary(
        PendingTextNote note, PdfIndirectReference page,
        PdfIndirectReference parent)
    {
        PdfAnnotationPopup popup = note.Popup!;
        return Dictionary(
            ("Type", Name("Annot")), ("Subtype", Name("Popup")),
            ("Rect", Rectangle(popup.X, popup.Y, popup.Width, popup.Height)),
            ("P", page),
            ("F", new PdfInteger((int)(note.Metadata?.Flags
                ?? PdfAnnotationFlags.Print))),
            ("Parent", parent), ("Open", new PdfBoolean(popup.Open)));
    }

    private static PdfStream TextNoteAppearance(PendingTextNote note)
    {
        using var output = new MemoryStream();
        WriteAscii(output,
            $"q\n{ColorOperands(note.Color)} rg\n0 0 {Format(note.Size)} {Format(note.Size)} re\nf\n" +
            $"0 G\n1 w\n0.5 0.5 {Format(Math.Max(0, note.Size - 1))} {Format(Math.Max(0, note.Size - 1))} re\nS\n");
        double fold = note.Size * 0.3;
        WriteAscii(output,
            $"{Format(note.Size - fold)} {Format(note.Size)} m\n" +
            $"{Format(note.Size - fold)} {Format(note.Size - fold)} l\n" +
            $"{Format(note.Size)} {Format(note.Size - fold)} l\nS\n" +
            $"{Format(note.Size * 0.22)} {Format(note.Size * 0.58)} m\n" +
            $"{Format(note.Size * 0.7)} {Format(note.Size * 0.58)} l\n" +
            $"{Format(note.Size * 0.22)} {Format(note.Size * 0.38)} m\n" +
            $"{Format(note.Size * 0.62)} {Format(note.Size * 0.38)} l\nS\nQ\n");
        return Appearance(note.Size, note.Size, Dictionary(), output.ToArray());
    }

    private static PdfDictionary TextMarkupDictionary(
        PendingTextMarkup markup, PdfIndirectReference page, PdfIndirectReference annotation,
        PdfIndirectReference appearance)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")), ("Subtype", Name(markup.Type.ToString())),
            ("Rect", Rectangle(markup.X, markup.Y, markup.Width, markup.Height)),
            ("QuadPoints", new PdfArray(markup.Quads.SelectMany(quad =>
                new PdfObject[]
                {
                    Number(quad.UpperLeft.X), Number(quad.UpperLeft.Y),
                    Number(quad.UpperRight.X), Number(quad.UpperRight.Y),
                    Number(quad.LowerLeft.X), Number(quad.LowerLeft.Y),
                    Number(quad.LowerRight.X), Number(quad.LowerRight.Y)
                }))),
            ("P", page), ("F", new PdfInteger((int)(markup.Metadata?.Flags
                ?? PdfAnnotationFlags.Print))),
            ("NM", Latin1String($"KillerPDF-{markup.Type}-{annotation.ObjectNumber}")),
            ("C", ColorArray(markup.Color)), ("CA", Number(markup.Opacity)),
            ("AP", Dictionary(("N", appearance)))
        };
        if (!string.IsNullOrEmpty(markup.Contents))
            entries.Add(("Contents", UnicodeString(markup.Contents)));
        PdfLinkAnnotationFactory.AddMetadata(entries, markup.Metadata);
        return Dictionary([.. entries]);
    }

    private static PdfStream TextMarkupAppearance(PendingTextMarkup markup)
    {
        PdfDictionary graphicsState = Dictionary(
            ("Type", Name("ExtGState")), ("ca", Number(markup.Opacity)),
            ("CA", Number(markup.Opacity)), ("BM", Name("Multiply")));
        PdfDictionary resources = Dictionary(("ExtGState", new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("GS1"), graphicsState)])));
        string drawing = markup.Type switch
        {
            PdfTextMarkupType.Highlight => HighlightQuads(markup),
            PdfTextMarkupType.Underline => MarkupLines(markup, strikeOut: false),
            PdfTextMarkupType.StrikeOut => MarkupLines(markup, strikeOut: true),
            PdfTextMarkupType.Squiggly => SquigglyLines(markup),
            _ => throw new InvalidOperationException(
                $"Unsupported text-markup type: {markup.Type}.")
        };
        byte[] content = Encoding.ASCII.GetBytes($"q\n/GS1 gs\n{drawing}Q\n");
        return Appearance(markup.Width, markup.Height, resources, content);
    }

    private static string HighlightQuads(PendingTextMarkup markup)
    {
        var output = new StringBuilder($"{ColorOperands(markup.Color)} rg\n");
        foreach (PdfTextQuad quad in markup.Quads)
        {
            AppendPoint(output, quad.UpperLeft, markup, "m");
            AppendPoint(output, quad.UpperRight, markup, "l");
            AppendPoint(output, quad.LowerRight, markup, "l");
            AppendPoint(output, quad.LowerLeft, markup, "l");
            output.Append("h\nf\n");
        }
        return output.ToString();
    }

    private static string MarkupLines(PendingTextMarkup markup, bool strikeOut)
    {
        double averageHeight = markup.Quads.Average(quad =>
            (Distance(quad.UpperLeft, quad.LowerLeft)
                + Distance(quad.UpperRight, quad.LowerRight)) / 2);
        var output = new StringBuilder(
            $"{ColorOperands(markup.Color)} RG\n" +
            $"{Format(Math.Max(0.75, averageHeight * 0.07))} w\n");
        foreach (PdfTextQuad quad in markup.Quads)
        {
            PdfPoint start = strikeOut
                ? Midpoint(quad.UpperLeft, quad.LowerLeft)
                : quad.LowerLeft;
            PdfPoint end = strikeOut
                ? Midpoint(quad.UpperRight, quad.LowerRight)
                : quad.LowerRight;
            AppendPoint(output, start, markup, "m");
            AppendPoint(output, end, markup, "l");
            output.Append("S\n");
        }
        return output.ToString();
    }

    private static string SquigglyLines(PendingTextMarkup markup)
    {
        var output = new StringBuilder($"{ColorOperands(markup.Color)} RG\n");
        foreach (PdfTextQuad quad in markup.Quads)
        {
            double dx = quad.LowerRight.X - quad.LowerLeft.X;
            double dy = quad.LowerRight.Y - quad.LowerLeft.Y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            double height = (Distance(quad.UpperLeft, quad.LowerLeft)
                + Distance(quad.UpperRight, quad.LowerRight)) / 2;
            double amplitude = Math.Max(0.75, height * 0.1);
            double step = Math.Max(1.5, amplitude * 2);
            double ux = dx / length;
            double uy = dy / length;
            double nx = -uy;
            double ny = ux;
            output.Append(Format(Math.Max(0.75, amplitude * 0.55))).Append(" w\n");
            AppendPoint(output, quad.LowerLeft, markup, "m");
            bool high = true;
            for (double distance = step; distance < length; distance += step)
            {
                double offset = high ? amplitude : 0;
                AppendPoint(output, new PdfPoint(
                    quad.LowerLeft.X + (ux * distance) + (nx * offset),
                    quad.LowerLeft.Y + (uy * distance) + (ny * offset)),
                    markup, "l");
                high = !high;
            }
            AppendPoint(output, quad.LowerRight, markup, "l");
            output.Append("S\n");
        }
        return output.ToString();
    }

    private static void AppendPoint(
        StringBuilder output, PdfPoint point, PendingTextMarkup markup,
        string operation) => output.Append(Format(point.X - markup.X)).Append(' ')
            .Append(Format(point.Y - markup.Y)).Append(' ')
            .Append(operation).Append('\n');

    private static PdfPoint Midpoint(PdfPoint left, PdfPoint right) =>
        new((left.X + right.X) / 2, (left.Y + right.Y) / 2);

    private static double Distance(PdfPoint left, PdfPoint right) =>
        Math.Sqrt(Math.Pow(right.X - left.X, 2)
            + Math.Pow(right.Y - left.Y, 2));

    private static PdfDictionary FreeTextDictionary(
        PendingFreeText value, PdfIndirectReference page, PdfIndirectReference annotation,
        PdfIndirectReference appearance, PdfName fontResource)
    {
        Bounds bounds = FreeTextBounds(value);
        var entries = CommonEntries("FreeText", bounds.X, bounds.Y,
            bounds.Width, bounds.Height,
            page, annotation, appearance, value.BorderColor, value.Opacity,
            value.Contents, value.Metadata);
        entries.Add(("DA", Latin1String(
            $"{NameToken(fontResource)} {Format(value.FontSize)} Tf {ColorOperands(value.TextColor)} rg")));
        entries.Add(("Q", new PdfInteger((int)value.Alignment)));
        entries.Add(("IT", Name(PdfAnnotationIntentNames.Name(value.Intent))));
        if (value.CalloutLine is not null)
        {
            entries.Add(("CL", new PdfArray(value.CalloutLine.SelectMany(point =>
                new PdfObject[] { Number(point.X), Number(point.Y) }))));
            entries.Add(("LE", Name(PdfLineEndingStyleNames.Name(
                value.CalloutEnding))));
        }
        entries.Add(("BS", BorderStyle(value.BorderWidth, value.DashPattern)));
        if (value.FillColor.HasValue) entries.Add(("IC", ColorArray(value.FillColor.Value)));
        return Dictionary([.. entries]);
    }

    private static PdfStream FreeTextAppearance(
        PendingFreeText value, PdfName fontResource, PdfIndirectReference type0Reference,
        EmbeddedFontUsage usage)
    {
        PdfDictionary resources = OpacityResources(value.Opacity,
            (fontResource, type0Reference));
        Bounds bounds = FreeTextBounds(value);
        using var output = new MemoryStream();
        WriteAscii(output, "q\n/GS1 gs\n");
        WriteAscii(output, DashOperator(value.DashPattern));
        if (value.CalloutLine is not null)
            WriteFreeTextCallout(output, value, bounds);
        WriteAscii(output,
            $"q\n1 0 0 1 {Format(value.X - bounds.X)} " +
            $"{Format(value.Y - bounds.Y)} cm\n");
        WriteBox(output, value.Width, value.Height, value.BorderWidth,
            value.BorderColor, value.FillColor, ellipse: false);
        WriteFreeText(output, value, fontResource, usage);
        output.Write("Q\nQ\n"u8);
        return Appearance(bounds.Width, bounds.Height, resources, output.ToArray());
    }

    private static Bounds FreeTextBounds(PendingFreeText value)
    {
        if (value.CalloutLine is null)
            return new Bounds(value.X, value.Y, value.Width, value.Height);
        PdfPoint[] points =
        [
            new(value.X, value.Y),
            new(value.X + value.Width, value.Y + value.Height),
            .. value.CalloutLine
        ];
        return PointBounds(points, Math.Max(value.BorderWidth, 1));
    }

    private static void WriteFreeTextCallout(
        Stream output, PendingFreeText value, Bounds bounds)
    {
        PdfPoint[] points = [.. value.CalloutLine!
            .Select(point => new PdfPoint(
                point.X - bounds.X, point.Y - bounds.Y))];
        WriteAscii(output,
            $"{ColorOperands(value.BorderColor)} RG\n" +
            $"{Format(value.BorderWidth)} w\n" +
            $"{Format(points[0].X)} {Format(points[0].Y)} m\n");
        foreach (PdfPoint point in points.Skip(1))
            WriteAscii(output,
                $"{Format(point.X)} {Format(point.Y)} l\n");
        output.Write("S\n"u8);
        WriteLineEnding(output, points[0].X, points[0].Y,
            points[1].X, points[1].Y, value.CalloutEnding,
            value.BorderWidth, value.BorderColor, null);
    }

    private static PdfDictionary LineDictionary(
        PendingLine line, PdfIndirectReference page, PdfIndirectReference annotation,
        PdfIndirectReference appearance)
    {
        Bounds bounds = PointBounds([line.Start, line.End],
            LineEndingPadding(line.LineWidth, line.StartEnding, line.EndEnding));
        var entries = CommonEntries("Line", bounds.X, bounds.Y, bounds.Width, bounds.Height,
            page, annotation, appearance, line.Color, line.Opacity,
            line.Contents, line.Metadata);
        entries.Add(("L", new PdfArray([
            Number(line.Start.X), Number(line.Start.Y), Number(line.End.X), Number(line.End.Y)])));
        entries.Add(("LE", new PdfArray([
            Name(PdfLineEndingStyleNames.Name(line.StartEnding)),
            Name(PdfLineEndingStyleNames.Name(line.EndEnding))])));
        entries.Add(("BS", BorderStyle(line.LineWidth, line.DashPattern)));
        if (line.InteriorColor.HasValue)
            entries.Add(("IC", ColorArray(line.InteriorColor.Value)));
        if (line.Intent.HasValue)
            entries.Add(("IT", Name(PdfAnnotationIntentNames.Name(line.Intent.Value))));
        if (line.Measurement is not null)
            entries.Add(("Measure", MeasurementDictionary(line.Measurement, false)));
        return Dictionary([.. entries]);
    }

    private static PdfDictionary MeasurementDictionary(
        PdfMeasurementProfile profile, bool includeArea)
    {
        long denominator = 1;
        for (int index = 0; index < profile.Precision; index++) denominator *= 10;
        PdfDictionary format = Dictionary(
            ("Type", Name("NumberFormat")),
            ("U", UnicodeString(profile.UnitSymbol)),
            ("C", Number(profile.UnitsPerPoint)),
            ("F", Name("D")),
            ("D", new PdfInteger(denominator)),
            ("FD", new PdfBoolean(true)));
        PdfArray formats = new([format]);
        var entries = new List<(string, PdfObject)>
        {
            ("Type", Name("Measure")),
            ("Subtype", Name("RL")),
            ("R", UnicodeString($"1 pt = {Format(profile.UnitsPerPoint)} {profile.UnitSymbol}")),
            ("X", formats),
            ("Y", formats),
            ("D", formats)
        };
        if (includeArea)
            entries.Add(("A", new PdfArray([Dictionary(
                ("Type", Name("NumberFormat")),
                ("U", UnicodeString(profile.UnitSymbol + "^2")),
                ("C", Number(profile.UnitsPerPoint * profile.UnitsPerPoint)),
                ("F", Name("D")),
                ("D", new PdfInteger(denominator)),
                ("FD", new PdfBoolean(true)))])));
        return Dictionary([.. entries]);
    }

    private static PdfStream LineAppearance(PendingLine line)
    {
        Bounds bounds = PointBounds([line.Start, line.End],
            LineEndingPadding(line.LineWidth, line.StartEnding, line.EndEnding));
        using var output = new MemoryStream();
        WriteAscii(output,
            $"q\n/GS1 gs\n{ColorOperands(line.Color)} RG\n{Format(line.LineWidth)} w\n" +
            DashOperator(line.DashPattern) +
            $"{Format(line.Start.X - bounds.X)} {Format(line.Start.Y - bounds.Y)} m\n" +
            $"{Format(line.End.X - bounds.X)} {Format(line.End.Y - bounds.Y)} l\nS\n");
        WriteLineEnding(output,
            line.Start.X - bounds.X, line.Start.Y - bounds.Y,
            line.End.X - bounds.X, line.End.Y - bounds.Y,
            line.StartEnding, line.LineWidth, line.Color, line.InteriorColor);
        WriteLineEnding(output,
            line.End.X - bounds.X, line.End.Y - bounds.Y,
            line.Start.X - bounds.X, line.Start.Y - bounds.Y,
            line.EndEnding, line.LineWidth, line.Color, line.InteriorColor);
        output.Write("Q\n"u8);
        return Appearance(bounds.Width, bounds.Height,
            OpacityResources(line.Opacity), output.ToArray());
    }

    private static PdfDictionary ShapeDictionary(
        PendingShape shape, PdfIndirectReference page, PdfIndirectReference annotation,
        PdfIndirectReference appearance)
    {
        string subtype = shape.Type.ToString();
        var entries = CommonEntries(subtype, shape.X, shape.Y, shape.Width, shape.Height,
            page, annotation, appearance, shape.StrokeColor, shape.Opacity,
            shape.Contents, shape.Metadata);
        entries.Add(("BS", BorderStyle(shape.LineWidth, shape.DashPattern)));
        if (shape.FillColor.HasValue) entries.Add(("IC", ColorArray(shape.FillColor.Value)));
        return Dictionary([.. entries]);
    }

    private static PdfStream ShapeAppearance(PendingShape shape)
    {
        using var output = new MemoryStream();
        WriteAscii(output, "q\n/GS1 gs\n");
        WriteAscii(output, DashOperator(shape.DashPattern));
        WriteBox(output, shape.Width, shape.Height, shape.LineWidth,
            shape.StrokeColor, shape.FillColor, shape.Type == PendingShapeType.Circle);
        output.Write("Q\n"u8);
        return Appearance(shape.Width, shape.Height, OpacityResources(shape.Opacity), output.ToArray());
    }

    private static PdfDictionary VertexDictionary(
        PendingVertex vertex, PdfIndirectReference page,
        PdfIndirectReference annotation, PdfIndirectReference appearance)
    {
        double padding = vertex.Closed ? vertex.LineWidth / 2
            : LineEndingPadding(vertex.LineWidth,
                vertex.StartEnding, vertex.EndEnding);
        Bounds bounds = PointBounds(vertex.Vertices, padding);
        string subtype = vertex.Closed ? "Polygon" : "PolyLine";
        var entries = CommonEntries(subtype, bounds.X, bounds.Y,
            bounds.Width, bounds.Height, page, annotation, appearance,
            vertex.Color, vertex.Opacity, vertex.Contents, vertex.Metadata);
        entries.Add(("Vertices", new PdfArray(vertex.Vertices.SelectMany(point =>
            new PdfObject[] { Number(point.X), Number(point.Y) }))));
        entries.Add(("BS", BorderStyle(vertex.LineWidth, vertex.DashPattern)));
        if (vertex.Intent is not null)
            entries.Add(("IT", Name(PdfAnnotationIntentNames.Name(
                vertex.Intent.Value, vertex.Closed))));
        if (vertex.Measurement is not null)
            entries.Add(("Measure", MeasurementDictionary(
                vertex.Measurement, vertex.Closed)));
        if (vertex.Closed && vertex.FillColor.HasValue)
            entries.Add(("IC", ColorArray(vertex.FillColor.Value)));
        if (!vertex.Closed)
        {
            entries.Add(("LE", new PdfArray([
                Name(PdfLineEndingStyleNames.Name(vertex.StartEnding)),
                Name(PdfLineEndingStyleNames.Name(vertex.EndEnding))])));
            if (vertex.InteriorColor.HasValue)
                entries.Add(("IC", ColorArray(vertex.InteriorColor.Value)));
        }
        return Dictionary([.. entries]);
    }

    private static PdfStream VertexAppearance(PendingVertex vertex)
    {
        double padding = vertex.Closed ? vertex.LineWidth / 2
            : LineEndingPadding(vertex.LineWidth,
                vertex.StartEnding, vertex.EndEnding);
        Bounds bounds = PointBounds(vertex.Vertices, padding);
        using var output = new MemoryStream();
        WriteAscii(output, "q\n/GS1 gs\n");
        if (vertex.FillColor.HasValue)
            WriteAscii(output, $"{ColorOperands(vertex.FillColor.Value)} rg\n");
        WriteAscii(output,
            $"{ColorOperands(vertex.Color)} RG\n{Format(vertex.LineWidth)} w\n" +
            DashOperator(vertex.DashPattern) +
            $"{Format(vertex.Vertices[0].X - bounds.X)} " +
            $"{Format(vertex.Vertices[0].Y - bounds.Y)} m\n");
        foreach (PdfPoint point in vertex.Vertices.Skip(1))
            WriteAscii(output,
                $"{Format(point.X - bounds.X)} {Format(point.Y - bounds.Y)} l\n");
        if (vertex.Closed) output.Write("h\n"u8);
        output.Write(vertex.FillColor.HasValue ? "B\n"u8 : "S\n"u8);
        if (!vertex.Closed)
        {
            WriteLineEnding(output,
                vertex.Vertices[0].X - bounds.X,
                vertex.Vertices[0].Y - bounds.Y,
                vertex.Vertices[1].X - bounds.X,
                vertex.Vertices[1].Y - bounds.Y,
                vertex.StartEnding, vertex.LineWidth,
                vertex.Color, vertex.InteriorColor);
            int last = vertex.Vertices.Count - 1;
            WriteLineEnding(output,
                vertex.Vertices[last].X - bounds.X,
                vertex.Vertices[last].Y - bounds.Y,
                vertex.Vertices[last - 1].X - bounds.X,
                vertex.Vertices[last - 1].Y - bounds.Y,
                vertex.EndEnding, vertex.LineWidth,
                vertex.Color, vertex.InteriorColor);
        }
        output.Write("Q\n"u8);
        return Appearance(bounds.Width, bounds.Height,
            OpacityResources(vertex.Opacity), output.ToArray());
    }

    private static PdfDictionary InkDictionary(
        PendingInk ink, PdfIndirectReference page, PdfIndirectReference annotation,
        PdfIndirectReference appearance)
    {
        Bounds bounds = PointBounds(ink.Strokes.SelectMany(stroke => stroke), ink.LineWidth / 2);
        var entries = CommonEntries("Ink", bounds.X, bounds.Y, bounds.Width, bounds.Height,
            page, annotation, appearance, ink.Color, ink.Opacity,
            ink.Contents, ink.Metadata);
        entries.Add(("InkList", new PdfArray(ink.Strokes.Select(stroke =>
            (PdfObject)new PdfArray(stroke.SelectMany(point => new PdfObject[]
                { Number(point.X), Number(point.Y) }))))));
        entries.Add(("BS", BorderStyle(ink.LineWidth, ink.DashPattern)));
        return Dictionary([.. entries]);
    }

    private static PdfStream InkAppearance(PendingInk ink)
    {
        Bounds bounds = PointBounds(ink.Strokes.SelectMany(stroke => stroke), ink.LineWidth / 2);
        using var output = new MemoryStream();
        WriteAscii(output,
            $"q\n/GS1 gs\n{ColorOperands(ink.Color)} RG\n{Format(ink.LineWidth)} w\n1 J\n1 j\n");
        WriteAscii(output, DashOperator(ink.DashPattern));
        foreach (IReadOnlyList<PdfPoint> stroke in ink.Strokes)
        {
            WriteAscii(output,
                $"{Format(stroke[0].X - bounds.X)} {Format(stroke[0].Y - bounds.Y)} m\n");
            foreach (PdfPoint point in stroke.Skip(1))
                WriteAscii(output, $"{Format(point.X - bounds.X)} {Format(point.Y - bounds.Y)} l\n");
            output.Write("S\n"u8);
        }
        output.Write("Q\n"u8);
        return Appearance(bounds.Width, bounds.Height, OpacityResources(ink.Opacity), output.ToArray());
    }

    private static PdfDictionary ImageStampDictionary(
        PendingImageStamp stamp, PdfIndirectReference page, PdfIndirectReference annotation,
        PdfIndirectReference appearance)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")), ("Subtype", Name("Stamp")),
            ("Rect", Rectangle(stamp.X, stamp.Y, stamp.Width, stamp.Height)),
            ("P", page), ("F", new PdfInteger((int)(stamp.Metadata?.Flags
                ?? PdfAnnotationFlags.Print))),
            ("NM", Latin1String($"KillerPDF-Image-{annotation.ObjectNumber}")),
            ("Name", Name(PdfStampIconNames.Name(stamp.Icon))),
            ("AP", Dictionary(("N", appearance)))
        };
        if (!string.IsNullOrEmpty(stamp.Contents))
            entries.Add(("Contents", UnicodeString(stamp.Contents)));
        PdfLinkAnnotationFactory.AddMetadata(entries, stamp.Metadata);
        return Dictionary([.. entries]);
    }

    private static PdfDictionary CaretDictionary(
        PendingCaret caret, PdfIndirectReference page,
        PdfIndirectReference annotation, PdfIndirectReference appearance)
    {
        var entries = CommonEntries("Caret", caret.X, caret.Y,
            caret.Width, caret.Height, page, annotation, appearance,
            caret.Color, caret.Opacity, caret.Contents, caret.Metadata);
        if (caret.Symbol == PdfCaretSymbol.Paragraph)
            entries.Add(("Sy", Name("P")));
        return Dictionary([.. entries]);
    }

    private static PdfStream CaretAppearance(PendingCaret caret)
    {
        double center = caret.Width / 2;
        double inset = Math.Max(1, Math.Min(caret.Width, caret.Height) * 0.12);
        byte[] content = Encoding.ASCII.GetBytes(
            $"q\n/GS1 gs\n{ColorOperands(caret.Color)} RG\n" +
            $"{Format(Math.Max(1, Math.Min(caret.Width, caret.Height) * 0.1))} w\n" +
            $"{Format(inset)} {Format(caret.Height - inset)} m\n" +
            $"{Format(center)} {Format(inset)} l\n" +
            $"{Format(caret.Width - inset)} {Format(caret.Height - inset)} l\nS\nQ\n");
        return Appearance(caret.Width, caret.Height,
            OpacityResources(caret.Opacity), content);
    }

    private static PdfDictionary RedactionDictionary(
        PendingRedaction value, PdfIndirectReference page,
        PdfIndirectReference annotation, PdfIndirectReference appearance,
        PdfName? fontResource)
    {
        var entries = CommonEntries("Redact", value.X, value.Y,
            value.Width, value.Height, page, annotation, appearance,
            value.MarkColor, value.Opacity, value.Contents, value.Metadata);
        entries.Add(("QuadPoints", new PdfArray(value.Quads.SelectMany(quad =>
            new PdfObject[]
            {
                Number(quad.UpperLeft.X), Number(quad.UpperLeft.Y),
                Number(quad.UpperRight.X), Number(quad.UpperRight.Y),
                Number(quad.LowerLeft.X), Number(quad.LowerLeft.Y),
                Number(quad.LowerRight.X), Number(quad.LowerRight.Y)
            }))));
        entries.Add(("IC", ColorArray(value.FillColor)));
        if (value.OverlayText is not null)
        {
            entries.Add(("OverlayText", UnicodeString(value.OverlayText)));
            entries.Add(("Repeat", new PdfBoolean(value.RepeatOverlayText)));
            entries.Add(("Q", new PdfInteger((int)value.OverlayAlignment)));
            entries.Add(("DA", Latin1String(
                $"{NameToken(fontResource!)} {Format(value.OverlayFontSize)} Tf 1 1 1 rg")));
        }
        return Dictionary([.. entries]);
    }

    private static PdfStream RedactionAppearance(
        PendingRedaction value, PdfName? fontResource,
        PdfIndirectReference? fontReference, EmbeddedFontUsage? fontUsage)
    {
        PdfDictionary resources = fontResource is null
            ? OpacityResources(value.Opacity)
            : OpacityResources(value.Opacity, (fontResource, fontReference!));
        using var output = new MemoryStream();
        WriteAscii(output,
            $"q\n/GS1 gs\n{ColorOperands(value.MarkColor)} RG\n1 w\n");
        foreach (PdfTextQuad source in value.Quads)
        {
            PdfPoint ul = new(source.UpperLeft.X - value.X,
                source.UpperLeft.Y - value.Y);
            PdfPoint ur = new(source.UpperRight.X - value.X,
                source.UpperRight.Y - value.Y);
            PdfPoint ll = new(source.LowerLeft.X - value.X,
                source.LowerLeft.Y - value.Y);
            PdfPoint lr = new(source.LowerRight.X - value.X,
                source.LowerRight.Y - value.Y);
            WriteAscii(output,
                $"{Format(ll.X)} {Format(ll.Y)} m\n" +
                $"{Format(lr.X)} {Format(lr.Y)} l\n" +
                $"{Format(ur.X)} {Format(ur.Y)} l\n" +
                $"{Format(ul.X)} {Format(ul.Y)} l\nh\n" +
                $"{Format(ll.X)} {Format(ll.Y)} m\n" +
                $"{Format(ur.X)} {Format(ur.Y)} l\n" +
                $"{Format(ul.X)} {Format(ul.Y)} m\n" +
                $"{Format(lr.X)} {Format(lr.Y)} l\nS\n");
        }
        if (value.OverlayText is not null)
        {
            IEnumerable<PdfTextQuad> textQuads = value.RepeatOverlayText
                ? value.Quads : value.Quads.Take(1);
            foreach (PdfTextQuad quad in textQuads)
            {
                double left = Math.Min(quad.LowerLeft.X, quad.UpperLeft.X) - value.X;
                double right = Math.Max(quad.LowerRight.X, quad.UpperRight.X) - value.X;
                double bottom = Math.Min(quad.LowerLeft.Y, quad.LowerRight.Y) - value.Y;
                double top = Math.Max(quad.UpperLeft.Y, quad.UpperRight.Y) - value.Y;
                double textWidth = value.OverlayFont is null
                    ? value.OverlayText.Length * value.OverlayFontSize * 0.5
                    : TextWidth(value.OverlayText, value.OverlayFont,
                        value.OverlayFontSize);
                double textX = value.OverlayAlignment switch
                {
                    PdfTextAlignment.Left => left + 2,
                    PdfTextAlignment.Center => left + Math.Max(0,
                        (right - left - textWidth) / 2),
                    PdfTextAlignment.Right => Math.Max(left,
                        right - textWidth - 2),
                    _ => throw new InvalidOperationException(
                        $"Unsupported overlay alignment: {value.OverlayAlignment}.")
                };
                double textY = bottom + Math.Max(1,
                    (top - bottom - value.OverlayFontSize) / 2);
                WriteAscii(output,
                    $"BT\n{NameToken(fontResource!)} {Format(value.OverlayFontSize)} Tf\n" +
                    $"1 1 1 rg\n1 0 0 1 {Format(textX)} {Format(textY)} Tm\n");
                if (value.OverlayFont is null)
                {
                    output.Write(PdfObjectWriter.Write(Latin1String(value.OverlayText)));
                    output.Write(" Tj\n"u8);
                }
                else
                    WriteGlyphText(output, value.OverlayText,
                        value.OverlayFont, fontUsage!);
                output.Write("ET\n"u8);
            }
        }
        output.Write("Q\n"u8);
        return Appearance(value.Width, value.Height, resources, output.ToArray());
    }

    private static PdfStream ImageStampAppearance(
        PendingImageStamp stamp, PdfIndirectReference imageReference)
    {
        PdfDictionary resources = Dictionary(("XObject", new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Im1"), imageReference)])));
        byte[] content = Encoding.ASCII.GetBytes(
            $"q\n{Format(stamp.Width)} 0 0 {Format(stamp.Height)} 0 0 cm\n/Im1 Do\nQ\n");
        return Appearance(stamp.Width, stamp.Height, resources, content);
    }

    private static PdfDictionary FileAttachmentDictionary(
        PendingFileAttachment value, PdfIndirectReference page,
        PdfIndirectReference annotation, PdfIndirectReference appearance)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")), ("Subtype", Name("FileAttachment")),
            ("Rect", Rectangle(value.X, value.Y, value.Size, value.Size)),
            ("P", page),
            ("F", new PdfInteger((int)(value.Metadata?.Flags
                ?? PdfAnnotationFlags.Print))),
            ("NM", Latin1String(
                $"KillerPDF-FileAttachment-{annotation.ObjectNumber}")),
            ("Name", Name(value.Icon.ToString())),
            ("C", ColorArray(value.Color)),
            ("FS", value.FileSpecification),
            ("AP", Dictionary(("N", appearance)))
        };
        if (!string.IsNullOrEmpty(value.Contents))
            entries.Add(("Contents", UnicodeString(value.Contents)));
        PdfLinkAnnotationFactory.AddMetadata(entries, value.Metadata);
        return Dictionary([.. entries]);
    }

    private static PdfStream FileAttachmentAppearance(
        PendingFileAttachment value)
    {
        double inset = value.Size * 0.15;
        double fold = value.Size * 0.28;
        using var output = new MemoryStream();
        WriteAscii(output,
            $"q\n{ColorOperands(value.Color)} rg\n0 G\n1 w\n" +
            $"{Format(inset)} {Format(inset)} m\n" +
            $"{Format(value.Size - inset - fold)} {Format(inset)} l\n" +
            $"{Format(value.Size - inset)} {Format(inset + fold)} l\n" +
            $"{Format(value.Size - inset)} {Format(value.Size - inset)} l\n" +
            $"{Format(inset)} {Format(value.Size - inset)} l\nh\nB\n" +
            $"{Format(value.Size - inset - fold)} {Format(inset)} m\n" +
            $"{Format(value.Size - inset - fold)} {Format(inset + fold)} l\n" +
            $"{Format(value.Size - inset)} {Format(inset + fold)} l\nS\nQ\n");
        return Appearance(value.Size, value.Size, Dictionary(), output.ToArray());
    }

    private static List<(string Name, PdfObject Value)> CommonEntries(
        string subtype, double x, double y, double width, double height,
        PdfIndirectReference page, PdfIndirectReference annotation, PdfIndirectReference appearance,
        PdfRgbColor color, double opacity, string? contents,
        PdfAnnotationMetadata? metadata)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")), ("Subtype", Name(subtype)),
            ("Rect", Rectangle(x, y, width, height)), ("P", page),
            ("F", new PdfInteger((int)(metadata?.Flags
                ?? PdfAnnotationFlags.Print))),
            ("NM", Latin1String($"KillerPDF-{subtype}-{annotation.ObjectNumber}")),
            ("C", ColorArray(color)), ("CA", Number(opacity)),
            ("AP", Dictionary(("N", appearance)))
        };
        if (!string.IsNullOrEmpty(contents)) entries.Add(("Contents", UnicodeString(contents)));
        PdfLinkAnnotationFactory.AddMetadata(entries, metadata);
        return entries;
    }

    private static PdfDictionary BorderStyle(
        double width, IReadOnlyList<double>? dashPattern = null)
    {
        if (dashPattern is null)
            return Dictionary(("W", Number(width)), ("S", Name("S")));
        return Dictionary(
            ("W", Number(width)),
            ("S", Name("D")),
            ("D", new PdfArray(dashPattern.Select(Number))));
    }

    private static string DashOperator(IReadOnlyList<double>? dashPattern) =>
        dashPattern is null
            ? string.Empty
            : $"[{string.Join(' ', dashPattern.Select(Format))}] 0 d\n";

    private static double LineEndingPadding(
        double lineWidth, PdfLineEndingStyle start, PdfLineEndingStyle end) =>
        start == PdfLineEndingStyle.None && end == PdfLineEndingStyle.None
            ? lineWidth / 2 : Math.Max(6, lineWidth * 4);

    private static void WriteLineEnding(
        Stream output, double x, double y, double neighborX, double neighborY,
        PdfLineEndingStyle style, double lineWidth, PdfRgbColor _,
        PdfRgbColor? interiorColor)
    {
        if (style == PdfLineEndingStyle.None) return;
        double dx = neighborX - x;
        double dy = neighborY - y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length == 0) return;
        dx /= length;
        dy /= length;
        double nx = -dy;
        double ny = dx;
        double size = Math.Max(6, lineWidth * 4);
        bool reverse = style is PdfLineEndingStyle.ReverseOpenArrow
            or PdfLineEndingStyle.ReverseClosedArrow;
        double direction = reverse ? -1 : 1;
        double backX = x + dx * size * direction;
        double backY = y + dy * size * direction;
        double wing = size * 0.45;
        double firstX = backX + nx * wing;
        double firstY = backY + ny * wing;
        double secondX = backX - nx * wing;
        double secondY = backY - ny * wing;
        switch (style)
        {
            case PdfLineEndingStyle.OpenArrow:
            case PdfLineEndingStyle.ReverseOpenArrow:
                WriteAscii(output,
                    $"{Format(firstX)} {Format(firstY)} m\n" +
                    $"{Format(x)} {Format(y)} l\n" +
                    $"{Format(secondX)} {Format(secondY)} l\nS\n");
                break;
            case PdfLineEndingStyle.ClosedArrow:
            case PdfLineEndingStyle.ReverseClosedArrow:
                WriteAscii(output,
                    (interiorColor.HasValue
                        ? $"{ColorOperands(interiorColor.Value)} rg\n" : string.Empty) +
                    $"{Format(x)} {Format(y)} m\n" +
                    $"{Format(firstX)} {Format(firstY)} l\n" +
                    $"{Format(secondX)} {Format(secondY)} l\nh\n" +
                    (interiorColor.HasValue ? "B\n" : "S\n"));
                break;
            case PdfLineEndingStyle.Square:
            {
                double half = size * 0.35;
                WriteAscii(output,
                    $"{Format(x - half)} {Format(y - half)} " +
                    $"{Format(half * 2)} {Format(half * 2)} re\n" +
                    (interiorColor.HasValue
                        ? $"{ColorOperands(interiorColor.Value)} rg\nB\n" : "S\n"));
                break;
            }
            case PdfLineEndingStyle.Circle:
            {
                double diameter = size * 0.7;
                WriteEllipse(output, x - diameter / 2, y - diameter / 2,
                    diameter, diameter);
                if (interiorColor.HasValue)
                {
                    WriteAscii(output, $"{ColorOperands(interiorColor.Value)} rg\n");
                    output.Write("B\n"u8);
                }
                else output.Write("S\n"u8);
                break;
            }
            case PdfLineEndingStyle.Diamond:
            {
                double half = size * 0.45;
                WriteAscii(output,
                    $"{Format(x)} {Format(y + half)} m\n" +
                    $"{Format(x + half)} {Format(y)} l\n" +
                    $"{Format(x)} {Format(y - half)} l\n" +
                    $"{Format(x - half)} {Format(y)} l\nh\n" +
                    (interiorColor.HasValue
                        ? $"{ColorOperands(interiorColor.Value)} rg\nB\n" : "S\n"));
                break;
            }
            case PdfLineEndingStyle.Butt:
            {
                double half = size * 0.45;
                WriteAscii(output,
                    $"{Format(x + nx * half)} {Format(y + ny * half)} m\n" +
                    $"{Format(x - nx * half)} {Format(y - ny * half)} l\nS\n");
                break;
            }
            case PdfLineEndingStyle.Slash:
            {
                double half = size * 0.5;
                double slashX = nx * 0.85 + dx * 0.5;
                double slashY = ny * 0.85 + dy * 0.5;
                WriteAscii(output,
                    $"{Format(x + slashX * half)} {Format(y + slashY * half)} m\n" +
                    $"{Format(x - slashX * half)} {Format(y - slashY * half)} l\nS\n");
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(style));
        }
    }

    private static PdfDictionary OpacityResources(
        double opacity, (PdfName Name, PdfObject Reference)? font = null)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("ExtGState", new PdfDictionary([
                new KeyValuePair<PdfName, PdfObject>(Name("GS1"), Dictionary(
                    ("Type", Name("ExtGState")), ("ca", Number(opacity)), ("CA", Number(opacity))))]))
        };
        if (font.HasValue)
            entries.Add(("Font", new PdfDictionary([
                new KeyValuePair<PdfName, PdfObject>(font.Value.Name, font.Value.Reference)])));
        return Dictionary([.. entries]);
    }

    private static void WriteBox(
        Stream output, double width, double height, double lineWidth,
        PdfRgbColor stroke, PdfRgbColor? fill, bool ellipse)
    {
        double inset = lineWidth / 2;
        if (fill.HasValue) WriteAscii(output, $"{ColorOperands(fill.Value)} rg\n");
        WriteAscii(output, $"{ColorOperands(stroke)} RG\n{Format(lineWidth)} w\n");
        if (ellipse)
            WriteEllipse(output, inset, inset, Math.Max(0, width - lineWidth), Math.Max(0, height - lineWidth));
        else
            WriteAscii(output,
                $"{Format(inset)} {Format(inset)} {Format(Math.Max(0, width - lineWidth))} {Format(Math.Max(0, height - lineWidth))} re\n");
        output.Write(fill.HasValue ? "B\n"u8 : "S\n"u8);
    }

    private static void WriteEllipse(Stream output, double x, double y, double width, double height)
    {
        const double kappa = 0.5522847498307936;
        double rx = width / 2, ry = height / 2, cx = x + rx, cy = y + ry;
        WriteAscii(output, $"{Format(cx + rx)} {Format(cy)} m\n");
        WriteAscii(output, $"{Format(cx + rx)} {Format(cy + ry * kappa)} {Format(cx + rx * kappa)} {Format(cy + ry)} {Format(cx)} {Format(cy + ry)} c\n");
        WriteAscii(output, $"{Format(cx - rx * kappa)} {Format(cy + ry)} {Format(cx - rx)} {Format(cy + ry * kappa)} {Format(cx - rx)} {Format(cy)} c\n");
        WriteAscii(output, $"{Format(cx - rx)} {Format(cy - ry * kappa)} {Format(cx - rx * kappa)} {Format(cy - ry)} {Format(cx)} {Format(cy - ry)} c\n");
        WriteAscii(output, $"{Format(cx + rx * kappa)} {Format(cy - ry)} {Format(cx + rx)} {Format(cy - ry * kappa)} {Format(cx + rx)} {Format(cy)} c\nh\n");
    }

    private static void WriteFreeText(
        Stream output, PendingFreeText value, PdfName fontResource, EmbeddedFontUsage usage)
    {
        double padding = Math.Max(3, value.BorderWidth + 2);
        double lineHeight = value.FontSize * 1.2;
        List<string> lines = WrapText(value.Contents, value.Font, value.FontSize,
            Math.Max(1, value.Width - padding * 2));
        WriteAscii(output,
            $"BT\n{NameToken(fontResource)} {Format(value.FontSize)} Tf\n{ColorOperands(value.TextColor)} rg\n" +
            $"{Format(padding)} {Format(Math.Max(padding, value.Height - padding - value.FontSize))} Td\n");
        for (int index = 0; index < lines.Count; index++)
        {
            double lineWidth = TextWidth(lines[index], value.Font, value.FontSize);
            double textX = value.Alignment switch
            {
                PdfTextAlignment.Left => padding,
                PdfTextAlignment.Center => Math.Max(padding,
                    (value.Width - lineWidth) / 2),
                PdfTextAlignment.Right => Math.Max(padding,
                    value.Width - padding - lineWidth),
                _ => throw new InvalidOperationException(
                    $"Unsupported free-text alignment: {value.Alignment}.")
            };
            if (index == 0)
                WriteAscii(output,
                    $"1 0 0 1 {Format(textX)} " +
                    $"{Format(Math.Max(padding, value.Height - padding - value.FontSize))} Tm\n");
            else
                WriteAscii(output,
                    $"1 0 0 1 {Format(textX)} " +
                    $"{Format(Math.Max(padding, value.Height - padding - value.FontSize - index * lineHeight))} Tm\n");
            WriteGlyphText(output, lines[index], value.Font, usage);
            if ((index + 2) * lineHeight > value.Height - padding) break;
        }
        output.Write("ET\n"u8);
    }

    private static List<string> WrapText(
        string text, TrueTypeFont font, double fontSize, double maximumWidth)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(string.Empty); continue; }
            var current = new StringBuilder();
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = current.Length == 0 ? word : $"{current} {word}";
                if (current.Length > 0 && TextWidth(candidate, font, fontSize) > maximumWidth)
                {
                    lines.Add(current.ToString()); current.Clear(); current.Append(word);
                }
                else
                {
                    if (current.Length > 0) current.Append(' ');
                    current.Append(word);
                }
            }
            lines.Add(current.ToString());
        }
        return lines;
    }

    private static double TextWidth(string value, TrueTypeFont font, double fontSize) =>
        font.MapText(value).Sum(mapping => font.GetPdfAdvanceWidth(mapping.Glyph))
            * fontSize / 1000;

    private static void WriteGlyphText(
        Stream output, string value, TrueTypeFont font, EmbeddedFontUsage usage)
    {
        output.WriteByte((byte)'<');
        foreach (FontGlyphMapping mapping in font.MapText(value))
            WriteAscii(output, usage.AddMapping(mapping.Glyph, mapping.UnicodeSequence)
                .ToString("X4", CultureInfo.InvariantCulture));
        output.Write("> Tj\n"u8);
    }

    private static Bounds PointBounds(IEnumerable<PdfPoint> points, double padding)
    {
        PdfPoint[] values = [.. points];
        double minX = values.Min(point => point.X) - padding;
        double minY = values.Min(point => point.Y) - padding;
        double maxX = values.Max(point => point.X) + padding;
        double maxY = values.Max(point => point.Y) + padding;
        return new Bounds(minX, minY, maxX - minX, maxY - minY);
    }

    private static PdfStream Appearance(
        double width, double height, PdfDictionary resources, byte[] content) =>
        new(Dictionary(
            ("Type", Name("XObject")), ("Subtype", Name("Form")),
            ("FormType", new PdfInteger(1)),
            ("BBox", new PdfArray([new PdfInteger(0), new PdfInteger(0), Number(width), Number(height)])),
            ("Resources", resources)), content);

    private void ValidatePage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
    }

    private bool HasNamedDestination(string destinationName)
    {
        if (_tree.Catalog.TryGetValue(NamesName, out PdfObject? namesValue))
        {
            PdfDictionary names = ResolveDictionary(namesValue,
                "The catalog /Names value");
            if (names.TryGetValue(DestsName, out PdfObject? destinations)
                && PdfNameTree.Read(_document, destinations).Any(entry =>
                    string.Equals(PdfUnicodeEncoding.DecodeTextString(
                            entry.Key.Bytes.Span, "A named-destination key"),
                        destinationName, StringComparison.Ordinal)))
                return true;
        }
        if (_tree.Catalog.TryGetValue(DestsName, out PdfObject? legacyValue))
        {
            PdfDictionary legacy = ResolveDictionary(legacyValue,
                "The catalog /Dests value");
            return legacy.Keys.Any(key => string.Equals(
                key.ValueAsLatin1(), destinationName,
                StringComparison.Ordinal));
        }
        return false;
    }

    private PdfIndirectReference FindEmbeddedFileSpecification(string fileName)
    {
        if (!_tree.Catalog.TryGetValue(NamesName, out PdfObject? namesValue))
            throw new ArgumentException(
                $"The attachment '{fileName}' has not been embedded.", nameof(fileName));
        PdfDictionary names = ResolveDictionary(namesValue,
            "The catalog /Names value");
        if (!names.TryGetValue(EmbeddedFilesName, out PdfObject? filesValue))
            throw new ArgumentException(
                $"The attachment '{fileName}' has not been embedded.", nameof(fileName));

        PdfIndirectReference? match = null;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PdfNameTreeEntry entry in PdfNameTree.Read(_document, filesValue))
        {
            string key = PdfUnicodeEncoding.DecodeTextString(
                entry.Key.Bytes.Span, "An embedded-file name");
            if (!keys.Add(key))
                throw new InvalidOperationException(
                    "The embedded-files name tree contains duplicate file names.");
            ResolvedValue resolved = ResolveWithIdentity(entry.Value,
                $"The embedded-file specification for '{key}'");
            PdfDictionary specification = resolved.Value as PdfDictionary
                ?? throw new InvalidOperationException(
                    $"The embedded-file specification for '{key}' is not a dictionary.");
            if (resolved.FinalReference is not PdfIndirectReference reference)
                throw new InvalidOperationException(
                    $"The embedded-file specification for '{key}' is not indirect.");
            if (specification.TryGetValue(Name("Type"), out PdfObject? typeValue)
                && (ResolveValue(typeValue,
                        $"The embedded-file specification for '{key}' /Type value")
                    is not PdfName type || type.ValueAsLatin1() != "Filespec"))
                throw new InvalidOperationException(
                    $"The embedded-file specification for '{key}' has an invalid /Type value.");
            if (string.Equals(key, fileName, StringComparison.OrdinalIgnoreCase))
                match = reference;
        }
        return match ?? throw new ArgumentException(
            $"The attachment '{fileName}' has not been embedded.", nameof(fileName));
    }

    private static void ValidateCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(parameterName);
    }

    private void ApplyRequiredVersionUpgrade(PdfIncrementalUpdateBuilder update)
    {
        if (RequiredVersionOverride() is not PdfVersion required) return;
        var entries = _tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[VersionName] = Name(required.ToString());
        update.ReplaceObject(_tree.CatalogReference.ObjectNumber,
            new PdfDictionary(entries));
    }

    private PdfVersion? RequiredVersionOverride()
    {
        PdfVersion required = _annotations.Any(annotation =>
            annotation is PendingRedaction)
                ? PdfVersion.Pdf17
                : _annotations.Any(annotation =>
            annotation is PendingVertex or PendingCaret)
                ? new PdfVersion(1, 5)
                : _annotations.Any(annotation => annotation is
                    PendingTextMarkup or PendingFreeText or PendingLine
                    or PendingShape or PendingInk)
                    ? new PdfVersion(1, 4)
                    : _annotations.OfType<PendingTextNote>().Any(note =>
                        note.Popup is not null)
                        ? new PdfVersion(1, 3)
                    : _annotations.Any(annotation => annotation is
                        PendingImageStamp or PendingFileAttachment)
                        ? new PdfVersion(1, 3)
                        : _annotations.OfType<PendingLink>().Any(link =>
                            link.Target == PendingLinkTarget.Uri)
                            ? new PdfVersion(1, 1)
                            : new PdfVersion(1, 0);
        PdfVersion effective = EffectiveVersion();
        if (effective.CompareTo(required) >= 0) return null;
        if (required.CompareTo(new PdfVersion(1, 4)) < 0)
            required = new PdfVersion(1, 4);
        return required;
    }

    private PdfVersion EffectiveVersion()
    {
        PdfVersion version = _document.Header.Version;
        if (!_tree.Catalog.TryGetValue(VersionName, out PdfObject? value))
            return version;
        PdfObject resolved = ResolveValue(value, "The catalog /Version value");
        if (resolved is not PdfName name)
            throw new InvalidOperationException(
                "The catalog /Version value is not a name.");
        string text = name.ValueAsLatin1();
        if (text.Length != 3 || text[1] != '.'
            || !char.IsAsciiDigit(text[0]) || !char.IsAsciiDigit(text[2])
            || !PdfVersion.IsDefined(text[0] - '0', text[2] - '0'))
            throw new InvalidOperationException(
                "The catalog /Version value is not a defined PDF version.");
        var declared = new PdfVersion(text[0] - '0', text[2] - '0');
        return declared.CompareTo(version) > 0 ? declared : version;
    }

    private static void ValidateRectangle(double x, double y, double width, double height)
    {
        ValidateCoordinate(x, nameof(x)); ValidateCoordinate(y, nameof(y));
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
    }

    private static void ValidateStroke(double lineWidth, double opacity)
    {
        if (!double.IsFinite(lineWidth) || lineWidth <= 0) throw new ArgumentOutOfRangeException(nameof(lineWidth));
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
    }

    private static double[]? ValidateDashPattern(IReadOnlyList<double>? pattern)
    {
        if (pattern is null) return null;
        if (pattern.Count == 0
            || pattern.Any(value => !double.IsFinite(value) || value < 0))
            throw new ArgumentOutOfRangeException(nameof(pattern));
        if (pattern.All(value => value == 0))
            throw new ArgumentException(
                "A dash pattern cannot contain only zeros.", nameof(pattern));
        return [.. pattern];
    }

    private static void ValidateDrawableText(TrueTypeFont font, string value, string parameterName)
    {
        if (!font.EmbeddingAllowed)
            throw new ArgumentException($"Font {font.PostScriptName} prohibits PDF embedding.", parameterName);
        foreach (FontGlyphMapping mapping in font.MapText(value))
        {
            if (mapping.UnicodeSequence is "\r" or "\n") continue;
            if (mapping.Glyph == 0 && mapping.UnicodeSequence != "\0")
                throw new ArgumentException(
                    $"Font {font.PostScriptName} has no glyph for {FormatUnicodeSequence(mapping.UnicodeSequence)}.", parameterName);
        }
    }

    private static string FormatUnicodeSequence(string value) =>
        string.Join(" ", value.EnumerateRunes().Select(rune => $"U+{rune.Value:X4}"));

    private static PdfArray Rectangle(double x, double y, double width, double height) =>
        new([Number(x), Number(y), Number(x + width), Number(y + height)]);
    private static PdfArray ColorArray(PdfRgbColor color) =>
        new([Number(color.Red), Number(color.Green), Number(color.Blue)]);
    private static string ColorOperands(PdfRgbColor color) =>
        $"{Format(color.Red)} {Format(color.Green)} {Format(color.Blue)}";
    private static PdfObject Number(double value) => value == Math.Truncate(value)
        ? new PdfInteger(checked((long)value)) : new PdfReal(value);
    private static string Format(double value) =>
        Encoding.ASCII.GetString(PdfObjectWriter.Write(Number(value)));
    private static string NameToken(PdfName value) =>
        Encoding.ASCII.GetString(PdfObjectWriter.Write(value));
    private static PdfString Latin1String(string value) =>
        new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);
    private static PdfString UnicodeString(string value) =>
        new([0xFE, 0xFF, .. PdfUnicodeEncoding.EncodeBigEndian(value)],
            PdfStringForm.Hexadecimal);
    private static PdfDictionary Dictionary(params (string Name, PdfObject Value)[] entries) =>
        new(entries.Select(entry => new KeyValuePair<PdfName, PdfObject>(Name(entry.Name), entry.Value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static void WriteAscii(Stream output, string value)
    {
        foreach (char character in value) output.WriteByte(checked((byte)character));
    }

    private abstract record PendingAnnotation(int PageIndex);
    private sealed record PendingTextNote(
        int PageIndex, double X, double Y, double Size, string Contents,
        PdfRgbColor Color, bool Open, PdfAnnotationMetadata? Metadata,
        PdfTextNoteIcon Icon, PdfTextNoteState? State, string? Name,
        string? InReplyTo, PdfAnnotationReplyType ReplyType,
        PdfAnnotationPopup? Popup)
        : PendingAnnotation(PageIndex);
    private sealed record PendingTextMarkup(
        PdfTextMarkupType Type, int PageIndex, double X, double Y, double Width, double Height,
        IReadOnlyList<PdfTextQuad> Quads, string? Contents,
        PdfRgbColor Color, double Opacity,
        PdfAnnotationMetadata? Metadata) : PendingAnnotation(PageIndex);
    private sealed record PendingFreeText(
        int PageIndex, double X, double Y, double Width, double Height, string Contents,
        TrueTypeFont Font, double FontSize, PdfRgbColor TextColor, PdfRgbColor? FillColor,
        PdfRgbColor BorderColor, double BorderWidth, double Opacity,
        PdfAnnotationMetadata? Metadata, PdfTextAlignment Alignment,
        IReadOnlyList<double>? DashPattern, PdfFreeTextIntent Intent,
        IReadOnlyList<PdfPoint>? CalloutLine,
        PdfLineEndingStyle CalloutEnding) : PendingAnnotation(PageIndex);
    private sealed record PendingLine(
        int PageIndex, PdfPoint Start, PdfPoint End, PdfRgbColor Color,
        double LineWidth, double Opacity, string? Contents,
        PdfAnnotationMetadata? Metadata,
        IReadOnlyList<double>? DashPattern,
        PdfLineEndingStyle StartEnding, PdfLineEndingStyle EndEnding,
        PdfRgbColor? InteriorColor, PdfLineAnnotationIntent? Intent,
        PdfMeasurementProfile? Measurement)
        : PendingAnnotation(PageIndex);
    private sealed record PendingShape(
        PendingShapeType Type, int PageIndex, double X, double Y, double Width, double Height,
        PdfRgbColor StrokeColor, PdfRgbColor? FillColor, double LineWidth, double Opacity,
        string? Contents, PdfAnnotationMetadata? Metadata,
        IReadOnlyList<double>? DashPattern) : PendingAnnotation(PageIndex);
    private sealed record PendingVertex(
        int PageIndex, IReadOnlyList<PdfPoint> Vertices, bool Closed,
        PdfRgbColor Color, PdfRgbColor? FillColor, double LineWidth,
        double Opacity, string? Contents, PdfLineEndingStyle StartEnding,
        PdfLineEndingStyle EndEnding, IReadOnlyList<double>? DashPattern,
        PdfRgbColor? InteriorColor, PdfAnnotationMetadata? Metadata,
        PdfVertexAnnotationIntent? Intent, PdfMeasurementProfile? Measurement)
        : PendingAnnotation(PageIndex);
    private sealed record PendingInk(
        int PageIndex, IReadOnlyList<IReadOnlyList<PdfPoint>> Strokes, PdfRgbColor Color,
        double LineWidth, double Opacity, string? Contents,
        PdfAnnotationMetadata? Metadata,
        IReadOnlyList<double>? DashPattern) : PendingAnnotation(PageIndex);
    private sealed record PendingImageStamp(
        int PageIndex, double X, double Y, double Width, double Height,
        PdfImage Image, string? Contents, PdfAnnotationMetadata? Metadata,
        PdfStampIcon Icon)
        : PendingAnnotation(PageIndex);
    private sealed record PendingCaret(
        int PageIndex, double X, double Y, double Width, double Height,
        string? Contents, PdfRgbColor Color, double Opacity,
        PdfCaretSymbol Symbol, PdfAnnotationMetadata? Metadata)
        : PendingAnnotation(PageIndex);
    private sealed record PendingRedaction(
        int PageIndex, double X, double Y, double Width, double Height,
        IReadOnlyList<PdfTextQuad> Quads, string? Contents,
        PdfRgbColor FillColor, PdfRgbColor MarkColor, double Opacity,
        PdfAnnotationMetadata? Metadata, string? OverlayText,
        bool RepeatOverlayText, PdfTextAlignment OverlayAlignment,
        double OverlayFontSize, TrueTypeFont? OverlayFont)
        : PendingAnnotation(PageIndex);
    private sealed record PendingFileAttachment(
        int PageIndex, double X, double Y, double Size, string FileName,
        string? Contents, PdfFileAttachmentIcon Icon, PdfRgbColor Color,
        PdfAnnotationMetadata? Metadata,
        PdfIndirectReference FileSpecification) : PendingAnnotation(PageIndex);
    private sealed record PendingLink(
        int PageIndex, double X, double Y, double Width, double Height,
        PdfLinkAppearance Appearance, PendingLinkTarget Target, string? Name,
        (int PageIndex, PdfDestination Destination)? PageTarget,
        IReadOnlyList<PdfTextQuad>? Quads, PdfAnnotationMetadata? Metadata,
        string? Contents) : PendingAnnotation(PageIndex);
    private sealed record AllocatedAnnotation(
        PendingAnnotation Definition, PdfIndirectReference AnnotationReference,
        PdfIndirectReference? AppearanceReference,
        PdfIndirectReference? PopupReference);
    private sealed record PendingRemoval(
        int PageIndex, string Name, PdfIndirectReference Reference,
        PdfDictionary Dictionary, PdfIndirectReference? PopupReference);
    private sealed record PendingAnnotationUpdate(
        int PageIndex, string Name, PdfIndirectReference Reference,
        PdfDictionary Dictionary, bool UpdateContents, string? Contents,
        bool UpdateMetadata, PdfAnnotationMetadata? Metadata,
        bool StripLinkAppearance, bool UpdateLinkTarget = false,
        PendingLinkTarget? LinkTarget = null, string? LinkName = null,
        (int PageIndex, PdfDestination Destination)? PageTarget = null,
        bool UpdateFileAttachmentIcon = false,
        PdfFileAttachmentIcon FileAttachmentIcon = PdfFileAttachmentIcon.Paperclip);
    private sealed record EditorFontBinding(
        PdfName Resource, PdfIndirectReference Type0Reference, EmbeddedFontUsage Usage);
    private sealed record ResolvedValue(
        PdfObject Value, PdfIndirectReference? FinalReference);
    private sealed record Bounds(double X, double Y, double Width, double Height);
    private enum PendingShapeType { Square, Circle }
    private enum PendingLinkTarget { Uri, Page, Named }
}
