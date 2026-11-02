using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;

namespace KillerPDF.Features;

/// <summary>What the command line tells a protected input, from the same answers the app reads. The
/// dispatcher asks once for the verb it is about to run, so no verb carries a gate of its own.</summary>
internal static class CliProtectionGate
{
    internal const string RestrictedContentMessage =
        "The owner of this PDF has set limits this output would not keep, so it takes the owner password. Give it with --password.";

    private static readonly string[] WindowSwitches = ["--crash-dialog-preview", "--startup-smoke"];

    internal static string Message(PdfAccess access) =>
        access.Role == PdfAccessRole.Refused ? access.Refusal ?? RestrictedContentMessage : RestrictedContentMessage;

    internal static string PrintRefusal(PdfAccess access) => access.Role == PdfAccessRole.Refused
        ? access.Refusal ?? PdfRestrictions.LockedMessage
        : "Printing is not allowed: the owner of this PDF has restricted printing. Give the owner password with --password.";

    /// <summary>The refusal for a verb, or null. A verb that keeps its input's encryption is a save, so
    /// it is not listed. A report is a text or data export when it leaves the app, as a saved file or
    /// as stdout that can be redirected, and reproduces what was written into the document: page text,
    /// comment bodies with their authors and subjects, form values, table cells, measurement values and
    /// notes, or attachment bytes. Comments, measurements and the recognize-fields report answer as
    /// data for that reason. A report that describes the file through its metadata, counts, findings,
    /// inks, layer names, signature state or the names of what it holds writes none of that, so
    /// document-info, search-text, signatures, layers, print-production, separations as a listing,
    /// preflight, accessibility, navigation-audit, compare-structure, portfolio and the attachment
    /// listing stay free.</summary>
    internal static string? VerbRefusal(string command, IReadOnlyList<string> positionals,
        IReadOnlyDictionary<string, string> options)
    {
        if (positionals.Count == 0) return null;
        options.TryGetValue("--password", out string? password);
        string input = positionals[0];
        switch (command.ToLowerInvariant())
        {
            case "--merge":
                return positionals.Take(positionals.Count - 1)
                    .Select(path => OutputRefusal(path, password, PdfOutput.Printable))
                    .FirstOrDefault(refusal => refusal is not null);
            case "--extract-pages" or "--split" or "--booklet" or "--nup" or "--flatten" or "--to-image"
                or "--ocr" or "--straighten" or "--data-merge":
                return OutputRefusal(input, password, PdfOutput.Printable);
            case "--separations":
                return positionals.Count > 1 ? OutputRefusal(input, password, PdfOutput.Printable) : null;
            case "--export":
                return OutputRefusal(input, password, options.TryGetValue("--layout", out string? layout)
                    && layout.Equals("images", StringComparison.OrdinalIgnoreCase) ? PdfOutput.Printable : PdfOutput.Data);
            case "--export-form-data" or "--scan-to-text" or "--comments" or "--measurements":
                return OutputRefusal(input, password, PdfOutput.Data);
            case "--recognize-fields":
                return positionals.Count == 1 ? OutputRefusal(input, password, PdfOutput.Data) : null;
            case "--attachments":
                return positionals.Count > 1 ? OutputRefusal(input, password, PdfOutput.Data) : null;
            case "--decrypt" or "--protect":
                return RemovalRefusal(input, password);
            default:
                return null;
        }
    }

    private static bool IsEncrypted(string path) =>
        PdfRestrictions.ReadEncryptionState(Path.GetFullPath(path)) == PdfEncryptionState.Encrypted;

    private static string? OutputRefusal(string path, string? password, PdfOutput output)
    {
        if (!IsEncrypted(path)) return null;
        PdfAccess access = PdfRestrictions.Read(Path.GetFullPath(path), password);
        return access.Allows(output) ? null : Message(access);
    }

    private static string? RemovalRefusal(string path, string? password)
    {
        if (!IsEncrypted(path)) return null;
        PdfAccess access = PdfRestrictions.Read(Path.GetFullPath(path), password);
        return access.MayRemoveProtection ? null : Message(access);
    }

    /// <summary>Opens a file for a report with the password given, or the empty user password when none
    /// was. Returns null with the refusal when the password opens nothing.</summary>
    internal static PdfDocument? OpenForReport(string path, string? password, out string? refusal)
    {
        refusal = null;
        PdfAccess access = PdfRestrictions.Open(File.ReadAllBytes(Path.GetFullPath(path)), password, out PdfDocument? document);
        if (document is not null) return document;
        refusal = Message(access);
        return null;
    }

