using System;

namespace KillerPDF
{
    /// <summary>
    /// The words a dialog's wordmark caption shows after "KillerPDF". The main title bar puts the
    /// file name straight after the wordmark with no dash, so a dialog drops the "- " its Title
    /// carries for the taskbar and the plain caption.
    /// </summary>
    internal static class DialogTitleText
    {
        public static string Qualifier(string fullTitle)
        {
            int kp = fullTitle.IndexOf("KillerPDF", StringComparison.Ordinal);
            string after = kp >= 0 ? fullTitle[(kp + "KillerPDF".Length)..] : fullTitle;
            return after.Trim().TrimStart('-').TrimStart();
        }
    }
}
