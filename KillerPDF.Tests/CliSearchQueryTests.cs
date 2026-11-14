using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace KillerPDF.Tests;

// KillerMCP review KM-A step 7: the query of --search-text is the text to find, even when it looks like a switch.
// Read as a switch, --secrets-stdin made the app wait on stdin, which hung a KillerMCP call that has no input to send.
public sealed class CliSearchQueryTests
{
    private static string Exe()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("KILLERPDF_CLI_TEST_EXE");
        if (!string.IsNullOrEmpty(fromEnvironment)) return fromEnvironment;
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return Path.Combine(root, "bin", "Release", "net10.0-windows", "KillerPDF.exe");
    }

    [Theory]
    [InlineData("--secrets-stdin")]
    [InlineData("--password")]
    [InlineData("--json")]
    public async Task SearchTreatsAFlagShapedQueryAsLiteralText(string query)
    {
        string exe = Exe();
        Assert.True(File.Exists(exe), "Build KillerPDF.csproj in Release first: " + exe);
        string root = Path.Combine(Path.GetTempPath(), "killerpdf-search-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string pdf = Path.Combine(root, "text.pdf");
            File.WriteAllBytes(pdf, PdfContaining(query));
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "--search-text", pdf, query }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            // Leave input open so a query that turns into --secrets-stdin cannot hide the bug by seeing EOF.
            var output = process.StandardOutput.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { process.Kill(true); throw new Xunit.Sdk.XunitException("Search read stdin instead of searching the query."); }
            string text = await output;
            Assert.True(process.ExitCode == 0, text + await process.StandardError.ReadToEndAsync());
            Assert.Contains("\"pages\":[1]", text.Replace(" ", ""), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    private static byte[] PdfContaining(string query)
    {
        string stream = "BT /F1 12 Tf 20 100 Td (" + query + ") Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + stream.Length + " >>\nstream\n" + stream + "\nendstream",
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
