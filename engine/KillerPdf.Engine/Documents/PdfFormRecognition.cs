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
        string? suggestedValue = null)
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
        if (suggestedValue is not null && kind != PdfRecognizedFieldKind.EditableComboBox
            && !options.Contains(suggestedValue, StringComparer.Ordinal))
            throw new ArgumentException("The suggested value must match a suggested choice.", nameof(suggestedValue));
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
    /// <summary>Gets the proposed selected value for a choice field.</summary>
    public string? SuggestedValue { get; }
    /// <summary>Gets the current review state.</summary>
    public PdfFormProposalStatus Status { get; }

    internal PdfFormFieldProposal Review(PdfFormProposalStatus status, string? name = null,
        PdfRecognizedFieldKind? kind = null, PdfContentBounds? bounds = null,
        string? tooltip = null, IEnumerable<string>? options = null, string? value = null) =>
        new(Id, PageIndex, bounds ?? Bounds, kind ?? Kind, Confidence, name ?? SuggestedName,
            status, tooltip ?? SuggestedTooltip, options ?? SuggestedOptions, value ?? SuggestedValue);
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
        string? tooltip = null, IEnumerable<string>? options = null, string? value = null) =>
        Change(id, item => item.Review(
            PdfFormProposalStatus.Accepted, name, kind, bounds, tooltip, options, value));

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
        var editor = new PdfIncrementalPageEditor(document);
        foreach (PdfFormFieldProposal proposal in Accepted)
        {
            PdfContentBounds bounds = proposal.Bounds;
            PdfFormFieldMetadata? metadata = proposal.SuggestedTooltip is null ? null
                : new PdfFormFieldMetadata { Tooltip = proposal.SuggestedTooltip };
            switch (proposal.Kind)
            {
                case PdfRecognizedFieldKind.Text:
                    editor.AddTextField(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        fieldMetadata: metadata);
                    break;
                case PdfRecognizedFieldKind.CheckBox:
                    editor.AddCheckBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        fieldMetadata: metadata);
                    break;
                case PdfRecognizedFieldKind.DropDown:
                case PdfRecognizedFieldKind.EditableComboBox:
                    editor.AddComboBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        proposal.SuggestedOptions, proposal.SuggestedValue,
                        editable: proposal.Kind == PdfRecognizedFieldKind.EditableComboBox,
                        fieldMetadata: metadata);
                    break;
                case PdfRecognizedFieldKind.ListBox:
                    editor.AddListBox(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        proposal.SuggestedOptions, proposal.SuggestedValue,
                        fieldMetadata: metadata);
                    break;
                case PdfRecognizedFieldKind.Signature:
                    editor.AddSignatureField(proposal.PageIndex, proposal.SuggestedName,
                        bounds.Left, bounds.Bottom, bounds.Width, bounds.Height,
                        fieldMetadata: metadata);
                    break;
            }
        }
        return editor.Build();
    }

    private static byte[] CopySource(PdfDocument document) => document.Source.ToArray();

    private static bool CanAuthor(PdfFormFieldProposal proposal) => proposal.Kind switch
    {
        PdfRecognizedFieldKind.Text or PdfRecognizedFieldKind.CheckBox
            or PdfRecognizedFieldKind.Signature => true,
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
