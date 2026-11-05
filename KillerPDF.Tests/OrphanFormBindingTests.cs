using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OrphanFormBindingTests
{
    [Fact]
    public void IndependentUnlistedWidgetsHaveStableKeysAndSaveIndependently()
    {
        byte[] source = Fixture();
        string path = Path.Combine(Path.GetTempPath(), $"orphan-form-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            var widgets = PdfEngineIntegration.ReadPageFormWidgets(PdfDocument.Open(source), 0);
            Assert.Equal(4, widgets.Select(w => w.FieldName).Distinct().Count());
            Assert.Equal(widgets.Select(w => w.FieldName),
                PdfEngineIntegration.ReadAllPageFormWidgets(path, null)[0].Select(w => w.FieldName));
            Assert.Equal(source, File.ReadAllBytes(path));
            var edits = new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string> { [widgets[0].FieldName] = "Only first" },
                new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<string>>(),
                new Dictionary<string, bool> { [widgets[2].FieldName] = true },
                new Dictionary<string, string>(), new Dictionary<string, double>());
            PdfEngineIntegration.ApplyFormValues(path, edits);
            byte[] saved = File.ReadAllBytes(path);
            Assert.True(saved.AsSpan(0, source.Length).SequenceEqual(source));
            var reopened = PdfEngineIntegration.ReadPageFormWidgets(PdfDocument.Open(saved), 0);
            Assert.Equal(widgets.Select(w => w.FieldName), reopened.Select(w => w.FieldName));
            Assert.Equal("Only first", reopened[0].Value);
            Assert.Empty(reopened[1].Value);
            Assert.Equal("Yes", reopened[2].Value.TrimStart('/'));
            Assert.True(reopened[3].Value is "" or "Off" or "/Off");
            var document = PdfDocument.Open(saved);
            var root = (PdfDictionary)document.Resolve(1);
            PdfObject formValue = root[new PdfName("AcroForm"u8)];
            var form = (PdfDictionary)(formValue is PdfIndirectReference r ? document.Resolve(r) : formValue);
            Assert.Equal(5, ((PdfArray)form[new PdfName("Fields"u8)]).Count);
            Assert.True(form.ContainsKey(new PdfName("DR"u8)));
            Assert.True(form.ContainsKey(new PdfName("DA"u8)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SharedRegisteredParentRetainsItsNameAndSynchronizedValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shared-form-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Fixture(shared: true));
            var before = PdfEngineIntegration.ReadAllPageFormWidgets(path, null)[0];
            Assert.Equal("Shared", before[0].FieldName);
            Assert.Equal(before[0].FieldName, before[1].FieldName);
            PdfEngineIntegration.ApplyFormValues(path, Edits("Shared", "Together"));
            var after = PdfEngineIntegration.ReadAllPageFormWidgets(path, null)[0];
            Assert.Equal("Together", after[0].Value);
            Assert.Equal("Together", after[1].Value);
            Assert.Equal("Shared", after[0].FieldName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GeneratedKeyAvoidsRegisteredNamesAndRemovalTargetsOnlyOneOrphan()
    {
        string path = Path.Combine(Path.GetTempPath(), $"collision-form-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Fixture(collision: true));
            var widgets = PdfEngineIntegration.ReadAllPageFormWidgets(path, null)[0];
            Assert.NotEqual("KillerPDF_orphan_5_0", widgets[0].FieldName);
            PdfEngineIntegration.RemoveFormField(path, widgets[0].FieldName);
            var remaining = PdfEngineIntegration.ReadAllPageFormWidgets(path, null)[0];
            Assert.Equal(3, remaining.Count);
            Assert.DoesNotContain(remaining, w => w.ObjectNumber == 5);
            Assert.Contains(remaining, w => w.ObjectNumber == 6);
            var saved = PdfDocument.Open(File.ReadAllBytes(path));
            var registered = (PdfDictionary)saved.Resolve(4);
            Assert.Equal("Keep me", Encoding.ASCII.GetString(((PdfString)registered[new PdfName("V"u8)]).Bytes.Span));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnregisteredParentedWidgetsRemainReadableButSaveIsRefused()
    {
        byte[] source = Fixture(shared: true, unregistered: true);
        string path = Path.Combine(Path.GetTempPath(), $"parented-form-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            var readable = PdfEngineIntegration.ReadAllPageFormWidgets(path, null)[0];
            Assert.Equal(4, readable.Count);
            Assert.Equal("Shared", readable[0].FieldName);
            Assert.Equal("Shared", readable[1].FieldName);
            Assert.Equal(readable.Select(w => w.FieldName),
                PdfEngineIntegration.ReadPageFormWidgets(PdfDocument.Open(source), 0).Select(w => w.FieldName));
            Assert.Throws<NotSupportedException>(() => PdfEngineIntegration.ApplyFormValues(path, Edits("Shared", "Refused")));
            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MalformedRegistryPreservesReadableWidgetNamesButRefusesSave(int registry)
    {
        byte[] source = Fixture(registry: registry);
        string path = Path.Combine(Path.GetTempPath(), $"invalid-registry-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            var readable = PdfEngineIntegration.ReadAllPageFormWidgets(path, null)[0];
            Assert.Equal(4, readable.Count);
            Assert.All(readable, widget => Assert.Equal("0", widget.FieldName));
            Assert.Equal(readable.Select(w => w.FieldName),
                PdfEngineIntegration.ReadPageFormWidgets(PdfDocument.Open(source), 0).Select(w => w.FieldName));
            Assert.Throws<NotSupportedException>(() => PdfEngineIntegration.ApplyFormValues(path, Edits("0", "Refused")));
            Assert.Equal(source, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    private static PdfEngineIntegration.FormEdits Edits(string name, string value) => new(
        new Dictionary<string, string> { [name] = value }, new Dictionary<string, string>(),
        new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
        new Dictionary<string, string>(), new Dictionary<string, double>());

    private static byte[] Fixture(bool shared = false, bool collision = false, bool unregistered = false, int registry = 0)
    {
        string[] bodies =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 9 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Annots [5 0 R 6 0 R 7 0 R 8 0 R] >>",
            "<< /FT /Tx /T (Registered) /V (Keep me) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (0) /Rect [50 650 300 675] /DA (/Helv 12 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (0) /Rect [50 600 300 625] /DA (/Helv 12 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (0) /Rect [50 550 65 565] /AP << /N << /Off 10 0 R /Yes 10 0 R >> >> /AS /Off >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (0) /Rect [100 550 115 565] /AP << /N << /Off 10 0 R /Yes 10 0 R >> >> /AS /Off >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 15 15] /Length 0 >>\nstream\n\nendstream"
        ];
        if (shared)
        {
            bodies[3] = "<< /FT /Tx /T (Shared) /Kids [5 0 R 6 0 R] /DA (/Helv 12 Tf 0 g) >>";
            bodies[4] = "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [50 650 300 675] >>";
            bodies[5] = "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [50 600 300 625] >>";
        }
        if (collision) bodies[3] = "<< /FT /Tx /T (KillerPDF_orphan_5_0) /V (Keep me) >>";
        if (unregistered) bodies[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] >> >>";
        if (registry == 1) bodies[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields 42 >> >>";
        if (registry == 2) bodies[3] = "<< /FT /Tx /T (Registered) /Kids [4 0 R] >>";
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
