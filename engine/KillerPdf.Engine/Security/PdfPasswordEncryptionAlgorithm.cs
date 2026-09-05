namespace KillerPdf.Engine.Security;

/// <summary>Selects the authenticated encryption used for PDF password protection.</summary>
public enum PdfPasswordEncryptionAlgorithm
{
    /// <summary>AES-256 in CBC mode with revision 6 password authentication.</summary>
    Aes256Cbc,
    /// <summary>AES-256 in GCM mode with revision 7 password authentication.</summary>
    Aes256Gcm
}
