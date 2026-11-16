#if NETFRAMEWORK
// Instance methods of .NET 8 that .NET Framework 4.8 lacks (Windows 7 build), as extension methods with the same
// signatures: the call sites stay unchanged. Public, so that every assembly referencing Core shares this single copy.
// Declared in the namespaces of the extended types (imported by ImplicitUsings).
using System.Security.Cryptography;
using System.Text;

namespace System
{
    public static class NetFxStringExtensions
    {
        public static bool Contains(this string s, char value) => s.IndexOf(value) >= 0;

        public static bool Contains(this string s, string value, StringComparison comparison) =>
            s.IndexOf(value, comparison) >= 0;

        public static bool StartsWith(this string s, char value) => s.Length > 0 && s[0] == value;

        public static bool EndsWith(this string s, char value) => s.Length > 0 && s[s.Length - 1] == value;

        public static int IndexOf(this string s, char value, StringComparison comparison) =>
            s.IndexOf(value.ToString(), comparison);

        public static string[] Split(this string s, char separator, StringSplitOptions options) =>
            s.Split([separator], options);

        public static string[] Split(this string s, char separator, int count, StringSplitOptions options = StringSplitOptions.None) =>
            s.Split([separator], count, options);

        public static string[] Split(this string s, string separator, StringSplitOptions options = StringSplitOptions.None) =>
            s.Split([separator], options);

        public static string Replace(this string s, string oldValue, string? newValue, StringComparison comparison)
        {
            if (oldValue.Length == 0)
                throw new ArgumentException("The string to replace cannot be empty.", nameof(oldValue));
            var result = new StringBuilder(s.Length);
            var start = 0;
            int index;
            while ((index = s.IndexOf(oldValue, start, comparison)) >= 0)
            {
                result.Append(s, start, index - start).Append(newValue);
                start = index + oldValue.Length;
            }
            return start == 0 ? s : result.Append(s, start, s.Length - start).ToString();
        }
    }
}

namespace System.Collections.Generic
{
    public static class NetFxCollectionExtensions
    {
        public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key))
                return false;
            dictionary.Add(key, value);
            return true;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
            dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue) =>
            dictionary.TryGetValue(key, out var value) ? value : defaultValue;

        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}

namespace System.Linq
{
    public static class NetFxEnumerableExtensions
    {
        public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source, IComparer<T>? comparer = null) =>
            source.OrderBy(x => x, comparer);

        public static IOrderedEnumerable<T> OrderDescending<T>(this IEnumerable<T> source) => source.OrderByDescending(x => x);

        public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector,
            IEqualityComparer<TKey>? comparer = null)
        {
            var seen = new HashSet<TKey>(comparer);
            foreach (var item in source)
            {
                if (seen.Add(keySelector(item)))
                    yield return item;
            }
        }

        public static T? MaxBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            var comparer = Comparer<TKey>.Default;
            using var e = source.GetEnumerator();
            if (!e.MoveNext())
                return default;
            var best = e.Current;
            var bestKey = keySelector(best);
            while (e.MoveNext())
            {
                var key = keySelector(e.Current);
                if (comparer.Compare(key, bestKey) > 0)
                    (best, bestKey) = (e.Current, key);
            }
            return best;
        }
    }
}

namespace System.IO
{
    public static class NetFxStreamExtensions
    {
        public static void Write(this Stream stream, ReadOnlySpan<byte> buffer) => stream.Write(buffer.ToArray(), 0, buffer.Length);

        public static int Read(this Stream stream, Span<byte> buffer)
        {
            var array = new byte[buffer.Length];
            var read = stream.Read(array, 0, array.Length);
            array.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public static void ReadExactly(this Stream stream, byte[] buffer) => ReadExactly(stream, buffer, 0, buffer.Length);

        public static void ReadExactly(this Stream stream, byte[] buffer, int offset, int count)
        {
            if (ReadAtLeast(stream, buffer, offset, count) < count)
                throw new EndOfStreamException();
        }

        public static void ReadExactly(this Stream stream, Span<byte> buffer)
        {
            var array = new byte[buffer.Length];
            stream.ReadExactly(array);
            array.CopyTo(buffer);
        }

        public static int ReadAtLeast(this Stream stream, Span<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true)
        {
            var array = new byte[buffer.Length];
            var read = ReadAtLeast(stream, array, 0, minimumBytes, array.Length);
            if (read < minimumBytes && throwOnEndOfStream)
                throw new EndOfStreamException();
            array.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        private static int ReadAtLeast(Stream stream, byte[] buffer, int offset, int count) =>
            ReadAtLeast(stream, buffer, offset, count, count);

        /// <summary>Reads until <paramref name="minimum"/> bytes (at most <paramref name="maximum"/>) or the end of the stream.</summary>
        private static int ReadAtLeast(Stream stream, byte[] buffer, int offset, int minimum, int maximum)
        {
            var total = 0;
            while (total < minimum)
            {
                var read = stream.Read(buffer, offset + total, maximum - total);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }
    }
}

namespace System.Text
{
    public static class NetFxEncodingExtensions
    {
        public static string GetString(this Encoding encoding, ReadOnlySpan<byte> bytes) => encoding.GetString(bytes.ToArray());
    }
}

namespace System.Security.Cryptography
{
    public static class NetFxCryptographyExtensions
    {
        public static void AppendData(this IncrementalHash hash, ReadOnlySpan<byte> data) => hash.AppendData(data.ToArray());
    }
}
#endif
