using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests
{
    /// <summary>
    /// A dialog's confirm button (Unlock) keeps hinted text: an effect on its caption would
    /// rasterize it and blur it at rest. Reads the shipped Black theme and the kit's
    /// OutlineButton style from source, since the test project does not load the app assembly.
    /// </summary>
    public sealed class OutlineButtonCaptionTests
    {
        [Fact]
        public void OutlineButtonCaptionsAreNotRasterized()
        {
            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { Check(); }
                catch (Exception ex) { failure = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }

        private static void Check()
        {
            string repo = FindRepoRoot();
            var root = new Border { Width = 300 };
            using (var theme = File.OpenRead(Path.Combine(repo, "Themes", "Black.xaml")))
                root.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(theme));
            var button = new Button { Content = "Unlock", Style = LoadOutlineButton(repo) };
            root.Child = button;
            root.Measure(new Size(300, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();

            var label = (ContentPresenter)button.Template.FindName("label", button);
            Assert.NotNull(label);
            Assert.Null(label.Effect);
        }

        /// <summary>The OutlineButton style exactly as written in the kit's Controls.xaml.</summary>
        private static Style LoadOutlineButton(string repo)
        {
            XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var doc = XDocument.Load(Path.Combine(repo, "Controls", "Controls.xaml"));
            var style = new XElement(doc.Descendants(w + "Style")
                .First(s => (string?)s.Attribute(x + "Key") == "OutlineButton"));
            foreach (var attr in style.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration).ToList())
                attr.Remove();
            // ThemeMotif paints the ink border behind the face, never the caption, and its type
            // lives in the app assembly this project does not load.
            XNamespace kui = "clr-namespace:KillerPDF.Controls";
            foreach (var attr in style.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.Name.Namespace == kui).ToList())
                attr.Remove();
            style.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
            return (Style)XamlReader.Parse(style.ToString());
        }

        private static string FindRepoRoot()
        {
            string file = Path.Combine("Controls", "Controls.xaml");
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, file))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new DirectoryNotFoundException(file + " not found above the test output.");
        }
    }
}
