using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Validation;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Rendering;

if (args.Length == 3 && args[0] == "--render-one")
{
    try
    {
        string file = Path.GetFullPath(args[1]);
        if (!int.TryParse(args[2], out int size) || size < 1)
            throw new ArgumentException("Render size must be a positive integer.");
        byte[] source = File.ReadAllBytes(file);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);
        if (document.IsEncrypted)
        {
            string? credentialState = Environment.GetEnvironmentVariable(
                "KILLERPDF_CORPUS_CREDENTIAL_STATE");
            string? encodedPasswords = Environment.GetEnvironmentVariable(
                "KILLERPDF_CORPUS_PASSWORDS");
            string? encodedCertificate = Environment.GetEnvironmentVariable(
                "KILLERPDF_CORPUS_CERTIFICATE");
            if (credentialState == "unavailable")
            {
                Console.WriteLine($"KILLERPDF-RENDER\tskipped\t{Encode(
                    "Credentials are unavailable for this encrypted corpus file.")}");
                return 0;
            }
            if (encodedCertificate is not null)
            {
                CertificateCredential certificateCredential = JsonSerializer.Deserialize<
                    CertificateCredential>(Convert.FromBase64String(encodedCertificate))
                    ?? throw new InvalidDataException(
                        "The corpus certificate credential is invalid.");
                using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    certificateCredential.KeyStorePath, certificateCredential.Password,
                    X509KeyStorageFlags.EphemeralKeySet);
                document = PdfDocument.OpenWithCompatibilityRecovery(source, certificate);
            }
            else if (encodedPasswords is null)
            {
                Console.WriteLine($"KILLERPDF-RENDER\tskipped\t{Encode(
                    "No credential is registered for this encrypted corpus file.")}");
                return 0;
            }
            else
            {
                string[] passwords = JsonSerializer.Deserialize<string[]>(
                    Convert.FromBase64String(encodedPasswords))
                    ?? throw new InvalidDataException("The corpus password list is invalid.");
                Exception? lastError = null;
                foreach (string password in passwords)
                {
                    try
                    {
                        document = PdfDocument.OpenWithCompatibilityRecovery(source, password);
                        lastError = null;
                        break;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        lastError = error;
                    }
                }
                if (lastError is not null || !document.IsDecrypted)
                    throw new InvalidOperationException(
                        "The registered corpus credentials could not decrypt the PDF.", lastError);
            }
        }
        if (PdfPageInformation.Read(document).Count == 0)
            throw new InvalidDataException("The PDF contains no pages.");
        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(size, size,
                includeAnnotations: true, includeFormFields: true));
        string status = page.Diagnostics.Count == 0 ? "clean" : "diagnostic";
        string detail = string.Join(" | ", page.Diagnostics.Distinct(StringComparer.Ordinal));
        Console.WriteLine($"KILLERPDF-RENDER\t{status}\t{Encode(detail)}");
        return 0;
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        Console.WriteLine($"KILLERPDF-RENDER\tfailure\t{Encode(
            error.GetType().Name + ": " + error.Message)}");
        return 1;
    }

    static string Encode(string value) => Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes(value));
}

if (args.Length >= 2 && args[0] == "--render-corpus")
{
    string renderRoot = Path.GetFullPath(args[1]);
    if (!Directory.Exists(renderRoot))
    {
        Console.Error.WriteLine($"Directory not found: {renderRoot}");
        return 2;
    }
    int renderMaximum = int.MaxValue, timeoutSeconds = 30, size = 256;
    int parallelism = Math.Min(Environment.ProcessorCount, 8);
    string? passwordManifestPath = null;
    string? certificateManifestPath = null;
    for (int index = 2; index < args.Length; index++)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Render corpus option {args[index]} requires a value.");
            return 2;
        }
        if (args[index] == "--password-manifest")
        {
            passwordManifestPath = Path.GetFullPath(args[++index]);
            continue;
        }
        if (args[index] == "--certificate-manifest")
        {
            certificateManifestPath = Path.GetFullPath(args[++index]);
            continue;
        }
        if (!int.TryParse(args[index + 1], out int value) || value < 1)
        {
            Console.Error.WriteLine("Render corpus numeric options require a positive integer value.");
            return 2;
        }
        switch (args[index])
        {
            case "--max": renderMaximum = value; break;
            case "--timeout-seconds": timeoutSeconds = value; break;
            case "--size": size = value; break;
            case "--parallel": parallelism = value; break;
            default:
                Console.Error.WriteLine($"Unknown render corpus option: {args[index]}");
                return 2;
        }
        index++;
    }

    var certificatesByPath = new Dictionary<string, CertificateCredential>(
        StringComparer.OrdinalIgnoreCase);
    if (certificateManifestPath is not null)
    {
        if (!File.Exists(certificateManifestPath))
        {
            Console.Error.WriteLine(
                $"Certificate manifest not found: {certificateManifestPath}");
            return 2;
        }
        Dictionary<string, CertificateCredential> manifest = JsonSerializer.Deserialize<
            Dictionary<string, CertificateCredential>>(
                File.ReadAllText(certificateManifestPath))
            ?? throw new InvalidDataException(
                "The corpus certificate manifest is invalid.");
        string manifestDirectory = Path.GetDirectoryName(certificateManifestPath)
            ?? throw new InvalidDataException(
                "The corpus certificate manifest directory is unavailable.");
        foreach ((string path, CertificateCredential credential) in manifest)
        {
            string normalized = path.Replace('\\', '/');
            if (Path.IsPathRooted(path) || normalized.Split('/').Contains(".."))
                throw new InvalidDataException(
                    $"Corpus certificate paths must be relative and contained: {path}");
            if (credential is null || string.IsNullOrWhiteSpace(credential.KeyStorePath)
                || credential.Password is null || credential.Password.Length > 4096)
                throw new InvalidDataException(
                    $"Corpus certificate credential is invalid: {path}");
            string keyStorePath = Path.GetFullPath(
                Path.Combine(manifestDirectory, credential.KeyStorePath));
            if (!File.Exists(keyStorePath))
                throw new InvalidDataException(
                    $"Corpus certificate key store was not found: {credential.KeyStorePath}");
            if (!certificatesByPath.TryAdd(normalized,
                    credential with { KeyStorePath = keyStorePath }))
                throw new InvalidDataException(
                    $"Duplicate corpus certificate path: {path}");
        }
    }

    var passwordsByPath = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
    if (passwordManifestPath is not null)
    {
        if (!File.Exists(passwordManifestPath))
        {
            Console.Error.WriteLine($"Password manifest not found: {passwordManifestPath}");
            return 2;
        }
        Dictionary<string, string[]?> manifest = JsonSerializer.Deserialize<
            Dictionary<string, string[]?>>(File.ReadAllText(passwordManifestPath))
            ?? throw new InvalidDataException("The corpus password manifest is invalid.");
        foreach ((string path, string[]? candidates) in manifest)
        {
            string normalized = path.Replace('\\', '/');
            if (Path.IsPathRooted(path) || normalized.Split('/').Contains(".."))
                throw new InvalidDataException(
                    $"Corpus password paths must be relative and contained: {path}");
            if (candidates is { Length: 0 or > 32 }
                || candidates?.Any(password => password is null || password.Length > 4096) == true)
                throw new InvalidDataException(
                    $"Corpus password candidates exceed the configured limits: {path}");
            if (!passwordsByPath.TryAdd(normalized, candidates))
                throw new InvalidDataException($"Duplicate corpus password path: {path}");
        }
    }

    string[] renderFiles = [.. Directory.EnumerateFiles(
            renderRoot, "*.pdf", SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase).Take(renderMaximum)];
    int clean = 0, diagnostic = 0, skipped = 0, failure = 0, timedOut = 0, processed = 0;
    var signatures = new Dictionary<string, (int Count, List<string> Examples)>(
        StringComparer.Ordinal);
    var renderStarted = Stopwatch.StartNew();
    string executable = Environment.ProcessPath
        ?? throw new InvalidOperationException("The corpus executable path is unavailable.");
    await Parallel.ForEachAsync(renderFiles,
        new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (file, _) =>
    {
        string relative = Path.GetRelativePath(renderRoot, file);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--render-one");
        start.ArgumentList.Add(file);
        start.ArgumentList.Add(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.Environment.Remove("KILLERPDF_CORPUS_CREDENTIAL_STATE");
        start.Environment.Remove("KILLERPDF_CORPUS_PASSWORDS");
        start.Environment.Remove("KILLERPDF_CORPUS_CERTIFICATE");
        string credentialPath = relative.Replace('\\', '/');
        if (certificatesByPath.TryGetValue(
                credentialPath, out CertificateCredential? certificateCredential))
            start.Environment["KILLERPDF_CORPUS_CERTIFICATE"] = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(certificateCredential));
        else if (passwordsByPath.TryGetValue(credentialPath, out string[]? passwords))
        {
            if (passwords is null)
                start.Environment["KILLERPDF_CORPUS_CREDENTIAL_STATE"] = "unavailable";
            else
                start.Environment["KILLERPDF_CORPUS_PASSWORDS"] = Convert.ToBase64String(
                    JsonSerializer.SerializeToUtf8Bytes(passwords));
        }
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The render worker could not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task completed = await Task.WhenAny(process.WaitForExitAsync(),
            Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        string status, detail;
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await outputTask;
            await errorTask;
            status = "timeout";
            detail = $"Exceeded {timeoutSeconds} seconds.";
            Interlocked.Increment(ref timedOut);
        }
        else
        {
            string output = await outputTask;
            string error = await errorTask;
            string? resultLine = output.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.StartsWith(
                    "KILLERPDF-RENDER\t", StringComparison.Ordinal));
            string[] fields = resultLine?.Split('\t', 3) ?? [];
            if (fields.Length != 3)
            {
                status = "failure";
                detail = error.Trim().Length > 0 ? error.Trim()
                    : "The render worker returned no result.";
            }
            else
            {
                status = fields[1];
                detail = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(fields[2]));
            }
            if (status == "clean") Interlocked.Increment(ref clean);
            else if (status == "diagnostic") Interlocked.Increment(ref diagnostic);
            else if (status == "skipped") Interlocked.Increment(ref skipped);
            else Interlocked.Increment(ref failure);
        }
        if (status != "clean") AddSignature(status, detail, relative);
        int completedCount = Interlocked.Increment(ref processed);
        if (completedCount % 100 == 0 || completedCount == renderFiles.Length)
            Console.WriteLine($"Rendered {completedCount:N0}/{renderFiles.Length:N0}: "
                + $"{clean:N0} clean, {diagnostic:N0} diagnostic, "
                + $"{skipped:N0} skipped, {failure:N0} failed, {timedOut:N0} timed out.");
    });
    foreach ((string signature, (int count, List<string> examples)) in signatures
                 .OrderByDescending(entry => entry.Value.Count)
                 .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        Console.WriteLine($"{count:N0} x {signature}: {string.Join(" | ", examples)}");
    Console.WriteLine($"Render corpus completed in {renderStarted.Elapsed.TotalSeconds:N1}s: "
        + $"{clean:N0} clean, {diagnostic:N0} diagnostic, {skipped:N0} skipped, "
        + $"{failure:N0} failed, {timedOut:N0} timed out.");
    return diagnostic == 0 && failure == 0 && timedOut == 0 ? 0 : 1;

    void AddSignature(string status, string detail, string relative)
    {
        string signature = status + ": " + detail;
        lock (signatures)
        {
            if (!signatures.TryGetValue(signature, out var current))
                current = (0, []);
            if (current.Examples.Count < 3) current.Examples.Add(relative);
            signatures[signature] = (current.Count + 1, current.Examples);
        }
    }
}

