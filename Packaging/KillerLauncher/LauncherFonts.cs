using System;
using KillerPDF.Services;

namespace KillerLauncher
{
    /// <summary>
    /// The installer and portable launcher font stacks for a resolved launcher locale (the name
    /// LauncherStrings picked the strings for). Chinese, Japanese and Korean take the same CJK face
    /// after Killer Sans UI and Segoe UI as the application (Services/UiFontStack.cs); every other
    /// language keeps the stacks the launcher always had.
    /// </summary>
    internal static class LauncherFonts
    {
        private const string Latin = "./Fonts/#Killer Sans UI, Segoe UI";

        internal static string Ui(string locale) => UiFontStack.IsCjk(locale) ? UiFontStack.For(locale) : Latin;

        internal static string Heading(string locale) => UiFontStack.Heading(locale);
    }
}
