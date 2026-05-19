namespace TDPdf.Diagnostics
{
    // Placeholder partial. All constants empty — HasKey returns false,
    // TryDecrypt returns null, and TDPdf auto-provisioning is a no-op.
    //
    // At release time, build\embed-telemetry-key.ps1 generates
    // EmbeddedTelemetry.Generated.cs (gitignored) with real base64
    // values, and TDPdf.csproj excludes THIS file from compilation when
    // the generated file exists. The placeholder is what ships in the
    // GPLv3 source bundle (built from `git ls-files`).
    internal static partial class EmbeddedTelemetry
    {
        internal const string CiphertextB64 = "";
        internal const string KeyPart1B64 = "";
        internal const string KeyPart2B64 = "";
        internal const string IvB64 = "";
    }
}
