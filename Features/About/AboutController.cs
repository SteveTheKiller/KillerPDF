using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        private const string Repo = "https://github.com/Rafie-kun/KillerPDF";

        private readonly IAboutHost _host;

        /// <summary>"vX.Y.Z" of the available update, set by the update check. Null until one is found.</summary>
        private string? _updateTag;

        internal AboutController(IAboutHost host) => _host = host;

        /// <summary>The running build's SemVer, including a prerelease label when present.</summary>
        internal static string Version => AppVersion.Display;

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

            _host.Publisher   = sigValid ? sigSubject : _host.Loc("Str_Margin_None");
            _host.Thumbprint  = string.IsNullOrEmpty(sigThumbprint) ? _host.Loc("Str_Margin_None") : sigThumbprint;
            _host.Sha256      = _host.Loc("Str_About_Computing");
            _host.ReleaseDate = ReleaseDate;

            _host.SetVersion(Version);

            // Signed, verified, AND signed by Steve - all three, not merely "is signed".
            bool signedByMe = sigValid
                           && sigSubject.Contains(SignerName, StringComparison.OrdinalIgnoreCase);
            // 0x201C / 0x201D are the curly quotes, built from codepoints so this file stays ASCII
            // on disk - the same encoding trap that made release.ps1 PS7-only.
            _host.SetAlias(signedByMe ? (char)0x201C + AkaName + (char)0x201D : null);

            _host.UpdateVisible = false;
            _host.ShowCard();

            CheckForUpdateAsync(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
            ComputeSha256Async();
        }

        /// <summary>Opens the GitHub releases page. The fork tags carry suffixes
        /// (v1.8.6-enhanced) that never match a bare version, so /latest always resolves.</summary>
        internal static void OpenReleaseNotes() => OpenUrl($"{Repo}/releases/latest");

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
        /// Parses a release tag into a comparable version. Fork tags carry suffixes
        /// (v1.8.6-enhanced) that System.Version chokes on, so only the numeric head
        /// is parsed and the rest is ignored for comparison purposes.
        /// </summary>
        internal static bool TryParseReleaseTag(string? tag, out System.Version version)
        {
            version = new System.Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string t = tag.Trim().TrimStart('v', 'V').Trim();
            int i = 0;
            while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.')) i++;
            if (i == 0) return false;
            // System.Version spelled out: this class has a string property called Version, which
            // shadows the type in expression position, so a bare "Version.TryParse" binds to
            // string.TryParse and does not compile.
            return System.Version.TryParse(t[..i], out version!);
        }

        private static bool IsNewerThanCurrent(System.Version latest, System.Version? current)
        {
            if (current is null) return false;
            var cur = new System.Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
            var lat = new System.Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
            return lat > cur;
        }

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
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-UpdateCheck");
                var json = await http.GetStringAsync($"{Repo.Replace("github.com", "api.github.com/repos")}/releases/latest")
                    .ConfigureAwait(true);

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
                var tag = tagEl.GetString();
                if (!TryParseReleaseTag(tag, out var latest)) return;

                if (!IsNewerThanCurrent(latest, current)) return;

                _updateTag = $"v{latest.ToString(3)}";
                _host.UpdateText    = string.Format(_host.Loc("Str_UpdateAvailable"), _updateTag);
                _host.UpdateVisible = true;
            }
            catch { /* offline, timeout, or API error - quietly do nothing */ }
        }

        /// <summary>
        /// Startup update check: same GitHub endpoint, but instead of lighting the About
        /// button it shows the update prompt with the release notes - unless this exact tag
        /// was skipped, in which case it stays silent until a newer release appears.
        /// </summary>
        internal async void CheckForUpdateOnStartupAsync()
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-UpdateCheck");
                var json = await http.GetStringAsync($"{Repo.Replace("github.com", "api.github.com/repos")}/releases/latest")
                    .ConfigureAwait(true);

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
                var tag = tagEl.GetString();
                if (!TryParseReleaseTag(tag, out var latest)) return;

                if (!IsNewerThanCurrent(latest,
                        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version))
                    return;

                string fullTag = tag!.Trim();
                if (string.Equals(App.GetSetting("SkipUpdateVersion"), fullTag,
                        StringComparison.OrdinalIgnoreCase))
                    return;   // skipped: stay silent until a newer tag appears

                string notes = "";
                if (doc.RootElement.TryGetProperty("body", out var bodyEl))
                    notes = bodyEl.GetString() ?? "";

                _updateTag = fullTag;
                switch (_host.ShowUpdatePrompt(fullTag, notes))
                {
                    case UpdateChoice.Update: UpdateCore(confirmFirst: false); break;
                    case UpdateChoice.Skip:
                        App.SetSetting("SkipUpdateVersion", fullTag);
                        break;
                    default: break;   // Later: ask again next launch
                }
            }
            catch { /* offline, timeout, or API error - quietly do nothing */ }
        }

        // ---- Self-update ---------------------------------------------------------------------

        /// <summary>
        /// One-click self-update: downloads and verifies the public portable/installer. Installed
        /// copies hand it the same payload-based install command used by a manual upgrade; portable
        /// copies replace their original launcher after both launcher and inner app have exited.
        /// </summary>
        internal async void Update() => UpdateCore(confirmFirst: true);

        internal async void UpdateCore(bool confirmFirst)
        {
            var tag = _updateTag;
            if (string.IsNullOrEmpty(tag)) return;

            if (_host.IsDirty)
            {
                KillerDialog.Show(_host.Window, _host.Loc("Str_Dlg_SaveBeforeUpdate"),
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = confirmFirst
                ? KillerDialog.Show(_host.Window,
                    string.Format(_host.Loc("Str_UpdatePrompt"), tag),
                    "KillerPDF", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                : MessageBoxResult.OK;   // the startup prompt already confirmed
            if (confirm != MessageBoxResult.OK) return;

            _host.UpdateEnabled = false;
            _host.UpdateText    = _host.Loc("Str_UpdateDownloading");

            string? newExe = await DownloadVerifiedAsync(tag!).ConfigureAwait(true);
            if (newExe is null)
            {
                // Offline, timed out, or verification failed: restore the button and open the
                // releases page so the user can update manually.
                _host.UpdateEnabled = true;
                _host.UpdateText    = string.Format(_host.Loc("Str_UpdateAvailable"), tag);
                OpenUrl($"{Repo}/releases/latest");
                return;
            }

            if (!LaunchSwapAndExit(newExe))
            {
                try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
                _host.UpdateEnabled = true;
                _host.UpdateText    = string.Format(_host.Loc("Str_UpdateAvailable"), tag);
            }
        }

        /// <summary>Downloads the release exe and checks it against the published checksum.
        /// Returns the temp path, or null if anything at all went wrong.</summary>
        private static async System.Threading.Tasks.Task<string?> DownloadVerifiedAsync(string tag)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-UpdateCheck");

                string assetName = App.IsPortable() ? "KillerPDF-Portable.exe" : "KillerPDF.exe";
                var exeUrl = $"{Repo}/releases/download/{tag}/{assetName}";
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
                    if (line.TrimStart().StartsWith(assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) expected = parts[^1];
                        break;
                    }
                }
                if (string.IsNullOrEmpty(expected)) return null;

                string actual = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(exeBytes));
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
                var curExe = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The current executable path is unavailable.");
                var reopen = _host.FileToReopen;
                var pid    = Environment.ProcessId;
                var relArg = string.IsNullOrEmpty(reopen) ? "" : $" \"{reopen}\"";
                var bat    = Path.Combine(Path.GetTempPath(), $"killerpdf_update_{Guid.NewGuid():N}.bat");
                bool portable = App.IsPortable();
                string? portableLauncher = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
                bool packagedPortable = portable && !string.IsNullOrWhiteSpace(portableLauncher) && File.Exists(portableLauncher);

                if (!App.VerifyAuthenticode(newExe).Valid && App.GetExeSignerInfo().Valid)
                    throw new InvalidDataException("The downloaded update is not signed by a trusted publisher.");
                // Fork: when the RUNNING build is itself unsigned there is no publisher identity
                // to compare against, so a download already verified against the release's
                // published SHA256SUMS.txt is accepted. A signed install still demands a trusted
                // signature and will never downgrade itself to unsigned bytes. Hash-over-HTTPS
                // keeps corruption/CDN-mixup protection either way.

                // A machine-wide install (Program Files, from winget, choco or an RMM) is not
                // writable by a normal user, so the swap has to run elevated. This previously ran
                // the batch unelevated and sent the copy to >nul with no errorlevel check, so on
                // those installs it silently failed and then relaunched the OLD exe - the app
                // appeared to "update" to the same version, with no error.
                string updateTarget = packagedPortable ? portableLauncher! : curExe;
                bool needsElevation = !CanWriteTo(Path.GetDirectoryName(updateTarget)!);

                // When elevated, relaunch through explorer.exe so the app comes back at the user's
                // normal integrity level rather than inheriting the elevated token. explorer.exe
                // cannot forward arguments, so the currently-open file is not reopened on that
                // path - a one-off convenience loss, preferred over leaving KillerPDF running as
                // administrator for the rest of the session.
                var script = new StringBuilder()
                    .AppendLine("@echo off")
                    .AppendLine(":waitapp")
                    .AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul")
                    .AppendLine("if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto waitapp )");

                if (packagedPortable)
                {
                    if (int.TryParse(Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PID"), out int launcherPid))
                    {
                        script.AppendLine(":waitlauncher")
                              .AppendLine($"tasklist /fi \"PID eq {launcherPid}\" 2>nul | find \"{launcherPid}\" >nul")
                              .AppendLine("if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto waitlauncher )");
                    }
                    script.AppendLine($"attrib -r \"{portableLauncher}\" >nul 2>&1")
                          .AppendLine($"copy /y \"{newExe}\" \"{portableLauncher}\" >nul 2>&1")
                          .AppendLine("if errorlevel 1 goto failed")
                          .AppendLine(needsElevation
                              ? $"start \"\" explorer.exe \"{portableLauncher}\""
                              : $"start \"\" \"{portableLauncher}\"{relArg}");
                }
                else
                {
                    bool machineInstall = !CanWriteTo(Path.GetDirectoryName(curExe)!);
                    string installArg = machineInstall ? "/silent" : "/install-user";
                    script.AppendLine($"start /wait \"\" \"{newExe}\" {installArg}")
                          .AppendLine("if errorlevel 1 goto failed")
                          .AppendLine(needsElevation
                              ? $"start \"\" explorer.exe \"{curExe}\""
                              : $"start \"\" \"{curExe}\"{relArg}");
                }

                script.AppendLine("goto cleanup")
                      .AppendLine(":failed")
                      .AppendLine($"start \"\" \"{Repo}/releases/latest\"")
                      .AppendLine(":cleanup")
                      .AppendLine($"del \"{newExe}\" >nul 2>&1")
                      .AppendLine("del \"%~f0\" >nul 2>&1");
                File.WriteAllText(bat, script.ToString());

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
