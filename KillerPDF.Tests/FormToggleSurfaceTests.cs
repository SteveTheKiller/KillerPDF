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
            var marks = new RepeatedFieldMarks();
            marks.AddCheck(0, "same", first);
            marks.AddCheck(0, "same", twin);
            marks.AddCheck(0, "other", other);

            marks.ShowCheck("same", true);

            Assert.Equal(System.Windows.Visibility.Visible, first.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, twin.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, other.Visibility);

            marks.ShowCheck("same", false);

            Assert.Equal(System.Windows.Visibility.Collapsed, first.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, twin.Visibility);
        });

        // R3-021: each page builds its overlay on its own, and the common repeated field spans pages.
        [Fact]
        public void ACheckBoxOnAnotherPageFollowsAToggle() => OnSta(() =>
        {
            var onFirst = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var onSecond = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var marks = new RepeatedFieldMarks();

            marks.BeginPage(0);
            marks.AddCheck(0, "agree", onFirst);
            marks.BeginPage(1);
            marks.AddCheck(1, "agree", onSecond);
            marks.ShowCheck("agree", true);

            Assert.Equal(System.Windows.Visibility.Visible, onFirst.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, onSecond.Visibility);
        });

        [Fact]
        public void ARadioGroupAcrossPagesShowsOneSelection() => OnSta(() =>
        {
            var yes = new TextBlock { Visibility = System.Windows.Visibility.Visible };
            var no = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var marks = new RepeatedFieldMarks();

            marks.BeginPage(0);
            marks.AddRadio(0, "choice", yes, "/Yes");
            marks.BeginPage(1);
            marks.AddRadio(1, "choice", no, "/No");
            marks.SelectRadio("choice", "/No");

            Assert.Equal(System.Windows.Visibility.Collapsed, yes.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, no.Visibility);
        });

        [Fact]
        public void ARebuiltOverlayReplacesOnlyItsOwnPage() => OnSta(() =>
        {
            var kept = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var stale = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var rebuilt = new TextBlock { Visibility = System.Windows.Visibility.Collapsed };
            var marks = new RepeatedFieldMarks();
            marks.AddCheck(0, "agree", kept);
            marks.AddCheck(1, "agree", stale);

            marks.BeginPage(1);
            marks.AddCheck(1, "agree", rebuilt);
            marks.ShowCheck("agree", true);

            Assert.Equal(System.Windows.Visibility.Visible, kept.Visibility);
            Assert.Equal(System.Windows.Visibility.Visible, rebuilt.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, stale.Visibility);
        });

        [Fact]
        public void TypingShowsInEveryBoxOfTheFieldAcrossPages() => OnSta(() =>
        {
            var typed = new TextBox { Text = "Carol" };
            var samePage = new TextBox { Text = "Bob" };
            var otherPage = new TextBox { Text = "Bob" };
            var stranger = new TextBox { Text = "Bob" };
            var marks = new RepeatedFieldMarks();
            marks.AddText(0, "name", typed);
            marks.AddText(0, "name", samePage);
            marks.AddText(3, "name", otherPage);
            marks.AddText(0, "other", stranger);

            marks.ShowText("name", "Carol", typed);

            Assert.Equal("Carol", samePage.Text);
            Assert.Equal("Carol", otherPage.Text);
            Assert.Equal("Bob", stranger.Text);
        });

        // SN01 M3: a font size set in one box of a repeated field shows in every box of the name at
        // once, each at its own page's scale, as a typed value does.
        [Fact]
        public void AFontSizeShowsInEveryBoxOfTheFieldAtItsOwnScale() => OnSta(() =>
        {
            var edited = new TextBox { FontSize = 20 };
            var twin = new TextBox { FontSize = 15 };
            var stranger = new TextBox { FontSize = 15 };
            var marks = new RepeatedFieldMarks();
            marks.AddText(0, "name", edited, 2);
            marks.AddText(2, "name", twin, 1.5);
            marks.AddText(0, "other", stranger, 1.5);

            edited.FontSize = 24;
            marks.ShowFontSize("name", 12, edited);

            Assert.Equal(18, twin.FontSize, 6);
            Assert.Equal(24, edited.FontSize, 6);
            Assert.Equal(15, stranger.FontSize, 6);
        });

        [Fact]
        public void APageBuiltAgainDropsItsOldTextBoxes() => OnSta(() =>
        {
            var typed = new TextBox { Text = "Carol" };
            var stale = new TextBox { Text = "Bob" };
            var rebuilt = new TextBox { Text = "Bob" };
            var marks = new RepeatedFieldMarks();
            marks.AddText(0, "name", typed);
            marks.AddText(1, "name", stale);

            marks.BeginPage(1);
            marks.AddText(1, "name", rebuilt);
            marks.ShowText("name", "Carol", typed);

            Assert.Equal("Carol", rebuilt.Text);
            Assert.Equal("Bob", stale.Text);
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
