using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace EasyImageSharp.Web.Internal;

/// <summary>
/// Validates <see cref="EasyImageSharpWebOptions"/> without reflection. Registered with <c>ValidateOnStart</c>, so a
/// bad key, an unknown format name or a malformed size fails host start-up instead of the first request.
/// </summary>
internal sealed class EasyImageSharpWebOptionsValidator : IValidateOptions<EasyImageSharpWebOptions>
{
    private readonly IImageOutputFormat[] formats;

    public EasyImageSharpWebOptionsValidator(IEnumerable<IImageOutputFormat> formats) => this.formats = formats.ToArray();

    public ValidateOptionsResult Validate(string? name, EasyImageSharpWebOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrEmpty(options.RequestPathPrefix) || options.RequestPathPrefix[0] != '/')
        {
            failures.Add($"{nameof(EasyImageSharpWebOptions.RequestPathPrefix)} must start with '/'.");
        }
        else
        {
            try
            {
                _ = new PathString(options.RequestPathPrefix);
            }
            catch (ArgumentException)
            {
                failures.Add($"{nameof(EasyImageSharpWebOptions.RequestPathPrefix)} is not a valid path.");
            }
        }

        RequireRange(failures, nameof(EasyImageSharpWebOptions.MaxWidth), options.MaxWidth, 1, 65_535);
        RequireRange(failures, nameof(EasyImageSharpWebOptions.MaxHeight), options.MaxHeight, 1, 65_535);
        RequireRange(failures, nameof(EasyImageSharpWebOptions.MaxOutputPixels), options.MaxOutputPixels, 1, int.MaxValue);
        RequireRange(failures, nameof(EasyImageSharpWebOptions.MaxSourceBytes), options.MaxSourceBytes, 1, Array.MaxLength);
        RequireRange(failures, nameof(EasyImageSharpWebOptions.MaxSourcePixels), options.MaxSourcePixels, 1, long.MaxValue);
        RequireRange(failures, nameof(EasyImageSharpWebOptions.MaxConcurrentProcessing), options.MaxConcurrentProcessing, 1, int.MaxValue);
        RequireRange(failures, nameof(EasyImageSharpWebOptions.MaxQueueLength), options.MaxQueueLength, 0, int.MaxValue);
        if (options.DefaultQuality is int quality)
        {
            RequireRange(failures, nameof(EasyImageSharpWebOptions.DefaultQuality), quality, 1, 100);
        }

        if (options.BrowserCacheMaxAge < TimeSpan.Zero)
        {
            failures.Add($"{nameof(EasyImageSharpWebOptions.BrowserCacheMaxAge)} must not be negative.");
        }

        if (options.CacheMaxAge <= TimeSpan.Zero)
        {
            failures.Add($"{nameof(EasyImageSharpWebOptions.CacheMaxAge)} must be positive.");
        }

        // The remaining checks (keys, formats, sizes, colour) are exactly what building the runtime settings parses,
        // so the validator builds them and reports what fails.
        try
        {
            _ = WebSettings.Create(options, this.formats);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            failures.Add(ex.Message);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireRange(List<string> failures, string name, long value, long minimum, long maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {minimum} and {maximum} (was {value})."));
        }
    }
}
