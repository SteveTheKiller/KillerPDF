using System.Configuration;

namespace TDPdf.Properties
{
    internal sealed class Settings : ApplicationSettingsBase
    {
        private static readonly Settings defaultInstance = (Settings)Synchronized(new Settings());

        public static Settings Default => defaultInstance;

        [UserScopedSetting]
        [DefaultSettingValue("System")]
        public string Theme
        {
            get => (string)this[nameof(Theme)];
            set => this[nameof(Theme)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool UseNativeWindowFrame
        {
            get => (bool)this[nameof(UseNativeWindowFrame)];
            set => this[nameof(UseNativeWindowFrame)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("1")]
        public double LastZoomLevel
        {
            get => (double)this[nameof(LastZoomLevel)];
            set => this[nameof(LastZoomLevel)] = value;
        }

        // When true (default), launching TDPdf while it is already running
        // (e.g. double-clicking another PDF in Explorer) forwards the file to
        // the existing window as a new tab instead of opening a second window.
        [UserScopedSetting]
        [DefaultSettingValue("True")]
        public bool SingleInstanceTabs
        {
            get => (bool)this[nameof(SingleInstanceTabs)];
            set => this[nameof(SingleInstanceTabs)] = value;
        }
    }
}
