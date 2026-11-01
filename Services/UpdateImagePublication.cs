using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace KillerPDF.Services
{
    internal static class UpdateImagePublication
    {
        private static readonly ConcurrentDictionary<string, (string Hash, string Signer)> VerifiedHashes =
            new ConcurrentDictionary<string, (string Hash, string Signer)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records the SHA-256 a download was verified against and the thumbprint of the
        /// certificate that signed it. The helper holds the published image to both.</summary>
        internal static void RememberVerified(string path, string sha256, string signer)
        {
            if (sha256.Length != 64 || Array.Exists(sha256.ToCharArray(), c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("The verified update hash is invalid.");
            if (signer.Length != 40 || Array.Exists(signer.ToCharArray(), c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("The verified update signer is invalid.");
            VerifiedHashes[Path.GetFullPath(path)] = (sha256, signer);
        }

        internal static string VerifiedHash(string download)
        {
            if (!VerifiedHashes.TryGetValue(Path.GetFullPath(download), out var expected))
                throw new InvalidDataException("The update has no retained verification hash.");
            return expected.Hash;
        }

        /// <summary>The publication script. With <paramref name="showReport"/> a failure opens the recovery report.
        /// <paramref name="overrides"/> replaces individual operations and exists for checks only. A caller
        /// that has already emitted <see cref="Guard"/> ahead of this text passes <paramref name="guardEmitted"/>,
        /// so the helper stays within the Windows command limit.</summary>
        internal static string Script(string download, string target, bool elevated,
            string releasesUrl, string displayName, string? version = null, string? hive = null,
            string? reopen = null, bool showReport = false, string? overrides = null, bool guardEmitted = false)
        {
            download = Path.GetFullPath(download);
            target = Path.GetFullPath(target);
            if (!VerifiedHashes.TryGetValue(download, out var verified))
                throw new InvalidDataException("This update has no retained verification hash. Download retained at: " + download);
            string expected = verified.Hash;
            string staged = target + ".new-" + Guid.NewGuid().ToString("N");
            string backup = target + ".old-" + Guid.NewGuid().ToString("N");
            string restore = target + ".restore-" + Guid.NewGuid().ToString("N");
            string report = download + ".recovery.txt";
            File.WriteAllText(report, "Update publication has not been confirmed. If the helper cannot run, retain these files."
                + Environment.NewLine + "Target: " + target + Environment.NewLine + "Download: " + download
                + Environment.NewLine + "Staging path (not yet verified): " + staged
                + Environment.NewLine + "Backup path: " + backup + Environment.NewLine + "Restoration path: " + restore
                + Environment.NewLine + "Expected SHA-256: " + expected + Environment.NewLine + "Manual download: " + releasesUrl);
            var script = new StringBuilder(Function);
            script.AppendLine("$plan = [pscustomobject]@{");
            Value(script, "Download", download); Value(script, "Target", target);
            Value(script, "Staged", staged); Value(script, "Backup", backup);
            Value(script, "Restore", restore);
            Value(script, "Report", report); Value(script, "Hash", expected);
            Value(script, "Releases", releasesUrl); Value(script, "DisplayName", displayName);
            Value(script, "Version", version ?? ""); Value(script, "Hive", hive ?? "");
            Value(script, "Reopen", reopen ?? "");
            Value(script, "Signer", AppInfo.SignerName); Value(script, "Thumb", verified.Signer);
            script.AppendLine("Elevated = " + (elevated ? "$true" : "$false"));
            script.AppendLine("ShowReport = " + (showReport ? "$true" : "$false")).AppendLine("}");
            if (!guardEmitted) script.AppendLine(Guard);
            script.AppendLine(Operations);
            if (overrides is not null) script.AppendLine(overrides);
            // The elevated helper must not open a window as administrator, so instead of showing the
            // report it reopens whatever copy is installed: the user is not left with the app closed.
            // The old version then runs without a message; the report stays beside the staged copy
            // in the installation folder, and the next update check offers the release again.
            script.AppendLine("if (Invoke-UpdateImagePublication $plan $ops) { exit 0 } else { if ($plan.ShowReport) { try { & $ops.ShowReport $plan.Report } catch { } }; "
                + "if ($plan.Elevated) { try { & $ops.Relaunch $plan } catch { } }; exit 1 }");
            return script.ToString();
        }

        /// <summary>The installer-mode helper (KillerBench's installed-copy update). It stages the
        /// verified installer in a fresh folder (administrators only when elevated), holds it open
        /// against writes, renames and deletion, rechecks its hash and publisher signature through
        /// that hold, runs it with <paramref name="arguments"/>, then relaunches
        /// <paramref name="relaunch"/>. <paramref name="overrides"/> exists for checks only.</summary>
        internal static string Installer(string download, string arguments, bool elevated,
            string relaunch, string stageRoot, string releasesUrl, string? overrides = null)
        {
            download = Path.GetFullPath(download);
            if (!VerifiedHashes.TryGetValue(download, out var verified))
                throw new InvalidDataException("This update has no retained verification hash. Download retained at: " + download);
            var script = new StringBuilder(InstallerFunction);
            script.AppendLine("$plan = [pscustomobject]@{");
            Value(script, "Download", download); Value(script, "Hash", verified.Hash.ToUpperInvariant());
            Value(script, "Signer", AppInfo.SignerName); Value(script, "Thumb", verified.Signer);
            Value(script, "Arguments", arguments); Value(script, "Setup", AppInfo.DisplayName + "-Setup.exe");
            Value(script, "Target", Path.GetFullPath(relaunch)); Value(script, "Reopen", "");
            Value(script, "StageRoot", Path.GetFullPath(stageRoot));
            Value(script, "Releases", releasesUrl); Value(script, "Report", download + ".update-failure.txt");
            script.AppendLine("Elevated = " + (elevated ? "$true" : "$false")).AppendLine("}");
            script.AppendLine(Guard);
            script.AppendLine(Operations);
            script.AppendLine(InstallerOperations);
            if (overrides is not null) script.AppendLine(overrides);
            script.AppendLine("if (Invoke-UpdateInstaller $plan $ops) { exit 0 } else { exit 1 }");
            return script.ToString();
        }

        private static void Value(StringBuilder script, string name, string value) =>
            script.AppendLine(name + " = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))");

        // Every parent folder of the target stays pinned for the whole publication, so none can be
        // renamed or redirected under it. The published image is then reopened once without
        // following links, and its hash and publisher signature are checked through that one held
        // handle (KillerBench's portable publication).
        private const string Function = """
$ErrorActionPreference = 'Stop'
function Invoke-UpdateImagePublication {
    param($plan, $ops)
    $stageVerified = $false
    $published = $false
    $parents = $null
    $image = $null
    try {
        # The download and the staged copy are each held open against writers, renames and
        # deletion while their hash and publisher signature are checked (Killendar's helper checks).
        $hold = & $ops.OpenImage $plan.Download
        try {
            $bytes = & $ops.Read $plan.Download
            if ((& $ops.Hash $bytes) -ne $plan.Hash) { throw 'The download no longer matches its verified SHA-256.' }
            if (-not (& $ops.Publisher $plan.Download $plan)) { throw 'The downloaded update is not signed by the publisher.' }
        }
        finally { $hold.Dispose() }
        $parents = & $ops.PinParents $plan.Target
        & $ops.Stage $plan.Staged $bytes
        $hold = & $ops.OpenImage $plan.Staged
        try {
            if ((& $ops.HashStream $hold) -ne $plan.Hash) { throw 'The staged image failed SHA-256 verification.' }
            if (-not (& $ops.Publisher $plan.Staged $plan)) { throw 'The staged update is not signed by the publisher.' }
        }
        finally { $hold.Dispose() }
        $stageVerified = $true
        if (& $ops.Exists $plan.Target) {
            & $ops.PrepareTarget $plan.Target
            & $ops.Replace $plan.Staged $plan.Target $plan.Backup
        }
        else { & $ops.Move $plan.Staged $plan.Target }
        $image = & $ops.OpenImage $plan.Target
        if ((& $ops.HashStream $image) -ne $plan.Hash) { throw 'The published image failed SHA-256 verification.' }
        if (-not (& $ops.Publisher $plan.Target $plan)) { throw 'The published image is not signed by the publisher.' }
        & $ops.Metadata $plan
        $published = $true
    }
    catch {
        $failure = $_.Exception.ToString()
        $restoration = ''
        try {
            if (!(& $ops.Exists $plan.Target) -and (& $ops.Exists $plan.Backup)) {
                & $ops.Restore $plan.Backup $plan.Target $plan.Restore
            }
        }
        catch { $restoration = [Environment]::NewLine + 'Restoration failed: ' + $_.Exception.ToString() }
        $message = 'Update publication failed: ' + $failure + $restoration + [Environment]::NewLine +
            'Target: ' + $plan.Target + [Environment]::NewLine + 'Download: ' + $plan.Download +
            [Environment]::NewLine + 'Staging path (verified: ' + $stageVerified + '): ' + $plan.Staged +
            [Environment]::NewLine + 'Backup: ' + $plan.Backup + [Environment]::NewLine + 'Restoration path: ' + $plan.Restore +
            [Environment]::NewLine + 'Report: ' + $plan.Report + [Environment]::NewLine + 'Manual download: ' + $plan.Releases
        try { & $ops.Report $plan.Report $message } catch { [Console]::Error.WriteLine($message) }
        return $false
    }
    finally {
        if (-not $published) {
            if ($null -ne $image) { $image.Dispose() }
            if ($null -ne $parents) { $parents.Dispose() }
        }
    }
    foreach ($path in @($plan.Download, $plan.Staged, $plan.Backup, $plan.Restore, $plan.Report)) {
        try { & $ops.Delete $path } catch { }
    }
    # Reopening comes after the update is complete and cleaned up. It can fail on its own (an
    # administrator from another account, no desktop), and that must never report a finished
    # update as failed or leave its backup behind (2026-10-28 review, DEP-002). The verified image
    # and its parents stay held until the launch has created a process or definitively failed.
    try { & $ops.Relaunch $plan } catch { }
    finally {
        $image.Dispose()
        $parents.Dispose()
    }
    return $true
}

""";
        private const string Operations = """
$ops = @{
    Read = { param($path) ,([IO.File]::ReadAllBytes($path)) }
    Hash = { param($bytes)
        $hash = [Security.Cryptography.SHA256]::Create()
        try { [BitConverter]::ToString($hash.ComputeHash([byte[]]$bytes)).Replace('-', '') }
        finally { $hash.Dispose() }
    }
    HashStream = { param($stream)
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $stream.Position = 0; [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
        finally { $hash.Dispose() }
    }
    PinParents = { param($target) [KillerUpdateGuard]::PinParents($target) }
    OpenImage = { param($path) [KillerUpdateGuard]::OpenImage($path) }
    Publisher = { param($path, $plan) [KillerUpdateGuard]::Publisher($path, $plan.Thumb, $plan.Signer) }
    Stage = { param($path, $bytes)
        $stream = New-Object IO.FileStream($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 65536, [IO.FileOptions]::WriteThrough)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
    }
    Exists = { param($path) [IO.File]::Exists($path) }
    PrepareTarget = { param($path)
        $attributes = [IO.File]::GetAttributes($path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The publication target is redirected.' }
        if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            [IO.File]::SetAttributes($path, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
        }
    }
    Replace = { param($source, $target, $backup)
        $originalHash = & $ops.Hash (& $ops.Read $target)
        $replacementHash = & $ops.Hash (& $ops.Read $source)
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            try { [IO.File]::Replace($source, $target, $backup); return }
            catch {
                $code = $_.Exception.GetBaseException().HResult -band 65535
                if ($attempt -ge 9 -or $code -notin @(32, 33)) { throw }
                if (!(& $ops.Exists $source) -or !(& $ops.Exists $target) -or (& $ops.Exists $backup)) { throw }
                if ((& $ops.Hash (& $ops.Read $source)) -ne $replacementHash -or
                    (& $ops.Hash (& $ops.Read $target)) -ne $originalHash) { throw }
                Start-Sleep -Seconds 1
                if (!(& $ops.Exists $source) -or !(& $ops.Exists $target) -or (& $ops.Exists $backup)) { throw }
                if ((& $ops.Hash (& $ops.Read $source)) -ne $replacementHash -or
                    (& $ops.Hash (& $ops.Read $target)) -ne $originalHash) { throw }
            }
        }
    }
    Move = { param($source, $target) [IO.File]::Move($source, $target) }
    Restore = { param($backup, $target, $restore)
        $bytes = & $ops.Read $backup
        $expected = & $ops.Hash $bytes
        & $ops.Stage $restore $bytes
        if ((& $ops.Hash (& $ops.Read $restore)) -ne $expected) { throw 'The restoration image failed SHA-256 verification.' }
        & $ops.Move $restore $target
    }
    Report = { param($path, $message) [IO.File]::WriteAllText($path, $message) }
    ShowReport = { param($path)
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.UseShellExecute = $true
        $start.FileName = Join-Path ([Environment]::SystemDirectory) 'notepad.exe'
        $start.Arguments = '"' + $path + '"'
        $process = [Diagnostics.Process]::Start($start)
        if ($null -eq $process) { throw 'The update recovery report did not open.' }
        $process.Dispose()
    }
    Delete = { param($path) [IO.File]::Delete($path) }
    Metadata = { param($plan)
        if ($plan.Version.Length -eq 0 -or $plan.Hive.Length -eq 0) { return }
        if ($plan.Hive -eq 'HKLM') { $root = [Microsoft.Win32.Registry]::LocalMachine }
        elseif ($plan.Hive -eq 'HKCU') { $root = [Microsoft.Win32.Registry]::CurrentUser }
        else { throw 'The update registry scope is invalid.' }
        foreach ($keyPath in @(('Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $plan.DisplayName),
            ('Software\' + $plan.DisplayName))) {
            $key = $root.CreateSubKey($keyPath)
            try {
                if ($keyPath.Contains('Uninstall')) {
                    $key.SetValue('DisplayVersion', $plan.Version)
                    $key.SetValue('Publisher', 'Steve the Killer')
                }
                else { $key.SetValue('Version', $plan.Version) }
            }
            finally { if ($null -ne $key) { $key.Dispose() } }
        }
    }
    Relaunch = { param($plan)
        Start-KillerUpdatedApp $plan.Target $plan.Reopen $plan.Elevated
    }
}
""";

        private const string InstallerFunction = """
$ErrorActionPreference = 'Stop'
function Invoke-UpdateInstaller {
    param($plan, $ops)
    $stage = $null
    $hold = $null
    $installed = $false
    try {
        $source = & $ops.OpenImage $plan.Download
        try {
            $bytes = & $ops.Read $plan.Download
            if ((& $ops.Hash $bytes) -ne $plan.Hash) { throw 'The download no longer matches its verified SHA-256.' }
            if (-not (& $ops.Publisher $plan.Download $plan)) { throw 'The downloaded installer is not signed by the publisher.' }
        }
        finally { $source.Dispose() }
        $stage = & $ops.StageFolder $plan
        $staged = Join-Path $stage $plan.Setup
        & $ops.Stage $staged $bytes
        # From here until the installer exits, nothing can write, rename or delete the staged image.
        $hold = & $ops.OpenImage $staged
        if ((& $ops.HashStream $hold) -ne $plan.Hash) { throw 'The staged installer failed SHA-256 verification.' }
        if (-not (& $ops.Publisher $staged $plan)) { throw 'The staged installer is not signed by the publisher.' }
        $code = & $ops.Run $staged $plan.Arguments
        if ($code -ne 0) { throw ('The installer exited with code ' + $code + '.') }
        $installed = $true
    }
    catch {
        $message = 'Update installation failed: ' + $_.Exception.Message + [Environment]::NewLine + 'Download: ' + $plan.Download +
            [Environment]::NewLine + 'Manual download: ' + $plan.Releases
        try { & $ops.Report $plan.Report $message } catch { [Console]::Error.WriteLine($message) }
        try { & $ops.Releases $plan.Releases } catch { }
        return $false
    }
    finally {
        if ($null -ne $hold) { $hold.Dispose() }
        if ($null -ne $stage) { try { [IO.Directory]::Delete($stage, $true) } catch { } }
        # A failed install keeps the download its report names for recovery.
        if ($installed) { try { [IO.File]::Delete($plan.Download) } catch { } }
    }
    try { & $ops.Relaunch $plan } catch { }
    return $true
}

""";
        private const string InstallerOperations = """
$ops.StageFolder = { param($plan)
    $path = Join-Path $plan.StageRoot ([Guid]::NewGuid().ToString('N'))
    if ([IO.Directory]::Exists($path)) { throw 'The staging folder already exists.' }
    [void][IO.Directory]::CreateDirectory($plan.StageRoot)
    if ($plan.Elevated) {
        # Only SYSTEM and Administrators may change what an elevated helper is about to run.
        $security = New-Object Security.AccessControl.DirectorySecurity
        $security.SetAccessRuleProtection($true, $false)
        foreach ($rule in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
            $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
                (New-Object Security.Principal.SecurityIdentifier($rule[0])), $rule[1], 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
        }
        [void][IO.Directory]::CreateDirectory($path, $security)
    }
    else { [void][IO.Directory]::CreateDirectory($path) }
    $guard = [KillerUpdateGuard]::PinParents((Join-Path $path 'x'))
    $guard.Dispose()
    $path
}
$ops.Run = { param($path, $arguments)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $path
    $start.Arguments = $arguments
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($path)
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'The installer did not start.' }
    try { $process.WaitForExit(); $process.ExitCode } finally { $process.Dispose() }
}
$ops.Releases = { param($url)
    if ($plan.Elevated) { return }
    Start-Process $url
}
""";

        // Compiled by Windows PowerShell 5.1, so it stays within C# 5. TrustedUpdate.Publication emits
        // it ahead of its own prologue too; the type check at the top makes a second copy a no-op.
        internal const string Guard = """
if (-not ('KillerUpdateGuard' -as [type])) {
# A helper running as administrator (the trusted machine path, or a per-user copy run as
# administrator) compiles in a fresh folder only Administrators and SYSTEM can open. Add-Type
# writes its source and DLL to TMP before loading them, and the inherited TMP is the user's own
# folder, where any process of that user could swap them in between (2026-10-28 review, DEP-001).
$killerTemp = $null; $killerSaved = @($env:TMP, $env:TEMP)
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $killerTemp = Join-Path ([Environment]::GetFolderPath('Windows')) ('Temp\KillerTools-' + [Guid]::NewGuid().ToString('N'))
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $null = [IO.Directory]::CreateDirectory($killerTemp, $acl)
    $env:TMP = $killerTemp; $env:TEMP = $killerTemp
}
try {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class KillerUpdateGuard {
    private sealed class Parents : IDisposable {
        internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
        public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); Handles.Clear(); }
    }
    // Opens every parent folder without following links and without delete sharing, so none can
    // be renamed or swapped for a link until the lease is disposed. UNC paths are refused.
    public static IDisposable PinParents(string target) {
        target = Path.GetFullPath(target);
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("Update publication requires a local, unredirected path.");
        var paths = new Stack<string>();
        for (var dir = Directory.GetParent(target); dir != null; dir = dir.Parent) paths.Push(dir.FullName);
        var lease = new Parents();
        try {
            while (paths.Count > 0) {
                string path = paths.Pop();
                // Read access, not attributes only: NTFS keeps share modes only for data access, so an
                // attributes handle would not stop a rename (as InstallationFiles.PinDirectory does).
                var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
                lease.Handles.Add(handle);
                FileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) throw new IOException("A publication directory is redirected: " + path);
            }
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("The publication target is redirected.");
            return lease;
        } catch { lease.Dispose(); throw; }
    }
    // Opens the image itself rather than anything a link points to, refuses a link or folder, and
    // denies writers, renames and deletion while the caller holds the stream.
    public static FileStream OpenImage(string path) {
        var handle = CreateFile(Path.GetFullPath(path), 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        try {
            FileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x410) != 0) throw new IOException("The published image is redirected.");
            return new FileStream(handle, FileAccess.Read);
        } catch { handle.Dispose(); throw; }
    }
    // True when the file's embedded Authenticode signature validates (cache-only retrieval, no
    // network), its certificate has the expected thumbprint, and its subject names the publisher.
    public static bool Publisher(string path, string thumbprint, string signer) {
        string thumb, subject;
        if (!Signature(path, out thumb, out subject)) return false;
        return string.Equals(thumb, thumbprint, StringComparison.OrdinalIgnoreCase)
            && subject.IndexOf(signer, StringComparison.OrdinalIgnoreCase) >= 0;
    }
    public static bool Signature(string path, out string thumbprint, out string subject) {
        thumbprint = ""; subject = "";
        IntPtr name = IntPtr.Zero, file = IntPtr.Zero, data = IntPtr.Zero;
        try {
            using (var source = X509Certificate.CreateFromSignedFile(path))
            using (var certificate = new X509Certificate2(source)) {
                thumbprint = certificate.Thumbprint ?? "";
                subject = certificate.Subject ?? "";
            }
            name = Marshal.StringToHGlobalUni(path);
            file = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
            data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustData)));
            var fileInfo = new TrustFile(); fileInfo.Size = (uint)Marshal.SizeOf(typeof(TrustFile)); fileInfo.Path = name;
            Marshal.StructureToPtr(fileInfo, file, false);
            var trust = new TrustData(); trust.Size = (uint)Marshal.SizeOf(typeof(TrustData));
            trust.UiChoice = 2; trust.UnionChoice = 1; trust.File = file; trust.ProviderFlags = 0x1000;
            Marshal.StructureToPtr(trust, data, false);
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return thumbprint.Length > 0 && WinVerifyTrust(IntPtr.Zero, ref action, data) == 0;
        }
        catch { return false; }
        finally {
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (file != IntPtr.Zero) Marshal.FreeHGlobal(file);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
        }
    }
    // Starts the published image and fails unless a process was actually created. An elevated
    // helper starts it with the desktop shell's own token, at normal integrity, and only when that
    // shell belongs to this account and session.
    public static void Launch(string target, string reopen, bool elevated) {
        string arguments = string.IsNullOrEmpty(reopen) ? "" : Quote(reopen);
        if (!elevated) {
            var start = new ProcessStartInfo(target, arguments);
            start.UseShellExecute = false; start.CreateNoWindow = true; start.WorkingDirectory = Path.GetDirectoryName(target);
            using (var process = Process.Start(start))
                if (process == null) throw new IOException("The updated application did not start.");
            return;
        }
        uint shellPid;
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out shellPid) == 0) throw new IOException("The normal desktop shell is unavailable.");
        using (var shellProcess = Process.GetProcessById((int)shellPid))
            if (shellProcess.SessionId != Process.GetCurrentProcess().SessionId) throw new IOException("The shell is in another session.");
        IntPtr processHandle = OpenProcess(0x1000, false, shellPid), token = IntPtr.Zero, primary = IntPtr.Zero, environment = IntPtr.Zero;
        if (processHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            if (!OpenProcessToken(processHandle, 0xB, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (var shellIdentity = new WindowsIdentity(token))
            using (var current = WindowsIdentity.GetCurrent())
                if (shellIdentity.User == null || !shellIdentity.User.Equals(current.User)) throw new IOException("The shell belongs to another account.");
            int elevation, returned;
            if (!GetTokenInformation(token, 20, out elevation, sizeof(int), out returned) || returned != sizeof(int) || elevation != 0)
                throw new IOException("A normal-integrity shell token could not be established.");
            // Windows refuses the shell's own token here with access denied; a primary copy that can
            // adjust its session and defaults is what CreateProcessWithToken accepts.
            if (!DuplicateTokenEx(token, 0x18B, IntPtr.Zero, 2, 1, out primary)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateEnvironmentBlock(out environment, primary, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo(); startup.Size = Marshal.SizeOf(typeof(StartupInfo)); startup.Desktop = @"winsta0\default";
            ProcessInfo info;
            if (!CreateProcessWithToken(primary, 0, target, new StringBuilder(Quote(target) + " " + arguments), 0x400, environment, Path.GetDirectoryName(target), ref startup, out info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.Thread); CloseHandle(info.Process);
        } finally {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(processHandle);
        }
    }
    public static uint ConsoleSession() { return WTSGetActiveConsoleSessionId(); }
    public static string Quote(string value) {
        if (value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) throw new IOException("The launch path is invalid.");
        int trailingSlashes = 0;
        for (int i = value.Length - 1; i >= 0 && value[i] == '\\'; i--) trailingSlashes++;
        return "\"" + value + new string('\\', trailingSlashes) + "\"";
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrustFile { public uint Size; public IntPtr Path, File, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData {
        public uint Size; public IntPtr Policy, Sip; public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr StateData, Url; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo {
        public int Size; public string Reserved; public string Desktop; public string Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags; public short Show, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint WinVerifyTrust(IntPtr window, ref Guid action, IntPtr data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonation, int type, out IntPtr duplicate);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int length, out int returned);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithToken(IntPtr token, uint flags, string application, StringBuilder command, uint creation, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
}
'@
} finally {
    $env:TMP = $killerSaved[0]; $env:TEMP = $killerSaved[1]
    if ($null -ne $killerTemp) { Remove-Item -LiteralPath $killerTemp -Recurse -Force -ErrorAction SilentlyContinue }
}
}
# Reopens the updated app for the person at this desktop. Someone who elevated with another
# administrator's account has no shell token this helper can use, and that account must never
# start the app itself, so a one-time task for the Users group starts it in this session, as
# whoever is signed in there and at normal rights. Task Scheduler needs no password for that.
function Start-KillerUpdatedApp {
    param($target, $reopen, $elevated)
    try { [KillerUpdateGuard]::Launch($target, $reopen, $elevated); return }
    catch { if (-not $elevated) { throw } }
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($session -eq 0) { $session = [KillerUpdateGuard]::ConsoleSession() }
    $name = 'KillerUpdate-' + [Guid]::NewGuid().ToString('N')
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $folder = $service.GetFolder('')
    # An earlier helper killed before its cleanup leaves its hidden task behind; tasks still queued
    # or running belong to a start in progress and stay.
    foreach ($old in @($folder.GetTasks(1))) {
        if ($old.Name -like 'KillerUpdate-*' -and $old.State -ne 2 -and $old.State -ne 4) {
            try { $folder.DeleteTask($old.Name, 0) } catch { }
        }
    }
    $task = $service.NewTask(0)
    $task.Settings.Hidden = $true
    $task.Settings.DisallowStartIfOnBatteries = $false
    $task.Settings.StopIfGoingOnBatteries = $false
    $task.Settings.ExecutionTimeLimit = 'PT0S'
    $task.Principal.GroupId = 'S-1-5-32-545'
    $task.Principal.RunLevel = 0
    $action = $task.Actions.Create(0)
    $action.Path = $target
    $action.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
    if ($reopen) { $action.Arguments = [KillerUpdateGuard]::Quote($reopen) }
    $registered = $folder.RegisterTaskDefinition($name, $task, 2, $null, $null, 4)
    try {
        $null = $registered.RunEx($null, 4, $session, $null)
        # Task Scheduler can be slow just after an update, and deleting the task while it is still
        # queued loses the start, so the wait is long and a task still queued is left to run.
        for ($wait = 0; $wait -lt 600 -and $registered.State -eq 2; $wait++) { Start-Sleep -Milliseconds 100 }
    }
    finally { if ($registered.State -ne 2 -and $registered.State -ne 4) { $folder.DeleteTask($name, 0) } }
}
""";
    }
}
