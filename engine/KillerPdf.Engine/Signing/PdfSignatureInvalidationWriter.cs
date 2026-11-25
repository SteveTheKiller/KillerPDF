using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Signing;

/// <summary>Clears signature values that would become invalid after a document rewrite.</summary>
public static class PdfSignatureInvalidationWriter
{
    private static readonly PdfName AcroFormName = Name("AcroForm");
    private static readonly PdfName FieldsName = Name("Fields");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName FieldTypeName = Name("FT");
    private static readonly PdfName SignatureName = Name("Sig");
    private static readonly PdfName ValueName = Name("V");
    private static readonly PdfName PermissionsName = Name("Perms");
    private static readonly PdfName SubtypeName = Name("Subtype");
    private static readonly PdfName WidgetName = Name("Widget");
    private static readonly PdfName StampName = Name("Stamp");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName FlagsName = Name("F");
    private static readonly PdfName AppearanceName = Name("AP");
    private static readonly PdfName NormalName = Name("N");
    private static readonly PdfName TitleName = Name("T");

    // Field and signature keys a stamp made from a signature widget leaves behind.
    private static readonly PdfName[] FieldOnlyNames =
        [.. new[] { "FT", "T", "TU", "TM", "V", "Ff", "Lock", "SV", "Parent", "Kids", "DA", "MK" }.Select(Name)];

    /// <summary>
    /// Appends a revision that removes every signature value and catalog certification permission.
    /// A signed field with a visible widget becomes a stamp annotation with the same appearance, so the
    /// signer's name and date stay on the page; viewers such as Acrobat draw an unsigned signature field
    /// as an empty box (#505). An invisible signature keeps its empty field for later re-signing.
    /// Returns the original bytes when the document contains no invalidatable signature state.
    /// </summary>
    public static byte[] ClearSignatureValues(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        bool changed = RewriteSignatureValues(document,
            (number, value) => update.ReplaceObject(number, value), tree);
        return changed ? update.Build() : document.Source.ToArray();
    }

