namespace KillerPDF.Services;

/// <summary>Turns the engine's reason for a picture whose position and size cannot be edited into the
/// resource key of the one-line status sentence the viewer shows.</summary>
internal static class SourceImageDiagnosis
{
    internal static string ReasonKey(string? reason) => reason switch
    {
        "InlineImage" => "Str_SourceImage_Inline",
        "NestedImage" => "Str_SourceImage_Nested",
        "ImageMask" => "Str_SourceImage_Masked",
        "OptionalContent" => "Str_SourceImage_Layers",
        "PageTransparencyGroup" => "Str_SourceImage_Transparency",
        "CompatibilityRecovery" => "Str_SourceImage_Repaired",
        "ComplexPageState" or "IncompletePageState" => "Str_SourceImage_Complex",
        "NonAxisAlignedPlacement" => "Str_SourceImage_Turned",
        "ClippedImage" => "Str_SourceImage_Clipped",
        _ => "Str_SourceImage_Unreadable"
    };
}
