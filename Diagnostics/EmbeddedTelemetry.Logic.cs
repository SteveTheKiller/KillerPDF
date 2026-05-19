using System;
using System.Security.Cryptography;
using System.Text;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Decryption logic for the optional build-time-embedded Application
    /// Insights connection string. Constants live in a sibling partial
    /// declaration — either the placeholder (<c>EmbeddedTelemetry.cs</c>,
    /// all-empty, tracked in git) or the build-time generated file
    /// (<c>EmbeddedTelemetry.Generated.cs</c>, gitignored, produced by
    /// <c>build\embed-telemetry-key.ps1</c> during <c>release.ps1</c>).
    ///
    /// Security framing — same "speed bump, not crypto" model as
    /// <see cref="TelemetryStore"/>: the AES key is split across two
    /// XOR-masked halves both compiled into TDPdf.exe, so a non-admin
    /// user on the device cannot dump the connection string with a
    /// three-line PowerShell, but a determined reverse engineer with
    /// the EXE can recover it. Pair with a dedicated App Insights
    /// resource, a daily ingestion cap, and key rotation.
    /// </summary>
    internal static partial class EmbeddedTelemetry
    {
        /// <summary>True if this build contains an embedded connection string.</summary>
        public static bool HasKey =>
            !string.IsNullOrEmpty(CiphertextB64) &&
            !string.IsNullOrEmpty(KeyPart1B64) &&
            !string.IsNullOrEmpty(KeyPart2B64) &&
            !string.IsNullOrEmpty(IvB64);

        /// <summary>
        /// Decrypts the embedded connection string. Returns null on any
        /// failure (no key, malformed constants, AES error). Never
        /// throws.
        /// </summary>
        public static string? TryDecrypt()
        {
            if (!HasKey) return null;

            byte[]? key = null;
            byte[]? plaintext = null;
            try
            {
                var ciphertext = Convert.FromBase64String(CiphertextB64);
                var k1 = Convert.FromBase64String(KeyPart1B64);
                var k2 = Convert.FromBase64String(KeyPart2B64);
                var iv = Convert.FromBase64String(IvB64);

                if (k1.Length != 32 || k2.Length != 32 || iv.Length != 16 || ciphertext.Length == 0)
                    return null;

                key = new byte[32];
                for (int i = 0; i < 32; i++)
                    key[i] = (byte)(k1[i] ^ k2[i]);

                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;

                using var dec = aes.CreateDecryptor();
                plaintext = dec.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
                return Encoding.UTF8.GetString(plaintext);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (key != null) Array.Clear(key, 0, key.Length);
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
        }
    }
}
