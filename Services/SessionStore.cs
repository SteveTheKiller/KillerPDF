using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPDF.Services;

/// <summary>How a plain launch treats the stored session (#502). Ask is the default.</summary>
internal enum SessionRestoreMode { Ask, Always, Never }

/// <summary>What a launch does with the stored session.</summary>
internal enum SessionRestoreAction { None, Silent, Ask }

internal sealed record SessionTab(string Path, int Page, double Zoom);

/// <summary>One pane's tabs in strip order, and the index of its front tab.</summary>
internal sealed record SessionPane(IReadOnlyList<SessionTab> Tabs, int Active);

/// <summary>One window: pane A, then pane B when the window was split.</summary>
internal sealed record SessionWindow(IReadOnlyList<SessionPane> Panes);

internal sealed record SessionSnapshot(IReadOnlyList<SessionWindow> Windows)
{
    [JsonIgnore]
    internal int FileCount => Windows.Sum(window => window.Panes.Sum(pane => pane.Tabs.Count));
}

/// <summary>
/// #502: the open tabs of every window, kept in a file beside the settings so a crash or reboot still
/// leaves a recent copy. Paths only: unsaved edits are never written here. Each write replaces the
/// whole file atomically, so a reader sees the old session or the new one and never half of either.
/// </summary>
internal static class SessionStore
{
    internal const string ModeSetting = "RestoreSession";
    internal const string FileName = "session.json";
    internal static string DefaultFile => Path.Combine(AppDataPaths.UserRoot, FileName);

    // The keys the session used to live in, read once by an upgrade and cleared by Never.
    private static readonly string[] LegacyKeys = ["OpenTabs", "ActiveTab", "OpenTabsB", "ActiveTabB", "LastFile"];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static SessionRestoreMode ParseMode(string? value) => value switch
    {
        "always" => SessionRestoreMode.Always,
        "never" => SessionRestoreMode.Never,
        _ => SessionRestoreMode.Ask
    };

    internal static string ModeValue(SessionRestoreMode mode) => mode switch
    {
        SessionRestoreMode.Always => "always",
        SessionRestoreMode.Never => "never",
        _ => "ask"
    };

    /// <summary>A launch that names a document (a double-clicked PDF or a killerpdf: handoff) opens
    /// that document and leaves the stored session alone. A restart after a render failure restores
    /// without asking, since it replaces the window that was just showing those tabs.</summary>
    internal static SessionRestoreAction Decide(SessionRestoreMode mode, IReadOnlyList<string> args,
        Func<string, bool> fileExists, bool forced, int restorableFiles)
    {
        if (restorableFiles <= 0) return SessionRestoreAction.None;
        if (forced) return SessionRestoreAction.Silent;
        for (int i = 1; i < args.Count; i++)
        {
            string argument = args[i];
            if (argument.StartsWith("killerpdf:", StringComparison.OrdinalIgnoreCase)) return SessionRestoreAction.None;
            if (!argument.StartsWith('-') && !argument.StartsWith('/') && fileExists(argument)) return SessionRestoreAction.None;
        }
        return mode switch
        {
            SessionRestoreMode.Always => SessionRestoreAction.Silent,
            SessionRestoreMode.Ask => SessionRestoreAction.Ask,
            _ => SessionRestoreAction.None
        };
    }

    /// <summary>Never forgets the stored session, file and older settings keys alike, so a later
    /// switch back to Ask or Always cannot bring back a stale list.</summary>
    internal static void ApplyMode(SessionRestoreMode mode, string file, Action<string> removeSetting)
    {
        if (mode != SessionRestoreMode.Never) return;
        Delete(file);
        foreach (string key in LegacyKeys) removeSetting(key);
    }

    internal static void Save(string file, SessionSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        AtomicFileOutput.Write(file, JsonSerializer.SerializeToUtf8Bytes(snapshot, Json));
    }

