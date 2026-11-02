using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TabDragPreviewRenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DragGhostRendersTheSourceTabAtItsOriginalSize(bool bottom)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { CheckDragGhost(bottom); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckDragGhost(bool bottom)
    {
        string root = FindRepositoryRoot();
        var window = XDocument.Load(Path.Combine(root, "Shell", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement ghost = window.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "TabDragGhost");
        ghost.Name = XName.Get(ghost.Name.LocalName, "clr-namespace:KillerFamily.Windowing;assembly=KillerPDF.Tests");
        var context = new System.Windows.Markup.ParserContext();
        context.XmlnsDictionary["snap"] = "clr-namespace:KillerFamily.Windowing;assembly=KillerPDF.Tests";
        var preview = (System.Windows.FrameworkElement)System.Windows.Markup.XamlReader.Parse(ghost.ToString(), context);
        preview.Opacity = 1;
        preview.Effect = null;
        var label = new System.Windows.Controls.TextBlock { Text = "KillerBench", FontSize = 12 };
        var tab = new KillerPDF.Controls.RetroTabBorder
        {
            Width = 210, Height = 30, CornerRadius = new System.Windows.CornerRadius(3, 3, 0, 0),
            Background = System.Windows.Media.Brushes.DimGray,
            BorderBrush = System.Windows.Media.Brushes.Cyan,
            BorderThickness = new System.Windows.Thickness(1, 3, 1, 0),
            Padding = new System.Windows.Thickness(7, 2, 7, 2), Child = label
        };
        var size = new System.Windows.Size(210, 30);
        if (bottom)
        {
            tab.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            tab.RenderTransform = new System.Windows.Media.ScaleTransform(1, -1);
            label.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            label.RenderTransform = new System.Windows.Media.ScaleTransform(1, -1);
        }
        var container = new System.Windows.Controls.Grid { Width = 210, Height = 30 };
        container.Children.Add(tab);
        container.Measure(size);
        container.Arrange(new System.Windows.Rect(size));
        var livePreview = Assert.IsType<KillerFamily.Windowing.TabDragPreview>(preview);
        livePreview.Source = container;
        preview.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(size, preview.DesiredSize);
        preview.Arrange(new System.Windows.Rect(size));
        byte[] Pixels(System.Windows.Media.Visual visual)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(210, 30, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[210 * 30 * 4];
            bitmap.CopyPixels(pixels, 210 * 4, 0);
            return pixels;
        }
        Assert.Equal(Pixels(container), Pixels(preview));
        byte[] original = Pixels(preview);
        container.RenderTransform = new System.Windows.Media.TranslateTransform(73, 0);
        Assert.Equal(original, Pixels(preview));
        container.RenderTransform = null;
        label.Text = "Changed title";
        tab.UpdateLayout();
        Assert.Equal(Pixels(container), Pixels(preview));
        Assert.False(original.SequenceEqual(Pixels(preview)), "The preview must reflect the changed title.");
        Assert.Same(label, tab.Child);
        livePreview.Source = null;
        preview.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(new System.Windows.Size(), preview.DesiredSize);
    }


    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(source))!;
}
