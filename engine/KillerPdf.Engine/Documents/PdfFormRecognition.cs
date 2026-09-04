using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Documents;

/// <summary>A field type proposed by automatic form recognition.</summary>
public enum PdfRecognizedFieldKind
{
    /// <summary>A text field.</summary>
    Text,
    /// <summary>A checkbox.</summary>
    CheckBox,
    /// <summary>One option in a radio group.</summary>
    RadioButton,
    /// <summary>A fixed-choice dropdown.</summary>
    DropDown,
    /// <summary>An editable combo box.</summary>
    EditableComboBox,
    /// <summary>A list box.</summary>
    ListBox,
    /// <summary>A push button.</summary>
    PushButton,
    /// <summary>A barcode field.</summary>
    Barcode,
    /// <summary>A signature field.</summary>
    Signature
}

/// <summary>A safe action that may be assigned to a reviewed push-button proposal.</summary>
public enum PdfRecognizedPushButtonActionKind
{
    /// <summary>Opens an absolute URI.</summary>
    Uri,
    /// <summary>Navigates to a page.</summary>
    Page,
    /// <summary>Navigates to a named destination.</summary>
    NamedDestination,
    /// <summary>Resets all or selected fields.</summary>
    ResetForm,
    /// <summary>Submits PDF form data to an absolute URI.</summary>
    SubmitPdf
}

/// <summary>Describes a reviewed push-button action without executable script content.</summary>
public sealed record PdfRecognizedPushButtonAction
{
    /// <summary>Creates a validated reviewed action.</summary>
    public PdfRecognizedPushButtonAction(PdfRecognizedPushButtonActionKind kind,
        string? target = null, int? pageIndex = null,
        IEnumerable<string>? fields = null, bool excludeFields = false)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        string[]? selectedFields = fields?.ToArray();
        if (selectedFields?.Any(string.IsNullOrWhiteSpace) == true
            || selectedFields?.Distinct(StringComparer.Ordinal).Count() != selectedFields?.Length)
            throw new ArgumentException("Push-button field selections must be nonempty and unique.", nameof(fields));
        if (excludeFields && selectedFields is not { Length: > 0 })
            throw new ArgumentException("Excluded push-button fields must be specified.", nameof(fields));
        bool needsTarget = kind is PdfRecognizedPushButtonActionKind.Uri
            or PdfRecognizedPushButtonActionKind.NamedDestination
            or PdfRecognizedPushButtonActionKind.SubmitPdf;
        if (needsTarget != !string.IsNullOrWhiteSpace(target))
            throw new ArgumentException(needsTarget
                ? "This push-button action requires a target."
                : "This push-button action does not accept a target.", nameof(target));
        if ((kind == PdfRecognizedPushButtonActionKind.Page) != pageIndex.HasValue
            || pageIndex < 0)
            throw new ArgumentException(kind == PdfRecognizedPushButtonActionKind.Page
                ? "A page push-button action requires a nonnegative page index."
                : "Only a page push-button action accepts a page index.", nameof(pageIndex));
        if (selectedFields is not null
            && kind is not (PdfRecognizedPushButtonActionKind.ResetForm
                or PdfRecognizedPushButtonActionKind.SubmitPdf))
            throw new ArgumentException("Only reset and submit actions accept field selections.", nameof(fields));
        Kind = kind;
        Target = target;
        PageIndex = pageIndex;
        Fields = selectedFields is null ? null : Array.AsReadOnly(selectedFields);
        ExcludeFields = excludeFields;
    }

    /// <summary>Gets the reviewed action type.</summary>
    public PdfRecognizedPushButtonActionKind Kind { get; }
    /// <summary>Gets the URI or named destination target.</summary>
    public string? Target { get; }
    /// <summary>Gets the zero-based destination page index.</summary>
    public int? PageIndex { get; }
    /// <summary>Gets the selected reset or submit fields.</summary>
    public IReadOnlyList<string>? Fields { get; }
    /// <summary>Gets whether the field selection is excluded rather than included.</summary>
    public bool ExcludeFields { get; }
}

/// <summary>The review state of a recognized field proposal.</summary>
public enum PdfFormProposalStatus
{
    /// <summary>The proposal is awaiting review.</summary>
    Proposed,
    /// <summary>The proposal was accepted for authoring.</summary>
    Accepted,
    /// <summary>The proposal was rejected.</summary>
    Rejected
}

/// <summary>Conservative geometry limits for digital-page field recognition.</summary>
public sealed record PdfFormRecognitionOptions
{
    /// <summary>Gets the smallest accepted rectangle dimension in points.</summary>
    public double MinimumDimension { get; init; } = 8;
    /// <summary>Gets the largest accepted field height in points.</summary>
    public double MaximumHeight { get; init; } = 60;
    /// <summary>Gets the largest square dimension proposed as a checkbox.</summary>
    public double MaximumCheckBoxDimension { get; init; } = 30;
    /// <summary>Gets the largest gap between a field and a proposed text label.</summary>
    public double MaximumLabelDistance { get; init; } = 72;
}