    /// <summary>The stored session, or null when there is none or it cannot be read.</summary>
    internal static SessionSnapshot? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllBytes(file), Json);
            return snapshot?.Windows is null ? null : Clean(snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    internal static void Delete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // A hand-edited or partly empty file must not hand nulls to the restore.
    private static SessionSnapshot Clean(SessionSnapshot snapshot) => new([..
        snapshot.Windows.Where(window => window?.Panes is not null).Select(window => new SessionWindow([..
            window.Panes.Where(pane => pane?.Tabs is not null).Select(pane =>
            {
                SessionTab[] tabs = [.. pane.Tabs.Where(tab => tab is not null && !string.IsNullOrWhiteSpace(tab.Path))
                    .Select(tab => tab with { Page = Math.Max(0, tab.Page), Zoom = tab.Zoom > 0 && double.IsFinite(tab.Zoom) ? tab.Zoom : 1 })];
                return new SessionPane(tabs, Math.Clamp(pane.Active, 0, Math.Max(0, tabs.Length - 1)));
            })]))]);

    /// <summary>The session without the files that are no longer on disk, and those files' paths,
    /// each named once. Windows and panes left empty are dropped. A pane whose front tab went
    /// missing fronts the tab before it.</summary>
    internal static (SessionSnapshot Present, IReadOnlyList<string> Missing) WithoutMissing(
        SessionSnapshot snapshot, Func<string, bool> fileExists)
    {
        var missing = new List<string>();
        var windows = new List<SessionWindow>();
        foreach (SessionWindow window in snapshot.Windows)
        {
            var panes = new List<SessionPane>();
            foreach (SessionPane pane in window.Panes)
            {
                SessionPane kept = Keep(pane, tab => fileExists(tab.Path), tab =>
                {
                    if (!missing.Contains(tab.Path, StringComparer.OrdinalIgnoreCase)) missing.Add(tab.Path);
                });
                panes.Add(kept);
            }
            // An empty pane B is dropped; an empty pane A hands its place to B.
            while (panes.Count > 0 && panes[^1].Tabs.Count == 0) panes.RemoveAt(panes.Count - 1);
            if (panes.Count > 0 && panes[0].Tabs.Count > 0) windows.Add(new SessionWindow(panes));
            else if (panes.Count > 1) windows.Add(new SessionWindow([panes[1]]));
        }
        return (new SessionSnapshot(windows), missing);
    }

    /// <summary>The stored session without the given files, written back in place. Used once
    /// recovered copies of those files reopen from a render recovery manifest.</summary>
    internal static void RemoveFiles(string file, IEnumerable<string> paths)
    {
        if (Load(file) is not { } snapshot) return;
        var drop = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        (SessionSnapshot kept, IReadOnlyList<string> removed) = WithoutMissing(snapshot, path => !drop.Contains(path));
        if (removed.Count > 0) Save(file, kept);
    }

    private static SessionPane Keep(SessionPane pane, Func<SessionTab, bool> keep, Action<SessionTab> dropped)
    {
        var tabs = new List<SessionTab>();
        int active = 0;
        for (int i = 0; i < pane.Tabs.Count; i++)
        {
            SessionTab tab = pane.Tabs[i];
            if (keep(tab))
            {
                if (i <= pane.Active) active = tabs.Count;
                tabs.Add(tab);
            }
            else dropped(tab);
        }
        return new SessionPane(tabs, tabs.Count == 0 ? 0 : Math.Min(active, tabs.Count - 1));
    }

    /// <summary>A pane's tabs as they are worth restoring: real files on disk only, each once.
    /// Untitled tabs, imports still living in a working temp file and files deleted since they
    /// opened are left out.</summary>
    internal static SessionPane PaneFrom(IEnumerable<(string? Path, int Page, double Zoom)> tabs,
        string? activePath, Func<string, bool> isTemporary)
    {
        var kept = new List<SessionTab>();
        foreach ((string? path, int page, double zoom) in tabs)
        {
            if (string.IsNullOrWhiteSpace(path) || isTemporary(path) || !File.Exists(path)) continue;
            if (kept.Exists(tab => string.Equals(tab.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            kept.Add(new SessionTab(path, Math.Max(0, page), zoom > 0 && double.IsFinite(zoom) ? zoom : 1));
        }
        int active = activePath is null ? -1
            : kept.FindIndex(tab => string.Equals(tab.Path, activePath, StringComparison.OrdinalIgnoreCase));
        return new SessionPane(kept, Math.Max(0, active));
    }

    /// <summary>The window a launch restores. KillerPDF restores into the one window it starts with:
    /// a single window keeps its split, and several windows come back as one window's tabs, in
    /// window order with each file once, fronting the first window's front tab.</summary>
    internal static SessionWindow IntoOneWindow(SessionSnapshot snapshot)
    {
        if (snapshot.Windows.Count == 0) return new SessionWindow([]);
        if (snapshot.Windows.Count == 1) return snapshot.Windows[0];
        var tabs = new List<SessionTab>();
        foreach (SessionTab tab in snapshot.Windows.SelectMany(window => window.Panes).SelectMany(pane => pane.Tabs))
            if (!tabs.Exists(kept => string.Equals(kept.Path, tab.Path, StringComparison.OrdinalIgnoreCase)))
                tabs.Add(tab);
        SessionPane firstPane = snapshot.Windows[0].Panes[0];
        string? front = firstPane.Tabs.Count > firstPane.Active ? firstPane.Tabs[firstPane.Active].Path : null;
        int active = front is null ? 0 : tabs.FindIndex(tab => string.Equals(tab.Path, front, StringComparison.OrdinalIgnoreCase));
        return new SessionWindow([new SessionPane(tabs, Math.Max(0, active))]);
    }

    /// <summary>One stored pane as tabs made by <paramref name="deferred"/>, which is handed only the
    /// path: no restored file is opened here, so the launch stays fast and a password-protected file
    /// asks for its password when its tab is first viewed. <paramref name="seed"/> copies each tab's
    /// page and zoom across. Returns the tabs in order and the one to front.</summary>
    internal static (List<T> Tabs, T? Active) Build<T>(SessionPane pane, Func<string, T> deferred,
        Action<T, SessionTab> seed) where T : class
    {
        var tabs = new List<T>();
        foreach (SessionTab tab in pane.Tabs)
        {
            T made = deferred(tab.Path);
            seed(made, tab);
            tabs.Add(made);
        }
        return (tabs, tabs.Count == 0 ? null : tabs[Math.Clamp(pane.Active, 0, tabs.Count - 1)]);
    }

    /// <summary>The session as settings written before this store existed describe it: pane A in
    /// OpenTabs (or the single LastFile), pane B in OpenTabsB, each with its front tab.</summary>
    internal static SessionSnapshot FromLegacy(Func<string, string?> read)
    {
        static SessionPane Pane(string? list, string? active)
        {
            SessionTab[] tabs = [.. (list ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(path => new SessionTab(path, 0, 1))];
            int index = Array.FindIndex(tabs, tab => string.Equals(tab.Path, active, StringComparison.OrdinalIgnoreCase));
            return new SessionPane(tabs, Math.Max(0, index));
        }

        string? openTabs = read("OpenTabs");
        if (string.IsNullOrEmpty(openTabs)) openTabs = read("LastFile");
        SessionPane a = Pane(openTabs, read("ActiveTab"));
        SessionPane b = Pane(read("OpenTabsB"), read("ActiveTabB"));
        if (a.Tabs.Count == 0 && b.Tabs.Count == 0) return new SessionSnapshot([]);
        return new SessionSnapshot([new SessionWindow(b.Tabs.Count > 0 ? [a, b] : [a])]);
    }
}
