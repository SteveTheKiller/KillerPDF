using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// #523: zooming from the footer buttons or the zoom box keeps the spot in the middle of the view where
// it was. The check reads the document point under the viewport's center, in the page grid's own
// unscaled coordinates, so it stays put across zoom levels only when the scroll offsets follow the zoom.
internal static class ZoomAnchorSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const int PageCount = 8;

    internal static int Run(Window owner, string appPath, string folder)
    {
        string stage = "fixture";
        Exception? failure = null;
        var events = new List<object>();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(25) };
        DispatcherUnhandledExceptionEventHandler dispatcherFailure = (_, error) =>
        {
            failure ??= error.Exception;
            error.Handled = true;
        };
        timer.Tick += (_, _) =>
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(120))
                failure ??= new TimeoutException("Overall deadline expired at " + stage);
            foreach (Window window in Application.Current.Windows.Cast<Window>().Where(w => w != owner && w.IsVisible).ToArray())
            {
                failure ??= new InvalidOperationException($"Unexpected window at {stage}: {window.GetType().FullName} {window.Title}");
                try { window.Close(); } catch (Exception error) { failure ??= error; }
            }
        };
        Application.Current.DispatcherUnhandledException += dispatcherFailure;
        try
        {
            string source = Path.Combine(folder, "zoom-anchor-pages.pdf");
            File.WriteAllBytes(source, Fixture(Assembly.Load("KillerPdf.Engine")));
            owner.Activate();
            timer.Start();
            Invoke(owner, "OpenFromExternal", source);
            Wait(() => Named<TextBlock>(owner, "FileNameLabel")?.Text == Path.GetFileName(source)
                && Named<ListBox>(owner, "PageList")?.Items.Count == PageCount);
            FrameworkElement viewer = Descendants<FrameworkElement>(owner)
                .Single(v => v.GetType().FullName == "KillerPDF.Controls.PdfViewer" && v.IsVisible);
            ScrollViewer viewport = Named<ScrollViewer>(viewer, "PagePreviewPanel")
                ?? throw new MissingMemberException("PagePreviewPanel");
            // The page grid sits inside the reading surface's content, so it is found by name, not by walking visuals.
            var grid = viewer.FindName("PageContentGrid") as FrameworkElement
                ?? throw new MissingMemberException("PageContentGrid");

            stage = "continuous-100";
            var continuous = (RadioButton)owner.FindName("ViewContinuousRadio");
            if (continuous.IsChecked != true) continuous.IsChecked = true;
            SelectZoom("1.0");
            Require(viewport.ScrollableHeight > viewport.ViewportHeight, "The fixture must scroll well past one screen at 100%.");

            stage = "scroll-middle";
            viewport.ScrollToVerticalOffset(viewport.ScrollableHeight / 2);
            Stable();
            Require(viewport.VerticalOffset > viewport.ViewportHeight, "The view must start well below the top.");
            Point anchor = Center();
            Record("start", anchor);

            stage = "zoom-in-button";
            Step(() => Click("ZoomInBtn"), anchor);
            stage = "zoom-out-button";
            Step(() => Click("ZoomOutBtn"), anchor);
            stage = "zoom-out-button-again";
            Step(() => Click("ZoomOutBtn"), anchor);
            stage = "zoom-box-200";
            Step(() => SelectZoom("2.0"), anchor);
            stage = "zoom-box-75";
            Step(() => SelectZoom("0.75"), anchor);

            // A document read from the top stays at the top when it zooms.
            stage = "top-zoom-in";
            viewport.ScrollToTop();
            Stable();
            Click("ZoomInBtn");
            Stable();
            Record(stage, Center());
            Require(viewport.VerticalOffset == 0, $"Zooming in at the top moved the view to {viewport.VerticalOffset:0.##}.");
            stage = "top-zoom-box";
            SelectZoom("1.5");
            Record(stage, Center());
            Require(viewport.VerticalOffset == 0, $"A zoom box pick at the top moved the view to {viewport.VerticalOffset:0.##}.");

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Console.WriteLine($"PASS zoom anchor: footer buttons and zoom box keep the view's center point within tolerance across {events.Count - 1} zoom changes; the top stays at the top.");
            return 0;

            Point Center()
            {
                owner.UpdateLayout();
                return viewport.TranslatePoint(new Point(viewport.ViewportWidth / 2, viewport.ViewportHeight / 2), grid);
            }

            double Zoom() => grid.LayoutTransform is ScaleTransform scale ? scale.ScaleY : double.NaN;

            void Record(string name, Point center) => events.Add(new
            {
                stage = name, zoom = Zoom(), centerX = center.X, centerY = center.Y,
                viewport.HorizontalOffset, viewport.VerticalOffset, viewport.ScrollableWidth, viewport.ScrollableHeight
            });

            void Step(Action zoom, Point expected)
            {
                double before = Zoom();
                zoom();
                Stable();
                Require(Math.Abs(Zoom() - before) > .01, $"{stage} did not change the zoom from {before:0.###}.");
                Point center = Center();
                Record(stage, center);
                // Two screen pixels of slack at the new zoom, in the grid's unscaled units.
                double tolerance = 2 / Zoom() + .5;
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                Require(Math.Abs(center.Y - expected.Y) <= tolerance,
                    $"{stage} moved the view's center from document y={expected.Y:0.#} to {center.Y:0.#} at zoom {Zoom():0.###}.");
                if (viewport.ScrollableWidth > 0)
                    Require(Math.Abs(center.X - expected.X) <= tolerance,
                        $"{stage} moved the view's center from document x={expected.X:0.#} to {center.X:0.#} at zoom {Zoom():0.###}.");
            }

            void Click(string name)
            {
                Wait(() => owner.FindName(name) is Button { IsVisible: true, IsEnabled: true });
                ((Button)owner.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            void SelectZoom(string tag)
            {
                Wait(() => Named<ComboBox>(owner, "ZoomBox") is { IsVisible: true, IsEnabled: true });
                ComboBox box = Named<ComboBox>(owner, "ZoomBox")!;
                ComboBoxItem choice = box.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, tag));
                box.SelectedItem = choice;
                Wait(() => ReferenceEquals(box.SelectedItem, choice));
                Stable();
            }

            // Offsets, extent and zoom have held for 400 ms, which outlasts the zoom settle timer's rerender.
            void Stable()
            {
                string previous = "";
                var steady = Stopwatch.StartNew();
                Wait(() =>
                {
                    owner.UpdateLayout();
                    string state = string.Join("|", Zoom(), viewport.ExtentWidth, viewport.ExtentHeight,
                        viewport.HorizontalOffset, viewport.VerticalOffset);
                    if (state != previous) { previous = state; steady.Restart(); }
                    return steady.Elapsed >= TimeSpan.FromMilliseconds(400);
                });
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"FAIL zoom anchor at {stage}: {error.Message}");
            throw;
        }
        finally
        {
            timer.Stop();
            Application.Current.DispatcherUnhandledException -= dispatcherFailure;
            File.WriteAllText(Path.Combine(folder, "zoom-anchor-events.json"),
                JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // Eight tall pages, each with a filled band, so the continuous view scrolls several screens at 100%.
    private static byte[] Fixture(Assembly engine)
    {
        Type type = engine.GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = type.GetConstructors().Single();
        object builder = constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
        for (int page = 0; page < PageCount; page++)
        {
            string content = string.Create(CultureInfo.InvariantCulture,
                $"q 1 1 1 rg 0 0 600 800 re f {page % 3 * .4} .3 {1 - page % 2 * .6} rg 60 {80 + page * 70} 480 120 re f Q\n");
            type.GetMethod("AddPage", [typeof(double), typeof(double), typeof(ReadOnlyMemory<byte>)])!
                .Invoke(builder, [600d, 800d, new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes(content))]);
        }
        return (byte[])type.GetMethod("Build", Type.EmptyTypes)!.Invoke(builder, null)!;
    }

    private static void Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Timed out waiting for the zoom check.");
            Thread.Sleep(10);
        }
    }
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static object? Invoke(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Members)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).SingleOrDefault(e => e.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
