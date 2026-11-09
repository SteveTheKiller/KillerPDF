using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using static DocumentStateSmoke;

internal static class CaptionThemeSmoke
{
    // DialogFrame.Frame gains optional parameters over time; reflection must pass every one, so the
    // defaults fill whatever the call leaves out.
    private static object?[] WithDefaults(MethodInfo method, object?[] given)
    {
        ParameterInfo[] parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (int index = 0; index < args.Length; index++)
            args[index] = index < given.Length ? given[index] : parameters[index].DefaultValue;
        return args;
    }

    internal static int RunButtonTransitions(Window owner, string folder, bool invokeActions = false)
    {
        if (invokeActions)
        {
            ShortcutHintsSmoke.Focus(owner);
            ShortcutHintsSmoke.MovePointer(owner, new Point(20, owner.ActualHeight - 30));
        }
        Assembly assembly = owner.GetType().Assembly;
        Type themes = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply")!;
        MethodInfo make = assembly.GetType("KillerPDF.UiKit", true)!.GetMethod("Make", [typeof(object), typeof(bool)])!;
        MethodInfo build = assembly.GetType("KillerPDF.DialogFrame", true)!.GetMethod("Frame")!;
        var windows = new List<Window>();
        var clicks = new Dictionary<Button, int>();
        void Theme(string name) => apply.Invoke(null, [Enum.Parse(themes, name)]);
        Button[] Open()
        {
            var window = new Window { Owner = owner, Width = 600, Height = 200, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var body = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Button[] buttons = new[] { false, true }.Select(accent => (Button)make.Invoke(null, ["Continue", accent])!).ToArray();
            foreach (Button button in buttons)
            {
                button.Width = 140; button.Height = 40; button.Margin = new Thickness(12);
                clicks.Add(button, 0);
                button.Click += (_, _) => clicks[button]++;
                body.Children.Add(button);
            }
            window.Content = build.Invoke(null, WithDefaults(build, [window, owner, "KillerPDF - Button controls", (Action)window.Close, body, null]));
            windows.Add(window); window.Show();
            Wait(() => buttons.All(button => button.IsVisible && button.IsEnabled && button.ActualWidth == 140 && button.ActualHeight == 40), "public factory buttons laid out");
            return buttons;
        }
        byte[] Render(Button button, string name)
        {
            button.UpdateLayout();
            Console.WriteLine($"CONTROL button render {name}: hover={button.IsMouseOver}, pressed={button.IsPressed}, enabled={button.IsEnabled}, keyboardFocus={button.IsKeyboardFocused}, size={button.ActualWidth}x{button.ActualHeight}.");
            if (invokeActions && (button.IsMouseOver || button.IsPressed || !button.IsEnabled))
                throw new InvalidOperationException("Button rest-render readiness changed its hover, pressed or enabled state.");
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(button), null, new Rect(0, 0, 140, 40));
            var bitmap = new RenderTargetBitmap(140, 40, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[140 * 40 * 4]; bitmap.CopyPixels(pixels, 140 * 4, 0);
            if (!Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] > 0))
                throw new InvalidOperationException("The owned button render has no painted pixels.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            byte[] bytes = stream.ToArray(); File.WriteAllBytes(Path.Combine(folder, name + ".png"), bytes);
            return bytes;
        }
        void LabelControl(Button button, byte[] present, string name)
        {
            object label = button.Content;
            try
            {
                button.Content = null;
                if (Render(button, name + "-label-absent").AsSpan().SequenceEqual(present))
                    throw new InvalidOperationException("The button label render did not distinguish its presence and absence controls.");
            }
            finally { button.Content = label; button.UpdateLayout(); }
        }
        void InvokeActions(Button[] buttons, int expected)
        {
            foreach (Button button in buttons)
            {
                var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
                invoke.Invoke();
                Wait(() => clicks[button] == expected, "same factory button action after theme transition");
                button.IsEnabled = false;
                try
                {
                    try { invoke.Invoke(); throw new InvalidOperationException("Disabled factory button accepted Invoke."); }
                    catch (System.Windows.Automation.ElementNotEnabledException) { }
                    if (clicks[button] != expected) throw new InvalidOperationException("Disabled button changed its action count.");
                }
                finally { button.IsEnabled = true; }
            }
            Console.WriteLine($"CONTROL factory button actions: both callbacks={expected}, disabled Invoke refused.");
        }
        try
        {
            Theme("Dark"); Button[] original = Open();
            byte[][] before = original.Select((button, index) => Render(button, "buttons-dark-before-" + index)).ToArray();
            for (int index = 0; index < 2; index++) LabelControl(original[index], before[index], "buttons-dark-" + index);
            Theme("SE98"); Button[] fresh = Open();
            byte[][] expected = fresh.Select((button, index) => Render(button, "buttons-classic-fresh-" + index)).ToArray();
            for (int index = 0; index < 2; index++) LabelControl(fresh[index], expected[index], "buttons-classic-" + index);
            for (int index = 0; index < 2; index++)
                if (before[index].AsSpan().SequenceEqual(expected[index]))
                    throw new InvalidOperationException("Independent Dark and classic button render controls did not differ.");
            windows[1].Close();
            for (int index = 0; index < 2; index++)
            {
                byte[] actual = Render(original[index], "buttons-classic-transition-" + index);
                Console.WriteLine($"CONTROL button transition: accent={index == 1}, matchesFreshClassic={actual.AsSpan().SequenceEqual(expected[index])}.");
                if (!actual.AsSpan().SequenceEqual(expected[index]))
                    throw new InvalidOperationException("An open dialog button did not adopt the independent fresh classic appearance.");
            }
            if (invokeActions) InvokeActions(original, 1);
            Theme("Dark");
            for (int index = 0; index < 2; index++)
                if (!Render(original[index], "buttons-dark-restored-" + index).AsSpan().SequenceEqual(before[index]))
                    throw new InvalidOperationException("An open dialog button did not restore its original Dark appearance.");
            if (invokeActions)
            {
                InvokeActions(original, 2);
                Console.WriteLine("PASS dialog button actions: same secondary and accent controls invoke once after each theme transition and refuse disabled Invoke.");
            }
            Console.WriteLine("PASS dialog button transitions: existing secondary and accent buttons match fresh classic controls and restore Dark.");
            return 0;
        }
        finally { foreach (Window window in windows) if (window.IsVisible) window.Close(); Theme("Dark"); }
    }

