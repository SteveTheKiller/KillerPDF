using System;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests
{
    // BF-U002: drawn PDF checkboxes and radios are keyboard-focusable and report the same
    // automation semantics as real CheckBox and RadioButton controls.
    public class FormToggleSurfaceTests
    {
        [Fact]
        public void CheckBoxTogglesThroughItsAutomationPeer() => OnSta(() =>
        {
            bool on = false;
            var box = new FormToggleSurface(new Border(), FormToggleSurface.ToggleKind.CheckBox, () => on, () => on = !on);
            var peer = (FrameworkElementAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(box);

            Assert.True(box.Focusable);
            Assert.True(box.IsTabStop);
            Assert.Equal(AutomationControlType.CheckBox, peer.GetAutomationControlType());
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            Assert.Equal(ToggleState.Off, toggle.ToggleState);
            toggle.Toggle();
            Assert.True(on);
            Assert.Equal(ToggleState.On, toggle.ToggleState);
        });

        [Fact]
        public void ReadOnlyFieldsRefuseAutomationChanges() => OnSta(() =>
        {
            var box = new FormToggleSurface(new Border(), FormToggleSurface.ToggleKind.CheckBox, () => false, null);
            var toggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(box).GetPattern(PatternInterface.Toggle);

            Assert.Throws<ElementNotEnabledException>(() => toggle.Toggle());
        });

        [Fact]
        public void RadioSelectsAndKeepsItsGroupExclusive() => OnSta(() =>
        {
            string selected = "A";
            var a = new FormToggleSurface(new Border(), FormToggleSurface.ToggleKind.Radio, () => selected == "A", () => selected = "A", "g");
            var b = new FormToggleSurface(new Border(), FormToggleSurface.ToggleKind.Radio, () => selected == "B", () => selected = "B", "g");
            var peerB = UIElementAutomationPeer.CreatePeerForElement(b);

            Assert.Equal(AutomationControlType.RadioButton, peerB.GetAutomationControlType());
            var item = Assert.IsAssignableFrom<ISelectionItemProvider>(peerB.GetPattern(PatternInterface.SelectionItem));
            Assert.Null(peerB.GetPattern(PatternInterface.Toggle));
            Assert.False(item.IsSelected);
            item.Select();
            Assert.True(item.IsSelected);
            Assert.False(a.IsOn);
        });

        // R2-014: check boxes that share a name show one state, as the save writes it.
        [Fact]
        public void EveryCheckMarkOfANameFollowsAToggle() => OnSta(() =>
        {
            var first = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var twin = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var other = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var marks = new RepeatedCheckMarks();
            marks.Add("same", first);
            marks.Add("same", twin);
            marks.Add("other", other);

            marks.Show("same", true);

            Assert.Equal(System.Windows.Visibility.Visible, first.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, twin.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, other.Visibility);

            marks.Show("same", false);

            Assert.Equal(System.Windows.Visibility.Collapsed, first.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, twin.Visibility);
        });

        private static void OnSta(Action test)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { test(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The STA test timed out.");
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
