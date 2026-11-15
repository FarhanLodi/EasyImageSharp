namespace EasyImageSharp.Web.Internal;

/// <summary>Path checks applied before any source provider sees a request path.</summary>
internal static class RequestPath
{
    /// <summary>
    /// True when <paramref name="path"/> (already URL-decoded by the server) is a plain relative file path: it starts
    /// with <c>/</c>, has no empty, <c>.</c> or <c>..</c> segment, no segment ending in a dot or space (which Windows
    /// silently strips, changing the extension the middleware checked), and no backslash, control character, drive or
    /// alternate-data-stream colon, wildcard, quote, angle bracket, pipe or percent sign (a percent sign surviving
    /// decoding means the client double-encoded the path).
    /// </summary>
    public static bool IsSafe(string path)
    {
        if (path.Length < 2 || path[0] != '/' || path.Length > 1024)
        {
            return false;
        }

        int segmentStart = 1;
        for (int i = 1; i <= path.Length; i++)
        {
            if (i == path.Length || path[i] == '/')
            {
                ReadOnlySpan<char> segment = path.AsSpan(segmentStart, i - segmentStart);
                if (segment.Length == 0
                    || segment is "." or ".."
                    || segment[^1] is '.' or ' ')
                {
                    return false;
                }

                segmentStart = i + 1;
                continue;
            }

            char c = path[i];
            if (char.IsControl(c) || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '%')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The lowercase extension of the last segment without the dot, or an empty string.</summary>
    public static string GetExtension(string path)
    {
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        return dot > slash && dot < path.Length - 1 ? path[(dot + 1)..].ToLowerInvariant() : string.Empty;
    }
}
