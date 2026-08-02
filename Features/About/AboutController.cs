using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace KillerPDF.Features
{
    /// <summary>
    /// Everything the About card does that is not drawing: reading the signature and release date,
    /// hashing the exe, asking GitHub whether there is a newer release, and performing the
    /// one-click self-update.
    ///
    /// Holds no controls. Talks to the window only through <see cref="IAboutHost"/>, so the whole
    /// of this file is testable against a stub host.
    /// </summary>
    internal sealed class AboutController
    {
        // The certificate subject is the legal name ("Open Source Developer Stephen Riley"), so the
        // About card ties it back to the name people know. Gated on the subject actually being
        // Steve's: a fork signed by somebody else must not claim the alias, and an unsigned build
        // has no subject at all. Family standard, see code/CLAUDE.md.
        private const string SignerName = "Stephen Riley";
        private const string AkaName    = "Steve the Killer";

        private const string Repo = "https://github.com/SteveTheKiller/KillerPDF";

        private readonly IAboutHost _host;

        /// <summary>"vX.Y.Z" of the available update, set by the update check. Null until one is found.</summary>
        private string? _updateTag;

        internal AboutController(IAboutHost host) => _host = host;

        /// <summary>The running assembly's version, three parts.</summary>
        internal static string Version =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

        /// <summary>Release date baked in from the csproj's ReleaseDate property, so a user can see
        /// how old their build is. A file timestamp would not survive being copied and the PE linker
        /// stamp is a build date, not a release date. Empty when the attribute is missing (an older
        /// build), in which case the version line shows the version alone.</summary>
        internal static string ReleaseDate
        {
            get
            {
                foreach (var a in System.Reflection.CustomAttributeExtensions.GetCustomAttributes
                             <System.Reflection.AssemblyMetadataAttribute>(
                                 System.Reflection.Assembly.GetExecutingAssembly()))
                    if (a.Key == "ReleaseDate") return a.Value ?? string.Empty;
                return string.Empty;
            }
        }

        /// <summary>Populates the card and shows it. The SHA-256 is slow, so it lands later.</summary>
        internal void Show()
        {
            var (sigValid, sigSubject, sigThumbprint) = App.GetExeSignerInfo();

            _host.Publisher   = sigValid ? sigSubject : "(not signed or chain failed)";
            _host.Thumbprint  = string.IsNullOrEmpty(sigThumbprint) ? "(none)" : sigThumbprint;
            _host.Sha256      = _host.Loc("Str_About_Computing");
            _host.ReleaseDate = ReleaseDate;

            _host.SetVersion(Version);

            // Signed, verified, AND signed by Steve - all three, not merely "is signed".
            bool signedByMe = sigValid
                           && sigSubject.IndexOf(SignerName, StringComparison.OrdinalIgnoreCase) >= 0;
            // 0x201C / 0x201D are the curly quotes, built from codepoints so this file stays ASCII
            // on disk - the same encoding trap that made release.ps1 PS7-only.
            _host.SetAlias(signedByMe ? (char)0x201C + AkaName + (char)0x201D : null);

            _host.UpdateVisible = false;
            _host.ShowCard();

            CheckForUpdateAsync(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
            ComputeSha256Async();
        }

        /// <summary>Opens the GitHub release for the running version.</summary>
        internal void OpenReleaseNotes() => OpenUrl($"{Repo}/releases/tag/v{Version}");

        internal static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser, or the shell refused - nothing useful to say */ }
        }

        // ---- SHA-256 -------------------------------------------------------------------------

        private async void ComputeSha256Async()
        {
            var sha256 = await System.Threading.Tasks.Task.Run(App.GetExeSha256).ConfigureAwait(true);
            _host.Sha256 = sha256;
        }

        // ---- Update check --------------------------------------------------------------------

        /// <summary>
        /// Quietly checks GitHub for a newer release when the About card opens. Runs only on demand
        /// (no background service), times out fast, and silently does nothing if there is no
        /// internet or the request fails. Shows the update button only if a newer tag exists.
        /// </summary>
        private async void CheckForUpdateAsync(System.Version? current)
        {
            if (current is null) return;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-UpdateCheck");
                var json = await http.GetStringAsync($"{Repo.Replace("github.com", "api.github.com/repos")}/releases/latest")
                    .ConfigureAwait(true);

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
                var tag = tagEl.GetString();
                if (string.IsNullOrWhiteSpace(tag)) return;
                // System.Version spelled out: this class has a string property called Version, which
                // shadows the type in expression position, so a bare "Version.TryParse" binds to
                // string.TryParse and does not compile.
                if (!System.Version.TryParse(tag!.TrimStart('v', 'V').Trim(), out var latest)) return;

                var cur = new System.Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
                var lat = new System.Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
                if (lat <= cur) return;

                _updateTag = $"v{lat.ToString(3)}";
                _host.UpdateText    = $"Update available: {_updateTag}";
                _host.UpdateVisible = true;
            }
            catch { /* offline, timeout, or API error - quietly do nothing */ }
        }

        // ---- Self-update ---------------------------------------------------------------------

        /// <summary>
        /// One-click self-update: downloads the released exe, verifies it against the published
        /// SHA256SUMS.txt, then hands off to a small batch that waits for this process to exit,
        /// swaps the exe in place, and relaunches with the currently-open PDF. Falls back to the
        /// releases page if anything fails (offline, checksum mismatch, unwritable location).
        /// </summary>
        internal async void Update()
        {
            var tag = _updateTag;
            if (string.IsNullOrEmpty(tag)) return;

            if (_host.IsDirty)
            {
                KillerDialog.Show(_host.Window, _host.Loc("Str_Dlg_SaveBeforeUpdate"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = KillerDialog.Show(_host.Window,
                $"Download and install KillerPDF {tag}?\n\nThe app will close and reopen automatically.",
                "KillerPDF", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            _host.UpdateEnabled = false;
            _host.UpdateText    = "Downloading...";

            string? newExe = await DownloadVerifiedAsync(tag!).ConfigureAwait(true);
            if (newExe is null)
            {
                // Offline, timed out, or verification failed: restore the button and open the
                // releases page so the user can update manually.
                _host.UpdateEnabled = true;
                _host.UpdateText    = $"Update available: {tag}";
                OpenUrl($"{Repo}/releases/latest");
                return;
            }

            if (!LaunchSwapAndExit(newExe))
            {
                try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
                _host.UpdateEnabled = true;
                _host.UpdateText    = $"Update available: {tag}";
            }
        }

        /// <summary>Downloads the release exe and checks it against the published checksum.
        /// Returns the temp path, or null if anything at all went wrong.</summary>
        private static async System.Threading.Tasks.Task<string?> DownloadVerifiedAsync(string tag)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-UpdateCheck");

                var exeUrl = $"{Repo}/releases/download/{tag}/KillerPDF.exe";
                // Read the checksums from the release ASSET next to the exe, not from
                // raw.githubusercontent at the tag. Both files are uploaded to the release
                // together, so the hash can never drift from the exe the way a repo-committed
                // file does when the tag/commit order gets muddled.
                var sumsUrl = $"{Repo}/releases/download/{tag}/SHA256SUMS.txt";

                var exeBytes = await http.GetByteArrayAsync(exeUrl).ConfigureAwait(false);
                var sumsTxt  = await http.GetStringAsync(sumsUrl).ConfigureAwait(false);

                string? expected = null;
                foreach (var line in sumsTxt.Replace("\r", "").Split('\n'))
                {
                    if (line.TrimStart().StartsWith("KillerPDF.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) expected = parts[^1];
                        break;
                    }
                }
                if (string.IsNullOrEmpty(expected)) return null;

                string actual;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(exeBytes)).Replace("-", "");
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return null;

                var path = Path.Combine(Path.GetTempPath(), $"KillerPDF_update_{Guid.NewGuid():N}.exe");
                File.WriteAllBytes(path, exeBytes);
                return path;
            }
            catch { return null; }
        }

        /// <summary>Writes the swap batch, starts it, and shuts the app down. Returns false if the
        /// helper could not be started, in which case nothing has been changed.</summary>
        private bool LaunchSwapAndExit(string newExe)
        {
            try
            {
                var curExe = Process.GetCurrentProcess().MainModule!.FileName;
                var reopen = _host.FileToReopen;
                var pid    = Process.GetCurrentProcess().Id;
                var relArg = string.IsNullOrEmpty(reopen) ? "" : $" \"{reopen}\"";
                var bat    = Path.Combine(Path.GetTempPath(), $"killerpdf_update_{Guid.NewGuid():N}.bat");

                // A machine-wide install (Program Files, from winget, choco or an RMM) is not
                // writable by a normal user, so the swap has to run elevated. This previously ran
                // the batch unelevated and sent the copy to >nul with no errorlevel check, so on
                // those installs it silently failed and then relaunched the OLD exe - the app
                // appeared to "update" to the same version, with no error.
                bool needsElevation = !CanWriteTo(Path.GetDirectoryName(curExe)!);

                // When elevated, relaunch through explorer.exe so the app comes back at the user's
                // normal integrity level rather than inheriting the elevated token. explorer.exe
                // cannot forward arguments, so the currently-open file is not reopened on that
                // path - a one-off convenience loss, preferred over leaving KillerPDF running as
                // administrator for the rest of the session.
                string relaunch = needsElevation
                    ? $"start \"\" explorer.exe \"{curExe}\""
                    : $"start \"\" \"{curExe}\"{relArg}";

                File.WriteAllText(bat,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                    "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
                    $"copy /y \"{newExe}\" \"{curExe}\" >nul 2>&1\r\n" +
                    "if errorlevel 1 goto failed\r\n" +
                    relaunch + "\r\n" +
                    "goto cleanup\r\n" +
                    ":failed\r\n" +
                    // Do not relaunch a stale exe and call it an update: send the user to the
                    // releases page so the failure is visible and fixable by hand.
                    $"start \"\" \"{Repo}/releases/latest\"\r\n" +
                    ":cleanup\r\n" +
                    $"del \"{newExe}\" >nul 2>&1\r\n" +
                    "del \"%~f0\" >nul 2>&1\r\n");

                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
                {
                    WindowStyle     = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                if (needsElevation) psi.Verb = "runas";   // triggers the UAC prompt

                // Declining UAC throws Win32Exception 1223, so only shut down once the helper is
                // actually running - otherwise the app would close without updating.
                Process.Start(psi);
                Application.Current.Shutdown();
                return true;
            }
            catch { return false; }
        }

        /// <summary>True if this process can create a file in <paramref name="dir"/>. Used to decide
        /// whether the self-update swap needs elevating: Program Files installs are not writable by
        /// a normal user, per-user installs under LOCALAPPDATA always are.</summary>
        private static bool CanWriteTo(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, $".kp_write_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                      1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }
    }
}
