using System;
using System.Threading.Tasks;
using KillerPDF.Controls;
using KillerPDF.Services;

namespace KillerPDF.Features.About
{
    /// <summary>
    /// The About card's content: version and release date, the code-signing publisher and
    /// thumbprint, the running exe's SHA-256, and a quiet update check with optional one-click
    /// self-update.
    ///
    /// One controller serves every app. Rows only some cards have arrive as capability interfaces
    /// on the host (IAboutStartup, IAboutTray, in IAboutHost.cs), and anything a single app adds
    /// lives in that app's own AboutController.Extras.cs, which implements the partial hooks below.
    /// A hook nobody implements compiles away.
    /// </summary>
    internal sealed partial class AboutController
    {
        /// <summary>App hook: fill the app's own rows; runs as the card is filled, before it shows.</summary>
        partial void OnShow();

        /// <summary>App hook: adjust the signature the card shows (KillerScan's --demo preview).</summary>
        partial void AdjustSignature(ref bool valid, ref string subject, ref string thumbprint);

        /// <summary>App hook: replace a raw placeholder value ("(none)", "(unavailable)") with
        /// localized text.</summary>
        partial void ShownValue(ref string value);

        /// <summary>App hook: skip the startup update prompt (KillerScan skips it under --demo).</summary>
        partial void SkipStartupCheck(ref bool skip);

        /// <summary>App hook: hold the update back, checked before the download starts and again
        /// once it is verified (KillerBench holds it while a repair is running). A held download
        /// is discarded and the update button comes back.</summary>
        partial void HoldUpdate(ref bool hold);

        /// <summary>App hook: the last word before the swap helper starts and the app closes,
        /// after the download, since documents can change while it runs (KillerShell's unsaved
        /// documents check, KillerNotes saving the open note). Set proceed false to cancel; the
        /// download is discarded and the update button comes back.</summary>
        partial void ConfirmUpdateShutdown(ref bool proceed);

        /// <summary>App hook: the helper did not start after ConfirmUpdateShutdown agreed (UAC
        /// declined), so the app stays open; undo whatever the confirmation set up (KillerShell's
        /// CancelShutdown).</summary>
        partial void UpdateShutdownCanceled();

        /// <summary>App hook: close the app once the helper is running. Set handled to shut down
        /// through the app's own path; otherwise Application.Current.Shutdown() runs.</summary>
        partial void ShutdownForUpdate(ref bool handled);

        /// <summary>App hook: runs immediately before the verified update is handed off (the swap
        /// helper or installer starts), after ConfirmUpdateShutdown agreed. Set proceed false to cancel
        /// without starting anything; the download is discarded and the update button comes back
        /// (KillerPDF records its open documents here and refuses a session it cannot reopen).</summary>
        partial void BeforeUpdateHandoff(ref bool proceed);

        /// <summary>App hook: the handoff BeforeUpdateHandoff prepared has ended. Started is true once
        /// the helper or installer is running and the app is about to close (KillerPDF commits the
        /// documents to reopen); false when it did not start, so the app stays open and undoes what it
        /// prepared.</summary>
        partial void AfterUpdateHandoff(bool started);

        private readonly IAboutHost _host;

        /// <summary>Tag of the newer release found by the check, null when there is nothing to
        /// install.</summary>
        private string? _updateTag;

        /// <summary>Setting that turns the startup check off. Anything but "0" leaves it on.</summary>
        internal const string StartupCheckSetting = "CheckForUpdatesOnStartup";

        // Static: once per process, so an app with several windows (each with its own About
        // controller) prompts once.
        private static bool _startupCheckStarted;
        private bool _updating;

        internal AboutController(IAboutHost host) => _host = host;

        internal static bool StartupCheckEnabled => ThemeManager.GetSetting(StartupCheckSetting) != "0";

        /// <summary>Saves the startup check choice from either the About checkbox or the prompt.</summary>
        internal void SetStartupCheck(bool enabled)
        {
            ThemeManager.SetSetting(StartupCheckSetting, enabled ? "1" : "0");
            _host.StartupUpdateCheck = enabled;
        }

        /// <summary>Adds or removes the app's Run value from the About checkbox, then shows what the
        /// registry actually holds, so a refused write unchecks the box again.</summary>
        internal void SetStartWithWindows(bool enabled)
        {
            KillerFamily.Deployment.StartupRegistration.Set(AppInfo.DisplayName, enabled);
            if (_host is IAboutStartup startup)
                startup.StartWithWindows = KillerFamily.Deployment.StartupRegistration.IsEnabled(AppInfo.DisplayName);
        }

