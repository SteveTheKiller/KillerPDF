#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerFamily.Accessibility
{
    internal static class AccessibilityAudit
    {
        // Explicit local diagnostic output only. Normal startup does not collect a control report.
        internal static void Record(Window window)
        {
            string? folder = Environment.GetEnvironmentVariable("KILLER_ACCESSIBILITY_REPORT");
            if (string.IsNullOrWhiteSpace(folder)) return;
            var lines = new List<string>
            {
                "Type\tId\tName\tKeyboardFocusable\tEnabled\tVisible\tFocusVisual\tPatterns",
            };
            Visit(window, lines);
            // A diagnostic report must never take the window down: a bad folder is skipped.
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, window.GetType().Assembly.GetName().Name + "-" +
                    window.GetType().Name + "-" + Guid.NewGuid().ToString("N") + ".tsv");
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                foreach (string line in lines) writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
            }
        }

        private static void Visit(DependencyObject element, List<string> lines)
        {
            if (element is FrameworkElement control && control.IsVisible && (control is Control || control.Focusable))
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(control);
                if (peer == null)
                    lines.Add(string.Join("\t", [control.GetType().Name, Clean(control.Name),
                        Clean(AutomationProperties.GetName(control)), control.Focusable.ToString(),
                        control.IsEnabled.ToString(), control.IsVisible.ToString(),
                        (control.FocusVisualStyle != null).ToString(), "MISSING_PEER"]));
                if (peer != null && peer.IsControlElement())
                {
                    string id = AutomationProperties.GetAutomationId(control);
                    if (string.IsNullOrWhiteSpace(id)) id = control.Name;
                    var patterns = new List<string>();
                    foreach (var pattern in new[] { PatternInterface.Invoke, PatternInterface.Toggle,
                        PatternInterface.Value, PatternInterface.RangeValue, PatternInterface.Selection,
                        PatternInterface.SelectionItem, PatternInterface.ExpandCollapse, PatternInterface.Text })
                        if (peer.GetPattern(pattern) != null) patterns.Add(pattern.ToString());
                    lines.Add(string.Join("\t", [peer.GetAutomationControlType().ToString(), Clean(id),
                        Clean(peer.GetName()), peer.IsKeyboardFocusable().ToString(), peer.IsEnabled().ToString(),
                        control.IsVisible.ToString(), (control.FocusVisualStyle != null).ToString(),
                        string.Join(",", patterns)]));
                }
            }
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int index = 0; index < count; index++) Visit(VisualTreeHelper.GetChild(element, index), lines);
        }

        private static string Clean(string value) => value.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
    }
}
