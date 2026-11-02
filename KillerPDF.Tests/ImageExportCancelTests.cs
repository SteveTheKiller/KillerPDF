using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerPdf.Engine.Authoring;
using KillerPDF.Controls;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>An export the user stops stops there, and what it says afterward is what happened.</summary>
public sealed class ImageExportCancelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kp-export-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string ThreePages()
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < 3; page++) builder.AddBlankPage(200, 200);
        string path = Path.Combine(_folder, "three.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private int Export(string source, CancellationToken cancellation, OperationHold? hold, Action<int, int> progress) =>
        PdfRasterize.ExportPageImages(source, [0, 1, 2], new int[3], 36, false, _folder, "page", 3,
            progress, cancellation, new IncompletePageLog(), null, null, hold);

    private int Images => Directory.GetFiles(_folder, "*.png").Length;

    [Fact]
    public void ACanceledExportSaysSoWithWhatItWrote()
    {
        using var cancel = new CancellationTokenSource();

        int written = Export(ThreePages(), cancel.Token, null, (done, _) => { if (done == 1) cancel.Cancel(); });

        Assert.Equal(1, written);
        Assert.Equal("Str_ExportImg_Canceled", PdfRasterize.ExportStatusKey(written, 3, cancel.IsCancellationRequested));
    }

    [Fact]
    public void AnExportCanceledBeforeItsFirstPageSaysCanceled()
    {
        Assert.Equal("Str_ExportImg_Canceled", PdfRasterize.ExportStatusKey(0, 568, canceled: true));
    }

    [Fact]
    public void AnExportThatFinishedAsTheCancelArrivedSaysItFinished()
    {
        Assert.Equal("Str_ExportImg_Done", PdfRasterize.ExportStatusKey(3, 3, canceled: true));
    }

    [Fact]
    public async Task AHeldExportWritesNothingMoreUntilItIsAnswered()
    {
        using var cancel = new CancellationTokenSource();
        using var hold = new OperationHold();
        using var firstPage = new ManualResetEventSlim();
        string source = ThreePages();

        Task<int> export = Task.Run(() => Export(source, cancel.Token, hold, (done, _) =>
        {
            if (done != 1) return;
            hold.Hold();
            firstPage.Set();
        }));
        Assert.True(firstPage.Wait(TimeSpan.FromSeconds(20)));
        await Task.Delay(500);

        Assert.Equal(1, Images);
        cancel.Cancel();
        Assert.Equal(1, await export.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, Images);
    }

    [Fact]
    public async Task AHoldThatIsLetGoFinishesTheExport()
    {
        using var hold = new OperationHold();
        string source = ThreePages();
        Task<int> export = Task.Run(() => Export(source, default, hold, (done, _) =>
        {
            if (done != 1) return;
            hold.Hold();
            ThreadPool.QueueUserWorkItem(_ => hold.Release());
        }));

        Assert.Equal(3, await export.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void TheProgressPanelCarriesACancelThatStopsTheJob() => OnSta(() =>
    {
        int canceled = 0;

        StackPanel panel = BusyProgressPanel.Build("Exporting page 0 of 3", "{0}/{1}", "Cancel", () => canceled++, null);

        Button? button = panel.Children.OfType<Button>().FirstOrDefault();
        Assert.NotNull(button);
        Assert.Equal("Cancel", button!.Content);
        button.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, canceled);
        Assert.False(button.IsEnabled);
    });

    [Fact]
    public void AJobWithNothingToStopShowsNoCancel() => OnSta(() =>
    {
        StackPanel panel = BusyProgressPanel.Build("Flattening", "{0}/{1}", "Cancel", null, null);

        Assert.Empty(panel.Children.OfType<Button>());
    });

    [Fact]
    public async Task TheCueReachesTheScreenBeforeTheWorkBlocksIt()
    {
        var order = new List<string>();

        string result = await BusyCue.ThenRun(() => order.Add("cue"),
            () => { order.Add("screen"); return Task.CompletedTask; },
            () => { order.Add("work"); return "done"; });

        Assert.Equal(["cue", "screen", "work"], order);
        Assert.Equal("done", result);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
