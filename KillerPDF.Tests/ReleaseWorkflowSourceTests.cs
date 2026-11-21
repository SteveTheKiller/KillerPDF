using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests
{
    // BF-A010 / BF-A011: the release workflows that hold publishing secrets take the tag only as
    // validated data and run only a pinned, hash-checked Komac.
    public class ReleaseWorkflowSourceTests
    {
        private static string Workflow(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml"))) dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from the test output folder");
            return File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", name));
        }

        [Fact]
        public void ChocolateyNeverSplicesTheTagIntoScriptText()
        {
            string workflow = Workflow("chocolatey-release.yml");

            // Expressions inside a run: block become script text; the tag may only arrive through env:.
            MatchCollection runs = Regex.Matches(workflow, @"run: \|\r?\n((?:[ ]{10}.*\r?\n?)+)");
            Assert.True(runs.Count >= 3, "expected the workflow's three run blocks");
            foreach (Match run in runs)
                Assert.DoesNotContain("${{ github.event", run.Groups[1].Value, StringComparison.Ordinal);
            Assert.Contains(@"-cnotmatch '^v\d{1,4}\.\d{1,4}\.\d{1,6}\z'", workflow, StringComparison.Ordinal);
        }

        // BF-A009: every check runs before the first change outside the checkout.
        [Fact]
        public void ReleaseScriptWritesNothingRemoteBeforeEveryCheckHasRun()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml"))) dir = dir.Parent;
            string script = File.ReadAllText(Path.Combine(dir!.FullName, "release.ps1"));

            int dryRunStop = script.IndexOf("DryRun: stopping before tag and release", StringComparison.Ordinal);
            Assert.True(dryRunStop > 0, "the DryRun stop marker moved");
            string beforeStop = script[..dryRunStop];
            Assert.DoesNotContain("git push", beforeStop, StringComparison.Ordinal);
            Assert.DoesNotContain("merge-upstream", beforeStop, StringComparison.Ordinal);
            Assert.DoesNotContain("gh release create", beforeStop, StringComparison.Ordinal);
            // The unit tests run from the local test.ps1, not from release.ps1 (eb386f6).
            foreach (string check in new[] { "git ls-remote --tags origin", "-PreflightOnly" })
                Assert.Contains(check, beforeStop, StringComparison.Ordinal);
        }

        [Fact]
        public void WinGetRunsOnlyAPinnedHashCheckedKomac()
        {
            string workflow = Workflow("winget-release.yml");

            Assert.DoesNotContain("releases/latest", workflow, StringComparison.Ordinal);
            Assert.Matches(@"KOMAC_VERSION: \d+\.\d+\.\d+", workflow);
            Assert.Matches(@"KOMAC_SHA256: [0-9A-F]{64}", workflow);
            int check = workflow.IndexOf("Get-FileHash", StringComparison.Ordinal);
            int path = workflow.IndexOf("GITHUB_PATH", StringComparison.Ordinal);
            Assert.True(check > 0 && check < path, "the hash must be checked before Komac is put on PATH");
        }
    }
}
