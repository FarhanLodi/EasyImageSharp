using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Xunit;

namespace EasyImageSharp.Tests;

/// <summary>
/// Grey-level morphology, despeckling, thinning and connected-component labelling. The morphological
/// operators are checked against a naive O(r^2) scan of the structuring element and the labeller against a
/// brute-force flood fill, both written independently in this file.
/// </summary>
public class DocumentMorphologyTests
{
    public static TheoryData<string, int> Elements()
    {
        var data = new TheoryData<string, int>();
        foreach (string shape in new[] { "square", "disk", "cross" })
        {
            foreach (int radius in new[] { 1, 2, 3, 5 })
            {
                data.Add(shape, radius);
            }
        }

        return data;
    }

    private static StructuringElement Build(string shape, int radius) => shape switch
    {
        "square" => StructuringElement.Square(radius),
        "disk" => StructuringElement.Disk(radius),
        "cross" => StructuringElement.Cross(radius),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown element."),
    };

    // ----- Erode / Dilate against the naive reference -----

    [Theory]
    [MemberData(nameof(Elements))]
    public void Erode_MatchesNaiveReference_OnGray(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomGray(37, 29, seed: 11), Build(shape, radius), Op.Erode);

    [Theory]
    [MemberData(nameof(Elements))]
    public void Dilate_MatchesNaiveReference_OnGray(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomGray(37, 29, seed: 12), Build(shape, radius), Op.Dilate);

    [Theory]
    [MemberData(nameof(Elements))]
    public void Erode_MatchesNaiveReference_OnBinary(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomBinary(41, 33, seed: 13), Build(shape, radius), Op.Erode);

    [Theory]
    [MemberData(nameof(Elements))]
    public void Dilate_MatchesNaiveReference_OnBinary(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomBinary(41, 33, seed: 14), Build(shape, radius), Op.Dilate);

    [Theory]
    [MemberData(nameof(Elements))]
    public void Open_MatchesNaiveReference(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomGray(35, 31, seed: 15), Build(shape, radius), Op.Open);

    [Theory]
    [MemberData(nameof(Elements))]
    public void Close_MatchesNaiveReference(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomGray(35, 31, seed: 16), Build(shape, radius), Op.Close);

    [Theory]
    [MemberData(nameof(Elements))]
    public void TopHat_MatchesNaiveReference(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomGray(33, 27, seed: 17), Build(shape, radius), Op.TopHat);

    [Theory]
    [MemberData(nameof(Elements))]
    public void BlackHat_MatchesNaiveReference(string shape, int radius)
        => AssertMorphology(DocumentPages.RandomGray(33, 27, seed: 18), Build(shape, radius), Op.BlackHat);

    /// <summary>A non-rectangular, non-run element takes the direct-scan path inside the operator.</summary>
    [Fact]
    public void Erode_MatchesNaiveReference_ForACustomSplitElement()
    {
        // A row with a hole in it is not a single run, so the fast row-run path cannot be used.
        bool[,] mask =
        {
            { true, false, true },
            { false, true, false },
            { true, false, true },
        };
        StructuringElement element = StructuringElement.FromMask(mask);
        AssertMorphology(DocumentPages.RandomGray(23, 19, seed: 19), element, Op.Erode);
        AssertMorphology(DocumentPages.RandomGray(23, 19, seed: 20), element, Op.Dilate);
    }

    [Fact]
    public void Morphology_MatchesNaiveReference_ForAnAsymmetricRectangle()
    {
        StructuringElement element = StructuringElement.Rectangle(9, 3);
        AssertMorphology(DocumentPages.RandomGray(29, 25, seed: 21), element, Op.Erode);
        AssertMorphology(DocumentPages.RandomGray(29, 25, seed: 22), element, Op.Dilate);
        AssertMorphology(DocumentPages.RandomGray(29, 25, seed: 23), element, Op.Open);
    }

