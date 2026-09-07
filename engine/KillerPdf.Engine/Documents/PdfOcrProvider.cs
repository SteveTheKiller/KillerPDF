namespace KillerPdf.Engine.Documents;

/// <summary>Describes one local OCR provider that can be selected for a document.</summary>
public sealed class PdfOcrProviderDescriptor
{
    private readonly string[] _languages;

    /// <summary>Creates immutable provider identity, capability, and preference metadata.</summary>
    public PdfOcrProviderDescriptor(string id, string displayName, Version version,
        IEnumerable<string> languages, int automaticPriority = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(languages);
        if (id.Length > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('-' or '_')))
            throw new ArgumentException(
                "OCR provider identifiers may contain only letters, numbers, hyphens, and underscores.",
                nameof(id));
        if (displayName.Length > 160)
            throw new ArgumentException("The OCR provider display name is too long.",
                nameof(displayName));
        _languages = [.. languages.Select(language => language?.Trim())
            .Where(language => !string.IsNullOrEmpty(language))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        if (_languages.Any(language => language.Length > 35
            || language.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_'))))
            throw new ArgumentException(
                "OCR provider language names may contain only letters, numbers, hyphens, and underscores.",
                nameof(languages));
        Id = id;
        DisplayName = displayName;
        Version = version;
        Languages = Array.AsReadOnly(_languages);
        AutomaticPriority = automaticPriority;
    }

    /// <summary>Gets the stable, settings-safe provider identifier.</summary>
    public string Id { get; }
    /// <summary>Gets the human-readable provider name.</summary>
    public string DisplayName { get; }
    /// <summary>Gets the provider implementation or model-pack version.</summary>
    public Version Version { get; }
    /// <summary>Gets the installed language identifiers supported by this provider.</summary>
    public IReadOnlyList<string> Languages { get; }
    /// <summary>Gets the automatic-selection preference. Higher values win.</summary>
    public int AutomaticPriority { get; }

    /// <summary>Returns whether every requested language is installed for this provider.</summary>
    public bool SupportsLanguages(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        return languages.All(language => language is not null
            && _languages.Contains(language, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>Describes the caller's provider choice.</summary>
public sealed record PdfOcrProviderPreference
{
    /// <summary>Uses the highest-priority installed provider that supports the request.</summary>
    public static PdfOcrProviderPreference Automatic { get; } = new((string?)null);

    /// <summary>Creates an automatic or explicitly selected provider preference.</summary>
    public PdfOcrProviderPreference(string? providerId)
    {
        if (providerId is not null && (string.IsNullOrWhiteSpace(providerId)
            || providerId.Length > 80 || providerId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
            throw new ArgumentException(
                "OCR provider identifiers may contain only letters, numbers, hyphens, and underscores.",
                nameof(providerId));
        ProviderId = providerId;
    }

    /// <summary>Gets the explicit provider identifier, or null for automatic selection.</summary>
    public string? ProviderId { get; }
    /// <summary>Gets whether provider selection is automatic.</summary>
    public bool IsAutomatic => ProviderId is null;
}

/// <summary>Provides metadata and capability checks common to OCR provider shapes.</summary>
public interface IPdfOcrProviderMetadata
{
    /// <summary>Gets immutable provider identity and installed language capabilities.</summary>
    PdfOcrProviderDescriptor Descriptor { get; }

    /// <summary>Returns whether this provider supports all requested recognition options.</summary>
    bool Supports(PdfOcrOptions options);
}

/// <summary>Recognizes raw rendered page pixels through one local OCR provider.</summary>
public interface IPdfOcrRasterProvider : IPdfOcrProviderMetadata
{
    /// <summary>Recognizes one raw BGRA page without a temporary image file.</summary>
    PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height, int stride,
        PdfOcrOptions options, string? characterWhitelist = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Runs an OCR-capable ONNX session without exposing a runtime-specific session type.</summary>
public interface IPdfOnnxOcrSession : IDisposable
{
    /// <summary>Recognizes a raw BGRA image using the loaded ONNX model.</summary>
    PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height, int stride,
        PdfOcrOptions options, string? characterWhitelist = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapts a local ONNX OCR session factory to the engine's provider contract.</summary>
public sealed class PdfOnnxOcrProvider : IPdfOcrRasterProvider
{
    private readonly Func<IPdfOnnxOcrSession> _createSession;
    private readonly Func<PdfOcrOptions, bool> _supports;

    /// <summary>Creates an ONNX provider with isolated sessions for individual recognition calls.</summary>
    public PdfOnnxOcrProvider(PdfOcrProviderDescriptor descriptor,
        Func<IPdfOnnxOcrSession> createSession, Func<PdfOcrOptions, bool>? supports = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        if (!string.Equals(descriptor.Id, "onnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ONNX providers must use the stable 'onnx' identifier.",
                nameof(descriptor));
        _createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
        _supports = supports ?? (_ => true);
    }

    /// <inheritdoc />
    public PdfOcrProviderDescriptor Descriptor { get; }

    /// <inheritdoc />
    public bool Supports(PdfOcrOptions options) => _supports(options ?? throw new ArgumentNullException(nameof(options)));

    /// <inheritdoc />
    public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height, int stride,
        PdfOcrOptions options, string? characterWhitelist = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < checked(width * 4))
            throw new ArgumentOutOfRangeException(nameof(stride));
        if (bgra.Length < checked(stride * height))
            throw new ArgumentException("The BGRA buffer is shorter than the requested image.", nameof(bgra));
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        using IPdfOnnxOcrSession session = _createSession()
            ?? throw new InvalidOperationException("The ONNX OCR session factory returned null.");
        return session.RecognizeBgra(bgra, width, height, stride, options,
            characterWhitelist, cancellationToken);
    }
}

/// <summary>Resolves local OCR providers without exposing one provider's model details to another.</summary>
public static class PdfOcrProviderSelector
{
    /// <summary>Resolves an explicit provider or the best installed provider for one request.</summary>
    public static TProvider Select<TProvider>(IEnumerable<TProvider> providers,
        PdfOcrOptions options, PdfOcrProviderPreference? preference = null)
        where TProvider : IPdfOcrProviderMetadata
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        preference ??= PdfOcrProviderPreference.Automatic;
        TProvider[] supplied = [.. providers];
        if (supplied.Any(provider => provider is null))
            throw new ArgumentException("OCR providers cannot contain null entries.", nameof(providers));
        string? duplicate = supplied.Select(provider => provider.Descriptor.Id)
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new ArgumentException(
                $"OCR providers contain duplicate identifier '{duplicate}'.", nameof(providers));

        if (!preference.IsAutomatic)
        {
            TProvider? selected = supplied.SingleOrDefault(provider =>
                string.Equals(provider.Descriptor.Id, preference.ProviderId,
                    StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                throw new InvalidOperationException(
                    $"The OCR provider '{preference.ProviderId}' is not installed.");
            if (!Supports(selected, options))
                throw new InvalidOperationException(
                    $"The OCR provider '{selected.Descriptor.Id}' does not support the requested languages.");
            return selected;
        }

        TProvider? automatic = supplied.Where(provider => Supports(provider, options))
            .OrderByDescending(provider => provider.Descriptor.AutomaticPriority)
            .ThenBy(provider => provider.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return automatic ?? throw new InvalidOperationException(
            "No installed OCR provider supports the requested languages.");
    }

    private static bool Supports(IPdfOcrProviderMetadata provider, PdfOcrOptions options) =>
        provider.Descriptor.SupportsLanguages(options.Languages)
        && provider.Supports(options);
}
