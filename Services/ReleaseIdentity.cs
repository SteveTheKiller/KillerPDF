using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;

namespace KillerPDF.Services
{
    /// <summary>
    /// Decides whether an update image is a newer release of this app from the same publisher as the
    /// running copy. Both update paths call it: the per-user swap on the downloaded file and the
    /// elevated helper on the held-open download and again on the protected staged copy.
    ///
    /// Trust rule: the incoming file needs a valid Authenticode signature with a trusted chain, and its
    /// certificate subject must name AppInfo.SignerName. When the running copy is signed, the incoming
    /// subject must also name the running copy's publisher (CodeSignature.SamePublisher), and a running
    /// copy whose own signature is present but fails refuses every update. Thumbprints are never
    /// compared with the installed copy: a renewed certificate has a new thumbprint for the same
    /// publisher, and updates must keep working across the renewal. The thumbprint returned here is the
    /// verified file's own, which the publication helper pins the staged image to.
    /// </summary>
    internal static class ReleaseIdentity
    {
        /// <summary>Throws <see cref="SecurityException"/> unless <paramref name="exe"/> is a validly
        /// signed <paramref name="version"/> that is newer than the running copy and, when
        /// the running copy is signed, signed by the same publisher (any certificate of it). An unsigned running copy is a
        /// local build that cannot vouch for a publisher, so it accepts any validly signed release;
        /// <paramref name="requireInstalledSignature"/> removes that allowance for the elevated path.</summary>
        /// <returns>The thumbprint of the certificate that signed <paramref name="exe"/>, which the
        /// publication helper holds the published image to.</returns>
        internal static string Verify(string exe, string version, bool requireInstalledSignature)
        {
            var installed = CodeSignature.GetSignerInfo();
            var incoming = CodeSignature.GetSignerInfo(exe);
            AssemblyName name = ReadIdentity(exe);
            Verify(name, version, Assembly.GetExecutingAssembly().GetName().Version!,
                (installed.valid, installed.subject), (incoming.valid, incoming.subject), requireInstalledSignature);
            // KillerShell's same-publisher rule: a signed copy only takes a replacement from the same
            // publisher, and a copy whose own signature is present but fails is refused outright
            // instead of being treated as an unsigned local build.
            string current;
            using (var self = Process.GetCurrentProcess()) current = self.MainModule?.FileName ?? string.Empty;
            if (!CodeSignature.MayReplace(current, exe))
                throw new SecurityException("The update is not signed by the publisher of the installed copy.");
            PublisherChain.Verify(exe);
            return incoming.thumb;
        }

