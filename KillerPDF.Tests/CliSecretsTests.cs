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

    [Fact]
    public void HelpListsTheOptionSoCallersCanTellItIsSupported()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        Assert.Contains("--secrets-stdin", File.ReadAllText(Path.Combine(root, "Features", "Cli", "CliRunner.cs")), StringComparison.Ordinal);
    }
}
