using System.Globalization;
using System.Text.Json;

namespace KillerPDF.Services
{
    internal sealed class SignatureStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        // A protected library is this header followed by the DPAPI blob; anything else is read as plain JSON.
        private static readonly byte[] ProtectedHeader = "KPSIG-DPAPI1\n"u8.ToArray();
        private static readonly byte[] Entropy = "KillerPDF signature library"u8.ToArray();

        private readonly string _dir;
        private readonly string _file;
        private readonly bool _protect;

        private static string DefaultDir => AppDataPaths.UserRoot;

        // A portable library travels between PCs and accounts, so only the installed one is tied to this user.
        public SignatureStore()
            : this(DefaultDir, AppDataPaths.SignaturesFile, protect: AppDataPaths.PortableRoot is null) { }

        internal SignatureStore(string dir, string file, bool protect = true)
        {
            _dir  = dir;
            _file = file;
            _protect = protect;
        }

        private static bool IsProtected(byte[] data) => data.AsSpan().StartsWith(ProtectedHeader);

        private static byte[] Seal(byte[] plain) =>
            [.. ProtectedHeader, .. UserDataProtection.Protect(plain, Entropy)];

        private static byte[] Open(byte[] data) => IsProtected(data)
            ? UserDataProtection.Unprotect(data[ProtectedHeader.Length..], Entropy)
            : data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? data[3..] : data;

        private List<SavedSignature> _items = [];

        public IReadOnlyList<SavedSignature> Signatures => _items;

        /// <summary>
        /// The path a damaged library was moved aside to by the last <see cref="Load"/>, or null.
        /// The library then starts empty, and the damaged file is never overwritten.
        /// </summary>
        public string? PreservedDamagedFile { get; private set; }

        public void Load()
        {
            PreservedDamagedFile = null;
            _items = [];
            if (!System.IO.File.Exists(_file)) return;
            try
            {
                var json = Open(System.IO.File.ReadAllBytes(_file));
                var items = JsonSerializer.Deserialize<List<SavedSignature>>(json)
                    ?? throw new JsonException("The signature library must be an array.");
                if (items.Any(item => !HasUsablePreview(item)))
                    throw new JsonException("The signature library contains an invalid entry.");
                _items = items;
            }
            catch (Exception ex) when (ex is JsonException or System.IO.IOException or UnauthorizedAccessException
                or NotSupportedException or System.Security.Cryptography.CryptographicException)
            {
                // Unreadable (including a library sealed for another Windows account) is not the same as absent: keep the bytes before anything replaces them.
                try
                {
                    string aside = _file + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    System.IO.File.Copy(_file, aside, overwrite: false);
                    PreservedDamagedFile = aside;
                }
                catch (Exception copyError) when (copyError is System.IO.IOException or UnauthorizedAccessException)
                {
                    PreservedDamagedFile = _file;   // could not copy, so the original is left in place
                }
            }
        }

        private static bool HasUsablePreview(SavedSignature? item)
        {
            if (item is null || !Enum.IsDefined(item.Kind)
                || !double.IsFinite(item.CanvasWidth) || item.CanvasWidth <= 0
                || !double.IsFinite(item.CanvasHeight) || item.CanvasHeight <= 0
                || !double.IsFinite(item.StrokeWidth) || item.StrokeWidth <= 0
                || item.Strokes is null
                || item.Strokes.Any(stroke => stroke is null || stroke.Any(point =>
                    point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y)))) return false;

            double previewScale = Math.Min(288 / item.CanvasWidth, 50 / item.CanvasHeight) * 0.9;
            if (!double.IsFinite(previewScale) || previewScale <= 0
                || !double.IsFinite(item.StrokeWidth * previewScale)) return false;
            double offsetX = (288 - item.CanvasWidth * previewScale) / 2;
            double offsetY = (50 - item.CanvasHeight * previewScale) / 2;
            bool FinitePreviewPoint(double x, double y) => double.IsFinite(x) && double.IsFinite(y)
                && double.IsFinite(x * previewScale + offsetX) && double.IsFinite(y * previewScale + offsetY);
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY)
                || item.Strokes.Any(stroke => stroke.Any(point => !FinitePreviewPoint(point.X, point.Y)))) return false;

            if (item.ImageData is not null)
            {
                try
                {
                    using var stream = new System.IO.MemoryStream(Convert.FromBase64String(item.ImageData));
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                        System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    return decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > 0
                        && decoder.Frames[0].PixelHeight > 0;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException
                    or System.IO.IOException or NotSupportedException or InvalidOperationException)
                { return false; }
            }

            if (item.Vectors is { } vectors)
            {
                if (vectors.Count == 0) return false;
                bool hasDrawableOutline = false;
                foreach (var vector in vectors)
                {
                    if (vector is null || string.IsNullOrWhiteSpace(vector.Data)
                        || !double.IsFinite(vector.StrokeWidth) || vector.StrokeWidth < 0
                        || !double.IsFinite(vector.StrokeWidth * previewScale)) return false;
                    try
                    {
                        var segments = SvgPathData.Parse(vector.Data);
                        if (segments.Any(segment => !FinitePreviewPoint(segment.X, segment.Y)
                                || !FinitePreviewPoint(segment.X1, segment.Y1)
                                || !FinitePreviewPoint(segment.X2, segment.Y2))) return false;
                        bool painted = (vector.Fill is uint fill && fill >> 24 != 0)
                            || (vector.Stroke is uint stroke && stroke >> 24 != 0 && vector.StrokeWidth > 0);
                        hasDrawableOutline |= painted && segments.Any(segment => segment.Verb is VectorVerb.Line or VectorVerb.Cubic);
                    }
                    catch (FormatException) { return false; }
                }
                return hasDrawableOutline;
            }

            return item.Strokes.Any(stroke => stroke.Count >= 2);
        }

        /// <summary>
        /// Writes the library through a sibling temp file and one replace, keeping the previous
        /// version as a .bak. False when it could not be saved; the in-memory list is unchanged.
        /// </summary>
        public bool Persist()
        {
            try
            {
                if (PreservedDamagedFile == _file) return false;   // never overwrite a damaged library we could not copy
                System.IO.Directory.CreateDirectory(_dir);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(_items, JsonOptions);
                if (System.IO.File.Exists(_file) && PreservedDamagedFile is null)
                {
                    // The backup of a plaintext library from before protection is sealed too.
                    byte[] previous = System.IO.File.ReadAllBytes(_file);
                    AtomicFileOutput.Write(_file + ".bak",
                        _protect && !IsProtected(previous) ? Seal(previous) : previous);
                }
                AtomicFileOutput.Write(_file, _protect ? Seal(json) : json);
                return true;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                or NotSupportedException or System.Security.Cryptography.CryptographicException)
            {
                return false;
            }
        }

        public void Add(SavedSignature sig) => _items.Add(sig);

        public void Remove(SavedSignature sig) => _items.Remove(sig);
    }
}
