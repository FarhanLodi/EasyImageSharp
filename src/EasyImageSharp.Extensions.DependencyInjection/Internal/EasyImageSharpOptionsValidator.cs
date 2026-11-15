using System.Globalization;
using Microsoft.Extensions.Options;

namespace EasyImageSharp.Extensions.DependencyInjection.Internal;

/// <summary>
/// Validates <see cref="EasyImageSharpOptions"/> without reflection, so it runs under trimming and Native AOT.
/// Registered together with <c>ValidateOnStart</c>, it turns a bad configuration value into a start-up failure.
/// </summary>
internal sealed class EasyImageSharpOptionsValidator : IValidateOptions<EasyImageSharpOptions>
{
    public ValidateOptionsResult Validate(string? name, EasyImageSharpOptions options)
    {
        var failures = new List<string>();
        RequirePositive(failures, nameof(EasyImageSharpOptions.MaxPixels), options.MaxPixels);
        RequirePositive(failures, nameof(EasyImageSharpOptions.MaxTotalPixels), options.MaxTotalPixels);
        RequirePositive(failures, nameof(EasyImageSharpOptions.MaxFrames), options.MaxFrames);
        RequirePositive(failures, nameof(EasyImageSharpOptions.MaxEncodedBytes), options.MaxEncodedBytes);
        RequirePositive(failures, nameof(EasyImageSharpOptions.MaxPixelBufferBytes), options.MaxPixelBufferBytes);

        if (options.MaxDegreeOfParallelism is int parallelism && parallelism < 1)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{nameof(EasyImageSharpOptions.MaxDegreeOfParallelism)} must be at least 1 when set (was {parallelism})."));
        }

        if (options.MinimumPixelsPerTask is int pixelsPerTask && pixelsPerTask < 1)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{nameof(EasyImageSharpOptions.MinimumPixelsPerTask)} must be at least 1 when set (was {pixelsPerTask})."));
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(List<string> failures, string name, long value)
    {
        if (value <= 0)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be positive (was {value})."));
        }
    }
}
