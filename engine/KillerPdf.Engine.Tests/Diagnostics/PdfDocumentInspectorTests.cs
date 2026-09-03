using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;
using System.Text.Json;

namespace KillerPdf.Engine.Tests.Diagnostics;

public sealed class PdfDocumentInspectorTests
{
    [Fact]
    public void ReportExportsStableMachineReadableJson()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        PdfInspectionReport report = PdfDocumentInspector.Inspect(source);

        using JsonDocument json = JsonDocument.Parse(report.ToJson(indented: true));
        JsonElement root = json.RootElement;
        Assert.Equal(report.Version?.ToString(), root.GetProperty("version").GetString());
        Assert.Equal(report.InspectedObjectCount, root.GetProperty("inspectedObjectCount").GetInt32());
        Assert.True(root.GetProperty("isStructurallyValid").GetBoolean());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("diagnostics").ValueKind);
    }

    [Fact]
    public void Inspect_ReportsAValidDocumentWithoutFindings()
    {
        byte[] source = ClassicPdf("1 0 obj << /Type /Catalog >> endobj\n", includeRoot: true);

        PdfInspectionReport report = PdfDocumentInspector.Inspect(source);

        Assert.True(report.IsStructurallyValid);
        Assert.False(report.RequiresRepair);
        Assert.Equal(PdfVersion.Pdf20, report.Version);
        Assert.NotNull(report.StartXrefOffset);
        Assert.Equal(2, report.CrossReferenceEntryCount);
        Assert.Equal(1, report.InspectedObjectCount);
        Assert.Empty(report.Diagnostics);
    }

    [Fact]
    public void Inspect_ResolvesMultiHopCatalogRoots()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        PdfIndirectReference rootReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[new PdfName("Root"u8)]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            document.Resolve(rootReference));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference catalogType = update.AddObject(new PdfName("Catalog"u8));
        PdfIndirectReference catalogTypeAlias = update.AddObject(catalogType);
        PdfIndirectReference movedCatalog = update.AddObject(new PdfDictionary(
            catalog.Select(entry => entry.Key.Equals(new PdfName("Type"u8))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, catalogTypeAlias)
                : entry)));
        update.ReplaceObject(rootReference.ObjectNumber, movedCatalog);

        PdfInspectionReport report = PdfDocumentInspector.Inspect(update.Build());

        Assert.DoesNotContain(report.Diagnostics,
            item => item.Code == PdfDiagnosticCode.InvalidCatalogRoot);
        Assert.True(report.IsStructurallyValid);
    }

    [Fact]
    public void Inspect_ReportsHeaderAndStartXrefFailuresWithoutThrowing()
    {
        PdfInspectionReport report = PdfDocumentInspector.Inspect("not a pdf"u8.ToArray());

        Assert.True(report.RequiresRepair);
        Assert.Contains(report.Diagnostics, item => item.Code == PdfDiagnosticCode.InvalidHeader);
        Assert.Contains(report.Diagnostics, item => item.Code == PdfDiagnosticCode.InvalidStartXref);
        Assert.Null(report.CrossReferenceEntryCount);
    }

    [Fact]
    public void Inspect_DistinguishesABrokenXrefTargetFromABrokenStartXrefDeclaration()
    {
        byte[] source = "%PDF-2.0\nstartxref\n1\n%%EOF\n"u8.ToArray();

        PdfInspectionReport report = PdfDocumentInspector.Inspect(source);

        Assert.DoesNotContain(report.Diagnostics, item => item.Code == PdfDiagnosticCode.InvalidStartXref);
        Assert.Contains(report.Diagnostics, item => item.Code == PdfDiagnosticCode.InvalidCrossReference);
    }

    [Fact]
    public void Inspect_ReportsInvalidTrailerOffsetsWithoutThrowing()
    {
        byte[] source = ClassicPdf(
            "1 0 obj << /Type /Catalog >> endobj\n", includeRoot: true);
        string text = Encoding.ASCII.GetString(source)
            .Replace("/Root 1 0 R", "/Root 1 0 R /Prev -1", StringComparison.Ordinal);

        PdfInspectionReport report = PdfDocumentInspector.Inspect(
            Encoding.ASCII.GetBytes(text));

        Assert.True(report.RequiresRepair);
        Assert.Contains(report.Diagnostics,
            item => item.Code == PdfDiagnosticCode.InvalidCrossReference);
    }

    [Fact]
    public void Inspect_DoesNotThrowForDeterministicallyMutatedPdfBytes()
    {
        byte[] valid = ClassicPdf(
            "1 0 obj << /Type /Catalog >> endobj\n", includeRoot: true);
        var random = new Random(18_00_20);

        for (int sample = 0; sample < 500; sample++)
        {
            byte[] mutated = [.. valid];
            int changes = random.Next(1, 9);
            for (int change = 0; change < changes; change++)
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);

            Exception? error = Record.Exception(() => PdfDocumentInspector.Inspect(mutated));
            Assert.Null(error);
        }
    }

    [Fact]
    public void Inspect_IdentifiesTheObjectWhoseXrefEntryPointsToTheWrongHeader()
    {
        byte[] source = ClassicPdf("2 0 obj << /Type /Catalog >> endobj\n", includeRoot: true);

        PdfInspectionReport report = PdfDocumentInspector.Inspect(source);

        PdfDiagnostic finding = Assert.Single(
            report.Diagnostics,
            item => item.Code == PdfDiagnosticCode.InvalidIndirectObject);
        Assert.Equal(1, finding.ObjectNumber);
        Assert.NotNull(finding.Offset);
        Assert.Contains("points to object 2 0", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_ReportsAMissingCatalogSeparatelyFromObjectDamage()
    {
        byte[] source = ClassicPdf("1 0 obj << /Type /Example >> endobj\n", includeRoot: false);

        PdfInspectionReport report = PdfDocumentInspector.Inspect(source);

        Assert.Contains(report.Diagnostics, item => item.Code == PdfDiagnosticCode.MissingCatalogRoot);
        Assert.DoesNotContain(report.Diagnostics, item => item.Code == PdfDiagnosticCode.InvalidIndirectObject);
    }

    [Fact]
    public void Inspect_RejectsRootDictionariesWithoutCatalogType()
    {
        byte[] source = ClassicPdf(
            "1 0 obj << /Type /Example >> endobj\n", includeRoot: true);

        PdfInspectionReport report = PdfDocumentInspector.Inspect(source);

        Assert.Contains(report.Diagnostics,
            item => item.Code == PdfDiagnosticCode.InvalidCatalogRoot
                && item.Message.Contains("/Type /Catalog", StringComparison.Ordinal));
        Assert.False(report.IsStructurallyValid);
    }

    [Fact]
    public void Inspect_BoundsObjectResolutionAndReportsTheLimit()
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int firstOffset = source.Length;
        source.Append("1 0 obj << /Type /Catalog >> endobj\n");
        int secondOffset = source.Length;
        source.Append("2 0 obj true endobj\n");
        int xrefOffset = source.Length;
        source.Append("xref\n0 3\n0000000000 65535 f\n");
        source.Append($"{firstOffset:0000000000} 00000 n\n");
        source.Append($"{secondOffset:0000000000} 00000 n\n");
        source.Append("trailer << /Size 3 /Root 1 0 R >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        PdfInspectionReport report = PdfDocumentInspector.Inspect(
            Encoding.ASCII.GetBytes(source.ToString()),
            maximumInspectedObjects: 1);

        Assert.Equal(1, report.InspectedObjectCount);
        Assert.Contains(report.Diagnostics, item => item.Code == PdfDiagnosticCode.InspectionLimitReached);
        Assert.True(report.IsStructurallyValid);
    }

    [Fact]
    public void InspectAuthenticated_ResolvesEncryptedCompressedObjects()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user-password",
                OwnerPassword = "owner-password"
            })
            .AddPage(612, 792, "q 0 0 10 10 re f Q"u8.ToArray())
            .Build();

        PdfInspectionReport report = PdfDocumentInspector.InspectAuthenticated(
            source, "owner-password");
        PdfInspectionReport rejected = PdfDocumentInspector.InspectAuthenticated(
            source, "wrong");
        PdfInspectionReport unauthenticated = PdfDocumentInspector.Inspect(source);

        Assert.True(report.IsStructurallyValid);
        Assert.Contains(rejected.Diagnostics,
            item => item.Code == PdfDiagnosticCode.AuthenticationFailed);
        Assert.True(rejected.RequiresAuthentication);
        Assert.True(rejected.IsStructurallyValid);
        Assert.False(rejected.RequiresRepair);
        Assert.True(unauthenticated.RequiresAuthentication);
        Assert.True(unauthenticated.IsStructurallyValid);
        Assert.False(unauthenticated.RequiresRepair);
        Assert.Equal(0, unauthenticated.InspectedObjectCount);
    }

    [Fact]
    public void InspectAuthenticated_ReportsCorruptedEncryptedStreamWithoutThrowing()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user-password",
                OwnerPassword = "owner-password"
            })
            .AddPage(612, 792, "q 0 0 10 10 re f Q"u8.ToArray())
            .Build();
        PdfDocument raw = PdfDocument.Open(source);
        PdfStream encryptedStream = raw.CrossReferences.Values
            .Where(entry => entry.Type == KillerPdf.Engine.CrossReference.PdfCrossReferenceEntryType.InUse)
            .Select(entry => raw.Resolve(entry.ObjectNumber))
            .OfType<PdfStream>()
            .First();
        int streamOffset = source.AsSpan().IndexOf(encryptedStream.EncodedData.Span);
        Assert.True(streamOffset >= 0);
        Assert.True(encryptedStream.EncodedData.Length >= 32);
        source[streamOffset + encryptedStream.EncodedData.Length - 17] ^= 0xFF;

        PdfInspectionReport report = PdfDocumentInspector.InspectAuthenticated(
            source, "owner-password");

        Assert.True(report.RequiresRepair);
        Assert.False(report.RequiresAuthentication);
        Assert.Contains(report.Diagnostics,
            item => item.Code == PdfDiagnosticCode.InvalidIndirectObject);
    }

    [Fact]
    public void InspectAuthenticated_ClassifiesCorruptedPermissionBlockAsDamage()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user-password",
                OwnerPassword = "owner-password"
            })
            .AddBlankPage()
            .Build();
        PdfDocument raw = PdfDocument.Open(source);
        PdfIndirectReference encryptionReference = Assert.IsType<PdfIndirectReference>(
            raw.Trailer[new PdfName("Encrypt"u8)]);
        PdfDictionary encryption = Assert.IsType<PdfDictionary>(
            raw.Resolve(encryptionReference));
        PdfString permissions = Assert.IsType<PdfString>(
            encryption[new PdfName("Perms"u8)]);
        string permissionsHex = Convert.ToHexString(permissions.Bytes.Span);
        int permissionsOffset = source.AsSpan().IndexOf(
            Encoding.ASCII.GetBytes(permissionsHex));
        Assert.True(permissionsOffset >= 0);
        source[permissionsOffset] = source[permissionsOffset] == (byte)'0'
            ? (byte)'1' : (byte)'0';

        PdfInspectionReport report = PdfDocumentInspector.InspectAuthenticated(
            source, "owner-password");

        Assert.True(report.RequiresRepair);
        Assert.False(report.RequiresAuthentication);
        Assert.Contains(report.Diagnostics,
            item => item.Code == PdfDiagnosticCode.InvalidCrossReference);
    }

    private static byte[] ClassicPdf(string objectDeclaration, bool includeRoot)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        int objectOffset = source.Length;
        source.Append(objectDeclaration);
        int xrefOffset = source.Length;
        source.Append("xref\n0 2\n0000000000 65535 f\n");
        source.Append($"{objectOffset:0000000000} 00000 n\n");
        source.Append(includeRoot
            ? "trailer << /Size 2 /Root 1 0 R >>\n"
            : "trailer << /Size 2 >>\n");
        source.Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }
}
