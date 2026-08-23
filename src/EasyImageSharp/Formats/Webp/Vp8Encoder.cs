namespace EasyImageSharp.Formats.Webp;

/// <summary>
/// A VP8 key-frame encoder (RFC 6386). It produces the raw bitstream of a single intra-coded frame: the
/// uncompressed 10-byte header, the first partition holding the frame header and the per-macroblock modes,
/// and one token partition holding the quantized DCT coefficients.
/// </summary>
/// <remarks>
/// <para>
/// The encoder mirrors the decoder's macroblock work buffer exactly and reuses <see cref="Vp8Dsp"/> for
/// prediction and for the inverse transforms, so the reconstruction it predicts from is by construction the
/// one the decoder will produce. Intra prediction inside a key frame reads the unfiltered reconstruction,
/// so the in-loop deblocking filter - which only affects the frame that is handed to the caller and,
/// in a video stream, later inter frames - cannot make the two drift apart.
/// </para>
/// <para>
/// The frame uses a single segment and a single token partition. Mode decision is either a plain
/// sum-of-squared-error choice or a rate-distortion choice depending on the requested effort.
/// </para>
/// </remarks>
internal sealed class Vp8Encoder : IVp8FrameEncoder
{
    private const int Bps = Vp8Dsp.Bps;
    private const int YuvSize = (Bps * 17) + (Bps * 9);
    private const int YOff = Bps + 8;
    private const int UOff = YOff + (Bps * 16) + Bps;
    private const int VOff = UOff + 16;

    /// <summary>Sixteen luma blocks, four U blocks, four V blocks and the second-order luma DC block.</summary>
    private const int LevelsPerMb = 25 * 16;

    /// <summary>Offset of the second-order luma DC block inside a macroblock's level store.</summary>
    private const int Y2Off = 24 * 16;

    /// <summary>Offset of the first chroma block inside a macroblock's level store.</summary>
    private const int UvOff = 16 * 16;

    /// <summary>Largest frame dimension the 14-bit size fields can express.</summary>
    private const int MaxDimension = 16383;

    /// <summary>Loop filter strength on libwebp's 0..100 scale; 60 is its default.</summary>
    private const int FilterStrength = 60;

    private static readonly byte[][] BModePaths = BuildBModePaths();
    private static readonly ushort[] BModeCostTable = BuildBModeCosts();
    private static readonly ushort[] I16ModeCostTable = BuildI16ModeCosts();
    private static readonly ushort[] UvModeCostTable = BuildUvModeCosts();

    private readonly byte[] work = new byte[YuvSize];
    private readonly byte[] source = new byte[YuvSize];
    private readonly byte[] savedLuma = new byte[16 * 16];
    private readonly byte[] bestI16Luma = new byte[16 * 16];
    private readonly byte[] savedChroma = new byte[2 * 8 * 8];
    private readonly byte[] savedBlock = new byte[16];
    private readonly short[] bestI16Levels = new short[LevelsPerMb];
    private readonly short[] bestUvLevels = new short[LevelsPerMb];
    private readonly short[] transformed = new short[16 * 16];
    private readonly short[] dcTransformed = new short[16];
    private readonly short[] blockCoeffs = new short[16];
    private readonly short[] i16Levels = new short[LevelsPerMb];
    private readonly short[] i4Levels = new short[LevelsPerMb];
    private readonly short[] uvLevels = new short[LevelsPerMb];
    private readonly short[] blockLevels = new short[16];
    private readonly byte[] i4Modes = new byte[16];
    private readonly byte[] leftNz = new byte[9];
    private readonly byte[] intraLeft = new byte[4];
    private readonly byte[] probas = new byte[Vp8Cost.ProbaCount];
    private readonly bool[] probaUpdates = new bool[Vp8Cost.ProbaCount];
    private readonly ushort[] levelCosts = new ushort[Vp8Cost.LevelTableSize];
    private readonly uint[] stats = new uint[Vp8Cost.ProbaCount];
    private readonly Vp8EncoderSegment segment = new Vp8EncoderSegment();

    private byte[] planeY = Array.Empty<byte>();
    private byte[] planeU = Array.Empty<byte>();
    private byte[] planeV = Array.Empty<byte>();
    private byte[] topY = Array.Empty<byte>();
    private byte[] topU = Array.Empty<byte>();
    private byte[] topV = Array.Empty<byte>();
    private byte[] topNz = Array.Empty<byte>();
    private byte[] intraTop = Array.Empty<byte>();
    private byte[] mbModes = Array.Empty<byte>();
    private byte[] mbUvMode = Array.Empty<byte>();
    private bool[] mbIsI4x4 = Array.Empty<bool>();
    private bool[] mbSkip = Array.Empty<bool>();
    private short[] levels = Array.Empty<short>();

    private int width;
    private int height;
    private int uvWidth;
    private int uvHeight;
    private int mbW;
    private int mbH;
    private int i4ModeCount;
    private bool useRateDistortion;
    private bool useSkipProba;
    private int skipProba;
    private int filterLevel;

    /// <inheritdoc/>
    public byte[] EncodeKeyFrame(
        ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int width, int height, int quality, int method)
    {
        if (width <= 0 || width > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, $"VP8 frame width must be between 1 and {MaxDimension}.");
        }

