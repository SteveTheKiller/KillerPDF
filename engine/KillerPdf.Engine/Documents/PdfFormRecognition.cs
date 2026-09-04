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

/// <summary>One field proposed by a form-recognition provider.</summary>
public sealed record PdfFormFieldProposal
{
    /// <summary>Creates a validated field proposal.</summary>
    public PdfFormFieldProposal(string id, int pageIndex, PdfContentBounds bounds,
        PdfRecognizedFieldKind kind, double confidence, string suggestedName,
        PdfFormProposalStatus status = PdfFormProposalStatus.Proposed,
        string? suggestedTooltip = null, IEnumerable<string>? suggestedOptions = null,
        string? suggestedValue = null, bool suggestedReadOnly = false,
        bool suggestedRequired = false, bool suggestedChecked = false)
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
        if (suggestedChecked && kind != PdfRecognizedFieldKind.CheckBox)
            throw new ArgumentException(
                "A checked state can be suggested only for a checkbox.", nameof(suggestedChecked));
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
    /// <summary>Gets whether a proposed checkbox should initially be checked.</summary>
    public bool SuggestedChecked { get; }
    /// <summary>Gets the current review state.</summary>
    public PdfFormProposalStatus Status { get; }

    internal PdfFormFieldProposal Review(PdfFormProposalStatus status, string? name = null,
        PdfRecognizedFieldKind? kind = null, PdfContentBounds? bounds = null,
        string? tooltip = null, IEnumerable<string>? options = null, string? value = null,
        bool? readOnly = null, bool? required = null, bool? isChecked = null) =>
        new(Id, PageIndex, bounds ?? Bounds, kind ?? Kind, Confidence, name ?? SuggestedName,
            status, tooltip ?? SuggestedTooltip, options ?? SuggestedOptions, value ?? SuggestedValue,
            readOnly ?? SuggestedReadOnly, required ?? SuggestedRequired,
            isChecked ?? SuggestedChecked);
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
        bool? readOnly = null, bool? required = null, bool? isChecked = null) =>
        Change(id, item => item.Review(
            PdfFormProposalStatus.Accepted, name, kind, bounds, tooltip, options, value,
            readOnly, required, isChecked));

    /// <summary>Returns a new review with a proposal rejected.</summary>
    public PdfFormRecognitionReview Reject(string id) =>
        Change(id, item => item.Review(PdfFormProposalStatus.Rejected));

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
                .Distinct(StringComparer.Ordinal).Count() != group.Length);
        if (invalidRadioGroup is not null)
            throw new NotSupportedException(
                $"Radio group '{invalidRadioGroup[0].SuggestedName}' requires at least two unique options.");
        var editor = new PdfIncrementalPageEditor(document);
        foreach (PdfFormFieldProposal proposal in Accepted)
        {
            PdfContentBounds bounds = proposal.Bounds;
            PdfFormFieldMetadata? metadata = proposal.SuggestedTooltip is null ? null
                : new PdfFormFieldMetadata { Tooltip = proposal.SuggestedTooltip };
            var fieldOptions = new PdfFormFieldOptions
            {
                ReadOnly = proposal.SuggestedReadOnly,
                Required = proposal.SuggestedRequired
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
                            Required = proposal.SuggestedRequired
                        },
                        fieldMetadata: metadata);
                    break;
                case PdfRecognizedFieldKind.CheckBox:
                    editor.AddCheckBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        isChecked: proposal.SuggestedChecked,
                        exportValue: proposal.SuggestedValue ?? "Yes",
                        fieldMetadata: metadata, options: fieldOptions);
                    break;
                case PdfRecognizedFieldKind.DropDown:
                case PdfRecognizedFieldKind.EditableComboBox:
                    editor.AddComboBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        proposal.SuggestedOptions, proposal.SuggestedValue,
                        editable: proposal.Kind == PdfRecognizedFieldKind.EditableComboBox,
                        fieldMetadata: metadata, fieldOptions: fieldOptions);
                    break;
                case PdfRecognizedFieldKind.ListBox:
                    editor.AddListBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        proposal.SuggestedOptions, proposal.SuggestedValue,
                        fieldMetadata: metadata, fieldOptions: fieldOptions);
                    break;
                case PdfRecognizedFieldKind.Signature:
                    editor.AddSignatureField(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        fieldMetadata: metadata, fieldOptions: fieldOptions);
                    break;
            }
        }
        foreach (PdfFormFieldProposal[] group in radioGroups)
        {
            if (group.Any(proposal => proposal.SuggestedReadOnly != group[0].SuggestedReadOnly
                || proposal.SuggestedRequired != group[0].SuggestedRequired))
                throw new NotSupportedException(
                    $"Radio group '{group[0].SuggestedName}' has inconsistent field requirements.");
            PdfFormFieldMetadata? metadata = group[0].SuggestedTooltip is null ? null
                : new PdfFormFieldMetadata { Tooltip = group[0].SuggestedTooltip };
            editor.AddRadioGroup(group[0].SuggestedName, group.Select(proposal =>
                new PdfRadioButtonOption(proposal.PageIndex, proposal.Bounds.Left,
                    proposal.Bounds.Bottom, proposal.Bounds.Width, proposal.Bounds.Height,
                    proposal.SuggestedValue!)), fieldMetadata: metadata,
                fieldOptions: new PdfFormFieldOptions
                {
                    ReadOnly = group[0].SuggestedReadOnly,
                    Required = group[0].SuggestedRequired
                });
        }
        return editor.Build();
    }

    private static byte[] CopySource(PdfDocument document) => document.Source.ToArray();

    private static bool CanAuthor(PdfFormFieldProposal proposal) => proposal.Kind switch
    {
        PdfRecognizedFieldKind.Text or PdfRecognizedFieldKind.CheckBox
            or PdfRecognizedFieldKind.Signature => true,
        PdfRecognizedFieldKind.RadioButton => proposal.SuggestedValue is not null,
        PdfRecognizedFieldKind.DropDown or PdfRecognizedFieldKind.EditableComboBox
            or PdfRecognizedFieldKind.ListBox => proposal.SuggestedOptions.Count > 0,
        _ => false
    };

    private PdfFormRecognitionReview Change(string id,
        Func<PdfFormFieldProposal, PdfFormFieldProposal> change)
    {
        ArgumentNullException.ThrowIfNull(id);
        int index = Array.FindIndex(_proposals, item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (index < 0) throw new KeyNotFoundException($"Form proposal '{id}' was not found.");
        PdfFormFieldProposal[] changed = (PdfFormFieldProposal[])_proposals.Clone();
        changed[index] = change(changed[index]);
        return new PdfFormRecognitionReview(changed);
    }
}
