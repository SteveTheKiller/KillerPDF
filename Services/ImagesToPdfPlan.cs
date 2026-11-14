using System.IO;

namespace KillerPDF.Services;

/// <summary>
/// Plans a PDF made from images Explorer hands KillerPDF: through Open with (#480), which passes plain
/// file paths and opens the result as a new document, or through the Save as KillerPDF verb (#479), which
/// passes <see cref="VerbSwitch"/> and writes the PDF beside the images without a window.
///
/// Explorer starts one process per selected file, so the files arrive in whatever order those processes
/// win the race. Pages therefore follow the file names the way Explorer sorts them by name (numbers in
/// numeric order: "scan 2" before "scan 10"), and the PDF is named after the first of them.
/// </summary>
internal static class ImagesToPdfPlan
{
    internal const string VerbSwitch = "--images-to-pdf";

    /// <summary>How long the open window waits after the last image arrives before making the PDF.</summary>
    internal static readonly TimeSpan OpenWithQuietPeriod = TimeSpan.FromMilliseconds(750);

    /// <summary>The image types both entry points accept, the same ones dropping and Import take.</summary>
    internal static readonly IReadOnlyList<string> Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];

    internal static bool IsImage(string path)
    {
        string extension = Path.GetExtension(path);
        return Extensions.Any(known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The files of a Save as KillerPDF launch, or null when the launch is something else.</summary>
    internal static IReadOnlyList<string>? VerbFiles(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && string.Equals(arguments[0], VerbSwitch, StringComparison.OrdinalIgnoreCase)
            ? [.. arguments.Skip(1)]
            : null;

    /// <summary>The images of an Open with launch: every argument is an image path. Null for anything else,
    /// such as a PDF, a mix of images and documents, or a command, which keep their normal handling.</summary>
    internal static IReadOnlyList<string>? OpenWithImages(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments.All(argument => !argument.StartsWith('-') && !argument.StartsWith('/') && IsImage(argument))
            ? [.. arguments]
            : null;

    /// <summary>Full paths, each image once, in name order (numbers numerically), then by folder.</summary>
    internal static IReadOnlyList<string> Order(IEnumerable<string> paths) =>
        [.. paths.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), Comparer<string>.Create(CompareNames))
            .ThenBy(path => Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase)];

    /// <summary>Compares names the way Explorer's name sort does: runs of digits by value, other text
    /// without regard to case.</summary>
    internal static int CompareNames(string? left, string? right)
    {
        left ??= ""; right ??= "";
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                int startLeft = i, startRight = j;
                while (i < left.Length && char.IsAsciiDigit(left[i])) i++;
                while (j < right.Length && char.IsAsciiDigit(right[j])) j++;
                ReadOnlySpan<char> a = left.AsSpan(startLeft, i - startLeft).TrimStart('0');
                ReadOnlySpan<char> b = right.AsSpan(startRight, j - startRight).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int digits = a.SequenceCompareTo(b);
                if (digits != 0) return digits;
                continue;
            }
            int text = string.Compare(left, i, right, j, 1, StringComparison.OrdinalIgnoreCase);
            if (text != 0) return text;
            i++; j++;
        }
        int remaining = (left.Length - i).CompareTo(right.Length - j);
        return remaining != 0 ? remaining : string.Compare(left, right, StringComparison.Ordinal);
    }

    /// <summary>The first image's name with .pdf, which a new document offers on its first Save.</summary>
    internal static string SuggestedName(IReadOnlyList<string> ordered) =>
        Path.GetFileNameWithoutExtension(ordered[0]) + ".pdf";

    /// <summary>The PDF beside the first image: "name.pdf", or "name (2).pdf" and up when that is taken.
    /// An existing file is never replaced.</summary>
    internal static string OutputPath(IReadOnlyList<string> ordered, Func<string, bool> exists)
    {
        for (int attempt = 1; ; attempt++)
        {
            string candidate = CandidatePath(ordered[0], attempt);
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>The output name for one attempt: 1 is "name.pdf", 2 is "name (2).pdf" and so on.</summary>
    internal static string CandidatePath(string firstImage, int attempt)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(firstImage)) ?? "";
        string name = Path.GetFileNameWithoutExtension(firstImage);
        return Path.Combine(folder, attempt <= 1 ? name + ".pdf" : $"{name} ({attempt}).pdf");
    }
}
