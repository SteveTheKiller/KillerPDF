using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class ProcessTempFilesTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StaleImportCleanupHonorsRecoveryRetentionAndPreservesUnknownDirectories(bool retained)
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-stale-import-" + Guid.NewGuid().ToString("N"));
            string session = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
            string import = Path.Combine(session, "import-" + Guid.NewGuid().ToString("N"));
            string unknown = Path.Combine(session, "import-user-data");
            Directory.CreateDirectory(import);
            Directory.CreateDirectory(unknown);
            File.WriteAllText(Path.Combine(session, "owner.lease"), "old process");
            File.WriteAllText(Path.Combine(import, "image.png"), "import bytes");
            File.WriteAllText(Path.Combine(unknown, "keep.txt"), "unrelated");
            if (retained) File.WriteAllText(Path.Combine(session, "recovery-retained.json"), "{}");
            try
            {
                using var nextLaunch = new ProcessTempFiles(root);
                nextLaunch.CleanupStale();
                Assert.Equal(retained, Directory.Exists(import));
                Assert.Equal("unrelated", File.ReadAllText(Path.Combine(unknown, "keep.txt")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void RegisteredImportDirectoryIsRemovedBySessionCleanup()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-import-life-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var temps = new ProcessTempFiles(root);
                string directory = temps.CreateImportDirectoryPath();
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "image.png"), "import bytes");
                Assert.True(temps.Owns(directory));
                temps.CleanupOwned();
                Assert.False(Directory.Exists(directory));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void RecoveryRetentionProtectsWorkingBytesFromBothCleanupPaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retained-" + Path.GetRandomFileName());
            var temps = new ProcessTempFiles(root);
            try
            {
                string working = temps.CreatePath("working");
                File.WriteAllText(working, "available unsaved working bytes");
                File.WriteAllText(Path.Combine(temps.DirectoryPath, "recovery-retained.json"),
                    "{\"Reason\":\"A recovery snapshot failed.\"}");

                temps.CleanupOwned();
                Assert.True(File.Exists(working), "Current-process cleanup removed retained recovery bytes.");
                temps.Dispose();
                using var nextLaunch = new ProcessTempFiles(root);
                nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "Next-launch cleanup removed retained recovery bytes.");
                Assert.Equal("available unsaved working bytes", File.ReadAllText(working));
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A retained session used to be kept forever; it now expires after thirty days.
        [Fact]
        public void RecoveryRetentionExpiresAfterThirtyDays()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retention-expiry-" + Path.GetRandomFileName());
            var temps = new ProcessTempFiles(root);
            try
            {
                string working = temps.CreatePath("working");
                File.WriteAllText(working, "unsaved working bytes");
                string directory = temps.DirectoryPath;
                temps.RetainForRecovery(null);
                temps.Dispose();

                using (var nextLaunch = new ProcessTempFiles(root)) nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "A fresh retention was cleaned up.");

                File.SetLastWriteTimeUtc(Path.Combine(directory, "recovery-retained.json"),
                    DateTime.UtcNow.AddDays(-31));
                using (var monthLater = new ProcessTempFiles(root)) monthLater.CleanupStale();
                Assert.False(Directory.Exists(directory), "An expired retention kept its session forever.");
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A full disk can refuse the retention marker as well as the recovery copies. The restarted
        // process is then told which session it replaces, keeps that folder and marks it for later
        // launches, instead of deleting the working files its recovery message points at.
        [Fact]
        public void NamedRetainedSessionSurvivesWithoutItsMarker()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retained-named-" + Path.GetRandomFileName());
            var temps = new ProcessTempFiles(root);
            try
            {
                string working = temps.CreatePath("working");
                File.WriteAllText(working, "unsaved working bytes");
                string directory = temps.DirectoryPath;
                temps.RetainForRecovery(null);
                File.Delete(Path.Combine(directory, "recovery-retained.json"));
                Assert.Equal(directory, temps.RetainedDirectory);
                temps.Dispose();

                using (var restarted = new ProcessTempFiles(root)) restarted.CleanupStale(directory);
                Assert.True(File.Exists(working), "The restart deleted the session it was told to keep.");
                using (var nextLaunch = new ProcessTempFiles(root)) nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "The kept session was not marked for later launches.");
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // On a full disk the marker write fails in the crashed process and again in the restart a
        // moment later. Renaming the session's lease needs no new space, so the lease becomes the
        // marker and every later launch keeps the session until the retention expires.
        [Fact]
        public void RetentionSurvivesMarkerWritesTheDiskRefuses()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retained-full-" + Path.GetRandomFileName());
            static void Full(string path, byte[] bytes) => throw new IOException("There is not enough space on the disk.");
            var crashed = new ProcessTempFiles(root, Full);
            try
            {
                string working = crashed.CreatePath("working");
                File.WriteAllText(working, "unsaved working bytes");
                string directory = crashed.DirectoryPath;
                crashed.RetainForRecovery(null);
                crashed.RetainForRecovery("recovery.json");
                crashed.Dispose();

                using (var restarted = new ProcessTempFiles(root, Full)) restarted.CleanupStale(directory);
                using (var nextLaunch = new ProcessTempFiles(root, Full)) nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "A later launch deleted a session the disk had no room to mark.");

                File.SetLastWriteTimeUtc(Path.Combine(directory, "recovery-retained.json"), DateTime.UtcNow.AddDays(-31));
                using (var monthLater = new ProcessTempFiles(root)) monthLater.CleanupStale();
                Assert.False(Directory.Exists(directory), "An expired retention without a lease was kept forever.");
            }
            finally
            {
                crashed.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // When the crashed process could not mark its session at all, the restart turns the now
        // free lease into the marker itself.
        [Fact]
        public void RestartMarksTheNamedSessionWhenItsOwnWriteFails()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retained-restart-" + Path.GetRandomFileName());
            static void Full(string path, byte[] bytes) => throw new IOException("There is not enough space on the disk.");
            var crashed = new ProcessTempFiles(root);
            try
            {
                string working = crashed.CreatePath("working");
                File.WriteAllText(working, "unsaved working bytes");
                string directory = crashed.DirectoryPath;
                crashed.RetainForRecovery(null);
                File.Delete(Path.Combine(directory, "recovery-retained.json"));
                crashed.Dispose();

                using (var restarted = new ProcessTempFiles(root, Full)) restarted.CleanupStale(directory);
                using (var nextLaunch = new ProcessTempFiles(root, Full)) nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "A later launch deleted the session the restart was told to keep.");
            }
            finally
            {
                crashed.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // The retained session comes from the restart's command line, which anyone can type. A
        // blank or malformed one is ignored instead of failing startup.
        [Theory]
        [InlineData("   ")]
        [InlineData("C:\\bad\0path")]
        public void MalformedRetainedSessionIsIgnored(string retained)
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retained-bad-" + Path.GetRandomFileName());
            var stale = new ProcessTempFiles(root);
            try
            {
                File.WriteAllText(stale.CreatePath("working"), "stale bytes");
                string directory = stale.DirectoryPath;
                stale.Dispose();

                using (var next = new ProcessTempFiles(root)) next.CleanupStale(retained);
                Assert.False(Directory.Exists(directory));
            }
            finally
            {
                stale.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void UnretainedSessionNamesNoDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-unretained-" + Path.GetRandomFileName());
            using var temps = new ProcessTempFiles(root);
            try
            {
                _ = temps.CreatePath("working");
                Assert.Null(temps.RetainedDirectory);
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void OwnsOnlyPathsUnderItsRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-temps-" + Path.GetRandomFileName());
            using var temps = new ProcessTempFiles(root);
            try
            {
                string created = temps.CreatePath("browser");

                Assert.True(temps.Owns(created));
                Assert.False(temps.Owns(Path.Combine(Path.GetTempPath(), "invoice.pdf")));
                Assert.False(temps.Owns(root + "-sibling" + Path.DirectorySeparatorChar + "x.pdf"));
                Assert.False(temps.Owns("invoice.pdf"));
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
