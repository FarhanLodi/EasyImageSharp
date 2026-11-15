using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// <see cref="EasyImageSharpWebOptions"/> parsed once per options instance: decoded keys, resolved formats, the size
/// allowlist and the fingerprint of every option that changes output bytes.
/// </summary>
internal sealed class WebSettings
{
    private readonly Dictionary<string, IImageOutputFormat> formatsByName;
    private readonly HashSet<string> allowed;
    private readonly HashSet<(int Width, int Height)>? allowedSizes;

    private WebSettings(
        EasyImageSharpWebOptions options,
        Dictionary<string, IImageOutputFormat> formatsByName,
        HashSet<string> allowed,
        IReadOnlyList<IImageOutputFormat> negotiated,
        HashSet<(int Width, int Height)>? allowedSizes,
        IReadOnlyList<byte[]> signingKeys,
        Color defaultBackground)
    {
        this.Options = options;
        this.formatsByName = formatsByName;
        this.allowed = allowed;
        this.Negotiated = negotiated;
        this.allowedSizes = allowedSizes;
        this.SigningKeys = signingKeys;
        this.DefaultBackground = defaultBackground;
        this.RequestPathPrefix = options.RequestPathPrefix == "/" ? PathString.Empty : new PathString(options.RequestPathPrefix.TrimEnd('/'));

        // The allowed and negotiated formats decide fallbacks for "auto" requests, and the implementation behind a name
        // decides the bytes, so both are part of the fingerprint along with the scalar output options.
        string formats = string.Join(
            ",",
            allowed.Order(StringComparer.Ordinal).Select(name => name + ":" + formatsByName[name].GetType().FullName));
        this.Fingerprint = string.Create(
            CultureInfo.InvariantCulture,
            $"strip={options.StripMetadata};orient={options.AutoOrient};up={options.AllowUpscaling};q={options.DefaultQuality};"
            + $"bg={defaultBackground.ToHex()};allowed={formats};negotiated={string.Join(",", negotiated.Select(f => f.Name))}");
    }

    public EasyImageSharpWebOptions Options { get; }

    /// <summary>The prefix requests must start with; <see cref="PathString.Empty"/> for <c>/</c>.</summary>
    public PathString RequestPathPrefix { get; }

    /// <summary>The decoded signing keys; empty when signing is disabled.</summary>
    public IReadOnlyList<byte[]> SigningKeys { get; }

    /// <summary>The allowed formats format negotiation may choose, in preference order.</summary>
    public IReadOnlyList<IImageOutputFormat> Negotiated { get; }

    public Color DefaultBackground { get; }

    /// <summary>Every option that changes the encoded output, as part of the cache key.</summary>
    public string Fingerprint { get; }

    public bool SigningEnabled => this.SigningKeys.Count > 0;

    /// <summary>Builds the settings; throws <see cref="FormatException"/> or <see cref="ArgumentException"/> on invalid values.</summary>
    public static WebSettings Create(EasyImageSharpWebOptions options, IEnumerable<IImageOutputFormat> registeredFormats)
    {
        Dictionary<string, IImageOutputFormat> byName = BuildFormatTable(registeredFormats);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in options.AllowedFormats.Count == 0 ? BuiltInOutputFormats.DefaultAllowed : (IEnumerable<string>)options.AllowedFormats)
        {
            allowed.Add(ResolveConfiguredFormat(byName, name, nameof(EasyImageSharpWebOptions.AllowedFormats)).Name);
        }

        var negotiated = new List<IImageOutputFormat>();
        foreach (string name in options.NegotiatedFormats.Count == 0 ? BuiltInOutputFormats.DefaultNegotiated : (IEnumerable<string>)options.NegotiatedFormats)
        {
            IImageOutputFormat format = ResolveConfiguredFormat(byName, name, nameof(EasyImageSharpWebOptions.NegotiatedFormats));
            if (allowed.Contains(format.Name) && !negotiated.Contains(format))
            {
                negotiated.Add(format);
            }
        }

        HashSet<(int Width, int Height)>? sizes = null;
        if (options.AllowedSizes.Count > 0)
        {
            sizes = new HashSet<(int Width, int Height)>();
            foreach (string entry in options.AllowedSizes)
            {
                sizes.Add(ParseSize(entry));
            }
        }