    /// <summary>Opens a file for a command that rewrites it and keeps its protection, the way Reduce
    /// Size does: the document comes back with no encryption of its own, and <paramref name="protection"/>
    /// seals the result. Returns null with the refusal when the file does not allow the rewrite.</summary>
    internal static PdfDocument? OpenForRewrite(string path, string? password, out OpenedProtection protection,
        out string? refusal)
    {
        protection = OpenedProtection.None;
        refusal = null;
        string input = Path.GetFullPath(path);
        if (!IsEncrypted(input)) return PdfEngineIntegration.OpenForReading(File.ReadAllBytes(input));

        protection = OpenedProtection.Capture(input, password);
        if (!protection.Access.MayModify || !protection.CanSeal)
        {
            refusal = Message(protection.Access);
            return null;
        }
        string plain = Path.Combine(Path.GetTempPath(), "killerpdf_rewrite_" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            protection.WriteViewingCopy(input, plain);
            return PdfDocument.Open(File.ReadAllBytes(plain));
        }
        finally { File.Delete(plain); }
    }

    /// <summary>Puts a rewritten document back under the encryption its source was opened with.</summary>
    internal static byte[] SealRewrite(byte[] rewritten, OpenedProtection protection)
    {
        if (!protection.CanSeal) return rewritten;
        string plain = Path.Combine(Path.GetTempPath(), "killerpdf_rewrite_" + Guid.NewGuid().ToString("N") + ".pdf");
        string encrypted = plain + ".sealed";
        try
        {
            File.WriteAllBytes(plain, rewritten);
            protection.Seal(plain, encrypted);
            return File.ReadAllBytes(encrypted);
        }
        finally
        {
            File.Delete(plain);
            File.Delete(encrypted);
        }
    }

    internal const string EncryptedInputMessage =
        "This PDF is encrypted, so this command cannot change it. Make an unprotected copy with decrypt first, which takes the owner password when the file sets limits, and run the command on that copy.";

    /// <summary>What a failed command tells the user. The engine's wording for an encrypted input
    /// speaks to programmers, so it becomes what the user can do about it. Any other failure keeps
    /// its own words, and so does one on a plain file.</summary>
    internal static string FailureMessage(string engineMessage, IReadOnlyList<string> positionals)
    {
        ArgumentNullException.ThrowIfNull(engineMessage);
        ArgumentNullException.ThrowIfNull(positionals);
        bool namesEncryption = engineMessage.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
            || engineMessage.Contains("FlateDecode", StringComparison.Ordinal);
        return namesEncryption && positionals.Any(path => File.Exists(path) && IsEncrypted(path))
            ? EncryptedInputMessage : engineMessage;
    }

    /// <summary>True when the arguments hold a double-dash switch that nothing reads. A file by that
    /// name still opens, and every switch the window reads is left to it. A bare word stays a file
    /// path, because nothing can tell a mistyped verb from a name.</summary>
    internal static bool IsUnknownSwitch(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(argument => Same(argument, RenderFailureRecovery.RecoverySwitch))) return false;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal)) continue;
            if (Same(argument, "--lang-file")) { index++; continue; }
            if (WindowSwitches.Any(known => Same(argument, known)) || File.Exists(argument)) continue;
            return true;
        }
        return false;

        static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How a batch render reads one file. One password spans a whole tree, so a file it does
    /// not open is read as if none was given.</summary>
    internal static (string? Refusal, string? ReadWith) BatchRender(string path, string? password)
    {
        PdfAccess access = PdfRestrictions.Read(path, password);
        bool opened = access.Role != PdfAccessRole.Refused;
        if (!opened && !string.IsNullOrEmpty(password)) access = PdfRestrictions.Read(path, null);
        string? refusal = access.Allows(PdfOutput.Printable) ? null : "restricted: " + Message(access);
        return (refusal, opened && access.Role != PdfAccessRole.Unencrypted ? password : null);
    }

    /// <summary>The bytes a macro's steps read, or a refusal. The steps open what they are given with
    /// no password, so a protected input reaches them decrypted once its outputs are allowed. A macro
    /// writes new PDFs and exports, so the stricter of the two output classes decides.</summary>
    internal static byte[] MacroInput(string path, string? password, out string? refusal)
    {
        refusal = null;
        byte[] source = File.ReadAllBytes(path);
        PdfAccess access = PdfRestrictions.Open(source, password, out PdfDocument? document);
        if (access.Role == PdfAccessRole.Unencrypted) return source;
        if (document is null || !access.Allows(PdfOutput.Printable))
        {
            refusal = Message(access);
            return [];
        }
        return PdfDocumentWriter.Write(document, new PdfDocumentWriteOptions { RemoveEncryption = true });
    }

    /// <summary>Why batch resave passes over a file, or null when it may go on to open it.</summary>
    internal static string? BatchResaveSkip(string path) => PdfRestrictions.ReadEncryptionState(path) switch
    {
        PdfEncryptionState.Encrypted => "encrypted - batch mode does not strip encryption",
        PdfEncryptionState.Unreadable => "unreadable: the file could not be read",
        _ => null
    };
}