    internal static bool RewriteSignatureValues(
        PdfDocument document, Action<int, PdfObject> replaceObject, PdfPageTree? parsedTree = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(replaceObject);
        PdfPageTree tree = parsedTree ?? PdfPageTree.Read(document);
        bool changed = false;
        var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
        if (catalogEntries.Remove(PermissionsName)) changed = true;

        if (tree.Catalog.TryGetValue(AcroFormName, out PdfObject? formValue))
        {
            Resolved formResolved = Resolve(document, formValue, "The catalog /AcroForm value");
            if (formResolved.Value is not PdfDictionary form)
                throw new InvalidOperationException("The catalog /AcroForm value is not a dictionary.");
            PdfDictionary rewrittenForm = form;
            if (form.TryGetValue(FieldsName, out PdfObject? fieldsValue))
            {
                Resolved fieldsResolved = Resolve(document, fieldsValue, "The AcroForm /Fields value");
                if (fieldsResolved.Value is not PdfArray fields)
                    throw new InvalidOperationException("The AcroForm /Fields value is not an array.");
                PdfArray rewrittenFields = RewriteArray(fields, null, 0);
                if (!ReferenceEquals(rewrittenFields, fields))
                {
                    changed = true;
                    PdfObject replacementFields = rewrittenFields;
                    if (fieldsResolved.Reference is PdfIndirectReference fieldsReference)
                    {
                        replaceObject(fieldsReference.ObjectNumber, rewrittenFields);
                        replacementFields = fieldsValue;
                    }
                    rewrittenForm = Replace(form, FieldsName, replacementFields);
                }
            }
            if (!ReferenceEquals(rewrittenForm, form))
            {
                if (formResolved.Reference is PdfIndirectReference formReference)
                    replaceObject(formReference.ObjectNumber, rewrittenForm);
                else
                    catalogEntries[AcroFormName] = rewrittenForm;
            }
        }

        if (!changed) return false;
        replaceObject(tree.CatalogReference.ObjectNumber,
            new PdfDictionary(catalogEntries));
        return true;

        PdfArray RewriteArray(PdfArray fields, PdfName? inheritedType, int depth)
        {
            if (depth >= 256)
                throw new InvalidOperationException("The AcroForm field tree is too deeply nested.");
            bool arrayChanged = false;
            var values = new List<PdfObject>(fields.Count);
            for (int index = 0; index < fields.Count; index++)
            {
                PdfObject original = fields[index];
                Resolved resolved = Resolve(document, original, "An AcroForm field");
                if (resolved.Value is not PdfDictionary field)
                    throw new InvalidOperationException("An AcroForm field is not a dictionary.");
                PdfName? type = inheritedType;
                if (field.TryGetValue(FieldTypeName, out PdfObject? typeValue))
                    type = Resolve(document, typeValue, "An AcroForm field /FT value").Value
                        as PdfName ?? throw new InvalidOperationException(
                            "An AcroForm field /FT value is not a name.");
                bool signed = type?.Equals(SignatureName) == true && field.ContainsKey(ValueName);
                // A signed field whose widgets show on the page leaves the field tree; its widgets stay in
                // the pages' annotation lists as stamps with the same appearance. (#505)
                if (signed && resolved.Reference is not null && ConvertToStamps(field, resolved.Reference))
                {
                    changed = true;
                    arrayChanged = true;
                    continue;
                }
                var entries = field.ToDictionary(entry => entry.Key, entry => entry.Value);
                bool fieldChanged = signed && entries.Remove(ValueName);
                if (field.TryGetValue(KidsName, out PdfObject? kidsValue))
                {
                    Resolved kidsResolved = Resolve(document, kidsValue, "An AcroForm field /Kids value");
                    if (kidsResolved.Value is not PdfArray kids)
                        throw new InvalidOperationException("An AcroForm field /Kids value is not an array.");
                    PdfArray rewrittenKids = RewriteArray(kids, type, depth + 1);
                    if (!ReferenceEquals(rewrittenKids, kids))
                    {
                        PdfObject replacementKids = rewrittenKids;
                        if (kidsResolved.Reference is PdfIndirectReference kidsReference)
                        {
                            replaceObject(kidsReference.ObjectNumber, rewrittenKids);
                            replacementKids = kidsValue;
                        }
                        entries[KidsName] = replacementKids;
                        fieldChanged = true;
                    }
                }
                if (!fieldChanged) { values.Add(original); continue; }
                changed = true;
                var rewritten = new PdfDictionary(entries);
                if (resolved.Reference is PdfIndirectReference reference)
                {
                    replaceObject(reference.ObjectNumber, rewritten);
                    values.Add(original);
                }
                else
                    values.Add(rewritten);
                arrayChanged = true;
            }
            return arrayChanged ? new PdfArray(values) : fields;
        }

        // The field is its own widget, or its kids are its widgets. Converts them when at least one
        // shows on the page; otherwise the field stays as an empty field.
        bool ConvertToStamps(PdfDictionary field, PdfIndirectReference reference)
        {
            if (IsWidget(field))
            {
                if (!ShowsOnPage(field)) return false;
                replaceObject(reference.ObjectNumber, Stamp(field));
                return true;
            }
            if (!field.TryGetValue(KidsName, out PdfObject? kidsValue)
                || Resolve(document, kidsValue, "An AcroForm field /Kids value").Value is not PdfArray kids)
                return false;
            var widgets = new List<(PdfIndirectReference Reference, PdfDictionary Widget)>();
            foreach (PdfObject kid in kids)
            {
                Resolved kidResolved = Resolve(document, kid, "A signature field widget");
                if (kidResolved.Reference is not PdfIndirectReference kidReference
                    || kidResolved.Value is not PdfDictionary widget
                    || !IsWidget(widget) || widget.ContainsKey(TitleName))
                    return false;
                widgets.Add((kidReference, widget));
            }
            if (!widgets.Any(entry => ShowsOnPage(entry.Widget))) return false;
            foreach (var (kidReference, widget) in widgets)
                replaceObject(kidReference.ObjectNumber, Stamp(widget));
            return true;
        }

        bool IsWidget(PdfDictionary annotation) =>
            annotation.TryGetValue(SubtypeName, out PdfObject? subtype)
            && Resolve(document, subtype, "An annotation /Subtype value").Value is PdfName name
            && name.Equals(WidgetName);

        // A nonempty /Rect, not hidden, and a normal appearance to draw.
        bool ShowsOnPage(PdfDictionary widget)
        {
            if (!widget.TryGetValue(RectName, out PdfObject? rectValue)
                || Resolve(document, rectValue, "A widget /Rect value").Value is not PdfArray rect
                || rect.Count != 4)
                return false;
            double[] corners = new double[4];
            for (int corner = 0; corner < 4; corner++)
            {
                if (Number(Resolve(document, rect[corner], "A widget /Rect value").Value) is not double value)
                    return false;
                corners[corner] = value;
            }
            if (corners[0] == corners[2] || corners[1] == corners[3]) return false;
            if (widget.TryGetValue(FlagsName, out PdfObject? flagsValue)
                && Resolve(document, flagsValue, "A widget /F value").Value is PdfInteger flags
                && (flags.Value & (2 | 32)) != 0)
                return false;
            return widget.TryGetValue(AppearanceName, out PdfObject? appearanceValue)
                && Resolve(document, appearanceValue, "A widget /AP value").Value is PdfDictionary appearances
                && appearances.ContainsKey(NormalName);
        }
    }

    private static PdfDictionary Stamp(PdfDictionary widget) =>
        new(widget.Where(entry => !entry.Key.Equals(SubtypeName) && !FieldOnlyNames.Contains(entry.Key))
            .Append(new KeyValuePair<PdfName, PdfObject>(SubtypeName, StampName)));

    private static double? Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => null
    };

    private static PdfDictionary Replace(
        PdfDictionary source, PdfName name, PdfObject value) =>
        new(source.Where(entry => !entry.Key.Equals(name))
            .Append(new KeyValuePair<PdfName, PdfObject>(name, value)));

    private static Resolved Resolve(PdfDocument document, PdfObject value, string description)
    {
        PdfIndirectReference? reference = null;
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference current; depth++)
        {
            if (depth >= 64 || !visited.Add((current.ObjectNumber, current.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            reference = current;
            value = document.Resolve(current);
        }
        return new Resolved(value, reference);
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
    private sealed record Resolved(PdfObject Value, PdfIndirectReference? Reference);
}
