using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class SignatureStoreTests
    {
        [Fact]
        public void NewStore_HasEmptySignatures()
        {
            var store = new SignatureStore();
            Assert.Empty(store.Signatures);
        }

        [Fact]
        public void Add_IncreasesCount()
        {
            var store = new SignatureStore();
            store.Add(new SavedSignature { Name = "Test" });
            Assert.Single(store.Signatures);
        }

        [Fact]
        public void Remove_DecreasesCount()
        {
            var store = new SignatureStore();
            var sig = new SavedSignature { Name = "Test" };
            store.Add(sig);
            store.Remove(sig);
            Assert.Empty(store.Signatures);
        }

        [Fact]
        public void RoundTrip_PersistAndLoad()
        {
            var dir  = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, "sig_test.json");

            try
            {
                var store1 = new SignatureStore(dir, file);
                store1.Add(new SavedSignature { Name = "Alpha", CanvasWidth = 400, CanvasHeight = 150,
                    Strokes = [[new() { X = 1, Y = 2 }, new() { X = 3, Y = 4 }]] });
                store1.Add(new SavedSignature { Name = "Beta", CanvasWidth = 300, CanvasHeight = 100,
                    Strokes = [[new() { X = 2, Y = 3 }, new() { X = 4, Y = 5 }]] });
                store1.Persist();

                var store2 = new SignatureStore(dir, file);
                store2.Load();

                Assert.Equal(2, store2.Signatures.Count);
                Assert.Equal("Alpha", store2.Signatures[0].Name);
                Assert.Equal("Beta",  store2.Signatures[1].Name);
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void DamagedLibraryIsKeptAsideBeforeItCanBeOverwritten()
        {
            var dir  = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, "sig_test.json");
            try
            {
                System.IO.File.WriteAllText(file, "[{\"Name\": \"Alpha\"");   // truncated JSON
                var store = new SignatureStore(dir, file);
                store.Load();

                Assert.Empty(store.Signatures);
                Assert.NotNull(store.PreservedDamagedFile);
                Assert.Equal("[{\"Name\": \"Alpha\"", System.IO.File.ReadAllText(store.PreservedDamagedFile!));

                store.Add(new SavedSignature { Name = "Beta" });
                Assert.True(store.Persist());
                Assert.Equal("[{\"Name\": \"Alpha\"", System.IO.File.ReadAllText(store.PreservedDamagedFile!));
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void PersistReportsFailureAndKeepsThePreviousVersion()
        {
            var dir  = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, "sig_test.json");
            try
            {
                var store = new SignatureStore(dir, file);
                store.Add(new SavedSignature { Name = "Alpha" });
                Assert.True(store.Persist());
                store.Add(new SavedSignature { Name = "Beta" });
                Assert.True(store.Persist());
                Assert.Contains("Alpha", System.IO.File.ReadAllText(file + ".bak"));
                Assert.DoesNotContain("Beta", System.IO.File.ReadAllText(file + ".bak"));

                // The library file is held open exclusively, so the replace cannot happen.
                using (new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                {
                    store.Add(new SavedSignature { Name = "Gamma" });
                    Assert.False(store.Persist());
                }
                Assert.Equal(3, store.Signatures.Count);   // nothing lost in memory
                Assert.DoesNotContain("Gamma", System.IO.File.ReadAllText(file));
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }
}