/// <summary>Finds conservative rectangular field candidates for explicit review.</summary>
public static class PdfFormRecognizer
{
    /// <summary>Returns pending proposals without modifying the source document.</summary>
    public static PdfFormRecognitionReview Recognize(
        PdfDocument document, PdfFormRecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfFormRecognitionOptions();
        if (!double.IsFinite(options.MinimumDimension)
            || options.MinimumDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(options.MaximumHeight)
            || options.MaximumHeight < options.MinimumDimension)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(options.MaximumCheckBoxDimension)
            || options.MaximumCheckBoxDimension < options.MinimumDimension)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(options.MaximumLabelDistance)
            || options.MaximumLabelDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        var reader = new PdfPageContentReader(document);
        var proposals = new List<PdfFormFieldProposal>();
        int pageCount = PdfPageBoxInformation.Read(document).Count;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageContent content = reader.Read(pageIndex, cancellationToken);
            PdfContentBounds[] widgets = [.. PdfFormWidgetReader.ReadPage(document, pageIndex)
                .Select(widget => new PdfContentBounds(
                    widget.Left, widget.Bottom, widget.Right, widget.Top))];
            int sequence = 0;
            foreach ((PdfExtractedPath path, int pathIndex) in content.Paths
                         .Select((path, index) => (path, index)))
            {
                if (path.IsClippingPath || path.Segments.Count != 1
                    || path.Segments[0].Operator != "re"
                    || path.PaintOperator is not ("S" or "s" or "B" or "B*"))
                    continue;
                PdfContentBounds bounds = path.BoundingBox;
                if (bounds.Width < options.MinimumDimension
                    || bounds.Height < options.MinimumDimension
                    || bounds.Height > options.MaximumHeight
                    || widgets.Any(widget => Intersects(widget, bounds)))
                    continue;
                bool square = Math.Max(bounds.Width, bounds.Height)
                    <= options.MaximumCheckBoxDimension
                    && Math.Abs(bounds.Width - bounds.Height)
                        <= Math.Max(bounds.Width, bounds.Height) * 0.2;
                sequence++;
                string? label = FindLabel(content.Lines, bounds, square,
                    options.MaximumLabelDistance);
                string suggestedName = label is null
                    ? $"field_{pageIndex + 1}_{sequence}"
                    : NormalizeName(label, pageIndex, sequence);
                proposals.Add(new PdfFormFieldProposal(
                    $"page-{pageIndex + 1}-path-{pathIndex + 1}", pageIndex, bounds,
                    square ? PdfRecognizedFieldKind.CheckBox : PdfRecognizedFieldKind.Text,
                    (square ? 0.9 : 0.7) + (label is null ? 0 : 0.05),
                    suggestedName, suggestedTooltip: label));
            }
        }
        return new PdfFormRecognitionReview(proposals);
    }

    private static bool Intersects(PdfContentBounds left, PdfContentBounds right) =>
        left.Left < right.Right && left.Right > right.Left
        && left.Bottom < right.Top && left.Top > right.Bottom;

    private static string? FindLabel(IReadOnlyList<PdfExtractedLine> lines,
        PdfContentBounds field, bool square, double maximumDistance)
    {
        double centerY = (field.Bottom + field.Top) / 2;
        return lines.Select(line =>
            {
                PdfContentBounds text = line.BoundingBox;
                double textCenterY = (text.Bottom + text.Top) / 2;
                double? distance = text.Right <= field.Left
                    && Math.Abs(textCenterY - centerY) <= Math.Max(field.Height, text.Height) / 2
                        ? field.Left - text.Right
                    : square && text.Left >= field.Right
                        && Math.Abs(textCenterY - centerY) <= Math.Max(field.Height, text.Height) / 2
                            ? text.Left - field.Right
                        : text.Bottom >= field.Top
                            && text.Left < field.Right && text.Right > field.Left
                                ? text.Bottom - field.Top : null;
                return (line, distance);
            })
            .Where(candidate => candidate.distance is >= 0
                && candidate.distance <= maximumDistance
                && !string.IsNullOrWhiteSpace(candidate.line.Text))
            .OrderBy(candidate => candidate.distance)
            .ThenBy(candidate => candidate.line.BoundingBox.Left)
            .Select(candidate => candidate.line.Text.Trim().TrimEnd(':', '*'))
            .FirstOrDefault(text => text.Length > 0);
    }

    private static string NormalizeName(string label, int pageIndex, int sequence)
    {
        string normalized = string.Join('_', label.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => new string(part.Where(char.IsLetterOrDigit).ToArray()))
            .Where(part => part.Length > 0));
        return normalized.Length > 0
            ? normalized : $"field_{pageIndex + 1}_{sequence}";
    }
}

