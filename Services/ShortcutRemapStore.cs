namespace KillerPDF
{
    /// <summary>
    /// The user's custom shortcuts as one app-wide value. Every window reads the same map and
    /// is told when it changes, so a key moved in one window is moved in all of them. Free of WPF
    /// so the tests can link it.
    /// </summary>
    internal sealed class ShortcutRemapStore(Func<string?> read, Action<string> write)
    {
        private ShortcutRemap? _current;

        /// <summary>The map every window translates keys with. Read from the setting once.</summary>
        internal ShortcutRemap Current => _current ??= ShortcutRemap.Parse(read());

        /// <summary>Raised after a new map is saved.</summary>
        internal event Action? Changed;

        /// <summary>Keeps <paramref name="remap"/> as the current map and writes it to the setting.</summary>
        internal void Save(ShortcutRemap remap)
        {
            _current = remap;
            write(remap.Serialize());
            Changed?.Invoke();
        }
    }
}
