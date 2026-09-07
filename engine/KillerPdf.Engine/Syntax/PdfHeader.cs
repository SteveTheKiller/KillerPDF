using System.Text;

namespace KillerPdf.Engine.Syntax;

/// <summary>The version marker found near the beginning of a PDF file.</summary>
public readonly record struct PdfHeader(PdfVersion Version, int Offset)
{
    private static ReadOnlySpan<byte> Signature => "%PDF-"u8;
    /// <summary>Maximum number of leading bytes searched for a compatible PDF header.</summary>
    public const int SearchLimit = 1024;

    /// <summary>Parses a bounded PDF header and its source offset.</summary>
    public static PdfHeader Parse(ReadOnlySpan<byte> source)
    {
        int searchableLength = Math.Min(source.Length, SearchLimit);
        int offset = source[..searchableLength].IndexOf(Signature);
        if (offset < 0)
            throw new FormatException("A PDF header was not found in the first 1,024 bytes.");

        ReadOnlySpan<byte> version = source[(offset + Signature.Length)..];
        if (version.Length < 3 || version[1] != (byte)'.'
            || version[0] is < (byte)'0' or > (byte)'9'
            || version[2] is < (byte)'0' or > (byte)'9')
            throw new FormatException("The PDF header does not contain a valid major.minor version.");
        int major = version[0] - (byte)'0';
        int minor = version[2] - (byte)'0';
        if (major == 1 && minor is 8 or 9)
            return new PdfHeader(PdfVersion.Pdf20, offset);
        if (!PdfVersion.IsDefined(major, minor))
            throw new NotSupportedException($"PDF {major}.{minor} is not a defined PDF version.");

        return new PdfHeader(new PdfVersion(major, minor), offset);
    }

    /// <summary>
    /// Parses a header the way mainstream viewers do: a missing, malformed, or undefined
    /// version falls back to PDF 1.7 at offset zero, or at the "%PDF" marker when one exists.
    /// </summary>
    public static PdfHeader ParseWithCompatibilityRecovery(ReadOnlySpan<byte> source)
    {
        try
        {
            return Parse(source);
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException)
        {
            int searchableLength = Math.Min(source.Length, SearchLimit);
            int marker = source[..searchableLength].IndexOf("%PDF"u8);
            if (marker < 0) return new PdfHeader(PdfVersion.Pdf17, 0);
            ReadOnlySpan<byte> rest = source[(marker + 4)..];
            int index = 0;
            while (index < rest.Length && rest[index] is (byte)'-' or (byte)' ') index++;
            if (index < rest.Length && rest[index] is >= (byte)'0' and <= (byte)'9')
            {
                int major = rest[index] - '0';
                int minor = index + 2 < rest.Length && rest[index + 1] == (byte)'.'
                    && rest[index + 2] is >= (byte)'0' and <= (byte)'9' ? rest[index + 2] - '0' : 7;
                if (PdfVersion.IsDefined(major, minor)) return new PdfHeader(new PdfVersion(major, minor), marker);
                if (major == 1 && minor is 8 or 9) return new PdfHeader(PdfVersion.Pdf20, marker);
                if (major >= 2) return new PdfHeader(PdfVersion.Pdf20, marker);
            }
            return new PdfHeader(PdfVersion.Pdf17, marker);
        }
    }

    /// <summary>Creates canonical header bytes for a defined PDF version.</summary>
    public static byte[] Create(PdfVersion version)
    {
        if (!PdfVersion.IsDefined(version.Major, version.Minor))
            throw new ArgumentOutOfRangeException(nameof(version),
                "The PDF header version is not defined.");
        return Encoding.ASCII.GetBytes($"%PDF-{version}\n");
    }
}
