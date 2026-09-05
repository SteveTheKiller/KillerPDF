using System.Globalization;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Fonts;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;

namespace KillerPdf.Engine.Authoring;

/// <summary>Creates a new PDF catalog and page tree without relying on the legacy writer.</summary>
public sealed partial class PdfDocumentBuilder
{
    private static readonly PdfName RootName = Name("Root");
    private static readonly PdfName SizeName = Name("Size");
    private readonly List<PageDefinition> _pages = [];
    private readonly List<BookmarkDefinition> _bookmarks = [];
    private readonly List<NamedDestinationDefinition> _namedDestinations = [];
    private readonly List<PageLabelDefinition> _pageLabels = [];
    private readonly List<StructureElementDefinition> _structureElements = [];
    private readonly List<AttachmentDefinition> _attachments = [];
    private readonly List<FileAttachmentAnnotationDefinition> _fileAttachmentAnnotations = [];
    private readonly List<TextFieldDefinition> _textFields = [];
    private readonly List<CheckBoxDefinition> _checkBoxes = [];
    private readonly List<RadioGroupDefinition> _radioGroups = [];
    private readonly List<ChoiceFieldDefinition> _choiceFields = [];
    private readonly List<PushButtonDefinition> _pushButtons = [];
    private readonly List<SignatureFieldDefinition> _signatureFields = [];
    private OutputIntentDefinition? _outputIntent;
    private PdfA4Flavor _pdfA4Flavor;
    private bool _pdfUa2Conformance;
    private PdfPasswordEncryptionOptions? _encryption;
    private PdfCertificateEncryptionOptions? _certificateEncryption;
    private PdfPageLayout? _pageLayout;
    private PdfPageMode? _pageMode;
    private PdfViewerPreferences? _viewerPreferences;
    private OpenActionDefinition? _openAction;
    private readonly List<TextNoteDefinition> _textNotes = [];
    private readonly List<TextMarkupDefinition> _textMarkups = [];

    /// <summary>Initializes a document using PDF 2.0 or the specified defined PDF version.</summary>
    public PdfDocumentBuilder(PdfVersion? version = null)
    {
        PdfVersion selected = version ?? PdfVersion.Pdf20;
        if (!PdfVersion.IsDefined(selected.Major, selected.Minor))
            throw new ArgumentOutOfRangeException(nameof(version),
                "The PDF document version is not defined.");
        Version = selected;
    }

    /// <summary>Gets the header version selected for the authored document.</summary>
    public PdfVersion Version { get; }
    /// <summary>Gets the number of pages currently defined by the builder.</summary>
    public int PageCount => _pages.Count;
    /// <summary>Gets the descriptive metadata that will be written to the document.</summary>
    public PdfDocumentMetadata? Metadata { get; private set; }

