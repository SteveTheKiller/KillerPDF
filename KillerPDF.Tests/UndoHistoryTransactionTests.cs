using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class UndoHistoryTransactionTests
{
    [Fact]
    public void RefusedEntryAndEmptyHistoryDoNotConsumeOrApplyAnything()
    {
        var history = new Stack<string>();
        Assert.False(UndoHistoryBudget.TryApply(history, _ => throw new InvalidOperationException()));
        history.Push("retryable");
        Assert.False(UndoHistoryBudget.TryApply(history, _ => false));
        Assert.Equal("retryable", Assert.Single(history));
    }

    [Fact]
    public void FailedSnapshotWriteLeavesTheEntryAvailableForRetry()
    {
        var history = new Stack<string>();
        history.Push("older edit");
        history.Push("pending edit");
        Assert.Throws<IOException>(() => UndoHistoryBudget.TryApply(history,
            _ => throw new IOException("Snapshot disk unavailable")));
        Assert.Equal(new[] { "pending edit", "older edit" }, history);
        string? restored = null;
        Assert.True(UndoHistoryBudget.TryApply(history, entry => { restored = entry; return true; }));
        Assert.Equal("pending edit", restored);
        Assert.Equal("older edit", Assert.Single(history));
    }
}
