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
        // Tesseract code -> display name, covering KillerPDF's 8 UI locales. English is bundled; the rest
        // are downloaded on demand into OcrNativeBootstrap.TessDataDir.
        // Order mirrors the Settings language picker (English first, then the
        // same sequence as the LangGroup radios in MainWindow.xaml).
        internal static readonly (string Code, string Name)[] OcrLanguageCatalog =
        [
            ("eng", "English"),
            ("ben", "Bengali"),
            ("ces", "Czech"),
            ("deu", "German"),
            ("spa", "Spanish"),
            ("fra", "French"),
            ("jpn", "Japanese"),
            ("tur", "Turkish"),
            ("chi_sim", "Chinese (Simplified)"),
            ("chi_tra", "Chinese (Traditional)"),
        ];

        // True if <code>.traineddata exists in the tessdata folder. Nothing is bundled now (not even English);
        // models are downloaded on demand, so this is a pure file-presence check.
        internal static bool IsLanguageInstalled(string code) =>
            File.Exists(Path.Combine(OcrNativeBootstrap.TessDataDir, code + ".traineddata"));

        // Download URL for a language's traineddata, honoring the caller's high-quality preference.
        // Standard tier uses tessdata_fast: the same integer LSTM model as the full "tessdata" repo but without
        // the unused legacy-engine data, so it is ~4MB instead of ~22MB with identical LSTM accuracy. HQ uses
        // tessdata_best (float LSTM): larger (~14MB) but the most accurate.
        internal static string LanguageDataUrl(string code, bool highQuality) => highQuality
            ? $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/{code}.traineddata"
            : $"https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/{code}.traineddata";

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
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-OCR");
            return http;
        }

        // Streams one traineddata file to destFile, reporting MB progress through the callback and honoring
        // the cancel token; writes via a .part file and atomically moves into place only on full success.
        // Throws on cancel/error. The GUI points the callback at the busy overlay's message line.
        internal static async Task DownloadTrainedDataAsync(System.Net.Http.HttpClient http, string url, string destFile,
            string label, Action<string> progress, CancellationToken ct)
        {
            string part = destFile + ".part";
            using (var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                // using-var (not a block): these dispose at the end of the resp block, before the File.Move below.
                using var netStream = await resp.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await netStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, n, ct);
                    read += n;
                    double mb = read / 1048576.0;
                    progress(total.HasValue
                        ? $"{label} {mb:F1} / {total.Value / 1048576.0:F1} MB  (Esc to cancel)"
                        : $"{label} {mb:F1} MB  (Esc to cancel)");
                }
            }
            if (File.Exists(destFile)) File.Delete(destFile);
            File.Move(part, destFile);
        }

        internal static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }
}
