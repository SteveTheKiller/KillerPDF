using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class InstallerRegistrationTests
{
    private static string AppSource() => File.ReadAllText(Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!, "App.xaml.cs"));

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " was not found.");
        int end = source.IndexOf("\n        private static ", start + signature.Length, StringComparison.Ordinal);
        return source[start..end];
    }

    // A desktop shortcut that could not be written used to abort setup after the payload was copied
    // and before the uninstall entry existed, leaving a copy nothing could remove.
    [Fact]
    public void InstalledCopyIsRegisteredForUninstallBeforeOptionalSteps()
    {
        string register = Method(AppSource(), "private static string? RegisterInstalledCopy(");
        int uninstall = register.IndexOf("key.SetValue(\"UninstallString\"", StringComparison.Ordinal);
        Assert.True(uninstall >= 0);
        string[] steps = ["CreateShortcut(", "RegisterFileHandler(", "ProtocolRegistrar.Register(",
            "CommandLinePath.Register(", "PreviewHandlerRegistration."];
        foreach (string step in steps)
            Assert.True(uninstall < register.IndexOf(step, StringComparison.Ordinal), step + " runs before the uninstall entry.");
        // The entry must keep naming the version that is really installed. Setup's launcher rolls a
        // failed upgrade back, so there the version is written only after every required step. A
        // fresh setup install and the app's own installs have nothing to roll back to, so they
        // record it first. The size is measured last on every path, once every file is in place.
        int early = register.IndexOf("if (!setupRollsBack) RecordVersion();", StringComparison.Ordinal);
        int late = register.LastIndexOf("RecordVersion();", StringComparison.Ordinal);
        Assert.True(early > uninstall, "Installs without a rollback do not record the version after the uninstall entry.");
        Assert.DoesNotContain("if (setupRollsBack) RecordVersion();", register);
        foreach (string step in steps)
        {
            Assert.True(early < register.IndexOf(step, StringComparison.Ordinal), step + " runs before the early version.");
            Assert.True(late > register.LastIndexOf(step, StringComparison.Ordinal), "The final version and size come before " + step);
        }
        foreach (string version in new[] { "key.SetValue(\"DisplayVersion\"", "key.SetValue(\"Version\"", "key.SetValue(\"EstimatedSize\"" })
            Assert.Equal(1, register.Split(version).Length - 1);

        string source = AppSource();
        Assert.Contains("bool setupRollsBack = !e.Args.Any(a => string.Equals(a, \"/no-rollback\", StringComparison.OrdinalIgnoreCase));", source);
        Assert.Contains("RegisterInstalledCopy(machine, desktop, preview, setupRollsBack: setupRollsBack)", source);
        string launcher = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Packaging", "KillerLauncher", "Program.cs"));
        Assert.Contains("RunRegistration(destination, machine, desktop, preview, rollsBack: movedExisting)", launcher);
        Assert.Contains("if (!rollsBack) arguments.Add(\"/no-rollback\");", launcher);
    }

    // Setup rolls back only what registration has not yet committed. A backup that could not be
    // deleted after a successful registration used to restore a half-deleted previous copy under a
    // registry entry naming the new version. An empty folder, such as one made in the folder picker,
    // is not a previous copy, so a failed first install there no longer deletes the registered payload.
    [Fact]
    public void SetupRollsBackOnlyBeforeRegistrationCommits()
    {
        string launcher = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Packaging", "KillerLauncher", "Program.cs"));
        string install = launcher[launcher.IndexOf("Directory.CreateDirectory(parent);", StringComparison.Ordinal)..];
        install = install[..install.IndexOf("internal static string DefaultInstallDirectory(", StringComparison.Ordinal)];

        // An empty folder is removed, never renamed into a "previous copy", and a lock on it is
        // waited out the way a rename's is.
        int empty = install.IndexOf("if (!Directory.EnumerateFileSystemEntries(destination).Any()) DeleteEmptyDirectoryWithRetries(destination);", StringComparison.Ordinal);
        Assert.True(empty >= 0 && empty < install.IndexOf("movedExisting = true;", StringComparison.Ordinal));
        // The commit point follows the failed-registration throw as its own statement, not inside it.
        string failed = "LauncherStrings.Format(\"RegistrationFailed\", registrationExit));";
        int committed = install.IndexOf("registered = true;", StringComparison.Ordinal);
        int thrown = install.IndexOf(failed, StringComparison.Ordinal);
        Assert.True(thrown >= 0 && committed > thrown);
        Assert.DoesNotContain("{", install[(thrown + failed.Length)..committed]);
        Assert.True(committed < install.IndexOf("RunMaintenance(destination, \"/remove-user-install\");", StringComparison.Ordinal));
        // Nothing after the commit point can report a complete install as failed.
        Assert.Contains("try { RunMaintenance(destination, \"/remove-user-install\"); }", install);
        Assert.Contains("if (movedExisting) PortableDirectoryCleanup.TryDelete(backup);", install);
        Assert.DoesNotContain("DeleteDirectoryWithRetries(backup)", install);
        Assert.Contains("if (!registered && movedExisting && Directory.Exists(backup))", install);
        // A previous copy left behind earlier is removed by the next install.
        int sweep = install.IndexOf("foreach (string leftover in PortableDirectoryCleanup.LeftoverSiblings(destination))", StringComparison.Ordinal);
        Assert.True(sweep >= 0 && sweep < install.IndexOf("ExtractAndVerify(staging", StringComparison.Ordinal));
    }

    // Owner decision, 2026-10-23: an optional desktop shortcut that Windows refuses no longer fails
    // setup. The install finishes and reports a warning; a Start menu shortcut failure stays fatal.
    [Fact]
    public void DesktopShortcutFailureIsAWarningButStartMenuFailureIsNot()
    {
        string source = AppSource();
        string register = Method(source, "private static string? RegisterInstalledCopy(");
        int startMenu = register.IndexOf("CreateShortcut(startMenuShortcut, exePath);", StringComparison.Ordinal);
        Assert.True(startMenu >= 0);
        Assert.DoesNotContain("try", register[(register.LastIndexOf('\n', startMenu) + 1)..startMenu]);
        Assert.Contains("try { CreateShortcut(InstalledShortcuts.DesktopPath(machine), exePath); }", register);
        Assert.Contains("desktopProblem = ex.Message;", register);
        Assert.Contains("return desktopProblem;", register);

        Assert.Equal(2, source.Split("Console.Error.WriteLine(\"Installed without the desktop shortcut: \" + desktopProblem);").Length - 1);
        Assert.Contains("SetupText(\"Str_Dlg_InstallNoDesktopShortcut\"", source);

        string wizard = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Packaging", "KillerLauncher", "InstallerWizard.xaml.cs"));
        Assert.Contains("if (desktop && !Program.DesktopShortcutOpensInstalledCopy(machine, installDirectory))", wizard);
        Assert.Contains("LauncherStrings.Get(\"ShortcutSkipped\")", wizard);
    }

    // A declined or failed elevated uninstall used to exit 0.
    [Fact]
    public void ElevatedUninstallReportsItsOwnResult()
    {
        string source = AppSource();
        string relaunch = Method(source, "private static int? RelaunchMachineUninstallElevatedIfNeeded(");
        Assert.Contains("elevated.WaitForExit();", relaunch);
        Assert.Contains("return elevated.ExitCode;", relaunch);
        Assert.Contains("return 1223;", relaunch);
        Assert.Contains("return 1;", relaunch);
        Assert.Contains("is int elevatedExit) return elevatedExit;", source);
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
