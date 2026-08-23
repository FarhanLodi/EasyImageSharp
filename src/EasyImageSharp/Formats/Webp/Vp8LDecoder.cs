namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// Decoder for the VP8L lossless bitstream (RFC 9649 section 3): LSB-first entropy coded ARGB pixels with
/// LZ77 backward references, a colour cache, per-tile prefix code groups selected through a meta prefix
/// image, and the four reversible transforms (predictor, cross-colour, subtract-green and colour indexing
/// with pixel bundling). The same machinery decodes the green-channel image embedded in an ALPH chunk.
/// </summary>
internal sealed class Vp8LDecoder
{
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int NumCodeLengthCodes = 19;
    private const int MaxCacheBits = 11;
    private const int CodeToPlaneCodes = 120;
    private const int DefaultCodeLength = 8;

    private const int PredictorTransform = 0;
    private const int CrossColorTransform = 1;
    private const int SubtractGreenTransform = 2;
    private const int ColorIndexingTransform = 3;

    private static ReadOnlySpan<byte> CodeLengthCodeOrder => new byte[] { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    private static ReadOnlySpan<byte> CodeLengthExtraBits => new byte[] { 2, 3, 7 };

    private static ReadOnlySpan<byte> CodeLengthRepeatOffsets => new byte[] { 3, 3, 11 };

    /// <summary>The 120 short-distance codes as (dx, dy) pairs (RFC 9649 section 3.7.2.3).</summary>
    private static ReadOnlySpan<sbyte> DistanceMap => new sbyte[]
    {
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2,
        2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0,
        1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0,
        1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0,
        1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3,
        0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2,
        3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6,
        -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    };

    private readonly Vp8LBitReader reader;
    private readonly List<Transform> transforms = new();
    private readonly int[] codeLengthBuffer = new int[NumLiteralCodes + NumLengthCodes + (1 << MaxCacheBits)];
    private int transformsSeen;

    // Metadata of the image stream currently being decoded.
    private HuffmanGroup[] groups = Array.Empty<HuffmanGroup>();
    private uint[]? huffmanImage;
    private int huffmanBits;
    private int huffmanXSize;
    private int colorCacheBits;

    private Vp8LDecoder(Vp8LBitReader reader) => this.reader = reader;

    /// <summary>Parses the 5-byte VP8L header (signature, dimensions, alpha hint). Returns false when the signature or version is wrong.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out int width, out int height, out bool hasAlpha)
    {
        width = 0;
        height = 0;
        hasAlpha = false;
        if (data.Length < 5 || data[0] != 0x2f)
        {
            return false;
        }

        uint bits = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
        width = (int)(bits & 0x3fff) + 1;
        height = (int)((bits >> 14) & 0x3fff) + 1;
        hasAlpha = ((bits >> 28) & 1) != 0;
        int version = (int)(bits >> 29);
        return version == 0;
    }

    /// <summary>Decodes a complete VP8L bitstream (the payload of a 'VP8L' chunk) into ARGB pixels.</summary>
    /// <param name="data">The buffer holding the bitstream.</param>
    /// <param name="start">Offset of the first bitstream byte within <paramref name="data"/>.</param>
    /// <param name="length">Length of the bitstream in bytes.</param>
    /// <param name="options">Decoder limits, checked once the dimensions are known and before pixel memory is allocated.</param>
    /// <param name="width">Receives the image width from the bitstream header.</param>
    /// <param name="height">Receives the image height from the bitstream header.</param>
    public static uint[] Decode(byte[] data, int start, int length, DecoderOptions options, out int width, out int height)
    {
        if (!TryReadHeader(data.AsSpan(start, length), out width, out height, out _))
        {
            throw new InvalidImageContentException("Invalid VP8L header.");
        }

        options.EnsureFrameWithinLimits(width, height, "WebP");
        var reader = new Vp8LBitReader(data, start, length);
        reader.Skip(8);  // Signature byte.
        reader.Skip(32); // 14-bit width, 14-bit height, alpha hint, 3-bit version.
        var decoder = new Vp8LDecoder(reader);
        return decoder.DecodeMainImage(width, height);
    }

    /// <summary>Decodes the header-less VP8L image stream of a lossless-compressed ALPH chunk; the alpha values are the green channel.</summary>
    public static byte[] DecodeAlpha(byte[] data, int start, int length, int width, int height)
    {
        var decoder = new Vp8LDecoder(new Vp8LBitReader(data, start, length));
        uint[] argb = decoder.DecodeMainImage(width, height);
        var alpha = new byte[argb.Length];
        for (int i = 0; i < argb.Length; i++)
        {
            alpha[i] = (byte)(argb[i] >> 8);
        }

        return alpha;
    }

    // ----- Image stream -----

    /// <summary>Decodes a level-0 image stream of the given final size, applying the inverse transforms.</summary>
    private uint[] DecodeMainImage(int width, int height)
    {
        this.DecodeImageStream(width, height, isLevel0: true, out int codedWidth, out int codedHeight);
        var pixels = new uint[width * height];
        this.DecodeImageData(pixels, codedWidth, codedHeight);
        this.ApplyInverseTransforms(pixels);
        return pixels;
    }

    /// <summary>
    /// Reads the headers of an image stream (transforms for level 0, colour cache, prefix codes). For sub-images
    /// (transform data, meta prefix image) the pixel data is decoded and returned as well.
    /// </summary>
    private uint[]? DecodeImageStream(int xsize, int ysize, bool isLevel0, out int codedWidth, out int codedHeight)
    {
        int transformXSize = xsize;
        int transformYSize = ysize;
        if (isLevel0)
        {
            while (this.reader.ReadBit())
            {
                this.ReadTransform(ref transformXSize, transformYSize);
            }
        }

        int cacheBits = 0;
        if (this.reader.ReadBit())
        {
            cacheBits = (int)this.reader.ReadBits(4);
            if (cacheBits < 1 || cacheBits > MaxCacheBits)
            {
                throw new InvalidImageContentException($"Invalid VP8L colour cache size {cacheBits}.");
            }
        }

        this.ReadHuffmanCodes(transformXSize, transformYSize, cacheBits, allowRecursion: isLevel0);
        this.colorCacheBits = cacheBits;
        this.huffmanXSize = SubSampleSize(transformXSize, this.huffmanBits);

        codedWidth = transformXSize;
        codedHeight = transformYSize;
        if (isLevel0)
        {
            return null;
        }

        var data = new uint[transformXSize * transformYSize];
        this.DecodeImageData(data, transformXSize, transformYSize);
        return data;
    }

    private void ReadTransform(ref int xsize, int ysize)
    {
        int type = (int)this.reader.ReadBits(2);
        if ((this.transformsSeen & (1 << type)) != 0)
        {
            throw new InvalidImageContentException("VP8L transform appears more than once.");
        }

        this.transformsSeen |= 1 << type;
        var transform = new Transform { Type = type, XSize = xsize, YSize = ysize };
        switch (type)
        {
            case PredictorTransform:
            case CrossColorTransform:
                transform.Bits = 2 + (int)this.reader.ReadBits(3);
                transform.Data = this.DecodeImageStream(
                    SubSampleSize(xsize, transform.Bits), SubSampleSize(ysize, transform.Bits), isLevel0: false, out _, out _)!;
                break;

            case ColorIndexingTransform:
            {
                int numColors = (int)this.reader.ReadBits(8) + 1;
                int bits = numColors > 16 ? 0 : numColors > 4 ? 1 : numColors > 2 ? 2 : 3;
                xsize = SubSampleSize(xsize, bits);
                transform.Bits = bits;
                uint[] palette = this.DecodeImageStream(numColors, 1, isLevel0: false, out _, out _)!;
                transform.Data = ExpandColorMap(palette, numColors, bits);
                break;
            }

            default:
                break; // Subtract green carries no data.
        }

        this.transforms.Add(transform);
    }

    /// <summary>Un-deltas the palette and pads it to every index a bundled pixel can address (unused entries are transparent black).</summary>
    private static uint[] ExpandColorMap(uint[] palette, int numColors, int bits)
    {
        int finalNumColors = 1 << (8 >> bits);
        var map = new uint[Math.Max(finalNumColors, numColors)];
        map[0] = palette[0];
        for (int i = 1; i < numColors; i++)
        {
            map[i] = AddPixels(palette[i], map[i - 1]);
        }

        return map;
    }

    // ----- Prefix codes -----

    private void ReadHuffmanCodes(int xsize, int ysize, int cacheBits, bool allowRecursion)
    {
        int numGroups = 1;
        int numGroupsMax = 1;
        uint[]? image = null;
        int bits = 0;
        int[]? mapping = null;

        if (allowRecursion && this.reader.ReadBit())
        {
            bits = 2 + (int)this.reader.ReadBits(3);
            int hx = SubSampleSize(xsize, bits);
            int hy = SubSampleSize(ysize, bits);
            image = this.DecodeImageStream(hx, hy, isLevel0: false, out _, out _)!;
            for (int i = 0; i < image.Length; i++)
            {
                int group = (int)((image[i] >> 8) & 0xffff);
                image[i] = (uint)group;
                if (group >= numGroupsMax)
                {
                    numGroupsMax = group + 1;
                }
            }

            if (numGroupsMax > 1000 || numGroupsMax > xsize * ysize)
            {
                // Only materialise the groups the meta image actually references.
                mapping = new int[numGroupsMax];
                Array.Fill(mapping, -1);
                numGroups = 0;
                for (int i = 0; i < image.Length; i++)
                {
                    ref int mapped = ref mapping[image[i]];
                    if (mapped == -1)
                    {
                        mapped = numGroups++;
                    }

                    image[i] = (uint)mapped;
                }
            }
            else
            {
                numGroups = numGroupsMax;
            }
        }

        var groups = new HuffmanGroup[numGroups];
        int greenAlphabet = NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        for (int i = 0; i < numGroupsMax; i++)
        {
            if (mapping is not null && mapping[i] == -1)
            {
                // Unused group: its codes must still be well formed but are not kept.
                this.ReadHuffmanCode(greenAlphabet);
                this.ReadHuffmanCode(NumLiteralCodes);
                this.ReadHuffmanCode(NumLiteralCodes);
                this.ReadHuffmanCode(NumLiteralCodes);
                this.ReadHuffmanCode(NumDistanceCodes);
                continue;
            }

            var group = new HuffmanGroup
            {
                Green = this.ReadHuffmanCode(greenAlphabet),
                Red = this.ReadHuffmanCode(NumLiteralCodes),
                Blue = this.ReadHuffmanCode(NumLiteralCodes),
                Alpha = this.ReadHuffmanCode(NumLiteralCodes),
                Distance = this.ReadHuffmanCode(NumDistanceCodes),
            };

            if (group.Red.IsSingleSymbol && group.Blue.IsSingleSymbol && group.Alpha.IsSingleSymbol)
            {
                group.TrivialLiteral = true;
                group.LiteralArb = ((uint)group.Alpha.SingleSymbol << 24) | ((uint)group.Red.SingleSymbol << 16) | (uint)group.Blue.SingleSymbol;
            }

            groups[mapping is null ? i : mapping[i]] = group;
        }

        this.groups = groups;
        this.huffmanImage = image;
        this.huffmanBits = bits;
    }

    private HuffmanTree ReadHuffmanCode(int alphabetSize)
    {
        int[] codeLengths = this.codeLengthBuffer;
        Array.Clear(codeLengths, 0, Math.Max(alphabetSize, NumLiteralCodes));

        if (this.reader.ReadBit())
        {
            // Simple code: one or two symbols listed explicitly.
            int numSymbols = (int)this.reader.ReadBits(1) + 1;
            int firstSymbolLength = (int)this.reader.ReadBits(1);
            int symbol = (int)this.reader.ReadBits(firstSymbolLength == 0 ? 1 : 8);
            codeLengths[CheckSymbol(symbol, alphabetSize)] = 1;
            if (numSymbols == 2)
            {
                symbol = (int)this.reader.ReadBits(8);
                codeLengths[CheckSymbol(symbol, alphabetSize)] = 1;
            }
        }
        else
        {
            Span<int> codeLengthCodeLengths = stackalloc int[NumCodeLengthCodes];
            codeLengthCodeLengths.Clear();
            int numCodes = (int)this.reader.ReadBits(4) + 4;
            for (int i = 0; i < numCodes; i++)
            {
                codeLengthCodeLengths[CodeLengthCodeOrder[i]] = (int)this.reader.ReadBits(3);
            }

            this.ReadHuffmanCodeLengths(codeLengthCodeLengths, alphabetSize, codeLengths);
        }

        return HuffmanTree.Build(codeLengths.AsSpan(0, alphabetSize))
            ?? throw new InvalidImageContentException("Invalid VP8L prefix code.");
    }

    private static int CheckSymbol(int symbol, int alphabetSize)
        => symbol < alphabetSize
            ? symbol
            : throw new InvalidImageContentException($"VP8L simple prefix code names symbol {symbol}, outside its {alphabetSize}-symbol alphabet.");

    private void ReadHuffmanCodeLengths(ReadOnlySpan<int> codeLengthCodeLengths, int numSymbols, int[] codeLengths)
    {
        HuffmanTree tree = HuffmanTree.Build(codeLengthCodeLengths)
            ?? throw new InvalidImageContentException("Invalid VP8L code length code.");

        int maxSymbol;
        if (this.reader.ReadBit())
        {
            int lengthBits = 2 + (2 * (int)this.reader.ReadBits(3));
            maxSymbol = 2 + (int)this.reader.ReadBits(lengthBits);
            if (maxSymbol > numSymbols)
            {
                throw new InvalidImageContentException("VP8L code length count exceeds the alphabet size.");
            }
        }
        else
        {
            maxSymbol = numSymbols;
        }

        int prevCodeLength = DefaultCodeLength;
        int symbol = 0;
        while (symbol < numSymbols)
        {
            if (maxSymbol-- == 0)
            {
                break;
            }

            int codeLength = tree.ReadSymbol(this.reader);
            if (codeLength < 16)
            {
                codeLengths[symbol++] = codeLength;
                if (codeLength != 0)
                {
                    prevCodeLength = codeLength;
                }
            }
            else
            {
                bool usePrevious = codeLength == 16;
                int slot = codeLength - 16;
                int repeat = (int)this.reader.ReadBits(CodeLengthExtraBits[slot]) + CodeLengthRepeatOffsets[slot];
                if (symbol + repeat > numSymbols)
                {
                    throw new InvalidImageContentException("VP8L code length repeat overruns the alphabet.");
                }

                int length = usePrevious ? prevCodeLength : 0;
                while (repeat-- > 0)
                {
                    codeLengths[symbol++] = length;
                }
            }
        }
    }

    // ----- Entropy-coded pixel data -----

    private void DecodeImageData(uint[] data, int width, int height)
    {
        Vp8LBitReader br = this.reader;
        int end = width * height;
        int lengthCodeLimit = NumLiteralCodes + NumLengthCodes;
        int cacheSize = this.colorCacheBits > 0 ? 1 << this.colorCacheBits : 0;
        int cacheLimit = lengthCodeLimit + cacheSize;
        uint[]? cache = cacheSize > 0 ? new uint[cacheSize] : null;
        int cacheShift = 32 - this.colorCacheBits;
        int mask = this.huffmanBits == 0 ? ~0 : (1 << this.huffmanBits) - 1;

        int src = 0;
        int lastCached = 0;
        int col = 0;
        int row = 0;
        HuffmanGroup group = this.groups[0];

        while (src < end)
        {
            if ((col & mask) == 0)
            {
                group = this.GetGroup(col, row);
            }

            int code = group.Green.ReadSymbol(br);
            if (code < NumLiteralCodes)
            {
                uint argb;
                if (group.TrivialLiteral)
                {
                    argb = group.LiteralArb | ((uint)code << 8);
                }
                else
                {
                    uint red = (uint)group.Red.ReadSymbol(br);
                    uint blue = (uint)group.Blue.ReadSymbol(br);
                    uint alpha = (uint)group.Alpha.ReadSymbol(br);
                    argb = (alpha << 24) | (red << 16) | ((uint)code << 8) | blue;
                }

                data[src++] = argb;
                if (++col >= width)
                {
                    col = 0;
                    row++;
                    if (cache is not null)
                    {
                        while (lastCached < src)
                        {
                            CacheInsert(cache, cacheShift, data[lastCached++]);
                        }
                    }
                }
            }
            else if (code < lengthCodeLimit)
            {
                int length = this.GetCopyValue(code - NumLiteralCodes);
                int distanceSymbol = group.Distance.ReadSymbol(br);
                int distanceCode = this.GetCopyValue(distanceSymbol);
                int distance = PlaneCodeToDistance(width, distanceCode);
                if (src < distance || end - src < length)
                {
                    throw new InvalidImageContentException("VP8L backward reference points outside the image.");
                }

                for (int i = 0; i < length; i++)
                {
                    data[src + i] = data[src + i - distance];
                }

                src += length;
                col += length;
                while (col >= width)
                {
                    col -= width;
                    row++;
                }

                if (src < end && (col & mask) != 0)
                {
                    group = this.GetGroup(col, row);
                }

                if (cache is not null)
                {
                    while (lastCached < src)
                    {
                        CacheInsert(cache, cacheShift, data[lastCached++]);
                    }
                }
            }
            else if (code < cacheLimit)
            {
                int key = code - lengthCodeLimit;
                while (lastCached < src)
                {
                    CacheInsert(cache!, cacheShift, data[lastCached++]);
                }

                data[src++] = cache![key];
                if (++col >= width)
                {
                    col = 0;
                    row++;
                    while (lastCached < src)
                    {
                        CacheInsert(cache, cacheShift, data[lastCached++]);
                    }
                }
            }
            else
            {
                throw new InvalidImageContentException("Invalid VP8L symbol.");
            }
        }
    }

    private HuffmanGroup GetGroup(int col, int row)
    {
        if (this.huffmanImage is null)
        {
            return this.groups[0];
        }

        int index = ((row >> this.huffmanBits) * this.huffmanXSize) + (col >> this.huffmanBits);
        return this.groups[this.huffmanImage[index]];
    }

    private static void CacheInsert(uint[] cache, int shift, uint argb)
        => cache[(int)(unchecked(0x1e35a7bdu * argb) >> shift)] = argb;

    /// <summary>Decodes a length or distance value from its prefix symbol plus extra bits.</summary>
    private int GetCopyValue(int symbol)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        int extraBits = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extraBits;
        return offset + (int)this.reader.ReadBits(extraBits) + 1;
    }

