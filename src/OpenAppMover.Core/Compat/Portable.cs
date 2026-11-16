using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenAppMover;

/// <summary>
/// Static .NET 8 APIs that .NET Framework 4.8 (Windows 7 build) lacks, with the same behavior on both targets.
/// The missing instance methods are extension methods in NetFxExtensions.cs (.NET Framework only).
/// </summary>
public static class Portable
{
    /// <summary>ISO-8859-1 (<c>Encoding.Latin1</c>).</summary>
    public static Encoding Latin1 { get; } =
#if NETFRAMEWORK
        Encoding.GetEncoding(28591);
#else
        Encoding.Latin1;
#endif

    /// <summary>Uppercase hexadecimal (<c>Convert.ToHexString</c>).</summary>
    public static string ToHexString(ReadOnlySpan<byte> bytes)
    {
#if NETFRAMEWORK
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            hex.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return hex.ToString();
#else
        return Convert.ToHexString(bytes);
#endif
    }

    /// <summary><c>SHA256.HashData</c> of a stream, read to its end.</summary>
    public static byte[] Sha256(Stream stream)
    {
#if NETFRAMEWORK
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
#else
        return SHA256.HashData(stream);
#endif
    }

    /// <summary><c>SHA256.HashData</c> of bytes.</summary>
    public static byte[] Sha256(ReadOnlySpan<byte> data)
    {
#if NETFRAMEWORK
        using var sha = SHA256.Create();
        return sha.ComputeHash(data.ToArray());
#else
        return SHA256.HashData(data);
#endif
    }

    /// <summary>Cryptographically strong random bytes (<c>RandomNumberGenerator.Fill</c>).</summary>
    public static void FillRandom(Span<byte> data)
    {
#if NETFRAMEWORK
        var bytes = new byte[data.Length];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);
        bytes.CopyTo(data);
#else
        RandomNumberGenerator.Fill(data);
#endif
    }

    /// <summary>PBKDF2 (<c>Rfc2898DeriveBytes.Pbkdf2</c>). .NET Framework requires a salt of 8 bytes or more.</summary>
    public static byte[] Pbkdf2(string password, byte[] salt, int iterations, HashAlgorithmName hash, int length)
    {
#if NETFRAMEWORK
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, hash);
        return pbkdf2.GetBytes(length);
#else
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, hash, length);
#endif
    }

    /// <summary>Constant-time comparison (<c>CryptographicOperations.FixedTimeEquals</c>).</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
#if NETFRAMEWORK
        if (left.Length != right.Length)
            return false;
        var difference = 0;
        for (var i = 0; i < left.Length; i++)
            difference |= left[i] ^ right[i];
        return difference == 0;
#else
        return CryptographicOperations.FixedTimeEquals(left, right);
#endif
    }

    /// <summary><c>File.Move(source, destination, overwrite)</c>: replaces the destination atomically when it exists.</summary>
    public static void MoveFile(string source, string destination, bool overwrite)
    {
#if NETFRAMEWORK
        if (!overwrite)
            File.Move(source, destination);
        else if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            // Mono (tests of the .NET Framework build outside Windows).
            File.Delete(destination);
            File.Move(source, destination);
        }
        else if (!MoveFileEx(source, destination, MoveFileReplaceExisting | MoveFileCopyAllowed))
            throw new IOException($"Cannot move {source} to {destination}.", Marshal.GetHRForLastWin32Error());
#else
        File.Move(source, destination, overwrite);
#endif
    }

    /// <summary>Loads a native DLL by its full path (<c>NativeLibrary.Load</c>).</summary>
    public static IntPtr LoadNativeLibrary(string path)
    {
#if NETFRAMEWORK
        var handle = LoadLibrary(path);
        return handle != IntPtr.Zero ? handle
            : throw new DllNotFoundException($"Cannot load {path} (error {Marshal.GetLastWin32Error()}).");
#else
        return NativeLibrary.Load(path);
#endif
    }

