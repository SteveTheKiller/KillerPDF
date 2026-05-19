using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// At-rest store for the optional Application Insights connection
    /// string. By design the file is absent in default installs; the
    /// app emits zero telemetry in that case. An administrator opts a
    /// device in by running <c>TDPdf.exe /set-telemetry</c>.
    ///
    /// Security model — a "reasonable speed bump", not crypto:
    ///   * Ciphertext is produced by Windows DPAPI with
    ///     <see cref="DataProtectionScope.LocalMachine"/>, so a stolen
    ///     <c>telemetry.dat</c> is useless on another machine.
    ///   * A fixed entropy byte array compiled into TDPdf is passed as
    ///     <c>optionalEntropy</c>, so a non-admin user on the same box
    ///     cannot recover the connection string with a three-line
    ///     PowerShell that calls <c>ProtectedData.Unprotect</c>. They
    ///     would have to extract the entropy from the EXE first.
    ///   * The containing directory and file are ACL'd to grant
    ///     SYSTEM / Administrators FullControl and Authenticated Users
    ///     ReadAndExecute only — non-admins can't pre-create the
    ///     directory to influence ownership before install, and can't
    ///     overwrite the file once provisioned.
    ///
    /// A determined same-machine attacker who can reverse engineer or
    /// instrument the running TDPdf process can still recover the
    /// connection string. Treat the connection string as a low-value
    /// secret, set a daily ingestion cap on the App Insights resource,
    /// and rotate it if it leaks.
    /// </summary>
    internal static class TelemetryStore
    {
        // Fixed entropy. Not a secret — its only job is to require an
        // attacker to extract bytes from TDPdf.exe before they can
        // decrypt telemetry.dat, instead of getting the plaintext from
        // a stock DPAPI Unprotect call. Treat as an app-compatibility
        // constant: never change it without also re-running
        // /set-telemetry on every provisioned device.
        private static readonly byte[] s_entropy = new byte[]
        {
            0x54, 0x44, 0x50, 0x64, 0x66, 0x2D, 0x74, 0x65,
            0x6C, 0x65, 0x6D, 0x65, 0x74, 0x72, 0x79, 0x2D,
            0x76, 0x31, 0x2D, 0xA7, 0x3C, 0x91, 0x6E, 0x18,
            0xD2, 0x4B, 0x05, 0xFE, 0x82, 0x70, 0xCA, 0x39,
        };

        private static readonly string s_dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TDPdf");

        public static string Path => System.IO.Path.Combine(s_dir, "telemetry.dat");

        /// <summary>
        /// Sentinel file that, when present, disables auto-provisioning
        /// from the build-time-embedded connection string on the next
        /// launch. Written by <see cref="MarkDisabled"/> when a user
        /// runs <c>TDPdf.exe /clear-telemetry</c>; respected by
        /// <see cref="IsDisabled"/>.
        /// </summary>
        public static string DisabledMarkerPath => System.IO.Path.Combine(s_dir, "telemetry.disabled");

        /// <summary>True if the provisioning file is present on disk.</summary>
        public static bool Exists()
        {
            try { return File.Exists(Path); }
            catch { return false; }
        }

        /// <summary>
        /// True if the user has explicitly disabled telemetry on this
        /// device via <c>/clear-telemetry</c>. Auto-provisioning skips
        /// when this returns true.
        /// </summary>
        public static bool IsDisabled()
        {
            try { return File.Exists(DisabledMarkerPath); }
            catch { return false; }
        }

        /// <summary>
        /// Best-effort decrypt of <see cref="Path"/>. Returns
        /// <c>null</c> when the file is absent or any step fails — never
        /// throws.
        /// </summary>
        public static string? TryLoad()
        {
            try
            {
                if (!File.Exists(Path))
                    return null;

                byte[] cipher = File.ReadAllBytes(Path);
                byte[] plain = ProtectedData.Unprotect(cipher, s_entropy, DataProtectionScope.LocalMachine);
                string value = Encoding.UTF8.GetString(plain).Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Encrypt <paramref name="connectionString"/> with DPAPI
        /// LocalMachine + entropy and write it to <see cref="Path"/>
        /// with hardened ACLs. Caller must be elevated (SYSTEM or
        /// Administrators).
        /// </summary>
        public static void Save(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));

            EnsureHardenedDirectory();

            byte[] plain = Encoding.UTF8.GetBytes(connectionString.Trim());
            byte[] cipher = ProtectedData.Protect(plain, s_entropy, DataProtectionScope.LocalMachine);

            // Atomic write via temp + replace so we never leave a
            // truncated ciphertext on disk.
            string tmp = Path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            try
            {
                if (File.Exists(Path))
                    File.Replace(tmp, Path, destinationBackupFileName: null);
                else
                    File.Move(tmp, Path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { /* swallow */ }
                throw;
            }

            HardenFileAcl(Path);

            // Clear the in-memory plaintext as soon as possible.
            Array.Clear(plain);
        }

        /// <summary>Remove the provisioning file. No-op if absent.</summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch { /* swallow — caller logs */ }
        }

        /// <summary>
        /// Write the disabled-marker sentinel so the next launch will
        /// not re-provision from the build-time-embedded key. Best
        /// effort — failure is swallowed.
        /// </summary>
        public static void MarkDisabled()
        {
            try
            {
                EnsureHardenedDirectory();
                File.WriteAllText(DisabledMarkerPath, "disabled\r\n");
                HardenFileAcl(DisabledMarkerPath);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Clear the disabled-marker sentinel so the next launch can
        /// auto-provision again from the build-time-embedded key.
        /// </summary>
        public static void ClearDisabledMarker()
        {
            try
            {
                if (File.Exists(DisabledMarkerPath))
                    File.Delete(DisabledMarkerPath);
            }
            catch { /* swallow */ }
        }

        // ============================================================
        // ACL hardening — explicit, not inherited.
        //
        // Rubber-duck blocking finding #3: relying on default
        // %ProgramData% ACLs is risky because a non-admin user can
        // pre-create C:\ProgramData\TDPdf before the installer runs
        // and influence ownership. Setting an explicit DACL here is
        // idempotent and overrides any pre-existing weakness.
        // ============================================================

        private static void EnsureHardenedDirectory()
        {
            if (!Directory.Exists(s_dir))
                Directory.CreateDirectory(s_dir);

            try
            {
                var info = new DirectoryInfo(s_dir);
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

                acl.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                acl.AddAccessRule(new FileSystemAccessRule(
                    admins, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                acl.AddAccessRule(new FileSystemAccessRule(
                    users, FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));

                info.SetAccessControl(acl);
            }
            catch
            {
                // ACL hardening is best-effort. If we lack the right
                // (e.g. the caller is not elevated), Save() above will
                // also fail at file-write time and surface a useful
                // error. Don't mask that with an ACL exception here.
            }
        }

        private static void HardenFileAcl(string path)
        {
            try
            {
                var info = new FileInfo(path);
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

                acl.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl,
                    AccessControlType.Allow));
                acl.AddAccessRule(new FileSystemAccessRule(
                    admins, FileSystemRights.FullControl,
                    AccessControlType.Allow));

                // Read-only to non-admins. The GUI process running as
                // the interactive user still decrypts at startup.
                acl.AddAccessRule(new FileSystemAccessRule(
                    users, FileSystemRights.Read,
                    AccessControlType.Allow));

                info.SetAccessControl(acl);
            }
            catch
            {
                // Same rationale as EnsureHardenedDirectory.
            }
        }
    }
}
