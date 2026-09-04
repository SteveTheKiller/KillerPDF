using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Reports XFA constructs that require capabilities outside the safe engine subset.</summary>
public static class PdfXfaCompatibility
{
    private static readonly HashSet<string> SupportedControls = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "checkButton", "choiceList", "dateTimeEdit", "defaultUi",
        "imageEdit", "numericEdit", "passwordEdit", "signature", "textEdit"
    };

    /// <summary>Analyzes an XFA form without executing scripts or changing packet data.</summary>
    public static PdfXfaCompatibilityReport Analyze(PdfXfaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var findings = new List<PdfXfaCompatibilityFinding>();
        if (!info.IsPacketArray)
            findings.Add(new("combined-xdp", null,
                "Combined XDP streams cannot yet be edited or converted safely."));
        if (info.FormType == PdfXfaFormType.Dynamic && !HasSupportedDynamicLayout(info))
            findings.Add(new("dynamic-layout", null,
                "The dynamic XFA form does not use a supported flowed layout."));

        PdfXfaPacket? templatePacket = info.Packets.FirstOrDefault(packet =>
            packet.Name.Equals("template", StringComparison.OrdinalIgnoreCase));
        if (templatePacket is not null)
        {
            PdfXfaTemplateInfo template = PdfXfaTemplate.Read(info);
            foreach (PdfXfaTemplateField field in template.Fields.Where(field =>
                field.ControlType is not null && !SupportedControls.Contains(field.ControlType)))
                findings.Add(new("unsupported-control", field.Path,
                    $"The XFA control '{field.ControlType}' is not supported."));
            foreach (PdfXfaTemplateBehavior behavior in template.Behaviors.Where(behavior =>
                !string.IsNullOrWhiteSpace(behavior.Script)
                && !IsFormCalc(behavior.ScriptContentType)))
                findings.Add(new("unsafe-script-language", behavior.FieldPath,
                    "The XFA behavior uses an unsupported script language and will not be executed."));
        }
        return new PdfXfaCompatibilityReport(findings.Count == 0,
            Array.AsReadOnly(findings.ToArray()));
    }

    private static bool IsFormCalc(string? contentType) => contentType is not null
        && (contentType.Equals("application/x-formcalc", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/x-formcalc;", StringComparison.OrdinalIgnoreCase));

    private static bool HasSupportedDynamicLayout(PdfXfaInfo info)
    {
        PdfXfaPacket? packet = info.Packets.FirstOrDefault(item =>
            item.Name.Equals("template", StringComparison.OrdinalIgnoreCase));
        if (packet is null) return false;
        using var input = new MemoryStream(packet.Data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024,
            IgnoreComments = true
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        string[] layouts = [.. document.Descendants().Where(element =>
                element.Name.LocalName.Equals("subform", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("layout", StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)];
        bool flowed = layouts.Any(layout => layout.Equals("tb", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("lr-tb", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("rl-tb", StringComparison.OrdinalIgnoreCase));
        return flowed && layouts.All(layout => layout.Equals("position", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("row", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("tb", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("lr-tb", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("rl-tb", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A non-mutating XFA compatibility summary.</summary>
public sealed record PdfXfaCompatibilityReport(
    bool IsSupported, IReadOnlyList<PdfXfaCompatibilityFinding> Findings)
{
    /// <summary>Exports compatibility findings as stable machine-readable JSON.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        IsSupported,
        Findings
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });

    /// <summary>Formats XFA compatibility findings without exposing packet contents.</summary>
    public string ToText()
    {
        var output = new StringBuilder();
        output.Append("XFA compatibility: ").AppendLine(IsSupported ? "supported" : "unsupported content found");
        output.Append("Findings: ").AppendLine(Findings.Count.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        foreach (PdfXfaCompatibilityFinding finding in Findings)
        {
            output.Append("  ").Append(finding.Code);
            if (!string.IsNullOrWhiteSpace(finding.FieldPath))
                output.Append(" at ").Append(finding.FieldPath);
            output.AppendLine();
            output.Append("    ").AppendLine(finding.Message);
        }
        return output.ToString().TrimEnd();
    }
}

/// <summary>One unsupported or unsafe XFA construct.</summary>
public sealed record PdfXfaCompatibilityFinding(
    string Code, string? FieldPath, string Message);
