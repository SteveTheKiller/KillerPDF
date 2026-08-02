using System.IO;
using System.Windows;
using System.Windows.Media;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace KillerPDF.Services
{
    // ============================================================
    // Burn-to-document core - draws the overlay annotation layer
    // and the stamp layer (page numbers / watermark) into a
    // PdfDocument via XGraphics, in PDF-point space. Static and
    // window-free by design (split out of Shell/Annotations.cs and
    // Shell/Stamps.cs in the KillerUI refactor): the print flow
    // runs these on a background thread against a throwaway copy
    // of the document, and being static means the compiler
    // guarantees no live UI state is touched.
    // ============================================================
    internal static class PdfBurn
    {
        // Builds the geometry for a carved highlight: the painted rectangle MINUS the union of the eraser
        // strokes (each widened to its brush radius with round caps) - one smooth, anti-aliased shape. Used
        // for both on-screen rendering and PDF export. Null when the highlight hasn't been carved.
        internal static Geometry? HighlightEraseGeometry(HighlightAnnotation h)
        {
            if (h.Erases is not { Count: > 0 } erases) return null;
            var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var e in erases)
            {
                if (e.Points.Count == 0) continue;
                if (e.Points.Count == 1)
                {
                    holes.Children.Add(new EllipseGeometry(e.Points[0], e.Radius, e.Radius));
                    continue;
                }
                var fig = new PathFigure { StartPoint = e.Points[0], IsClosed = false, IsFilled = false };
                for (int i = 1; i < e.Points.Count; i++) fig.Segments.Add(new LineSegment(e.Points[i], true));
                var pg = new PathGeometry();
                pg.Figures.Add(fig);
                var pen = new Pen(Brushes.Black, Math.Max(0.5, e.Radius * 2))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                holes.Children.Add(pg.GetWidenedPathGeometry(pen));
            }
            if (holes.Children.Count == 0) return null;
            return new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(h.DrawRect()), holes);
        }

        // ── The rotated-page frame (#169, thanks terada-d) ────────────────────────────────────
        // A page's rotation lives OUTSIDE the working document: TempReload strips /Rotate to 0 and
        // keeps the angle in the shell's _pageRotations, and the render path rotates the bitmap
        // instead. So the canvas - and everything measured against it - is in the VISUAL frame,
        // while XGraphics draws in the page's own unrotated frame. Every path that writes canvas
        // coordinates back into a page has to bridge the two; these two helpers are that bridge,
        // shared by the annotation burn and the stamp burn.
        //
        // Both frames are top-left origin with y down (XGraphics' default page direction and the
        // bitmap convention), and the render path rotates CLOCKWISE by the angle, so the mapping
        // is a plain quarter-turn about the page box.

        /// <summary>The page's size as the user sees it: the point dimensions swap on a quarter turn.</summary>
        private static (double w, double h) VisualPageSize(double pw, double ph, int rot)
            => rot == 90 || rot == 270 ? (ph, pw) : (pw, ph);

        /// <summary>Maps VISUAL-frame points onto the unrotated page frame XGraphics draws in.
        /// Null for an unrotated page (nothing to apply). Prepend it to the graphics transform and
        /// every subsequent draw call can keep passing visual coordinates unchanged.</summary>
        private static XMatrix? VisualToPageMatrix(int rot, double pw, double ph) => rot switch
        {
            // Derived from the render path's clockwise bitmap rotation, then inverted:
            //  90: x_page = y_vis,      y_page = ph - x_vis
            // 180: x_page = pw - x_vis, y_page = ph - y_vis
            // 270: x_page = pw - y_vis, y_page = x_vis
            // XMatrix is (m11, m12, m21, m22, dx, dy) with x' = x*m11 + y*m21 + dx.
            90  => new XMatrix(0, -1, 1, 0, 0, ph),
            180 => new XMatrix(-1, 0, 0, -1, pw, ph),
            270 => new XMatrix(0, 1, -1, 0, pw, 0),
            _   => null,
        };

        private static int NormalizeRot(IReadOnlyDictionary<int, int>? rotations, int pageIdx)
            => rotations is not null && rotations.TryGetValue(pageIdx, out int r) ? ((r % 360) + 360) % 360 : 0;

        // Burns annotations into the given document using only the supplied annotation + render-dim data and
        // nothing from the live UI state (static, so the compiler guarantees it). This makes it safe to run on
        // a background thread against a throwaway copy of the document - the print flow uses that to keep the
        // UI responsive while annotated pages are flattened.
        //
        // rotations: the out-of-document page angles (the shell's _pageRotations). Omitting them was
        // #169 - annotations were burned in the unrotated frame, so on a rotated page they landed
        // turned 90 degrees from where they were placed, offset, and scaled on swapped axes.
        internal static void DrawAnnotationsIntoDoc(
            PdfDocument? doc,
            IReadOnlyDictionary<int, List<PageAnnotation>> annotations,
            IReadOnlyDictionary<int, (int w, int h)> renderDims,
            int? onlyPage = null,
            IReadOnlyDictionary<int, int>? rotations = null)
        {
            if (doc is null) return;

            // Strip link annotation borders so they don't render as colored rectangles
            // (e.g. strikethrough-like lines) in other PDF viewers.
            PdfScrub.StripLinkAnnotationBorders(doc);

            foreach (var kvp in annotations)
            {
                int pageIdx = kvp.Key;
                if (onlyPage.HasValue && pageIdx != onlyPage.Value) continue;
                var annots = kvp.Value;
                if (annots.Count == 0 || pageIdx >= doc.PageCount) continue;
                if (!renderDims.ContainsKey(pageIdx)) continue;

                var page = doc.Pages[pageIdx];
                var (renderW, renderH) = renderDims[pageIdx];
                // #169: the render dims are in the VISUAL frame (that is what the user drew on),
                // so scale against the visual page size, not the raw page box - on a quarter-turned
                // page those are on swapped axes.
                int rot = NormalizeRot(rotations, pageIdx);
                double pwPt = page.Width.Point, phPt = page.Height.Point;
                var (visW, visH) = VisualPageSize(pwPt, phPt, rot);
                double sx = visW / renderW;
                double sy = visH / renderH;

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                // ...then turn the whole drawing back into the page's own frame, so every draw
                // call below can keep working in visual coordinates exactly as it always has.
                if (VisualToPageMatrix(rot, pwPt, phPt) is XMatrix m) gfx.MultiplyTransform(m);

                foreach (var annot in annots)
                {
                    switch (annot)
                    {
                        case TextAnnotation ta:
                        {
                            double tboxX = ta.Position.X * sx;
                            double tboxY = ta.Position.Y * sy;
                            double tboxW = ta.Width * sx;
                            double tboxH = ta.Height * sy;
                            // Background fill (whiteout) first, behind the text.
                            if (ta.HasFill)
                            {
                                var fc = ta.GetFill();
                                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B)),
                                    tboxX, tboxY, Math.Max(1, tboxW), Math.Max(1, tboxH));
                            }
                            // Match the on-screen typeface + B/I/S. Strikeout is a font-style flag PDFsharp
                            // draws as a line. Fall back to Segoe UI if the font can't be resolved/embedded.
                            var xstyle = XFontStyle.Regular;
                            if (ta.Bold) xstyle |= XFontStyle.Bold;
                            if (ta.Italic) xstyle |= XFontStyle.Italic;
                            if (ta.Strike) xstyle |= XFontStyle.Strikeout;
                            if (ta.Underline) xstyle |= XFontStyle.Underline;
                            XFont font;
                            // #168: the editor is WPF and falls back per character, so anything
                            // typed looks right on screen; PdfSharpCore resolves one face and
                            // boxes whatever it lacks. Pick a family that actually covers this
                            // text - the user's own font whenever it can carry it.
                            string wantFamily = string.IsNullOrEmpty(ta.FontName) ? "Segoe UI" : ta.FontName;
                            string useFamily = FontCoverage.PickFamily(wantFamily, ta.Content);
                            try { font = new XFont(useFamily, ta.FontSize * sy, xstyle); }
                            catch { font = new XFont("Segoe UI", ta.FontSize * sy, xstyle); }
                            var taColor = ta.GetColor();
                            var taBrush = new XSolidBrush(XColor.FromArgb(taColor.A, taColor.R, taColor.G, taColor.B));
                            // Wrap inside the box, matching the on-screen TextWrapping=Wrap. The 2px editor
                            // padding is scaled into the layout rect so wrap points line up with the canvas.
                            double padX = 2 * sx, padY = 2 * sy;
                            var layoutRect = new XRect(tboxX + padX, tboxY + padY,
                                                       Math.Max(1, tboxW - 2 * padX), Math.Max(1, tboxH));
                            // #142 root cause (PR #144, thanks Ryokoxx): the short DrawString
                            // overload hardcodes Justify, whose draw path NREd on the null-Text
                            // LineBreak blocks a newline produces - any font, any machine. The
                            // formatter is fixed, and the alignment is now stated explicitly:
                            // the on-screen TextBlock sets no TextAlignment, so WPF renders it
                            // Left - burned output must match, not silently justify.
                            // The retry/skip below stays as the net for GENUINE font failures:
                            // DrawString resolves the typeface lazily, so a font that CONSTRUCTED
                            // fine can still throw at first draw on machines missing that face.
                            var taAlign = new PdfSharpCore.Drawing.Layout.TextFormatAlignment
                            {
                                Horizontal = PdfSharpCore.Drawing.Layout.XParagraphAlignment.Left
                            };
                            if (!string.IsNullOrEmpty(ta.Content))
                            {
                                try
                                {
                                    var tf = new PdfSharpCore.Drawing.Layout.XTextFormatter(gfx);
                                    tf.DrawString(ta.Content, font, taBrush, layoutRect, taAlign);
                                }
                                catch
                                {
                                    try
                                    {
                                        var tf2 = new PdfSharpCore.Drawing.Layout.XTextFormatter(gfx);
                                        // Retry with the same COVERING family (#168) - this catch is
                                        // for the formatter's null-LineBreak crash (#142), not a font
                                        // problem, so dropping to Segoe UI here would only add boxes.
                                        tf2.DrawString(ta.Content, new XFont(useFamily, ta.FontSize * sy, xstyle), taBrush, layoutRect, taAlign);
                                    }
                                    catch { /* skip this annotation rather than fail the whole save (#142) */ }
                                }
                            }
                            break;
                        }

                        case HighlightAnnotation ha:
                            var hc = ha.GetColor();
                            var hBrush = new XSolidBrush(XColor.FromArgb(hc.A, hc.R, hc.G, hc.B));
                            if (HighlightEraseGeometry(ha) is { } hgeo)
                            {
                                // Carved highlight: flatten the rect-minus-strokes geometry to polygons and
                                // draw as one filled path so the smooth hole survives into the saved PDF.
                                var flat = hgeo.GetFlattenedPathGeometry();
                                var hpath = new XGraphicsPath();
                                foreach (var fig in flat.Figures)
                                {
                                    var poly = new System.Collections.Generic.List<XPoint> { new(fig.StartPoint.X * sx, fig.StartPoint.Y * sy) };
                                    foreach (var seg in fig.Segments)
                                        if (seg is PolyLineSegment pls) foreach (var p in pls.Points) poly.Add(new XPoint(p.X * sx, p.Y * sy));
                                        else if (seg is LineSegment ls) poly.Add(new XPoint(ls.Point.X * sx, ls.Point.Y * sy));
                                    if (poly.Count >= 3) hpath.AddPolygon([.. poly]);
                                }
                                hpath.FillMode = XFillMode.Winding;
                                gfx.DrawPath(hBrush, hpath);
                            }
                            else
                            {
                                var hdr = ha.DrawRect();
                                gfx.DrawRectangle(hBrush,
                                    hdr.X * sx, hdr.Y * sy,
                                    hdr.Width * sx, hdr.Height * sy);
                            }
                            break;

                        case InkAnnotation ia:
                            if (ia.Points.Count < 2) break;
                            var ic = ia.GetColor();
                            if (ia.HasFill)
                            {
                                // Filled shape (#127 Phase 3): fill the enclosed region first, then stroke.
                                var fc = ia.GetFillColor();
                                var fillPts = ia.Points.Select(p => new XPoint(p.X * sx, p.Y * sy)).ToArray();
                                gfx.DrawPolygon(new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B)),
                                                fillPts, XFillMode.Alternate);
                            }
                            var pen = new XPen(XColor.FromArgb(ic.A, ic.R, ic.G, ic.B), ia.StrokeWidth * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            for (int i = 0; i < ia.Points.Count - 1; i++)
                            {
                                gfx.DrawLine(pen,
                                    ia.Points[i].X * sx, ia.Points[i].Y * sy,
                                    ia.Points[i + 1].X * sx, ia.Points[i + 1].Y * sy);
                            }
                            break;

                        case SignatureAnnotation sa:
                            if (sa.ImageData is not null)
                            {
                                try
                                {
                                    var imgBytes = Convert.FromBase64String(sa.ImageData);
                                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(imgBytes));
                                    double imgX = sa.Position.X * sx;
                                    double imgY = sa.Position.Y * sy;
                                    double imgW = sa.SourceWidth * sa.Scale * sx;
                                    double imgH = sa.SourceHeight * sa.Scale * sy;
                                    gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                                }
                                catch { /* skip broken image */ }
                            }
                            else
                            {
                                var sigPen = new XPen(XColors.Black, sa.StrokeWidth * sa.Scale * sx)
                                {
                                    LineJoin = XLineJoin.Round,
                                    LineCap = XLineCap.Round
                                };
                                foreach (var stroke in sa.Strokes)
                                {
                                    for (int i = 0; i < stroke.Count - 1; i++)
                                    {
                                        double x1 = (sa.Position.X + stroke[i].X * sa.Scale) * sx;
                                        double y1 = (sa.Position.Y + stroke[i].Y * sa.Scale) * sy;
                                        double x2 = (sa.Position.X + stroke[i + 1].X * sa.Scale) * sx;
                                        double y2 = (sa.Position.Y + stroke[i + 1].Y * sa.Scale) * sy;
                                        gfx.DrawLine(sigPen, x1, y1, x2, y2);
                                    }
                                }
                            }
                            break;

                        case ImageAnnotation ia:
                            try
                            {
                                var iaBytes = Convert.FromBase64String(ia.ImageData);
                                var xia = XImage.FromStream(() => new System.IO.MemoryStream(iaBytes));
                                double iaX = ia.Position.X * sx;
                                double iaY = ia.Position.Y * sy;
                                double iaW = ia.SourceWidth * ia.Scale * sx;
                                double iaH = ia.SourceHeight * ia.Scale * sy;
                                gfx.DrawImage(xia, iaX, iaY, iaW, iaH);
                            }
                            catch { /* skip broken image */ }
                            break;
                    }
                }
            }
        }

        // ---- Stamp layer (page numbers / watermark) ------------------------------------------

        // 0-based page indices for a 1-based "1-3,5" range string ("" = all pages). Shared with
        // the shell's on-screen stamp renderer.
        internal static IEnumerable<int> StampPageRange(string range, int pageCount)
        {
            var set = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(range))
            {
                for (int i = 0; i < pageCount; i++) set.Add(i);
                return set;
            }
            foreach (var part in range.Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                int dash = p.IndexOf('-');
                if (dash > 0)
                {
                    if (int.TryParse(p[..dash].Trim(), out int a) && int.TryParse(p[(dash + 1)..].Trim(), out int b))
                        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) if (i >= 1 && i <= pageCount) set.Add(i - 1);
                }
                else if (int.TryParse(p, out int single) && single >= 1 && single <= pageCount) set.Add(single - 1);
            }
            return set;
        }

        // Draws the active stamps into the doc via XGraphics, in PDF-point space. Called BEFORE
        // DrawAnnotationsIntoDoc at each save site so stamps sit beneath annotations. Static so the
        // print flow can run it on a background thread against a throwaway document copy.
        // rotations: same story as the annotation burn (#169) - stamp positions are corners of the
        // page as the USER sees it, so on a rotated page they have to be laid out in the visual
        // frame and mapped back. The stamp PREVIEW already swapped the dimensions, so preview and
        // output disagreed until this landed.
        internal static void DrawStampsIntoDoc(PdfDocument? doc, StampSpec? spec, int? onlyPage = null,
            IReadOnlyDictionary<int, int>? rotations = null)
        {
            if (doc is null || spec is null || (!spec.NumbersEnabled && !spec.WmEnabled)) return;
            int n = doc.PageCount;

            HashSet<int> numPages = spec.NumbersEnabled ? [.. StampPageRange(spec.NumRange, n)] : [];
            HashSet<int> wmPages  = spec.WmEnabled     ? [.. StampPageRange(spec.WmRange, n)]  : [];
            int firstNumPage = int.MaxValue;
            foreach (int p in numPages) if (p < firstNumPage) firstNumPage = p;
            if (firstNumPage == int.MaxValue) firstNumPage = 0;

            // Pre-fade the watermark image once (reused for all pages).
            XImage? wmImg = null;
            if (spec.WmEnabled && spec.WmIsImage && !string.IsNullOrEmpty(spec.WmImagePath) && System.IO.File.Exists(spec.WmImagePath))
                wmImg = LoadStampImage(spec.WmImagePath!, spec.WmOpacity);

            for (int i = 0; i < n && i < doc.PageCount; i++)
            {
                if (onlyPage.HasValue && i != onlyPage.Value) continue;
                bool doNum = numPages.Contains(i);
                bool doWm = wmPages.Contains(i);
                if (!doNum && !doWm) continue;

                var page = doc.Pages[i];
                int rot = NormalizeRot(rotations, i);
                double pwPt = page.Width.Point, phPt = page.Height.Point;
                var (pw, ph) = VisualPageSize(pwPt, phPt, rot);   // #169: lay out on the visual page
                double mx = pw * 0.05, my = ph * 0.04;
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                if (VisualToPageMatrix(rot, pwPt, phPt) is XMatrix m) gfx.MultiplyTransform(m);

                if (doWm)  DrawWatermarkPdf(gfx, spec, pw, ph, mx, my, wmImg);   // watermark first (underneath)
                if (doNum) DrawNumberPdf(gfx, spec, i, firstNumPage, n, pw, ph, mx, my);
            }
        }

        private static void DrawNumberPdf(XGraphics gfx, StampSpec spec, int pageIndex, int firstNumPage, int total, double pw, double ph, double mx, double my)
        {
            int number = spec.StartNumber + Math.Max(0, pageIndex - firstNumPage);
            string text = (string.IsNullOrEmpty(spec.Format) ? "{n}" : spec.Format)
                .Replace("{n}", number.ToString()).Replace("{N}", total.ToString());
            if (text.Length == 0) return;

            // #168: the format string is user text - a "Page {n}" written in Japanese or Bengali
            // has to survive the save the same as an annotation does.
            var font = new XFont(FontCoverage.PickFamily("Segoe UI", text), Math.Max(1, spec.NumFontPt), XFontStyle.Regular);
            var c = spec.NumColor;
            var brush = new XSolidBrush(XColor.FromArgb(255, c.R, c.G, c.B));
            var size = gfx.MeasureString(text, font);
            double w = size.Width, h = size.Height;

            int posH = spec.NumPosH;
            double x, y;
            if (posH < 0)   // custom
            {
                double cx = spec.NumCustomX;
                if (spec.NumMirror && (pageIndex % 2 == 1)) cx = 1 - cx;
                x = cx * pw - w / 2; y = spec.NumCustomY * ph - h / 2;
            }
            else
            {
                if (spec.NumMirror && posH != 1 && (pageIndex % 2 == 1)) posH = 2 - posH;
                x = posH == 0 ? mx : posH == 2 ? pw - w - mx : (pw - w) / 2;
                y = spec.NumPosV == 0 ? my : spec.NumPosV == 1 ? (ph - h) / 2 : ph - h - my;
            }
            gfx.DrawString(text, font, brush, new XRect(x, y, w, h), XStringFormats.TopLeft);
        }

        private static void DrawWatermarkPdf(XGraphics gfx, StampSpec spec, double pw, double ph, double mx, double my, XImage? img)
        {
            double w, h;
            XFont? font = null;
            if (spec.WmIsImage)
            {
                if (img is null) return;
                w = pw * 0.5 * spec.WmScale;
                h = w * img.PixelHeight / Math.Max(1, img.PixelWidth);
            }
            else
            {
                if (string.IsNullOrEmpty(spec.WmText)) return;
                // #168: same as the page numbers - a watermark is user text in any script.
                string wmWant = string.IsNullOrWhiteSpace(spec.WmFont) ? "Segoe UI" : spec.WmFont;
                try { font = new XFont(FontCoverage.PickFamily(wmWant, spec.WmText), Math.Max(1, spec.WmFontPt), XFontStyle.Bold); }
                catch { font = new XFont("Segoe UI", Math.Max(1, spec.WmFontPt), XFontStyle.Bold); }
                var size = gfx.MeasureString(spec.WmText, font);
                w = size.Width; h = size.Height;
            }

            double cx, cy;
            if (spec.WmPosH < 0) { cx = spec.WmCustomX * pw; cy = spec.WmCustomY * ph; }
            else
            {
                cx = spec.WmPosH == 0 ? mx + w / 2 : spec.WmPosH == 2 ? pw - mx - w / 2 : pw / 2;
                cy = spec.WmPosV == 0 ? my + h / 2 : spec.WmPosV == 1 ? ph / 2 : ph - my - h / 2;
            }

            var state = gfx.Save();
            gfx.TranslateTransform(cx, cy);
            gfx.RotateTransform(-spec.WmAngle);
            if (spec.WmIsImage)
            {
                gfx.DrawImage(img, -w / 2, -h / 2, w, h);
            }
            else
            {
                byte a = (byte)Math.Max(0, Math.Min(255, spec.WmOpacity * 255));
                var c = spec.WmColor;
                gfx.DrawString(spec.WmText, font, new XSolidBrush(XColor.FromArgb(a, c.R, c.G, c.B)), new XRect(-w / 2, -h / 2, w, h), XStringFormats.Center);
            }
            gfx.Restore(state);
        }

        // Loads a watermark image as an XImage, pre-faded to the requested opacity (PdfSharpCore has no
        // per-draw image opacity, so we bake it into the pixels).
        private static XImage? LoadStampImage(string path, double opacity)
        {
            try
            {
                byte[] bytes;
                if (opacity >= 0.999)
                {
                    bytes = System.IO.File.ReadAllBytes(path);
                }
                else
                {
                    using var src = System.Drawing.Image.FromFile(path);
                    using var bmp = new System.Drawing.Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)Math.Max(0, Math.Min(1, opacity)) };
                        using var ia = new System.Drawing.Imaging.ImageAttributes();
                        ia.SetColorMatrix(cm);
                        g.DrawImage(src, new System.Drawing.Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel, ia);
                    }
                    using var ms = new System.IO.MemoryStream();
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    bytes = ms.ToArray();
                }
                return XImage.FromStream(() => new System.IO.MemoryStream(bytes));
            }
            catch { return null; }
        }
    }
}