if (args.Length >= 3 && args[0] == "--ocr-train-corpus")
{
    string ocrRoot = Path.GetFullPath(args[1]);
    string modelPath = Path.GetFullPath(args[2]);
    if (!Directory.Exists(ocrRoot))
    {
        Console.Error.WriteLine($"Directory not found: {ocrRoot}");
        return 2;
    }
    if (File.Exists(modelPath))
    {
        Console.Error.WriteLine($"Output already exists: {modelPath}");
        return 2;
    }
    int ocrMaximum = int.MaxValue, renderSize = 1600, pagesPerFile = 1;
    int holdoutPercent = 10, modelWidth = 32, modelHeight = 32;
    string? ocrLabelFilePath = null;
    string? ocrPasswordManifestPath = null;
    string? ocrCertificateManifestPath = null;
    for (int index = 3; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"OCR corpus option {args[index]} requires a value.");
            return 2;
        }
        if (args[index] == "--password-manifest")
        {
            ocrPasswordManifestPath = Path.GetFullPath(args[index + 1]);
            continue;
        }
        if (args[index] == "--certificate-manifest")
        {
            ocrCertificateManifestPath = Path.GetFullPath(args[index + 1]);
            continue;
        }
        if (args[index] == "--label-file")
        {
            ocrLabelFilePath = Path.GetFullPath(args[index + 1]);
            continue;
        }
        if (!int.TryParse(args[index + 1], out int value) || value < 1)
        {
            Console.Error.WriteLine("OCR corpus options require a positive integer value.");
            return 2;
        }
        switch (args[index])
        {
            case "--max": ocrMaximum = value; break;
            case "--size": renderSize = value; break;
            case "--pages-per-file": pagesPerFile = value; break;
            case "--holdout-percent" when value < 100: holdoutPercent = value; break;
            case "--model-width" when value <= 128: modelWidth = value; break;
            case "--model-height" when value <= 128: modelHeight = value; break;
            default:
                Console.Error.WriteLine($"Unknown or invalid OCR corpus option: {args[index]}");
                return 2;
        }
    }

    Dictionary<string, string[]?> ocrPasswords = ReadPasswordManifest(
        ocrPasswordManifestPath);
    Dictionary<string, CertificateCredential> ocrCertificates =
        ReadCertificateManifest(ocrCertificateManifestPath);
    HashSet<string>? ocrLabels = ReadLabelFile(ocrLabelFilePath);

    string[] ocrFiles = [.. Directory.EnumerateFiles(
            ocrRoot, "*.pdf", SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase).Take(ocrMaximum)];
    if (ocrFiles.Length == 0)
    {
        Console.Error.WriteLine("The OCR corpus contains no PDF files.");
        return 2;
    }
    var trainingStarted = Stopwatch.StartNew();
    int trainingSampleCount = 0;
    PdfOcrRecognitionModel ocrModel;
    try
    {
        ocrModel = PdfOcrModelTrainer.Train(
            modelWidth, modelHeight, Samples(selectHoldout: false, reportFailures: true));
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"OCR training failed: {error.GetType().Name}: {error.Message}");
        return 1;
    }
    PdfOcrModelEvaluation evaluation;
    try
    {
        evaluation = PdfOcrModelTrainer.Evaluate(
            ocrModel, Samples(selectHoldout: true, reportFailures: false));
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"OCR holdout evaluation failed: "
            + $"{error.GetType().Name}: {error.Message}");
        return 1;
    }
    byte[] modelBytes = ocrModel.Save();
    Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
    try
    {
        using var output = new FileStream(modelPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        output.Write(modelBytes);
    }
    catch (IOException error)
    {
        Console.Error.WriteLine($"OCR model output failed: {error.Message}");
        return 1;
    }
    Console.WriteLine($"OCR model: {ocrModel.Labels.Count:N0} labels, "
        + $"{modelBytes.Length:N0} bytes, "
        + $"{trainingSampleCount:N0} training samples, "
        + $"{evaluation.SampleCount:N0} holdout samples, "
        + $"{evaluation.Accuracy:P2} accuracy, "
        + $"{evaluation.AverageConfidence:P2} average confidence.");
    foreach (PdfOcrConfusion confusion in evaluation.Confusion
                 .Where(item => item.Expected != item.Predicted)
                 .OrderByDescending(item => item.Count).Take(25))
        Console.WriteLine($"  {confusion.Count:N0} x {confusion.Expected} -> {confusion.Predicted}");
    Console.WriteLine($"OCR corpus training completed in "
        + $"{trainingStarted.Elapsed.TotalSeconds:N1}s: {modelPath}");
    return 0;

    IEnumerable<PdfOcrTrainingSample> Samples(bool selectHoldout, bool reportFailures)
    {
        var options = new PdfOcrOptions(["und"], deskew: false,
            correctOrientation: false, removeBackground: true, removeNoise: true,
            detectPageSegments: false);
        for (int fileIndex = 0; fileIndex < ocrFiles.Length; fileIndex++)
        {
            string file = ocrFiles[fileIndex];
            string relative = Path.GetRelativePath(ocrRoot, file);
            bool isHoldout = PdfOcrTrainingPartition.IsHoldout(relative, holdoutPercent);
            if (isHoldout != selectHoldout)
            {
                ReportProgress();
                continue;
            }
            IReadOnlyList<PdfOcrTrainingSample>[] pages;
            try
            {
                PdfDocument document = OpenOcrDocument(
                    File.ReadAllBytes(file), relative);
                IReadOnlyList<PdfPageInformation> information = PdfPageInformation.Read(document);
                int pageCount = Math.Min(information.Count, pagesPerFile);
                pages = new IReadOnlyList<PdfOcrTrainingSample>[pageCount];
                for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    PdfPageInformation page = information[pageIndex];
                    bool quarterTurn = page.Rotation is 90 or 270;
                    double pageWidth = quarterTurn ? page.Height : page.Width;
                    double pageHeight = quarterTurn ? page.Width : page.Height;
                    double scale = renderSize / Math.Max(pageWidth, pageHeight);
                    int width = Math.Max(1, (int)Math.Round(pageWidth * scale));
                    int height = Math.Max(1, (int)Math.Round(pageHeight * scale));
                    pages[pageIndex] = PdfOcrModelTrainer.CreatePageSamples(
                        document, pageIndex,
                        new PdfRenderOptions(width, height, includeAnnotations: false,
                            includeFormFields: false),
                        options, modelWidth, modelHeight);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (reportFailures)
                    Console.WriteLine($"SKIP  {relative}: "
                        + $"{error.GetType().Name}: {error.Message}");
                continue;
            }
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
                for (int sampleIndex = 0; sampleIndex < pages[pageIndex].Count; sampleIndex++)
                {
                    PdfOcrTrainingSample sample = pages[pageIndex][sampleIndex];
                    if (Encoding.UTF8.GetByteCount(sample.Label) > 64) continue;
                    if (ocrLabels is not null && !ocrLabels.Contains(sample.Label)) continue;
                    if (!selectHoldout) trainingSampleCount++;
                    yield return sample;
                }
            ReportProgress();

            void ReportProgress()
            {
                if (reportFailures && ((fileIndex + 1) % 100 == 0
                    || fileIndex + 1 == ocrFiles.Length))
                    Console.WriteLine(
                        $"Sampled {fileIndex + 1:N0}/{ocrFiles.Length:N0} PDF files.");
            }
        }
    }

    PdfDocument OpenOcrDocument(byte[] source, string relative)
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);
        if (!document.IsEncrypted) return document;
        string normalized = relative.Replace('\\', '/');
        if (ocrCertificates.TryGetValue(normalized,
                out CertificateCredential? certificateCredential))
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificateCredential.KeyStorePath, certificateCredential.Password,
                X509KeyStorageFlags.EphemeralKeySet);
            return PdfDocument.OpenWithCompatibilityRecovery(source, certificate);
        }
        if (!ocrPasswords.TryGetValue(normalized, out string[]? passwords)
            || passwords is null)
            throw new InvalidOperationException(
                "No credential is registered for this encrypted OCR corpus file.");
        Exception? lastError = null;
        foreach (string password in passwords)
        {
            try { return PdfDocument.OpenWithCompatibilityRecovery(source, password); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                lastError = error;
            }
        }
        throw new InvalidOperationException(
            "The registered OCR corpus credentials could not decrypt the PDF.", lastError);
    }

    static Dictionary<string, string[]?> ReadPasswordManifest(string? path)
    {
        var result = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);
        if (path is null) return result;
        if (!File.Exists(path)) throw new FileNotFoundException(
            "The OCR corpus password manifest was not found.", path);
        Dictionary<string, string[]?> manifest = JsonSerializer.Deserialize<
            Dictionary<string, string[]?>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The OCR corpus password manifest is invalid.");
        foreach ((string relative, string[]? candidates) in manifest)
        {
            string normalized = relative.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || normalized.Split('/').Contains("..")
                || candidates is { Length: 0 or > 32 }
                || candidates?.Any(password => password is null
                    || password.Length > 4096) == true
                || !result.TryAdd(normalized, candidates))
                throw new InvalidDataException(
                    $"OCR corpus password credential is invalid: {relative}");
        }
        return result;
    }

    static HashSet<string>? ReadLabelFile(string? path)
    {
        if (path is null) return null;
        if (!File.Exists(path)) throw new FileNotFoundException(
            "The OCR label file was not found.", path);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (string supplied in File.ReadLines(path))
        {
            string label = supplied.Trim();
            if (label.Length == 0) continue;
            if (Encoding.UTF8.GetByteCount(label) > 64
                || label.EnumerateRunes().Any(rune => rune == Rune.ReplacementChar
                    || Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
                || !labels.Add(label))
                throw new InvalidDataException(
                    $"OCR training label is invalid or duplicated: {label}");
        }
        if (labels.Count == 0)
            throw new InvalidDataException("The OCR label file contains no labels.");
        return labels;
    }

    static Dictionary<string, CertificateCredential> ReadCertificateManifest(string? path)
    {
        var result = new Dictionary<string, CertificateCredential>(
            StringComparer.OrdinalIgnoreCase);
        if (path is null) return result;
        if (!File.Exists(path)) throw new FileNotFoundException(
            "The OCR corpus certificate manifest was not found.", path);
        Dictionary<string, CertificateCredential> manifest = JsonSerializer.Deserialize<
            Dictionary<string, CertificateCredential>>(File.ReadAllText(path))
            ?? throw new InvalidDataException(
                "The OCR corpus certificate manifest is invalid.");
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException(
                "The OCR corpus certificate manifest directory is unavailable.");
        foreach ((string relative, CertificateCredential credential) in manifest)
        {
            string normalized = relative.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || normalized.Split('/').Contains("..")
                || credential is null || string.IsNullOrWhiteSpace(credential.KeyStorePath)
                || credential.Password is null || credential.Password.Length > 4096)
                throw new InvalidDataException(
                    $"OCR corpus certificate credential is invalid: {relative}");
            string keyStorePath = Path.GetFullPath(
                Path.Combine(directory, credential.KeyStorePath));
            if (!File.Exists(keyStorePath)
                || !result.TryAdd(normalized,
                    credential with { KeyStorePath = keyStorePath }))
                throw new InvalidDataException(
                    $"OCR corpus certificate credential is invalid: {relative}");
        }
        return result;
    }

}

