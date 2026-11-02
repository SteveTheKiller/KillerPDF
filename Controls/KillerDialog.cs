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
using KillerFamily.Accessibility;

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

        // The app icon that leads the wordmark, at the main title bar's size and spacing.
        private static Image TitleIcon()
            => DialogFrame.TitleIcon();

        // Carries the checkbox state of the last Show() call back to ShowWithCheckbox. Dialogs are
        // modal and UI-thread only, so a shared field is safe and avoids a duplicate dialog body.
        private static bool _lastCheckboxChecked;

        // Message text stays at body size, including multiline warnings and confirmations.
        private static Grid NoticeBody(string message, MessageBoxImage image, bool confirmation, out StackPanel textStack)
        {
            var grid = new Grid { Margin = new Thickness(24, 18, 24, 18) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var (glyph, brush) = Notice(image, confirmation);
            grid.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(2),
                BorderBrush = brush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Glyph
                {
                    Text = glyph,
                    Foreground = brush,
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            textStack = new StackPanel();
            Grid.SetColumn(textStack, 1);
            var head = new TextBlock
            {
                Text = message,
                Foreground = R("TextBrush"),
                FontFamily = UiKit.UiFont,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
            textStack.Children.Add(head);
            grid.Children.Add(textStack);
            return grid;
        }

        // Amber "!" for a question or warning, red for an error and the accent "i" for information.
        // Amber darkens on a light card so it keeps its contrast.
        private static (string glyph, Brush brush) Notice(MessageBoxImage image, bool confirmation)
        {
            if (image == MessageBoxImage.Error)
                return ("×", new SolidColorBrush(Color.FromRgb(227, 93, 106)));
            bool warn = image == MessageBoxImage.Warning || image == MessageBoxImage.Question
                        || (image == MessageBoxImage.None && confirmation);
            if (!warn)
                return ("i", UiKit.Brush("PrimaryBrush", Brushes.Gray));
            bool light = UiKit.Brush("MenuBackgroundBrush") is SolidColorBrush card && card.Color.R * 0.299 + card.Color.G * 0.587 + card.Color.B * 0.114 > 140;
            return ("!", new SolidColorBrush(light ? Color.FromRgb(176, 112, 0) : Color.FromRgb(255, 190, 80)));
        }

        // Family dialog button size: 28px tall, at least 80 wide.
        private static Button Sized(Button button)
        {
            button.Height = 28;
            button.MinWidth = 80;
            button.Padding = new Thickness(12, 2, 12, 2);
            return button;
        }

        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None,
            bool fadeClose = true,
            string? checkboxText = null,
            MessageBoxResult? defaultResult = null,
            bool checkboxInitial = false,
            string? confirmLabel = null,
            string? cancelLabel = null)
        {
            var result = MessageBoxResult.OK;
            bool boxChecked = checkboxInitial;

            var win = new Window
            {
                Title = title,
                Width = 390,
                SizeToContent = SizeToContent.Height
            };
            DialogFrame.Configure(win, owner, fade: fadeClose);

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

            // Every dialog title bar is the shared family chrome: wordmark, qualifier on its baseline,
            // the same lift as the main window, and the family close X.
            var titleBar = DialogFrame.BuildTitleBar(win, owner, title, () => { result = MessageBoxResult.Cancel; win.Close(); });
            titleBar.Height = Application.Current.TryFindResource("DialogTitleBarHeight") is double titleHeight ? titleHeight : double.NaN;
            root.Children.Add(titleBar);

            // Message
            root.Children.Add(NoticeBody(message, image, buttons != MessageBoxButton.OK, out var textStack));

            // Optional checkbox (e.g. "Remember my choice"). Extra top padding sets it apart from the message.
            if (checkboxText is not null)
            {
                var chk = UiKit.CheckBox(checkboxText);
                chk.IsChecked = checkboxInitial;
                chk.Margin = new Thickness(0, 12, 0, 0);
                chk.Checked += (_, _2) => boxChecked = true;
                chk.Unchecked += (_, _2) => boxChecked = false;
                textStack.Children.Add(chk);
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
                var btn = Sized(UiKit.Make(label, isDefault));
                btn.Margin = new Thickness(8, 0, 0, 0);
                btn.IsDefault = isDefault;
                btn.IsCancel  = res == MessageBoxResult.Cancel;   // Esc triggers Cancel where there is one
                btn.Click += (_, _2) => { result = res; win.Close(); };
                return btn;
            }

            // The family order: the way out first and the action last, at the right edge.
            switch (buttons)
            {
                case MessageBoxButton.OK:
                    btnPanel.Children.Add(MakeBtn(confirmLabel ?? L("Str_Btn_OK", "OK"), MessageBoxResult.OK, accent: true));
                    break;
                case MessageBoxButton.OKCancel:
                    btnPanel.Children.Add(MakeBtn(cancelLabel ?? L("Str_Btn_Cancel", "Cancel"), MessageBoxResult.Cancel));
                    btnPanel.Children.Add(MakeBtn(confirmLabel ?? L("Str_Btn_OK", "OK"), MessageBoxResult.OK, accent: true));
                    break;
                case MessageBoxButton.YesNo:
                    btnPanel.Children.Add(MakeBtn(cancelLabel ?? L("Str_Btn_No", "No"), MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn(confirmLabel ?? L("Str_Btn_Yes", "Yes"), MessageBoxResult.Yes, accent: true));
                    break;
                case MessageBoxButton.YesNoCancel:
                    btnPanel.Children.Add(MakeBtn(L("Str_Btn_Cancel", "Cancel"), MessageBoxResult.Cancel));
                    btnPanel.Children.Add(MakeBtn(cancelLabel ?? L("Str_Btn_No", "No"), MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn(confirmLabel ?? L("Str_Btn_Yes", "Yes"), MessageBoxResult.Yes, accent: true));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 0, 24, 20),
                Child = btnPanel
            });

            // Paint the same film-grain texture the app's panels use, behind the content, so the
            // dialog reads as part of the same surface family instead of a flat box.
            var contentGrid = new Grid();
            var grain = DialogFrame.GrainTexture(owner);
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
            // A message is drawn at the app's scale like the window it opens over, and grows to hold it.
            double scale = Controls.ScaledDialogFit.OwnerScale<Window>(owner,
                window => window is Shell.MainWindow main ? main.AppScale : null, window => window.Owner);
            if (scale != 1)
            {
                contentGrid.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
                win.Width *= scale;
            }
            win.Content = DialogFrame.WrapContent(owner, contentGrid);
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
            string title = "KillerPDF",
            bool fadeClose = true)
        {
            int result = -1;

            var win = new Window { Title = title, MinWidth = 380, MaxWidth = 760, SizeToContent = SizeToContent.WidthAndHeight };
            DialogFrame.Configure(win, owner, fade: fadeClose);

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

            // Every dialog title bar is the shared family chrome: wordmark, qualifier on its baseline,
            // the same lift as the main window, and the family close X.
            var titleBar = DialogFrame.BuildTitleBar(win, owner, title, () => win.Close());
            titleBar.Height = Application.Current.TryFindResource("DialogTitleBarHeight") is double titleHeight ? titleHeight : double.NaN;
            root.Children.Add(titleBar);

            var choiceBody = NoticeBody(message, MessageBoxImage.None, labels.Length > 1, out _);
            choiceBody.MaxWidth = 620;
            choiceBody.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(choiceBody);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            // Callers list the main choice first. Lay the row out in reverse so the main choice sits
            // last at the right edge, the family order, while the returned indices stay the same.
            for (int i = labels.Length - 1; i >= 0; i--)
            {
                int idx = i;
                var btn = Sized(UiKit.Make(labels[i], accent: i == accentIndex));
                btn.Margin = new Thickness(8, 0, 0, 0);
                btn.Click += (_, _2) => { result = idx; win.Close(); };
                btnPanel.Children.Add(btn);
            }
            root.Children.Add(new Border { Padding = new Thickness(16, 0, 24, 20), Child = btnPanel });

            var contentGrid = new Grid();
            var grain = DialogFrame.GrainTexture(owner);
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
            win.Content = DialogFrame.WrapContent(owner, contentGrid);
            win.ShowDialog();
            return result;
        }

        /// <summary>
        /// The family prompt for closing with unsaved work: Save (the accent, at the right edge),
        /// Don't Save and Cancel. Returns Yes for Save, No for Don't Save and Cancel for Cancel or a
        /// dialog closed without a choice.
        /// </summary>
        public static MessageBoxResult AskToSave(
            Window? owner,
            string message,
            string saveLabel,
            string dontSaveLabel,
            string cancelLabel,
            bool fadeClose = true)
            => ShowChoices(owner, message, [saveLabel, dontSaveLabel, cancelLabel], accentIndex: 0, fadeClose: fadeClose) switch
            {
                0 => MessageBoxResult.Yes,
                1 => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel,
            };

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
            MessageBoxResult? defaultResult = null,
            bool checkboxInitial = false)
        {
            var result = Show(owner, message, title, buttons, checkboxText: checkboxText,
                defaultResult: defaultResult, checkboxInitial: checkboxInitial);
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
        /// Enter default and Esc cancels. Used by the quit prompt, the unsaved-changes quit
        /// prompt and the install prompt (desktop shortcut + install for all users).
        /// cancelIsDefault moves Enter to Cancel, for a confirm that discards work.
        /// </summary>
        public static (bool confirmed, bool check1, bool check2) ShowTwoCheckPrompt(
            Window? owner,
            string message,
            string check1Text,
            bool check1Initial,
            string check2Text,
            bool check2Initial,
            string confirmLabel,
            string cancelLabel,
            bool cancelIsDefault = false)
        {
            var (choice, check1, check2) = ShowTwoCheckChoice(owner, message, check1Text, check1Initial,
                check2Text, check2Initial, confirmLabel, null, cancelLabel, cancelIsDefault);
            return (choice == 0, check1, check2);
        }

        /// <summary>
        /// The two-checkbox confirm with an optional second action between Cancel and the confirm
        /// button, such as Don't Save beside Save. Returns 0 for the confirm button, 1 for the
        /// second action and -1 for Cancel or a dialog closed without a choice.
        /// </summary>
        public static (int choice, bool check1, bool check2) ShowTwoCheckChoice(
            Window? owner,
            string message,
            string check1Text,
            bool check1Initial,
            string check2Text,
            bool check2Initial,
            string confirmLabel,
            string? secondaryLabel,
            string cancelLabel,
            bool cancelIsDefault = false)
        {
            int choice = -1;
            bool closeTabs = check1Initial;
            bool remember  = check2Initial;

            var win = new Window { Title = "KillerPDF", Width = secondaryLabel is null ? 390 : 440, SizeToContent = SizeToContent.Height };
            // fade:false - the app's own fade-out follows immediately on confirm; two fades
            // back-to-back read as lag (same reasoning as the unsaved-changes prompt).
            DialogFrame.Configure(win, owner, fade: false);

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

            // Every dialog title bar is the shared family chrome: wordmark, qualifier on its baseline,
            // the same lift as the main window, and the family close X.
            var titleBar = DialogFrame.BuildTitleBar(win, owner, "KillerPDF", () => win.Close());
            titleBar.Height = Application.Current.TryFindResource("DialogTitleBarHeight") is double titleHeight ? titleHeight : double.NaN;
            root.Children.Add(titleBar);

            root.Children.Add(NoticeBody(message, MessageBoxImage.None, true, out var textStack));

            var chk1 = UiKit.CheckBox(check1Text);
            chk1.Margin = new Thickness(0, 12, 0, 0);
            chk1.IsChecked = check1Initial;
            chk1.Checked   += (_, _2) => closeTabs = true;
            chk1.Unchecked += (_, _2) => closeTabs = false;
            textStack.Children.Add(chk1);

            var chk2 = UiKit.CheckBox(check2Text);
            chk2.Margin = new Thickness(0, 8, 0, 0);
            chk2.IsChecked = check2Initial;
            chk2.Checked   += (_, _2) => remember = true;
            chk2.Unchecked += (_, _2) => remember = false;
            textStack.Children.Add(chk2);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancelBtn = Sized(UiKit.Make(cancelLabel, false));
            cancelBtn.Margin   = new Thickness(8, 0, 0, 0);
            cancelBtn.IsCancel = true;
            cancelBtn.Click   += (_, _2) => win.Close();
            var quitBtn = Sized(UiKit.Make(confirmLabel, true));
            quitBtn.Margin    = new Thickness(8, 0, 0, 0);
            quitBtn.IsDefault = !cancelIsDefault;
            cancelBtn.IsDefault = cancelIsDefault;
            quitBtn.Click    += (_, _2) => { choice = 0; win.Close(); };
            btnPanel.Children.Add(cancelBtn);
            if (secondaryLabel is not null)
            {
                var secondaryBtn = Sized(UiKit.Make(secondaryLabel, false));
                secondaryBtn.Margin = new Thickness(8, 0, 0, 0);
                secondaryBtn.Click += (_, _2) => { choice = 1; win.Close(); };
                btnPanel.Children.Add(secondaryBtn);
            }
            btnPanel.Children.Add(quitBtn);
            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 0, 24, 20),
                Child = btnPanel
            });

            // Film grain across the whole card, same as Show().
            var contentGrid = new Grid();
            var grain = DialogFrame.GrainTexture(owner);
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
            win.Content = DialogFrame.WrapContent(owner, contentGrid);
            win.ShowDialog();
            return (choice, closeTabs, remember);
        }

        /// <summary>
        /// Themed "Password Required" prompt: the family dialog chrome (wordmark title bar, grain,
        /// red close, Esc to cancel) around a themed PasswordBox. Returns the entered password, or
        /// null if the user canceled / closed the dialog.
        /// </summary>
        public static string? PromptPassword(Window? owner, string filename)
            => PromptPassword(owner, filename, L("Str_PasswordProtected", "“{0}” is password protected."),
                L("Str_Btn_Open", "Open"));

        /// <summary>The same prompt with its own message ("{0}" is the file name) and confirm
        /// button, for the owner password that unlocks a restricted PDF.</summary>
        public static string? PromptPassword(Window? owner, string filename, string template, string confirmText)
        {
            string? result = null;

            var win = new Window { Width = 380, SizeToContent = SizeToContent.Height };
            DialogFrame.Configure(win, owner, fade: true);

            void CloseCancel() { result = null; win.Close(); }

            var body = new StackPanel();

            // Keep filename emphasis while allowing each language to place it naturally.
            var msg = new TextBlock { Foreground = R("TextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            int filenamePosition = template.IndexOf("{0}", StringComparison.Ordinal);
            msg.Inlines.Add(new System.Windows.Documents.Run(template[..filenamePosition]));
            msg.Inlines.Add(new System.Windows.Documents.Run(System.IO.Path.GetFileName(filename)) { FontWeight = FontWeights.SemiBold });
            msg.Inlines.Add(new System.Windows.Documents.Run(template[(filenamePosition + 3)..]));
            body.Children.Add(new Border { Padding = new Thickness(20, 4, 20, 10), Child = msg });

            var pw = UiKit.PasswordField();
            body.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 4), Child = pw });

            var openBtn = UiKit.Make(confirmText, accent: true);
            openBtn.IsDefault = true;
            openBtn.Click += (_, _2) => { result = pw.Password; win.Close(); };
            var cancelBtn = UiKit.Make(L("Str_Btn_Cancel", "Cancel"), accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => CloseCancel();
            body.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = UiKit.ButtonRow(openBtn, cancelBtn) });

            // Enter anywhere in the field submits.
            pw.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = pw.Password; win.Close(); } };

            win.Content = DialogFrame.Frame(win, owner, "KillerPDF", CloseCancel, body);
            win.Loaded += (_, _2) => pw.Focus();
            win.ShowDialog();
            return result;
        }

        /// <summary>Asks for one line of text, starting from <paramref name="initial"/>. Returns null on Cancel.</summary>
        public static string? PromptText(Window? owner, string title, string message, string initial, string confirmText)
        {
            string? result = null;

            var win = new Window { Width = 420, SizeToContent = SizeToContent.Height };
            DialogFrame.Configure(win, owner, fade: true);

            void CloseCancel() { result = null; win.Close(); }

            var body = new StackPanel();
            body.Children.Add(new Border
            {
                Padding = new Thickness(20, 4, 20, 10),
                Child = new TextBlock { Text = message, Foreground = R("TextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap }
            });

            TextBox box = UiKit.Field();
            box.Text = initial;
            body.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 4), Child = box });

            var okBtn = UiKit.Make(confirmText, accent: true);
            okBtn.IsDefault = true;
            okBtn.Click += (_, _2) => { result = box.Text; win.Close(); };
            var cancelBtn = UiKit.Make(L("Str_Btn_Cancel", "Cancel"), accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => CloseCancel();
            body.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = UiKit.ButtonRow(okBtn, cancelBtn) });

            win.Content = DialogFrame.Frame(win, owner, title, CloseCancel, body);
            win.Loaded += (_, _2) => { box.Focus(); box.SelectAll(); };
            win.ShowDialog();
            return result;
        }

        /// <summary>Asks for several lines of text, one entry per line. Returns null on Cancel.</summary>
        public static string? PromptLines(Window? owner, string title, string message, string confirmText)
        {
            string? result = null;

            var win = new Window { Width = 420, SizeToContent = SizeToContent.Height };
            DialogFrame.Configure(win, owner, fade: true);

            void CloseCancel() { result = null; win.Close(); }

            var body = new StackPanel();
            body.Children.Add(new Border
            {
                Padding = new Thickness(20, 4, 20, 10),
                Child = new TextBlock { Text = message, Foreground = R("TextBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap }
            });

            TextBox box = UiKit.Field();
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.NoWrap;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.VerticalContentAlignment = VerticalAlignment.Top;
            box.Height = 120;
            body.Children.Add(new Border { Padding = new Thickness(20, 0, 20, 4), Child = box });

            var okBtn = UiKit.Make(confirmText, accent: true);
            okBtn.Click += (_, _2) => { result = box.Text; win.Close(); };
            var cancelBtn = UiKit.Make(L("Str_Btn_Cancel", "Cancel"), accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => CloseCancel();
            body.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = UiKit.ButtonRow(okBtn, cancelBtn) });

            win.Content = DialogFrame.Frame(win, owner, title, CloseCancel, body);
            win.Loaded += (_, _2) => box.Focus();
            win.ShowDialog();
            return result;
        }

    }
}
