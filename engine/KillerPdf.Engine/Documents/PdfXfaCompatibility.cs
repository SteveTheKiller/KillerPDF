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
        if (info.FormType == PdfXfaFormType.Dynamic)
            findings.Add(new("dynamic-layout", null,
                "Dynamic XFA layout and pagination are not supported."));

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
}

/// <summary>A non-mutating XFA compatibility summary.</summary>
public sealed record PdfXfaCompatibilityReport(
    bool IsSupported, IReadOnlyList<PdfXfaCompatibilityFinding> Findings);

/// <summary>One unsupported or unsafe XFA construct.</summary>
public sealed record PdfXfaCompatibilityFinding(
    string Code, string? FieldPath, string Message);
