using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

// The native child-window presenter was off in 1.9.3 through 1.9.6, and is on again from 1.9.7 (#534). A build with
// PdfViewer.NativePrimaryViewportEnabled = false gives smokes whose point is that presenter nothing to observe, so they
// report SKIPPED with exit code 0 instead of waiting for it. The flag is read from the shipped app being tested.
internal static class NativePresenterGate
{
    internal const string Reason = "The native presenter is disabled in this build (PdfViewer.NativePrimaryViewportEnabled is false), so there is no presenter to observe.";

    internal static bool Disabled(Window owner)
    {
        Type viewer = owner.GetType().Assembly.GetType("KillerPDF.Controls.PdfViewer", true)!;
        FieldInfo flag = viewer.GetField("NativePrimaryViewportEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PdfViewer.NativePrimaryViewportEnabled is missing; update the presenter gate.");
        return !(bool)flag.GetValue(null)!;
    }

    internal static int Skip(Window owner, string folder, string smoke)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
        {
            passed = true, skipped = true, result = "SKIPPED", smoke, reason = Reason, machine = Environment.MachineName
        }, new JsonSerializerOptions { WriteIndented = true }));
        try { Console.Error.WriteLine("SKIPPED " + smoke + ": " + Reason); } catch (IOException) { }
        owner.Close();
        return 0;
    }
}
