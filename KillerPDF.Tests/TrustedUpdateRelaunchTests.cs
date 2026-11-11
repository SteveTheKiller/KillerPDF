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
        var start = TrustedUpdate.Publication(staged, target, "1.9.0", new string('C', 64), 4242, DateTime.UtcNow.Ticks, 4243, DateTime.UtcNow.Ticks, new string('B', 40));

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

public sealed class UpdateDeferredTaskBoundaryTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(3, 3, true)]
    [InlineData(2, 2, false)]
    [InlineData(4, 4, false)]
    [InlineData(3, 4, false)]
    public void DeferredTaskIsRemovedWhenNoInstanceOrRegistrationIsActive(int instanceState, int registeredState, bool deleted)
    {
        using var run = new DeferredRun(instanceState, registeredState);
        Assert.True(run.Exit == 0, run.Output);
        Assert.DoesNotContain("automatic relaunch failed", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(deleted, File.Exists(Path.Combine(run.Root, "deleted.txt")));
        Assert.Equal("updated image", File.ReadAllText(run.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredRelaunchOnlyAddsStatusToARetainedWarningReport(bool warning)
    {
        using var run = new DeferredRun(3, 3, warning);
        Assert.True(run.Exit == 0, run.Output);
        Assert.DoesNotContain("automatic relaunch failed", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("updated image", File.ReadAllText(run.Target));
        Assert.False(File.Exists(run.Download));
        Assert.Equal(warning, File.Exists(run.Download + ".recovery.txt"));
        if (warning)
        {
            Assert.Contains("Fixture metadata warning", run.Output);
            Assert.Contains("Automatic relaunch helper state: completed.", run.Output);
        }
    }

    private sealed class DeferredRun : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "killerpdf-task-boundary-" + Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(Root, "app.exe");
        internal string Download => Path.Combine(Root, "download.exe");
        internal int Exit { get; }
        internal string Output { get; }

        internal DeferredRun(int instanceState, int registeredState, bool warning = false)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Target, "original image");
            File.WriteAllText(Download, "updated image");
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Download)));
            UpdateImagePublication.RememberVerified(Download, hash, new string('B', 40));
            string prelude = """
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
public static class KillerUpdateGuard {
    public static IDisposable PinParents(string path) { return new MemoryStream(); }
    public static Stream OpenImage(string path) { return new MemoryStream(File.ReadAllBytes(path)); }
    public static bool Publisher(string path, string thumb, string signer) { return true; }
    public static void Launch(string path, string args, bool elevated) { throw new InvalidOperationException("No desktop token in fixture"); }
    public static int ConsoleSession() { return 1; }
    public static string SessionUser(int session) { return "S-1-5-21-1"; }
}
'@
$script:killerDeferredAssembly = [byte[]]@(1, 2, 3)
$fixtureInstance = [pscustomobject]@{ State = INSTANCE_STATE }
$fixtureInstance | Add-Member ScriptMethod Refresh { }
$fixtureRegistered = [pscustomobject]@{ State = REGISTERED_STATE }
$fixtureRegistered | Add-Member ScriptMethod RunEx { param($a,$b,$c,$d) $fixtureInstance }
$fixtureRegistered | Add-Member ScriptMethod GetSecurityDescriptor { param($flags) 'O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)' }
$folder = [pscustomobject]@{}
$folder | Add-Member ScriptMethod GetTasks { param($flags) @() }
$folder | Add-Member ScriptMethod RegisterTaskDefinition { param($a,$b,$c,$d,$e,$f,$g) $fixtureRegistered }
$folder | Add-Member ScriptMethod DeleteTask { param($name,$flags) [IO.File]::WriteAllText((Join-Path $fixtureRoot 'deleted.txt'), $name) }
$actions = [pscustomobject]@{}
$actions | Add-Member ScriptMethod Create { param($kind) [pscustomobject]@{Path='';WorkingDirectory='';Arguments=''} }
$task = [pscustomobject]@{
    RegistrationInfo=[pscustomobject]@{Date=''}
    Settings=[pscustomobject]@{Hidden=$false;DisallowStartIfOnBatteries=$true;StopIfGoingOnBatteries=$true;ExecutionTimeLimit=''}
    Principal=[pscustomobject]@{GroupId='';RunLevel=1}
    Actions=$actions
}
$service = [pscustomobject]@{}
$service | Add-Member ScriptMethod Connect { }
$service | Add-Member ScriptMethod GetFolder { param($path) $folder }
$service | Add-Member ScriptMethod NewTask { param($flags) $task }
function New-Object {
    param([string]$TypeName, [object[]]$ArgumentList, [string]$ComObject)
    if ($ComObject) {
        if ($ComObject -ne 'Schedule.Service') { throw 'Unexpected COM request' }
        return $service
    }
    Microsoft.PowerShell.Utility\New-Object @PSBoundParameters
}
function Start-Sleep { param($Milliseconds) }
function Start-Process { throw 'Unexpected process launch' }
""";
            prelude = "$fixtureRoot = '" + Root.Replace("'", "''") + "'\n" + prelude
                .Replace("INSTANCE_STATE", instanceState.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("REGISTERED_STATE", registeredState.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string script = prelude + "\n" + UpdateImagePublication.Script(Download, Target, true,
                "https://example.invalid/releases", "KillerPDF",
                overrides: warning ? "$ops.Metadata = { throw 'Fixture metadata warning' }" : null);
            var start = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                ImmutablePowerShell.Arguments(script))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var process = System.Diagnostics.Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(true);
                Assert.Fail("The deferred launch fixture did not finish: " + Root);
            }
            Exit = process.ExitCode;
            Output = "Exit " + Exit + "\n" + output.Result + errors.Result +
                (File.Exists(Download + ".recovery.txt") ? File.ReadAllText(Download + ".recovery.txt") : "");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
