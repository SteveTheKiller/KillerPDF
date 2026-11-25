using System.Collections;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The kit's runtime-injection refusal (KillerUI a62aadb): an elevated mode, or a runas start, must
/// not run with variables set that make the .NET runtime load someone else's code.
/// </summary>
public sealed class ElevatedStartTests
{
    [Fact]
    public void InjectionVariablesAreFoundByNameAndPrefixAndEmptyOnesAreIgnored()
    {
        var environment = new Hashtable
        {
            ["dotnet_startup_hooks"] = @"C:\Users\x\hook.dll",
            ["DOTNET_ROOT_X64"] = @"C:\Users\x\dotnet",
            ["CORECLR_PROFILER_PATH_64"] = @"C:\Users\x\p.dll",
            ["COR_ENABLE_PROFILING"] = "",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["Path"] = @"C:\Windows\System32",
        };

        Assert.Equal(["CORECLR_PROFILER_PATH_64", "DOTNET_ROOT_X64", "dotnet_startup_hooks"],
            ElevatedStart.InjectedVariables(environment));
        Assert.Empty(ElevatedStart.InjectedVariables(new Hashtable { ["TEMP"] = @"C:\Temp" }));
    }

    [Fact]
    public void ARunasStartIsRefusedWhileAStartupHookIsSet()
    {
        string? previous = Environment.GetEnvironmentVariable("DOTNET_ADDITIONAL_DEPS");
        Environment.SetEnvironmentVariable("DOTNET_ADDITIONAL_DEPS", @"C:\Users\x\extra.deps.json");
        try
        {
            var refused = Assert.Throws<SecurityException>(ElevatedStart.RefuseInjectedEnvironment);
            Assert.Contains("DOTNET_ADDITIONAL_DEPS", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ADDITIONAL_DEPS", previous);
        }
    }

    [Fact]
    public void HeadlessElevatedModesEnterThroughTheKitBeforeDoingAnything()
    {
        // Each pair: where the mode's work begins, and the first thing it does. Enter sits inside
        // the mode's own try, so a refusal becomes that mode's installer exit code.
        AssertEntersFirst(Source(Path.Combine("Services", "TrustedUpdate.cs")),
            "internal static int Apply(string[] args)", "ValidateRequest(args);");
        string app = Source("App.xaml.cs");
        AssertEntersFirst(app, "private static int DoSilentInstall()", "InstallerExitCodes.Acquire(");
        AssertEntersFirst(app, "bool setupRollsBack =", "RegisterInstalledCopy(machine,");
    }

    private static void AssertEntersFirst(string source, string mode, string work)
    {
        int at = source.IndexOf(mode, StringComparison.Ordinal);
        Assert.True(at >= 0, mode);
        int first = source.IndexOf(work, at, StringComparison.Ordinal);
        Assert.True(first > at, work);
        Assert.Contains("ElevatedStart.Enter()", source[at..first], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRunasStartRefusesAnInjectedEnvironmentFirst()
    {
        foreach (string file in new[] { "App.xaml.cs", Path.Combine("Services", "TrustedUpdate.cs"), Path.Combine("Services", "UpdateService.cs") })
        {
            string source = Source(file);
            var sites = Regex.Matches(source, @"Verb\s*=\s*""runas""");
            Assert.NotEmpty(sites);
            foreach (Match site in sites)
            {
                // PackageStart is a pure builder that tests call; StartPackage refuses before using it.
                if (source.LastIndexOf("internal static ProcessStartInfo PackageStart(", site.Index, StringComparison.Ordinal)
                    > source.LastIndexOf("private static void StartPackage(", site.Index, StringComparison.Ordinal)
                    && source.LastIndexOf("internal static ProcessStartInfo PackageStart(", site.Index, StringComparison.Ordinal) >= 0)
                {
                    int use = source.IndexOf("private static void StartPackage(", StringComparison.Ordinal);
                    int refuse = source.IndexOf("RefuseInjectedEnvironment()", use, StringComparison.Ordinal);
                    Assert.True(use >= 0 && refuse > use
                        && refuse < source.IndexOf("platform.Start(start)", use, StringComparison.Ordinal));
                    continue;
                }
                int start = Math.Max(0, site.Index - 1500);
                string before = source[start..site.Index];
                int last = before.LastIndexOf("RefuseInjectedEnvironment()", StringComparison.Ordinal);
                Assert.True(last >= 0 && !before[last..].Contains(" static ", StringComparison.Ordinal),
                    $"{file}: the runas start at offset {site.Index} must call ElevatedStart.RefuseInjectedEnvironment() in the same method first.");
            }
        }
    }

    [Fact]
    public void TheAppTurnsOffStartupHooksInTheRuntime()
    {
        Assert.Matches(new Regex(@"<StartupHookSupport>\s*false\s*</StartupHookSupport>"), Source("KillerPDF.csproj"));
    }

    private static string Source(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root."), relative));
    }
}
