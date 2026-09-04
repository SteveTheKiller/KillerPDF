namespace KillerPdf.Engine.Authoring;

/// <summary>Controls whether a form widget is shown on screen or included when printing.</summary>
public enum PdfFormFieldVisibility
{
    /// <summary>Shows the widget on screen and when printing.</summary>
    Visible,
    /// <summary>Hides the widget on screen and when printing.</summary>
    Hidden,
    /// <summary>Shows the widget on screen but omits it when printing.</summary>
    VisibleButDoesNotPrint,
    /// <summary>Hides the widget on screen but includes it when printing.</summary>
    HiddenButPrintable
}

/// <summary>Behavior shared by AcroForm field types.</summary>
public sealed record PdfFormFieldOptions
{
    /// <summary>Gets whether users may change the field value.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Gets whether the field must have a value when submitted.</summary>
    public bool Required { get; init; }
    /// <summary>Gets whether the field is omitted from form submission.</summary>
    public bool NoExport { get; init; }
    /// <summary>Gets where the field widget is visible.</summary>
    public PdfFormFieldVisibility Visibility { get; init; }
}
