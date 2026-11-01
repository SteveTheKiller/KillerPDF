using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerPDF.Services;

internal sealed class SavedSignatureButton : Button
{
    public SavedSignatureButton(string name, Action<MouseButtonEventArgs?> activate)
    {
        AutomationProperties.SetName(this, name);
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Selection starts on press so the same gesture can continue onto the page as a drag.
            e.Handled = true;
            activate(e);
        };
        Click += (_, _) => activate(null);
    }
}
