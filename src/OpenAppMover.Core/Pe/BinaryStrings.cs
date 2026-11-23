using System.Text;

namespace OpenAppMover.Core.Pe;

/// <summary>Strings stored in a binary file.</summary>
public static class BinaryStrings
{
    private const int MaxNameLength = 64;

    /// <summary>
    /// Bare .ini file names ("budget.ini", no folder) stored as NUL-terminated ANSI or UTF-16 strings, lower case. Given to
    /// GetPrivateProfileString and WritePrivateProfileString without a path, such a file is in the Windows folder.
    /// </summary>
    public static IReadOnlyList<string> FindIniFileNames(byte[] data)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] != '.')
                continue;
            if (Lower(data, i + 1) == 'i' && Lower(data, i + 2) == 'n' && Lower(data, i + 3) == 'i' && At(data, i + 4) == 0)
                Add(names, ReadBack(data, i, 1));
            if (Lower(data, i + 2) == 'i' && Lower(data, i + 4) == 'n' && Lower(data, i + 6) == 'i'
                && At(data, i + 1) == 0 && At(data, i + 3) == 0 && At(data, i + 5) == 0 && At(data, i + 7) == 0
                && At(data, i + 8) == 0 && At(data, i + 9) == 0)
                Add(names, ReadBack(data, i, 2));
        }
        return names.Select(n => n.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>The file name characters before the '.' at <paramref name="dot"/>, when a NUL (start of string) precedes them.</summary>
    private static string? ReadBack(byte[] data, int dot, int width)
    {
        var name = new StringBuilder();
        for (var i = dot - width; i >= 0 && name.Length <= MaxNameLength; i -= width)
        {
            var c = data[i];
            if (width == 2 && At(data, i + 1) != 0)
                return null;
            if (c == 0)
                return name.Length == 0 ? null : name.ToString();
            if (!IsNameChar(c))
                return null;
            name.Insert(0, (char)c);
        }
        return null;
    }

    private static void Add(HashSet<string> names, string? name)
    {
        if (name is not null)
            names.Add(name + ".ini");
    }

    /// <summary>ASCII characters of an ordinary file name (no path separator, drive or format specifier).</summary>
    private static bool IsNameChar(byte c) =>
        c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-' or (byte)' ' or (byte)'.';

    private static int At(byte[] data, int index) => index < data.Length ? data[index] : -1;

    private static int Lower(byte[] data, int index) => index < data.Length ? char.ToLowerInvariant((char)data[index]) : -1;
}
