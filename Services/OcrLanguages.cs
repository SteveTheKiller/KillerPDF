using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KillerPDF.Services
{
    // ============================================================
    // OCR languages - the catalog, install checks and traineddata
    // downloads. Pure helpers over files, settings and HTTP; no
    // window state. Split out of Ocr.cs (KillerUI refactor).
    // ============================================================
    internal static class OcrLanguages
    {
        // The catalog itself lives in OcrCatalog.cs - pure data with no App, bootstrap or HTTP
        // dependency, so the test project can link that one file and check it against Strings\.
        // These forwarders keep every existing call site (OcrLanguageCatalog) working unchanged.
        internal static readonly (string Code, string Name)[] OcrLanguageCatalog = OcrCatalog.Languages;
        internal static readonly (string Locale, string Code)[] LocaleToOcrCode = OcrCatalog.LocaleToCode;

        // Engine and Tesseract models share the language-data folder. Either format can satisfy OCR.
        internal static bool IsLanguageInstalled(string code) =>
            OcrModelFiles.IsLanguageInstalled(OcrNativeBootstrap.TessDataDir, code);

        internal static bool IsTesseractLanguageInstalled(string code) =>
            OcrModelFiles.HasTesseractModel(OcrNativeBootstrap.TessDataDir, code);

        internal static IReadOnlyList<string> MissingLanguagesForOcr(IEnumerable<string> languages) =>
            OcrModelFiles.MissingForCommonBackend(OcrNativeBootstrap.TessDataDir, languages);

        // Download URL for a language's traineddata, honoring the caller's high-quality preference.
        // Standard tier uses tessdata_fast: the same integer LSTM model as the full "tessdata" repo but without
        // the unused legacy-engine data, so it is ~4MB instead of ~22MB with identical LSTM accuracy. HQ uses
        // tessdata_best (float LSTM): larger (~14MB) but the most accurate.
        // Both come from a fixed commit, not main, so a change pushed to either repository later
        // cannot reach a KillerPDF that already shipped. Moving to newer models means changing
        // these two hashes on purpose.
        internal const string TessdataBestCommit = "e12c65a915945e4c28e237a9b52bc4a8f39a0cec";
        internal const string TessdataFastCommit = "87416418657359cb625c412a48b6e1d6d41c29bd";

        internal static string LanguageDataUrl(string code, bool highQuality) => highQuality
            ? $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/{TessdataBestCommit}/{code}.traineddata"
            : $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/{TessdataFastCommit}/{code}.traineddata";

        internal static string NameForCode(string code)
        {
            foreach (var (c, n) in OcrLanguageCatalog) if (c == code) return n;
            return code;
        }

        // Tracks which installed languages currently hold the high-quality (best) model, so toggling HQ off
        // then on again doesn't re-download ones that are already HQ.
        internal static HashSet<string> GetHqLanguages()
        {
            var set = new HashSet<string>();
            foreach (var c in (App.GetSetting("OcrHqLanguages") ?? "").Split(['+'], StringSplitOptions.RemoveEmptyEntries))
                set.Add(c);
            return set;
        }

        internal static void MarkLanguageHq(string code, bool isHq)
        {
            var set = GetHqLanguages();
            if (isHq) set.Add(code); else set.Remove(code);
            App.SetSetting("OcrHqLanguages", string.Join("+", set));
        }

        internal static System.Net.Http.HttpClient MakeDownloadClient()
        {
            // Timeout covers connect + headers; the body is bounded by the cancellation token instead.
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-OCR");
            return http;
        }

        // Streams one traineddata file to destFile, reporting MB progress through the callback and honoring
        // the cancel token; writes via a .part file and atomically moves into place only on full success.
        // Throws on cancel/error. The GUI points the callback at the busy overlay's message line.
        internal static async Task DownloadTrainedDataAsync(System.Net.Http.HttpClient http, string url, string destFile,
            string label, string cancelHint, Action<string> progress, CancellationToken ct)
        {
            // The largest tessdata_best model is well under this; anything bigger is not a model.
            const long maximumBytes = 256L * 1024 * 1024;
            // A unique part name keeps two downloads of the same model from racing, and the
            // whole transfer has a deadline so a stalled server cannot hold the busy overlay open.
            string part = destFile + "." + Guid.NewGuid().ToString("N") + ".part";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            CancellationToken token = deadline.Token;
            try
            {
                using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token))
                {
                    resp.EnsureSuccessStatusCode();
                    long? total = resp.Content.Headers.ContentLength;
                    if (total is long declared && declared > maximumBytes) throw new HttpDownloadSizeException();
                    // using-var (not a block): these dispose at the end of the resp block, before the File.Move below.
                    using var netStream = await resp.Content.ReadAsStreamAsync(token);
                    using var fileStream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await netStream.ReadAsync(buffer, token)) > 0)
                    {
                        if (n > maximumBytes - read) throw new HttpDownloadSizeException();
                        await fileStream.WriteAsync(buffer.AsMemory(0, n), token);
                        read += n;
                        double mb = read / 1048576.0;
                        progress(total.HasValue
                            ? $"{label} {ByteSizeText.MegabytesOf(mb, total.Value / 1048576.0)}  {cancelHint}"
                            : $"{label} {ByteSizeText.Megabytes(mb)}  {cancelHint}");
                    }
                }
                File.Move(part, destFile, overwrite: true);
            }
            finally
            {
                TryDeleteFile(part);
            }
        }

        internal static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
