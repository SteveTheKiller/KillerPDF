using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Immutable selections for portable printer proof conversion.</summary>
public sealed record PdfProofOptions
{
    /// <summary>Gets the source-to-printer rendering intent.</summary>
    public PdfRenderingIntent RenderingIntent { get; init; } = PdfRenderingIntent.RelativeColorimetric;

    /// <summary>Gets whether source-to-printer black-point compensation is enabled.</summary>
    public bool BlackPointCompensation { get; init; } = true;

    /// <summary>Gets whether the proof simulates printer paper.</summary>
    /// <remarks>
    /// With black simulation disabled, a viewing aid preserves paper tint while
    /// mapping printer black to display black. This is not an ICC rendering intent.
    /// Unsupported profile endpoints cause the proof transform to be unavailable.
    /// </remarks>
    public bool SimulatePaper { get; init; }

    /// <summary>Gets whether the proof preserves printer black rather than normalizing it.</summary>
    public bool SimulateBlack { get; init; }
}