/// <summary>One field proposed by a form-recognition provider.</summary>
public sealed record PdfFormFieldProposal
{
    /// <summary>Creates a validated field proposal.</summary>
    public PdfFormFieldProposal(string id, int pageIndex, PdfContentBounds bounds,
        PdfRecognizedFieldKind kind, double confidence, string suggestedName,
        PdfFormProposalStatus status = PdfFormProposalStatus.Proposed,
        string? suggestedTooltip = null, IEnumerable<string>? suggestedOptions = null,
        string? suggestedValue = null, bool suggestedReadOnly = false,
        bool suggestedRequired = false, bool suggestedChecked = false,
        bool suggestedMultiline = false, bool suggestedDoNotScroll = false,
        bool suggestedPassword = false, bool suggestedDoNotSpellCheck = false,
        bool suggestedComb = false, int? suggestedMaximumLength = null,
        PdfTextFieldAlignment suggestedAlignment = PdfTextFieldAlignment.Left,
        double suggestedFontSize = 12,
        PdfFormFieldAppearanceStyle? suggestedAppearanceStyle = null,
        bool suggestedNoExport = false,
        PdfFormFieldVisibility suggestedVisibility = PdfFormFieldVisibility.Visible,
        PdfRecognizedPushButtonAction? suggestedPushButtonAction = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A proposal ID is required.", nameof(id));
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentException("Field bounds must have positive dimensions.", nameof(bounds));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (string.IsNullOrWhiteSpace(suggestedName)) throw new ArgumentException("A suggested field name is required.", nameof(suggestedName));
        if (suggestedTooltip is not null && string.IsNullOrWhiteSpace(suggestedTooltip))
            throw new ArgumentException("A suggested tooltip cannot be empty.", nameof(suggestedTooltip));
        string[] options = suggestedOptions?.ToArray() ?? [];
        if (options.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Suggested choices cannot be empty.", nameof(suggestedOptions));
        if (options.Distinct(StringComparer.Ordinal).Count() != options.Length)
            throw new ArgumentException("Suggested choices must be unique.", nameof(suggestedOptions));
        if (suggestedValue is not null && string.IsNullOrWhiteSpace(suggestedValue))
            throw new ArgumentException("A suggested value cannot be empty.", nameof(suggestedValue));
        if (suggestedValue is not null
            && kind is PdfRecognizedFieldKind.DropDown or PdfRecognizedFieldKind.ListBox
            && !options.Contains(suggestedValue, StringComparer.Ordinal))
            throw new ArgumentException("The suggested value must match a suggested choice.", nameof(suggestedValue));
        if (suggestedChecked && kind is not (PdfRecognizedFieldKind.CheckBox
                or PdfRecognizedFieldKind.RadioButton))
            throw new ArgumentException(
                "A selected state can be suggested only for a checkbox or radio option.",
                nameof(suggestedChecked));
        if (suggestedMultiline && kind != PdfRecognizedFieldKind.Text)
            throw new ArgumentException(
                "Multiline behavior can be suggested only for a text field.",
                nameof(suggestedMultiline));
        if (suggestedDoNotScroll && kind != PdfRecognizedFieldKind.Text)
            throw new ArgumentException(
                "No-scroll behavior can be suggested only for a text field.",
                nameof(suggestedDoNotScroll));
        if ((suggestedPassword || suggestedDoNotSpellCheck || suggestedComb
                || suggestedMaximumLength.HasValue)
            && kind != PdfRecognizedFieldKind.Text)
            throw new ArgumentException(
                "Text-entry behavior can be suggested only for a text field.", nameof(kind));
        if (suggestedMaximumLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(suggestedMaximumLength));
        if (suggestedPassword && suggestedMultiline)
            throw new ArgumentException("A password field cannot be multiline.", nameof(suggestedPassword));
        if (suggestedComb && (!suggestedMaximumLength.HasValue
                || suggestedMultiline || suggestedPassword))
            throw new ArgumentException(
                "A comb field requires a maximum length and cannot be multiline or password protected.",
                nameof(suggestedComb));
        if (!Enum.IsDefined(suggestedAlignment))
            throw new ArgumentOutOfRangeException(nameof(suggestedAlignment));
        if (!double.IsFinite(suggestedFontSize) || suggestedFontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(suggestedFontSize));
        suggestedAppearanceStyle = ValidateAppearance(suggestedAppearanceStyle);
        if (!Enum.IsDefined(suggestedVisibility))
            throw new ArgumentOutOfRangeException(nameof(suggestedVisibility));
        if (suggestedPushButtonAction is not null
            && kind != PdfRecognizedFieldKind.PushButton)
            throw new ArgumentException(
                "A push-button action can be suggested only for a push button.",
                nameof(suggestedPushButtonAction));
        if (suggestedAlignment != PdfTextFieldAlignment.Left
            && kind is not (PdfRecognizedFieldKind.Text or PdfRecognizedFieldKind.DropDown
                or PdfRecognizedFieldKind.EditableComboBox or PdfRecognizedFieldKind.ListBox
                or PdfRecognizedFieldKind.PushButton))
            throw new ArgumentException(
                "Alignment can be suggested only for a text, choice, or push-button field.",
                nameof(suggestedAlignment));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Id = id;
        PageIndex = pageIndex;
        Bounds = bounds;
        Kind = kind;
        Confidence = confidence;
        SuggestedName = suggestedName;
        SuggestedTooltip = suggestedTooltip;
        SuggestedOptions = Array.AsReadOnly(options);
        SuggestedValue = suggestedValue;
        SuggestedReadOnly = suggestedReadOnly;
        SuggestedRequired = suggestedRequired;
        SuggestedChecked = suggestedChecked;
        SuggestedMultiline = suggestedMultiline;
        SuggestedDoNotScroll = suggestedDoNotScroll;
        SuggestedPassword = suggestedPassword;
        SuggestedDoNotSpellCheck = suggestedDoNotSpellCheck;
        SuggestedComb = suggestedComb;
        SuggestedMaximumLength = suggestedMaximumLength;
        SuggestedAlignment = suggestedAlignment;
        SuggestedFontSize = suggestedFontSize;
        SuggestedAppearanceStyle = suggestedAppearanceStyle;
        SuggestedNoExport = suggestedNoExport;
        SuggestedVisibility = suggestedVisibility;
        SuggestedPushButtonAction = suggestedPushButtonAction;
        Status = status;
    }

    /// <summary>Gets the stable proposal ID.</summary>
    public string Id { get; }
    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex { get; }
    /// <summary>Gets the proposed field bounds in PDF points.</summary>
    public PdfContentBounds Bounds { get; }
    /// <summary>Gets the proposed field type.</summary>
    public PdfRecognizedFieldKind Kind { get; }
    /// <summary>Gets recognition confidence from zero through one.</summary>
    public double Confidence { get; }
    /// <summary>Gets the proposed field name.</summary>
    public string SuggestedName { get; }
    /// <summary>Gets the proposed user-facing field description.</summary>
    public string? SuggestedTooltip { get; }
    /// <summary>Gets the proposed choices for a choice field.</summary>
    public IReadOnlyList<string> SuggestedOptions { get; }
    /// <summary>Gets the proposed text, selected choice, or button export value.</summary>
    public string? SuggestedValue { get; }
    /// <summary>Gets whether the proposed field should be read-only.</summary>
    public bool SuggestedReadOnly { get; }
    /// <summary>Gets whether the proposed field should require a value.</summary>
    public bool SuggestedRequired { get; }
    /// <summary>Gets whether a proposed checkbox or radio option should initially be selected.</summary>
    public bool SuggestedChecked { get; }
    /// <summary>Gets whether a proposed text field should accept multiple lines.</summary>
    public bool SuggestedMultiline { get; }
    /// <summary>Gets whether a proposed text field should disable viewer scrolling.</summary>
    public bool SuggestedDoNotScroll { get; }
    /// <summary>Gets whether the proposed text value is visually obscured.</summary>
    public bool SuggestedPassword { get; }
    /// <summary>Gets whether viewer spell checking is disabled for the proposed text field.</summary>
    public bool SuggestedDoNotSpellCheck { get; }
    /// <summary>Gets whether the proposed text field uses equally spaced character cells.</summary>
    public bool SuggestedComb { get; }
    /// <summary>Gets the proposed maximum text length.</summary>
    public int? SuggestedMaximumLength { get; }
    /// <summary>Gets the proposed horizontal text or choice alignment.</summary>
    public PdfTextFieldAlignment SuggestedAlignment { get; }
    /// <summary>Gets the proposed appearance font size in points.</summary>
    public double SuggestedFontSize { get; }
    /// <summary>Gets the proposed widget colors and border geometry.</summary>
    public PdfFormFieldAppearanceStyle SuggestedAppearanceStyle { get; }
    /// <summary>Gets whether the proposed field is omitted from form submission.</summary>
    public bool SuggestedNoExport { get; }
    /// <summary>Gets the proposed screen and print visibility.</summary>
    public PdfFormFieldVisibility SuggestedVisibility { get; }
    /// <summary>Gets the reviewed safe action for a proposed push button.</summary>
    public PdfRecognizedPushButtonAction? SuggestedPushButtonAction { get; }
    /// <summary>Gets the current review state.</summary>
    public PdfFormProposalStatus Status { get; }

    internal PdfFormFieldProposal Review(PdfFormProposalStatus status, string? name = null,
        PdfRecognizedFieldKind? kind = null, PdfContentBounds? bounds = null,
        string? tooltip = null, IEnumerable<string>? options = null, string? value = null,
        bool? readOnly = null, bool? required = null, bool? isChecked = null,
        bool? multiline = null, bool? doNotScroll = null,
        bool? password = null, bool? doNotSpellCheck = null,
        bool? comb = null, int? maximumLength = null,
        PdfTextFieldAlignment? alignment = null, double? fontSize = null,
        PdfFormFieldAppearanceStyle? appearanceStyle = null, bool? noExport = null,
        PdfFormFieldVisibility? visibility = null,
        PdfRecognizedPushButtonAction? pushButtonAction = null) =>
        new(Id, PageIndex, bounds ?? Bounds, kind ?? Kind, Confidence, name ?? SuggestedName,
            status, tooltip ?? SuggestedTooltip, options ?? SuggestedOptions, value ?? SuggestedValue,
            readOnly ?? SuggestedReadOnly, required ?? SuggestedRequired,
            isChecked ?? SuggestedChecked, multiline ?? SuggestedMultiline,
            doNotScroll ?? SuggestedDoNotScroll, password ?? SuggestedPassword,
            doNotSpellCheck ?? SuggestedDoNotSpellCheck, comb ?? SuggestedComb,
            maximumLength ?? SuggestedMaximumLength, alignment ?? SuggestedAlignment,
            fontSize ?? SuggestedFontSize, appearanceStyle ?? SuggestedAppearanceStyle,
            noExport ?? SuggestedNoExport, visibility ?? SuggestedVisibility,
            pushButtonAction ?? SuggestedPushButtonAction);

    internal PdfFormFieldProposal Duplicate(string id, int pageIndex,
        PdfContentBounds bounds, string suggestedName) =>
        new(id, pageIndex, bounds, Kind, Confidence, suggestedName,
            PdfFormProposalStatus.Proposed, SuggestedTooltip, SuggestedOptions, SuggestedValue,
            SuggestedReadOnly, SuggestedRequired, SuggestedChecked, SuggestedMultiline,
            SuggestedDoNotScroll, SuggestedPassword, SuggestedDoNotSpellCheck,
            SuggestedComb, SuggestedMaximumLength, SuggestedAlignment, SuggestedFontSize,
            SuggestedAppearanceStyle, SuggestedNoExport, SuggestedVisibility,
            SuggestedPushButtonAction);

    private static PdfFormFieldAppearanceStyle ValidateAppearance(
        PdfFormFieldAppearanceStyle? style)
    {
        style ??= new PdfFormFieldAppearanceStyle();
        if (!double.IsFinite(style.BorderWidth) || style.BorderWidth < 0
            || !Enum.IsDefined(style.BorderStyle))
            throw new ArgumentOutOfRangeException(nameof(style));
        double[]? dash = style.DashPattern?.ToArray();
        if (style.BorderStyle == PdfFormFieldBorderStyle.Dashed)
        {
            dash ??= [3];
            if (dash.Length == 0 || dash.Any(value => !double.IsFinite(value) || value < 0)
                || dash.All(value => value == 0))
                throw new ArgumentException("A dashed field border requires a valid dash pattern.", nameof(style));
        }
        else if (dash is not null)
            throw new ArgumentException("A dash pattern requires a dashed field border.", nameof(style));
        return style with { DashPattern = dash };
    }
}

/// <summary>An immutable review boundary between field detection and AcroForm authoring.</summary>
public sealed class PdfFormRecognitionReview
{
    private readonly PdfFormFieldProposal[] _proposals;

