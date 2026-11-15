using System.Globalization;
using System.Text;
using EasyImageSharp.Web.Internal;

namespace EasyImageSharp.Web;

/// <summary>
/// Creates signed image URLs that the middleware accepts when <see cref="EasyImageSharpWebOptions.SigningKeys"/> is
/// set. Resolve it from the container (it uses the first configured key) or construct one with a key directly.
/// </summary>
/// <remarks>
/// <para>
/// The signature is HMAC-SHA256 over a canonical string, sent as the <c>sig</c> query parameter in unpadded
/// base64url. The canonical string is the path with each segment percent-encoded, a <c>?</c>, then every other query
/// parameter as <c>name=value</c> joined with <c>&amp;</c>: names lowercased, pairs sorted ordinally by name and then
/// value, and names and values percent-encoded per RFC 3986 (every UTF-8 byte outside <c>A-Z a-z 0-9 - . _ ~</c> as
/// uppercase <c>%XX</c>). For example <c>/photos/cat.jpg?width=300&amp;format=webp</c> signs
/// <c>/photos/cat.jpg?format=webp&amp;width=300</c>. Any language can reproduce it.
/// </para>
/// <para>
/// The path is the full path the browser requests, including any <c>PathBase</c> the application is mounted under.
/// Every query parameter except <c>sig</c> is signed, including ones the middleware ignores, so a signed URL cannot
/// be extended. Instances are immutable and thread-safe.
/// </para>
/// </remarks>
public sealed class ImageUrlSigner
{
    /// <summary>The minimum key length in bytes: 32, the output size of SHA-256.</summary>
    public const int MinimumKeyLength = 32;

    private readonly byte[] key;

    /// <summary>Creates a signer for <paramref name="key"/>; the key is copied.</summary>
    /// <param name="key">The HMAC key, at least <see cref="MinimumKeyLength"/> bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is shorter than <see cref="MinimumKeyLength"/> bytes.</exception>
    public ImageUrlSigner(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinimumKeyLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The signing key must be at least {MinimumKeyLength} bytes long."),
                nameof(key));
        }

        this.key = (byte[])key.Clone();
    }

    /// <summary>Creates a signer from a base64-encoded key, the form used by <see cref="EasyImageSharpWebOptions.SigningKeys"/>.</summary>
    /// <param name="base64Key">The key as standard base64.</param>
    /// <returns>The signer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="base64Key"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="base64Key"/> is not valid base64.</exception>
    /// <exception cref="ArgumentException">The decoded key is shorter than <see cref="MinimumKeyLength"/> bytes.</exception>
    public static ImageUrlSigner FromBase64(string base64Key)
    {
        ArgumentNullException.ThrowIfNull(base64Key);
        return new ImageUrlSigner(Convert.FromBase64String(base64Key));
    }

    /// <summary>Computes the <c>sig</c> value for a path and its query parameters.</summary>
    /// <param name="path">The request path, starting with <c>/</c>, unescaped (as <c>HttpRequest.Path</c> reports it).</param>
    /// <param name="parameters">
    /// Every query parameter the URL will carry, unescaped, including <c>expires</c>; a <c>sig</c> entry is ignored.
    /// </param>
    /// <returns>The signature as unpadded base64url.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not start with <c>/</c>.</exception>
    public string ComputeSignature(string path, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ValidatePath(path);
        ArgumentNullException.ThrowIfNull(parameters);
        Span<byte> tag = stackalloc byte[UrlSignature.TagLength];
        UrlSignature.ComputeTag(this.key, UrlSignature.Canonicalize(path, parameters), tag);
        return UrlSignature.Encode(tag);
    }

    /// <summary>
    /// Builds a signed relative URL: the escaped path, the commands in the order given, an optional
    /// <c>expires</c>, and <c>sig</c>.
    /// </summary>
    /// <param name="path">The request path, starting with <c>/</c>, unescaped.</param>
    /// <param name="commands">The query parameters, unescaped, for example <c>("width", "300")</c>.</param>
    /// <param name="expiresAt">When the URL stops working, or <see langword="null"/> for a URL that never expires.</param>
    /// <returns>The URL, for example <c>/photos/cat.jpg?width=300&amp;expires=1767225600&amp;sig=…</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> does not start with <c>/</c>, or <paramref name="commands"/> already contains
    /// <c>sig</c> or <c>expires</c>.
    /// </exception>
    public string CreateSignedUrl(string path, IEnumerable<KeyValuePair<string, string>> commands, DateTimeOffset? expiresAt = null)
    {
        ValidatePath(path);
        ArgumentNullException.ThrowIfNull(commands);
        var parameters = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> command in commands)
        {
            ArgumentNullException.ThrowIfNull(command.Key, nameof(commands));
            if (string.Equals(command.Key, ImageCommandNames.Signature, StringComparison.OrdinalIgnoreCase)
                || string.Equals(command.Key, ImageCommandNames.Expires, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Pass the expiry as expiresAt; sig and expires are added by the signer.", nameof(commands));
            }

            parameters.Add(new KeyValuePair<string, string>(command.Key, command.Value ?? string.Empty));
        }

        if (expiresAt is DateTimeOffset expiry)
        {
            parameters.Add(new KeyValuePair<string, string>(
                ImageCommandNames.Expires,
                expiry.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        string signature = this.ComputeSignature(path, parameters);

        var url = new StringBuilder();
        UrlSignature.AppendEscapedPath(url, path);
        char separator = '?';
        foreach (KeyValuePair<string, string> parameter in parameters)
        {
            url.Append(separator).Append(Uri.EscapeDataString(parameter.Key)).Append('=').Append(Uri.EscapeDataString(parameter.Value));
            separator = '&';
        }

        url.Append(separator).Append(ImageCommandNames.Signature).Append('=').Append(signature);
        return url.ToString();
    }

    private static void ValidatePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path[0] != '/')
        {
            throw new ArgumentException("The path must start with '/'.", nameof(path));
        }
    }
}
