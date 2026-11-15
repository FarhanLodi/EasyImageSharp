using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests.Conformance;

/// <summary>
/// The WebP vectors of libwebp-test-data (VP8 comprehensive, intra, inter, segmentation, partition and sharpness
/// vectors, every VP8L transform combination, alpha filtering and compression), against Pillow's libwebp decode,
/// itself cross-checked against the dwebp MD5 sums the suite publishes.
/// </summary>
/// <remarks>
/// The vectors are <b>not committed</b>: libwebp-test-data has no licence file for its images, so the set is built
/// locally by <c>python -I build_conformance.py --libwebp</c> into the git-ignored <c>Fixtures/conformance/libwebp/</c>.
/// Without it, the theory has a single row saying so and asserts nothing; see <c>Fixtures/conformance/README.md</c>.
/// </remarks>
public class LibwebpConformanceTests
{
    private const string Suite = "libwebp";

    private const string NotBuilt = "(libwebp-test-data not built locally; see Fixtures/conformance/README.md)";

    /// <summary>Files the library currently gets wrong, keyed by name, valued by category (one skipped theory each).</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownFailures = new Dictionary<string, string>();

    private readonly ITestOutputHelper output;

    public LibwebpConformanceTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Files
        => ConformanceCorpus.IsPresent(Suite) ? ConformanceCorpus.Rows(Suite, KnownFailures) : new[] { new object[] { NotBuilt } };

    [Theory]
    [MemberData(nameof(Files))]
    public void Vector_MatchesReference(string name)
    {
        if (name == NotBuilt)
        {
            this.output.WriteLine("Fixtures/conformance/libwebp/ is absent, so nothing was checked. Build it with: python -I build_conformance.py --libwebp");
            return;
        }

        ConformanceCorpus.Verify(Suite, name, this.output);
    }
}
