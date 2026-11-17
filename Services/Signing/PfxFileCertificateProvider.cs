using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace KillerPDF.Services.Signing
{
    /// <summary>
    /// Loads a signing certificate from a .pfx / .p12 file plus its password. Fully cross-platform,
    /// so this is the default certificate source on every OS.
    /// </summary>
    internal sealed class PfxFileCertificateProvider(string path, string password) : ICertificateProvider
    {
        public string DisplayName => Path.GetFileName(path);

        public X509Certificate2 GetCertificate()
            // Signing uses the key in place, so it is loaded without Exportable and cannot be
            // copied out of the process as raw key material.
            => X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
    }
}