    private static int PlaneCodeToDistance(int xsize, int planeCode)
    {
        if (planeCode > CodeToPlaneCodes)
        {
            return planeCode - CodeToPlaneCodes;
        }

        int dx = DistanceMap[(planeCode - 1) * 2];
        int dy = DistanceMap[((planeCode - 1) * 2) + 1];
        int distance = dx + (dy * xsize);
        return distance >= 1 ? distance : 1;
    }

    // ----- Inverse transforms -----

    private void ApplyInverseTransforms(uint[] pixels)
    {
        for (int t = this.transforms.Count - 1; t >= 0; t--)
        {
            Transform transform = this.transforms[t];
            switch (transform.Type)
            {
                case SubtractGreenTransform:
                    AddGreenToBlueAndRed(pixels, transform.XSize * transform.YSize);
                    break;

                case PredictorTransform:
                    PredictorInverse(transform, pixels);
                    break;

                case CrossColorTransform:
                    CrossColorInverse(transform, pixels);
                    break;

                case ColorIndexingTransform:
                    ColorIndexInverse(transform, pixels);
                    break;
            }
        }
    }

    private static void AddGreenToBlueAndRed(uint[] pixels, int count)
    {
        for (int i = 0; i < count; i++)
        {
            uint argb = pixels[i];
            uint green = (argb >> 8) & 0xff;
            uint redBlue = argb & 0x00ff00ffu;
            redBlue += (green << 16) | green;
            redBlue &= 0x00ff00ffu;
            pixels[i] = (argb & 0xff00ff00u) | redBlue;
        }
    }

