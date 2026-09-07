# Bookmarks, links, and opening destinations

This guide covers the 1.9 development navigation API. The engine reads or writes
PDF navigation data; the host owns bookmark trees, link hit testing, viewport
changes, and opening external targets. Reading navigation does not activate it.

## Create, inspect, and edit navigation

These examples use an unencrypted, unsigned document. Import the two namespaces
below and use normal .NET implicit usings:

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

static byte[] CreateNavigationSample()
{
    return new PdfDocumentBuilder().AddBlankPage().AddBlankPage()
        .AddNamedDestination("details", 1, PdfDestination.FitWidth())
        .AddBookmark("Overview", 0)
        .AddNamedDestinationBookmark("Details", "details", level: 1)
        .AddPageLink(0, 36, 700, 120, 24, 1, destination: PdfDestination.FitPage())
        .AddUriLink(0, 36, 660, 120, 24, "https://example.com/",
            contents: "Open the reference website")
        .SetOpenAction(0, PdfDestination.FitPage()).Build();
}

static (IReadOnlyList<PdfBookmarkInfo> Bookmarks, IReadOnlyList<PdfLinkInfo> Links,
    PdfInitialView InitialView) ReadNavigation(byte[] pdf)
{
    PdfDocument document = PdfDocument.Open(pdf);
    PdfDocumentInformation information = PdfDocumentInformation.Read(document);
    PdfLinkInfo[] links = Enumerable.Range(0, information.PageCount)
        .SelectMany(page => PdfLinkReader.ReadPage(document, page)).ToArray();
    return (PdfBookmarkReader.Read(document), links, information.InitialView);
}

static byte[] ChangeOpeningPage(byte[] pdf, int pageIndex)
{
    PdfDocument document = PdfDocument.Open(pdf);
    PdfInitialView current = PdfDocumentInformation.Read(document).InitialView;
    return (current with
    {
        NamedDestination = null,
        PageIndex = pageIndex,
        Destination = PdfDestination.FitPage()
    }).Apply(document);
}
```

Add pages before referring to their indices. All page indices are zero-based;
a displayed page label is not a page index. The sample creates one top-level
bookmark with a child targeting a named destination on page 2, plus local and
URI link annotations on page 1. A link annotation supplies a target and hit
rectangle; it does not create a visible text label.

## Present the returned data

`PdfBookmarkReader.Read` returns top-level items in document order.
Each item's `Children` retains the hierarchy. Titles are decoded strings;
style, color, and initial expansion state are separate properties. Object number
and generation identify the source item, not its title or current tree position.

`DestinationPageIndex` is nullable. Do not turn an unresolved destination into
page 1. Preserve `NamedDestination` when displaying or diagnosing named targets.
A bookmark can also expose a typed `Destination` with its view mode and
coordinates.

`PdfLinkReader.ReadPage` returns supported local and URI link targets.
`AnnotationIndex` refers to the page annotation array, which can also contain
other annotation types. Direct annotations need not have an object number.
The rectangle endpoints are ordered in PDF page coordinates; the host must
account for page boxes, rotation, zoom, and its screen origin during hit testing.
These values are not screen pixels.

The link reader does not expose every PDF action type or the full destination
view operands. Use its nullable local page, named destination, and URI fields for
the behavior it represents. The host should decide which URI schemes it will
open when the user activates a link. Reading a URI is not authorization to fetch it.

## Destination modes and initial view

`PdfDestination` supplies typed fit-page, fit-width, fit-height, bounding-box,
rectangle, and XYZ destinations. `At` accepts optional left/top coordinates and
a positive zoom factor; `FitRectangle` requires a nonempty rectangle.
Do not substitute a zoom percentage for a factor.

Read saved opening settings through
`PdfDocumentInformation.Read(document).InitialView`. This includes page layout,
navigation panel, viewer preferences, and a direct or named opening target.
A host may choose how to honor presentation requests such as full-screen mode.

`PdfInitialView.Apply` writes the complete selection in an incremental revision.
Null layout or mode clears that setting, and the absence of an opening target
clears the open action. A direct target requires both `PageIndex` and
`Destination`; it cannot be combined with `NamedDestination`.
The example starts from the existing record and changes only the opening target,
preserving its other presentation preferences.

Save the returned bytes to the host-selected destination. A preserved source
prefix is not proof that a signature permits the edit; use the
[security guide](security.md) for signature policy.

## Limits and related APIs

Malformed navigation structures can throw. Bookmark reading rejects cycles and
shared items, with bounds of 256 hierarchy levels and one million items.
These are parsing bounds, not a recommendation to eagerly create that many UI
controls. Large trees need appropriate host presentation.

Bookmark exchange and generation, navigation audits, and navigation macros are
separate APIs. This guide does not imply that all PDF actions are executable or
that rewriting a document preserves object identities.

The three functions were compiled and executed against the current engine.
Checks covered bookmark hierarchy, named and local page resolution, URI values,
the opening page before and after an edit, byte-prefix preservation, and rejection
of an out-of-range opening page.

