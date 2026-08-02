namespace KillerPDF.Features
{
    /// <summary>
    /// What AboutController needs from the window hosting it, beyond the shared shell services.
    ///
    /// Every member is a value or a plain string, never a control, so the controller holds no
    /// reference to a TextBlock or a Button and can be driven by a stub in a test.
    ///
    /// KillerPDF differs from Killendar's version in one way worth knowing: several of the About
    /// card's lines are built as INLINES rather than set as text - the wordmark is two differently
    /// styled runs, and the tagline, version and alias each carry a hyperlink. Constructing those
    /// is UI work, so it stays in the shell and the controller hands over only the strings and the
    /// one boolean the shell needs to decide what to build.
    /// </summary>
    internal interface IAboutHost : IShellServices
    {
        /// <summary>Code-signing subject, or the unsigned message.</summary>
        string Publisher { set; }

        /// <summary>Certificate thumbprint, or "(none)".</summary>
        string Thumbprint { set; }

        /// <summary>SHA-256 of the running exe. Set twice: the "computing" placeholder first,
        /// then the real digest once the background hash finishes.</summary>
        string Sha256 { set; }

        /// <summary>Release date baked in from the csproj, shown muted opposite the version.
        /// Empty on an older build that predates the attribute.</summary>
        string ReleaseDate { set; }

        /// <summary>Builds the version line as a hyperlink through to that release tag.</summary>
        void SetVersion(string version);

        /// <summary>The quoted alias line. Null hides it - which is the case unless the exe is
        /// signed AND the signature verifies AND the subject is Steve's, because a fork signed by
        /// somebody else must not claim the alias.</summary>
        void SetAlias(string? alias);

        /// <summary>Whether a newer release exists, and whether the button is live while a
        /// download is running.</summary>
        string UpdateText { set; }
        bool UpdateVisible { set; }
        bool UpdateEnabled { set; }

        /// <summary>Blocks a self-update while there are unsaved changes.</summary>
        bool IsDirty { get; }

        /// <summary>The document to reopen after the update relaunches, if any.</summary>
        string? FileToReopen { get; }

        /// <summary>Dismisses any other full-window overlay, then fades the About card in.
        /// The overlays are mutually exclusive rather than stacking.</summary>
        void ShowCard();
    }
}