    /// <summary>Sets the document information, language, and descriptive XMP values.</summary>
    public PdfDocumentBuilder SetMetadata(PdfDocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Language is not null && !PdfLanguageTag.IsValid(metadata.Language))
            throw new ArgumentException(
                "The document language is not a valid BCP 47 language tag.",
                nameof(metadata));
        Metadata = metadata;
        return this;
    }

    /// <summary>Protects the authored document with Standard Security password encryption.</summary>
    public PdfDocumentBuilder SetPasswordEncryption(PdfPasswordEncryptionOptions options)
    {
        if (_certificateEncryption is not null)
            throw new InvalidOperationException(
                "Password and certificate encryption cannot both be configured.");
        ArgumentNullException.ThrowIfNull(options);
        if (options.Algorithm == PdfPasswordEncryptionAlgorithm.Aes256Gcm
            && Version.CompareTo(PdfVersion.Pdf20) < 0)
            throw new ArgumentException(
                "AES-GCM password encryption requires PDF 2.0.", nameof(options));
        _encryption = options;
        return this;
    }

    /// <summary>Protects the authored document for certificate recipients.</summary>
    public PdfDocumentBuilder SetCertificateEncryption(PdfCertificateEncryptionOptions options)
    {
        if (_encryption is not null)
            throw new InvalidOperationException(
                "Password and certificate encryption cannot both be configured.");
        _certificateEncryption = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>Sets the document output intent and its embedded ICC destination profile.</summary>
    public PdfDocumentBuilder SetOutputIntent(
        PdfIccProfile profile,
        string outputConditionIdentifier,
        string? outputCondition = null,
        string? registryName = null,
        string? information = null)
    {
        PdfOutputIntentFactory.Validate(
            profile, outputConditionIdentifier);
        _outputIntent = new OutputIntentDefinition(
            profile, outputConditionIdentifier, outputCondition, registryName, information);
        return this;
    }

    /// <summary>Enables general PDF/A-4 authoring and conformance checks.</summary>
    public PdfDocumentBuilder EnablePdfA4Conformance()
    {
        _pdfA4Flavor = PdfA4Flavor.General;
        return this;
    }

    /// <summary>Enables PDF/A-4f authoring, including associated embedded files.</summary>
    public PdfDocumentBuilder EnablePdfA4fConformance()
    {
        _pdfA4Flavor = PdfA4Flavor.EmbeddedFiles;
        return this;
    }

    /// <summary>Enables the PDF/A-4e engineering-document conformance flavour.</summary>
    public PdfDocumentBuilder EnablePdfA4eConformance()
    {
        _pdfA4Flavor = PdfA4Flavor.Engineering;
        return this;
    }

    /// <summary>Enables PDF/UA-2 identification and accessibility conformance checks.</summary>
    public PdfDocumentBuilder EnablePdfUa2Conformance()
    {
        _pdfUa2Conformance = true;
        return this;
    }

    /// <summary>Sets the initial arrangement of pages in a conforming viewer.</summary>
    public PdfDocumentBuilder SetPageLayout(PdfPageLayout layout)
    {
        if (!Enum.IsDefined(layout)) throw new ArgumentOutOfRangeException(nameof(layout));
        _pageLayout = layout;
        return this;
    }

    /// <summary>Sets the navigation panel or presentation mode shown when the document opens.</summary>
    public PdfDocumentBuilder SetPageMode(PdfPageMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _pageMode = mode;
        return this;
    }

    /// <summary>Sets typed viewer and print preferences for the document catalog.</summary>
    public PdfDocumentBuilder SetViewerPreferences(PdfViewerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!Enum.IsDefined(preferences.ReadingDirection))
            throw new ArgumentOutOfRangeException(nameof(preferences));
        if (!Enum.IsDefined(preferences.PrintScaling))
            throw new ArgumentOutOfRangeException(nameof(preferences));
        if (!Enum.IsDefined(preferences.Duplex))
            throw new ArgumentOutOfRangeException(nameof(preferences));
        _viewerPreferences = preferences;
        return this;
    }

    /// <summary>Adds an empty page with the specified media-box dimensions.</summary>
    public PdfDocumentBuilder AddBlankPage(double width = 612, double height = 792) =>
        AddPage(width, height, ReadOnlyMemory<byte>.Empty);

    /// <summary>Adds a page containing caller-supplied raw PDF content-stream bytes.</summary>
    public PdfDocumentBuilder AddPage(double width, double height, ReadOnlyMemory<byte> content)
    {
        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));
        EnsurePageCapacity();
        _pages.Add(new PageDefinition(width, height, content.ToArray(),
            new Dictionary<PdfStandardFont, PdfName>(), [],
            new Dictionary<PdfImage, PdfName>(),
            new Dictionary<PdfOptionalContentGroup, PdfName>(),
            new Dictionary<PdfGraphicsState, PdfName>(),
            new Dictionary<PdfShading, PdfName>(),
            new Dictionary<PdfFormXObject, PdfName>(),
            new Dictionary<PdfTilingPattern, PdfName>(),
            new Dictionary<PdfIccProfile, PdfName>(),
            new Dictionary<PdfSpotColor, PdfName>(),
            new Dictionary<PdfLabColorSpace, PdfName>(),
            new Dictionary<PdfIndexedColorSpace, PdfName>(),
            new Dictionary<PdfCalibratedColorSpace, PdfName>(), [], [], content.Length > 0,
            0, 1, new Dictionary<PdfPageBox, PageBoxDefinition>(), null, null, null, null));
        return this;
    }

    /// <summary>Adds a page and transfers the typed content builder's complete resource graph.</summary>
    public PdfDocumentBuilder AddPage(double width, double height, PdfContentStreamBuilder content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));
        EnsurePageCapacity();
        _pages.Add(new PageDefinition(
            width,
            height,
            content.Build(),
            content.FontResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            [.. content.EmbeddedFontResources],
            content.ImageResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.OptionalContentResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.GraphicsStateResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.ShadingResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.FormResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.PatternResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.IccColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.SpotColorResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.LabColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.IndexedColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value),
            content.CalibratedColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value), [],
            [.. content.MarkedContentIds.Order()], content.HasUntaggedContent,
            0, 1, new Dictionary<PdfPageBox, PageBoxDefinition>(), null, null, null, null));
        return this;
    }

    private void EnsurePageCapacity()
    {
        if (_pages.Count >= PdfPageTree.MaximumPageCount)
            throw new NotSupportedException(
                "A PDF cannot contain more than 1,000,000 pages.");
    }

    /// <summary>Sets the clockwise page rotation shown by conforming viewers.</summary>
    public PdfDocumentBuilder SetPageRotation(int pageIndex, int degrees)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        if (degrees is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(degrees),
                "Page rotation must be 0, 90, 180, or 270 degrees.");
        _pages[pageIndex] = _pages[pageIndex] with { Rotation = degrees };
        return this;
    }

    /// <summary>Sets a visible, print-production, or artwork boundary within the media box.</summary>
    public PdfDocumentBuilder SetPageBox(
        int pageIndex, PdfPageBox box, double x, double y, double width, double height)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        if (!Enum.IsDefined(box)) throw new ArgumentOutOfRangeException(nameof(box));
        ValidateRectangle(x, y, width, height);
        PageDefinition page = _pages[pageIndex];
        if (x < 0 || y < 0 || x + width > page.Width || y + height > page.Height)
            throw new ArgumentOutOfRangeException(nameof(width),
                "A page box must remain inside the page media box.");
        var boxes = page.Boxes.ToDictionary(entry => entry.Key, entry => entry.Value);
        boxes[box] = new PageBoxDefinition(x, y, width, height);
        _pages[pageIndex] = page with { Boxes = boxes };
        return this;
    }

    /// <summary>Scales default user-space units for unusually large or small pages.</summary>
    public PdfDocumentBuilder SetPageUserUnit(int pageIndex, double userUnit)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        if (!double.IsFinite(userUnit) || userUnit is <= 0 or > 75_000)
            throw new ArgumentOutOfRangeException(nameof(userUnit));
        _pages[pageIndex] = _pages[pageIndex] with { UserUnit = userUnit };
        return this;
    }

    /// <summary>Sets the order in which keyboard focus traverses annotations on a page.</summary>
    public PdfDocumentBuilder SetPageTabOrder(int pageIndex, PdfPageTabOrder tabOrder)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        if (!Enum.IsDefined(tabOrder))
            throw new ArgumentOutOfRangeException(nameof(tabOrder));
        _pages[pageIndex] = _pages[pageIndex] with { TabOrder = tabOrder };
        return this;
    }

    /// <summary>Sets the visual transition used when advancing to the page.</summary>
    public PdfDocumentBuilder SetPageTransition(
        int pageIndex, PdfPageTransition transition)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentNullException.ThrowIfNull(transition);
        _pages[pageIndex] = _pages[pageIndex] with { Transition = transition };
        return this;
    }

    /// <summary>Sets how long a page remains visible during automatic presentation.</summary>
    public PdfDocumentBuilder SetPageDisplayDuration(int pageIndex, double seconds)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        _pages[pageIndex] = _pages[pageIndex] with { DisplayDuration = seconds };
        return this;
    }

    /// <summary>Sets the thumbnail image associated with a page.</summary>
    public PdfDocumentBuilder SetPageThumbnail(int pageIndex, PdfImage thumbnail)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentNullException.ThrowIfNull(thumbnail);
        _pages[pageIndex] = _pages[pageIndex] with { Thumbnail = thumbnail };
        return this;
    }

    /// <summary>Adds a rectangular link that opens an absolute URI.</summary>
    public PdfDocumentBuilder AddUriLink(
        int pageIndex, double x, double y, double width, double height, string uri,
        PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        string normalizedUri = PdfLinkAnnotationFactory.ValidateUri(uri);
        PageDefinition page = _pages[pageIndex];
        _pages[pageIndex] = page with
        {
            Links = [.. page.Links, new UriLinkDefinition(
                x, y, width, height, appearance ?? new PdfLinkAppearance(), normalizedUri,
                null, annotationMetadata, contents)]
        };
        return this;
    }

    /// <summary>Adds a quadrilateral link that opens an absolute URI.</summary>
    public PdfDocumentBuilder AddUriLink(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads, string uri,
        PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        string normalizedUri = PdfLinkAnnotationFactory.ValidateUri(uri);
        (double minX, double minY, double maxX, double maxY) = TextMarkupBounds(values);
        PageDefinition page = _pages[pageIndex];
        _pages[pageIndex] = page with
        {
            Links = [.. page.Links, new UriLinkDefinition(
                minX, minY, maxX - minX, maxY - minY,
                appearance ?? new PdfLinkAppearance(), normalizedUri, values,
                annotationMetadata, contents)]
        };
        return this;
    }

    /// <summary>Adds a rectangular link to a destination in this document.</summary>
    public PdfDocumentBuilder AddPageLink(
        int pageIndex, double x, double y, double width, double height, int destinationPageIndex,
        PdfLinkAppearance? appearance = null, PdfDestination? destination = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidatePageIndex(destinationPageIndex, nameof(destinationPageIndex));
        ValidateRectangle(x, y, width, height);
        destination ??= PdfDestination.FitPage();
        PageDefinition page = _pages[pageIndex];
        _pages[pageIndex] = page with
        {
            Links = [.. page.Links,
                new PageLinkDefinition(x, y, width, height,
                    appearance ?? new PdfLinkAppearance(), destinationPageIndex, destination,
                    null, annotationMetadata, contents)]
        };
        return this;
    }

    /// <summary>Adds a quadrilateral link to a destination in this document.</summary>
    public PdfDocumentBuilder AddPageLink(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads, int destinationPageIndex,
        PdfLinkAppearance? appearance = null, PdfDestination? destination = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidatePageIndex(destinationPageIndex, nameof(destinationPageIndex));
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        (double minX, double minY, double maxX, double maxY) = TextMarkupBounds(values);
        PageDefinition page = _pages[pageIndex];
        _pages[pageIndex] = page with
        {
            Links = [.. page.Links, new PageLinkDefinition(
                minX, minY, maxX - minX, maxY - minY,
                appearance ?? new PdfLinkAppearance(), destinationPageIndex,
                destination ?? PdfDestination.FitPage(), values, annotationMetadata, contents)]
        };
        return this;
    }

    /// <summary>Adds a rectangular link to a previously defined named destination.</summary>
    public PdfDocumentBuilder AddNamedDestinationLink(
        int pageIndex, double x, double y, double width, double height, string destinationName,
        PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        if (string.IsNullOrWhiteSpace(destinationName))
            throw new ArgumentException("A named destination cannot be empty.", nameof(destinationName));
        if (!_namedDestinations.Any(destination =>
                string.Equals(destination.Name, destinationName, StringComparison.Ordinal)))
            throw new ArgumentException("The named destination has not been defined.", nameof(destinationName));
        PageDefinition page = _pages[pageIndex];
        _pages[pageIndex] = page with
        {
            Links = [.. page.Links,
                new NamedDestinationLinkDefinition(x, y, width, height,
                    appearance ?? new PdfLinkAppearance(), destinationName,
                    null, annotationMetadata, contents)]
        };
        return this;
    }

    /// <summary>Adds a quadrilateral link to a previously defined named destination.</summary>
    public PdfDocumentBuilder AddNamedDestinationLink(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads, string destinationName,
        PdfLinkAppearance? appearance = null,
        PdfAnnotationMetadata? annotationMetadata = null, string? contents = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        PdfTextQuad[] values = PdfLinkAnnotationFactory.ValidateQuads(quads);
        if (string.IsNullOrWhiteSpace(destinationName)
            || !_namedDestinations.Any(destination =>
                string.Equals(destination.Name, destinationName, StringComparison.Ordinal)))
            throw new ArgumentException("The named destination has not been defined.", nameof(destinationName));
        (double minX, double minY, double maxX, double maxY) = TextMarkupBounds(values);
        PageDefinition page = _pages[pageIndex];
        _pages[pageIndex] = page with
        {
            Links = [.. page.Links, new NamedDestinationLinkDefinition(
                minX, minY, maxX - minX, maxY - minY,
                appearance ?? new PdfLinkAppearance(), destinationName, values,
                annotationMetadata, contents)]
        };
        return this;
    }

    /// <summary>Adds a named destination that fits its target page in the viewer.</summary>
    public PdfDocumentBuilder AddNamedDestination(string name, int pageIndex) =>
        AddNamedDestination(name, pageIndex, PdfDestination.FitPage());

    /// <summary>Adds a named destination with an explicit target view.</summary>
    public PdfDocumentBuilder AddNamedDestination(
        string name, int pageIndex, PdfDestination destination)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A named destination cannot be empty.", nameof(name));
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentNullException.ThrowIfNull(destination);
        if (_namedDestinations.Any(destination =>
                string.Equals(destination.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Named destinations must be unique.", nameof(name));
        _namedDestinations.Add(new NamedDestinationDefinition(name, pageIndex, destination));
        return this;
    }

    /// <summary>Sets the page destination shown when the document opens.</summary>
    public PdfDocumentBuilder SetOpenAction(int pageIndex, PdfDestination destination)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentNullException.ThrowIfNull(destination);
        _openAction = new OpenActionDefinition(pageIndex, null, destination);
        return this;
    }

    /// <summary>Sets a previously defined named destination as the document open action.</summary>
    public PdfDocumentBuilder SetNamedOpenAction(string destinationName)
    {
        if (string.IsNullOrWhiteSpace(destinationName)
            || !_namedDestinations.Any(destination =>
                string.Equals(destination.Name, destinationName, StringComparison.Ordinal)))
            throw new ArgumentException(
                "A named open action requires an existing destination.", nameof(destinationName));
        _openAction = new OpenActionDefinition(null, destinationName, null);
        return this;
    }

    /// <summary>Starts a typed page-label numbering range at the specified page.</summary>
    public PdfDocumentBuilder AddPageLabelRange(
        int pageIndex,
        PdfPageLabelStyle style,
        string? prefix = null,
        int startNumber = 1)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        if (!Enum.IsDefined(style))
            throw new ArgumentOutOfRangeException(nameof(style));
        ArgumentOutOfRangeException.ThrowIfLessThan(startNumber, 1);
        if (style == PdfPageLabelStyle.None && string.IsNullOrEmpty(prefix))
            throw new ArgumentException("A page-label range without numbering requires a prefix.", nameof(prefix));
        if (_pageLabels.Any(label => label.PageIndex == pageIndex))
            throw new ArgumentException("A page-label range already begins on this page.", nameof(pageIndex));
        _pageLabels.Add(new PageLabelDefinition(pageIndex, style, prefix, startNumber));
        return this;
    }

    /// <summary>Adds a hierarchical bookmark targeting a page in this document.</summary>
    public PdfDocumentBuilder AddBookmark(
        string title, int pageIndex, int level = 0, PdfBookmarkOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A bookmark title cannot be empty.", nameof(title));
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (_bookmarks.Count == 0 && level != 0)
            throw new ArgumentException("The first bookmark must be at level zero.", nameof(level));
        if (_bookmarks.Count > 0 && level > _bookmarks[^1].Level + 1)
            throw new ArgumentException("A bookmark level cannot skip its parent level.", nameof(level));
        options ??= new PdfBookmarkOptions();
        ArgumentNullException.ThrowIfNull(options.Destination);
        _bookmarks.Add(new BookmarkDefinition(title, pageIndex, null, level, options));
        return this;
    }

    /// <summary>Adds a hierarchical bookmark targeting a previously defined named destination.</summary>
    public PdfDocumentBuilder AddNamedDestinationBookmark(
        string title, string destinationName, int level = 0,
        PdfBookmarkOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A bookmark title cannot be empty.", nameof(title));
        if (string.IsNullOrWhiteSpace(destinationName)
            || !_namedDestinations.Any(destination =>
                string.Equals(destination.Name, destinationName, StringComparison.Ordinal)))
            throw new ArgumentException(
                "A named-destination bookmark requires an existing destination.", nameof(destinationName));
        ValidateBookmarkLevel(level);
        options ??= new PdfBookmarkOptions();
        _bookmarks.Add(new BookmarkDefinition(title, null, destinationName, level, options));
        return this;
    }

    private void ValidateBookmarkLevel(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (_bookmarks.Count == 0 && level != 0)
            throw new ArgumentException("The first bookmark must be at level zero.", nameof(level));
        if (_bookmarks.Count > 0 && level > _bookmarks[^1].Level + 1)
            throw new ArgumentException("A bookmark level cannot skip its parent level.", nameof(level));
    }

    /// <summary>Adds a logical structure container without direct page content.</summary>
    public PdfDocumentBuilder AddStructureContainer(
        PdfStructureType type,
        int level = 0,
        string? alternateDescription = null,
        string? actualText = null,
        PdfListNumbering? listNumbering = null) =>
        AddStructure(type, level, null, null, alternateDescription, actualText, listNumbering);

    /// <summary>Associates tagged page content with an element in the logical structure tree.</summary>
    public PdfDocumentBuilder AddStructureElement(
        PdfStructureType type,
        int pageIndex,
        int markedContentId,
        int level = 0,
        string? alternateDescription = null,
        string? actualText = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentOutOfRangeException.ThrowIfNegative(markedContentId);
        if (!_pages[pageIndex].MarkedContentIds.Contains(markedContentId))
            throw new ArgumentException(
                "The page content does not define this marked-content identifier.",
                nameof(markedContentId));
        if (_structureElements.Any(element =>
                element.PageIndex == pageIndex && element.MarkedContentId == markedContentId))
            throw new ArgumentException(
                "A marked-content identifier can belong to only one structure element.",
                nameof(markedContentId));
        return AddStructure(
            type, level, pageIndex, markedContentId, alternateDescription, actualText, null);
    }

    private PdfDocumentBuilder AddStructure(
        PdfStructureType type,
        int level,
        int? pageIndex,
        int? markedContentId,
        string? alternateDescription,
        string? actualText,
        PdfListNumbering? listNumbering)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        if (_structureElements.Count == 0 && level != 0)
            throw new ArgumentException("The first structure element must be at level zero.", nameof(level));
        if (_structureElements.Count > 0 && level > _structureElements[^1].Level + 1)
            throw new ArgumentException(
                "A structure level cannot skip its parent level.", nameof(level));
        if (alternateDescription is not null && string.IsNullOrWhiteSpace(alternateDescription))
            throw new ArgumentException(
                "An alternate description cannot be empty.", nameof(alternateDescription));
        if (actualText is not null && string.IsNullOrEmpty(actualText))
            throw new ArgumentException("Actual text cannot be empty.", nameof(actualText));
        if (listNumbering.HasValue && (!Enum.IsDefined(listNumbering.Value)
            || type != PdfStructureType.List))
            throw new ArgumentException(
                "List numbering can be assigned only to a List structure container.",
                nameof(listNumbering));
        _structureElements.Add(new StructureElementDefinition(
            type, level, pageIndex, markedContentId, alternateDescription, actualText,
            listNumbering));
        return this;
    }

    /// <summary>Embeds a file and optionally associates it with the document.</summary>
    public PdfDocumentBuilder AddAttachment(
        string fileName,
        ReadOnlyMemory<byte> data,
        string mimeType = "application/octet-stream",
        string? description = null,
        PdfAssociatedFileRelationship relationship = PdfAssociatedFileRelationship.Data,
        DateTimeOffset? modificationDate = null,
        DateTimeOffset? creationDate = null)
    {
        PdfAttachmentFactory.Validate(fileName, mimeType, relationship);
        if (_attachments.Any(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Attachment file names must be unique.", nameof(fileName));
        _attachments.Add(new AttachmentDefinition(
            fileName, data.ToArray(), mimeType, description, relationship,
            modificationDate, creationDate));
        return this;
    }

    /// <summary>Places an embedded file on a page as a file-attachment annotation.</summary>
    public PdfDocumentBuilder AddFileAttachmentAnnotation(
        int pageIndex,
        double x,
        double y,
        double size,
        string fileName,
        string? contents = null,
        PdfFileAttachmentIcon icon = PdfFileAttachmentIcon.Paperclip,
        PdfRgbColor? color = null,
        PdfAnnotationMetadata? annotationMetadata = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("A file name is required.", nameof(fileName));
        if (!_attachments.Any(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"The attachment '{fileName}' must be added before placing it on a page.", nameof(fileName));
        if (!Enum.IsDefined(icon)) throw new ArgumentOutOfRangeException(nameof(icon));
        _fileAttachmentAnnotations.Add(new FileAttachmentAnnotationDefinition(
            pageIndex, x, y, size, fileName, contents, icon,
            color ?? new PdfRgbColor(0.2, 0.45, 0.85), annotationMetadata));
        return this;
    }

    /// <summary>Adds a text form field and its widget appearance to a page.</summary>
    public PdfDocumentBuilder AddTextField(
        int pageIndex,
        string name,
        double x,
        double y,
        double width,
        double height,
        string value = "",
        double fontSize = 12,
        PdfTextFieldOptions? options = null,
        TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null,
        string? defaultValue = null,
        string? richTextValue = null,
        PdfFormFieldAppearanceStyle? appearanceStyle = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        ArgumentNullException.ThrowIfNull(value);
        defaultValue ??= value;
        if (embeddedFont is null && (value.Any(character => character > 0xFF)
            || defaultValue.Any(character => character > 0xFF)))
            throw new ArgumentException(
                "Unicode text-field values require an embedded font.",
                nameof(value));
        if (embeddedFont is not null)
        {
            ValidateFormFontText(embeddedFont, value, nameof(value));
            ValidateFormFontText(embeddedFont, defaultValue, nameof(defaultValue));
        }
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        options ??= new PdfTextFieldOptions();
        if (!Enum.IsDefined(options.Alignment))
            throw new ArgumentOutOfRangeException(nameof(options), "Text alignment is invalid.");
        if (!options.Multiline && value.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("A single-line text field cannot contain line breaks.", nameof(value));
        if (!options.Multiline && defaultValue.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("A single-line default value cannot contain line breaks.", nameof(defaultValue));
        if (options.Password && options.Multiline)
            throw new ArgumentException("A password field cannot be multiline.", nameof(options));
        if (options.FileSelect && (options.Multiline || options.Password || options.Comb))
            throw new ArgumentException(
                "A file-selection field cannot be multiline, a password field, or a comb field.",
                nameof(options));
        if (richTextValue is not null)
        {
            if (options.Password || options.FileSelect || options.Comb)
                throw new ArgumentException(
                    "Rich text cannot be combined with password, file-selection, or comb fields.",
                    nameof(richTextValue));
            ValidateRichTextValue(richTextValue);
        }
        appearanceStyle = ValidateFormFieldAppearanceStyle(appearanceStyle);
        if (options.Password && embeddedFont is not null && embeddedFont.GetGlyphId('*') == 0)
            throw new ArgumentException("A password field font must contain the mask glyph U+002A.", nameof(embeddedFont));
        if (options.MaximumLength is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaximumLength must be positive.");
        int valueCharacterCount = value.EnumerateRunes().Count();
        int defaultCharacterCount = defaultValue.EnumerateRunes().Count();
        if (options.MaximumLength.HasValue && valueCharacterCount > options.MaximumLength.Value)
            throw new ArgumentException("The initial value exceeds the text field's maximum length.", nameof(value));
        if (options.MaximumLength.HasValue && defaultCharacterCount > options.MaximumLength.Value)
            throw new ArgumentException("The default value exceeds the text field's maximum length.", nameof(defaultValue));
        if (options.Comb && (!options.MaximumLength.HasValue || options.Multiline || options.Password))
            throw new ArgumentException(
                "A comb field requires MaximumLength and cannot also be multiline or a password field.", nameof(options));
        var definition = new TextFieldDefinition(
            pageIndex, name, x, y, width, height, value, defaultValue, richTextValue,
            fontSize, options, embeddedFont, appearanceStyle,
            ValidateFieldMetadata(fieldMetadata));
        ValidateInitialTextFieldFit(definition);
        _textFields.Add(definition);
        return this;
    }

    /// <summary>Adds a checkbox form field and its widget appearance to a page.</summary>
    public PdfDocumentBuilder AddCheckBox(
        int pageIndex,
        string name,
        double x,
        double y,
        double width,
        double height,
        bool isChecked = false,
        string exportValue = "Yes",
        PdfFormFieldMetadata? fieldMetadata = null,
        PdfFormFieldOptions? options = null,
        PdfCheckBoxMark mark = PdfCheckBoxMark.Check,
        bool? defaultChecked = null,
        PdfFormFieldAppearanceStyle? appearanceStyle = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        if (string.IsNullOrWhiteSpace(exportValue)
            || exportValue.Any(character => character is < '!' or > '~')
            || exportValue == "Off")
            throw new ArgumentException(
                "A checkbox export value must contain printable ASCII and cannot be the reserved Off state.",
                nameof(exportValue));
        if (!Enum.IsDefined(mark)) throw new ArgumentOutOfRangeException(nameof(mark));
        _checkBoxes.Add(new CheckBoxDefinition(
            pageIndex, name, x, y, width, height, isChecked, defaultChecked ?? isChecked, exportValue,
            ValidateFieldMetadata(fieldMetadata), options ?? new PdfFormFieldOptions(), mark,
            ValidateFormFieldAppearanceStyle(appearanceStyle)));
        return this;
    }

    /// <summary>Adds a radio-button group whose widgets may span multiple pages.</summary>
    public PdfDocumentBuilder AddRadioGroup(
        string name,
        IEnumerable<PdfRadioButtonOption> options,
        string? selectedValue = null,
        PdfFormFieldMetadata? fieldMetadata = null,
        PdfFormFieldOptions? fieldOptions = null,
        PdfRadioGroupOptions? radioOptions = null,
        string? defaultSelectedValue = null)
    {
        ValidateUniqueFieldName(name);
        ArgumentNullException.ThrowIfNull(options);
        PdfRadioButtonOption[] values = [.. options];
        if (values.Length < 2)
            throw new ArgumentException("A radio group requires at least two options.", nameof(options));
        radioOptions ??= new PdfRadioGroupOptions();
        radioOptions = radioOptions with
        {
            AppearanceStyle = ValidateFormFieldAppearanceStyle(radioOptions.AppearanceStyle)
        };
        var exportValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfRadioButtonOption option in values)
        {
            ArgumentNullException.ThrowIfNull(option);
            ValidatePageIndex(option.PageIndex, nameof(options));
            ValidateRectangle(option.X, option.Y, option.Width, option.Height);
            if (string.IsNullOrWhiteSpace(option.ExportValue)
                || option.ExportValue.Any(character => character is < '!' or > '~')
                || option.ExportValue == "Off")
                throw new ArgumentException(
                    "Radio export values must contain printable ASCII and cannot be the reserved Off state.",
                    nameof(options));
            if (!exportValues.Add(option.ExportValue) && !radioOptions.RadiosInUnison)
                throw new ArgumentException("Radio export values must be unique within a group.", nameof(options));
        }
        if (selectedValue is not null && !exportValues.Contains(selectedValue))
            throw new ArgumentException("The selected radio value must name one of the options.", nameof(selectedValue));
        if (defaultSelectedValue is not null && !exportValues.Contains(defaultSelectedValue))
            throw new ArgumentException(
                "The default radio value must name one of the options.", nameof(defaultSelectedValue));
        _radioGroups.Add(new RadioGroupDefinition(
            name, values, selectedValue, defaultSelectedValue ?? selectedValue,
            ValidateFieldMetadata(fieldMetadata),
            fieldOptions ?? new PdfFormFieldOptions(), radioOptions));
        return this;
    }

    /// <summary>Adds a combo box whose export and display strings are identical.</summary>
    public PdfDocumentBuilder AddComboBox(
        int pageIndex,
        string name,
        double x,
        double y,
        double width,
        double height,
        IEnumerable<string> options,
        string? selectedValue = null,
        bool editable = false,
        double fontSize = 12,
        TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null,
        PdfFormFieldOptions? fieldOptions = null,
        PdfChoiceFieldOptions? choiceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        ArgumentNullException.ThrowIfNull(options);
        PdfChoiceOption[] values = [.. options.Select(value => new PdfChoiceOption(value, value))];
        if (values.Length == 0 || values.Any(value => string.IsNullOrEmpty(value.ExportValue)))
            throw new ArgumentException("Combo-box options cannot be empty.", nameof(options));
        if (embeddedFont is null && values.Any(value => value.DisplayValue.Any(character => character > 0xFF)))
            throw new ArgumentException("Combo-box options require an embedded font for Unicode text.", nameof(options));
        if (values.Select(value => value.ExportValue).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Combo-box options must be unique.", nameof(options));
        if (selectedValue is not null && !editable
            && !values.Any(value => string.Equals(value.ExportValue, selectedValue, StringComparison.Ordinal)))
            throw new ArgumentException("A non-editable combo-box value must be one of its options.", nameof(selectedValue));
        if (embeddedFont is null && selectedValue?.Any(character => character > 0xFF) == true)
            throw new ArgumentException("The baseline combo-box appearance supports Latin-1 values.", nameof(selectedValue));
        if (embeddedFont is not null)
        {
            foreach (PdfChoiceOption value in values)
                ValidateFormFontText(embeddedFont, value.DisplayValue, nameof(options));
            if (selectedValue is not null)
                ValidateFormFontText(embeddedFont, selectedValue, nameof(selectedValue));
        }
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        choiceOptions = ValidateChoiceFieldOptions(choiceOptions);
        if (choiceOptions.SortOptions)
            Array.Sort(values, (left, right) => StringComparer.Ordinal.Compare(left.DisplayValue, right.DisplayValue));
        string[] selections = [selectedValue ?? values[0].ExportValue];
        _choiceFields.Add(new ChoiceFieldDefinition(
            pageIndex, name, x, y, width, height, values, selections,
            ResolveChoiceDefaultValues(values, selections, false, editable, embeddedFont, choiceOptions),
            IsComboBox: true, IsMultiSelect: false, editable,
            TopIndex: 0, fontSize, embeddedFont,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(), choiceOptions));
        return this;
    }

    /// <summary>Adds a single-selection list box whose export and display strings are identical.</summary>
    public PdfDocumentBuilder AddListBox(
        int pageIndex,
        string name,
        double x,
        double y,
        double width,
        double height,
        IEnumerable<string> options,
        string? selectedValue = null,
        double fontSize = 12,
        TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null,
        PdfFormFieldOptions? fieldOptions = null,
        int topIndex = 0,
        PdfChoiceFieldOptions? choiceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        ArgumentNullException.ThrowIfNull(options);
        PdfChoiceOption[] values = [.. options.Select(value => new PdfChoiceOption(value, value))];
        if (values.Length == 0 || values.Any(value => string.IsNullOrEmpty(value.ExportValue)))
            throw new ArgumentException("List-box options cannot be empty.", nameof(options));
        if (values.Select(value => value.ExportValue).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("List-box options must be unique.", nameof(options));
        if (selectedValue is not null
            && !values.Any(value => string.Equals(value.ExportValue, selectedValue, StringComparison.Ordinal)))
            throw new ArgumentException("A list-box value must be one of its options.", nameof(selectedValue));
        if (embeddedFont is null && values.Any(value => value.DisplayValue.Any(character => character > 0xFF)))
            throw new ArgumentException("List-box options require an embedded font for Unicode text.", nameof(options));
        if (embeddedFont is not null)
            foreach (PdfChoiceOption value in values)
                ValidateFormFontText(embeddedFont, value.DisplayValue, nameof(options));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (topIndex < 0 || topIndex >= values.Length)
            throw new ArgumentOutOfRangeException(nameof(topIndex));
        choiceOptions = ValidateChoiceFieldOptions(choiceOptions);
        if (choiceOptions.SortOptions)
            Array.Sort(values, (left, right) => StringComparer.Ordinal.Compare(left.DisplayValue, right.DisplayValue));
        string[] selections = [selectedValue ?? values[0].ExportValue];
        _choiceFields.Add(new ChoiceFieldDefinition(
            pageIndex, name, x, y, width, height, values, selections,
            ResolveChoiceDefaultValues(values, selections, false, false, embeddedFont, choiceOptions),
            IsComboBox: false, IsMultiSelect: false, Editable: false,
            topIndex, fontSize, embeddedFont,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(), choiceOptions));
        return this;
    }

    /// <summary>Adds a combo box with distinct export and display values.</summary>
    public PdfDocumentBuilder AddComboBoxOptions(
        int pageIndex, string name, double x, double y, double width, double height,
        IEnumerable<PdfChoiceOption> options, string? selectedExportValue = null,
        bool editable = false, double fontSize = 12, TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        PdfChoiceFieldOptions? choiceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        PdfChoiceOption[] values = ValidateChoiceOptions(options, embeddedFont, nameof(options));
        if (selectedExportValue is not null && !editable
            && !values.Any(value => string.Equals(
                value.ExportValue, selectedExportValue, StringComparison.Ordinal)))
            throw new ArgumentException(
                "A non-editable combo-box value must be one of its export values.",
                nameof(selectedExportValue));
        if (embeddedFont is null
            && selectedExportValue?.Any(character => character > 0xFF) == true
            && !values.Any(value => value.ExportValue == selectedExportValue))
            throw new ArgumentException(
                "An editable Unicode combo-box value requires an embedded font.",
                nameof(selectedExportValue));
        if (embeddedFont is not null && selectedExportValue is not null
            && !values.Any(value => value.ExportValue == selectedExportValue))
            ValidateFormFontText(embeddedFont, selectedExportValue, nameof(selectedExportValue));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        choiceOptions = ValidateChoiceFieldOptions(choiceOptions);
        if (choiceOptions.SortOptions)
            Array.Sort(values, (left, right) =>
                StringComparer.Ordinal.Compare(left.DisplayValue, right.DisplayValue));
        string[] selections = [selectedExportValue ?? values[0].ExportValue];
        _choiceFields.Add(new ChoiceFieldDefinition(
            pageIndex, name, x, y, width, height, values, selections,
            ResolveChoiceDefaultValues(values, selections, false, editable, embeddedFont, choiceOptions),
            IsComboBox: true,
            IsMultiSelect: false, editable, TopIndex: 0, fontSize, embeddedFont,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            choiceOptions));
        return this;
    }

    /// <summary>Adds a multiselect list box whose export and display strings are identical.</summary>
    public PdfDocumentBuilder AddMultiSelectListBox(
        int pageIndex,
        string name,
        double x,
        double y,
        double width,
        double height,
        IEnumerable<string> options,
        IEnumerable<string>? selectedValues = null,
        double fontSize = 12,
        TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null,
        PdfFormFieldOptions? fieldOptions = null,
        int topIndex = 0,
        PdfChoiceFieldOptions? choiceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        ArgumentNullException.ThrowIfNull(options);
        PdfChoiceOption[] values = [.. options.Select(value => new PdfChoiceOption(value, value))];
        if (values.Length == 0 || values.Any(value => string.IsNullOrEmpty(value.ExportValue)))
            throw new ArgumentException("List-box options cannot be empty.", nameof(options));
        if (values.Select(value => value.ExportValue).Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("List-box options must be unique.", nameof(options));
        string[] requestedSelections = selectedValues?.ToArray() ?? [];
        if (requestedSelections.Distinct(StringComparer.Ordinal).Count() != requestedSelections.Length)
            throw new ArgumentException("Selected list-box values must be unique.", nameof(selectedValues));
        if (requestedSelections.Any(selection => !values.Any(value =>
            string.Equals(value.ExportValue, selection, StringComparison.Ordinal))))
            throw new ArgumentException("Every selected list-box value must be one of its options.", nameof(selectedValues));
        choiceOptions = ValidateChoiceFieldOptions(choiceOptions);
        if (choiceOptions.SortOptions)
            Array.Sort(values, (left, right) => StringComparer.Ordinal.Compare(left.DisplayValue, right.DisplayValue));
        var selectedSet = requestedSelections.ToHashSet(StringComparer.Ordinal);
        string[] selections =
        [
            .. values.Where(value => selectedSet.Contains(value.ExportValue))
                .Select(value => value.ExportValue)
        ];
        if (embeddedFont is null && values.Any(value => value.DisplayValue.Any(character => character > 0xFF)))
            throw new ArgumentException("List-box options require an embedded font for Unicode text.", nameof(options));
        if (embeddedFont is not null)
            foreach (PdfChoiceOption value in values)
                ValidateFormFontText(embeddedFont, value.DisplayValue, nameof(options));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (topIndex < 0 || topIndex >= values.Length)
            throw new ArgumentOutOfRangeException(nameof(topIndex));
        _choiceFields.Add(new ChoiceFieldDefinition(
            pageIndex, name, x, y, width, height, values, selections,
            ResolveChoiceDefaultValues(values, selections, true, false, embeddedFont, choiceOptions),
            IsComboBox: false, IsMultiSelect: true, Editable: false, topIndex, fontSize, embeddedFont,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(), choiceOptions));
        return this;
    }

    /// <summary>Adds a single-selection list box with distinct export and display values.</summary>
    public PdfDocumentBuilder AddListBoxOptions(
        int pageIndex, string name, double x, double y, double width, double height,
        IEnumerable<PdfChoiceOption> options, string? selectedExportValue = null,
        double fontSize = 12, TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        int topIndex = 0, PdfChoiceFieldOptions? choiceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        PdfChoiceOption[] values = ValidateChoiceOptions(options, embeddedFont, nameof(options));
        if (selectedExportValue is not null && !values.Any(value =>
            string.Equals(value.ExportValue, selectedExportValue, StringComparison.Ordinal)))
            throw new ArgumentException(
                "A list-box value must be one of its export values.", nameof(selectedExportValue));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (topIndex < 0 || topIndex >= values.Length)
            throw new ArgumentOutOfRangeException(nameof(topIndex));
        choiceOptions = ValidateChoiceFieldOptions(choiceOptions);
        if (choiceOptions.SortOptions)
            Array.Sort(values, (left, right) =>
                StringComparer.Ordinal.Compare(left.DisplayValue, right.DisplayValue));
        string[] selections = [selectedExportValue ?? values[0].ExportValue];
        _choiceFields.Add(new ChoiceFieldDefinition(
            pageIndex, name, x, y, width, height, values, selections,
            ResolveChoiceDefaultValues(values, selections, false, false, embeddedFont, choiceOptions),
            IsComboBox: false,
            IsMultiSelect: false, Editable: false, topIndex, fontSize, embeddedFont,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            choiceOptions));
        return this;
    }

    /// <summary>Adds a multiselect list box with distinct export and display values.</summary>
    public PdfDocumentBuilder AddMultiSelectListBoxOptions(
        int pageIndex, string name, double x, double y, double width, double height,
        IEnumerable<PdfChoiceOption> options, IEnumerable<string>? selectedExportValues = null,
        double fontSize = 12, TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        int topIndex = 0, PdfChoiceFieldOptions? choiceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        PdfChoiceOption[] values = ValidateChoiceOptions(options, embeddedFont, nameof(options));
        string[] requested = selectedExportValues?.ToArray() ?? [];
        if (requested.Distinct(StringComparer.Ordinal).Count() != requested.Length)
            throw new ArgumentException("Selected export values must be unique.", nameof(selectedExportValues));
        if (requested.Any(selection => !values.Any(value =>
            string.Equals(value.ExportValue, selection, StringComparison.Ordinal))))
            throw new ArgumentException(
                "Every selected value must name an option export value.", nameof(selectedExportValues));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (topIndex < 0 || topIndex >= values.Length)
            throw new ArgumentOutOfRangeException(nameof(topIndex));
        choiceOptions = ValidateChoiceFieldOptions(choiceOptions);
        if (choiceOptions.SortOptions)
            Array.Sort(values, (left, right) =>
                StringComparer.Ordinal.Compare(left.DisplayValue, right.DisplayValue));
        var selectedSet = requested.ToHashSet(StringComparer.Ordinal);
        string[] selections =
        [
            .. values.Where(value => selectedSet.Contains(value.ExportValue))
                .Select(value => value.ExportValue)
        ];
        _choiceFields.Add(new ChoiceFieldDefinition(
            pageIndex, name, x, y, width, height, values, selections,
            ResolveChoiceDefaultValues(values, selections, true, false, embeddedFont, choiceOptions),
            IsComboBox: false, IsMultiSelect: true, Editable: false, topIndex,
            fontSize, embeddedFont, ValidateFieldMetadata(fieldMetadata),
            fieldOptions ?? new PdfFormFieldOptions(), choiceOptions));
        return this;
    }

    /// <summary>Adds a push button that opens an absolute URI.</summary>
    public PdfDocumentBuilder AddUriPushButton(
        int pageIndex, string name, double x, double y, double width, double height,
        string label, string uri, double fontSize = 12, TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        PdfPushButtonHighlightMode highlightMode = PdfPushButtonHighlightMode.Push,
        PdfFormFieldAppearanceStyle? appearanceStyle = null,
        PdfPushButtonAppearanceOptions? appearanceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        if (string.IsNullOrEmpty(label))
            throw new ArgumentException("A push-button label cannot be empty.", nameof(label));
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https" or "mailto"))
            throw new ArgumentException("A push-button URI must use http, https, or mailto.", nameof(uri));
        if (embeddedFont is null && label.Any(character => character > 0xFF))
            throw new ArgumentException("A Unicode push-button label requires an embedded font.", nameof(label));
        if (embeddedFont is not null)
            ValidateFormFontText(embeddedFont, label, nameof(label));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!Enum.IsDefined(highlightMode)) throw new ArgumentOutOfRangeException(nameof(highlightMode));
        _pushButtons.Add(new PushButtonDefinition(
            pageIndex, name, x, y, width, height, label, parsed.AbsoluteUri,
            DestinationPageIndex: null, Destination: null, NamedDestination: null, fontSize, embeddedFont,
            IsResetAction: false, ResetFields: null, ExcludeResetFields: false,
            SubmitUri: null, SubmitFields: null, ExcludeSubmitFields: false,
            highlightMode,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            ValidateFormFieldAppearanceStyle(appearanceStyle),
            ValidatePushButtonAppearanceOptions(appearanceOptions, embeddedFont)));
        return this;
    }

    /// <summary>Adds a push button that navigates to a page in this document.</summary>
    public PdfDocumentBuilder AddPagePushButton(
        int pageIndex, string name, double x, double y, double width, double height,
        string label, int destinationPageIndex, PdfDestination? destination = null,
        double fontSize = 12, TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        PdfPushButtonHighlightMode highlightMode = PdfPushButtonHighlightMode.Push,
        PdfFormFieldAppearanceStyle? appearanceStyle = null,
        PdfPushButtonAppearanceOptions? appearanceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidatePageIndex(destinationPageIndex, nameof(destinationPageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        if (string.IsNullOrEmpty(label))
            throw new ArgumentException("A push-button label cannot be empty.", nameof(label));
        if (embeddedFont is null && label.Any(character => character > 0xFF))
            throw new ArgumentException("A Unicode push-button label requires an embedded font.", nameof(label));
        if (embeddedFont is not null)
            ValidateFormFontText(embeddedFont, label, nameof(label));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!Enum.IsDefined(highlightMode)) throw new ArgumentOutOfRangeException(nameof(highlightMode));
        _pushButtons.Add(new PushButtonDefinition(
            pageIndex, name, x, y, width, height, label, Uri: null,
            destinationPageIndex, destination ?? PdfDestination.FitPage(), NamedDestination: null,
            fontSize, embeddedFont, IsResetAction: false, ResetFields: null, ExcludeResetFields: false,
            SubmitUri: null, SubmitFields: null, ExcludeSubmitFields: false,
            highlightMode,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            ValidateFormFieldAppearanceStyle(appearanceStyle),
            ValidatePushButtonAppearanceOptions(appearanceOptions, embeddedFont)));
        return this;
    }

    /// <summary>Adds a push button that navigates to a previously defined named destination.</summary>
    public PdfDocumentBuilder AddNamedDestinationPushButton(
        int pageIndex, string name, double x, double y, double width, double height,
        string label, string destinationName, double fontSize = 12,
        TrueTypeFont? embeddedFont = null, PdfFormFieldMetadata? fieldMetadata = null,
        PdfFormFieldOptions? fieldOptions = null,
        PdfPushButtonHighlightMode highlightMode = PdfPushButtonHighlightMode.Push,
        PdfFormFieldAppearanceStyle? appearanceStyle = null,
        PdfPushButtonAppearanceOptions? appearanceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        if (string.IsNullOrEmpty(label))
            throw new ArgumentException("A push-button label cannot be empty.", nameof(label));
        if (string.IsNullOrWhiteSpace(destinationName)
            || !_namedDestinations.Any(destination =>
                string.Equals(destination.Name, destinationName, StringComparison.Ordinal)))
            throw new ArgumentException("The named destination has not been defined.", nameof(destinationName));
        if (embeddedFont is null && label.Any(character => character > 0xFF))
            throw new ArgumentException("A Unicode push-button label requires an embedded font.", nameof(label));
        if (embeddedFont is not null)
            ValidateFormFontText(embeddedFont, label, nameof(label));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!Enum.IsDefined(highlightMode)) throw new ArgumentOutOfRangeException(nameof(highlightMode));
        _pushButtons.Add(new PushButtonDefinition(
            pageIndex, name, x, y, width, height, label, Uri: null,
            DestinationPageIndex: null, Destination: null, destinationName, fontSize, embeddedFont,
            IsResetAction: false, ResetFields: null, ExcludeResetFields: false,
            SubmitUri: null, SubmitFields: null, ExcludeSubmitFields: false,
            highlightMode,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            ValidateFormFieldAppearanceStyle(appearanceStyle),
            ValidatePushButtonAppearanceOptions(appearanceOptions, embeddedFont)));
        return this;
    }

    /// <summary>Adds a push button that resets all, selected, or excluded form fields.</summary>
    public PdfDocumentBuilder AddResetFormPushButton(
        int pageIndex, string name, double x, double y, double width, double height,
        string label, IEnumerable<string>? fields = null, bool excludeFields = false,
        double fontSize = 12, TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        PdfPushButtonHighlightMode highlightMode = PdfPushButtonHighlightMode.Push,
        PdfFormFieldAppearanceStyle? appearanceStyle = null,
        PdfPushButtonAppearanceOptions? appearanceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        if (string.IsNullOrEmpty(label))
            throw new ArgumentException("A push-button label cannot be empty.", nameof(label));
        string[]? resetFields = fields?.ToArray();
        if (resetFields is { Length: 0 } || resetFields?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Reset field names cannot be empty.", nameof(fields));
        if (resetFields?.Distinct(StringComparer.Ordinal).Count() != resetFields?.Length)
            throw new ArgumentException("Reset field names must be unique.", nameof(fields));
        if (excludeFields && resetFields is null)
            throw new ArgumentException("Reset exclusion mode requires a field list.", nameof(fields));
        if (resetFields?.Any(field => !FormFieldNameExists(field)) == true)
            throw new ArgumentException("Every reset field must already be defined.", nameof(fields));
        if (embeddedFont is null && label.Any(character => character > 0xFF))
            throw new ArgumentException("A Unicode push-button label requires an embedded font.", nameof(label));
        if (embeddedFont is not null)
            ValidateFormFontText(embeddedFont, label, nameof(label));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!Enum.IsDefined(highlightMode)) throw new ArgumentOutOfRangeException(nameof(highlightMode));
        _pushButtons.Add(new PushButtonDefinition(
            pageIndex, name, x, y, width, height, label, Uri: null,
            DestinationPageIndex: null, Destination: null, NamedDestination: null,
            fontSize, embeddedFont, IsResetAction: true, resetFields, excludeFields,
            SubmitUri: null, SubmitFields: null, ExcludeSubmitFields: false,
            highlightMode,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            ValidateFormFieldAppearanceStyle(appearanceStyle),
            ValidatePushButtonAppearanceOptions(appearanceOptions, embeddedFont)));
        return this;
    }

    /// <summary>Adds a push button that submits the PDF for all, selected, or excluded fields.</summary>
    public PdfDocumentBuilder AddSubmitPdfPushButton(
        int pageIndex, string name, double x, double y, double width, double height,
        string label, string uri, IEnumerable<string>? fields = null, bool excludeFields = false,
        double fontSize = 12, TrueTypeFont? embeddedFont = null,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        PdfPushButtonHighlightMode highlightMode = PdfPushButtonHighlightMode.Push,
        PdfFormFieldAppearanceStyle? appearanceStyle = null,
        PdfPushButtonAppearanceOptions? appearanceOptions = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        if (string.IsNullOrEmpty(label))
            throw new ArgumentException("A push-button label cannot be empty.", nameof(label));
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme is not ("http" or "https"))
            throw new ArgumentException("A submit URI must use http or https.", nameof(uri));
        string[]? submitFields = fields?.ToArray();
        if (submitFields is { Length: 0 } || submitFields?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Submit field names cannot be empty.", nameof(fields));
        if (submitFields?.Distinct(StringComparer.Ordinal).Count() != submitFields?.Length)
            throw new ArgumentException("Submit field names must be unique.", nameof(fields));
        if (excludeFields && submitFields is null)
            throw new ArgumentException("Submit exclusion mode requires a field list.", nameof(fields));
        if (submitFields?.Any(field => !FormFieldNameExists(field)) == true)
            throw new ArgumentException("Every submit field must already be defined.", nameof(fields));
        if (embeddedFont is null && label.Any(character => character > 0xFF))
            throw new ArgumentException("A Unicode push-button label requires an embedded font.", nameof(label));
        if (embeddedFont is not null)
            ValidateFormFontText(embeddedFont, label, nameof(label));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!Enum.IsDefined(highlightMode)) throw new ArgumentOutOfRangeException(nameof(highlightMode));
        _pushButtons.Add(new PushButtonDefinition(
            pageIndex, name, x, y, width, height, label, Uri: null,
            DestinationPageIndex: null, Destination: null, NamedDestination: null,
            fontSize, embeddedFont, IsResetAction: false, ResetFields: null,
            ExcludeResetFields: false, parsed.AbsoluteUri, submitFields, excludeFields,
            highlightMode,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            ValidateFormFieldAppearanceStyle(appearanceStyle),
            ValidatePushButtonAppearanceOptions(appearanceOptions, embeddedFont)));
        return this;
    }

    /// <summary>Adds an unsigned signature field with optional seed and locking constraints.</summary>
    public PdfDocumentBuilder AddSignatureField(
        int pageIndex, string name, double x, double y, double width, double height,
        PdfFormFieldMetadata? fieldMetadata = null, PdfFormFieldOptions? fieldOptions = null,
        PdfSignatureFieldLock? fieldLock = null,
        PdfSignatureSeedValue? seedValue = null,
        string? appearanceText = null, double fontSize = 12,
        TrueTypeFont? embeddedFont = null,
        PdfFormFieldAppearanceStyle? appearanceStyle = null,
        PdfTextFieldAlignment appearanceAlignment = PdfTextFieldAlignment.Left)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        ValidateUniqueFieldName(name);
        fieldLock = ValidateSignatureFieldLock(fieldLock);
        seedValue = ValidateSignatureSeedValue(seedValue);
        if (appearanceText is not null && string.IsNullOrWhiteSpace(appearanceText))
            throw new ArgumentException("Signature appearance text cannot be empty.", nameof(appearanceText));
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (embeddedFont is null && appearanceText?.Any(character => character > 0xFF) == true)
            throw new ArgumentException(
                "Unicode signature appearance text requires an embedded font.", nameof(appearanceText));
        if (embeddedFont is not null && appearanceText is not null)
            ValidateFormFontText(embeddedFont, appearanceText, nameof(appearanceText));
        if (!Enum.IsDefined(appearanceAlignment))
            throw new ArgumentOutOfRangeException(nameof(appearanceAlignment));
        _signatureFields.Add(new SignatureFieldDefinition(
            pageIndex, name, x, y, width, height,
            ValidateFieldMetadata(fieldMetadata), fieldOptions ?? new PdfFormFieldOptions(),
            fieldLock, seedValue, appearanceText, fontSize, embeddedFont,
            ValidateFormFieldAppearanceStyle(appearanceStyle), appearanceAlignment));
        return this;
    }

    /// <summary>Adds a text-note annotation with optional popup, reply, and workflow state.</summary>
    public PdfDocumentBuilder AddTextNote(
        int pageIndex,
        double x,
        double y,
        string contents,
        PdfRgbColor? color = null,
        bool open = false,
        double size = 24,
        PdfAnnotationMetadata? annotationMetadata = null,
        PdfTextNoteIcon icon = PdfTextNoteIcon.Note,
        PdfTextNoteState? state = null,
        string? name = null,
        string? inReplyTo = null,
        PdfAnnotationReplyType replyType = PdfAnnotationReplyType.Reply,
        PdfAnnotationPopup? popup = null)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentNullException.ThrowIfNull(contents);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!Enum.IsDefined(icon)) throw new ArgumentOutOfRangeException(nameof(icon));
        if (state is not null && !Enum.IsDefined(state.Value))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (!Enum.IsDefined(replyType)) throw new ArgumentOutOfRangeException(nameof(replyType));
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Annotation names cannot be empty.", nameof(name));
            if (_textNotes.Any(note => string.Equals(note.Name, name, StringComparison.Ordinal)))
                throw new ArgumentException($"A text-note annotation named '{name}' already exists.", nameof(name));
        }
        if (inReplyTo is not null)
        {
            if (string.IsNullOrWhiteSpace(inReplyTo))
                throw new ArgumentException("Reply targets cannot be empty.", nameof(inReplyTo));
            if (!_textNotes.Any(note => string.Equals(note.Name, inReplyTo, StringComparison.Ordinal)))
                throw new ArgumentException(
                    $"The reply target '{inReplyTo}' must name an earlier text-note annotation.", nameof(inReplyTo));
        }
        else if (replyType != PdfAnnotationReplyType.Reply)
            throw new ArgumentException("A grouped reply requires an annotation target.", nameof(replyType));
        _textNotes.Add(new TextNoteDefinition(
            pageIndex, x, y, size, contents, color ?? PdfRgbColor.NoteYellow, open,
            annotationMetadata, icon, state, name, inReplyTo, replyType, popup));
        return this;
    }

    /// <summary>Adds a highlight annotation over a rectangular text region.</summary>
    public PdfDocumentBuilder AddHighlight(
        int pageIndex,
        double x,
        double y,
        double width,
        double height,
        string? contents = null,
        PdfRgbColor? color = null,
        double opacity = 0.35,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Highlight, pageIndex, x, y, width, height,
            contents, color ?? PdfRgbColor.Yellow, opacity, annotationMetadata);

    /// <summary>Adds a highlight annotation over one or more text quadrilaterals.</summary>
    public PdfDocumentBuilder AddHighlight(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 0.35,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Highlight, pageIndex, quads, contents,
            color ?? PdfRgbColor.Yellow, opacity, annotationMetadata);

    /// <summary>Adds an underline annotation over a rectangular text region.</summary>
    public PdfDocumentBuilder AddUnderline(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Underline, pageIndex, x, y, width, height,
            contents, color ?? new PdfRgbColor(0, 0.35, 0.9), opacity, annotationMetadata);

    /// <summary>Adds an underline annotation over one or more text quadrilaterals.</summary>
    public PdfDocumentBuilder AddUnderline(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Underline, pageIndex, quads, contents,
            color ?? new PdfRgbColor(0, 0.35, 0.9), opacity, annotationMetadata);

    /// <summary>Adds a strikeout annotation over a rectangular text region.</summary>
    public PdfDocumentBuilder AddStrikeOut(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.StrikeOut, pageIndex, x, y, width, height,
            contents, color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity, annotationMetadata);

    /// <summary>Adds a strikeout annotation over one or more text quadrilaterals.</summary>
    public PdfDocumentBuilder AddStrikeOut(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.StrikeOut, pageIndex, quads, contents,
            color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity, annotationMetadata);

    /// <summary>Adds a squiggly-underline annotation over a rectangular text region.</summary>
    public PdfDocumentBuilder AddSquiggly(
        int pageIndex, double x, double y, double width, double height,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Squiggly, pageIndex, x, y, width, height,
            contents, color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity, annotationMetadata);

    /// <summary>Adds a squiggly-underline annotation over one or more text quadrilaterals.</summary>
    public PdfDocumentBuilder AddSquiggly(
        int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents = null, PdfRgbColor? color = null, double opacity = 1,
        PdfAnnotationMetadata? annotationMetadata = null)
        => AddTextMarkup(PdfTextMarkupType.Squiggly, pageIndex, quads, contents,
            color ?? new PdfRgbColor(0.9, 0.1, 0.1), opacity, annotationMetadata);

    private PdfDocumentBuilder AddTextMarkup(
        PdfTextMarkupType type, int pageIndex, double x, double y, double width, double height,
        string? contents, PdfRgbColor color, double opacity,
        PdfAnnotationMetadata? annotationMetadata)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ValidateRectangle(x, y, width, height);
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        return AddTextMarkup(type, pageIndex, [PdfTextQuad.FromRectangle(x, y, width, height)],
            contents, color, opacity, annotationMetadata);
    }

    private PdfDocumentBuilder AddTextMarkup(
        PdfTextMarkupType type, int pageIndex, IReadOnlyList<PdfTextQuad> quads,
        string? contents, PdfRgbColor color, double opacity,
        PdfAnnotationMetadata? annotationMetadata)
    {
        ValidatePageIndex(pageIndex, nameof(pageIndex));
        ArgumentNullException.ThrowIfNull(quads);
        if (quads.Count == 0) throw new ArgumentException("At least one text quad is required.", nameof(quads));
        foreach (PdfTextQuad quad in quads) quad.Validate();
        if (!double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        _textMarkups.Add(new TextMarkupDefinition(
            type, pageIndex, [.. quads], contents, color, opacity, annotationMetadata));
        return this;
    }

    /// <summary>Validates the complete authored graph and serializes the PDF document.</summary>
    public byte[] Build()
    {
        bool pdfA4 = _pdfA4Flavor != PdfA4Flavor.None;
        if (pdfA4 && Version.CompareTo(PdfVersion.Pdf20) < 0)
            throw new InvalidOperationException("PDF/A-4 authoring requires PDF 2.0 or later.");
        if (_pdfUa2Conformance && Version.CompareTo(PdfVersion.Pdf20) < 0)
            throw new InvalidOperationException("PDF/UA-2 authoring requires PDF 2.0 or later.");
        if (_pages.Any(page => page.TabOrder == PdfPageTabOrder.AnnotationArray)
            && Version.CompareTo(PdfVersion.Pdf20) < 0)
            throw new InvalidOperationException(
                "Annotation-array page tab order requires PDF 2.0 or later.");
        if (_pages.Any(page => page.TabOrder.HasValue)
            && Version.CompareTo(new PdfVersion(1, 5)) < 0)
            throw new InvalidOperationException("Page tab ordering requires PDF 1.5 or later.");
        if (_pageLayout is PdfPageLayout.TwoPageLeft or PdfPageLayout.TwoPageRight
            && Version.CompareTo(new PdfVersion(1, 5)) < 0)
            throw new InvalidOperationException("Two-page layouts require PDF 1.5 or later.");
        if (_pageMode == PdfPageMode.UseOptionalContent
            && Version.CompareTo(new PdfVersion(1, 5)) < 0)
            throw new InvalidOperationException(
                "Optional-content page mode requires PDF 1.5 or later.");
        if (_pageMode == PdfPageMode.UseAttachments
            && Version.CompareTo(new PdfVersion(1, 6)) < 0)
            throw new InvalidOperationException(
                "Attachments page mode requires PDF 1.6 or later.");
        if (_viewerPreferences is not null)
            RequireVersion(_viewerPreferences.MinimumVersion(), "viewer preferences");
        if (pdfA4 && (_encryption is not null || _certificateEncryption is not null))
            throw new InvalidOperationException("PDF/A documents cannot be encrypted.");
        var forms = new List<PdfFormXObject>();
        var patterns = new List<PdfTilingPattern>();
        var knownForms = new HashSet<PdfFormXObject>();
        var knownPatterns = new HashSet<PdfTilingPattern>();
        void AddPattern(PdfTilingPattern pattern)
        {
            if (!knownPatterns.Add(pattern)) return;
            patterns.Add(pattern);
            foreach (PdfFormXObject form in pattern.Forms.Keys) AddForm(form);
            foreach (PdfTilingPattern nested in pattern.Patterns.Keys) AddPattern(nested);
            foreach (PdfGraphicsState state in pattern.GraphicsStates.Keys) AddGraphicsState(state);
        }
        void AddForm(PdfFormXObject form)
        {
            if (!knownForms.Add(form)) return;
            forms.Add(form);
            foreach (PdfFormXObject nested in form.Forms.Keys) AddForm(nested);
            foreach (PdfTilingPattern pattern in form.Patterns.Keys) AddPattern(pattern);
            foreach (PdfGraphicsState state in form.GraphicsStates.Keys) AddGraphicsState(state);
        }
        void AddGraphicsState(PdfGraphicsState state)
        {
            if (state.SoftMask is not null) AddForm(state.SoftMask.Group);
        }
        foreach (PdfFormXObject form in _pages.SelectMany(page => page.Forms.Keys)) AddForm(form);
        foreach (PdfTilingPattern pattern in _pages.SelectMany(page => page.Patterns.Keys)) AddPattern(pattern);
        foreach (PdfGraphicsState state in _pages.SelectMany(page => page.GraphicsStates.Keys))
            AddGraphicsState(state);
        if (Metadata is not null) RequireVersion(new PdfVersion(1, 4), "XMP metadata");
        if (_outputIntent is not null) RequireVersion(new PdfVersion(1, 4), "output intents");
        if (_structureElements.Count > 0) RequireVersion(new PdfVersion(1, 3), "structure trees");
        if (_pages.Any(page => page.UserUnit != 1))
            RequireVersion(new PdfVersion(1, 6), "page user units");
        if (_pageLabels.Count > 0)
            RequireVersion(new PdfVersion(1, 3), "page labels");
        if (_viewerPreferences is not null)
            RequireVersion(new PdfVersion(1, 2), "viewer preferences");
        if (_namedDestinations.Count > 0)
            RequireVersion(new PdfVersion(1, 2), "destination name trees");
        if (_pages.SelectMany(page => page.Links).Any(link => link is UriLinkDefinition))
            RequireVersion(new PdfVersion(1, 1), "URI actions");
        if (_pages.Any(page => page.Boxes.Keys.Any(box => box != PdfPageBox.Crop)))
            RequireVersion(new PdfVersion(1, 3), "print-production page boxes");
        if (_pages.Any(page => page.Transition is not null
                || page.DisplayDuration.HasValue))
            RequireVersion(new PdfVersion(1, 1), "page presentation timing");
        if (_pages.Any(page => page.Transition?.Style is
                PdfPageTransitionStyle.Replace or PdfPageTransitionStyle.Fly
                or PdfPageTransitionStyle.Push or PdfPageTransitionStyle.Cover
                or PdfPageTransitionStyle.Uncover or PdfPageTransitionStyle.Fade))
            RequireVersion(new PdfVersion(1, 5), "advanced page transitions");
        if (_attachments.Count > 0)
            RequireVersion(PdfVersion.Pdf20, "associated files");
        if (_encryption is not null)
            RequireVersion(PdfVersion.Pdf20, "revision-6 AES-256 encryption");
        if (_certificateEncryption is not null)
            RequireVersion(PdfVersion.Pdf20, "AES-256 certificate encryption");
        if (_textFields.Count > 0 || _checkBoxes.Count > 0 || _radioGroups.Count > 0
            || _choiceFields.Count > 0 || _pushButtons.Count > 0
            || _signatureFields.Count > 0)
            RequireVersion(new PdfVersion(1, 2), "interactive forms");
        if (_signatureFields.Count > 0)
            RequireVersion(new PdfVersion(1, 3), "signature fields");
        if (_freeTexts.Count > 0 || _textMarkups.Count > 0
            || _visualAnnotations.Count > 0)
            RequireVersion(new PdfVersion(1, 4), "opacity-aware markup annotations");
        if (_visualAnnotations.Any(annotation => annotation is VertexAnnotationDefinition)
            || _caretAnnotations.Count > 0)
            RequireVersion(new PdfVersion(1, 5), "vertex and caret annotations");
        if (_redactionAnnotations.Count > 0)
            RequireVersion(PdfVersion.Pdf17, "redaction annotations");
        if (_imageStamps.Count > 0
            || _textNotes.Any(note => note.Popup is not null))
            RequireVersion(new PdfVersion(1, 3), "stamp and popup annotations");
        if (_pages.Any(page => page.OptionalContentGroups.Count > 0)
            || forms.Any(form => form.OptionalContentGroups.Count > 0)
            || patterns.Any(pattern => pattern.OptionalContentGroups.Count > 0))
            RequireVersion(new PdfVersion(1, 5), "optional content");
        if (_pages.Any(page => page.GraphicsStates.Count > 0)
            || forms.Any(form => form.GraphicsStates.Count > 0)
            || patterns.Any(pattern => pattern.GraphicsStates.Count > 0))
            RequireVersion(new PdfVersion(1, 4), "extended graphics states");
        if (forms.Count > 0 || patterns.Count > 0)
            RequireVersion(new PdfVersion(1, 2), "form XObjects and tiling patterns");
        if (forms.Any(form => form.IsolatedTransparencyGroup
                || form.KnockoutTransparencyGroup))
            RequireVersion(new PdfVersion(1, 4), "transparency groups");
        if (_pages.Any(page => page.Shadings.Count > 0)
            || forms.Any(form => form.Shadings.Count > 0)
            || patterns.Any(pattern => pattern.Shadings.Count > 0))
            RequireVersion(new PdfVersion(1, 3), "shading resources");
        if (_pages.Any(page => page.IccColorSpaces.Count > 0)
            || forms.Any(form => form.IccColorSpaces.Count > 0)
            || patterns.Any(pattern => pattern.IccColorSpaces.Count > 0))
            RequireVersion(new PdfVersion(1, 3), "ICCBased color spaces");
        if (_pages.Any(page => page.LabColorSpaces.Count > 0
                || page.IndexedColorSpaces.Count > 0
                || page.CalibratedColorSpaces.Count > 0)
            || forms.Any(form => form.LabColorSpaces.Count > 0
                || form.IndexedColorSpaces.Count > 0
                || form.CalibratedColorSpaces.Count > 0)
            || patterns.Any(pattern => pattern.LabColorSpaces.Count > 0
                || pattern.IndexedColorSpaces.Count > 0
                || pattern.CalibratedColorSpaces.Count > 0))
            RequireVersion(new PdfVersion(1, 1), "calibrated, Lab, and Indexed color spaces");
        if (_pages.Any(page => page.SpotColors.Count > 0)
            || forms.Any(form => form.SpotColors.Count > 0)
            || patterns.Any(pattern => pattern.SpotColors.Count > 0))
            RequireVersion(new PdfVersion(1, 2), "Separation color spaces");
        if (_pages.Any(page => page.EmbeddedFonts.Count > 0)
            || forms.Any(form => form.EmbeddedFonts.Count > 0)
            || patterns.Any(pattern => pattern.EmbeddedFonts.Count > 0)
            || _textFields.Any(field => field.EmbeddedFont is not null)
            || _choiceFields.Any(field => field.EmbeddedFont is not null)
            || _pushButtons.Any(field => field.EmbeddedFont is not null)
            || _signatureFields.Any(field => field.EmbeddedFont is not null)
            || _freeTexts.Count > 0
            || _redactionAnnotations.Any(annotation => annotation.OverlayFont is not null))
            RequireVersion(new PdfVersion(1, 2), "embedded CID fonts");
        IEnumerable<PdfImage> authoredImages = _pages.SelectMany(page => page.Images.Keys)
            .Concat(_pages.Select(page => page.Thumbnail)
                .Where(image => image is not null).Cast<PdfImage>())
            .Concat(forms.SelectMany(form => form.Images.Keys))
            .Concat(patterns.SelectMany(pattern => pattern.Images.Keys))
            .Concat(_imageStamps.Select(stamp => stamp.Image))
            .Concat(_pushButtons.SelectMany(field => new[]
            {
                field.AppearanceOptions.Icon,
                field.AppearanceOptions.RolloverIcon,
                field.AppearanceOptions.DownIcon
            }).Where(image => image is not null).Cast<PdfImage>());
        if (authoredImages.Any(HasImageSoftMask))
            RequireVersion(new PdfVersion(1, 4), "image soft masks");
        if (_pdfUa2Conformance && (Metadata is null
            || string.IsNullOrWhiteSpace(Metadata.Title)
            || string.IsNullOrWhiteSpace(Metadata.Language)))
            throw new InvalidOperationException(
                "PDF/UA-2 authoring requires document metadata with a title and language.");
        if (_pdfUa2Conformance && _encryption is not null
            && !_encryption.AllowAccessibilityExtraction)
            throw new InvalidOperationException(
                "PDF/UA-2 encryption must permit accessibility extraction.");
        if (_pdfUa2Conformance && _certificateEncryption is not null
            && !_certificateEncryption.AllowAccessibilityExtraction)
            throw new InvalidOperationException(
                "PDF/UA-2 encryption must permit accessibility extraction.");
        if (_pdfUa2Conformance && _pages.Any(page =>
                page.TabOrder.HasValue && page.TabOrder != PdfPageTabOrder.Structure))
            throw new InvalidOperationException(
                "PDF/UA-2 authoring requires structure-order page tabs.");
        if (_pdfUa2Conformance && (_structureElements.Count == 0
            || _structureElements.Count(element => element.Level == 0) != 1
            || _structureElements[0].Type != PdfStructureType.Document))
            throw new InvalidOperationException(
                "PDF/UA-2 authoring requires one top-level Document structure element.");
        if (_pdfUa2Conformance && _structureElements.Any(element =>
                element.Type is PdfStructureType.Figure or PdfStructureType.Formula
                && element.AlternateDescription is null && element.ActualText is null))
            throw new InvalidOperationException(
                "PDF/UA-2 Figure and Formula structure elements require alternate or replacement text.");
        if (_pdfUa2Conformance)
            ValidatePdfUaStructureHierarchy(_structureElements);
        if (_pdfUa2Conformance)
        {
            IEnumerable<int> destinationPages = _namedDestinations.Select(value => value.PageIndex)
                .Concat(_openAction?.PageIndex is int openPage ? [openPage] : [])
                .Concat(_bookmarks.Where(value => value.PageIndex.HasValue)
                    .Select(value => value.PageIndex!.Value))
                .Concat(_pages.SelectMany(page => page.Links)
                    .OfType<PageLinkDefinition>()
                    .Select(value => value.DestinationPageIndex))
                .Concat(_pushButtons.Where(value => value.DestinationPageIndex.HasValue)
                    .Select(value => value.DestinationPageIndex!.Value));
            int? unstructuredDestinationPage = destinationPages.Distinct()
                .Where(pageIndex => !_structureElements.Any(
                    element => element.PageIndex == pageIndex))
                .Select(pageIndex => (int?)pageIndex)
                .FirstOrDefault();
            if (unstructuredDestinationPage.HasValue)
                throw new InvalidOperationException(
                    $"PDF/UA-2 navigation target page {unstructuredDestinationPage.Value} requires a page-associated structure element.");
        }
        if (_pdfUa2Conformance && (_pages.Any(page => page.Fonts.Count > 0)
            || forms.Any(form => form.Fonts.Count > 0)))
            throw new InvalidOperationException(
                "PDF/UA-2 authoring requires embedded fonts for page text.");
        if (_pdfUa2Conformance && (_textFields.Any(field => field.EmbeddedFont is null)
            || _choiceFields.Any(field => field.EmbeddedFont is null)
            || _pushButtons.Any(field => field.EmbeddedFont is null)
            || _signatureFields.Any(field =>
                field.AppearanceText is not null && field.EmbeddedFont is null)))
            throw new InvalidOperationException(
                "PDF/UA-2 text-bearing form appearances require embedded fonts.");
        if (_pdfUa2Conformance && _pages.Any(page => page.HasUntaggedContent))
            throw new InvalidOperationException(
                "PDF/UA-2 authoring requires all painted page content to be tagged or marked as an artifact.");
        if (_pdfUa2Conformance && _pages.SelectMany(page => page.Links)
            .Any(link => string.IsNullOrWhiteSpace(link.Contents)))
            throw new InvalidOperationException(
                "PDF/UA-2 link annotations require descriptive contents.");
        if (_pdfUa2Conformance && _textNotes.Any(note =>
                string.IsNullOrWhiteSpace(note.Contents)))
            throw new InvalidOperationException(
                "PDF/UA-2 text annotations require descriptive contents.");
        if (_pdfUa2Conformance && _textMarkups.Any(markup =>
                string.IsNullOrWhiteSpace(markup.Contents)))
            throw new InvalidOperationException(
                "PDF/UA-2 text-markup annotations require descriptive contents.");
        if (_pdfUa2Conformance && (_freeTexts.Any(annotation =>
                string.IsNullOrWhiteSpace(annotation.Contents))
            || _visualAnnotations.Any(annotation =>
                string.IsNullOrWhiteSpace(annotation.Contents))
            || _imageStamps.Any(annotation =>
                string.IsNullOrWhiteSpace(annotation.Contents))
            || _caretAnnotations.Any(annotation =>
                string.IsNullOrWhiteSpace(annotation.Contents))
            || _redactionAnnotations.Any(annotation =>
                string.IsNullOrWhiteSpace(annotation.Contents))))
            throw new InvalidOperationException(
                "PDF/UA-2 visual and editorial annotations require descriptive contents.");
        if (_pdfUa2Conformance && _fileAttachmentAnnotations.Any(annotation =>
                string.IsNullOrWhiteSpace(annotation.Contents)))
            throw new InvalidOperationException(
                "PDF/UA-2 file-attachment annotations require descriptive contents.");
        if (_pdfUa2Conformance && _attachments.Any(attachment =>
                string.IsNullOrWhiteSpace(attachment.Description)))
            throw new InvalidOperationException(
                "PDF/UA-2 embedded files require descriptive file-specification metadata.");
        if (_pdfUa2Conformance && _textFields.Select(field => field.Metadata)
            .Concat(_checkBoxes.Select(field => field.Metadata))
            .Concat(_radioGroups.Select(field => field.Metadata))
            .Concat(_choiceFields.Select(field => field.Metadata))
            .Concat(_pushButtons.Select(field => field.Metadata))
            .Concat(_signatureFields.Select(field => field.Metadata))
            .Any(metadata => string.IsNullOrWhiteSpace(metadata?.Tooltip)))
            throw new InvalidOperationException(
                "PDF/UA-2 form fields require descriptive tooltips.");
        for (int pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            var registered = _structureElements.Where(element => element.PageIndex == pageIndex)
                .Select(element => element.MarkedContentId!.Value).ToHashSet();
            if (_pages[pageIndex].MarkedContentIds.Any(id => !registered.Contains(id)))
                throw new InvalidOperationException(
                    $"Page {pageIndex} contains marked content without a structure element.");
        }
        if (pdfA4 && Metadata is null)
            throw new InvalidOperationException("PDF/A-4 authoring requires XMP document metadata.");
        if (pdfA4 && _outputIntent is null)
            throw new InvalidOperationException("PDF/A-4 authoring requires an ICC output intent.");
        if (pdfA4 && _outputIntent?.Profile.ComponentCount == 4
            && _pages.SelectMany(page => page.IccColorSpaces.Keys)
                .Concat(forms.SelectMany(form => form.IccColorSpaces.Keys))
                .Concat(patterns.SelectMany(pattern => pattern.IccColorSpaces.Keys))
                .Any(profile => profile.ComponentCount == 4
                    && profile.Data.Span.SequenceEqual(_outputIntent.Profile.Data.Span)))
            throw new InvalidOperationException(
                "PDF/A-4 forbids an ICCBased CMYK content space identical to the output-intent profile; use DeviceCMYK or a distinct source profile.");
        if (pdfA4 && (_pages.Any(page => page.Fonts.Count > 0)
            || forms.Any(form => form.Fonts.Count > 0)))
            throw new InvalidOperationException("PDF/A-4 authoring requires embedded fonts; the 14 built-in PDF fonts are not embedded.");
        if (pdfA4 && (_textFields.Any(field => field.EmbeddedFont is null)
            || _choiceFields.Any(field => field.EmbeddedFont is null)
            || _pushButtons.Any(field => field.EmbeddedFont is null)
            || _signatureFields.Any(field =>
                field.AppearanceText is not null && field.EmbeddedFont is null)))
            throw new InvalidOperationException(
                "PDF/A-4 text and choice fields require an embedded TrueType form font.");
        if (pdfA4 && _pushButtons.Count > 0)
            throw new InvalidOperationException(
                "PDF/A-4 widget annotations cannot contain the action required by a push button.");
        if (pdfA4 && _redactionAnnotations.Any(annotation =>
            annotation.OverlayText is not null && annotation.OverlayFont is null))
            throw new InvalidOperationException(
                "PDF/A-4 redaction overlay text requires an embedded TrueType font.");
        if (_pdfA4Flavor == PdfA4Flavor.General && _attachments.Count > 0)
            throw new InvalidOperationException("General PDF/A-4 does not permit attachments; enable PDF/A-4f conformance instead.");
        const int catalogNumber = 1;
        const int pagesNumber = 2;
        int nextObjectNumber = 3;
        int? metadataNumber = Metadata is null ? null : nextObjectNumber++;
        int? infoNumber = Metadata is null || pdfA4 ? null : nextObjectNumber++;
        int? outlinesNumber = _bookmarks.Count == 0 ? null : nextObjectNumber++;
        int[] bookmarkNumbers = [.. _bookmarks.Select(_ => nextObjectNumber++)];
        int? structureRootNumber = _structureElements.Count == 0 ? null : nextObjectNumber++;
        int? parentTreeNumber = _structureElements.Count == 0 ? null : nextObjectNumber++;
        int? structureNamespaceNumber = _pdfUa2Conformance ? nextObjectNumber++ : null;
        int? legacyStructureNamespaceNumber = _pdfUa2Conformance
            && _structureElements.Any(element =>
                PdfStructureTypeNames.UsesPdf17Namespace(element.Type, _pdfUa2Conformance))
            ? nextObjectNumber++ : null;
        int[] structureElementNumbers = [.. _structureElements.Select(_ => nextObjectNumber++)];
        int[] accessibleLinkStructureNumbers = _pdfUa2Conformance
            ? [.. _pages.SelectMany(page => page.Links).Select(_ => nextObjectNumber++)]
            : [];
        int accessibleWidgetCount = _textFields.Count + _checkBoxes.Count
            + _radioGroups.Sum(group => group.Options.Count) + _choiceFields.Count
            + _pushButtons.Count + _signatureFields.Count;
        int[] accessibleWidgetStructureNumbers = _pdfUa2Conformance
            ? [.. Enumerable.Range(0, accessibleWidgetCount).Select(_ => nextObjectNumber++)]
            : [];
        int[] accessibleTextNoteStructureNumbers = _pdfUa2Conformance
            ? [.. _textNotes.Select(_ => nextObjectNumber++)]
            : [];
        int[] accessibleTextMarkupStructureNumbers = _pdfUa2Conformance
            ? [.. _textMarkups.Select(_ => nextObjectNumber++)]
            : [];
        int accessibleEditorialCount = _freeTexts.Count + _visualAnnotations.Count
            + _imageStamps.Count + _caretAnnotations.Count + _redactionAnnotations.Count;
        int[] accessibleEditorialStructureNumbers = _pdfUa2Conformance
            ? [.. Enumerable.Range(0, accessibleEditorialCount).Select(_ => nextObjectNumber++)]
            : [];
        int[] accessibleFileAttachmentStructureNumbers = _pdfUa2Conformance
            ? [.. _fileAttachmentAnnotations.Select(_ => nextObjectNumber++)]
            : [];
        var allocatedAttachments = _attachments.Select(attachment =>
            new AllocatedAttachment(attachment, nextObjectNumber++, nextObjectNumber++)).ToArray();
        IReadOnlyDictionary<string, int> attachmentFileSpecificationNumbers = allocatedAttachments
            .ToDictionary(attachment => attachment.Definition.FileName,
                attachment => attachment.FileSpecificationNumber, StringComparer.OrdinalIgnoreCase);
        var allocatedFileAttachmentAnnotations = _fileAttachmentAnnotations.Select(annotation =>
            new AllocatedFileAttachmentAnnotation(
                annotation, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedTextFields = _textFields.Select(field =>
            new AllocatedTextField(field, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedCheckBoxes = _checkBoxes.Select(field =>
            new AllocatedCheckBox(field,
                nextObjectNumber++, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedRadioGroups = new List<AllocatedRadioGroup>();
        foreach (RadioGroupDefinition group in _radioGroups)
        {
            int parentNumber = nextObjectNumber++;
            var widgets = group.Options.Select(option => new AllocatedRadioWidget(
                option, nextObjectNumber++, nextObjectNumber++, nextObjectNumber++)).ToArray();
            allocatedRadioGroups.Add(new AllocatedRadioGroup(group, parentNumber, widgets));
        }
        var allocatedChoiceFields = _choiceFields.Select(field =>
            new AllocatedChoiceField(field, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedPushButtons = _pushButtons.Select(field =>
            new AllocatedPushButton(
                field, nextObjectNumber++, nextObjectNumber++,
                field.AppearanceOptions.RolloverLabel is null
                    && field.AppearanceOptions.RolloverIcon is null ? null : nextObjectNumber++,
                field.AppearanceOptions.DownLabel is null
                    && field.AppearanceOptions.DownIcon is null ? null : nextObjectNumber++)).ToArray();
        var allocatedSignatureFields = _signatureFields.Select(field =>
            new AllocatedSignatureField(field, nextObjectNumber++, nextObjectNumber++,
                field.FieldLock is null ? null : nextObjectNumber++,
                field.SeedValue is null ? null : nextObjectNumber++)).ToArray();
        AllocatedFieldHierarchy fieldHierarchy = AllocateFieldHierarchy(
            allocatedTextFields.Select(field => (field.Definition.Name, field.FieldNumber))
                .Concat(allocatedCheckBoxes.Select(field =>
                    (field.Definition.Name, field.FieldNumber)))
                .Concat(allocatedRadioGroups.Select(field =>
                    (field.Definition.Name, field.ParentNumber)))
                .Concat(allocatedChoiceFields.Select(field =>
                    (field.Definition.Name, field.FieldNumber)))
                .Concat(allocatedPushButtons.Select(field =>
                    (field.Definition.Name, field.FieldNumber)))
                .Concat(allocatedSignatureFields.Select(field =>
                    (field.Definition.Name, field.FieldNumber))),
            ref nextObjectNumber);
        var allocatedTextNotes = _textNotes.Select(note =>
            new AllocatedTextNote(note, nextObjectNumber++, nextObjectNumber++,
                note.Popup is null ? null : nextObjectNumber++)).ToArray();
        IReadOnlyDictionary<string, int> textNoteNumbersByName = allocatedTextNotes
            .Where(note => note.Definition.Name is not null)
            .ToDictionary(note => note.Definition.Name!, note => note.AnnotationNumber,
                StringComparer.Ordinal);
        var allocatedTextMarkups = _textMarkups.Select(markup =>
            new AllocatedTextMarkup(markup, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedCaretAnnotations = _caretAnnotations.Select(annotation =>
            new AllocatedCaretAnnotation(annotation, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedRedactionAnnotations = _redactionAnnotations.Select(annotation =>
            new AllocatedRedactionAnnotation(annotation, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedFreeTexts = _freeTexts.Select(freeText =>
            new AllocatedFreeText(freeText, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedVisualAnnotations = _visualAnnotations.Select(annotation =>
            new AllocatedVisualAnnotation(annotation, nextObjectNumber++, nextObjectNumber++)).ToArray();
        var allocatedImageStamps = _imageStamps.Select(stamp =>
            new AllocatedImageStamp(stamp, nextObjectNumber++, nextObjectNumber++)).ToArray();
        PdfIccProfile[] authoredIccProfiles = [.. _pages.SelectMany(page => page.IccColorSpaces.Keys)
            .Concat(forms.SelectMany(form => form.IccColorSpaces.Keys))
            .Concat(patterns.SelectMany(pattern => pattern.IccColorSpaces.Keys))
            .Distinct()];
        PdfIccProfile[] allIccProfiles = [.. (_outputIntent is null
                ? authoredIccProfiles
                : authoredIccProfiles.Append(_outputIntent.Profile).Distinct())];
        var iccProfileNumbers = allIccProfiles
            .ToDictionary(profile => profile, _ => nextObjectNumber++);
        int? iccProfileNumber = _outputIntent is null
            ? null : iccProfileNumbers[_outputIntent.Profile];
        int? outputIntentNumber = _outputIntent is null ? null : nextObjectNumber++;
        PdfOptionalContentGroup[] optionalContentGroups = [.. _pages
            .SelectMany(page => page.OptionalContentGroups.Keys)
            .Concat(forms.SelectMany(form => form.OptionalContentGroups.Keys))
            .Concat(patterns.SelectMany(pattern => pattern.OptionalContentGroups.Keys))
            .Distinct()
            .OrderBy(group => group.Name, StringComparer.Ordinal)];
        string? duplicateLayerName = optionalContentGroups
            .GroupBy(group => group.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateLayerName is not null)
            throw new InvalidOperationException(
                $"Optional-content group name '{duplicateLayerName}' is used by more than one group.");
        var optionalContentNumbers = optionalContentGroups
            .ToDictionary(group => group, _ => nextObjectNumber++);
        PdfGraphicsState[] graphicsStates = [.. _pages
            .SelectMany(page => page.GraphicsStates.Keys)
            .Concat(forms.SelectMany(form => form.GraphicsStates.Keys))
            .Concat(patterns.SelectMany(pattern => pattern.GraphicsStates.Keys))
            .Distinct()
            .OrderBy(state => state.FillOpacity)
            .ThenBy(state => state.StrokeOpacity)
            .ThenBy(state => state.BlendMode)];
        var graphicsStateNumbers = graphicsStates
            .ToDictionary(state => state, _ => nextObjectNumber++);
        PdfShading[] shadings = [.. _pages.SelectMany(page => page.Shadings.Keys)
            .Concat(forms.SelectMany(form => form.Shadings.Keys))
            .Concat(patterns.SelectMany(pattern => pattern.Shadings.Keys))
            .Distinct()];
        var shadingNumbers = shadings.ToDictionary(shading => shading, _ => nextObjectNumber++);
        var formNumbers = forms.ToDictionary(form => form, _ => nextObjectNumber++);
        var patternNumbers = patterns.ToDictionary(pattern => pattern, _ => nextObjectNumber++);
        TrueTypeFont[] formEmbeddedFonts = [.. _textFields.Select(field => field.EmbeddedFont)
            .Concat(_choiceFields.Select(field => field.EmbeddedFont))
            .Concat(_pushButtons.Select(field => field.EmbeddedFont))
            .Concat(_signatureFields.Select(field => field.EmbeddedFont))
            .Concat(_freeTexts.Select(freeText => (TrueTypeFont?)freeText.Font))
            .Concat(_redactionAnnotations.Select(redaction => redaction.OverlayFont))
            .Where(font => font is not null).Cast<TrueTypeFont>().Distinct()];
        var formFontResources = formEmbeddedFonts.Select((font, index) => (font, index))
            .ToDictionary(item => item.font,
                item => new PdfName(Encoding.ASCII.GetBytes($"FormF{item.index + 1}")));
        var formFontUsages = new List<EmbeddedFontUsage>();
        foreach (TrueTypeFont font in formEmbeddedFonts)
        {
            var usage = new EmbeddedFontUsage(font, formFontResources[font]);
            foreach (string value in _textFields.Where(field => ReferenceEquals(field.EmbeddedFont, font))
                .SelectMany(field => new[] { TextFieldAppearanceValue(field), field.DefaultValue })
                .Concat(_choiceFields.Where(field => ReferenceEquals(field.EmbeddedFont, font))
                    .SelectMany(field => field.Options.Select(option => option.DisplayValue)
                        .Concat(field.SelectedValues.Select(value => ChoiceDisplayValue(field, value)))
                        .Concat(field.DefaultSelectedValues.Select(value =>
                            ChoiceDisplayValue(field, value)))))
                .Concat(_pushButtons.Where(field => ReferenceEquals(field.EmbeddedFont, font))
                    .SelectMany(field => new[]
                    {
                        field.Label,
                        field.AppearanceOptions.RolloverLabel,
                        field.AppearanceOptions.DownLabel
                    }.Where(value => value is not null).Cast<string>()))
                .Concat(_signatureFields.Where(field =>
                        ReferenceEquals(field.EmbeddedFont, font) && field.AppearanceText is not null)
                    .Select(field => field.AppearanceText!))
                .Concat(_freeTexts.Where(freeText => ReferenceEquals(freeText.Font, font))
                    .Select(freeText => freeText.Contents))
                .Concat(_redactionAnnotations
                    .Where(redaction => ReferenceEquals(redaction.OverlayFont, font)
                        && redaction.OverlayText is not null)
                    .Select(redaction => redaction.OverlayText!)))
                AddDrawableTextMappings(usage, value);
            formFontUsages.Add(usage);
        }
        var fontNumbers = new Dictionary<PdfStandardFont, int>();
        IEnumerable<PdfStandardFont> requestedStandardFonts = _pages.SelectMany(page => page.Fonts.Keys)
            .Concat(forms.SelectMany(form => form.Fonts.Keys))
            .Concat(patterns.SelectMany(pattern => pattern.Fonts.Keys));
        if (_textFields.Any(field => field.EmbeddedFont is null)
            || _choiceFields.Any(field => field.EmbeddedFont is null)
            || _pushButtons.Any(field => field.EmbeddedFont is null)
            || _signatureFields.Any(field =>
                field.AppearanceText is not null && field.EmbeddedFont is null)
            || _redactionAnnotations.Any(annotation =>
                annotation.OverlayText is not null && annotation.OverlayFont is null))
            requestedStandardFonts = requestedStandardFonts.Append(PdfStandardFont.Helvetica);
        foreach (PdfStandardFont font in requestedStandardFonts.Distinct().Order())
            fontNumbers.Add(font, nextObjectNumber++);
        var embeddedFontUsageGroups = new List<List<EmbeddedFontUsage>>();
        foreach (EmbeddedFontUsage usage in
            _pages.SelectMany(page => page.EmbeddedFonts)
                .Concat(forms.SelectMany(form => form.EmbeddedFonts))
                .Concat(patterns.SelectMany(pattern => pattern.EmbeddedFonts))
                .Concat(formFontUsages))
        {
            List<EmbeddedFontUsage>? compatible = embeddedFontUsageGroups.FirstOrDefault(group =>
                ReferenceEquals(group[0].Font, usage.Font)
                && EmbeddedMappingsEqual(group[0].Mappings, usage.Mappings));
            if (compatible is null)
                embeddedFontUsageGroups.Add([usage]);
            else
                compatible.Add(usage);
        }
        var embeddedFonts = new List<AllocatedEmbeddedFont>();
        foreach (List<EmbeddedFontUsage> usages in embeddedFontUsageGroups)
        {
            EmbeddedFontUsage usage = usages[0];
            embeddedFonts.Add(new AllocatedEmbeddedFont(
                usages, usage.Mappings,
                nextObjectNumber++, nextObjectNumber++, nextObjectNumber++,
                nextObjectNumber++, nextObjectNumber++, nextObjectNumber++));
        }
        var imageNumbers = new Dictionary<PdfImage, int>();
        foreach (PdfImage image in _pages.SelectMany(page => page.Images.Keys)
            .Concat(forms.SelectMany(form => form.Images.Keys))
            .Concat(patterns.SelectMany(pattern => pattern.Images.Keys))
            .Concat(_pages.Select(page => page.Thumbnail).Where(image => image is not null).Cast<PdfImage>())
            .Concat(_pushButtons.SelectMany(field => new[]
                {
                    field.AppearanceOptions.Icon,
                    field.AppearanceOptions.RolloverIcon,
                    field.AppearanceOptions.DownIcon
                })
                .Where(image => image is not null).Cast<PdfImage>())
            .Concat(_imageStamps.Select(stamp => stamp.Image)).Distinct())
            AddImageAndMask(image);
        void AddImageAndMask(PdfImage image)
        {
            if (imageNumbers.ContainsKey(image))
                return;
            imageNumbers.Add(image, nextObjectNumber++);
            if (image.SoftMask is not null)
                AddImageAndMask(image.SoftMask);
        }
        var allocated = new List<AllocatedPage>(_pages.Count);
        for (int pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            PageDefinition page = _pages[pageIndex];
            int pageNumber = nextObjectNumber++;
            int? contentNumber = page.Content.Length == 0 ? null : nextObjectNumber++;
            int[] annotationNumbers = [
                .. page.Links.Select(_ => nextObjectNumber++),
                .. allocatedTextFields.Where(field => field.Definition.PageIndex == pageIndex)
                    .Select(field => field.FieldNumber),
                .. allocatedCheckBoxes.Where(field => field.Definition.PageIndex == pageIndex)
                    .Select(field => field.FieldNumber),
                .. allocatedRadioGroups.SelectMany(group => group.Widgets)
                    .Where(widget => widget.Option.PageIndex == pageIndex)
                    .Select(widget => widget.WidgetNumber),
                .. allocatedChoiceFields.Where(field => field.Definition.PageIndex == pageIndex)
                    .Select(field => field.FieldNumber),
                .. allocatedPushButtons.Where(field => field.Definition.PageIndex == pageIndex)
                    .Select(field => field.FieldNumber),
                .. allocatedSignatureFields.Where(field => field.Definition.PageIndex == pageIndex)
                    .Select(field => field.FieldNumber),
                .. allocatedTextNotes.Where(note => note.Definition.PageIndex == pageIndex)
                    .SelectMany(note => note.PopupNumber is null
                        ? [note.AnnotationNumber]
                        : new[] { note.AnnotationNumber, note.PopupNumber.Value }),
                .. allocatedFileAttachmentAnnotations
                    .Where(annotation => annotation.Definition.PageIndex == pageIndex)
                    .Select(annotation => annotation.AnnotationNumber),
                .. allocatedTextMarkups.Where(markup => markup.Definition.PageIndex == pageIndex)
                    .Select(markup => markup.AnnotationNumber),
                .. allocatedCaretAnnotations.Where(annotation => annotation.Definition.PageIndex == pageIndex)
                    .Select(annotation => annotation.AnnotationNumber),
                .. allocatedRedactionAnnotations.Where(annotation => annotation.Definition.PageIndex == pageIndex)
                    .Select(annotation => annotation.AnnotationNumber),
                .. allocatedFreeTexts.Where(freeText => freeText.Definition.PageIndex == pageIndex)
                    .Select(freeText => freeText.AnnotationNumber),
                .. allocatedVisualAnnotations.Where(annotation => annotation.Definition.PageIndex == pageIndex)
                    .Select(annotation => annotation.AnnotationNumber),
                .. allocatedImageStamps.Where(stamp => stamp.Definition.PageIndex == pageIndex)
                    .Select(stamp => stamp.AnnotationNumber)];
            allocated.Add(new AllocatedPage(page, pageNumber, contentNumber, annotationNumbers));
        }
        int pageStructureKeyCount = _structureElements
            .Where(element => element.PageIndex.HasValue)
            .Select(element => element.PageIndex!.Value)
            .Distinct().Count();
        int nextStructureParentKey = pageStructureKeyCount;
        int AllocateStructureParentKey()
        {
            int key = nextStructureParentKey;
            nextStructureParentKey = checked(nextStructureParentKey + 1);
            return key;
        }
        var accessibleLinks = new List<AccessibleAnnotationStructure>();
        int accessibleLinkIndex = 0;
        if (_pdfUa2Conformance)
        {
            for (int pageIndex = 0; pageIndex < allocated.Count; pageIndex++)
            {
                for (int linkIndex = 0;
                    linkIndex < allocated[pageIndex].Definition.Links.Count; linkIndex++)
                {
                    LinkDefinition link = allocated[pageIndex].Definition.Links[linkIndex];
                    accessibleLinks.Add(new AccessibleAnnotationStructure(
                        pageIndex,
                        allocated[pageIndex].AnnotationNumbers[linkIndex],
                        accessibleLinkStructureNumbers[accessibleLinkIndex],
                        AllocateStructureParentKey(),
                        "Link", link.Contents!));
                    accessibleLinkIndex++;
                }
            }
        }
        var accessibleWidgets = new List<AccessibleAnnotationStructure>();
        int accessibleWidgetIndex = 0;
        if (_pdfUa2Conformance)
        {
            foreach (AllocatedTextField field in allocatedTextFields)
                AddWidget(field.Definition.PageIndex, field.FieldNumber,
                    field.Definition.Metadata!.Tooltip!);
            foreach (AllocatedCheckBox field in allocatedCheckBoxes)
                AddWidget(field.Definition.PageIndex, field.FieldNumber,
                    field.Definition.Metadata!.Tooltip!);
            foreach (AllocatedRadioGroup group in allocatedRadioGroups)
                foreach (AllocatedRadioWidget widget in group.Widgets)
                    AddWidget(widget.Option.PageIndex, widget.WidgetNumber,
                        group.Definition.Metadata!.Tooltip!);
            foreach (AllocatedChoiceField field in allocatedChoiceFields)
                AddWidget(field.Definition.PageIndex, field.FieldNumber,
                    field.Definition.Metadata!.Tooltip!);
            foreach (AllocatedPushButton field in allocatedPushButtons)
                AddWidget(field.Definition.PageIndex, field.FieldNumber,
                    field.Definition.Metadata!.Tooltip!);
            foreach (AllocatedSignatureField field in allocatedSignatureFields)
                AddWidget(field.Definition.PageIndex, field.FieldNumber,
                    field.Definition.Metadata!.Tooltip!);

            void AddWidget(int pageIndex, int annotationNumber, string description)
            {
                accessibleWidgets.Add(new AccessibleAnnotationStructure(
                    pageIndex, annotationNumber,
                    accessibleWidgetStructureNumbers[accessibleWidgetIndex],
                    AllocateStructureParentKey(),
                    "Form", description));
                accessibleWidgetIndex++;
            }
        }
        var accessibleTextNotes = new List<AccessibleAnnotationStructure>();
        if (_pdfUa2Conformance)
        {
            for (int index = 0; index < allocatedTextNotes.Length; index++)
            {
                AllocatedTextNote note = allocatedTextNotes[index];
                accessibleTextNotes.Add(new AccessibleAnnotationStructure(
                    note.Definition.PageIndex,
                    note.AnnotationNumber,
                    accessibleTextNoteStructureNumbers[index],
                    AllocateStructureParentKey(),
                    "Annot", note.Definition.Contents));
            }
        }
        var accessibleTextMarkups = new List<AccessibleAnnotationStructure>();
        if (_pdfUa2Conformance)
        {
            for (int index = 0; index < allocatedTextMarkups.Length; index++)
            {
                AllocatedTextMarkup markup = allocatedTextMarkups[index];
                accessibleTextMarkups.Add(new AccessibleAnnotationStructure(
                    markup.Definition.PageIndex,
                    markup.AnnotationNumber,
                    accessibleTextMarkupStructureNumbers[index],
                    AllocateStructureParentKey(),
                    "Annot", markup.Definition.Contents!));
            }
        }
        var accessibleEditorialAnnotations = new List<AccessibleAnnotationStructure>();
        if (_pdfUa2Conformance)
        {
            foreach (AllocatedFreeText annotation in allocatedFreeTexts)
                AddEditorial(annotation.Definition.PageIndex, annotation.AnnotationNumber,
                    annotation.Definition.Contents);
            foreach (AllocatedVisualAnnotation annotation in allocatedVisualAnnotations)
                AddEditorial(annotation.Definition.PageIndex, annotation.AnnotationNumber,
                    annotation.Definition.Contents!);
            foreach (AllocatedImageStamp annotation in allocatedImageStamps)
                AddEditorial(annotation.Definition.PageIndex, annotation.AnnotationNumber,
                    annotation.Definition.Contents!);
            foreach (AllocatedCaretAnnotation annotation in allocatedCaretAnnotations)
                AddEditorial(annotation.Definition.PageIndex, annotation.AnnotationNumber,
                    annotation.Definition.Contents!);
            foreach (AllocatedRedactionAnnotation annotation in allocatedRedactionAnnotations)
                AddEditorial(annotation.Definition.PageIndex, annotation.AnnotationNumber,
                    annotation.Definition.Contents!);

            void AddEditorial(int pageIndex, int annotationNumber, string description)
            {
                int index = accessibleEditorialAnnotations.Count;
                accessibleEditorialAnnotations.Add(new AccessibleAnnotationStructure(
                    pageIndex, annotationNumber,
                    accessibleEditorialStructureNumbers[index],
                    AllocateStructureParentKey(),
                    "Annot", description));
            }
        }
        var accessibleFileAttachments = new List<AccessibleAnnotationStructure>();
        if (_pdfUa2Conformance)
        {
            for (int index = 0; index < allocatedFileAttachmentAnnotations.Length; index++)
            {
                AllocatedFileAttachmentAnnotation annotation =
                    allocatedFileAttachmentAnnotations[index];
                accessibleFileAttachments.Add(new AccessibleAnnotationStructure(
                    annotation.Definition.PageIndex,
                    annotation.AnnotationNumber,
                    accessibleFileAttachmentStructureNumbers[index],
                    AllocateStructureParentKey(),
                    "Annot", annotation.Definition.Contents!));
            }
        }
        AccessibleAnnotationStructure[] accessibleAnnotations =
            [.. accessibleLinks, .. accessibleWidgets, .. accessibleTextNotes,
                .. accessibleTextMarkups, .. accessibleEditorialAnnotations,
                .. accessibleFileAttachments];
        IReadOnlyDictionary<int, AccessibleAnnotationStructure> accessibleByAnnotation =
            accessibleAnnotations.ToDictionary(annotation => annotation.AnnotationNumber);
        PdfIndirectReference DestinationReference(int pageIndex)
        {
            if (!_pdfUa2Conformance)
                return new PdfIndirectReference(allocated[pageIndex].PageNumber, 0);
            int structureIndex = _structureElements.FindIndex(
                element => element.PageIndex == pageIndex);
            return new PdfIndirectReference(structureElementNumbers[structureIndex], 0);
        }

        var kids = new PdfArray(allocated.Select(page =>
            (PdfObject)new PdfIndirectReference(page.PageNumber, 0)));
        var catalogEntries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Catalog")),
            ("Pages", new PdfIndirectReference(pagesNumber, 0))
        };
        if (metadataNumber.HasValue)
            catalogEntries.Add(("Metadata", new PdfIndirectReference(metadataNumber.Value, 0)));
        if (!string.IsNullOrWhiteSpace(Metadata?.Language))
            catalogEntries.Add(("Lang", UnicodeString(Metadata.Language)));
        if (outlinesNumber.HasValue)
        {
            catalogEntries.Add(("Outlines", new PdfIndirectReference(outlinesNumber.Value, 0)));
        }
        PdfPageMode? effectivePageMode = _pageMode
            ?? (outlinesNumber.HasValue ? PdfPageMode.UseOutlines : null);
        if (effectivePageMode.HasValue)
            catalogEntries.Add(("PageMode", Name(PageModeName(effectivePageMode.Value))));
        if (_pageLayout.HasValue)
            catalogEntries.Add(("PageLayout", Name(PageLayoutName(_pageLayout.Value))));
        if (_openAction is not null)
            catalogEntries.Add(("OpenAction", _openAction.NamedDestination is not null
                ? UnicodeString(_openAction.NamedDestination)
                : _openAction.Destination!.ToArray(
                    DestinationReference(_openAction.PageIndex!.Value))));
        if (structureRootNumber.HasValue)
        {
            catalogEntries.Add(("StructTreeRoot",
                new PdfIndirectReference(structureRootNumber.Value, 0)));
            catalogEntries.Add(("MarkInfo", Dictionary(("Marked", new PdfBoolean(true)))));
        }
        if (_viewerPreferences is not null || _pdfUa2Conformance)
            catalogEntries.Add(("ViewerPreferences",
                (_viewerPreferences ?? new PdfViewerPreferences())
                    .ToDictionary(_pdfUa2Conformance)));
        var catalogNameEntries = new List<(string Name, PdfObject Value)>();
        if (allocatedAttachments.Length > 0)
        {
            if (allocatedAttachments.Length > PdfNameTree.MaximumEntryCount)
                throw new NotSupportedException(
                    "The embedded-files name tree contains too many entries.");
            var names = new List<PdfObject>();
            foreach (AllocatedAttachment attachment in allocatedAttachments
                .OrderBy(value => value.Definition.FileName, StringComparer.Ordinal))
            {
                names.Add(UnicodeString(attachment.Definition.FileName));
                names.Add(new PdfIndirectReference(attachment.FileSpecificationNumber, 0));
            }
            catalogNameEntries.Add(
                ("EmbeddedFiles", Dictionary(("Names", new PdfArray(names)))));
            catalogEntries.Add(("AF", new PdfArray(allocatedAttachments.Select(attachment =>
                (PdfObject)new PdfIndirectReference(attachment.FileSpecificationNumber, 0)))));
        }
        if (_namedDestinations.Count > 0)
        {
            if (_namedDestinations.Count > PdfNameTree.MaximumEntryCount)
                throw new NotSupportedException(
                    "The named-destination tree contains too many entries.");
            var names = new List<PdfObject>();
            foreach (NamedDestinationDefinition destination in _namedDestinations
                .OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                names.Add(UnicodeString(destination.Name));
                names.Add(DestinationArray(
                    DestinationReference(destination.PageIndex),
                    destination.Destination));
            }
            catalogNameEntries.Add(("Dests", Dictionary(("Names", new PdfArray(names)))));
        }
        if (catalogNameEntries.Count > 0)
            catalogEntries.Add(("Names", Dictionary([.. catalogNameEntries])));
        if (_pageLabels.Count > 0)
        {
            if (_pageLabels.Count > PdfNumberTree.MaximumEntryCount)
                throw new NotSupportedException(
                    "The page-label number tree contains too many entries.");
            var numbers = new List<PdfObject>();
            foreach (PageLabelDefinition label in _pageLabels.OrderBy(value => value.PageIndex))
            {
                var entries = new List<(string Name, PdfObject Value)>();
                PdfName? style = PageLabelName(label.Style);
                if (style is not null) entries.Add(("S", style));
                if (!string.IsNullOrEmpty(label.Prefix))
                    entries.Add(("P", UnicodeString(label.Prefix)));
                if (label.Style != PdfPageLabelStyle.None && label.StartNumber != 1)
                    entries.Add(("St", new PdfInteger(label.StartNumber)));
                numbers.Add(new PdfInteger(label.PageIndex));
                numbers.Add(Dictionary([.. entries]));
            }
            catalogEntries.Add(("PageLabels", Dictionary(("Nums", new PdfArray(numbers)))));
        }
        if (allocatedTextFields.Length > 0 || allocatedCheckBoxes.Length > 0
            || allocatedRadioGroups.Count > 0 || allocatedChoiceFields.Length > 0
            || allocatedPushButtons.Length > 0 || allocatedSignatureFields.Length > 0)
        {
            var fieldReferences = fieldHierarchy.RootFieldNumbers
                .Select(number => (PdfObject)new PdfIndirectReference(number, 0));
            var formEntries = new List<(string Name, PdfObject Value)>
            {
                ("Fields", new PdfArray(fieldReferences)),
                ("NeedAppearances", new PdfBoolean(false))
            };
            if (allocatedSignatureFields.Length > 0)
                formEntries.Add(("SigFlags", new PdfInteger(1)));
            if (allocatedTextFields.Length > 0 || allocatedChoiceFields.Length > 0
                || allocatedPushButtons.Length > 0)
            {
                var formFontEntries = new List<KeyValuePair<PdfName, PdfObject>>();
                if (fontNumbers.TryGetValue(PdfStandardFont.Helvetica, out int helveticaNumber))
                    formFontEntries.Add(new(Name("Helv"), new PdfIndirectReference(helveticaNumber, 0)));
                foreach ((TrueTypeFont font, PdfName resource) in formFontResources)
                    formFontEntries.Add(new(resource, new PdfIndirectReference(
                        embeddedFonts.Single(value => ReferenceEquals(value.Font, font)).Type0Number, 0)));
                PdfName defaultFormFont = fontNumbers.ContainsKey(PdfStandardFont.Helvetica)
                    ? Name("Helv") : formFontResources.Values.First();
                var formFonts = new PdfDictionary(formFontEntries);
                formEntries.Add(("DA", Latin1String($"{NameToken(defaultFormFont)} 12 Tf 0 g")));
                formEntries.Add(("DR", Dictionary(("Font", formFonts))));
            }
            catalogEntries.Add(("AcroForm", Dictionary([.. formEntries])));
        }
        if (outputIntentNumber.HasValue)
            catalogEntries.Add(("OutputIntents", new PdfArray([
                new PdfIndirectReference(outputIntentNumber.Value, 0)])));
        if (optionalContentGroups.Length > 0)
        {
            PdfObject[] groupReferences = [.. optionalContentGroups.Select(group =>
                (PdfObject)new PdfIndirectReference(optionalContentNumbers[group], 0))];
            PdfObject[] initiallyHidden = [.. optionalContentGroups
                .Where(group => !group.InitiallyVisible)
                .Select(group => (PdfObject)new PdfIndirectReference(
                    optionalContentNumbers[group], 0))];
            var defaultConfiguration = new List<(string Name, PdfObject Value)>
            {
                ("Name", UnicodeString("KillerPDF Layers")),
                ("BaseState", Name("ON")),
                ("Order", new PdfArray(groupReferences))
            };
            if (initiallyHidden.Length > 0)
                defaultConfiguration.Add(("OFF", new PdfArray(initiallyHidden)));
            catalogEntries.Add(("OCProperties", Dictionary(
                ("OCGs", new PdfArray(groupReferences)),
                ("D", Dictionary([.. defaultConfiguration])))));
        }
        var catalog = Dictionary([.. catalogEntries]);
        var pages = Dictionary(
            ("Type", Name("Pages")),
            ("Count", new PdfInteger(allocated.Count)),
            ("Kids", kids));

        var objects = new List<PdfIndirectObject>
        {
            new(catalogNumber, 0, catalog, 0),
            new(pagesNumber, 0, pages, 0)
        };
        if (Metadata is not null)
        {
            objects.Add(new PdfIndirectObject(metadataNumber!.Value, 0,
                new PdfStream(Dictionary(
                    ("Type", Name("Metadata")),
                    ("Subtype", Name("XML"))), BuildXmp(
                        Metadata, _pdfA4Flavor, _pdfUa2Conformance)), 0));
            if (infoNumber.HasValue)
                objects.Add(new PdfIndirectObject(infoNumber.Value, 0, BuildInfo(Metadata), 0));
        }
        if (outlinesNumber.HasValue)
        {
            var parents = new int?[_bookmarks.Count];
            var children = Enumerable.Range(0, _bookmarks.Count)
                .Select(_ => new List<int>()).ToArray();
            var levelStack = new List<int>();
            var topLevel = new List<int>();
            for (int index = 0; index < _bookmarks.Count; index++)
            {
                int level = _bookmarks[index].Level;
                while (levelStack.Count > level) levelStack.RemoveAt(levelStack.Count - 1);
                if (level == 0) topLevel.Add(index);
                else
                {
                    int parent = levelStack[level - 1];
                    parents[index] = parent;
                    children[parent].Add(index);
                }
                if (levelStack.Count == level) levelStack.Add(index);
                else levelStack[level] = index;
            }
            objects.Add(new PdfIndirectObject(outlinesNumber.Value, 0,
                Dictionary(
                    ("Type", Name("Outlines")),
                    ("First", new PdfIndirectReference(bookmarkNumbers[topLevel[0]], 0)),
                    ("Last", new PdfIndirectReference(bookmarkNumbers[topLevel[^1]], 0)),
                    ("Count", new PdfInteger(topLevel.Sum(index =>
                        1 + (_bookmarks[index].Options.IsOpen
                            ? VisibleDescendantCount(index) : 0))))), 0));
            for (int index = 0; index < _bookmarks.Count; index++)
            {
                BookmarkDefinition bookmark = _bookmarks[index];
                var entries = new List<(string Name, PdfObject Value)>
                {
                    ("Title", UnicodeString(bookmark.Title)),
                    ("Parent", parents[index].HasValue
                        ? new PdfIndirectReference(bookmarkNumbers[parents[index]!.Value], 0)
                        : new PdfIndirectReference(outlinesNumber.Value, 0)),
                    ("Dest", bookmark.NamedDestination is not null
                        ? UnicodeString(bookmark.NamedDestination)
                        : DestinationArray(
                            DestinationReference(bookmark.PageIndex!.Value),
                            bookmark.Options.Destination))
                };
                if (bookmark.Options.Color.HasValue)
                    entries.Add(("C", ColorArray(bookmark.Options.Color.Value)));
                if (bookmark.Options.Style != PdfBookmarkStyle.Regular)
                    entries.Add(("F", new PdfInteger((int)bookmark.Options.Style)));
                List<int> siblings = parents[index].HasValue
                    ? children[parents[index]!.Value] : topLevel;
                int siblingIndex = siblings.IndexOf(index);
                if (siblingIndex > 0)
                    entries.Add(("Prev", new PdfIndirectReference(
                        bookmarkNumbers[siblings[siblingIndex - 1]], 0)));
                if (siblingIndex + 1 < siblings.Count)
                    entries.Add(("Next", new PdfIndirectReference(
                        bookmarkNumbers[siblings[siblingIndex + 1]], 0)));
                if (children[index].Count > 0)
                {
                    entries.Add(("First", new PdfIndirectReference(
                        bookmarkNumbers[children[index][0]], 0)));
                    entries.Add(("Last", new PdfIndirectReference(
                        bookmarkNumbers[children[index][^1]], 0)));
                    int visibleDescendants = VisibleDescendantCount(index);
                    entries.Add(("Count", new PdfInteger(
                        bookmark.Options.IsOpen ? visibleDescendants : -visibleDescendants)));
                }
                objects.Add(new PdfIndirectObject(
                    bookmarkNumbers[index], 0, Dictionary([.. entries]), 0));
            }

            int VisibleDescendantCount(int index) => children[index]
                .Sum(child => 1 + (_bookmarks[child].Options.IsOpen
                    ? VisibleDescendantCount(child) : 0));
        }
        var structureParentKeys = _structureElements
            .Where(element => element.PageIndex.HasValue)
            .Select(element => element.PageIndex!.Value)
            .Distinct()
            .Order()
            .Select((pageIndex, key) => (pageIndex, key))
            .ToDictionary(item => item.pageIndex, item => item.key);
        if (structureRootNumber.HasValue)
        {
            var parents = new int?[_structureElements.Count];
            var children = Enumerable.Range(0, _structureElements.Count)
                .Select(_ => new List<int>()).ToArray();
            var levelStack = new List<int>();
            var topLevel = new List<int>();
            for (int index = 0; index < _structureElements.Count; index++)
            {
                int level = _structureElements[index].Level;
                while (levelStack.Count > level) levelStack.RemoveAt(levelStack.Count - 1);
                if (level == 0) topLevel.Add(index);
                else
                {
                    int parent = levelStack[level - 1];
                    parents[index] = parent;
                    children[parent].Add(index);
                }
                if (levelStack.Count == level) levelStack.Add(index);
                else levelStack[level] = index;
            }

            if (structureParentKeys.Count > PdfNumberTree.MaximumEntryCount
                - accessibleAnnotations.Length)
                throw new NotSupportedException(
                    "The structure ParentTree contains too many entries.");
            var parentTreeNumbers = new List<PdfObject>();
            foreach ((int pageIndex, int key) in structureParentKeys.OrderBy(item => item.Value))
            {
            StructureElementDefinition[] pageElements =
                [.. _structureElements.Where(element => element.PageIndex == pageIndex)];
                int maximumMcid = pageElements.Max(element => element.MarkedContentId!.Value);
                PdfObject[] mappings = [.. Enumerable.Repeat<PdfObject>(
                    PdfNull.Instance, maximumMcid + 1)];
                foreach (StructureElementDefinition element in pageElements)
                {
                    int elementIndex = _structureElements.IndexOf(element);
                    mappings[element.MarkedContentId!.Value] =
                        new PdfIndirectReference(structureElementNumbers[elementIndex], 0);
                }
                parentTreeNumbers.Add(new PdfInteger(key));
                parentTreeNumbers.Add(new PdfArray(mappings));
            }
            foreach (AccessibleAnnotationStructure link in accessibleAnnotations)
            {
                parentTreeNumbers.Add(new PdfInteger(link.ParentTreeKey));
                parentTreeNumbers.Add(new PdfIndirectReference(
                    link.StructureElementNumber, 0));
            }
            objects.Add(new PdfIndirectObject(parentTreeNumber!.Value, 0,
                Dictionary(("Nums", new PdfArray(parentTreeNumbers))), 0));
            var rootEntries = new List<(string Name, PdfObject Value)>
            {
                ("Type", Name("StructTreeRoot")),
                ("K", new PdfArray(topLevel.Select(index =>
                    (PdfObject)new PdfIndirectReference(structureElementNumbers[index], 0)))),
                ("ParentTree", new PdfIndirectReference(parentTreeNumber.Value, 0)),
                ("ParentTreeNextKey", new PdfInteger(nextStructureParentKey))
            };
            if (structureNamespaceNumber.HasValue)
            {
                var namespaces = new List<PdfObject>
                {
                    new PdfIndirectReference(structureNamespaceNumber.Value, 0)
                };
                if (legacyStructureNamespaceNumber.HasValue)
                    namespaces.Add(new PdfIndirectReference(
                        legacyStructureNamespaceNumber.Value, 0));
                rootEntries.Add(("Namespaces", new PdfArray(namespaces)));
            }
            objects.Add(new PdfIndirectObject(structureRootNumber.Value, 0,
                Dictionary([.. rootEntries]), 0));
            if (structureNamespaceNumber.HasValue)
                objects.Add(new PdfIndirectObject(structureNamespaceNumber.Value, 0,
                    Dictionary(
                        ("Type", Name("Namespace")),
                        ("NS", Latin1String("http://iso.org/pdf2/ssn"))), 0));
            if (legacyStructureNamespaceNumber.HasValue)
                objects.Add(new PdfIndirectObject(legacyStructureNamespaceNumber.Value, 0,
                    Dictionary(
                        ("Type", Name("Namespace")),
                        ("NS", Latin1String("http://iso.org/pdf/ssn"))), 0));

            for (int index = 0; index < _structureElements.Count; index++)
            {
                StructureElementDefinition definition = _structureElements[index];
                var entries = new List<(string Name, PdfObject Value)>
                {
                    ("Type", Name("StructElem")),
                    ("S", Name(PdfStructureTypeNames.Name(
                        definition.Type, _pdfUa2Conformance))),
                    ("P", parents[index].HasValue
                        ? new PdfIndirectReference(structureElementNumbers[parents[index]!.Value], 0)
                        : new PdfIndirectReference(structureRootNumber.Value, 0))
                };
                if (definition.PageIndex.HasValue)
                    entries.Add(("Pg", new PdfIndirectReference(
                        allocated[definition.PageIndex.Value].PageNumber, 0)));
                if (structureNamespaceNumber.HasValue)
                    entries.Add(("NS", new PdfIndirectReference(
                        PdfStructureTypeNames.UsesPdf17Namespace(
                            definition.Type, _pdfUa2Conformance)
                            ? legacyStructureNamespaceNumber!.Value
                            : structureNamespaceNumber.Value, 0)));
                var structureKids = new List<PdfObject>();
                if (definition.MarkedContentId.HasValue)
                    structureKids.Add(new PdfInteger(definition.MarkedContentId.Value));
                structureKids.AddRange(children[index].Select(child =>
                    (PdfObject)new PdfIndirectReference(structureElementNumbers[child], 0)));
                if (_pdfUa2Conformance && index == 0)
                    structureKids.AddRange(accessibleAnnotations.Select(link =>
                        (PdfObject)new PdfIndirectReference(
                            link.StructureElementNumber, 0)));
                if (structureKids.Count == 1) entries.Add(("K", structureKids[0]));
                else if (structureKids.Count > 1)
                    entries.Add(("K", new PdfArray(structureKids)));
                if (definition.AlternateDescription is not null)
                    entries.Add(("Alt", UnicodeString(definition.AlternateDescription)));
                if (definition.ActualText is not null)
                    entries.Add(("ActualText", UnicodeString(definition.ActualText)));
                if (definition.ListNumbering.HasValue)
                    entries.Add(("A", Dictionary(
                        ("O", Name("List")),
                        ("ListNumbering", Name(PdfListNumberingNames.Name(
                            definition.ListNumbering.Value))))));
                objects.Add(new PdfIndirectObject(
                    structureElementNumbers[index], 0, Dictionary([.. entries]), 0));
            }
            foreach (AccessibleAnnotationStructure link in accessibleAnnotations)
            {
                PdfIndirectReference pageReference = new(
                    allocated[link.PageIndex].PageNumber, 0);
                objects.Add(new PdfIndirectObject(
                    link.StructureElementNumber, 0,
                    Dictionary(
                        ("Type", Name("StructElem")),
                        ("S", Name(link.StructureType)),
                        ("P", new PdfIndirectReference(structureElementNumbers[0], 0)),
                        ("Pg", pageReference),
                        ("NS", new PdfIndirectReference(structureNamespaceNumber!.Value, 0)),
                        ("Alt", UnicodeString(link.Description)),
                        ("K", Dictionary(
                            ("Type", Name("OBJR")),
                            ("Pg", pageReference),
                            ("Obj", new PdfIndirectReference(link.AnnotationNumber, 0))))), 0));
            }
        }
        foreach (AllocatedAttachment allocatedAttachment in allocatedAttachments)
            AddAttachmentObjects(objects, allocatedAttachment);
        foreach (AllocatedHierarchyField hierarchyField in fieldHierarchy.Fields)
        {
            var entries = new List<(string Name, PdfObject Value)>
            {
                ("T", UnicodeString(hierarchyField.PartialName)),
                ("Kids", new PdfArray(hierarchyField.KidNumbers.Select(number =>
                    (PdfObject)new PdfIndirectReference(number, 0))))
            };
            if (hierarchyField.ParentNumber.HasValue)
                entries.Add(("Parent", new PdfIndirectReference(
                    hierarchyField.ParentNumber.Value, 0)));
            objects.Add(new PdfIndirectObject(
                hierarchyField.FieldNumber, 0, Dictionary([.. entries]), 0));
        }
        for (int index = 0; index < allocatedFileAttachmentAnnotations.Length; index++)
            AddFileAttachmentAnnotationObjects(objects, allocatedFileAttachmentAnnotations[index],
                allocated, index + 1, attachmentFileSpecificationNumbers);
        foreach (AllocatedTextField allocatedTextField in allocatedTextFields)
        {
            (PdfName resource, int number, EmbeddedFontUsage? usage) = FormFontBinding(
                allocatedTextField.Definition.EmbeddedFont,
                fontNumbers, formFontResources, embeddedFonts);
            AddTextFieldObjects(
                objects, allocatedTextField, allocated, resource, number, usage);
        }
        foreach (AllocatedCheckBox allocatedCheckBox in allocatedCheckBoxes)
            AddCheckBoxObjects(objects, allocatedCheckBox, allocated);
        foreach (AllocatedRadioGroup allocatedRadioGroup in allocatedRadioGroups)
            AddRadioGroupObjects(objects, allocatedRadioGroup, allocated);
        foreach (AllocatedChoiceField allocatedChoiceField in allocatedChoiceFields)
        {
            (PdfName resource, int number, EmbeddedFontUsage? usage) = FormFontBinding(
                allocatedChoiceField.Definition.EmbeddedFont,
                fontNumbers, formFontResources, embeddedFonts);
            AddChoiceFieldObjects(
                objects, allocatedChoiceField, allocated, resource, number, usage);
        }
        foreach (AllocatedPushButton allocatedPushButton in allocatedPushButtons)
        {
            (PdfName resource, int number, EmbeddedFontUsage? usage) = FormFontBinding(
                allocatedPushButton.Definition.EmbeddedFont,
                fontNumbers, formFontResources, embeddedFonts);
            PdfPushButtonAppearanceOptions appearance =
                allocatedPushButton.Definition.AppearanceOptions;
            AddPushButtonObjects(objects, allocatedPushButton, allocated, resource, number, usage,
                appearance.Icon is null ? null : imageNumbers[appearance.Icon],
                appearance.RolloverIcon is null ? null : imageNumbers[appearance.RolloverIcon],
                appearance.DownIcon is null ? null : imageNumbers[appearance.DownIcon],
                _pdfUa2Conformance ? structureElementNumbers[0] : null);
        }
        foreach (AllocatedSignatureField allocatedSignatureField in allocatedSignatureFields)
        {
            (PdfName resource, int number, EmbeddedFontUsage? usage) = allocatedSignatureField.Definition.AppearanceText is null
                ? (Name("Helv"), 0, null)
                : FormFontBinding(
                    allocatedSignatureField.Definition.EmbeddedFont,
                    fontNumbers, formFontResources, embeddedFonts);
            AddSignatureFieldObject(
                objects, allocatedSignatureField, allocated, resource, number, usage);
        }
        ApplyFieldHierarchy(objects, fieldHierarchy);
        if (_outputIntent is not null)
            AddOutputIntentObjects(
                objects, _outputIntent, iccProfileNumber!.Value, outputIntentNumber!.Value);
        foreach (PdfOptionalContentGroup group in optionalContentGroups)
        {
            var groupEntries = new List<(string Name, PdfObject Value)>
            {
                    ("Type", Name("OCG")),
                    ("Name", UnicodeString(group.Name)),
                    ("Intent", new PdfArray([Name("View"), Name("Design")]))
            };
            var usageEntries = new List<(string Name, PdfObject Value)>();
            if (group.VisibleWhenPrinting.HasValue)
                usageEntries.Add(("Print", Dictionary(("PrintState",
                    Name(group.VisibleWhenPrinting.Value ? "ON" : "OFF")))));
            if (group.VisibleWhenExporting.HasValue)
                usageEntries.Add(("Export", Dictionary(("ExportState",
                    Name(group.VisibleWhenExporting.Value ? "ON" : "OFF")))));
            if (usageEntries.Count > 0)
                groupEntries.Add(("Usage", Dictionary([.. usageEntries])));
            objects.Add(new PdfIndirectObject(optionalContentNumbers[group], 0,
                Dictionary([.. groupEntries]), 0));
        }
        foreach (PdfGraphicsState state in graphicsStates)
        {
            var entries = new List<(string Name, PdfObject Value)>
            {
                    ("Type", Name("ExtGState")),
                    ("ca", Number(state.FillOpacity)),
                    ("CA", Number(state.StrokeOpacity)),
                    ("BM", Name(PdfBlendModeNames.Name(state.BlendMode))),
                    ("op", new PdfBoolean(state.FillOverprint)),
                    ("OP", new PdfBoolean(state.StrokeOverprint)),
                    ("OPM", new PdfInteger((int)state.OverprintMode)),
                    ("AIS", new PdfBoolean(state.AlphaIsShape)),
                    ("TK", new PdfBoolean(state.TextKnockout))
            };
            if (state.SoftMask is not null)
            {
                var softMaskEntries = new List<(string Name, PdfObject Value)>
                {
                    ("S", Name(state.SoftMask.Subtype == PdfSoftMaskSubtype.Alpha
                        ? "Alpha" : "Luminosity")),
                    ("G", new PdfIndirectReference(
                        formNumbers[state.SoftMask.Group], 0))
                };
                if (state.SoftMask.Backdrop.HasValue)
                    softMaskEntries.Add(("BC", new PdfArray(
                        state.SoftMask.Backdrop.Value.Components.Select(Number))));
                entries.Add(("SMask", Dictionary([.. softMaskEntries])));
            }
            else
                entries.Add(("SMask", Name("None")));
            objects.Add(new PdfIndirectObject(graphicsStateNumbers[state], 0,
                Dictionary([.. entries]), 0));
        }
        foreach (PdfShading shading in shadings)
            objects.Add(new PdfIndirectObject(
                shadingNumbers[shading], 0, ShadingDictionary(shading), 0));
        foreach (PdfIccProfile profile in allIccProfiles)
            objects.Add(new PdfIndirectObject(iccProfileNumbers[profile], 0,
                PdfOutputIntentFactory.Profile(profile), 0));
        for (int index = 0; index < allocatedTextNotes.Length; index++)
            AddTextNoteObjects(objects, allocatedTextNotes[index], allocated, index + 1,
                textNoteNumbersByName);
        for (int index = 0; index < allocatedTextMarkups.Length; index++)
            AddTextMarkupObjects(objects, allocatedTextMarkups[index], allocated, index + 1);
        for (int index = 0; index < allocatedCaretAnnotations.Length; index++)
            AddCaretAnnotationObjects(objects, allocatedCaretAnnotations[index], allocated, index + 1);
        for (int index = 0; index < allocatedRedactionAnnotations.Length; index++)
        {
            RedactionAnnotationDefinition redaction = allocatedRedactionAnnotations[index].Definition;
            PdfName? resource = null;
            int? number = null;
            EmbeddedFontUsage? usage = null;
            if (redaction.OverlayText is not null)
                (resource, number, usage) = FormFontBinding(
                    redaction.OverlayFont, fontNumbers, formFontResources, embeddedFonts);
            AddRedactionAnnotationObjects(objects, allocatedRedactionAnnotations[index], allocated,
                index + 1, resource, number, usage);
        }
        for (int index = 0; index < allocatedFreeTexts.Length; index++)
        {
            FreeTextDefinition freeText = allocatedFreeTexts[index].Definition;
            (PdfName resource, int number, EmbeddedFontUsage? usage) = FormFontBinding(
                freeText.Font, fontNumbers, formFontResources, embeddedFonts);
            AddFreeTextObjects(objects, allocatedFreeTexts[index], allocated, index + 1,
                resource, number, usage);
        }
        for (int index = 0; index < allocatedVisualAnnotations.Length; index++)
            AddVisualAnnotationObjects(objects, allocatedVisualAnnotations[index], allocated, index + 1);
        for (int index = 0; index < allocatedImageStamps.Length; index++)
            AddImageStampObjects(objects, allocatedImageStamps[index], allocated, index + 1,
                imageNumbers[allocatedImageStamps[index].Definition.Image]);
        ApplyAccessibleAnnotationParents(objects, accessibleByAnnotation);
        foreach ((PdfStandardFont font, int number) in fontNumbers.OrderBy(entry => entry.Value))
            objects.Add(new PdfIndirectObject(number, 0, StandardFontDictionary(font), 0));
        foreach (AllocatedEmbeddedFont font in embeddedFonts)
            AddEmbeddedFontObjects(objects, font);
        foreach ((PdfImage image, int number) in imageNumbers.OrderBy(entry => entry.Value))
            objects.Add(new PdfIndirectObject(number, 0,
                PdfImageXObjectFactory.Create(image, image.SoftMask is null ? null
                    : new PdfIndirectReference(imageNumbers[image.SoftMask], 0)), 0));
        foreach (PdfFormXObject form in forms)
        {
            var entries = new List<(string Name, PdfObject Value)>
            {
                ("Type", Name("XObject")),
                ("Subtype", Name("Form")),
                ("FormType", new PdfInteger(1)),
                ("BBox", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    Number(form.Width), Number(form.Height)])),
                ("Resources", ResourceDictionary(
                    form.Fonts, form.EmbeddedFonts, form.Images,
                    form.OptionalContentGroups, form.GraphicsStates,
                    form.Shadings, form.Forms, form.Patterns, form.IccColorSpaces,
                    form.SpotColors, form.LabColorSpaces, form.IndexedColorSpaces,
                    form.CalibratedColorSpaces,
                    fontNumbers, embeddedFonts, imageNumbers,
                    optionalContentNumbers, graphicsStateNumbers,
                    shadingNumbers, formNumbers, patternNumbers, iccProfileNumbers))
            };
            if (form.IsolatedTransparencyGroup || form.KnockoutTransparencyGroup)
                entries.Add(("Group", Dictionary(
                    ("S", Name("Transparency")),
                    ("CS", Name(form.TransparencyGroupColorSpace switch
                    {
                        PdfTransparencyGroupColorSpace.Gray => "DeviceGray",
                        PdfTransparencyGroupColorSpace.Rgb => "DeviceRGB",
                        PdfTransparencyGroupColorSpace.Cmyk => "DeviceCMYK",
                        _ => throw new ArgumentOutOfRangeException(nameof(form))
                    })),
                    ("I", new PdfBoolean(form.IsolatedTransparencyGroup)),
                    ("K", new PdfBoolean(form.KnockoutTransparencyGroup)))));
            objects.Add(new PdfIndirectObject(formNumbers[form], 0,
                new PdfStream(Dictionary([.. entries]), form.Content), 0));
        }
        foreach (PdfTilingPattern pattern in patterns)
        {
            objects.Add(new PdfIndirectObject(patternNumbers[pattern], 0,
                new PdfStream(Dictionary(
                    ("Type", Name("Pattern")),
                    ("PatternType", new PdfInteger(1)),
                    ("PaintType", new PdfInteger((int)pattern.PaintType)),
                    ("TilingType", new PdfInteger((int)pattern.TilingType)),
                    ("BBox", new PdfArray([
                        new PdfInteger(0), new PdfInteger(0),
                        Number(pattern.Width), Number(pattern.Height)])),
                    ("XStep", Number(pattern.HorizontalStep)),
                    ("YStep", Number(pattern.VerticalStep)),
                    ("Matrix", new PdfArray([
                        Number(pattern.Matrix.A), Number(pattern.Matrix.B),
                        Number(pattern.Matrix.C), Number(pattern.Matrix.D),
                        Number(pattern.Matrix.E), Number(pattern.Matrix.F)])),
                    ("Resources", ResourceDictionary(
                        pattern.Fonts, pattern.EmbeddedFonts, pattern.Images,
                        pattern.OptionalContentGroups, pattern.GraphicsStates,
                        pattern.Shadings, pattern.Forms, pattern.Patterns,
                        pattern.IccColorSpaces, pattern.SpotColors, pattern.LabColorSpaces,
                        pattern.IndexedColorSpaces, pattern.CalibratedColorSpaces,
                        fontNumbers, embeddedFonts, imageNumbers,
                        optionalContentNumbers, graphicsStateNumbers,
                        shadingNumbers, formNumbers, patternNumbers, iccProfileNumbers))), pattern.Content), 0));
        }
        foreach (AllocatedPage allocatedPage in allocated)
        {
            PdfDictionary resources = ResourceDictionary(
                allocatedPage.Definition.Fonts, allocatedPage.Definition.EmbeddedFonts,
                allocatedPage.Definition.Images, allocatedPage.Definition.OptionalContentGroups,
                allocatedPage.Definition.GraphicsStates, allocatedPage.Definition.Shadings,
                allocatedPage.Definition.Forms, allocatedPage.Definition.Patterns,
                allocatedPage.Definition.IccColorSpaces, allocatedPage.Definition.SpotColors,
                allocatedPage.Definition.LabColorSpaces, allocatedPage.Definition.IndexedColorSpaces,
                allocatedPage.Definition.CalibratedColorSpaces,
                fontNumbers, embeddedFonts, imageNumbers, optionalContentNumbers,
                graphicsStateNumbers, shadingNumbers, formNumbers, patternNumbers,
                iccProfileNumbers);
            var entries = new List<(string Name, PdfObject Value)>
            {
                ("Type", Name("Page")),
                ("Parent", new PdfIndirectReference(pagesNumber, 0)),
                ("MediaBox", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    Number(allocatedPage.Definition.Width), Number(allocatedPage.Definition.Height)])),
                ("Resources", resources)
            };
            if (allocatedPage.ContentNumber.HasValue)
                entries.Add(("Contents", new PdfIndirectReference(allocatedPage.ContentNumber.Value, 0)));
            if (allocatedPage.Definition.Rotation != 0)
                entries.Add(("Rotate", new PdfInteger(allocatedPage.Definition.Rotation)));
            if (allocatedPage.Definition.UserUnit != 1)
                entries.Add(("UserUnit", Number(allocatedPage.Definition.UserUnit)));
            PdfPageTabOrder? tabOrder = allocatedPage.Definition.TabOrder
                ?? (_pdfUa2Conformance ? PdfPageTabOrder.Structure : null);
            if (tabOrder.HasValue)
                entries.Add(("Tabs", Name(PageTabOrderName(tabOrder.Value))));
            foreach ((PdfPageBox box, PageBoxDefinition rectangle) in allocatedPage.Definition.Boxes.OrderBy(entry => entry.Key))
                entries.Add((PageBoxName(box), new PdfArray([
                    Number(rectangle.X), Number(rectangle.Y),
                    Number(rectangle.X + rectangle.Width), Number(rectangle.Y + rectangle.Height)])));
            if (allocatedPage.Definition.DisplayDuration.HasValue)
                entries.Add(("Dur", Number(allocatedPage.Definition.DisplayDuration.Value)));
            if (allocatedPage.Definition.Transition is not null)
                entries.Add(("Trans", allocatedPage.Definition.Transition.ToDictionary()));
            if (allocatedPage.Definition.Thumbnail is not null)
                entries.Add(("Thumb", new PdfIndirectReference(
                    imageNumbers[allocatedPage.Definition.Thumbnail], 0)));
            if (allocatedPage.AnnotationNumbers.Length > 0)
                entries.Add(("Annots", new PdfArray(allocatedPage.AnnotationNumbers.Select(number =>
                    (PdfObject)new PdfIndirectReference(number, 0)))));
            int pageIndex = allocated.IndexOf(allocatedPage);
            if (structureParentKeys.TryGetValue(pageIndex, out int structureParentKey))
                entries.Add(("StructParents", new PdfInteger(structureParentKey)));
            objects.Add(new PdfIndirectObject(
                allocatedPage.PageNumber, 0, Dictionary([.. entries]), 0));

            if (allocatedPage.ContentNumber.HasValue)
            {
                objects.Add(new PdfIndirectObject(
                    allocatedPage.ContentNumber.Value,
                    0,
                    new PdfStream(Dictionary(), allocatedPage.Definition.Content),
                    0));
            }
            for (int index = 0; index < allocatedPage.Definition.Links.Count; index++)
            {
                LinkDefinition link = allocatedPage.Definition.Links[index];
                var annotationEntries = new List<(string Name, PdfObject Value)>
                {
                    ("Type", Name("Annot")),
                    ("Subtype", Name("Link")),
                    ("Rect", new PdfArray([
                        Number(link.X), Number(link.Y),
                        Number(link.X + link.Width), Number(link.Y + link.Height)])),
                    ("P", new PdfIndirectReference(allocatedPage.PageNumber, 0)),
                    ("F", new PdfInteger((int)(link.Metadata?.Flags ?? PdfAnnotationFlags.Print))),
                    ("NM", Latin1String($"KillerPDF-Link-{index + 1}")),
                    ("Border", new PdfArray([
                        Number(link.Appearance.HorizontalCornerRadius),
                        Number(link.Appearance.VerticalCornerRadius),
                        Number(link.Appearance.BorderWidth)]))
                };
                if (link.Appearance.BorderWidth > 0)
                {
                    var borderStyleEntries = new List<(string Name, PdfObject Value)>
                    {
                        ("W", Number(link.Appearance.BorderWidth)),
                        ("S", Name(LinkBorderStyleName(link.Appearance.BorderStyle)))
                    };
                    if (link.Appearance.BorderStyle == PdfLinkBorderStyle.Dashed)
                        borderStyleEntries.Add(("D", new PdfArray(
                            link.Appearance.DashPattern.Select(Number))));
                    annotationEntries.Add(("BS", Dictionary([.. borderStyleEntries])));
                }
                if (link.Appearance.Color.HasValue)
                    annotationEntries.Add(("C", ColorArray(link.Appearance.Color.Value)));
                if (!string.IsNullOrEmpty(link.Contents))
                    annotationEntries.Add(("Contents", UnicodeString(link.Contents)));
                if (accessibleByAnnotation.TryGetValue(
                        allocatedPage.AnnotationNumbers[index],
                        out AccessibleAnnotationStructure? accessibleLink))
                    annotationEntries.Add(("StructParent",
                        new PdfInteger(accessibleLink.ParentTreeKey)));
                AddAnnotationMetadata(annotationEntries, link.Metadata);
                if (link.Quads is not null)
                    annotationEntries.Add(("QuadPoints", new PdfArray(link.Quads.SelectMany(quad =>
                        new PdfObject[]
                        {
                            Number(quad.UpperLeft.X), Number(quad.UpperLeft.Y),
                            Number(quad.UpperRight.X), Number(quad.UpperRight.Y),
                            Number(quad.LowerLeft.X), Number(quad.LowerLeft.Y),
                            Number(quad.LowerRight.X), Number(quad.LowerRight.Y)
                        }))));
                annotationEntries.Add(("H", Name(LinkHighlightModeName(
                    link.Appearance.HighlightMode))));
                if (link is UriLinkDefinition uri)
                {
                    annotationEntries.Add(("A", Dictionary(
                        ("S", Name("URI")),
                        ("URI", new PdfString(PdfUnicodeEncoding.EncodeUtf8(uri.Uri),
                            PdfStringForm.Literal)))));
                }
                else if (link is PageLinkDefinition pageLink)
                {
                    annotationEntries.Add(("Dest", DestinationArray(
                        DestinationReference(pageLink.DestinationPageIndex),
                        pageLink.Destination)));
                }
                else if (link is NamedDestinationLinkDefinition named)
                {
                    annotationEntries.Add(("Dest", UnicodeString(named.DestinationName)));
                }
                objects.Add(new PdfIndirectObject(
                    allocatedPage.AnnotationNumbers[index], 0,
                    Dictionary([.. annotationEntries]), 0));
            }
        }

        return Assemble(objects, catalogNumber, infoNumber);
    }

    private byte[] Assemble(List<PdfIndirectObject> objects, int rootNumber, int? infoNumber)
    {
        byte[] identifier = DocumentIdentifier(objects);
        PdfStandardSecurityHandler? security = null;
        int? encryptionNumber = null;
        if (_encryption is not null)
        {
            (security, PdfDictionary encryptionDictionary) =
                PdfStandardSecurityHandler.CreateModernPassword(_encryption);
            encryptionNumber = checked(objects.Max(item => item.ObjectNumber) + 1);
            objects.Add(new PdfIndirectObject(
                encryptionNumber.Value, 0, encryptionDictionary, 0));
        }
        else if (_certificateEncryption is not null)
        {
            (security, PdfDictionary encryptionDictionary) =
                PdfStandardSecurityHandler.CreateCertificate(_certificateEncryption);
            encryptionNumber = checked(objects.Max(item => item.ObjectNumber) + 1);
            objects.Add(new PdfIndirectObject(
                encryptionNumber.Value, 0, encryptionDictionary, 0));
        }
        using var output = new MemoryStream();
        WriteAscii(output, $"%PDF-{Version}\n");
        output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var offsets = new List<(int Number, int Offset)>(objects.Count);
        foreach (PdfIndirectObject value in objects.OrderBy(value => value.ObjectNumber))
        {
            int offset = checked((int)output.Position);
            offsets.Add((value.ObjectNumber, offset));
            PdfObject written = security is not null && value.ObjectNumber != encryptionNumber
                ? security.Encrypt(value.Value, value.ObjectNumber, value.Generation)
                : value.Value;
            PdfObjectWriter.Write(output, new PdfIndirectObject(
                value.ObjectNumber, value.Generation, written, offset));
        }

        int xrefOffset = checked((int)output.Position);
        output.Write("xref\n0 1\n0000000000 65535 f \n"u8);
        foreach ((int number, int offset) in offsets)
            WriteAscii(output, $"{number} 1\n{offset:0000000000} 00000 n \n");
        output.Write("trailer\n"u8);
        var trailerEntries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(SizeName, new PdfInteger(objects.Max(item => item.ObjectNumber) + 1)),
            new(RootName, new PdfIndirectReference(rootNumber, 0)),
            new(Name("ID"), new PdfArray([
                new PdfString(identifier, PdfStringForm.Hexadecimal),
                new PdfString(identifier, PdfStringForm.Hexadecimal)]))
        };
        if (infoNumber.HasValue)
            trailerEntries.Add(new(Name("Info"), new PdfIndirectReference(infoNumber.Value, 0)));
        if (encryptionNumber.HasValue)
            trailerEntries.Add(new(Name("Encrypt"),
                new PdfIndirectReference(encryptionNumber.Value, 0)));
        PdfObjectWriter.Write(output, new PdfDictionary(trailerEntries));
        output.Write("\nstartxref\n"u8);
        WriteAscii(output, xrefOffset.ToString(CultureInfo.InvariantCulture));
        output.Write("\n%%EOF\n"u8);
        return output.ToArray();
    }

    private static PdfObject Number(double value) =>
        value == Math.Truncate(value) && value is >= long.MinValue and <= long.MaxValue
            ? new PdfInteger((long)value)
            : new PdfReal(value);

    private static PdfDictionary StandardFontDictionary(PdfStandardFont font)
    {
        string baseName = font switch
        {
            PdfStandardFont.Helvetica => "Helvetica",
            PdfStandardFont.HelveticaBold => "Helvetica-Bold",
            PdfStandardFont.HelveticaOblique => "Helvetica-Oblique",
            PdfStandardFont.HelveticaBoldOblique => "Helvetica-BoldOblique",
            PdfStandardFont.TimesRoman => "Times-Roman",
            PdfStandardFont.TimesBold => "Times-Bold",
            PdfStandardFont.TimesItalic => "Times-Italic",
            PdfStandardFont.TimesBoldItalic => "Times-BoldItalic",
            PdfStandardFont.Courier => "Courier",
            PdfStandardFont.CourierBold => "Courier-Bold",
            PdfStandardFont.CourierOblique => "Courier-Oblique",
            PdfStandardFont.CourierBoldOblique => "Courier-BoldOblique",
            PdfStandardFont.Symbol => "Symbol",
            PdfStandardFont.ZapfDingbats => "ZapfDingbats",
            _ => throw new ArgumentOutOfRangeException(nameof(font))
        };
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Font")),
            ("Subtype", Name("Type1")),
            ("BaseFont", Name(baseName))
        };
        if (font is not PdfStandardFont.Symbol and not PdfStandardFont.ZapfDingbats)
            entries.Add(("Encoding", Name("WinAnsiEncoding")));
        return Dictionary([.. entries]);
    }

    private static void AddAttachmentObjects(
        List<PdfIndirectObject> objects, AllocatedAttachment allocated)
    {
        AttachmentDefinition attachment = allocated.Definition;
        objects.Add(new PdfIndirectObject(allocated.EmbeddedFileNumber, 0,
            PdfAttachmentFactory.EmbeddedFile(
                attachment.Data, attachment.MimeType, attachment.ModificationDate,
                attachment.CreationDate), 0));
        objects.Add(new PdfIndirectObject(allocated.FileSpecificationNumber, 0,
            PdfAttachmentFactory.FileSpecification(
                attachment.FileName, attachment.Description,
                attachment.Relationship,
                new PdfIndirectReference(allocated.EmbeddedFileNumber, 0)), 0));
    }

    private static void AddFileAttachmentAnnotationObjects(
        List<PdfIndirectObject> objects,
        AllocatedFileAttachmentAnnotation allocated,
        IReadOnlyList<AllocatedPage> pages,
        int sequence,
        IReadOnlyDictionary<string, int> fileSpecificationNumbers)
    {
        FileAttachmentAnnotationDefinition value = allocated.Definition;
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")),
            ("Subtype", Name("FileAttachment")),
            ("Rect", new PdfArray([
                Number(value.X), Number(value.Y),
                Number(value.X + value.Size), Number(value.Y + value.Size)])),
            ("P", new PdfIndirectReference(pages[value.PageIndex].PageNumber, 0)),
            ("F", new PdfInteger((int)(value.Metadata?.Flags ?? PdfAnnotationFlags.Print))),
            ("NM", Latin1String($"KillerPDF-FileAttachment-{sequence}")),
            ("Name", Name(value.Icon.ToString())),
            ("C", ColorArray(value.Color)),
            ("FS", new PdfIndirectReference(fileSpecificationNumbers[value.FileName], 0)),
            ("AP", Dictionary(("N", new PdfIndirectReference(allocated.AppearanceNumber, 0))))
        };
        if (!string.IsNullOrEmpty(value.Contents))
            entries.Add(("Contents", UnicodeString(value.Contents)));
        AddAnnotationMetadata(entries, value.Metadata);
        objects.Add(new PdfIndirectObject(
            allocated.AnnotationNumber, 0, Dictionary([.. entries]), 0));

        double inset = value.Size * 0.15;
        double fold = value.Size * 0.28;
        using var appearance = new MemoryStream();
        WriteAscii(appearance,
            $"q\n{ColorOperands(value.Color)} rg\n0 G\n1 w\n" +
            $"{FormatNumber(inset)} {FormatNumber(inset)} m\n" +
            $"{FormatNumber(value.Size - inset - fold)} {FormatNumber(inset)} l\n" +
            $"{FormatNumber(value.Size - inset)} {FormatNumber(inset + fold)} l\n" +
            $"{FormatNumber(value.Size - inset)} {FormatNumber(value.Size - inset)} l\n" +
            $"{FormatNumber(inset)} {FormatNumber(value.Size - inset)} l\nh\nB\n" +
            $"{FormatNumber(value.Size - inset - fold)} {FormatNumber(inset)} m\n" +
            $"{FormatNumber(value.Size - inset - fold)} {FormatNumber(inset + fold)} l\n" +
            $"{FormatNumber(value.Size - inset)} {FormatNumber(inset + fold)} l\nS\nQ\n");
        objects.Add(new PdfIndirectObject(allocated.AppearanceNumber, 0,
            AnnotationAppearance(value.Size, value.Size, Dictionary(), appearance.ToArray()), 0));
    }

    private static void AddTextFieldObjects(
        List<PdfIndirectObject> objects,
        AllocatedTextField allocatedField,
        IReadOnlyList<AllocatedPage> pages,
        PdfName fontResource,
        int fontNumber,
        EmbeddedFontUsage? fontUsage)
    {
        TextFieldDefinition field = allocatedField.Definition;
        int pageNumber = pages[field.PageIndex].PageNumber;
        PdfString defaultAppearance = Latin1String(
            $"{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf " +
            $"{ColorOperands(field.AppearanceStyle.TextColor)} rg");
        var fieldEntries = new List<(string Name, PdfObject Value)>
        {
                ("Type", Name("Annot")),
                ("Subtype", Name("Widget")),
                ("FT", Name("Tx")),
                ("T", UnicodeString(field.Name)),
                ("V", UnicodeString(field.Value)),
                ("DV", UnicodeString(field.DefaultValue)),
                ("Rect", new PdfArray([
                    Number(field.X), Number(field.Y),
                    Number(field.X + field.Width), Number(field.Y + field.Height)])),
                ("P", new PdfIndirectReference(pageNumber, 0)),
                ("F", new PdfInteger(FormWidgetFlags(field.Options.Visibility))),
                ("DA", defaultAppearance),
                ("MK", FormFieldAppearanceCharacteristics(field.AppearanceStyle)),
                ("BS", FormFieldBorderDictionary(field.AppearanceStyle)),
                ("AP", Dictionary(("N", new PdfIndirectReference(allocatedField.AppearanceNumber, 0))))
        };
        int flags = TextFieldFlags(field.Options, field.RichTextValue is not null);
        if (flags != 0)
            fieldEntries.Add(("Ff", new PdfInteger(flags)));
        if (field.Options.MaximumLength.HasValue)
            fieldEntries.Add(("MaxLen", new PdfInteger(field.Options.MaximumLength.Value)));
        if (field.Options.Alignment != PdfTextFieldAlignment.Left)
            fieldEntries.Add(("Q", new PdfInteger((int)field.Options.Alignment)));
        if (field.RichTextValue is not null)
            fieldEntries.Add(("RV", UnicodeString(field.RichTextValue)));
        AddFieldMetadata(fieldEntries, field.Metadata);
        objects.Add(new PdfIndirectObject(allocatedField.FieldNumber, 0,
            Dictionary([.. fieldEntries]), 0));

        byte[] appearance = BuildTextFieldAppearance(field, fontResource, fontUsage);
        objects.Add(new PdfIndirectObject(allocatedField.AppearanceNumber, 0,
            new PdfStream(Dictionary(
                ("Type", Name("XObject")),
                ("Subtype", Name("Form")),
                ("FormType", new PdfInteger(1)),
                ("BBox", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    Number(field.Width), Number(field.Height)])),
                ("Resources", Dictionary(("Font", new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(
                        fontResource, new PdfIndirectReference(fontNumber, 0))]))))),
                appearance), 0));
    }

    private static int TextFieldFlags(PdfTextFieldOptions options, bool hasRichText)
    {
        int flags = 0;
        if (options.ReadOnly) flags |= 1;
        if (options.Required) flags |= 1 << 1;
        if (options.NoExport) flags |= 1 << 2;
        if (options.Multiline) flags |= 1 << 12;
        if (options.Password) flags |= 1 << 13;
        if (options.FileSelect) flags |= 1 << 20;
        if (options.DoNotSpellCheck) flags |= 1 << 22;
        if (options.DoNotScroll) flags |= 1 << 23;
        if (options.Comb) flags |= 1 << 24;
        if (hasRichText) flags |= 1 << 25;
        return flags;
    }

    private static void AddCheckBoxObjects(
        List<PdfIndirectObject> objects,
        AllocatedCheckBox allocatedField,
        IReadOnlyList<AllocatedPage> pages)
    {
        CheckBoxDefinition field = allocatedField.Definition;
        PdfName onState = Name(field.ExportValue);
        PdfName currentState = field.IsChecked ? onState : Name("Off");
        PdfName defaultState = field.DefaultChecked ? onState : Name("Off");
        var entries = new List<(string Name, PdfObject Value)>
        {
                ("Type", Name("Annot")),
                ("Subtype", Name("Widget")),
                ("FT", Name("Btn")),
                ("T", UnicodeString(field.Name)),
                ("V", currentState),
                ("DV", defaultState),
                ("AS", currentState),
                ("Rect", new PdfArray([
                    Number(field.X), Number(field.Y),
                    Number(field.X + field.Width), Number(field.Y + field.Height)])),
                ("P", new PdfIndirectReference(pages[field.PageIndex].PageNumber, 0)),
                ("F", new PdfInteger(FormWidgetFlags(field.Options.Visibility))),
                ("MK", CheckBoxAppearanceCharacteristics(field)),
                ("BS", FormFieldBorderDictionary(field.AppearanceStyle)),
                ("AP", Dictionary(("N", new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(
                        Name("Off"), new PdfIndirectReference(allocatedField.OffAppearanceNumber, 0)),
                    new KeyValuePair<PdfName, PdfObject>(
                        onState, new PdfIndirectReference(allocatedField.OnAppearanceNumber, 0))]))))
        };
        int flags = FormFieldFlags(field.Options);
        if (flags != 0)
            entries.Add(("Ff", new PdfInteger(flags)));
        AddFieldMetadata(entries, field.Metadata);
        objects.Add(new PdfIndirectObject(
            allocatedField.FieldNumber, 0, Dictionary([.. entries]), 0));

        objects.Add(new PdfIndirectObject(allocatedField.OffAppearanceNumber, 0,
            CheckBoxAppearance(field, isChecked: false), 0));
        objects.Add(new PdfIndirectObject(allocatedField.OnAppearanceNumber, 0,
            CheckBoxAppearance(field, isChecked: true), 0));
    }

    private static PdfStream CheckBoxAppearance(CheckBoxDefinition field, bool isChecked)
    {
        using var output = new MemoryStream();
        WriteFormFieldBackgroundAndBorder(
            output, field.Width, field.Height, field.AppearanceStyle, clip: false);
        if (isChecked)
        {
            double inset = Math.Min(3, Math.Min(field.Width, field.Height) / 4);
            WriteCheckBoxMark(output, field, inset);
        }
        output.Write("Q\n"u8);
        return new PdfStream(Dictionary(
            ("Type", Name("XObject")),
            ("Subtype", Name("Form")),
            ("FormType", new PdfInteger(1)),
            ("BBox", new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                Number(field.Width), Number(field.Height)])),
            ("Resources", Dictionary())), output.ToArray());
    }

    private static string CheckBoxMarkCaption(PdfCheckBoxMark mark) => mark switch
    {
        PdfCheckBoxMark.Check => "4",
        PdfCheckBoxMark.Cross => "8",
        PdfCheckBoxMark.Circle => "l",
        PdfCheckBoxMark.Diamond => "u",
        PdfCheckBoxMark.Square => "n",
        PdfCheckBoxMark.Star => "H",
        _ => throw new ArgumentOutOfRangeException(nameof(mark))
    };

    private static PdfDictionary CheckBoxAppearanceCharacteristics(CheckBoxDefinition field)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("CA", Latin1String(CheckBoxMarkCaption(field.Mark)))
        };
        if (field.AppearanceStyle.BackgroundColor.HasValue)
            entries.Add(("BG", RgbArray(field.AppearanceStyle.BackgroundColor.Value)));
        if (field.AppearanceStyle.BorderColor.HasValue)
            entries.Add(("BC", RgbArray(field.AppearanceStyle.BorderColor.Value)));
        return Dictionary([.. entries]);
    }

    private static void WriteCheckBoxMark(Stream output, CheckBoxDefinition field, double inset)
    {
        WriteAscii(output,
            $"{ColorOperands(field.AppearanceStyle.TextColor)} RG\n" +
            $"{ColorOperands(field.AppearanceStyle.TextColor)} rg\n");
        double right = field.Width - inset;
        double top = field.Height - inset;
        switch (field.Mark)
        {
            case PdfCheckBoxMark.Check:
                WriteAscii(output,
                    $"2 w\n{FormatNumber(inset)} {FormatNumber(field.Height * 0.5)} m\n" +
                    $"{FormatNumber(field.Width * 0.42)} {FormatNumber(inset)} l\n" +
                    $"{FormatNumber(right)} {FormatNumber(top)} l\nS\n");
                break;
            case PdfCheckBoxMark.Cross:
                WriteAscii(output,
                    $"2 w\n{FormatNumber(inset)} {FormatNumber(inset)} m\n" +
                    $"{FormatNumber(right)} {FormatNumber(top)} l\n" +
                    $"{FormatNumber(right)} {FormatNumber(inset)} m\n" +
                    $"{FormatNumber(inset)} {FormatNumber(top)} l\nS\n");
                break;
            case PdfCheckBoxMark.Circle:
                WriteCircle(output, field.Width / 2, field.Height / 2,
                    Math.Max(0, Math.Min(field.Width, field.Height) / 2 - inset));
                output.Write("f\n"u8);
                break;
            case PdfCheckBoxMark.Diamond:
                WriteAscii(output,
                    $"{FormatNumber(field.Width / 2)} {FormatNumber(top)} m\n" +
                    $"{FormatNumber(right)} {FormatNumber(field.Height / 2)} l\n" +
                    $"{FormatNumber(field.Width / 2)} {FormatNumber(inset)} l\n" +
                    $"{FormatNumber(inset)} {FormatNumber(field.Height / 2)} l\nh\nf\n");
                break;
            case PdfCheckBoxMark.Square:
                WriteAscii(output,
                    $"{FormatNumber(inset)} {FormatNumber(inset)} " +
                    $"{FormatNumber(Math.Max(0, field.Width - inset * 2))} " +
                    $"{FormatNumber(Math.Max(0, field.Height - inset * 2))} re\nf\n");
                break;
            case PdfCheckBoxMark.Star:
                double centerX = field.Width / 2;
                double centerY = field.Height / 2;
                double outer = Math.Max(0, Math.Min(field.Width, field.Height) / 2 - inset);
                double inner = outer * 0.42;
                for (int point = 0; point < 10; point++)
                {
                    double angle = -Math.PI / 2 + point * Math.PI / 5;
                    double radius = point % 2 == 0 ? outer : inner;
                    string operation = point == 0 ? "m" : "l";
                    WriteAscii(output,
                        $"{FormatNumber(centerX + Math.Cos(angle) * radius)} " +
                        $"{FormatNumber(centerY + Math.Sin(angle) * radius)} {operation}\n");
                }
                output.Write("h\nf\n"u8);
                break;
            default:
                throw new InvalidOperationException($"Unsupported checkbox mark: {field.Mark}.");
        }
    }

    private static void AddRadioGroupObjects(
        List<PdfIndirectObject> objects,
        AllocatedRadioGroup allocatedGroup,
        IReadOnlyList<AllocatedPage> pages)
    {
        RadioGroupDefinition group = allocatedGroup.Definition;
        PdfFormFieldAppearanceStyle style = group.RadioOptions.AppearanceStyle!;
        PdfName selected = Name(group.SelectedValue ?? "Off");
        PdfName defaultSelected = Name(group.DefaultSelectedValue ?? "Off");
        var groupEntries = new List<(string Name, PdfObject Value)>
        {
                ("FT", Name("Btn")),
                ("Ff", new PdfInteger((1 << 15)
                    | (group.RadioOptions.NoToggleToOff ? 1 << 14 : 0)
                    | (group.RadioOptions.RadiosInUnison ? 1 << 25 : 0)
                    | FormFieldFlags(group.FieldOptions))),
                ("T", UnicodeString(group.Name)),
                ("V", selected),
                ("DV", defaultSelected),
                ("Kids", new PdfArray(allocatedGroup.Widgets.Select(widget =>
                    (PdfObject)new PdfIndirectReference(widget.WidgetNumber, 0))))
        };
        AddFieldMetadata(groupEntries, group.Metadata);
        objects.Add(new PdfIndirectObject(
            allocatedGroup.ParentNumber, 0, Dictionary([.. groupEntries]), 0));

        foreach (AllocatedRadioWidget allocatedWidget in allocatedGroup.Widgets)
        {
            PdfRadioButtonOption option = allocatedWidget.Option;
            PdfName onState = Name(option.ExportValue);
            PdfName appearanceState = group.SelectedValue == option.ExportValue ? onState : Name("Off");
            objects.Add(new PdfIndirectObject(allocatedWidget.WidgetNumber, 0,
                Dictionary(
                    ("Type", Name("Annot")),
                    ("Subtype", Name("Widget")),
                    ("Parent", new PdfIndirectReference(allocatedGroup.ParentNumber, 0)),
                    ("Rect", new PdfArray([
                        Number(option.X), Number(option.Y),
                        Number(option.X + option.Width), Number(option.Y + option.Height)])),
                    ("P", new PdfIndirectReference(pages[option.PageIndex].PageNumber, 0)),
                    ("F", new PdfInteger(FormWidgetFlags(group.FieldOptions.Visibility))),
                    ("AS", appearanceState),
                    ("MK", FormFieldAppearanceCharacteristics(style)),
                    ("BS", FormFieldBorderDictionary(style)),
                    ("AP", Dictionary(("N", new PdfDictionary([
                        new KeyValuePair<PdfName, PdfObject>(
                            Name("Off"), new PdfIndirectReference(allocatedWidget.OffAppearanceNumber, 0)),
                        new KeyValuePair<PdfName, PdfObject>(
                            onState, new PdfIndirectReference(allocatedWidget.OnAppearanceNumber, 0))]))))), 0));
            objects.Add(new PdfIndirectObject(allocatedWidget.OffAppearanceNumber, 0,
                RadioAppearance(option, style, selected: false), 0));
            objects.Add(new PdfIndirectObject(allocatedWidget.OnAppearanceNumber, 0,
                RadioAppearance(option, style, selected: true), 0));
        }
    }

    private static PdfStream RadioAppearance(
        PdfRadioButtonOption option, PdfFormFieldAppearanceStyle style, bool selected)
    {
        using var output = new MemoryStream();
        double centerX = option.Width / 2;
        double centerY = option.Height / 2;
        double radius = Math.Max(0, Math.Min(option.Width, option.Height) / 2 - 0.75);
        output.Write("q\n"u8);
        if (style.BackgroundColor.HasValue)
        {
            WriteAscii(output, $"{ColorOperands(style.BackgroundColor.Value)} rg\n");
            WriteCircle(output, centerX, centerY, radius);
            output.Write("f\n"u8);
        }
        if (style.BorderColor.HasValue && style.BorderWidth > 0)
        {
            WriteAscii(output,
                $"{ColorOperands(style.BorderColor.Value)} RG\n" +
                $"{FormatNumber(style.BorderWidth)} w\n");
            if (style.BorderStyle == PdfFormFieldBorderStyle.Dashed)
                WriteAscii(output,
                    $"[{string.Join(' ', style.DashPattern!.Select(FormatNumber))}] 0 d\n");
            if (style.BorderStyle == PdfFormFieldBorderStyle.Underline)
                WriteAscii(output,
                    $"0 {FormatNumber(style.BorderWidth / 2)} m\n" +
                    $"{FormatNumber(option.Width)} {FormatNumber(style.BorderWidth / 2)} l\nS\n");
            else
            {
                WriteCircle(output, centerX, centerY,
                    Math.Max(0, radius - style.BorderWidth / 2));
                output.Write("S\n"u8);
                if (style.BorderStyle is PdfFormFieldBorderStyle.Beveled
                    or PdfFormFieldBorderStyle.Inset)
                {
                    PdfRgbColor edge = BlendRgb(style.BorderColor.Value,
                        style.BorderStyle == PdfFormFieldBorderStyle.Beveled
                            ? new PdfRgbColor(1, 1, 1) : new PdfRgbColor(0, 0, 0), 0.55);
                    WriteAscii(output, $"{ColorOperands(edge)} RG\n");
                    WriteCircle(output, centerX, centerY,
                        Math.Max(0, radius - style.BorderWidth * 1.5));
                    output.Write("S\n"u8);
                }
            }
            if (style.BorderStyle == PdfFormFieldBorderStyle.Dashed)
                output.Write("[] 0 d\n"u8);
        }
        if (selected)
        {
            WriteAscii(output, $"{ColorOperands(style.TextColor)} rg\n");
            WriteCircle(output, centerX, centerY, radius * 0.48);
            output.Write("f\n"u8);
        }
        output.Write("Q\n"u8);
        return new PdfStream(Dictionary(
            ("Type", Name("XObject")),
            ("Subtype", Name("Form")),
            ("FormType", new PdfInteger(1)),
            ("BBox", new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                Number(option.Width), Number(option.Height)])),
            ("Resources", Dictionary())), output.ToArray());
    }

    private static void WriteCircle(Stream output, double x, double y, double radius)
    {
        const double kappa = 0.5522847498307936;
        double control = radius * kappa;
        WriteAscii(output, $"{FormatNumber(x + radius)} {FormatNumber(y)} m\n");
        WriteAscii(output, $"{FormatNumber(x + radius)} {FormatNumber(y + control)} {FormatNumber(x + control)} {FormatNumber(y + radius)} {FormatNumber(x)} {FormatNumber(y + radius)} c\n");
        WriteAscii(output, $"{FormatNumber(x - control)} {FormatNumber(y + radius)} {FormatNumber(x - radius)} {FormatNumber(y + control)} {FormatNumber(x - radius)} {FormatNumber(y)} c\n");
        WriteAscii(output, $"{FormatNumber(x - radius)} {FormatNumber(y - control)} {FormatNumber(x - control)} {FormatNumber(y - radius)} {FormatNumber(x)} {FormatNumber(y - radius)} c\n");
        WriteAscii(output, $"{FormatNumber(x + control)} {FormatNumber(y - radius)} {FormatNumber(x + radius)} {FormatNumber(y - control)} {FormatNumber(x + radius)} {FormatNumber(y)} c\nh\n");
    }

    private static void AddChoiceFieldObjects(
        List<PdfIndirectObject> objects,
        AllocatedChoiceField allocatedField,
        IReadOnlyList<AllocatedPage> pages,
        PdfName fontResource,
        int fontNumber,
        EmbeddedFontUsage? fontUsage)
    {
        ChoiceFieldDefinition field = allocatedField.Definition;
        int flags = (field.IsComboBox ? 1 << 17 : 0)
            | (field.Editable ? 1 << 18 : 0)
            | (field.IsMultiSelect ? 1 << 21 : 0)
            | (field.ChoiceOptions.SortOptions ? 1 << 19 : 0)
            | (field.ChoiceOptions.DoNotSpellCheck ? 1 << 22 : 0)
            | (field.ChoiceOptions.CommitOnSelectionChange ? 1 << 26 : 0)
            | FormFieldFlags(field.FieldOptions);
        var entries = new List<(string Name, PdfObject Value)>
        {
                ("Type", Name("Annot")),
                ("Subtype", Name("Widget")),
                ("FT", Name("Ch")),
                ("Ff", new PdfInteger(flags)),
                ("T", UnicodeString(field.Name)),
                ("Opt", new PdfArray(field.Options.Select(ChoiceOptionObject))),
                ("Rect", new PdfArray([
                    Number(field.X), Number(field.Y),
                    Number(field.X + field.Width), Number(field.Y + field.Height)])),
                ("P", new PdfIndirectReference(pages[field.PageIndex].PageNumber, 0)),
                ("F", new PdfInteger(FormWidgetFlags(field.FieldOptions.Visibility))),
                ("DA", Latin1String(
                    $"{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf " +
                    $"{ColorOperands(field.ChoiceOptions.AppearanceStyle!.TextColor)} rg")),
                ("MK", FormFieldAppearanceCharacteristics(
                    field.ChoiceOptions.AppearanceStyle)),
                ("BS", FormFieldBorderDictionary(
                    field.ChoiceOptions.AppearanceStyle)),
                ("AP", Dictionary(("N", new PdfIndirectReference(allocatedField.AppearanceNumber, 0))))
        };
        if (!field.IsMultiSelect || field.SelectedValues.Count != 0)
        {
            PdfObject value = field.IsMultiSelect
                ? new PdfArray(field.SelectedValues.Select(value => (PdfObject)UnicodeString(value)))
                : UnicodeString(field.SelectedValues[0]);
            entries.Add(("V", value));
        }
        if (!field.IsMultiSelect || field.DefaultSelectedValues.Count != 0)
        {
            PdfObject defaultValue = field.IsMultiSelect
                ? new PdfArray(field.DefaultSelectedValues.Select(value =>
                    (PdfObject)UnicodeString(value)))
                : UnicodeString(field.DefaultSelectedValues[0]);
            entries.Add(("DV", defaultValue));
        }
        if (field.IsMultiSelect && field.SelectedValues.Count != 0)
        {
            entries.Add(("I", new PdfArray(field.SelectedValues
                .Select(value => (PdfObject)new PdfInteger(
                    field.Options.ToList().FindIndex(option =>
                        string.Equals(option.ExportValue, value, StringComparison.Ordinal)))))));
        }
        if (!field.IsComboBox && field.TopIndex != 0)
            entries.Add(("TI", new PdfInteger(field.TopIndex)));
        if (field.ChoiceOptions.Alignment != PdfTextFieldAlignment.Left)
            entries.Add(("Q", new PdfInteger((int)field.ChoiceOptions.Alignment)));
        AddFieldMetadata(entries, field.Metadata);
        objects.Add(new PdfIndirectObject(
            allocatedField.FieldNumber, 0, Dictionary([.. entries]), 0));

        byte[] appearance = field.IsComboBox
            ? BuildChoiceTextAppearance(field, fontResource, fontUsage)
            : BuildListBoxAppearance(field, fontResource, fontUsage);
        objects.Add(new PdfIndirectObject(allocatedField.AppearanceNumber, 0,
            new PdfStream(Dictionary(
                ("Type", Name("XObject")),
                ("Subtype", Name("Form")),
                ("FormType", new PdfInteger(1)),
                ("BBox", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    Number(field.Width), Number(field.Height)])),
                ("Resources", Dictionary(("Font", new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(
                        fontResource, new PdfIndirectReference(fontNumber, 0))]))))),
                appearance), 0));
    }

    private static byte[] BuildSimpleTextAppearance(
        double width,
        double height,
        double fontSize,
        string value,
        PdfName fontResource,
        TrueTypeFont? embeddedFont,
        PdfFormFieldAppearanceStyle style,
        PdfTextFieldAlignment alignment,
        EmbeddedFontUsage? fontUsage)
    {
        using var output = new MemoryStream();
        WriteFormFieldBackgroundAndBorder(output, width, height, style, clip: false);
        WriteAscii(output,
            $"BT\n{NameToken(fontResource)} {FormatNumber(fontSize)} Tf\n" +
            $"{ColorOperands(style.TextColor)} rg\n" +
            $"{FormatNumber(SimpleTextX(width, fontSize, value, embeddedFont, alignment))} " +
            $"{FormatNumber(Math.Max(1, (height - fontSize) / 2))} Td\n");
        WriteShownText(output, value, embeddedFont, fontUsage);
        output.Write("ET\nQ\n"u8);
        return output.ToArray();
    }

    private static double SimpleTextX(
        double width, double fontSize, string value, TrueTypeFont? embeddedFont,
        PdfTextFieldAlignment alignment)
    {
        double textWidth = embeddedFont is null
            ? value.EnumerateRunes().Count() * fontSize * 0.55
            : embeddedFont.MapText(value).Sum(mapping =>
                embeddedFont.GetPdfAdvanceWidth(mapping.Glyph))
                * fontSize / 1000;
        return alignment switch
        {
            PdfTextFieldAlignment.Left => 3,
            PdfTextFieldAlignment.Center => Math.Max(1, (width - textWidth) / 2),
            PdfTextFieldAlignment.Right => Math.Max(1, width - textWidth - 3),
            _ => throw new ArgumentOutOfRangeException(nameof(alignment))
        };
    }

    private static byte[] BuildTextFieldAppearance(
        TextFieldDefinition field, PdfName fontResource, EmbeddedFontUsage? fontUsage)
    {
        if (field.Options.Comb)
            return BuildCombTextFieldAppearance(field, fontResource, fontUsage);
        if (!field.Options.Multiline)
            return BuildAlignedTextFieldAppearance(field, fontResource, fontUsage);

        using var output = new MemoryStream();
        WriteFormFieldBackgroundAndBorder(
            output, field.Width, field.Height, field.AppearanceStyle, clip: true);
        double leading = field.FontSize * 1.2;
        double baseline = field.Height - field.FontSize - 2;
        foreach (string line in WrapTextFieldLines(field))
        {
            if (baseline < 1)
                break;
            double x = TextFieldTextX(field, line);
            WriteAscii(output,
                $"BT\n{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf\n" +
                $"{ColorOperands(field.AppearanceStyle.TextColor)} rg\n" +
                $"{FormatNumber(x)} {FormatNumber(baseline)} Td\n");
            WriteShownText(output, line, field.EmbeddedFont, fontUsage);
            output.Write("ET\n"u8);
            baseline -= leading;
        }
        output.Write("Q\n"u8);
        return output.ToArray();
    }

    private static string TextFieldAppearanceValue(TextFieldDefinition field) =>
        field.Options.Password
            ? new string('*', field.Value.EnumerateRunes().Count())
            : field.Value;

    private static byte[] BuildAlignedTextFieldAppearance(
        TextFieldDefinition field, PdfName fontResource, EmbeddedFontUsage? fontUsage)
    {
        string value = TextFieldAppearanceValue(field);
        using var output = new MemoryStream();
        WriteFormFieldBackgroundAndBorder(
            output, field.Width, field.Height, field.AppearanceStyle, clip: false);
        WriteAscii(output,
            $"BT\n{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf\n" +
            $"{ColorOperands(field.AppearanceStyle.TextColor)} rg\n" +
            $"{FormatNumber(TextFieldTextX(field, value))} " +
            $"{FormatNumber(Math.Max(1, (field.Height - field.FontSize) / 2))} Td\n");
        WriteShownText(output, value, field.EmbeddedFont, fontUsage);
        output.Write("ET\nQ\n"u8);
        return output.ToArray();
    }

    private static double TextFieldTextX(TextFieldDefinition field, string value)
    {
        double textWidth = MeasureTextFieldText(field, value);
        return field.Options.Alignment switch
        {
            PdfTextFieldAlignment.Left => 3,
            PdfTextFieldAlignment.Center => Math.Max(1, (field.Width - textWidth) / 2),
            PdfTextFieldAlignment.Right => Math.Max(1, field.Width - textWidth - 3),
            _ => throw new InvalidOperationException(
                $"Unsupported text-field alignment: {field.Options.Alignment}.")
        };
    }

    private static double MeasureTextFieldText(TextFieldDefinition field, string value) =>
        field.EmbeddedFont is null
            ? value.EnumerateRunes().Count() * field.FontSize * 0.55
            : field.EmbeddedFont.MapText(value).Sum(mapping =>
                field.EmbeddedFont.GetPdfAdvanceWidth(mapping.Glyph))
                * field.FontSize / 1000;

    private static List<string> WrapTextFieldLines(TextFieldDefinition field)
    {
        double availableWidth = Math.Max(1, field.Width - 6);
        var lines = new List<string>();
        foreach (string paragraph in field.Value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }
            string current = string.Empty;
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : $"{current} {word}";
                if (MeasureTextFieldText(field, candidate) <= availableWidth)
                {
                    current = candidate;
                    continue;
                }
                if (current.Length != 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }
                IEnumerable<string> textElements = field.EmbeddedFont is null
                    ? word.EnumerateRunes().Select(rune => rune.ToString())
                    : field.EmbeddedFont.MapText(word).Select(mapping => mapping.UnicodeSequence);
                foreach (string textElement in textElements)
                {
                    string next = current + textElement;
                    if (current.Length != 0 && MeasureTextFieldText(field, next) > availableWidth)
                    {
                        lines.Add(current);
                        current = textElement;
                    }
                    else
                    {
                        current = next;
                    }
                }
            }
            if (current.Length != 0)
                lines.Add(current);
        }
        return lines;
    }

    private static void ValidateInitialTextFieldFit(TextFieldDefinition field)
    {
        if (!field.Options.DoNotScroll || field.Options.Comb)
            return;
        if (!field.Options.Multiline)
        {
            if (MeasureTextFieldText(field, TextFieldAppearanceValue(field)) > field.Width - 6)
                throw new ArgumentException(
                    "The initial value does not fit in a no-scroll text field.");
            return;
        }
        double leading = field.FontSize * 1.2;
        double baseline = field.Height - field.FontSize - 2;
        int visibleLines = 0;
        while (baseline >= 1)
        {
            visibleLines++;
            baseline -= leading;
        }
        if (WrapTextFieldLines(field).Count > visibleLines)
            throw new ArgumentException(
                "The initial value does not fit in a no-scroll multiline text field.");
    }

    private static byte[] BuildCombTextFieldAppearance(
        TextFieldDefinition field, PdfName fontResource, EmbeddedFontUsage? fontUsage)
    {
        int cells = field.Options.MaximumLength!.Value;
        double cellWidth = field.Width / cells;
        using var output = new MemoryStream();
        WriteFormFieldBackgroundAndBorder(
            output, field.Width, field.Height, field.AppearanceStyle, clip: false);
        if (field.AppearanceStyle.BorderColor.HasValue
            && field.AppearanceStyle.BorderWidth > 0)
        {
            WriteAscii(output,
                $"{ColorOperands(field.AppearanceStyle.BorderColor.Value)} RG\n" +
                $"{FormatNumber(field.AppearanceStyle.BorderWidth)} w\n");
            for (int index = 1; index < cells; index++)
            {
                double x = cellWidth * index;
                WriteAscii(output,
                    $"{FormatNumber(x)} 0.5 m\n{FormatNumber(x)} " +
                    $"{FormatNumber(Math.Max(0.5, field.Height - 0.5))} l\nS\n");
            }
        }
        int glyphCount = field.EmbeddedFont?.MapText(field.Value).Count
            ?? field.Value.EnumerateRunes().Count();
        int cell = field.Options.Alignment switch
        {
            PdfTextFieldAlignment.Left => 0,
            PdfTextFieldAlignment.Center => (cells - glyphCount) / 2,
            PdfTextFieldAlignment.Right => cells - glyphCount,
            _ => throw new InvalidOperationException(
                $"Unsupported text-field alignment: {field.Options.Alignment}.")
        };
        IEnumerable<string> shownValues = field.EmbeddedFont is null
            ? field.Value.EnumerateRunes().Select(rune => rune.ToString())
            : field.EmbeddedFont.MapText(field.Value).Select(mapping => mapping.UnicodeSequence);
        foreach (string shownValue in shownValues)
        {
            double x = cellWidth * cell + Math.Max(1, (cellWidth - field.FontSize * 0.55) / 2);
            double y = Math.Max(1, (field.Height - field.FontSize) / 2);
            WriteAscii(output,
                $"BT\n{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf\n" +
                $"{ColorOperands(field.AppearanceStyle.TextColor)} rg\n" +
                $"{FormatNumber(x)} {FormatNumber(y)} Td\n");
            WriteShownText(output, shownValue, field.EmbeddedFont, fontUsage);
            output.Write("ET\n"u8);
            cell++;
        }
        output.Write("Q\n"u8);
        return output.ToArray();
    }

    private static void AddPushButtonObjects(
        List<PdfIndirectObject> objects,
        AllocatedPushButton allocatedField,
        IReadOnlyList<AllocatedPage> pages,
        PdfName fontResource,
        int fontNumber,
        EmbeddedFontUsage? fontUsage,
        int? iconNumber,
        int? rolloverIconNumber,
        int? downIconNumber,
        int? structureDestinationNumber)
    {
        PushButtonDefinition field = allocatedField.Definition;
        int flags = (1 << 16) | FormFieldFlags(field.FieldOptions);
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")),
            ("Subtype", Name("Widget")),
            ("FT", Name("Btn")),
            ("Ff", new PdfInteger(flags)),
            ("T", UnicodeString(field.Name)),
            ("Rect", new PdfArray([
                Number(field.X), Number(field.Y),
                Number(field.X + field.Width), Number(field.Y + field.Height)])),
            ("P", new PdfIndirectReference(pages[field.PageIndex].PageNumber, 0)),
            ("F", new PdfInteger(FormWidgetFlags(field.FieldOptions.Visibility))),
            ("AS", Name("Normal")),
            ("H", Name(PushButtonHighlightName(field.HighlightMode))),
            ("DA", Latin1String(
                $"{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf " +
                $"{ColorOperands(field.AppearanceStyle.TextColor)} rg")),
            ("MK", PushButtonAppearanceCharacteristics(
                field.Label, field.AppearanceStyle, field.AppearanceOptions,
                iconNumber, rolloverIconNumber, downIconNumber)),
            ("BS", FormFieldBorderDictionary(field.AppearanceStyle)),
            ("A", field.Uri is not null
                ? Dictionary(("S", Name("URI")), ("URI", UnicodeString(field.Uri)))
                : field.NamedDestination is not null
                    ? Dictionary(("S", Name("GoTo")), ("D", UnicodeString(field.NamedDestination)))
                    : field.SubmitUri is not null
                        ? SubmitPdfAction(field.SubmitUri, field.SubmitFields, field.ExcludeSubmitFields)
                    : field.IsResetAction
                        ? ResetFormAction(field.ResetFields, field.ExcludeResetFields)
                    : Dictionary(
                    ("S", Name("GoTo")),
                    ("D", DestinationArray(
                        new PdfIndirectReference(structureDestinationNumber
                            ?? pages[field.DestinationPageIndex!.Value].PageNumber, 0),
                        field.Destination!)))),
            ("AP", PushButtonAppearanceDictionary(allocatedField))
        };
        AddFieldMetadata(entries, field.Metadata);
        objects.Add(new PdfIndirectObject(
            allocatedField.FieldNumber, 0, Dictionary([.. entries]), 0));

        byte[] appearance = BuildPushButtonAppearance(
            field, field.Label, fontResource, iconNumber, fontUsage);
        objects.Add(new PdfIndirectObject(allocatedField.AppearanceNumber, 0,
            new PdfStream(Dictionary(
                ("Type", Name("XObject")),
                ("Subtype", Name("Form")),
                ("FormType", new PdfInteger(1)),
                ("BBox", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    Number(field.Width), Number(field.Height)])),
                ("Resources", PushButtonAppearanceResources(
                    fontResource, fontNumber, iconNumber))),
                appearance), 0));
        AddAlternatePushButtonAppearance(
            objects, allocatedField.RolloverAppearanceNumber,
            field.AppearanceOptions.RolloverLabel ?? field.Label,
            field, fontResource, fontNumber, rolloverIconNumber ?? iconNumber, fontUsage);
        AddAlternatePushButtonAppearance(
            objects, allocatedField.DownAppearanceNumber,
            field.AppearanceOptions.DownLabel ?? field.Label,
            field, fontResource, fontNumber, downIconNumber ?? iconNumber, fontUsage);
    }

    private static PdfDictionary PushButtonAppearanceDictionary(AllocatedPushButton field)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("N", Dictionary(("Normal",
                new PdfIndirectReference(field.AppearanceNumber, 0))))
        };
        if (field.RolloverAppearanceNumber.HasValue)
            entries.Add(("R", Dictionary(("Normal",
                new PdfIndirectReference(field.RolloverAppearanceNumber.Value, 0)))));
        if (field.DownAppearanceNumber.HasValue)
            entries.Add(("D", Dictionary(("Normal",
                new PdfIndirectReference(field.DownAppearanceNumber.Value, 0)))));
        return Dictionary([.. entries]);
    }

    private static PdfDictionary PushButtonAppearanceResources(
        PdfName fontResource, int fontNumber, int? iconNumber)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Font", new PdfDictionary([
                new KeyValuePair<PdfName, PdfObject>(
                    fontResource, new PdfIndirectReference(fontNumber, 0))]))
        };
        if (iconNumber.HasValue)
            entries.Add(("XObject", Dictionary(
                ("BtnIcon", new PdfIndirectReference(iconNumber.Value, 0)))));
        return Dictionary([.. entries]);
    }

    private static byte[] BuildPushButtonAppearance(
        PushButtonDefinition field, string label, PdfName fontResource, int? iconNumber,
        EmbeddedFontUsage? fontUsage)
    {
        using var output = new MemoryStream();
        WriteFormFieldBackgroundAndBorder(
            output, field.Width, field.Height, field.AppearanceStyle, clip: true);
        PdfPushButtonCaptionPosition position = field.AppearanceOptions.CaptionPosition;
        double inset = field.AppearanceOptions.FitIconToBounds
            ? 0 : Math.Max(1, field.AppearanceStyle.BorderWidth);
        double x = inset;
        double y = inset;
        double width = Math.Max(0, field.Width - inset * 2);
        double height = Math.Max(0, field.Height - inset * 2);
        double textX = x;
        double textY = y;
        double textWidth = width;
        double textHeight = height;
        double iconX = x;
        double iconY = y;
        double iconWidth = width;
        double iconHeight = height;
        double captionHeight = Math.Min(height, field.FontSize * 1.3);
        switch (position)
        {
            case PdfPushButtonCaptionPosition.CaptionOnly:
                iconWidth = iconHeight = 0;
                break;
            case PdfPushButtonCaptionPosition.IconOnly:
                textWidth = textHeight = 0;
                break;
            case PdfPushButtonCaptionPosition.CaptionBelowIcon:
                textHeight = captionHeight;
                iconY += captionHeight;
                iconHeight = Math.Max(0, height - captionHeight);
                break;
            case PdfPushButtonCaptionPosition.CaptionAboveIcon:
                textY += Math.Max(0, height - captionHeight);
                textHeight = captionHeight;
                iconHeight = Math.Max(0, height - captionHeight);
                break;
            case PdfPushButtonCaptionPosition.CaptionRightOfIcon:
                iconWidth = width / 2;
                textX += iconWidth;
                textWidth = width - iconWidth;
                break;
            case PdfPushButtonCaptionPosition.CaptionLeftOfIcon:
                textWidth = width / 2;
                iconX += textWidth;
                iconWidth = width - textWidth;
                break;
            case PdfPushButtonCaptionPosition.CaptionOverIcon:
                break;
            default:
#pragma warning disable CA2208 // The overload supplies parameter name, actual enum value, and message.
                throw new ArgumentOutOfRangeException(
                    nameof(position), position, "The push-button caption position is not supported.");
#pragma warning restore CA2208
        }
        if (iconNumber.HasValue && iconWidth > 0 && iconHeight > 0)
            WritePushButtonIcon(output, field.AppearanceOptions, iconX, iconY, iconWidth, iconHeight);
        if (position != PdfPushButtonCaptionPosition.IconOnly && textWidth > 0 && textHeight > 0)
        {
            double baseline = textY + Math.Max(1, (textHeight - field.FontSize) / 2);
            double captionX = textX + SimpleTextX(
                textWidth, field.FontSize, label, field.EmbeddedFont,
                field.AppearanceOptions.Alignment);
            WriteAscii(output,
                $"BT\n{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf\n" +
                $"{ColorOperands(field.AppearanceStyle.TextColor)} rg\n" +
                $"{FormatNumber(captionX)} {FormatNumber(baseline)} Td\n");
            WriteShownText(output, label, field.EmbeddedFont, fontUsage);
            output.Write("ET\n"u8);
        }
        output.Write("Q\n"u8);
        return output.ToArray();
    }

    private static void WritePushButtonIcon(
        Stream output, PdfPushButtonAppearanceOptions options,
        double x, double y, double width, double height)
    {
        PdfImage icon = options.Icon!;
        double scaleX = width / icon.Width;
        double scaleY = height / icon.Height;
        if (options.ProportionalIconScaling)
            scaleX = scaleY = Math.Min(scaleX, scaleY);
        switch (options.IconScaleMode)
        {
            case PdfPushButtonIconScaleMode.Always:
                break;
            case PdfPushButtonIconScaleMode.Never:
                scaleX = scaleY = 1;
                break;
            case PdfPushButtonIconScaleMode.WhenTooLarge:
                scaleX = Math.Min(1, scaleX);
                scaleY = Math.Min(1, scaleY);
                break;
            case PdfPushButtonIconScaleMode.WhenTooSmall:
                scaleX = Math.Max(1, scaleX);
                scaleY = Math.Max(1, scaleY);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.IconScaleMode,
                    "The push-button icon scale mode is unsupported.");
        }
        double drawnWidth = icon.Width * scaleX;
        double drawnHeight = icon.Height * scaleY;
        double drawnX = x + (width - drawnWidth) * options.IconHorizontalAlignment;
        double drawnY = y + (height - drawnHeight) * options.IconVerticalAlignment;
        WriteAscii(output,
            $"q\n{FormatNumber(drawnWidth)} 0 0 {FormatNumber(drawnHeight)} " +
            $"{FormatNumber(drawnX)} {FormatNumber(drawnY)} cm\n/BtnIcon Do\nQ\n");
    }

    private static void AddAlternatePushButtonAppearance(
        List<PdfIndirectObject> objects, int? objectNumber, string label,
        PushButtonDefinition field, PdfName fontResource, int fontNumber, int? iconNumber,
        EmbeddedFontUsage? fontUsage)
    {
        if (!objectNumber.HasValue)
            return;
        byte[] appearance = BuildPushButtonAppearance(
            field, label, fontResource, iconNumber, fontUsage);
        objects.Add(new PdfIndirectObject(objectNumber.Value, 0,
            new PdfStream(Dictionary(
                ("Type", Name("XObject")),
                ("Subtype", Name("Form")),
                ("FormType", new PdfInteger(1)),
                ("BBox", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    Number(field.Width), Number(field.Height)])),
                ("Resources", PushButtonAppearanceResources(
                    fontResource, fontNumber, iconNumber))),
                appearance), 0));
    }

    private static PdfDictionary ResetFormAction(
        IReadOnlyList<string>? fields, bool excludeFields)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("S", Name("ResetForm"))
        };
        if (fields is not null)
            entries.Add(("Fields", new PdfArray(
                fields.Select(field => (PdfObject)UnicodeString(field)))));
        if (excludeFields)
            entries.Add(("Flags", new PdfInteger(1)));
        return Dictionary([.. entries]);
    }

    private static string PushButtonHighlightName(PdfPushButtonHighlightMode mode) => mode switch
    {
        PdfPushButtonHighlightMode.None => "N",
        PdfPushButtonHighlightMode.Invert => "I",
        PdfPushButtonHighlightMode.Outline => "O",
        PdfPushButtonHighlightMode.Push => "P",
        PdfPushButtonHighlightMode.Toggle => "T",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static PdfDictionary SubmitPdfAction(
        string uri, IReadOnlyList<string>? fields, bool excludeFields)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("S", Name("SubmitForm")),
            ("F", Dictionary(("Type", Name("Filespec")), ("FS", Name("URL")),
                ("F", UnicodeString(uri)))),
            ("Flags", new PdfInteger((1 << 8) | (excludeFields ? 1 : 0)))
        };
        if (fields is not null)
            entries.Add(("Fields", new PdfArray(
                fields.Select(field => (PdfObject)UnicodeString(field)))));
        return Dictionary([.. entries]);
    }

    private static PdfDictionary SignatureFieldLockDictionary(PdfSignatureFieldLock fieldLock)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("SigFieldLock")),
            ("Action", Name(fieldLock.Action.ToString()))
        };
        if (fieldLock.Fields is not null)
            entries.Add(("Fields", new PdfArray(
                fieldLock.Fields.Select(field => (PdfObject)UnicodeString(field)))));
        if (fieldLock.Permission.HasValue)
            entries.Add(("P", new PdfInteger((int)fieldLock.Permission.Value)));
        return Dictionary([.. entries]);
    }

    private static PdfDictionary SignatureSeedValueDictionary(PdfSignatureSeedValue seedValue)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("SV"))
        };
        int flags = 0;
        if (seedValue.Handler.HasValue)
        {
            entries.Add(("Filter", Name(SignatureHandlerName(seedValue.Handler.Value))));
            if (seedValue.RequireHandler) flags |= 1;
        }
        if (seedValue.ParserVersion.HasValue)
        {
            entries.Add(("V", new PdfReal((int)seedValue.ParserVersion.Value)));
            if (seedValue.RequireParserVersion) flags |= 1 << 2;
        }
        if (seedValue.SubFilters is not null)
        {
            entries.Add(("SubFilter", new PdfArray(seedValue.SubFilters.Select(subFilter =>
                (PdfObject)Name(SignatureSubFilterName(subFilter))))));
            if (seedValue.RequireSubFilter) flags |= 1 << 1;
        }
        if (seedValue.DigestMethods is not null)
        {
            entries.Add(("DigestMethod", new PdfArray(seedValue.DigestMethods.Select(method =>
                (PdfObject)Name(SignatureDigestMethodName(method))))));
            if (seedValue.RequireDigestMethod) flags |= 1 << 6;
        }
        if (seedValue.AddRevocationInformation)
        {
            entries.Add(("AddRevInfo", new PdfBoolean(true)));
            if (seedValue.RequireRevocationInformation) flags |= 1 << 5;
        }
        if (seedValue.Reasons is not null)
        {
            entries.Add(("Reasons", new PdfArray(seedValue.Reasons.Select(reason =>
                (PdfObject)UnicodeString(reason)))));
            if (seedValue.RequireReason) flags |= 1 << 3;
        }
        if (seedValue.LegalAttestations is not null)
        {
            entries.Add(("LegalAttestation", new PdfArray(seedValue.LegalAttestations.Select(
                attestation => (PdfObject)UnicodeString(attestation)))));
            if (seedValue.RequireLegalAttestation) flags |= 1 << 4;
        }
        if (seedValue.CertificationPermission.HasValue)
            entries.Add(("MDP", Dictionary(("P", new PdfInteger(
                (int)seedValue.CertificationPermission.Value)))));
        if (seedValue.Timestamp is not null)
            entries.Add(("TimeStamp", Dictionary(
                ("URL", Latin1String(seedValue.Timestamp.ServerUrl)),
                ("Ff", new PdfInteger(seedValue.Timestamp.Required ? 1 : 0)))));
        if (seedValue.Certificate is not null)
        {
            var certificateEntries = new List<(string Name, PdfObject Value)>
            {
                ("Type", Name("SVCert"))
            };
            if (seedValue.Certificate.SubjectCertificates is not null)
                certificateEntries.Add(("Subject", new PdfArray(
                    seedValue.Certificate.SubjectCertificates.Select(certificate =>
                        (PdfObject)new PdfString(certificate, PdfStringForm.Hexadecimal)))));
            if (seedValue.Certificate.IssuerCertificates is not null)
                certificateEntries.Add(("Issuer", new PdfArray(
                    seedValue.Certificate.IssuerCertificates.Select(certificate =>
                        (PdfObject)new PdfString(certificate, PdfStringForm.Hexadecimal)))));
            if (seedValue.Certificate.CertificatePolicyObjectIdentifiers is not null)
                certificateEntries.Add(("OID", new PdfArray(
                    seedValue.Certificate.CertificatePolicyObjectIdentifiers.Select(oid =>
                        (PdfObject)Latin1String(oid)))));
            if (seedValue.Certificate.SubjectDistinguishedNames is not null)
                certificateEntries.Add(("SubjectDN", new PdfArray(
                    seedValue.Certificate.SubjectDistinguishedNames.Select(distinguishedName =>
                        (PdfObject)Dictionary([.. distinguishedName.Attributes.Select(attribute =>
                            (attribute.Key, (PdfObject)UnicodeString(attribute.Value)))])))));
            if (seedValue.Certificate.KeyUsages is not null)
                certificateEntries.Add(("KeyUsage", new PdfArray(
                    seedValue.Certificate.KeyUsages.Select(usage =>
                        (PdfObject)Latin1String(CertificateKeyUsagePattern(usage))))));
            if (seedValue.Certificate.EnrollmentUrl is not null)
                certificateEntries.Add(("URL", Latin1String(seedValue.Certificate.EnrollmentUrl)));
            if (seedValue.Certificate.EnrollmentUrlType.HasValue)
                certificateEntries.Add(("URLType", Name(seedValue.Certificate.EnrollmentUrlType.Value
                    == PdfCertificateEnrollmentUrlType.Html ? "HTML" : "ASSP")));
            int certificateFlags = 0;
            if (seedValue.Certificate.RequireSubject) certificateFlags |= 1;
            if (seedValue.Certificate.RequireIssuer) certificateFlags |= 1 << 1;
            if (seedValue.Certificate.RequireCertificatePolicy) certificateFlags |= 1 << 2;
            if (seedValue.Certificate.RequireSubjectDistinguishedName) certificateFlags |= 1 << 3;
            if (seedValue.Certificate.RequireKeyUsage) certificateFlags |= 1 << 5;
            if (seedValue.Certificate.RequireEnrollmentUrl) certificateFlags |= 1 << 6;
            if (certificateFlags != 0)
                certificateEntries.Add(("Ff", new PdfInteger(certificateFlags)));
            entries.Add(("Cert", Dictionary([.. certificateEntries])));
        }
        if (seedValue.DocumentLockIntent.HasValue)
        {
            entries.Add(("LockDocument", Name(seedValue.DocumentLockIntent.Value switch
            {
                PdfSignatureDocumentLockIntent.Automatic => "auto",
                PdfSignatureDocumentLockIntent.Lock => "true",
                PdfSignatureDocumentLockIntent.DoNotLock => "false",
                _ => throw new ArgumentOutOfRangeException(nameof(seedValue))
            })));
            if (seedValue.RequireDocumentLockIntent) flags |= 1 << 7;
        }
        if (seedValue.AppearanceName is not null)
        {
            entries.Add(("AppearanceFilter", UnicodeString(seedValue.AppearanceName)));
            if (seedValue.RequireAppearance) flags |= 1 << 8;
        }
        if (flags != 0)
            entries.Add(("Ff", new PdfInteger(flags)));
        return Dictionary([.. entries]);
    }

    private static string SignatureHandlerName(PdfSignatureHandler handler) => handler switch
    {
        PdfSignatureHandler.AdobePpkLite => "Adobe.PPKLite",
        _ => throw new ArgumentOutOfRangeException(nameof(handler))
    };

    private static string SignatureSubFilterName(PdfSignatureSubFilter subFilter) => subFilter switch
    {
        PdfSignatureSubFilter.AdobePkcs7Detached => "adbe.pkcs7.detached",
        PdfSignatureSubFilter.EtsiCadesDetached => "ETSI.CAdES.detached",
        _ => throw new ArgumentOutOfRangeException(nameof(subFilter))
    };

    private static string SignatureDigestMethodName(PdfSignatureDigestMethod method) => method switch
    {
        PdfSignatureDigestMethod.Sha256 => "SHA256",
        PdfSignatureDigestMethod.Sha384 => "SHA384",
        PdfSignatureDigestMethod.Sha512 => "SHA512",
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static string CertificateKeyUsagePattern(PdfCertificateKeyUsage usage) => string.Concat(
        KeyUsageCharacter(usage.DigitalSignature),
        KeyUsageCharacter(usage.NonRepudiation),
        KeyUsageCharacter(usage.KeyEncipherment),
        KeyUsageCharacter(usage.DataEncipherment),
        KeyUsageCharacter(usage.KeyAgreement),
        KeyUsageCharacter(usage.KeyCertificateSigning),
        KeyUsageCharacter(usage.CertificateRevocationListSigning),
        KeyUsageCharacter(usage.EncipherOnly),
        KeyUsageCharacter(usage.DecipherOnly));

    private static char KeyUsageCharacter(bool? value) => value switch
    {
        true => '1',
        false => '0',
        null => 'X'
    };

    private static void AddSignatureFieldObject(
        List<PdfIndirectObject> objects,
        AllocatedSignatureField allocatedField,
        IReadOnlyList<AllocatedPage> pages,
        PdfName fontResource,
        int fontNumber,
        EmbeddedFontUsage? fontUsage)
    {
        SignatureFieldDefinition field = allocatedField.Definition;
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")),
            ("Subtype", Name("Widget")),
            ("FT", Name("Sig")),
            ("T", UnicodeString(field.Name)),
            ("Rect", new PdfArray([
                Number(field.X), Number(field.Y),
                Number(field.X + field.Width), Number(field.Y + field.Height)])),
            ("P", new PdfIndirectReference(pages[field.PageIndex].PageNumber, 0)),
            ("F", new PdfInteger(FormWidgetFlags(field.FieldOptions.Visibility))),
            ("AP", Dictionary(("N", new PdfIndirectReference(allocatedField.AppearanceNumber, 0))))
        };
        int flags = FormFieldFlags(field.FieldOptions);
        if (flags != 0)
            entries.Add(("Ff", new PdfInteger(flags)));
        AddFieldMetadata(entries, field.Metadata);
        if (field.FieldLock is not null)
        {
            entries.Add(("Lock", new PdfIndirectReference(
                allocatedField.FieldLockNumber!.Value, 0)));
            objects.Add(new PdfIndirectObject(allocatedField.FieldLockNumber.Value, 0,
                SignatureFieldLockDictionary(field.FieldLock), 0));
        }
        if (field.SeedValue is not null)
        {
            entries.Add(("SV", new PdfIndirectReference(
                allocatedField.SeedValueNumber!.Value, 0)));
            objects.Add(new PdfIndirectObject(allocatedField.SeedValueNumber.Value, 0,
                SignatureSeedValueDictionary(field.SeedValue), 0));
        }
        entries.Add(("MK", FormFieldAppearanceCharacteristics(field.AppearanceStyle)));
        entries.Add(("BS", FormFieldBorderDictionary(field.AppearanceStyle)));
        objects.Add(new PdfIndirectObject(
            allocatedField.FieldNumber, 0, Dictionary([.. entries]), 0));

        byte[] appearance;
        PdfDictionary resources;
        if (field.AppearanceText is null)
        {
            using var output = new MemoryStream();
            WriteFormFieldBackgroundAndBorder(
                output, field.Width, field.Height, field.AppearanceStyle, clip: false);
            output.Write("Q\n"u8);
            appearance = output.ToArray();
            resources = Dictionary();
        }
        else
        {
            appearance = BuildSimpleTextAppearance(
                field.Width, field.Height, field.FontSize, field.AppearanceText,
                fontResource, field.EmbeddedFont, field.AppearanceStyle,
                field.AppearanceAlignment, fontUsage);
            resources = Dictionary(("Font", new PdfDictionary([
                new KeyValuePair<PdfName, PdfObject>(
                    fontResource, new PdfIndirectReference(fontNumber, 0))])));
        }
        objects.Add(new PdfIndirectObject(allocatedField.AppearanceNumber, 0,
            new PdfStream(Dictionary(
                ("Type", Name("XObject")),
                ("Subtype", Name("Form")),
                ("FormType", new PdfInteger(1)),
                ("BBox", new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    Number(field.Width), Number(field.Height)])),
                ("Resources", resources)), appearance), 0));
    }

    private static byte[] BuildListBoxAppearance(
        ChoiceFieldDefinition field, PdfName fontResource, EmbeddedFontUsage? fontUsage)
    {
        using var output = new MemoryStream();
        PdfFormFieldAppearanceStyle style = field.ChoiceOptions.AppearanceStyle!;
        WriteFormFieldBackgroundAndBorder(
            output, field.Width, field.Height, style, clip: true);
        double rowHeight = Math.Max(field.FontSize * 1.2, field.FontSize + 2);
        double rowTop = field.Height - 1;
        foreach (PdfChoiceOption option in field.Options.Skip(field.TopIndex))
        {
            double rowBottom = rowTop - rowHeight;
            if (rowTop <= 1)
                break;
            if (field.SelectedValues.Contains(option.ExportValue, StringComparer.Ordinal))
            {
                WriteAscii(output,
                    $"0.75 g\n1 {FormatNumber(rowBottom)} " +
                    $"{FormatNumber(Math.Max(0, field.Width - 2))} {FormatNumber(rowHeight)} re\nf\n");
            }
            double textX = ChoiceTextX(field, option.DisplayValue);
            WriteAscii(output,
                $"BT\n{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf\n" +
                $"{ColorOperands(style.TextColor)} rg\n" +
                $"{FormatNumber(textX)} {FormatNumber(rowBottom + (rowHeight - field.FontSize) / 2)} Td\n");
            WriteShownText(output, option.DisplayValue, field.EmbeddedFont, fontUsage);
            output.Write("ET\n"u8);
            rowTop = rowBottom;
        }
        output.Write("Q\n"u8);
        return output.ToArray();
    }

    private static byte[] BuildChoiceTextAppearance(
        ChoiceFieldDefinition field, PdfName fontResource, EmbeddedFontUsage? fontUsage)
    {
        string value = ChoiceDisplayValue(field, field.SelectedValues[0]);
        using var output = new MemoryStream();
        PdfFormFieldAppearanceStyle style = field.ChoiceOptions.AppearanceStyle!;
        WriteFormFieldBackgroundAndBorder(
            output, field.Width, field.Height, style, clip: false);
        WriteAscii(output,
            $"BT\n{NameToken(fontResource)} {FormatNumber(field.FontSize)} Tf\n" +
            $"{ColorOperands(style.TextColor)} rg\n" +
            $"{FormatNumber(ChoiceTextX(field, value))} " +
            $"{FormatNumber(Math.Max(1, (field.Height - field.FontSize) / 2))} Td\n");
        WriteShownText(output, value, field.EmbeddedFont, fontUsage);
        output.Write("ET\nQ\n"u8);
        return output.ToArray();
    }

    private static double ChoiceTextX(ChoiceFieldDefinition field, string value)
    {
        double textWidth = field.EmbeddedFont is null
            ? value.EnumerateRunes().Count() * field.FontSize * 0.55
            : field.EmbeddedFont.MapText(value).Sum(mapping =>
                field.EmbeddedFont.GetPdfAdvanceWidth(mapping.Glyph))
                * field.FontSize / 1000;
        return field.ChoiceOptions.Alignment switch
        {
            PdfTextFieldAlignment.Left => 3,
            PdfTextFieldAlignment.Center => Math.Max(1, (field.Width - textWidth) / 2),
            PdfTextFieldAlignment.Right => Math.Max(1, field.Width - textWidth - 3),
            _ => throw new InvalidOperationException(
                $"Unsupported choice-field alignment: {field.ChoiceOptions.Alignment}.")
        };
    }

    private static PdfObject ChoiceOptionObject(PdfChoiceOption option) =>
        string.Equals(option.ExportValue, option.DisplayValue, StringComparison.Ordinal)
            ? UnicodeString(option.ExportValue)
            : new PdfArray([UnicodeString(option.ExportValue), UnicodeString(option.DisplayValue)]);

    private static string ChoiceDisplayValue(ChoiceFieldDefinition field, string exportValue) =>
        field.Options.FirstOrDefault(option =>
            string.Equals(option.ExportValue, exportValue, StringComparison.Ordinal))?.DisplayValue
        ?? exportValue;

    private static void AddOutputIntentObjects(
        List<PdfIndirectObject> objects,
        OutputIntentDefinition definition,
        int profileNumber,
        int outputIntentNumber)
    {
        objects.Add(new PdfIndirectObject(
            outputIntentNumber, 0,
            PdfOutputIntentFactory.OutputIntent(
                new PdfIndirectReference(profileNumber, 0),
                definition.Identifier, definition.Condition,
                definition.RegistryName, definition.Information), 0));
    }

    private static void AddTextNoteObjects(
        List<PdfIndirectObject> objects,
        AllocatedTextNote allocated,
        IReadOnlyList<AllocatedPage> pages,
        int sequence,
        IReadOnlyDictionary<string, int> textNoteNumbersByName)
    {
        TextNoteDefinition note = allocated.Definition;
        var annotationEntries = new List<(string Name, PdfObject Value)>
        {
                ("Type", Name("Annot")),
                ("Subtype", Name("Text")),
                ("Rect", new PdfArray([
                    Number(note.X), Number(note.Y),
                    Number(note.X + note.Size), Number(note.Y + note.Size)])),
                ("P", new PdfIndirectReference(pages[note.PageIndex].PageNumber, 0)),
                ("F", new PdfInteger((int)(note.Metadata?.Flags ?? PdfAnnotationFlags.Print))),
                ("Contents", UnicodeString(note.Contents)),
                ("NM", note.Name is null
                    ? Latin1String($"KillerPDF-Note-{sequence}")
                    : UnicodeString(note.Name)),
                ("Name", Name(PdfTextNoteIconNames.Name(note.Icon))),
                ("Open", new PdfBoolean(note.Open)),
                ("C", ColorArray(note.Color)),
                ("AP", Dictionary(("N", new PdfIndirectReference(allocated.AppearanceNumber, 0))))
        };
        if (note.State is not null)
        {
            annotationEntries.Add(("State", Name(PdfTextNoteStateNames.State(note.State.Value))));
            annotationEntries.Add(("StateModel", Name(PdfTextNoteStateNames.Model(note.State.Value))));
        }
        if (note.InReplyTo is not null)
        {
            annotationEntries.Add(("IRT", new PdfIndirectReference(
                textNoteNumbersByName[note.InReplyTo], 0)));
            annotationEntries.Add(("RT", Name(PdfAnnotationReplyTypeNames.Name(note.ReplyType))));
        }
        if (allocated.PopupNumber is not null)
            annotationEntries.Add(("Popup", new PdfIndirectReference(allocated.PopupNumber.Value, 0)));
        AddAnnotationMetadata(annotationEntries, note.Metadata);
        objects.Add(new PdfIndirectObject(
            allocated.AnnotationNumber, 0, Dictionary([.. annotationEntries]), 0));

        using var appearance = new MemoryStream();
        WriteAscii(appearance,
            $"q\n{ColorOperands(note.Color)} rg\n0 0 {FormatNumber(note.Size)} {FormatNumber(note.Size)} re\nf\n" +
            $"0 G\n1 w\n0.5 0.5 {FormatNumber(Math.Max(0, note.Size - 1))} {FormatNumber(Math.Max(0, note.Size - 1))} re\nS\n");
        double fold = note.Size * 0.3;
        WriteAscii(appearance,
            $"{FormatNumber(note.Size - fold)} {FormatNumber(note.Size)} m\n" +
            $"{FormatNumber(note.Size - fold)} {FormatNumber(note.Size - fold)} l\n" +
            $"{FormatNumber(note.Size)} {FormatNumber(note.Size - fold)} l\nS\n" +
            $"{FormatNumber(note.Size * 0.22)} {FormatNumber(note.Size * 0.58)} m\n" +
            $"{FormatNumber(note.Size * 0.7)} {FormatNumber(note.Size * 0.58)} l\n" +
            $"{FormatNumber(note.Size * 0.22)} {FormatNumber(note.Size * 0.38)} m\n" +
            $"{FormatNumber(note.Size * 0.62)} {FormatNumber(note.Size * 0.38)} l\nS\nQ\n");
        objects.Add(new PdfIndirectObject(allocated.AppearanceNumber, 0,
            AnnotationAppearance(note.Size, note.Size, Dictionary(), appearance.ToArray()), 0));

        if (allocated.PopupNumber is not null)
        {
            PdfAnnotationPopup popup = note.Popup!;
            objects.Add(new PdfIndirectObject(allocated.PopupNumber.Value, 0, Dictionary(
                ("Type", Name("Annot")),
                ("Subtype", Name("Popup")),
                ("Rect", new PdfArray([
                    Number(popup.X), Number(popup.Y),
                    Number(popup.X + popup.Width), Number(popup.Y + popup.Height)])),
                ("P", new PdfIndirectReference(pages[note.PageIndex].PageNumber, 0)),
                ("F", new PdfInteger((int)(note.Metadata?.Flags ?? PdfAnnotationFlags.Print))),
                ("Parent", new PdfIndirectReference(allocated.AnnotationNumber, 0)),
                ("Open", new PdfBoolean(popup.Open))), 0));
        }
    }

    private static void AddTextMarkupObjects(
        List<PdfIndirectObject> objects,
        AllocatedTextMarkup allocated,
        IReadOnlyList<AllocatedPage> pages,
        int sequence)
    {
        TextMarkupDefinition highlight = allocated.Definition;
        (double minX, double minY, double maxX, double maxY) = TextMarkupBounds(highlight.Quads);
        var quadPoints = new List<PdfObject>(highlight.Quads.Count * 8);
        foreach (PdfTextQuad quad in highlight.Quads)
        {
            quadPoints.AddRange([
                Number(quad.UpperLeft.X), Number(quad.UpperLeft.Y),
                Number(quad.UpperRight.X), Number(quad.UpperRight.Y),
                Number(quad.LowerLeft.X), Number(quad.LowerLeft.Y),
                Number(quad.LowerRight.X), Number(quad.LowerRight.Y)]);
        }
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("Type", Name("Annot")),
            ("Subtype", Name(highlight.Type.ToString())),
            ("Rect", new PdfArray([
                Number(minX), Number(minY), Number(maxX), Number(maxY)])),
            ("QuadPoints", new PdfArray(quadPoints)),
            ("P", new PdfIndirectReference(pages[highlight.PageIndex].PageNumber, 0)),
            ("F", new PdfInteger((int)(highlight.Metadata?.Flags ?? PdfAnnotationFlags.Print))),
            ("NM", Latin1String($"KillerPDF-{highlight.Type}-{sequence}")),
            ("C", ColorArray(highlight.Color)),
            ("CA", new PdfReal(highlight.Opacity)),
            ("AP", Dictionary(("N", new PdfIndirectReference(allocated.AppearanceNumber, 0))))
        };
        if (!string.IsNullOrEmpty(highlight.Contents))
            entries.Add(("Contents", UnicodeString(highlight.Contents)));
        AddAnnotationMetadata(entries, highlight.Metadata);
        objects.Add(new PdfIndirectObject(
            allocated.AnnotationNumber, 0, Dictionary([.. entries]), 0));

        var graphicsState = Dictionary(
            ("Type", Name("ExtGState")),
            ("ca", new PdfReal(highlight.Opacity)),
            ("CA", new PdfReal(highlight.Opacity)),
            ("BM", Name("Multiply")));
        PdfDictionary resources = Dictionary(("ExtGState", new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("GS1"), graphicsState)])));
        byte[] appearance = TextMarkupAppearance(highlight);
        objects.Add(new PdfIndirectObject(allocated.AppearanceNumber, 0,
            AnnotationAppearance(maxX - minX, maxY - minY, resources, appearance), 0));
    }

    private static byte[] TextMarkupAppearance(TextMarkupDefinition markup)
    {
        (double minX, double minY, _, _) = TextMarkupBounds(markup.Quads);
        var drawing = new StringBuilder();
        foreach (PdfTextQuad source in markup.Quads)
        {
            PdfTextQuad quad = OffsetQuad(source, -minX, -minY);
            drawing.Append(markup.Type switch
            {
                PdfTextMarkupType.Highlight => MarkupFill(markup.Color, quad),
                PdfTextMarkupType.Underline => MarkupLine(markup.Color, quad, 0.08),
                PdfTextMarkupType.StrikeOut => MarkupLine(markup.Color, quad, 0.5),
                PdfTextMarkupType.Squiggly => SquigglyLine(markup.Color, quad),
                _ => throw new InvalidOperationException(
                    $"Unsupported text-markup type: {markup.Type}.")
            });
        }
        return Encoding.ASCII.GetBytes($"q\n/GS1 gs\n{drawing}Q\n");
    }

    private static string MarkupFill(PdfRgbColor color, PdfTextQuad quad) =>
        $"{ColorOperands(color)} rg\n" +
        $"{PointOperands(quad.LowerLeft)} m\n{PointOperands(quad.LowerRight)} l\n" +
        $"{PointOperands(quad.UpperRight)} l\n{PointOperands(quad.UpperLeft)} l\nh\nf\n";

    private static string MarkupLine(PdfRgbColor color, PdfTextQuad quad, double position)
    {
        double height = (PointDistance(quad.UpperLeft, quad.LowerLeft) +
            PointDistance(quad.UpperRight, quad.LowerRight)) / 2;
        PdfPoint left = Interpolate(quad.LowerLeft, quad.UpperLeft, position);
        PdfPoint right = Interpolate(quad.LowerRight, quad.UpperRight, position);
        return $"{ColorOperands(color)} RG\n{FormatNumber(Math.Max(0.75, height * 0.07))} w\n" +
            $"{PointOperands(left)} m\n{PointOperands(right)} l\nS\n";
    }

    private static string SquigglyLine(PdfRgbColor color, PdfTextQuad quad)
    {
        double height = (PointDistance(quad.UpperLeft, quad.LowerLeft) +
            PointDistance(quad.UpperRight, quad.LowerRight)) / 2;
        double amplitude = Math.Max(0.75, height * 0.1);
        double step = Math.Max(1.5, amplitude * 2);
        double dx = quad.LowerRight.X - quad.LowerLeft.X;
        double dy = quad.LowerRight.Y - quad.LowerLeft.Y;
        double length = PointDistance(quad.LowerLeft, quad.LowerRight);
        double nx = length == 0 ? 0 : -dy / length;
        double ny = length == 0 ? 1 : dx / length;
        var result = new StringBuilder(
            $"{ColorOperands(color)} RG\n{FormatNumber(Math.Max(0.75, amplitude * 0.55))} w\n" +
            $"{PointOperands(new PdfPoint(quad.LowerLeft.X + nx * amplitude, quad.LowerLeft.Y + ny * amplitude))} m\n");
        bool high = false;
        for (double distance = step; distance < length; distance += step)
        {
            double offset = high ? amplitude * 2 : 0;
            result.Append(PointOperands(new PdfPoint(
                quad.LowerLeft.X + dx * distance / length + nx * offset,
                quad.LowerLeft.Y + dy * distance / length + ny * offset))).Append(" l\n");
            high = !high;
        }
        double finalOffset = high ? amplitude * 2 : 0;
        result.Append(PointOperands(new PdfPoint(quad.LowerRight.X + nx * finalOffset,
            quad.LowerRight.Y + ny * finalOffset))).Append(" l\nS\n");
        return result.ToString();
    }

    private static string PointOperands(PdfPoint point) =>
        $"{FormatNumber(point.X)} {FormatNumber(point.Y)}";

    private static double PointDistance(PdfPoint first, PdfPoint second) =>
        Math.Sqrt(Math.Pow(second.X - first.X, 2) + Math.Pow(second.Y - first.Y, 2));

    private static PdfPoint Interpolate(PdfPoint start, PdfPoint end, double amount) =>
        new(start.X + ((end.X - start.X) * amount), start.Y + ((end.Y - start.Y) * amount));

    private static PdfTextQuad OffsetQuad(PdfTextQuad quad, double x, double y) =>
        new(new PdfPoint(quad.UpperLeft.X + x, quad.UpperLeft.Y + y),
            new PdfPoint(quad.UpperRight.X + x, quad.UpperRight.Y + y),
            new PdfPoint(quad.LowerLeft.X + x, quad.LowerLeft.Y + y),
            new PdfPoint(quad.LowerRight.X + x, quad.LowerRight.Y + y));

    private static (double MinX, double MinY, double MaxX, double MaxY) TextMarkupBounds(
        IReadOnlyList<PdfTextQuad> quads)
    {
        IEnumerable<PdfPoint> points = quads.SelectMany(quad => new[]
            { quad.UpperLeft, quad.UpperRight, quad.LowerLeft, quad.LowerRight });
        return (points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));
    }

    private static PdfStream AnnotationAppearance(
        double width, double height, PdfDictionary resources, byte[] content) =>
        new(Dictionary(
            ("Type", Name("XObject")),
            ("Subtype", Name("Form")),
            ("FormType", new PdfInteger(1)),
            ("BBox", new PdfArray([
                new PdfInteger(0), new PdfInteger(0), Number(width), Number(height)])),
            ("Resources", resources)), content);

    private static PdfArray ColorArray(PdfRgbColor color) =>
        new([Number(color.Red), Number(color.Green), Number(color.Blue)]);

    private static string ColorOperands(PdfRgbColor color) =>
        $"{FormatNumber(color.Red)} {FormatNumber(color.Green)} {FormatNumber(color.Blue)}";

    private static string FormatNumber(double value) =>
        Encoding.ASCII.GetString(PdfObjectWriter.Write(Number(value)));

    private static string NameToken(PdfName value) =>
        Encoding.ASCII.GetString(PdfObjectWriter.Write(value));

    private static void AddTextMappings(EmbeddedFontUsage usage, string value)
    {
        foreach (FontGlyphMapping mapping in usage.Font.MapText(value))
            usage.AddMapping(mapping.Glyph, mapping.UnicodeSequence);
    }

    private static void AddDrawableTextMappings(EmbeddedFontUsage usage, string value)
    {
        foreach (FontGlyphMapping mapping in usage.Font.MapText(value))
        {
            if (mapping.UnicodeSequence is "\r" or "\n")
                continue;
            usage.AddMapping(mapping.Glyph, mapping.UnicodeSequence);
        }
    }

    private static void WriteShownText(
        Stream output, string value, TrueTypeFont? embeddedFont,
        EmbeddedFontUsage? usage = null)
    {
        if (embeddedFont is null)
        {
            PdfObjectWriter.Write(output,
                new PdfString(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal));
        }
        else
        {
            output.WriteByte((byte)'<');
            foreach (FontGlyphMapping mapping in embeddedFont.MapText(value))
            {
                ushort code = usage?.AddMapping(mapping.Glyph, mapping.UnicodeSequence)
                    ?? mapping.Glyph;
                WriteAscii(output, code.ToString("X4", CultureInfo.InvariantCulture));
            }
            output.WriteByte((byte)'>');
        }
        output.Write(" Tj\n"u8);
    }

    private static (PdfName Resource, int ObjectNumber, EmbeddedFontUsage? Usage) FormFontBinding(
        TrueTypeFont? embeddedFont,
        Dictionary<PdfStandardFont, int> standardFonts,
        Dictionary<TrueTypeFont, PdfName> formFontResources,
        IReadOnlyList<AllocatedEmbeddedFont> embeddedFonts)
    {
        if (embeddedFont is null)
            return (Name("Helv"), standardFonts[PdfStandardFont.Helvetica], null);
        AllocatedEmbeddedFont allocated = embeddedFonts.Single(value =>
            ReferenceEquals(value.Font, embeddedFont)
            && value.Usages.Any(usage =>
                usage.ResourceName.Equals(formFontResources[embeddedFont])));
        EmbeddedFontUsage usage = allocated.Usages.Single(value =>
            value.ResourceName.Equals(formFontResources[embeddedFont]));
        return (formFontResources[embeddedFont], allocated.Type0Number, usage);
    }

    private static bool EmbeddedMappingsEqual(
        IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> left,
        IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> right) =>
        left.Count == right.Count && left.All(entry =>
            right.TryGetValue(entry.Key, out EmbeddedCharacterMapping? value)
            && value == entry.Value);

    private void ValidateUniqueFieldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A form field name cannot be empty.", nameof(name));
        string[] parts = name.Split('.');
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "A qualified form field name cannot contain an empty path component.", nameof(name));
        foreach (string part in parts)
            PdfUnicodeEncoding.EncodeBigEndian(part);
        string? conflict = FormFieldNames().FirstOrDefault(existing =>
            string.Equals(existing, name, StringComparison.Ordinal)
            || existing.StartsWith(name + ".", StringComparison.Ordinal)
            || name.StartsWith(existing + ".", StringComparison.Ordinal));
        if (conflict is not null)
            throw new ArgumentException(
                "Form field names must be unique and cannot also name a hierarchy parent.",
                nameof(name));
    }

    private IEnumerable<string> FormFieldNames() =>
        _textFields.Select(field => field.Name)
        .Concat(_checkBoxes.Select(field => field.Name))
        .Concat(_radioGroups.Select(field => field.Name))
        .Concat(_choiceFields.Select(field => field.Name))
        .Concat(_pushButtons.Select(field => field.Name))
        .Concat(_signatureFields.Select(field => field.Name));

    private bool FormFieldNameExists(string name) =>
        FormFieldNames().Contains(name, StringComparer.Ordinal);

    private static AllocatedFieldHierarchy AllocateFieldHierarchy(
        IEnumerable<(string Name, int FieldNumber)> terminalFields,
        ref int nextObjectNumber)
    {
        var fields = new List<AllocatedHierarchyField>();
        var fieldsByPath = new Dictionary<string, AllocatedHierarchyField>(StringComparer.Ordinal);
        var bindings = new Dictionary<int, TerminalFieldBinding>();
        var roots = new List<int>();
        foreach ((string name, int fieldNumber) in terminalFields)
        {
            string[] parts = name.Split('.');
            AllocatedHierarchyField? parent = null;
            string path = string.Empty;
            for (int index = 0; index < parts.Length - 1; index++)
            {
                path = index == 0 ? parts[index] : path + "." + parts[index];
                if (!fieldsByPath.TryGetValue(path, out AllocatedHierarchyField? current))
                {
                    current = new AllocatedHierarchyField(
                        path, parts[index], nextObjectNumber++, parent?.FieldNumber, []);
                    fieldsByPath.Add(path, current);
                    fields.Add(current);
                    if (parent is null) roots.Add(current.FieldNumber);
                    else parent.KidNumbers.Add(current.FieldNumber);
                }
                parent = current;
            }
            bindings.Add(fieldNumber, new TerminalFieldBinding(parts[^1], parent?.FieldNumber));
            if (parent is null) roots.Add(fieldNumber);
            else parent.KidNumbers.Add(fieldNumber);
        }
        return new AllocatedFieldHierarchy(fields, bindings, roots);
    }

    private static void ApplyFieldHierarchy(
        List<PdfIndirectObject> objects, AllocatedFieldHierarchy hierarchy)
    {
        for (int index = 0; index < objects.Count; index++)
        {
            PdfIndirectObject item = objects[index];
            if (!hierarchy.TerminalBindings.TryGetValue(
                    item.ObjectNumber, out TerminalFieldBinding? binding))
                continue;
            PdfDictionary dictionary = (PdfDictionary)item.Value;
            var entries = dictionary
                .Where(entry => !entry.Key.Equals(Name("T"))
                    && !entry.Key.Equals(Name("Parent")))
                .ToList();
            entries.Add(new KeyValuePair<PdfName, PdfObject>(
                Name("T"), UnicodeString(binding.PartialName)));
            if (binding.ParentNumber.HasValue)
                entries.Add(new KeyValuePair<PdfName, PdfObject>(
                    Name("Parent"), new PdfIndirectReference(binding.ParentNumber.Value, 0)));
            objects[index] = new PdfIndirectObject(
                item.ObjectNumber, item.Generation, new PdfDictionary(entries), item.Offset);
        }
    }

    private static void ApplyAccessibleAnnotationParents(
        List<PdfIndirectObject> objects,
        IReadOnlyDictionary<int, AccessibleAnnotationStructure> associations)
    {
        for (int index = 0; index < objects.Count; index++)
        {
            PdfIndirectObject item = objects[index];
            if (!associations.TryGetValue(
                    item.ObjectNumber, out AccessibleAnnotationStructure? association)
                || item.Value is not PdfDictionary dictionary)
                continue;
            var entries = dictionary
                .Where(entry => !entry.Key.Equals(Name("StructParent")))
                .ToList();
            entries.Add(new KeyValuePair<PdfName, PdfObject>(
                Name("StructParent"), new PdfInteger(association.ParentTreeKey)));
            if (association.StructureType == "Form"
                && !dictionary.ContainsKey(Name("Contents")))
            {
                entries.Add(new KeyValuePair<PdfName, PdfObject>(
                    Name("Contents"), UnicodeString(association.Description)));
            }
            objects[index] = new PdfIndirectObject(
                item.ObjectNumber, item.Generation, new PdfDictionary(entries), item.Offset);
        }
    }

    private static void ValidatePdfUaStructureHierarchy(
        IReadOnlyList<StructureElementDefinition> elements)
    {
        var levelStack = new Dictionary<int, PdfStructureType>();
        foreach (StructureElementDefinition element in elements)
        {
            if (element.Type == PdfStructureType.Heading)
                throw new InvalidOperationException(
                    "PDF/UA-2 structure cannot use the generic Heading type; use Heading1 through Heading6.");
            if (element.MarkedContentId.HasValue
                && element.Type is PdfStructureType.Document or PdfStructureType.Article
                    or PdfStructureType.Section or PdfStructureType.List
                    or PdfStructureType.ListItem
                    or PdfStructureType.Table or PdfStructureType.TableRow)
                throw new InvalidOperationException(
                    $"PDF/UA-2 {element.Type} structure elements cannot contain marked content directly.");
            PdfStructureType? parent = element.Level == 0
                ? null
                : levelStack.TryGetValue(element.Level - 1, out PdfStructureType value)
                    ? value
                    : throw new InvalidOperationException(
                        "A PDF/UA-2 structure element has no parent at the preceding level.");
            if (element.Type == PdfStructureType.Document && parent.HasValue)
                throw new InvalidOperationException(
                    "PDF/UA-2 Document structure elements must be the sole top-level structure element.");
            if (element.Type is PdfStructureType.Quote
                    or PdfStructureType.Reference or PdfStructureType.Code
                && parent is not (PdfStructureType.Paragraph or PdfStructureType.Span))
                throw new InvalidOperationException(
                    $"PDF/UA-2 {element.Type} structure elements require a Paragraph or Span parent.");
            PdfStructureType? requiredParent = element.Type switch
            {
                PdfStructureType.ListItem => PdfStructureType.List,
                PdfStructureType.Label or PdfStructureType.ListBody => PdfStructureType.ListItem,
                PdfStructureType.TableRow => PdfStructureType.Table,
                PdfStructureType.TableHeaderCell or PdfStructureType.TableDataCell =>
                    PdfStructureType.TableRow,
                _ => null
            };
            if (requiredParent.HasValue && parent != requiredParent)
                throw new InvalidOperationException(
                    $"PDF/UA-2 {element.Type} structure elements require a {requiredParent.Value} parent.");
            if (element.Type is PdfStructureType.List or PdfStructureType.Table
                && parent.HasValue
                && IsPdfUaInlineOrContainerRestrictedParent(parent.Value))
                throw new InvalidOperationException(
                    $"PDF/UA-2 {parent.Value} structure elements cannot contain {element.Type} children.");
            if (parent.HasValue && IsPdfUaNumberedHeading(parent.Value)
                && IsPdfUaNumberedHeadingForbiddenChild(element.Type))
                throw new InvalidOperationException(
                    $"PDF/UA-2 {parent.Value} structure elements cannot contain {element.Type} children.");
            if (IsPdfUaNumberedHeading(element.Type) && parent.HasValue
                && IsPdfUaNumberedHeadingRestrictedParent(parent.Value))
                throw new InvalidOperationException(
                    $"PDF/UA-2 {parent.Value} structure elements cannot contain numbered headings.");
            if (element.Type == PdfStructureType.Span && parent is
                PdfStructureType.Document or PdfStructureType.Part
                    or PdfStructureType.Article or PdfStructureType.Section
                    or PdfStructureType.Division)
                throw new InvalidOperationException(
                    $"PDF/UA-2 {parent.Value} structure elements cannot contain Span children directly.");
            levelStack[element.Level] = element.Type;
            foreach (int deeperLevel in levelStack.Keys
                .Where(level => level > element.Level).ToArray())
                levelStack.Remove(deeperLevel);
        }
        for (int index = 0; index < elements.Count; index++)
        {
            StructureElementDefinition list = elements[index];
            if (list.Type == PdfStructureType.List)
            {
                bool containsLabel = elements.Skip(index + 1)
                    .TakeWhile(element => element.Level > list.Level)
                    .Any(element => element.Type == PdfStructureType.Label);
                if (containsLabel && list.ListNumbering is null or PdfListNumbering.None)
                    throw new InvalidOperationException(
                        "A PDF/UA-2 List containing Label elements requires non-None list numbering.");
            }
            if (list.Type == PdfStructureType.Table)
                ValidatePdfUaTableRegularity(elements, index);
            if (IsPdfUaNumberedHeading(list.Type))
            {
                int sectionChildren = elements.Skip(index + 1)
                    .TakeWhile(element => element.Level > list.Level)
                    .Count(element => element.Level == list.Level + 1
                        && element.Type == PdfStructureType.Section);
                if (sectionChildren > 1)
                    throw new InvalidOperationException(
                        $"PDF/UA-2 {list.Type} structure elements can contain at most one direct Section child.");
            }
        }
    }

    private static void ValidatePdfUaTableRegularity(
        IReadOnlyList<StructureElementDefinition> elements, int tableIndex)
    {
        int tableLevel = elements[tableIndex].Level;
        int? columnCount = null;
        for (int index = tableIndex + 1;
             index < elements.Count && elements[index].Level > tableLevel;
             index++)
        {
            StructureElementDefinition row = elements[index];
            if (row.Level != tableLevel + 1 || row.Type != PdfStructureType.TableRow)
                continue;
            int cells = elements.Skip(index + 1)
                .TakeWhile(element => element.Level > row.Level)
                .Count(element => element.Level == row.Level + 1
                    && element.Type is PdfStructureType.TableHeaderCell
                        or PdfStructureType.TableDataCell);
            if (columnCount.HasValue && cells != columnCount.Value)
                throw new InvalidOperationException(
                    "PDF/UA-2 table rows must contain the same number of cells.");
            columnCount = cells;
        }
    }

    private static bool IsPdfUaInlineOrContainerRestrictedParent(PdfStructureType type) =>
        type is PdfStructureType.Heading or PdfStructureType.Heading1
            or PdfStructureType.Heading2 or PdfStructureType.Heading3
            or PdfStructureType.Heading4 or PdfStructureType.Heading5
            or PdfStructureType.Heading6 or PdfStructureType.List
            or PdfStructureType.ListItem or PdfStructureType.Label
            or PdfStructureType.Table or PdfStructureType.TableRow
            or PdfStructureType.Span or PdfStructureType.Quote;

    private static bool IsPdfUaNumberedHeading(PdfStructureType type) =>
        type is PdfStructureType.Heading1 or PdfStructureType.Heading2
            or PdfStructureType.Heading3 or PdfStructureType.Heading4
            or PdfStructureType.Heading5 or PdfStructureType.Heading6;

    private static bool IsPdfUaNumberedHeadingForbiddenChild(PdfStructureType type) =>
        type is PdfStructureType.Document or PdfStructureType.Part
            or PdfStructureType.Article or PdfStructureType.Division
            or PdfStructureType.Paragraph or PdfStructureType.Heading
            or PdfStructureType.Heading1 or PdfStructureType.Heading2
            or PdfStructureType.Heading3 or PdfStructureType.Heading4
            or PdfStructureType.Heading5 or PdfStructureType.Heading6
            or PdfStructureType.List or PdfStructureType.ListItem
            or PdfStructureType.ListBody or PdfStructureType.Table
            or PdfStructureType.TableRow or PdfStructureType.TableHeaderCell
            or PdfStructureType.TableDataCell;

    private static bool IsPdfUaNumberedHeadingRestrictedParent(PdfStructureType type) =>
        type is PdfStructureType.Code or PdfStructureType.Form
            or PdfStructureType.Heading or PdfStructureType.Heading1
            or PdfStructureType.Heading2 or PdfStructureType.Heading3
            or PdfStructureType.Heading4 or PdfStructureType.Heading5
            or PdfStructureType.Heading6 or PdfStructureType.List
            or PdfStructureType.ListItem or PdfStructureType.Label
            or PdfStructureType.Note or PdfStructureType.Paragraph
            or PdfStructureType.Quote or PdfStructureType.Span
            or PdfStructureType.Table or PdfStructureType.TableRow;

    private static PdfChoiceOption[] ValidateChoiceOptions(
        IEnumerable<PdfChoiceOption> options, TrueTypeFont? embeddedFont, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(options);
        PdfChoiceOption[] values = [.. options];
        if (values.Length == 0 || values.Any(value => value is null
            || string.IsNullOrEmpty(value.ExportValue)
            || string.IsNullOrEmpty(value.DisplayValue)))
            throw new ArgumentException("Choice export and display values cannot be empty.", parameterName);
        if (values.Select(value => value.ExportValue)
            .Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Choice export values must be unique.", parameterName);
        if (embeddedFont is null
            && values.Any(value => value.DisplayValue.Any(character => character > 0xFF)))
            throw new ArgumentException(
                "Unicode choice display values require an embedded font.", parameterName);
        if (embeddedFont is not null)
            foreach (PdfChoiceOption value in values)
                ValidateFormFontText(embeddedFont, value.DisplayValue, parameterName);
        return values;
    }

    private static PdfChoiceFieldOptions ValidateChoiceFieldOptions(
        PdfChoiceFieldOptions? options)
    {
        options ??= new PdfChoiceFieldOptions();
        if (!Enum.IsDefined(options.Alignment))
            throw new ArgumentOutOfRangeException(nameof(options));
        return options with
        {
            AppearanceStyle = ValidateFormFieldAppearanceStyle(options.AppearanceStyle)
        };
    }

    private static PdfFormFieldAppearanceStyle ValidateFormFieldAppearanceStyle(
        PdfFormFieldAppearanceStyle? style)
    {
        style ??= new PdfFormFieldAppearanceStyle();
        if (!double.IsFinite(style.BorderWidth) || style.BorderWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(style));
        if (!Enum.IsDefined(style.BorderStyle))
            throw new ArgumentOutOfRangeException(nameof(style));
        double[]? dashPattern = style.DashPattern?.ToArray();
        if (style.BorderStyle == PdfFormFieldBorderStyle.Dashed)
        {
            dashPattern ??= [3];
            if (dashPattern.Length == 0
                || dashPattern.Any(value => !double.IsFinite(value) || value < 0)
                || dashPattern.All(value => value == 0))
                throw new ArgumentException(
                    "A dashed field border requires a finite, nonnegative, nonzero dash pattern.",
                    nameof(style));
        }
        else if (dashPattern is not null)
            throw new ArgumentException(
                "A field-border dash pattern requires the dashed border style.", nameof(style));
        return style with { DashPattern = dashPattern };
    }

    private static PdfDictionary FormFieldBorderDictionary(
        PdfFormFieldAppearanceStyle style)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("W", Number(style.BorderWidth)),
            ("S", Name(FormFieldBorderStyleName(style.BorderStyle)))
        };
        if (style.BorderStyle == PdfFormFieldBorderStyle.Dashed)
            entries.Add(("D", new PdfArray(style.DashPattern!.Select(Number))));
        return Dictionary([.. entries]);
    }

    private static string FormFieldBorderStyleName(PdfFormFieldBorderStyle style) => style switch
    {
        PdfFormFieldBorderStyle.Solid => "S",
        PdfFormFieldBorderStyle.Dashed => "D",
        PdfFormFieldBorderStyle.Beveled => "B",
        PdfFormFieldBorderStyle.Inset => "I",
        PdfFormFieldBorderStyle.Underline => "U",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    private static PdfPushButtonAppearanceOptions ValidatePushButtonAppearanceOptions(
        PdfPushButtonAppearanceOptions? options, TrueTypeFont? embeddedFont)
    {
        options ??= new PdfPushButtonAppearanceOptions();
        if (!Enum.IsDefined(options.Alignment))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!Enum.IsDefined(options.CaptionPosition)
            || !Enum.IsDefined(options.IconScaleMode))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Icon is null && options.RolloverIcon is null && options.DownIcon is null
            && options.CaptionPosition != PdfPushButtonCaptionPosition.CaptionOnly)
            throw new ArgumentException(
                "A push-button icon position requires an icon.", nameof(options));
        if (!double.IsFinite(options.IconHorizontalAlignment)
            || options.IconHorizontalAlignment is < 0 or > 1
            || !double.IsFinite(options.IconVerticalAlignment)
            || options.IconVerticalAlignment is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        foreach (string? label in new[] { options.RolloverLabel, options.DownLabel })
        {
            if (label is null)
                continue;
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException(
                    "Alternate push-button captions cannot be empty.", nameof(options));
            if (embeddedFont is null && label.Any(character => character > 0xFF))
                throw new ArgumentException(
                    "Unicode alternate push-button captions require an embedded font.",
                    nameof(options));
            if (embeddedFont is not null)
                ValidateFormFontText(embeddedFont, label, nameof(options));
        }
        return options;
    }

    private static PdfDictionary FormFieldAppearanceCharacteristics(
        PdfFormFieldAppearanceStyle style)
    {
        var entries = new List<(string Name, PdfObject Value)>();
        if (style.BackgroundColor.HasValue)
            entries.Add(("BG", RgbArray(style.BackgroundColor.Value)));
        if (style.BorderColor.HasValue)
            entries.Add(("BC", RgbArray(style.BorderColor.Value)));
        return Dictionary([.. entries]);
    }

    private static PdfDictionary PushButtonAppearanceCharacteristics(
        string label, PdfFormFieldAppearanceStyle style,
        PdfPushButtonAppearanceOptions options, int? iconNumber,
        int? rolloverIconNumber, int? downIconNumber)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("CA", UnicodeString(label))
        };
        if (style.BackgroundColor.HasValue)
            entries.Add(("BG", RgbArray(style.BackgroundColor.Value)));
        if (style.BorderColor.HasValue)
            entries.Add(("BC", RgbArray(style.BorderColor.Value)));
        if (options.RolloverLabel is not null)
            entries.Add(("RC", UnicodeString(options.RolloverLabel)));
        if (options.DownLabel is not null)
            entries.Add(("AC", UnicodeString(options.DownLabel)));
        if (iconNumber.HasValue)
            entries.Add(("I", new PdfIndirectReference(iconNumber.Value, 0)));
        if (rolloverIconNumber.HasValue)
            entries.Add(("RI", new PdfIndirectReference(rolloverIconNumber.Value, 0)));
        if (downIconNumber.HasValue)
            entries.Add(("IX", new PdfIndirectReference(downIconNumber.Value, 0)));
        if (iconNumber.HasValue || rolloverIconNumber.HasValue || downIconNumber.HasValue)
        {
            entries.Add(("TP", new PdfInteger((int)options.CaptionPosition)));
            entries.Add(("IF", Dictionary(
                ("SW", Name(PushButtonIconScaleName(options.IconScaleMode))),
                ("S", Name(options.ProportionalIconScaling ? "P" : "A")),
                ("A", new PdfArray([
                    Number(options.IconHorizontalAlignment),
                    Number(options.IconVerticalAlignment)])),
                ("FB", new PdfBoolean(options.FitIconToBounds)))));
        }
        return Dictionary([.. entries]);
    }

    private static string PushButtonIconScaleName(PdfPushButtonIconScaleMode mode) => mode switch
    {
        PdfPushButtonIconScaleMode.Always => "A",
        PdfPushButtonIconScaleMode.Never => "N",
        PdfPushButtonIconScaleMode.WhenTooLarge => "B",
        PdfPushButtonIconScaleMode.WhenTooSmall => "S",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static PdfArray RgbArray(PdfRgbColor color) => new([
        Number(color.Red), Number(color.Green), Number(color.Blue)]);

    private static void WriteFormFieldBackgroundAndBorder(
        Stream output, double width, double height,
        PdfFormFieldAppearanceStyle style, bool clip)
    {
        output.Write("q\n"u8);
        if (style.BackgroundColor.HasValue)
            WriteAscii(output,
                $"{ColorOperands(style.BackgroundColor.Value)} rg\n" +
                $"0 0 {FormatNumber(width)} {FormatNumber(height)} re\nf\n");
        if (style.BorderColor.HasValue && style.BorderWidth > 0)
        {
            double inset = style.BorderWidth / 2;
            PdfRgbColor border = style.BorderColor.Value;
            WriteAscii(output, $"{ColorOperands(border)} RG\n" +
                $"{FormatNumber(style.BorderWidth)} w\n");
            switch (style.BorderStyle)
            {
                case PdfFormFieldBorderStyle.Solid:
                    WriteFieldBorderRectangle(output, width, height, style.BorderWidth);
                    break;
                case PdfFormFieldBorderStyle.Dashed:
                    WriteAscii(output,
                        $"[{string.Join(' ', style.DashPattern!.Select(FormatNumber))}] 0 d\n");
                    WriteFieldBorderRectangle(output, width, height, style.BorderWidth);
                    output.Write("[] 0 d\n"u8);
                    break;
                case PdfFormFieldBorderStyle.Underline:
                    WriteAscii(output,
                        $"0 {FormatNumber(inset)} m\n{FormatNumber(width)} " +
                        $"{FormatNumber(inset)} l\nS\n");
                    break;
                case PdfFormFieldBorderStyle.Beveled:
                case PdfFormFieldBorderStyle.Inset:
                    WriteFieldBorderRectangle(output, width, height, style.BorderWidth);
                    WriteBeveledFieldBorder(output, width, height, style);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported form-field border style: {style.BorderStyle}.");
            }
        }
        if (clip)
        {
            double inset = Math.Max(1, style.BorderWidth);
            WriteAscii(output,
                $"{FormatNumber(inset)} {FormatNumber(inset)} " +
                $"{FormatNumber(Math.Max(0, width - inset * 2))} " +
                $"{FormatNumber(Math.Max(0, height - inset * 2))} re\nW\nn\n");
        }
    }

    private static void WriteFieldBorderRectangle(
        Stream output, double width, double height, double borderWidth)
    {
        double inset = borderWidth / 2;
        WriteAscii(output,
            $"{FormatNumber(inset)} {FormatNumber(inset)} " +
            $"{FormatNumber(Math.Max(0, width - borderWidth))} " +
            $"{FormatNumber(Math.Max(0, height - borderWidth))} re\nS\n");
    }

    private static void WriteBeveledFieldBorder(
        Stream output, double width, double height, PdfFormFieldAppearanceStyle style)
    {
        PdfRgbColor border = style.BorderColor!.Value;
        PdfRgbColor light = BlendRgb(border, new PdfRgbColor(1, 1, 1), 0.65);
        PdfRgbColor dark = BlendRgb(border, new PdfRgbColor(0, 0, 0), 0.45);
        if (style.BorderStyle == PdfFormFieldBorderStyle.Inset)
            (light, dark) = (dark, light);
        double edge = Math.Max(0.5, style.BorderWidth);
        WriteAscii(output,
            $"{ColorOperands(light)} RG\n{FormatNumber(edge)} w\n" +
            $"{FormatNumber(edge)} {FormatNumber(edge)} m\n" +
            $"{FormatNumber(edge)} {FormatNumber(Math.Max(edge, height - edge))} l\n" +
            $"{FormatNumber(Math.Max(edge, width - edge))} " +
            $"{FormatNumber(Math.Max(edge, height - edge))} l\nS\n" +
            $"{ColorOperands(dark)} RG\n" +
            $"{FormatNumber(edge)} {FormatNumber(edge)} m\n" +
            $"{FormatNumber(Math.Max(edge, width - edge))} {FormatNumber(edge)} l\n" +
            $"{FormatNumber(Math.Max(edge, width - edge))} " +
            $"{FormatNumber(Math.Max(edge, height - edge))} l\nS\n");
    }

    private static PdfRgbColor BlendRgb(PdfRgbColor from, PdfRgbColor to, double amount) =>
        new(from.Red + (to.Red - from.Red) * amount,
            from.Green + (to.Green - from.Green) * amount,
            from.Blue + (to.Blue - from.Blue) * amount);

    private static void ValidateRichTextValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A rich-text value cannot be empty.", nameof(value));
        try
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(value);
            XmlElement? root = document.DocumentElement;
            if (root is null || root.LocalName != "body"
                || root.NamespaceURI != "http://www.w3.org/1999/xhtml")
                throw new ArgumentException(
                    "A rich-text value must have an XHTML body root.", nameof(value));
        }
        catch (XmlException error)
        {
            throw new ArgumentException("A rich-text value must be well-formed XHTML.", nameof(value), error);
        }
    }

    private static string[] ResolveChoiceDefaultValues(
        IReadOnlyList<PdfChoiceOption> options,
        IReadOnlyList<string> selectedValues,
        bool isMultiSelect,
        bool editable,
        TrueTypeFont? embeddedFont,
        PdfChoiceFieldOptions choiceOptions)
    {
        string[] defaults = choiceOptions.DefaultSelectedExportValues?.ToArray()
            ?? [.. selectedValues];
        if (!isMultiSelect && defaults.Length != 1)
            throw new ArgumentException(
                "A single-select choice field requires exactly one default value.",
                nameof(choiceOptions));
        if (defaults.Distinct(StringComparer.Ordinal).Count() != defaults.Length)
            throw new ArgumentException("Default choice values must be unique.", nameof(choiceOptions));
        if (defaults.Any(value => string.IsNullOrEmpty(value)))
            throw new ArgumentException("Default choice values cannot be empty.", nameof(choiceOptions));
        foreach (string value in defaults)
        {
            bool isOption = options.Any(option => option.ExportValue == value);
            if (!isOption && !editable)
                throw new ArgumentException(
                    "Every default choice value must name an option export value.",
                    nameof(choiceOptions));
            if (!isOption && embeddedFont is null && value.Any(character => character > 0xFF))
                throw new ArgumentException(
                    "An editable Unicode default choice value requires an embedded font.",
                    nameof(choiceOptions));
            if (!isOption && embeddedFont is not null)
                ValidateFormFontText(embeddedFont, value, nameof(choiceOptions));
        }
        if (!isMultiSelect)
            return defaults;
        var defaultSet = defaults.ToHashSet(StringComparer.Ordinal);
        return
        [
            .. options.Where(option => defaultSet.Contains(option.ExportValue))
                .Select(option => option.ExportValue)
        ];
    }

    private PdfSignatureFieldLock? ValidateSignatureFieldLock(PdfSignatureFieldLock? fieldLock)
    {
        if (fieldLock is null)
            return null;
        if (!Enum.IsDefined(fieldLock.Action))
            throw new ArgumentOutOfRangeException(nameof(fieldLock));
        if (fieldLock.Permission.HasValue && !Enum.IsDefined(fieldLock.Permission.Value))
            throw new ArgumentOutOfRangeException(nameof(fieldLock));
        string[]? fields = fieldLock.Fields?.ToArray();
        if (fieldLock.Action == PdfSignatureLockAction.All && fields is not null)
            throw new ArgumentException("An all-fields signature lock cannot list fields.", nameof(fieldLock));
        if (fieldLock.Action != PdfSignatureLockAction.All && fields is not { Length: > 0 })
            throw new ArgumentException("Include and exclude signature locks require fields.", nameof(fieldLock));
        if (fields?.Any(string.IsNullOrWhiteSpace) == true
            || fields?.Distinct(StringComparer.Ordinal).Count() != fields?.Length)
            throw new ArgumentException("Signature lock field names must be non-empty and unique.", nameof(fieldLock));
        if (fields?.Any(field => !FormFieldNameExists(field)) == true)
            throw new ArgumentException("Every signature lock field must already be defined.", nameof(fieldLock));
        return fieldLock with { Fields = fields };
    }

    private static PdfSignatureSeedValue? ValidateSignatureSeedValue(
        PdfSignatureSeedValue? seedValue)
    {
        if (seedValue is null)
            return null;
        if (seedValue.Handler.HasValue && !Enum.IsDefined(seedValue.Handler.Value))
            throw new ArgumentOutOfRangeException(nameof(seedValue));
        if (seedValue.ParserVersion.HasValue
            && !Enum.IsDefined(seedValue.ParserVersion.Value))
            throw new ArgumentOutOfRangeException(nameof(seedValue));
        PdfSignatureSubFilter[]? subFilters = seedValue.SubFilters?.ToArray();
        PdfSignatureDigestMethod[]? methods = seedValue.DigestMethods?.ToArray();
        string[]? reasons = seedValue.Reasons?.ToArray();
        string[]? legalAttestations = seedValue.LegalAttestations?.ToArray();
        PdfCertificateKeyUsage[]? keyUsages = seedValue.Certificate?.KeyUsages?.ToArray();
        byte[][]? subjectCertificates = seedValue.Certificate?.SubjectCertificates?
            .Select(certificate => certificate?.ToArray() ?? []).ToArray();
        byte[][]? issuerCertificates = seedValue.Certificate?.IssuerCertificates?
            .Select(certificate => certificate?.ToArray() ?? []).ToArray();
        string[]? policyOids = seedValue.Certificate?.CertificatePolicyObjectIdentifiers?.ToArray();
        PdfCertificateDistinguishedName[]? subjectDistinguishedNames =
            seedValue.Certificate?.SubjectDistinguishedNames?.Select(distinguishedName =>
                distinguishedName is null
                    ? new PdfCertificateDistinguishedName(
                        new Dictionary<string, string>(StringComparer.Ordinal))
                    : new PdfCertificateDistinguishedName(
                        new Dictionary<string, string>(distinguishedName.Attributes,
                            StringComparer.Ordinal))).ToArray();
        if (subFilters is { Length: 0 }
            || subFilters?.Any(subFilter => !Enum.IsDefined(subFilter)) == true
            || subFilters?.Distinct().Count() != subFilters?.Length)
            throw new ArgumentException(
                "Signature subfilters must be non-empty, valid, and unique.", nameof(seedValue));
        if (methods is { Length: 0 }
            || methods?.Any(method => !Enum.IsDefined(method)) == true
            || methods?.Distinct().Count() != methods?.Length)
            throw new ArgumentException(
                "Signature digest methods must be non-empty, valid, and unique.", nameof(seedValue));
        if (reasons is { Length: 0 }
            || reasons?.Any(string.IsNullOrWhiteSpace) == true
            || reasons?.Distinct(StringComparer.Ordinal).Count() != reasons?.Length)
            throw new ArgumentException(
                "Signature reasons must be non-empty and unique.", nameof(seedValue));
        if (legalAttestations is { Length: 0 }
            || legalAttestations?.Any(string.IsNullOrWhiteSpace) == true
            || legalAttestations?.Distinct(StringComparer.Ordinal).Count()
                != legalAttestations?.Length)
            throw new ArgumentException(
                "Signature legal attestations must be non-empty and unique.", nameof(seedValue));
        if (seedValue.RequireDigestMethod && methods is null)
            throw new ArgumentException(
                "A required signature digest method needs an allowed-method list.", nameof(seedValue));
        if (seedValue.RequireRevocationInformation && !seedValue.AddRevocationInformation)
            throw new ArgumentException(
                "Required revocation information must be enabled.", nameof(seedValue));
        if (seedValue.RequireSubFilter && subFilters is null)
            throw new ArgumentException(
                "A required signature subfilter needs an allowed-subfilter list.", nameof(seedValue));
        if (seedValue.RequireHandler && !seedValue.Handler.HasValue)
            throw new ArgumentException(
                "A required signature handler needs a handler value.", nameof(seedValue));
        if (seedValue.RequireParserVersion && !seedValue.ParserVersion.HasValue)
            throw new ArgumentException(
                "A required seed parser version needs a version value.", nameof(seedValue));
        if (seedValue.RequireReason && reasons is null)
            throw new ArgumentException(
                "A required signature reason needs an allowed-reason list.", nameof(seedValue));
        if (seedValue.RequireLegalAttestation && legalAttestations is null)
            throw new ArgumentException(
                "A required legal attestation needs an allowed-attestation list.", nameof(seedValue));
        if (seedValue.Timestamp is not null
            && (!Uri.TryCreate(seedValue.Timestamp.ServerUrl, UriKind.Absolute, out Uri? timestampUri)
                || timestampUri.Scheme is not ("http" or "https")
                || !seedValue.Timestamp.ServerUrl.All(character => character <= 0x7f)))
            throw new ArgumentException(
                "A signature timestamp server must be an absolute ASCII HTTP or HTTPS URL.",
                nameof(seedValue));
        if (seedValue.Certificate is not null)
        {
            ValidateCertificateSeedCertificates(subjectCertificates, "subject", nameof(seedValue));
            ValidateCertificateSeedCertificates(issuerCertificates, "issuer", nameof(seedValue));
            if (policyOids is { Length: 0 }
                || policyOids?.Any(oid => !IsValidObjectIdentifier(oid)) == true
                || policyOids?.Distinct(StringComparer.Ordinal).Count() != policyOids?.Length)
                throw new ArgumentException(
                    "Certificate policy object identifiers must be valid and unique.",
                    nameof(seedValue));
            if (policyOids is not null && issuerCertificates is null)
                throw new ArgumentException(
                    "Certificate policy constraints require acceptable issuer certificates.",
                    nameof(seedValue));
            if (seedValue.Certificate.RequireIssuer && issuerCertificates is null)
                throw new ArgumentException(
                    "A required certificate issuer needs an acceptable-certificate list.",
                    nameof(seedValue));
            if (seedValue.Certificate.RequireSubject && subjectCertificates is null)
                throw new ArgumentException(
                    "A required certificate subject needs an acceptable-certificate list.",
                    nameof(seedValue));
            if (seedValue.Certificate.RequireCertificatePolicy && policyOids is null)
                throw new ArgumentException(
                    "A required certificate policy needs an object-identifier list.",
                    nameof(seedValue));
            if (subjectDistinguishedNames is { Length: 0 }
                || subjectDistinguishedNames?.Any(distinguishedName =>
                    distinguishedName.Attributes.Count == 0
                    || distinguishedName.Attributes.Any(attribute =>
                        attribute.Key.Length == 0
                        || attribute.Key.Any(character => !char.IsAsciiLetterOrDigit(character)
                            && character != '.')
                        || string.IsNullOrWhiteSpace(attribute.Value))) == true)
                throw new ArgumentException(
                    "Certificate subject distinguished names require valid attributes and values.",
                    nameof(seedValue));
            if (seedValue.Certificate.RequireSubjectDistinguishedName
                && subjectDistinguishedNames is null)
                throw new ArgumentException(
                    "A required subject distinguished name needs an allowed-name list.",
                    nameof(seedValue));
            if (keyUsages is { Length: 0 })
                throw new ArgumentException(
                    "Certificate key-usage constraints cannot be empty.", nameof(seedValue));
            if (seedValue.Certificate.RequireKeyUsage && keyUsages is null)
                throw new ArgumentException(
                    "A required certificate key usage needs an allowed-usage list.",
                    nameof(seedValue));
            if (keyUsages?.Any(usage => CertificateKeyUsagePattern(usage) == "XXXXXXXXX") == true)
                throw new ArgumentException(
                    "Each certificate key-usage constraint must restrict at least one usage.",
                    nameof(seedValue));
            if (keyUsages?.Select(CertificateKeyUsagePattern).Distinct(StringComparer.Ordinal).Count()
                != keyUsages?.Length)
                throw new ArgumentException(
                    "Certificate key-usage constraints must be unique.", nameof(seedValue));
            if (seedValue.Certificate.EnrollmentUrl is not null
                && (!Uri.TryCreate(seedValue.Certificate.EnrollmentUrl, UriKind.Absolute,
                        out Uri? enrollmentUri)
                    || enrollmentUri.Scheme is not ("http" or "https")
                    || !seedValue.Certificate.EnrollmentUrl.All(character => character <= 0x7f)))
                throw new ArgumentException(
                    "A certificate enrollment URL must be an absolute ASCII HTTP or HTTPS URL.",
                    nameof(seedValue));
            if (seedValue.Certificate.EnrollmentUrlType.HasValue
                && !Enum.IsDefined(seedValue.Certificate.EnrollmentUrlType.Value))
                throw new ArgumentOutOfRangeException(nameof(seedValue));
            if (seedValue.Certificate.EnrollmentUrlType.HasValue
                && seedValue.Certificate.EnrollmentUrl is null)
                throw new ArgumentException(
                    "A certificate enrollment URL type requires an enrollment URL.",
                    nameof(seedValue));
            if (seedValue.Certificate.RequireEnrollmentUrl
                && seedValue.Certificate.EnrollmentUrl is null)
                throw new ArgumentException(
                    "A required certificate enrollment URL needs a URL value.", nameof(seedValue));
            if (subjectCertificates is null && issuerCertificates is null && policyOids is null
                && subjectDistinguishedNames is null && keyUsages is null
                && seedValue.Certificate.EnrollmentUrl is null)
                throw new ArgumentException(
                    "A certificate seed value must define at least one constraint.",
                    nameof(seedValue));
        }
        if (seedValue.DocumentLockIntent.HasValue
            && !Enum.IsDefined(seedValue.DocumentLockIntent.Value))
            throw new ArgumentOutOfRangeException(nameof(seedValue));
        if (seedValue.RequireDocumentLockIntent && !seedValue.DocumentLockIntent.HasValue)
            throw new ArgumentException(
                "A required document-lock intent needs an intent value.", nameof(seedValue));
        if (seedValue.AppearanceName is not null
            && string.IsNullOrWhiteSpace(seedValue.AppearanceName))
            throw new ArgumentException(
                "A signature appearance name cannot be empty.", nameof(seedValue));
        if (seedValue.RequireAppearance && seedValue.AppearanceName is null)
            throw new ArgumentException(
                "A required signature appearance needs an appearance name.", nameof(seedValue));
        if (seedValue.CertificationPermission.HasValue
            && !Enum.IsDefined(seedValue.CertificationPermission.Value))
            throw new ArgumentOutOfRangeException(nameof(seedValue));
        if (!seedValue.Handler.HasValue && !seedValue.ParserVersion.HasValue
            && subFilters is null && methods is null && reasons is null
            && legalAttestations is null && seedValue.Timestamp is null
            && seedValue.Certificate is null
            && !seedValue.DocumentLockIntent.HasValue && seedValue.AppearanceName is null
            && !seedValue.AddRevocationInformation
            && !seedValue.CertificationPermission.HasValue)
            throw new ArgumentException(
                "A signature seed value must define at least one constraint.", nameof(seedValue));
        return seedValue with
        {
            SubFilters = subFilters,
            DigestMethods = methods,
            Reasons = reasons,
            LegalAttestations = legalAttestations,
            Certificate = seedValue.Certificate is null ? null : seedValue.Certificate with
            {
                SubjectCertificates = subjectCertificates,
                IssuerCertificates = issuerCertificates,
                CertificatePolicyObjectIdentifiers = policyOids,
                SubjectDistinguishedNames = subjectDistinguishedNames,
                KeyUsages = keyUsages
            }
        };
    }

    private static void ValidateCertificateSeedCertificates(
        byte[][]? certificates, string constraintName, string parameterName)
    {
        if (certificates is null) return;
        if (certificates.Length == 0 || certificates.Any(certificate => certificate.Length == 0))
            throw new ArgumentException(
                $"Certificate {constraintName} constraints must contain DER-encoded certificates.",
                parameterName);
        if (certificates.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count()
            != certificates.Length)
            throw new ArgumentException(
                $"Certificate {constraintName} constraints must be unique.", parameterName);
        foreach (byte[] certificateBytes in certificates)
        {
            try
            {
                using X509Certificate2 certificate =
                    X509CertificateLoader.LoadCertificate(certificateBytes);
                if (certificate.Version != 3)
                    throw new ArgumentException(
                        $"Certificate {constraintName} constraints require X.509v3 certificates.",
                        parameterName);
            }
            catch (CryptographicException exception)
            {
                throw new ArgumentException(
                    $"Certificate {constraintName} constraints must contain valid DER certificates.",
                    parameterName, exception);
            }
        }
    }

    private static bool IsValidObjectIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] arcs = value.Split('.');
        if (arcs.Length < 2 || arcs.Any(arc => arc.Length == 0
            || (arc.Length > 1 && arc[0] == '0')
            || arc.Any(character => character is < '0' or > '9')))
            return false;
        if (!int.TryParse(arcs[0], NumberStyles.None, CultureInfo.InvariantCulture,
                out int first) || first is < 0 or > 2)
            return false;
        return first == 2 || int.TryParse(arcs[1], NumberStyles.None,
            CultureInfo.InvariantCulture, out int second) && second <= 39;
    }

    private static PdfFormFieldMetadata? ValidateFieldMetadata(PdfFormFieldMetadata? metadata)
    {
        if (metadata?.Tooltip is not null && string.IsNullOrWhiteSpace(metadata.Tooltip))
            throw new ArgumentException("A field tooltip cannot be empty.", nameof(metadata));
        if (metadata?.MappingName is not null && string.IsNullOrWhiteSpace(metadata.MappingName))
            throw new ArgumentException("A field mapping name cannot be empty.", nameof(metadata));
        return metadata;
    }

    private static void AddFieldMetadata(
        List<(string Name, PdfObject Value)> entries,
        PdfFormFieldMetadata? metadata)
    {
        if (metadata?.Tooltip is not null)
            entries.Add(("TU", UnicodeString(metadata.Tooltip)));
        if (metadata?.MappingName is not null)
            entries.Add(("TM", UnicodeString(metadata.MappingName)));
    }

    private static int FormFieldFlags(PdfFormFieldOptions options)
    {
        int flags = 0;
        if (options.ReadOnly) flags |= 1;
        if (options.Required) flags |= 1 << 1;
        if (options.NoExport) flags |= 1 << 2;
        return flags;
    }

    private static int FormWidgetFlags(PdfFormFieldVisibility visibility) => visibility switch
    {
        PdfFormFieldVisibility.Visible => 4,
        PdfFormFieldVisibility.Hidden => 2,
        PdfFormFieldVisibility.VisibleButDoesNotPrint => 0,
        PdfFormFieldVisibility.HiddenButPrintable => 36,
        _ => throw new ArgumentOutOfRangeException(nameof(visibility))
    };

    private static void ValidateFormFontText(TrueTypeFont font, string value, string parameterName)
    {
        if (!font.EmbeddingAllowed)
            throw new ArgumentException($"Font {font.PostScriptName} prohibits PDF embedding.", parameterName);
        foreach (FontGlyphMapping mapping in font.MapText(value))
        {
            if (mapping.UnicodeSequence is "\r" or "\n")
                continue;
            if (mapping.Glyph == 0 && mapping.UnicodeSequence != "\0")
                throw new ArgumentException(
                    $"Font {font.PostScriptName} has no glyph for {FormatUnicodeSequence(mapping.UnicodeSequence)}.", parameterName);
        }
    }

    private static string FormatUnicodeSequence(string value) =>
        string.Join(" ", value.EnumerateRunes().Select(rune => $"U+{rune.Value:X4}"));

    private static void AddEmbeddedFontObjects(
        List<PdfIndirectObject> objects, AllocatedEmbeddedFont allocated)
    {
        var type0 = new PdfIndirectReference(allocated.Type0Number, 0);
        var cidFont = new PdfIndirectReference(allocated.CidFontNumber, 0);
        var descriptor = new PdfIndirectReference(allocated.DescriptorNumber, 0);
        var fontFile = new PdfIndirectReference(allocated.FontFileNumber, 0);
        var toUnicode = new PdfIndirectReference(allocated.ToUnicodeNumber, 0);
        var encoding = new PdfIndirectReference(allocated.EncodingNumber, 0);
        EmbeddedTrueTypeFontObjects values = PdfEmbeddedTrueTypeFontFactory.Create(
            allocated.Font, allocated.Mappings, type0, cidFont, descriptor, fontFile, toUnicode,
            encoding);
        objects.Add(new PdfIndirectObject(allocated.FontFileNumber, 0, values.FontFile, 0));
        objects.Add(new PdfIndirectObject(allocated.DescriptorNumber, 0, values.Descriptor, 0));
        objects.Add(new PdfIndirectObject(allocated.CidFontNumber, 0, values.CidFont, 0));
        objects.Add(new PdfIndirectObject(allocated.ToUnicodeNumber, 0, values.ToUnicode, 0));
        objects.Add(new PdfIndirectObject(allocated.EncodingNumber, 0, values.Encoding, 0));
        objects.Add(new PdfIndirectObject(allocated.Type0Number, 0, values.Type0, 0));
    }

    private static PdfString Latin1String(string value) =>
        new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);

    private static PdfString UnicodeString(string value)
    {
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(value);
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        text.CopyTo(bytes, 2);
        return new PdfString(bytes, PdfStringForm.Hexadecimal);
    }

    internal static PdfDictionary BuildInfo(PdfDocumentMetadata metadata)
    {
        var entries = new List<(string Name, PdfObject Value)>();
        Add("Title", metadata.Title);
        Add("Author", metadata.Author);
        Add("Subject", metadata.Subject);
        Add("Keywords", metadata.Keywords);
        Add("Creator", metadata.Creator);
        Add("Producer", metadata.Producer);
        AddDate("CreationDate", metadata.CreationDate);
        AddDate("ModDate", metadata.ModificationDate);
        if (metadata.Trapped.HasValue)
            entries.Add(("Trapped", Name(metadata.Trapped.Value.ToString())));
        return Dictionary([.. entries]);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) entries.Add((name, UnicodeString(value)));
        }
        void AddDate(string name, DateTimeOffset? value)
        {
            if (value.HasValue) entries.Add((name, Latin1String(PdfDate(value.Value))));
        }
    }

    private static byte[] BuildXmp(
        PdfDocumentMetadata metadata, PdfA4Flavor pdfA4Flavor, bool pdfUa2)
    {
        using var output = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            Indent = false,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.None
        };
        using (XmlWriter xml = XmlWriter.Create(output, settings))
        {
            xml.WriteProcessingInstruction("xpacket", "begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"");
            xml.WriteStartElement("x", "xmpmeta", "adobe:ns:meta/");
            xml.WriteStartElement("rdf", "RDF", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
            xml.WriteStartElement("rdf", "Description", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
            xml.WriteAttributeString("rdf", "about", null, string.Empty);
            WriteSimple("dc", "format", "http://purl.org/dc/elements/1.1/", "application/pdf");
            WriteAlternative("title", metadata.Title);
            WriteSequence("creator", metadata.Author);
            WriteAlternative("description", metadata.Subject);
            WriteBag("language", metadata.Language);
            WriteSimple("pdf", "Keywords", "http://ns.adobe.com/pdf/1.3/", metadata.Keywords);
            WriteSimple("pdf", "Producer", "http://ns.adobe.com/pdf/1.3/", metadata.Producer);
            WriteSimple("pdf", "Trapped", "http://ns.adobe.com/pdf/1.3/",
                metadata.Trapped?.ToString());
            WriteSimple("xmp", "CreatorTool", "http://ns.adobe.com/xap/1.0/", metadata.Creator);
            WriteSimple("xmp", "CreateDate", "http://ns.adobe.com/xap/1.0/", XmpDate(metadata.CreationDate));
            WriteSimple("xmp", "ModifyDate", "http://ns.adobe.com/xap/1.0/", XmpDate(metadata.ModificationDate));
            if (pdfA4Flavor != PdfA4Flavor.None)
            {
                WriteSimple("pdfaid", "part", "http://www.aiim.org/pdfa/ns/id/", "4");
                WriteSimple("pdfaid", "rev", "http://www.aiim.org/pdfa/ns/id/", "2020");
                string? conformance = pdfA4Flavor switch
                {
                    PdfA4Flavor.EmbeddedFiles => "F",
                    PdfA4Flavor.Engineering => "E",
                    _ => null
                };
                WriteSimple("pdfaid", "conformance", "http://www.aiim.org/pdfa/ns/id/", conformance);
            }
            if (pdfUa2)
            {
                WriteSimple("pdfuaid", "part", "http://www.aiim.org/pdfua/ns/id/", "2");
                WriteSimple("pdfuaid", "rev", "http://www.aiim.org/pdfua/ns/id/", "2024");
            }
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteProcessingInstruction("xpacket", "end=\"w\"");

            void WriteSimple(string prefix, string name, string ns, string? value)
            {
                if (!string.IsNullOrEmpty(value)) xml.WriteElementString(prefix, name, ns, value);
            }
            void WriteAlternative(string name, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                xml.WriteStartElement("dc", name, "http://purl.org/dc/elements/1.1/");
                xml.WriteStartElement("rdf", "Alt", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
                xml.WriteStartElement("rdf", "li", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
                xml.WriteAttributeString("xml", "lang", null, "x-default");
                xml.WriteString(value);
                xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
            }
            void WriteSequence(string name, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                xml.WriteStartElement("dc", name, "http://purl.org/dc/elements/1.1/");
                xml.WriteStartElement("rdf", "Seq", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
                xml.WriteElementString("rdf", "li", "http://www.w3.org/1999/02/22-rdf-syntax-ns#", value);
                xml.WriteEndElement(); xml.WriteEndElement();
            }
            void WriteBag(string name, string? value)
            {
                if (string.IsNullOrEmpty(value)) return;
                xml.WriteStartElement("dc", name, "http://purl.org/dc/elements/1.1/");
                xml.WriteStartElement("rdf", "Bag", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
                xml.WriteElementString(
                    "rdf", "li", "http://www.w3.org/1999/02/22-rdf-syntax-ns#", value);
                xml.WriteEndElement(); xml.WriteEndElement();
            }
        }
        return output.ToArray();
    }

    internal static byte[] BuildXmp(PdfDocumentMetadata metadata) =>
        BuildXmp(metadata, PdfA4Flavor.None, pdfUa2: false);

    private enum PdfA4Flavor
    {
        None,
        General,
        EmbeddedFiles,
        Engineering
    }

    private static byte[] DocumentIdentifier(IEnumerable<PdfIndirectObject> objects)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (PdfIndirectObject value in objects.OrderBy(value => value.ObjectNumber))
            hash.AppendData(PdfObjectWriter.Write(value));
        return hash.GetHashAndReset()[..16];
    }

    private static string PdfDate(DateTimeOffset value)
    {
        TimeSpan offset = value.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'";
    }

    private static string? XmpDate(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private void RequireVersion(PdfVersion minimum, string feature)
    {
        if (Version.CompareTo(minimum) < 0)
            throw new InvalidOperationException(
                $"{feature} requires PDF {minimum} or later.");
    }

    private static bool HasImageSoftMask(PdfImage image) =>
        image.SoftMask is not null && (image.SoftMask.SoftMask is null
            || HasImageSoftMask(image.SoftMask));

    private static void ValidateDimension(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Page dimensions must be positive finite numbers.");
    }

    private void ValidatePageIndex(int pageIndex, string name)
    {
        if ((uint)pageIndex >= (uint)_pages.Count)
            throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateRectangle(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
    }

    private static void WriteAscii(Stream output, string value)
    {
        foreach (char character in value)
            output.WriteByte(checked((byte)character));
    }

    private static PdfDictionary ResourceDictionary(
        IReadOnlyDictionary<PdfStandardFont, PdfName> fonts,
        IReadOnlyCollection<EmbeddedFontUsage> embeddedFontUsages,
        IReadOnlyDictionary<PdfImage, PdfName> images,
        IReadOnlyDictionary<PdfOptionalContentGroup, PdfName> optionalContentGroups,
        IReadOnlyDictionary<PdfGraphicsState, PdfName> graphicsStates,
        IReadOnlyDictionary<PdfShading, PdfName> shadings,
        IReadOnlyDictionary<PdfFormXObject, PdfName> forms,
        IReadOnlyDictionary<PdfTilingPattern, PdfName> patterns,
        IReadOnlyDictionary<PdfIccProfile, PdfName> iccColorSpaces,
        IReadOnlyDictionary<PdfSpotColor, PdfName> spotColors,
        IReadOnlyDictionary<PdfLabColorSpace, PdfName> labColorSpaces,
        IReadOnlyDictionary<PdfIndexedColorSpace, PdfName> indexedColorSpaces,
        IReadOnlyDictionary<PdfCalibratedColorSpace, PdfName> calibratedColorSpaces,
        Dictionary<PdfStandardFont, int> fontNumbers,
        IReadOnlyCollection<AllocatedEmbeddedFont> embeddedFonts,
        Dictionary<PdfImage, int> imageNumbers,
        Dictionary<PdfOptionalContentGroup, int> optionalContentNumbers,
        Dictionary<PdfGraphicsState, int> graphicsStateNumbers,
        Dictionary<PdfShading, int> shadingNumbers,
        Dictionary<PdfFormXObject, int> formNumbers,
        Dictionary<PdfTilingPattern, int> patternNumbers,
        Dictionary<PdfIccProfile, int> iccProfileNumbers)
    {
        var entries = new List<(string Name, PdfObject Value)>();
        if (fonts.Count > 0 || embeddedFontUsages.Count > 0)
        {
            var values = fonts.Select(entry => new KeyValuePair<PdfName, PdfObject>(
                entry.Value, new PdfIndirectReference(fontNumbers[entry.Key], 0))).ToList();
            values.AddRange(embeddedFontUsages.Select(usage =>
                new KeyValuePair<PdfName, PdfObject>(usage.ResourceName,
                    new PdfIndirectReference(embeddedFonts.Single(font =>
                        font.Usages.Contains(usage)).Type0Number, 0))));
            entries.Add(("Font", new PdfDictionary(values)));
        }
        if (images.Count > 0 || forms.Count > 0)
        {
            var values = images.Select(entry => new KeyValuePair<PdfName, PdfObject>(
                entry.Value, new PdfIndirectReference(imageNumbers[entry.Key], 0))).ToList();
            values.AddRange(forms.Select(entry => new KeyValuePair<PdfName, PdfObject>(
                entry.Value, new PdfIndirectReference(formNumbers[entry.Key], 0))));
            entries.Add(("XObject", new PdfDictionary(values)));
        }
        if (optionalContentGroups.Count > 0)
            entries.Add(("Properties", new PdfDictionary(optionalContentGroups.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    new PdfIndirectReference(optionalContentNumbers[entry.Key], 0))))));
        if (graphicsStates.Count > 0)
            entries.Add(("ExtGState", new PdfDictionary(graphicsStates.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    new PdfIndirectReference(graphicsStateNumbers[entry.Key], 0))))));
        if (shadings.Count > 0)
            entries.Add(("Shading", new PdfDictionary(shadings.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    new PdfIndirectReference(shadingNumbers[entry.Key], 0))))));
        if (patterns.Count > 0)
            entries.Add(("Pattern", new PdfDictionary(patterns.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    new PdfIndirectReference(patternNumbers[entry.Key], 0))))));
        if (iccColorSpaces.Count > 0 || spotColors.Count > 0 || labColorSpaces.Count > 0
            || indexedColorSpaces.Count > 0 || calibratedColorSpaces.Count > 0)
        {
            var colorSpaces = iccColorSpaces.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    new PdfArray([
                        Name("ICCBased"),
                        new PdfIndirectReference(iccProfileNumbers[entry.Key], 0)])))
                .ToList();
            colorSpaces.AddRange(spotColors.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    SpotColorSpace(entry.Key))));
            colorSpaces.AddRange(labColorSpaces.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    LabColorSpace(entry.Key))));
            colorSpaces.AddRange(indexedColorSpaces.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    IndexedColorSpace(entry.Key))));
            colorSpaces.AddRange(calibratedColorSpaces.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Value,
                    CalibratedColorSpace(entry.Key))));
            entries.Add(("ColorSpace", new PdfDictionary(colorSpaces)));
        }
        return Dictionary([.. entries]);
    }

    private static PdfDictionary ShadingDictionary(PdfShading shading)
    {
        PdfArray coordinates = shading switch
        {
            PdfAxialGradient axial => new PdfArray([
                Number(axial.StartX), Number(axial.StartY),
                Number(axial.EndX), Number(axial.EndY)]),
            PdfRadialGradient radial => new PdfArray([
                Number(radial.StartX), Number(radial.StartY), Number(radial.StartRadius),
                Number(radial.EndX), Number(radial.EndY), Number(radial.EndRadius)]),
            _ => throw new NotSupportedException(
                $"Shading type {shading.GetType().FullName} cannot be authored.")
        };
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("ShadingType", new PdfInteger(shading is PdfAxialGradient ? 2 : 3)),
            ("ColorSpace", Name(shading.ColorSpace switch
            {
                PdfGradientColorSpace.Gray => "DeviceGray",
                PdfGradientColorSpace.Rgb => "DeviceRGB",
                PdfGradientColorSpace.Cmyk => "DeviceCMYK",
                _ => throw new ArgumentOutOfRangeException(nameof(shading))
            })),
            ("Coords", coordinates),
            ("Function", GradientFunction(shading.Stops)),
            ("Extend", new PdfArray([
                new PdfBoolean(shading.ExtendStart), new PdfBoolean(shading.ExtendEnd)])),
            ("AntiAlias", new PdfBoolean(shading.AntiAlias))
        };
        if (shading.Bounds is PdfShadingBounds bounds)
            entries.Add(("BBox", new PdfArray([
                Number(bounds.MinimumX), Number(bounds.MinimumY),
                Number(bounds.MaximumX), Number(bounds.MaximumY)])));
        if (shading.Background is not null)
            entries.Add(("Background", new PdfArray(
                shading.Background.Components.Select(Number))));
        return Dictionary([.. entries]);
    }

    private static PdfArray SpotColorSpace(PdfSpotColor color) => new([
        Name("Separation"),
        new PdfName(PdfUnicodeEncoding.EncodeUtf8(color.Name)),
        Name("DeviceCMYK"),
        Dictionary(
            ("FunctionType", new PdfInteger(2)),
            ("Domain", new PdfArray([new PdfInteger(0), new PdfInteger(1)])),
            ("C0", new PdfArray([
                new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)])),
            ("C1", new PdfArray([
                Number(color.AlternateColor.Cyan), Number(color.AlternateColor.Magenta),
                Number(color.AlternateColor.Yellow), Number(color.AlternateColor.Black)])),
            ("N", new PdfInteger(1))) ]);

    private static PdfArray LabColorSpace(PdfLabColorSpace colorSpace) => new([
        Name("Lab"),
        Dictionary(
            ("WhitePoint", new PdfArray([
                Number(colorSpace.WhiteX), Number(colorSpace.WhiteY), Number(colorSpace.WhiteZ)])),
            ("BlackPoint", new PdfArray([
                Number(colorSpace.BlackX), Number(colorSpace.BlackY), Number(colorSpace.BlackZ)])),
            ("Range", new PdfArray([
                Number(colorSpace.MinimumA), Number(colorSpace.MaximumA),
                Number(colorSpace.MinimumB), Number(colorSpace.MaximumB)]))) ]);

    private static PdfArray IndexedColorSpace(PdfIndexedColorSpace colorSpace) => new([
        Name("Indexed"),
        Name(colorSpace.BaseColorSpace switch
        {
            PdfIndexedBaseColorSpace.Gray => "DeviceGray",
            PdfIndexedBaseColorSpace.Rgb => "DeviceRGB",
            PdfIndexedBaseColorSpace.Cmyk => "DeviceCMYK",
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace))
        }),
        new PdfInteger(colorSpace.EntryCount - 1),
        new PdfString(colorSpace.Palette.Span, PdfStringForm.Hexadecimal) ]);

    private static PdfArray CalibratedColorSpace(PdfCalibratedColorSpace colorSpace)
    {
        var entries = new List<(string Name, PdfObject Value)>
        {
            ("WhitePoint", new PdfArray([
                Number(colorSpace.WhiteX), Number(colorSpace.WhiteY), Number(colorSpace.WhiteZ)])),
            ("BlackPoint", new PdfArray([
                Number(colorSpace.BlackX), Number(colorSpace.BlackY), Number(colorSpace.BlackZ)]))
        };
        string name;
        switch (colorSpace)
        {
            case PdfCalGrayColorSpace gray:
                name = "CalGray";
                entries.Add(("Gamma", Number(gray.Gamma)));
                break;
            case PdfCalRgbColorSpace rgb:
                name = "CalRGB";
                entries.Add(("Gamma", new PdfArray(rgb.Gamma.Select(Number))));
                entries.Add(("Matrix", new PdfArray(rgb.Matrix.Select(Number))));
                break;
            default:
                throw new NotSupportedException(
                    $"Calibrated color space {colorSpace.GetType().FullName} cannot be authored.");
        }
        return new PdfArray([Name(name), Dictionary([.. entries])]);
    }

    private static PdfDictionary GradientFunction(IReadOnlyList<PdfGradientStop> stops)
    {
        static PdfDictionary Segment(PdfGradientStop start, PdfGradientStop end) => Dictionary(
            ("FunctionType", new PdfInteger(2)),
            ("Domain", new PdfArray([new PdfInteger(0), new PdfInteger(1)])),
            ("C0", new PdfArray(start.Components.Select(Number))),
            ("C1", new PdfArray(end.Components.Select(Number))),
            ("N", new PdfInteger(1)));

        if (stops.Count == 2) return Segment(stops[0], stops[1]);
        var functions = new List<PdfObject>(stops.Count - 1);
        var bounds = new List<PdfObject>(stops.Count - 2);
        var encode = new List<PdfObject>((stops.Count - 1) * 2);
        for (int index = 0; index + 1 < stops.Count; index++)
        {
            functions.Add(Segment(stops[index], stops[index + 1]));
            if (index + 1 < stops.Count - 1) bounds.Add(Number(stops[index + 1].Offset));
            encode.Add(new PdfInteger(0));
            encode.Add(new PdfInteger(1));
        }
        return Dictionary(
            ("FunctionType", new PdfInteger(3)),
            ("Domain", new PdfArray([new PdfInteger(0), new PdfInteger(1)])),
            ("Functions", new PdfArray(functions)),
            ("Bounds", new PdfArray(bounds)),
            ("Encode", new PdfArray(encode)));
    }

    private static PdfDictionary Dictionary(params (string Name, PdfObject Value)[] entries) =>
        new(entries.Select(entry => new KeyValuePair<PdfName, PdfObject>(Name(entry.Name), entry.Value)));

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));

    private static PdfName? PageLabelName(PdfPageLabelStyle style) => style switch
    {
        PdfPageLabelStyle.None => null,
        PdfPageLabelStyle.Decimal => Name("D"),
        PdfPageLabelStyle.UpperRoman => Name("R"),
        PdfPageLabelStyle.LowerRoman => Name("r"),
        PdfPageLabelStyle.UpperLetters => Name("A"),
        PdfPageLabelStyle.LowerLetters => Name("a"),
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    private static string PageBoxName(PdfPageBox box) => box switch
    {
        PdfPageBox.Crop => "CropBox",
        PdfPageBox.Bleed => "BleedBox",
        PdfPageBox.Trim => "TrimBox",
        PdfPageBox.Art => "ArtBox",
        _ => throw new ArgumentOutOfRangeException(nameof(box))
    };

    private static string PageTabOrderName(PdfPageTabOrder tabOrder) => tabOrder switch
    {
        PdfPageTabOrder.Row => "R",
        PdfPageTabOrder.Column => "C",
        PdfPageTabOrder.Structure => "S",
        PdfPageTabOrder.AnnotationArray => "A",
        _ => throw new ArgumentOutOfRangeException(nameof(tabOrder))
    };

    private static string LinkBorderStyleName(PdfLinkBorderStyle style) => style switch
    {
        PdfLinkBorderStyle.Solid => "S",
        PdfLinkBorderStyle.Dashed => "D",
        PdfLinkBorderStyle.Beveled => "B",
        PdfLinkBorderStyle.Inset => "I",
        PdfLinkBorderStyle.Underline => "U",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    private static string LinkHighlightModeName(PdfLinkHighlightMode mode) => mode switch
    {
        PdfLinkHighlightMode.None => "N",
        PdfLinkHighlightMode.Invert => "I",
        PdfLinkHighlightMode.Outline => "O",
        PdfLinkHighlightMode.Push => "P",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static string PageLayoutName(PdfPageLayout layout) => layout switch
    {
        PdfPageLayout.SinglePage => "SinglePage",
        PdfPageLayout.OneColumn => "OneColumn",
        PdfPageLayout.TwoColumnLeft => "TwoColumnLeft",
        PdfPageLayout.TwoColumnRight => "TwoColumnRight",
        PdfPageLayout.TwoPageLeft => "TwoPageLeft",
        PdfPageLayout.TwoPageRight => "TwoPageRight",
        _ => throw new ArgumentOutOfRangeException(nameof(layout))
    };

    private static string PageModeName(PdfPageMode mode) => mode switch
    {
        PdfPageMode.UseNone => "UseNone",
        PdfPageMode.UseOutlines => "UseOutlines",
        PdfPageMode.UseThumbs => "UseThumbs",
        PdfPageMode.FullScreen => "FullScreen",
        PdfPageMode.UseOptionalContent => "UseOC",
        PdfPageMode.UseAttachments => "UseAttachments",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static PdfArray DestinationArray(
        PdfIndirectReference page, PdfDestination destination)
        => destination.ToArray(page);

    private sealed record PageDefinition(
        double Width,
        double Height,
        byte[] Content,
        IReadOnlyDictionary<PdfStandardFont, PdfName> Fonts,
        IReadOnlyList<EmbeddedFontUsage> EmbeddedFonts,
        IReadOnlyDictionary<PdfImage, PdfName> Images,
        IReadOnlyDictionary<PdfOptionalContentGroup, PdfName> OptionalContentGroups,
        IReadOnlyDictionary<PdfGraphicsState, PdfName> GraphicsStates,
        IReadOnlyDictionary<PdfShading, PdfName> Shadings,
        IReadOnlyDictionary<PdfFormXObject, PdfName> Forms,
        IReadOnlyDictionary<PdfTilingPattern, PdfName> Patterns,
        IReadOnlyDictionary<PdfIccProfile, PdfName> IccColorSpaces,
        IReadOnlyDictionary<PdfSpotColor, PdfName> SpotColors,
        IReadOnlyDictionary<PdfLabColorSpace, PdfName> LabColorSpaces,
        IReadOnlyDictionary<PdfIndexedColorSpace, PdfName> IndexedColorSpaces,
        IReadOnlyDictionary<PdfCalibratedColorSpace, PdfName> CalibratedColorSpaces,
        IReadOnlyList<LinkDefinition> Links,
        IReadOnlyCollection<int> MarkedContentIds,
        bool HasUntaggedContent,
        int Rotation,
        double UserUnit,
        IReadOnlyDictionary<PdfPageBox, PageBoxDefinition> Boxes,
        PdfPageTransition? Transition,
        double? DisplayDuration,
        PdfImage? Thumbnail,
        PdfPageTabOrder? TabOrder);
    private sealed record PageBoxDefinition(
        double X, double Y, double Width, double Height);
    private sealed record AllocatedPage(
        PageDefinition Definition, int PageNumber, int? ContentNumber, int[] AnnotationNumbers);
    private sealed record AccessibleAnnotationStructure(
        int PageIndex, int AnnotationNumber, int StructureElementNumber, int ParentTreeKey,
        string StructureType, string Description);
    private abstract record LinkDefinition(
        double X, double Y, double Width, double Height, PdfLinkAppearance Appearance,
        IReadOnlyList<PdfTextQuad>? Quads, PdfAnnotationMetadata? Metadata, string? Contents);
    private sealed record UriLinkDefinition(
        double X, double Y, double Width, double Height, PdfLinkAppearance Appearance, string Uri,
        IReadOnlyList<PdfTextQuad>? Quads = null, PdfAnnotationMetadata? Metadata = null,
        string? Contents = null)
        : LinkDefinition(X, Y, Width, Height, Appearance, Quads, Metadata, Contents);
    private sealed record PageLinkDefinition(
        double X, double Y, double Width, double Height, PdfLinkAppearance Appearance,
        int DestinationPageIndex, PdfDestination Destination,
        IReadOnlyList<PdfTextQuad>? Quads = null, PdfAnnotationMetadata? Metadata = null,
        string? Contents = null)
        : LinkDefinition(X, Y, Width, Height, Appearance, Quads, Metadata, Contents);
    private sealed record NamedDestinationLinkDefinition(
        double X, double Y, double Width, double Height, PdfLinkAppearance Appearance,
        string DestinationName, IReadOnlyList<PdfTextQuad>? Quads = null,
        PdfAnnotationMetadata? Metadata = null, string? Contents = null)
        : LinkDefinition(X, Y, Width, Height, Appearance, Quads, Metadata, Contents);
    private sealed record BookmarkDefinition(
        string Title, int? PageIndex, string? NamedDestination, int Level,
        PdfBookmarkOptions Options);
    private sealed record StructureElementDefinition(
        PdfStructureType Type,
        int Level,
        int? PageIndex,
        int? MarkedContentId,
        string? AlternateDescription,
        string? ActualText,
        PdfListNumbering? ListNumbering);
    private sealed record NamedDestinationDefinition(
        string Name, int PageIndex, PdfDestination Destination);
    private sealed record OpenActionDefinition(
        int? PageIndex, string? NamedDestination, PdfDestination? Destination);
    private sealed record PageLabelDefinition(
        int PageIndex, PdfPageLabelStyle Style, string? Prefix, int StartNumber);
    private sealed record AttachmentDefinition(
        string FileName,
        byte[] Data,
        string MimeType,
        string? Description,
        PdfAssociatedFileRelationship Relationship,
        DateTimeOffset? ModificationDate,
        DateTimeOffset? CreationDate);
    private sealed record AllocatedAttachment(
        AttachmentDefinition Definition, int EmbeddedFileNumber, int FileSpecificationNumber);
    private sealed record FileAttachmentAnnotationDefinition(
        int PageIndex, double X, double Y, double Size, string FileName, string? Contents,
        PdfFileAttachmentIcon Icon, PdfRgbColor Color, PdfAnnotationMetadata? Metadata);
    private sealed record AllocatedFileAttachmentAnnotation(
        FileAttachmentAnnotationDefinition Definition, int AnnotationNumber, int AppearanceNumber);
    private sealed record AllocatedFieldHierarchy(
        IReadOnlyList<AllocatedHierarchyField> Fields,
        IReadOnlyDictionary<int, TerminalFieldBinding> TerminalBindings,
        IReadOnlyList<int> RootFieldNumbers);
    private sealed record AllocatedHierarchyField(
        string QualifiedName, string PartialName, int FieldNumber, int? ParentNumber,
        List<int> KidNumbers);
    private sealed record TerminalFieldBinding(string PartialName, int? ParentNumber);
    private sealed record TextFieldDefinition(
        int PageIndex, string Name, double X, double Y, double Width, double Height,
        string Value, string DefaultValue, string? RichTextValue,
        double FontSize, PdfTextFieldOptions Options,
        TrueTypeFont? EmbeddedFont, PdfFormFieldAppearanceStyle AppearanceStyle,
        PdfFormFieldMetadata? Metadata);
    private sealed record AllocatedTextField(
        TextFieldDefinition Definition, int FieldNumber, int AppearanceNumber);
    private sealed record CheckBoxDefinition(
        int PageIndex, string Name, double X, double Y, double Width, double Height,
        bool IsChecked, bool DefaultChecked, string ExportValue, PdfFormFieldMetadata? Metadata,
        PdfFormFieldOptions Options, PdfCheckBoxMark Mark,
        PdfFormFieldAppearanceStyle AppearanceStyle);
    private sealed record AllocatedCheckBox(
        CheckBoxDefinition Definition,
        int FieldNumber,
        int OffAppearanceNumber,
        int OnAppearanceNumber);
    private sealed record RadioGroupDefinition(
        string Name, IReadOnlyList<PdfRadioButtonOption> Options, string? SelectedValue,
        string? DefaultSelectedValue,
        PdfFormFieldMetadata? Metadata, PdfFormFieldOptions FieldOptions,
        PdfRadioGroupOptions RadioOptions);
    private sealed record AllocatedRadioGroup(
        RadioGroupDefinition Definition, int ParentNumber, IReadOnlyList<AllocatedRadioWidget> Widgets);
    private sealed record AllocatedRadioWidget(
        PdfRadioButtonOption Option,
        int WidgetNumber,
        int OffAppearanceNumber,
        int OnAppearanceNumber);
    private sealed record ChoiceFieldDefinition(
        int PageIndex, string Name, double X, double Y, double Width, double Height,
        IReadOnlyList<PdfChoiceOption> Options, IReadOnlyList<string> SelectedValues,
        IReadOnlyList<string> DefaultSelectedValues,
        bool IsComboBox, bool IsMultiSelect, bool Editable, int TopIndex, double FontSize,
        TrueTypeFont? EmbeddedFont, PdfFormFieldMetadata? Metadata, PdfFormFieldOptions FieldOptions,
        PdfChoiceFieldOptions ChoiceOptions);
    private sealed record AllocatedChoiceField(
        ChoiceFieldDefinition Definition, int FieldNumber, int AppearanceNumber);
    private sealed record PushButtonDefinition(
        int PageIndex, string Name, double X, double Y, double Width, double Height,
        string Label, string? Uri, int? DestinationPageIndex, PdfDestination? Destination,
        string? NamedDestination,
        double FontSize, TrueTypeFont? EmbeddedFont,
        bool IsResetAction, IReadOnlyList<string>? ResetFields, bool ExcludeResetFields,
        string? SubmitUri, IReadOnlyList<string>? SubmitFields, bool ExcludeSubmitFields,
        PdfPushButtonHighlightMode HighlightMode,
        PdfFormFieldMetadata? Metadata, PdfFormFieldOptions FieldOptions,
        PdfFormFieldAppearanceStyle AppearanceStyle,
        PdfPushButtonAppearanceOptions AppearanceOptions);
    private sealed record AllocatedPushButton(
        PushButtonDefinition Definition, int FieldNumber, int AppearanceNumber,
        int? RolloverAppearanceNumber, int? DownAppearanceNumber);
    private sealed record SignatureFieldDefinition(
        int PageIndex, string Name, double X, double Y, double Width, double Height,
        PdfFormFieldMetadata? Metadata, PdfFormFieldOptions FieldOptions,
        PdfSignatureFieldLock? FieldLock, PdfSignatureSeedValue? SeedValue,
        string? AppearanceText, double FontSize, TrueTypeFont? EmbeddedFont,
        PdfFormFieldAppearanceStyle AppearanceStyle,
        PdfTextFieldAlignment AppearanceAlignment);
    private sealed record AllocatedSignatureField(
        SignatureFieldDefinition Definition, int FieldNumber, int AppearanceNumber,
        int? FieldLockNumber, int? SeedValueNumber);
    private sealed record OutputIntentDefinition(
        PdfIccProfile Profile,
        string Identifier,
        string? Condition,
        string? RegistryName,
        string? Information);
    private sealed record TextNoteDefinition(
        int PageIndex, double X, double Y, double Size, string Contents,
        PdfRgbColor Color, bool Open, PdfAnnotationMetadata? Metadata, PdfTextNoteIcon Icon,
        PdfTextNoteState? State, string? Name, string? InReplyTo,
        PdfAnnotationReplyType ReplyType, PdfAnnotationPopup? Popup);
    private sealed record AllocatedTextNote(
        TextNoteDefinition Definition, int AnnotationNumber, int AppearanceNumber,
        int? PopupNumber);
    private sealed record TextMarkupDefinition(
        PdfTextMarkupType Type, int PageIndex, IReadOnlyList<PdfTextQuad> Quads,
        string? Contents, PdfRgbColor Color, double Opacity, PdfAnnotationMetadata? Metadata);
    private sealed record AllocatedTextMarkup(
        TextMarkupDefinition Definition, int AnnotationNumber, int AppearanceNumber);
    private sealed record AllocatedEmbeddedFont(
        IReadOnlyList<EmbeddedFontUsage> Usages,
        IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> Mappings,
        int Type0Number,
        int CidFontNumber,
        int DescriptorNumber,
        int FontFileNumber,
        int ToUnicodeNumber,
        int EncodingNumber)
    {
        public TrueTypeFont Font => Usages[0].Font;
    }
}
