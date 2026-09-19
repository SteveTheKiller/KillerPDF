using System.IO;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Controls
{
    // Live AcroForm recalculation. Calculated fields update from the engine's safe built-in
    // script subset when a value is committed, the way a paid viewer recalculates a total as
    // soon as a line item changes. Scripts outside the subset are left alone and never run.
    public partial class PdfViewer
    {
        private readonly Dictionary<string, TextBox> _formValueBoxes = [];
        private string? _formScriptProbePath;
        private bool _formHasScripts;
        private bool _formRecalculating;

        private void RegisterFormValueBox(string fieldName, TextBox box)
        {
            if (!string.IsNullOrEmpty(fieldName)) _formValueBoxes[fieldName] = box;
        }

        private void RecalculateFormFields()
        {
            if (_formRecalculating || _doc is null) return;
            try
            {
                PdfDocument engineDocument = EnsureEngineDocumentSession().Document;
                // Probing is keyed to the open file so a tab switch re-probes that document.
                if (!string.Equals(_formScriptProbePath, _currentFile, StringComparison.Ordinal))
                {
                    _formHasScripts = PdfFormCalculation.ReadScripts(engineDocument).Count > 0;
                    _formScriptProbePath = _currentFile;
                }
                if (!_formHasScripts) return;

                var values = new Dictionary<string, string>(
                    PdfFormCalculation.ReadValues(engineDocument), StringComparer.Ordinal);
                if (values.Count == 0) return;
                foreach ((string name, string text) in _formTextValues)
                    if (values.ContainsKey(name)) values[name] = text;
                foreach ((string name, string text) in _formChoiceValues)
                    if (values.ContainsKey(name)) values[name] = text;

                PdfFormCalculationReport report =
                    PdfFormCalculation.Evaluate(engineDocument, values);
                _formRecalculating = true;
                bool changed = false;
                foreach (PdfFormCalculationEntry entry in report.Fields)
                {
                    if (entry.Status != PdfFormScriptStatus.Evaluated) continue;
                    if (!values.TryGetValue(entry.FieldName, out string? previous)
                        || previous == entry.Value) continue;
                    _formTextValues[entry.FieldName] = entry.Value;
                    changed = true;
                    if (_formValueBoxes.TryGetValue(entry.FieldName, out TextBox? box)
                        && !box.IsKeyboardFocusWithin) box.Text = entry.Value;
                }
                if (changed) MarkDirty(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or KeyNotFoundException or ArgumentException or NotSupportedException
                or IOException)
            {
                // A form the safe subset cannot read stays editable; it simply does not recalculate.
                _formHasScripts = false;
            }
            finally
            {
                _formRecalculating = false;
            }
        }
    }
}
