using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests.Conformance;

/// <summary>
/// libjpeg-turbo 3.2.1's <c>testimages</c>: the IJG reference JPEGs (baseline, progressive, arithmetic-coded), a
/// 12-bit JPEG, and the BMP, PNG and Netpbm companions the suite ships. JPEG is compared with Pillow's libjpeg-turbo
/// decode by PSNR, as <see cref="EasyImageSharp.Tests.JpegDecoderTests"/> does; the lossless formats exactly.
/// </summary>
public class LibjpegTurboConformanceTests
{
    private const string Suite = "libjpeg-turbo";

    /// <summary>Files the library currently gets wrong, keyed by name, valued by category (one skipped theory each).</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownFailures = new Dictionary<string, string>();

    private readonly ITestOutputHelper output;

    public LibjpegTurboConformanceTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Files => ConformanceCorpus.Rows(Suite, KnownFailures);

    [Theory]
    [MemberData(nameof(Files))]
    public void File_MatchesReferenceOrHonoursContract(string name) => ConformanceCorpus.Verify(Suite, name, this.output);

    [Fact]
    public void Suite_IsPresent() => Assert.True(ConformanceCorpus.Load(Suite).Length >= 12, "Fixtures/conformance/libjpeg-turbo is incomplete; run build_conformance.py.");
}
