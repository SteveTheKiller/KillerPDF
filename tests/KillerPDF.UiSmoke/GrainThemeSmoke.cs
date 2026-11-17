using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class GrainThemeSmoke
{
    internal static int Run(Window owner, Assembly assembly, string folder)
    {
        var shared = (ImageBrush)owner.FindResource("GrainBrushShared");
        var combo = new ComboBox
        {
            Style = (Style)owner.FindResource("FooterZoomCombo"),
            ItemsSource = new[] { "100%", "200%" }, SelectedIndex = 0
        };
        var window = new Window
        {
            Owner = owner, Content = combo, Width = 220, Height = 100,
            Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            window.Show();
            combo.ApplyTemplate();
            combo.IsDropDownOpen = true;
            Pump();
            combo.IsDropDownOpen = false;
            // WPF seals brushes referenced by style setters when the style is first used.
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BackgroundProperty, shared));
            style.Seal();
            Console.WriteLine("Shared grain frozen after style sealing: " + shared.IsFrozen);
            Type theme = assembly.GetType("KillerPDF.Services.Theme", true)!;
            MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!
                .GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!;
            foreach (string name in new[] { "Greed" }.Concat(Enum.GetNames(theme)))
            {
                apply.Invoke(null, [Enum.Parse(theme, name)]);
                Pump();
                var tile = (ImageBrush)Application.Current.FindResource("GrainTileBrush");
                if (!ReferenceEquals(shared.ImageSource, tile.ImageSource))
                    throw new InvalidOperationException(name + " left shared grain on the previous theme.");
            }
            File.WriteAllText(Path.Combine(folder, "result.txt"),
                "PASS dropdown followed by Greed and all 14 themes; shared grain matches the published tile.");
            Console.WriteLine("PASS grain theme switching after dropdown.");
            return 0;
        }
        finally { window.Close(); }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
