using System;
using System.Collections.Generic;
using System.IO;
using KillerPDF.Features;
using Xunit;

namespace KillerPDF.Tests;

public sealed class CliSecretsTests
{
    [Fact]
    public void SecretsOnStandardInputBecomeTheOptionsTheyReplace()
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["--secrets-stdin"] = string.Empty, ["--dpi"] = "150" };
        Assert.Null(CliSecrets.Read(new StringReader("{\"password\":\"open sesame\",\"pfx-password\":\"pfx secret\"}"), options));
        Assert.Equal("open sesame", options["--password"]);
        Assert.Equal("pfx secret", options["--pfx-password"]);
        Assert.Equal("150", options["--dpi"]);
        Assert.False(options.ContainsKey("--secrets-stdin"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"dpi\":\"300\"}")]
    [InlineData("{\"password\":5}")]
    public void InputOtherThanKnownSecretsIsRefused(string text)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(CliSecrets.Read(new StringReader(text), options));
        Assert.False(options.ContainsKey("--dpi"));
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--user-password")]
    [InlineData("--owner-password")]
    [InlineData("--pfx-password")]
    public void APasswordOnTheCommandLineGetsAWarningThatPointsToStandardInput(string option)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [option] = "hunter2", ["--dpi"] = "150" };
        string? warning = CliSecrets.CommandLineWarning(options);
        Assert.NotNull(warning);
        Assert.Contains(option, warning, StringComparison.Ordinal);
        Assert.Contains("--secrets-stdin", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWarningWithoutACommandLinePassword() =>
        Assert.Null(CliSecrets.CommandLineWarning(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["--dpi"] = "150" }));

    [Fact]
    public void HelpListsTheOptionSoCallersCanTellItIsSupported()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        Assert.Contains("--secrets-stdin", File.ReadAllText(Path.Combine(root, "Features", "Cli", "CliRunner.cs")), StringComparison.Ordinal);
    }
}
