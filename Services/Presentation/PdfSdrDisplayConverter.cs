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
    // The monitor profile read for one display generation, reused by every page, tile and
    // refinement converted for that observation.
    private sealed record CachedProfile(long Generation, string? Monitor, string Name, PdfColorProfile Profile);
    private CachedProfile? _profile;

    /// <summary>Replaces the immutable display observation for subsequent conversions.</summary>
    public void UpdateDisplay(PdfDisplayDiscoverySnapshot display)
    {
        ArgumentNullException.ThrowIfNull(display);
        Volatile.Write(ref _display, display);
    }

    /// <summary>Whether pages shown for <paramref name="previous"/> look exactly as they would for
    /// <paramref name="next"/>, so a new display observation needs no redraw.</summary>
    public bool PresentsAlike(PdfDisplayDiscoverySnapshot previous, PdfDisplayDiscoverySnapshot next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        var policy = PdfDisplayPresentationPolicy.Resolve(next);
        if (policy != PdfDisplayPresentationPolicy.Resolve(previous)) return false;
        // Without application conversion the page bytes do not depend on the monitor's profile.
        if (policy.TransformOwner != PdfMonitorTransformOwner.Application) return true;
        // The pages were converted with the profile read for the previous observation; a profile of the same name can
        // have been replaced since, so the next observation's profile is read and compared. The previous observation
        // stays current when they match, so its profile stays the one kept for conversions.
        var cached = Volatile.Read(ref _profile);
        var before = previous.TargetAssociation!.Targets[0].StandardProfile;
        var after = next.TargetAssociation!.Targets[0].StandardProfile;
        if (cached is null || cached.Generation != previous.Generation || cached.Monitor != previous.MonitorIdentity
            || before is not { Status: PdfDisplayProfileStatus.Named } || after is not { Status: PdfDisplayProfileStatus.Named }
            || cached.Name != before.Name || string.IsNullOrWhiteSpace(after.Name))
            return false;
        try
        {
            return profileReader(after.Name, cancellationToken).ContentSha256 == cached.Profile.ContentSha256;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or FormatException or NotSupportedException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return false;
        }
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
        // A monitor without a profile is never the application's to convert (the policy above returns first).
        var association = display.TargetAssociation!.Targets[0].StandardProfile;
        if (association is not { Status: PdfDisplayProfileStatus.Named }
            || string.IsNullOrWhiteSpace(association.Name))
            return new(PdfSdrConversionOutcome.Unavailable, display.Generation, display.MonitorIdentity,
                null, ReadOnlyMemory<byte>.Empty, "MonitorProfileNotEstablished");
        try
        {
            var cached = Volatile.Read(ref _profile);
            PdfColorProfile profile;
            if (cached is not null && cached.Generation == display.Generation
                && cached.Monitor == display.MonitorIdentity && cached.Name == association.Name)
                profile = cached.Profile;
            else
            {
                profile = profileReader(association.Name, cancellationToken);
                Volatile.Write(ref _profile, new(display.Generation, display.MonitorIdentity, association.Name, profile));
            }
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