    /// <summary>Creates a review from untrusted recognition proposals.</summary>
    public PdfFormRecognitionReview(IEnumerable<PdfFormFieldProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        _proposals = proposals.OrderBy(item => item.PageIndex).ThenByDescending(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left).ToArray();
        if (_proposals.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != _proposals.Length)
            throw new ArgumentException("Form proposal IDs must be unique.", nameof(proposals));
        Proposals = Array.AsReadOnly(_proposals);
    }

    /// <summary>Gets proposals in visual page order.</summary>
    public IReadOnlyList<PdfFormFieldProposal> Proposals { get; }
    /// <summary>Gets whether every proposal has an explicit decision.</summary>
    public bool IsReadyToApply => _proposals.Length > 0
        && _proposals.All(item => item.Status != PdfFormProposalStatus.Proposed);
    /// <summary>Gets only proposals accepted for later authoring.</summary>
    public IReadOnlyList<PdfFormFieldProposal> Accepted => Array.AsReadOnly(_proposals
        .Where(item => item.Status == PdfFormProposalStatus.Accepted).ToArray());

    /// <summary>Returns a new review with a proposal accepted and optionally adjusted.</summary>
    public PdfFormRecognitionReview Accept(string id, string? name = null,
        PdfRecognizedFieldKind? kind = null, PdfContentBounds? bounds = null,
        string? tooltip = null, IEnumerable<string>? options = null, string? value = null,
        bool? readOnly = null, bool? required = null, bool? isChecked = null,
        bool? multiline = null, bool? doNotScroll = null,
        bool? password = null, bool? doNotSpellCheck = null,
        bool? comb = null, int? maximumLength = null,
        PdfTextFieldAlignment? alignment = null, double? fontSize = null,
        PdfFormFieldAppearanceStyle? appearanceStyle = null, bool? noExport = null,
        PdfFormFieldVisibility? visibility = null,
        PdfRecognizedPushButtonAction? pushButtonAction = null) =>
        Change(id, item => item.Review(
            PdfFormProposalStatus.Accepted, name, kind, bounds, tooltip, options, value,
            readOnly, required, isChecked, multiline, doNotScroll,
            password, doNotSpellCheck, comb, maximumLength, alignment,
            fontSize, appearanceStyle, noExport, visibility, pushButtonAction));

