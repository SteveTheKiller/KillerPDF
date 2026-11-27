using System.Windows.Controls;

namespace KillerPDF;

/// <summary>
/// Two number boxes that show one page, each beside its own choice, like Insert Pages' "Before page" and "After
/// page". Typing in either box picks that box's choice and copies the number into the other box. The copy never
/// picks the other choice, so switching choices keeps the page while a typed page keeps the choice it was typed under.
/// </summary>
internal sealed class TwinPageBoxes
{
    private bool _copying;

    /// <param name="picks">Whether typing picks a choice yet; false while the window is still being built.</param>
    /// <param name="changed">Runs once per typed change, after both boxes show it.</param>
    internal TwinPageBoxes(TextBox first, RadioButton firstChoice, TextBox second, RadioButton secondChoice,
        Func<bool> picks, Action changed)
    {
        Wire(first, firstChoice, second, picks, changed);
        Wire(second, secondChoice, first, picks, changed);
    }

    private void Wire(TextBox box, RadioButton choice, TextBox twin, Func<bool> picks, Action changed)
    {
        box.TextChanged += (_, _) =>
        {
            if (_copying) return;
            if (picks()) choice.IsChecked = true;
            if (twin.Text != box.Text)
            {
                _copying = true;
                try { twin.Text = box.Text; }
                finally { _copying = false; }
            }
            changed();
        };
        box.GotKeyboardFocus += (_, _) => choice.IsChecked = true;
    }
}
