using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests.Conformance;

/// <summary>
/// The synthetic images from libtiff 4.7.2's <c>test/images</c>: photometric/bit-depth combinations, LZW in the old
/// "compat" bit order, tiled JPEG-in-TIFF, CCITT Group 3 and 4 pages, multi-page and SubIFD chains, and IFD chains that
/// loop. Files with a reference (Pillow/libtiff, or tifffile for 16-bit samples) must match it; the rest — the
/// looping chains and the encodings no independent reader here decodes — must honour the decoder contract.
/// </summary>
public class LibtiffConformanceTests
{
    private const string Suite = "libtiff";

    /// <summary>Files the library currently gets wrong, keyed by name, valued by category (one skipped theory each).</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownFailures = new Dictionary<string, string>();

    private readonly ITestOutputHelper output;

    public LibtiffConformanceTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Files => ConformanceCorpus.Rows(Suite, KnownFailures);

    [Theory]
    [MemberData(nameof(Files))]
    public void File_MatchesReferenceOrHonoursContract(string name) => ConformanceCorpus.Verify(Suite, name, this.output);

    [Fact]
    public void Suite_IsPresent() => Assert.True(ConformanceCorpus.Load(Suite).Length >= 20, "Fixtures/conformance/libtiff is incomplete; run build_conformance.py.");
}
