using System.Runtime.InteropServices;

namespace KillerPDF.Services;

/// <summary>
/// Windows DPAPI for the current user, so a file copied off this account cannot be read elsewhere.
/// Calls crypt32 directly rather than pulling in the ProtectedData package.
/// </summary>
internal static partial class UserDataProtection
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] plain, byte[] entropy) => Run(plain, entropy, protect: true);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the data
    /// was protected for another user or machine, or was altered.</summary>
    public static byte[] Unprotect(byte[] sealedData, byte[] entropy) => Run(sealedData, entropy, protect: false);

    private static unsafe byte[] Run(byte[] input, byte[] entropy, bool protect)
    {
        fixed (byte* inputPtr = input)
        fixed (byte* entropyPtr = entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (IntPtr)inputPtr };
            var entropyBlob = new DataBlob { Size = entropy.Length, Data = (IntPtr)entropyPtr };
            bool ok = protect
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
            if (!ok)
                throw new System.Security.Cryptography.CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally { LocalFree(outBlob.Data); }
        }
    }
}