    private static void PredictorInverse(Transform transform, uint[] pixels)
    {
        int width = transform.XSize;
        int height = transform.YSize;
        int bits = transform.Bits;
        int tilesPerRow = SubSampleSize(width, bits);
        uint[] modes = transform.Data;

        // First row: top-left pixel predicts black, the rest predict from the left.
        pixels[0] = AddPixels(pixels[0], 0xff000000u);
        for (int x = 1; x < width; x++)
        {
            pixels[x] = AddPixels(pixels[x], pixels[x - 1]);
        }

        for (int y = 1; y < height; y++)
        {
            int rowStart = y * width;
            int modeRow = (y >> bits) * tilesPerRow;

            // First column predicts from the top.
            pixels[rowStart] = AddPixels(pixels[rowStart], pixels[rowStart - width]);
            for (int x = 1; x < width; x++)
            {
                int i = rowStart + x;
                int mode = (int)((modes[modeRow + (x >> bits)] >> 8) & 0xf);
                uint left = pixels[i - 1];
                uint top = pixels[i - width];
                uint pred = mode switch
                {
                    0 => 0xff000000u,
                    1 => left,
                    2 => top,
                    3 => pixels[i - width + 1],
                    4 => pixels[i - width - 1],
                    5 => Average2(Average2(left, pixels[i - width + 1]), top),
                    6 => Average2(left, pixels[i - width - 1]),
                    7 => Average2(left, top),
                    8 => Average2(pixels[i - width - 1], top),
                    9 => Average2(top, pixels[i - width + 1]),
                    10 => Average2(Average2(left, pixels[i - width - 1]), Average2(top, pixels[i - width + 1])),
                    11 => Select(top, left, pixels[i - width - 1]),
                    12 => ClampedAddSubtractFull(left, top, pixels[i - width - 1]),
                    13 => ClampedAddSubtractHalf(left, top, pixels[i - width - 1]),
                    _ => 0xff000000u,
                };
                pixels[i] = AddPixels(pixels[i], pred);
            }
        }
    }

