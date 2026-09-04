using System.Text.RegularExpressions;
using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>The supported search expression for a redaction preview.</summary>
public enum PdfRedactionSearchKind
{
    /// <summary>Literal text or phrase.</summary>
    ExactText,
    /// <summary>A caller-supplied regular expression.</summary>
    RegularExpression,
    /// <summary>A common email-address pattern.</summary>
    EmailAddress,
    /// <summary>A common North American phone-number pattern.</summary>
    PhoneNumber,
    /// <summary>A U.S. Social Security number written with standard separators.</summary>
    SocialSecurityNumber,
    /// <summary>A payment card account number with a valid checksum.</summary>
    PaymentCardNumber,
    /// <summary>An international bank account number with a valid checksum.</summary>
    InternationalBankAccountNumber
}

/// <summary>Options for locating reviewable redaction candidates.</summary>
public sealed record PdfRedactionSearchOptions
{
    /// <summary>Gets the search kind.</summary>
    public PdfRedactionSearchKind Kind { get; init; }
    /// <summary>Gets literal text or a regular expression. Common-pattern searches ignore this value.</summary>
    public string Query { get; init; } = string.Empty;
    /// <summary>Gets whether matching distinguishes letter case.</summary>
    public bool MatchCase { get; init; }
    /// <summary>Gets the bounded regular-expression evaluation time.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Gets the optional reason code carried by every proposed match.</summary>
    public string? ReasonCode { get; init; }
    /// <summary>Gets the optional replacement text carried by every proposed match.</summary>
    public string? OverlayText { get; init; }
}

/// <summary>One text match proposed for redaction.</summary>
public sealed record PdfRedactionMatch(string Id, int PageIndex, string Text,
    PdfContentBounds Bounds, int FirstWordIndex, int WordCount,
    string? ReasonCode = null, string? OverlayText = null);

/// <summary>An immutable preview whose matches must be explicitly excluded when retained.</summary>
public sealed class PdfRedactionReview
{
    private readonly PdfRedactionMatch[] _matches;
    private readonly HashSet<string> _excluded;

    internal PdfRedactionReview(IEnumerable<PdfRedactionMatch> matches, IEnumerable<string>? excluded = null)
    {
        _matches = matches.ToArray();
        _excluded = new HashSet<string>(excluded ?? [], StringComparer.Ordinal);
        Matches = Array.AsReadOnly(_matches);
        Included = Array.AsReadOnly(_matches.Where(match => !_excluded.Contains(match.Id)).ToArray());
    }

    /// <summary>Gets every proposed match.</summary>
    public IReadOnlyList<PdfRedactionMatch> Matches { get; }
    /// <summary>Gets matches still selected for later permanent removal.</summary>
    public IReadOnlyList<PdfRedactionMatch> Included { get; }

