using System.Printing;
using System.Xml.Linq;

namespace KillerPDF.Services;

/// <summary>Combines the print window's own settings with a ticket from the printer driver's
/// dialog.</summary>
internal static class PrintJobTicket
{
    private static readonly XNamespace Framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private static readonly XNamespace Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    /// <summary>The settings the print window has controls for. ShortEdge picks the binding
    /// side while TwoSided is on: long edge for books, short edge for notepads.</summary>
    internal readonly record struct Settings(
        bool Landscape, bool TwoSided, bool Grayscale, PageMediaSize? Media, InputBin? Source,
        bool ShortEdge = false, bool AppManagesColor = false);

    /// <summary>What a driver ticket says about those same settings; null where it is silent.</summary>
    internal readonly record struct DriverChoices(
        bool? Landscape, bool? TwoSided, bool? Grayscale, int? Copies, PageMediaSize? Media,
        bool? ShortEdge = null);

    /// <summary>Writes the print window's settings onto a ticket. A ticket that came from the
    /// driver dialog keeps the driver's exact variant (reverse landscape, monochrome) wherever
    /// it already agrees with the window. Use the returned ticket, which replaces the XML when
    /// the driver uses document-level duplex.</summary>
    internal static PrintTicket Apply(PrintTicket ticket, Settings settings, int copies, bool fromDriverDialog,
        bool documentDuplex = false)
    {
        PrintTicket applied = ApplyLayout(ticket, settings, copies, fromDriverDialog, documentDuplex);
        return settings.AppManagesColor ? WithoutDriverColorManagement(applied) : applied;
    }

    /// <summary>Asks the driver to apply no color management of its own (Print Schema
    /// PageColorManagement None), for pages already converted to the printer profile (#520).</summary>
    /// <remarks>A driver may ignore the request, so the print window tells the user to turn its
    /// color management off unless <see cref="ConfirmsNoColorManagement"/> holds.</remarks>
    internal static PrintTicket WithoutDriverColorManagement(PrintTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        using var xml = ticket.GetXmlStream();
        XDocument document = XDocument.Load(xml);
        XElement root = document.Root!;
        root.Elements(Framework + "Feature")
            .Where(element => HasKeyword(element, "PageColorManagement"))
            .Remove();
        string keywords = KeywordPrefix(root);
        var feature = new XElement(Framework + "Feature",
            new XAttribute("name", $"{keywords}:PageColorManagement"),
            new XElement(Framework + "Option", new XAttribute("name", $"{keywords}:None")));
        root.Add(feature);
        using var rewritten = new System.IO.MemoryStream();
        document.Save(rewritten);
        rewritten.Position = 0;
        return new PrintTicket(rewritten);
    }

    /// <summary>Reads whether a ticket asks the driver for no color management.</summary>
    internal static bool AsksForNoDriverColorManagement(PrintTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        using var xml = ticket.GetXmlStream();
        return XDocument.Load(xml).Root?.Elements(Framework + "Feature")
            .Where(element => HasKeyword(element, "PageColorManagement"))
            .SelectMany(element => element.Elements(Framework + "Option"))
            .Any(element => HasKeyword(element, "None")) == true;
    }

    /// <summary>Reads whether a driver's capabilities offer the Print Schema option for no color
    /// management (PageColorManagement None).</summary>
    internal static bool OffersNoColorManagement(System.IO.Stream capabilities) =>
        XDocument.Load(capabilities).Root?.Elements(Framework + "Feature")
            .Where(element => HasKeyword(element, "PageColorManagement"))
            .SelectMany(element => element.Elements(Framework + "Option"))
            .Any(element => HasKeyword(element, "None")) == true;

    /// <summary>Whether a driver confirms it will skip its own color management: it offers the
    /// option and keeps it in the ticket it validated from a request for it (#520).</summary>
    /// <remarks>Vendor color controls in a driver's private settings are invisible to the Print
    /// Schema, so a confirmation covers the standard switch only.</remarks>
    internal static bool ConfirmsNoColorManagement(System.IO.Stream capabilities, PrintTicket validated)
    {
        ArgumentNullException.ThrowIfNull(validated);
        return OffersNoColorManagement(capabilities) && AsksForNoDriverColorManagement(validated);
    }

