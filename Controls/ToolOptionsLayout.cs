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
    }
}
