using System.Linq;
using System.Text;

namespace DiscordRPC.Helper;

/// <summary>
/// Collection of helpful string extensions
/// </summary>
public static class StringTools
{
    /// <summary>
    /// Will return null if the string is whitespace, otherwise it will return the string.
    /// </summary>
    /// <param name="str">The string to check</param>
    /// <returns>Null if the string is empty, otherwise the string</returns>
    public static string? GetNullOrString(this string str) => str.Length == 0 || string.IsNullOrEmpty(str.Trim()) ? null : str;

    /// <summary>
    /// Does the string fit within the given amount of bytes?
    /// </summary>
    /// <param name="str">The string to check</param>
    /// <param name="bytes">The maximum number of bytes the string can take up</param>
    /// <param name="encoding">The encoding to count the bytes with</param>
    /// <returns>True if the string fits within the number of bytes</returns>
    public static bool WithinLength(this string str, int bytes, Encoding? encoding) => (encoding ?? Encoding.UTF8).GetByteCount(str) <= bytes;

    /// <summary>
    /// Converts the string into UPPER_SNAKE_CASE
    /// </summary>
    /// <param name="str">The string to convert</param>
    /// <returns></returns>
    public static string? ToSnakeCase(this string? str)
    {
        if (str == null) return null;
        string concat = string.Concat(str.Select((x, i) => i > 0 && char.IsUpper(x) ? "_" + x : x.ToString()).ToArray());
        return concat.ToUpperInvariant();
    }
}
