using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SavedSignatureButtonTests
{
    [Fact]
    public void SavedChoiceIsNamedFocusableAndInvokesTheSharedActivationPath() => OnSta(() =>
    {
        int picks = 0;
        var choice = new SavedSignatureButton("Initials: Jane", mouse =>
        {
            Assert.Null(mouse);
            picks++;
        });
        var button = Assert.IsAssignableFrom<Button>(choice);
        Assert.True(button.Focusable);
        Assert.True(button.IsTabStop);
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.Equal("Initials: Jane", peer.GetName());
        var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert.Equal(1, picks);
    });

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The control test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
