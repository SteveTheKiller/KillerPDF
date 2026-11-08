using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using System.IO;

namespace KillerPDF.Presentation;

/// <summary>Identifies the outcome of preparing application-managed SDR pixels.</summary>
public enum PdfSdrConversionOutcome { Converted, Stale, Unavailable }

/// <summary>Retains prepared monitor bytes and their observed display identity.</summary>
public sealed record PdfSdrDisplayConversion(PdfSdrConversionOutcome Outcome,
    long DisplayGeneration, string? MonitorIdentity, string? ProfileContentSha256,
    ReadOnlyMemory<byte> BgraPixels, string? FailureOperation);

/// <summary>Prepares monitor device bytes only for an observed conventional SDR display.</summary>
public sealed class PdfSdrDisplayConverter(Func<string, CancellationToken, PdfColorProfile> profileReader)
{
    private PdfDisplayDiscoverySnapshot? _display;

    /// <summary>Replaces the immutable display observation for subsequent conversions.</summary>
    public void UpdateDisplay(PdfDisplayDiscoverySnapshot display)
    {
        ArgumentNullException.ThrowIfNull(display);
        Volatile.Write(ref _display, display);
    }

    /// <summary>Prepares device bytes for the currently observed display.</summary>
    public PdfSdrDisplayConversion Convert(PdfRenderedFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var display = Volatile.Read(ref _display)
            ?? throw new InvalidOperationException("A display observation is required.");
        var policy = PdfDisplayPresentationPolicy.Resolve(display);
        if (policy.TransformOwner != PdfMonitorTransformOwner.Application)
            return new(PdfSdrConversionOutcome.Unavailable, display.Generation, display.MonitorIdentity,
                null, ReadOnlyMemory<byte>.Empty, "ApplicationMonitorConversionNotSelected");
        var association = display.TargetAssociation!.Targets[0].StandardProfile;
        if (association is not { Status: PdfDisplayProfileStatus.Named }
            || string.IsNullOrWhiteSpace(association.Name))
            return new(PdfSdrConversionOutcome.Unavailable, display.Generation, display.MonitorIdentity,
                null, ReadOnlyMemory<byte>.Empty, "MonitorProfileNotEstablished");
        try
        {
            var profile = profileReader(association.Name, cancellationToken);
            if (!ReferenceEquals(display, Volatile.Read(ref _display)))
                return new(PdfSdrConversionOutcome.Stale, display.Generation, display.MonitorIdentity,
                    null, ReadOnlyMemory<byte>.Empty, null);
            byte[] pixels = frame.ToDisplayBgra32(profile, cancellationToken);
            if (!ReferenceEquals(display, Volatile.Read(ref _display)))
                return new(PdfSdrConversionOutcome.Stale, display.Generation, display.MonitorIdentity,
                    null, ReadOnlyMemory<byte>.Empty, null);
            return new(PdfSdrConversionOutcome.Converted, display.Generation, display.MonitorIdentity,
                profile.ContentSha256, pixels, null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or FormatException or NotSupportedException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool stale = !ReferenceEquals(display, Volatile.Read(ref _display));
            return new(stale ? PdfSdrConversionOutcome.Stale : PdfSdrConversionOutcome.Unavailable,
                display.Generation, display.MonitorIdentity, null, ReadOnlyMemory<byte>.Empty,
                stale ? null : "MonitorProfileConversionUnavailable");
        }
    }
}