#if NETFRAMEWORK
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryW")]
    private static extern IntPtr LoadLibrary(string fileName);

    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileCopyAllowed = 0x2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
#endif

    /// <summary>Adds the arguments to the process command line (<c>ProcessStartInfo.ArgumentList</c>).</summary>
    public static void AddArguments(ProcessStartInfo info, IEnumerable<string> arguments)
    {
#if NETFRAMEWORK
        var line = new StringBuilder(info.Arguments);
        foreach (var argument in arguments)
        {
            if (line.Length > 0)
                line.Append(' ');
            AppendQuoted(line, argument);
        }
        info.Arguments = line.ToString();
#else
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
#endif
    }

#if NETFRAMEWORK
    /// <summary>Quotes an argument the way CommandLineToArgvW / the C runtime parse it back (same rules as .NET).</summary>
    private static void AppendQuoted(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }
        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal, except before a quote: they are then doubled, and the quote escaped.
            line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        line.Append('\\', backslashes * 2).Append('"');
    }
#endif

    /// <summary>A GUID from its 16-byte binary form (<c>new Guid(ReadOnlySpan&lt;byte&gt;)</c>).</summary>
    public static Guid ToGuid(ReadOnlySpan<byte> bytes) =>
#if NETFRAMEWORK
        new(bytes.ToArray());
#else
        new(bytes);
#endif

    /// <summary>Path of the running executable (<c>Environment.ProcessPath</c>).</summary>
    public static string? ProcessPath =>
#if NETFRAMEWORK
        Process.GetCurrentProcess().MainModule?.FileName;
#else
        Environment.ProcessPath;
#endif

    /// <summary>
    /// <c>Split(separator, count, StringSplitOptions.TrimEntries)</c>, plus <c>RemoveEmptyEntries</c> when
    /// <paramref name="removeEmpty"/> (entries empty after trimming): TrimEntries does not exist in .NET Framework.
    /// </summary>
    public static string[] SplitTrimmed(string s, char separator, int count = int.MaxValue, bool removeEmpty = false)
    {
#if NETFRAMEWORK
        var entries = s.Split([separator], count).Select(e => e.Trim());
        return (removeEmpty ? entries.Where(e => e.Length > 0) : entries).ToArray();
#else
        return s.Split(separator, count,
            StringSplitOptions.TrimEntries | (removeEmpty ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None));
#endif
    }

    /// <summary>Beyond this length, a path (a directory: room left for an 8.3 file name) exceeds MAX_PATH (260).</summary>
    private const int MaxShortPath = 248;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// A long absolute path with the \\?\ prefix, which lifts the MAX_PATH limit on Windows 7 (.NET 8 adds it itself).
    /// Short, relative or non-canonical paths ("." or ".." segments: not resolved behind the prefix) are returned unchanged.
    /// </summary>
    public static string WithExtendedPrefix(string path)
    {
        if (path.Length < MaxShortPath || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.IndexOf('/') >= 0
            || path.Contains(@"\.\", StringComparison.Ordinal) || path.Contains(@"\..\", StringComparison.Ordinal)
            || path.EndsWith(@"\.", StringComparison.Ordinal) || path.EndsWith(@"\..", StringComparison.Ordinal))
            return path;
        if (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return ExtendedPrefix + path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2 && path[2] != '.')
            return ExtendedUncPrefix + path.Substring(2);
        return path;
    }

    /// <summary>The usual form of a path returned with the \\?\ prefix.</summary>
    public static string WithoutExtendedPrefix(string path) =>
        path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase) ? @"\\" + path.Substring(ExtendedUncPrefix.Length)
        : path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? path.Substring(ExtendedPrefix.Length)
        : path;

    public static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    public static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';

    public static bool IsAsciiLetterOrDigit(char c) => IsAsciiLetter(c) || IsAsciiDigit(c);
}
