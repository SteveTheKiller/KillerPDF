#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerFamily.Controls
{
    /// <summary>
    /// A row of color swatches for a card menu (KillerNotes 1.7.0 is the reference): a "no color"
    /// outline first, then the app's saved swatches, then a button that opens the full picker.
    /// It owns no data. The app calls <see cref="Show"/> with its palette and the current value,
    /// and handles <see cref="ColorChosen"/> and <see cref="PickRequested"/>; the menu closing is
    /// the app's call too, because only it knows whether a live preview is still running.
    /// Swatches use the CardSwatch style and the picker uses MenuRowButton (Controls.xaml).
    /// A WrapPanel, so a longer palette or a narrow card wraps to a second line instead of
    /// clipping the picker off the end.
    /// </summary>
    public sealed class SwatchRow : WrapPanel
    {
        /// <summary>"#RRGGBB" for a swatch, or "" for the no-color outline.</summary>
        public event Action<string>? ColorChosen;

        /// <summary>The picker button was pressed.</summary>
        public event Action? PickRequested;

        /// <summary>Accessible name and tooltip of the no-color outline.</summary>
        public string NoneName { get; set; } = "No color";

        /// <summary>Accessible name and tooltip of the picker button, shortcut included.</summary>
        public string PickerName { get; set; } = "More colors";

        private readonly List<Button> _swatches = [];

        public SwatchRow()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
        }

        /// <summary>Rebuilds the row. <paramref name="current"/> is the value to ring: a hex
        /// color, "" for no color, or null when a selection mixes several values.</summary>
        public void Show(IEnumerable<Color> colors, string? current)
        {
            Children.Clear();
            _swatches.Clear();
            Children.Add(Swatch("", Brushes.Transparent, NoneName, current != null && current.Length == 0));
            foreach (var c in colors)
            {
                string hex = Hex(c);
                Children.Add(Swatch(hex, new SolidColorBrush(c), hex,
                    current != null && string.Equals(current, hex, StringComparison.OrdinalIgnoreCase)));
            }

            var picker = new Button
            {
                Content = new TextBlock
                {
                    Text = ((char)0xE790).ToString(), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
                Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(1, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = PickerName,
            };
            picker.SetResourceReference(StyleProperty, "MenuRowButton");
            AutomationProperties.SetName(picker, PickerName);
            picker.Click += (_, _) => PickRequested?.Invoke();
            Children.Add(picker);
        }

        public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private Button Swatch(string hex, Brush fill, string name, bool on)
        {
            var b = new Button { Background = fill, ToolTip = name, Tag = on ? "on" : null };
            b.SetResourceReference(StyleProperty, "CardSwatch");
            // The no-color outline has no fill to show it, so it takes the dim text color instead
            // of the faint menu border the filled swatches use.
            if (hex.Length == 0) b.SetResourceReference(Control.BorderBrushProperty, "DimTextBrush");
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) =>
            {
                foreach (var other in _swatches) other.Tag = null;
                b.Tag = "on";
                ColorChosen?.Invoke(hex);
            };
            _swatches.Add(b);
            return b;
        }
    }
}
