using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Diagnostics;

internal static class PdfConformanceInspection
{
    private static readonly XNamespace PdfA = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly XNamespace PdfUa = "http://www.aiim.org/pdfua/ns/id/";
    private static readonly XNamespace PdfX = "http://ns.adobe.com/pdfx/1.3/";

    internal static IReadOnlyList<PdfPreflightFinding> Check(PdfDocument document)
    {
        try
        {
            PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
            if (!catalog.TryGetValue(Name("Metadata"), out PdfObject? metadataValue)) return [];
            PdfStream metadata = Resolve(document, metadataValue) as PdfStream
                ?? throw new InvalidOperationException("The catalog metadata value is not a stream.");
            using var input = new MemoryStream(PdfStreamDecoder.Decode(
                metadata, document.Resolve, 64 * 1024 * 1024), writable: false);
            using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64 * 1024 * 1024
            });
            XDocument xmp = XDocument.Load(reader, LoadOptions.None);
            string? pdfAPart = Value(xmp, PdfA + "part");
            string? pdfUaPart = Value(xmp, PdfUa + "part");
            string? pdfXVersion = Value(xmp, PdfX + "GTS_PDFXVersion");
            var findings = new List<PdfPreflightFinding>();
            if (pdfAPart is not null)
            {
                if (pdfAPart != "4")
                    findings.Add(Unsupported("Conformance.UnsupportedPdfA",
                        $"PDF/A-{pdfAPart} validation is not implemented."));
                else
                {
                    if (document.Trailer.ContainsKey(Name("Encrypt")))
                        findings.Add(Error("Conformance.PdfAEncrypted",
                            "PDF/A-4 does not permit encryption."));
                    findings.AddRange(PdfPreflightDocumentChecks.CheckOutputIntent(document)
                        .Select(finding => finding with
                        {
                            Code = "Conformance.PdfA." + finding.Code
                        }));
                }
            }
            if (pdfUaPart is not null)
            {
                if (pdfUaPart != "2")
                    findings.Add(Unsupported("Conformance.UnsupportedPdfUa",
                        $"PDF/UA-{pdfUaPart} validation is not implemented."));
                else
                    findings.AddRange(PdfAccessibilityInspector.Inspect(document).Findings
                        .Select(finding => new PdfPreflightFinding(
                            "Conformance.PdfUa." + finding.Code,
                            finding.Severity, finding.Message,
                            finding.PageIndex, finding.ObjectNumber)));
            }
            if (pdfXVersion is not null)
            {
                if (document.Trailer.ContainsKey(Name("Encrypt")))
                    findings.Add(Error("Conformance.PdfXEncrypted",
                        "PDF/X does not permit encryption."));
                findings.AddRange(PdfPreflightDocumentChecks.CheckOutputIntent(document)
                    .Select(finding => finding with
                    {
                        Code = "Conformance.PdfX." + finding.Code
                    }));
                findings.Add(Unsupported("Conformance.PdfXValidationUnavailable",
                    $"The document declares {pdfXVersion}; PDF/X validation is not implemented."));
            }
            return Array.AsReadOnly(findings.ToArray());
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException
            or XmlException or OverflowException)
        {
            return [Error("Conformance.InvalidMetadata", error.Message)];
        }
    }

    private static string? Value(XDocument document, XName name) =>
        document.Descendants(name).Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0)
        ?? document.Descendants().Attributes(name)
            .Select(attribute => attribute.Value.Trim())
            .FirstOrDefault(value => value.Length > 0);

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A metadata reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfPreflightFinding Error(string code, string message) =>
        new(code, PdfDiagnosticSeverity.Error, message);

    private static PdfPreflightFinding Unsupported(string code, string message) =>
        new(code, PdfDiagnosticSeverity.Unsupported, message);

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}
