using Xunit;

namespace EasyImageSharp.AI.Tests;

/// <summary>
/// Invariants of the built-in model catalogue: every entry is addressable, every source is HTTPS, every pinned
/// checksum is well formed, and the ready-made contracts validate.
/// </summary>
public class ModelRegistryTests
{
    public static TheoryData<string> AllModels()
    {
        var data = new TheoryData<string>();
        foreach (ModelDescriptor descriptor in ModelRegistry.All)
        {
            data.Add(descriptor.Name);
        }

        return data;
    }

    private static ModelDescriptor ByName(string name) => ModelRegistry.Find(name)!;

    [Fact]
    public void TheCatalogueIsNotEmptyAndHasUniqueNames()
    {
        Assert.NotEmpty(ModelRegistry.All);
        Assert.Equal(
            ModelRegistry.All.Count,
            ModelRegistry.All.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            ModelRegistry.All.Count,
            ModelRegistry.All.Select(d => d.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(AllModels))]
    public void EveryDescriptorIsWellFormed(string name)
    {
        ModelDescriptor descriptor = ByName(name);

        Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.FileName));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.License));
        Assert.EndsWith(".onnx", descriptor.FileName, StringComparison.Ordinal);

        // File names must be single path segments so they cannot escape the cache directory.
        Assert.Equal(descriptor.FileName, Path.GetFileName(descriptor.FileName));
        ModelHub.ValidateFileName(descriptor.FileName);
        if (descriptor.Int8FileName is not null)
        {
            ModelHub.ValidateFileName(descriptor.Int8FileName);
        }

        Assert.StartsWith("https://", descriptor.BaseUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", descriptor.Url, StringComparison.Ordinal);
        Assert.EndsWith(descriptor.FileName, descriptor.Url, StringComparison.Ordinal);
        Assert.NotNull(descriptor.Normalization);
        Assert.NotEmpty(descriptor.InputShape);
    }

    [Theory]
    [MemberData(nameof(AllModels))]
    public void PinnedChecksumsAreUpperCaseHexSha256(string name)
    {
        ModelDescriptor descriptor = ByName(name);
        foreach (string? sha in new[] { descriptor.Sha256, descriptor.Int8Sha256 })
        {
            if (sha is null)
            {
                continue;
            }

            Assert.Equal(64, sha.Length);
            Assert.All(sha, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'A' && c <= 'F'), $"'{c}' is not upper-case hex."));
        }
    }

    [Fact]
    public void PublishedEntriesAreExactlyTheOnesWithAChecksum()
    {
        Assert.Equal(
            ModelRegistry.All.Where(d => d.Sha256 is not null).ToArray(),
            ModelRegistry.Published.ToArray());
        Assert.All(ModelRegistry.Published, d => Assert.True(d.IsPublished));

        // Anything without a pinned checksum cannot be downloaded, so it must also be reachable by an
        // override: the operation names below are what a caller passes to ImageAiOptions.ModelPathOverrides.
        foreach (ModelDescriptor descriptor in ModelRegistry.All.Where(d => !d.IsPublished))
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.FileName));
        }

        // Published today. This list grows as models are exported and pinned; it is asserted so that
        // pinning a checksum without also making the model reachable is caught here.
        Assert.True(ModelRegistry.DocumentOrientation.IsPublished);
        Assert.True(ModelRegistry.DocumentDewarp.IsPublished);
        Assert.True(ModelRegistry.SuperResolutionX4.IsPublished);
        Assert.True(ModelRegistry.DenoiseGray.IsPublished);
        Assert.True(ModelRegistry.Saliency.IsPublished);
    }

    [Fact]
    public void GetChecksumAgreesWithTheDescriptors()
    {
        Assert.Equal(ModelRegistry.DocumentOrientation.Sha256, ModelRegistry.GetChecksum("PP-LCNet_x1_0_doc_ori.onnx"));
        Assert.Equal(ModelRegistry.DocumentDewarp.Sha256, ModelRegistry.GetChecksum("UVDoc.onnx"));
        Assert.Null(ModelRegistry.GetChecksum("not-a-model.onnx"));
    }

    [Fact]
    public void HasPublishedInt8IsFalseUntilBothHalvesExist()
        => Assert.All(ModelRegistry.All, d => Assert.Equal(d.Int8FileName is not null && d.Int8Sha256 is not null, d.HasPublishedInt8));

    [Fact]
    public void FindLooksUpByNameFileNameAndInt8FileName()
    {
        Assert.Same(ModelRegistry.DocumentOrientation, ModelRegistry.Find("doc-orientation"));
        Assert.Same(ModelRegistry.DocumentOrientation, ModelRegistry.Find("DOC-ORIENTATION"));
        Assert.Same(ModelRegistry.DocumentOrientation, ModelRegistry.Find("PP-LCNet_x1_0_doc_ori.onnx"));
        Assert.Same(ModelRegistry.SuperResolutionX4, ModelRegistry.Find("realesrgan_general_x4v3.int8.onnx"));
        Assert.Null(ModelRegistry.Find("nope"));
        Assert.Throws<ArgumentNullException>(() => ModelRegistry.Find(null!));
    }

    [Fact]
    public void Int8UrlIsNullWhenNoVariantExists()
    {
        Assert.Null(ModelRegistry.DocumentOrientation.Int8FileName);
        Assert.Null(ModelRegistry.DocumentOrientation.Int8Url);
        Assert.NotNull(ModelRegistry.SuperResolutionX4.Int8Url);
        Assert.EndsWith(ModelRegistry.SuperResolutionX4.Int8FileName!, ModelRegistry.SuperResolutionX4.Int8Url!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocumentModelsCarryTheirDocumentedIoContract()
    {
        Assert.Equal("x", ModelRegistry.DocumentOrientation.InputName);
        Assert.Equal([1, 3, 224, 224], ModelRegistry.DocumentOrientation.InputShape);
        Assert.Same(TensorNormalization.ImageNet, ModelRegistry.DocumentOrientation.Normalization);
        Assert.Equal(ImageModelTask.DocumentOrientation, ModelRegistry.DocumentOrientation.Task);

        Assert.Equal("image", ModelRegistry.DocumentDewarp.InputName);
        Assert.Same(TensorNormalization.Unit, ModelRegistry.DocumentDewarp.Normalization);
        Assert.Equal(ImageModelTask.Dewarp, ModelRegistry.DocumentDewarp.Task);
    }

    // ----- The ready-made image-to-image models -----

    [Fact]
    public void EveryBuiltInContractValidates()
    {
        foreach (RegistryImageModel model in new[] { ImageModels.SuperResolutionX4, ImageModels.DenoiseGray, ImageModels.DocumentDewarp })
        {
            model.Contract.Validate();
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
            Assert.NotNull(model.Descriptor);
        }
    }

    [Fact]
    public void SuperResolutionIsAFourTimesTiledRgbContract()
    {
        ImageModelContract contract = ImageModels.SuperResolutionX4.Contract;

        Assert.Equal(3, contract.InputChannels);
        Assert.Equal(4, contract.ScaleFactor);
        Assert.Equal(256, contract.TileSize);
        Assert.Equal(16, contract.TileOverlap);
        Assert.Equal(ImageModelOutputKind.Image, contract.OutputKind);
    }

    [Fact]
    public void DenoiseIsAGrayResidualContract()
    {
        ImageModelContract contract = ImageModels.DenoiseGray.Contract;

        Assert.Equal(1, contract.InputChannels);
        Assert.Equal(ImageModelOutputKind.Residual, contract.OutputKind);
        Assert.Equal(1, contract.ScaleFactor);
        Assert.Equal(256, contract.TileSize);
    }

    [Fact]
    public void DewarpIsAFixedSizeContract()
    {
        ImageModelContract contract = ImageModels.DocumentDewarp.Contract;

        Assert.Equal(new Size(488, 712), contract.FixedInputSize);
        Assert.Equal(1, contract.ScaleFactor);
        Assert.Equal(0, contract.TileSize);
    }

    [Fact]
    public async Task RegistryImageModel_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new RegistryImageModel(null!, new ImageModelContract()));
        Assert.Throws<ArgumentNullException>(() => new RegistryImageModel(ModelRegistry.Saliency, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ImageModels.SuperResolutionX4.ResolveModelPathAsync(null!, CancellationToken.None));
    }

    [Fact]
    public void LocalImageModel_RejectsNullArguments()
    {
        Assert.Throws<ArgumentException>(() => new LocalImageModel(" ", new ImageModelContract()));
        Assert.Throws<ArgumentNullException>(() => new LocalImageModel("m.onnx", null!));
    }

    [Fact]
    public void LocalImageModel_DefaultsItsNameToTheFileName()
    {
        var model = new LocalImageModel(TestModels.IdentityGray, new ImageModelContract { InputChannels = 1 });
        Assert.Equal("identity_gray", model.Name);
        Assert.True(Path.IsPathRooted(model.ModelPath));
    }
}
