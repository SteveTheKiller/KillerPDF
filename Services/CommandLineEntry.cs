#nullable enable
using System;
using System.IO;

namespace KillerFamily.CommandLine
{
    /// <summary>Recognizes app commands without taking filenames or normal launches away from the UI.
    /// <c>argumentCommands</c> names the commands that cannot run alone: with exactly one argument,
    /// one of those names, and a file or folder of that name on disk, the launch stays with the UI.</summary>
    public static class CommandLineEntry
    {
        public static string[]? GetArguments(string[] arguments, string[] commands, string commandPrefix = "",
            string[]? argumentCommands = null)
        {
            if (arguments.Length == 0) return null;
            if (arguments[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
            {
                var legacy = new string[arguments.Length - 1];
                Array.Copy(arguments, 1, legacy, 0, legacy.Length);
                return legacy;
            }
            string first = arguments[0].ToLowerInvariant();
            if (first == "help" || first == "--help" || first == "-h" || first == "/?")
            {
                var help = (string[])arguments.Clone();
                help[0] = "--help";
                return help;
            }
            // A lone verb that needs an argument cannot be a command, and a file or folder in the
            // current folder carries its name, so it is the document the user opened.
            if (arguments.Length == 1 && argumentCommands != null
                && Array.Exists(argumentCommands, name => first.Equals(name, StringComparison.OrdinalIgnoreCase))
                && (File.Exists(arguments[0]) || Directory.Exists(arguments[0])))
                return null;
            foreach (string command in commands)
                if (first.Equals(command, StringComparison.OrdinalIgnoreCase))
                {
                    var routed = (string[])arguments.Clone();
                    routed[0] = commandPrefix + command;
                    return routed;
                }
            return null;
        }
    }
}
