using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The kit update check (Services/UpdateService.cs) and the real portable publication helper
/// (Services/UpdateImagePublication.cs), run headless through hidden PowerShell with a copy of
/// cmd.exe standing in for the download. The helper's own check overrides stand in for the
/// publisher signature and the relaunch, so nothing is opened.
/// </summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.8.5", null)]
    [InlineData("v1.8.4", null)]
    [InlineData("v1.8.6", "v1.8.6")]
    [InlineData("v1.10.0", "v1.10.0")]
    [InlineData("1.9.0", "v1.9.0")]
    [InlineData("v1.8.6-enhanced", null)]
    [InlineData("v1.8.6-beta", null)]
    [InlineData("../v1.8.6", null)]
    public void OnlyNewerStableReleaseTagsAreOffered(string tag, string? offered)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { tag_name = tag });
        Assert.Equal(offered, UpdateService.NewerReleaseTag(json, new Version(1, 8, 5, 0)));
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v9.0.0\",\"draft\":true}")]
    [InlineData("{\"tag_name\":\"v9.0.0\",\"prerelease\":true}")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("invalid JSON")]
    public void UnpublishedOrMalformedReleaseMetadataIsNotOffered(string json)
        => Assert.Null(UpdateService.NewerReleaseTag(json, new Version(1, 8, 5)));

    private static readonly byte[] StandIn = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

    [Fact]
    public void VerifiedStandInIsPublishedAndRelaunched()
    {
        using var run = new HelperRun(StandIn);
        Assert.True(run.Exit == 0, run.ToString());
        Assert.Equal(StandIn, File.ReadAllBytes(run.Target));
        Assert.True(run.Relaunched, run.ToString());
        Assert.False(File.Exists(run.Download), run.ToString());
        Assert.Empty(Directory.GetFiles(run.Root, "KillerPDF-Portable.exe.new-*"));
    }

    [Fact]
    public void DownloadSwappedAfterVerificationIsRefused()
    {
        using var run = new HelperRun([.. StandIn, 0]);
        Assert.True(run.Exit != 0, run.ToString());
        Assert.Equal(HelperRun.Original, File.ReadAllBytes(run.Target));
        Assert.False(run.Relaunched, run.ToString());
        Assert.True(File.Exists(run.Download + ".recovery.txt"), run.ToString());
        Assert.Empty(Directory.GetFiles(run.Root, "KillerPDF-Portable.exe.new-*"));
    }

    [Fact]
    public void ChangedDownloadStillReopensTheInstalledCopy()
    {
        // The download fails its recheck before anything is published. The installed copy's identity
        // must already be known, so the helper can reopen it instead of leaving the app closed.
        using var run = new HelperRun([.. StandIn, 0], "$ops.VerifiedTargetThumb = { param($path, $signer) '" + new string('0', 40) + "' }");
        Assert.True(run.Exit != 0, run.ToString());
        Assert.Equal(HelperRun.Original, File.ReadAllBytes(run.Target));
        Assert.True(run.Relaunched, run.ToString());
        Assert.True(File.Exists(run.Download + ".recovery.txt"), run.ToString());
    }

    [Fact]
    public void FailedIdentityCaptureIsWrittenIntoTheRecoveryReport()
    {
        using var run = new HelperRun([.. StandIn, 0], "$ops.VerifiedTargetThumb = { param($path, $signer) throw 'fixture identity failure' }");
        Assert.True(run.Exit != 0, run.ToString());
        Assert.False(run.Relaunched, run.ToString());
        Assert.Contains("identity of the installed copy could not be captured", File.ReadAllText(run.Download + ".recovery.txt"));
    }

    private sealed class HelperRun : IDisposable
    {
        internal static readonly byte[] Original = Encoding.UTF8.GetBytes("original inert launcher image");

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "KillerPDF-update-helper-" + Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(Root, "KillerPDF-Portable.exe");
        internal string Download => Path.Combine(Root, "download.exe");
        internal int Exit { get; }
        internal bool Relaunched => File.Exists(Path.Combine(Root, "relaunched.txt"));

        internal HelperRun(byte[] download, string? extraOverrides = null)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Target, Original);
            File.WriteAllBytes(Download, download);
            UpdateImagePublication.RememberVerified(Download, Convert.ToHexString(SHA256.HashData(StandIn)), new string('0', 40));
            string overrides = "$ops.Publisher = { param($path, $plan) $true }\n" +
                "$ops.Relaunch = { param($plan) [IO.File]::WriteAllText('" + Path.Combine(Root, "relaunched.txt") + "', 'yes') }" +
                (extraOverrides is null ? "" : "\n" + extraOverrides);
            string script = UpdateImagePublication.Script(Download, Target, false,
                "https://example.invalid/releases", "KillerPDF", overrides: overrides);
            var start = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                ImmutablePowerShell.Arguments(script))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string errors = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(true);
                Assert.Fail("The update helper did not finish. Scratch retained: " + Root);
            }
            Exit = process.ExitCode;
            Errors = errors;
        }

        private string Errors { get; } = "";

        public override string ToString()
        {
            string report = Download + ".recovery.txt";
            return "Exit " + Exit + ", relaunched " + Relaunched + Environment.NewLine + Errors +
                (File.Exists(report) ? Environment.NewLine + File.ReadAllText(report) : "");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