    /// <summary>
    /// Regression: the row-run path sized its scratch buffers from the element's widest row, but the padded
    /// length is not monotonic in the run length (33 pixels need 45 bytes for a 9-long run yet only 44 for an
    /// 11-long one), so some width/element pairs overflowed the buffer and threw.
    /// </summary>
    [Fact]
    public void Morphology_RowRunPath_HandlesEveryWidthAndRadius()
    {
        for (int radius = 2; radius <= 6; radius++)
        {
            StructuringElement element = StructuringElement.Disk(radius);
            for (int width = 12; width <= 48; width++)
            {
                using Image<L8> image = DocumentPages.RandomGray(width, 7, seed: (radius * 100) + width);
                byte[] source = DocumentFixtures.Plane(image);
                image.Mutate(ctx => ctx.Erode(element));

                byte[] expected = NaiveFilter(source, width, 7, element, isMin: true);
                Assert.Equal(expected, DocumentFixtures.Plane(image));
            }
        }
    }

    [Fact]
    public void Morphology_WithASinglePixelElement_LeavesTheImageUnchanged()
    {
        using Image<L8> image = DocumentPages.RandomGray(17, 13, seed: 24);
        byte[] before = DocumentFixtures.Plane(image);
        image.Mutate(ctx => ctx.Erode(StructuringElement.Square(0)));
        Assert.Equal(before, DocumentFixtures.Plane(image));
    }

