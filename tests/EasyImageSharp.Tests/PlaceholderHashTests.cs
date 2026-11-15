using System.Security.Cryptography;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// BlurHash and ThumbHash against vectors produced by the reference implementations (blurhash 2.0.5 and
/// thumbhash 0.1.1 from npm, both MIT) on byte-identical synthetic inputs; see
/// <c>Reference/placeholder_vectors.mjs</c> for the script that printed every expected value below.
/// </summary>
public class PlaceholderHashTests
{
    /// <summary>The integer-only pattern of <c>placeholder_vectors.mjs</c>: gradients, a disc, optional alpha.</summary>
    internal static Image<Rgba32> Pattern(int width, int height, string kind)
    {
        var image = new Image<Rgba32>(width, height);
        int cx = width / 2, cy = height / 3, radius = Math.Min(width, height) / 3;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool inDisc = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) < radius * radius;
                byte r = (byte)((x * 255) / Math.Max(1, width - 1));
                byte g = inDisc ? (byte)230 : (byte)((y * 200) / Math.Max(1, height - 1));
                byte b = inDisc ? (byte)30 : (byte)(((x * 7) + (y * 13)) % 256);
                byte a = kind == "alpha" ? (inDisc ? (byte)255 : (byte)(((x * 9) + (y * 5)) % 256)) : (byte)255;
                image[x, y] = new Rgba32(r, g, b, a);
            }
        }

        if (kind == "mirror")
        {
            image.Mutate(c => c.Rotate(RotateMode.Rotate180));
        }

        return image;
    }

    // ----- BlurHash -----

    [Theory]
    [InlineData(32, 24, 4, 3, "L.Hf62}Gzhw5pHn$kok8etfCgKf%")]
    [InlineData(20, 30, 9, 9, "|=HoH$}G+RwOwywhs,t6sopHn,kSk9jtkBjbj[juete]gHf*flf#e?f8f9paohi%jJjcjIk9j?j@f,fNfhfifOfkf6fQfQn.n%kCjbk9kBjbj[jbf+flflfie?f8fif8fioeo3j=j=jbjrjsjtjugKfle:f9fjfkflfjf8")]
    [InlineData(17, 13, 1, 1, "00Hx=:")]
    [InlineData(64, 40, 5, 4, "V+HVL=|^zgwNtdpGjYk%k7esfAf8gGfhe?pCj;i~jWfS")]
    [InlineData(7, 50, 3, 7, "u~IDwW}G;Rl6kBj?f{fjfiklj=jJfdfOfijwjwkBg5fle?")]
    public void BlurHashEncodeMatchesReference(int width, int height, int componentsX, int componentsY, string expected)
    {
        using Image<Rgba32> image = Pattern(width, height, "mirror");
        Assert.Equal(expected, BlurHash.Encode(image, componentsX, componentsY));
    }

    [Fact]
    public void BlurHashUsesTheLargestAbsoluteAcTermAsTheSpecificationRequires()
    {
        // The unmirrored pattern's largest-magnitude AC term is negative. The specification (Algorithm.md) and
        // the reference C encoder normalise by the largest absolute value; the TypeScript encoder takes the
        // largest signed value and prints "LNHf622+.,T0vgR%h-adecfagrfh" here. The expected maximum digit is
        // evaluated directly from the specification's formula below.
        using Image<Rgba32> image = Pattern(32, 24, "opaque");
        string hash = BlurHash.Encode(image, 4, 3);
        Assert.Equal(28, hash.Length);
        Assert.Equal("Hf62", hash.Substring(2, 4)); // the DC term does not depend on the normalisation

        double maxAbs = 0, maxSigned = double.MinValue;
        for (int j = 0; j < 3; j++)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i == 0 && j == 0)
                {
                    continue;
                }

                double[] sum = new double[3];
                for (int y = 0; y < 24; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        double basis = 2 * Math.Cos(Math.PI * i * x / 32) * Math.Cos(Math.PI * j * y / 24);
                        Rgba32 p = image[x, y];
                        sum[0] += basis * SrgbToLinear(p.R);
                        sum[1] += basis * SrgbToLinear(p.G);
                        sum[2] += basis * SrgbToLinear(p.B);
                    }
                }

                foreach (double s in sum)
                {
                    maxAbs = Math.Max(maxAbs, Math.Abs(s / (32 * 24)));
                    maxSigned = Math.Max(maxSigned, s / (32 * 24));
                }
            }
        }

        const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
        int Quantise(double m) => (int)Math.Max(0, Math.Min(82, Math.Floor((m * 166) - 0.5)));
        Assert.NotEqual(Quantise(maxSigned), Quantise(maxAbs)); // the case really distinguishes the two rules
        Assert.Equal(Alphabet[Quantise(maxAbs)], hash[1]);
        Assert.Equal('N', "LNHf622+.,T0vgR%h-adecfagrfh"[1]);
        Assert.Equal('N', Alphabet[Quantise(maxSigned)]);

        static double SrgbToLinear(byte value)
        {
            double v = value / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }

    [Theory]
    [InlineData("LlMF%n00%#MwS|WCWEM{R*bbWBbH", 12, 9, "d500c7031f5542787fe3eb4d083aa09ed18485913efa88c8287b4eeafcd59140")]
    [InlineData("L.Hf62}Gzhw5pHn$kok8etfCgKf%", 31, 17, "50bcd1f78040a94f0066cd7fa0b2cafe286033a5aa2dd2bc004986a828ed1478")]
    public void BlurHashDecodeMatchesReferencePixels(string hash, int width, int height, string expectedSha256)
    {
        using Image<Rgba32> decoded = BlurHash.Decode(hash, width, height);
        Assert.Equal(expectedSha256, Sha256(decoded));
    }

    [Fact]
    public void BlurHashDecodeIsIndependentOfParallelism()
    {
        using Image<Rgba32> parallel = BlurHash.Decode("L.Hf62}Gzhw5pHn$kok8etfCgKf%", 300, 200);
        using var execution = OperationExecution.Enter(new ImageExecutionOptions { Configuration = new Configuration { MaxDegreeOfParallelism = 1 } });
        using Image<Rgba32> serial = BlurHash.Decode("L.Hf62}Gzhw5pHn$kok8etfCgKf%", 300, 200);
        Assert.Equal(Sha256(serial), Sha256(parallel));
    }

    [Theory]
    [InlineData("")]
    [InlineData("L.Hf6")]
    [InlineData("L.Hf62}Gzhw5pHn$kok8etfCgKf")] // one character short
    [InlineData("L.Hf62}Gzhw5pHn$kok8etfCgKf%%")] // one character long
    [InlineData("L.Hf62}Gzhw5pHn$kok8etfCgK\"%")] // not a base-83 character
    [InlineData("L.Hf62}Gzhw5pHn$kok8etfCgKé%")]
    [InlineData("L.~~~~}Gzhw5pHn$kok8etfCgKf%")] // DC beyond 24 bits
    [InlineData("L.Hf62~~zhw5pHn$kok8etfCgKf%")] // AC beyond 19^3
    [InlineData("~.Hf62")] // size flag beyond 9 x 9
    public void BlurHashRejectsMalformedHashes(string hash)
    {
        Assert.False(BlurHash.IsValid(hash));
        Assert.Throws<FormatException>(() => BlurHash.Decode(hash, 8, 8));
    }

    [Fact]
    public void BlurHashValidatesArguments()
    {
        using Image<Rgba32> image = Pattern(8, 8, "opaque");
        Assert.Throws<ArgumentOutOfRangeException>(() => BlurHash.Encode(image, 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlurHash.Encode(image, 4, 10));
        Assert.Throws<ArgumentNullException>(() => BlurHash.Encode<Rgba32>(null!));
        Assert.Throws<ArgumentNullException>(() => BlurHash.Decode(null!, 8, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlurHash.Decode("00Hx=:", 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlurHash.Decode("00Hx=:", 8, 8, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlurHash.Decode("00Hx=:", 8, 8, 0f));
        Assert.False(BlurHash.IsValid(null));
        Assert.True(BlurHash.IsValid("LlMF%n00%#MwS|WCWEM{R*bbWBbH"));
        Assert.Equal((4, 3), BlurHash.GetComponents("LlMF%n00%#MwS|WCWEM{R*bbWBbH"));
    }

    [Fact]
    public void BlurHashReadsWidePixelFormatsThroughTheirEightBitValues()
    {
        using Image<Rgba32> image = Pattern(32, 24, "mirror");
        using Image<Rgba64> wide = image.CloneAs<Rgba64>();
        Assert.Equal(BlurHash.Encode(image), BlurHash.Encode(wide));
    }

    // ----- ThumbHash -----

    [Theory]
    [InlineData(32, 24, "opaque", "1ee90d4d9a6087878376875777789778037805bc98", 32, 23, "bd01fbd96437d9edca44aee467c9dfd551d5dce947e51b5da76ddc05c33a25d2")]
    [InlineData(24, 40, "alpha", "1fc9851b1019707598695700cd00bab719640b94543e48", 19, 32, "f084b95d7dd4ea8c6e4f86af09b26fc3707483d633ebf5ce7842f3c87a58670f")]
    [InlineData(100, 75, "opaque", "1ee9094d9a6087877386787887688787028805ac98", 32, 23, "a001301c3c1bbc38523c866b336eaf8023eb8435c2ea9036c69a91bdc598bbfa")]
    [InlineData(1, 1, "opaque", "000802070000000000000000000000000000000000000000", 32, 32, "af8f31ba22abd706803416381ff240f982f0428cb1a309f698b0473f2ec61340")]
    [InlineData(60, 60, "alpha", "5faa85350e1a70788587887878027a058da707668c94636887", 32, 32, "1c423ebead57b73b4228b54e016ddb5283f2636fdf5805d16b294b8b39d55610")]
    public void ThumbHashMatchesReference(int width, int height, string kind, string expectedHex, int decodedWidth, int decodedHeight, string decodedSha256)
    {
        using Image<Rgba32> image = Pattern(width, height, kind);
        byte[] hash = ThumbHash.Encode(image);
        Assert.Equal(expectedHex, Convert.ToHexString(hash).ToLowerInvariant());

        using Image<Rgba32> decoded = ThumbHash.Decode(hash);
        Assert.Equal(decodedWidth, decoded.Width);
        Assert.Equal(decodedHeight, decoded.Height);
        Assert.Equal(decodedSha256, Sha256(decoded));
    }

    [Fact]
    public void ThumbHashReportsAverageColourAndAspectRatio()
    {
        byte[] opaque = Convert.FromHexString("1ee90d4d9a6087878376875777789778037805bc98");
        Assert.Equal(7f / 5f, ThumbHash.GetApproximateAspectRatio(opaque), 5);
        Assert.Equal(255, ThumbHash.GetAverageColor(opaque).A);

        byte[] withAlpha = Convert.FromHexString("1fc9851b1019707598695700cd00bab719640b94543e48");
        Assert.True(ThumbHash.GetAverageColor(withAlpha).A < 255);
        Assert.True(ThumbHash.GetApproximateAspectRatio(withAlpha) < 1f);
    }

    [Fact]
    public void ThumbHashDownscalesLargeInputs()
    {
        using Image<Rgba32> image = Pattern(400, 300, "opaque");
        byte[] hash = ThumbHash.Encode(image);
        using Image<Rgba32> small = image.Clone(c => c.Resize(100, 75, KnownResamplers.Triangle));
        Assert.Equal(ThumbHash.Encode(small), hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1ee90d4d")]
    [InlineData("1ee90d4d9a60878783")] // truncated AC data
    [InlineData("1fc9851b10")] // alpha flag without the alpha byte
    [InlineData("1ee9090800")] // zero stored luminance width
    public void ThumbHashRejectsMalformedHashes(string hex)
    {
        byte[] hash = Convert.FromHexString(hex);
        Assert.Throws<FormatException>(() => ThumbHash.Decode(hash));
    }

    private static string Sha256(Image<Rgba32> image)
    {
        byte[] bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
