using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KillerPDF.Features
{
    // Passwords given as options can be read by any program running as the same user and are kept in
    // process-creation logs. With --secrets-stdin, a caller such as KillerMCP sends them on standard input
    // as one JSON object instead, each named like the option it replaces, without the leading dashes.
    internal static class CliSecrets
    {
        internal const string Option = "--secrets-stdin";
        private static readonly string[] Names = ["password", "user-password", "owner-password", "pfx-password"];

        // A warning naming each password given as a command-line option, or null when there is none. Call it
        // before Read, which adds the standard-input secrets under the same option names.
        internal static string? CommandLineWarning(IReadOnlyDictionary<string, string> options)
        {
            string[] given = [.. Names.Select(name => "--" + name).Where(options.ContainsKey)];
            return given.Length == 0
                ? null
                : "Warning: " + string.Join(", ", given) + " on the command line can be read by other programs and is kept in shell history. Use " + Option + " instead.";
        }

        // Adds the secrets to the parsed options. Returns a usage message when the input is not one JSON
        // object of known secrets with string values, or null when it was read.
        internal static string? Read(TextReader input, IDictionary<string, string> options)
        {
            options.Remove(Option);
            string usage = Option + " expects one JSON object on standard input with string values for " + string.Join(", ", Names) + ".";
            JsonDocument document;
            try { document = JsonDocument.Parse(input.ReadToEnd()); }
            catch (JsonException) { return usage; }
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) return usage;
                var secrets = new List<KeyValuePair<string, string>>();
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!Names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String) return usage;
                    secrets.Add(new KeyValuePair<string, string>("--" + property.Name.ToLowerInvariant(), property.Value.GetString()!));
                }
                foreach (var secret in secrets) options[secret.Key] = secret.Value;
            }
            return null;
        }
    }
}
