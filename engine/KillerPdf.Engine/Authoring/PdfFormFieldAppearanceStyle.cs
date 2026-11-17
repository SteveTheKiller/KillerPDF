namespace KillerPdf.Engine.Authoring;

/// <summary>Colors and border geometry used by an authored form-field widget.</summary>
public sealed record PdfFormFieldAppearanceStyle
{
    /// <summary>Gets the optional widget background color.</summary>
    public PdfRgbColor? BackgroundColor { get; init; } = new PdfRgbColor(1, 1, 1);
    /// <summary>Gets the optional widget border color.</summary>
    public PdfRgbColor? BorderColor { get; init; } = new PdfRgbColor(0, 0, 0);
    /// <summary>Gets the text or selection-mark color.</summary>
    public PdfRgbColor TextColor { get; init; } = new PdfRgbColor(0, 0, 0);
    /// <summary>Gets whether a text field's value is drawn underlined.</summary>
    public bool Underline { get; init; }
    /// <summary>Gets whether a text field's value is drawn struck through.</summary>
    public bool StrikeThrough { get; init; }
    /// <summary>
    /// Gets whether a widget without a background or border color says so in so many words, with empty
    /// /MK /BG and /BC arrays, so a viewer that tints fields with no colors of their own leaves it clear.
    /// </summary>
    public bool MarkedTransparent { get; init; }
    /// <summary>Gets the nonnegative border width.</summary>
    public double BorderWidth { get; init; } = 1;
    /// <summary>Gets the widget border style.</summary>
    public PdfFormFieldBorderStyle BorderStyle { get; init; }
    /// <summary>Gets the positive alternating dash and gap lengths for a dashed border.</summary>
    public IReadOnlyList<double>? DashPattern { get; init; }
}
