namespace KillerPDF.Services;

internal static class ContinuousRenderOrder
{
    internal static IEnumerable<int> Around(int center, int first, int last)
    {
        if (first > last) yield break;
        center = Math.Clamp(center, first, last);
        yield return center;
        int distance = 1;
        while (center + distance <= last || center - distance >= first)
        {
            if (center + distance <= last) yield return center + distance;
            if (center - distance >= first) yield return center - distance;
            distance++;
        }
    }
}
