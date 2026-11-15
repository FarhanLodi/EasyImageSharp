using System.Security.Cryptography;
using System.Text;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// The canonical string, MAC and encoding behind signed URLs, shared by <see cref="ImageUrlSigner"/> and the
/// middleware so that both sides can never disagree about what was signed.
/// </summary>
/// <remarks>
/// <para>
/// Canonical form: the path with every segment percent-encoded, a <c>?</c>, then every query parameter except
/// <c>sig</c> as <c>name=value</c> joined with <c>&amp;</c>. Names are lowercased (query names are case-insensitive
/// in ASP.NET Core), pairs are sorted ordinally by name and then by value, and names and values are
/// percent-encoded with the RFC 3986 rules (every UTF-8 byte outside <c>A-Z a-z 0-9 - . _ ~</c> becomes an uppercase
/// <c>%XX</c>). Because the separators can never appear inside an encoded part, two different parameter sets can
/// never produce the same canonical string - <c>a=1%26b%3D2</c> and <c>a=1&amp;b=2</c> sign differently.
/// </para>
/// <para>The MAC is HMAC-SHA256 over the UTF-8 bytes of that string, sent as base64url without padding (43 characters).</para>
/// </remarks>
internal static class UrlSignature
{
    /// <summary>The size of an HMAC-SHA256 tag in bytes.</summary>
    public const int TagLength = 32;

    /// <summary>The length of <see cref="TagLength"/> bytes as unpadded base64url.</summary>
    public const int EncodedTagLength = 43;

    public static string Canonicalize(string path, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> parameter in parameters)
        {
            string name = parameter.Key.ToLowerInvariant();
            if (name == ImageCommandNames.Signature)
            {
                continue;
            }

            pairs.Add(new KeyValuePair<string, string>(name, parameter.Value ?? string.Empty));
        }

        pairs.Sort(static (left, right) =>
        {
            int byName = string.CompareOrdinal(left.Key, right.Key);
            return byName != 0 ? byName : string.CompareOrdinal(left.Value, right.Value);
        });

        var builder = new StringBuilder(path.Length + (pairs.Count * 16));
        AppendEscapedPath(builder, path);
        builder.Append('?');
        for (int i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(pairs[i].Key)).Append('=').Append(Uri.EscapeDataString(pairs[i].Value));
        }

        return builder.ToString();
    }

    public static void ComputeTag(ReadOnlySpan<byte> key, string canonical, Span<byte> destination)
    {
        byte[] message = Encoding.UTF8.GetBytes(canonical);
        HMACSHA256.HashData(key, message, destination);
    }

    public static string Encode(ReadOnlySpan<byte> tag)
    {
        string base64 = Convert.ToBase64String(tag);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes an unpadded base64url tag of exactly <see cref="TagLength"/> bytes; anything else is rejected.</summary>
    public static bool TryDecode(string? encoded, Span<byte> destination)
    {
        if (encoded is null || encoded.Length != EncodedTagLength || destination.Length < TagLength)
        {
            return false;
        }

        Span<char> padded = stackalloc char[EncodedTagLength + 1];
        for (int i = 0; i < EncodedTagLength; i++)
        {
            char c = encoded[i];
            padded[i] = c switch
            {
                '-' => '+',
                '_' => '/',
                (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') => c,
                _ => '\0',
            };

            if (padded[i] == '\0')
            {
                return false;
            }
        }

        padded[EncodedTagLength] = '=';
        return Convert.TryFromBase64Chars(padded, destination, out int written) && written == TagLength;
    }

    /// <summary>Verifies <paramref name="tag"/> against every key in constant time per key.</summary>
    public static bool Verify(IReadOnlyList<byte[]> keys, string canonical, ReadOnlySpan<byte> tag)
    {
        byte[] message = Encoding.UTF8.GetBytes(canonical);
        Span<byte> expected = stackalloc byte[TagLength];
        bool valid = false;

        // Every key is tried even after a match so the time taken does not reveal which key signed the URL.
        foreach (byte[] key in keys)
        {
            HMACSHA256.HashData(key, message, expected);
            valid |= CryptographicOperations.FixedTimeEquals(expected, tag);
        }

        return valid;
    }

    /// <summary>Appends <paramref name="path"/> with every segment percent-encoded; decoding it yields <paramref name="path"/> again.</summary>
    public static void AppendEscapedPath(StringBuilder builder, string path)
    {
        int start = 0;
        while (true)
        {
            int slash = path.IndexOf('/', start);
            string segment = slash < 0 ? path[start..] : path[start..slash];
            builder.Append(Uri.EscapeDataString(segment));
            if (slash < 0)
            {
                return;
            }

            builder.Append('/');
            start = slash + 1;
        }
    }
}
