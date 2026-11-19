using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class DataMergeDialog : Window
{
    private readonly PdfDocument _template;
    private readonly string[] _sources;
    private readonly string[] _targets;
    private readonly ListBox _list = new();
    private readonly CheckBox _flatten;
    private List<PdfDataMergeFieldMapping> _mappings = [];
    private List<PdfDataMergeImageMapping> _images = [];
    private string _name = L("Str_DocumentTools_DataMerge");
    private string _output = "";
    private string? _conditionField, _conditionValue;
    private PdfMissingMergeValueBehavior _missing = PdfMissingMergeValueBehavior.Error;
    internal PdfDataMergeProfile? Profile { get; private set; }
    internal PdfDataMergeOutputMode OutputMode => _flatten.IsChecked == true
        ? PdfDataMergeOutputMode.Flattened : PdfDataMergeOutputMode.Editable;

    internal DataMergeDialog(Window owner, PdfDocument template,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records, string outputName)
    {
        _template = template;
        _sources = records.SelectMany(record => record.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        _targets = PdfFormDataExporter.Export(template, includeAnnotations: false).Fields.Select(item => item.Name).ToArray();
        try { _mappings = [.. PdfDataMergeWorkflow.CreatePlan(template, records, outputName).Profile.Mappings]; }
        catch (InvalidOperationException) { }
        Title = "KillerPDF - " + L("Str_DocumentTools_DataMerge");
        Width = 850; Height = 560; MinWidth = 650; MinHeight = 380;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        // The mapping list's actions are icons in its header; Profile settings opens its own window, so it keeps its
        // words, on the footer's left beside the output choice.
        DockPanel header = DialogParts.Header(L("Str_DocumentTools_DataMerge"),
            Icon("Str_Merge_AddField", 0xE710, () => AddField()),
            Icon("Str_Merge_AddImage", 0xEB9F, () => AddImage()),
            Icon("Str_Redact_Properties", 0xE713, EditSelected),
            Icon("Str_Portfolio_Remove", 0xE711, RemoveSelected),
            Icon("Str_Merge_LoadProfile", 0xE896, ImportProfile),
            Icon("Str_Merge_SaveProfile", 0xE898, ExportProfile));
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        _flatten = UiKit.CheckBox(L("Str_FormTools_Flatten"));
        Button profileSettings = DialogParts.LabeledAction("Str_Merge_Profile", 0xE713, (_, _) => Safely(EditProfile));
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var run = UiKit.Make(L("Str_Btn_Run"), accent: true);
        run.IsDefault = true;
        run.Click += (_, _) => Safely(() =>
        {
            var profile = BuildProfile();
            _ = PdfDataMergeWorkflow.CreatePlan(template, records, outputName, profile);
            Profile = profile; DialogResult = true;
        });
        DockPanel bottom = DialogParts.Footer([cancel, run], _flatten, profileSettings);
        DockPanel.SetDock(bottom, Dock.Bottom); body.Children.Add(bottom);
        System.Windows.Automation.AutomationProperties.SetName(_list, L("Str_DocumentTools_DataMerge"));
        body.Children.Add(DialogParts.Well(_list)); Refresh();
        Content = DialogFrame.Frame(this, owner, Title, Close, body);

        Button Icon(string key, int glyph, Action action) => DialogParts.IconAction(key, glyph, (_, _) => Safely(action));
    }

    private void Refresh(int selected = 0)
    {
        _list.ItemsSource = _mappings.Select(item => item.SourceField + " > " + item.TargetField)
            .Concat(_images.Select(item => item.SourceField + " > " + L("Str_Repair_Figure") + " ("
                + L("Str_AutoBookmarks_Page") + " " + (item.PageIndex + 1) + ")")).ToArray();
        _list.SelectedIndex = _list.Items.Count == 0 ? -1 : Math.Clamp(selected, 0, _list.Items.Count - 1);
    }

    private int Select(string titleKey, IEnumerable<string> labels, int selected = 0)
    {
        var dialog = new DocumentListDialog(this, L(titleKey), labels);
        dialog.SelectedIndex = selected;
        return dialog.ShowDialog() == true ? dialog.SelectedIndex : -1;
    }

    private void AddField()
    {
        int source = Select("Str_Merge_Source", _sources); if (source < 0) return;
        int target = Select("Str_Merge_Target", _targets.Concat([L("Str_Merge_Placeholder")]));
        if (target < 0) return;
        string name;
        var kind = PdfDataMergeTargetKind.FormField;
        if (target == _targets.Length)
        {
            var input = new DocumentValuesDialog(this, L("Str_Merge_Placeholder"), (L("Str_Attachment_Name"), ""));
            if (input.ShowDialog() != true) return;
            name = input.Values[0]; kind = PdfDataMergeTargetKind.TextPlaceholder;
        }
        else name = _targets[target];
        var mapping = EditMapping(new(_sources[source], name, TargetKind: kind));
        if (mapping is null) return;
        _mappings.Add(mapping); Refresh(_mappings.Count - 1);
    }

    private PdfDataMergeFieldMapping? EditMapping(PdfDataMergeFieldMapping mapping)
    {
        int kind = Select("Str_Merge_ValueKind", new[] { L("Str_Lbl_Text"), L("Str_Merge_Number"), L("Str_Merge_Date") }, (int)mapping.ValueKind);
        if (kind < 0) return null;
        var input = new DocumentValuesDialog(this, mapping.SourceField + " > " + mapping.TargetField,
            (L("Str_Merge_Default"), mapping.DefaultValue ?? ""),
            (L("Str_Merge_Format"), mapping.Format ?? ""),
            (L("Str_Merge_Culture"), mapping.CultureName ?? ""),
            (L("Str_Merge_ConditionField"), mapping.IncludeWhenField ?? ""),
            (L("Str_Merge_ConditionValue"), mapping.IncludeWhenValue ?? ""));
        if (input.ShowDialog() != true) return null;
        string[] values = input.Values;
        return mapping with { ValueKind = (PdfDataMergeValueKind)kind, DefaultValue = Null(values[0]),
            Format = Null(values[1]), CultureName = Null(values[2]), IncludeWhenField = Null(values[3]), IncludeWhenValue = Null(values[4]) };
    }

    private void AddImage()
    {
        int source = Select("Str_Merge_Source", _sources); if (source < 0) return;
        var mapping = EditImage(new(_sources[source], 0, 18, 18, 100, 100));
        if (mapping is null) return;
        _images.Add(mapping); Refresh(_mappings.Count + _images.Count - 1);
    }

    private PdfDataMergeImageMapping? EditImage(PdfDataMergeImageMapping image)
    {
        string N(double number) => number.ToString(CultureInfo.CurrentCulture);
        var input = new DocumentValuesDialog(this, L("Str_Merge_AddImage"),
            (L("Str_AutoBookmarks_Page"), (image.PageIndex + 1).ToString(CultureInfo.CurrentCulture)),
            ("X (pt)", N(image.X)), ("Y (pt)", N(image.Y)),
            (L("Str_Sign_Width") + " (pt)", N(image.Width)), (L("Str_Sign_Height") + " (pt)", N(image.Height)),
            (L("Str_Merge_Default"), image.DefaultValue ?? ""),
            (L("Str_Merge_ConditionField"), image.IncludeWhenField ?? ""),
            (L("Str_Merge_ConditionValue"), image.IncludeWhenValue ?? ""));
        if (input.ShowDialog() != true) return null;
        string[] values = input.Values;
        double D(int index) => double.Parse(values[index], CultureInfo.CurrentCulture);
        int page = int.Parse(values[0], CultureInfo.CurrentCulture) - 1;
        var boxes = PdfPageBoxInformation.Read(_template);
        if (page < 0 || page >= boxes.Count) throw new ArgumentOutOfRangeException(nameof(page));
        return image with { PageIndex = page, X = D(1), Y = D(2), Width = D(3), Height = D(4),
            DefaultValue = Null(values[5]), IncludeWhenField = Null(values[6]), IncludeWhenValue = Null(values[7]) };
    }

    private void EditSelected()
    {
        int index = _list.SelectedIndex; if (index < 0) return;
        if (index < _mappings.Count)
        {
            var updated = EditMapping(_mappings[index]); if (updated is not null) _mappings[index] = updated;
        }
        else
        {
            var updated = EditImage(_images[index - _mappings.Count]);
            if (updated is not null) _images[index - _mappings.Count] = updated;
        }
        Refresh(index);
    }

    private void RemoveSelected()
    {
        int index = _list.SelectedIndex; if (index < 0) return;
        if (index < _mappings.Count) _mappings.RemoveAt(index); else _images.RemoveAt(index - _mappings.Count);
        Refresh(index);
    }

    private void EditProfile()
    {
        var input = new DocumentValuesDialog(this, L("Str_Merge_Profile"),
            (L("Str_Attachment_Name"), _name), (L("Str_Merge_OutputTemplate"), _output),
            (L("Str_Merge_ConditionField"), _conditionField ?? ""), (L("Str_Merge_ConditionValue"), _conditionValue ?? ""));
        if (input.ShowDialog() != true) return;
        _name = input.Values[0]; _output = input.Values[1];
        _conditionField = Null(input.Values[2]); _conditionValue = Null(input.Values[3]);
    }

    private PdfDataMergeProfile BuildProfile() => new(_name, _mappings,
        _output.Length == 0 ? "{{__KillerPDFOutputFileName}}" : _output, _missing,
        _images, _conditionField, _conditionValue);

    private void ImportProfile()
    {
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
            { Title = L("Str_Merge_Profile"), Filter = "JSON|*.json" };
        if (picker.ShowDialog(this) != true) return;
        var profile = PdfDataMergeProfile.FromJson(File.ReadAllText(picker.FileName));
        _name = profile.Name; _mappings = [.. profile.Mappings]; _images = [.. profile.ImageMappings];
        _output = profile.OutputFileNameTemplate == "{{__KillerPDFOutputFileName}}" ? "" : profile.OutputFileNameTemplate;
        _conditionField = profile.IncludeWhenField; _conditionValue = profile.IncludeWhenValue;
        _missing = profile.MissingValueBehavior; Refresh();
    }

    private void ExportProfile()
    {
        var profile = BuildProfile();
        var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
            { Title = L("Str_Merge_Profile"), Filter = "JSON|*.json", DefaultExt = "json", FileName = "merge-profile.json" };
        if (picker.ShowDialog(this) != true) return;
        PdfEngineIntegration.WriteExchangeFile(picker.FileName, System.Text.Encoding.UTF8.GetBytes(profile.ToJson(indented: true)));
    }

    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            string reason = error is DataMergeNoColumnsException ? L("Str_DataMerge_NoColumns") : error.Message;
            KillerDialog.Show(this, L("Str_DataMerge_Failed") + "\n" + reason);
        }
    }
    private static string? Null(string text) => text.Length == 0 ? null : text;
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
