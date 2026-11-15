using System.Globalization;
using System.Text;
using EasyImageSharp.Processing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace EasyImageSharp.Web.Internal;

/// <summary>The normalised processing commands of one request.</summary>
internal sealed class ImageCommands
{
    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public ResizeMode Mode { get; private set; } = ResizeMode.Max;

    public AnchorPositionMode Anchor { get; private set; } = AnchorPositionMode.Center;

    /// <summary>The explicitly requested format, or <see langword="null"/> for <c>auto</c>.</summary>
    public IImageOutputFormat? Format { get; private set; }

    public int? Quality { get; private set; }

    public Color? Background { get; private set; }

    public bool AutoOrient { get; private set; }

    public IResampler Sampler { get; private set; } = KnownResamplers.Bicubic;

    public string SamplerName { get; private set; } = "bicubic";

    /// <summary>Clockwise degrees: 0, 90, 180 or 270.</summary>
    public int Rotate { get; private set; }

    public bool FlipHorizontal { get; private set; }

    public bool FlipVertical { get; private set; }

    public bool HasResize => this.Width is not null || this.Height is not null;

    /// <summary>True when the query carries at least one recognised command (not counting <c>sig</c> and <c>expires</c>).</summary>
    public static bool HasAnyCommand(IQueryCollection query)
    {
        foreach (KeyValuePair<string, StringValues> parameter in query)
        {
            if (IsCommand(parameter.Key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the recognised commands; unknown parameters are ignored.</summary>
    /// <returns><see langword="null"/> on success, otherwise the reason for a 400 response.</returns>
    public static string? TryParse(IQueryCollection query, WebSettings settings, out ImageCommands commands)
    {
        commands = new ImageCommands { AutoOrient = settings.Options.AutoOrient };
        foreach (KeyValuePair<string, StringValues> parameter in query)
        {
            string name = parameter.Key.ToLowerInvariant();
            if (!IsCommand(name))
            {
                continue;
            }

            if (parameter.Value.Count != 1)
            {
                return $"The '{name}' command may appear only once.";
            }

            string value = parameter.Value[0] ?? string.Empty;
            if (!commands.Apply(name, value, settings))
            {
                return $"The '{name}' command has an invalid value.";
            }
        }

        return null;
    }

    /// <summary>
    /// The canonical form for the cache key: every field, defaults included, so equivalent spellings
    /// (<c>format=jpg</c> and <c>format=JPEG</c>, an explicit default mode) share one entry. Fields that cannot affect
    /// the output (the resize settings when there is no resize) are written as <c>-</c>.
    /// </summary>
    public string ToCanonicalString()
    {
        var builder = new StringBuilder(96);
        builder.Append(CultureInfo.InvariantCulture, $"w={this.Width};h={this.Height};");
        if (this.HasResize)
        {
            builder.Append(CultureInfo.InvariantCulture, $"m={this.Mode};a={this.Anchor};s={this.SamplerName};");
        }
        else
        {
            builder.Append("m=-;a=-;s=-;");
        }

        builder.Append(CultureInfo.InvariantCulture, $"f={this.Format?.Name ?? "auto"};q={this.Quality};");
        builder.Append(CultureInfo.InvariantCulture, $"bg={this.Background?.ToHex() ?? "-"};o={(this.AutoOrient ? 1 : 0)};");
        builder.Append(CultureInfo.InvariantCulture, $"r={this.Rotate};fh={(this.FlipHorizontal ? 1 : 0)};fv={(this.FlipVertical ? 1 : 0)}");
        return builder.ToString();
    }

    private static bool IsCommand(string name)
        => name.ToLowerInvariant() switch
        {
            ImageCommandNames.Width or ImageCommandNames.Height or ImageCommandNames.Mode or ImageCommandNames.Anchor
                or ImageCommandNames.Format or ImageCommandNames.Quality or ImageCommandNames.BackgroundColor
                or ImageCommandNames.AutoOrient or ImageCommandNames.Sampler or ImageCommandNames.Rotate
                or ImageCommandNames.Flip => true,
            _ => false,
        };

    /// <summary>A positive decimal integer of at most nine digits: no sign, no whitespace, no zero.</summary>
    private static bool TryParseDimension(string value, out int result)
    {
        if (value.Length is > 0 and <= 9
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result > 0)
        {
            return true;
        }

        result = 0;
        return false;
    }

    private bool Apply(string name, string value, WebSettings settings)
    {
        switch (name)
        {
            case ImageCommandNames.Width:
                if (!TryParseDimension(value, out int width))
                {
                    return false;
                }

                this.Width = width;
                return true;

            case ImageCommandNames.Height:
                if (!TryParseDimension(value, out int height))
                {
                    return false;
                }

                this.Height = height;
                return true;

            case ImageCommandNames.Mode:
                ResizeMode? mode = value.ToLowerInvariant() switch
                {
                    "max" => ResizeMode.Max,
                    "crop" => ResizeMode.Crop,
                    "pad" => ResizeMode.Pad,
                    "boxpad" => ResizeMode.BoxPad,
                    "min" => ResizeMode.Min,
                    "stretch" => ResizeMode.Stretch,
                    _ => null,
                };

                this.Mode = mode ?? ResizeMode.Max;
                return mode is not null;

            case ImageCommandNames.Anchor:
                AnchorPositionMode? anchor = value.ToLowerInvariant() switch
                {
                    "center" => AnchorPositionMode.Center,
                    "top" => AnchorPositionMode.Top,
                    "bottom" => AnchorPositionMode.Bottom,
                    "left" => AnchorPositionMode.Left,
                    "right" => AnchorPositionMode.Right,
                    "topleft" => AnchorPositionMode.TopLeft,
                    "topright" => AnchorPositionMode.TopRight,
                    "bottomleft" => AnchorPositionMode.BottomLeft,
                    "bottomright" => AnchorPositionMode.BottomRight,
                    _ => null,
                };

                this.Anchor = anchor ?? AnchorPositionMode.Center;
                return anchor is not null;

            case ImageCommandNames.Format:
                if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    this.Format = null;
                    return true;
                }

                if (!settings.TryGetFormat(value, out IImageOutputFormat format))
                {
                    return false;
                }

                this.Format = format;
                return true;

            case ImageCommandNames.Quality:
                if (value.Length is 0 or > 3
                    || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int quality)
                    || quality is < 1 or > 100)
                {
                    return false;
                }

                this.Quality = quality;
                return true;

            case ImageCommandNames.BackgroundColor:
                if (value.Length > 32 || !Color.TryParse(value, out Color color))
                {
                    return false;
                }

                this.Background = color;
                return true;

            case ImageCommandNames.AutoOrient:
                bool? orient = value.ToLowerInvariant() switch
                {
                    "true" or "1" => true,
                    "false" or "0" => false,
                    _ => null,
                };

                this.AutoOrient = orient ?? this.AutoOrient;
                return orient is not null;

            case ImageCommandNames.Sampler:
                (string Name, IResampler Resampler)? sampler = value.ToLowerInvariant() switch
                {
                    "bicubic" => ("bicubic", KnownResamplers.Bicubic),
                    "lanczos3" => ("lanczos3", KnownResamplers.Lanczos3),
                    "bilinear" => ("bilinear", KnownResamplers.Bilinear),
                    "nearest" => ("nearest", KnownResamplers.NearestNeighbor),
                    "box" => ("box", KnownResamplers.Box),
                    "catmullrom" => ("catmullrom", KnownResamplers.CatmullRom),
                    "mitchell" => ("mitchell", KnownResamplers.MitchellNetravali),
                    _ => null,
                };

                if (sampler is not (string samplerName, IResampler resampler))
                {
                    return false;
                }

                this.SamplerName = samplerName;
                this.Sampler = resampler;
                return true;

            case ImageCommandNames.Rotate:
                int? degrees = value switch
                {
                    "0" => 0,
                    "90" => 90,
                    "180" => 180,
                    "270" => 270,
                    _ => null,
                };

                this.Rotate = degrees ?? 0;
                return degrees is not null;

            case ImageCommandNames.Flip:
                switch (value.ToLowerInvariant())
                {
                    case "h":
                        this.FlipHorizontal = true;
                        return true;
                    case "v":
                        this.FlipVertical = true;
                        return true;
                    case "hv" or "vh":
                        this.FlipHorizontal = true;
                        this.FlipVertical = true;
                        return true;
                    default:
                        return false;
                }

            default:
                return false;
        }
    }
}
