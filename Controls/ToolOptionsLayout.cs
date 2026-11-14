namespace KillerPDF.Controls
{
    /// <summary>Which options bar a tool docks above the document, if any.</summary>
    public enum ToolOptionsBarKind { None, Text, Draw, Crop, Redact }

    /// <summary>The tools that own a docked options bar, and which bar each one builds.</summary>
    public static class ToolOptionsCatalog
    {
        /// <summary>The bar the tool shows. A language or theme change rebuilds every bar this
        /// names, so a tool that is missing here keeps a stale bar until it is chosen again.</summary>
        public static ToolOptionsBarKind BarFor(EditTool? tool) => tool switch
        {
            EditTool.Text => ToolOptionsBarKind.Text,
            EditTool.Draw or EditTool.Highlight or EditTool.Line or EditTool.Strikethrough
                or EditTool.Underline or EditTool.Shape or EditTool.Measure => ToolOptionsBarKind.Draw,
            EditTool.Crop => ToolOptionsBarKind.Crop,
            EditTool.Redact => ToolOptionsBarKind.Redact,
            _ => ToolOptionsBarKind.None,
        };

        /// <summary>The order a tool's groups leave the row for the "..." menu as its pane narrows,
        /// first to go first (redesign 3.4). Groups not named here stay, unless even they cannot
        /// fit.</summary>
        public static IReadOnlyList<string> ShedOrder(EditTool tool) => tool switch
        {
            EditTool.Measure => [ToolOptionGroup.Opacity, ToolOptionGroup.Size, ToolOptionGroup.PageSize,
                ToolOptionGroup.Report, ToolOptionGroup.Units, ToolOptionGroup.Color],
            EditTool.Text => [ToolOptionGroup.Blur, ToolOptionGroup.Spacing, ToolOptionGroup.FillOpacity,
                ToolOptionGroup.Opacity, ToolOptionGroup.Paragraph, ToolOptionGroup.Fill],
            EditTool.Highlight => [ToolOptionGroup.Eraser, ToolOptionGroup.Opacity],
            EditTool.Shape => [ToolOptionGroup.Opacity, ToolOptionGroup.Size, ToolOptionGroup.Fill],
            EditTool.Draw or EditTool.Line => [ToolOptionGroup.Opacity, ToolOptionGroup.Size],
            EditTool.Underline or EditTool.Strikethrough => [ToolOptionGroup.Opacity],
            EditTool.Crop => [ToolOptionGroup.Pages, ToolOptionGroup.Units],
            EditTool.Redact => [ToolOptionGroup.Properties, ToolOptionGroup.MarkSearch, ToolOptionGroup.MarkPages,
                ToolOptionGroup.Find, ToolOptionGroup.Clear],
            _ => [],
        };
    }

    /// <summary>The ids of the groups an options bar can hold.</summary>
    public static class ToolOptionGroup
    {
        public const string Modes = "modes", Scale = "scale", Units = "units", PageSize = "pagesize",
            Report = "report", Color = "color", Size = "size", Opacity = "opacity", Font = "font",
            Style = "style", Fill = "fill", FillOpacity = "fillopacity", Spacing = "spacing", Blur = "blur",
            Paragraph = "paragraph", Layers = "layers", Eraser = "eraser", Kinds = "kinds",
            Position = "position", Pages = "pages", Apply = "apply", Count = "count", Find = "find",
            MarkSearch = "marksearch", MarkPages = "markpages", Properties = "properties", Clear = "clear",
            EnterKey = "enterkey";
    }

    /// <summary>One group as the row measured it: its frozen width, the narrower width of its
    /// compact form if it has one, and whether it only ever lives in the "..." menu.</summary>
    public sealed record ToolOptionsGroupSpec(string Id, double Width, double? CompactWidth = null,
        bool OverflowOnly = false);

    /// <summary>Which groups stay in the row (in row order), which go to the "..." menu (also in
    /// row order), and whether the color group shows as its compact chip.</summary>
    public sealed record ToolOptionsAssignment(IReadOnlyList<string> Inline, IReadOnlyList<string> Overflow,
        bool Compact);

    /// <summary>Fits a tool's groups into one row of a given width.</summary>
    public static class ToolOptionsLayout
    {
        /// <summary>Horizontal padding of the row, both sides together.</summary>
        public const double Padding = 16;
        /// <summary>A separator between two groups: 1px line and 8px on each side.</summary>
        public const double Separator = 17;
        /// <summary>The "..." button and its 6px lead.</summary>
        public const double OverflowButton = 34;

        /// <summary>
        /// One line, never two. When the groups do not fit, the color group first drops to its chip
        /// (that loses nothing), then groups leave in shed order, and if even the groups that never
        /// shed cannot fit they leave from the right, keeping the first one and the "..." button.
        /// Widths are measured once by the caller and frozen, so the answer for a width never changes.
        /// </summary>
        public static ToolOptionsAssignment Assign(double available, IReadOnlyList<ToolOptionsGroupSpec> groups,
            IReadOnlyList<string> shedOrder)
        {
            var shed = new HashSet<string>(groups.Where(g => g.OverflowOnly).Select(g => g.Id));
            bool compact = false;

            double Need()
            {
                double width = Padding;
                int shown = 0;
                foreach (var group in groups)
                {
                    if (shed.Contains(group.Id)) continue;
                    width += compact && group.CompactWidth is double chip ? chip : group.Width;
                    shown++;
                }
                width += Separator * Math.Max(0, shown - 1);
                if (shed.Count > 0) width += OverflowButton;
                return width;
            }

            if (Need() > available && groups.Any(g => g.CompactWidth is not null && !shed.Contains(g.Id)))
                compact = true;
            foreach (string id in shedOrder)
            {
                if (Need() <= available) break;
                if (groups.Any(g => g.Id == id)) shed.Add(id);
            }
            for (int i = groups.Count - 1; i > 0 && Need() > available; i--)
                shed.Add(groups[i].Id);

            return new([.. groups.Where(g => !shed.Contains(g.Id)).Select(g => g.Id)],
                [.. groups.Where(g => shed.Contains(g.Id)).Select(g => g.Id)], compact);
        }
    }
}
