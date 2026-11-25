using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KillerPDF.Services
{
    /// <summary>
    /// Release checking and one-click self-update against the GitHub releases of
    /// <see cref="AppInfo.GitHubRepo"/>.
    ///
    /// Split into three steps so the caller owns every piece of UI and the shutdown:
    /// <see cref="CheckAsync"/> finds a newer tag, <see cref="DownloadAsync"/> fetches and verifies
    /// the exe, <see cref="StartSwap"/> launches the helper that replaces it.
    /// </summary>
    internal static partial class UpdateService
    {
        /// <summary>How this copy updates. The default swaps this exe for the released one. An app
        /// that installs through its own installer, or runs behind a portable launcher, describes
        /// that in <see cref="ChooseUpdateTarget"/> (KillerBench's installer and launcher modes).</summary>
        internal sealed class UpdateTarget
        {
            /// <summary>Release asset to download and verify.</summary>
            internal string Asset { get; set; } = AppInfo.ExeName;
            /// <summary>An exe other than this one to swap, such as a portable launcher.</summary>
            internal string? Replace { get; set; }
            /// <summary>Another process (the launcher) the helper waits for before it swaps.</summary>
            internal int WaitForProcess { get; set; }
            /// <summary>When set, the verified installer is run with these arguments instead of a swap.</summary>
            internal string? InstallerArguments { get; set; }
            /// <summary>The installer targets a machine-wide installation, so its helper runs elevated.</summary>
            internal bool Machine { get; set; }
            /// <summary>This copy cannot update itself (a development build, say); the download step fails.</summary>
            internal bool Unsupported { get; set; }
            /// <summary>Opt-in for the installer mode: start the verified installer itself, through
            /// "runas" when it needs elevation, so the UAC prompt names the signed publisher, instead
            /// of the PowerShell staging helper. Its hash and publisher signature are checked again
            /// under a handle that keeps the file from changing until it has started. The installer
            /// closes this process itself (KillerPDF).</summary>
            internal bool DirectInstaller { get; set; }

            internal bool IsPackage => InstallerArguments is not null || Replace is not null;
        }

        /// <summary>App hook: describe how this copy updates. Left alone, it swaps this exe.</summary>
        static partial void ChooseUpdateTarget(UpdateTarget target);

        internal static UpdateTarget Target()
        {
            var target = new UpdateTarget();
            ChooseUpdateTarget(target);
            return target;
        }

        /// <summary>Latest release page, used as the fallback whenever an automatic step fails.</summary>
        internal static string ReleasesUrl =>
            $"https://github.com/{AppInfo.GitHubRepo}/releases/latest";

        /// <summary>The one argument the update helper starts the reopened app with. Empty, the default,
        /// reopens it bare, as every app did before this setting; an app whose bare launch does something
        /// else (waking a copy hidden in the tray, say) sets this at startup to an argument it ignores.</summary>
        internal static string RelaunchArgument { get; set; } = "";

        /// <summary>Release page for one version.</summary>
        internal static string ReleaseUrl(string version) =>
            $"https://github.com/{AppInfo.GitHubRepo}/releases/tag/v{version}";

        private static HttpClient NewClient(int timeoutSeconds)
        {
#if NETFRAMEWORK
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.DisplayName}-UpdateCheck");
            return http;
        }

        /// <summary>Asks GitHub for the latest release tag and returns it only when it is newer than
        /// <paramref name="current"/>, as "v1.2.3". Null when there is nothing newer, or on any
        /// failure: with no internet this has to stay quiet. Times out fast.</summary>
        internal static async Task<string?> CheckAsync(Version? current)
        {
            if (current is null) return null;
            try
            {
                using var http = NewClient(4);
                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{AppInfo.GitHubRepo}/releases/latest")
                    .ConfigureAwait(false);
                return NewerReleaseTag(json, current);
            }
            catch { return null; }
        }

        /// <summary>The release in GitHub's latest-release <paramref name="json"/>, as "v1.2.3",
        /// when it is a published, stable vX.Y.Z release newer than <paramref name="current"/>
        /// (KillerBench's release check). Null for anything else.</summary>
        internal static string? NewerReleaseTag(string json, Version current)
        {
            // The fields are read from the document's top level, not matched in its text: the release
            // notes in the same JSON can quote any of these names.
            if (!ReadReleaseFields(json, out string? tag, out bool unpublished) || unpublished || tag is null) return null;
            if (!Regex.IsMatch(tag, "^v?[0-9]+\\.[0-9]+\\.[0-9]+\\z")) return null;
            if (!Version.TryParse(tag.TrimStart('v'), out var latest))
                return null;

            var cur = new Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
            var lat = new Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
            return lat > cur ? $"v{lat.ToString(3)}" : null;
        }

        /// <summary>Reads <c>tag_name</c>, <c>draft</c> and <c>prerelease</c> from the top-level object of a
        /// release document, in any field order, skipping every other value (nested objects, arrays and
        /// string escapes included). False when the document is not a well-formed object.</summary>
        private static bool ReadReleaseFields(string json, out string? tag, out bool unpublished)
        {
            tag = null;
            unpublished = false;
            int i = 0;
            SkipSpace(json, ref i);
            if (i >= json.Length || json[i++] != '{') return false;
            SkipSpace(json, ref i);
            if (i < json.Length && json[i] == '}') return AtEnd(json, i + 1);
            while (true)
            {
                SkipSpace(json, ref i);
                if (!ReadString(json, ref i, out string? key)) return false;
                SkipSpace(json, ref i);
                if (i >= json.Length || json[i++] != ':') return false;
                SkipSpace(json, ref i);
                if (i >= json.Length) return false;
                if (key == "tag_name")
                {
                    tag = null;
                    if (json[i] == '"') { if (!ReadString(json, ref i, out tag)) return false; }
                    else if (!SkipValue(json, ref i)) return false;
                }
                else if (key == "draft" || key == "prerelease")
                {
                    int start = i;
                    if (!SkipValue(json, ref i)) return false;
                    if (i - start == 4 && string.CompareOrdinal(json, start, "true", 0, 4) == 0) unpublished = true;
                }
                else if (!SkipValue(json, ref i)) return false;
                SkipSpace(json, ref i);
                if (i >= json.Length) return false;
                char next = json[i++];
                if (next == '}') return AtEnd(json, i);
                if (next != ',') return false;
            }
        }

        private static void SkipSpace(string json, ref int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
        }

        private static bool AtEnd(string json, int i)
        {
            SkipSpace(json, ref i);
            return i == json.Length;
        }

        /// <summary>Reads the JSON string whose opening quote is at <paramref name="i"/>, decoding its escapes.</summary>
        private static bool ReadString(string json, ref int i, out string? value)
        {
            value = null;
            if (i >= json.Length || json[i] != '"') return false;
            var text = new StringBuilder();
            i++;
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') { value = text.ToString(); return true; }
                if (c < ' ') return false;
                if (c != '\\') { text.Append(c); continue; }
                if (i >= json.Length) return false;
                char e = json[i++];
                switch (e)
                {
                    case '"': case '\\': case '/': text.Append(e); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'u':
                        if (i + 4 > json.Length || !int.TryParse(json.Substring(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int unit))
                            return false;
                        text.Append((char)unit);
                        i += 4;
                        break;
                    default: return false;
                }
            }
            return false;
        }

        /// <summary>Steps over one well-formed JSON value: a string, an object or array whose brackets match
        /// and whose members are themselves well formed, or a number or true, false or null.</summary>
        private static bool SkipValue(string json, ref int i, int depth = 0)
        {
            if (i >= json.Length || depth > 64) return false;
            char first = json[i];
            if (first == '"') return ReadString(json, ref i, out _);
            if (first == '{' || first == '[')
            {
                char close = first == '{' ? '}' : ']';
                i++;
                SkipSpace(json, ref i);
                if (i < json.Length && json[i] == close) { i++; return true; }
                while (true)
                {
                    SkipSpace(json, ref i);
                    if (first == '{')
                    {
                        if (!ReadString(json, ref i, out _)) return false;
                        SkipSpace(json, ref i);
                        if (i >= json.Length || json[i++] != ':') return false;
                        SkipSpace(json, ref i);
                    }
                    if (!SkipValue(json, ref i, depth + 1)) return false;
                    SkipSpace(json, ref i);
                    if (i >= json.Length) return false;
                    char next = json[i++];
                    if (next == close) return true;
                    if (next != ',') return false;
                }
            }
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']' && !char.IsWhiteSpace(json[i])) i++;
            return i > start && Regex.IsMatch(json.Substring(start, i - start), "^(true|false|null|-?(0|[1-9][0-9]*)(\\.[0-9]+)?([eE][+-]?[0-9]+)?)\\z");
        }

        /// <summary>Downloads the released exe for <paramref name="tag"/>, checks it against the
        /// published SHA256SUMS.txt and writes it to a temp file, whose path is returned. Throws if
        /// the download fails, the checksum entry is missing, or the hash does not match: an update
        /// that cannot be verified must not be installed.</summary>
        internal static async Task<string> DownloadAsync(string tag)
        {
            var target = Target();
            if (target.Unsupported) throw new InvalidOperationException("This copy is neither installed nor portable.");
            using var http = NewClient(90);

            var exeUrl = $"https://github.com/{AppInfo.GitHubRepo}/releases/download/{tag}/{target.Asset}";
            // The checksums come from the release asset next to the exe, not from the repo at the
            // tag. Both files are uploaded to the release together, so the hash cannot drift from
            // the exe the way a committed file does when the tag and commit order get muddled.
            var sumsUrl = $"https://github.com/{AppInfo.GitHubRepo}/releases/download/{tag}/SHA256SUMS.txt";
            return await DownloadVerifiedAsync(http, new Uri(exeUrl), new Uri(sumsUrl), target.Asset, target.IsPackage,
                tag, Path.GetTempPath(), MaximumDownloadBytes, DownloadDeadline).ConfigureAwait(false);
        }

        /// <summary>The largest installer or exe the update will download; the releases are far smaller.</summary>
        internal const long MaximumDownloadBytes = 400L * 1024 * 1024;

        /// <summary>How long the whole transfer may take. The 90 second client timeout covers only
        /// the response headers, so a slow link has this long to finish the body.</summary>
        internal static readonly TimeSpan DownloadDeadline = TimeSpan.FromMinutes(10);

        /// <summary>Fetches the checksum list, streams the release into a new file under
        /// <paramref name="directory"/> and verifies it. The file exists only when every check passed;
        /// a refused download (<see cref="UpdateVerificationException"/>) is told apart from a
        /// network failure, which surfaces as the transport's own exception.</summary>
        internal static async Task<string> DownloadVerifiedAsync(HttpClient http, Uri exeUrl, Uri sumsUrl, string asset,
            bool isPackage, string tag, string directory, long maximumBytes, TimeSpan deadline)
        {
            var sumsTxt = await http.GetStringAsync(sumsUrl).ConfigureAwait(false);
            string? expected = ChecksumFor(sumsTxt, asset);
            if (string.IsNullOrEmpty(expected)) throw new UpdateVerificationException("checksum entry not found");

            SweepStaleDownloads(directory, DateTime.UtcNow);
            var path = Path.Combine(directory, $"{AppInfo.DisplayName}_update_{Guid.NewGuid():N}.exe");
            string actual = await DownloadToFileAsync(http, exeUrl, path, maximumBytes, deadline).ConfigureAwait(false);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                DiscardDownload(path);
                throw new UpdateVerificationException("checksum mismatch");
            }
            // The checksum file comes from the same release as the exe, so it only proves the
            // download arrived intact. The signature, product and version prove where it came from.
            string signer;
            try
            {
                // An installer or launcher package is not this app's assembly, so it is held to the
                // publisher's signature alone; the app's own exe also has to be a newer release of it.
                signer = isPackage ? ReleaseIdentity.VerifyPackage(path, tag)
                    : ReleaseIdentity.Verify(path, tag, requireInstalledSignature: false);
            }
            catch (System.Security.SecurityException ex)
            {
                DiscardDownload(path);
                throw new UpdateVerificationException(ex.Message, ex);
            }
            catch { DiscardDownload(path); throw; }
            UpdateImagePublication.RememberVerified(path, actual, signer);
            return path;
        }

        /// <summary>Streams <paramref name="url"/> into a new file at <paramref name="path"/> through
        /// <see cref="HttpDownload"/>, which enforces the size cap and the whole-transfer deadline, and
        /// returns the file's SHA-256 as hex. A failed or oversized download leaves no file behind.</summary>
        internal static async Task<string> DownloadToFileAsync(HttpClient http, Uri url, string path, long maximumBytes, TimeSpan deadline)
        {
            try
            {
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await HttpDownload.CopyToAsync(http, url, output, deadline, maximumBytes).ConfigureAwait(false);
                using var written = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sha = SHA256.Create();
                return BitConverter.ToString(sha.ComputeHash(written)).Replace("-", "");
            }
            catch { DiscardDownload(path); throw; }
        }

        /// <summary>The hash SHA256SUMS.txt publishes for <paramref name="asset"/>: the first two-token line
        /// for exactly this asset name (KillerBench's checksum rule), so "KillerApp.exe" never takes the
        /// line of "KillerApp-Portable.exe". Both line orders are read: the standard "&lt;hash&gt;  &lt;asset&gt;"
        /// form, with or without sha256sum's '*' binary marker on the name, and the older
        /// "&lt;asset&gt; &lt;hash&gt;" form. A 64-digit hex token is always the hash. Null when there is none.</summary>
        internal static string? ChecksumFor(string sums, string asset)
        {
            foreach (var line in sums.Replace("\r", "").Split('\n'))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;
                if (IsSha256(parts[0]) && string.Equals(parts[1].TrimStart('*'), asset, StringComparison.OrdinalIgnoreCase))
                    return parts[0];
                if (IsSha256(parts[1]) && string.Equals(parts[0], asset, StringComparison.OrdinalIgnoreCase))
                    return parts[1];
            }
            return null;
        }

        private static bool IsSha256(string token)
        {
            if (token.Length != 64) return false;
            foreach (char c in token)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>Starts a helper that waits for this process to exit, swaps
        /// <paramref name="newExe"/> over the running exe and relaunches it. Throws if the helper
        /// cannot be started, including when the user declines the UAC prompt (Win32 1223) - the
        /// caller must only shut down once this returns.
        ///
        /// A machine-wide install (Program Files, from the /silent path, winget, choco or an RMM) is
        /// not writable by a normal user, so the swap has to run elevated. The copy result is
        /// checked: without that, an unelevated swap fails silently and relaunches the OLD exe, and
        /// the app appears to update to the same version with no error.</summary>
        internal sealed class Platform
        {
            internal Func<string, bool> CanWrite = CanWriteTo;
            internal Func<bool> IsElevated = KillerFamily.Deployment.InstallerExitCodes.IsElevated;
            internal Func<ProcessStartInfo, Process?> Start = Process.Start;
        }

        internal static void StartSwap(string newExe, string? newVersion = null, Platform? platform = null)
        {
            platform ??= new Platform();
            using var self = Process.GetCurrentProcess();
            var curExe = self.MainModule!.FileName;
            var target = Target();
            if (target.Unsupported) throw new InvalidOperationException("This copy is neither installed nor portable.");
            if (target.IsPackage)
            {
                if (target.InstallerArguments is not null && target.DirectInstaller)
                    StartInstaller(newExe, newVersion, curExe, target, platform);
                else
                    StartPackage(newExe, curExe, self, target, platform);
                return;
            }
            string hive = RegistryHiveFor(curExe);

            // The machine install always goes through the trusted helper, elevated or not. A per-user
            // or portable copy run as administrator can still write its own folder, and the trusted
            // helper would refuse it, so it takes the same helper an unelevated copy uses.
            bool needsElevation = !platform.CanWrite(Path.GetDirectoryName(curExe)!);
            if (needsElevation || (hive == "HKLM" && platform.IsElevated()))
            {
                TrustedUpdate.Start(newExe, newVersion, platform);
                return;
            }

            // The whole helper travels in the process command line, so no file the user account can
            // write is read after it starts. A failed publication opens the recovery report.
            // A copy run as administrator runs this helper at the same level. It must reopen the app
            // through the de-elevated path (and fail closed when that is unavailable) and must not open
            // a window of its own as administrator, so it takes the elevated helper's settings.
            bool elevated = platform.IsElevated();
            string script = "$ErrorActionPreference = 'Stop'\n"
                + (elevated ? TrustedUpdate.Wait(self.Id, self.StartTime.ToUniversalTime().Ticks)
                    : TrustedUpdate.Wait(self.Id, self.StartTime.ToUniversalTime().Ticks, NotClosed(newExe + ".recovery.txt"))) + "\n"
                + UpdateImagePublication.Script(newExe, curExe, elevated, ReleasesUrl, AppInfo.DisplayName,
                    newVersion, hive, reopen: RelaunchArgument, showReport: !elevated);
            var psi = HelperStart(script);
            if (platform.Start(psi) is null)
                throw new IOException("The update helper did not start. Download retained at: " + newExe);
        }

        /// <summary>The per-user helper's timeout when the app has not closed within a minute: the
        /// reason goes into the recovery report Script already wrote, and the report opens, as a
        /// failed publication's does, instead of the update silently doing nothing.</summary>
        internal static string NotClosed(string report)
        {
            // The path travels as Base64 data, as the publication script's values do, so no quote character in it can end a literal.
            string quoted = "([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(report)) + "')))";
            return "try{[IO.File]::AppendAllText(" + quoted + ",[Environment]::NewLine+'KillerTools: the app did not close within a minute, so this update was not applied.');"
                + "[void][Diagnostics.Process]::Start([IO.Path]::Combine([Environment]::SystemDirectory,'notepad.exe'),'\"'+" + quoted + "+'\"')}catch{};exit 1618";
        }

        /// <summary>The installer and launcher modes. The helper waits for this process (and the
        /// launcher), then either runs the verified installer from a staging folder it holds open or
        /// swaps the launcher. A machine install or a protected launcher folder runs the helper
        /// elevated, with its whole script in the consented command line.</summary>
        private static void StartPackage(string download, string curExe, Process self, UpdateTarget target, Platform platform)
        {
            var start = PackageStart(download, curExe, self.Id, self.StartTime.ToUniversalTime().Ticks, target, platform);
            if (platform.Start(start) is null)
                throw new IOException("The update helper did not start. Download retained at: " + download);
        }

        /// <summary>The installer and launcher helper's start info. A helper that "runas" elevates, and the
        /// helper of a copy already running as administrator (which inherits those rights), both take the
        /// elevated settings: no report window, and the app reopens through the de-elevated path. Only
        /// the helper that needs the prompt gets the "runas" verb.</summary>
        internal static ProcessStartInfo PackageStart(string download, string curExe, int processId, long processStartTicks,
            UpdateTarget target, Platform platform)
        {
            string replaced = target.Replace ?? curExe;
            bool elevate = target.Machine || !platform.CanWrite(Path.GetDirectoryName(replaced)!);
            string script = PackageHelperScript(download, replaced, target, elevate || platform.IsElevated(), processId, processStartTicks);
            var start = HelperStart(script);
            if (elevate) start.Verb = "runas";
            return start;
        }

        /// <summary>The installer and launcher helper's script. An unelevated helper that times out
        /// waiting for this process writes the reason to the recovery report and opens it, as the
        /// per-user swap does; the elevated helper keeps the silent exit, since it cannot open
        /// notepad as the user.</summary>
        internal static string PackageHelperScript(string download, string replaced, UpdateTarget target, bool elevate,
            int processId, long processStartTicks)
        {
            string script = "$ErrorActionPreference = 'Stop'\n"
                + (elevate ? TrustedUpdate.Wait(processId, processStartTicks)
                    : TrustedUpdate.Wait(processId, processStartTicks, NotClosed(download + ".recovery.txt"))) + "\n";
            if (target.WaitForProcess > 0)
                script += LauncherWait(target.WaitForProcess);
            if (target.InstallerArguments is null)
                script += UpdateImagePublication.Script(download, replaced, elevate, ReleasesUrl, AppInfo.DisplayName,
                    reopen: RelaunchArgument, showReport: !elevate);
            else
            {
                // An elevated helper stages under Program Files, where only administrators can write.
                string stageRoot = elevate
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.DisplayName + " Update")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DisplayName, "UpdateStage");
                script += UpdateImagePublication.Installer(download, target.InstallerArguments, elevate, replaced, stageRoot, ReleasesUrl);
            }
            return script;
        }

        /// <summary>The direct installer mode: the verified installer starts with its arguments, elevated
        /// through "runas" on the signed file when the target is a machine install or a folder this process
        /// cannot write. The download is held open against writes and deletion while its hash, product
        /// version and publisher signature are checked again, and until the installer has started.</summary>
        private static void StartInstaller(string download, string? version, string curExe, UpdateTarget target, Platform platform)
        {
            if (string.IsNullOrEmpty(version)) throw new InvalidOperationException("The installer needs the offered release version.");
            string path = ReleaseIdentity.RequireLocalFile(download);
            bool elevate = target.Machine || !platform.CanWrite(Path.GetDirectoryName(target.Replace ?? curExe)!);
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actual;
            using (var sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(held)).Replace("-", "");
            if (!actual.Equals(UpdateImagePublication.VerifiedHash(path), StringComparison.OrdinalIgnoreCase))
                throw new System.Security.SecurityException("The update installer changed after it was verified.");
            ReleaseIdentity.VerifyPackage(path, version!);
            var start = new ProcessStartInfo(path, target.InstallerArguments!)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetTempPath()
            };
            if (elevate) start.Verb = "runas";
            if (platform.Start(start) is null)
                throw new IOException("The update installer did not start. Download retained at: " + download);
        }

        /// <summary>Waits for the launcher <paramref name="processId"/> to exit, for at most five minutes. The launcher
        /// exposes only its process ID, so the deadline keeps a reused or hung ID from holding the helper forever;
        /// past it, the publication goes ahead and reports its own failure if the launcher is still locked.</summary>
        internal static string LauncherWait(int processId)
            => "$deadline = [DateTime]::UtcNow.AddMinutes(5)\n"
                + "while ((Get-Process -Id " + processId.ToString(CultureInfo.InvariantCulture)
                + " -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500 }\n";

        /// <summary>Starts Windows PowerShell directly with the whole unelevated helper in its command line.</summary>
        internal static ProcessStartInfo HelperStart(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return new ProcessStartInfo(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
                ImmutablePowerShell.Arguments(script))
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = system,
                UseShellExecute = true
            };
        }

        /// <summary>"HKCU" when <paramref name="exePath"/> is the per-user install,
        /// "HKLM" when it is the machine-wide install, "" for a portable copy - which has no
        /// install keys and must not gain any from an update.</summary>
        private static string RegistryHiveFor(string exePath)
        {
            try
            {
                string userExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", AppInfo.DisplayName, AppInfo.ExeName);
                string machineExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    AppInfo.DisplayName, AppInfo.ExeName);
                if (string.Equals(exePath, userExe, StringComparison.OrdinalIgnoreCase)) return "HKCU";
                if (string.Equals(exePath, machineExe, StringComparison.OrdinalIgnoreCase)) return "HKLM";
            }
            catch { }
            return "";
        }

        /// <summary>Deletes update downloads and recovery reports from earlier attempts that were
        /// refused, declined or interrupted. Only names this app writes are touched, and only after an
        /// hour, so a download another window is installing right now is left alone.</summary>
        internal static void SweepStaleDownloads(string directory, DateTime utcNow)
        {
            string prefix = AppInfo.DisplayName + "_update_";
            string[] files;
            try { files = Directory.GetFiles(directory, prefix + "*.exe*"); }
            catch { return; }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string rest = name.Substring(prefix.Length);
                if (!rest.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !rest.EndsWith(".exe.recovery.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (rest.Length < 32 || !Guid.TryParseExact(rest.Substring(0, 32), "N", out _) || rest[32] != '.') continue;
                try
                {
                    if (utcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1)) File.Delete(file);
                }
                catch { /* In use or protected: a later sweep gets it. */ }
            }
        }

        /// <summary>Deletes a downloaded update that will not be installed.</summary>
        internal static void DiscardDownload(string? path)
        {
            try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>True if this process can create a file in <paramref name="dir"/>. Decides whether
        /// the swap needs elevating: Program Files installs are not writable by a normal user,
        /// per-user installs under LOCALAPPDATA always are.</summary>
        private static bool CanWriteTo(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, $".upd_write_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                      1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>A downloaded update was refused: its checksum is missing or wrong, or it is not signed
    /// by the publisher. Distinct from a network failure so the user is told it was discarded.</summary>
    internal sealed class UpdateVerificationException : Exception
    {
        internal UpdateVerificationException(string message) : base(message) { }
        internal UpdateVerificationException(string message, Exception inner) : base(message, inner) { }
    }
}
