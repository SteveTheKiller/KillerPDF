using System.Windows;

namespace KillerPDF.Services
{
    // #169: a plain page rotation used to reload with SaveTempAndReload's keepAnnotations
    // default, which cleared every overlay annotation - committed, unsaved user work was
    // destroyed the moment a page was rotated. Rotation now keeps the annotations and maps
    // their canvas coordinates through the turn instead. Coordinates live in the page's
    // render-dim space (the visual frame the user drew on); rotating the page turns that
    // frame, so the old (w, h) render dims become (h, w) when the reload re-renders.
    internal static class AnnotationRotate
    {
        /// <summary>Remaps one page's annotations for an in-app rotation by <paramref name="delta"/>
        /// degrees (clockwise positive, matching the render path), where <paramref name="oldW"/> and
        /// <paramref name="oldH"/> are the page's render dims BEFORE the turn. Everything on the page turns
        /// with it: highlights, covers, ink, shapes and measurements map point by point, text boxes map their
        /// box and read turned, and pictures and signatures turn their own pixels, strokes and outlines.</summary>
        public static void Remap(IEnumerable<PageAnnotation> annots, int delta, double oldW, double oldH)
        {
            int d = ((delta % 360) + 360) % 360;
            if (d == 0) return;
            double newW = d == 90 || d == 270 ? oldH : oldW;
            double newH = d == 90 || d == 270 ? oldW : oldH;

            Point MapPoint(Point p) => d switch
            {
                // Forward quarter-turn of the visual frame, same convention as the render path's
                // clockwise bitmap rotation (see PdfBurn's VisualToPageMatrix, which is its inverse).
                90  => new Point(oldH - p.Y, p.X),
                270 => new Point(p.Y, oldW - p.X),
                _   => new Point(oldW - p.X, oldH - p.Y),   // 180
            };
            Rect MapRect(Rect r)
            {
                var a = MapPoint(new Point(r.X, r.Y));
                var b = MapPoint(new Point(r.Right, r.Bottom));
                return new Rect(a, b);   // Rect(Point, Point) normalizes the corners
            }

            // The replacement text of an in-place edit sits on a cover that turns with the page, so it
            // turns too and stays aligned with what it replaced. Free text keeps its own orientation.
            var list = annots as IList<PageAnnotation> ?? [.. annots];
            var coverPairs = new HashSet<string>(list.OfType<CoverAnnotation>()
                .Where(c => c.PairId.Length > 0).Select(c => c.PairId));

            foreach (var annot in list)
            {
                switch (annot)
                {
                    // Everything on a page turns with it. A text box keeps its place on the content and
                    // reads turned, as an in-place edit always did.
                    case TextAnnotation text:
                        text.Position = MapRect(new Rect(text.Position, text.FootprintSize)).Location;
                        text.Rotation = TextTurn.Normalize(text.Rotation + d);
                        break;

                    // A picture or signature turns its own pixels, strokes and outlines by the same
                    // quarter turn, and its rectangle maps like a highlight's.
                    case PlacedAnnotation placed:
                        placed.Position = MapRect(new Rect(placed.Position,
                            new Size(placed.SourceWidth * placed.Scale, placed.SourceHeight * placed.Scale))).Location;
                        TurnPlacedContent(placed, d);
                        break;

                    case HighlightAnnotation ha:   // includes CoverAnnotation
                        ha.Bounds = MapRect(ha.Bounds);
                        if (ha.Erases != null)
                            foreach (var e in ha.Erases)
                                for (int i = 0; i < e.Points.Count; i++)
                                    e.Points[i] = MapPoint(e.Points[i]);
                        break;

                    case InkAnnotation ia:
                        for (int i = 0; i < ia.Points.Count; i++)
                            ia.Points[i] = MapPoint(ia.Points[i]);
                        break;

                    case MeasurementAnnotation ma:   // measures what it marks, so it turns with it
                        for (int i = 0; i < ma.Points.Count; i++)
                            ma.Points[i] = MapPoint(ma.Points[i]);
                        if (ma.LabelCenter is Point label) ma.LabelCenter = MapPoint(label);
                        break;
                }
            }
        }