        /// <summary>Whether closing the main window hides it to the notification area. Read by
        /// CloseToTray each time the window closes, so the checkbox takes effect at once.</summary>
        internal static bool CloseToTrayEnabled => ThemeManager.GetSetting(KillerFamily.Shell.CloseToTray.SettingName) == "1";

        /// <summary>Saves the Close to system tray choice from the About checkbox.</summary>
        internal void SetCloseToTray(bool enabled)
        {
            ThemeManager.SetSetting(KillerFamily.Shell.CloseToTray.SettingName, enabled ? "1" : "0");
            if (_host is IAboutTray tray) tray.CloseToTray = enabled;
        }

        /// <summary>Once per run, after the main window is showing: when a newer release exists, offers
        /// it in the same dialog as the About card's update, with an "Always check on startup" box so the
        /// check can be turned off right there. The About card's checkbox turns it back on.</summary>
        internal async void CheckOnStartup()
        {
            bool skip = false;
            SkipStartupCheck(ref skip);
            if (_startupCheckStarted || skip || !StartupCheckEnabled) return;
            _startupCheckStarted = true;
            var tag = await UpdateService.CheckAsync(AppInfo.AssemblyVersion);
            if (tag is null || _updating) return;

            _updateTag          = tag;
            _host.UpdateText    = string.Format(_host.Loc("Str_About_UpdateAvailable"), tag);
            _host.UpdateVisible = true;

            // Never stack the prompt on a dialog the user already has open.
            if (!_host.Window.IsVisible || _host.Window.OwnedWindows.Count > 0) return;
            var dlg = new ConfirmDialog(
                string.Format(_host.Loc("Str_About_StartupUpdateHeading"), AppInfo.DisplayName, tag),
                _host.Loc("Str_About_StartupUpdateDetail"),
                _host.Loc("Str_About_UpdateNow"), _host.Loc("Str_About_NotNow"),
                _host.Loc("Str_About_AlwaysCheckOnStartup"), StartupCheckEnabled) { Owner = _host.Window };
            dlg.ShowDialog();
            SetStartupCheck(dlg.Check1Checked);
            if (dlg.Confirmed) await InstallAsync(tag);
        }

