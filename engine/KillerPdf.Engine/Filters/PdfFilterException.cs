namespace KillerPdf.Engine.Filters;

/// <summary>An error while decoding a PDF stream filter or predictor.</summary>
public sealed class PdfFilterException : Exception
{
    internal bool IsSizeLimit { get; init; }
    /// <summary>Creates a filter exception with a descriptive message.</summary>
    public PdfFilterException(string message) : base(message) { }
    /// <summary>Creates a filter exception with a descriptive message and underlying cause.</summary>
    public PdfFilterException(string message, Exception innerException) : base(message, innerException) { }
}