    private static PrintTicket ApplyLayout(PrintTicket ticket, Settings settings, int copies, bool fromDriverDialog,
        bool documentDuplex)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ticket.CopyCount = copies;
        if (!fromDriverDialog || IsLandscape(ticket.PageOrientation) != settings.Landscape)
            ticket.PageOrientation = settings.Landscape
                ? PageOrientation.Landscape : PageOrientation.Portrait;
        // The window names the binding side itself now, so the job asks for that exact duplex
        // variant instead of inheriting whatever the driver's dialog last picked.
        Duplexing duplex = settings.TwoSided
            ? settings.ShortEdge ? Duplexing.TwoSidedShortEdge : Duplexing.TwoSidedLongEdge
            : Duplexing.OneSided;
        if (!fromDriverDialog || IsGrayscale(ticket.OutputColor) != settings.Grayscale)
            ticket.OutputColor = settings.Grayscale ? OutputColor.Grayscale : OutputColor.Color;
        if (settings.Media is not null) ticket.PageMediaSize = settings.Media;
        if (settings.Source is { } source) ticket.InputBin = source;
        using var xml = ticket.GetXmlStream();
        XDocument document = XDocument.Load(xml);
        XElement root = document.Root!;
        XElement? feature = root.Elements(Framework + "Feature")
            .FirstOrDefault(element => HasKeyword(element, "DocumentDuplex"));
        if (!documentDuplex && feature is null)
        {
            ticket.Duplexing = duplex;
            return ticket;
        }
        // The two scopes are mutually exclusive in the Print Schema. Keep the scope
        // advertised by this driver rather than letting a conflicting job default win.
        root.Elements(Framework + "Feature")
            .Where(element => HasKeyword(element, "JobDuplexAllDocumentsContiguously"))
            .Remove();
        feature ??= new XElement(Framework + "Feature");
        if (feature.Parent is null) root.Add(feature);
        string keywords = KeywordPrefix(root);
        feature.SetAttributeValue("name", $"{keywords}:DocumentDuplex");
        if (feature.Element(Framework + "Option") is not { } selected
            || !HasKeyword(selected, duplex.ToString()))
        {
            feature.Elements(Framework + "Option").Remove();
            feature.Add(new XElement(Framework + "Option", new XAttribute("name", $"{keywords}:{duplex}")));
        }
        using var rewritten = new System.IO.MemoryStream();
        document.Save(rewritten);
        rewritten.Position = 0;
        return new PrintTicket(rewritten);
    }

    /// <summary>The scale that fits an oversized page but never enlarges a smaller one.</summary>
    internal static double ShrinkOversized(double actual, double fit) => Math.Min(actual, fit);

    /// <summary>Reads a command-line duplex choice: none, long or short.</summary>
    internal static bool TryParseDuplex(string text, out bool twoSided, out bool shortEdge)
    {
        string choice = text.Trim().ToLowerInvariant();
        twoSided = choice is "long" or "short";
        shortEdge = choice == "short";
        return twoSided || choice == "none";
    }

    /// <summary>Groups pages into sheets. Reverse flips the order of the sheets and leaves the
    /// pages on each sheet in reading order.</summary>
    internal static List<List<int>> Sheets(IReadOnlyList<int> pages, int pagesPerSheet, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerSheet);
        List<List<int>> sheets = [.. pages.Chunk(pagesPerSheet).Select(sheet => sheet.ToList())];
        if (reverse) sheets.Reverse();
        return sheets;
    }

    /// <summary>Reads the settings a driver ticket implies for the print window's controls.</summary>
    internal static DriverChoices Read(PrintTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        Duplexing? duplexing = ReadDocumentDuplex(ticket)
            ?? (ticket.Duplexing is { } jobDuplex && jobDuplex != Duplexing.Unknown ? jobDuplex : null);
        return new DriverChoices(
            ticket.PageOrientation is { } orientation && orientation != PageOrientation.Unknown
                ? IsLandscape(orientation) : null,
            duplexing is { } duplex ? IsTwoSided(duplex) : null,
            ticket.OutputColor is { } color && color != OutputColor.Unknown
                ? IsGrayscale(color) : null,
            ticket.CopyCount is { } copies && copies >= 1 ? copies : null,
            ticket.PageMediaSize,
            duplexing is { } bound && IsTwoSided(bound) ? bound == Duplexing.TwoSidedShortEdge : null);
    }

    /// <summary>Recognizes the document-level duplex feature that WPF's typed capability omits.</summary>
    internal static bool SupportsDocumentDuplex(System.IO.Stream capabilities) =>
        XDocument.Load(capabilities).Root?.Elements(Framework + "Feature")
            .Where(element => HasKeyword(element, "DocumentDuplex"))
            .SelectMany(element => element.Elements(Framework + "Option"))
            .Any(element => HasKeyword(element, "TwoSidedLongEdge")) == true;

    private static Duplexing? ReadDocumentDuplex(PrintTicket ticket)
    {
        using var xml = ticket.GetXmlStream();
        XElement? feature = XDocument.Load(xml).Root?.Elements(Framework + "Feature")
            .FirstOrDefault(element => HasKeyword(element, "DocumentDuplex"));
        XElement? option = feature?.Element(Framework + "Option");
        if (option is null) return null;
        if (HasKeyword(option, "OneSided")) return Duplexing.OneSided;
        if (HasKeyword(option, "TwoSidedLongEdge")) return Duplexing.TwoSidedLongEdge;
        if (HasKeyword(option, "TwoSidedShortEdge")) return Duplexing.TwoSidedShortEdge;
        return null;
    }

    /// <summary>The ticket's prefix for the Print Schema keywords, declared on the root when the
    /// ticket has none. The spooler allows only a name attribute on a Feature or Option, so a
    /// declaration there fails the whole job (#520).</summary>
    private static string KeywordPrefix(XElement root)
    {
        string? prefix = root.GetPrefixOfNamespace(Keywords);
        if (prefix is not null) return prefix;
        prefix = "psk";
        for (int n = 1; root.GetNamespaceOfPrefix(prefix) is not null; n++) prefix = $"psk{n}";
        root.SetAttributeValue(XNamespace.Xmlns + prefix, Keywords.NamespaceName);
        return prefix;
    }

    private static bool HasKeyword(XElement element, string name)
    {
        string? value = (string?)element.Attribute("name");
        if (value is null) return false;
        int colon = value.IndexOf(':');
        return colon >= 0
            ? value[(colon + 1)..] == name && element.GetNamespaceOfPrefix(value[..colon]) == Keywords
            : value == name && element.GetDefaultNamespace() == Keywords;
    }

    private static bool IsLandscape(PageOrientation? orientation) =>
        orientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;

    private static bool IsTwoSided(Duplexing? duplexing) =>
        duplexing is Duplexing.TwoSidedLongEdge or Duplexing.TwoSidedShortEdge;

    private static bool IsGrayscale(OutputColor? color) =>
        color is OutputColor.Grayscale or OutputColor.Monochrome;
}
