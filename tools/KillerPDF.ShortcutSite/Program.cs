using System.Text.Json;
using System.Xml.Linq;
using KillerPDF;

Console.OutputEncoding = new System.Text.UTF8Encoding(false);

// Emits the app's shortcut data for the landing page without maintaining another binding table.
if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--check")
    throw new ArgumentException("Usage: KillerPDF.ShortcutSite REPOSITORY [--check]");
string root = Path.GetFullPath(args[0]);
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
var locales = new Dictionary<string, object>();
foreach (string path in Directory.GetFiles(Path.Combine(root, "Strings"), "*.xaml").Order(StringComparer.Ordinal))
{
    string name = Path.GetFileNameWithoutExtension(path);
    string locale = name switch { "zh-TW" => "zh", "zh-CN" => "zh-cn", _ => name.Split('-')[0] };
    var strings = XDocument.Load(path).Root!.Elements()
        .Where(element => element.Attribute(x + "Key") is not null)
        .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => element.Value);
    string Label(string key) => strings.TryGetValue(key, out string? value) ? value : throw new InvalidDataException(name + ": " + key);
    string Keys(string text)
    {
        foreach (var (token, key) in ShortcutTable.KeyTokens) text = text.Replace(token, Label(key));
        return text.Replace("%zin%", ShortcutTable.SiteZoomInChar(locale)).Replace("%zout%", "-");
    }
    locales[locale] = new
    {
        groups = ShortcutTable.KsGroups.Select(group => new { id = group.Cat.ToLowerInvariant(), title = Label(group.TitleKey), right = group.Right }),
        rows = ShortcutTable.KsAll.Select(binding => new { keys = Keys(binding.Keys), label = Label(binding.LabelKey), category = binding.Cat.ToLowerInvariant() }),
        labels = ShortcutTable.KsAll.SelectMany(binding => binding.Caps.Select(cap => cap.LabelKey.Length > 0 ? cap.LabelKey : binding.LabelKey))
            .Concat(ShortcutTable.KsGroups.Select(group => group.TitleKey)).Distinct().ToDictionary(key => key, Label)
    };
}
var map = ShortcutTable.BuildMap().ToDictionary(layer => layer.Key.ToString().ToLowerInvariant(),
    layer => layer.Value.ToDictionary(cap => cap.Key, cap => new[] { cap.Value.Cat.ToLowerInvariant(), cap.Value.Label }));
string output = "window.KillerPDFShortcuts = " + JsonSerializer.Serialize(new { locales, map }, new JsonSerializerOptions
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("<", "\\u003c") + ";\n";
if (output.IndexOfAny(['\u2013', '\u2014']) >= 0)
    throw new InvalidDataException("Shortcut text contains a prohibited dash character.");
if (args.Length == 2)
{
    string current = File.ReadAllText(Path.Combine(root, "pdf-landing", "shortcuts.generated.js")).Replace("\r\n", "\n");
    if (current != output) throw new InvalidDataException("Landing shortcuts are out of date. Regenerate them from the app table.");
    Console.WriteLine($"PASS landing shortcut data matches the app in all {locales.Count} languages.");
}
else Console.Write(output);
