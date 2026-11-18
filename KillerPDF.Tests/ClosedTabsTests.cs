using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

[Collection("ClosedTabs")]
public sealed class ClosedTabsTests
{
    [Fact]
    public void TheNewestClosedTabComesBackFirstAndOnlyOnce()
    {
        ClosedTabs.Clear();
        string first = Path.GetTempFileName(), second = Path.GetTempFileName();
        try
        {
            ClosedTabs.Remember(first);
            ClosedTabs.Remember(second);

            Assert.Equal(second, ClosedTabs.Take());
            Assert.Equal(first, ClosedTabs.Take());
            Assert.Null(ClosedTabs.Take());
            Assert.False(ClosedTabs.CanReopen);
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public void AFileDeletedAfterItsTabClosedIsSkipped()
    {
        ClosedTabs.Clear();
        string kept = Path.GetTempFileName(), deleted = Path.GetTempFileName();
        try
        {
            ClosedTabs.Remember(kept);
            ClosedTabs.Remember(deleted);
            File.Delete(deleted);

            Assert.Equal(kept, ClosedTabs.Take());
        }
        finally { File.Delete(kept); }
    }

    [Fact]
    public void ClosingTheSameFileTwiceKeepsOneEntry()
    {
        ClosedTabs.Clear();
        string path = Path.GetTempFileName();
        try
        {
            ClosedTabs.Remember(path);
            ClosedTabs.Remember(path.ToUpperInvariant());

            Assert.NotNull(ClosedTabs.Take());
            Assert.Null(ClosedTabs.Take());
        }
        finally { File.Delete(path); }
    }
}
