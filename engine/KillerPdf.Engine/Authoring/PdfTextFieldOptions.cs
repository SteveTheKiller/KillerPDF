namespace KillerPdf.Engine.Authoring;

/// <summary>Behavior and presentation options for an authored text field.</summary>
public sealed record PdfTextFieldOptions
{
    /// <summary>Gets whether users may change the field value.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Gets whether the field must have a value when submitted.</summary>
    public bool Required { get; init; }
    /// <summary>Gets whether the field is omitted from form submission.</summary>
    public bool NoExport { get; init; }
    /// <summary>Gets where the field widget is visible.</summary>
    public PdfFormFieldVisibility Visibility { get; init; }
    /// <summary>Gets whether the field accepts multiple lines.</summary>
    public bool Multiline { get; init; }
    /// <summary>Gets whether entered text is visually obscured.</summary>
    public bool Password { get; init; }
    /// <summary>Gets whether the value represents a selected file path.</summary>
    public bool FileSelect { get; init; }
    /// <summary>Gets whether viewer spell checking is disabled.</summary>
    public bool DoNotSpellCheck { get; init; }
    /// <summary>Gets whether viewer scrolling is disabled.</summary>
    public bool DoNotScroll { get; init; }
    /// <summary>Gets whether text is divided among equally spaced character cells.</summary>
    public bool Comb { get; init; }
    /// <summary>Gets the optional maximum number of characters.</summary>
    public int? MaximumLength { get; init; }
    /// <summary>Gets the horizontal text alignment.</summary>
    public PdfTextFieldAlignment Alignment { get; init; }
}
