using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace KillerPDF.PreviewHandler;

internal static partial class PreviewUiThread
{
    // prevhost.exe is only system DPI aware, but Outlook and Explorer hand the preview its bounds in
    // physical pixels. A system-aware window on a monitor scaled unlike the primary one gets those
    // bounds rescaled and lands off to the side (#410), so this thread runs per-monitor aware.
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    private static readonly Lazy<Dispatcher> Owner = new(Start);

    internal static void Invoke(Action action)
    {
        Dispatcher dispatcher = Owner.Value;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    internal static T Invoke<T>(Func<T> action)
    {
        Dispatcher dispatcher = Owner.Value;
        return dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
    }

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Before any window exists on this thread; a failure leaves the host's own mode.
                SetThreadDpiAwarenessContext(PerMonitorAwareV2);
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(DispatcherPriority.Send, () => ready.TrySetResult(dispatcher));
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "KillerPDF preview UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
