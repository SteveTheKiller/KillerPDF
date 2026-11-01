using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// When the elevated publication helper gives up before anything is published (the app did not
/// close, another install holds the mutex, the installed image changed), it reopens the installed
/// copy for the user. That relaunch runs as the signed-in user through KillerUpdateGuard.Launch,
/// which checks the desktop shell's session, account and non-elevated token, and never through an
/// explorer.exe hand-off that can end up running the app as administrator (BF-A004).
/// </summary>
public sealed class TrustedUpdateRelaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-trusted-relaunch-" + Guid.NewGuid().ToString("N"));

    public TrustedUpdateRelaunchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheEarlyFailureRelaunchGoesThroughTheGuard()
    {
        string staged = Path.Combine(_directory, ".update-" + Guid.NewGuid().ToString("N") + ".exe");
        string target = Path.Combine(_directory, "KillerPDF.exe");
        UpdateImagePublication.RememberVerified(staged, new string('A', 64), new string('B', 40));
        var start = TrustedUpdate.Publication(staged, target, "1.9.0", new string('C', 64), 4242, DateTime.UtcNow.Ticks, 4243, DateTime.UtcNow.Ticks);

        string script = Decode(start.Arguments);
        int guard = script.IndexOf("'KillerUpdateGuard' -as [type]", StringComparison.Ordinal);
        // The Wait prologue, not the guard's own GetProcessById inside Launch.
        int wait = script.IndexOf("$p=$null;try{$p=[Diagnostics.Process]::GetProcessById(", StringComparison.Ordinal);
        Assert.True(guard >= 0, "The guard type is not defined in the publication helper.");
        Assert.True(wait >= 0, "The early-failure wait prologue is missing.");
        Assert.True(guard < wait, "The guard type is defined after the wait prologue, so an early failure cannot use it.");

        string outerCatch = script.Substring(script.LastIndexOf("}catch{$message=", StringComparison.Ordinal));
        Assert.Contains("Start-KillerUpdatedApp ", outerCatch);
        Assert.DoesNotContain("explorer.exe", outerCatch);
        int wrapper = script.IndexOf("function Start-KillerUpdatedApp {", StringComparison.Ordinal);
        Assert.True(wrapper >= 0 && wrapper < wait, "The relaunch wrapper must exist before early failure.");
        string relaunch = script.Substring(wrapper, script.IndexOf("\n}\n", wrapper, StringComparison.Ordinal) - wrapper);
        Assert.Contains("[KillerUpdateGuard]::Launch($target, $reopen, $elevated)", relaunch);
        Assert.Contains("catch { if (-not $elevated) { throw } }", relaunch);
        Assert.Contains("$task.Principal.GroupId = 'S-1-5-32-545'", relaunch);
        Assert.Contains("$task.Principal.RunLevel = 0", relaunch);
        Assert.DoesNotContain("explorer.exe", relaunch);
    }

    // Inspect the actual immutable gzip payload without executing the publication helper.
    private static string Decode(string arguments)
    {
        const string prefix = "-NoLogo -NoProfile -NonInteractive -Command \"";
        Assert.StartsWith(prefix, arguments);
        Assert.EndsWith("\"", arguments);
        string bootstrap = arguments.Substring(prefix.Length, arguments.Length - prefix.Length - 1);
        Match payload = Regex.Match(bootstrap, @"FromBase64String\('([A-Za-z0-9+/=]+)'\)");
        Assert.True(payload.Success, "The bootstrap carries no compressed script.");
        using var compressed = new MemoryStream(Convert.FromBase64String(payload.Groups[1].Value));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
