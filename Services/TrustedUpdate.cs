using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KillerPDF.Services
{
    internal static class TrustedUpdate
    {
        internal const string HelperArgument = "/apply-verified-update";

        /// <summary>The protected machine installation folder, the only one the elevated helper updates.</summary>
        internal static string MachineDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.DisplayName);

        internal static void Start(string download, string? version, UpdateService.Platform platform)
        {
            string current = Process.GetCurrentProcess().MainModule!.FileName;
            string directory = MachineDirectory;
            string target = Path.Combine(directory, AppInfo.ExeName);
            if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Automatic elevated updates require the protected machine installation. Download retained at: "
                    + download + ". Manual download: " + UpdateService.ReleasesUrl);
            using var pinned = InstallationFiles.PinDirectory(directory, mustExist: true);
            ValidateMachineOwner(directory);
            if (!CodeSignature.GetSignerInfo().valid) throw new SecurityException("The installed update helper has no valid signature.");
            string hash = UpdateImagePublication.VerifiedHash(download);
            string requested = NormalizeVersion(version);
            string identity = Guid.NewGuid().ToString("N");
            string staged = Path.Combine(directory, ".update-" + identity + ".exe");
            File.WriteAllText(download + ".recovery.txt", "Automatic update completion has not been confirmed."
                + Environment.NewLine + "Download: " + download + Environment.NewLine + "Protected staging (not yet verified): " + staged
                + Environment.NewLine + "Protected report: " + staged + ".recovery.txt"
                + Environment.NewLine + "Manual download: " + UpdateService.ReleasesUrl);
            using var owner = Process.GetCurrentProcess();
            // Keep the installed helper immutable across the UAC handoff.
            using var image = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
            var start = new ProcessStartInfo(current, HelperArgument + " "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(Path.GetFullPath(download))) + " " + hash + " " + requested
                + " " + owner.Id.ToString(CultureInfo.InvariantCulture)
                + " " + owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + " " + identity)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) };
            using var helper = platform.Start(start) ?? throw new IOException("The trusted update helper did not start.");
            helper.WaitForExit();
            if (helper.ExitCode != 0) throw new Win32Exception(helper.ExitCode);
            // The verified copy now sits in protected staging, and its own report lives beside it, so
            // the download and the report written above are no longer anyone's recovery path.
            UpdateService.DiscardDownload(download);
            UpdateService.DiscardDownload(download + ".recovery.txt");
        }

        internal static int Apply(string[] args)
        {
            try
            {
                ValidateRequest(args);
                if (!KillerFamily.Deployment.InstallerExitCodes.IsElevated())
                    throw new SecurityException("The machine update operation requires a complete elevated request.");
                string directory = MachineDirectory;
                string target = Path.Combine(directory, AppInfo.ExeName);
                using var self = Process.GetCurrentProcess();
                if (!string.Equals(self.MainModule!.FileName, target, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The update helper is not the protected installed application.");
                using var lease = KillerFamily.Deployment.InstallerExitCodes.Acquire(AppInfo.DisplayName);
                using var pinned = InstallationFiles.PinDirectory(directory, mustExist: true);
                using var creator = WindowsIdentity.GetCurrent();
                ValidateMachineOwner(directory, creator.Owner
                    ?? throw new SecurityException("The machine staging creator owner is unavailable."));
                string download = ReleaseIdentity.RequireLocalFile(Encoding.Unicode.GetString(Convert.FromBase64String(args[1])));
                string expected = args[2], version = NormalizeVersion(args[3]);
                // The publication helper pins the published image to the thumbprint of the file just
                // verified (ReleaseIdentity in Stage below), never the installed copy's own, so a
                // release signed with the publisher's renewed certificate still publishes.
                string signer = string.Empty;
                if (!Guid.TryParseExact(args[6], "N", out _)) throw new SecurityException("The update staging identity is invalid.");
                int parentId = int.Parse(args[4], CultureInfo.InvariantCulture);
                long parentTicks = long.Parse(args[5], CultureInfo.InvariantCulture);
                using var parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks != parentTicks
                    || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The update owner does not match the installed application.");
                using var sourcePath = InstallationFiles.PinDirectory(Path.GetDirectoryName(download)!, mustExist: true);
                using var source = new FileStream(download, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = SHA256.Create();
                if (!Hex(hash.ComputeHash(source)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The update changed after download verification.");
                string staged = Path.Combine(directory, ".update-" + args[6] + ".exe");
                Stage(download, staged, expected, image =>
                {
                    string thumb = ReleaseIdentity.Verify(image, version, requireInstalledSignature: true);
                    if (signer.Length != 0 && !string.Equals(signer, thumb, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityException("The staged update is not the verified download.");
                    signer = thumb;
                    if (image == download) UpdateImagePublication.RememberVerified(download, expected, signer);
                });
                string originalHash;
                using (var original = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                    originalHash = Hex(hash.ComputeHash(original));
                UpdateImagePublication.RememberVerified(staged, expected, signer);
                var start = Publication(staged, target, version, originalHash,
                    parentId, parentTicks, self.Id, self.StartTime.ToUniversalTime().Ticks);
                using var child = Process.Start(start) ?? throw new IOException("The immutable publication helper did not start.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(error);
            }
        }

        /// <summary>Copies a hash-verified download into protected staging. The caller holds the
        /// download open without delete sharing, so its path cannot be swapped: release identity is
        /// checked there before anything reaches the installation folder, and again on the staged
        /// bytes that will be published. A staged copy that fails either check is deleted.</summary>
        internal static void Stage(string download, string staged, string expected, Action<string> verifyIdentity)
        {
            verifyIdentity(download);
            if (File.Exists(staged) || Directory.Exists(staged)) throw new IOException("The update staging path is already occupied.");
            try
            {
                ExecutableDeployment.Install(download, staged);
                using (var protectedCopy = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var hash = SHA256.Create())
                    if (!Hex(hash.ComputeHash(protectedCopy)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityException("The protected update staging failed SHA-256 verification.");
                verifyIdentity(staged);
            }
            catch
            {
                try { File.Delete(staged); } catch { /* The failure below is what the caller reports. */ }
                throw;
            }
            // The installed exe is in place and this staging has been verified, so what earlier failed
            // updates left in the installation folder can go (only when over a day old; see the sweep).
            ExecutableDeployment.RemovePublicationLeftovers(Path.Combine(Path.GetDirectoryName(staged)!, AppInfo.ExeName));
        }

        internal static ProcessStartInfo Publication(string staged, string target, string version, string originalHash,
            int parentId, long parentTicks, int helperId, long helperTicks)
        {
            string publication = UpdateImagePublication.Script(staged, target, true, UpdateService.ReleasesUrl,
                AppInfo.DisplayName, version, "HKLM", guardEmitted: true);
            string report = staged + ".recovery.txt";
            // The guard type comes first, so a failure before the publication text has run (the app
            // did not close, another install holds the mutex, the installed image changed) can still
            // reopen the installed copy the safe way below.
            string body = "$ErrorActionPreference='Stop';$lease=$null;$owned=$false;try{\n"
                + UpdateImagePublication.Guard + "\n"
                + Wait(parentId, parentTicks, Abandoned) + Wait(helperId, helperTicks, Abandoned)
                + "$lease=New-Object Threading.Mutex($false,'Global\\KillerTools-Install-" + AppInfo.DisplayName + "');"
                + "try{$owned=$lease.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true};if(-not $owned){throw 'Another KillerTools install or update is running, so this update was not applied.'};"
                + "$original=[IO.File]::OpenRead(" + Data(target) + ");$hash=[Security.Cryptography.SHA256]::Create();"
                + "try{if([BitConverter]::ToString($hash.ComputeHash($original)).Replace('-','') -ne " + Data(originalHash)
                + "){throw 'The installed image changed before update publication.'}}finally{$hash.Dispose();$original.Dispose()};\n"
                + publication
                + "\n}catch{$message=$_.Exception.ToString();[Console]::Error.WriteLine($message);try{[IO.File]::AppendAllText("
                + Data(report) + ",[Environment]::NewLine+$message)}catch{};"
                // Nothing was published, so the installed copy is reopened for the user the same way
                // a finished update is, never as this administrator (Start-KillerUpdatedApp). The
                // report beside the staged copy still says what happened.
                + "try{Start-KillerUpdatedApp " + Data(target) + " '' $true}catch{};exit 1}"
                + "finally{if($null -ne $lease){if($owned){$lease.ReleaseMutex()};$lease.Dispose()}}";
            return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"), ImmutablePowerShell.Arguments(body))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) };
        }

        /// <summary>PowerShell that waits up to a minute for one process, identified by id and start
        /// time so a reused id is never mistaken for it, and runs <paramref name="onTimeout"/> (by
        /// default, exit 1618) if it is still running.</summary>
        internal static string Wait(int id, long ticks, string onTimeout = "exit 1618") =>
            "$p=$null;try{$p=[Diagnostics.Process]::GetProcessById(" + id.ToString(CultureInfo.InvariantCulture)
            // The start time is read inside the try: a process that exits between the lookup and
            // this read has finished, and must not abort the update under ErrorActionPreference Stop.
            + ");if($p.StartTime.ToUniversalTime().Ticks -ne " + ticks.ToString(CultureInfo.InvariantCulture)
            + "){$p=$null}}catch{$p=$null};if($null -ne $p -and -not $p.WaitForExit(60000)){" + onTimeout + "};";

        /// <summary>The protected publication's timeout: thrown, so its catch writes the reason into
        /// the recovery report beside the staged copy instead of leaving it with no word of why.</summary>
        private const string Abandoned = "throw 'The app did not close within a minute, so this update was not applied. The staged copy and this report can be deleted.'";

        /// <summary>Refuses an elevated request unless every argument has exactly the shape
        /// <see cref="Start"/> writes: the helper verb, an absolute download path, a 64-digit hex
        /// hash, a release version, a positive owner id and start time, and a staging identity
        /// (Killendar's and KillerNotes' request check).</summary>
        internal static void ValidateRequest(string[] args)
        {
            if (args.Length != 7 || args[0] != HelperArgument
                || args[2].Length != 64 || args[2].Any(c => !Uri.IsHexDigit(c))
                || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0
                || !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                || ticks <= 0 || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(args[6], "N", out _))
                throw new SecurityException("The update request is invalid.");
            string download;
            try { download = Encoding.Unicode.GetString(Convert.FromBase64String(args[1])); }
            catch (FormatException error) { throw new SecurityException("The update request is invalid.", error); }
            if (download.Length == 0 || !Path.IsPathRooted(download))
                throw new SecurityException("The update download path must be absolute.");
            _ = Path.GetFullPath(download);
            _ = NormalizeVersion(args[3]);
        }

        private static string NormalizeVersion(string? text) => ReleaseIdentity.NormalizeVersion(text);
        private static string Data(string text) => "[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(text)) + "'))";
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

        internal static void ValidateMachineOwner(string directory, SecurityIdentifier? stagingOwner = null)
            => ValidateMachineOwner(new DirectoryInfo(directory).GetAccessControl(), stagingOwner);

        // Pre-UAC validation defers creator proof; the elevated staging caller supplies its token owner.
        internal static void ValidateMachineOwner(DirectorySecurity security, SecurityIdentifier? stagingOwner = null)
        {
            var trusted = new[] { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };
            if (security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value))
                throw new SecurityException("The machine installation has an untrusted owner.");
            if (stagingOwner != null && !trusted.Contains(stagingOwner.Value))
                throw new SecurityException("The machine staging creator has an untrusted owner.");
            var writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            const uint genericWrites = 0x50000000u; // GENERIC_ALL and GENERIC_WRITE can remain unmapped in inheritable ACEs.
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow
                    && ((unchecked((uint)rule.FileSystemRights) & ((uint)writes | genericWrites)) != 0)
                    && !trusted.Contains(rule.IdentityReference.Value)
                    && !(rule.IdentityReference.Value == "S-1-3-0"
                        && (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0))
                    throw new SecurityException("The machine installation permits untrusted writes.");
        }
    }
}
