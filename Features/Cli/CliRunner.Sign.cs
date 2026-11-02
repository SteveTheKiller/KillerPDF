using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Signing;
using KillerPDF.Services;
using KillerPDF.Services.Signing;

namespace KillerPDF.Features
{
    // Sign Document from the command line: an approval signature from a .pfx file or a certificate
    // in the Windows store, with an optional trusted RFC 3161 timestamp and visible appearance.
    internal static partial class CliRunner
    {
        private const string DefaultSignTimestampServer = "http://timestamp.digicert.com";
        private const string DefaultSignAppearanceText = "Digitally signed by {name}\nDate: {date}\nReason: {reason}\nLocation: {location}";

        private const string SignUsage =
            "Usage: KillerPDF.exe --sign <in.pdf> <out.pdf> (--pfx <file.pfx> [--pfx-password <p>] | --cert <thumbprint or name>)\n"
            + "         [--reason <text>] [--location <text>] [--contact <text>]\n"
            + "         [--timestamp] [--timestamp-server <url>]\n"
            + "         [--appearance <page>[:bottom-left|bottom-right|top-left|top-right]] [--appearance-text <text>]\n"
            + "         [--width <pt>] [--height <pt>] [--font-size <pt>]\n"
            + "  --cert picks a signing certificate from your Windows personal store by thumbprint or name.\n"
            + "  --timestamp adds a trusted timestamp (default server " + DefaultSignTimestampServer + ").\n"
            + "  --appearance draws a visible signature (default 240 x 84 pt, 10 pt text, bottom left); the text\n"
            + "  can use {name}, {date}, {reason} and {location}, and \\n starts a new line.";

