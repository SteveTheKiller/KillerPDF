using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;

namespace KillerPDF.Services;

/// <summary>
/// Builds the text of the document report windows in the app's language from the engine's structured results
/// (KillerPDF #506). The engine's own ToText output stays English for the command line, so the windows never
/// show it. Box names, PAdES levels, colorant names, profile names and finding codes are PDF terms and stay as
/// they are, as do messages the engine or Windows writes about one particular object or failure.
/// </summary>
internal static class LocalizedReports
{
    // Same lookup as LocaleManager.Loc, written out so the test project can compile this file on its own.
    private static string Default(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    // ---- Print production ----------------------------------------------------------------------------------------

    internal static string PrintProduction(PdfPrintProductionReport report,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_PP_Summary", report.Pages.Count, report.Colorants.Count, report.OutputIntents.Count);
        foreach (PdfPageBoxInformation page in report.Pages)
        {
            text.Line(1, "Str_Rep_PageHeading", page.PageIndex + 1);
            Box("MediaBox", page.MediaBox, "Str_Rep_PP_Effective");
            Box("CropBox", page.CropBox, page.HasExplicitCropBox ? "Str_Rep_PP_Explicit" : "Str_Rep_PP_Inherited");
            Box("BleedBox", page.BleedBox, page.HasExplicitBleedBox ? "Str_Rep_PP_Explicit" : "Str_Rep_PP_Inherited");
            Box("TrimBox", page.TrimBox, page.HasExplicitTrimBox ? "Str_Rep_PP_Explicit" : "Str_Rep_PP_Inherited");
            Box("ArtBox", page.ArtBox, page.HasExplicitArtBox ? "Str_Rep_PP_Explicit" : "Str_Rep_PP_Inherited");
        }
        foreach (PdfSeparationColorant colorant in report.Colorants)
            text.Line(1, colorant.IsProcess ? "Str_Rep_ProcessColorant" : "Str_Rep_SpotColorant",
                colorant.Name, Pages(colorant.PageIndexes));
        foreach (PdfOutputIntentInformation intent in report.OutputIntents)
            text.Line(1, "Str_Rep_PP_OutputIntent", OneLine(intent.OutputConditionIdentifier), intent.Subtype,
                intent.Profile.ColorSpace, intent.Profile.ComponentCount, intent.Profile.Data.Length);
        return text.ToString();

        void Box(string name, PdfPageBoxBounds box, string sourceKey) => text.Line(2, "Str_Rep_PP_Box", name,
            text.Number(box.Left), text.Number(box.Bottom), text.Number(box.Right), text.Number(box.Top),
            text.Number(box.Width), text.Number(box.Height), text.Loc(sourceKey));
    }

    // ---- Separation preview --------------------------------------------------------------------------------------

    internal static string SeparationPreview(PdfSeparationPreview preview,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_Sep_Summary", preview.Plates.Count, preview.Pages.Count);
        foreach (PdfSeparationPreviewPlate plate in preview.Plates)
            text.Line(1, plate.IsProcess ? "Str_Rep_ProcessPlate" : "Str_Rep_SpotPlate",
                plate.Name, Pages(plate.PageIndexes));
        foreach (PdfSeparationPreviewPage page in preview.Pages)
            text.Line(1, "Str_Rep_PageValue", page.PageIndex + 1, page.PlateNames.Count == 0
                ? text.Loc("Str_Rep_Sep_NoPlates") : string.Join(", ", page.PlateNames));
        return text.ToString();
    }

    // ---- Layers --------------------------------------------------------------------------------------------------

    internal static string Layers(PdfOptionalContentInfo layers,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_Layers_Count", layers.Groups.Count);
        foreach (PdfOptionalContentGroupInfo group in layers.Groups)
            text.Line(1, "Str_Rep_Layers_Group", group.Name, group.ObjectNumber, group.Generation,
                text.Loc(group.IsInitiallyVisible ? "Str_Rep_Visible" : "Str_Rep_Hidden"),
                text.Loc(group.IsLocked ? "Str_Rep_Locked" : "Str_Rep_Unlocked"),
                State(group.IsVisibleWhenPrinting), State(group.IsVisibleWhenExporting));
        text.Line(0, "Str_Rep_Layers_Configs", layers.Configurations.Count);
        foreach (PdfOptionalContentConfigurationInfo configuration in layers.Configurations)
        {
            text.Line(1, configuration.IsDefault ? "Str_Rep_Layers_Default" : "Str_Rep_Layers_Alternate",
                configuration.Name ?? text.Loc("Str_Rep_Unnamed"), text.Loc(configuration.BaseState switch
                {
                    PdfOptionalContentBaseState.Off => "Str_Rep_BaseState_Off",
                    PdfOptionalContentBaseState.Unchanged => "Str_Rep_BaseState_Unchanged",
                    _ => "Str_Rep_BaseState_On"
                }));
            if (!string.IsNullOrWhiteSpace(configuration.Creator))
                text.Line(2, "Str_Rep_Layers_Creator", configuration.Creator);
            text.Line(2, "Str_Rep_Layers_VisibleObjects", Objects(configuration.VisibleGroupObjectNumbers));
            text.Line(2, "Str_Rep_Layers_LockedObjects", Objects(configuration.LockedGroupObjectNumbers));
            text.Line(2, "Str_Rep_Layers_DisplayOrder", Objects(configuration.DisplayOrderGroupObjectNumbers));
        }
        return text.ToString();

        string State(bool? value) => text.Loc(value switch
        {
            true => "Str_Rep_Visible",
            false => "Str_Rep_Hidden",
            null => "Str_Rep_Unspecified"
        });

        string Objects(IEnumerable<int> values)
        {
            string result = string.Join(", ", values.Order().Select(value => text.Integer(value)));
            return result.Length == 0 ? text.Loc("Str_Rep_None") : result;
        }
    }

    // ---- Portfolio -----------------------------------------------------------------------------------------------

    internal static string Portfolio(PdfCollectionInfo? collection,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        if (collection is null)
        {
            text.Line(0, "Str_Rep_Portfolio_None");
            return text.ToString();
        }
        string view = text.Loc(collection.View switch
        {
            PdfCollectionView.Details => "Str_Rep_View_Details",
            PdfCollectionView.Tile => "Str_Rep_View_Tile",
            PdfCollectionView.Hidden => "Str_Rep_View_Hidden",
            _ => "Str_Rep_View_Unknown"
        });
        if (collection.View == PdfCollectionView.Unknown && collection.RawViewName is not null)
            view += " (" + collection.RawViewName + ")";
        text.Line(0, "Str_Rep_Portfolio_View", view);
        text.Line(0, "Str_Rep_Portfolio_Initial", collection.InitialDocument ?? text.Loc("Str_Rep_None"));
        text.Line(0, "Str_Rep_Portfolio_Fields", collection.Fields.Count);
        foreach (PdfCollectionFieldInfo field in collection.Fields)
        {
            string subtype = field.Subtype ?? text.Loc("Str_Rep_Unspecified");
            string visible = text.Loc(field.IsVisible ? "Str_Rep_Visible" : "Str_Rep_Hidden");
            string editable = text.Loc(field.IsEditable ? "Str_Rep_Editable" : "Str_Rep_ReadOnly");
            if (field.Order is int order)
                text.Line(1, "Str_Rep_Portfolio_FieldOrder", field.Key, field.DisplayName, subtype, visible, editable,
                    order);
            else
                text.Line(1, "Str_Rep_Portfolio_Field", field.Key, field.DisplayName, subtype, visible, editable);
        }
        text.Line(0, "Str_Rep_Portfolio_Sort", collection.Sort.Count);
        foreach (PdfCollectionSortInfo sort in collection.Sort)
            text.Line(1, "Str_Rep_KeyValue", sort.Key,
                text.Loc(sort.Ascending ? "Str_Rep_Ascending" : "Str_Rep_Descending"));
        text.Line(0, "Str_Rep_Portfolio_Folders", collection.Folders.Count);
        foreach (PdfCollectionFolderInfo folder in collection.Folders)
        {
            text.Line(folder.Depth + 1, "Str_Rep_Portfolio_Folder", folder.Name, folder.Id, folder.ObjectNumber);
            if (!string.IsNullOrWhiteSpace(folder.Description))
                text.Line(folder.Depth + 2, "Str_Rep_Portfolio_Description", folder.Description);
        }
        return text.ToString();
    }

    // ---- Signatures ----------------------------------------------------------------------------------------------

    internal static string Signatures(PdfSignatureInspectionReport report,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_Sig_Count", report.Entries.Count);
        foreach (PdfSignatureInspectionEntry entry in report.Entries)
        {
            PdfSignatureInfo signature = entry.Signature;
            PdfSignatureVerificationResult verification = entry.Verification;
            text.Raw(0, signature.FieldName);
            text.Line(1, "Str_Rep_Sig_Signed", YesNo(signature.IsSigned));
            // An empty signature field has nothing to verify; it is only waiting to be signed.
            if (!signature.IsSigned) continue;
            text.Line(1, "Str_Rep_Sig_DocumentTimestamp", YesNo(signature.IsDocumentTimestamp));
            text.Line(1, "Str_Rep_Sig_Integrity", ValidInvalid(verification.IsCryptographicallyValid));
            text.Line(1, "Str_Rep_Sig_Overall", Status(verification.ValidationStatus.ToString()));
            text.Line(1, "Str_Rep_Sig_Trust", Status(verification.CertificateTrustStatus.ToString()));
            text.Line(1, "Str_Rep_Sig_Revocation", Status(verification.RevocationStatus.ToString()));
            text.Line(1, "Str_Rep_Sig_Pades", entry.PadesProfile switch
            {
                PdfPadesProfile.BaselineB => "PAdES B-B",
                PdfPadesProfile.BaselineT => "PAdES B-T",
                PdfPadesProfile.BaselineLt => "PAdES B-LT",
                _ => text.Loc("Str_Rep_None")
            });
            text.Line(1, "Str_Rep_Sig_WholeDocument", YesNo(signature.CoversWholeDocument));
            Optional("Str_Rep_Sig_SignerName", signature.SignerName);
            if (signature.SigningTime is DateTimeOffset signingTime)
                text.Line(1, "Str_Rep_Sig_SigningTime", text.Date(signingTime));
            Optional("Str_Rep_Sig_Reason", signature.Reason);
            Optional("Str_Rep_Sig_Location", signature.Location);
            Optional("Str_Rep_Sig_Contact", signature.ContactInformation);
            if (verification.RequestedRevocationMode is X509RevocationMode revocationMode)
                text.Line(1, "Str_Rep_Sig_RequestedRevocation", text.Loc(revocationMode switch
                {
                    X509RevocationMode.Online => "Str_Rep_RevMode_Online",
                    X509RevocationMode.Offline => "Str_Rep_RevMode_Offline",
                    _ => "Str_Rep_RevMode_NoCheck"
                }));
            if (verification.RequestedVerificationTime is DateTime requested)
                text.Line(1, "Str_Rep_Sig_RequestedTime", text.Date(requested));
            if (verification.EvaluatedVerificationTime is DateTime evaluated)
                text.Line(1, "Str_Rep_Sig_EvaluatedTime", text.Date(evaluated));
            if (verification.SignatureTimestampStatus != PdfSignatureTimestampStatus.NotChecked)
            {
                text.Line(1, "Str_Rep_Sig_Timestamp", Status(verification.SignatureTimestampStatus.ToString()));
                text.Line(1, "Str_Rep_Sig_TsaRevocation", Status(verification.TimestampRevocationStatus.ToString()));
            }
            if (verification.TrustedTimestampTime is DateTimeOffset timestampTime)
                text.Line(1, "Str_Rep_Sig_TrustedTimestamp", text.Date(timestampTime));
            Optional("Str_Rep_Sig_TimestampEvidence", verification.TimestampError);
            Optional("Str_Rep_Sig_TsaSubject", verification.TimestampAuthoritySubject);
            Optional("Str_Rep_Sig_TsaSha256", verification.TimestampAuthorityCertificateSha256);
            if (verification.CertificateDownloadsDisabled is bool downloadsDisabled)
                text.Line(1, "Str_Rep_Sig_NetworkRetrieval",
                    text.Loc(downloadsDisabled ? "Str_Rep_Disabled" : "Str_Rep_Allowed"));
            Optional("Str_Rep_Sig_SignerSubject", verification.SignerSubject);
            Optional("Str_Rep_Sig_SignerIssuer", verification.SignerIssuer);
            Optional("Str_Rep_Sig_CertificateSha256", verification.SignerCertificateSha256);
            Optional("Str_Rep_Sig_DigestAlgorithm", verification.DigestAlgorithmOid);
            Optional("Str_Rep_Sig_SignatureAlgorithm", verification.SignatureAlgorithmOid);
            foreach (string policy in verification.SignaturePolicyOids)
                text.Line(1, "Str_Rep_Sig_Policy", policy);
            if (verification.CertificateNotBefore is DateTimeOffset notBefore)
                text.Line(1, "Str_Rep_Sig_ValidFrom", text.Date(notBefore));
            if (verification.CertificateNotAfter is DateTimeOffset notAfter)
                text.Line(1, "Str_Rep_Sig_ValidUntil", text.Date(notAfter));
            if (verification.IsCertificateTimeValid is bool timeValid)
                text.Line(1, "Str_Rep_Sig_CertificateTime",
                    verification.CertificateTimeStatus == PdfCertificateTimeStatus.NotChecked
                        ? ValidInvalid(timeValid) : Status(verification.CertificateTimeStatus.ToString()));
            foreach (string error in verification.CertificateChainErrors)
                text.Line(1, "Str_Rep_Sig_Chain", error);
            int index = 0;
            foreach (PdfCertificateChainElement certificate in verification.CertificateChain)
            {
                text.Line(1, "Str_Rep_Sig_ChainCertificate", ++index, certificate.Subject);
                text.Line(2, "Str_Rep_Sig_Issuer", certificate.Issuer);
                text.Line(2, "Str_Rep_Sig_Sha256", certificate.Sha256Fingerprint);
                foreach (string status in certificate.StatusMessages)
                    text.Line(2, "Str_Rep_Sig_Status", status);
            }
            if (entry.Revision is not null)
                text.Line(1, "Str_Rep_Sig_LaterChanges", YesNo(entry.Revision.HasLaterChanges));
            Optional("Str_Rep_Sig_Error", verification.Error);
        }
        return text.ToString();

        string YesNo(bool value) => text.Loc(value ? "Str_Btn_Yes" : "Str_Btn_No");
        string ValidInvalid(bool value) => text.Loc(value ? "Str_Panel_SigValid" : "Str_Panel_SigInvalid");
        // The status enums share their value names (Valid, Trusted, Indeterminate...), so one key covers each name.
        string Status(string name) => text.Loc("Str_Rep_Status_" + name);
        void Optional(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) text.Line(1, key, value);
        }
    }

