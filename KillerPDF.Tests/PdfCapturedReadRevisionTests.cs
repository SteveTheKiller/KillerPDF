using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Security;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfCapturedReadRevisionTests
{
    [Fact]
    public void OpenWorkingOwnerRecapturesItsProtectedContentAfterExplicitRevisionDisposal()
    {
        string path = Path.Combine(Path.GetTempPath(), $"captured-recapture-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "reader", OwnerPassword = "owner" })
                .AddPage(2, 1, new PdfContentStreamBuilder().SetFillGray(.5001).Rectangle(0, 0, 2, 1).Fill()).Build());
            OpenedProtection protection = OpenedProtection.Capture(path, "reader");
            using PdfWorkingDocument working = PdfWorkingDocument.Open(path, protection);
            using var first = working.CaptureReadRevision();
            using var oldLease = first.Rent();
            first.Dispose();
            File.Delete(path);
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(9, 3).Build());

            using var second = working.CaptureReadRevision();
            Assert.NotSame(first, second);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(first.ContentIdentity, second.ContentIdentity);
            Assert.Equal(working.ContentIdentity, second.ContentIdentity);
            Assert.ThrowsAny<OperationCanceledException>(() => oldLease.RenderFrame(0, new PdfRenderOptions(2, 1)));
            Assert.Throws<ObjectDisposedException>(() => first.Rent());
            Assert.Equal(2, second.GetPageGeometry(0).Width);
            Assert.Equal(1, second.GetPageGeometry(0).Height);
            using (var lease = second.Rent())
            {
                PdfRenderedFrame frame = lease.RenderFrame(0, new PdfRenderOptions(2, 1));
                Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
                for (int pixel = 0; pixel < 2; pixel++)
                {
                    for (int channel = 0; channel < 3; channel++)
                        Assert.InRange(frame.Pixels[pixel * 4 + channel], .21413370f, .21413373f);
                    Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
                }
            }
            Assert.Same(second, working.CaptureReadRevision());
            working.Close();
            Assert.Throws<ObjectDisposedException>(() => working.CaptureReadRevision());
            Assert.Throws<ObjectDisposedException>(() => second.Rent());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OwnerCloseCancelsAnotherTasksAcquiredLeaseAndLetsThatTaskReleaseIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"captured-concurrent-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(2, 1).Build());
            using PdfWorkingDocument working = PdfWorkingDocument.Open(path, null);
            using var revision = working.CaptureReadRevision();
            var rented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task renderTask = Task.Run(async () =>
            {
                try
                {
                    using var lease = revision.Rent();
                    Assert.Equal(1f, lease.RenderFrame(0, new PdfRenderOptions(2, 1)).Pixels[0]);
                    rented.SetResult();
                    await closed.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    Assert.ThrowsAny<OperationCanceledException>(() => lease.RenderFrame(0, new PdfRenderOptions(2, 1)));
                }
                catch (Exception error)
                {
                    rented.TrySetException(error);
                    throw;
                }
            });
            try
            {
                await rented.Task.WaitAsync(TimeSpan.FromSeconds(60));
                working.Close();
            }
            finally
            {
                closed.TrySetResult();
                await renderTask.WaitAsync(TimeSpan.FromSeconds(60));
            }
            Assert.Throws<ObjectDisposedException>(() => revision.Rent());
            Assert.Throws<ObjectDisposedException>(() => working.CaptureReadRevision());
            revision.Dispose();
            working.Close();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturningRetiredLeaseCannotReplaceTheNewRevisionSession()
    {
        string path = Path.Combine(Path.GetTempPath(), $"captured-return-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(2, 1,
                new PdfContentStreamBuilder().SetFillGray(0).Rectangle(0, 0, 2, 1).Fill()).Build());
            using PdfWorkingDocument oldWorking = PdfWorkingDocument.Open(path, null);
            using var oldRevision = oldWorking.CaptureReadRevision();
            using var oldLease = oldRevision.Rent();
            Assert.Equal(0f, oldLease.RenderFrame(0, new PdfRenderOptions(2, 1)).Pixels[0]);
            oldWorking.Close();

            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(9, 3).Build());
            using PdfWorkingDocument newWorking = PdfWorkingDocument.Open(path, null);
            using var newRevision = newWorking.CaptureReadRevision();
            Assert.NotEqual(oldRevision.Id, newRevision.Id);
            Assert.NotEqual(oldRevision.ContentIdentity, newRevision.ContentIdentity);
            using (var newLease = newRevision.Rent())
            {
                oldLease.Dispose();
                oldLease.Dispose();
                Assert.Equal(1f, newLease.RenderFrame(0, new PdfRenderOptions(9, 3)).Pixels[0]);
            }
            using var next = newRevision.Rent();
            Assert.Equal(9, newRevision.GetPageGeometry(0).Width);
            Assert.Equal(3, newRevision.GetPageGeometry(0).Height);
            Assert.Equal(1f, next.RenderFrame(0, new PdfRenderOptions(9, 3)).Pixels[0]);
            Assert.Throws<ObjectDisposedException>(() => oldRevision.Rent());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CaptureAfterLegacyRentalReadsOwnedProtectedBytesEvenAfterSourceDeletion()
    {
        string path = Path.Combine(Path.GetTempPath(), $"captured-owned-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "reader", OwnerPassword = "owner" })
                .AddPage(2, 1, new PdfContentStreamBuilder().SetFillGray(.5001).Rectangle(0, 0, 2, 1).Fill()).Build());
            OpenedProtection protection = OpenedProtection.Capture(path, "reader");
            using PdfWorkingDocument working = PdfWorkingDocument.Open(path, protection);
            using var legacy = PdfPageRenderSession.OpenEngineFirst(path, 2, 1, protection);
            File.Delete(path);
            using var revision = working.CaptureReadRevision();
            using var lease = revision.Rent();
            Assert.Equal(working.ContentIdentity, revision.ContentIdentity);
            Assert.Equal(2, revision.GetPageGeometry(0).Width);
            Assert.InRange(lease.RenderFrame(0, new PdfRenderOptions(2, 1)).Pixels[0], .21413370f, .21413373f);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CallerCancellationStopsItsLeaseAndDoesNotRetireTheRevision()
    {
        string path = Path.Combine(Path.GetTempPath(), $"captured-cancel-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(2, 1).Build());
            using PdfWorkingDocument working = PdfWorkingDocument.Open(path, null);
            using var revision = working.CaptureReadRevision();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => revision.Rent(canceled.Token));
            using (var caller = new CancellationTokenSource())
            using (var lease = revision.Rent(caller.Token))
            {
                caller.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => lease.RenderFrame(0, new PdfRenderOptions(2, 1)));
            }
            using var next = revision.Rent();
            Assert.Equal(1f, next.RenderFrame(0, new PdfRenderOptions(2, 1)).Pixels[0]);
            working.Close();
            Assert.ThrowsAny<OperationCanceledException>(() => next.RenderFrame(0, new PdfRenderOptions(2, 1)));
            Assert.Throws<ObjectDisposedException>(() => revision.Rent());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProtectedCapturedRevisionReadsOriginalPrecisionAfterPathReplacementAndRetiresWithOwner()
    {
        string path = Path.Combine(Path.GetTempPath(), $"captured-read-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "reader", OwnerPassword = "owner" })
                .AddPage(2, 1, new PdfContentStreamBuilder().SetFillGray(.5001).Rectangle(0, 0, 2, 1).Fill()).Build());
            OpenedProtection protection = OpenedProtection.Capture(path, "reader");
            using PdfWorkingDocument working = PdfWorkingDocument.Open(path, protection);
            using PdfCapturedReadRevision revision = working.CaptureReadRevision();
            Assert.NotEqual(Guid.Empty, revision.Id);
            Assert.Equal(working.ContentIdentity, revision.ContentIdentity);
            Assert.Same(revision, working.CaptureReadRevision());
            Assert.Equal(1, revision.PageCount);
            Assert.Equal(2, revision.GetPageGeometry(0).Width);
            Assert.Equal(1, revision.GetPageGeometry(0).Height);
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(9, 3).Build());
            using (var lease = revision.Rent())
            {
                PdfRenderedFrame frame = lease.RenderFrame(0, new PdfRenderOptions(2, 1));
                Assert.Equal(2, frame.Width);
                Assert.Equal(1, frame.Height);
                Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
                for (int pixel = 0; pixel < 2; pixel++)
                {
                    for (int channel = 0; channel < 3; channel++)
                        Assert.InRange(frame.Pixels[pixel * 4 + channel], .21413370f, .21413373f);
                    Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
                }
            }
            using var active = revision.Rent();
            working.Close();
            Assert.ThrowsAny<OperationCanceledException>(() => active.RenderFrame(0, new PdfRenderOptions(2, 1)));
            Assert.Throws<ObjectDisposedException>(() => revision.Rent());
            Assert.Throws<ObjectDisposedException>(() => working.CaptureReadRevision());
            active.Dispose();
            active.Dispose();
            revision.Dispose();
            revision.Dispose();
            working.Close();
            File.Delete(path);
        }
        finally { File.Delete(path); }
    }
}