    /// <summary>Returns a new review with one match excluded.</summary>
    public PdfRedactionReview Exclude(string id)
    {
        if (!_matches.Any(match => string.Equals(match.Id, id, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"Redaction match '{id}' was not found.");
        return new PdfRedactionReview(_matches, _excluded.Append(id));
    }

    /// <summary>Returns a new review with an excluded match restored.</summary>
    public PdfRedactionReview Include(string id)
    {
        if (!_matches.Any(match => string.Equals(match.Id, id, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"Redaction match '{id}' was not found.");
        return new PdfRedactionReview(_matches, _excluded.Where(value => !string.Equals(value, id, StringComparison.Ordinal)));
    }

    /// <summary>Returns a new review with every match excluded.</summary>
    public PdfRedactionReview ExcludeAll() =>
        new(_matches, _matches.Select(match => match.Id));

    /// <summary>Returns a new review with every match included.</summary>
    public PdfRedactionReview IncludeAll() => new(_matches);

    /// <summary>Exports a stable review report without matched text unless explicitly requested.</summary>
    public string ToJson(bool includeMatchedText = false, bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        return JsonSerializer.Serialize(new
        {
            Version = 1,
            MatchCount = _matches.Length,
            IncludedCount = Included.Count,
            Matches = _matches.Select(match => new
            {
                match.Id,
                match.PageIndex,
                Text = includeMatchedText ? match.Text : null,
                match.Bounds,
                match.FirstWordIndex,
                match.WordCount,
                match.ReasonCode,
                match.OverlayText,
                Included = !_excluded.Contains(match.Id)
            })
        }, options);
    }
}

/// <summary>Finds text redaction candidates without modifying the source document.</summary>
public static class PdfRedactionSearch
{
    private const string EmailPattern = @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b";
    private const string PhonePattern = @"(?<!\d)(?:\+?1[ .-]?)?(?:\(\d{3}\)|\d{3})[ .-]?\d{3}[ .-]?\d{4}(?!\d)";
    private const string SocialSecurityNumberPattern = @"(?<!\d)\d{3}[ -]\d{2}[ -]\d{4}(?!\d)";
    private const string PaymentCardNumberPattern = @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)";
    private const string InternationalBankAccountNumberPattern =
        @"(?<![A-Z0-9])(?:[A-Z]{2}\d{2}[A-Z0-9]{11,30}|[A-Z]{2}\d{2}(?: [A-Z0-9]{4}){2,7}(?: [A-Z0-9]{1,4})?)(?![A-Z0-9])";

    /// <summary>Builds a reviewable match list from extracted pages.</summary>
    public static PdfRedactionReview Find(IEnumerable<PdfPageContent> pages,
        PdfRedactionSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Kind)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(options), "The search timeout must be from zero through ten seconds.");
        if (options.ReasonCode is not null && string.IsNullOrWhiteSpace(options.ReasonCode))
            throw new ArgumentException("A redaction reason code cannot be blank.", nameof(options));
        if (options.OverlayText is not null && string.IsNullOrWhiteSpace(options.OverlayText))
            throw new ArgumentException("Redaction overlay text cannot be blank.", nameof(options));
        string pattern = options.Kind switch
        {
            PdfRedactionSearchKind.ExactText when !string.IsNullOrEmpty(options.Query) => Regex.Escape(options.Query),
            PdfRedactionSearchKind.RegularExpression when !string.IsNullOrEmpty(options.Query) => options.Query,
            PdfRedactionSearchKind.EmailAddress => EmailPattern,
            PdfRedactionSearchKind.PhoneNumber => PhonePattern,
            PdfRedactionSearchKind.SocialSecurityNumber => SocialSecurityNumberPattern,
            PdfRedactionSearchKind.PaymentCardNumber => PaymentCardNumberPattern,
            PdfRedactionSearchKind.InternationalBankAccountNumber =>
                InternationalBankAccountNumberPattern,
            _ => throw new ArgumentException("Search text is required.", nameof(options))
        };
        RegexOptions flags = RegexOptions.CultureInvariant;
        if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;
        var expression = new Regex(pattern, flags, options.Timeout);
        var results = new List<PdfRedactionMatch>();
        int pageIndex = 0;
        foreach (PdfPageContent page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spans = new List<(PdfExtractedWord Word, int Start, int End)>();
            string text = string.Join(' ', page.Words.Select(word => word.Text));
            int offset = 0;
            foreach (PdfExtractedWord word in page.Words)
            {
                spans.Add((word, offset, offset + word.Text.Length));
                offset += word.Text.Length + 1;
            }
            foreach (Match match in expression.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (options.Kind == PdfRedactionSearchKind.PaymentCardNumber
                    && !HasValidPaymentCardChecksum(match.Value)) continue;
                if (options.Kind == PdfRedactionSearchKind.SocialSecurityNumber
                    && !HasValidSocialSecurityNumber(match.Value)) continue;
                if (options.Kind == PdfRedactionSearchKind.InternationalBankAccountNumber
                    && !HasValidInternationalBankAccountNumber(match.Value)) continue;
                var words = spans.Select((span, index) => (span, index))
                    .Where(item => item.span.End > match.Index && item.span.Start < match.Index + match.Length)
                    .ToArray();
                if (words.Length == 0) continue;
                results.Add(new PdfRedactionMatch($"{pageIndex}:{match.Index}:{match.Length}", pageIndex,
                    match.Value, PdfContentBounds.Union(words.Select(item => item.span.Word.BoundingBox)),
                    words[0].index, words.Length, options.ReasonCode, options.OverlayText));
            }
            pageIndex++;
        }
        return new PdfRedactionReview(results);
    }

    private static bool HasValidPaymentCardChecksum(string value)
    {
        int digitCount = value.Count(char.IsAsciiDigit);
        if (digitCount is < 13 or > 19) return false;
        int sum = 0;
        bool doubleDigit = false;
        for (int index = value.Length - 1; index >= 0; index--)
        {
            if (!char.IsAsciiDigit(value[index])) continue;
            int digit = value[index] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    private static bool HasValidSocialSecurityNumber(string value)
    {
        string digits = new(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 9) return false;
        int area = int.Parse(digits.AsSpan(0, 3), System.Globalization.CultureInfo.InvariantCulture);
        int group = int.Parse(digits.AsSpan(3, 2), System.Globalization.CultureInfo.InvariantCulture);
        int serial = int.Parse(digits.AsSpan(5, 4), System.Globalization.CultureInfo.InvariantCulture);
        return area is > 0 and < 900 and not 666 && group > 0 && serial > 0;
    }

    private static bool HasValidInternationalBankAccountNumber(string value)
    {
        string compact = new(value.Where(character => character != ' ')
            .Select(char.ToUpperInvariant).ToArray());
        if (compact.Length is < 15 or > 34
            || !char.IsAsciiLetter(compact[0]) || !char.IsAsciiLetter(compact[1])
            || !char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3])
            || compact.Any(character => !char.IsAsciiLetterOrDigit(character))) return false;
        int remainder = 0;
        foreach (char character in compact[4..].Concat(compact[..4]))
        {
            if (char.IsAsciiDigit(character))
                remainder = (remainder * 10 + character - '0') % 97;
            else
                remainder = (remainder * 100 + character - 'A' + 10) % 97;
        }
        return remainder == 1;
    }
}
