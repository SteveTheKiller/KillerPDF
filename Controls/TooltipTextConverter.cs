#nullable enable
using System;
using System.Globalization;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KillerFamily.Layout
{
    /// <summary>Presentation preference shared by menu gestures and hover hints. Key bindings stay active.</summary>
    public sealed class ShortcutHints : INotifyPropertyChanged
    {
        public const string SettingName = "ShowShortcutsInMenus";
        public static ShortcutHints Current { get; } = new ShortcutHints();
        private bool _enabled = true;
        private Action<string, string>? _save;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                _save?.Invoke(SettingName, value ? "1" : "0");
                Notify();
            }
        }

        public Visibility MenuVisibility => Enabled ? Visibility.Visible : Visibility.Collapsed;

        public static void Initialize(Func<string, string?> read, Action<string, string> save)
        {
            Current._enabled = read(SettingName) != "0";
            Current._save = save;
            Current.Notify();
        }

        private void Notify()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MenuVisibility)));
        }
    }

    /// <summary>
    /// Keeps the family hover popup compact. A string tooltip is shown through Convert, which moves a
    /// trailing shortcut such as "(Ctrl+S)" onto its own line, and WrapContent makes the text blocks
    /// inside a structured tooltip wrap at 300 px instead of running across the screen.
    /// </summary>
    public sealed class TooltipTextConverter : IValueConverter, IMultiValueConverter
    {
        public static readonly DependencyProperty WrapContentProperty = DependencyProperty.RegisterAttached(
            "WrapContent", typeof(bool), typeof(TooltipTextConverter), new PropertyMetadata(false, OnWrapContentChanged));

        public static bool GetWrapContent(DependencyObject element) => (bool)element.GetValue(WrapContentProperty);
        public static void SetWrapContent(DependencyObject element, bool value) => element.SetValue(WrapContentProperty, value);

        private static void OnWrapContentChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
        {
            if (element is not ToolTip tip || e.NewValue is not true) return;
            var restore = new List<Action>();
            void Restore() { foreach (var undo in restore) undo(); restore.Clear(); }
            void Wrap() { if (tip.Content is DependencyObject content) WrapText(content, null); }
            void Refresh()
            {
                Restore();
                if (tip.Content is DependencyObject content) WrapText(content, restore);
            }
            void Changed(object? sender, PropertyChangedEventArgs args)
            { if (args.PropertyName == nameof(ShortcutHints.Enabled)) Refresh(); }
            Wrap();
            tip.Loaded += (_, _) => Wrap();
            tip.Opened += (_, _) =>
            {
                Refresh();
                ShortcutHints.Current.PropertyChanged += Changed;
            };
            tip.Closed += (_, _) =>
            {
                ShortcutHints.Current.PropertyChanged -= Changed;
                Restore();
            };
        }

        private static void WrapText(DependencyObject content, List<Action>? restore)
        {
            if (content is TextBlock text)
            {
                text.MaxWidth = 300;
                text.TextWrapping = TextWrapping.Wrap;
                if (restore != null && !ShortcutHints.Current.Enabled)
                {
                    string original = text.Text;
                    string display = Format(original, false);
                    if (display != original)
                    {
                        Visibility visibility = text.Visibility;
                        text.SetCurrentValue(TextBlock.TextProperty, display);
                        if (display.Length == 0)
                            text.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                        restore.Add(() =>
                        {
                            if (text.Text == display) text.SetCurrentValue(TextBlock.TextProperty, original);
                            text.SetCurrentValue(UIElement.VisibilityProperty, visibility);
                        });
                    }
                }
            }
            foreach (object child in LogicalTreeHelper.GetChildren(content))
                if (child is DependencyObject node) WrapText(node, restore);
        }

        private static readonly Regex ShortcutSuffix = new(@"\s+\((?=[^()]*\b(?:Ctrl|Alt|Shift|F\d{1,2}|Esc|Enter|Space|Tab|Delete|Backspace)\b)([^()]*)\)$");
        private static readonly Regex ShortcutLine = new(@"^\s*(?:(?:(?:Ctrl|Alt|Shift|Win|Cmd)\s*\+\s*)+\S+|F\d{1,2}|Esc|Enter|Space|Tab|Delete|Backspace)(?:\s*(?:,|/|or)\s*\S+)*\s*$");
        private static readonly Regex UnbracketedSuffix = new(@"\s{2,}(?=(?:Ctrl|Alt|Shift|Win|Cmd)\+|F\d{1,2}\b|Enter\b|Esc\b|Delete\b).*$");
        private static readonly Regex ParenthesizedHints = new(@"\s*\((?=[^()]*\b(?:Ctrl|Alt|Shift|F\d{1,2}|Esc|Enter|Space|Tab|Delete|Backspace)\b)[^()]*\)");
        private static readonly Regex ExtraHintClause = new(@"\s*\|\s*(?=(?:Ctrl|Alt|Shift|Win|Cmd)\+|F\d{1,2}\b).*$");

        private static string Format(string text, bool show)
        {
            if (show) return ShortcutSuffix.Replace(text, "\n$1");
            string withoutSuffix = UnbracketedSuffix.Replace(ExtraHintClause.Replace(ParenthesizedHints.Replace(text, ""), ""), "");
            string kept = string.Join("\n", withoutSuffix.Replace("\r\n", "\n").Split('\n').Where(line => !ShortcutLine.IsMatch(line))).TrimEnd();
            // A label that is itself a key name ("Delete (Enter)") would be filtered away with its hint.
            // Only text that carried a hint falls back; a bare shortcut row is still dropped.
            return kept.Length == 0 && withoutSuffix.Trim().Length > 0 && withoutSuffix.Trim() != text.Trim()
                ? withoutSuffix.TrimEnd() : kept;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string text ? Format(text, parameter is bool show ? show : ShortcutHints.Current.Enabled) : value;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => values.Length > 0 ? Convert(values[0], targetType,
                values.Length > 1 && values[1] is bool show ? show : ShortcutHints.Current.Enabled, culture) : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
