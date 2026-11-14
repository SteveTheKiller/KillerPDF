using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows.Markup;
using System.Xml;

namespace KillerLauncher
{
    internal static class LauncherStrings
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Tables = Load();

        internal static string Get(string key)
        {
            string locale = CurrentLocale();
            if (Tables[locale].TryGetValue(key, out string value)) return value;
            return Tables["en-US"][key];
        }

        internal static string Format(string key, params object[] values) =>
            string.Format(CultureInfo.CurrentCulture, Get(key), values);

        internal static string CurrentLocale() => ResolveLocale(CultureInfo.CurrentUICulture);

        internal static string ResolveLocale(CultureInfo culture)
        {
            // Keep Chinese script selection before the general language fallback.
            string name = culture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase))
                    return "zh-TW";
                return "zh-CN";
            }
            if (Tables.ContainsKey(name)) return name;
            string language = culture.TwoLetterISOLanguageName;
            foreach (string candidate in Tables.Keys)
                if (candidate.Equals(language, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            return "en-US";
        }

        // Each language is its own embedded Strings.<locale>.xml (#409), so a translation can be
        // reviewed and compared on its own. A value writes \n where it breaks a line, which keeps
        // every string on one line of the file and the files line up key for key.
        private static Dictionary<string, Dictionary<string, string>> Load()
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string resource in assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith("KillerLauncher.Strings.", StringComparison.Ordinal)
                    || !resource.EndsWith(".xml", StringComparison.Ordinal)) continue;
                using (var stream = assembly.GetManifestResourceStream(resource))
                {
                    var document = new XmlDocument { XmlResolver = null };
                    document.Load(stream!);
                    XmlElement locale = document.DocumentElement!;
                    var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (XmlElement entry in locale.SelectNodes("string")!)
                        strings.Add(entry.GetAttribute("key"), Unescape(entry.InnerText));
                    result.Add(locale.GetAttribute("name"), strings);
                }
            }
            return result;
        }

        internal static string Unescape(string value) => value.Replace("\\n", "\n");
    }

    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension(string key) { Key = key; }
        public string Key { get; }
        public override object ProvideValue(IServiceProvider serviceProvider) => LauncherStrings.Get(Key);
    }
}
