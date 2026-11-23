using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;
using KillerPDF.Shell;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The family language picker (Killendar's): every language in two columns, native name on the
    /// left and locale code on the right, as radio rows in the button's own ContextMenu. Rebuilt on
    /// each open so the current locale is the checked row.
    /// </summary>
    internal sealed class LanguageMenu
    {
        // English pinned first; the rest alphabetical by locale code. Native names are written as
        // escapes so this file stays pure ASCII - a literal glyph does not survive every tool.
        private static readonly (Locale Loc, string Name, string Code)[] Languages =
        [
            (Locale.EnUS, "English", "en-US"),
            (Locale.Bn,   "\u09AC\u09BE\u0982\u09B2\u09BE", "bn"),
            (Locale.CsCZ, "\u010Ce\u0161tina", "cs-CZ"),
            (Locale.De,   "Deutsch", "de-DE"),
            (Locale.Es,   "Espa\u00F1ol", "es"),
            (Locale.Fr,   "Fran\u00E7ais", "fr-FR"),
            (Locale.HuHU, "Magyar", "hu-HU"),
            (Locale.ItIT, "Italiano", "it-IT"),
            (Locale.JaJP, "\u65E5\u672C\u8A9E", "ja-JP"),
            (Locale.KkKZ, "\u049A\u0430\u0437\u0430\u049B\u0448\u0430", "kk-KZ"),
            (Locale.KoKR, "\uD55C\uAD6D\uC5B4", "ko-KR"),
            (Locale.NbNO, "Norsk bokm\u00E5l", "nb-NO"),
            (Locale.PlPL, "Polski", "pl-PL"),
            (Locale.PtBR, "Portugu\u00EAs (Brasil)", "pt-BR"),
            (Locale.RuRU, "\u0420\u0443\u0441\u0441\u043A\u0438\u0439", "ru-RU"),
            (Locale.SlSI, "Sloven\u0161\u010Dina", "sl-SI"),
            (Locale.SvSE, "Svenska", "sv-SE"),
            (Locale.TrTR, "T\u00FCrk\u00E7e", "tr-TR"),
            (Locale.UkUA, "\u0423\u043A\u0440\u0430\u0457\u043D\u0441\u044C\u043A\u0430", "uk-UA"),
            (Locale.ViVN, "Ti\u1EBFng Vi\u1EC7t", "vi-VN"),
            (Locale.ZhCN, "\u4E2D\u6587 (\u7B80\u4F53)", "zh-CN"),
            (Locale.ZhTW, "\u4E2D\u6587 (\u7E41\u9AD4)", "zh-TW"),
        ];

        private readonly ContextMenu _menu;
        private readonly FrameworkElement _button;
        private readonly FrameworkElement _pane;
        private readonly Action _localeChanged;

        internal LanguageMenu(ContextMenu menu, FrameworkElement button, FrameworkElement contentPane,
                              Action localeChanged)
        {
            _menu = menu;
            _button = button;
            _pane = contentPane;
            _localeChanged = localeChanged;
            _button.ContextMenuOpening += (_, e) => { e.Handled = true; Open(); };
        }

        /// <summary>Rebuilds the items and opens the menu beside its button, inside the content
        /// pane (Shell/FlyoutPlacement.cs), with the family fade.</summary>
        internal void Open()
        {
            if (_menu.IsOpen) { _menu.IsOpen = false; return; }
            Build();
            FlyoutPlacement.UsePane(_pane);
            FlyoutPlacement.Attach(_menu, null);   // a big menu: always the pane corner
            _menu.IsOpen = true;
            Anim.FadeIn(_menu);
        }

        private void Build()
        {
            _menu.Items.Clear();
            _menu.Padding = new Thickness(0);   // the columns' margin is the whole 8px edge, as in the theme menu
            var current = LocaleManager.Current;

            // Two columns, so the last row stays on screen in a short window. The left column fills
            // first, which keeps reading order and English at the top.
            // Each hover row sits 8px from the card's outer edge (family rule): the 1px border, then 15 left
            // less the row's -8, 7 top and bottom, and 5 right plus the row's own 2.
            var columns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(15, 7, 5, 7) };
            var left = new StackPanel { Width = 200 };
            var right = new StackPanel { Width = 200 };
            var divider = new Border
            {
                Width = 1,
                // 8px from the line to the hover box on both sides: the right column's rows reach 8px back past their column.
                Margin = new Thickness(6, 8, 16, 8),
                Opacity = 0.18,
                IsHitTestVisible = false,
                OpacityMask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop(Colors.Transparent, 0),
                        new GradientStop(Colors.Black, 0.15),
                        new GradientStop(Colors.Black, 0.85),
                        new GradientStop(Colors.Transparent, 1),
                    },
                },
            };
            divider.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            columns.Children.Add(left);
            columns.Children.Add(divider);
            columns.Children.Add(right);

            int half = (Languages.Length + 1) / 2;
            int index = 0;
            foreach (var (loc, name, code) in Languages)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var nameBlock = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
                var codeBlock = new TextBlock
                {
                    Text = code,
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                codeBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                Grid.SetColumn(codeBlock, 1);
                grid.Children.Add(nameBlock);
                grid.Children.Add(codeBlock);

                var item = new RadioButton
                {
                    Content = grid,
                    Tag = loc,
                    GroupName = "LangGroup",
                    Style = (Style)Application.Current.FindResource("ThemeRadio"),
                    IsChecked = loc == current,
                };
                item.Checked += LocaleItem_Checked;
                (index++ < half ? left : right).Children.Add(item);
            }
            // The rows' 8px gap is for between rows; under the last one it would double the bottom edge.
            foreach (var column in new[] { left, right })
                if (column.Children.Count > 0 && column.Children[^1] is FrameworkElement last)
                    last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);

            // A raw panel added to a ContextMenu is wrapped in the normal MenuItem template, which
            // reserves an icon gutter and row padding around the WHOLE picker. PanelMenuItem is the
            // shared gutter-free container for custom menu panels like this one.
            _menu.Items.Add(new MenuItem
            {
                Header = columns,
                StaysOpenOnClick = true,
                Style = (Style)Application.Current.FindResource("PanelMenuItem"),
            });
        }

        private void LocaleItem_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: Locale loc } || loc == LocaleManager.Current) return;
            LocaleManager.Apply(loc);
            _localeChanged();
            _menu.IsOpen = false;
        }
    }
}
