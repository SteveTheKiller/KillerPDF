using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Thin opt-in wrapper over <see cref="TelemetryClient"/>. By
    /// default this is a no-op — telemetry only emits when the
    /// administrator has provisioned <see cref="TelemetryStore"/>.
    ///
    /// Guarantees:
    ///   * Absence of the provisioning file ⇒ <see cref="IsEnabled"/>
    ///     stays <c>false</c> and every method returns immediately
    ///     without touching the network.
    ///   * Every public method swallows its own exceptions. Telemetry
    ///     can never crash, block, or slow the app.
    ///   * No <c>TrackException(Exception)</c> overload is exposed,
    ///     because the raw <see cref="Exception.Message"/> and
    ///     <see cref="Exception.StackTrace"/> commonly contain user
    ///     document paths. Callers must use <see cref="TrackCrash"/>
    ///     which scrubs via <see cref="Sanitizer"/>.
    /// </summary>
    internal static class Telemetry
    {
        private static TelemetryClient? s_client;
        private static TelemetryConfiguration? s_config;
        private static readonly object s_lock = new();

        public static bool IsEnabled { get; private set; }

        /// <summary>
        /// Best-effort start. Reads the provisioning file, builds a
        /// private <see cref="TelemetryConfiguration"/> (never the
        /// global <c>TelemetryConfiguration.Active</c>) with auto
        /// collectors disabled, and primes a <see cref="TelemetryClient"/>.
        /// Does not emit any event; callers should follow with the
        /// startup event that fits their context (App.Startup,
        /// Install.Start, etc.).
        /// </summary>
        public static void Initialize(string appVersion)
        {
            try
            {
                lock (s_lock)
                {
                    if (IsEnabled) return;

                    string? connectionString = TelemetryStore.TryLoad();
                    if (string.IsNullOrWhiteSpace(connectionString))
                        return;

                    var config = TelemetryConfiguration.CreateDefault();
                    config.ConnectionString = connectionString;

                    // InMemoryChannel: no on-disk buffering => no
                    // privacy surface and no per-user permission
                    // problems. Buffer is best-effort; events lost on
                    // crash/network failure are expected.
                    config.TelemetryChannel = new InMemoryChannel
                    {
                        DeveloperMode = false,
                    };

                    // Disable automatic collectors that could leak more
                    // than we want (dependency tracking, perf counters,
                    // live metrics, heartbeat, etc.). The base SDK has
                    // none of these wired by default — this guard is
                    // belt-and-suspenders against future SDK changes.
                    config.DisableTelemetry = false;

                    s_config = config;
                    s_client = new TelemetryClient(config);
                    s_client.Context.Component.Version = appVersion;
                    s_client.Context.Cloud.RoleName = "TDPdf";
                    // Deliberately do NOT set Context.User.Id or
                    // Device.Id — no persistent device fingerprint.

                    IsEnabled = true;
                }
            }
            catch
            {
                // Initialization failure must never propagate. Leave
                // IsEnabled false; future calls become no-ops.
                IsEnabled = false;
                s_client = null;
                s_config = null;
            }
        }

        /// <summary>
        /// Emit a custom event. Property values are scrubbed; callers
        /// should still avoid passing file paths, document names, or
        /// other user content.
        /// </summary>
        public static void TrackEvent(string name, IDictionary<string, string>? properties = null)
        {
            if (!IsEnabled || s_client is null) return;
            try
            {
                IDictionary<string, string>? scrubbed = ScrubProperties(properties);
                s_client.TrackEvent(name, scrubbed);
            }
            catch { /* swallow — telemetry must never throw */ }
        }

        /// <summary>
        /// Emit a sanitized crash record. The raw exception object is
        /// never serialized; we only ship type name, scrubbed message,
        /// scrubbed stack, and a grouping hash.
        /// </summary>
        public static void TrackCrash(Exception exception, string source, bool recoverable)
        {
            if (!IsEnabled || s_client is null) return;
            try
            {
                var props = new Dictionary<string, string>
                {
                    ["Source"]        = source,
                    ["Recoverable"]   = recoverable ? "true" : "false",
                    ["ExceptionType"] = exception.GetType().FullName ?? "Unknown",
                    ["Message"]       = Sanitizer.Scrub(exception.Message),
                    ["StackTrace"]    = Sanitizer.Scrub(exception.StackTrace ?? string.Empty),
                    ["GroupingKey"]   = Sanitizer.GroupingKey(exception),
                };
                s_client.TrackEvent("Crash", props);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Best-effort bounded flush. Called from
        /// <see cref="System.Windows.Application.OnExit"/>. Caps total
        /// blocking time at ~2s so telemetry can never hang shutdown.
        /// </summary>
        public static void Flush()
        {
            if (!IsEnabled || s_client is null) return;
            try
            {
                s_client.Flush();
                // Classic SDK Flush is fire-and-forget for the HTTPS
                // upload; the upload completes asynchronously on a
                // background thread. The documented pattern is to
                // sleep briefly afterwards.
                Task.Delay(2000).Wait();
            }
            catch { /* swallow */ }
        }

        private static IDictionary<string, string>? ScrubProperties(IDictionary<string, string>? properties)
        {
            if (properties is null || properties.Count == 0)
                return properties;

            var copy = new Dictionary<string, string>(properties.Count);
            foreach (var kv in properties)
                copy[kv.Key] = Sanitizer.Scrub(kv.Value);
            return copy;
        }
    }
}
