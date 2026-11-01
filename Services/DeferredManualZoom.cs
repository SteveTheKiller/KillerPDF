namespace KillerPDF.Services;

internal sealed class DeferredManualZoom
{
    private readonly List<(bool Scale, double Value)> _operations = [];

    internal DeferredManualZoom(double trueZoom)
    {
        if (!double.IsFinite(trueZoom) || trueZoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(trueZoom));
        TrueZoom = trueZoom;
    }

    internal double TrueZoom { get; }
    internal bool HasOperations => _operations.Count != 0;

    internal void QueueScale(double scale)
    {
        if (double.IsNaN(scale) || scale < 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        _operations.Add((true, scale));
    }

    internal void QueueStep(double amount)
    {
        if (!double.IsFinite(amount))
            throw new ArgumentOutOfRangeException(nameof(amount));
        _operations.Add((false, amount));
    }

    internal double Resolve(double displayFactor, double min, double max)
    {
        if (!double.IsFinite(displayFactor) || displayFactor <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayFactor));
        return ResolveFromInternal(TrueZoom / displayFactor, min, max);
    }

    internal double ResolveFromInternal(double initialZoom, double min, double max)
    {
        if (double.IsNaN(initialZoom) || initialZoom < 0)
            throw new ArgumentOutOfRangeException(nameof(initialZoom));
        if (!double.IsFinite(min) || min <= 0)
            throw new ArgumentOutOfRangeException(nameof(min));
        if (!double.IsFinite(max) || max < min)
            throw new ArgumentOutOfRangeException(nameof(max));
        double current = Math.Clamp(initialZoom, min, max);
        foreach (var operation in _operations)
            current = Math.Clamp(operation.Scale ? current * operation.Value : current + operation.Value, min, max);
        return current;
    }
}