    internal static int RunCloseControls(Window owner, string folder, bool nativePointer = false, bool nativeKeyboard = false)
    {
        Rect work = SystemParameters.WorkArea;
        owner.WindowState = WindowState.Normal;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        if (nativePointer || nativeKeyboard) ShortcutHintsSmoke.Focus(owner);
        Assembly assembly = owner.GetType().Assembly;
        Type themes = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply")!;
        MethodInfo build = assembly.GetType("KillerPDF.DialogFrame", true)!.GetMethod("Frame")!;
        void Theme(string name) => apply.Invoke(null, [Enum.Parse(themes, name)]);
        void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        try
        {
            foreach (string theme in new[] { "Dark", "SE98" })
            {
                Theme(theme);
                int closeCalls = 0, controlCalls = 0;
                var window = new Window { Owner = owner, Width = 600, Height = 160, WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var control = new Button { Content = "Invoke presence control" };
                control.Click += (_, _) => controlCalls++;
                var halo = new Thickness(7, 8, 9, 10);
                var frame = (FrameworkElement)build.Invoke(null, WithDefaults(build, [window, owner, "KillerPDF - Close control",
                    (Action)(() => { closeCalls++; window.Close(); }), control, halo]))!;
                window.Content = frame;
                try
                {
                    window.Show();
                    Wait(() => frame.IsVisible && control.IsVisible && control.IsEnabled, "owned close component and Invoke control");
                    if (frame.Margin != halo) throw new InvalidOperationException("Frame ignored its explicit caller halo.");
                    Theme(theme == "Dark" ? "SE98" : "Dark"); frame.UpdateLayout();
                    if (frame.Margin != halo) throw new InvalidOperationException("Theme change replaced an explicit caller halo.");
                    Theme(theme); frame.UpdateLayout();
                    Invoke(control);
                    Wait(() => controlCalls == 1, "independent public Invoke presence control");
                    var bar = Descendants<DockPanel>(frame).Single().Children.OfType<Border>().First();
                    Button close = Descendants<Button>(bar).Single();
                    Wait(() => close.IsVisible && close.IsEnabled, "actual enabled caption close action");
                    string name = new ButtonAutomationPeer(close).GetName();
                    Console.WriteLine($"CONTROL caption close {theme}: independentInvokeCalls={controlCalls}, accessibleName={name}, halo={frame.Margin}.");
                    if (nativePointer)
                    {
                        bool down = false;
                        PreProcessInputEventHandler observed = (_, input) =>
                        {
                            if (input.StagingItem.Input is MouseButtonEventArgs mouse && mouse.ChangedButton == MouseButton.Left
                                && mouse.ButtonState == MouseButtonState.Pressed && ReferenceEquals(mouse.MouseDevice.ActiveSource?.RootVisual, window)) down = true;
                        };
                        Point screen = close.PointToScreen(new Point(close.ActualWidth / 2, close.ActualHeight / 2));
                        Point point = window.PointFromScreen(new Point(Math.Round(screen.X), Math.Round(screen.Y)));
                        InputManager.Current.PreProcessInput += observed;
                        try
                        {
                            ShortcutHintsSmoke.InjectClickAt(window, point, () =>
                            {
                                DependencyObject? hit = window.InputHitTest(point) as DependencyObject;
                                while (hit is not null && !ReferenceEquals(hit, close)) hit = hit is Visual
                                    ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
                                if (hit is null) throw new InvalidOperationException("The rounded pointer target is not the actual caption close button.");
                            });
                            Wait(() => down && closeCalls == 1 && !window.IsVisible, "native close down consumed and callback once");
                            ShortcutHintsSmoke.MovePointer(owner, new Point(owner.ActualWidth / 2, owner.ActualHeight / 2));
                            long released = Environment.TickCount64;
                            Wait(() => Environment.TickCount64 - released >= 200 && Mouse.LeftButton == MouseButtonState.Released
                                && Mouse.Captured is null, "native close release settles on owned client area");
                            if (closeCalls != 1) throw new InvalidOperationException("Pointer close invoked its callback more than once.");
                            Console.WriteLine($"CONTROL native caption close {theme}: downObserved={down}, callbackCalls={closeCalls}, released=True.");
                        }
                        finally { InputManager.Current.PreProcessInput -= observed; }
                    }
                    else if (nativeKeyboard)
                    {
                        ShortcutHintsSmoke.Focus(window);
                        close.Focus();
                        Wait(() => close.IsKeyboardFocused, "actual caption close keyboard focus");
                        bool down = false;
                        PreProcessInputEventHandler observed = (_, input) =>
                        {
                            if (input.StagingItem.Input is KeyEventArgs key && key.Key == Key.Space && key.IsDown
                                && ReferenceEquals(key.KeyboardDevice.FocusedElement, close)) down = true;
                        };
                        InputManager.Current.PreProcessInput += observed;
                        try
                        {
                            ShortcutHintsSmoke.Key(window, 0x20);
                            Wait(() => down, "native Space reached actual focused close button");
                            Wait(() => closeCalls == 1 && !window.IsVisible && Keyboard.IsKeyUp(Key.Space),
                                "native Space closes once and releases");
                            Console.WriteLine($"CONTROL native caption key {theme}: downAtClose={down}, callbackCalls={closeCalls}, released=True.");
                        }
                        finally { InputManager.Current.PreProcessInput -= observed; }
                    }
                    else
                    {
                        Invoke(close);
                        Wait(() => closeCalls == 1 && !window.IsVisible, "caption close Invoke callback and closed window");
                    }
                    if (name != (string)Application.Current.FindResource("Str_Sys_Close"))
                        throw new InvalidOperationException("Caption close action did not expose its localized accessible name.");
                }
                finally { if (window.IsVisible) window.Close(); }
            }
            if (Dirty(owner)) throw new InvalidOperationException("Caption controls changed empty owner state.");
            Console.WriteLine(nativePointer
                ? "PASS caption pointer close controls: actual owned native clicks close once in Dark and classic, release capture/buttons and preserve explicit caller halo."
                : nativeKeyboard ? "PASS caption keyboard close controls: native Space reaches the actual focused close button and closes once in Dark and classic with released input."
                : "PASS caption close controls: public Invoke closes once with a localized name in Dark and classic; explicit caller halo survives live theme changes.");
            return 0;
        }
        finally { Theme("Dark"); }
    }

    internal static int Run(Window owner, string folder, bool frameGeometry = false, bool grainCheck = false)
    {
        Rect work = SystemParameters.WorkArea;
        owner.WindowState = WindowState.Normal;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        Assembly assembly = owner.GetType().Assembly;
        Type themes = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply")!;
        MethodInfo build = assembly.GetType("KillerPDF.DialogFrame", true)!.GetMethod("Frame")!;
        const string title = "KillerPDF - Caption control";
        var windows = new List<Window>();
        void Theme(string name) => apply.Invoke(null, [Enum.Parse(themes, name)]);
        (Window Window, Border Bar, FrameworkElement Frame) Open()
        {
            var window = new Window { Owner = owner, Width = 600, Height = 100, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var frame = (FrameworkElement)build.Invoke(null, WithDefaults(build, [window, owner, title, (Action)window.Close, new Grid(), null]))!;
            window.Content = frame; windows.Add(window); window.Show();
            var bar = Descendants<DockPanel>(frame).Single().Children.OfType<Border>().First();
            Wait(() => bar.IsVisible && bar.ActualWidth > 300 && bar.ActualHeight >= 18, "actual shared caption rendered");
            return (window, bar, frame);
        }
        bool Plain(Border bar) => Descendants<TextBlock>(bar).Any(text => text.IsVisible && text.Text == title && text.ActualWidth > 20);
        (Thickness Margin, CornerRadius Radius, Thickness Stroke, double Shadow, double CaptionWidth, double Grain) Geometry(FrameworkElement frame, Border bar)
        {
            var host = (Panel)frame;
            var outer = host.Children.OfType<Border>().Single(border => border.GetType().Name == "PickerBorder");
            var shadow = host.Children.OfType<Border>().Single(border => border.Effect is DropShadowEffect);
            var dock = Descendants<DockPanel>(frame).Single();
            var content = (Panel)VisualTreeHelper.GetParent(dock);
            var grain = content.Children.OfType<Border>().SingleOrDefault(border => border.Background is ImageBrush);
            return (frame.Margin, outer.CornerRadius, outer.BorderThickness, ((DropShadowEffect)shadow.Effect).Opacity,
                bar.ActualWidth, grainCheck ? grain?.Opacity ?? 0 : 0);
        }
        void Save(FrameworkElement bar, string name)
        {
            bar.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth), (int)Math.Ceiling(bar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(bar);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(stream);
        }
        try
        {
            Theme("Dark"); var modern = Open(); Save(modern.Bar, "caption-dark-before");
            if (Plain(modern.Bar)) throw new InvalidOperationException("Modern caption absence control failed.");
            var darkGeometry = Geometry(modern.Frame, modern.Bar);
            if (frameGeometry) Save(modern.Frame, "frame-dark-before");
            Theme("SE98"); var classic = Open(); Save(classic.Bar, "caption-classic-fresh");
            if (!Plain(classic.Bar)) throw new InvalidOperationException("Fresh classic plain-caption presence control failed.");
            double classicHeight = classic.Bar.ActualHeight;
            var classicGeometry = Geometry(classic.Frame, classic.Bar);
            if (grainCheck && (darkGeometry.Grain <= 0 || classicGeometry.Grain != 0))
                throw new InvalidOperationException("Independent Dark/classic body grain presence and absence controls failed.");
            if (frameGeometry)
            {
                Save(classic.Frame, "frame-classic-fresh");
                if (classicGeometry.Equals(darkGeometry)) throw new InvalidOperationException("Frame geometry controls do not distinguish Dark from classic.");
            }
            classic.Window.Close();
            Wait(() => modern.Bar.IsVisible, "original caption survives theme switch");
            Save(modern.Bar, "caption-classic-transition");
            Console.WriteLine($"CONTROL caption transition: freshClassicHeight={classicHeight}, existingHeight={modern.Bar.ActualHeight}, existingPlain={Plain(modern.Bar)}.");
            if (!Plain(modern.Bar) || modern.Bar.ActualHeight != classicHeight)
                throw new InvalidOperationException("An open dialog did not adopt the fresh classic caption and geometry.");
            if (frameGeometry)
            {
                Save(modern.Frame, "frame-classic-transition");
                var actual = Geometry(modern.Frame, modern.Bar);
                Console.WriteLine($"CONTROL frame transition: freshClassic={classicGeometry}, existing={actual}.");
                if (!actual.Equals(classicGeometry)) throw new InvalidOperationException("An open dialog kept stale frame geometry after switching to classic.");
            }
            Theme("Dark"); Save(modern.Bar, "caption-dark-restored");
            if (Plain(modern.Bar)) throw new InvalidOperationException("An open dialog kept its plain caption after returning to Dark.");
            if (frameGeometry)
            {
                Save(modern.Frame, "frame-dark-restored");
                if (!Geometry(modern.Frame, modern.Bar).Equals(darkGeometry)) throw new InvalidOperationException("Dialog frame did not restore Dark geometry.");
                Console.WriteLine("PASS caption frame transition: live margins, corners, border, shadow and caption width match independent fresh-theme controls and restore after switching back.");
                if (grainCheck) Console.WriteLine("PASS caption frame grain: body grain follows independent Dark/classic presence and absence controls through both live theme changes.");
            }
            Console.WriteLine("PASS caption theme transition: actual shared dialog caption follows Dark, classic and Dark without reopening; independent fresh-caption controls passed.");
            return 0;
        }
        finally { foreach (Window window in windows) if (window.IsVisible) window.Close(); Theme("Dark"); }
    }
}
