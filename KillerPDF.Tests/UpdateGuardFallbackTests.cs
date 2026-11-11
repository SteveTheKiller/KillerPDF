using System;
using System.IO;
using KillerPDF.Controls;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class UpdateGuardFallbackTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async System.Threading.Tasks.Task GuardFailureHonorsRequestedReportAndStillReopensTheApp(bool showReport, bool reportLaunchFails)
    {
        string root = Path.Combine(Path.GetTempPath(), "killerpdf-guard-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "app.exe");
            string download = Path.Combine(root, "download.exe");
            string calls = Path.Combine(root, "launches.txt");
            string reopen = "\"" + Path.Combine(root, "book to reopen.pdf") + "\"";
            File.WriteAllText(target, "inert installed image");
            UpdateImagePublication.RememberVerified(download, new string('A', 64), new string('B', 40));
            string prelude = "$fixtureCalls = '" + calls.Replace("'", "''") + "'\n" +
                "$fixtureReportFails = " + (reportLaunchFails ? "$true" : "$false") + "\n" + """
$ErrorActionPreference = 'Stop'
function Add-Type { throw 'Fixture guard unavailable' }
function New-Object { throw 'Fixture guard unavailable' }
function Start-Process {
    param($FilePath, $ArgumentList)
    [IO.File]::AppendAllText($fixtureCalls, $FilePath + '|' + $ArgumentList + [Environment]::NewLine)
    if ($fixtureReportFails -and [IO.Path]::GetFileName($FilePath) -eq 'notepad.exe') { throw 'Fixture report launch failed' }
}
""";
            string script = prelude + "\n" + UpdateImagePublication.Script(download, target, false,
                "https://example.invalid/releases", "KillerPDF", reopen: reopen, showReport: showReport);
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
                Assert.Fail("The guard failure fixture did not finish: " + root);
            }
            string standardOutput = await output;
            string standardError = await errors;
            Assert.True(process.ExitCode == 1, standardOutput + standardError);
            Assert.Contains("Fixture guard unavailable", File.ReadAllText(download + ".recovery.txt"));
            string appCall = target + "|" + reopen;
            string reportCall = Path.Combine(Environment.SystemDirectory, "notepad.exe") + "|\"" + download + ".recovery.txt\"";
            Assert.Equal(showReport ? new[] { reportCall, appCall } : new[] { appCall }, File.ReadAllLines(calls));
            Assert.Equal("inert installed image", File.ReadAllText(target));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string HelperScript(bool elevated)
    {
        string path = Path.Combine(Path.GetTempPath(), "KillerPDF.Tests", "guard", Guid.NewGuid().ToString("N") + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        UpdateImagePublication.RememberVerified(path, new string('A', 64), new string('B', 40));
        try
        {
            return UpdateImagePublication.Script(path, @"C:\Apps\KillerPDF.exe", elevated,
                "https://example.invalid/releases", "KillerPDF");
        }
        finally { if (File.Exists(path + ".recovery.txt")) File.Delete(path + ".recovery.txt"); }
    }

    [Fact]
    public void PerUserHelperReopensTheAppWhenItsGuardCannotCompile()
    {
        // An antivirus or locked-down PowerShell can refuse Add-Type. The per-user helper runs as
        // the same user as the app, so it falls back to a plain start of the installed copy.
        string script = HelperScript(elevated: false);
        int guard = script.IndexOf("if (-not ('KillerUpdateGuard' -as [type]))", StringComparison.Ordinal);
        int wrapper = script.LastIndexOf("try {", guard, StringComparison.Ordinal);
        int fallback = script.IndexOf("Start-Process -FilePath $plan.Target", guard, StringComparison.Ordinal);
        Assert.True(guard > 0, "The guard is emitted.");
        Assert.True(wrapper >= 0 && script.IndexOf("$plan = ", StringComparison.Ordinal) < wrapper,
            "The guard is wrapped after the plan exists.");
        Assert.True(fallback > guard, "A guard failure starts the installed copy.");
        Assert.True(fallback < script.IndexOf("Invoke-UpdateImagePublication $plan $ops", StringComparison.Ordinal),
            "The fallback belongs to the guard, not to the publication.");
    }

    [Fact]
    public void ElevatedHelperKeepsItsGuardUnwrapped()
    {
        // The elevated helper must never start the app as administrator, so it gets no plain fallback.
        Assert.DoesNotContain("Start-Process -FilePath $plan.Target", HelperScript(elevated: true));
    }

    [Fact]
    public void RelaunchTaskAndSweepCarryTheAppPrefix()
    {
        // Every Killer app shares the KillerUpdate- stem, so the sweep must never match another app's task.
        string prefix = UpdateImagePublication.TaskPrefix(AppInfo.DisplayName);
        Assert.Equal("KillerUpdate-KillerPDF-", prefix);
        string script = HelperScript(elevated: false);
        Assert.Contains("$name = '" + prefix + "' + [Guid]", script);
        Assert.Contains("$old.Name -notlike '" + prefix + "*'", script);
        Assert.DoesNotContain("'KillerUpdate-*'", script);
        Assert.DoesNotContain("__KILLER_TASK_PREFIX__", script);
    }

    [Fact]
    public void FailedListingStillAcceptsAFullTypedPath()
    {
        Assert.True(PickerAcceptRule.CanAccept(false, false, false, false, @"C:\Data\x.txt"));
        Assert.False(PickerAcceptRule.CanAccept(false, false, false, false, "x.txt"));
        Assert.False(PickerAcceptRule.CanAccept(true, false, false, false, @"C:\Data\x.txt"));
        Assert.False(PickerAcceptRule.CanAccept(false, false, false, true, @"C:\Data"));
    }

    [Fact]
    public void MarkedShortcutHintHidesInAnyLanguage()
    {
        var converter = new KillerFamily.Layout.TooltipTextConverter();
        foreach (var (text, bare) in new[] { (KillerFamily.Layout.ShortcutHints.WithHint("Today", "T"), "Today"),
                                             (KillerFamily.Layout.ShortcutHints.WithHint("Duplizieren", "Strg+D"), "Duplizieren") })
            Assert.Equal(bare, converter.Convert(text, typeof(string), false, System.Globalization.CultureInfo.InvariantCulture));
    }
}
