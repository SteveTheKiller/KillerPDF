using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A second Enter in the file dialog's name box while a slow folder probe is still out must do nothing, the way a
// second Recents click already does; otherwise the two presses navigate twice or race Accept.
public sealed class SingleFlightTests
{
    [Fact]
    public async Task ASecondCallWhileTheFirstIsOutIsSkipped()
    {
        var flight = new SingleFlight();
        var release = new TaskCompletionSource<bool>();
        int runs = 0;

        Task<bool> first = flight.RunAsync(async () => { runs++; await release.Task; });
        bool second = await flight.RunAsync(() => { runs++; return Task.CompletedTask; });

        Assert.False(second);
        release.SetResult(true);
        Assert.True(await first);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task TheNextCallRunsOnceTheFirstHasFinishedOrFailed()
    {
        var flight = new SingleFlight();
        await Assert.ThrowsAsync<InvalidOperationException>(() => flight.RunAsync(() => throw new InvalidOperationException()));
        int runs = 0;

        Assert.True(await flight.RunAsync(() => { runs++; return Task.CompletedTask; }));
        Assert.Equal(1, runs);
    }
}