if (args.Length == 2 && args[0] == "--incremental-xref-stream-smoke")
{
    byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
    var firstUpdate = new PdfIncrementalUpdateBuilder(PdfDocument.Open(source));
    PdfIndirectReference temporary = firstUpdate.AddObject(new PdfInteger(18));
    PdfDocument document = PdfDocument.Open(firstUpdate.Build());
    var update = new PdfIncrementalUpdateBuilder(document).FreeObject(temporary.ObjectNumber);
    update.AddObject(new PdfString("incremental xref stream"u8, PdfStringForm.Literal));
    byte[] pdf = update.Build(new PdfIncrementalUpdateWriteOptions
    {
        CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
        CompressCrossReferenceStream = true,
        UseObjectStreams = true,
        CompressObjectStreams = true
    });
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte incremental xref-stream PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--pdfua-link-smoke")
{
    var content = new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Note, 1)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Quote, 2)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Reference, 3)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Code, 4)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.TableHeaderCell, 5)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.TableDataCell, 6)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Label, 7)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.ListBody, 8)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Heading1, 9)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Heading2, 10)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Heading3, 11)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Heading4, 12)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Heading5, 13)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Heading6, 14)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Span, 15)
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Formula, 16)
        .EndMarkedContent();
    PdfImage reviewImage = PdfImage.FromRgb(1, 1, new byte[] { 30, 100, 200 });
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF accessible link smoke test",
            Language = "en-US"
        })
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddNamedDestination("review", 0, PdfDestination.At(top: 720))
        .SetOpenAction(0, PdfDestination.At(top: 720))
        .AddBookmark("Accessible review", 0)
        .AddUriLink(0, 72, 700, 180, 24, "https://killerpdf.net",
            contents: "Open the KillerPDF website")
        .AddTextNote(0, 72, 650, "Review the accessible link")
        .AddHighlight(0, 72, 610, 180, 18, "Highlighted accessible text")
        .AddLineAnnotation(0, new PdfPoint(72, 570), new PdfPoint(250, 570),
            contents: "Accessible review line")
        .AddRectangleAnnotation(0, 72, 520, 180, 30,
            contents: "Accessible review rectangle")
        .AddCaretAnnotation(0, 72, 480, 18, 24,
            contents: "Accessible insertion point")
        .AddRedactionMark(0,
            [new PdfTextQuad(
                new PdfPoint(72, 460), new PdfPoint(252, 460),
                new PdfPoint(72, 440), new PdfPoint(252, 440))],
            contents: "Accessible proposed redaction")
        .AddImageStamp(0, 110, 395, 24, 24, reviewImage,
            contents: "Accessible blue review image")
        .AddAttachment("review.txt", "Accessible review evidence"u8.ToArray(),
            "text/plain", "Plain-text review evidence")
        .AddFileAttachmentAnnotation(0, 72, 395, 24, "review.txt",
            contents: "Open the plain-text review evidence")
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "Accessible review destination")
        .AddStructureElement(PdfStructureType.Note, 0, 1, 1,
            actualText: "Accessible review note")
        .AddStructureContainer(PdfStructureType.Article, 1)
        .AddStructureContainer(PdfStructureType.Section, 2)
        .AddStructureContainer(PdfStructureType.Paragraph, 3)
        .AddStructureElement(PdfStructureType.Quote, 0, 2, 4,
            actualText: "Accessible quoted text")
        .AddStructureElement(PdfStructureType.Reference, 0, 3, 4,
            actualText: "Accessible reference")
        .AddStructureElement(PdfStructureType.Code, 0, 4, 4,
            actualText: "Accessible code")
        .AddStructureContainer(PdfStructureType.Part, 1)
        .AddStructureContainer(PdfStructureType.Division, 2)
        .AddStructureElement(PdfStructureType.Heading1, 0, 9, 3,
            actualText: "Heading level one")
        .AddStructureElement(PdfStructureType.Heading2, 0, 10, 3,
            actualText: "Heading level two")
        .AddStructureElement(PdfStructureType.Heading3, 0, 11, 3,
            actualText: "Heading level three")
        .AddStructureElement(PdfStructureType.Heading4, 0, 12, 3,
            actualText: "Heading level four")
        .AddStructureElement(PdfStructureType.Heading5, 0, 13, 3,
            actualText: "Heading level five")
        .AddStructureElement(PdfStructureType.Heading6, 0, 14, 3,
            actualText: "Heading level six")
        .AddStructureContainer(PdfStructureType.Paragraph, 3)
        .AddStructureElement(PdfStructureType.Span, 0, 15, 4,
            actualText: "Accessible inline span")
        .AddStructureElement(PdfStructureType.Formula, 0, 16, 3,
            alternateDescription: "x squared plus y squared")
        .AddStructureContainer(PdfStructureType.Table, 1)
        .AddStructureContainer(PdfStructureType.TableRow, 2)
        .AddStructureElement(PdfStructureType.TableHeaderCell, 0, 5, 3,
            actualText: "Header")
        .AddStructureElement(PdfStructureType.TableDataCell, 0, 6, 3,
            actualText: "Value")
        .AddStructureContainer(PdfStructureType.List, 1,
            listNumbering: PdfListNumbering.Decimal)
        .AddStructureContainer(PdfStructureType.ListItem, 2)
        .AddStructureElement(PdfStructureType.Label, 0, 7, 3,
            actualText: "1.")
        .AddStructureElement(PdfStructureType.ListBody, 0, 8, 3,
            actualText: "Accessible list item")
        .Build();
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/UA link PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--pdfua-form-smoke")
{
    static PdfFormFieldMetadata Metadata(string tooltip) => new() { Tooltip = tooltip };
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF accessible form smoke test",
            Language = "en-US"
        })
        .EnablePdfUa2Conformance()
        .AddBlankPage()
        .AddCheckBox(0, "survey.accepted", 72, 700, 18, 18, isChecked: true,
            fieldMetadata: Metadata("Accept the survey terms"))
        .AddRadioGroup("survey.plan",
        [
            new PdfRadioButtonOption(0, 72, 650, 18, 18, "Free"),
            new PdfRadioButtonOption(0, 120, 650, 18, 18, "Pro")
        ], "Pro", fieldMetadata: Metadata("Choose a survey plan"))
        .AddSignatureField(0, "survey.signature", 72, 580, 180, 42,
            fieldMetadata: Metadata("Sign the completed survey"))
        .AddStructureContainer(PdfStructureType.Document)
        .Build();
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/UA form PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--pdfua-incremental-form-smoke")
{
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF accessible incremental form smoke test",
            Language = "en-US"
        })
        .EnablePdfUa2Conformance()
        .AddBlankPage()
        .AddStructureContainer(PdfStructureType.Document)
        .Build();
    byte[] added = new PdfIncrementalPageEditor(PdfDocument.Open(source))
        .AddCheckBox(0, "survey.accepted", 72, 700, 18, 18,
            fieldMetadata: new PdfFormFieldMetadata
            {
                Tooltip = "Accept the survey terms"
            })
        .AddSignatureField(0, "survey.signature", 72, 580, 180, 42,
            fieldMetadata: new PdfFormFieldMetadata
            {
                Tooltip = "Sign the completed survey"
            })
        .Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(added))
        .RemoveFormField("survey.accepted")
        .Build();
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine(
        $"Wrote {pdf.Length:N0} byte incremental PDF/UA form PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--font-info")
{
    TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(args[1]));
    Console.WriteLine($"{font.PostScriptName}: {font.GlyphCount:N0} glyphs, {font.UnitsPerEm} UPM");
    Console.WriteLine($"Embedding: {font.EmbeddingAllowed}; subsetting: {font.SubsettingAllowed}; U+0041 -> {font.GetGlyphId('A')}");
    return 0;
}

if (args.Length == 3 && args[0] == "--unicode-smoke")
{
    TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(args[1]));
    var content = new PdfContentStreamBuilder()
        .BeginText()
        .SetFont(font, 24)
        .MoveText(72, 720)
        .ShowUnicodeText("KillerPDF café Ω")
        .EndText();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF Unicode smoke test", Language = "en-US" })
        .AddPage(612, 792, content).Build();
    string destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} bytes with embedded {font.PostScriptName} to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--text-state-smoke")
{
    TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(args[1]));
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[2]));
    string destination = Path.GetFullPath(args[3]);
    var content = new PdfContentStreamBuilder()
        .BeginArtifact()
        .SetStrokeRgb(0.2, 0.7, 0.8).SetLineWidth(2)
        .MoveTo(60, 520).CurveTo(120, 580, 180, 520).CurveToFinalControl(240, 460, 300, 520)
        .CloseAndStroke()
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Paragraph, 0)
        .BeginText().SetFont(font, 28)
        .SetTextMatrix(0.966, 0.259, -0.259, 0.966, 90, 650)
        .SetCharacterSpacing(0.3).SetWordSpacing(1.2).SetHorizontalTextScale(94)
        .SetTextRise(2).SetTextRenderingMode(PdfTextRenderingMode.Fill)
        .ShowPositionedUnicodeText(["Killer", "PDF"], [-45])
        .SetTextLeading(40).MoveToNextTextLine().SetTextRise(0)
        .ShowUnicodeText("positioned text")
        .EndText().EndMarkedContent();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF positioned text smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance().EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .SetPageBox(0, PdfPageBox.Crop, 18, 18, 576, 756)
        .SetPageBox(0, PdfPageBox.Bleed, 12, 12, 588, 768)
        .SetPageBox(0, PdfPageBox.Trim, 24, 24, 564, 744)
        .SetPageLayout(PdfPageLayout.SinglePage)
        .SetPageMode(PdfPageMode.UseNone)
        .SetViewerPreferences(new PdfViewerPreferences
        {
            FitWindow = true,
            CenterWindow = true,
            DisplayDocumentTitle = true
        })
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Paragraph, 0, 0, 1)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte positioned-text PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--image-smoke")
{
    PdfImage image = PdfImage.FromJpeg(File.ReadAllBytes(args[1]));
    double width = 468;
    double height = width * image.Height / image.Width;
    var content = new PdfContentStreamBuilder().DrawImage(image, 72, 720 - height, width, height);
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF image smoke test", Language = "en-US" })
        .AddPage(612, 792, content).Build();
    string destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} bytes with {image.Width}x{image.Height} JPEG to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--output-intent-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    var content = new PdfContentStreamBuilder()
        .SetFillRgb(0.9, 0.2, 0.4).Rectangle(72, 600, 240, 100).Fill();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF output intent smoke test", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddPage(612, 792, content)
        .Build();
    string destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} bytes with {profile.ColorSpace} output intent to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--presentation-effects-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    PdfImage thumbnail = PdfImage.FromRgba(2, 2, new byte[] {
        30, 110, 210, 255, 240, 90, 60, 210,
        240, 90, 60, 210, 30, 110, 210, 255 });
    var content = new PdfContentStreamBuilder()
        .SetFillRgb(0.08, 0.15, 0.3).Rectangle(72, 540, 468, 160).Fill()
        .DrawImage(thumbnail, 260, 580, 92, 92);
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF presentation effects smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddPage(612, 792, content)
        .SetPageThumbnail(0, thumbnail)
        .SetPageDisplayDuration(0, 8)
        .SetPageTransition(0, PdfPageTransition.Fly(
            90, PdfTransitionMotion.Outward, 0.7, opaque: true, duration: 1.5))
        .AddUriLink(0, 210, 520, 192, 28, "https://killerpdf.net",
            new PdfLinkAppearance(2, PdfLinkBorderStyle.Dashed, [5, 2],
                new PdfRgbColor(0.15, 0.55, 0.95), PdfLinkHighlightMode.Push))
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte presentation-effects PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--cmyk-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    if (profile.ColorSpace != "CMYK")
        throw new ArgumentException("The CMYK smoke test requires a CMYK ICC profile.");
    string destination = Path.GetFullPath(args[2]);
    var stencil = new PdfTilingPattern(18, 18,
        new PdfContentStreamBuilder().Rectangle(2, 2, 7, 7).Fill(),
        paintType: PdfTilingPatternPaintType.Uncolored);
    var spot = new PdfSpotColor("Killer Orange", new PdfCmykColor(0, 0.72, 1, 0));
    var content = new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .SetRenderingIntent(PdfRenderingIntent.RelativeColorimetric)
        .SetFlatnessTolerance(0.75)
        .SetFillCmyk(0.85, 0.2, 0, 0.1).Rectangle(72, 540, 210, 150).Fill()
        .SetFillPattern(stencil, new PdfCmykColor(0, 0.8, 0.9, 0.05))
        .Rectangle(330, 540, 210, 150).Fill()
        .SetFillSpotColor(spot, 0.75).Rectangle(246, 480, 120, 36).Fill()
        .EndMarkedContent();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF CMYK authoring smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "CMYK press profile")
        .EnablePdfA4Conformance()
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "CMYK solid and stencil-pattern samples")
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte CMYK PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--pdfa4f-attachment-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF PDF/A-4f attachment smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4fConformance()
        .AddBlankPage()
        .AddAttachment("evidence.txt", "KillerPDF PDF/A-4f attachment"u8.ToArray(),
            "text/plain", "PDF/A-4f validation payload", PdfAssociatedFileRelationship.Data,
            DateTimeOffset.UtcNow)
        .AddFileAttachmentAnnotation(0, 72, 680, 28, "evidence.txt",
            "Open the attached validation evidence", PdfFileAttachmentIcon.Paperclip,
            annotationMetadata: new PdfAnnotationMetadata
            {
                Author = "KillerPDF",
                Subject = "Validation evidence"
            })
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/A-4f attachment PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--pdfa4e-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF PDF/A-4e engineering smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4eConformance()
        .AddPage(612, 792, new PdfContentStreamBuilder()
            .SetStrokeRgb(0.2, 0.4, 0.8).SetLineWidth(2)
            .Rectangle(72, 500, 468, 200).Stroke())
        .AddAttachment("engineering-data.txt", "KillerPDF engineering data"u8.ToArray(),
            "text/plain", "Engineering validation payload",
            PdfAssociatedFileRelationship.Data, DateTimeOffset.UtcNow)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/A-4e engineering PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--authoring-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    var content = new PdfContentStreamBuilder()
        .SaveState()
        .SetFillRgb(0.9, 0.2, 0.4)
        .Rectangle(72, 72, 200, 100)
        .Fill()
        .RestoreState();
    byte[] pdf = new PdfDocumentBuilder().AddPage(612, 792, content).Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} bytes to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--tagged-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    var content = new PdfContentStreamBuilder()
        .BeginArtifact().SetStrokeGray(0.5).Rectangle(36, 36, 540, 720).Stroke()
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .SetFillRgb(0.9, 0.2, 0.4).Rectangle(72, 600, 240, 100).Fill()
        .EndMarkedContent();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF tagged document smoke test",
            Language = "en-US"
        })
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "A pink rectangle")
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte tagged PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--tagged-import-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    var content = new PdfContentStreamBuilder()
        .BeginArtifact().SetStrokeGray(0.5).Rectangle(36, 36, 540, 720).Stroke()
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .SetFillRgb(0.9, 0.2, 0.4).Rectangle(72, 600, 240, 100).Fill()
        .EndMarkedContent();
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF imported tagged document smoke test",
            Language = "en-US"
        })
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "A pink rectangle")
        .Build();
    byte[] target = new PdfDocumentBuilder().Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(PdfDocument.Open(source))
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte imported tagged PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--tagged-subset-import-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    PdfContentStreamBuilder TaggedPage(double red) => new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .SetFillRgb(red, 0.25, 0.65).Rectangle(72, 600, 240, 100).Fill()
        .EndMarkedContent();
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF selected tagged page smoke test",
            Language = "en-US"
        })
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, TaggedPage(0.3))
        .AddPage(612, 792, TaggedPage(0.9))
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "Omitted rectangle")
        .AddStructureElement(PdfStructureType.Figure, 1, 0, 1,
            alternateDescription: "Selected rectangle")
        .Build();
    byte[] target = new PdfDocumentBuilder().Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedPage(PdfDocument.Open(source), 1)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte selected tagged-page PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--form-subset-import-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    byte[] source = new PdfDocumentBuilder()
        .AddBlankPage().AddBlankPage()
        .AddTextField(0, "omitted", 72, 680, 180, 24, "omitted value")
        .AddTextField(1, "selected", 72, 680, 180, 24, "selected value")
        .Build();
    byte[] target = new PdfDocumentBuilder()
        .AddBlankPage().AddTextField(0, "target", 72, 640, 180, 24)
        .Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedPage(PdfDocument.Open(source), 1)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte selected form-page PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--layer-subset-import-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    var omitted = new PdfOptionalContentGroup("Omitted smoke layer");
    var selected = new PdfOptionalContentGroup(
        "Selected smoke layer", initiallyVisible: false);
    byte[] source = new PdfDocumentBuilder()
        .AddPage(200, 200, new PdfContentStreamBuilder()
            .BeginOptionalContent(omitted)
            .Rectangle(20, 20, 80, 80).Fill().EndMarkedContent())
        .AddPage(200, 200, new PdfContentStreamBuilder()
            .BeginOptionalContent(selected)
            .Rectangle(100, 100, 80, 80).Fill().EndMarkedContent())
        .Build();
    byte[] pdf = new PdfIncrementalPageEditor(
            PdfDocument.Open(new PdfDocumentBuilder().Build()))
        .AddImportedPage(PdfDocument.Open(source), 1)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte selected layered-page PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--layers-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    var artwork = new PdfOptionalContentGroup("Artwork");
    var review = new PdfOptionalContentGroup("Review notes", initiallyVisible: false);
    var content = new PdfContentStreamBuilder()
        .BeginOptionalContent(artwork)
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .SetFillRgb(0.15, 0.45, 0.85).Rectangle(72, 560, 240, 140).Fill()
        .EndMarkedContent()
        .EndMarkedContent()
        .BeginOptionalContent(review)
        .BeginMarkedContent(PdfStructureType.Figure, 1)
        .SetStrokeRgb(0.9, 0.15, 0.3).SetLineWidth(4)
        .Rectangle(96, 584, 192, 92).Stroke()
        .EndMarkedContent()
        .EndMarkedContent();
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF optional-content layer smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "A blue rectangle")
        .AddStructureElement(PdfStructureType.Figure, 0, 1, 1,
            alternateDescription: "A red review outline")
        .Build();
    byte[] target = new PdfDocumentBuilder().Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(PdfDocument.Open(source))
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte layered PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--layered-occupied-merge-smoke")
{
    var layer = new PdfOptionalContentGroup("Imported artwork");
    byte[] source = new PdfDocumentBuilder()
        .AddPage(200, 200, new PdfContentStreamBuilder()
            .BeginOptionalContent(layer)
            .SetFillRgb(0.15, 0.45, 0.85).Rectangle(20, 20, 80, 80).Fill()
            .EndMarkedContent())
        .Build();
    byte[] target = new PdfDocumentBuilder().AddBlankPage(200, 200).Build();
    byte[] merged = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(PdfDocument.Open(source)).Build();
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, merged);
    Console.WriteLine($"Wrote {merged.Length:N0} byte occupied layered merge PDF to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--signature-smoke")
{
    string openSsl = Path.GetFullPath(args[1]);
    if (!File.Exists(openSsl))
        throw new FileNotFoundException("The OpenSSL executable was not found.", openSsl);
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[2]));
    string destination = Path.GetFullPath(args[3]);
    string scratch = Path.Combine(Path.GetTempPath(),
        $"killerpdf-signature-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratch);
    try
    {
        string keyPath = Path.Combine(scratch, "key.pem");
        string certificatePath = Path.Combine(scratch, "certificate.pem");
        string contentPath = Path.Combine(scratch, "content.bin");
        string signaturePath = Path.Combine(scratch, "signature.der");
        string verifiedPath = Path.Combine(scratch, "verified.bin");
        RunOpenSsl("req", "-x509", "-newkey", "rsa:2048",
            "-keyout", keyPath, "-out", certificatePath,
            "-days", "1", "-nodes", "-subj", "/CN=KillerPDF Signature Smoke");
        using X509Certificate2 signerCertificate =
            X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        byte[] signerCertificateDer = signerCertificate.RawData;

        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "KillerPDF detached CMS signature smoke test",
                Language = "en-US"
            })
            .SetOutputIntent(profile, "sRGB IEC61966-2.1")
            .EnablePdfA4Conformance()
            .AddBlankPage()
            .AddSignatureField(0, "ReleaseApproval", 72, 680, 220, 44,
                fieldLock: new PdfSignatureFieldLock(
                    PdfSignatureLockAction.All,
                    Permission: PdfSignatureLockPermission.NoChanges),
                seedValue: new PdfSignatureSeedValue
                {
                    SubFilters = [PdfSignatureSubFilter.EtsiCadesDetached],
                    RequireSubFilter = true,
                    DigestMethods = [PdfSignatureDigestMethod.Sha256],
                    RequireDigestMethod = true,
                    Certificate = new PdfSignatureCertificateSeed
                    {
                        SubjectCertificates = [signerCertificateDer],
                        RequireSubject = true
                    }
                })
            .Build();
        byte[] pdf = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), content =>
            {
                File.WriteAllBytes(contentPath, content.ToArray());
                RunOpenSsl("cms", "-sign", "-binary", "-in", contentPath,
                    "-signer", certificatePath, "-inkey", keyPath,
                    "-outform", "DER", "-out", signaturePath,
                    "-md", "sha256", "-nosmimecap");
                return File.ReadAllBytes(signaturePath);
            }, new PdfSignatureOptions
            {
                FieldName = "ReleaseApproval",
                SignerName = "KillerPDF Signature Smoke",
                Reason = "Engine validation",
                SigningTime = DateTimeOffset.UtcNow,
                SignerCertificate = signerCertificateDer,
                CertificationPermission =
                    PdfSignatureCertificationPermission.FormFillingAndSignatures,
                IncrementalWriteOptions = new PdfIncrementalUpdateWriteOptions
                {
                    CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                    CompressCrossReferenceStream = true,
                    UseObjectStreams = true,
                    CompressObjectStreams = true
                }
            });
        RunOpenSsl("cms", "-verify", "-binary", "-inform", "DER",
            "-in", signaturePath, "-content", contentPath,
            "-certfile", certificatePath, "-noverify", "-out", verifiedPath);
        if (!File.ReadAllBytes(contentPath).AsSpan()
            .SequenceEqual(File.ReadAllBytes(verifiedPath)))
            throw new InvalidOperationException("OpenSSL returned different verified signature content.");
        PdfDocument signedDocument = PdfDocument.Open(pdf);
        PdfSignatureInfo inspectedSignature = PdfSignatureReader.Read(signedDocument).Single();
        PdfSignatureVerificationResult verification =
            PdfSignatureVerifier.VerifyIntegrity(signedDocument, inspectedSignature);
        if (!inspectedSignature.IsCertificationSignature
            || !inspectedSignature.HasValidByteRange
            || !inspectedSignature.CoversWholeDocument
            || !inspectedSignature.HasValidCmsEncoding
            || !verification.IsCryptographicallyValid
            || !PdfSignatureReader.GetSignedContent(signedDocument, inspectedSignature)
                .AsSpan().SequenceEqual(File.ReadAllBytes(contentPath)))
            throw new InvalidOperationException(
                "The signed PDF did not pass KillerPDF signature inspection.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, pdf);
        Console.WriteLine($"Wrote {pdf.Length:N0} byte CMS-signed PDF to {destination}");
    }
    finally
    {
        Directory.Delete(scratch, recursive: true);
    }
    return 0;

    void RunOpenSsl(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(openSsl)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("OpenSSL could not be started.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"OpenSSL exited with code {process.ExitCode}: {standardError}{standardOutput}");
    }
}

