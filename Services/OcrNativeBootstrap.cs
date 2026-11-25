using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KillerPDF.Services
{
    /// <summary>
    /// Keeps the single-exe build self-sufficient for OCR. The native Tesseract DLLs (x64) and the bundled
    /// language data are embedded as resources and self-extracted on first use, the same pattern Costura
    /// uses for the managed assemblies. Native libs go in a per-version cache (they must match the app);
    /// language data goes in a STABLE folder so user-downloaded packs survive app updates. Thread-safe.
    /// </summary>
    internal static partial class OcrNativeBootstrap
    {
        private const string NativePrefix = "KillerPDF.OcrNative.";
        private const string TessDataPrefix = "KillerPDF.OcrTessData.";
        private const string LeptonicaFileName = "leptonica-1.82.0.dll";
        private const string TesseractFileName = "tesseract50.dll";
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        private static readonly Lock _gate = new();
        private static bool _langReady;
        private static bool _nativeReady;

        [LibraryImport("kernel32", EntryPoint = "LoadLibraryExW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        /// <summary>
        /// Version-independent tessdata folder. The bundled English is extracted here on first use, and
        /// user-downloaded language packs are written here too, so they persist across app updates.
        /// </summary>
        public static string TessDataDir { get; } = AppDataPaths.TessDataDirectory;

        /// <summary>
        /// Ensures the bundled language data (English) is present in <see cref="TessDataDir"/> and returns
        /// that folder. Light - does not touch the native libraries, so it is safe to call just to inspect
        /// or list installed languages (e.g. when building the language menu).
        /// </summary>
        public static string EnsureLanguageData()
        {
            if (_langReady) return TessDataDir;
            lock (_gate)
            {
                if (_langReady) return TessDataDir;
                Directory.CreateDirectory(TessDataDir);

                var asm = typeof(OcrNativeBootstrap).Assembly;
                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (res.StartsWith(TessDataPrefix, StringComparison.Ordinal))
                    {
                        string file = res[TessDataPrefix.Length..];
                        ExtractResource(asm, res, Path.Combine(TessDataDir, file), onlyIfMissing: true);
                    }
                }

                _langReady = true;
                return TessDataDir;
            }
        }

        /// <summary>
        /// Extracts the native libs to a per-version cache, ensures language data, configures Tesseract's
        /// native loader, and returns the tessdata folder for OcrService. Call before constructing OcrService.
        /// </summary>
        public static string EnsureReady()
        {
            EnsureLanguageData();
            if (_nativeReady) return TessDataDir;
            lock (_gate)
            {
                if (_nativeReady) return TessDataDir;

                var asm = typeof(OcrNativeBootstrap).Assembly;
                string version = asm.GetName().Version?.ToString() ?? "0";
                string baseDir = Path.Combine(AppDataPaths.LocalRoot, "ocr", version);
                string nativeDir = Path.Combine(baseDir, "x64");
                Directory.CreateDirectory(nativeDir);

                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (res.StartsWith(NativePrefix, StringComparison.Ordinal))
                    {
                        string file = res[NativePrefix.Length..];
                        // Tesseract's loader looks in the x64 subfolder; the flat copy covers any loader
                        // path that does not append the platform name.
                        ExtractResource(asm, res, Path.Combine(nativeDir, file), onlyIfMissing: false);
                        ExtractResource(asm, res, Path.Combine(baseDir, file), onlyIfMissing: false);
                    }
                }

                // Point Tesseract's native loader at the cache. Reflection avoids a compile-time bind in
                // case the loader type's visibility differs across package versions; the preload below is
                // the hard guarantee regardless.
                try
                {
                    var loaderType = Type.GetType("InteropDotNet.LibraryLoader, Tesseract");
                    object? instance = loaderType?
                        .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
                        .GetValue(null);
                    loaderType?.GetProperty("CustomSearchPath")?.SetValue(instance, baseDir);
                }
                catch { /* fall through to the preload */ }

                // Load the exact bundled libraries without changing the process-wide DLL search path.
                // leptonica must load before tesseract50, which depends on it.
                LoadNativeLibrary(Path.Combine(nativeDir, LeptonicaFileName));
                LoadNativeLibrary(Path.Combine(nativeDir, TesseractFileName));

                _nativeReady = true;
                return TessDataDir;
            }
        }

        private static void ExtractResource(Assembly asm, string resourceName, string targetPath, bool onlyIfMissing)
        {
            // Language data is extracted only-if-missing: a user-downloaded pack (e.g. a high-quality model,
            // or an HQ English) must never be clobbered by the bundled copy on the next launch. Native libs
            // are hash-checked so a same-length replacement cannot be trusted.
            if (onlyIfMissing && File.Exists(targetPath)) return;

            using var src = asm.GetManifestResourceStream(resourceName);
            if (src == null) return;

            if (!onlyIfMissing && File.Exists(targetPath))
            {
                byte[] expectedHash = SHA256.HashData(src);
                using var existing = File.OpenRead(targetPath);
                byte[] existingHash = SHA256.HashData(existing);
                if (CryptographicOperations.FixedTimeEquals(expectedHash, existingHash)) return;
                src.Position = 0;
            }

            string tmp = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst);
                File.Move(tmp, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        private static void LoadNativeLibrary(string path)
        {
            const uint flags = LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32;
            if (LoadLibraryEx(path, IntPtr.Zero, flags) == IntPtr.Zero)
                throw new DllNotFoundException($"Could not load the bundled OCR library '{Path.GetFileName(path)}'. Windows error {Marshal.GetLastWin32Error()}.");
        }
    }
}