        // Turns a placed item's content clockwise by d degrees inside its own SourceWidth x SourceHeight
        // space, the same direction MapPoint turns the page, and swaps that space for a quarter turn.
        private static void TurnPlacedContent(PlacedAnnotation placed, int d)
        {
            double sw = placed.SourceWidth, sh = placed.SourceHeight;
            Point Turn(Point p) => d switch
            {
                90 => new Point(sh - p.Y, p.X),
                270 => new Point(p.Y, sw - p.X),
                _ => new Point(sw - p.X, sh - p.Y),
            };
            System.Windows.Media.Matrix matrix = d switch
            {
                90 => new System.Windows.Media.Matrix(0, 1, -1, 0, sh, 0),
                270 => new System.Windows.Media.Matrix(0, -1, 1, 0, 0, sw),
                _ => new System.Windows.Media.Matrix(-1, 0, 0, -1, sw, sh),
            };
            switch (placed)
            {
                // A picture's bytes stay as they are; it records the turn and is drawn turned, so a picture
                // lifted from the page (stored as its own PDF) turns as losslessly as a PNG.
                case ImageAnnotation image:
                    image.Rotation = TextTurn.Normalize(image.Rotation + d);
                    break;
                case SignatureAnnotation signature:
                    if (!string.IsNullOrEmpty(signature.ImageData))
                        try { signature.ImageData = TurnPixels(signature.ImageData, d); }
                        catch (Exception error) when (error is NotSupportedException or System.IO.IOException or FormatException
                            or InvalidOperationException or ArgumentException) { }
                    foreach (var stroke in signature.Strokes)
                        for (int i = 0; i < stroke.Count; i++) stroke[i] = Turn(stroke[i]);
                    if (signature.Vectors is { } vectors)
                        foreach (var vector in vectors) vector.Data = TurnPath(vector.Data, matrix);
                    break;
            }
            if (d is 90 or 270) (placed.SourceWidth, placed.SourceHeight) = (sh, sw);
            placed.CachedBitmap = null;
        }

        // A quarter or half turn of the pixels, lossless, saved as PNG.
        private static string TurnPixels(string base64, int d)
        {
            using var input = new System.IO.MemoryStream(Convert.FromBase64String(base64));
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var turned = new System.Windows.Media.Imaging.TransformedBitmap(frame, new System.Windows.Media.RotateTransform(d));
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(turned));
            using var output = new System.IO.MemoryStream();
            encoder.Save(output);
            return Convert.ToBase64String(output.ToArray());
        }

        // Transforms every point of an outline. A Transform set on the geometry is dropped when the path is
        // written back as text, so the figures are rewritten point by point; arcs also turn their axis.
        private static string TurnPath(string data, System.Windows.Media.Matrix matrix)
        {
            var path = System.Windows.Media.PathGeometry.CreateFromGeometry(System.Windows.Media.Geometry.Parse(data));
            double turn = Math.Atan2(matrix.M12, matrix.M11) * 180 / Math.PI;
            foreach (var figure in path.Figures)
            {
                figure.StartPoint = matrix.Transform(figure.StartPoint);
                foreach (var segment in figure.Segments)
                    switch (segment)
                    {
                        case System.Windows.Media.LineSegment line: line.Point = matrix.Transform(line.Point); break;
                        case System.Windows.Media.BezierSegment bezier:
                            bezier.Point1 = matrix.Transform(bezier.Point1);
                            bezier.Point2 = matrix.Transform(bezier.Point2);
                            bezier.Point3 = matrix.Transform(bezier.Point3);
                            break;
                        case System.Windows.Media.QuadraticBezierSegment quadratic:
                            quadratic.Point1 = matrix.Transform(quadratic.Point1);
                            quadratic.Point2 = matrix.Transform(quadratic.Point2);
                            break;
                        case System.Windows.Media.PolyLineSegment poly: TurnAll(poly.Points); break;
                        case System.Windows.Media.PolyBezierSegment polyBezier: TurnAll(polyBezier.Points); break;
                        case System.Windows.Media.PolyQuadraticBezierSegment polyQuadratic: TurnAll(polyQuadratic.Points); break;
                        case System.Windows.Media.ArcSegment arc:
                            arc.Point = matrix.Transform(arc.Point);
                            arc.RotationAngle += turn;
                            break;
                    }
            }
            return path.ToString(System.Globalization.CultureInfo.InvariantCulture);

            void TurnAll(System.Windows.Media.PointCollection points)
            {
                for (int i = 0; i < points.Count; i++) points[i] = matrix.Transform(points[i]);
            }
        }
    }
}