        if (height <= 0 || height > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, $"VP8 frame height must be between 1 and {MaxDimension}.");
        }

        this.width = width;
        this.height = height;
        this.uvWidth = (width + 1) / 2;
        this.uvHeight = (height + 1) / 2;
        if (y.Length < width * height)
        {
            throw new ArgumentException("The luma plane is smaller than the frame.", nameof(y));
        }

        if (u.Length < this.uvWidth * this.uvHeight)
        {
            throw new ArgumentException("The U plane is smaller than half the frame.", nameof(u));
        }

        if (v.Length < this.uvWidth * this.uvHeight)
        {
            throw new ArgumentException("The V plane is smaller than half the frame.", nameof(v));
        }

        method = Math.Clamp(method, 0, 6);

        // Effort ladder: the cheapest level looks only at the four 4x4 modes that mirror the 16x16 ones,
        // level 1 opens the full set, and level 2 and above turn the decision from plain distortion into
        // rate-distortion. Levels above 2 behave as level 2: a greedy coefficient refinement pass was
        // written and measured for them, and it lost to the plain quantizer, so it is not used.
        this.i4ModeCount = method == 0 ? 4 : 10;
        this.useRateDistortion = method >= 2;

        this.planeY = y[..(width * height)].ToArray();
        this.planeU = u[..(this.uvWidth * this.uvHeight)].ToArray();
        this.planeV = v[..(this.uvWidth * this.uvHeight)].ToArray();

        this.Initialize(quality);
        this.EncodeMacroblocks();
        this.FinalizeSkipProbability();
        this.CollectTokenStatistics();
        this.FinalizeTokenProbabilities();
        return this.Assemble();
    }

    private static int CheckMode(int mbX, int mbY, int mode)
    {
        if (mode != Vp8Decoder.DcPred)
        {
            return mode;
        }

        return mbX == 0
            ? mbY == 0 ? Vp8Decoder.DcPredNoTopLeft : Vp8Decoder.DcPredNoLeft
            : mbY == 0 ? Vp8Decoder.DcPredNoTop : Vp8Decoder.DcPred;
    }

    /// <summary>Maps the caller's 1..100 quality onto a quantizer index, matching libwebp's cube-root curve.</summary>
    private static int QualityToQuantIndex(int quality)
    {
        double c = Math.Clamp(quality, 0, 100) / 100.0;
        double linear = c < 0.75 ? c * (2.0 / 3.0) : (2.0 * c) - 1.0;
        double compression = Math.Pow(linear, 1.0 / 3.0);
        return Math.Clamp((int)(127.0 * (1.0 - compression)), 0, 127);
    }

    /// <summary>
    /// Picks a deblocking strength for the quantizer. A step of <c>delta</c> at a block edge passes the
    /// normal filter's threshold test when <c>4 * delta &lt;= 2 * level + inner + 4</c>; with sharpness zero
    /// the inner limit equals the level, which gives the linear rule below.
    /// </summary>
    private static int FilterLevelForQuant(int quantIndex)
    {
        int qStep = Vp8EncoderTables.AcTable[quantIndex] >> 2;
        int baseStrength = ((4 * qStep) - 4) / 3;
        if (baseStrength <= 0)
        {
            return 0;
        }

        int level = baseStrength * (5 * FilterStrength) / 256;
        return level < 2 ? 0 : Math.Min(level, 63);
    }

    private static void ImportPlane(byte[] plane, int planeWidth, int planeHeight, int x0, int y0, int size, byte[] dst, int dstOff)
    {
        int available = Math.Min(size, planeWidth - x0);
        for (int j = 0; j < size; j++)
        {
            int row = Math.Min(y0 + j, planeHeight - 1) * planeWidth;
            int d = dstOff + (j * Bps);
            for (int i = 0; i < available; i++)
            {
                dst[d + i] = plane[row + x0 + i];
            }

            byte edge = dst[d + available - 1];
            for (int i = available; i < size; i++)
            {
                dst[d + i] = edge;
            }
        }
    }

    private static byte[][] BuildBModePaths()
    {
        var result = new byte[10][];
        var path = new List<byte>();
        WalkBModeTree(0, path, result);
        return result;
    }

    private static void WalkBModeTree(int node, List<byte> path, byte[][] result)
    {
        ReadOnlySpan<sbyte> tree = Vp8EncoderTables.BModeTree;
        for (int bit = 0; bit < 2; bit++)
        {
            int next = tree[node + bit];
            path.Add((byte)((node & ~1) | bit));
            if (next > 0)
            {
                WalkBModeTree(next, path, result);
            }
            else
            {
                result[-next] = path.ToArray();
            }

            path.RemoveAt(path.Count - 1);
        }
    }

    private static ushort[] BuildBModeCosts()
    {
        ReadOnlySpan<byte> probas = Vp8EncoderTables.BModesProba;
        var table = new ushort[10 * 10 * 10];
        for (int above = 0; above < 10; above++)
        {
            for (int left = 0; left < 10; left++)
            {
                int probaOff = ((above * 10) + left) * 9;
                for (int mode = 0; mode < 10; mode++)
                {
                    int cost = 0;
                    foreach (byte step in BModePaths[mode])
                    {
                        cost += Vp8Cost.Bit(step & 1, probas[probaOff + (step >> 1)]);
                    }

                    table[(((above * 10) + left) * 10) + mode] = (ushort)cost;
                }
            }
        }

        return table;
    }

    private static ushort[] BuildI16ModeCosts()
    {
        // The i16/i4 flag is coded with probability 145, then the mode with 156, 163 and 128.
        int flag = Vp8Cost.Bit(1, 145);
        var table = new ushort[4];
        table[Vp8Decoder.DcPred] = (ushort)(flag + Vp8Cost.Bit(0, 156) + Vp8Cost.Bit(0, 163));
        table[Vp8Decoder.VPred] = (ushort)(flag + Vp8Cost.Bit(0, 156) + Vp8Cost.Bit(1, 163));
        table[Vp8Decoder.HPred] = (ushort)(flag + Vp8Cost.Bit(1, 156) + Vp8Cost.Bit(0, 128));
        table[Vp8Decoder.TmPred] = (ushort)(flag + Vp8Cost.Bit(1, 156) + Vp8Cost.Bit(1, 128));
        return table;
    }

    private static ushort[] BuildUvModeCosts()
    {
        var table = new ushort[4];
        table[Vp8Decoder.DcPred] = (ushort)Vp8Cost.Bit(0, 142);
        table[Vp8Decoder.VPred] = (ushort)(Vp8Cost.Bit(1, 142) + Vp8Cost.Bit(0, 114));
        table[Vp8Decoder.HPred] = (ushort)(Vp8Cost.Bit(1, 142) + Vp8Cost.Bit(1, 114) + Vp8Cost.Bit(0, 183));
        table[Vp8Decoder.TmPred] = (ushort)(Vp8Cost.Bit(1, 142) + Vp8Cost.Bit(1, 114) + Vp8Cost.Bit(1, 183));
        return table;
    }

    private void Initialize(int quality)
    {
        this.mbW = (this.width + 15) >> 4;
        this.mbH = (this.height + 15) >> 4;
        int count = this.mbW * this.mbH;

        this.segment.QuantIndex = QualityToQuantIndex(quality);
        this.segment.Setup();
        this.filterLevel = FilterLevelForQuant(this.segment.QuantIndex);

        Vp8EncoderTables.CoeffsProba0.CopyTo(this.probas);
        Vp8Cost.BuildLevelCosts(this.probas, this.levelCosts);

        this.topY = new byte[this.mbW * 16];
        this.topU = new byte[this.mbW * 8];
        this.topV = new byte[this.mbW * 8];
        this.topNz = new byte[this.mbW * 9];
        this.intraTop = new byte[this.mbW * 4];
        this.mbModes = new byte[count * 16];
        this.mbUvMode = new byte[count];
        this.mbIsI4x4 = new bool[count];
        this.mbSkip = new bool[count];
        if ((long)count * LevelsPerMb > int.MaxValue)
        {
            throw new InvalidOperationException("The frame has too many macroblocks to encode.");
        }

        this.levels = new short[count * LevelsPerMb];
    }

    // ----- Macroblock loop -----

    private void EncodeMacroblocks()
    {
        for (int mbY = 0; mbY < this.mbH; mbY++)
        {
            this.StartRow(mbY);
            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                this.SetupContext(mbX, mbY);
                this.ImportSource(mbX, mbY);
                this.EncodeMacroblock(mbX, mbY);
                this.SaveTopContext(mbX);
            }
        }
    }

    /// <summary>Initialises the row's prediction context exactly as the decoder does at the start of a row.</summary>
    private void StartRow(int mbY)
    {
        byte[] buf = this.work;
        Array.Clear(this.leftNz);
        Array.Clear(this.intraLeft);

        for (int j = 0; j < 16; j++)
        {
            buf[YOff + (j * Bps) - 1] = 129;
        }

        for (int j = 0; j < 8; j++)
        {
            buf[UOff + (j * Bps) - 1] = 129;
            buf[VOff + (j * Bps) - 1] = 129;
        }

        if (mbY > 0)
        {
            buf[YOff - Bps - 1] = 129;
            buf[UOff - Bps - 1] = 129;
            buf[VOff - Bps - 1] = 129;
        }
        else
        {
            buf.AsSpan(YOff - Bps - 1, 16 + 4 + 1).Fill(127);
            buf.AsSpan(UOff - Bps - 1, 8 + 1).Fill(127);
            buf.AsSpan(VOff - Bps - 1, 8 + 1).Fill(127);
        }
    }

    /// <summary>Rotates the previous macroblock into the left context and pulls in the row above.</summary>
    private void SetupContext(int mbX, int mbY)
    {
        byte[] buf = this.work;
        if (mbX > 0)
        {
            for (int j = -1; j < 16; j++)
            {
                Copy4(buf, YOff + (j * Bps) + 12, YOff + (j * Bps) - 4);
            }

            for (int j = -1; j < 8; j++)
            {
                Copy4(buf, UOff + (j * Bps) + 4, UOff + (j * Bps) - 4);
                Copy4(buf, VOff + (j * Bps) + 4, VOff + (j * Bps) - 4);
            }
        }

        if (mbY > 0)
        {
            this.topY.AsSpan(mbX * 16, 16).CopyTo(buf.AsSpan(YOff - Bps, 16));
            this.topU.AsSpan(mbX * 8, 8).CopyTo(buf.AsSpan(UOff - Bps, 8));
            this.topV.AsSpan(mbX * 8, 8).CopyTo(buf.AsSpan(VOff - Bps, 8));
        }

        // The above-right samples of the right-most sub-block column always come from the row above the
        // macroblock, replicated downwards; the decoder builds them the same way before predicting.
        int topRight = YOff - Bps + 16;
        if (mbY > 0)
        {
            if (mbX >= this.mbW - 1)
            {
                buf.AsSpan(topRight, 4).Fill(this.topY[(mbX * 16) + 15]);
            }
            else
            {
                this.topY.AsSpan((mbX + 1) * 16, 4).CopyTo(buf.AsSpan(topRight, 4));
            }
        }

        for (int k = 1; k <= 3; k++)
        {
            buf.AsSpan(topRight, 4).CopyTo(buf.AsSpan(topRight + (4 * k * Bps), 4));
        }
    }

    private static void Copy4(byte[] buffer, int from, int to)
    {
        buffer[to] = buffer[from];
        buffer[to + 1] = buffer[from + 1];
        buffer[to + 2] = buffer[from + 2];
        buffer[to + 3] = buffer[from + 3];
    }

    private void ImportSource(int mbX, int mbY)
    {
        ImportPlane(this.planeY, this.width, this.height, mbX * 16, mbY * 16, 16, this.source, YOff);
        ImportPlane(this.planeU, this.uvWidth, this.uvHeight, mbX * 8, mbY * 8, 8, this.source, UOff);
        ImportPlane(this.planeV, this.uvWidth, this.uvHeight, mbX * 8, mbY * 8, 8, this.source, VOff);
    }

    private void SaveTopContext(int mbX)
    {
        this.work.AsSpan(YOff + (15 * Bps), 16).CopyTo(this.topY.AsSpan(mbX * 16, 16));
        this.work.AsSpan(UOff + (7 * Bps), 8).CopyTo(this.topU.AsSpan(mbX * 8, 8));
        this.work.AsSpan(VOff + (7 * Bps), 8).CopyTo(this.topV.AsSpan(mbX * 8, 8));
    }

    private void EncodeMacroblock(int mbX, int mbY)
    {
        int mb = (mbY * this.mbW) + mbX;
        long scoreI16 = this.PickBestIntra16(mbX, mbY, out int mode16);
        this.SaveLuma();

        long scoreI4 = this.PickBestIntra4(mbX, mbY);
        bool useI4 = scoreI4 < scoreI16;
        if (!useI4)
        {
            this.RestoreLuma();
        }

        this.mbIsI4x4[mb] = useI4;
        short[] lumaLevels = useI4 ? this.i4Levels : this.i16Levels;
        if (useI4)
        {
            this.i4Modes.CopyTo(this.mbModes.AsSpan(mb * 16, 16));
            for (int x = 0; x < 4; x++)
            {
                this.intraTop[(mbX * 4) + x] = this.i4Modes[12 + x];
            }

            for (int y = 0; y < 4; y++)
            {
                this.intraLeft[y] = this.i4Modes[(y * 4) + 3];
            }
        }
        else
        {
            this.mbModes[mb * 16] = (byte)mode16;
            for (int i = 0; i < 4; i++)
            {
                this.intraTop[(mbX * 4) + i] = (byte)mode16;
                this.intraLeft[i] = (byte)mode16;
            }
        }

        this.mbUvMode[mb] = (byte)this.PickBestUv(mbX, mbY);

        int off = mb * LevelsPerMb;
        lumaLevels.AsSpan(0, UvOff).CopyTo(this.levels.AsSpan(off, UvOff));
        lumaLevels.AsSpan(Y2Off, 16).CopyTo(this.levels.AsSpan(off + Y2Off, 16));
        this.uvLevels.AsSpan(UvOff, 8 * 16).CopyTo(this.levels.AsSpan(off + UvOff, 8 * 16));
        if (useI4)
        {
            this.levels.AsSpan(off + Y2Off, 16).Clear();
        }

        bool skip = true;
        for (int i = 0; i < LevelsPerMb && skip; i++)
        {
            skip = this.levels[off + i] == 0;
        }

        this.mbSkip[mb] = skip;
        this.UpdateNzContexts(mbX, useI4, this.levels, off);
    }

    private void SaveLuma()
    {
        for (int j = 0; j < 16; j++)
        {
            this.work.AsSpan(YOff + (j * Bps), 16).CopyTo(this.savedLuma.AsSpan(j * 16, 16));
        }
    }

    private void RestoreLuma()
    {
        for (int j = 0; j < 16; j++)
        {
            this.savedLuma.AsSpan(j * 16, 16).CopyTo(this.work.AsSpan(YOff + (j * Bps), 16));
        }
    }

    // ----- Mode search -----

    private long PickBestIntra16(int mbX, int mbY, out int bestMode)
    {
        ReadOnlySpan<ushort> scan = Vp8EncoderTables.Scan;
        int lambda = this.segment.LambdaI16;
        long best = long.MaxValue;
        long bestModeScore = long.MaxValue;
        bestMode = Vp8Decoder.DcPred;

        for (int mode = 0; mode < 4; mode++)
        {
            Vp8Dsp.PredictBlock(this.work, YOff, 16, CheckMode(mbX, mbY, mode));
            for (int n = 0; n < 16; n++)
            {
                Vp8EncoderDsp.FTransform(this.source, YOff + scan[n], this.work, YOff + scan[n], this.transformed, n * 16);
            }

            Vp8EncoderDsp.FTransformWht(this.transformed, this.dcTransformed);
            this.segment.Y2.Quantize(this.dcTransformed, 0, this.i16Levels, Y2Off);
            for (int n = 0; n < 16; n++)
            {
                // The DC of every sub-block travels through the second-order block instead.
                this.transformed[n * 16] = 0;
                this.segment.Y1.Quantize(this.transformed, n * 16, this.i16Levels, n * 16);
            }

            Vp8Dsp.TransformWht(this.dcTransformed, this.transformed);
            for (int n = 0; n < 16; n++)
            {
                Vp8Dsp.TransformOne(this.transformed, n * 16, this.work, YOff + scan[n]);
            }

            long distortion = Vp8EncoderDsp.Sse(this.source, YOff, this.work, YOff, 16);
            int header = I16ModeCostTable[mode];
            int rate = this.useRateDistortion ? this.CostLuma16(mbX, this.i16Levels) : 0;
            long score = ((long)(rate + header) * lambda) + (256 * distortion);
            if (score < best)
            {
                best = score;
                bestMode = mode;
                bestModeScore = ((long)(rate + header) * this.segment.LambdaMode) + (256 * distortion);
                for (int j = 0; j < 16; j++)
                {
                    this.work.AsSpan(YOff + (j * Bps), 16).CopyTo(this.bestI16Luma.AsSpan(j * 16, 16));
                }

                this.i16Levels.AsSpan(0, UvOff).CopyTo(this.bestI16Levels.AsSpan(0, UvOff));
                this.i16Levels.AsSpan(Y2Off, 16).CopyTo(this.bestI16Levels.AsSpan(Y2Off, 16));
            }
        }

        for (int j = 0; j < 16; j++)
        {
            this.bestI16Luma.AsSpan(j * 16, 16).CopyTo(this.work.AsSpan(YOff + (j * Bps), 16));
        }

        this.bestI16Levels.AsSpan(0, UvOff).CopyTo(this.i16Levels.AsSpan(0, UvOff));
        this.bestI16Levels.AsSpan(Y2Off, 16).CopyTo(this.i16Levels.AsSpan(Y2Off, 16));
        return bestModeScore;
    }

    private long PickBestIntra4(int mbX, int mbY)
    {
        ReadOnlySpan<ushort> scan = Vp8EncoderTables.Scan;
        int lambda = this.segment.LambdaI4;
        Span<byte> top = stackalloc byte[4];
        Span<byte> left = stackalloc byte[4];
        this.topNz.AsSpan(mbX * 9, 4).CopyTo(top);
        this.leftNz.AsSpan(0, 4).CopyTo(left);

        long totalDistortion = 0;
        int totalRate = 0;
        int totalHeader = Vp8Cost.Bit(0, 145); // The flag that selects 4x4 prediction.

        for (int n = 0; n < 16; n++)
        {
            int x = n & 3;
            int y = n >> 2;
            int dst = YOff + scan[n];
            int above = y == 0 ? this.intraTop[(mbX * 4) + x] : this.i4Modes[n - 4];
            int leftMode = x == 0 ? this.intraLeft[y] : this.i4Modes[n - 1];
            int costOff = ((above * 10) + leftMode) * 10;
            int ctx = top[x] + left[y];

            long bestScore = long.MaxValue;
            int bestMode = 0;
            int bestLast = -1;
            long bestDistortion = 0;
            int bestRate = 0;

            for (int mode = 0; mode < this.i4ModeCount; mode++)
            {
                Vp8Dsp.PredictLuma4(this.work, dst, mode);
                Vp8EncoderDsp.FTransform(this.source, dst, this.work, dst, this.blockCoeffs, 0);
                int last = this.segment.Y1.Quantize(this.blockCoeffs, 0, this.blockLevels, 0);
                Vp8Dsp.TransformOne(this.blockCoeffs, 0, this.work, dst);

                long distortion = Vp8EncoderDsp.Sse(this.source, dst, this.work, dst, 4);
                int header = BModeCostTable[costOff + mode];
                int rate = this.useRateDistortion
                    ? Vp8Cost.ResidualCost(this.blockLevels, 0, 0, last, 3, ctx, this.probas, this.levelCosts)
                    : 0;
                long score = ((long)(rate + header) * lambda) + (256 * distortion);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestMode = mode;
                    bestLast = last;
                    bestDistortion = distortion;
                    bestRate = rate;
                    this.blockLevels.CopyTo(this.i4Levels.AsSpan(n * 16, 16));
                    for (int j = 0; j < 4; j++)
                    {
                        this.work.AsSpan(dst + (j * Bps), 4).CopyTo(this.savedBlock.AsSpan(j * 4, 4));
                    }
                }
            }

            for (int j = 0; j < 4; j++)
            {
                this.savedBlock.AsSpan(j * 4, 4).CopyTo(this.work.AsSpan(dst + (j * Bps), 4));
            }

            this.i4Modes[n] = (byte)bestMode;
            top[x] = left[y] = (byte)(bestLast >= 0 ? 1 : 0);
            totalDistortion += bestDistortion;
            totalRate += bestRate;
            totalHeader += BModeCostTable[costOff + bestMode];
        }

        return ((long)(totalRate + totalHeader) * this.segment.LambdaMode) + (256 * totalDistortion);
    }

    private int PickBestUv(int mbX, int mbY)
    {
        int lambda = this.segment.LambdaUv;
        long best = long.MaxValue;
        int bestMode = Vp8Decoder.DcPred;

        for (int mode = 0; mode < 4; mode++)
        {
            int predMode = CheckMode(mbX, mbY, mode);
            Vp8Dsp.PredictBlock(this.work, UOff, 8, predMode);
            Vp8Dsp.PredictBlock(this.work, VOff, 8, predMode);
            for (int ch = 0; ch < 2; ch++)
            {
                int planeOff = ch == 0 ? UOff : VOff;
                for (int b = 0; b < 4; b++)
                {
                    int off = planeOff + ((b & 1) * 4) + ((b >> 1) * 4 * Bps);
                    Vp8EncoderDsp.FTransform(this.source, off, this.work, off, this.blockCoeffs, 0);
                    this.segment.Uv.Quantize(this.blockCoeffs, 0, this.uvLevels, UvOff + (((ch * 4) + b) * 16));
                    Vp8Dsp.TransformOne(this.blockCoeffs, 0, this.work, off);
                }
            }

            long distortion = Vp8EncoderDsp.Sse(this.source, UOff, this.work, UOff, 8)
                + Vp8EncoderDsp.Sse(this.source, VOff, this.work, VOff, 8);
            int header = UvModeCostTable[mode];
            int rate = this.useRateDistortion ? this.CostUv(mbX, this.uvLevels) : 0;
            long score = ((long)(rate + header) * lambda) + (256 * distortion);
            if (score < best)
            {
                best = score;
                bestMode = mode;
                this.SaveChroma();
                this.uvLevels.AsSpan(UvOff, 8 * 16).CopyTo(this.bestUvLevels.AsSpan(UvOff, 8 * 16));
            }
        }

        this.RestoreChroma();
        this.bestUvLevels.AsSpan(UvOff, 8 * 16).CopyTo(this.uvLevels.AsSpan(UvOff, 8 * 16));
        return bestMode;
    }

    private void SaveChroma()
    {
        for (int j = 0; j < 8; j++)
        {
            this.work.AsSpan(UOff + (j * Bps), 8).CopyTo(this.savedChroma.AsSpan(j * 8, 8));
            this.work.AsSpan(VOff + (j * Bps), 8).CopyTo(this.savedChroma.AsSpan(64 + (j * 8), 8));
        }
    }

    private void RestoreChroma()
    {
        for (int j = 0; j < 8; j++)
        {
            this.savedChroma.AsSpan(j * 8, 8).CopyTo(this.work.AsSpan(UOff + (j * Bps), 8));
            this.savedChroma.AsSpan(64 + (j * 8), 8).CopyTo(this.work.AsSpan(VOff + (j * Bps), 8));
        }
    }

    // ----- Rate estimation over a whole macroblock -----

    private int CostLuma16(int mbX, short[] lev)
    {
        Span<byte> top = stackalloc byte[9];
        Span<byte> left = stackalloc byte[9];
        this.topNz.AsSpan(mbX * 9, 9).CopyTo(top);
        this.leftNz.AsSpan(0, 9).CopyTo(left);

        int last = Vp8Cost.LastNonZero(lev, Y2Off);
        int rate = Vp8Cost.ResidualCost(lev, Y2Off, 0, last, 1, top[8] + left[8], this.probas, this.levelCosts);
        top[8] = left[8] = (byte)(last >= 0 ? 1 : 0);

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int off = ((y * 4) + x) * 16;
                last = Vp8Cost.LastNonZero(lev, off);
                rate += Vp8Cost.ResidualCost(lev, off, 1, last, 0, top[x] + left[y], this.probas, this.levelCosts);
                top[x] = left[y] = (byte)(last >= 0 ? 1 : 0);
            }
        }

        return rate;
    }

    private int CostUv(int mbX, short[] lev)
    {
        Span<byte> top = stackalloc byte[4];
        Span<byte> left = stackalloc byte[4];
        this.topNz.AsSpan((mbX * 9) + 4, 4).CopyTo(top);
        this.leftNz.AsSpan(4, 4).CopyTo(left);

        int rate = 0;
        for (int ch = 0; ch < 4; ch += 2)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    int off = UvOff + (((ch * 2) + x + (y * 2)) * 16);
                    int last = Vp8Cost.LastNonZero(lev, off);
                    rate += Vp8Cost.ResidualCost(lev, off, 0, last, 2, top[ch + x] + left[ch + y], this.probas, this.levelCosts);
                    top[ch + x] = left[ch + y] = (byte)(last >= 0 ? 1 : 0);
                }
            }
        }

        return rate;
    }

    private void UpdateNzContexts(int mbX, bool isI4x4, short[] lev, int off)
    {
        int t = mbX * 9;
        if (!isI4x4)
        {
            byte nz = (byte)(Vp8Cost.LastNonZero(lev, off + Y2Off) >= 0 ? 1 : 0);
            this.topNz[t + 8] = this.leftNz[8] = nz;
        }

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                byte nz = (byte)(Vp8Cost.LastNonZero(lev, off + (((y * 4) + x) * 16)) >= 0 ? 1 : 0);
                this.topNz[t + x] = this.leftNz[y] = nz;
            }
        }

        for (int ch = 0; ch < 4; ch += 2)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    byte nz = (byte)(Vp8Cost.LastNonZero(lev, off + UvOff + (((ch * 2) + x + (y * 2)) * 16)) >= 0 ? 1 : 0);
                    this.topNz[t + 4 + ch + x] = this.leftNz[4 + ch + y] = nz;
                }
            }
        }
    }

    // ----- Probability finalisation -----

    private void FinalizeSkipProbability()
    {
        int total = this.mbW * this.mbH;
        int skipped = 0;
        foreach (bool s in this.mbSkip)
        {
            if (s)
            {
                skipped++;
            }
        }

        this.skipProba = total > 0 ? (total - skipped) * 255 / total : 255;
        this.useSkipProba = this.skipProba < 250;
    }

    private void CollectTokenStatistics()
    {
        Array.Clear(this.stats);
        var recorder = new Vp8TokenRecorder(this.stats);
        this.WalkResiduals(ref recorder);
    }

    private void FinalizeTokenProbabilities()
    {
        ReadOnlySpan<byte> updateProba = Vp8EncoderTables.CoeffsUpdateProba;
        ReadOnlySpan<byte> defaults = Vp8EncoderTables.CoeffsProba0;
        for (int i = 0; i < Vp8Cost.ProbaCount; i++)
        {
            uint packed = this.stats[i];
            int ones = (int)(packed & 0xffff);
            int total = (int)(packed >> 16);
            int oldP = defaults[i];
            int newP = ones > 0 ? 255 - (ones * 255 / total) : 255;
            int oldCost = BranchCost(ones, total, oldP) + Vp8Cost.Bit(0, updateProba[i]);
            int newCost = BranchCost(ones, total, newP) + Vp8Cost.Bit(1, updateProba[i]) + (8 * 256);
            bool update = oldCost > newCost;
            this.probaUpdates[i] = update;
            this.probas[i] = (byte)(update ? newP : oldP);
        }
    }

    private static int BranchCost(int ones, int total, int proba)
        => (ones * Vp8Cost.Bit(1, proba)) + ((total - ones) * Vp8Cost.Bit(0, proba));

    // ----- Residual traversal shared by the statistics and the writing pass -----

    private void WalkResiduals<TSink>(ref TSink sink)
        where TSink : struct, IVp8TokenSink
    {
        Array.Clear(this.topNz);
        short[] lev = this.levels;
        for (int mbY = 0; mbY < this.mbH; mbY++)
        {
            Array.Clear(this.leftNz);
            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                int mb = (mbY * this.mbW) + mbX;
                int t = mbX * 9;
                bool isI4x4 = this.mbIsI4x4[mb];
                if (this.useSkipProba && this.mbSkip[mb])
                {
                    for (int i = 0; i < 8; i++)
                    {
                        this.topNz[t + i] = 0;
                        this.leftNz[i] = 0;
                    }

                    if (!isI4x4)
                    {
                        this.topNz[t + 8] = 0;
                        this.leftNz[8] = 0;
                    }

                    continue;
                }

                int off = mb * LevelsPerMb;
                int first;
                int type;
                if (!isI4x4)
                {
                    int last = Vp8Cost.LastNonZero(lev, off + Y2Off);
                    int nz = Vp8EncoderTokens.PutCoeffs(
                        ref sink, this.topNz[t + 8] + this.leftNz[8], 1, 0, lev, off + Y2Off, last);
                    this.topNz[t + 8] = this.leftNz[8] = (byte)nz;
                    first = 1;
                    type = 0;
                }
                else
                {
                    first = 0;
                    type = 3;
                }

                for (int y = 0; y < 4; y++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        int blockOff = off + (((y * 4) + x) * 16);
                        int last = Vp8Cost.LastNonZero(lev, blockOff);
                        int nz = Vp8EncoderTokens.PutCoeffs(
                            ref sink, this.topNz[t + x] + this.leftNz[y], type, first, lev, blockOff, last);
                        this.topNz[t + x] = this.leftNz[y] = (byte)nz;
                    }
                }

                for (int ch = 0; ch < 4; ch += 2)
                {
                    for (int y = 0; y < 2; y++)
                    {
                        for (int x = 0; x < 2; x++)
                        {
                            int blockOff = off + UvOff + (((ch * 2) + x + (y * 2)) * 16);
                            int last = Vp8Cost.LastNonZero(lev, blockOff);
                            int nz = Vp8EncoderTokens.PutCoeffs(
                                ref sink, this.topNz[t + 4 + ch + x] + this.leftNz[4 + ch + y], 2, 0, lev, blockOff, last);
                            this.topNz[t + 4 + ch + x] = this.leftNz[4 + ch + y] = (byte)nz;
                        }
                    }
                }
            }
        }
    }

    // ----- Bitstream assembly -----

    private byte[] Assemble()
    {
        byte[] partition0 = this.WriteFirstPartition();
        byte[] tokens = this.WriteTokenPartition();
        if (partition0.Length >= 1 << 19)
        {
            throw new InvalidOperationException("The VP8 first partition does not fit in the 19-bit size field.");
        }

        var frame = new byte[10 + partition0.Length + tokens.Length];
        uint tag = (uint)((0 << 0) | (0 << 1) | (1 << 4) | (partition0.Length << 5));
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        frame[3] = 0x9d;
        frame[4] = 0x01;
        frame[5] = 0x2a;
        frame[6] = (byte)this.width;
        frame[7] = (byte)(this.width >> 8);
        frame[8] = (byte)this.height;
        frame[9] = (byte)(this.height >> 8);
        partition0.CopyTo(frame, 10);
        tokens.CopyTo(frame, 10 + partition0.Length);
        return frame;
    }

    private byte[] WriteFirstPartition()
    {
        var bw = new Vp8BoolWriter(4096 + (this.mbW * this.mbH * 4));
        bw.PutFlag(false); // Colour space: YUV as defined by the specification.
        bw.PutFlag(false); // Clamping: the decoder must clamp.
        bw.PutFlag(false); // Segmentation disabled: a single segment.

        bw.PutFlag(false); // Normal (not simple) loop filter.
        bw.PutValue(this.filterLevel, 6);
        bw.PutValue(0, 3); // Sharpness.
        bw.PutFlag(false); // No loop filter delta adjustments.

        bw.PutValue(0, 2); // log2 of the token partition count: one partition.

        bw.PutValue(this.segment.QuantIndex, 7);
        for (int i = 0; i < 5; i++)
        {
            bw.PutOptionalSigned(0, 4); // No per-plane quantizer deltas.
        }

        bw.PutFlag(false); // refresh_entropy_probs.

        ReadOnlySpan<byte> updateProba = Vp8EncoderTables.CoeffsUpdateProba;
        for (int i = 0; i < Vp8Cost.ProbaCount; i++)
        {
            if (this.probaUpdates[i])
            {
                bw.PutBit(1, updateProba[i]);
                bw.PutValue(this.probas[i], 8);
            }
            else
            {
                bw.PutBit(0, updateProba[i]);
            }
        }

        bw.PutFlag(this.useSkipProba);
        if (this.useSkipProba)
        {
            bw.PutValue(this.skipProba, 8);
        }

        this.WriteModes(bw);
        return bw.Finish();
    }

    private void WriteModes(Vp8BoolWriter bw)
    {
        ReadOnlySpan<byte> bModesProba = Vp8EncoderTables.BModesProba;
        var top = new byte[this.mbW * 4];
        Span<byte> left = stackalloc byte[4];

        for (int mbY = 0; mbY < this.mbH; mbY++)
        {
            left.Clear();
            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                int mb = (mbY * this.mbW) + mbX;
                int topOff = mbX * 4;
                if (this.useSkipProba)
                {
                    bw.PutBit(this.mbSkip[mb] ? 1 : 0, this.skipProba);
                }

                bool isI4x4 = this.mbIsI4x4[mb];
                bw.PutBit(isI4x4 ? 0 : 1, 145);
                if (!isI4x4)
                {
                    int mode = this.mbModes[mb * 16];
                    if (mode == Vp8Decoder.DcPred)
                    {
                        bw.PutBit(0, 156);
                        bw.PutBit(0, 163);
                    }
                    else if (mode == Vp8Decoder.VPred)
                    {
                        bw.PutBit(0, 156);
                        bw.PutBit(1, 163);
                    }
                    else if (mode == Vp8Decoder.HPred)
                    {
                        bw.PutBit(1, 156);
                        bw.PutBit(0, 128);
                    }
                    else
                    {
                        bw.PutBit(1, 156);
                        bw.PutBit(1, 128);
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        top[topOff + i] = (byte)mode;
                        left[i] = (byte)mode;
                    }
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                    {
                        int leftMode = left[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int mode = this.mbModes[(mb * 16) + (y * 4) + x];
                            int probaOff = ((top[topOff + x] * 10) + leftMode) * 9;
                            foreach (byte step in BModePaths[mode])
                            {
                                bw.PutBit(step & 1, bModesProba[probaOff + (step >> 1)]);
                            }

                            top[topOff + x] = (byte)mode;
                            leftMode = mode;
                        }

                        left[y] = (byte)leftMode;
                    }
                }

                int uvMode = this.mbUvMode[mb];
                if (uvMode == Vp8Decoder.DcPred)
                {
                    bw.PutBit(0, 142);
                }
                else if (uvMode == Vp8Decoder.VPred)
                {
                    bw.PutBit(1, 142);
                    bw.PutBit(0, 114);
                }
                else if (uvMode == Vp8Decoder.HPred)
                {
                    bw.PutBit(1, 142);
                    bw.PutBit(1, 114);
                    bw.PutBit(0, 183);
                }
                else
                {
                    bw.PutBit(1, 142);
                    bw.PutBit(1, 114);
                    bw.PutBit(1, 183);
                }
            }
        }
    }

    private byte[] WriteTokenPartition()
    {
        var bw = new Vp8BoolWriter(65536);
        var writer = new Vp8TokenWriter(bw, this.probas);
        this.WalkResiduals(ref writer);
        return bw.Finish();
    }
}
