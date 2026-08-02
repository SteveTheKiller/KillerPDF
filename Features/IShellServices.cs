using System.Windows;

namespace KillerPDF.Features
{
    /// <summary>
    /// The three things every feature needs from the window: an owner for modal dialogs, string
    /// lookup, and the status line. Each feature's own interface extends this, so the shell
    /// implements these once rather than once per feature.
    ///
    /// Ported from Killendar, which is the reference implementation for this pattern.
    /// </summary>
    internal interface IShellServices
    {
        /// <summary>Owner for modal dialogs.</summary>
        Window Window { get; }

        /// <summary>Localized string for a Str_ key.</summary>
        string Loc(string key);

        /// <summary>Writes the status line.</summary>
        void SetStatus(string text);
    }
}
