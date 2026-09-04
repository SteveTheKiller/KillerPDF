using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Diagnostics;

internal static class PdfPreflightDocumentChecks
{
    private static readonly PdfName MediaBoxName = Name("MediaBox");
    private static readonly PdfName CropBoxName = Name("CropBox");
    private static readonly PdfName BleedBoxName = Name("BleedBox");
    private static readonly PdfName TrimBoxName = Name("TrimBox");
    private static readonly PdfName ArtBoxName = Name("ArtBox");
    private static readonly PdfName OutputIntentsName = Name("OutputIntents");
    private static readonly PdfName ResourcesName = Name("Resources");
    private static readonly PdfName FontName = Name("Font");
    private static readonly PdfName XObjectName = Name("XObject");
    private static readonly PdfName ExtGStateName = Name("ExtGState");
    private static readonly PdfName ColorSpaceName = Name("ColorSpace");

    internal static IReadOnlyList<PdfPreflightFinding> CheckPageBoxes(PdfDocument document)
    {
        var findings = new List<PdfPreflightFinding>();
        PdfPageTree tree;
        try
        {
            tree = PdfPageTree.Read(document);
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            return [Error("PageBoxes.InvalidPageTree", error.Message)];
        }
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            try
            {
                PdfBox media = Box(document, page, MediaBoxName, required: true)
                    ?? throw new InvalidOperationException("A page has no media box.");
                PdfBox crop = Box(document, page, CropBoxName, required: false) ?? media;
                if (crop.Left < media.Left || crop.Bottom < media.Bottom
                    || crop.Right > media.Right || crop.Top > media.Top)
                    findings.Add(Error("PageBoxes.CropOutsideMediaBox",
                        "The crop box extends outside the media box.", page.Index,
                        page.Reference.ObjectNumber));
                CheckContained(BleedBoxName, "Bleed");
                CheckContained(TrimBoxName, "Trim");
                CheckContained(ArtBoxName, "Art");

                void CheckContained(PdfName name, string label)
                {
                    PdfBox? box = Box(document, page, name, required: false);
                    if (box is null || box.Left >= media.Left && box.Bottom >= media.Bottom
                        && box.Right <= media.Right && box.Top <= media.Top) return;
                    findings.Add(Error($"PageBoxes.{label}OutsideMediaBox",
                        $"The {label.ToLowerInvariant()} box extends outside the media box.",
                        page.Index, page.Reference.ObjectNumber));
                }
            }
            catch (Exception error) when (IsDocumentFailure(error))
            {
                findings.Add(Error("PageBoxes.InvalidBox", error.Message,
                    page.Index, page.Reference.ObjectNumber));
            }
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckOutputIntent(PdfDocument document)
    {
        try
        {
            PdfPageTree tree = PdfPageTree.Read(document);
            if (!tree.Catalog.TryGetValue(OutputIntentsName, out PdfObject? value))
                return [Error("OutputIntent.Missing", "The document has no output intent.")];
            PdfArray intents = Resolve(document, value) as PdfArray
                ?? throw new InvalidOperationException("The catalog output intents value is not an array.");
            if (intents.Count == 0)
                return [Error("OutputIntent.Empty", "The document has no output intent.")];
            var findings = new List<PdfPreflightFinding>();
            foreach (PdfObject item in intents)
            {
                PdfIndirectReference? reference = item as PdfIndirectReference;
                PdfDictionary intent = Resolve(document, item) as PdfDictionary
                    ?? throw new InvalidOperationException("An output intent is not a dictionary.");
                if (!intent.TryGetValue(Name("DestOutputProfile"), out PdfObject? profileValue)
                    || Resolve(document, profileValue) is not PdfStream profile
                    || !profile.Dictionary.ContainsKey(Name("N")))
                    findings.Add(Error("OutputIntent.MissingProfile",
                        "An output intent has no usable destination ICC profile.",
                        objectNumber: reference?.ObjectNumber));
            }
            if (findings.Count > 0) return Array.AsReadOnly(findings.ToArray());
            _ = PdfOutputIntentInspection.Inspect(document);
            return [];
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            return [Error("OutputIntent.Invalid", error.Message)];
        }
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckImageResolution(
        PdfDocument document, double minimumDpi)
    {
        var findings = new List<PdfPreflightFinding>();
        IReadOnlyList<PdfPageContentBatchResult> pages = PdfPageContentBatch.Read(document);
        foreach (PdfPageContentBatchResult page in pages)
        {
            if (!page.Succeeded)
            {
                findings.Add(Error("ImageResolution.PageUnreadable",
                    page.Error ?? "The page content could not be read.", page.PageIndex));
                continue;
            }
            foreach (PdfExtractedImage image in page.Content!.Images)
            {
                if (image.HorizontalDpi is not double horizontal
                    || image.VerticalDpi is not double vertical)
                {
                    findings.Add(Warning("ImageResolution.Unknown",
                        "An image's effective resolution could not be determined.", page.PageIndex));
                    continue;
                }
                if (horizontal < minimumDpi || vertical < minimumDpi)
                    findings.Add(Warning("ImageResolution.BelowMinimum",
                        $"An image is {horizontal:0.#} by {vertical:0.#} DPI; the minimum is {minimumDpi:0.#} DPI.",
                        page.PageIndex));
            }
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckFontEmbedding(PdfDocument document)
    {
        var findings = new List<PdfPreflightFinding>();
        var visitedFonts = new HashSet<(int, int)>();
        var visitedForms = new HashSet<(int, int)>();
        try
        {
            foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
                if (page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? resources))
                    InspectResources(resources, page.Index);
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            findings.Add(Error("FontEmbedding.Invalid", error.Message));
        }
        return Array.AsReadOnly(findings.ToArray());

        void InspectResources(PdfObject value, int pageIndex)
        {
            PdfDictionary resources = Resolve(document, value) as PdfDictionary
                ?? throw new InvalidOperationException("A page or form resource value is not a dictionary.");
            if (resources.TryGetValue(FontName, out PdfObject? fontsValue))
            {
                PdfDictionary fonts = Resolve(document, fontsValue) as PdfDictionary
                    ?? throw new InvalidOperationException("A font resource value is not a dictionary.");
                foreach (KeyValuePair<PdfName, PdfObject> item in fonts)
                {
                    PdfIndirectReference? reference = item.Value as PdfIndirectReference;
                    if (reference is not null
                        && !visitedFonts.Add((reference.ObjectNumber, reference.Generation)))
                        continue;
                    PdfDictionary font = Resolve(document, item.Value) as PdfDictionary
                        ?? throw new InvalidOperationException("A font resource is not a dictionary.");
                    if (!IsEmbeddedFont(font))
                    {
                        string name = FontDisplayName(font, item.Key.ValueAsLatin1());
                        findings.Add(Warning("FontEmbedding.NotEmbedded",
                            $"Font {name} has no embedded font program.", pageIndex,
                            reference?.ObjectNumber));
                    }
                }
            }
            if (!resources.TryGetValue(XObjectName, out PdfObject? xObjectsValue)) return;
            PdfDictionary xObjects = Resolve(document, xObjectsValue) as PdfDictionary
                ?? throw new InvalidOperationException("An XObject resource value is not a dictionary.");
            foreach (KeyValuePair<PdfName, PdfObject> item in xObjects)
            {
                PdfIndirectReference? reference = item.Value as PdfIndirectReference;
                if (reference is not null
                    && !visitedForms.Add((reference.ObjectNumber, reference.Generation)))
                    continue;
                if (Resolve(document, item.Value) is not PdfStream stream
                    || !IsName(stream.Dictionary, "Subtype", "Form")) continue;
                if (stream.Dictionary.TryGetValue(ResourcesName, out PdfObject? formResources))
                    InspectResources(formResources, pageIndex);
            }
        }

        bool IsEmbeddedFont(PdfDictionary font)
        {
            if (IsName(font, "Subtype", "Type3")) return true;
            if (IsName(font, "Subtype", "Type0"))
            {
                if (!font.TryGetValue(Name("DescendantFonts"), out PdfObject? descendantsValue)
                    || Resolve(document, descendantsValue) is not PdfArray descendants
                    || descendants.Count == 0) return false;
                return descendants.All(item => Resolve(document, item) is PdfDictionary descendant
                    && HasEmbeddedProgram(descendant));
            }
            return HasEmbeddedProgram(font);
        }

        bool HasEmbeddedProgram(PdfDictionary font)
        {
            if (!font.TryGetValue(Name("FontDescriptor"), out PdfObject? descriptorValue)
                || Resolve(document, descriptorValue) is not PdfDictionary descriptor) return false;
            return descriptor.ContainsKey(Name("FontFile"))
                || descriptor.ContainsKey(Name("FontFile2"))
                || descriptor.ContainsKey(Name("FontFile3"));
        }

        string FontDisplayName(PdfDictionary font, string fallback) =>
            font.TryGetValue(Name("BaseFont"), out PdfObject? value)
                && Resolve(document, value) is PdfName name ? name.ValueAsLatin1() : fallback;
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckTransparency(PdfDocument document)
    {
        var findings = new List<PdfPreflightFinding>();
        var visitedStates = new HashSet<(int, int)>();
        var visitedObjects = new HashSet<(int, int)>();
        try
        {
            foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
                if (page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? resources))
                    InspectResources(resources, page.Index);
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            findings.Add(Error("Transparency.Invalid", error.Message));
        }
        return Array.AsReadOnly(findings.ToArray());

        void InspectResources(PdfObject value, int pageIndex)
        {
            PdfDictionary resources = Resolve(document, value) as PdfDictionary
                ?? throw new InvalidOperationException("A page or form resource value is not a dictionary.");
            if (resources.TryGetValue(ExtGStateName, out PdfObject? statesValue))
            {
                PdfDictionary states = Resolve(document, statesValue) as PdfDictionary
                    ?? throw new InvalidOperationException("An extended graphics-state resource is not a dictionary.");
                foreach (KeyValuePair<PdfName, PdfObject> item in states)
                {
                    PdfIndirectReference? reference = item.Value as PdfIndirectReference;
                    if (reference is not null
                        && !visitedStates.Add((reference.ObjectNumber, reference.Generation)))
                        continue;
                    PdfDictionary state = Resolve(document, item.Value) as PdfDictionary
                        ?? throw new InvalidOperationException("An extended graphics state is not a dictionary.");
                    if (UsesTransparency(state))
                        findings.Add(Warning("Transparency.GraphicsState",
                            "An extended graphics state uses transparency or a non-normal blend mode.",
                            pageIndex, reference?.ObjectNumber));
                }
            }
            if (!resources.TryGetValue(XObjectName, out PdfObject? xObjectsValue)) return;
            PdfDictionary xObjects = Resolve(document, xObjectsValue) as PdfDictionary
                ?? throw new InvalidOperationException("An XObject resource value is not a dictionary.");
            foreach (KeyValuePair<PdfName, PdfObject> item in xObjects)
            {
                PdfIndirectReference? reference = item.Value as PdfIndirectReference;
                if (reference is not null
                    && !visitedObjects.Add((reference.ObjectNumber, reference.Generation)))
                    continue;
                if (Resolve(document, item.Value) is not PdfStream stream) continue;
                if (stream.Dictionary.ContainsKey(Name("SMask")))
                    findings.Add(Warning("Transparency.ImageSoftMask",
                        "An image uses a transparency soft mask.", pageIndex,
                        reference?.ObjectNumber));
                if (!IsName(stream.Dictionary, "Subtype", "Form")) continue;
                if (stream.Dictionary.TryGetValue(Name("Group"), out PdfObject? groupValue)
                    && Resolve(document, groupValue) is PdfDictionary group
                    && IsName(group, "S", "Transparency"))
                    findings.Add(Warning("Transparency.Group",
                        "A form uses a transparency group.", pageIndex,
                        reference?.ObjectNumber));
                if (stream.Dictionary.TryGetValue(ResourcesName, out PdfObject? formResources))
                    InspectResources(formResources, pageIndex);
            }
        }

        bool UsesTransparency(PdfDictionary state)
        {
            bool fillAlpha = state.TryGetValue(Name("ca"), out PdfObject? fillValue)
                && Number(document, fillValue) < 1;
            bool strokeAlpha = state.TryGetValue(Name("CA"), out PdfObject? strokeValue)
                && Number(document, strokeValue) < 1;
            bool blend = state.TryGetValue(Name("BM"), out PdfObject? blendValue)
                && !IsNormalBlendMode(Resolve(document, blendValue));
            bool softMask = false;
            if (state.TryGetValue(Name("SMask"), out PdfObject? maskValue))
            {
                PdfObject mask = Resolve(document, maskValue);
                softMask = mask is not PdfName name || name.ValueAsLatin1() != "None";
            }
            return fillAlpha || strokeAlpha || blend || softMask;
        }

        static bool IsNormalBlendMode(PdfObject value) => value switch
        {
            PdfName name => name.ValueAsLatin1() is "Normal" or "Compatible",
            PdfArray array => array.All(IsNormalBlendMode),
            _ => false
        };
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckColorUsage(
        PdfDocument document, double maximumInkCoveragePercent)
    {
        var findings = new List<PdfPreflightFinding>();
        var visitedColorSpaces = new HashSet<(int, int)>();
        var visitedStates = new HashSet<(int, int)>();
        var visitedForms = new HashSet<(int, int)>();
        foreach (PdfPageContentBatchResult page in PdfPageContentBatch.Read(document))
        {
            if (!page.Succeeded) continue;
            if (page.Content!.Instructions.Any(instruction =>
                instruction.Operator is "rg" or "RG"))
                findings.Add(Warning("ColorUsage.DeviceRgb",
                    "The page paints with device RGB color.", page.PageIndex));
            foreach (double coverage in page.Content.Instructions
                .Where(instruction => instruction.Operator is "k" or "K"
                    && instruction.Operands.Count == 4)
                .Select(instruction => instruction.Operands.Sum(operand =>
                    Number(document, operand)) * 100)
                .Where(coverage => coverage > maximumInkCoveragePercent))
                findings.Add(Warning("ColorUsage.InkCoverageAboveMaximum",
                    $"A process color uses {coverage:0.#}% total ink; the maximum is {maximumInkCoveragePercent:0.#}%.",
                    page.PageIndex));
        }
        try
        {
            foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
                if (page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? resources))
                    InspectResources(resources, page.Index);
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            findings.Add(Error("ColorUsage.Invalid", error.Message));
        }
        return Array.AsReadOnly(findings.ToArray());

        void InspectResources(PdfObject value, int pageIndex)
        {
            PdfDictionary resources = Resolve(document, value) as PdfDictionary
                ?? throw new InvalidOperationException("A page or form resource value is not a dictionary.");
            if (resources.TryGetValue(ColorSpaceName, out PdfObject? spacesValue))
            {
                PdfDictionary spaces = Resolve(document, spacesValue) as PdfDictionary
                    ?? throw new InvalidOperationException("A color-space resource value is not a dictionary.");
                foreach (KeyValuePair<PdfName, PdfObject> item in spaces)
                {
                    PdfIndirectReference? reference = item.Value as PdfIndirectReference;
                    if (reference is not null
                        && !visitedColorSpaces.Add((reference.ObjectNumber, reference.Generation)))
                        continue;
                    PdfObject space = Resolve(document, item.Value);
                    if (space is not PdfArray array || array.Count == 0
                        || Resolve(document, array[0]) is not PdfName family) continue;
                    if (family.ValueAsLatin1() is "Separation" or "DeviceN")
                        findings.Add(Information("ColorUsage.SpotColor",
                            "The document declares a spot color space.", pageIndex,
                            reference?.ObjectNumber));
                    if (family.ValueAsLatin1() == "ICCBased")
                        ValidateIccProfile(array, pageIndex, reference?.ObjectNumber);
                }
            }
            if (resources.TryGetValue(ExtGStateName, out PdfObject? statesValue))
            {
                PdfDictionary states = Resolve(document, statesValue) as PdfDictionary
                    ?? throw new InvalidOperationException("An extended graphics-state resource is not a dictionary.");
                foreach (KeyValuePair<PdfName, PdfObject> item in states)
                {
                    PdfIndirectReference? reference = item.Value as PdfIndirectReference;
                    if (reference is not null
                        && !visitedStates.Add((reference.ObjectNumber, reference.Generation)))
                        continue;
                    PdfDictionary state = Resolve(document, item.Value) as PdfDictionary
                        ?? throw new InvalidOperationException("An extended graphics state is not a dictionary.");
                    if (IsTrue(state, "op") || IsTrue(state, "OP"))
                        findings.Add(Information("ColorUsage.Overprint",
                            "An extended graphics state enables overprint.", pageIndex,
                            reference?.ObjectNumber));
                }
            }
            if (!resources.TryGetValue(XObjectName, out PdfObject? xObjectsValue)) return;
            PdfDictionary xObjects = Resolve(document, xObjectsValue) as PdfDictionary
                ?? throw new InvalidOperationException("An XObject resource value is not a dictionary.");
            foreach (KeyValuePair<PdfName, PdfObject> item in xObjects)
            {
                PdfIndirectReference? reference = item.Value as PdfIndirectReference;
                if (reference is not null
                    && !visitedForms.Add((reference.ObjectNumber, reference.Generation)))
                    continue;
                if (Resolve(document, item.Value) is PdfStream stream
                    && IsName(stream.Dictionary, "Subtype", "Form")
                    && stream.Dictionary.TryGetValue(ResourcesName, out PdfObject? formResources))
                    InspectResources(formResources, pageIndex);
            }
        }

        bool IsTrue(PdfDictionary dictionary, string key) =>
            dictionary.TryGetValue(Name(key), out PdfObject? value)
                && Resolve(document, value) is PdfBoolean { Value: true };

        void ValidateIccProfile(PdfArray colorSpace, int pageIndex, int? objectNumber)
        {
            int? profileObjectNumber = colorSpace.Count > 1
                ? (colorSpace[1] as PdfIndirectReference)?.ObjectNumber : null;
            try
            {
                if (colorSpace.Count < 2
                    || Resolve(document, colorSpace[1]) is not PdfStream stream
                    || !stream.Dictionary.TryGetValue(Name("N"), out PdfObject? countValue)
                    || Resolve(document, countValue) is not PdfInteger count)
                    throw new FormatException("An ICCBased color space has no usable profile.");
                PdfIccProfile profile = PdfIccProfile.Load(PdfStreamDecoder.Decode(
                    stream, document.Resolve, 64 * 1024 * 1024));
                if (count.Value != profile.ComponentCount)
                    throw new FormatException(
                        "An ICCBased color space component count does not match its profile.");
            }
            catch (Exception error) when (IsDocumentFailure(error))
            {
                findings.Add(Error("ColorUsage.InvalidIccProfile", error.Message,
                    pageIndex, objectNumber ?? profileObjectNumber));
            }
        }
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckOptionalContent(PdfDocument document)
    {
        try
        {
            PdfOptionalContentInfo layers = PdfOptionalContentReader.Read(document);
            return Array.AsReadOnly(layers.Groups.Select(layer => Information(
                "OptionalContent.LayerState",
                $"Layer {layer.Name}: visible={layer.IsInitiallyVisible}, locked={layer.IsLocked}, "
                    + $"print={State(layer.IsVisibleWhenPrinting)}, export={State(layer.IsVisibleWhenExporting)}.",
                objectNumber: layer.ObjectNumber)).ToArray());
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            return [Error("OptionalContent.Invalid", error.Message)];
        }

        static string State(bool? value) => value switch
        {
            true => "on",
            false => "off",
            null => "default"
        };
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckDocumentMetadata(PdfDocument document)
    {
        try
        {
            PdfDocumentInformation information = PdfDocumentInformation.Read(document);
            var findings = new List<PdfPreflightFinding>();
            AddMissing(information.Title, "Metadata.MissingTitle",
                "The document metadata has no title.", PdfDiagnosticSeverity.Warning);
            AddMissing(information.Author, "Metadata.MissingAuthor",
                "The document metadata has no author.", PdfDiagnosticSeverity.Information);
            AddMissing(information.Subject, "Metadata.MissingSubject",
                "The document metadata has no subject.", PdfDiagnosticSeverity.Information);
            AddMissing(information.Keywords, "Metadata.MissingKeywords",
                "The document metadata has no keywords.", PdfDiagnosticSeverity.Information);
            return Array.AsReadOnly(findings.ToArray());

            void AddMissing(string? value, string code, string message,
                PdfDiagnosticSeverity severity)
            {
                if (string.IsNullOrWhiteSpace(value))
                    findings.Add(new PdfPreflightFinding(code, severity, message));
            }
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            return [Error("Metadata.Invalid", error.Message)];
        }
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckAttachmentSafety(PdfDocument document)
    {
        var findings = new List<PdfPreflightFinding>();
        var seenFileSpecifications = new HashSet<int>();
        try
        {
            foreach (PdfAttachmentInfo attachment in PdfAttachmentReader.Read(document))
            {
                if (attachment.FileSpecificationObjectNumber is int objectNumber)
                    seenFileSpecifications.Add(objectNumber);
                AddFindings(attachment, null);
            }
        }
        catch (Exception error) when (IsDocumentFailure(error))
        {
            findings.Add(Error("Attachment.Invalid", error.Message));
        }

        PdfPageTree tree = PdfPageTree.Read(document);
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            try
            {
                foreach (PdfAttachmentAnnotationInfo annotation in
                    PdfAttachmentReader.ReadPageAnnotations(document, page.Index))
                {
                    int? objectNumber = annotation.Attachment.FileSpecificationObjectNumber;
                    if (objectNumber.HasValue && !seenFileSpecifications.Add(objectNumber.Value))
                        continue;
                    AddFindings(annotation.Attachment, page.Index);
                }
            }
            catch (Exception error) when (IsDocumentFailure(error))
            {
                findings.Add(Error("Attachment.Invalid", error.Message, page.Index));
            }
        }
        return Array.AsReadOnly(findings.ToArray());

        void AddFindings(PdfAttachmentInfo attachment, int? pageIndex)
        {
            int? objectNumber = attachment.FileSpecificationObjectNumber;
            if (attachment.HasUnsafeFileName)
                findings.Add(Error("Attachment.UnsafeFileName",
                    $"Attachment '{attachment.FileName}' has an unsafe file name.",
                    pageIndex, objectNumber));
            if (attachment.IsPotentiallyExecutable)
                findings.Add(Warning("Attachment.ExecutableFileName",
                    $"Attachment '{attachment.FileName}' uses an executable file extension.",
                    pageIndex, objectNumber));
            if (attachment.HasExecutableContent)
                findings.Add(Error("Attachment.ExecutableContent",
                    $"Attachment '{attachment.FileName}' contains a recognized executable signature.",
                    pageIndex, objectNumber));
            if (attachment.HasEncryptedContent)
                findings.Add(Warning("Attachment.EncryptedContent",
                    $"Attachment '{attachment.FileName}' contains encrypted content.",
                    pageIndex, objectNumber));
            if (attachment.SizeMatches == false)
                findings.Add(Error("Attachment.SizeMismatch",
                    $"Attachment '{attachment.FileName}' does not match its declared size.",
                    pageIndex, objectNumber));
            if (attachment.ChecksumMatches == false)
                findings.Add(Error("Attachment.ChecksumMismatch",
                    $"Attachment '{attachment.FileName}' does not match its declared checksum.",
                    pageIndex, objectNumber));
        }
    }

    internal static IReadOnlyList<PdfPreflightFinding> CheckMeasurementAnnotations(
        PdfDocument document)
    {
        var findings = new List<PdfPreflightFinding>();
        PdfPageTree tree = PdfPageTree.Read(document);
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (!page.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue))
                continue;
            PdfArray annotations;
            try
            {
                annotations = Resolve(document, annotationsValue) as PdfArray
                    ?? throw new InvalidOperationException("A page /Annots value is not an array.");
            }
            catch (Exception error) when (IsDocumentFailure(error))
            {
                findings.Add(Error("Measurement.InvalidAnnotations", error.Message, page.Index));
                continue;
            }
            foreach (PdfObject annotationValue in annotations)
            {
                int? objectNumber = (annotationValue as PdfIndirectReference)?.ObjectNumber;
                try
                {
                    if (Resolve(document, annotationValue) is not PdfDictionary annotation
                        || !annotation.TryGetValue(Name("Measure"), out PdfObject? measureValue))
                        continue;
                    PdfDictionary measure = Resolve(document, measureValue) as PdfDictionary
                        ?? throw new InvalidOperationException(
                            "A measurement annotation /Measure value is not a dictionary.");
                    if (!IsName(measure, "Subtype", "RL"))
                        throw new InvalidOperationException(
                            "A measurement annotation does not use a rectilinear scale.");
                    (double scale, string unit, int precision) = ReadMeasurementFormat(
                        document, measure, "D");
                    findings.Add(Information("Measurement.Calibration",
                        $"Measurement scale: {scale:R} {unit} per PDF point, precision {precision}.",
                        page.Index, objectNumber));
                }
                catch (Exception error) when (IsDocumentFailure(error))
                {
                    findings.Add(Error("Measurement.Invalid", error.Message,
                        page.Index, objectNumber));
                }
            }
        }
        return Array.AsReadOnly(findings.ToArray());
    }

    private static (double Scale, string Unit, int Precision) ReadMeasurementFormat(
        PdfDocument document, PdfDictionary measure, string key)
    {
        if (!measure.TryGetValue(Name(key), out PdfObject? formatsValue)
            || Resolve(document, formatsValue) is not PdfArray { Count: > 0 } formats
            || Resolve(document, formats[0]) is not PdfDictionary format)
            throw new InvalidOperationException(
                $"A measurement annotation has no usable /{key} number format.");
        double scale = format.TryGetValue(Name("C"), out PdfObject? scaleValue)
            ? Number(document, scaleValue) : 1;
        if (scale <= 0)
            throw new InvalidOperationException(
                "A measurement annotation scale must be positive.");
        if (!format.TryGetValue(Name("U"), out PdfObject? unitValue)
            || Resolve(document, unitValue) is not PdfString unitString)
            throw new InvalidOperationException(
                "A measurement annotation has no display unit.");
        string unit = PdfUnicodeEncoding.DecodeTextString(
            unitString.Bytes.Span, "A measurement display unit");
        if (string.IsNullOrWhiteSpace(unit))
            throw new InvalidOperationException(
                "A measurement annotation display unit is empty.");
        long denominator = format.TryGetValue(Name("D"), out PdfObject? denominatorValue)
            && Resolve(document, denominatorValue) is PdfInteger integer ? integer.Value : 1;
        if (denominator <= 0 || denominator > 1_000_000_000_000)
            throw new InvalidOperationException(
                "A measurement annotation precision denominator is invalid.");
        int precision = 0;
        for (long value = denominator; value > 1 && value % 10 == 0; value /= 10)
            precision++;
        return (scale, unit, precision);
    }

    private static PdfBox? Box(PdfDocument document, PdfPageTreeEntry page,
        PdfName name, bool required)
    {
        if (!page.Dictionary.TryGetValue(name, out PdfObject? value)
            && !page.InheritedValues.TryGetValue(name, out value))
        {
            if (required) throw new InvalidOperationException("A page has no media box.");
            return null;
        }
        PdfArray array = Resolve(document, value) as PdfArray
            ?? throw new InvalidOperationException("A page box is not an array.");
        if (array.Count != 4) throw new InvalidOperationException("A page box must have four coordinates.");
        double left = Number(document, array[0]);
        double bottom = Number(document, array[1]);
        double right = Number(document, array[2]);
        double top = Number(document, array[3]);
        if (right <= left || top <= bottom)
            throw new InvalidOperationException("A page box has zero or negative size.");
        return new PdfBox(left, bottom, right, top);
    }

    private static double Number(PdfDocument document, PdfObject value) =>
        Resolve(document, value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when double.IsFinite(real.Value) => real.Value,
            _ => throw new InvalidOperationException("A page-box coordinate is not a finite number.")
        };

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A preflight reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static bool IsName(PdfDictionary dictionary, string key, string expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && value is PdfName name && name.ValueAsLatin1() == expected;

    private static bool IsDocumentFailure(Exception error) =>
        error is ArgumentException or InvalidOperationException or FormatException
            or NotSupportedException or OverflowException;

    private static PdfPreflightFinding Error(string code, string message,
        int? pageIndex = null, int? objectNumber = null) =>
        new(code, PdfDiagnosticSeverity.Error, message, pageIndex, objectNumber);

    private static PdfPreflightFinding Warning(string code, string message,
        int? pageIndex = null, int? objectNumber = null) =>
        new(code, PdfDiagnosticSeverity.Warning, message, pageIndex, objectNumber);

    private static PdfPreflightFinding Information(string code, string message,
        int? pageIndex = null, int? objectNumber = null) =>
        new(code, PdfDiagnosticSeverity.Information, message, pageIndex, objectNumber);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private sealed record PdfBox(double Left, double Bottom, double Right, double Top);
}