if (args.Length == 4 && args[0] == "--encrypted-rewrite-smoke")
{
    string source = Path.GetFullPath(args[1]);
    string destination = Path.GetFullPath(args[3]);
    PdfDocument encrypted = PdfDocument.Open(File.ReadAllBytes(source), args[2]);
    byte[] rewritten = PdfDocumentWriter.Write(encrypted);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, rewritten);
    Console.WriteLine($"Wrote {rewritten.Length:N0} byte encrypted PDF to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--encrypted-structural-rewrite-smoke")
{
    string source = Path.GetFullPath(args[1]);
    string destination = Path.GetFullPath(args[3]);
    PdfDocument encrypted = PdfDocument.Open(File.ReadAllBytes(source), args[2]);
    byte[] rewritten = PdfDocumentWriter.Write(encrypted, new PdfDocumentWriteOptions
    {
        TargetVersion = encrypted.Header.Version.CompareTo(new PdfVersion(1, 5)) < 0
            ? new PdfVersion(1, 5) : null,
        CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
        UseObjectStreams = true,
        CompressStructuralStreams = true,
        AllowSignatureInvalidation = true
    });
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, rewritten);
    Console.WriteLine($"Wrote {rewritten.Length:N0} byte encrypted object-stream PDF to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--encrypted-incremental-structural-smoke")
{
    byte[] source = File.ReadAllBytes(args[1]);
    PdfDocument document = PdfDocument.Open(source, args[2]);
    var update = new PdfIncrementalUpdateBuilder(document);
    update.AddObject(new PdfString(
        "encrypted incremental structural marker"u8, PdfStringForm.Literal));
    byte[] pdf = update.Build(new PdfIncrementalUpdateWriteOptions
    {
        CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
        UseObjectStreams = true,
        CompressObjectStreams = true,
        CompressCrossReferenceStream = true
    });
    if (!pdf.AsSpan(0, source.Length).SequenceEqual(source))
        throw new InvalidDataException("The encrypted incremental update changed source bytes.");
    string destination = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length - source.Length:N0} encrypted structural bytes to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--structural-rewrite-smoke")
{
    string source = Path.GetFullPath(args[1]);
    string destination = Path.GetFullPath(args[2]);
    PdfDocument document = PdfDocument.Open(File.ReadAllBytes(source));
    byte[] rewritten = PdfDocumentWriter.Write(document, new PdfDocumentWriteOptions
    {
        TargetVersion = document.Header.Version.CompareTo(new PdfVersion(1, 5)) < 0
            ? new PdfVersion(1, 5) : null,
        CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
        UseObjectStreams = true,
        CompressStructuralStreams = true,
        AllowSignatureInvalidation = true
    });
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, rewritten);
    Console.WriteLine($"Wrote {rewritten.Length:N0} byte object-stream PDF to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--encrypted-authoring-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    byte[] encrypted = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "Encrypted KillerPDF smoke test" })
        .SetPasswordEncryption(new PdfPasswordEncryptionOptions
        {
            UserPassword = args[2],
            OwnerPassword = args[3]
        })
        .AddPage(612, 792, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 18)
            .MoveText(72, 720).ShowLatin1Text("Encrypted by KillerPDF").EndText())
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, encrypted);
    Console.WriteLine($"Wrote {encrypted.Length:N0} byte authored encrypted PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--tagged-removal-smoke")
{
    static PdfContentStreamBuilder TaggedSquare() => new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .Rectangle(10, 10, 20, 20).Fill().EndMarkedContent();
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "Tagged page-removal smoke test", Language = "en-US"
        })
        .EnablePdfUa2Conformance()
        .AddPage(100, 100, TaggedSquare()).AddPage(100, 100, TaggedSquare())
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "Removed square")
        .AddStructureElement(PdfStructureType.Figure, 1, 0, 1,
            alternateDescription: "Retained square")
        .Build();
    byte[] edited = new PdfIncrementalPageEditor(PdfDocument.Open(source))
        .RemovePage(0).Build();
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, edited);
    Console.WriteLine($"Wrote {edited.Length:N0} byte tagged page-removal PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--tagged-merge-smoke")
{
    static byte[] Tagged(string title, string description)
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill().EndMarkedContent();
        return new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = title, Language = "en-US" })
            .EnablePdfUa2Conformance().AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
                alternateDescription: description).Build();
    }
    PdfDocument target = PdfDocument.Open(Tagged("Tagged merge target", "Target square"));
    PdfDocument source = PdfDocument.Open(Tagged("Tagged merge source", "Source square"));
    byte[] merged = new PdfIncrementalPageEditor(target)
        .AddImportedDocument(source).Build();
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, merged);
    Console.WriteLine($"Wrote {merged.Length:N0} byte tagged merge PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--tagged-remove-merge-smoke")
{
    static byte[] Tagged(string title, params string[] descriptions)
    {
        var builder = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = title, Language = "en-US" })
            .EnablePdfUa2Conformance();
        for (int index = 0; index < descriptions.Length; index++)
            builder.AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginMarkedContent(PdfStructureType.Figure, 0)
                .Rectangle(10, 10, 20, 20).Fill().EndMarkedContent());
        builder.AddStructureContainer(PdfStructureType.Document);
        for (int index = 0; index < descriptions.Length; index++)
            builder.AddStructureElement(PdfStructureType.Figure, index, 0, 1,
                alternateDescription: descriptions[index]);
        return builder.Build();
    }
    PdfDocument target = PdfDocument.Open(Tagged(
        "Tagged removal and merge target", "Removed target square", "Retained target square"));
    PdfDocument source = PdfDocument.Open(Tagged(
        "Tagged removal and merge source", "Imported source square"));
    byte[] merged = new PdfIncrementalPageEditor(target)
        .RemovePage(0).AddImportedDocument(source).Build();
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, merged);
    Console.WriteLine($"Wrote {merged.Length:N0} byte tagged removal-and-merge PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--transparency-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    var maskGradient = new PdfRadialGradient(90, 90, 0, 90, 90, 90, [
        new PdfGradientStop(0, 1),
        new PdfGradientStop(0.65, 0.8),
        new PdfGradientStop(1, 0)],
        extendEnd: true);
    var mask = new PdfFormXObject(180, 180, new PdfContentStreamBuilder()
        .PaintShading(maskGradient),
        isolatedTransparencyGroup: true);
    var content = new PdfContentStreamBuilder()
        .SetFillRgb(0.15, 0.45, 0.85)
        .SetGraphicsState(new PdfGraphicsState(
            0.7, 1, PdfBlendMode.Multiply,
            fillOverprint: true, overprintMode: PdfOverprintMode.One))
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .Rectangle(72, 520, 260, 180).Fill()
        .EndMarkedContent()
        .SetFillRgb(0.9, 0.2, 0.4)
        .SetGraphicsState(new PdfGraphicsState(0.55, 1, PdfBlendMode.Screen))
        .BeginMarkedContent(PdfStructureType.Figure, 1)
        .Rectangle(220, 580, 260, 140).Fill()
        .EndMarkedContent()
        .SetFillRgb(0.95, 0.75, 0.15)
        .SetGraphicsState(new PdfGraphicsState(
            softMask: new PdfSoftMask(mask, PdfSoftMaskSubtype.Luminosity)))
        .BeginMarkedContent(PdfStructureType.Figure, 2)
        .Rectangle(360, 500, 180, 180).Fill()
        .EndMarkedContent();
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF transparency and blend-mode smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "A translucent blue rectangle")
        .AddStructureElement(PdfStructureType.Figure, 0, 1, 1,
            alternateDescription: "A translucent pink rectangle")
        .AddStructureElement(PdfStructureType.Figure, 0, 2, 1,
            alternateDescription: "A yellow square faded through a circular luminosity mask")
        .Build();
    byte[] target = new PdfDocumentBuilder().Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(PdfDocument.Open(source))
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte transparency PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--gradient-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    var axial = new PdfAxialGradient(72, 560, 300, 700, [
        new PdfGradientStop(0, new PdfRgbColor(0.1, 0.3, 0.9)),
        new PdfGradientStop(0.45, new PdfRgbColor(0.2, 0.9, 0.7)),
        new PdfGradientStop(1, new PdfRgbColor(1, 0.8, 0.1))]);
    var radial = new PdfRadialGradient(420, 630, 0, 420, 630, 90, [
        new PdfGradientStop(0, new PdfRgbColor(1, 1, 1)),
        new PdfGradientStop(0.55, new PdfRgbColor(0.95, 0.25, 0.5)),
        new PdfGradientStop(1, new PdfRgbColor(0.2, 0.05, 0.3))]);
    var grayGradient = new PdfAxialGradient(72, 535, 240, 535, [
        new PdfGradientStop(0, 0.1),
        new PdfGradientStop(0.5, 0.9),
        new PdfGradientStop(1, 0.3)]);
    var lab = new PdfLabColorSpace();
    var indexed = new PdfIndexedColorSpace(PdfIndexedBaseColorSpace.Rgb, new byte[]
    {
        245, 80, 55, 35, 125, 235, 245, 215, 55
    });
    var calGray = new PdfCalGrayColorSpace(gamma: 2.2);
    var calRgb = new PdfCalRgbColorSpace(
        gamma: [2.2, 2.2, 2.2],
        matrix: [0.4124, 0.3576, 0.1805, 0.2126, 0.7152, 0.0722, 0.0193, 0.1192, 0.9505]);
    var content = new PdfContentStreamBuilder()
        .BeginArtifact()
        .SetStrokeIccColor(profile, 0.2, 0.75, 0.9).SetLineWidth(5)
        .SetLineCap(PdfLineCap.Round).SetLineJoin(PdfLineJoin.Bevel)
        .SetMiterLimit(6).SetDashPattern([18, 7, 3, 7], 2)
        .Rectangle(60, 488, 492, 244).Stroke()
        .SetFillLabColor(lab, 72, 38, 54).Rectangle(276, 500, 60, 20).Fill()
        .SetFillIndexedColor(indexed, 1).Rectangle(346, 500, 60, 20).Fill()
        .SetFillCalibratedColor(calGray, 0.45).Rectangle(416, 500, 60, 20).Fill()
        .SetFillCalibratedColor(calRgb, 0.2, 0.65, 0.35).Rectangle(486, 500, 60, 20).Fill()
        .SaveState().Rectangle(72, 528, 168, 14).Clip().PaintShading(grayGradient).RestoreState()
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .SaveState().Rectangle(72, 560, 228, 140).Clip().PaintShading(axial).RestoreState()
        .EndMarkedContent()
        .BeginMarkedContent(PdfStructureType.Figure, 1)
        .SaveState().Rectangle(330, 540, 180, 180).Clip().PaintShading(radial).RestoreState()
        .EndMarkedContent();
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF gradient shading smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "A blue, green, and yellow axial gradient")
        .AddStructureElement(PdfStructureType.Figure, 0, 1, 1,
            alternateDescription: "A white, pink, and purple radial gradient")
        .Build();
    byte[] target = new PdfDocumentBuilder().Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(PdfDocument.Open(source))
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte gradient PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--form-xobject-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    var gradient = new PdfAxialGradient(0, 0, 180, 0, [
        new PdfGradientStop(0, new PdfRgbColor(0.08, 0.2, 0.65)),
        new PdfGradientStop(0.5, new PdfRgbColor(0.1, 0.75, 0.8)),
        new PdfGradientStop(1, new PdfRgbColor(0.95, 0.75, 0.15))]);
    var emblem = new PdfFormXObject(180, 80, new PdfContentStreamBuilder()
        .SetOpacity(0.92)
        .Rectangle(0, 0, 180, 80).Clip()
        .PaintShading(gradient), isolatedTransparencyGroup: true);
    var card = new PdfFormXObject(220, 120, new PdfContentStreamBuilder()
        .SetFillRgb(0.12, 0.12, 0.16).Rectangle(0, 0, 220, 120).Fill()
        .DrawForm(emblem, 20, 20));
    var content = new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .DrawForm(card, 72, 560)
        .DrawForm(card, 330, 560, 176, 96)
        .EndMarkedContent();
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF reusable form smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "Two reusable gradient cards")
        .Build();
    byte[] target = new PdfDocumentBuilder().Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(PdfDocument.Open(source))
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte Form XObject PDF to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--tiling-pattern-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    var pattern = new PdfTilingPattern(24, 24, new PdfContentStreamBuilder()
        .Rectangle(3, 3, 6, 6).Fill().Rectangle(15, 15, 6, 6).Fill(),
        paintType: PdfTilingPatternPaintType.Uncolored,
        matrix: new PdfPatternMatrix(0.966, 0.259, -0.259, 0.966, 0, 0));
    var content = new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .SetFillRgb(0.08, 0.12, 0.22).Rectangle(72, 500, 468, 220).Fill()
        .SetFillPattern(pattern, new PdfRgbColor(0.95, 0.3, 0.2))
        .Rectangle(72, 500, 468, 220).Fill()
        .SetStrokePattern(pattern, profile, 0.2, 0.65, 0.95).SetLineWidth(12)
        .Rectangle(84, 512, 444, 196).Stroke()
        .EndMarkedContent();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF tiling pattern smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .EnablePdfUa2Conformance()
        .AddPage(612, 792, content)
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
            alternateDescription: "A repeating red dot pattern on a dark blue field")
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte tiling-pattern PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--form-smoke")
{
    string destination = Path.GetFullPath(args[1]);
    PdfImage buttonIcon = PdfImage.FromRgba(2, 2, new byte[]
    {
        40, 100, 220, 255, 120, 180, 255, 255,
        120, 180, 255, 255, 40, 100, 220, 255
    });
    PdfImage rolloverButtonIcon = PdfImage.FromRgb(1, 1, new byte[] { 70, 170, 110 });
    PdfImage downButtonIcon = PdfImage.FromRgb(1, 1, new byte[] { 220, 120, 50 });
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF form smoke test", Language = "en-US" })
        .AddBlankPage()
        .AddNamedDestination("FormTop", 0, PdfDestination.At(top: 760))
        .AddTextField(0, "customer.name", 72, 680, 240, 28, "Steve the Killer", 12,
            new PdfTextFieldOptions { Alignment = PdfTextFieldAlignment.Center },
            appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(0.96, 0.96, 1),
                BorderColor = new PdfRgbColor(0.2, 0.3, 0.65),
                TextColor = new PdfRgbColor(0.1, 0.1, 0.3),
                BorderWidth = 1.5,
                BorderStyle = PdfFormFieldBorderStyle.Dashed,
                DashPattern = [3, 1.5]
            })
        .AddTextField(0, "customer.address", 72, 620, 200, 48, "First line\nSecond line", 11,
            new PdfTextFieldOptions { Multiline = true },
            richTextValue: "<body xmlns=\"http://www.w3.org/1999/xhtml\"><p><b>First line</b></p><p>Second line</p></body>")
        .AddTextField(0, "customer.pin", 72, 570, 120, 24, "1234", 12,
            new PdfTextFieldOptions { Comb = true, MaximumLength = 6 })
        .AddTextField(0, "customer.password", 72, 535, 160, 24, "secret", 12,
            new PdfTextFieldOptions { Password = true })
        .AddTextField(0, "customer.attachment", 300, 620, 180, 24, "C:/current.pdf", 10,
            new PdfTextFieldOptions { FileSelect = true }, defaultValue: "C:/default.pdf")
        .AddCheckBox(0, "customer.approved", 72, 590, 18, 18, isChecked: true,
            mark: PdfCheckBoxMark.Diamond, defaultChecked: false,
            appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(0.9, 1, 0.9),
                BorderColor = new PdfRgbColor(0.1, 0.5, 0.2),
                TextColor = new PdfRgbColor(0.05, 0.35, 0.12)
            })
        .AddRadioGroup("customer.plan", [
            new PdfRadioButtonOption(0, 72, 600, 18, 18, "Free"),
            new PdfRadioButtonOption(0, 120, 600, 18, 18, "Pro")], "Pro",
            radioOptions: new PdfRadioGroupOptions
            {
                AppearanceStyle = new PdfFormFieldAppearanceStyle
                {
                    BackgroundColor = new PdfRgbColor(0.94, 0.97, 1),
                    BorderColor = new PdfRgbColor(0.2, 0.35, 0.6),
                    TextColor = new PdfRgbColor(0.1, 0.2, 0.5)
                }
            }, defaultSelectedValue: "Free")
        .AddSignatureField(0, "customer.signature", 300, 470, 180, 52,
            new PdfFormFieldMetadata { Tooltip = "Customer signature" },
            fieldLock: new PdfSignatureFieldLock(
                PdfSignatureLockAction.Include, ["customer.name", "customer.approved"],
                PdfSignatureLockPermission.FormFillingAndSignatures),
            seedValue: new PdfSignatureSeedValue
            {
                Handler = PdfSignatureHandler.AdobePpkLite,
                RequireHandler = true,
                ParserVersion = PdfSignatureSeedParserVersion.Pdf20,
                RequireParserVersion = true,
                SubFilters = [PdfSignatureSubFilter.EtsiCadesDetached],
                RequireSubFilter = true,
                DigestMethods = [PdfSignatureDigestMethod.Sha256, PdfSignatureDigestMethod.Sha512],
                RequireDigestMethod = true,
                AddRevocationInformation = true,
                RequireRevocationInformation = true,
                Reasons = ["Approved", "Reviewed"],
                LegalAttestations = ["I have reviewed the document"],
                RequireLegalAttestation = true,
                Timestamp = new PdfSignatureTimestamp(
                    "https://timestamp.example.test/rfc3161", Required: true),
                DocumentLockIntent = PdfSignatureDocumentLockIntent.Lock,
                RequireDocumentLockIntent = true,
                AppearanceName = "KillerPDF Approval",
                RequireAppearance = true,
                Certificate = new PdfSignatureCertificateSeed
                {
                    KeyUsages = [new PdfCertificateKeyUsage { DigitalSignature = true }],
                    RequireKeyUsage = true,
                    SubjectDistinguishedNames =
                    [
                        new PdfCertificateDistinguishedName(
                            new Dictionary<string, string> { ["o"] = "Killer Tools" })
                    ],
                    EnrollmentUrl = "https://signing.example.test/enroll",
                    EnrollmentUrlType = PdfCertificateEnrollmentUrlType.SignatureService
                },
                CertificationPermission = PdfSignatureCertificationPermission.ApprovalSignature
            }, appearanceText: "Sign here", appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(1, 0.96, 0.86),
                BorderColor = new PdfRgbColor(0.6, 0.35, 0.1),
                TextColor = new PdfRgbColor(0.4, 0.15, 0.05),
                BorderWidth = 1.5,
                BorderStyle = PdfFormFieldBorderStyle.Inset
            }, appearanceAlignment: PdfTextFieldAlignment.Center)
        .AddComboBoxOptions(0, "customer.theme", 72, 550, 180, 24,
            [new PdfChoiceOption("dark", "Dark"),
             new PdfChoiceOption("mourning", "Mourning"),
             new PdfChoiceOption("98se", "98SE")], "mourning",
            choiceOptions: new PdfChoiceFieldOptions
            {
                Alignment = PdfTextFieldAlignment.Center,
                DefaultSelectedExportValues = ["dark"],
                AppearanceStyle = new PdfFormFieldAppearanceStyle
                {
                    BackgroundColor = new PdfRgbColor(0.98, 0.94, 0.86),
                    BorderColor = new PdfRgbColor(0.5, 0.3, 0.1),
                    TextColor = new PdfRgbColor(0.25, 0.12, 0.04)
                }
            })
        .AddUriPushButton(0, "customer.documentation", 300, 550, 180, 28,
            "Open KillerPDF docs", "https://killerpdf.com",
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Open KillerPDF documentation" },
            appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(0.86, 0.93, 1),
                BorderColor = new PdfRgbColor(0.15, 0.35, 0.65),
                TextColor = new PdfRgbColor(0.08, 0.2, 0.5),
                BorderStyle = PdfFormFieldBorderStyle.Beveled
            }, appearanceOptions: new PdfPushButtonAppearanceOptions
            {
                Alignment = PdfTextFieldAlignment.Center,
                Icon = buttonIcon,
                RolloverIcon = rolloverButtonIcon,
                DownIcon = downButtonIcon,
                CaptionPosition = PdfPushButtonCaptionPosition.CaptionRightOfIcon,
                IconScaleMode = PdfPushButtonIconScaleMode.WhenTooLarge,
                RolloverLabel = "Open documentation",
                DownLabel = "Opening docs"
            })
        .AddPagePushButton(0, "customer.top", 300, 515, 180, 28,
            "Return to top", 0, PdfDestination.At(top: 760))
        .AddNamedDestinationPushButton(0, "customer.namedTop", 300, 480, 180, 28,
            "Named top", "FormTop")
        .AddResetFormPushButton(0, "customer.reset", 300, 445, 180, 28, "Reset form")
        .AddSubmitPdfPushButton(0, "customer.submit", 300, 410, 180, 28,
            "Submit PDF", "https://killerpdf.com/forms",
            ["customer.name", "customer.approved"])
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte AcroForm PDF to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--pdfa-form-smoke")
{
    TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(args[1]));
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[2]));
    string destination = Path.GetFullPath(args[3]);
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF PDF/A form smoke test", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddBlankPage()
        .AddTextField(0, "customer.name", 72, 680, 240, 28, "KillerPDF café Ω", 12,
            options: new PdfTextFieldOptions { Alignment = PdfTextFieldAlignment.Right }, embeddedFont: font,
            fieldMetadata: new PdfFormFieldMetadata
            {
                Tooltip = "Customer name",
                MappingName = "customer_name"
            }, appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(0.96, 0.96, 1),
                BorderColor = new PdfRgbColor(0.2, 0.3, 0.65),
                TextColor = new PdfRgbColor(0.1, 0.1, 0.3),
                BorderWidth = 1.5,
                BorderStyle = PdfFormFieldBorderStyle.Dashed,
                DashPattern = [3, 1.5]
            })
        .AddTextField(0, "customer.address", 72, 660, 200, 48, "First line\nSecond line", 11,
            new PdfTextFieldOptions { Multiline = true }, embeddedFont: font,
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Address", MappingName = "customer_address" },
            richTextValue: "<body xmlns=\"http://www.w3.org/1999/xhtml\"><p><b>First line</b></p><p>Second line</p></body>")
        .AddTextField(0, "customer.pin", 300, 680, 120, 24, "1234", 12,
            new PdfTextFieldOptions { Comb = true, MaximumLength = 6 }, embeddedFont: font,
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "PIN", MappingName = "customer_pin" })
        .AddTextField(0, "customer.password", 300, 650, 160, 24, "secret", 12,
            new PdfTextFieldOptions { Password = true }, embeddedFont: font,
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Password", MappingName = "customer_password" })
        .AddTextField(0, "customer.attachment", 300, 620, 180, 24, "C:/current.pdf", 10,
            new PdfTextFieldOptions { FileSelect = true }, embeddedFont: font,
            defaultValue: "C:/default.pdf")
        .AddComboBoxOptions(0, "customer.theme", 72, 630, 180, 24,
            [new PdfChoiceOption("dark", "Dark"),
             new PdfChoiceOption("mourning", "Mourning"),
             new PdfChoiceOption("98se", "98SE")], "mourning", embeddedFont: font,
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Theme", MappingName = "customer_theme" },
            choiceOptions: new PdfChoiceFieldOptions
            {
                Alignment = PdfTextFieldAlignment.Right,
                DefaultSelectedExportValues = ["dark"],
                AppearanceStyle = new PdfFormFieldAppearanceStyle
                {
                    BackgroundColor = new PdfRgbColor(0.98, 0.94, 0.86),
                    BorderColor = new PdfRgbColor(0.5, 0.3, 0.1),
                    TextColor = new PdfRgbColor(0.25, 0.12, 0.04)
                }
            })
        .AddListBox(0, "customer.features", 300, 590, 180, 72,
            ["Annotations", "Forms", "PDF/A"], "Forms", embeddedFont: font,
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Features", MappingName = "customer_features" })
        .AddMultiSelectListBoxOptions(0, "customer.formats", 300, 490, 180, 72,
            [new PdfChoiceOption("pdf20", "PDF 2.0"),
             new PdfChoiceOption("pdfa4", "PDF/A-4"),
             new PdfChoiceOption("pdfua2", "PDF/UA-2")], ["pdfa4", "pdfua2"], embeddedFont: font,
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Formats", MappingName = "customer_formats" },
            choiceOptions: new PdfChoiceFieldOptions { DefaultSelectedExportValues = ["pdf20"] })
        .AddCheckBox(0, "customer.approved", 72, 590, 18, 18, isChecked: true,
            mark: PdfCheckBoxMark.Circle,
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Approved", MappingName = "customer_approved" },
            defaultChecked: false,
            appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(0.9, 1, 0.9),
                BorderColor = new PdfRgbColor(0.1, 0.5, 0.2),
                TextColor = new PdfRgbColor(0.05, 0.35, 0.12)
            })
        .AddRadioGroup("customer.plan", [
            new PdfRadioButtonOption(0, 72, 550, 18, 18, "Free"),
            new PdfRadioButtonOption(0, 120, 550, 18, 18, "Pro")], "Pro",
            fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Plan", MappingName = "customer_plan" },
            radioOptions: new PdfRadioGroupOptions
            {
                AppearanceStyle = new PdfFormFieldAppearanceStyle
                {
                    BackgroundColor = new PdfRgbColor(0.94, 0.97, 1),
                    BorderColor = new PdfRgbColor(0.2, 0.35, 0.6),
                    TextColor = new PdfRgbColor(0.1, 0.2, 0.5)
                }
            },
            defaultSelectedValue: "Free")
        .AddSignatureField(0, "customer.signature", 72, 470, 180, 52,
            new PdfFormFieldMetadata { Tooltip = "Customer signature", MappingName = "customer_signature" },
            fieldLock: new PdfSignatureFieldLock(
                PdfSignatureLockAction.Exclude, ["customer.theme"],
                PdfSignatureLockPermission.NoChanges),
            seedValue: new PdfSignatureSeedValue
            {
                Handler = PdfSignatureHandler.AdobePpkLite,
                RequireHandler = true,
                ParserVersion = PdfSignatureSeedParserVersion.Pdf20,
                RequireParserVersion = true,
                SubFilters = [PdfSignatureSubFilter.EtsiCadesDetached],
                RequireSubFilter = true,
                DigestMethods = [PdfSignatureDigestMethod.Sha256, PdfSignatureDigestMethod.Sha384],
                RequireDigestMethod = true,
                AddRevocationInformation = true,
                RequireRevocationInformation = true,
                Reasons = ["Approved for archival"],
                RequireReason = true,
                LegalAttestations = ["I have reviewed the archival copy"],
                RequireLegalAttestation = true,
                Timestamp = new PdfSignatureTimestamp(
                    "https://timestamp.example.test/rfc3161", Required: true),
                DocumentLockIntent = PdfSignatureDocumentLockIntent.Lock,
                RequireDocumentLockIntent = true,
                AppearanceName = "KillerPDF Archival Approval",
                RequireAppearance = true,
                Certificate = new PdfSignatureCertificateSeed
                {
                    KeyUsages = [new PdfCertificateKeyUsage { DigitalSignature = true }],
                    RequireKeyUsage = true,
                    SubjectDistinguishedNames =
                    [
                        new PdfCertificateDistinguishedName(
                            new Dictionary<string, string> { ["o"] = "Killer Tools" })
                    ],
                    EnrollmentUrl = "https://signing.example.test/enroll",
                    EnrollmentUrlType = PdfCertificateEnrollmentUrlType.SignatureService
                },
                CertificationPermission =
                    PdfSignatureCertificationPermission.FormFillingAndSignatures
            }, appearanceText: "Sign for archival", embeddedFont: font,
            appearanceStyle: new PdfFormFieldAppearanceStyle
            {
                BackgroundColor = new PdfRgbColor(1, 0.96, 0.86),
                BorderColor = new PdfRgbColor(0.6, 0.35, 0.1),
                TextColor = new PdfRgbColor(0.4, 0.15, 0.05),
                BorderWidth = 1.5,
                BorderStyle = PdfFormFieldBorderStyle.Inset
            }, appearanceAlignment: PdfTextFieldAlignment.Center)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/A-4 AcroForm PDF to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--pdfa-cff-smoke")
{
    TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(args[1]));
    if (!font.HasCffOutlines)
        throw new ArgumentException("The CFF smoke test requires an OTTO font with CFF outlines.");
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[2]));
    string destination = Path.GetFullPath(args[3]);
    var content = new PdfContentStreamBuilder()
        .BeginText().SetFont(font, 24).MoveText(72, 700)
        .ShowUnicodeText("KillerPDF").EndText();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata
        {
            Title = "KillerPDF CFF OpenType smoke test",
            Language = "en-US"
        })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddPage(612, 792, content)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/A-4 with embedded {font.PostScriptName} to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--pdfa-annotation-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string destination = Path.GetFullPath(args[2]);
    var content = new PdfContentStreamBuilder()
        .SetFillRgb(0.12, 0.12, 0.12)
        .Rectangle(72, 620, 360, 72)
        .Fill();
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF PDF/A annotation smoke test", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddPage(612, 792, content)
        .AddTextNote(0, 448, 650, "Review this section", PdfRgbColor.NoteYellow,
            annotationMetadata: new PdfAnnotationMetadata
            {
                Author = "KillerPDF",
                Subject = "Editorial review",
                CreationDate = new DateTimeOffset(
                    2026, 8, 23, 12, 0, 0, TimeSpan.FromHours(-7))
            }, icon: PdfTextNoteIcon.Comment, state: PdfTextNoteState.Accepted,
            name: "editorial-root",
            popup: new PdfAnnotationPopup(350, 690, 220, 80, open: true))
        .AddTextNote(0, 475, 610, "Approved after revision", PdfRgbColor.NoteYellow,
            annotationMetadata: new PdfAnnotationMetadata { Author = "Reviewer" },
            icon: PdfTextNoteIcon.Insert, state: PdfTextNoteState.Completed,
            name: "editorial-reply", inReplyTo: "editorial-root")
        .AddHighlight(0,
            [
                new PdfTextQuad(new PdfPoint(90, 668), new PdfPoint(390, 668),
                    new PdfPoint(90, 640), new PdfPoint(390, 640)),
                new PdfTextQuad(new PdfPoint(90, 630), new PdfPoint(300, 630),
                    new PdfPoint(90, 602), new PdfPoint(300, 602))
            ],
            "Highlighted wrapped passage", PdfRgbColor.Yellow, 0.35)
        .AddUnderline(0, 90, 590, 300, 28, "Underlined passage")
        .AddStrikeOut(0, 90, 540, 300, 28, "Struck passage")
        .AddSquiggly(0, 90, 490, 300, 28, "Spelling review")
        .AddCaretAnnotation(0, 410, 540, 24, 30, "Insert a paragraph",
            symbol: PdfCaretSymbol.Paragraph,
            annotationMetadata: new PdfAnnotationMetadata { Author = "Editor" })
        .AddRedactionMark(0,
            [
                new PdfTextQuad(new PdfPoint(90, 450), new PdfPoint(390, 450),
                    new PdfPoint(90, 425), new PdfPoint(390, 425)),
                new PdfTextQuad(new PdfPoint(90, 415), new PdfPoint(260, 415),
                    new PdfPoint(90, 390), new PdfPoint(260, 390))
            ],
            "Remove confidential identifiers",
            annotationMetadata: new PdfAnnotationMetadata
            {
                Author = "Privacy review",
                Subject = "Pending redaction"
            })
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/A-4 annotation PDF to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--pdfa-visual-annotation-smoke")
{
    TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(args[1]));
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[2]));
    PdfImage stampImage = PdfImage.FromRgba(2, 2, new byte[]
    {
        255, 40, 80, 230, 40, 120, 255, 150,
        40, 220, 120, 150, 255, 180, 40, 230
    });
    string destination = Path.GetFullPath(args[3]);
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF PDF/A visual annotation smoke test", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddBlankPage()
        .AddFreeText(0, 72, 660, 250, 70, "KillerPDF café Ω\nMultiline free text", font, 14,
            textColor: new PdfRgbColor(0.1, 0.1, 0.1), fillColor: new PdfRgbColor(1, 1, 0.8),
            opacity: 0.9, alignment: PdfTextAlignment.Center, dashPattern: [6, 3],
            intent: PdfFreeTextIntent.Callout,
            calloutLine:
            [
                new PdfPoint(40, 610), new PdfPoint(60, 640), new PdfPoint(72, 660)
            ],
            calloutEnding: PdfLineEndingStyle.ClosedArrow)
        .AddLineAnnotation(0, new PdfPoint(72, 620), new PdfPoint(320, 580),
            new PdfRgbColor(0.1, 0.35, 0.9), 3, 0.8, "Line annotation",
            PdfLineEndingStyle.OpenArrow, PdfLineEndingStyle.ClosedArrow,
            interiorColor: new PdfRgbColor(0.75, 0.85, 1),
            intent: PdfLineAnnotationIntent.Arrow)
        .AddRectangleAnnotation(0, 72, 490, 110, 65,
            new PdfRgbColor(0.9, 0.1, 0.2), new PdfRgbColor(1, 0.8, 0.85), 3, 0.75, "Rectangle")
        .AddEllipseAnnotation(0, 210, 490, 110, 65,
            new PdfRgbColor(0.1, 0.55, 0.25), new PdfRgbColor(0.8, 1, 0.85), 3, 0.75, "Ellipse")
        .AddPolygonAnnotation(0, [
            new PdfPoint(350, 520), new PdfPoint(405, 565),
            new PdfPoint(470, 525), new PdfPoint(430, 470)],
            new PdfRgbColor(0.15, 0.3, 0.8), new PdfRgbColor(0.75, 0.85, 1),
            3, 0.8, "Filled polygon", [8, 3],
            new PdfAnnotationMetadata
            {
                Author = "KillerPDF",
                Subject = "Polygon annotation review",
                CreationDate = new DateTimeOffset(
                    2026, 8, 23, 12, 0, 0, TimeSpan.FromHours(-7))
            }, intent: PdfVertexAnnotationIntent.Dimension)
        .AddPolylineAnnotation(0, [
            new PdfPoint(330, 455), new PdfPoint(370, 480),
            new PdfPoint(415, 450), new PdfPoint(465, 480)],
            new PdfRgbColor(0.85, 0.25, 0.15), 3, 0.85, "Open polyline",
            PdfLineEndingStyle.ClosedArrow, PdfLineEndingStyle.OpenArrow,
            interiorColor: new PdfRgbColor(1, 0.8, 0.6))
        .AddInkAnnotation(0,
        [
            [new PdfPoint(72, 430), new PdfPoint(110, 455), new PdfPoint(150, 425)],
            [new PdfPoint(170, 430), new PdfPoint(210, 455), new PdfPoint(250, 425)]
        ], new PdfRgbColor(0.45, 0.1, 0.7), 4, 0.85, "Two ink strokes", [10, 4])
        .AddImageStamp(0, 350, 390, 100, 60, stampImage, "RGBA image stamp",
            icon: PdfStampIcon.Final)
        .AddRedactionMark(0,
            [
                new PdfTextQuad(new PdfPoint(72, 350), new PdfPoint(310, 350),
                    new PdfPoint(72, 325), new PdfPoint(310, 325)),
                new PdfTextQuad(new PdfPoint(72, 315), new PdfPoint(240, 315),
                    new PdfPoint(72, 290), new PdfPoint(240, 290))
            ],
            "Replacement-text redaction review",
            overlayText: "REDACTED", repeatOverlayText: true,
            overlayAlignment: PdfTextAlignment.Center, overlayFont: font)
        .Build();
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/A-4 visual annotation PDF to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--rotation-smoke")
{
    byte[] source = new PdfDocumentBuilder()
        .AddBlankPage(612, 792)
        .AddBlankPage(792, 612)
        .Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(source))
        .SetRotation(0, 90)
        .SetRotation(1, 270)
        .Build();
    if (!pdf.AsSpan(0, source.Length).SequenceEqual(source))
        throw new InvalidDataException("The rotation update changed source bytes.");
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote two page rotations in {pdf.Length - source.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--incremental-smoke")
{
    byte[] source = File.ReadAllBytes(args[1]);
    PdfDocument document = PdfDocument.Open(source);
    var rootName = new PdfName("Root"u8);
    var pageModeName = new PdfName("PageMode"u8);
    var rootReference = document.Trailer[rootName] as PdfIndirectReference
        ?? throw new InvalidDataException("The source trailer does not contain an indirect /Root.");
    var rootDictionary = document.Resolve(rootReference) as PdfDictionary
        ?? throw new InvalidDataException("The source catalog is not a dictionary.");
    var entries = rootDictionary.Where(entry => !entry.Key.Equals(pageModeName)).ToList();
    entries.Add(new KeyValuePair<PdfName, PdfObject>(pageModeName, new PdfName("UseThumbs"u8)));
    byte[] pdf = new PdfIncrementalUpdateBuilder(document)
        .ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(entries))
        .Build();
    if (!pdf.AsSpan(0, source.Length).SequenceEqual(source))
        throw new InvalidDataException("The incremental update changed source bytes.");
    string destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length - source.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--incremental-annotation-smoke")
{
    byte[] source = File.ReadAllBytes(args[1]);
    byte[] pdf = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
        .AddTextNote(0, 500, 700, "Incrementally appended note", PdfRgbColor.NoteYellow, open: true)
        .AddHighlight(0, 360, 650, 160, 24, "Incrementally appended highlight")
        .AddUnderline(0, 360, 620, 160, 20, "Incrementally appended underline")
        .AddStrikeOut(0, 360, 590, 160, 20, "Incrementally appended strikeout")
        .AddSquiggly(0, 360, 560, 160, 20, "Incrementally appended squiggly")
        .Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressObjectStreams = true,
            CompressCrossReferenceStream = true
        });
    if (!pdf.AsSpan(0, source.Length).SequenceEqual(source))
        throw new InvalidDataException("The incremental annotation update changed source bytes.");
    string destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote five annotations in {pdf.Length - source.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--incremental-visual-annotation-smoke")
{
    TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes(args[1]));
    PdfImage stampImage = PdfImage.FromRgba(2, 2, new byte[]
    {
        255, 40, 80, 230, 40, 120, 255, 150,
        40, 220, 120, 150, 255, 180, 40, 230
    });
    byte[] source = File.ReadAllBytes(args[2]);
    byte[] pdf = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
        .AddFreeText(0, 350, 680, 170, 60, "Incremental café Ω\nEmbedded free text", font, 12,
            fillColor: new PdfRgbColor(1, 1, 0.8), opacity: 0.9)
        .AddLine(0, new PdfPoint(350, 640), new PdfPoint(520, 610),
            new PdfRgbColor(0.1, 0.35, 0.9), 3, 0.8, "Incremental line")
        .AddRectangle(0, 350, 530, 75, 50,
            new PdfRgbColor(0.9, 0.1, 0.2), new PdfRgbColor(1, 0.8, 0.85), 3, 0.75)
        .AddEllipse(0, 445, 530, 75, 50,
            new PdfRgbColor(0.1, 0.55, 0.25), new PdfRgbColor(0.8, 1, 0.85), 3, 0.75)
        .AddInk(0,
        [
            [new PdfPoint(350, 480), new PdfPoint(385, 500), new PdfPoint(420, 475)],
            [new PdfPoint(445, 480), new PdfPoint(480, 500), new PdfPoint(515, 475)]
        ], new PdfRgbColor(0.45, 0.1, 0.7), 4, 0.85)
        .AddImageStamp(0, 460, 410, 100, 60, stampImage, "Incremental RGBA image stamp")
        .Build();
    if (!pdf.AsSpan(0, source.Length).SequenceEqual(source))
        throw new InvalidDataException("The incremental visual annotation update changed source bytes.");
    string destination = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote six visual annotations in {pdf.Length - source.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--pdfa-page-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    string sourcePath = Path.GetFullPath(args[2]);
    string destination = Path.GetFullPath(args[3]);
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF PDF/A page operations smoke test", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddPage(600, 400, new PdfContentStreamBuilder()
            .SetFillRgb(0.9, 0.15, 0.25).Rectangle(50, 50, 500, 300).Fill())
        .AddPage(500, 500, new PdfContentStreamBuilder()
            .SetFillRgb(0.15, 0.7, 0.3).Rectangle(50, 50, 400, 400).Fill())
        .AddPage(400, 600, new PdfContentStreamBuilder()
            .SetFillRgb(0.1, 0.4, 0.9).Rectangle(50, 50, 300, 500).Fill())
        .AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman)
        .AddPageLabelRange(2, PdfPageLabelStyle.Decimal, "Appendix ")
        .Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(source))
        .RemovePage(1)
        .RotateClockwise(0)
        .SetCropBox(1, 25, 25, 350, 550)
        .MovePage(0, 1)
        .InsertBlankPage(1, 300, 300)
        .Build();
    if (!pdf.AsSpan(0, source.Length).SequenceEqual(source))
        throw new InvalidDataException("The incremental page update changed source bytes.");
    Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(sourcePath, source);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Inserted and removed pages, reordered two, rotated one, and cropped one in {pdf.Length - source.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--pdfa-import-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    byte[] importSource = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF page import source", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddPage(400, 600, new PdfContentStreamBuilder()
            .SetFillRgb(0.1, 0.4, 0.9).Rectangle(50, 50, 300, 500).Fill())
        .AddPage(600, 400, new PdfContentStreamBuilder()
            .SetFillRgb(0.9, 0.15, 0.25).Rectangle(50, 50, 500, 300).Fill())
        .AddTextNote(0, 340, 540, "Imported archival annotation")
        .AddPageLink(0, 20, 20, 40, 20, 1)
        .AddNamedDestination("imported-appendix", 1)
        .AddNamedDestinationLink(0, 72, 40, 120, 20, "imported-appendix")
        .AddPageLabelRange(0, PdfPageLabelStyle.Decimal, "Imported ")
        .AddBookmark("Imported appendix", 1)
        .AddCheckBox(1, "import.approved", 520, 330, 18, 18, isChecked: true)
        .Build();
    byte[] target = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF PDF/A page import smoke test", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddPage(300, 300, new PdfContentStreamBuilder()
            .SetFillRgb(0.55, 0.2, 0.75).Rectangle(40, 40, 220, 220).Fill())
        .AddNamedDestination("imported-appendix", 0)
        .AddPageLabelRange(0, PdfPageLabelStyle.None, "Cover")
        .AddBookmark("Target cover", 0)
        .AddCheckBox(0, "target.approved", 250, 250, 18, 18, isChecked: false)
        .Build();
    PdfDocument sourceDocument = PdfDocument.Open(importSource);
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(sourceDocument)
        .Build();
    if (!pdf.AsSpan(0, target.Length).SequenceEqual(target))
        throw new InvalidDataException("The incremental page import changed target source bytes.");
    string sourcePath = Path.GetFullPath(args[2]);
    string destination = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(sourcePath, target);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Imported two linked PDF/A pages in {pdf.Length - target.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 2 && args[0] == "--document-import-smoke")
{
    byte[] source = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF complete import source", Language = "en-US" })
        .AddBlankPage(400, 600)
        .AddBlankPage(600, 400)
        .AddBookmark("Imported appendix", 1)
        .AddBookmark("Imported detail", 0, 1)
        .AddNamedDestination("imported-appendix", 1)
        .AddNamedDestinationLink(0, 40, 40, 120, 24, "imported-appendix")
        .AddPageLabelRange(0, PdfPageLabelStyle.Decimal, "Imported ")
        .AddAttachment("source.txt", "source attachment"u8.ToArray(), "text/plain")
        .Build();
    byte[] target = new PdfDocumentBuilder()
        .AddBlankPage(300, 300)
        .AddNamedDestination("imported-appendix", 0)
        .AddPageLabelRange(0, PdfPageLabelStyle.None, "Cover")
        .AddBookmark("Target cover", 0)
        .AddAttachment("target.txt", "target attachment"u8.ToArray(), "text/plain")
        .Build();
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(PdfDocument.Open(source))
        .Build();
    if (!pdf.AsSpan(0, target.Length).SequenceEqual(target))
        throw new InvalidDataException("The complete document import changed target bytes.");
    string destination = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Imported bookmarks, navigation metadata, and attachments in {pdf.Length - target.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 4 && args[0] == "--import-document")
{
    PdfDocument importSource = PdfDocument.Open(File.ReadAllBytes(args[1]));
    byte[] target = File.ReadAllBytes(args[2]);
    byte[] pdf = new PdfIncrementalPageEditor(PdfDocument.Open(target))
        .AddImportedDocument(importSource)
        .Build();
    if (!pdf.AsSpan(0, target.Length).SequenceEqual(target))
        throw new InvalidDataException("The incremental document import changed target source bytes.");
    string destination = Path.GetFullPath(args[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Imported {new PdfIncrementalPageEditor(importSource).PageCount:N0} pages in " +
        $"{pdf.Length - target.Length:N0} appended bytes to {destination}");
    return 0;
}

if (args.Length == 3 && args[0] == "--pdfa-navigation-smoke")
{
    PdfIccProfile profile = PdfIccProfile.Load(File.ReadAllBytes(args[1]));
    byte[] pdf = new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "KillerPDF PDF/A navigation smoke test", Language = "en-US" })
        .SetOutputIntent(profile, "sRGB IEC61966-2.1")
        .EnablePdfA4Conformance()
        .AddBlankPage().AddBlankPage().AddBlankPage()
        .AddNamedDestination("appendix", 2)
        .AddNamedDestination("résumé", 1)
        .SetNamedOpenAction("résumé")
        .AddNamedDestinationLink(0,
            [
                new PdfTextQuad(new PdfPoint(72, 704), new PdfPoint(252, 704),
                    new PdfPoint(72, 680), new PdfPoint(252, 680)),
                new PdfTextQuad(new PdfPoint(72, 674), new PdfPoint(190, 674),
                    new PdfPoint(72, 650), new PdfPoint(190, 650))
            ], "appendix",
            new PdfLinkAppearance(2, PdfLinkBorderStyle.Dashed, [5, 2],
                new PdfRgbColor(0.15, 0.35, 0.85), PdfLinkHighlightMode.Push,
                horizontalCornerRadius: 4, verticalCornerRadius: 4),
            annotationMetadata: new PdfAnnotationMetadata
            {
                Author = "KillerPDF",
                Subject = "Appendix navigation",
                ModificationDate = new DateTimeOffset(
                    2026, 8, 23, 12, 0, 0, TimeSpan.FromHours(-7)),
                Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked
            },
            contents: "Open the appendix")
        .AddPageLink(1, 72, 680, 180, 24, 2,
            destination: PdfDestination.FitRectangle(50, 50, 550, 740))
        .AddBookmark("Document overview", 0, options: new PdfBookmarkOptions
        {
            Style = PdfBookmarkStyle.Bold,
            Color = new PdfRgbColor(0.12, 0.45, 0.2),
            IsOpen = false,
            Destination = PdfDestination.At(72, 720, 1.25)
        })
        .AddBookmark("Résumé", 1, 1, new PdfBookmarkOptions
        {
            Style = PdfBookmarkStyle.Italic,
            Destination = PdfDestination.FitWidth(760)
        })
        .AddNamedDestinationBookmark("Appendix", "appendix", 1, new PdfBookmarkOptions
        {
            Color = new PdfRgbColor(0.2, 0.3, 0.75)
        })
        .AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman)
        .AddPageLabelRange(2, PdfPageLabelStyle.Decimal, "Appendix ", 1)
        .Build();
    string destination = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, pdf);
    Console.WriteLine($"Wrote {pdf.Length:N0} byte PDF/A-4 navigation PDF to {destination}");
    return 0;
}