        /// <summary>The product name and version an update image declares in its version resource.
        /// A release exe can be a single-file bundle or an apphost, which carry no managed assembly
        /// header, so the name comes from the original file name the build stamps, the way
        /// VerifyPackage reads an installer.</summary>
        internal static AssemblyName ReadIdentity(string exe)
        {
            var file = FileVersionInfo.GetVersionInfo(exe);
            string original = Path.GetFileNameWithoutExtension(file.OriginalFilename ?? string.Empty);
            if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(file.FileVersion))
                throw new SecurityException("The update is not a " + AppInfo.DisplayName + " executable.");
            return new AssemblyName(original)
            {
                Version = new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart),
            };
        }

        /// <summary>Throws unless <paramref name="package"/> (an installer or launcher, not this
        /// app's assembly) has a valid signature whose subject names the publisher, from the same
        /// publisher as the running copy when that copy is signed (any certificate of it),
        /// and whose file version is the offered <paramref name="version"/>, newer than the running copy.</summary>
        /// <returns>The thumbprint of the certificate that signed it.</returns>
        internal static string VerifyPackage(string package, string version)
        {
            var file = FileVersionInfo.GetVersionInfo(package);
            VerifyPackageVersion(new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart),
                version, Assembly.GetExecutingAssembly().GetName().Version!);
            var installed = CodeSignature.GetSignerInfo();
            var incoming = CodeSignature.GetSignerInfo(package);
            VerifySigner((installed.valid, installed.subject), (incoming.valid, incoming.subject), requireInstalledSignature: false);
            string current;
            using (var self = Process.GetCurrentProcess()) current = self.MainModule?.FileName ?? string.Empty;
            if (!CodeSignature.MayReplace(current, package))
                throw new SecurityException("The update is not signed by the publisher of the installed copy.");
            PublisherChain.Verify(package);
            return incoming.thumb;
        }

        /// <summary>Throws unless the package's file version equals the offered <paramref name="version"/>
        /// and that version is newer than <paramref name="installed"/>, by the same rule Verify applies to the app's own exe.</summary>
        internal static void VerifyPackageVersion(Version package, string version, Version installed)
        {
            string requested = NormalizeVersion(version);
            if (NormalizeVersion(package.ToString()) != requested
                || Version.Parse(requested) <= Version.Parse(NormalizeVersion(installed.ToString())))
                throw new SecurityException("The update package's version does not match a newer " + AppInfo.DisplayName + " release.");
        }

        /// <summary>The pure form of Verify: signer subjects are the certificates' simple names, as
        /// CodeSignature.GetSignerInfo reports them.</summary>
        internal static void Verify(AssemblyName incoming, string version, Version installed,
            (bool valid, string subject) installedSigner, (bool valid, string subject) incomingSigner,
            bool requireInstalledSignature)
        {
            VerifySigner(installedSigner, incomingSigner, requireInstalledSignature);
            string requested = NormalizeVersion(version);
            if (incoming.Name != AppInfo.DisplayName || incoming.Version == null
                || NormalizeVersion(incoming.Version.ToString()) != requested
                || Version.Parse(requested) <= Version.Parse(NormalizeVersion(installed.ToString())))
                throw new SecurityException("The update's product or release version does not match a newer " + AppInfo.DisplayName + " release.");
        }

        /// <summary>Throws unless the incoming signature is valid, its subject names AppInfo.SignerName
        /// and, when the running copy is signed, it names the running copy's publisher. Never compares
        /// thumbprints, so a renewed certificate of the same publisher is accepted.</summary>
        internal static void VerifySigner((bool valid, string subject) installedSigner,
            (bool valid, string subject) incomingSigner, bool requireInstalledSignature)
        {
            if (requireInstalledSignature && !installedSigner.valid)
                throw new SecurityException("The installed update helper has no valid signature.");
            if (!incomingSigner.valid)
                throw new SecurityException("The update has no valid Authenticode signature.");
            if ((incomingSigner.subject ?? string.Empty).IndexOf(AppInfo.SignerName, StringComparison.OrdinalIgnoreCase) < 0)
                throw new SecurityException("The update is not signed by the publisher.");
            if (installedSigner.valid && !CodeSignature.SamePublisher(installedSigner.subject, incomingSigner.subject))
                throw new SecurityException("The update is not signed by the publisher of the installed copy.");
        }

        internal static string NormalizeVersion(string? text)
        {
            text = text?.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(text, out var version) || version.Build < 0) throw new SecurityException("The release version is invalid.");
            return version.ToString(3);
        }

        /// <summary>Accepts only an ordinary file on a local drive letter, so the elevated helper never
        /// opens a UNC, device, pipe or reparse-point path handed to it on its command line.</summary>
        internal static string RequireLocalFile(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 4
                || !char.IsLetter(full[0]) || full[1] != ':' || full[2] != '\\')
                throw new SecurityException("The update download must be a file on a local drive.");
            if (new DriveInfo(full.Substring(0, 3)).DriveType is DriveType.Network or DriveType.NoRootDirectory)
                throw new SecurityException("The update download must be a file on a local drive.");
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new SecurityException("The update download must be an ordinary file.");
            return full;
        }
    }
}
