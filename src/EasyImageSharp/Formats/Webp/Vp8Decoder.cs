namespace EasyImageSharp.Formats.Webp;

/// <summary>The reconstructed luma and chroma planes of one VP8 key frame, in 4:2:0 sub-sampling.</summary>
internal sealed class Vp8Planes
{
    public Vp8Planes(int width, int height, int mbW, int mbH)
    {
        this.Width = width;
        this.Height = height;
        this.YStride = mbW * 16;
        this.UvStride = mbW * 8;
        this.Y = new byte[this.YStride * mbH * 16];
        this.U = new byte[this.UvStride * mbH * 8];
        this.V = new byte[this.UvStride * mbH * 8];
    }

    public int Width { get; }

    public int Height { get; }

    public int YStride { get; }

    public int UvStride { get; }

    public byte[] Y { get; }

    public byte[] U { get; }

    public byte[] V { get; }
}

/// <summary>
/// Decoder for a VP8 key frame (RFC 6386): frame, segment, filter and quantizer headers, the per-macroblock
/// intra prediction records of the first partition, the DCT token partitions, dequantisation, the exact
/// integer inverse transforms, every intra predictor and the in-loop deblocking filter. Inter frames are
/// rejected because a still WebP image is always a key frame.
/// </summary>
/// <remarks>
/// Reconstruction mirrors the reference decoder's macroblock work buffer so that intra prediction reads the
/// <em>unfiltered</em> neighbours, while the deblocking filter runs over the finished planes in raster order.
/// </remarks>
internal sealed class Vp8Decoder
{
    /// <summary>4x4 luma mode, numbered as in RFC 6386 section 11.2 (<c>intra_bmode</c>).</summary>
    public const int BDcPred = 0;

    /// <summary>4x4 luma "true motion" mode.</summary>
    public const int BTmPred = 1;

    /// <summary>4x4 luma vertical mode.</summary>
    public const int BVePred = 2;

    /// <summary>4x4 luma horizontal mode.</summary>
    public const int BHePred = 3;

    /// <summary>4x4 luma left-down diagonal mode.</summary>
    public const int BLdPred = 4;

    /// <summary>4x4 luma right-down diagonal mode.</summary>
    public const int BRdPred = 5;

    /// <summary>4x4 luma vertical-right mode.</summary>
    public const int BVrPred = 6;

    /// <summary>4x4 luma vertical-left mode.</summary>
    public const int BVlPred = 7;

    /// <summary>4x4 luma horizontal-down mode.</summary>
    public const int BHdPred = 8;

    /// <summary>4x4 luma horizontal-up mode.</summary>
    public const int BHuPred = 9;

    // The whole-block modes are numbered so that each one equals the 4x4 mode it stands for. RFC 6386
    // section 11.3 says a macroblock coded in a 16x16 mode presents that mode's 4x4 equivalent
    // (DC->B_DC, V->B_VE, H->B_HE, TM->B_TM) as the sub-block mode context of its neighbours, and sharing
    // the numbering makes that substitution a plain copy.

    /// <summary>Whole-block DC mode (16x16 luma or 8x8 chroma), equivalent to <see cref="BDcPred"/>.</summary>
    public const int DcPred = BDcPred;

    /// <summary>Whole-block vertical mode, equivalent to <see cref="BVePred"/>.</summary>
    public const int VPred = BVePred;

    /// <summary>Whole-block horizontal mode, equivalent to <see cref="BHePred"/>.</summary>
    public const int HPred = BHePred;

    /// <summary>Whole-block "true motion" mode, equivalent to <see cref="BTmPred"/>.</summary>
    public const int TmPred = BTmPred;

    /// <summary>DC mode at the top frame edge, where only the left neighbours exist.</summary>
    public const int DcPredNoTop = 5;

    /// <summary>DC mode at the left frame edge, where only the top neighbours exist.</summary>
    public const int DcPredNoLeft = 6;

    /// <summary>DC mode in the very first macroblock, where no neighbours exist.</summary>
    public const int DcPredNoTopLeft = 7;

    private const int Bps = Vp8Dsp.Bps;
    private const int YuvSize = (Bps * 17) + (Bps * 9);
    private const int YOff = Bps + 8;
    private const int UOff = YOff + (Bps * 16) + Bps;
    private const int VOff = UOff + 16;

    private const int NumMbSegments = 4;
    private const int NumRefLfDeltas = 4;
    private const int NumModeLfDeltas = 4;

    /// <summary>Size of one [band][context][proba] probability block, i.e. of one coefficient type.</summary>
    private const int TypeStride = Vp8Tables.NumBands * Vp8Tables.NumContexts * Vp8Tables.NumProbas;