if (args.Length is 2 or 4 && args[0] == "--selected-page-import-corpus")
{
    string directory = Path.GetFullPath(args[1]);
    int importMaximum = int.MaxValue;
    if (args.Length == 4)
    {
        if (args[2] != "--max" || !int.TryParse(args[3], out importMaximum) || importMaximum <= 0)
            throw new ArgumentException("Use --max followed by a positive integer.");
    }
    string[] importFiles = [.. Directory.EnumerateFiles(directory, "*.pdf", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Take(importMaximum)];
    byte[] emptyTarget = new PdfDocumentBuilder().Build();
    int imported = 0;
    int unsupported = 0;
    int empty = 0;
    int malformed = 0;
    int rejected = 0;
    int importFailed = 0;
    var unsupportedReasons = new Dictionary<string, int>(StringComparer.Ordinal);
    var rejectedReasons = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (string file in importFiles)
    {
        PdfDocument source;
        try
        {
            source = PdfDocument.Open(File.ReadAllBytes(file));
            if (new PdfIncrementalPageEditor(source).PageCount == 0)
            {
                empty++;
                continue;
            }
        }
        catch (Exception exception)
        {
            malformed++;
            Console.WriteLine($"SOURCE {file}: {exception.GetType().Name}: {exception.Message}");
            continue;
        }
        byte[] output;
        try
        {
            output = new PdfIncrementalPageEditor(PdfDocument.Open(emptyTarget))
                .AddImportedPage(source, 0)
                .Build();
        }
        catch (NotSupportedException exception)
        {
            unsupported++;
            unsupportedReasons[exception.Message] =
                unsupportedReasons.GetValueOrDefault(exception.Message) + 1;
            continue;
        }
        catch (PdfSyntaxException exception)
        {
            malformed++;
            Console.WriteLine($"SOURCE {file}: {exception.GetType().Name}: {exception.Message}");
            continue;
        }
        catch (InvalidOperationException exception)
        {
            rejected++;
            rejectedReasons[exception.Message] =
                rejectedReasons.GetValueOrDefault(exception.Message) + 1;
            continue;
        }
        catch (Exception exception)
        {
            importFailed++;
            Console.WriteLine($"FAIL {file}: {exception.GetType().Name}: {exception.Message}");
            continue;
        }
        try
        {
            PdfDocument reopened = PdfDocument.Open(output);
            if (new PdfIncrementalPageEditor(reopened).PageCount != 1)
                throw new InvalidDataException("The selected-page output does not contain exactly one page.");
            imported++;
        }
        catch (Exception exception)
        {
            importFailed++;
            Console.WriteLine($"FAIL {file}: {exception.GetType().Name}: {exception.Message}");
        }
    }
    Console.WriteLine(
        $"Selected-page corpus: {importFiles.Length:N0} files, {imported:N0} imported, " +
        $"{unsupported:N0} intentionally unsupported, {empty:N0} empty, " +
        $"{malformed:N0} malformed or credential-protected sources, " +
        $"{rejected:N0} rejected by import validation, {importFailed:N0} unexpected failures.");
    foreach (var reason in unsupportedReasons.OrderByDescending(entry => entry.Value)
                 .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {reason.Value:N0} x {reason.Key}");
    foreach (var reason in rejectedReasons.OrderByDescending(entry => entry.Value)
                 .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {reason.Value:N0} rejected x {reason.Key}");
    return importFailed == 0 ? 0 : 1;
}

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: KillerPdf.Engine.Corpus <directory> [--max <count>] [--structural|--incremental-structural]");
    Console.WriteLine("       KillerPdf.Engine.Corpus --render-corpus <directory> [--max <count>] [--timeout-seconds <count>] [--size <pixels>] [--parallel <count>] [--password-manifest <file.json>] [--certificate-manifest <file.json>]");
    Console.WriteLine("       KillerPdf.Engine.Corpus --ocr-train-corpus <directory> <output.model> [--max <count>] [--size <pixels>] [--pages-per-file <count>] [--holdout-percent <1-99>] [--model-width <1-128>] [--model-height <1-128>] [--label-file <file.txt>] [--password-manifest <file.json>] [--certificate-manifest <file.json>]");
    Console.WriteLine("       KillerPdf.Engine.Corpus --selected-page-import-corpus <directory> [--max <count>]");
    Console.WriteLine("       KillerPdf.Engine.Corpus --authoring-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --tagged-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --tagged-import-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --tagged-subset-import-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --form-subset-import-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --layer-subset-import-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --layers-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --layered-occupied-merge-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --signature-smoke <openssl.exe> <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --encrypted-rewrite-smoke <input.pdf> <password> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --encrypted-structural-rewrite-smoke <input.pdf> <password> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --encrypted-incremental-structural-smoke <input.pdf> <password> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --structural-rewrite-smoke <input.pdf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --encrypted-authoring-smoke <output.pdf> <user-password> <owner-password>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --tagged-removal-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --tagged-merge-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --tagged-remove-merge-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --transparency-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --gradient-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --form-xobject-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --tiling-pattern-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --font-info <font.ttf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --unicode-smoke <font.ttf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --text-state-smoke <font.ttf> <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --image-smoke <image.jpg> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --form-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --output-intent-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --presentation-effects-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --cmyk-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa4f-attachment-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa4e-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa-form-smoke <font.ttf> <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa-cff-smoke <font.otf> <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa-annotation-smoke <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa-visual-annotation-smoke <font.ttf> <profile.icc> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --incremental-smoke <input.pdf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --rotation-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfua-link-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfua-form-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --incremental-annotation-smoke <input.pdf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --incremental-visual-annotation-smoke <font.ttf> <input.pdf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa-page-smoke <profile.icc> <source.pdf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa-import-smoke <profile.icc> <target.pdf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --document-import-smoke <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --import-document <source.pdf> <target.pdf> <output.pdf>");
    Console.WriteLine("       KillerPdf.Engine.Corpus --pdfa-navigation-smoke <profile.icc> <output.pdf>");
    return args.Length == 0 ? 2 : 0;
}

string root = Path.GetFullPath(args[0]);
if (!Directory.Exists(root))
{
    Console.Error.WriteLine($"Directory not found: {root}");
    return 2;
}

int maximum = int.MaxValue;
bool structural = false;
bool incrementalStructural = false;
for (int index = 1; index < args.Length; index++)
{
    if (args[index] == "--structural")
    {
        structural = true;
        continue;
    }
    if (args[index] == "--incremental-structural")
    {
        incrementalStructural = true;
        continue;
    }
    if (args[index] != "--max" || index + 1 >= args.Length
        || !int.TryParse(args[++index], out maximum) || maximum < 1)
    {
        Console.Error.WriteLine("Expected --max followed by a positive integer.");
        return 2;
    }
}

string[] files = [.. Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
    .Order(StringComparer.OrdinalIgnoreCase)
    .Take(maximum)];
int passed = 0;
int failed = 0;
var started = DateTimeOffset.UtcNow;

foreach (string file in files)
{
    try
    {
        byte[] sourceBytes = File.ReadAllBytes(file);
        if (incrementalStructural)
        {
            PdfDocument source = PdfDocument.Open(sourceBytes);
            var update = new PdfIncrementalUpdateBuilder(source);
            PdfIndirectReference marker = update.AddObject(new PdfString(
                "KillerPDF incremental corpus marker"u8, PdfStringForm.Literal));
            bool supportsStreams = source.Header.Version.CompareTo(new PdfVersion(1, 5)) >= 0;
            byte[] updated = update.Build(supportsStreams
                ? new PdfIncrementalUpdateWriteOptions
                {
                    CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                    UseObjectStreams = true,
                    CompressObjectStreams = true,
                    CompressCrossReferenceStream = true
                }
                : null);
            PdfDocument reopened = PdfDocument.Open(updated);
            PdfString value = reopened.Resolve(marker) as PdfString
                ?? throw new InvalidDataException("The incremental corpus marker did not resolve.");
            if (!value.Bytes.Span.SequenceEqual("KillerPDF incremental corpus marker"u8))
                throw new InvalidDataException("The incremental corpus marker changed.");
            if (!updated.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes))
                throw new InvalidDataException("The incremental update changed source bytes.");
            passed++;
            Console.WriteLine($"PASS  {Path.GetRelativePath(root, file)}  incremental");
            continue;
        }
        PdfDocumentWriteOptions? options = new()
        {
            AllowSignatureInvalidation = true
        };
        if (structural)
        {
            PdfVersion sourceVersion = PdfDocument.Open(sourceBytes).Header.Version;
            options = new PdfDocumentWriteOptions
            {
                TargetVersion = sourceVersion.CompareTo(new PdfVersion(1, 5)) < 0
                    ? new PdfVersion(1, 5) : null,
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressStructuralStreams = true,
                AllowSignatureInvalidation = true
            };
        }
        PdfRoundTripResult result = PdfRoundTripValidator.Validate(sourceBytes, options);
        if (result.Succeeded)
        {
            passed++;
            Console.WriteLine($"PASS  {Path.GetRelativePath(root, file)}  {result.RewrittenSha256}");
        }
        else
        {
            failed++;
            string findings = string.Join(
                "; ",
                result.SourceInspection.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
            string summary = result.Failure?.Format() ?? result.FailureMessage
                ?? "Unknown validation failure.";
            string details = findings.Length > 0 ? $"{summary} ({findings})" : summary;
            Console.WriteLine($"FAIL  {Path.GetRelativePath(root, file)}  {details}");
        }
    }
    catch (Exception error)
    {
        failed++;
        Console.WriteLine($"FAIL  {Path.GetRelativePath(root, file)}  {error.Message}");
    }
}

TimeSpan elapsed = DateTimeOffset.UtcNow - started;
Console.WriteLine($"Checked {files.Length:N0}: {passed:N0} passed, {failed:N0} failed in {elapsed.TotalSeconds:N1}s.");
return failed == 0 ? 0 : 1;

internal sealed record CertificateCredential(string KeyStorePath, string Password);
