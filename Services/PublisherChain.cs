using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace KillerPDF.Services
{
    /// <summary>
    /// The update path's origin check on top of WinVerifyTrust. A valid Authenticode chain to any
    /// trusted root proves little on its own: any code-signing CA can issue a certificate to someone
    /// else called Stephen Riley. So an update has to chain to a pinned root, be signed with one of
    /// the publisher's pinned public keys, and its certificate must not be revoked.
    ///
    /// The root is pinned, not the signing certificate: the certificate renews every year with a new
    /// thumbprint, and the installed copy is what checks the update, so a leaf pin would refuse every
    /// update after a renewal. Certum's root is valid until 2046. Moving to another CA means adding its
    /// root here one release before the first update signed under it. The key pin is the same set as
    /// the family kit's ReleaseIdentity.PublisherPublicKeys: a renewal on the same key keeps working,
    /// and a new key has to ship here in a release signed with an old one first.
    /// </summary>
    internal static class PublisherChain
    {
        /// <summary>Certum Trusted Network CA 2, the root above Certum Code Signing 2021 CA.</summary>
        internal static readonly IReadOnlySet<string> PinnedRoots = new HashSet<string>(
            ["D3DD483E2BBF4C05E8AF10F5FA7626CFD3DC3092"], StringComparer.OrdinalIgnoreCase);

        /// <summary>SHA-256 of the signing certificate's public key (X509Certificate.GetPublicKey),
        /// upper-case hex, as the kit computes it.</summary>
        internal static readonly IReadOnlySet<string> PinnedKeys = new HashSet<string>(
            ["E09F43EEE1DF45D4FDE291303F671C978DFC992E59FAD9DF29367B7223F0A83B"], StringComparer.OrdinalIgnoreCase);

        /// <summary>Throws <see cref="SecurityException"/> unless the signer of <paramref name="path"/>
        /// chains to a pinned root, carries a pinned key and is not revoked.</summary>
        internal static void Verify(string path)
        {
#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no X509CertificateLoader equivalent.
            using var source = X509Certificate.CreateFromSignedFile(path);
            using var signer = new X509Certificate2(source);
#pragma warning restore SYSLIB0057
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
            chain.Build(signer);
            Decide(chain.ChainElements.Select(element => element.Certificate.Thumbprint).LastOrDefault(),
                Convert.ToHexString(SHA256.HashData(signer.GetPublicKey())),
                chain.ChainStatus.Select(status => status.Status), IsElevated());
        }

        /// <summary>The pure decision. Revoked fails. Revocation that could not be checked, because a
        /// CRL or OCSP server was unreachable, passes for a per-user update, the way browsers
        /// soft-fail, so a CA outage cannot stop updates; WinVerifyTrust has already validated the
        /// chain itself. An elevated check refuses it, as the family kit's elevated paths do, because
        /// an elevated install writes to every account's copy.</summary>
        internal static void Decide(string? rootThumbprint, string? signerKeySha256,
            IEnumerable<X509ChainStatusFlags> statuses, bool elevated)
        {
            if (rootThumbprint is null || !PinnedRoots.Contains(rootThumbprint))
                throw new SecurityException("The update is not signed under the publisher's certificate authority.");
            if (string.IsNullOrEmpty(signerKeySha256) || !PinnedKeys.Contains(signerKeySha256.Trim()))
                throw new SecurityException("The update is not signed with the publisher's key.");
            var seen = statuses.ToList();
            if (seen.Contains(X509ChainStatusFlags.Revoked))
                throw new SecurityException("The update's signing certificate has been revoked.");
            if (elevated && (seen.Contains(X509ChainStatusFlags.RevocationStatusUnknown)
                || seen.Contains(X509ChainStatusFlags.OfflineRevocation)))
                throw new SecurityException("The update's signing certificate could not be checked for revocation.");
        }

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
