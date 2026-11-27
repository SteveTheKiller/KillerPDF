using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using KillerPDF.Services;
using KillerPDF.Shell;

namespace KillerPDF
{
    // #502: keeps the stored session (Services/SessionStore.cs) in step with every window's tabs. A
    // tab that opens, closes or comes to the front schedules one write a moment later, so a burst of
    // changes costs one file replace and a crash or reboot still leaves a recent copy.
    public partial class App
    {
        private static readonly TimeSpan SessionSaveDelay = TimeSpan.FromMilliseconds(800);
        private static DispatcherTimer? _sessionSaveTimer;

        // Windows closed earlier in this run, kept as they were so the session they belonged to
        // still comes back whole, the way the older restore kept their files.
        private static readonly List<SessionWindow> ClosedSessionWindows = [];

        /// <summary>True until the launch has decided what to do with the stored session. While the
        /// restore bar waits for an answer the empty window must not overwrite the list it offers.</summary>
        internal static bool SessionWritesHeld { get; private set; } = true;

        internal static SessionRestoreMode SessionRestoreMode
            => SessionStore.ParseMode(GetSetting(SessionStore.ModeSetting));

        internal static void SetSessionRestoreMode(SessionRestoreMode mode)
        {
            SetSetting(SessionStore.ModeSetting, SessionStore.ModeValue(mode));
            if (mode == SessionRestoreMode.Never)
            {
                _sessionSaveTimer?.Stop();
                ClosedSessionWindows.Clear();
            }
            SessionStore.ApplyMode(mode, SessionStore.DefaultFile, RemoveSetting);
            if (mode != SessionRestoreMode.Never) ScheduleSessionSave();
        }

        internal static void ReleaseSessionWrites() => SessionWritesHeld = false;

        /// <summary>The session a launch may restore: the store, or for an upgrade that has never
        /// written one, the older settings keys.</summary>
        internal static SessionSnapshot? LoadStoredSession()
        {
            string file = SessionStore.DefaultFile;
            if (File.Exists(file)) return SessionStore.Load(file);
            SessionSnapshot legacy = SessionStore.FromLegacy(GetSetting);
            return legacy.Windows.Count > 0 ? legacy : null;
        }

        /// <summary>Every file the stored session lists, or null when it cannot be read.</summary>
        internal static IEnumerable<string>? StoredSessionFiles()
        {
            try
            {
                return LoadStoredSession()?.Windows.SelectMany(window => window.Panes)
                    .SelectMany(pane => pane.Tabs).Select(tab => tab.Path).ToList() ?? [];
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        internal static void ScheduleSessionSave()
        {
            if (IsStartupSmoke || SessionWritesHeld || Current is null) return;
            if (_sessionSaveTimer is null)
            {
                _sessionSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Current.Dispatcher)
                { Interval = SessionSaveDelay };
                _sessionSaveTimer.Tick += (_, _) => SaveSessionNow();
            }
            _sessionSaveTimer.Stop();
            _sessionSaveTimer.Start();
        }

        /// <summary>Writes every window's tabs now. Skipped while the launch decision is pending,
        /// with Never chosen, and when the quit prompt was told to close the open tabs.</summary>
        internal static void SaveSessionNow()
        {
            _sessionSaveTimer?.Stop();
            if (IsStartupSmoke || SessionWritesHeld || Current is null) return;
            if (SessionRestoreMode == SessionRestoreMode.Never || GetSetting("RememberOpenFiles") == "0") return;
            var windows = Current.Windows.OfType<MainWindow>()
                .Select(window => window.SessionWindowForStore())
                .OfType<SessionWindow>()
                .Concat(ClosedSessionWindows)
                .Where(window => window.Panes.Count > 0)
                .ToList();
            try { SessionStore.Save(SessionStore.DefaultFile, new SessionSnapshot(windows)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Best effort like the other settings: the next change or the exit writes again.
            }
        }

        private static void RememberClosedSessionWindow(MainWindow window)
        {
            if (window.TakeSessionWindowForClose() is { Panes.Count: > 0 } closed
                && GetSetting("RememberOpenFiles") != "0")
                ClosedSessionWindows.Add(closed);
            ScheduleSessionSave();
        }

        private static void ForgetClosedSessionWindows() => ClosedSessionWindows.Clear();
    }
}
