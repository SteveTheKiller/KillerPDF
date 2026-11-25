using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class DialogTextSmoke
{
    internal static int Run(Window owner, Assembly assembly, string folder)
    {
        Type theme = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!;
        Type dialog = assembly.GetType("KillerPDF.KillerDialog", true)!;
        string saveMessage = (string)Application.Current.FindResource("Str_Dlg_UnsavedExit");
        const string message = "The operation could not finish.\nReview the details and try again.";
        void Invoke(string method, params object?[] arguments)
            => dialog.GetMethods().Single(candidate => candidate.Name == method
                && candidate.GetParameters().Length == arguments.Length).Invoke(null, arguments);
        var cases = new (string Name, string Message, Action Open)[]
        {
            ("save", saveMessage, () => Invoke("AskToSave", owner, saveMessage, "Save", "Don't Save", "Cancel", false)),
            ("save-options", saveMessage, () => Invoke("ShowTwoCheckChoice", owner, saveMessage,
                "Close my open tabs", false, "Remember my choice", false, "Save", "Don't Save", "Cancel", false)),
            ("information", message, () => Invoke("Show", owner, message, "KillerPDF", MessageBoxButton.OK,
                MessageBoxImage.Information, false, null, null, false, null, null)),
            ("warning", message, () => Invoke("Show", owner, message, "KillerPDF", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, false, null, null, false, null, null)),
            ("error", message, () => Invoke("Show", owner, message, "KillerPDF", MessageBoxButton.OK,
                MessageBoxImage.Error, false, null, null, false, null, null)),
            ("confirmation", message, () => Invoke("Show", owner, message, "KillerPDF", MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question, false, null, null, false, null, null)),
            ("choices", message, () => Invoke("ShowChoices", owner, message, new[] { "Continue", "Cancel" }, 0, "KillerPDF", false)),
            ("checkbox", message, () => Invoke("ShowWithCheckbox", owner, message, "Remember my choice", "KillerPDF",
                MessageBoxButton.OKCancel, null, false)),
            ("quit", message, () => Invoke("ShowQuitPrompt", owner, message, "Close my open tabs", false,
                "Remember my choice", "Quit", "Cancel")),
            ("password", "sample.pdf is password protected.", () => Invoke("PromptPassword", owner,
                "sample.pdf", "{0} is password protected.", "Open")),
            ("text", message, () => Invoke("PromptText", owner, "KillerPDF", message, "", "Continue")),
            ("lines", message, () => Invoke("PromptLines", owner, "KillerPDF", message, "Continue"))
        };
        int count = 0;
        foreach (string name in Enum.GetNames(theme))
        {
            apply.Invoke(null, [Enum.Parse(theme, name)]);
            foreach (var test in cases)
            {
                Exception? failure = null;
                var clock = Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += (_, _) =>
                {
                    Window? shown = Application.Current.Windows.Cast<Window>()
                        .FirstOrDefault(window => window != owner && window.IsVisible);
                    if (shown is null)
                    {
                        if (clock.Elapsed > TimeSpan.FromSeconds(10))
                            throw new TimeoutException($"{test.Name} prompt did not open.");
                        return;
                    }
                    timer.Stop();
                    try
                    {
                        TextBlock[] text = Descendants(shown).OfType<TextBlock>().Where(block =>
                        {
                            string value = new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;
                            return value.Length > 0 && (value == test.Message || test.Message.Split('\n').Contains(value));
                        }).ToArray();
                        if (text.Length == 0) throw new InvalidOperationException($"{test.Name} message was not visible.");
                        foreach (TextBlock block in text)
                        {
                            // The language stacks are composite families with no Source; their faces are in the maps.
                            string face = block.FontFamily.Source
                                ?? string.Join(", ", block.FontFamily.FamilyMaps.Select(map => map.Target));
                            if (block.FontSize > 13 || face.Contains("Lacquer", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException($"{name} {test.Name} message uses {block.FontSize} DIP {face}.");
                        }
                        var image = new RenderTargetBitmap((int)Math.Ceiling(shown.ActualWidth),
                            (int)Math.Ceiling(shown.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        image.Render(shown);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                        using var file = File.Create(Path.Combine(folder, name + "-" + test.Name + ".png"));
                        png.Save(file);
                    }
                    catch (Exception error) { failure = error; }
                    finally { shown.Close(); }
                };
                timer.Start();
                try
                {
                    test.Open();
                }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                count++;
            }
        }
        File.WriteAllText(Path.Combine(folder, "result.txt"),
            $"PASS {cases.Length} dialog variants use normal body text across {Enum.GetNames(theme).Length} themes ({count} rendered cases).");
        Console.WriteLine($"PASS dialog typography in {count} rendered cases.");
        return 0;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
