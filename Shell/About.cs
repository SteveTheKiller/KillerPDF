using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using KillerPDF.Controls;
using KillerPDF.Features.About;
using KillerPDF.Services;

namespace KillerPDF.Shell
{
    /// <summary>
    /// The About overlay's window half: the fade, the click handling, and the IAboutHost
    /// implementation that maps the controller's values onto the named XAML elements.
    /// Everything the card SAYS is decided in Features/About/AboutController.cs. Rows only this
    /// app's card has (Start with Windows, Close to tray, KillerScan's vendor database) are mapped
    /// in the app's own Shell/About.Extras.cs.
    /// </summary>
    public partial class MainWindow : IAboutHost
    {
        private AboutController _about = null!;

        private void ShowAboutOverlay() => _about.Show();

        // ---- IAboutHost ----

        string IAboutHost.Version     { set => AboutVersionBlock.Text = value; }
        string IAboutHost.ReleaseDate { set => AboutReleaseDateBlock.Text = value; }
        string IAboutHost.Publisher   { set => AboutPublisherBlock.Text = value; }
        string IAboutHost.Alias       { set => AboutAkaRun.Text = value; }
        string IAboutHost.Thumbprint  { set => AboutThumbprintBlock.Text = value; }
        string IAboutHost.Sha256      { set => AboutSha256Block.Text = value; }
        string IAboutHost.UpdateText  { set => AboutUpdateText.Text = value; }

        bool IAboutHost.AliasVisible
        {
            set => AboutAkaBlock.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateVisible
        {
            set => AboutUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateEnabled { set => AboutUpdateButton.IsEnabled = value; }

        bool IAboutHost.StartupUpdateCheck { set => AboutStartupUpdateCheck.IsChecked = value; }

        void IAboutHost.ShowCard()
        {
            bool handled = false;
            ShowingCard(ref handled);
            if (!handled) FadeOverlayIn(AboutOverlay);
        }

        /// <summary>App hook: show the card yourself and set <paramref name="handled"/> (KillerNotes
        /// opens a keyboard focus scope around the fade and focuses the card after it).</summary>
        partial void ShowingCard(ref bool handled);

        /// <summary>App hook: hide an overlay yourself and set <paramref name="handled"/> (KillerNotes
        /// hands its keyboard-managed overlays to their own close path).</summary>
        partial void OverlayFadingOut(UIElement overlay, ref bool handled);

        /// <summary>App hook: close the shortcuts overlay before the footer version opens About, and
        /// set <paramref name="handled"/>; otherwise the kit collapses ShortcutsOverlay.</summary>
        partial void CloseShortcutsForAbout(ref bool handled);

        // ---- Overlay fade (shared with the shortcuts overlay, ShortcutsOverlay.cs) ----

        /// <summary>App hook: an overlay is about to show (KillerScan dims the frame behind it).</summary>
        partial void OnOverlayShowing(UIElement overlay);

        /// <summary>App hook: an overlay finished hiding.</summary>
        partial void OnOverlayHidden(UIElement overlay);

        private readonly Dictionary<UIElement, DoubleAnimation> _overlayFadesOut = new();

        private void FadeOverlayIn(UIElement o)
        {
            _overlayFadesOut.Remove(o);
            OnOverlayShowing(o);
            o.Visibility = Visibility.Visible;
            Anim.FadeIn(o);
        }

        private void FadeOverlayOut(UIElement o)
        {
            bool handled = false;
            OverlayFadingOut(o, ref handled);
            if (handled) return;
            var a = new DoubleAnimation(o.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Anim.FadeMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            // A replaced fade still raises Completed, so only the latest fade out may collapse the
            // overlay; a reopen during the fade would otherwise vanish when the old one lands.
            _overlayFadesOut[o] = a;
            a.Completed += (_, _) =>
            {
                if (!_overlayFadesOut.TryGetValue(o, out var current) || current != a) return;
                _overlayFadesOut.Remove(o);
                o.Visibility = Visibility.Collapsed;
                OnOverlayHidden(o);
            };
            o.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // ---- Handlers ----

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void AboutOverlay_Click(object sender, MouseButtonEventArgs e) => FadeOverlayOut(AboutOverlay);
        private void AboutCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void AboutClose_Click(object sender, RoutedEventArgs e) => FadeOverlayOut(AboutOverlay);

        private void AboutVersion_Click(object sender, MouseButtonEventArgs e) => _about.OpenReleaseNotes();

        /// <summary>The footer version opens the About card, as in every app. One overlay at a
        /// time, so the F1 overlay steps aside first.</summary>
        private void VersionLabel_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            bool handled = false;
            CloseShortcutsForAbout(ref handled);
            if (!handled && ShortcutsOverlay.Visibility == Visibility.Visible) ShortcutsOverlay.Visibility = Visibility.Collapsed;
            ShowAboutOverlay();
        }

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => _about.Update();

        private void AboutStartupUpdateCheck_Click(object sender, RoutedEventArgs e)
            => _about.SetStartupCheck(AboutStartupUpdateCheck.IsChecked == true);

        private void AboutLink_Navigate(object sender, RequestNavigateEventArgs e)
        {
            WebLink.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
