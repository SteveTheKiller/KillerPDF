using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF
{
    // ============================================================
    // Themed dialog - replaces MessageBox for dark-UI consistency
    // ============================================================
    internal static class KillerDialog
    {
        // Pulls the current theme brush at call time so dialogs respect light/dark/HC themes.
        private static SolidColorBrush R(string key)
            => (SolidColorBrush)Application.Current.Resources[key];

        private static string L(string key, string fallback)
            => Application.Current.TryFindResource(key) as string ?? fallback;

        // Carries the checkbox state of the last Show() call back to ShowWithCheckbox. Dialogs are
        // modal and UI-thread only, so a shared field is safe and avoids a duplicate dialog body.
        private static bool _lastCheckboxChecked;

#pragma warning disable IDE0060 // image intentionally kept for API parity with MessageBox; not yet rendered
        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None,
            bool fadeClose = true,
            string? checkboxText = null,
            MessageBoxResult? defaultResult = null)
#pragma warning restore IDE0060
        {
            var result = MessageBoxResult.OK;
            bool boxChecked = false;

            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height
            };
            DialogChrome.Configure(win, owner, fade: fadeClose);

            var outerBorder = new Border
            {
                Background = R("MenuBackgroundBrush"),
                BorderBrush = UiKit.Brush("DialogFrameBrush"),
                BorderThickness = Application.Current.TryFindResource("DialogFrameThickness") is Thickness dft ? dft : new Thickness(1),
                Padding = Application.Current.TryFindResource("DialogFramePadding") is Thickness dfp ? dfp : new Thickness(0),
                CornerRadius = UiKit.RadWindow,
                Margin = Application.Current.TryFindResource("DialogHaloMargin") is Thickness hm ? hm : new Thickness(10),
                Effect = UiKit.ShadowDialog()
            };

            var root = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                // Transparent so the dialog-wide film grain shows through the title bar too (it sits
                // over the same BgModal surface, so it still reads as one continuous surface).
                Background = Application.Current.TryFindResource("UseDialogCaption") is true ? UiKit.Brush("TitleBarBrush") : Brushes.Transparent,
                Padding = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            // KillerPDF-prefixed titles keep the real wordmark. A qualifier such as "Uninstall"
            // follows in the same typewriter family instead of flattening the whole title into
            // generic monospace text.
            if (title == "KillerPDF" || title.StartsWith("KillerPDF ", System.StringComparison.Ordinal))
            {
                var wm = new StackPanel { Orientation = Orientation.Horizontal };
                var wmTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                wmTb.Inlines.Add(new System.Windows.Documents.Run("Killer") { FontFamily = UiKit.WordmarkFont, FontWeight = FontWeights.Normal, FontSize = 15, Foreground = R("TextBrush") });
                wmTb.Inlines.Add(new System.Windows.Documents.Run("PDF") { FontFamily = UiKit.WordmarkFontPdf, FontWeight = FontWeights.Bold, FontSize = 19.5, Foreground = R("AccentLogo") });
                if (title.Length > "KillerPDF".Length)
                    wmTb.Inlines.Add(new System.Windows.Documents.Run("  " + title["KillerPDF".Length..].TrimStart())
                    {
                        FontFamily = UiKit.WordmarkFont,
                        FontWeight = FontWeights.Normal,
                        FontSize = 14,
                        Foreground = R("TextBrush")
                    });
                wm.Children.Add(wmTb);
                // No DropShadowEffect on the text - it rasterizes and blurs the wordmark. Kept crisp.
                titleBar.Child = wm;
            }
            else
            {
                titleBar.Child = new TextBlock
                {
                    Text = title,
                    Foreground = R("PrimaryBrush"),
                    FontWeight = FontWeights.Bold,   // blue title -> bold
                    FontSize = 14,
                    FontFamily = UiKit.MonoFont
                };
            }
            if (Application.Current.TryFindResource("UseDialogCaption") is true)
                titleBar = DialogChrome.BuildTitleBar(win, owner, title, () => { result = MessageBoxResult.Cancel; win.Close(); });
            titleBar.Height = Application.Current.TryFindResource("DialogTitleBarHeight") is double titleHeight ? titleHeight : double.NaN;
            root.Children.Add(titleBar);

            // Message
            var msgBorder = new Border
            {
                Padding = new Thickness(20, 16, 20, 8),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = R("TextBrush"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            root.Children.Add(msgBorder);

            // Optional checkbox (e.g. "Remember my choice"). Extra top padding sets it apart from the message.
            if (checkboxText is not null)
            {
                var chk = UiKit.CheckBox(checkboxText);
                chk.Margin = new Thickness(20, 10, 20, 4);
                chk.Checked += (_, _2) => boxChecked = true;
                chk.Unchecked += (_, _2) => boxChecked = false;
                root.Children.Add(chk);
            }

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // Build a minimal ControlTemplate so Background binds correctly and
            // WPF's default blue hover chrome can't override our colors.
            Button MakeBtn(string label, MessageBoxResult res, bool accent = false)
            {
                // Enter triggers the primary action. Normally that's the accent button; a caller can
                // override which button is the default (e.g. the quit prompt makes the safe "No" the
                // default), and the accent highlight follows so the Enter target is obvious.
                bool isDefault = defaultResult is MessageBoxResult dr ? res == dr : accent;
                // Shared themed button (UiKit.Make) so this dialog matches the print dialog et al.
                var btn = UiKit.Make(label, isDefault);
                btn.Margin = new Thickness(8, 0, 0, 0);
                btn.IsDefault = isDefault;
                btn.IsCancel  = res == MessageBoxResult.Cancel;   // Esc triggers Cancel where there is one
                btn.Click += (_, _2) => { result = res; win.Close(); };
                return btn;
            }

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_OK", "OK"), MessageBoxResult.OK, accent: true));
                    break;
                case MessageBoxButton.OKCancel:
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_OK", "OK"), MessageBoxResult.OK, accent: true));
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_Cancel", "Cancel"), MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_Yes", "Yes"), MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_No", "No"), MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_Yes", "Yes"), MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_No", "No"), MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_Cancel", "Cancel"), MessageBoxResult.Cancel));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 8, 16, 16),
                Child = btnPanel
            });

            // Paint the same film-grain texture the app's panels use, behind the content, so the
            // dialog reads as part of the same surface family instead of a flat box.
            var contentGrid = new Grid();
            var grain = DialogChrome.GrainTexture(owner);
            if (grain is not null)
            {
                double grainOpacity = Application.Current.Resources["GrainOpacity"] is double go ? go : 0.05;
                contentGrid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity = grainOpacity,
                    Background = new System.Windows.Media.ImageBrush(grain)
                    {
                        TileMode = System.Windows.Media.TileMode.Tile,
                        ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, 256, 256),
                        Stretch = System.Windows.Media.Stretch.None
                    }
                });
            }
            contentGrid.Children.Add(root);
            win.Content = DialogChrome.WrapContent(owner, contentGrid);
            win.ShowDialog();
            _lastCheckboxChecked = boxChecked;
            return result;
        }

        /// <summary>
        /// Like <see cref="Show"/> but with a custom set of buttons. Returns the index of the clicked
        /// button, or -1 if the dialog was closed without a choice. The button at <paramref name="accentIndex"/>
        /// is rendered as the primary (accent) action.
        /// </summary>
        public static int ShowChoices(
            Window? owner,
            string message,
            string[] labels,
            int accentIndex = 0,
            string title = "KillerPDF")
        {
            int result = -1;

            var win = new Window { Title = title, MinWidth = 380, MaxWidth = 760, SizeToContent = SizeToContent.WidthAndHeight };
            DialogChrome.Configure(win, owner, fade: true);

            var outerBorder = new Border
            {
                Background = R("MenuBackgroundBrush"),
                BorderBrush = UiKit.Brush("DialogFrameBrush"),
                BorderThickness = Application.Current.TryFindResource("DialogFrameThickness") is Thickness dft ? dft : new Thickness(1),
                Padding = Application.Current.TryFindResource("DialogFramePadding") is Thickness dfp ? dfp : new Thickness(0),
                CornerRadius = UiKit.RadWindow,
                Margin = Application.Current.TryFindResource("DialogHaloMargin") is Thickness hm ? hm : new Thickness(10),
                Effect = UiKit.ShadowDialog()
            };

            var root = new StackPanel();

            var titleBar = new Border { Background = Application.Current.TryFindResource("UseDialogCaption") is true ? UiKit.Brush("TitleBarBrush") : Brushes.Transparent, Padding = new Thickness(16, 10, 16, 10) };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            if (title == "KillerPDF")
            {
                var wmTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                wmTb.Inlines.Add(new System.Windows.Documents.Run("Killer") { FontFamily = UiKit.WordmarkFont, FontWeight = FontWeights.Normal, FontSize = 15, Foreground = R("TextBrush") });
                wmTb.Inlines.Add(new System.Windows.Documents.Run("PDF") { FontFamily = UiKit.WordmarkFontPdf, FontWeight = FontWeights.Bold, FontSize = 19.5, Foreground = R("AccentLogo") });
                titleBar.Child = wmTb;
            }
            else
            {
                titleBar.Child = new TextBlock { Text = title, Foreground = R("PrimaryBrush"), FontWeight = FontWeights.Bold, FontSize = 14, FontFamily = UiKit.MonoFont };
            }
            if (Application.Current.TryFindResource("UseDialogCaption") is true)
                titleBar = DialogChrome.BuildTitleBar(win, owner, title, () => win.Close());
            titleBar.Height = Application.Current.TryFindResource("DialogTitleBarHeight") is double titleHeight ? titleHeight : double.NaN;
            root.Children.Add(titleBar);

            root.Children.Add(new Border
            {
                Padding = new Thickness(20, 16, 20, 8),
                Child = new TextBlock { Text = message, Foreground = R("TextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 }
            });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var btn = UiKit.Make(labels[i], accent: i == accentIndex);
                btn.Padding = new Thickness(22, 8, 22, 8);
                btn.MinWidth = 96;
                btn.Margin = new Thickness(8, 0, 0, 0);
                btn.Click += (_, _2) => { result = idx; win.Close(); };
                btnPanel.Children.Add(btn);
            }
            root.Children.Add(new Border { Padding = new Thickness(16, 8, 16, 16), Child = btnPanel });

            var contentGrid = new Grid();
            var grain = DialogChrome.GrainTexture(owner);
            if (grain is not null)
            {
                double grainOpacity = Application.Current.Resources["GrainOpacity"] is double go ? go : 0.05;
                contentGrid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity = grainOpacity,
                    Background = new ImageBrush(grain)
                    {
                        TileMode = TileMode.Tile,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, 256, 256),
                        Stretch = Stretch.None
                    }
                });
            }
            contentGrid.Children.Add(root);
            win.Content = DialogChrome.WrapContent(owner, contentGrid);
            win.ShowDialog();
            return result;
        }

        // #400: blank-page size picker. Match-current, presets, or custom W/H with a unit
        // switch. Returns the size in POINTS, or null when cancelled.
        public static (double Width, double Height)? ShowBlankPageSize(
            Window? owner, double? currentW, double? currentH, string? currentLabel)
        {
            (double Width, double Height)? result = null;
            const double minPt = 3, maxPt = 14400;   // Adobe's supported page-size range

            var presets = new System.Collections.Generic.List<(string Label, double W, double H)>();
            if (currentW is { } cw && currentH is { } ch && cw > 0 && ch > 0)
                presets.Add((string.Format(L("Str_BlankPage_Match", "Match current page ({0})"), currentLabel ?? ""), cw, ch));
            foreach (var (name, w, h) in new[] { ("A6", 298.0, 420.0), ("A5", 420.0, 595.0), ("A4", 595.0, 842.0), ("A3", 842.0, 1191.0), ("Letter", 612.0, 792.0), ("Legal", 612.0, 1008.0), ("Tabloid", 792.0, 1224.0) })
                presets.Add(($"{name}  {PageSizeFormatter.Format(w, h).Label}", w, h));
            if (presets.Count == 0) presets.Add(("A4", 595.0, 842.0));

            double wPt = presets[0].W, hPt = presets[0].H;
            int unit = 0;   // 0 = pt, 1 = in, 2 = mm
            double Factor() => unit == 1 ? 72.0 : unit == 2 ? 72.0 / 25.4 : 1.0;
            string ShowNum(double pt) => (pt / Factor()).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            static bool TryNum(string s, out double v) =>
                double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out v)
                || double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v);

            var win = new Window
            {
                Title = L("Str_BlankPage_Title", "Insert Blank Page"),
                Width = 430,
                SizeToContent = SizeToContent.Height
            };
            DialogChrome.Configure(win, owner, fade: true);

            var outerBorder = new Border
            {
                Background = R("MenuBackgroundBrush"),
                BorderBrush = UiKit.Brush("DialogFrameBrush"),
                BorderThickness = Application.Current.TryFindResource("DialogFrameThickness") is Thickness dft ? dft : new Thickness(1),
                Padding = Application.Current.TryFindResource("DialogFramePadding") is Thickness dfp ? dfp : new Thickness(0),
                CornerRadius = UiKit.RadWindow,
                Margin = Application.Current.TryFindResource("DialogHaloMargin") is Thickness hm ? hm : new Thickness(10),
                Effect = UiKit.ShadowDialog()
            };

            var root = new StackPanel();
            var titleBar = new Border { Background = Brushes.Transparent, Padding = new Thickness(16, 10, 16, 10) };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            titleBar.Child = new TextBlock { Text = L("Str_BlankPage_Title", "Insert Blank Page"), Foreground = R("PrimaryBrush"), FontWeight = FontWeights.Bold, FontSize = 14, FontFamily = UiKit.MonoFont };
            root.Children.Add(titleBar);

            var body = new StackPanel { Margin = new Thickness(20, 8, 20, 8) };
            body.Children.Add(new TextBlock { Text = L("Str_BlankPage_Size", "Page size"), Foreground = R("TextBrush"), FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            var sizeCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12), Height = 28 };
            if (owner?.TryFindResource("DarkComboBox") is Style dark) sizeCombo.Style = dark;
            foreach (var p in presets) sizeCombo.Items.Add(p.Label);
            sizeCombo.SelectedIndex = 0;
            body.Children.Add(sizeCombo);

            var wBox = UiKit.Field(90); wBox.VerticalContentAlignment = VerticalAlignment.Center;
            var hBox = UiKit.Field(90); hBox.VerticalContentAlignment = VerticalAlignment.Center;
            var unitCombo = new ComboBox { Height = 28, MinWidth = 120 };
            if (owner?.TryFindResource("DarkComboBox") is Style dark2) unitCombo.Style = dark2;
            unitCombo.Items.Add(L("Str_BlankPage_UnitPt", "Points"));
            unitCombo.Items.Add(L("Str_BlankPage_UnitIn", "Inches"));
            unitCombo.Items.Add(L("Str_BlankPage_UnitMm", "Millimeters"));
            unitCombo.SelectedIndex = 0;

            Grid dimsGrid = new();
            dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dimsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void AddDimRow(string labelKey, string fallback, TextBox box, int row)
            {
                dimsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var lbl = new TextBlock { Text = L(labelKey, fallback), Foreground = R("TextBrush"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
                Grid.SetRow(box, row); Grid.SetColumn(box, 1);
                dimsGrid.Children.Add(lbl); dimsGrid.Children.Add(box);
            }
            AddDimRow("Str_BlankPage_Width", "Width", wBox, 0);
            AddDimRow("Str_BlankPage_Height", "Height", hBox, 1);
            var unitLbl = new TextBlock { Text = L("Str_BlankPage_Unit", "Unit"), Foreground = R("TextBrush"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            dimsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(unitLbl, 2); Grid.SetColumn(unitLbl, 0);
            Grid.SetRow(unitCombo, 2); Grid.SetColumn(unitCombo, 1);
            dimsGrid.Children.Add(unitLbl); dimsGrid.Children.Add(unitCombo);
            body.Children.Add(dimsGrid);
            root.Children.Add(body);

            void RefreshFields() { wBox.Text = ShowNum(wPt); hBox.Text = ShowNum(hPt); }
            RefreshFields();
            sizeCombo.SelectionChanged += (_, _) =>
            {
                int i = sizeCombo.SelectedIndex;
                if (i >= 0 && i < presets.Count) { wPt = presets[i].W; hPt = presets[i].H; RefreshFields(); }
            };
            unitCombo.SelectionChanged += (_, _) =>
            {
                // Keep any typed custom values across the unit switch when they parse.
                if (TryNum(wBox.Text, out double wv) && TryNum(hBox.Text, out double hv))
                { wPt = wv * Factor(); hPt = hv * Factor(); }
                unit = Math.Max(0, unitCombo.SelectedIndex);
                RefreshFields();
            };

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var insert = UiKit.Make(L("Str_BlankPage_Insert", "Insert"), accent: true);
            insert.IsDefault = true;
            insert.Padding = new Thickness(22, 8, 22, 8);
            insert.MinWidth = 96;
            insert.Click += (_, _) =>
            {
                if (!TryNum(wBox.Text, out double wv) || !TryNum(hBox.Text, out double hv))
                {
                    Show(win, L("Str_BlankPage_Invalid", "Enter a valid size (width and height must be numbers)."), L("Str_BlankPage_Title", "Insert Blank Page"));
                    return;
                }
                double w = wv * Factor(), h = hv * Factor();
                if (w < minPt || h < minPt || w > maxPt || h > maxPt)
                {
                    Show(win, L("Str_BlankPage_Invalid", "Enter a valid size (width and height must be numbers)."), L("Str_BlankPage_Title", "Insert Blank Page"));
                    return;
                }
                result = (w, h);
                win.Close();
            };
            var cancel = UiKit.Make(L("Str_Btn_Cancel", "Cancel"), accent: false);
            cancel.IsCancel = true;
            cancel.Padding = new Thickness(22, 8, 22, 8);
            cancel.MinWidth = 96;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += (_, _) => win.Close();
            btnPanel.Children.Add(insert);
            btnPanel.Children.Add(cancel);
            root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = btnPanel });

            outerBorder.Child = root;
            win.Content = DialogChrome.WrapContent(owner, outerBorder);
            win.ShowDialog();
            return result;
        }

        /// <summary>
        /// Like <see cref="Show"/> but with a "don't warn again" style checkbox between the message and the
        /// buttons. Returns the button result and the checkbox state.
        /// </summary>
        // Same dialog as Show(), plus a checkbox (e.g. "Remember my choice"). Delegates to the single
        // Show() implementation so there is one KillerDialog box, not a duplicate.
        public static (MessageBoxResult result, bool isChecked) ShowWithCheckbox(
            Window? owner,
            string message,
            string checkboxText,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OKCancel,
            MessageBoxResult? defaultResult = null)
        {
            var result = Show(owner, message, title, buttons, checkboxText: checkboxText, defaultResult: defaultResult);
            return (result, _lastCheckboxChecked);
        }

        /// <summary>
        /// KillerFind-style quit prompt (family standard): a short question with TWO opt-out
        /// checkboxes stacked between the message and the buttons - "Close my open tabs"
        /// (unchecked = session reopens next launch) and "Remember my choice" - plus
        /// Cancel / Quit buttons where Quit is the accent + Enter default and Esc cancels.
        /// Returns (confirmed, closeTabsChecked, rememberChecked).
        ///
        /// A thin wrapper over <see cref="ShowTwoCheckPrompt"/> since the install prompt needed the
        /// same two-checkbox shape. Passing check2Initial:false keeps this behavior identical to
        /// what it was when the body lived here.
        /// </summary>
        public static (bool confirmed, bool closeTabs, bool remember) ShowQuitPrompt(
            Window? owner,
            string message,
            string closeTabsText,
            bool closeTabsInitial,
            string rememberText,
            string quitLabel,
            string cancelLabel)
            => ShowTwoCheckPrompt(owner, message, closeTabsText, closeTabsInitial,
                                  rememberText, false, quitLabel, cancelLabel);

        /// <summary>
        /// The family two-checkbox confirm: a short question, two checkboxes stacked between the
        /// message and the buttons, and Cancel / confirm buttons where confirm is the accent +
        /// Enter default and Esc cancels. Used by the quit prompt and by the install prompt
        /// (desktop shortcut + install for all users).
        /// </summary>
        public static (bool confirmed, bool check1, bool check2) ShowTwoCheckPrompt(
            Window? owner,
            string message,
            string check1Text,
            bool check1Initial,
            string check2Text,
            bool check2Initial,
            string confirmLabel,
            string cancelLabel)
        {
            bool confirmed = false;
            bool closeTabs = check1Initial;
            bool remember  = check2Initial;

            var win = new Window { Title = "KillerPDF", Width = 380, SizeToContent = SizeToContent.Height };
            // fade:false - the app's own fade-out follows immediately on confirm; two fades
            // back-to-back read as lag (same reasoning as the unsaved-changes prompt).
            DialogChrome.Configure(win, owner, fade: false);

            var outerBorder = new Border
            {
                Background = R("MenuBackgroundBrush"),
                BorderBrush = UiKit.Brush("DialogFrameBrush"),
                BorderThickness = Application.Current.TryFindResource("DialogFrameThickness") is Thickness dft ? dft : new Thickness(1),
                Padding = Application.Current.TryFindResource("DialogFramePadding") is Thickness dfp ? dfp : new Thickness(0),
                CornerRadius = UiKit.RadWindow,
                Margin = Application.Current.TryFindResource("DialogHaloMargin") is Thickness hm ? hm : new Thickness(10),
                Effect = UiKit.ShadowDialog()
            };

            var root = new StackPanel();

            // Title bar: the wordmark, exactly like Show()'s "KillerPDF" branch.
            var titleBar = new Border
            {
                Background = Application.Current.TryFindResource("UseDialogCaption") is true ? UiKit.Brush("TitleBarBrush") : Brushes.Transparent,
                Padding = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var wm = new StackPanel { Orientation = Orientation.Horizontal };
            var wmTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            wmTb.Inlines.Add(new System.Windows.Documents.Run("Killer") { FontFamily = UiKit.WordmarkFont, FontWeight = FontWeights.Normal, FontSize = 15, Foreground = R("TextBrush") });
            wmTb.Inlines.Add(new System.Windows.Documents.Run("PDF") { FontFamily = UiKit.WordmarkFontPdf, FontWeight = FontWeights.Bold, FontSize = 18, Foreground = R("AccentLogo") });
            wm.Children.Add(wmTb);
            titleBar.Child = wm;
            if (Application.Current.TryFindResource("UseDialogCaption") is true)
                titleBar = DialogChrome.BuildTitleBar(win, owner, "KillerPDF", () => win.Close());
            titleBar.Height = Application.Current.TryFindResource("DialogTitleBarHeight") is double titleHeight ? titleHeight : double.NaN;
            root.Children.Add(titleBar);

            root.Children.Add(new Border
            {
                Padding = new Thickness(20, 16, 20, 8),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = R("TextBrush"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                }
            });

            var chk1 = UiKit.CheckBox(check1Text);
            chk1.Margin = new Thickness(20, 10, 20, 0);
            chk1.IsChecked = check1Initial;
            chk1.Checked   += (_, _2) => closeTabs = true;
            chk1.Unchecked += (_, _2) => closeTabs = false;
            root.Children.Add(chk1);

            var chk2 = UiKit.CheckBox(check2Text);
            chk2.Margin = new Thickness(20, 8, 20, 4);
            chk2.IsChecked = check2Initial;
            chk2.Checked   += (_, _2) => remember = true;
            chk2.Unchecked += (_, _2) => remember = false;
            root.Children.Add(chk2);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancelBtn = UiKit.Make(cancelLabel, false);
            cancelBtn.Margin   = new Thickness(8, 0, 0, 0);
            cancelBtn.IsCancel = true;
            cancelBtn.Click   += (_, _2) => win.Close();
            var quitBtn = UiKit.Make(confirmLabel, true);
            quitBtn.Margin    = new Thickness(8, 0, 0, 0);
            quitBtn.IsDefault = true;
            quitBtn.Click    += (_, _2) => { confirmed = true; win.Close(); };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(quitBtn);
            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 12, 16, 16),
                Child = btnPanel
            });

            // Film grain across the whole card, same as Show().
            var contentGrid = new Grid();
            var grain = DialogChrome.GrainTexture(owner);
            if (grain is not null)
            {
                double grainOpacity = Application.Current.Resources["GrainOpacity"] is double go ? go : 0.05;
                contentGrid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity = grainOpacity,
                    Background = new System.Windows.Media.ImageBrush(grain)
                    {
                        TileMode = System.Windows.Media.TileMode.Tile,
                        ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, 256, 256),
                        Stretch = System.Windows.Media.Stretch.None
                    }
                });
            }
            contentGrid.Children.Add(root);
            win.Content = DialogChrome.WrapContent(owner, contentGrid);
            win.ShowDialog();
            return (confirmed, closeTabs, remember);
        }

        /// <summary>
        /// Themed "Password Required" prompt: the family dialog chrome (wordmark title bar, grain,
        /// red close, Esc to cancel) around a themed PasswordBox. Returns the entered password, or
        /// null if the user canceled / closed the dialog.
        /// </summary>
        public static string? PromptPassword(Window? owner, string filename)
        {
            string? result = null;

            var win = new Window { Width = 380, SizeToContent = SizeToContent.Height };
            DialogChrome.Configure(win, owner, fade: true);

            void CloseCancel() { result = null; win.Close(); }

            var body = new StackPanel();

            // Message: "<file>" is password protected.
            var msg = new TextBlock { Foreground = R("TextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            msg.Inlines.Add(new System.Windows.Documents.Run($"“{System.IO.Path.GetFileName(filename)}” ") { FontWeight = FontWeights.SemiBold });
            msg.Inlines.Add(new System.Windows.Documents.Run("is password protected."));
            body.Children.Add(new Border { Padding = new Thickness(20, 4, 20, 10), Child = msg });

            var pw = UiKit.PasswordField();
            body.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 4), Child = pw });

            var openBtn = UiKit.Make("Open", accent: true);
            openBtn.IsDefault = true;
            openBtn.Click += (_, _2) => { result = pw.Password; win.Close(); };
            var cancelBtn = UiKit.Make("Cancel", accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => CloseCancel();
            body.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = UiKit.ButtonRow(openBtn, cancelBtn) });

            // Enter anywhere in the field submits.
            pw.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = pw.Password; win.Close(); } };

            win.Content = DialogChrome.Frame(win, owner, "KillerPDF", CloseCancel, body);
            win.Loaded += (_, _2) => pw.Focus();
            win.ShowDialog();
            return result;
        }

    }
}