    /// <summary>Offsets of the sixteen 4x4 luma sub-blocks inside the macroblock work buffer.</summary>
    private static ReadOnlySpan<ushort> Scan => new ushort[]
    {
        0, 4, 8, 12,
        (4 * Bps) + 0, (4 * Bps) + 4, (4 * Bps) + 8, (4 * Bps) + 12,
        (8 * Bps) + 0, (8 * Bps) + 4, (8 * Bps) + 8, (8 * Bps) + 12,
        (12 * Bps) + 0, (12 * Bps) + 4, (12 * Bps) + 8, (12 * Bps) + 12,
    };

    /// <summary>The sub-block intra mode tree (RFC 6386 section 11.2, <c>bmode_tree</c>).</summary>
    private static ReadOnlySpan<sbyte> BModeTree => new sbyte[]
    {
        -BDcPred, 2,
        -BTmPred, 4,
        -BVePred, 6,
        8, 12,
        -BHePred, 10,
        -BRdPred, -BVrPred,
        -BLdPred, 14,
        -BVlPred, 16,
        -BHdPred, -BHuPred,
    };

    private readonly byte[] data;
    private readonly byte[] yuv = new byte[YuvSize];
    private readonly short[] coeffs = new short[384];
    private readonly short[] dcCoeffs = new short[16];
    private readonly byte[] segmentProbas = new byte[3];
    private readonly byte[] probas = new byte[Vp8Tables.NumTypes * TypeStride];
    private readonly int[] segmentQuantizer = new int[NumMbSegments];
    private readonly int[] segmentFilterStrength = new int[NumMbSegments];
    private readonly int[] refLfDelta = new int[NumRefLfDeltas];
    private readonly int[] modeLfDelta = new int[NumModeLfDeltas];
    private readonly ushort[] quant = new ushort[NumMbSegments * 6];
    private readonly byte[] segmentLimit = new byte[NumMbSegments * 2];
    private readonly byte[] segmentInnerLevel = new byte[NumMbSegments * 2];
    private readonly byte[] segmentHevThresh = new byte[NumMbSegments * 2];

    private Vp8BitReader br = null!;
    private Vp8BitReader[] parts = Array.Empty<Vp8BitReader>();
    private int numPartsMinusOne;

    private int mbW;
    private int mbH;
    private bool useSegment;
    private bool updateSegmentMap;
    private bool absoluteSegmentDelta;
    private bool useSkipProba;
    private byte skipProba;
    private int filterType; // 0 = off, 1 = simple, 2 = normal.
    private int filterLevel;
    private int filterSharpness;
    private bool useLfDelta;

    // Per-macroblock records parsed from the first partition.
    private byte[] mbSegment = Array.Empty<byte>();
    private bool[] mbSkip = Array.Empty<bool>();
    private bool[] mbIsI4x4 = Array.Empty<bool>();
    private byte[] mbUvMode = Array.Empty<byte>();
    private byte[] mbModes = Array.Empty<byte>();

    // Per-macroblock filter parameters, filled while the residuals are parsed.
    private byte[] fLimit = Array.Empty<byte>();
    private byte[] fInnerLevel = Array.Empty<byte>();
    private byte[] fHevThresh = Array.Empty<byte>();
    private bool[] fInner = Array.Empty<bool>();

    // Non-zero coefficient contexts: bits 0-3 luma, 4-5 U, 6-7 V, with the Y2 flag kept separately.
    private byte[] nzTop = Array.Empty<byte>();
    private byte[] nzTopDc = Array.Empty<byte>();
    private byte nzLeft;
    private byte nzLeftDc;

    // Which of the current macroblock's sub-blocks carry coefficients (two bits per block).
    private uint blockNonZeroY;
    private uint blockNonZeroUv;

    // Unfiltered bottom row of the macroblock row above, used as the intra prediction context.
    private byte[] topY = Array.Empty<byte>();
    private byte[] topU = Array.Empty<byte>();
    private byte[] topV = Array.Empty<byte>();

    private Vp8Decoder(byte[] data) => this.data = data;

    /// <summary>Reads the width and height of a VP8 key frame without decoding it.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 10)
        {
            return false;
        }

        uint tag = (uint)(data[0] | (data[1] << 8) | (data[2] << 16));
        if ((tag & 1) != 0)
        {
            return false; // Inter frame: carries no dimensions.
        }

        if (data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a)
        {
            return false;
        }