    /// <summary>Returns a new review with a proposal rejected.</summary>
    public PdfFormRecognitionReview Reject(string id) =>
        Change(id, item => item.Review(PdfFormProposalStatus.Rejected));

    /// <summary>Returns a new review with all selected proposals accepted.</summary>
    public PdfFormRecognitionReview AcceptMany(IEnumerable<string> ids) =>
        ChangeMany(ids, item => item.Review(PdfFormProposalStatus.Accepted));

    /// <summary>Returns a new review with all selected proposals rejected.</summary>
    public PdfFormRecognitionReview RejectMany(IEnumerable<string> ids) =>
        ChangeMany(ids, item => item.Review(PdfFormProposalStatus.Rejected));

    /// <summary>Returns a new review with an editable copy awaiting its own decision.</summary>
    public PdfFormRecognitionReview Duplicate(string sourceId, string newId,
        string suggestedName, PdfContentBounds bounds, int? pageIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        if (_proposals.Any(item => string.Equals(item.Id, newId, StringComparison.Ordinal)))
            throw new ArgumentException($"Form proposal ID '{newId}' is already in use.", nameof(newId));
        PdfFormFieldProposal source = Find(sourceId);
        PdfFormFieldProposal duplicate = source.Duplicate(
            newId, pageIndex ?? source.PageIndex, bounds, suggestedName);
        return new PdfFormRecognitionReview(_proposals.Append(duplicate));
    }

