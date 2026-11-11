using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

// The native child-window presenter is off in 1.9.3 (PdfViewer.NativePrimaryViewportEnabled = false), so smokes whose
// point is that presenter cannot observe it. They report SKIPPED with exit code 0 instead of waiting for a presenter
// that is never created. The flag is read from the shipped app so the smokes run again as soon as it is switched on.
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
