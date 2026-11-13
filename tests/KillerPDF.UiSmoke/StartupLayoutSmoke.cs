using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using static DocumentStateSmoke;

internal static class StartupLayoutSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    internal static int Stage(string appPath, string data)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly app = Assembly.LoadFrom(appPath);
        Type storeType = app.GetType("KillerPDF.Services.UpdateRestartStore", true)!;
        using var account = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string identity = Path.Combine(Path.GetDirectoryName(appPath)!, "KillerPDF.App.exe").ToUpperInvariant();
        object store = Activator.CreateInstance(storeType, Members, null,
            [Path.Combine(data, "UpdateRestart"), identity, account.User!.Value, process.SessionId, null, null], null)!;
        storeType.GetMethod("Stage")!.Invoke(store, [Array.Empty<string>(), null]);
        return 0;
    }

    internal static void PrepareRecent(Assembly app, string appPath, string data, string folder)
    {
        string file = Path.Combine(folder, "recent.pdf");
        File.WriteAllBytes(file, SignedCopySmoke.Fixture(measurement: false));
        Type type = app.GetType("KillerPDF.App", true)!;
        var setting = type.GetMethod("SetSetting", Members)!;
        setting.Invoke(null, ["RecentFiles", file]);
        setting.Invoke(null, ["RememberOpenFiles", "0"]);
        setting.Invoke(null, ["NoRecentFiles", "0"]);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(StartupLayoutSmoke).Assembly.Location);
        foreach (string argument in new[] { "--stage-empty-recent-restart", appPath, data }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(15000)) { child.Kill(true); throw new TimeoutException("Restart staging timed out."); }
        if (child.ExitCode != 0) throw new InvalidOperationException("Restart staging failed.");
        type.GetMethod("LoadPendingUpdateRestart", Members)!.Invoke(null, null);
    }

    internal static int Recent(Window owner, string folder)
    {
        owner.Opacity = 1;
        owner.Width = 1100; owner.Height = 760;
        Pump(); owner.UpdateLayout(); Pump();
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        var list = (ItemsControl)viewer.FindName("RecentFilesList");
        var box = (FrameworkElement)viewer.FindName("RecentFilesBox");
        CaptureOwnedWindow(owner, Path.Combine(folder, "recent-after-update.png"));
        if (list.Items.Count != 1 || !box.IsVisible)
            throw new InvalidOperationException($"Update reopened empty without its recent column: rows={list.Items.Count}, visible={box.IsVisible}, width={viewer.ActualWidth}.");
        owner.Width = 500; owner.UpdateLayout(); Pump();
        if (box.IsVisible) throw new InvalidOperationException("Narrow-pane negative control kept the recent column.");
        owner.Width = 1100; owner.UpdateLayout(); Pump();
        if (!box.IsVisible || list.Items.Count != 1) throw new InvalidOperationException("Widening did not restore the same recent entry.");
        File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS recent column populated immediately after a real empty update handoff; narrow/wide controls pass.");
        return 0;
    }

    internal static int About(Window owner, string folder)
    {
        owner.Opacity = 1;
        var label = (UIElement)owner.FindName("VersionLabel");
        label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        var about = (FrameworkElement)owner.FindName("AboutOverlay");
        Wait(() => about.IsVisible && about.Opacity > .95, "About opens from version label");
        var link = (CheckBox)owner.FindName("LinkConfirmCheck");
        var startup = (FrameworkElement)owner.FindName("AboutStartupUpdateCheck");
        TextBlock clear = Descendants<TextBlock>(about).Single(block => block.Inlines.OfType<Hyperlink>()
            .Any(hyperlink => hyperlink.Inlines.OfType<Run>().Any(run => run.Text == "Clear all Data")));
        Type manager = owner.GetType().Assembly.GetType("KillerPDF.Services.ThemeManager", true)!;
        Type theme = owner.GetType().Assembly.GetType("KillerPDF.Services.Theme", true)!;
        foreach (string name in new[] { "Dark", "Light", "SE98" })
        {
            manager.GetMethod("Apply")!.Invoke(null, [Enum.Parse(theme, name)]);
            Pump(); owner.UpdateLayout(); Pump();
            Point check = link.TransformToAncestor(about).Transform(new Point(0, link.ActualHeight / 2));
            Point first = startup.TransformToAncestor(about).Transform(new Point(0, 0));
            Point action = clear.TransformToAncestor(about).Transform(new Point(0, clear.ActualHeight / 2));
            CaptureOwnedWindow(owner, Path.Combine(folder, "about-" + name + ".png"));
            var tray = (CheckBox)owner.FindName("AboutCloseToTray");
            var recent = (CheckBox)owner.FindName("NoRecentCheck");
            double SquareY(CheckBox control)
            {
                var square = (FrameworkElement)control.Template.FindName("box", control);
                return square.TransformToAncestor(about).Transform(new Point()).Y;
            }
            if (Math.Abs(SquareY(tray) - SquareY(recent)) > 1)
                throw new InvalidOperationException($"About checkbox squares differ in {name}.");
            var zoom = (ComboBox)owner.FindName("ZoomBox");
            zoom.ApplyTemplate();
            var editor = (TextBox)zoom.Template.FindName("PART_EditableTextBox", zoom);
            editor.ApplyTemplate();
            var input = (FrameworkElement)editor.Template.FindName("InputRoot", editor);
            if (input.ActualHeight > editor.ActualHeight + 0.5)
                throw new InvalidOperationException($"Zoom text exceeds editor in {name}: {input.ActualHeight}/{editor.ActualHeight}.");
            File.AppendAllText(Path.Combine(folder, "alignment.txt"),
                $"{name}: squares={SquareY(tray):F2}/{SquareY(recent):F2}, zoom={zoom.ActualHeight}, editor={editor.ActualHeight}, root={input.ActualHeight}\n");
            if (Math.Abs(check.X - first.X) > 2 || action.X <= check.X || Math.Abs(check.Y - action.Y) > 2)
                throw new InvalidOperationException($"Unbalanced About columns in {name}: startup={first}, links={check}, clear={action}.");
        }
        File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS About link confirmation left and Clear all Data right share the bottom row in Dark, Light and 98SE.");
        return 0;
    }
}
