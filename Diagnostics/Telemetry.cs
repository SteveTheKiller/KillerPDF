using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
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
        /// Emit a single numeric measurement (e.g. page count, file
        /// size in KB, render milliseconds). No-op when disabled.
        /// </summary>
        public static void TrackMetric(string name, double value, IDictionary<string, string>? properties = null)
        {
            if (!IsEnabled || s_client is null) return;
            try
            {
                var mt = new MetricTelemetry(name, value);
                IDictionary<string, string>? scrubbed = ScrubProperties(properties);
                if (scrubbed is not null)
                    foreach (var kv in scrubbed)
                        mt.Properties[kv.Key] = kv.Value;
                s_client.TrackMetric(mt);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Lightweight structured log line. The message is scrubbed via
        /// <see cref="Sanitizer"/> before it leaves the process, but
        /// callers should still avoid embedding file paths or document
        /// content. No-op when disabled.
        /// </summary>
        public static void TrackTrace(string message, SeverityLevel severity = SeverityLevel.Information, IDictionary<string, string>? properties = null)
        {
            if (!IsEnabled || s_client is null) return;
            try
            {
                var tt = new TraceTelemetry(Sanitizer.Scrub(message), severity);
                IDictionary<string, string>? scrubbed = ScrubProperties(properties);
                if (scrubbed is not null)
                    foreach (var kv in scrubbed)
                        tt.Properties[kv.Key] = kv.Value;
                s_client.TrackTrace(tt);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Emit a completed-operation event carrying a duration metric
        /// and a success flag. Prefer <see cref="StartOperation"/> for
        /// the common "time a using-block" pattern.
        /// </summary>
        public static void TrackOperation(string name, double durationMs, bool success, IDictionary<string, string>? properties = null)
        {
            if (!IsEnabled || s_client is null) return;
            try
            {
                var props = new Dictionary<string, string>
                {
                    ["Success"]    = success ? "true" : "false",
                    ["DurationMs"] = ((long)durationMs).ToString(),
                };
                IDictionary<string, string>? scrubbed = ScrubProperties(properties);
                if (scrubbed is not null)
                    foreach (var kv in scrubbed)
                        props[kv.Key] = kv.Value;

                var metrics = new Dictionary<string, double> { ["DurationMs"] = durationMs };
                s_client.TrackEvent("Op." + name, props, metrics);
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Begin timing an operation. Dispose the returned scope (ideally
        /// via <c>using</c>) to emit an <c>Op.&lt;name&gt;</c> event with
        /// the elapsed duration. Call <see cref="Operation.Fail"/> to mark
        /// it unsuccessful. Always returns a usable object even when
        /// telemetry is disabled (the emit is then a no-op), so callers
        /// never have to null-check.
        /// </summary>
        public static Operation StartOperation(string name) => new(name);

        /// <summary>
        /// Disposable stopwatch scope produced by <see cref="StartOperation"/>.
        /// Cheap to allocate; safe to use whether or not telemetry is on.
        /// </summary>
        public sealed class Operation : IDisposable
        {
            private readonly string _name;
            private readonly Stopwatch _stopwatch;
            private readonly Dictionary<string, string> _properties = new();
            private bool _success = true;
            private bool _completed;

            internal Operation(string name)
            {
                _name = name;
                _stopwatch = Stopwatch.StartNew();
            }

            public Operation With(string key, string value)
            {
                _properties[key] = value;
                return this;
            }

            public void Fail(Exception? exception = null)
            {
                _success = false;
                if (exception is not null)
                    _properties["ExceptionType"] = exception.GetType().FullName ?? "Unknown";
            }

            public void Dispose()
            {
                if (_completed) return;
                _completed = true;
                _stopwatch.Stop();
                TrackOperation(_name, _stopwatch.Elapsed.TotalMilliseconds, _success, _properties);
            }
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
