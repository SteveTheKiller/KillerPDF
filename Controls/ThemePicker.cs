using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KillerPDF.Services;
using KillerPDF.Shell;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Canonical KillerPDF-style theme picker: a radio-row ContextMenu listing all fourteen themes,
    /// with ONE vertical accent strip beside the list, repainted for whichever accent-capable
    /// family (Dark, Light, Black, 98SE) is selected, and the Film Grain switch under both columns.
    /// Each accent is a radio option with a screen reader name, reachable by Tab and the arrow
    /// keys.
    ///
    /// App hooks (set after constructing; leave them null to keep the kit behavior):
    ///   ThemeChangeTransition and AccentChangeTransition wrap the apply in the app's own
    ///     transition. The default cross-fades through ThemeTransition; pass
    ///     <c>apply => apply()</c> for no fade.
    ///   ThemeApplied runs after a theme or accent is applied, for app frame work such as
    ///     DwmChrome.SetRoundedCorners.
    /// </summary>
    internal sealed class ThemePicker
    {
        private static readonly Theme[] Themes =
        [
            Theme.Dark, Theme.Light, Theme.Black, Theme.SE98, Theme.TrashPolka, Theme.Blood, Theme.Greed,
            Theme.Cyanotic, Theme.Ectoplasm, Theme.Decay, Theme.Malaise, Theme.Sepulchre,
            Theme.Delirium, Theme.Mourning
        ];

        // 7 gap from the divider, the 26px pill and its 2px right margin, with no slack, so the
        // pill's right edge lands 8px inside the card (the panel's 5 plus the 1px border).
        private const double AccentStripWidth = 35;
        private const double AccentSlideMs = 170;

        private readonly Window _window;
        private readonly ContextMenu _menu;
        private readonly FrameworkElement _button;
        private readonly FrameworkElement _pane;
        private Grid? _accentHost;
        private readonly List<RadioButton> _accentDots = [];
        private CheckBox? _filmGrainCheck;
        // True while this picker is the one changing the theme, so ThemeChanged does not rebuild
        // the menu out from under the accent strip's slide.
        private bool _applying;

        /// <summary>Wraps a theme change; null cross-fades through ThemeTransition.</summary>
        internal Action<Action>? ThemeChangeTransition { get; set; }

        /// <summary>Wraps an accent change; null cross-fades through ThemeTransition.</summary>
        internal Action<Action>? AccentChangeTransition { get; set; }

        /// <summary>Runs after the picker applies a theme or an accent.</summary>
        internal Action? ThemeApplied { get; set; }

        private void RunTransition(Action<Action>? transition, Action apply)
        {
            if (transition != null) transition(apply);
            else ThemeTransition.CrossFade((FrameworkElement)_window.Content, apply);
        }

        internal ThemePicker(Window window, ContextMenu menu, FrameworkElement button,
                             FrameworkElement contentPane)
        {
            _window = window;
            _menu = menu;
            _button = button;
            _pane = contentPane;
            if (_button is FrameworkElement themeButton)
                themeButton.ContextMenuOpening += (_, e) => { e.Handled = true; Toggle(); };
        }

        internal void Toggle()
        {
            if (_menu.IsOpen) { _menu.IsOpen = false; return; }
            Build();
            FlyoutPlacement.UsePane(_pane);
            FlyoutPlacement.Attach(_menu, null);   // a big menu: always the pane corner
            _menu.IsOpen = true;
            _menu.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        internal void Refresh()
        {
            if (_menu.IsOpen && !_applying) Build();
        }

        private void Build()
        {
            _menu.Items.Clear();
            // The panel margin below carries the whole inset, so the menu adds none of its own. A host
            // on the default ContextMenu template would otherwise stack its 7px padding on top.
            _menu.Padding = new Thickness(0);
            _accentHost = null;
            _accentDots.Clear();
            // Every hover row and accent pill sits 8px in from the card's outer edge (family rule):
            // the flyout card's 1px border, then 15 left less the row's -8, 7 top and bottom, and 5 right
            // plus the strip's own 2. The theme column fills the menu (*), so when the menu is wider than
            // the list the hover still reaches across.
            var panel = new Grid { Margin = new Thickness(15, 7, 5, 7) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Sized by the longest theme name, so the accent strip sits right beside the names.
            var list = new StackPanel();
            Grid.SetColumn(list, 0);
            panel.Children.Add(list);
            foreach (var theme in Themes)
            {
                var radio = new RadioButton
                {
                    Content = ThemeLabel(theme), Tag = theme, GroupName = "ThemeGroup",
                    Style = (Style)_window.FindResource("ThemeRadio"),
                    IsChecked = ThemeManager.Current == theme
                };
                radio.Checked += (_, _) => Theme_Checked(panel, theme);
                list.Children.Add(radio);
            }
            if (HasAccents(ThemeManager.Current))
            {
                _accentHost = BuildAccentStrip(ThemeManager.Current);
                panel.Children.Add(_accentHost);
            }

            // Film Grain: one switch for the grain texture on every surface (Services/FilmGrain.cs),
            // on its own row under both columns so the accent strip still runs the height of the
            // theme list only. Alt+Shift+G runs the same toggle, so the tooltip carries the chord.
            var grainRow = new StackPanel();
            Grid.SetRow(grainRow, 1);
            Grid.SetColumnSpan(grainRow, 2);
            var grainDivider = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 0, 9, 8),
                Opacity = 0.18,
                IsHitTestVisible = false,
                OpacityMask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop(Colors.Transparent, 0),
                        new GradientStop(Colors.Black, 0.15),
                        new GradientStop(Colors.Black, 0.85),
                        new GradientStop(Colors.Transparent, 1),
                    },
                },
            };
            grainDivider.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            grainRow.Children.Add(grainDivider);
            var filmGrain = new CheckBox
            {
                Style = (Style)_window.FindResource("ThemeCheck"),
                IsChecked = FilmGrain.Enabled,
                Margin = new Thickness(0),
            };
            filmGrain.SetResourceReference(ContentControl.ContentProperty, "Str_Theme_FilmGrain");
            filmGrain.Click += (_, _) => FilmGrain.Set(filmGrain.IsChecked == true);
            // #506: key names come from the active language (German reads Alt+Umschalt+G), and the
            // tip is rebuilt each time it opens so a language switch never leaves it stale.
            filmGrain.ToolTip = FilmGrainTip.Text(key => _window.TryFindResource(key) as string);
            filmGrain.ToolTipOpening += (_, _) =>
                filmGrain.ToolTip = FilmGrainTip.Text(key => _window.TryFindResource(key) as string);
            _filmGrainCheck = filmGrain;
            grainRow.Children.Add(filmGrain);
            panel.Children.Add(grainRow);

            _menu.Items.Add(panel);
        }

        /// <summary>Alt+Shift+G: Film Grain on or off, kept in step with the flyout's check box.</summary>
        internal void ToggleFilmGrain()
        {
            FilmGrain.Set(!FilmGrain.Enabled);
            _filmGrainCheck?.IsChecked = FilmGrain.Enabled;
        }

        private void Theme_Checked(Grid panel, Theme theme)
        {
            if (theme == ThemeManager.Current) return;
            _applying = true;
            try
            {
                RunTransition(ThemeChangeTransition, () =>
                {
                    ThemeManager.Apply(theme);
                    DwmChrome.SetThemeBorder(_window);
                    ThemeApplied?.Invoke();
                });
            }
            finally { _applying = false; }
            ReplaceAccentStrip(panel, theme, animate: true);
        }

        /// <summary>Swaps the strip for the newly selected family, sliding it in from zero width,
        /// or removes it for a fixed-palette theme.</summary>
        private void ReplaceAccentStrip(Grid panel, Theme family, bool animate)
        {
            if (_accentHost != null) panel.Children.Remove(_accentHost);
            _accentHost = null;
            _accentDots.Clear();
            if (!HasAccents(family)) return;

            _accentHost = BuildAccentStrip(family);
            panel.Children.Add(_accentHost);
            if (!animate) return;

            _accentHost.Width = 0;
            _accentHost.ClipToBounds = true;
            var slide = new DoubleAnimation(0, AccentStripWidth, TimeSpan.FromMilliseconds(AccentSlideMs))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            slide.Completed += (_, _) =>
            {
                if (_accentHost == null) return;
                _accentHost.BeginAnimation(FrameworkElement.WidthProperty, null);
                _accentHost.Width = AccentStripWidth;
            };
            _accentHost.BeginAnimation(FrameworkElement.WidthProperty, slide);
        }

        /// <summary>KillerPDF's picker: one vertical strip beside the complete theme list,
        /// repainted for whichever accent-capable family is selected.</summary>
        private Grid BuildAccentStrip(Theme family)
        {
            var host = new Grid { Width = AccentStripWidth };
            Grid.SetColumn(host, 1);

            var divider = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 6),
            };
            divider.SetResourceReference(Border.BackgroundProperty, "MenuBorderBrush");
            host.Children.Add(divider);

            var strip = new Grid { Margin = new Thickness(7, 0, 2, 6) };
            // One Tab stop for the whole strip; the arrow keys move between accents.
            KeyboardNavigation.SetTabNavigation(strip, KeyboardNavigationMode.Once);
            KeyboardNavigation.SetDirectionalNavigation(strip, KeyboardNavigationMode.Cycle);
            var colors = StripColors(family);
            for (int i = 0; i < colors.Length; i++)
            {
                strip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var (accent, hex) = colors[i];
                var dot = new Border
                {
                    Style = (Style)_window.FindResource("AccentDot"),
                    // AccentDot's 7px right margin spaces a row of dots; in this 26px column it
                    // would cut 7px off the right of every pill.
                    Margin = new Thickness(0),
                    Width = 26,
                    Height = double.NaN,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = AccentStripBrush(family, accent, hex),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(),
                };
                if (ThemeManager.AccentChoiceFor(family) == accent)
                    dot.BorderBrush = _window.TryFindResource("TextBrush") as Brush;

                // The pill is drawn as before, inside a bare radio option, so the keyboard and
                // screen readers reach it. The ring is the focus cue, so there is no focus rectangle.
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
                var option = new RadioButton
                {
                    Tag = accent, GroupName = "ThemeAccentGroup", Content = dot,
                    Width = 26, VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, i == colors.Length - 1 ? 0 : 8),
                    IsChecked = ThemeManager.AccentChoiceFor(family) == accent,
                    FocusVisualStyle = null,
                    Template = new ControlTemplate(typeof(RadioButton)) { VisualTree = presenter },
                };
                AutomationProperties.SetName(option,
                    _window.TryFindResource("Str_Color_" + accent) as string ?? accent.ToString());
                option.MouseEnter += (_, _) => RingAccentStrip(family);
                option.MouseLeave += (_, _) => RingAccentStrip(family);
                option.GotKeyboardFocus += (_, _) => RingAccentStrip(family);
                option.LostKeyboardFocus += (_, _) => RingAccentStrip(family);
                option.Checked += (_, _) =>
                {
                    if (ThemeManager.AccentChoiceFor(family) == accent) return;
                    _applying = true;
                    try
                    {
                        RunTransition(AccentChangeTransition, () =>
                        {
                            ThemeManager.ApplyAccent(family, accent);
                            DwmChrome.SetThemeBorder(_window);
                            ThemeApplied?.Invoke();
                        });
                    }
                    finally { _applying = false; }
                    RingAccentStrip(family);
                };
                _accentDots.Add(option);
                Grid.SetRow(option, i);
                strip.Children.Add(option);
            }
            host.Children.Add(strip);
            return host;
        }

        private static bool HasAccents(Theme theme) => ThemeManager.SupportsAccents(theme);

        private object ThemeLabel(Theme theme) =>
            _window.TryFindResource(theme == Theme.SE98 ? "Str_Theme_98SE" : "Str_Theme_" + theme)
                ?? (theme == Theme.SE98 ? "98SE" : theme.ToString());

        // The ring marks the chosen accent and follows the mouse, and the pill under the mouse
        // lifts a little (KillerNotes). 98SE swatches stay flat.
        private void RingAccentStrip(Theme family)
        {
            var selected = ThemeManager.AccentChoiceFor(family);
            Brush? ring = _window.TryFindResource("TextBrush") as Brush;
            foreach (RadioButton option in _accentDots)
            {
                var dot = (Border)option.Content;
                bool chosen = option.Tag is Accent accent && accent == selected;
                option.IsChecked = chosen;
                dot.BorderBrush = option.IsMouseOver || option.IsKeyboardFocusWithin || chosen ? ring : Brushes.Transparent;
                bool pop = option.IsMouseOver && family != Theme.SE98;
                if (dot.RenderTransform is ScaleTransform scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(pop ? 1.06 : 1, TimeSpan.FromMilliseconds(100)));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(pop ? 1.03 : 1, TimeSpan.FromMilliseconds(100)));
                }
            }
        }

        // Each pill shows its accent the way a selection does: the palette's own SelectionBg
        // gradient, read from that accent's theme file. 98SE keeps its flat Win98 swatches.
        private static readonly Dictionary<(Theme, Accent), Brush> AccentStripBrushes = [];

        private static Brush AccentStripBrush(Theme family, Accent accent, string flatHex)
        {
            if (AccentStripBrushes.TryGetValue((family, accent), out var cached)) return cached;
            Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(flatHex));
            if (family != Theme.SE98)
            {
                // The accent a family bakes into its base palette has no overlay of its own.
                string path = accent.ToString() == ThemeAccentRules.BakedAccent(family.ToString())
                    ? $"Themes/{family}.xaml"
                    : $"Themes/Accents/{family}/{accent}.xaml";
                try
                {
                    var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{path}") };
                    if (palette["SelectionBg"] is Brush selection) brush = selection.CloneCurrentValue();
                }
                catch { /* accent file not present: the flat swatch color stands */ }
            }
            brush.Freeze();
            AccentStripBrushes[(family, accent)] = brush;
            return brush;
        }

        // The swatch order and flat colors are the family's, top to bottom, shared with every app.
        private static (Accent Accent, string Hex)[] StripColors(Theme family) => family switch
        {
            Theme.Light =>
            [
                (Accent.Red, "#931A1A"), (Accent.Orange, "#C7710F"), (Accent.Yellow, "#EAD900"), (Accent.Green, "#1B5E20"),
                (Accent.Teal, "#0D827E"), (Accent.Blue, "#18608E"), (Accent.Purple, "#5A1690"), (Accent.Magenta, "#A60070")
            ],
            Theme.Black =>
            [
                (Accent.Red, "#FF2929"), (Accent.Orange, "#FF910A"), (Accent.Yellow, "#FFEB00"), (Accent.Green, "#00FF66"),
                (Accent.Teal, "#0AFFE7"), (Accent.Blue, "#298DFF"), (Accent.Purple, "#B829FF"), (Accent.Magenta, "#FF2BBD")
            ],
            // Blood, Ember, Ochre, Moss, Teal, Cobalt, Violet, Rose.
            Theme.TrashPolka =>
            [
                (Accent.Red, "#D7263D"), (Accent.Orange, "#EE7B22"), (Accent.Yellow, "#D9A21B"), (Accent.Green, "#7A9A3A"),
                (Accent.Teal, "#1E9A96"), (Accent.Blue, "#3A6FD0"), (Accent.Purple, "#8A4CC2"), (Accent.Magenta, "#E4588E")
            ],
            Theme.SE98 =>
            [
                (Accent.Red, "#800000"), (Accent.Orange, "#A05000"), (Accent.Yellow, "#EAD900"), (Accent.Green, "#006000"),
                (Accent.Teal, "#008080"), (Accent.Blue, "#000080"), (Accent.Purple, "#5A376E"), (Accent.Magenta, "#750052")
            ],
            _ =>
            [
                (Accent.Red, "#DD504B"), (Accent.Orange, "#E8962C"), (Accent.Yellow, "#EAD900"), (Accent.Green, "#1EA54C"),
                (Accent.Teal, "#1FB8A8"), (Accent.Blue, "#4580D9"), (Accent.Purple, "#B982E3"), (Accent.Magenta, "#FF52C9")
            ],
        };
    }
}
