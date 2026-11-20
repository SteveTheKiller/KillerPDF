namespace KillerPDF.Services
{
    // Where this process is running KillerPDF from. Only the windowed app talks to the person at
    // startup; every other surface stays silent.
    internal enum LaunchSurface
    {
        Window,
        CommandLine,
        PreviewHandler,
        StartupSmoke
    }

    // #509: KillerPDF is built on .NET 10 and draws its icons with Segoe MDL2 Assets, and both
    // need Windows 10 or later. On Windows 7, 8 and 8.1 the icons show as empty boxes, so the
    // windowed app says once per launch that this version of Windows is not supported.
    internal static class LegacyWindowsNotice
    {
        // Windows 10 1507 is the first release that ships Segoe MDL2 Assets.
        internal static readonly Version MinimumWindows = new(10, 0);

        internal static bool ShouldShow(Version osVersion, LaunchSurface surface)
            => surface == LaunchSurface.Window && osVersion < MinimumWindows;

        // A torn-out tab opens another main window in this process; the notice belongs to the
        // launch, so only the first window that asks gets it.
        private static int _claimed;

        internal static bool TryClaim(Version osVersion, LaunchSurface surface)
            => ShouldShow(osVersion, surface) && Interlocked.Exchange(ref _claimed, 1) == 0;
    }
}
