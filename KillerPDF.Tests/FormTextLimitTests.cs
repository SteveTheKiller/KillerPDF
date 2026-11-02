using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class FormTextLimitTests
    {
        [Theory]
        [InlineData("abc", "", 3, true)]       // at the limit
        [InlineData("abcd", "abc", 3, false)]  // above it
        [InlineData("ab😀", "ab", 3, true)]    // a supplementary character counts once
        [InlineData("ab😀😀", "ab😀", 3, false)]
        [InlineData("abcde", "abcdef", 3, true)] // an overlong value may shrink
        [InlineData("abcdefg", "abcdef", 3, false)]
        [InlineData("anything", "", 0, true)]  // no limit
        public void AcceptsWhatTheEngineWillSave(string next, string previous, int maximum, bool accepted)
        {
            Assert.Equal(accepted, FormTextLimit.Accepts(next, previous, maximum));
        }

        [Fact]
        public void CountsRunesNotUtf16Units()
        {
            Assert.Equal(2, FormTextLimit.Count("a😀"));
            Assert.True(FormTextLimit.IsOver("abcd", 3));
            Assert.False(FormTextLimit.IsOver("ab😀", 3));
        }

        // R4-008: the limits are read once per document and stay with it, so going back and forth
        // between two form tabs reads neither again.
        [Fact]
        public void EachDocumentsLimitsAreReadOnceAcrossTabSwitches()
        {
            var cache = new FormTextLimitCache();
            object first = new(), second = new();
            int reads = 0;
            IReadOnlyDictionary<string, int> Read() { reads++; return new Dictionary<string, int> { ["name"] = reads }; }

            int firstName = cache.Read(first, Read).Result["name"];
            foreach (object document in new[] { second, first, second, first })
                cache.Read(document, Read).Wait();

            Assert.Equal(2, reads);
            Assert.Equal(firstName, cache.Read(first, Read).Result["name"]);
            Assert.NotEqual(firstName, cache.Read(second, Read).Result["name"]);
        }

        // Measured on a 1,000-page file with 20 links a page, the read takes about a quarter of a
        // second, so it runs on a worker, and a field holds to its own limit until it is known.
        [Fact]
        public void BuildingAFieldDoesNotWaitForTheRead()
        {
            var cache = new FormTextLimitCache();
            object document = new();
            using var hold = new ManualResetEventSlim();
            IReadOnlyDictionary<string, int> Read() { hold.Wait(); return new Dictionary<string, int> { ["name"] = 5 }; }

            Assert.Null(cache.Known(document));
            Task<IReadOnlyDictionary<string, int>> reading = cache.Read(document, Read);
            Assert.Null(cache.Known(document));
            hold.Set();
            reading.Wait();

            Assert.Equal(5, cache.Known(document)!["name"]);
        }

        // A page edit opens a new working document, whose fields may differ, so it is read afresh.
        [Fact]
        public void ANewWorkingDocumentIsReadAgain()
        {
            var cache = new FormTextLimitCache();
            int reads = 0;
            IReadOnlyDictionary<string, int> Read() { reads++; return new Dictionary<string, int>(); }

            cache.Read(new object(), Read).Wait();
            cache.Read(new object(), Read).Wait();

            Assert.Equal(2, reads);
        }
    }
}
