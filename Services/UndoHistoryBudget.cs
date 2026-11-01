using System;
using System.Collections.Generic;

namespace KillerPDF.Services
{
    /// <summary>Keeps newest-first undo histories within both a depth and memory budget.</summary>
    internal static class UndoHistoryBudget
    {
        internal static bool TryApply<T>(Stack<T> history, Func<T, bool> apply)
        {
            if (history.Count == 0) return false;
            if (!apply(history.Peek())) return false;
            history.Pop();
            return true;
        }

        internal static void PushBounded<T>(Stack<T> history, T entry,
            Func<T, long> sizeOf, int maximumEntries, long maximumBytes)
        {
            ArgumentNullException.ThrowIfNull(history);
            ArgumentNullException.ThrowIfNull(sizeOf);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

            history.Push(entry);
            T[] newestFirst = [.. history];
            var retained = new List<T>(Math.Min(newestFirst.Length, maximumEntries));
            long retainedBytes = 0;
            foreach (T candidate in newestFirst)
            {
                long candidateBytes = Math.Max(0, sizeOf(candidate));
                if (retained.Count > 0 &&
                    (retained.Count >= maximumEntries || candidateBytes > maximumBytes - retainedBytes))
                    break;
                retained.Add(candidate);
                retainedBytes = Math.Min(maximumBytes, retainedBytes + candidateBytes);
            }

            history.Clear();
            for (int index = retained.Count - 1; index >= 0; index--)
                history.Push(retained[index]);
        }
    }
}