        // ============================================================
        // --sign <in.pdf> <out.pdf> ...
        // ============================================================
        private static int CliSign(List<string> pos, Dictionary<string, string> options, TextWriter con)
        {
            if (pos.Count != 2) { con.WriteLine(SignUsage); return 2; }
            if (!CliInspectEditInput(pos[0], pos[1], out string source, out string output, out int pageCount, con)) return 2;

            bool fromFile = options.TryGetValue("--pfx", out string? pfx);
            bool fromStore = options.TryGetValue("--cert", out string? wanted);
            if (fromFile == fromStore) { con.WriteLine("Name one certificate with --pfx or --cert."); con.WriteLine(SignUsage); return 2; }

            X509Certificate2 certificate;
            if (fromFile)
            {
                string full = Path.GetFullPath(pfx!);
                if (!File.Exists(full)) { con.WriteLine($"Certificate file not found: {full}"); return 2; }
                options.TryGetValue("--pfx-password", out string? pfxPassword);
                try { certificate = new PfxFileCertificateProvider(full, pfxPassword ?? string.Empty).GetCertificate(); }
                catch (CryptographicException)
                {
                    con.WriteLine("Could not open the certificate file: the password is wrong or it is not a .pfx or .p12 file.");
                    return 2;
                }
            }
            else
            {
                string key = (wanted ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
                X509Certificate2[] matches = [.. WindowsCertificateStore.ListSigningCertificates().Where(cert =>
                    string.Equals(cert.Thumbprint, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(new StoreCertificateProvider(cert).DisplayName, wanted, StringComparison.OrdinalIgnoreCase))];
                if (matches.Length != 1)
                {
                    con.WriteLine(matches.Length == 0
                        ? $"No signing certificate in your personal store matches \"{wanted}\"."
                        : $"{matches.Length} signing certificates match \"{wanted}\"; use the thumbprint.");
                    foreach (X509Certificate2 cert in WindowsCertificateStore.ListSigningCertificates())
                        con.WriteLine($"  {cert.Thumbprint}  {new StoreCertificateProvider(cert).DisplayName}  expires {cert.NotAfter:yyyy-MM-dd}");
                    return 2;
                }
                certificate = matches[0];
            }
            using X509Certificate2? importedCertificate = fromFile ? certificate : null;
            if (!certificate.HasPrivateKey) { con.WriteLine("The certificate has no private key, so it cannot sign."); return 2; }

            string? timestampServer = null;
            if (options.ContainsKey("--timestamp") || options.ContainsKey("--timestamp-server"))
            {
                string server = options.TryGetValue("--timestamp-server", out string? named) && named.Trim().Length > 0
                    ? named.Trim() : DefaultSignTimestampServer;
                if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? serverUri)
                    || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
                {
                    con.WriteLine("--timestamp-server must be an http or https address.");
                    return 2;
                }
                timestampServer = server;
            }

            options.TryGetValue("--reason", out string? reason);
            options.TryGetValue("--location", out string? location);
            options.TryGetValue("--contact", out string? contact);
            PdfSigner.VisibleSignatureInfo? appearance = null;
            if (options.TryGetValue("--appearance", out string? placement))
            {
                string[] parts = placement.Split(':', 2, StringSplitOptions.TrimEntries);
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageNumber)
                    || pageNumber < 1 || pageNumber > pageCount)
                {
                    con.WriteLine($"--appearance needs a page from 1 to {pageCount}, such as 1 or 1:bottom-right.");
                    return 2;
                }
                string corner = parts.Length > 1 ? parts[1].ToLowerInvariant() : "bottom-left";
                if (corner is not ("bottom-left" or "bottom-right" or "top-left" or "top-right"))
                {
                    con.WriteLine("The corner is bottom-left, bottom-right, top-left or top-right.");
                    return 2;
                }
                if (!CliSignDimension(options, "--width", 240, out double width)
                    || !CliSignDimension(options, "--height", 84, out double height)
                    || !CliSignDimension(options, "--font-size", 10, out double fontSize)
                    || width < 72 || height < 36 || fontSize is < 6 or > 72)
                {
                    con.WriteLine("The appearance must be at least 72 x 36 pt, with text from 6 to 72 pt.");
                    return 2;
                }

                // Placed the way the Sign Document dialog places it: a 36 pt margin from the chosen corner.
                var page = PdfEngineIntegration.ReadPageInformation(source, null)[pageNumber - 1];
                const double Margin = 36;
                width = Math.Min(width, Math.Max(1, page.Width - Margin * 2));
                height = Math.Min(height, Math.Max(1, page.Height - Margin * 2));
                double left = corner.EndsWith("right", StringComparison.Ordinal) ? page.Width - Margin - width : Margin;
                double bottom = corner.StartsWith("top", StringComparison.Ordinal) ? page.Height - Margin - height : Margin;
                string signer = certificate.GetNameInfo(X509NameType.SimpleName, false);
                if (string.IsNullOrWhiteSpace(signer)) signer = certificate.Subject;
                string template = options.TryGetValue("--appearance-text", out string? custom) && custom.Length > 0
                    ? custom.Replace("\\n", "\n", StringComparison.Ordinal) : DefaultSignAppearanceText;
                string text = template
                    .Replace("{name}", signer, StringComparison.OrdinalIgnoreCase)
                    .Replace("{date}", DateTimeOffset.Now.ToString("g", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
                    .Replace("{reason}", reason ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("{location}", location ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                appearance = new PdfSigner.VisibleSignatureInfo(pageNumber - 1, left, bottom, width, height, fontSize, text);
            }

            if (timestampServer is not null) con.WriteLine($"Requesting a timestamp from {timestampServer}...");
            try
            {
                CliEnsureParentDir(output);
                string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    new PdfSigner().Sign(source, temporary, certificate,
                        new PdfSigner.SignInfo(reason ?? string.Empty, location ?? string.Empty, contact ?? string.Empty,
                            appearance, timestampServer));
                    File.Move(temporary, output, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (PdfTimestampException ex)
            {
                con.WriteLine("The timestamp could not be added, so the document was not signed: " + ex.Message);
                return 1;
            }
            string who = new StoreCertificateProvider(certificate).DisplayName;
            con.WriteLine($"Signed as {who}"
                + (timestampServer is not null ? " with a trusted timestamp" : string.Empty)
                + (appearance is not null ? $" and a visible signature on page {appearance.PageIndex + 1}" : string.Empty)
                + $" -> {output}");
            return 0;
        }

        private static bool CliSignDimension(Dictionary<string, string> options, string name, double fallback, out double value)
        {
            value = fallback;
            return !options.TryGetValue(name, out string? raw)
                || (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value));
        }
    }
}
