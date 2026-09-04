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
        string? suggestedTooltip = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A proposal ID is required.", nameof(id));
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentException("Field bounds must have positive dimensions.", nameof(bounds));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (string.IsNullOrWhiteSpace(suggestedName)) throw new ArgumentException("A suggested field name is required.", nameof(suggestedName));
        if (suggestedTooltip is not null && string.IsNullOrWhiteSpace(suggestedTooltip))
            throw new ArgumentException("A suggested tooltip cannot be empty.", nameof(suggestedTooltip));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Id = id;
        PageIndex = pageIndex;
        Bounds = bounds;
        Kind = kind;
        Confidence = confidence;
        SuggestedName = suggestedName;
        SuggestedTooltip = suggestedTooltip;
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
    /// <summary>Gets the current review state.</summary>
    public PdfFormProposalStatus Status { get; }

    internal PdfFormFieldProposal Review(PdfFormProposalStatus status, string? name = null,
        PdfRecognizedFieldKind? kind = null, PdfContentBounds? bounds = null,
        string? tooltip = null) =>
        new(Id, PageIndex, bounds ?? Bounds, kind ?? Kind, Confidence, name ?? SuggestedName,
            status, tooltip ?? SuggestedTooltip);
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
        string? tooltip = null) =>
        Change(id, item => item.Review(
            PdfFormProposalStatus.Accepted, name, kind, bounds, tooltip));

    /// <summary>Returns a new review with a proposal rejected.</summary>
    public PdfFormRecognitionReview Reject(string id) =>
        Change(id, item => item.Review(PdfFormProposalStatus.Rejected));

    /// <summary>Creates accepted text, checkbox, and signature fields in an existing document.</summary>
    public byte[] ApplyAccepted(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsReadyToApply)
            throw new InvalidOperationException(
                "Every form proposal must be accepted or rejected before authoring.");
        PdfFormFieldProposal? unsupported = Accepted.FirstOrDefault(item =>
            item.Kind is not PdfRecognizedFieldKind.Text
                and not PdfRecognizedFieldKind.CheckBox
                and not PdfRecognizedFieldKind.Signature);
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
