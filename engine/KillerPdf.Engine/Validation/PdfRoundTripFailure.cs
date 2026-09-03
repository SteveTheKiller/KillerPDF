using System.Globalization;
using System.Resources;

namespace KillerPdf.Engine.Validation;

/// <summary>A stable identifier for a rewrite verification failure.</summary>
public enum PdfRoundTripFailureCode
{
    /// <summary>The source failed structural inspection.</summary>
    SourceInspection,
    /// <summary>The first rewrite failed structural inspection.</summary>
    RewrittenInspection,
    /// <summary>The second authenticated rewrite failed structural inspection.</summary>
    SecondAuthenticatedInspection,
    /// <summary>The authenticated rewrites have different object graphs.</summary>
    AuthenticatedGraphMismatch,
    /// <summary>The unencrypted rewrites have different bytes.</summary>
    RewriteMismatch,
    /// <summary>The source requires a password.</summary>
    AuthenticationRequired,
    /// <summary>The supplied password was rejected.</summary>
    AuthenticationFailed
}

/// <summary>A rewrite failure with culture-independent code and numeric details.</summary>
/// <param name="Code">The failure identifier.</param>
/// <param name="FirstDifference">The zero-based first differing byte, when available.</param>
/// <param name="FirstLength">The byte length of the first rewrite, when available.</param>
/// <param name="SecondLength">The byte length of the second rewrite, when available.</param>
public sealed record PdfRoundTripFailure(
    PdfRoundTripFailureCode Code,
    int? FirstDifference = null,
    int? FirstLength = null,
    int? SecondLength = null)
{
    // Keep these resources in the engine assembly so standalone API and corpus consumers
    // receive the same messages without WPF or a separate satellite deployment contract.
    private static readonly ResourceManager Messages = new(
        "KillerPdf.Engine.Validation.PdfRoundTripMessages", typeof(PdfRoundTripFailure).Assembly);

    /// <summary>Formats the failure using the supplied culture, or the current UI culture.</summary>
    /// <remarks>Unsupported languages fall back to English. No process culture is changed.</remarks>
    public string Format(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        string? template = null;
        for (CultureInfo current = culture; current != CultureInfo.InvariantCulture; current = current.Parent)
        {
            template = Messages.GetString($"{current.Name}.{Code}", CultureInfo.InvariantCulture);
            if (template is not null) break;
        }
        template ??= Messages.GetString($"en.{Code}", CultureInfo.InvariantCulture)
            ?? throw new ArgumentOutOfRangeException(nameof(Code));
        return string.Format(culture, template, FirstDifference, FirstLength, SecondLength);
    }
}