        /// <summary>Fills the card in and shows it, then finishes the two slow parts in the
        /// background.</summary>
        internal void Show()
        {
            _host.Version     = $"v{AppInfo.Version}";
            _host.ReleaseDate = AppInfo.ReleaseDate;

            var (sigValid, subject, thumb) = CodeSignature.GetSignerInfo();
            AdjustSignature(ref sigValid, ref subject, ref thumb);
            _host.Publisher = sigValid ? subject : _host.Loc("Str_About_Unsigned");

            // Shown only when the exe is signed by the expected publisher AND the signature actually
            // verifies: reading a certificate out of a file does not prove the file is untampered.
            bool signedByPublisher = sigValid &&
                subject.IndexOf(AppInfo.SignerName, StringComparison.OrdinalIgnoreCase) >= 0;

            // Only the quoted alias goes here; the prefix and the surrounding hyperlink live in the
            // XAML. 0x201C / 0x201D are the curly quotes, built from codepoints so this line adds no
            // non-ASCII bytes to the source.
            _host.Alias        = (char)0x201C + AppInfo.AkaName + (char)0x201D;
            _host.AliasVisible = signedByPublisher;
            string shownThumb  = thumb;
            ShownValue(ref shownThumb);
            _host.Thumbprint   = shownThumb;
            _host.Sha256       = _host.Loc("Str_About_Computing");
            _host.UpdateVisible = false;
            _host.StartupUpdateCheck = StartupCheckEnabled;
            if (_host is IAboutStartup startup)
                startup.StartWithWindows = KillerFamily.Deployment.StartupRegistration.IsEnabled(AppInfo.DisplayName);
            if (_host is IAboutTray tray) tray.CloseToTray = CloseToTrayEnabled;
            OnShow();

            _host.ShowCard();

            // SHA-256 is slow on a large exe, so it lands after the card is already up.
            Task.Run(() =>
            {
                var hash = CodeSignature.ExeSha256();
                _host.Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    string shown = hash;
                    ShownValue(ref shown);
                    _host.Sha256 = shown;
                }));
            });

            CheckForUpdate();
        }

        /// <summary>Opens the release page for the running version.</summary>
        internal void OpenReleaseNotes() => WebLink.Open(UpdateService.ReleaseUrl(AppInfo.Version));

        /// <summary>Shows the update button if a newer release exists. Silent when offline.</summary>
        private async void CheckForUpdate()
        {
            var tag = await UpdateService.CheckAsync(AppInfo.AssemblyVersion);
            if (tag is null || _updating) return;

            _updateTag          = tag;
            _host.UpdateText    = string.Format(_host.Loc("Str_About_UpdateAvailable"), tag);
            _host.UpdateVisible = true;
        }

        /// <summary>Downloads the newer release, verifies it, and hands off to the helper that swaps
        /// the exe and relaunches. Download failures open the releases page; the replacement helper
        /// shows its own error log if it cannot finish after elevation.</summary>
        internal async void Update()
        {
            var tag = _updateTag;
            if (string.IsNullOrEmpty(tag) || _updating) return;

            var dlg = new ConfirmDialog(
                string.Format(_host.Loc("Str_About_UpdateHeading"), AppInfo.DisplayName, tag),
                _host.Loc("Str_About_UpdateDetail"),
                _host.Loc("Str_About_UpdateNow"),
                _host.Loc("Str_Btn_Cancel")) { Owner = _host.Window };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;
            await InstallAsync(tag!);
        }

        private async Task InstallAsync(string tag)
        {
            if (_updating) return;
            bool hold = false;
            HoldUpdate(ref hold);
            if (hold) return;
            _updating = true;
            _host.UpdateEnabled = false;
            _host.UpdateText    = _host.Loc("Str_About_Downloading");

            string? newExe;
            try
            {
                newExe = await UpdateService.DownloadAsync(tag!);
            }
            catch (UpdateVerificationException ex)
            {
                // The download arrived but was refused, so the releases page is no remedy: say so.
                _updating           = false;
                _host.UpdateEnabled = true;
                _host.UpdateText    = string.Format(_host.Loc("Str_About_UpdateAvailable"), tag);
                UpdateNotVerified(ex);
                return;
            }
            catch
            {
                _updating           = false;
                _host.UpdateEnabled = true;
                _host.UpdateText    = string.Format(_host.Loc("Str_About_UpdateAvailable"), tag);
                WebLink.Open(UpdateService.ReleasesUrl);
                return;
            }

            bool held = false, proceed = true, handoff = true;
            HoldUpdate(ref held);
            if (!held) ConfirmUpdateShutdown(ref proceed);
            if (!held && proceed)
            {
                BeforeUpdateHandoff(ref handoff);
                if (!handoff) UpdateShutdownCanceled();
            }
            if (held || !proceed || !handoff)
            {
                UpdateService.DiscardDownload(newExe);
                _updating           = false;
                _host.UpdateEnabled = true;
                _host.UpdateText    = string.Format(_host.Loc("Str_About_UpdateAvailable"), tag);
                return;
            }

            // Declining UAC throws, so the shutdown only happens once the helper is actually
            // running - otherwise the app would close without updating.
            // Off the UI thread: the machine path waits while the elevated helper hashes, stages and
            // verifies the release, which takes seconds.
            string version = tag!.TrimStart('v', 'V');
            try { await System.Threading.Tasks.Task.Run(() => UpdateService.StartSwap(newExe, version)); }
            catch (Exception ex)
            {
                AfterUpdateHandoff(false);
                UpdateShutdownCanceled();
                // A failed helper launch retains the verified download for recovery.
                _updating           = false;
                _host.UpdateEnabled = true;
                _host.UpdateText    = string.Format(_host.Loc("Str_About_UpdateAvailable"), tag);
                UpdateHandoffFailed(ex);
                return;
            }
            // From here the helper is waiting for this process to exit and will replace its exe,
            // so a shutdown that fails leaves Update off: offering it again would start a second
            // update over the first.
            AfterUpdateHandoff(true);
            try
            {
                bool handled = false;
                ShutdownForUpdate(ref handled);
                if (!handled) System.Windows.Application.Current.Shutdown();
            }
            catch { /* The helper finishes once the app does close. */ }
        }

        /// <summary>Tells the user a downloaded update was refused by its checksum or signature. Each app
        /// implements it in its own AboutController.Extras.cs; an app that does not still compiles.</summary>
        partial void UpdateNotVerified(Exception failure);

        /// <summary>Tells the user the update could not start, which a declined UAC prompt also lands on.
        /// Each app implements it in its own AboutController.Extras.cs; an app that does not still compiles.</summary>
        partial void UpdateHandoffFailed(Exception failure);
    }
}
