namespace KillerPDF
{
    // How a document view lays its pages out, and how it fits them to the viewport.
    //
    // TOP-LEVEL, not nested in MainWindow. The viewer is a UserControl in KillerPDF.Controls, and
    // from there a type nested in MainWindow only spells as MainWindow.ViewMode - which would mean
    // qualifying 91 references for no gain. As top-level types in KillerPDF they resolve
    // unqualified from KillerPDF.Controls too (a namespace declaration puts its parent namespaces
    // in scope), so every call site compiles untouched.
    //
    // internal, not public: nothing outside the assembly has any business with either.

    /// <summary>Page layout for a document view. RenderPage is Single/TwoPage/Grid only and is
    /// guarded to no-op in Continuous - see the render pipeline's notes on why the two pipelines
    /// cannot be mixed.</summary>
    internal enum ViewMode { Single, Continuous, TwoPage, Grid }

    /// <summary>Automatic fit applied on resize, or None when the user has set a zoom.</summary>
    internal enum FitMode { None, Width, Page }
}
