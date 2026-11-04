namespace KillerPdf.Engine.Rendering;

/// <summary>Describes presentation selected from observed display facts.</summary>
/// <param name="Mode">The required transport mode, not an initialized transport.</param>
/// <param name="TransformOwner">The required final monitor-conversion owner.</param>
/// <param name="ReferenceWhiteScale">The linear RGB multiplier for document white; alpha is unchanged.</param>
/// <param name="FailureOperation">A reason when observation cannot establish a policy.</param>
public sealed record PdfDisplayPresentationDecision(PdfPresentationMode Mode,
    PdfMonitorTransformOwner TransformOwner, double ReferenceWhiteScale,
    string? FailureOperation);

/// <summary>Selects presentation from a display discovery snapshot.</summary>
/// <remarks>
/// This policy interprets Windows Advanced Color observations. It does not create a transport,
/// load a monitor profile, perform conversion or establish physical accuracy. Conventional SDR
/// requires application-managed conversion. Advanced Color leaves final conversion to the
/// compositor; only HDR needs the reported SDR-white adjustment to its 80-nit scRGB reference.
/// An unavailable decision must not be treated as a successfully managed fallback.
/// </remarks>
public static class PdfDisplayPresentationPolicy
{
    /// <summary>Resolves the observed display into a presentation decision.</summary>
    public static PdfDisplayPresentationDecision Resolve(PdfDisplayDiscoverySnapshot display)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (display.Status == PdfDisplayDiscoveryStatus.Available
            && display.TargetAssociation is { Status: PdfDisplayTargetStatus.SingleTarget, Targets.Count: 1 } association)
        {
            var target = association.Targets[0];
            if (target.ColorState?.Mode == PdfDisplayColorMode.ConventionalSdr)
                return new(PdfPresentationMode.CompatibilitySdr, PdfMonitorTransformOwner.Application, 1, null);
            if (target.ColorState?.Mode == PdfDisplayColorMode.AdvancedColorSdr)
                return new(PdfPresentationMode.LinearFloat, PdfMonitorTransformOwner.Compositor, 1, null);
            if (target is { ColorState.Mode: PdfDisplayColorMode.Hdr,
                SdrWhiteLevel: { RawValue: > 0, NativeErrorCode: null } white })
                return new(PdfPresentationMode.LinearFloat, PdfMonitorTransformOwner.Compositor,
                    white.RawValue.Value / 1000d, null);
        }
        return new(PdfPresentationMode.Unavailable, PdfMonitorTransformOwner.Unknown, 1,
            "DisplayPresentationNotEstablished");
    }
}
