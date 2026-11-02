using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfPrintSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_PrintsCurrentValuesBeforeAndAfterSaveWithoutChangingWorkingDocument(bool saveFirst)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-print-forms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] original = new PdfDocumentBuilder().AddBlankPage().AddBlankPage()
                .AddTextField(0, "customer.name", 20, 20, 140, 24, "Original")
                .AddComboBoxOptions(0, "customer.country", 20, 60, 140, 24,
                    [new PdfChoiceOption("US", "United States"), new PdfChoiceOption("CA", "Canada")], "US")
                .AddCheckBox(0, "customer.approved", 20, 100, 20, 20)
                .AddRadioGroup("customer.plan",
                    [new PdfRadioButtonOption(0, 20, 140, 20, 20, "Free"),
                     new PdfRadioButtonOption(1, 20, 20, 20, 20, "Pro")], "Free")
                .AddMultiSelectListBoxOptions(0, "customer.colors", 200, 20, 140, 70,
                    [new PdfChoiceOption("R", "Red"), new PdfChoiceOption("G", "Green"),
                     new PdfChoiceOption("B", "Blue")], ["G"])
                .Build();
            string sourcePath = Path.Combine(directory, "working.pdf");
            string savedPath = Path.Combine(directory, "saved.pdf");
            string printPath = Path.Combine(directory, "print.pdf");
            File.WriteAllBytes(sourcePath, original);
            using var working = PdfWorkingDocument.Open(sourcePath, null);
            var edits = new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string> { ["customer.name"] = "Updated" },
                new Dictionary<string, string> { ["customer.country"] = "CA" },
                new Dictionary<string, IReadOnlyList<string>> { ["customer.colors"] = ["R", "B"] },
                new Dictionary<string, bool> { ["customer.approved"] = true },
                new Dictionary<string, string> { ["customer.plan"] = "/Pro" },
                new Dictionary<string, double> { ["customer.name"] = 7.5 });
            if (saveFirst)
            {
                working.Save(savedPath);
                PdfEngineIntegration.ApplyFormValues(savedPath, edits);
            }

            PdfPrintSource.Create(sourcePath, printPath, PdfPrintSource.Snapshot(edits));

            PdfDocument printed = PdfDocument.Open(File.ReadAllBytes(printPath));
            var fields = Enumerable.Range(0, 2).SelectMany(page => PdfFormWidgetReader.ReadPage(printed, page))
                .GroupBy(widget => widget.FieldName).ToDictionary(group => group.Key, group => group.First());
            Assert.Equal("Updated", fields["customer.name"].Value);
            Assert.Equal("CA", fields["customer.country"].Value);
            Assert.Equal("Yes", fields["customer.approved"].Value.TrimStart('/'));
            Assert.Equal("Pro", fields["customer.plan"].Value.TrimStart('/'));
            Assert.Equal(["R", "B"], fields["customer.colors"].Values);
            Assert.Contains("7.5 Tf", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(printPath)));
            Assert.Equal(original, File.ReadAllBytes(sourcePath));
            using var serialized = new MemoryStream();
            working.Save(serialized);
            Assert.Equal(original, serialized.ToArray());
            Assert.Equal("Updated", edits.TextValues["customer.name"]);
            Assert.True(edits.CheckBoxValues["customer.approved"]);
            if (saveFirst)
                Assert.Equal("Updated", PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(savedPath)), 0)
                    .Single(widget => widget.FieldName == "customer.name").Value);
            else
                Assert.False(File.Exists(savedPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Snapshot_IsolatesEveryPendingDictionaryAndMultiChoiceList()
    {
        var text = new Dictionary<string, string> { ["text"] = "Before" };
        var choice = new Dictionary<string, string> { ["choice"] = "A" };
        var selected = new List<string> { "A", "B" };
        var multi = new Dictionary<string, IReadOnlyList<string>> { ["multi"] = selected };
        var check = new Dictionary<string, bool> { ["check"] = true };
        var radio = new Dictionary<string, string> { ["radio"] = "A" };
        var sizes = new Dictionary<string, double> { ["text"] = 7.5 };
        var snapshot = PdfPrintSource.Snapshot(new(text, choice, multi, check, radio, sizes));

        text.Clear(); choice.Clear(); multi.Clear(); selected.Clear(); check.Clear(); radio.Clear(); sizes.Clear();

        Assert.True(PdfPrintSource.HasValues(snapshot));
        Assert.Equal("Before", snapshot.TextValues["text"]);
        Assert.Equal("A", snapshot.ChoiceValues["choice"]);
        Assert.Equal(["A", "B"], snapshot.MultiChoiceValues["multi"]);
        Assert.True(snapshot.CheckBoxValues["check"]);
        Assert.Equal("A", snapshot.RadioValues["radio"]);
        Assert.Equal(7.5, snapshot.TextFontSizes["text"]);
    }

    [Fact]
    public void Create_RejectsSourceAsDestinationWithoutChangingBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-print-source-{Guid.NewGuid():N}.pdf");
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        File.WriteAllBytes(path, original);
        try
        {
            var empty = new PdfEngineIntegration.FormEdits(new Dictionary<string, string>(),
                new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, bool>(), new Dictionary<string, string>(), new Dictionary<string, double>());
            Assert.False(PdfPrintSource.HasValues(empty));
            Assert.Throws<ArgumentException>(() => PdfPrintSource.Create(path, path, empty));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
