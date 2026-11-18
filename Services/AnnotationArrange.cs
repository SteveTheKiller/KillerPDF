using System.Windows;

namespace KillerPDF.Services;

// Lines up, spaces and sizes several selected boxes at once (#504). Works on the boxes' bounds in
// page-canvas units and returns where each box goes, in the order given; the viewer moves and sizes
// the marks to match.
internal static class AnnotationArrange
{
    internal enum Action
    {
        AlignLeft, AlignCenter, AlignRight, AlignTop, AlignMiddle, AlignBottom,
        DistributeHorizontally, DistributeVertically,
        MoreHorizontalSpace, LessHorizontalSpace, MoreVerticalSpace, LessVerticalSpace,
        SameWidth, SameHeight, SameSize,
        FitWidth, FitHeight, FitSize
    }

    // How far one press of More or Less space moves each gap, in canvas units.
    internal const double SpacingStep = 4;

    internal static bool IsFit(Action action) => action is Action.FitWidth or Action.FitHeight or Action.FitSize;

    // Fitting works box by box, so one box is enough.
    internal static bool Applies(Action action, int count) =>
        count >= (IsFit(action) ? 1 : action is Action.DistributeHorizontally or Action.DistributeVertically ? 3 : 2);

    // Snaps each text box's frame to its text, keeping its top-left corner. fitted(i, width) gives
    // box i's text size laid out at that width (null for one line, unwrapped), or null when box i
    // holds no text and stays as it is.
    internal static Rect[] Fit(IReadOnlyList<Rect> boxes, Action action, Func<int, double?, Size?> fitted)
    {
        Rect[] result = [.. boxes];
        for (int i = 0; i < result.Length; i++)
        {
            Rect box = result[i];
            switch (action)
            {
                case Action.FitWidth when fitted(i, null) is { } line:
                    result[i] = box with { Width = line.Width };
                    break;
                case Action.FitHeight when fitted(i, box.Width) is { } wrapped:
                    result[i] = box with { Height = wrapped.Height };
                    break;
                case Action.FitSize when fitted(i, null) is { } natural:
                    result[i] = new Rect(box.Location, natural);
                    break;
            }
        }
        return result;
    }

    internal static Rect[] Apply(IReadOnlyList<Rect> boxes, Action action)
    {
        Rect[] result = [.. boxes];
        if (!Applies(action, result.Length)) return result;
        double left = boxes.Min(box => box.Left), right = boxes.Max(box => box.Right);
        double top = boxes.Min(box => box.Top), bottom = boxes.Max(box => box.Bottom);
        for (int i = 0; i < result.Length; i++)
        {
            Rect box = result[i];
            result[i] = action switch
            {
                Action.AlignLeft => box with { X = left },
                Action.AlignCenter => box with { X = (left + right - box.Width) / 2 },
                Action.AlignRight => box with { X = right - box.Width },
                Action.AlignTop => box with { Y = top },
                Action.AlignMiddle => box with { Y = (top + bottom - box.Height) / 2 },
                Action.AlignBottom => box with { Y = bottom - box.Height },
                Action.SameWidth => box with { Width = boxes.Max(item => item.Width) },
                Action.SameHeight => box with { Height = boxes.Max(item => item.Height) },
                Action.SameSize => new Rect(box.Location,
                    new Size(boxes.Max(item => item.Width), boxes.Max(item => item.Height))),
                _ => box
            };
        }
        return action switch
        {
            Action.DistributeHorizontally => Distribute(result, horizontal: true),
            Action.DistributeVertically => Distribute(result, horizontal: false),
            Action.MoreHorizontalSpace => Space(result, horizontal: true, SpacingStep),
            Action.LessHorizontalSpace => Space(result, horizontal: true, -SpacingStep),
            Action.MoreVerticalSpace => Space(result, horizontal: false, SpacingStep),
            Action.LessVerticalSpace => Space(result, horizontal: false, -SpacingStep),
            _ => result
        };
    }

    // The first and last boxes along the axis stay put and the boxes between them get equal gaps.
    private static Rect[] Distribute(Rect[] boxes, bool horizontal)
    {
        int[] order = Order(boxes, horizontal);
        Rect first = boxes[order[0]], last = boxes[order[^1]];
        double span = horizontal ? last.Right - first.Left : last.Bottom - first.Top;
        double gap = (span - order.Sum(i => Length(boxes[i], horizontal))) / (order.Length - 1);
        // Boxes that overlap too much for any gap get evenly spaced centers between the outermost
        // centers instead, so nothing piles up past where the selection started.
        if (gap < 0) return DistributeCenters(boxes, horizontal);
        double at = Start(first, horizontal);
        foreach (int i in order)
        {
            boxes[i] = Moved(boxes[i], horizontal, at);
            at += Length(boxes[i], horizontal) + gap;
        }
        return boxes;
    }

    private static Rect[] DistributeCenters(Rect[] boxes, bool horizontal)
    {
        double Center(Rect box) => Start(box, horizontal) + Length(box, horizontal) / 2;
        int[] order = [.. Enumerable.Range(0, boxes.Length).OrderBy(i => Center(boxes[i]))];
        double low = Center(boxes[order[0]]), step = (Center(boxes[order[^1]]) - low) / (order.Length - 1);
        for (int n = 1; n < order.Length - 1; n++)
        {
            Rect box = boxes[order[n]];
            boxes[order[n]] = Moved(box, horizontal, low + step * n - Length(box, horizontal) / 2);
        }
        return boxes;
    }

    // The first box along the axis stays put and every gap after it grows or shrinks by the step.
    // A gap never shrinks past the point where two boxes touch.
    private static Rect[] Space(Rect[] boxes, bool horizontal, double step)
    {
        int[] order = Order(boxes, horizontal);
        double shift = 0;
        for (int n = 1; n < order.Length; n++)
        {
            Rect before = boxes[order[n - 1]], box = boxes[order[n]];
            double gap = Start(box, horizontal) - (Start(before, horizontal) - shift + Length(before, horizontal));
            shift += step < 0 ? -Math.Min(-step, Math.Max(0, gap)) : step;
            boxes[order[n]] = Moved(box, horizontal, Start(box, horizontal) + shift);
        }
        return boxes;
    }

    private static int[] Order(Rect[] boxes, bool horizontal) =>
        [.. Enumerable.Range(0, boxes.Length).OrderBy(i => Start(boxes[i], horizontal)).ThenBy(i => i)];

    private static double Start(Rect box, bool horizontal) => horizontal ? box.X : box.Y;

    private static double Length(Rect box, bool horizontal) => horizontal ? box.Width : box.Height;

    private static Rect Moved(Rect box, bool horizontal, double start) =>
        horizontal ? box with { X = start } : box with { Y = start };
}