    private static void CrossColorInverse(Transform transform, uint[] pixels)
    {
        int width = transform.XSize;
        int height = transform.YSize;
        int bits = transform.Bits;
        int tilesPerRow = SubSampleSize(width, bits);
        uint[] data = transform.Data;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            int tileRow = (y >> bits) * tilesPerRow;
            for (int x = 0; x < width; x++)
            {
                uint m = data[tileRow + (x >> bits)];
                int greenToRed = (sbyte)(m & 0xff);
                int greenToBlue = (sbyte)((m >> 8) & 0xff);
                int redToBlue = (sbyte)((m >> 16) & 0xff);

                uint argb = pixels[rowStart + x];
                int green = (sbyte)(argb >> 8);
                int newRed = (int)((argb >> 16) & 0xff);
                int newBlue = (int)(argb & 0xff);
                newRed += (greenToRed * green) >> 5;
                newRed &= 0xff;
                newBlue += (greenToBlue * green) >> 5;
                newBlue += (redToBlue * (sbyte)newRed) >> 5;
                newBlue &= 0xff;
                pixels[rowStart + x] = (argb & 0xff00ff00u) | ((uint)newRed << 16) | (uint)newBlue;
            }
        }
    }

    /// <summary>Expands bundled palette indices in place; the packed rows are narrower than the output rows, so the image is walked from the end.</summary>
    private static void ColorIndexInverse(Transform transform, uint[] pixels)
    {
        int width = transform.XSize;
        int height = transform.YSize;
        int bits = transform.Bits;
        uint[] map = transform.Data;

        if (bits == 0)
        {
            for (int i = 0; i < width * height; i++)
            {
                pixels[i] = map[(pixels[i] >> 8) & 0xff];
            }

            return;
        }

        int bitsPerPixel = 8 >> bits;
        int pixelsPerByte = 1 << bits;
        int packedWidth = SubSampleSize(width, bits);
        uint bitMask = (1u << bitsPerPixel) - 1;

        for (int y = height - 1; y >= 0; y--)
        {
            int inRow = y * packedWidth;
            int outRow = y * width;
            for (int px = packedWidth - 1; px >= 0; px--)
            {
                uint packed = (pixels[inRow + px] >> 8) & 0xff;
                int x0 = px * pixelsPerByte;
                int count = Math.Min(pixelsPerByte, width - x0);
                for (int k = count - 1; k >= 0; k--)
                {
                    pixels[outRow + x0 + k] = map[(packed >> (k * bitsPerPixel)) & bitMask];
                }
            }
        }
    }

    // ----- Pixel arithmetic -----

    private static uint AddPixels(uint a, uint b)
    {
        uint alphaGreen = (a & 0xff00ff00u) + (b & 0xff00ff00u);
        uint redBlue = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
        return (alphaGreen & 0xff00ff00u) | (redBlue & 0x00ff00ffu);
    }

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);

    private static uint Select(uint a, uint b, uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24))
            + Sub3((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff), (int)((c >> 16) & 0xff))
            + Sub3((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff), (int)((c >> 8) & 0xff))
            + Sub3((int)(a & 0xff), (int)(b & 0xff), (int)(c & 0xff));
        return paMinusPb <= 0 ? a : b;
    }

    private static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        uint a = Clip255((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24));
        uint r = Clip255((int)((c0 >> 16) & 0xff) + (int)((c1 >> 16) & 0xff) - (int)((c2 >> 16) & 0xff));
        uint g = Clip255((int)((c0 >> 8) & 0xff) + (int)((c1 >> 8) & 0xff) - (int)((c2 >> 8) & 0xff));
        uint b = Clip255((int)(c0 & 0xff) + (int)(c1 & 0xff) - (int)(c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        uint ave = Average2(c0, c1);
        uint a = AddSubtractHalf((int)(ave >> 24), (int)(c2 >> 24));
        uint r = AddSubtractHalf((int)((ave >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        uint g = AddSubtractHalf((int)((ave >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        uint b = AddSubtractHalf((int)(ave & 0xff), (int)(c2 & 0xff));
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint AddSubtractHalf(int a, int b) => Clip255(a + ((a - b) / 2));

    private static uint Clip255(int v) => (uint)Math.Clamp(v, 0, 255);

    private static int SubSampleSize(int size, int samplingBits) => (size + (1 << samplingBits) - 1) >> samplingBits;

    private struct Transform
    {
        public int Type;
        public int Bits;
        public int XSize;
        public int YSize;
        public uint[] Data;
    }
}
