namespace KillerPDF.Services;

/// <summary>Where a press or wheel in a pane leaves the keyboard.</summary>
internal static class PointerFocus
{
    /// <summary>
    /// A press or wheel on the document hands the keyboard to the page. One on the options bar, or in
    /// a dropdown drawn from it, leaves the keyboard where it is: taking it from an open ComboBox
    /// closes the list before the click picks anything, and the wheel closed it too (#467).
    /// </summary>
    public static bool MovesKeyboardToPage(bool onOptionsBar, bool inPopup) => !(onOptionsBar || inPopup);
}
