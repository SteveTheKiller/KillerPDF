using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Editing;

/// <summary>Aligns and distributes selected AcroForm widgets without changing field state.</summary>
public static class PdfFormLayoutEditor
{
    /// <summary>Aligns two or more indirect widgets on one page.</summary>
    public static byte[] Align(PdfDocument document, IEnumerable<int> objectNumbers,
        PdfFormWidgetAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment));
        PdfFormWidgetInfo[] widgets = Select(document, objectNumbers, minimum: 2);
        double target = alignment switch
        {
            PdfFormWidgetAlignment.Left => widgets.Min(item => item.Left),
            PdfFormWidgetAlignment.HorizontalCenter =>
                (widgets.Min(item => item.Left) + widgets.Max(item => item.Right)) / 2,
            PdfFormWidgetAlignment.Right => widgets.Max(item => item.Right),
            PdfFormWidgetAlignment.Bottom => widgets.Min(item => item.Bottom),
            PdfFormWidgetAlignment.VerticalCenter =>
                (widgets.Min(item => item.Bottom) + widgets.Max(item => item.Top)) / 2,
            PdfFormWidgetAlignment.Top => widgets.Max(item => item.Top),
            _ => throw new ArgumentOutOfRangeException(nameof(alignment))
        };
        var editor = new PdfIncrementalPageEditor(document);
        foreach (PdfFormWidgetInfo widget in widgets)
        {
            double width = widget.Right - widget.Left;
            double height = widget.Top - widget.Bottom;
            (double left, double bottom) = alignment switch
            {
                PdfFormWidgetAlignment.Left => (target, widget.Bottom),
                PdfFormWidgetAlignment.HorizontalCenter => (target - width / 2, widget.Bottom),
                PdfFormWidgetAlignment.Right => (target - width, widget.Bottom),
                PdfFormWidgetAlignment.Bottom => (widget.Left, target),
                PdfFormWidgetAlignment.VerticalCenter => (widget.Left, target - height / 2),
                PdfFormWidgetAlignment.Top => (widget.Left, target - height),
                _ => throw new ArgumentOutOfRangeException(nameof(alignment))
            };
            editor.SetFormWidgetRectangle(widget.ObjectNumber, widget.Generation,
                left, bottom, left + width, bottom + height);
        }
        return editor.Build();
    }

    /// <summary>Evenly distributes three or more widget centers on one page.</summary>
    public static byte[] Distribute(PdfDocument document,
        IEnumerable<int> objectNumbers, PdfFormWidgetDistribution distribution)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(distribution))
            throw new ArgumentOutOfRangeException(nameof(distribution));
        PdfFormWidgetInfo[] widgets = Select(document, objectNumbers, minimum: 3);
        PdfFormWidgetInfo[] ordered = distribution == PdfFormWidgetDistribution.Horizontal
            ? [.. widgets.OrderBy(CenterX)] : [.. widgets.OrderBy(CenterY)];
        double first = distribution == PdfFormWidgetDistribution.Horizontal
            ? CenterX(ordered[0]) : CenterY(ordered[0]);
        double last = distribution == PdfFormWidgetDistribution.Horizontal
            ? CenterX(ordered[^1]) : CenterY(ordered[^1]);
        double step = (last - first) / (ordered.Length - 1);
        var editor = new PdfIncrementalPageEditor(document);
        for (int index = 0; index < ordered.Length; index++)
        {
            PdfFormWidgetInfo widget = ordered[index];
            double width = widget.Right - widget.Left;
            double height = widget.Top - widget.Bottom;
            double center = first + step * index;
            double left = distribution == PdfFormWidgetDistribution.Horizontal
                ? center - width / 2 : widget.Left;
            double bottom = distribution == PdfFormWidgetDistribution.Vertical
                ? center - height / 2 : widget.Bottom;
            editor.SetFormWidgetRectangle(widget.ObjectNumber, widget.Generation,
                left, bottom, left + width, bottom + height);
        }
        return editor.Build();
    }

    private static PdfFormWidgetInfo[] Select(PdfDocument document,
        IEnumerable<int> objectNumbers, int minimum)
    {
        ArgumentNullException.ThrowIfNull(objectNumbers);
        int[] requested = objectNumbers.ToArray();
        if (requested.Length < minimum || requested.Any(number => number <= 0)
            || requested.Distinct().Count() != requested.Length)
            throw new ArgumentException(
                $"Select at least {minimum} distinct indirect widgets.", nameof(objectNumbers));
        var reader = new PdfPageContentReader(document);
        PdfFormWidgetInfo[] all = [.. Enumerable.Range(0, reader.PageCount)
            .SelectMany(page => PdfFormWidgetReader.ReadPage(document, page))];
        PdfFormWidgetInfo[] selected = [.. requested.Select(number =>
            all.SingleOrDefault(widget => widget.ObjectNumber == number)
                ?? throw new KeyNotFoundException($"Form widget {number} was not found."))];
        if (selected.Select(widget => widget.PageIndex).Distinct().Count() != 1)
            throw new ArgumentException(
                "Selected form widgets must be on the same page.", nameof(objectNumbers));
        return selected;
    }

    private static double CenterX(PdfFormWidgetInfo widget) =>
        (widget.Left + widget.Right) / 2;
    private static double CenterY(PdfFormWidgetInfo widget) =>
        (widget.Bottom + widget.Top) / 2;
}

/// <summary>An edge or center used to align form widgets.</summary>
public enum PdfFormWidgetAlignment
{
    /// <summary>Align left edges.</summary>
    Left,
    /// <summary>Align horizontal centers.</summary>
    HorizontalCenter,
    /// <summary>Align right edges.</summary>
    Right,
    /// <summary>Align bottom edges.</summary>
    Bottom,
    /// <summary>Align vertical centers.</summary>
    VerticalCenter,
    /// <summary>Align top edges.</summary>
    Top
}

/// <summary>The axis used to distribute form widgets.</summary>
public enum PdfFormWidgetDistribution
{
    /// <summary>Distribute horizontal centers.</summary>
    Horizontal,
    /// <summary>Distribute vertical centers.</summary>
    Vertical
}