        width = ((data[7] << 8) | data[6]) & 0x3fff;
        height = ((data[9] << 8) | data[8]) & 0x3fff;
        return width > 0 && height > 0;
    }

    /// <summary>Decodes a VP8 key frame into 4:2:0 planes.</summary>
    public static Vp8Planes Decode(byte[] data, int start, int length, DecoderOptions options)
        => new Vp8Decoder(data).DecodeFrame(start, length, options);

    private static int Clip(int v, int max) => v < 0 ? 0 : v > max ? max : v;

    private static int CheckMode(int mbX, int mbY, int mode)
    {
        if (mode != DcPred)
        {
            return mode;
        }

        return mbX == 0
            ? mbY == 0 ? DcPredNoTopLeft : DcPredNoLeft
            : mbY == 0 ? DcPredNoTop : DcPred;
    }

    private static void DoTransform(uint bits, short[] src, int srcOff, byte[] dst, int dstOff)
    {
        switch (bits >> 30)
        {
            case 3:
                Vp8Dsp.TransformOne(src, srcOff, dst, dstOff);
                break;
            case 2:
                Vp8Dsp.TransformAc3(src, srcOff, dst, dstOff);
                break;
            case 1:
                Vp8Dsp.TransformDc(src, srcOff, dst, dstOff);
                break;
            default:
                break;
        }
    }

    private static void DoUvTransform(uint bits, short[] src, int srcOff, byte[] dst, int dstOff)
    {
        if ((bits & 0xff) == 0)
        {
            return;
        }

        if ((bits & 0xaa) != 0)
        {
            Vp8Dsp.TransformUv(src, srcOff, dst, dstOff);
        }
        else
        {
            Vp8Dsp.TransformDcUv(src, srcOff, dst, dstOff);
        }
    }

    private static uint NzCodeBits(uint nzCoeffs, int nz, bool dcNz)
        => (nzCoeffs << 2) | (uint)(nz > 3 ? 3 : nz > 1 ? 2 : dcNz ? 1 : 0);

    private static void Copy4(byte[] buffer, int from, int to)
    {
        buffer[to] = buffer[from];
        buffer[to + 1] = buffer[from + 1];
        buffer[to + 2] = buffer[from + 2];
        buffer[to + 3] = buffer[from + 3];
    }

    private Vp8Planes DecodeFrame(int start, int length, DecoderOptions options)
    {
        if (length < 10)
        {
            throw new InvalidImageContentException("VP8 frame is too short to hold a key frame header.");
        }

        ReadOnlySpan<byte> span = this.data.AsSpan(start, length);
        uint tag = (uint)(span[0] | (span[1] << 8) | (span[2] << 16));
        bool keyFrame = (tag & 1) == 0;
        int profile = (int)((tag >> 1) & 7);
        bool show = ((tag >> 4) & 1) != 0;
        int firstPartLength = (int)(tag >> 5);
        if (!keyFrame)
        {
            throw new NotSupportedException("WebP: VP8 inter frames are not supported; a still WebP image must be a key frame.");
        }

        if (profile > 3)
        {
            throw new InvalidImageContentException($"Unknown VP8 profile {profile}.");
        }

        if (!show)
        {
            throw new InvalidImageContentException("VP8 key frame is marked as not shown.");
        }

        if (span[3] != 0x9d || span[4] != 0x01 || span[5] != 0x2a)
        {
            throw new InvalidImageContentException("Missing VP8 key frame start code.");
        }

        int width = ((span[7] << 8) | span[6]) & 0x3fff;
        int height = ((span[9] << 8) | span[8]) & 0x3fff;
        if (width == 0 || height == 0)
        {
            throw new InvalidImageContentException("VP8 key frame declares a zero dimension.");
        }

        options.EnsureFrameWithinLimits(width, height, "WebP");

        const int headerEnd = 10;
        if (firstPartLength > length - headerEnd)
        {
            throw new InvalidImageContentException("VP8 first partition extends past the end of the frame.");
        }

        this.mbW = (width + 15) >> 4;
        this.mbH = (height + 15) >> 4;
        this.br = new Vp8BitReader(this.data, start + headerEnd, firstPartLength);

        this.br.GetFlag(); // Colour space.
        this.br.GetFlag(); // Clamping type.
        this.ParseSegmentHeader();
        this.ParseFilterHeader();
        this.ParsePartitions(start + headerEnd + firstPartLength, length - headerEnd - firstPartLength);
        this.ParseQuant();
        this.br.GetFlag(); // refresh_entropy_probs: irrelevant for a single key frame.
        this.ParseProbabilities();
        this.PrecomputeFilterStrengths();

        if (this.br.Eof)
        {
            throw new InvalidImageContentException("VP8 first partition is truncated.");
        }

        this.AllocateMacroblockState();
        this.ParseIntraModes();
        var planes = new Vp8Planes(width, height, this.mbW, this.mbH);
        this.ReconstructFrame(planes);
        this.FilterFrame(planes);
        return planes;
    }

    // ----- Headers -----

    private void ParseSegmentHeader()
    {
        this.segmentProbas.AsSpan().Fill(255);
        this.useSegment = this.br.GetFlag() != 0;
        if (!this.useSegment)
        {
            this.updateSegmentMap = false;
            return;
        }

        this.updateSegmentMap = this.br.GetFlag() != 0;
        if (this.br.GetFlag() != 0)
        {
            this.absoluteSegmentDelta = this.br.GetFlag() != 0;
            for (int s = 0; s < NumMbSegments; s++)
            {
                this.segmentQuantizer[s] = this.br.GetFlag() != 0 ? this.br.GetSignedValue(7) : 0;
            }

            for (int s = 0; s < NumMbSegments; s++)
            {
                this.segmentFilterStrength[s] = this.br.GetFlag() != 0 ? this.br.GetSignedValue(6) : 0;
            }
        }

        if (this.updateSegmentMap)
        {
            for (int s = 0; s < 3; s++)
            {
                this.segmentProbas[s] = this.br.GetFlag() != 0 ? (byte)this.br.GetValue(8) : (byte)255;
            }
        }
    }

    private void ParseFilterHeader()
    {
        bool simple = this.br.GetFlag() != 0;
        this.filterLevel = this.br.GetValue(6);
        this.filterSharpness = this.br.GetValue(3);
        this.useLfDelta = this.br.GetFlag() != 0;
        if (this.useLfDelta && this.br.GetFlag() != 0)
        {
            for (int i = 0; i < NumRefLfDeltas; i++)
            {
                if (this.br.GetFlag() != 0)
                {
                    this.refLfDelta[i] = this.br.GetSignedValue(6);
                }
            }

            for (int i = 0; i < NumModeLfDeltas; i++)
            {
                if (this.br.GetFlag() != 0)
                {
                    this.modeLfDelta[i] = this.br.GetSignedValue(6);
                }
            }
        }

        this.filterType = this.filterLevel == 0 ? 0 : simple ? 1 : 2;
    }

    private void ParsePartitions(int start, int size)
    {
        this.numPartsMinusOne = (1 << this.br.GetValue(2)) - 1;
        int last = this.numPartsMinusOne;
        if (size < 3 * last)
        {
            throw new InvalidImageContentException("VP8 token partition sizes are truncated.");
        }

        this.parts = new Vp8BitReader[last + 1];
        int partStart = start + (last * 3);
        int sizeLeft = size - (last * 3);
        int sz = start;
        for (int p = 0; p < last; p++)
        {
            int psize = this.data[sz] | (this.data[sz + 1] << 8) | (this.data[sz + 2] << 16);
            if (psize > sizeLeft)
            {
                psize = sizeLeft;
            }

            this.parts[p] = new Vp8BitReader(this.data, partStart, psize);
            partStart += psize;
            sizeLeft -= psize;
            sz += 3;
        }

        this.parts[last] = new Vp8BitReader(this.data, partStart, sizeLeft);
    }

    private void ParseQuant()
    {
        int baseQ = this.br.GetValue(7);
        int dqY1Dc = this.br.GetFlag() != 0 ? this.br.GetSignedValue(4) : 0;
        int dqY2Dc = this.br.GetFlag() != 0 ? this.br.GetSignedValue(4) : 0;
        int dqY2Ac = this.br.GetFlag() != 0 ? this.br.GetSignedValue(4) : 0;
        int dqUvDc = this.br.GetFlag() != 0 ? this.br.GetSignedValue(4) : 0;
        int dqUvAc = this.br.GetFlag() != 0 ? this.br.GetSignedValue(4) : 0;

        for (int s = 0; s < NumMbSegments; s++)
        {
            int q;
            if (this.useSegment)
            {
                q = this.segmentQuantizer[s];
                if (!this.absoluteSegmentDelta)
                {
                    q += baseQ;
                }
            }
            else
            {
                if (s > 0)
                {
                    Array.Copy(this.quant, 0, this.quant, s * 6, 6);
                    continue;
                }

                q = baseQ;
            }

            int i = s * 6;
            this.quant[i + 0] = Vp8Tables.DcTable[Clip(q + dqY1Dc, 127)];
            this.quant[i + 1] = Vp8Tables.AcTable[Clip(q, 127)];
            this.quant[i + 2] = (ushort)(Vp8Tables.DcTable[Clip(q + dqY2Dc, 127)] * 2);

            // The second-order AC step is 155/100 of the plain AC step; the reference decoder computes
            // that as (x * 101581) >> 16, which is bit-identical for every entry of the table.
            int y2Ac = (Vp8Tables.AcTable[Clip(q + dqY2Ac, 127)] * 101581) >> 16;
            this.quant[i + 3] = (ushort)Math.Max(y2Ac, 8);
            this.quant[i + 4] = Vp8Tables.DcTable[Clip(q + dqUvDc, 117)]; // Chroma DC saturates at 132.
            this.quant[i + 5] = Vp8Tables.AcTable[Clip(q + dqUvAc, 127)];
        }
    }

    private void ParseProbabilities()
    {
        ReadOnlySpan<byte> updateProba = Vp8Tables.CoeffsUpdateProba;
        ReadOnlySpan<byte> defaults = Vp8Tables.CoeffsProba0;
        for (int i = 0; i < this.probas.Length; i++)
        {
            this.probas[i] = this.br.GetBit(updateProba[i]) != 0 ? (byte)this.br.GetValue(8) : defaults[i];
        }

        this.useSkipProba = this.br.GetFlag() != 0;
        if (this.useSkipProba)
        {
            this.skipProba = (byte)this.br.GetValue(8);
        }
    }

    private void PrecomputeFilterStrengths()
    {
        if (this.filterType == 0)
        {
            return;
        }

        for (int s = 0; s < NumMbSegments; s++)
        {
            int baseLevel;
            if (this.useSegment)
            {
                baseLevel = this.segmentFilterStrength[s];
                if (!this.absoluteSegmentDelta)
                {
                    baseLevel += this.filterLevel;
                }
            }
            else
            {
                baseLevel = this.filterLevel;
            }

            for (int i4x4 = 0; i4x4 <= 1; i4x4++)
            {
                int level = baseLevel;
                if (this.useLfDelta)
                {
                    level += this.refLfDelta[0];
                    if (i4x4 != 0)
                    {
                        level += this.modeLfDelta[0];
                    }
                }

                level = Clip(level, 63);
                int index = (s * 2) + i4x4;
                if (level > 0)
                {
                    int ilevel = level;
                    if (this.filterSharpness > 0)
                    {
                        ilevel >>= this.filterSharpness > 4 ? 2 : 1;
                        if (ilevel > 9 - this.filterSharpness)
                        {
                            ilevel = 9 - this.filterSharpness;
                        }
                    }

                    ilevel = Math.Max(ilevel, 1);
                    this.segmentInnerLevel[index] = (byte)ilevel;
                    this.segmentLimit[index] = (byte)((2 * level) + ilevel);
                    this.segmentHevThresh[index] = (byte)(level >= 40 ? 2 : level >= 15 ? 1 : 0);
                }
                else
                {
                    this.segmentLimit[index] = 0;
                    this.segmentInnerLevel[index] = 0;
                    this.segmentHevThresh[index] = 0;
                }
            }
        }
    }

    // ----- Macroblock modes (first partition) -----

    private void AllocateMacroblockState()
    {
        int count = this.mbW * this.mbH;
        this.mbSegment = new byte[count];
        this.mbSkip = new bool[count];
        this.mbIsI4x4 = new bool[count];
        this.mbUvMode = new byte[count];
        this.mbModes = new byte[count * 16];
        this.fLimit = new byte[count];
        this.fInnerLevel = new byte[count];
        this.fHevThresh = new byte[count];
        this.fInner = new bool[count];
        this.nzTop = new byte[this.mbW];
        this.nzTopDc = new byte[this.mbW];
        this.topY = new byte[this.mbW * 16];
        this.topU = new byte[this.mbW * 8];
        this.topV = new byte[this.mbW * 8];
    }

    private void ParseIntraModes()
    {
        var intraTop = new byte[4 * this.mbW];
        Span<byte> intraLeft = stackalloc byte[4];
        ReadOnlySpan<byte> bModesProba = Vp8Tables.BModesProba;
        ReadOnlySpan<sbyte> tree = BModeTree;

        for (int mbY = 0; mbY < this.mbH; mbY++)
        {
            intraLeft.Clear();
            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                int mb = (mbY * this.mbW) + mbX;
                int topOff = 4 * mbX;

                this.mbSegment[mb] = !this.updateSegmentMap
                    ? (byte)0
                    : this.br.GetBit(this.segmentProbas[0]) != 0
                        ? (byte)(2 + this.br.GetBit(this.segmentProbas[2]))
                        : (byte)this.br.GetBit(this.segmentProbas[1]);
                this.mbSkip[mb] = this.useSkipProba && this.br.GetBit(this.skipProba) != 0;
                bool isI4x4 = this.br.GetBit(145) == 0;
                this.mbIsI4x4[mb] = isI4x4;

                if (!isI4x4)
                {
                    int ymode = this.br.GetBit(156) != 0
                        ? this.br.GetBit(128) != 0 ? TmPred : HPred
                        : this.br.GetBit(163) != 0 ? VPred : DcPred;
                    this.mbModes[mb * 16] = (byte)ymode;
                    for (int i = 0; i < 4; i++)
                    {
                        intraTop[topOff + i] = (byte)ymode;
                        intraLeft[i] = (byte)ymode;
                    }
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                    {
                        int ymode = intraLeft[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int probaOff = ((intraTop[topOff + x] * 10) + ymode) * 9;
                            int i = 0;
                            do
                            {
                                i = tree[i + this.br.GetBit(bModesProba[probaOff + (i >> 1)])];
                            }
                            while (i > 0);

                            ymode = -i;
                            intraTop[topOff + x] = (byte)ymode;
                            this.mbModes[(mb * 16) + (y * 4) + x] = (byte)ymode;
                        }

                        intraLeft[y] = (byte)ymode;
                    }
                }

                this.mbUvMode[mb] = this.br.GetBit(142) == 0 ? (byte)DcPred
                    : this.br.GetBit(114) == 0 ? (byte)VPred
                    : this.br.GetBit(183) != 0 ? (byte)TmPred : (byte)HPred;
            }
        }

        if (this.br.Eof)
        {
            throw new InvalidImageContentException("VP8 macroblock modes are truncated.");
        }
    }

    // ----- Residuals (token partitions) -----

    private int GetLargeValue(Vp8BitReader token, int p)
    {
        byte[] prob = this.probas;
        int v;
        if (token.GetBit(prob[p + 3]) == 0)
        {
            v = token.GetBit(prob[p + 4]) == 0 ? 2 : 3 + token.GetBit(prob[p + 5]);
        }
        else if (token.GetBit(prob[p + 6]) == 0)
        {
            if (token.GetBit(prob[p + 7]) == 0)
            {
                v = 5 + token.GetBit(159);
            }
            else
            {
                v = 7 + (2 * token.GetBit(165));
                v += token.GetBit(145);
            }
        }
        else
        {
            int bit1 = token.GetBit(prob[p + 8]);
            int bit0 = token.GetBit(prob[p + 9 + bit1]);
            int cat = (2 * bit1) + bit0;
            ReadOnlySpan<byte> tab = cat switch
            {
                0 => Vp8Tables.Cat3,
                1 => Vp8Tables.Cat4,
                2 => Vp8Tables.Cat5,
                _ => Vp8Tables.Cat6,
            };
            v = 0;
            for (int i = 0; tab[i] != 0; i++)
            {
                v += v + token.GetBit(tab[i]);
            }

            v += 3 + (8 << cat);
        }

        return v;
    }

    /// <summary>
    /// Decodes the DCT tokens of one 4x4 block into <paramref name="output"/> in raster order, dequantising on
    /// the way, and returns the index just past the last non-zero coefficient.
    /// </summary>
    private int GetCoeffs(Vp8BitReader token, int typeOff, int ctx, int quantOff, int n, short[] output, int outOff)
    {
        byte[] prob = this.probas;
        ReadOnlySpan<byte> bands = Vp8Tables.Bands;
        ReadOnlySpan<byte> zigzag = Vp8Tables.Zigzag;
        int p = typeOff + (((bands[n] * Vp8Tables.NumContexts) + ctx) * Vp8Tables.NumProbas);
        for (; n < 16; n++)
        {
            if (token.GetBit(prob[p]) == 0)
            {
                return n; // End of block.
            }

            while (token.GetBit(prob[p + 1]) == 0)
            {
                if (++n == 16)
                {
                    return 16;
                }

                p = typeOff + (bands[n] * Vp8Tables.NumContexts * Vp8Tables.NumProbas);
            }

            int next = typeOff + (bands[n + 1] * Vp8Tables.NumContexts * Vp8Tables.NumProbas);
            int v;
            if (token.GetBit(prob[p + 2]) == 0)
            {
                v = 1;
                p = next + Vp8Tables.NumProbas;
            }
            else
            {
                v = this.GetLargeValue(token, p);
                p = next + (2 * Vp8Tables.NumProbas);
            }

            output[outOff + zigzag[n]] = (short)(token.GetSigned(v) * this.quant[quantOff + (n > 0 ? 1 : 0)]);
        }

        return 16;
    }

    /// <summary>Parses every residual block of one macroblock; returns true when the macroblock has no coefficients at all.</summary>
    private bool ParseResiduals(int mbX, int mb, Vp8BitReader token)
    {
        Array.Clear(this.coeffs);
        int q = this.mbSegment[mb] * 6;
        uint nonZeroY = 0;
        uint nonZeroUv = 0;
        int first;
        int acType;

        if (!this.mbIsI4x4[mb])
        {
            // Second-order luma DC block (Y2).
            Array.Clear(this.dcCoeffs);
            int dcCtx = this.nzTopDc[mbX] + this.nzLeftDc;
            int dcNz = this.GetCoeffs(token, TypeStride, dcCtx, q + 2, 0, this.dcCoeffs, 0);
            this.nzTopDc[mbX] = this.nzLeftDc = (byte)(dcNz > 0 ? 1 : 0);
            if (dcNz > 1)
            {
                Vp8Dsp.TransformWht(this.dcCoeffs, this.coeffs);
            }
            else
            {
                short dc0 = (short)((this.dcCoeffs[0] + 3) >> 3);
                for (int i = 0; i < 16 * 16; i += 16)
                {
                    this.coeffs[i] = dc0;
                }
            }

            first = 1;
            acType = 0;
        }
        else
        {
            first = 0;
            acType = 3 * TypeStride;
        }

        byte tnz = (byte)(this.nzTop[mbX] & 0x0f);
        byte lnz = (byte)(this.nzLeft & 0x0f);
        int dst = 0;
        for (int y = 0; y < 4; y++)
        {
            int l = lnz & 1;
            uint nzCoeffs = 0;
            for (int x = 0; x < 4; x++)
            {
                int ctx = l + (tnz & 1);
                int nz = this.GetCoeffs(token, acType, ctx, q, first, this.coeffs, dst);
                l = nz > first ? 1 : 0;
                tnz = (byte)((tnz >> 1) | (l << 7));
                nzCoeffs = NzCodeBits(nzCoeffs, nz, this.coeffs[dst] != 0);
                dst += 16;
            }

            tnz >>= 4;
            lnz = (byte)((lnz >> 1) | (l << 7));
            nonZeroY = (nonZeroY << 8) | nzCoeffs;
        }

        uint outTnz = tnz;
        uint outLnz = (uint)(lnz >> 4);

        for (int ch = 0; ch < 4; ch += 2)
        {
            uint nzCoeffs = 0;
            tnz = (byte)(this.nzTop[mbX] >> (4 + ch));
            lnz = (byte)(this.nzLeft >> (4 + ch));
            for (int y = 0; y < 2; y++)
            {
                int l = lnz & 1;
                for (int x = 0; x < 2; x++)
                {
                    int ctx = l + (tnz & 1);
                    int nz = this.GetCoeffs(token, 2 * TypeStride, ctx, q + 4, 0, this.coeffs, dst);
                    l = nz > 0 ? 1 : 0;
                    tnz = (byte)((tnz >> 1) | (l << 3));
                    nzCoeffs = NzCodeBits(nzCoeffs, nz, this.coeffs[dst] != 0);
                    dst += 16;
                }

                tnz >>= 2;
                lnz = (byte)((lnz >> 1) | (l << 5));
            }

            nonZeroUv |= nzCoeffs << (4 * ch);
            outTnz |= (uint)(tnz << 4) << ch;
            outLnz |= (uint)(lnz & 0xf0) << ch;
        }

        this.nzTop[mbX] = (byte)outTnz;
        this.nzLeft = (byte)outLnz;
        this.blockNonZeroY = nonZeroY;
        this.blockNonZeroUv = nonZeroUv;
        return (nonZeroY | nonZeroUv) == 0;
    }

    // ----- Reconstruction -----

    private void ReconstructFrame(Vp8Planes planes)
    {
        byte[] buf = this.yuv;
        for (int mbY = 0; mbY < this.mbH; mbY++)
        {
            Vp8BitReader token = this.parts[mbY & this.numPartsMinusOne];
            this.nzLeft = 0;
            this.nzLeftDc = 0;

            // The left column is unavailable at the start of every row and reads as 129; above the first
            // row every sample reads as 127 (RFC 6386 section 12.2).
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

            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                int mb = (mbY * this.mbW) + mbX;
                bool skip = this.useSkipProba && this.mbSkip[mb];
                if (!skip)
                {
                    skip = this.ParseResiduals(mbX, mb, token);
                }
                else
                {
                    this.nzLeft = this.nzTop[mbX] = 0;
                    if (!this.mbIsI4x4[mb])
                    {
                        this.nzLeftDc = this.nzTopDc[mbX] = 0;
                    }

                    this.blockNonZeroY = 0;
                    this.blockNonZeroUv = 0;
                }

                if (this.filterType > 0)
                {
                    int index = (this.mbSegment[mb] * 2) + (this.mbIsI4x4[mb] ? 1 : 0);
                    this.fLimit[mb] = this.segmentLimit[index];
                    this.fInnerLevel[mb] = this.segmentInnerLevel[index];
                    this.fHevThresh[mb] = this.segmentHevThresh[index];
                    this.fInner[mb] = this.mbIsI4x4[mb] || !skip;
                }

                if (token.Eof)
                {
                    throw new InvalidImageContentException("VP8 token partition is truncated.");
                }

                this.ReconstructMacroblock(mbX, mbY, mb);
                this.StoreMacroblock(planes, mbX, mbY);
            }
        }
    }

    private void ReconstructMacroblock(int mbX, int mbY, int mb)
    {
        byte[] buf = this.yuv;

        // Rotate the four right-most columns of the previous macroblock into the left context; the row
        // above (j = -1) carries the above-left sample along with it.
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

        uint bits = this.blockNonZeroY;
        if (this.mbIsI4x4[mb])
        {
            // The above-right samples of the right-most sub-block column always come from the row above
            // the macroblock, replicated downwards.
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

            for (int n = 0; n < 16; n++, bits <<= 2)
            {
                int dst = YOff + Scan[n];
                Vp8Dsp.PredictLuma4(buf, dst, this.mbModes[(mb * 16) + n]);
                DoTransform(bits, this.coeffs, n * 16, buf, dst);
            }
        }
        else
        {
            Vp8Dsp.PredictBlock(buf, YOff, 16, CheckMode(mbX, mbY, this.mbModes[mb * 16]));
            if (bits != 0)
            {
                for (int n = 0; n < 16; n++, bits <<= 2)
                {
                    DoTransform(bits, this.coeffs, n * 16, buf, YOff + Scan[n]);
                }
            }
        }

        int uvMode = CheckMode(mbX, mbY, this.mbUvMode[mb]);
        Vp8Dsp.PredictBlock(buf, UOff, 8, uvMode);
        Vp8Dsp.PredictBlock(buf, VOff, 8, uvMode);
        DoUvTransform(this.blockNonZeroUv, this.coeffs, 16 * 16, buf, UOff);
        DoUvTransform(this.blockNonZeroUv >> 8, this.coeffs, 20 * 16, buf, VOff);
    }

    private void StoreMacroblock(Vp8Planes planes, int mbX, int mbY)
    {
        byte[] buf = this.yuv;
        int yStride = planes.YStride;
        int uvStride = planes.UvStride;
        int yOut = (mbY * 16 * yStride) + (mbX * 16);
        int uvOut = (mbY * 8 * uvStride) + (mbX * 8);
        for (int j = 0; j < 16; j++)
        {
            buf.AsSpan(YOff + (j * Bps), 16).CopyTo(planes.Y.AsSpan(yOut + (j * yStride), 16));
        }

        for (int j = 0; j < 8; j++)
        {
            buf.AsSpan(UOff + (j * Bps), 8).CopyTo(planes.U.AsSpan(uvOut + (j * uvStride), 8));
            buf.AsSpan(VOff + (j * Bps), 8).CopyTo(planes.V.AsSpan(uvOut + (j * uvStride), 8));
        }

        // Save the unfiltered bottom row as the next macroblock row's prediction context.
        buf.AsSpan(YOff + (15 * Bps), 16).CopyTo(this.topY.AsSpan(mbX * 16, 16));
        buf.AsSpan(UOff + (7 * Bps), 8).CopyTo(this.topU.AsSpan(mbX * 8, 8));
        buf.AsSpan(VOff + (7 * Bps), 8).CopyTo(this.topV.AsSpan(mbX * 8, 8));
    }

    // ----- In-loop deblocking filter (RFC 6386 section 15) -----

    private void FilterFrame(Vp8Planes planes)
    {
        if (this.filterType == 0)
        {
            return;
        }

        int yBps = planes.YStride;
        int uvBps = planes.UvStride;
        for (int mbY = 0; mbY < this.mbH; mbY++)
        {
            for (int mbX = 0; mbX < this.mbW; mbX++)
            {
                int mb = (mbY * this.mbW) + mbX;
                int limit = this.fLimit[mb];
                if (limit == 0)
                {
                    continue;
                }

                int yDst = (mbY * 16 * yBps) + (mbX * 16);
                int uvDst = (mbY * 8 * uvBps) + (mbX * 8);
                bool inner = this.fInner[mb];
                if (this.filterType == 1)
                {
                    if (mbX > 0)
                    {
                        Vp8Dsp.SimpleHFilter16(planes.Y, yDst, yBps, limit + 4);
                    }

                    if (inner)
                    {
                        Vp8Dsp.SimpleHFilter16i(planes.Y, yDst, yBps, limit);
                    }

                    if (mbY > 0)
                    {
                        Vp8Dsp.SimpleVFilter16(planes.Y, yDst, yBps, limit + 4);
                    }

                    if (inner)
                    {
                        Vp8Dsp.SimpleVFilter16i(planes.Y, yDst, yBps, limit);
                    }
                }
                else
                {
                    int ilevel = this.fInnerLevel[mb];
                    int hev = this.fHevThresh[mb];
                    if (mbX > 0)
                    {
                        Vp8Dsp.HFilter16(planes.Y, yDst, yBps, limit + 4, ilevel, hev);
                        Vp8Dsp.HFilter8(planes.U, uvDst, uvBps, limit + 4, ilevel, hev);
                        Vp8Dsp.HFilter8(planes.V, uvDst, uvBps, limit + 4, ilevel, hev);
                    }

                    if (inner)
                    {
                        Vp8Dsp.HFilter16i(planes.Y, yDst, yBps, limit, ilevel, hev);
                        Vp8Dsp.HFilter8i(planes.U, uvDst, uvBps, limit, ilevel, hev);
                        Vp8Dsp.HFilter8i(planes.V, uvDst, uvBps, limit, ilevel, hev);
                    }

                    if (mbY > 0)
                    {
                        Vp8Dsp.VFilter16(planes.Y, yDst, yBps, limit + 4, ilevel, hev);
                        Vp8Dsp.VFilter8(planes.U, uvDst, uvBps, limit + 4, ilevel, hev);
                        Vp8Dsp.VFilter8(planes.V, uvDst, uvBps, limit + 4, ilevel, hev);
                    }

                    if (inner)
                    {
                        Vp8Dsp.VFilter16i(planes.Y, yDst, yBps, limit, ilevel, hev);
                        Vp8Dsp.VFilter8i(planes.U, uvDst, uvBps, limit, ilevel, hev);
                        Vp8Dsp.VFilter8i(planes.V, uvDst, uvBps, limit, ilevel, hev);
                    }
                }
            }
        }
    }
}