        var keys = new List<byte[]>();
        foreach (string key in options.SigningKeys)
        {
            // The messages never include the key material.
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(key ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new FormatException("Every SigningKeys entry must be standard base64.");
            }

            if (decoded.Length < ImageUrlSigner.MinimumKeyLength)
            {
                throw new ArgumentException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Every SigningKeys entry must decode to at least {ImageUrlSigner.MinimumKeyLength} bytes."));
            }

            keys.Add(decoded);
        }

        if (!Color.TryParse(options.DefaultBackgroundColor, out Color background))
        {
            throw new FormatException("DefaultBackgroundColor is not a CSS colour name or hex colour.");
        }

        return new WebSettings(options, byName, allowed, negotiated, sizes, keys, background);
    }

    /// <summary>Resolves a <c>format</c> command value (a name or alias, case-insensitive).</summary>
    public bool TryGetFormat(string name, out IImageOutputFormat format)
        => this.formatsByName.TryGetValue(name.ToLowerInvariant(), out format!);

    public bool IsAllowed(IImageOutputFormat format) => this.allowed.Contains(format.Name);

    /// <summary>The allowed format with this canonical name, or <see langword="null"/>.</summary>
    public IImageOutputFormat? GetAllowed(string canonicalName)
        => this.allowed.Contains(canonicalName) && this.formatsByName.TryGetValue(canonicalName, out IImageOutputFormat? format) ? format : null;

    /// <summary>The first allowed format in a stable order, the last-resort output format.</summary>
    public IImageOutputFormat FirstAllowed()
    {
        foreach (IImageOutputFormat format in BuiltInOutputFormats.All)
        {
            if (this.GetAllowed(format.Name) is IImageOutputFormat allowedFormat)
            {
                return allowedFormat;
            }
        }

        string first = this.allowed.Order(StringComparer.Ordinal).First();
        return this.formatsByName[first];
    }

    public bool IsSizeAllowed(int? width, int? height)
        => this.allowedSizes is null || this.allowedSizes.Contains((width ?? 0, height ?? 0));

    /// <summary>Parses an <see cref="EasyImageSharpWebOptions.AllowedSizes"/> entry: <c>"320x240"</c>, <c>"320x"</c>, <c>"x240"</c>, zeros for a missing side.</summary>
    internal static (int Width, int Height) ParseSize(string? entry)
    {
        string value = entry?.Trim() ?? string.Empty;
        int separator = value.IndexOfAny(['x', 'X']);
        if (separator < 0)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"AllowedSizes entry '{entry}' is not WIDTHxHEIGHT."));
        }

        int width = ParseSide(value[..separator], entry);
        int height = ParseSide(value[(separator + 1)..], entry);
        if (width == 0 && height == 0)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"AllowedSizes entry '{entry}' specifies neither side."));
        }

        return (width, height);
    }

    private static int ParseSide(string side, string? entry)
    {
        if (side.Length == 0)
        {
            return 0;
        }

        if (!int.TryParse(side, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"AllowedSizes entry '{entry}' is not WIDTHxHEIGHT."));
        }

        return value;
    }

    private static Dictionary<string, IImageOutputFormat> BuildFormatTable(IEnumerable<IImageOutputFormat> registeredFormats)
    {
        var byName = new Dictionary<string, IImageOutputFormat>(StringComparer.Ordinal);
        foreach (IImageOutputFormat format in BuiltInOutputFormats.All)
        {
            Register(byName, format);
        }

        // Registered formats come last so they replace a built-in format (and its aliases) of the same name.
        foreach (IImageOutputFormat format in registeredFormats)
        {
            ValidateName(format.Name);
            if (byName.TryGetValue(format.Name, out IImageOutputFormat? replaced))
            {
                foreach (string alias in replaced.Aliases)
                {
                    byName.Remove(alias);
                }
            }

            Register(byName, format);
        }

        return byName;
    }

    private static void Register(Dictionary<string, IImageOutputFormat> byName, IImageOutputFormat format)
    {
        byName[format.Name] = format;
        foreach (string alias in format.Aliases)
        {
            ValidateName(alias);
            byName[alias] = format;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "auto" || !name.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
        {
            throw new ArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"Output format name '{name}' must be lowercase ASCII letters and digits and must not be 'auto'."));
        }
    }

    private static IImageOutputFormat ResolveConfiguredFormat(Dictionary<string, IImageOutputFormat> byName, string? name, string property)
    {
        string key = name?.Trim().ToLowerInvariant() ?? string.Empty;
        return byName.TryGetValue(key, out IImageOutputFormat? format)
            ? format
            : throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{property} names an unknown output format '{name}'."));
    }
}