    [Fact]
    public void StructuringElement_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StructuringElement.Square(-1));
        Assert.Throws<ArgumentNullException>(() => StructuringElement.FromMask(null!));
        Assert.Throws<ArgumentException>(() => StructuringElement.FromMask(new bool[2, 3]));
        Assert.Throws<ArgumentException>(() => StructuringElement.FromMask(new bool[3, 3]));
    }

    [Fact]
    public void StructuringElement_ShapesHaveTheDocumentedMasks()
    {
        StructuringElement cross = StructuringElement.Cross(1);
        Assert.Equal(3, cross.Width);
        Assert.Equal(3, cross.Height);
        Assert.False(cross[0, 0]);
        Assert.True(cross[1, 0]);
        Assert.True(cross[0, 1]);

        StructuringElement disk = StructuringElement.Disk(2);
        Assert.Equal(5, disk.Width);
        Assert.False(disk[0, 0]);
        Assert.True(disk[2, 0]);

        // Even sizes are rounded up to odd so the anchor is a whole pixel.
        StructuringElement rect = StructuringElement.Rectangle(4, 6);
        Assert.Equal(5, rect.Width);
        Assert.Equal(7, rect.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => rect[5, 0]);
    }

    // ----- Despeckle -----

    /// <summary>
    /// The 400 injected specks are 1x1 or 2x2, but a few of them happen to touch, so the fixture actually holds
    /// 395 components of area 1, 2, 4, 5 and 8. Despeckle removes components up to the bound and must keep the
    /// rest, so the expected survivors are derived from an independent labelling of the speck mask.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Despeckle_RemovesExactlyTheSpecksWithinTheAreaBound(int maxArea)
    {
        using Image<L8> page = DocumentFixtures.LoadGray("speckle_page.png");
        int width = page.Width;
        int height = page.Height;

        byte[] specks = DocumentFixtures.LoadMask("speckle_page_specks_mask.png");
        int[] labels = BruteForceLabels(specks, width, height, Connectivity.Eight, out int count);
        var areas = new int[count + 1];
        foreach (int label in labels)
        {
            if (label != 0)
            {
                areas[label]++;
            }
        }

        var mustGo = new byte[specks.Length];
        var mustStay = new byte[specks.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] == 0)
            {
                continue;
            }

            if (areas[labels[i]] <= maxArea)
            {
                mustGo[i] = 1;
            }
            else
            {
                mustStay[i] = 1;
            }
        }

        page.Mutate(ctx => ctx.Despeckle(maxArea));
        byte[] after = DocumentFixtures.InkMask(page);

        Assert.Equal(0.0, DocumentFixtures.Retained(after, mustGo));
        Assert.Equal(1.0, DocumentFixtures.Retained(after, mustStay));

        byte[] text = DocumentFixtures.LoadMask("rules_page_text_mask.png");
        double textRetention = DocumentFixtures.Retained(after, text);
        Assert.True(textRetention >= 0.99, $"Only {textRetention:P1} of the text survived.");
    }

    [Fact]
    public void Despeckle_AtTheFixturesNominalBound_RemovesNearlyEverySpeckPixel()
    {
        int maxArea = DocumentFixtures.Entry("speckle_page").GetProperty("max_speck_area").GetInt32();
        byte[] specks = DocumentFixtures.LoadMask("speckle_page_specks_mask.png");

        using Image<L8> page = DocumentFixtures.LoadGray("speckle_page.png");
        page.Mutate(ctx => ctx.Despeckle(maxArea));

        double survival = DocumentFixtures.Retained(DocumentFixtures.InkMask(page), specks);
        Assert.True(survival <= 0.02, $"{survival:P1} of the speck pixels survived.");
    }

    [Fact]
    public void Despeckle_FillsSmallWhitePinholes()
    {
        using Image<L8> page = DocumentPages.Blank(40, 40);
        DocumentPages.Fill(page, new Rectangle(8, 8, 24, 24), 0);
        DocumentPages.Fill(page, new Rectangle(18, 18, 2, 2), 255); // a 4-pixel pinhole inside the blob
        page.Mutate(ctx => ctx.Despeckle(4));

        byte[] plane = DocumentFixtures.Plane(page);
        Assert.Equal(0, plane[(18 * 40) + 18]);
        Assert.Equal(0, plane[(19 * 40) + 19]);
    }

    [Fact]
    public void Despeckle_WithZeroArea_IsANoOp()
    {
        using Image<L8> page = DocumentFixtures.LoadGray("speckle_page.png");
        byte[] before = DocumentFixtures.Plane(page);
        page.Mutate(ctx => ctx.Despeckle(0));
        Assert.Equal(before, DocumentFixtures.Plane(page));
    }

    [Fact]
    public void Despeckle_RejectsNegativeArea()
    {
        using Image<L8> page = DocumentPages.Blank(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.Despeckle(-1)));
    }

    // ----- Thinning -----

    [Fact]
    public void Thin_ProducesAOnePixelSkeletonThatKeepsConnectivity()
    {
        using Image<L8> page = DocumentPages.Blank(60, 48);
        DocumentPages.Fill(page, new Rectangle(6, 10, 34, 7), 0);   // a thick horizontal bar
        DocumentPages.Fill(page, new Rectangle(20, 24, 7, 18), 0);  // a thick vertical bar
        DocumentPages.Fill(page, new Rectangle(44, 28, 9, 9), 0);   // an isolated block

        int componentsBefore = CountComponents(page, Connectivity.Eight);
        page.Mutate(ctx => ctx.Thin());

        byte[] plane = DocumentFixtures.Plane(page);
        Assert.All(plane, value => Assert.True(value is 0 or 255));

        byte[] mask = DocumentFixtures.InkMask(page);
        Assert.True(mask.Any(v => v != 0), "Thinning erased the whole page.");

        // A one-pixel-wide skeleton never contains a fully inked 2x2 block.
        for (int y = 0; y < page.Height - 1; y++)
        {
            for (int x = 0; x < page.Width - 1; x++)
            {
                bool solid = mask[(y * page.Width) + x] != 0
                    && mask[(y * page.Width) + x + 1] != 0
                    && mask[((y + 1) * page.Width) + x] != 0
                    && mask[((y + 1) * page.Width) + x + 1] != 0;
                Assert.False(solid, $"A 2x2 block at ({x}, {y}) survived thinning.");
            }
        }

        Assert.Equal(componentsBefore, CountComponents(page, Connectivity.Eight));
    }

    [Fact]
    public void Thin_LeavesAnAlreadyThinLineAlone()
    {
        using Image<L8> page = DocumentPages.Blank(40, 20);
        DocumentPages.Fill(page, new Rectangle(5, 9, 30, 1), 0);
        byte[] before = DocumentFixtures.InkMask(page);
        page.Mutate(ctx => ctx.Thin());
        Assert.Equal(before, DocumentFixtures.InkMask(page));
    }

    // ----- Connected components -----

    [Theory]
    [InlineData(Connectivity.Four, 101)]
    [InlineData(Connectivity.Eight, 101)]
    [InlineData(Connectivity.Four, 202)]
    [InlineData(Connectivity.Eight, 202)]
    [InlineData(Connectivity.Four, 303)]
    [InlineData(Connectivity.Eight, 303)]
    public void Label_MatchesBruteForceFloodFill(Connectivity connectivity, int seed)
    {
        using Image<L8> page = DocumentPages.RandomBinary(43, 37, seed, inkProbability: 0.35);
        byte[] mask = DocumentFixtures.InkMask(page);

        int[] actual = ConnectedComponents.Label(page.Frames.RootFrame, connectivity, out ComponentStats[] components);
        int[] expected = BruteForceLabels(mask, page.Width, page.Height, connectivity, out int expectedCount);

        Assert.Equal(expectedCount, components.Length);
        Assert.Equal(expected, actual); // labels are numbered in scan order of their first pixel
    }

    [Fact]
    public void Label_ReportsAreaBoundsAndCentroid()
    {
        using Image<L8> page = DocumentPages.Blank(30, 20);
        DocumentPages.Fill(page, new Rectangle(3, 4, 6, 2), 0);    // 12 px, centroid (5.5, 4.5)
        DocumentPages.Fill(page, new Rectangle(20, 12, 4, 4), 0);  // 16 px, centroid (21.5, 13.5)

        ConnectedComponents.Label(page.Frames.RootFrame, Connectivity.Eight, out ComponentStats[] components);

        Assert.Equal(2, components.Length);
        Assert.Equal(1, components[0].Label);
        Assert.Equal(12, components[0].Area);
        Assert.Equal(new Rectangle(3, 4, 6, 2), components[0].Bounds);
        Assert.Equal(5.5f, components[0].Centroid.X, 3);
        Assert.Equal(4.5f, components[0].Centroid.Y, 3);

        Assert.Equal(16, components[1].Area);
        Assert.Equal(new Rectangle(20, 12, 4, 4), components[1].Bounds);
        Assert.Equal(21.5f, components[1].Centroid.X, 3);
        Assert.Equal(13.5f, components[1].Centroid.Y, 3);
    }

    /// <summary>A diagonal chain is one component under 8-connectivity and several under 4-connectivity.</summary>
    [Fact]
    public void Label_ConnectivityChangesTheDiagonalChain()
    {
        using Image<L8> page = DocumentPages.Blank(12, 12);
        for (int i = 0; i < 8; i++)
        {
            DocumentPages.Fill(page, new Rectangle(2 + i, 2 + i, 1, 1), 0);
        }

        ConnectedComponents.Label(page.Frames.RootFrame, Connectivity.Eight, out ComponentStats[] eight);
        ConnectedComponents.Label(page.Frames.RootFrame, Connectivity.Four, out ComponentStats[] four);

        Assert.Single(eight);
        Assert.Equal(8, four.Length);
    }

    [Fact]
    public void Label_RejectsAnUnknownConnectivity()
    {
        using Image<L8> page = DocumentPages.Blank(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ConnectedComponents.Label(page.Frames.RootFrame, (Connectivity)7, out ComponentStats[] _));
    }

    [Fact]
    public void Label_NullFrame_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => ConnectedComponents.Label((ImageFrame<L8>)null!, Connectivity.Eight, out ComponentStats[] _));

    // ----- Component filters -----

    [Fact]
    public void RemoveSmallObjects_DropsComponentsBelowTheThreshold()
    {
        using Image<L8> page = DocumentPages.Blank(40, 30);
        DocumentPages.Fill(page, new Rectangle(4, 4, 2, 2), 0);    // area 4
        DocumentPages.Fill(page, new Rectangle(14, 4, 3, 3), 0);   // area 9
        DocumentPages.Fill(page, new Rectangle(26, 4, 6, 6), 0);   // area 36

        page.Mutate(ctx => ctx.RemoveSmallObjects(9));
        byte[] plane = DocumentFixtures.Plane(page);

        Assert.Equal(255, plane[(4 * 40) + 4]);  // the 4-pixel blob is gone
        Assert.Equal(0, plane[(4 * 40) + 14]);   // exactly-9 is kept (the bound is "fewer than")
        Assert.Equal(0, plane[(4 * 40) + 26]);
    }

    [Fact]
    public void RemoveSmallObjects_WithThresholdBelowTwo_IsANoOp()
    {
        using Image<L8> page = DocumentPages.RandomBinary(20, 20, seed: 31);
        byte[] before = DocumentFixtures.Plane(page);
        page.Mutate(ctx => ctx.RemoveSmallObjects(1));
        Assert.Equal(before, DocumentFixtures.Plane(page));
    }

    [Fact]
    public void RemoveSmallObjects_RejectsNegativeArea()
    {
        using Image<L8> page = DocumentPages.Blank(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => page.Mutate(ctx => ctx.RemoveSmallObjects(-1)));
    }

    [Fact]
    public void KeepLargestComponent_ErasesEverythingElse()
    {
        using Image<L8> page = DocumentPages.Blank(40, 30);
        DocumentPages.Fill(page, new Rectangle(4, 4, 3, 3), 0);
        DocumentPages.Fill(page, new Rectangle(20, 10, 8, 8), 0);
        DocumentPages.Fill(page, new Rectangle(4, 22, 5, 5), 0);

        page.Mutate(ctx => ctx.KeepLargestComponent());

        ConnectedComponents.Label(page.Frames.RootFrame, Connectivity.Eight, out ComponentStats[] components);
        Assert.Single(components);
        Assert.Equal(new Rectangle(20, 10, 8, 8), components[0].Bounds);
    }

    [Fact]
    public void KeepLargestComponent_OnABlankPage_IsANoOp()
    {
        using Image<L8> page = DocumentPages.Blank(16, 16);
        page.Mutate(ctx => ctx.KeepLargestComponent());
        Assert.All(DocumentFixtures.Plane(page), value => Assert.Equal(255, value));
    }

    [Fact]
    public void FillHoles_FillsEnclosedBackgroundOnly()
    {
        using Image<L8> page = DocumentPages.Blank(40, 40);
        // A ring: an 18x18 black square with a 6x6 white hole inside.
        DocumentPages.Fill(page, new Rectangle(10, 10, 18, 18), 0);
        DocumentPages.Fill(page, new Rectangle(16, 16, 6, 6), 255);

        page.Mutate(ctx => ctx.FillHoles());
        byte[] plane = DocumentFixtures.Plane(page);

        Assert.Equal(0, plane[(18 * 40) + 18]);  // the hole is filled
        Assert.Equal(255, plane[(2 * 40) + 2]);  // the outside background is untouched
    }

    [Fact]
    public void FillHoles_LeavesBackgroundConnectedToTheBorderAlone()
    {
        using Image<L8> page = DocumentPages.Blank(30, 30);
        // A C shape: the inner background reaches the border through the opening, so it is not a hole.
        DocumentPages.Fill(page, new Rectangle(5, 5, 20, 20), 0);
        DocumentPages.Fill(page, new Rectangle(10, 10, 20, 10), 255);

        page.Mutate(ctx => ctx.FillHoles());
        Assert.Equal(255, DocumentFixtures.Plane(page)[(12 * 30) + 12]);
    }

    // ----- Helpers -----

    private enum Op
    {
        Erode,
        Dilate,
        Open,
        Close,
        TopHat,
        BlackHat,
    }

    private static void AssertMorphology(Image<L8> image, StructuringElement element, Op op)
    {
        using (image)
        {
            int width = image.Width;
            int height = image.Height;
            byte[] source = DocumentFixtures.Plane(image);

            image.Mutate(ctx =>
            {
                switch (op)
                {
                    case Op.Erode:
                        ctx.Erode(element);
                        break;
                    case Op.Dilate:
                        ctx.Dilate(element);
                        break;
                    case Op.Open:
                        ctx.Open(element);
                        break;
                    case Op.Close:
                        ctx.Close(element);
                        break;
                    case Op.TopHat:
                        ctx.TopHat(element);
                        break;
                    default:
                        ctx.BlackHat(element);
                        break;
                }
            });

            byte[] expected = NaiveMorphology(source, width, height, element, op);
            byte[] actual = DocumentFixtures.Plane(image);

            int differences = 0;
            int firstIndex = -1;
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    differences++;
                    if (firstIndex < 0)
                    {
                        firstIndex = i;
                    }
                }
            }

            Assert.True(
                differences == 0,
                differences == 0
                    ? string.Empty
                    : $"{op} with a {element.Width}x{element.Height} {element.Shape} element differs in {differences} "
                      + $"pixels; first at ({firstIndex % width}, {firstIndex / width}): expected "
                      + $"{expected[firstIndex]}, got {actual[firstIndex]}.");
        }
    }

    private static byte[] NaiveMorphology(byte[] source, int width, int height, StructuringElement element, Op op)
    {
        switch (op)
        {
            case Op.Erode:
                return NaiveFilter(source, width, height, element, isMin: true);
            case Op.Dilate:
                return NaiveFilter(source, width, height, element, isMin: false);
            case Op.Open:
                return NaiveOpen(source, width, height, element);
            case Op.Close:
                return NaiveClose(source, width, height, element);
            case Op.TopHat:
            {
                byte[] opened = NaiveOpen(source, width, height, element);
                return Subtract(source, opened);
            }

            default:
            {
                byte[] closed = NaiveClose(source, width, height, element);
                return Subtract(closed, source);
            }
        }
    }

    private static byte[] NaiveOpen(byte[] source, int width, int height, StructuringElement element)
        => NaiveFilter(NaiveFilter(source, width, height, element, isMin: true), width, height, element, isMin: false);

    private static byte[] NaiveClose(byte[] source, int width, int height, StructuringElement element)
        => NaiveFilter(NaiveFilter(source, width, height, element, isMin: false), width, height, element, isMin: true);

    private static byte[] Subtract(byte[] left, byte[] right)
    {
        var result = new byte[left.Length];
        for (int i = 0; i < left.Length; i++)
        {
            result[i] = (byte)Math.Clamp(left[i] - right[i], 0, 255);
        }

        return result;
    }

    /// <summary>
    /// The definition the operators must satisfy: the minimum (or maximum) over the element's set pixels,
    /// ignoring positions outside the image.
    /// </summary>
    private static byte[] NaiveFilter(byte[] source, int width, int height, StructuringElement element, bool isMin)
    {
        var result = new byte[source.Length];
        int anchorX = element.AnchorX;
        int anchorY = element.AnchorY;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int best = isMin ? 255 : 0;
                for (int j = 0; j < element.Height; j++)
                {
                    for (int i = 0; i < element.Width; i++)
                    {
                        if (!element[i, j])
                        {
                            continue;
                        }

                        int sx = x + i - anchorX;
                        int sy = y + j - anchorY;
                        if ((uint)sx >= (uint)width || (uint)sy >= (uint)height)
                        {
                            continue;
                        }

                        int v = source[(sy * width) + sx];
                        best = isMin ? Math.Min(best, v) : Math.Max(best, v);
                    }
                }

                result[(y * width) + x] = (byte)best;
            }
        }

        return result;
    }

    private static int[] BruteForceLabels(byte[] mask, int width, int height, Connectivity connectivity, out int count)
    {
        var labels = new int[mask.Length];
        var stack = new Stack<int>();
        count = 0;
        for (int seed = 0; seed < mask.Length; seed++)
        {
            if (mask[seed] == 0 || labels[seed] != 0)
            {
                continue;
            }

            count++;
            labels[seed] = count;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % width;
                int py = p / width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx == 0 && dy == 0) || (connectivity == Connectivity.Four && dx != 0 && dy != 0))
                        {
                            continue;
                        }

                        int nx = px + dx;
                        int ny = py + dy;
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height)
                        {
                            continue;
                        }

                        int n = (ny * width) + nx;
                        if (mask[n] != 0 && labels[n] == 0)
                        {
                            labels[n] = count;
                            stack.Push(n);
                        }
                    }
                }
            }
        }

        return labels;
    }

    private static int CountComponents(Image<L8> image, Connectivity connectivity)
    {
        ConnectedComponents.Label(image.Frames.RootFrame, connectivity, out ComponentStats[] components);
        return components.Length;
    }
}
