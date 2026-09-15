using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfExistingSignatureAppearanceTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Sign_PreservesSourceFormatUnlessExplicitlyOverridden(bool stream, bool? explicitStream)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddSignatureField(0, "first", 10, 20, 100, 30)
            .AddSignatureField(0, "second", 10, 70, 100, 30).Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        update.AddObject(new PdfInteger(42));
        byte[] input = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = stream ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table
        });
        source = PdfDocument.Open(input);
        var options = new PdfSignatureOptions
        {
            FieldName = "first", ReservedSignatureSize = 16,
            IncrementalWriteOptions = explicitStream.HasValue ? new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = explicitStream.Value
                    ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table
            } : null
        };
        byte[] first = PdfDetachedSignatureWriter.Sign(source, _ => [1], options);
        PdfDocument signed = PdfDocument.Open(first);
        Assert.Equal(explicitStream ?? stream, signed.CrossReferences.Sections[0].IsStream);
        Assert.True(first.AsSpan(0, input.Length).SequenceEqual(input));
        byte[] second = PdfDetachedSignatureWriter.Sign(signed, _ => [2], options with
        {
            FieldName = "second", IncrementalWriteOptions = null
        });
        Assert.True(second.AsSpan(0, first.Length).SequenceEqual(first));
        var signatures = PdfSignatureReader.Read(PdfDocument.Open(second));
        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, signature => Assert.True(signature.HasValidByteRange));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Sign_AttachesAppearanceToExistingWidgetsAndKeepsGeometry(bool separate, bool directField)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddSignatureField(0, "approval", 10, 20, 150, 35, appearanceText: "Old").Build());
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")]);
        PdfDictionary catalog = Resolve(source, root);
        PdfDictionary form = Resolve(source, catalog[Name("AcroForm")]);
        PdfIndirectReference fieldRef = Assert.IsType<PdfIndirectReference>(
            Assert.Single(Assert.IsType<PdfArray>(form[Name("Fields")])));
        PdfDictionary field = Resolve(source, fieldRef);
        PdfObject rectangle = field[Name("Rect")];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference? widgetRef = null;
        if (separate)
        {
            PdfDictionary widget = new(field.Where(pair => !pair.Key.Equals(Name("T"))
                && !pair.Key.Equals(Name("FT"))));
            widgetRef = update.AddObject(widget);
            // Cover an indirect widget and a directly embedded widget in the same field.
            field = Dictionary(("FT", Name("Sig")), ("T", new PdfString("approval"u8, PdfStringForm.Literal)),
                ("Kids", new PdfArray([widgetRef, widget])));
        }
        if (directField)
        {
            form = Replace(form, "Fields", new PdfArray([field]));
            update.ReplaceObject(root.ObjectNumber, Replace(catalog, "AcroForm", form));
        }
        else update.ReplaceObject(fieldRef.ObjectNumber, field);
        byte[] input = update.Build();
        source = PdfDocument.Open(input);
        byte[] output = PdfDetachedSignatureWriter.Sign(source, _ => [1], new PdfSignatureOptions
        {
            FieldName = "approval", PageIndex = 999, ReservedSignatureSize = 16,
            VisibleAppearance = new PdfSignatureAppearance { Text = "Approved", Left = 400, Width = 300 }
        });
        PdfDocument signed = PdfDocument.Open(output);
        Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input));
        PdfDictionary signedForm = Resolve(signed, Resolve(signed, root)[Name("AcroForm")]);
        PdfDictionary signedField = Resolve(signed,
            Assert.Single(Assert.IsType<PdfArray>(signedForm[Name("Fields")])));
        Assert.IsType<PdfIndirectReference>(signedField[Name("V")]);
        if (separate)
        {
            Assert.False(signedField.ContainsKey(Name("AP")));
            foreach (PdfObject widget in Assert.IsType<PdfArray>(signedField[Name("Kids")]))
                AssertAppearance(Resolve(signed, widget));
        }
        else AssertAppearance(signedField);

        void AssertAppearance(PdfDictionary widget)
        {
            Assert.Equal(Serialize(rectangle), Serialize(widget[Name("Rect")]));
            PdfDictionary ap = Resolve(signed, widget[Name("AP")]);
            PdfStream normal = Assert.IsType<PdfStream>(signed.Resolve(
                Assert.IsType<PdfIndirectReference>(ap[Name("N")])));
            Assert.Contains("Approved", Encoding.Latin1.GetString(normal.EncodedData.Span));
        }
    }

    private static string Serialize(PdfObject value) => string.Join(",",
        Assert.IsType<PdfArray>(value).Select(item => item is PdfInteger i
            ? i.Value.ToString() : Assert.IsType<PdfReal>(item).Value.ToString()));
    private static PdfDictionary Resolve(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference r ? document.Resolve(r) : value);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] pairs) =>
        new(pairs.Select(pair => new KeyValuePair<PdfName, PdfObject>(Name(pair.Key), pair.Value)));
    private static PdfDictionary Replace(PdfDictionary dictionary, string key, PdfObject value) =>
        new(dictionary.Where(pair => !pair.Key.Equals(Name(key)))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name(key), value)));
}
