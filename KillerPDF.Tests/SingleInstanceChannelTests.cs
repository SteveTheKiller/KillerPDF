using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    // BF-A008: one connected client must not be able to monopolize forwarding.
    public class SingleInstanceChannelTests
    {
        [Fact]
        public void ReadsOneLine()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("C:\\Docs\\a.pdf\r\nignored"));
            Assert.Equal("C:\\Docs\\a.pdf", SingleInstanceChannel.ReadMessage(stream, 1024, TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public void RejectsOversizedAndUnterminatedMessages()
        {
            using var big = new MemoryStream(new byte[5000]);
            Assert.Null(SingleInstanceChannel.ReadMessage(big, 4096, TimeSpan.FromSeconds(2)));
            using var partial = new MemoryStream(Encoding.UTF8.GetBytes("no newline"));
            Assert.Null(SingleInstanceChannel.ReadMessage(partial, 4096, TimeSpan.FromSeconds(2)));
        }

        // .NET's CurrentUserOnly compares the pipe owner with the token's Owner, which is Administrators in an
        // elevated token, while the server always sets the user's own SID as owner. Elevated, every forward
        // failed: a new window per file, and Images to PDF made one PDF per image. The sender checks the
        // owner against the user SID itself.
        [Fact]
        public void SenderAcceptsOnlyAPipeOwnedByTheUser()
        {
            var user = WindowsIdentity.GetCurrent().User!;
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            Assert.True(SingleInstanceChannel.IsOwnedBy(user, user));
            Assert.False(SingleInstanceChannel.IsOwnedBy(admins, user));
            Assert.False(SingleInstanceChannel.IsOwnedBy(null, user));
            Assert.False(SingleInstanceChannel.IsOwnedBy(user, null));
        }

        [Fact]
        public void SenderDoesNotUseTheTokenOwnerCheck()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
            string source = File.ReadAllText(Path.Combine(dir!.FullName, "Services", "SingleInstanceChannel.cs"));
            Assert.DoesNotContain("PipeOptions.CurrentUserOnly", source);
            Assert.Contains("IsOwnedBy(", source);
        }

        [Fact]
        public async Task SilentClientIsDroppedAndTheNextOneIsServed()
        {
            var channel = new SingleInstanceChannel("KillerPDF.Test." + Guid.NewGuid().ToString("N"),
                WindowsIdentity.GetCurrent().User!.Value, 0);
            string? second = null;
            // The first pipe exists before the client connects, so a slow start of the server
            // thread on a busy machine cannot time the client out.
            var first = channel.CreateServer();
            // A dedicated thread, like the app's KillerPDF-IPC thread: under a full parallel test
            // run a thread-pool task could wait past the sender's whole retry window to start.
            var server = Task.Factory.StartNew(() =>
            {
                using (first)
                {
                    first.WaitForConnection();
                    // The silent client must be dropped at its short deadline.
                    SingleInstanceChannel.ReadMessage(first, 1024, TimeSpan.FromMilliseconds(300));
                }
                // Like App.RunPipeServer: the next instance can fail while the dropped one is
                // still closing (the pipe allows one instance), so back off and try again rather
                // than stop serving. The real sender writes at once, but a busy test run can
                // schedule this thread late, so its read gets a deadline a delay cannot reach.
                for (int attempt = 0; attempt < 30 && second is null; attempt++)
                {
                    try
                    {
                        using var pipe = channel.CreateServer();
                        pipe.WaitForConnection();
                        second = SingleInstanceChannel.ReadMessage(pipe, 1024, TimeSpan.FromSeconds(5));
                    }
                    catch (IOException) { Thread.Sleep(150); }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            using (var silent = new NamedPipeClientStream(".", channel.PipeName, PipeDirection.Out))
            {
                silent.Connect(3000);
                Thread.Sleep(600);   // say nothing past the deadline
            }
            Assert.True(SpinWait.SpinUntil(() => channel.TrySend("C:\\next.pdf", 500), TimeSpan.FromSeconds(60)));
            await server.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal("C:\\next.pdf", second);
        }
    }
}
