using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Round flag badges for the language picker, the same art killertools.net uses in its own
    /// language menu. Each flag is drawn on a 24x24 grid and clipped to a circle, so there is no
    /// image file to ship and it stays sharp at any scale.
    /// </summary>
    internal static class LanguageFlags
    {
        private static readonly Dictionary<Locale, DrawingImage> Cache = new();

        /// <summary>A circular flag of the given diameter with a faint rim.</summary>
        internal static FrameworkElement Badge(Locale locale, double size = 18)
        {
            var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(new Image
            {
                Source = Get(locale),
                Width = size,
                Height = size,
                Stretch = Stretch.Fill,
                Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2),
            });
            var rim = new Ellipse { StrokeThickness = 1, Opacity = 0.25, IsHitTestVisible = false };
            rim.SetResourceReference(Shape.StrokeProperty, "TextBrush");
            host.Children.Add(rim);
            return host;
        }

        private static DrawingImage Get(Locale locale)
        {
            if (Cache.TryGetValue(locale, out var cached)) return cached;
            var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, 24, 24)) };
            Draw(locale, group.Children);
            var image = new DrawingImage(group);
            image.Freeze();
            Cache[locale] = image;
            return image;
        }

        private static void Draw(Locale locale, DrawingCollection d)
        {
            switch (locale)
            {
                case Locale.Bn:
                    R(d, "#006a4e", 0, 0, 24, 24); C(d, "#f42a41", 10.5, 12, 6); break;
                case Locale.CsCZ:
                    R(d, "#ffffff", 0, 0, 24, 12); R(d, "#d7141a", 0, 12, 24, 12);
                    P(d, "#11457e", "M0 0 L12 12 L0 24 Z"); break;
                case Locale.De:
                    R(d, "#000000", 0, 0, 24, 8); R(d, "#dd0000", 0, 8, 24, 8); R(d, "#ffce00", 0, 16, 24, 8); break;
                case Locale.Es:
                    R(d, "#c60b1e", 0, 0, 24, 24); R(d, "#ffc400", 0, 6, 24, 12); break;
                case Locale.Fr:
                    R(d, "#0055a4", 0, 0, 8, 24); R(d, "#ffffff", 8, 0, 8, 24); R(d, "#ef4135", 16, 0, 8, 24); break;
                case Locale.HuHU:
                    R(d, "#ce2939", 0, 0, 24, 8); R(d, "#ffffff", 0, 8, 24, 8); R(d, "#477050", 0, 16, 24, 8); break;
                case Locale.ItIT:
                    R(d, "#009246", 0, 0, 8, 24); R(d, "#ffffff", 8, 0, 8, 24); R(d, "#ce2b37", 16, 0, 8, 24); break;
                case Locale.JaJP:
                    R(d, "#ffffff", 0, 0, 24, 24); C(d, "#bc002d", 12, 12, 7); break;
                case Locale.KkKZ:
                    R(d, "#00afca", 0, 0, 24, 24); C(d, "#f6d34a", 12, 12, 4.5);
                    S(d, "#f6d34a", 1, "M12 3 v3 M12 18 v3 M3 12 h3 M18 12 h3 M5.6 5.6 l2.1 2.1 M16.3 16.3 l2.1 2.1 M18.4 5.6 l-2.1 2.1 M7.7 16.3 l-2.1 2.1"); break;
                case Locale.KoKR:
                    R(d, "#ffffff", 0, 0, 24, 24);
                    P(d, "#0047a0", "M12 7 A5 5 0 0 1 12 17 Z");
                    P(d, "#cd2e3a", "M12 7 A5 5 0 0 0 12 17 Z");
                    C(d, "#cd2e3a", 12, 9.5, 2.5); C(d, "#0047a0", 12, 14.5, 2.5);
                    S(d, "#000000", 1, "M3.5 6.5 l3 -3 M4.7 7.7 l3 -3 M16.3 20.5 l3 -3 M17.5 21.7 l3 -3 M3.5 17.5 l3 3 M4.7 16.3 l3 3 M16.3 3.5 l3 3 M17.5 2.3 l3 3"); break;
                case Locale.NbNO:
                    R(d, "#ba0c2f", 0, 0, 24, 24);
                    S(d, "#ffffff", 6, "M7 0 v24 M0 11 h24");
                    S(d, "#00205b", 3, "M7 0 v24 M0 11 h24"); break;
                case Locale.PlPL:
                    R(d, "#ffffff", 0, 0, 24, 12); R(d, "#dc143c", 0, 12, 24, 12); break;
                case Locale.PtBR:
                    R(d, "#006600", 0, 0, 10, 24); R(d, "#ff0000", 10, 0, 14, 24);
                    C(d, "#ffcc00", 10, 12, 5); C(d, "#ffffff", 10, 12, 3); break;
                case Locale.RuRU:
                    R(d, "#ffffff", 0, 0, 24, 8); R(d, "#0039a6", 0, 8, 24, 8); R(d, "#d52b1e", 0, 16, 24, 8); break;
                case Locale.SlSI:
                    R(d, "#ffffff", 0, 0, 24, 8); R(d, "#003da5", 0, 8, 24, 8); R(d, "#ed1c24", 0, 16, 24, 8);
                    const string shield = "M5 4 h7 v5 c0 3 -3.5 4.5 -3.5 4.5 S5 12 5 9 Z";
                    P(d, "#003da5", shield); S(d, "#ed1c24", 0.6, shield);
                    P(d, "#ffffff", "M5.7 9 l1.2 -1.3 l0.8 0.8 l0.8 -2 l1.1 2.2 l0.8 -0.7 l0.9 1 v1.1 c-1.5 1.1 -4.1 1.1 -5.6 0 Z");
                    C(d, "#ffdd00", 7.2, 5.4, 0.35); C(d, "#ffdd00", 9.8, 5.4, 0.35); C(d, "#ffdd00", 8.5, 6.3, 0.35); break;
                case Locale.SvSE:
                    R(d, "#006aa7", 0, 0, 24, 24); S(d, "#fecc00", 4, "M8.5 0 v24 M0 12 h24"); break;
                case Locale.TrTR:
                    R(d, "#e30a17", 0, 0, 24, 24); C(d, "#ffffff", 9.5, 12, 5); C(d, "#e30a17", 11, 12, 4);
                    P(d, "#ffffff", "M15.5 9.4 L16.12 11.15 L17.97 11.2 L16.5 12.32 L17.03 14.1 L15.5 13.05 L13.97 14.1 L14.5 12.32 L13.03 11.2 L14.88 11.15 Z"); break;
                case Locale.UkUA:
                    R(d, "#0057b7", 0, 0, 24, 12); R(d, "#ffd700", 0, 12, 24, 12); break;
                case Locale.ViVN:
                    R(d, "#da251d", 0, 0, 24, 24);
                    P(d, "#ffff00", "M12 5 L13.65 9.85 L18.75 9.85 L14.62 12.85 L16.2 17.7 L12 14.7 L7.8 17.7 L9.38 12.85 L5.25 9.85 L10.35 9.85 Z"); break;
                case Locale.ZhCN:
                    R(d, "#de2910", 0, 0, 24, 24);
                    P(d, "#ffde00", "M4 3 L4.9 5.6 L7.6 5.6 L5.4 7.3 L6.2 9.9 L4 8.3 L1.8 9.9 L2.6 7.3 L0.4 5.6 L3.1 5.6 Z"); break;
                case Locale.ZhTW:
                    R(d, "#fe0000", 0, 0, 24, 24); R(d, "#000095", 0, 0, 12, 12);
                    P(d, "#ffffff", "M6 3 L7.2 6.6 L11 6.6 L7.9 8.8 L9.1 12.4 L6 10.2 L2.9 12.4 L4.1 8.8 L1 6.6 L4.8 6.6 Z"); break;
                default: // en-US
                    R(d, "#ffffff", 0, 0, 24, 24);
                    for (int i = 0; i < 7; i++) R(d, "#b22234", 0, i * 3.7, 24, i == 6 ? 1.8 : 1.85);
                    R(d, "#3c3b6e", 0, 0, 11, 12.95); break;
            }
        }

        private static Brush B(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static void R(DrawingCollection d, string fill, double x, double y, double w, double h) =>
            d.Add(new GeometryDrawing(B(fill), null, new RectangleGeometry(new Rect(x, y, w, h))));

        private static void C(DrawingCollection d, string fill, double cx, double cy, double r) =>
            d.Add(new GeometryDrawing(B(fill), null, new EllipseGeometry(new Point(cx, cy), r, r)));

        private static void P(DrawingCollection d, string fill, string path) =>
            d.Add(new GeometryDrawing(B(fill), null, Geometry.Parse(path)));

        private static void S(DrawingCollection d, string stroke, double width, string path) =>
            d.Add(new GeometryDrawing(null, new Pen(B(stroke), width), Geometry.Parse(path)));
    }
}
