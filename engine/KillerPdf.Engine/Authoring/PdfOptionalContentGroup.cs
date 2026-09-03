namespace KillerPdf.Engine.Authoring;

/// <summary>A named PDF layer whose initial viewer visibility can be configured.</summary>
public sealed class PdfOptionalContentGroup
{
    /// <summary>Creates a named layer with an initial visibility state.</summary>
    public PdfOptionalContentGroup(
        string name, bool initiallyVisible = true,
        bool? visibleWhenPrinting = null,
        bool? visibleWhenExporting = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An optional-content group name cannot be empty.", nameof(name));
        Name = name;
        InitiallyVisible = initiallyVisible;
        VisibleWhenPrinting = visibleWhenPrinting;
        VisibleWhenExporting = visibleWhenExporting;
    }

    /// <summary>Gets the layer's nonempty display name.</summary>
    public string Name { get; }
    /// <summary>Gets whether the layer is visible in the default configuration.</summary>
    public bool InitiallyVisible { get; }
    /// <summary>Gets the preferred print visibility, or null when unspecified.</summary>
    public bool? VisibleWhenPrinting { get; }
    /// <summary>Gets the preferred export visibility, or null when unspecified.</summary>
    public bool? VisibleWhenExporting { get; }
}
