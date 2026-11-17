using KillerPdf.Engine.Signing;

namespace KillerPDF.Services
{
    internal static class SignatureStatus
    {
        // The string keys for a signature's panel status. A certification that allows no changes
        // is broken by any later revision, however valid the signature bytes still are, so that
        // verdict replaces Valid instead of trailing it as "changed".
        internal static string[] Keys(PdfSignatureInspectionEntry entry)
        {
            if (!entry.Signature.IsSigned) return ["Str_Panel_SigUnsigned"];
            if (entry.Revision?.PermissionAssessment == PdfSignedRevisionPermissionAssessment.Prohibited)
                return ["Str_Panel_SigCertBroken"];
            string verdict = entry.Verification.ValidationStatus switch
            {
                PdfSignatureValidationStatus.Valid => "Str_Panel_SigValid",
                PdfSignatureValidationStatus.Invalid => "Str_Panel_SigInvalid",
                _ => "Str_Panel_SigIncomplete",
            };
            return entry.Revision?.HasLaterChanges == true ? [verdict, "Str_Panel_SigChanged"] : [verdict];
        }
    }
}
