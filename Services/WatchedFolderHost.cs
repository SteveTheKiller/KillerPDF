using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

internal static class WatchedFolderHost
{
    internal static WatchedFolderReadinessIssue? InspectReadiness(PdfMacro macro)
    {
        for (int index = 0; index < macro.Steps.Count; index++)
        {
            PdfMacroStep step = macro.Steps[index];
            if (step.Operation != PdfMacroOperation.Ocr) continue;
            string language = step.Settings?.GetValueOrDefault("language") ?? "eng";
            IReadOnlyList<string> missing = OcrModelFiles.MissingForCommonBackend(
                OcrNativeBootstrap.EnsureLanguageData(), language.Split('+'));
            if (missing.Count > 0) return new(index, missing);
        }
        return null;
    }

    internal static PdfMacroDispatcher CreateDispatcher(string storageDirectory) => new([
        new(PdfMacroOperation.Save, (_, bytes, _, token) => { token.ThrowIfCancellationRequested(); return new(bytes); }),
        new(PdfMacroOperation.Resize, PdfResizeMacro.Execute),
        new(PdfMacroOperation.ConvertColor, WorkingCmykProfiles.ConvertColor),
        new(PdfMacroOperation.Rename, PdfRenameMacro.Execute),
        new(PdfMacroOperation.Watermark, PdfEngineBurn.ExecuteWatermarkMacro),
        new(PdfMacroOperation.Ocr, (step, bytes, _, token) => Recognize(step, bytes, storageDirectory, token))]);

    private static PdfMacroOperationResult Recognize(PdfMacroStep step, ReadOnlyMemory<byte> source,
        string storageDirectory, CancellationToken token)
    {
        string language = step.Settings?.GetValueOrDefault("language") ?? "eng";
        bool formAware = bool.TryParse(step.Settings?.GetValueOrDefault("formAware"), out bool enabled) && enabled;
        string models = OcrNativeBootstrap.EnsureLanguageData();
        _ = OcrModelFiles.SelectProvider(models, language.Split('+'));
        token.ThrowIfCancellationRequested();
        string work = Path.Combine(storageDirectory, "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string input = Path.Combine(work, "input.pdf"), output = Path.Combine(work, "output.pdf");
        try
        {
            File.WriteAllBytes(input, source.ToArray());
            Features.OcrController.BuildSearchablePdf(input, output, (_, _) => { }, language, formAware, token);
            token.ThrowIfCancellationRequested();
            return new(File.ReadAllBytes(output));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