    /// <summary>Creates accepted basic and choice fields in an existing document.</summary>
    public byte[] ApplyAccepted(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsReadyToApply)
            throw new InvalidOperationException(
                "Every form proposal must be accepted or rejected before authoring.");
        PdfFormFieldProposal? unsupported = Accepted.FirstOrDefault(item => !CanAuthor(item));
        if (unsupported is not null)
            throw new NotSupportedException(
                $"Accepted {unsupported.Kind} proposal '{unsupported.Id}' requires additional authoring choices.");
        if (Accepted.Count == 0) return CopySource(document);
        PdfFormFieldProposal[][] radioGroups = [.. Accepted
            .Where(item => item.Kind == PdfRecognizedFieldKind.RadioButton)
            .GroupBy(item => item.SuggestedName, StringComparer.Ordinal)
            .Select(group => group.ToArray())];
        PdfFormFieldProposal[]? invalidRadioGroup = radioGroups.FirstOrDefault(group =>
            group.Length < 2 || group.Select(item => item.SuggestedValue)
                .Distinct(StringComparer.Ordinal).Count() != group.Length
            || group.Count(item => item.SuggestedChecked) > 1);
        if (invalidRadioGroup is not null)
            throw new NotSupportedException(
                $"Radio group '{invalidRadioGroup[0].SuggestedName}' requires at least two unique options and no more than one selection.");
        var editor = new PdfIncrementalPageEditor(document);
        foreach (PdfFormFieldProposal proposal in Accepted)
        {
            PdfContentBounds bounds = proposal.Bounds;
            PdfFormFieldMetadata? metadata = proposal.SuggestedTooltip is null ? null
                : new PdfFormFieldMetadata { Tooltip = proposal.SuggestedTooltip };
            var fieldOptions = new PdfFormFieldOptions
            {
                ReadOnly = proposal.SuggestedReadOnly,
                Required = proposal.SuggestedRequired,
                NoExport = proposal.SuggestedNoExport,
                Visibility = proposal.SuggestedVisibility
            };
            switch (proposal.Kind)
            {
                case PdfRecognizedFieldKind.Text:
                    editor.AddTextField(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        value: proposal.SuggestedValue ?? string.Empty,
                        options: new PdfTextFieldOptions
                        {
                            ReadOnly = proposal.SuggestedReadOnly,
                            Required = proposal.SuggestedRequired,
                            NoExport = proposal.SuggestedNoExport,
                            Visibility = proposal.SuggestedVisibility,
                            Multiline = proposal.SuggestedMultiline,
                            Password = proposal.SuggestedPassword,
                            DoNotSpellCheck = proposal.SuggestedDoNotSpellCheck,
                            DoNotScroll = proposal.SuggestedDoNotScroll,
                            Comb = proposal.SuggestedComb,
                            MaximumLength = proposal.SuggestedMaximumLength,
                            Alignment = proposal.SuggestedAlignment
                        },
                        fontSize: proposal.SuggestedFontSize,
                        fieldMetadata: metadata,
                        appearanceStyle: proposal.SuggestedAppearanceStyle);
                    break;
                case PdfRecognizedFieldKind.CheckBox:
                    editor.AddCheckBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        isChecked: proposal.SuggestedChecked,
                        exportValue: proposal.SuggestedValue ?? "Yes",
                        fieldMetadata: metadata, options: fieldOptions,
                        appearanceStyle: proposal.SuggestedAppearanceStyle);
                    break;
                case PdfRecognizedFieldKind.DropDown:
                case PdfRecognizedFieldKind.EditableComboBox:
                    editor.AddComboBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        proposal.SuggestedOptions, proposal.SuggestedValue,
                        editable: proposal.Kind == PdfRecognizedFieldKind.EditableComboBox,
                        fontSize: proposal.SuggestedFontSize,
                        fieldMetadata: metadata, fieldOptions: fieldOptions,
                        choiceOptions: new PdfChoiceFieldOptions
                        {
                            Alignment = proposal.SuggestedAlignment,
                            AppearanceStyle = proposal.SuggestedAppearanceStyle
                        });
                    break;
                case PdfRecognizedFieldKind.ListBox:
                    editor.AddListBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        proposal.SuggestedOptions, proposal.SuggestedValue,
                        fontSize: proposal.SuggestedFontSize,
                        fieldMetadata: metadata, fieldOptions: fieldOptions,
                        choiceOptions: new PdfChoiceFieldOptions
                        {
                            Alignment = proposal.SuggestedAlignment,
                            AppearanceStyle = proposal.SuggestedAppearanceStyle
                        });
                    break;
                case PdfRecognizedFieldKind.PushButton:
                    AddPushButton(editor, proposal, bounds, metadata, fieldOptions);
                    break;
                case PdfRecognizedFieldKind.Signature:
                    editor.AddSignatureField(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        fieldMetadata: metadata, fieldOptions: fieldOptions,
                        fontSize: proposal.SuggestedFontSize,
                        appearanceStyle: proposal.SuggestedAppearanceStyle);
                    break;
            }
        }
        foreach (PdfFormFieldProposal[] group in radioGroups)
        {
            if (group.Any(proposal => proposal.SuggestedReadOnly != group[0].SuggestedReadOnly
                || proposal.SuggestedRequired != group[0].SuggestedRequired
                || proposal.SuggestedNoExport != group[0].SuggestedNoExport
                || proposal.SuggestedVisibility != group[0].SuggestedVisibility
                || proposal.SuggestedAppearanceStyle != group[0].SuggestedAppearanceStyle))
                throw new NotSupportedException(
                    $"Radio group '{group[0].SuggestedName}' has inconsistent field requirements.");
            PdfFormFieldMetadata? metadata = group[0].SuggestedTooltip is null ? null
                : new PdfFormFieldMetadata { Tooltip = group[0].SuggestedTooltip };
            editor.AddRadioGroup(group[0].SuggestedName, group.Select(proposal =>
                new PdfRadioButtonOption(proposal.PageIndex, proposal.Bounds.Left,
                    proposal.Bounds.Bottom, proposal.Bounds.Width, proposal.Bounds.Height,
                    proposal.SuggestedValue!)),
                selectedValue: group.SingleOrDefault(proposal => proposal.SuggestedChecked)
                    ?.SuggestedValue,
                fieldMetadata: metadata,
                fieldOptions: new PdfFormFieldOptions
                {
                    ReadOnly = group[0].SuggestedReadOnly,
                    Required = group[0].SuggestedRequired,
                    NoExport = group[0].SuggestedNoExport,
                    Visibility = group[0].SuggestedVisibility
                },
                radioOptions: new PdfRadioGroupOptions
                {
                    AppearanceStyle = group[0].SuggestedAppearanceStyle
                });
        }
        return editor.Build();
    }

    private static byte[] CopySource(PdfDocument document) => document.Source.ToArray();

    private static void AddPushButton(PdfIncrementalPageEditor editor,
        PdfFormFieldProposal proposal, PdfContentBounds bounds,
        PdfFormFieldMetadata? metadata, PdfFormFieldOptions fieldOptions)
    {
        PdfRecognizedPushButtonAction action = proposal.SuggestedPushButtonAction!;
        var appearance = new PdfPushButtonAppearanceOptions
        {
            Alignment = proposal.SuggestedAlignment
        };
        switch (action.Kind)
        {
            case PdfRecognizedPushButtonActionKind.Uri:
                editor.AddUriPushButton(proposal.PageIndex, proposal.SuggestedName,
                    bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                    proposal.SuggestedValue!, action.Target!, proposal.SuggestedFontSize,
                    fieldMetadata: metadata, fieldOptions: fieldOptions,
                    appearanceStyle: proposal.SuggestedAppearanceStyle,
                    appearanceOptions: appearance);
                break;
            case PdfRecognizedPushButtonActionKind.Page:
                editor.AddPagePushButton(proposal.PageIndex, proposal.SuggestedName,
                    bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                    proposal.SuggestedValue!, action.PageIndex!.Value,
                    fontSize: proposal.SuggestedFontSize,
                    fieldMetadata: metadata, fieldOptions: fieldOptions,
                    appearanceStyle: proposal.SuggestedAppearanceStyle,
                    appearanceOptions: appearance);
                break;
            case PdfRecognizedPushButtonActionKind.NamedDestination:
                editor.AddNamedDestinationPushButton(proposal.PageIndex, proposal.SuggestedName,
                    bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                    proposal.SuggestedValue!, action.Target!, proposal.SuggestedFontSize,
                    fieldMetadata: metadata, fieldOptions: fieldOptions,
                    appearanceStyle: proposal.SuggestedAppearanceStyle,
                    appearanceOptions: appearance);
                break;
            case PdfRecognizedPushButtonActionKind.ResetForm:
                editor.AddResetFormPushButton(proposal.PageIndex, proposal.SuggestedName,
                    bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                    proposal.SuggestedValue!, action.Fields, action.ExcludeFields,
                    proposal.SuggestedFontSize, fieldMetadata: metadata,
                    fieldOptions: fieldOptions,
                    appearanceStyle: proposal.SuggestedAppearanceStyle,
                    appearanceOptions: appearance);
                break;
            case PdfRecognizedPushButtonActionKind.SubmitPdf:
                editor.AddSubmitPdfPushButton(proposal.PageIndex, proposal.SuggestedName,
                    bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                    proposal.SuggestedValue!, action.Target!, action.Fields,
                    action.ExcludeFields, proposal.SuggestedFontSize,
                    fieldMetadata: metadata, fieldOptions: fieldOptions,
                    appearanceStyle: proposal.SuggestedAppearanceStyle,
                    appearanceOptions: appearance);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static bool CanAuthor(PdfFormFieldProposal proposal) => proposal.Kind switch
    {
        PdfRecognizedFieldKind.Text or PdfRecognizedFieldKind.CheckBox
            or PdfRecognizedFieldKind.Signature => true,
        PdfRecognizedFieldKind.RadioButton => proposal.SuggestedValue is not null,
        PdfRecognizedFieldKind.DropDown or PdfRecognizedFieldKind.EditableComboBox
            or PdfRecognizedFieldKind.ListBox => proposal.SuggestedOptions.Count > 0,
        PdfRecognizedFieldKind.PushButton => proposal.SuggestedValue is not null
            && proposal.SuggestedPushButtonAction is not null,
        _ => false
    };

    private PdfFormRecognitionReview Change(string id,
        Func<PdfFormFieldProposal, PdfFormFieldProposal> change)
    {
        int index = Array.IndexOf(_proposals, Find(id));
        PdfFormFieldProposal[] changed = (PdfFormFieldProposal[])_proposals.Clone();
        changed[index] = change(changed[index]);
        return new PdfFormRecognitionReview(changed);
    }

    private PdfFormRecognitionReview ChangeMany(IEnumerable<string> ids,
        Func<PdfFormFieldProposal, PdfFormFieldProposal> change)
    {
        ArgumentNullException.ThrowIfNull(ids);
        string[] selected = ids.ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("At least one form proposal must be selected.", nameof(ids));
        if (selected.Any(string.IsNullOrWhiteSpace)
            || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("Selected form proposal IDs must be nonempty and unique.", nameof(ids));
        var selectedIds = new HashSet<string>(selected, StringComparer.Ordinal);
        string? missing = selected.FirstOrDefault(id => !_proposals.Any(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal)));
        if (missing is not null)
            throw new KeyNotFoundException($"Form proposal '{missing}' was not found.");
        return new PdfFormRecognitionReview(_proposals.Select(item =>
            selectedIds.Contains(item.Id) ? change(item) : item));
    }

    private PdfFormFieldProposal Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _proposals.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Form proposal '{id}' was not found.");
    }
}
