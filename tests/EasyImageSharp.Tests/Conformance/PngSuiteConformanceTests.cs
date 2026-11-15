using Xunit;
using Xunit.Abstractions;

namespace EasyImageSharp.Tests.Conformance;

/// <summary>
/// PngSuite (Willem van Schaik, 2017jul19): every file of the suite. Valid files must decode to the reference that a PNG
/// reader written from the specification produced (cross-checked against Pillow), narrowed to RGBA8 by the library's
/// documented rules; the fourteen invalid-by-design <c>x*.png</c> files must be rejected through the exception contract.
/// </summary>
public class PngSuiteConformanceTests
{
    private const string Suite = "pngsuite";

    /// <summary>Files the library currently gets wrong, keyed by name, valued by category (one skipped theory each).</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownFailures = new Dictionary<string, string>();

    private readonly ITestOutputHelper output;

    public PngSuiteConformanceTests(ITestOutputHelper output) => this.output = output;

    public static IEnumerable<object[]> Files => ConformanceCorpus.Rows(Suite, KnownFailures);

    [Theory]
    [MemberData(nameof(Files))]
    public void File_MatchesReferenceOrIsRejected(string name) => ConformanceCorpus.Verify(Suite, name, this.output);

    [Fact]
    public void Suite_IsCompleteAndCoversTheCorruptSet()
    {
        ConformanceEntry[] entries = ConformanceCorpus.Load(Suite);
        Assert.Equal(176, entries.Length);
        Assert.Equal(14, entries.Count(e => e.Name.StartsWith('x') && e.Expect is not null));
        Assert.All(entries.Where(e => !e.Name.StartsWith('x')), e => Assert.NotNull(e.Reference));
    }
}
