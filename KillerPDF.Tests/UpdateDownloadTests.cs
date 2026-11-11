using System.IO;
using System.Net;
using System.Net.Http;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The update download streams through HttpDownload with a size cap, tells a refused update from a
/// dropped connection, and the release check reads the JSON fields rather than the notes text.
/// A stub message handler stands in for GitHub, so nothing touches the network.
/// </summary>
public sealed class UpdateDownloadTests
{
    [Fact]
    public void NotesThatQuoteTheDraftFlagDoNotHideTheRelease()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = "v9.0.0",
            body = "Notes mention \"prerelease\": true and \"draft\": true and \"tag_name\": \"v0.0.1\"."
        });
        Assert.Equal("v9.0.0", UpdateService.NewerReleaseTag(json, new Version(1, 8, 5)));
    }

    [Fact]
    public void ANestedTagNameIsNotTheReleaseTag()
    {
        const string json = "{\"author\":{\"tag_name\":\"v9.0.0\"},\"tag_name\":\"v1.0.0\"}";
        Assert.Null(UpdateService.NewerReleaseTag(json, new Version(1, 8, 5)));
    }

    [Fact]
    public async Task AnInstallerOverTheSizeCapIsRefusedAndLeavesNoFile()
    {
        string folder = NewFolder();
        try
        {
            string path = Path.Combine(folder, "update.exe");
            using var http = new HttpClient(new Stub(new byte[4096], declareLength: true));
            await Assert.ThrowsAsync<HttpDownloadSizeException>(() =>
                UpdateService.DownloadToFileAsync(http, new Uri("https://example.invalid/a.exe"), path, 1024, TimeSpan.FromSeconds(30)));
            Assert.False(File.Exists(path));

            using var undeclared = new HttpClient(new Stub(new byte[4096], declareLength: false));
            await Assert.ThrowsAsync<HttpDownloadSizeException>(() =>
                UpdateService.DownloadToFileAsync(undeclared, new Uri("https://example.invalid/a.exe"), path, 1024, TimeSpan.FromSeconds(30)));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AnInstallerWithinTheCapIsWrittenAndHashed()
    {
        string folder = NewFolder();
        try
        {
            byte[] body = new byte[2048];
            new Random(7).NextBytes(body);
            string path = Path.Combine(folder, "update.exe");
            using var http = new HttpClient(new Stub(body, declareLength: true));
            string hash = await UpdateService.DownloadToFileAsync(http, new Uri("https://example.invalid/a.exe"), path, 4096, TimeSpan.FromSeconds(30));
            Assert.Equal(body, File.ReadAllBytes(path));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)), hash, ignoreCase: true);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AChecksumThatDoesNotMatchIsAVerificationFailureWithNoFileLeft()
    {
        string folder = NewFolder();
        try
        {
            byte[] body = [1, 2, 3, 4];
            string wrong = new string('0', 64);
            using var http = new HttpClient(new Stub(body, declareLength: true, sums: "KillerPDF.exe " + wrong));
            var failure = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                UpdateService.DownloadVerifiedAsync(http, new Uri("https://example.invalid/KillerPDF.exe"),
                    new Uri("https://example.invalid/SHA256SUMS.txt"), "KillerPDF.exe", isPackage: false, "v9.0.0",
                    folder, 1024, TimeSpan.FromSeconds(30)));
            Assert.Contains("checksum", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task AMissingChecksumEntryIsAVerificationFailure()
    {
        string folder = NewFolder();
        try
        {
            using var http = new HttpClient(new Stub([1, 2, 3], declareLength: true, sums: "Other.exe " + new string('0', 64)));
            await Assert.ThrowsAsync<UpdateVerificationException>(() =>
                UpdateService.DownloadVerifiedAsync(http, new Uri("https://example.invalid/KillerPDF.exe"),
                    new Uri("https://example.invalid/SHA256SUMS.txt"), "KillerPDF.exe", isPackage: false, "v9.0.0",
                    folder, 1024, TimeSpan.FromSeconds(30)));
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ADroppedConnectionIsNotAVerificationFailure()
    {
        string folder = NewFolder();
        try
        {
            using var http = new HttpClient(new Stub([1], declareLength: true, status: HttpStatusCode.ServiceUnavailable));
            var failure = await Record.ExceptionAsync(() =>
                UpdateService.DownloadVerifiedAsync(http, new Uri("https://example.invalid/KillerPDF.exe"),
                    new Uri("https://example.invalid/SHA256SUMS.txt"), "KillerPDF.exe", isPackage: false, "v9.0.0",
                    folder, 1024, TimeSpan.FromSeconds(30)));
            Assert.NotNull(failure);
            Assert.IsNotType<UpdateVerificationException>(failure);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ThePortableSwapHelperReportsAnAppThatDidNotClose()
    {
        // The helper writes its recovery note beside the download, so the download lives in a folder
        // of the test's own, not in a fixed folder that may be missing or shared.
        string folder = NewFolder();
        try
        {
            string download = Path.Combine(folder, "KillerPDF_update_x.exe");
            var target = new UpdateService.UpdateTarget { Replace = @"C:\Tools\KillerPDF.exe" };
            UpdateImagePublication.RememberVerified(download, new string('a', 64), new string('b', 40));
            string unelevated = UpdateService.PackageHelperScript(download, target.Replace!, target, elevate: false, 4242, 1);
            Assert.Contains("did not close within a minute", unelevated, StringComparison.Ordinal);
            // The report path travels as Base64 so no quote in it can end a PowerShell literal.
            Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(download + ".recovery.txt")), unelevated, StringComparison.Ordinal);

            string elevated = UpdateService.PackageHelperScript(download, target.Replace!, target, elevate: true, 4242, 1);
            Assert.DoesNotContain("did not close within a minute", elevated, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void ARunAsAdministratorPortableUpdateUsesTheElevatedHelperSettings(bool appElevated, bool folderNeedsAdmin, bool expectUnelevated)
    {
        string folder = NewFolder();
        try
        {
            string download = Path.Combine(folder, "KillerPDF_update_x.exe");
            var target = new UpdateService.UpdateTarget { Replace = @"C:\Tools\KillerPDF.exe" };
            UpdateImagePublication.RememberVerified(download, new string('a', 64), new string('b', 40));
            var platform = new UpdateService.Platform { IsElevated = () => appElevated, CanWrite = _ => !folderNeedsAdmin };
            var start = UpdateService.PackageStart(download, @"C:\Tools\KillerPDF.exe", 4242, 1, target, platform);
            string script = Decode(start.Arguments);
            // Admin rights come from runas only when the folder needs them; an elevated app's helper inherits them.
            Assert.Equal(folderNeedsAdmin ? "runas" : "", start.Verb ?? "");
            if (expectUnelevated)
            {
                Assert.Contains("Elevated = $false", script, StringComparison.Ordinal);
                Assert.Contains("ShowReport = $true", script, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Elevated = $true", script, StringComparison.Ordinal);
                Assert.Contains("ShowReport = $false", script, StringComparison.Ordinal);
                Assert.DoesNotContain("did not close within a minute", script, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string Decode(string arguments)
    {
        const string prefix = "-NoLogo -NoProfile -NonInteractive -Command \"";
        string bootstrap = arguments.Substring(prefix.Length, arguments.Length - prefix.Length - 1);
        var payload = System.Text.RegularExpressions.Regex.Match(bootstrap, @"FromBase64String\('([A-Za-z0-9+/=]+)'\)");
        Assert.True(payload.Success, "The bootstrap carries no compressed script.");
        using var compressed = new MemoryStream(Convert.FromBase64String(payload.Groups[1].Value));
        using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void AboutReportsARefusedUpdateAndKeepsTheBrowserFallbackForNetworkFailures()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Features", "About", "AboutController.cs"));
        int download = source.IndexOf("UpdateService.DownloadAsync(", StringComparison.Ordinal);
        Assert.True(download >= 0);
        string afterDownload = source[download..];
        int typed = afterDownload.IndexOf("catch (UpdateVerificationException", StringComparison.Ordinal);
        int general = afterDownload.IndexOf("WebLink.Open(UpdateService.ReleasesUrl)", StringComparison.Ordinal);
        Assert.True(typed > 0 && general > typed, "The typed catch must come before the browser fallback.");
        Assert.Contains("UpdateNotVerified(", afterDownload[typed..general], StringComparison.Ordinal);

        string strings = File.ReadAllText(Path.Combine(RepositoryRoot(), "Strings", "en-US.xaml"));
        Assert.Contains("x:Key=\"Str_About_UpdateNotVerified\"", strings, StringComparison.Ordinal);
        string extras = File.ReadAllText(Path.Combine(RepositoryRoot(), "Features", "About", "AboutController.Extras.cs"));
        Assert.Contains("CrashReporter.Capture(", extras, StringComparison.Ordinal);
        Assert.Contains("Str_About_UpdateNotVerified", extras, StringComparison.Ordinal);
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-update-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }

    private sealed class Stub(byte[] body, bool declareLength, string? sums = null, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool checksums = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal);
            if (status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(status));
            HttpContent content = checksums
                ? new StringContent(sums ?? string.Empty)
                : declareLength ? new ByteArrayContent(body) : new StreamContent(new UnknownLengthStream(body));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class UnknownLengthStream(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
    }
}
