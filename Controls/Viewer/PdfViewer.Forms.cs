using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF.Controls
{
    // Moved from Shell/Forms.cs; the namespace and class line are the only changes. Window members
    // spelled bare here resolve through PdfViewer.Bridge.cs.
    public partial class PdfViewer
    {
        private readonly record struct FormFieldInfo(
            int    ObjNum,        // widget annotation object number (used as key)
            string FieldType,     // /Tx, /Btn, /Ch
            bool   IsCheckBox,
            bool   IsRadio,
            bool   IsMultiLine,   // /Tx with Multiline flag (bit 12)
            string FieldName,
            string CurrentValue,
            string OnValue,       // radio/checkbox on-state value (e.g. "/Yes")
            bool   IsReadOnly,
            double Cx, double Cy, double Cw, double Ch,
            List<string> Options,
            double DaFontPt,   // font size from the field's /DA (points); 0 = auto-size
            double Scale);     // canvas units per PDF point, for converting DaFontPt to canvas size

        /// <summary>
        /// Scans the current page's /Annots for Widget subtypes and overlays interactive
        /// WPF controls on the annotation canvas so the user can fill in form fields.
        /// </summary>
        private void RenderFormFields(int pageIndex, int canvasW, int canvasH)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndex >= _doc.PageCount) return;

            // Render onto the page's OWN surface: the per-page overlay used by continuous / grid /
            // two-page views, or the single-page canvas otherwise. Previously this always used the
            // single-page canvas, so interactive fields only appeared in Single Page view.
            var canvas = CanvasForPage(pageIndex);

            // Remove stale overlays without wiping the entire canvas.
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
                if (canvas.Children[i] is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    canvas.Children.RemoveAt(i);

            var fields = GetPageFormFields(pageIndex, canvasW, canvasH);
            if (fields.Count == 0) return;

            // Focus highlight (accent). Fields are NOT outlined at rest - the page's own field boxes
            // already show where to type - so we only tint a faint fill and show the accent on focus,
            // matching how Chrome/Brave render fields instead of drawing a green line around each one.
            var fieldBorder = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88)); // faint gray, check/radio only
            var darkBrush   = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            var fieldBg     = new SolidColorBrush(Color.FromArgb(200, 255, 253, 231));

            // Collect radio buttons per group so we can wire mutual exclusion after the loop.
            var radioGroups = new Dictionary<string, List<(Ellipse dot, string onVal)>>();

            bool anyField = false;
            foreach (var f in fields)
            {
                UIElement? ctrl = null;

                // Text field
                var fillRole = ClassifyFormField(f);
                if (fillRole == FormFillRole.Signature || fillRole == FormFillRole.Initials)
                {
                    ctrl = BuildSignZone(f, fillRole == FormFillRole.Initials, pageIndex);
                }
                else if (!f.IsCheckBox && !f.IsRadio && f.FieldType != "/Ch")
                {
                    string cur     = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    // Size text the way the field intends: use its /DA font size when one is given;
                    // otherwise auto-size - single-line fits the box height (capped so a tall field
                    // isn't giant), multi-line uses a steady readable size rather than shrinking with
                    // the box. This replaces the old box-height guess that made fields huge or tiny.
                    double fontSize;
                    if (_formFontSizes.TryGetValue(f.ObjNum, out var userPt) && userPt > 0 && f.Scale > 0)
                        fontSize = userPt * f.Scale;          // user override (the new per-field size control)
                    else if (f.DaFontPt > 0.5 && f.Scale > 0)
                        fontSize = f.DaFontPt * f.Scale;
                    else if (f.IsMultiLine)
                        fontSize = f.Scale > 0 ? 11.5 * f.Scale : Math.Max(11, Math.Min(f.Cw, f.Ch) * 0.5);
                    else
                        fontSize = f.Scale > 0 ? Math.Min(f.Ch * 0.62, 15 * f.Scale) : f.Ch * 0.62;
                    fontSize = Math.Max(9, Math.Min(fontSize, 400));
                    var tb = new TextBox
                    {
                        Tag              = FormOverlayTag,
                        Width            = f.Cw,
                        Height           = f.Ch,
                        Text             = cur,
                        IsReadOnly       = f.IsReadOnly,
                        AcceptsReturn    = f.IsMultiLine,
                        TextWrapping     = f.IsMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        VerticalScrollBarVisibility = f.IsMultiLine
                            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                        Background       = fieldBg,
                        Foreground       = Brushes.Black,
                        CaretBrush       = Brushes.Black,
                        SelectionBrush   = (System.Windows.Media.Brush)FindResource("HeaderLineBrush"),
                        Style            = (Style)FindResource("FormFieldTextBox"),
                        BorderBrush      = Brushes.Transparent,
                        BorderThickness  = new Thickness(1),
                        FontSize         = fontSize,
                        Padding          = new Thickness(3, 0, 3, 0),
                        VerticalContentAlignment = f.IsMultiLine
                            ? VerticalAlignment.Top : VerticalAlignment.Center,
                        ToolTip          = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    // No outline at rest (the page already shows the field box); accent only on focus.
                    // Focus also raises the per-field font-size stepper (and hides it on blur).
                    int    capturedKey   = f.ObjNum;
                    double capturedScale = f.Scale;
                    tb.GotFocus  += (_, _) => { tb.SetResourceReference(Control.BorderBrushProperty, "HeaderLineBrush"); ShowFormSizeBar(tb, capturedKey, capturedScale); };
                    tb.LostFocus += (_, _) => { tb.BorderBrush = Brushes.Transparent; HideFormSizeBar(); };
                    tb.TextChanged += (_, _) => { _formTextValues[capturedKey] = tb.Text; MarkDirty(true); };
                    ctrl = tb;
                }

                // Dropdown / choice
                else if (f.FieldType == "/Ch" && f.Options.Count > 0)
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    var combo = new ComboBox
                    {
                        Tag       = FormOverlayTag,
                        Width     = f.Cw,
                        Height    = f.Ch,
                        IsEnabled = !f.IsReadOnly,
                        Foreground = Brushes.Black,
                        FontSize  = f.DaFontPt > 0.5 && f.Scale > 0
                            ? f.DaFontPt * f.Scale
                            : Math.Min(Math.Max(10, f.Ch * 0.55), 16),
                        ToolTip   = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    foreach (var opt in f.Options) combo.Items.Add(opt);
                    combo.SelectedItem = cur;
                    int capturedKey = f.ObjNum;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string s) { _formTextValues[capturedKey] = s; MarkDirty(true); }
                    };
                    ctrl = combo;
                }

                // Checkbox
                else if (f.IsCheckBox)
                {
                    bool isChecked = _formCheckValues.TryGetValue(f.ObjNum, out var cv) ? cv
                        : !string.IsNullOrEmpty(f.CurrentValue)
                          && f.CurrentValue != "/Off" && f.CurrentValue != "Off";

                    // Custom border-based checkbox - WPF's built-in CheckBox indicator
                    // doesn't scale with Width/Height, so we draw it ourselves.
                    double checkFs = Math.Min(f.Cw, f.Ch) * 0.72;
                    var checkMark = new TextBlock
                    {
                        Text       = "✓",
                        FontSize   = checkFs,
                        FontWeight = FontWeights.Bold,
                        Foreground = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var box = new Border
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Background      = fieldBg,
                        BorderBrush     = fieldBorder,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(2),
                        Cursor          = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child           = checkMark,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (!f.IsReadOnly)
                    {
                        int capturedKey = f.ObjNum;
                        box.MouseLeftButtonDown += (_, e) =>
                        {
                            bool now = !(_formCheckValues.TryGetValue(capturedKey, out var v) ? v : isChecked);
                            _formCheckValues[capturedKey] = now;
                            checkMark.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = box;
                }

                // Radio button
                else if (f.IsRadio)
                {
                    string groupSelected = _formRadioValues.TryGetValue(f.FieldName, out var rv) ? rv
                        : f.CurrentValue; // CurrentValue = parent /V = currently selected on-value
                    bool isSelected = groupSelected == f.OnValue;

                    double size  = Math.Min(f.Cw, f.Ch) * 0.88;
                    double inner = size * 0.52;

                    var dot = new Ellipse
                    {
                        Width      = inner,
                        Height     = inner,
                        Fill       = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var ring = new Ellipse
                    {
                        Width           = size,
                        Height          = size,
                        Stroke          = fieldBorder,
                        StrokeThickness = 1.5,
                        Fill            = fieldBg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    };
                    var grid = new Grid { Width = f.Cw, Height = f.Ch };
                    grid.Children.Add(ring);
                    grid.Children.Add(dot);

                    var radioBorder = new Border
                    {
                        Tag    = FormOverlayTag,
                        Width  = f.Cw,
                        Height = f.Ch,
                        Background = Brushes.Transparent,
                        Cursor = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child  = grid,
                        ToolTip = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };

                    // Register dot for mutual-exclusion wiring after the loop.
                    if (!radioGroups.TryGetValue(f.FieldName, out var groupList))
                        radioGroups[f.FieldName] = groupList = [];
                    groupList.Add((dot, f.OnValue));

                    if (!f.IsReadOnly)
                    {
                        string capturedGroup = f.FieldName;
                        string capturedOn    = f.OnValue;
                        radioBorder.MouseLeftButtonDown += (_, e) =>
                        {
                            _formRadioValues[capturedGroup] = capturedOn;
                            // Deselect all in group, then select this one.
                            if (radioGroups.TryGetValue(capturedGroup, out var gl))
                                foreach (var (d, ov) in gl)
                                    d.Visibility = ov == capturedOn ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = radioBorder;
                }

                if (ctrl is null) continue;
                Canvas.SetLeft(ctrl, f.Cx);
                Canvas.SetTop(ctrl, f.Cy);
                // #156: field overlays sit BELOW the annotation layer. RenderAllAnnotations paints
                // the annotations and then restores these, and a Canvas paints later children on
                // top - so a signature dropped on a fill-in field disappeared behind the field's
                // own control. Annotations render at the default ZIndex 0, so -1 puts the fields
                // under them without touching the annotation paths. Clicking a covered field still
                // works: every annotation visual is IsHitTestVisible=false, so it never swallows
                // the click that reaches the field beneath it.
                Panel.SetZIndex(ctrl, -1);
                canvas.Children.Add(ctrl);
                anyField = true;
            }

            if (anyField)
                SetStatus(string.Format(Loc("Str_PageFormFields"), pageIndex + 1, _doc.PageCount));
        }

        /// <summary>
        /// Parses Widget annotations from the given page into field descriptors with canvas coordinates.
        /// Walks the parent chain for each widget to resolve inherited /FT, /T, /V, and /Ff.
        /// </summary>
        private List<FormFieldInfo> GetPageFormFields(int pageIndex, int canvasW, int canvasH)
        {
            var result = new List<FormFieldInfo>();
            if (_doc is null || pageIndex >= _doc.PageCount) return result;

            var page = _doc.Pages[pageIndex];
            // PDFium renders the CropBox (falling back to the MediaBox when there is no crop), so
            // field /Rect coordinates must be mapped relative to THAT box's origin and size - not
            // assumed to start at (0,0) with MediaBox dimensions. Pages whose box origin is offset,
            // or whose CropBox is inset from the MediaBox, otherwise shift every field a little;
            // mapping to the rendered box's own origin lines them up the way Acrobat/Chrome do.
            //
            // CRITICAL: read the boxes via the raw dictionary, NEVER via page.MediaBox/page.CropBox.
            // Those property getters have create-on-read semantics in PdfSharpCore: touching
            // page.CropBox on a page without one PLANTS an empty /CropBox [0 0 0 0] into the page
            // dictionary (the same lazy-getter trap as the phantom /Outlines, #103), which then
            // saves to disk and makes Adobe reject every page as "dimensions out-of-range".
            //
            // The entry can be a parsed PdfArray (as loaded from disk) OR a PdfRectangle:
            // PdfSharpCore's GetRectangle - which backs page.MediaBox / page.Width / page.Height -
            // converts the array to a PdfRectangle and STORES IT BACK into the dictionary
            // (PdfDictionary.GetRectangle: "this[key] = value"). The link layer reads
            // pdfPage.Width.Point on every page render, so by the time fields are parsed the array
            // is usually gone and a plain GetArray came back null - which dropped every non-A4
            // document into the A4 fallback below and shifted all field overlays, worst near the
            // top of the page (found 2026-07-23 via the US Letter brochure build; the shipped A4
            // brochure masked it). Handle both shapes, like ScrubDegenerateCropBoxes does, and
            // walk /Parent: /MediaBox and /CropBox are inheritable page-tree attributes.
            (double x, double y, double w, double h)? ReadBox(string key)
            {
                PdfDictionary? node = page;
                for (int depth = 0; node is not null && depth < 32; depth++)
                {
                    PdfItem? item = node.Elements[key];
                    if (item is not null && item is not PdfArray && item is not PdfRectangle)
                        item = DerefItem(item);
                    if (item is PdfRectangle pr)
                        return (Math.Min(pr.X1, pr.X2), Math.Min(pr.Y1, pr.Y2),
                                Math.Abs(pr.X2 - pr.X1), Math.Abs(pr.Y2 - pr.Y1));
                    if (item is PdfArray { Elements.Count: 4 } a)
                    {
                        double x1 = a.Elements.GetReal(0), y1 = a.Elements.GetReal(1);
                        double x2 = a.Elements.GetReal(2), y2 = a.Elements.GetReal(3);
                        return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
                    }
                    var parent = node.Elements["/Parent"];
                    node = parent is null ? null
                         : parent as PdfDictionary ?? DerefItem(parent) as PdfDictionary;
                }
                return null;
            }
            var media = ReadBox("/MediaBox");
            var crop  = ReadBox("/CropBox");
            var box   = (crop.HasValue && crop.Value.w > 1 && crop.Value.h > 1)
                        ? crop.Value
                        : media ?? (x: 0.0, y: 0.0, w: 595.28, h: 841.89);
            double boxX  = box.x;   // box lower-left origin in PDF user space
            double boxY  = box.y;
            double pageW = box.w > 0 ? box.w : 595.28;
            double pageH = box.h > 0 ? box.h : 841.89;
            int rotation = ((page.Rotate % 360) + 360) % 360;

            try
            {
                var annotsArr = page.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return result;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem   = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Widget")) continue;

                    // Get rect
                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    // Field rect relative to the rendered box's lower-left origin, so an offset
                    // MediaBox/CropBox doesn't push the field off its drawn box.
                    double fx1 = rx1 - boxX, fy1 = ry1 - boxY;
                    double fx2 = rx2 - boxX, fy2 = ry2 - boxY;

                    // Map PDF rect (bottom-left origin, unrotated) to canvas coords.
                    // The canvas matches the Docnet-rendered bitmap which has already applied
                    // the page rotation, so we must transform accordingly.
                    double cx, cy, cw, ch;
                    switch (rotation)
                    {
                        case 90: // 90 CW: bottom->left, left->top; canvas is pageH-wide x pageW-tall
                            // (px,py) -> canvas (py, px)
                            cx = fy1             / pageH * canvasW;
                            cy = fx1             / pageW * canvasH;
                            cw = (fy2 - fy1)     / pageH * canvasW;
                            ch = (fx2 - fx1)     / pageW * canvasH;
                            break;
                        case 180: // 180: both axes flipped
                            // (px,py) -> canvas (pageW-px, py)
                            cx = (pageW - fx2)   / pageW * canvasW;
                            cy = fy1             / pageH * canvasH;
                            cw = (fx2 - fx1)     / pageW * canvasW;
                            ch = (fy2 - fy1)     / pageH * canvasH;
                            break;
                        case 270: // 270 CW (= 90 CCW): bottom->right, right->top; canvas is pageH-wide x pageW-tall
                            // (px,py) -> canvas (pageH-py, pageW-px)
                            cx = (pageH - fy2)   / pageH * canvasW;
                            cy = (pageW - fx2)   / pageW * canvasH;
                            cw = (fy2 - fy1)     / pageH * canvasW;
                            ch = (fx2 - fx1)     / pageW * canvasH;
                            break;
                        default: // 0 - standard bottom-left PDF -> top-left canvas
                            cx = fx1             / pageW * canvasW;
                            cy = (pageH - fy2)   / pageH * canvasH;
                            cw = (fx2 - fx1)     / pageW * canvasW;
                            ch = (fy2 - fy1)     / pageH * canvasH;
                            break;
                    }
                    if (cw < 2 || ch < 2) continue;

                    // Walk the parent chain to resolve inherited attributes
                    string ft     = "";
                    string name   = "";
                    string curVal = "";
                    string da     = "";   // default appearance string (holds the field's font size)
                    int    flags  = 0;
                    var    options = new List<string>();

                    PdfDictionary? node = ann;
                    while (node is not null)
                    {
                        if (string.IsNullOrEmpty(ft)   && node.Elements["/FT"] is not null)
                            ft = node.Elements["/FT"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(name) && node.Elements["/T"] is PdfString ts)
                            name = ts.Value;
                        if (string.IsNullOrEmpty(curVal) && node.Elements["/V"] is not null)
                        {
                            var vElem = node.Elements["/V"];
                            curVal = vElem is PdfString vs ? vs.Value : vElem?.ToString() ?? "";
                        }
                        if (string.IsNullOrEmpty(da) && node.Elements["/DA"] is PdfString das)
                            da = das.Value;
                        if (flags == 0 && node.Elements["/Ff"] is PdfInteger fi)
                            flags = fi.Value;
                        if (options.Count == 0 && node.Elements.GetArray("/Opt") is PdfArray optArr)
                        {
                            for (int j = 0; j < optArr.Elements.Count; j++)
                            {
                                var o = optArr.Elements[j];
                                if (o is PdfString ps2) options.Add(ps2.Value);
                                else if (o is PdfArray pa2 && pa2.Elements.Count >= 2)
                                    options.Add((pa2.Elements[1] as PdfString)?.Value ?? "");
                            }
                        }

                        // Move to parent
                        var parentItem = node.Elements["/Parent"];
                        if (parentItem is null) break;
                        node = parentItem as PdfDictionary ?? DerefItem(parentItem) as PdfDictionary;
                    }

                    // No resolvable field type (directly or inherited) means this Widget is not a fillable
                    // field (just a bare annotation widget). Skip it rather than guessing it's a text box.
                    if (string.IsNullOrEmpty(ft)) continue;

                    bool isReadOnly  = (flags & 1) != 0;
                    bool isMultiLine = ft.Contains("Tx") && (flags & 4096) != 0;
                    bool isPushBtn   = ft.Contains("Btn") && (flags & (1 << 16)) != 0;
                    bool isRadio     = ft.Contains("Btn") && !isPushBtn && (flags & (1 << 15)) != 0;
                    bool isCheckBox  = ft.Contains("Btn") && !isPushBtn && !isRadio;

                    // A button widget that fires an action (navigation /GoTo, /URI, JavaScript, ...) is a
                    // pushbutton/link, not a fillable control. Some PDFs - e.g. manuals with a clickable
                    // page index down one side - omit the pushbutton flag, which would otherwise make every
                    // one of those render as a spurious checkbox. Treat any actioned button as a pushbutton.
                    // (A real checkbox/radio always carries an /AS appearance state; a pushbutton does not.)
                    if (ft.Contains("Btn") && (isPushBtn || ann.Elements["/A"] is not null || ann.Elements["/AS"] is null))
                        continue;

                    // Extract the "on" value for this widget (radio/checkbox selected state).
                    // Found in /AP /N as the key that is NOT /Off.
                    string onValue = "/Yes";
                    try
                    {
                        var apDict = ann.Elements.GetDictionary("/AP");
                        var nDict  = apDict?.Elements.GetDictionary("/N");
                        if (nDict is not null)
                            foreach (var k in nDict.Elements.Keys)
                                if (k != "/Off") { onValue = k; break; }
                    }
                    catch { }

                    int objNum = PdfScrub.GetObjectNumber(elem);
                    if (objNum < 0)
                        objNum = -(pageIndex * 10000 + i); // synthetic key for inline dicts

                    // Font size the field asks for (points) and the page's render scale, so the
                    // overlay can size text the way the form intends rather than guessing from the
                    // box height (which made tall fields huge and others shrink).
                    double daFontPt = ParseDaFontSize(da);
                    double fScale   = (rotation == 90 || rotation == 270)
                        ? canvasH / pageW : canvasH / pageH;

                    result.Add(new FormFieldInfo(objNum, ft, isCheckBox, isRadio, isMultiLine,
                        name, curVal, onValue, isReadOnly, cx, cy, cw, ch, options, daFontPt, fScale));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageFormFields: {ex}"); }

            return result;
        }

        // Parses the font size (points) from a PDF /DA default-appearance string, e.g.
        // "/Helv 11 Tf 0 g" -> 11. Returns 0 when the size is "auto" (0) or there's no Tf operator.
        private static double ParseDaFontSize(string da)
        {
            if (string.IsNullOrWhiteSpace(da)) return 0;
            var t = da.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < t.Length; i++)
                if (t[i] == "Tf" && double.TryParse(t[i - 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sz) && sz > 0)
                    return sz;
            return 0;
        }

        /// <summary>
        /// Writes all filled form values back into the PDF document's AcroForm field dictionaries.
        /// Called just before saving so values are persisted in the output file.
        /// </summary>
        private void WriteFormValuesToDocument()
        {
            if (_doc is null) return;
            if (_formTextValues.Count == 0 && _formCheckValues.Count == 0 && _formRadioValues.Count == 0) return;

            try
            {
                for (int p = 0; p < _doc.PageCount; p++)
                {
                    var page = _doc.Pages[p];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int i = 0; i < annotsArr.Elements.Count; i++)
                    {
                        PdfItem? elem = annotsArr.Elements[i];
                        PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Widget")) continue;

                        int objNum = PdfScrub.GetObjectNumber(elem);
                        if (objNum < 0) objNum = -(p * 10000 + i);

                        // Walk parent chain to find the canonical field dict (owns /FT)
                        PdfDictionary? fieldDict = ann;
                        PdfDictionary? node = ann;
                        while (node is not null)
                        {
                            if (node.Elements["/FT"] is not null) { fieldDict = node; break; }
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        // Gather field rect for AP stream sizing
                        var rectArr = ann.Elements.GetArray("/Rect");
                        double fieldW = 100, fieldH = 20;
                        if (rectArr?.Elements.Count >= 4)
                        {
                            double rx1 = rectArr.Elements.GetReal(0), ry1 = rectArr.Elements.GetReal(1);
                            double rx2 = rectArr.Elements.GetReal(2), ry2 = rectArr.Elements.GetReal(3);
                            fieldW = Math.Abs(rx2 - rx1);
                            fieldH = Math.Abs(ry2 - ry1);
                        }

                        // Resolve /DA for font name/size (walk parent chain)
                        string? daStr = null;
                        node = ann;
                        while (node is not null && daStr is null)
                        {
                            if (node.Elements["/DA"] is PdfString ds) daStr = ds.Value;
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        if (_formTextValues.TryGetValue(objNum, out var textVal) && fieldDict is not null)
                        {
                            fieldDict.Elements["/V"] = new PdfString(textVal);
                            // Bake a per-field font-size override (from the size stepper) into the
                            // field's /DA so the saved appearance and any later editor use it.
                            if (_formFontSizes.TryGetValue(objNum, out var ovPt) && ovPt > 0)
                            {
                                daStr = WithDaFontSize(daStr, ovPt);
                                fieldDict.Elements["/DA"] = new PdfString(daStr);
                            }
                            GenerateTextFieldAppearance(ann, textVal, daStr, fieldW, fieldH);
                        }
                        else if (_formCheckValues.TryGetValue(objNum, out var checkVal) && fieldDict is not null)
                        {
                            string onVal = "/Yes";
                            try
                            {
                                var apDict = ann.Elements.GetDictionary("/AP");
                                var nDict  = apDict?.Elements.GetDictionary("/N");
                                if (nDict is not null)
                                    foreach (var k in nDict.Elements.Keys)
                                        if (k != "/Off") { onVal = k; break; }
                            }
                            catch { }

                            fieldDict.Elements["/V"]  = new PdfName(checkVal ? onVal : "/Off");
                            fieldDict.Elements["/AS"] = new PdfName(checkVal ? onVal : "/Off");
                            ann.Elements["/AS"]        = new PdfName(checkVal ? onVal : "/Off");
                            GenerateCheckBoxAppearance(ann, checkVal, onVal, fieldW, fieldH);
                        }
                        else if (_formRadioValues.Count > 0 && fieldDict is not null)
                        {
                            // Radio button: look up by field name (shared across all widgets in the group)
                            string ft2 = fieldDict.Elements["/FT"]?.ToString() ?? "";
                            if (ft2.Contains("Btn"))
                            {
                                // Walk to find /T on the parent field node
                                string fieldName2 = "";
                                var n2 = fieldDict;
                                while (n2 is not null && string.IsNullOrEmpty(fieldName2))
                                {
                                    if (n2.Elements["/T"] is PdfString ts2) fieldName2 = ts2.Value;
                                    var pi2 = n2.Elements["/Parent"];
                                    if (pi2 is null) break;
                                    n2 = pi2 as PdfDictionary ?? DerefItem(pi2) as PdfDictionary;
                                }
                                if (_formRadioValues.TryGetValue(fieldName2, out var radioSel))
                                {
                                    // Set /V on the parent field
                                    fieldDict.Elements["/V"] = new PdfName(radioSel);
                                    // Set /AS on this widget to show selected or off
                                    string onVal2 = "/Yes";
                                    try
                                    {
                                        var apD = ann.Elements.GetDictionary("/AP");
                                        var nD  = apD?.Elements.GetDictionary("/N");
                                        if (nD is not null)
                                            foreach (var k in nD.Elements.Keys)
                                                if (k != "/Off") { onVal2 = k; break; }
                                    }
                                    catch { }
                                    ann.Elements["/AS"] = new PdfName(onVal2 == radioSel ? onVal2 : "/Off");
                                }
                            }
                        }
                    }
                }

                // Belt-and-suspenders: also set NeedAppearances in case any AP generation failed
                try
                {
                    var acroForm = _doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                    if (acroForm is not null)
                        acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
                }
                catch { }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WriteFormValuesToDocument: {ex}"); }
        }

        /// <summary>
        /// Generates a /AP /N form XObject appearance stream for a text field and sets it
        /// on the widget annotation. Uses reflection to access PdfSharpCore's internal
        /// PdfDictionary.PdfStream constructor since there is no public factory method.
        /// </summary>
        private void GenerateTextFieldAppearance(PdfDictionary widgetAnn, string text, string? da, double fieldW, double fieldH)
        {
            try
            {
                var (fontName, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                fontSize = Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // Vertical centering: PDF baseline is measured from bottom of the field rect.
                double textY = (fieldH - fontSize) / 2 + fontSize * 0.2;
                if (textY < 1) textY = 1;

                string escaped = EscapePdfString(text);
                string content =
                    $"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n" +
                    $"BT\n{fontName} {fontSize:F2} Tf\n0 g\n2 {textY:F2} Td\n({escaped}) Tj\nET\nQ\nEMC";

                var xobj = BuildFormXObject(fontName, fieldW, fieldH, content);
                if (xobj is null) return;

                AttachAppearance(widgetAnn, xobj);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateTextFieldAppearance: {ex}"); }
        }

        /// <summary>
        /// Generates /AP /N (checked) and /AP /Off (unchecked) appearance streams for a
        /// checkbox widget and sets them on the annotation.
        /// </summary>
        // isChecked unused - both AP states are always generated; /AS selects the active one
        private void GenerateCheckBoxAppearance(PdfDictionary widgetAnn, bool isChecked, string onVal, double fieldW, double fieldH)
        {
            try
            {
                double m = Math.Min(fieldW, fieldH) * 0.1; // margin
                double iw = fieldW - m * 2;
                double ih = fieldH - m * 2;

                // Checked: ZapfDingbats "4" = check, centred in the field
                double fs = Math.Min(iw, ih) * 0.85;
                double tx = (fieldW - fs * 0.6) / 2;
                double ty = (fieldH - fs) / 2 + fs * 0.15;

                string checkedContent =
                    $"q\nBT\n/ZaDb {fs:F2} Tf\n0 g\n{tx:F2} {ty:F2} Td\n(4) Tj\nET\nQ";

                string offContent = "q\nQ"; // empty - just clears

                // /Resources needs ZapfDingbats font for the checked state
                var checkedXobj = BuildFormXObject("/ZaDb", fieldW, fieldH, checkedContent, isZaDb: true);
                var offXobj     = BuildFormXObject("/ZaDb", fieldW, fieldH, offContent,     isZaDb: true);
                if (checkedXobj is null || offXobj is null) return;

                // /AP dictionary with /N being a sub-dict keyed by state name
                var nDict = new PdfDictionary(_doc);
                nDict.Elements[onVal]  = checkedXobj.Reference;
                nDict.Elements["/Off"] = offXobj.Reference;

                var apDict = new PdfDictionary(_doc);
                apDict.Elements["/N"] = nDict;

                widgetAnn.Elements["/AP"] = apDict;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateCheckBoxAppearance: {ex}"); }
        }

        /// <summary>
        /// Creates an indirect PdfDictionary stream object representing a Form XObject,
        /// suitable for use as an /AP /N appearance stream.
        /// </summary>
        private PdfDictionary? BuildFormXObject(string fontName, double w, double h, string content, bool isZaDb = false)
        {
            byte[] bytes = System.Text.Encoding.GetEncoding("iso-8859-1").GetBytes(content);

            var xobj = new PdfDictionary(_doc);
            xobj.Elements["/Type"]     = new PdfName("/XObject");
            xobj.Elements["/Subtype"]  = new PdfName("/Form");
            xobj.Elements["/FormType"] = new PdfInteger(1);

            var bbox = new PdfArray(_doc);
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(w));
            bbox.Elements.Add(new PdfReal(h));
            xobj.Elements["/BBox"] = bbox;

            // Inline font resource - avoids adding top-level objects for every field.
            var fontEntry = new PdfDictionary(_doc);
            fontEntry.Elements["/Type"]    = new PdfName("/Font");
            fontEntry.Elements["/Subtype"] = new PdfName("/Type1");
            fontEntry.Elements["/BaseFont"] = isZaDb
                ? new PdfName("/ZapfDingbats")
                : new PdfName("/Helvetica");
            if (!isZaDb)
                fontEntry.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");

            var fontDict = new PdfDictionary(_doc);
            fontDict.Elements[fontName] = fontEntry;

            var res = new PdfDictionary(_doc);
            res.Elements["/Font"] = fontDict;
            xobj.Elements["/Resources"] = res;

            if (!TryAttachStreamBytes(xobj, bytes)) return null;

            _doc!.Internals.AddObject(xobj);
            return xobj;
        }

        /// <summary>
        /// Sets /AP /N on a widget annotation to the given form XObject (indirect ref).
        /// Replaces any existing AP entry.
        /// </summary>
        private static void AttachAppearance(PdfDictionary widgetAnn, PdfDictionary xobj)
        {
            var apDict = new PdfDictionary();
            apDict.Elements["/N"] = xobj.Reference;
            widgetAnn.Elements["/AP"] = apDict;
        }

        /// <summary>
        /// Attaches raw content bytes to a PdfDictionary as a stream.
        /// Accesses PdfDictionary.PdfStream via reflection because its constructor is internal.
        /// Falls back to the backing field if the property setter is protected.
        /// </summary>
        private static bool TryAttachStreamBytes(PdfDictionary dict, byte[] bytes)
        {
            try
            {
                var dictType   = typeof(PdfDictionary);
                var streamType = dictType.GetNestedType("PdfStream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (streamType is null) return false;

                // Try (byte[], PdfDictionary) ctor first, then (byte[]) only
                System.Reflection.ConstructorInfo? ctor =
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, [typeof(byte[]), typeof(PdfDictionary)], null) ??
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, [typeof(byte[])], null);
                if (ctor is null) return false;

                object streamObj = ctor.GetParameters().Length == 2
                    ? ctor.Invoke([bytes, dict])
                    : ctor.Invoke([bytes]);

                // Try public Stream property setter first
                var prop = dictType.GetProperty("Stream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop?.CanWrite == true)
                {
                    prop.SetValue(dict, streamObj);
                    return true;
                }

                // Fall back to the backing field
                var field = dictType.GetField("_stream",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field is not null)
                {
                    field.SetValue(dict, streamObj);
                    return true;
                }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Parses a PDF Default Appearance string ("/Helv 12 Tf 0 g") to extract
        /// the font resource name and point size.
        /// </summary>
        private static (string fontName, double fontSize) ParseDaString(string? da)
        {
            string fontName = "/Helv";
            double fontSize = 0;
            if (string.IsNullOrWhiteSpace(da)) return (fontName, fontSize);

            var tokens = da!.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                if (tokens[i + 2] == "Tf" &&
                    double.TryParse(tokens[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double fs))
                {
                    fontName = tokens[i]; // e.g. "/Helv"
                    fontSize = fs;
                    break;
                }
            }
            return (fontName, fontSize);
        }

        /// <summary>
        /// Escapes a string for use in a PDF literal string (parentheses syntax).
        /// </summary>
        private static string EscapePdfString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '(':  sb.Append("\\(");  break;
                    case ')':  sb.Append("\\)");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    default:
                        // Keep Latin-1 range; replace anything outside with '?'
                        sb.Append(c < 256 ? c : '?');
                        break;
                }
            }
            return sb.ToString();
        }

        // Form-field font-size stepper
        // A small "Font size: - N +" bar shown while a form text field is focused, so the user can
        // resize that field's text (PDF forms otherwise lock the size to the field's /DA). The chosen
        // size is stored per field and baked into the field's /DA on save.
        //
        // Dressed like the annotate bars (same surface, grain, shadow, fade) but ANCHORED TO THE
        // FIELD: the bar drips down from the box being typed in, dropdown-style, and follows it
        // through scrolling and zoom. It flips above the field when there is no room below, so it
        // can never collide with the annotate bars or float detached over the page.
        private ScrollChangedEventHandler? _formBarScrollHook;   // detached in HideFormSizeBar

        private void ShowFormSizeBar(TextBox tb, int objNum, double scale)
        {
            HideFormSizeBar();
            _activeFormTb    = tb;
            _activeFormObj   = objNum;
            _activeFormScale = scale > 0 ? scale : 1;

            double curPt = Math.Round(_activeFormTb.FontSize / _activeFormScale);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 1, 3, 1), Background = Brushes.Transparent };

            // Fixed light text: the InlineFlyout pill is dark regardless of the app theme.
            var lbl = new TextBlock
            {
                Text = "Font size:",
                FontFamily = UiKit.UiFont, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            };
            panel.Children.Add(lbl);

            var sizeLbl = new TextBlock
            {
                Text = curPt.ToString("0"),
                FontFamily = UiKit.UiFont, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                MinWidth = 20, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(MakeFormSizeStep("", () => AdjustFormFontSize(-1, sizeLbl)));  // minus
            panel.Children.Add(sizeLbl);
            panel.Children.Add(MakeFormSizeStep("", () => AdjustFormFontSize(+1, sizeLbl)));  // plus

            // The on-document "inline flyout" style: translucent pill, solidifies on hover.
            _formSizeBar = UiKit.InlineFlyout(panel);
            _formSizeBar.HorizontalAlignment = HorizontalAlignment.Left;
            _formSizeBar.VerticalAlignment   = VerticalAlignment.Top;

            if (PagePreviewPanel.Parent is Grid g)
            {
                var bar = _formSizeBar;
                // Just under the field, aligned to its left edge (clamped inside the pane); flips
                // above the field when it sits at the bottom. Re-run on scroll/zoom and on the
                // bar's own size changes so it rides with the box instead of hanging in space.
                void Reposition()
                {
                    if (_formSizeBar != bar || _activeFormTb is null) return;
                    try
                    {
                        double barW = bar.ActualWidth  > 0 ? bar.ActualWidth  : 160;
                        double barH = bar.ActualHeight > 0 ? bar.ActualHeight : 34;
                        var below = _activeFormTb.TranslatePoint(new Point(0, _activeFormTb.ActualHeight), g);
                        double x = Math.Max(0, Math.Min(below.X, g.ActualWidth - barW));
                        double y = below.Y + 4;
                        if (y + barH > g.ActualHeight)
                            y = Math.Max(0, _activeFormTb.TranslatePoint(new Point(0, 0), g).Y - barH - 4);
                        bar.Margin = new Thickness(x, y, 0, 0);
                    }
                    catch { /* field mid-layout; the next scroll/size tick repositions */ }
                }

                Panel.SetZIndex(bar, 100);
                g.Children.Add(bar);
                Reposition();
                bar.SizeChanged += (_, _) => Reposition();   // the first real measure replaces the estimate
                _formBarScrollHook = (_, _) => Reposition();
                PagePreviewPanel.ScrollChanged += _formBarScrollHook;

                // Fade in to the pill's translucent rest state (hover solidifies it from there).
                bar.Opacity = 0;
                bar.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, UiKit.InlineFlyoutRestOpacity, new Duration(TimeSpan.FromMilliseconds(120)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        // A flat, non-focusable +/- step. It's a Border (not a Button) so clicking it doesn't move
        // keyboard focus out of the text field, which would otherwise blur the field and dismiss this
        // bar. The minus/plus are DRAWN (centered rounded rectangles), not font glyphs: the icon font
        // and the number's text font carry different line metrics, so glyph-based signs sat on a
        // slightly different vertical axis than the size readout between them and read as misaligned.
        // Fixed light color: the InlineFlyout pill is dark regardless of the app theme.
        // Shim for the original glyph-string call sites: E710 is the MDL2 Add glyph, anything
        // else is the minus. The glyphs themselves are no longer rendered (see above).
        private Border MakeFormSizeStep(string glyph, Action onClick) => MakeFormSizeStep(glyph == "", onClick);

        private Border MakeFormSizeStep(bool plus, Action onClick)
        {
            var fill = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            var shape = new Grid
            {
                Width = 9, Height = 9, SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            shape.Children.Add(new Border
            { Width = 9, Height = 1.6, CornerRadius = new CornerRadius(0.8), Background = fill, VerticalAlignment = VerticalAlignment.Center });
            if (plus)
                shape.Children.Add(new Border
                { Width = 1.6, Height = 9, CornerRadius = new CornerRadius(0.8), Background = fill, HorizontalAlignment = HorizontalAlignment.Center });
            var b = new Border
            {
                Width = 21, Height = 19, CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand,
                Margin = new Thickness(2, 0, 2, 0), Background = Brushes.Transparent, Child = shape
            };
            b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return b;
        }

        private void AdjustFormFontSize(int delta, TextBlock sizeLbl)
        {
            if (_activeFormTb is null) return;
            double scale = _activeFormScale > 0 ? _activeFormScale : 1;
            double pt = Math.Round(_activeFormTb.FontSize / scale);
            pt = Math.Max(4, Math.Min(96, pt + delta));
            _formFontSizes[_activeFormObj] = pt;
            _activeFormTb.FontSize = pt * scale;
            sizeLbl.Text = pt.ToString("0");
            MarkDirty(true);
        }

        private void HideFormSizeBar()
        {
            if (_formBarScrollHook is not null)
            {
                PagePreviewPanel.ScrollChanged -= _formBarScrollHook;
                _formBarScrollHook = null;
            }
            if (_formSizeBar is not null)
            {
                FadeOutAndRemoveBar(_formSizeBar);   // annotate-bar fade-out; removes it from its parent
                _formSizeBar = null;
            }
        }

        // Returns a /DA default-appearance string with its font size replaced (or a sensible default
        // when none exists), used to bake a user font-size override into the saved field.
        private static string WithDaFontSize(string? da, double pt)
        {
            string size = pt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(da)) return $"/Helv {size} Tf 0 g";
            var t = da!.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int i = 1; i < t.Count; i++)
                if (t[i] == "Tf") { t[i - 1] = size; return string.Join(" ", t); }
            return $"/Helv {size} Tf " + da;   // no Tf operator present; prepend a font selection
        }

        // Guided AcroForm signing -------------------------------------------------------------------
        private enum FormFillRole { None, Signature, Initials, Date }

        // Classifies a fillable field into a guided-signing role. A true PDF signature field
        // (/FT /Sig) is authoritative. Otherwise the name is matched on WHOLE WORDS, so labels
        // like "Computer Assigned" (contains the letters "sign") or "candidate"/"update" (contain
        // "date") are not mistaken for sign/date zones. Checkboxes, radios, dropdowns are never roles.
        private static FormFillRole ClassifyFormField(FormFieldInfo f)
        {
            if (f.IsCheckBox || f.IsRadio || f.FieldType == "/Ch") return FormFillRole.None;

            // A real signature field declares /FT /Sig - trust it regardless of name.
            if (f.FieldType.Contains("Sig")) return FormFillRole.Signature;

            string n = (f.FieldName ?? string.Empty).ToLowerInvariant();
            bool Word(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(n, pattern);

            if (Word(@"\binitials?\b"))               return FormFillRole.Initials;
            if (Word(@"\b(signature|signed|sign)\b")) return FormFillRole.Signature;
            if (Word(@"\bdated?\b"))                   return FormFillRole.Date;
            return FormFillRole.None;
        }

        // A highlighted, clickable overlay sized to the field rectangle. Clicking fills it.
        private UIElement BuildSignZone(FormFieldInfo f, bool initials, int pageIndex)
        {
            var accent = Color.FromRgb(0x2a, 0x6e, 0xa5);
            var zone = new Border
            {
                Tag             = FormOverlayTag,
                Width           = f.Cw,
                Height          = f.Ch,
                Background      = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1.4),
                CornerRadius    = new CornerRadius(2),
                Cursor          = Cursors.Hand,
                ToolTip         = initials ? "Click to add your initials" : "Click to sign",
                Child = new TextBlock
                {
                    Text                = initials ? "Initial" : "Sign",
                    FontSize            = Math.Max(8, Math.Min(f.Ch * 0.45, 12)),
                    FontWeight          = FontWeights.SemiBold,
                    Foreground          = new SolidColorBrush(accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    IsHitTestVisible    = false,
                },
            };
            double zx = f.Cx, zy = f.Cy, zw = f.Cw, zh = f.Ch; int zp = pageIndex, zo = f.ObjNum;
            zone.MouseLeftButtonDown += (_, e) => { e.Handled = true; FillSignField(initials, zo, zp, zx, zy, zw, zh); };
            return zone;
        }
    }
}
