using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KillerPDF.Services
{
    /// <summary>
    /// A fresh folder under %SystemRoot%\Temp that only Administrators and SYSTEM can write, for a
    /// script an elevated process hands to cmd. Inheritance is cut, so the Users create right on
    /// Windows\Temp does not reach it, and a folder someone else made first is refused.
    /// </summary>
    internal static class ProtectedScriptFolder
    {
        internal static string Create()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Temp", "KillerPDF-" + Guid.NewGuid().ToString("N"));
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(administrators);
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(path)) throw new IOException("The uninstall script folder already exists.");
            DirectoryInfo folder = security.CreateDirectory(path);
            if (!administrators.Equals(folder.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("The uninstall script folder is not owned by Administrators.");
            return path;
        }
    }
}