    // ---- Structural comparison -----------------------------------------------------------------------------------

    internal static string StructuralComparison(PdfStructuralComparison comparison,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, comparison.HasChanges ? "Str_Rep_Cmp_ChangesFound" : "Str_Rep_Cmp_NoChanges");
        text.Line(0, "Str_Rep_Cmp_Count", comparison.Changes.Count);
        foreach (PdfStructuralChange change in comparison.Changes)
            text.Line(1, "Str_Rep_Cmp_Change", change.PageIndex + 1, text.Loc("Str_Rep_Change_" + change.Kind),
                change.OriginalCount, change.ChangedCount);
        return text.ToString();
    }

    // ---- Navigation audit ----------------------------------------------------------------------------------------

    internal static string NavigationAudit(IReadOnlyList<PdfNavigationFinding> findings,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_Nav_Count", findings.Count);
        foreach (PdfNavigationFinding finding in findings)
        {
            var where = new List<string>
            {
                finding.Kind switch
                {
                    "Link" or "Bookmark" or "Action" => text.Loc("Str_Rep_Nav_Kind_" + finding.Kind),
                    _ => finding.Kind
                }
            };
            if (finding.SourcePageIndex is int page) where.Add(text.Format("Str_Rep_Nav_Page", page + 1));
            if (finding.SourceObjectNumber is int objectNumber)
                where.Add(text.Format("Str_Rep_Nav_Object", objectNumber));
            if (!string.IsNullOrEmpty(finding.Source))
                where.Add(text.Format("Str_Rep_Nav_Source", OneLine(finding.Source)));
            text.Line(1, "Str_Rep_KeyValue", text.Loc("Str_Rep_Nav_" + finding.Code), string.Join(", ", where));
            text.Line(2, "Str_Rep_Nav_Repair", text.Loc(finding.SuggestedRepair == PdfNavigationRepairKind.Remove
                ? "Str_Rep_Nav_Repair_Remove" : "Str_Rep_Nav_Repair_ChooseDestination"));
        }
        return text.ToString();
    }

    // ---- Preflight -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The preflight report in the app's language. Each finding code has its own message; the engine's own words
    /// appear only where it reports a particular failure (an exception about one object) or for a code this
    /// version does not know yet, so nothing is hidden.
    /// </summary>
    internal static string Preflight(PdfPreflightReport report,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        string profile = report.ProfileName;
        if (BuiltInProfileKey(profile) is string profileKey) profile = text.Loc(profileKey);
        text.Line(0, "Str_Rep_Pre_Profile", profile);
        text.Line(0, "Str_Rep_Pre_Result", text.Loc(!report.Passed ? "Str_Rep_Pre_Failed"
            : report.Complete ? "Str_Rep_Pre_Passed" : "Str_Rep_Pre_Incomplete"));
        if (report.Findings.Count == 0)
        {
            text.Line(0, "Str_Rep_Pre_NoFindings");
            return text.ToString();
        }
        foreach (PdfPreflightFinding finding in report.Findings)
            Finding(text, finding.Severity, finding.PageIndex, finding.ObjectNumber,
                PreflightMessage(text, finding.Code, finding),
                finding.Code.StartsWith("Structural.", StringComparison.Ordinal) && finding.Arguments.Count == 1
                    ? text.Format("Str_Rep_Pre_Offset", finding.Arguments[0]) : null);
        return text.ToString();
    }

    private static string? BuiltInProfileKey(string name) =>
        name == PdfPreflightProfile.General.Name ? "Str_Rep_Pre_Profile_General"
        : name == PdfPreflightProfile.Attachments.Name ? "Str_Rep_Pre_Profile_Attachments"
        : name == PdfPreflightProfile.PrintProduction.Name ? "Str_Rep_Pre_Profile_PrintProduction"
        : null;

    /// <summary>Preflight codes whose message carries the engine's description of one failure as {0}.</summary>
    internal static readonly string[] PreflightDetailCodes =
    [
        "PageBoxes.InvalidPageTree", "PageBoxes.InvalidBox", "OutputIntent.Invalid", "ImageResolution.PageUnreadable",
        "FontEmbedding.Invalid", "Transparency.Invalid", "ColorUsage.Invalid", "ColorUsage.InvalidIccProfile",
        "OptionalContent.Invalid", "Metadata.Invalid", "Attachment.Invalid", "Measurement.InvalidAnnotations",
        "Measurement.Invalid", "Conformance.InvalidMetadata",
    ];

    /// <summary>The string key for a preflight code, or null when this version has no message for it.</summary>
    internal static string? PreflightKey(string code)
    {
        foreach (string prefix in (string[])["Accessibility.", "Conformance.PdfUa."])
            if (code.StartsWith(prefix, StringComparison.Ordinal)
                && Enum.TryParse(code[prefix.Length..], out PdfAccessibilityFindingCode accessibility)
                && Enum.IsDefined(accessibility))
                return "Str_Rep_A11y_" + accessibility;
        if (code.StartsWith("Conformance.PdfUa1.", StringComparison.Ordinal)) return "Str_Rep_Pre_Conformance_PdfUa1";
        if (code.StartsWith("Structural.", StringComparison.Ordinal))
            return Enum.TryParse(code["Structural.".Length..], out PdfDiagnosticCode structural)
                && Enum.IsDefined(structural) ? "Str_Rep_Pre_Structural_" + structural : null;
        return KnownPreflightCodes.Contains(code) ? "Str_Rep_Pre_" + code.Replace('.', '_') : null;
    }

    /// <summary>Every fixed preflight code the engine writes, outside the enum-backed families.</summary>
    internal static readonly HashSet<string> KnownPreflightCodes = new(StringComparer.Ordinal)
    {
        "DocumentChecksUnavailable",
        "PageBoxes.InvalidPageTree", "PageBoxes.CropOutsideMediaBox", "PageBoxes.BleedOutsideMediaBox",
        "PageBoxes.TrimOutsideMediaBox", "PageBoxes.ArtOutsideMediaBox", "PageBoxes.InvalidBox",
        "OutputIntent.Missing", "OutputIntent.Empty", "OutputIntent.MissingProfile", "OutputIntent.Invalid",
        "ImageResolution.PageUnreadable", "ImageResolution.Unknown", "ImageResolution.BelowMinimum",
        "FontEmbedding.Invalid", "FontEmbedding.NotEmbedded",
        "Transparency.Invalid", "Transparency.GraphicsState", "Transparency.ImageSoftMask", "Transparency.Group",
        "ColorUsage.DeviceRgb", "ColorUsage.InkCoverageAboveMaximum", "ColorUsage.Invalid", "ColorUsage.Overprint",
        "ColorUsage.SpotColor", "ColorUsage.InvalidIccProfile",
        "OptionalContent.LayerState", "OptionalContent.Invalid",
        "Metadata.MissingTitle", "Metadata.MissingAuthor", "Metadata.MissingSubject", "Metadata.MissingKeywords",
        "Metadata.Invalid",
        "Attachment.Invalid", "Attachment.UnsafeFileName", "Attachment.ExecutableFileName",
        "Attachment.ExecutableContent", "Attachment.EncryptedContent", "Attachment.SizeMismatch",
        "Attachment.ChecksumMismatch",
        "Measurement.InvalidAnnotations", "Measurement.Calibration", "Measurement.Invalid",
        "Conformance.UnsupportedPdfA", "Conformance.PdfAEncrypted", "Conformance.UnsupportedPdfUa",
        "Conformance.PdfXEncrypted", "Conformance.PdfXValidationUnavailable", "Conformance.InvalidMetadata",
    };

    /// <summary>Preflight codes whose arguments are numbers, by argument position, formatted in the app culture.</summary>
    private static readonly Dictionary<string, int[]> NumericArguments = new(StringComparer.Ordinal)
    {
        ["ImageResolution.BelowMinimum"] = [0, 1, 2],
        ["ColorUsage.InkCoverageAboveMaximum"] = [0, 1],
        ["Measurement.Calibration"] = [0, 2],
    };

    private static string PreflightMessage(Text text, string code, PdfPreflightFinding finding)
    {
        // PDF/A-4 and PDF/X repeat the output intent checks under their own prefix; the prefix is a PDF term.
        foreach ((string prefix, string standard) in (ReadOnlySpan<(string, string)>)
                 [("Conformance.PdfA.", "PDF/A-4"), ("Conformance.PdfX.", "PDF/X")])
            if (code.StartsWith(prefix, StringComparison.Ordinal))
                return text.Format("Str_Rep_KeyValue", standard, PreflightMessage(text, code[prefix.Length..], finding));
        string? key = PreflightKey(code);
        if (key is null) return text.Format("Str_Rep_KeyValue", code, OneLine(finding.Message));
        bool detail = key == "Str_Rep_A11y_InvalidStructureTree" || PreflightDetailCodes.Contains(code);
        if (detail) return text.Format(key, OneLine(finding.Message));
        // A structural finding's only argument is its byte offset, shown with the location instead.
        if (code.StartsWith("Structural.", StringComparison.Ordinal)) return text.Loc(key);
        object[] values = finding.Arguments.Select((value, index) => (object)Argument(code, index, value)).ToArray();
        return values.Length == 0 ? text.Loc(key) : text.Format(key, values);

        string Argument(string argumentCode, int index, string value)
        {
            if (NumericArguments.TryGetValue(argumentCode, out int[]? numeric) && numeric.Contains(index)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return text.Number(number);
            if (argumentCode == "OptionalContent.LayerState")
                return index switch
                {
                    1 => text.Loc(value == "true" ? "Str_Rep_Visible" : "Str_Rep_Hidden"),
                    2 => text.Loc(value == "true" ? "Str_Rep_Locked" : "Str_Rep_Unlocked"),
                    3 or 4 => text.Loc(value switch
                    {
                        "on" => "Str_Rep_Visible",
                        "off" => "Str_Rep_Hidden",
                        _ => "Str_Rep_Unspecified"
                    }),
                    _ => OneLine(value)
                };
            return OneLine(value);
        }
    }

    /// <summary>How many {n} placeholders each preflight code's message takes, from its engine arguments.</summary>
    internal static int PreflightArgumentCount(string code) => code switch
    {
        "ImageResolution.BelowMinimum" or "Measurement.Calibration" => 3,
        "ColorUsage.InkCoverageAboveMaximum" => 2,
        "OptionalContent.LayerState" => 5,
        "FontEmbedding.NotEmbedded" or "Conformance.UnsupportedPdfA" or "Conformance.UnsupportedPdfUa"
            or "Conformance.PdfXValidationUnavailable" => 1,
        _ when code.StartsWith("Attachment.", StringComparison.Ordinal) && code != "Attachment.Invalid" => 1,
        _ when code.StartsWith("Conformance.PdfUa1.", StringComparison.Ordinal) => 2,
        _ when PreflightDetailCodes.Contains(code) || code.EndsWith(".InvalidStructureTree", StringComparison.Ordinal) => 1,
        _ => 0
    };

    // ---- Accessibility -------------------------------------------------------------------------------------------

    internal static string Accessibility(PdfAccessibilityReport report,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_A11y_Result",
            text.Loc(report.PassesImplementedChecks ? "Str_Rep_Pre_Passed" : "Str_Rep_Pre_Failed"));
        if (report.Findings.Count == 0)
        {
            text.Line(0, "Str_Rep_Pre_NoFindings");
            return text.ToString();
        }
        foreach (PdfAccessibilityFinding finding in report.Findings)
        {
            string message = finding.Code == PdfAccessibilityFindingCode.InvalidStructureTree
                ? text.Format("Str_Rep_A11y_InvalidStructureTree", OneLine(finding.Message))
                : text.Loc("Str_Rep_A11y_" + finding.Code);
            // The field name is the document's own text.
            if (!string.IsNullOrWhiteSpace(finding.FieldName)) message += " (" + OneLine(finding.FieldName) + ")";
            Finding(text, finding.Severity, finding.PageIndex, finding.ObjectNumber, message);
        }
        return text.ToString();
    }

    private static void Finding(Text text, PdfDiagnosticSeverity severity, int? pageIndex, int? objectNumber,
        string message, string? offset = null)
    {
        var line = new StringBuilder().Append('[').Append(text.Loc("Str_Rep_Severity_" + severity)).Append("] ");
        var where = new List<string>();
        if (pageIndex is int page) where.Add(text.Format("Str_Rep_Pre_Page", page + 1));
        if (objectNumber is int number) where.Add(text.Format("Str_Rep_Pre_Object", number));
        if (offset is not null) where.Add(offset);
        if (where.Count > 0) line.Append(string.Join(" | ", where)).Append(": ");
        text.Raw(0, line.Append(message).ToString());
    }

    // ---- Data merge ----------------------------------------------------------------------------------------------

    internal static string DataMerge(PdfDataMergeBatchReport report,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_Merge_Summary", report.TotalRecords, report.SucceededRecords, report.SkippedRecords,
            report.FailedRecords);
        foreach (PdfDataMergeBatchReportItem result in report.Results)
        {
            string outcome = text.Loc(result.Succeeded ? "Str_Rep_Outcome_Succeeded"
                : result.Skipped ? "Str_Rep_Outcome_Skipped" : "Str_Rep_Outcome_Failed");
            if (!string.IsNullOrWhiteSpace(result.OutputFileName))
                text.Line(1, "Str_Rep_RecordOutput", result.RecordIndex + 1, outcome, OneLine(result.OutputFileName));
            else
                text.Line(1, "Str_Rep_Record", result.RecordIndex + 1, outcome);
            // The error is the engine's account of what went wrong with this one record.
            if (!string.IsNullOrWhiteSpace(result.Error)) text.Line(2, "Str_Rep_Error", OneLine(result.Error));
        }
        return text.ToString();
    }

    // ---- Macro run -----------------------------------------------------------------------------------------------

    /// <param name="operationName">The app's name for a macro step, as the step editor shows it.</param>
    internal static string MacroRun(PdfMacroRunReport report, Func<PdfMacroOperation, string> operationName,
        Func<string, string>? loc = null, IFormatProvider? culture = null)
    {
        var text = new Text(loc, culture);
        text.Line(0, "Str_Rep_Macro_Summary", report.SucceededCount, report.FailedCount, report.CanceledCount,
            report.UnprocessedCount);
        foreach (PdfMacroFileResult result in report.Results)
        {
            string outcome = result.Succeeded
                ? result.OutputFileName is null ? text.Loc("Str_Rep_Outcome_Succeeded")
                    : text.Format("Str_Rep_Macro_SucceededOutput", OneLine(result.OutputFileName))
                : result.WasCanceled ? text.Loc("Str_Rep_Outcome_Canceled")
                : Failure(result.FailedOperation, result.Error);
            text.Line(0, "Str_Rep_Macro_Input", result.InputIndex + 1, outcome);
            if (result.Outputs is null) continue;
            foreach (PdfMacroOutput generated in result.Outputs)
            {
                string name = generated.OutputFileName is null ? text.Loc("Str_Rep_Unnamed")
                    : OneLine(generated.OutputFileName);
                string state = generated.Succeeded ? text.Loc("Str_Rep_Outcome_Succeeded")
                    : Failure(generated.FailedOperation, generated.Error);
                string line = text.Format("Str_Rep_KeyValue", name, state);
                if (generated.RecordIndex is int record) text.Line(1, "Str_Rep_Record", record + 1, line);
                else text.Raw(1, line);
            }
        }
        return text.ToString();

        string Failure(PdfMacroOperation? operation, string? error)
        {
            string failed = operation is PdfMacroOperation step
                ? text.Format("Str_Rep_Macro_FailedAt", operationName(step)) : text.Loc("Str_Rep_Outcome_Failed");
            // The error is the engine's account of this one input's failure.
            return string.IsNullOrWhiteSpace(error) ? failed : text.Format("Str_Rep_KeyValue", failed, OneLine(error));
        }
    }

    // ---- Shared --------------------------------------------------------------------------------------------------

    private static string Pages(IEnumerable<int> pageIndexes) => string.Join(", ",
        pageIndexes.Select(index => (index + 1).ToString(CultureInfo.InvariantCulture)));

    private static string OneLine(string value) => value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private sealed class Text(Func<string, string>? loc, IFormatProvider? culture)
    {
        private readonly Func<string, string> _loc = loc ?? Default;
        private readonly IFormatProvider _culture = culture ?? CultureInfo.CurrentCulture;
        private readonly StringBuilder _output = new();

        internal string Loc(string key) => _loc(key);

        internal string Format(string key, params object[] values) => string.Format(_culture, _loc(key), values);

        internal void Line(int indent, string key, params object[] values) => Raw(indent, Format(key, values));

        internal void Raw(int indent, string line) => _output.Append(' ', indent * 2).AppendLine(line);

        internal string Integer(int value) => value.ToString(_culture);

        // Coordinates come out of PDF arithmetic as 595.27599999999995; two decimals, trimmed, is what a person reads.
        internal string Number(double value)
        {
            double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            if (rounded == 0) rounded = 0; // no "-0"
            return rounded.ToString("0.##", _culture);
        }

        internal string Date(DateTimeOffset value) => value.ToString("G", _culture);

        internal string Date(DateTime value) => value.ToString("G", _culture);

        public override string ToString() => _output.ToString().TrimEnd();
    }
}
