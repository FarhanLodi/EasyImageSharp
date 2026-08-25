using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing.Quantization;

/// <summary>
/// The 33x33x33 moment histogram of Wu's algorithm (index 0 on every axis is an empty border that makes the
/// cumulative sums branch-free) plus the box-splitting palette builder. Raw counts stay intact after a build so
/// more colours can be added and the palette rebuilt.
/// </summary>
internal sealed class WuHistogram
{
    private const int Side = 33;
    private const int Cells = Side * Side * Side;
    private const int MaxSide = 32;

    private const int DirectionRed = 2;
    private const int DirectionGreen = 1;
    private const int DirectionBlue = 0;

    private static readonly int[] Squares = BuildSquares();

    private readonly long[] weights = new long[Cells];
    private readonly long[] sumR = new long[Cells];
    private readonly long[] sumG = new long[Cells];
    private readonly long[] sumB = new long[Cells];
    private readonly double[] sumSquares = new double[Cells];

    /// <summary>Adds the opaque pixels of a row; returns true when a pixel below the alpha cutoff was skipped.</summary>
    public bool Add(ReadOnlySpan<Rgba32> row, byte alphaCutoff)
    {
        bool transparent = false;
        for (int i = 0; i < row.Length; i++)
        {
            Rgba32 p = row[i];
            if (p.A < alphaCutoff)
            {
                transparent = true;
                continue;
            }

            int index = Index((p.R >> 3) + 1, (p.G >> 3) + 1, (p.B >> 3) + 1);
            this.weights[index]++;
            this.sumR[index] += p.R;
            this.sumG[index] += p.G;
            this.sumB[index] += p.B;
            this.sumSquares[index] += Squares[p.R] + Squares[p.G] + Squares[p.B];
        }

        return transparent;
    }

    public void Merge(WuHistogram other)
    {
        for (int i = 0; i < Cells; i++)
        {
            this.weights[i] += other.weights[i];
            this.sumR[i] += other.sumR[i];
            this.sumG[i] += other.sumG[i];
            this.sumB[i] += other.sumB[i];
            this.sumSquares[i] += other.sumSquares[i];
        }
    }

    /// <summary>Splits colour space into at most <paramref name="maxColors"/> boxes and returns their mean colours.</summary>
    public Rgba32[] BuildPalette(int maxColors)
    {
        var moments = new Moments(this);
        var cubes = new Box[maxColors];
        var variances = new double[maxColors];

        cubes[0] = new Box { R1 = MaxSide, G1 = MaxSide, B1 = MaxSide, Volume = MaxSide * MaxSide * MaxSide };
        int next = 0;
        int boxCount = maxColors;
        for (int i = 1; i < maxColors; i++)
        {
            if (moments.Cut(ref cubes[next], ref cubes[i]))
            {
                variances[next] = cubes[next].Volume > 1 ? moments.Variance(in cubes[next]) : 0d;
                variances[i] = cubes[i].Volume > 1 ? moments.Variance(in cubes[i]) : 0d;
            }
            else
            {
                variances[next] = 0d; // The box cannot be split; try the next most varied one for this slot.
                i--;
            }

            next = 0;
            double best = variances[0];
            for (int k = 1; k <= i; k++)
            {
                if (variances[k] > best)
                {
                    best = variances[k];
                    next = k;
                }
            }

            if (best <= 0d)
            {
                boxCount = i + 1;
                break;
            }
        }

        var palette = new List<Rgba32>(boxCount);
        for (int k = 0; k < boxCount; k++)
        {
            long weight = moments.Volume(in cubes[k], moments.Weights);
            if (weight <= 0)
            {
                continue; // An empty box contributes no colour.
            }

            palette.Add(new Rgba32(
                (byte)Math.Clamp((moments.Volume(in cubes[k], moments.SumR) + (weight / 2)) / weight, 0, 255),
                (byte)Math.Clamp((moments.Volume(in cubes[k], moments.SumG) + (weight / 2)) / weight, 0, 255),
                (byte)Math.Clamp((moments.Volume(in cubes[k], moments.SumB) + (weight / 2)) / weight, 0, 255)));
        }

        return palette.ToArray();
    }

    private static int Index(int r, int g, int b) => ((r * Side) + g) * Side + b;

    private static int[] BuildSquares()
    {
        var squares = new int[256];
        for (int i = 0; i < squares.Length; i++)
        {
            squares[i] = i * i;
        }

        return squares;
    }

    /// <summary>A box in histogram space covering (R0, R1] x (G0, G1] x (B0, B1] (lower bounds exclusive).</summary>
    private struct Box
    {
        public int R0;
        public int R1;
        public int G0;
        public int G1;
        public int B0;
        public int B1;
        public int Volume;
    }

    /// <summary>Cumulative moments over the histogram, and the box statistics derived from them.</summary>
    private sealed class Moments
    {
        public readonly long[] Weights = new long[Cells];
        public readonly long[] SumR = new long[Cells];
        public readonly long[] SumG = new long[Cells];
        public readonly long[] SumB = new long[Cells];
        public readonly double[] SumSquares = new double[Cells];

        public Moments(WuHistogram histogram)
        {
            // Convert raw cell sums into cumulative sums over the box (0, r] x (0, g] x (0, b].
            var areaW = new long[Side];
            var areaR = new long[Side];
            var areaG = new long[Side];
            var areaB = new long[Side];
            var areaS = new double[Side];

            for (int r = 1; r <= MaxSide; r++)
            {
                Array.Clear(areaW);
                Array.Clear(areaR);
                Array.Clear(areaG);
                Array.Clear(areaB);
                Array.Clear(areaS);

                for (int g = 1; g <= MaxSide; g++)
                {
                    long lineW = 0, lineR = 0, lineG = 0, lineB = 0;
                    double lineS = 0;
                    for (int b = 1; b <= MaxSide; b++)
                    {
                        int index = Index(r, g, b);
                        lineW += histogram.weights[index];
                        lineR += histogram.sumR[index];
                        lineG += histogram.sumG[index];
                        lineB += histogram.sumB[index];
                        lineS += histogram.sumSquares[index];

                        areaW[b] += lineW;
                        areaR[b] += lineR;
                        areaG[b] += lineG;
                        areaB[b] += lineB;
                        areaS[b] += lineS;

                        int previous = Index(r - 1, g, b);
                        this.Weights[index] = this.Weights[previous] + areaW[b];
                        this.SumR[index] = this.SumR[previous] + areaR[b];
                        this.SumG[index] = this.SumG[previous] + areaG[b];
                        this.SumB[index] = this.SumB[previous] + areaB[b];
                        this.SumSquares[index] = this.SumSquares[previous] + areaS[b];
                    }
                }
            }
        }

        /// <summary>The sum of a moment over a box (inclusion–exclusion over its eight corners).</summary>
        public long Volume(in Box cube, long[] moment)
            => moment[Index(cube.R1, cube.G1, cube.B1)]
             - moment[Index(cube.R1, cube.G1, cube.B0)]
             - moment[Index(cube.R1, cube.G0, cube.B1)]
             + moment[Index(cube.R1, cube.G0, cube.B0)]
             - moment[Index(cube.R0, cube.G1, cube.B1)]
             + moment[Index(cube.R0, cube.G1, cube.B0)]
             + moment[Index(cube.R0, cube.G0, cube.B1)]
             - moment[Index(cube.R0, cube.G0, cube.B0)];

        public double Volume(in Box cube, double[] moment)
            => moment[Index(cube.R1, cube.G1, cube.B1)]
             - moment[Index(cube.R1, cube.G1, cube.B0)]
             - moment[Index(cube.R1, cube.G0, cube.B1)]
             + moment[Index(cube.R1, cube.G0, cube.B0)]
             - moment[Index(cube.R0, cube.G1, cube.B1)]
             + moment[Index(cube.R0, cube.G1, cube.B0)]
             + moment[Index(cube.R0, cube.G0, cube.B1)]
             - moment[Index(cube.R0, cube.G0, cube.B0)];

        /// <summary>The weighted variance of the colours in a box.</summary>
        public double Variance(in Box cube)
        {
            double dr = this.Volume(in cube, this.SumR);
            double dg = this.Volume(in cube, this.SumG);
            double db = this.Volume(in cube, this.SumB);
            double squares = this.Volume(in cube, this.SumSquares);
            long weight = this.Volume(in cube, this.Weights);
            return weight == 0 ? 0d : squares - (((dr * dr) + (dg * dg) + (db * db)) / weight);
        }

        /// <summary>Splits <paramref name="set1"/> along the axis and position that minimise the resulting variance; returns false when it cannot be split.</summary>
        public bool Cut(ref Box set1, ref Box set2)
        {
            long wholeR = this.Volume(in set1, this.SumR);
            long wholeG = this.Volume(in set1, this.SumG);
            long wholeB = this.Volume(in set1, this.SumB);
            long wholeW = this.Volume(in set1, this.Weights);

            double maxR = this.Maximize(in set1, DirectionRed, set1.R0 + 1, set1.R1, out int cutR, wholeR, wholeG, wholeB, wholeW);
            double maxG = this.Maximize(in set1, DirectionGreen, set1.G0 + 1, set1.G1, out int cutG, wholeR, wholeG, wholeB, wholeW);
            double maxB = this.Maximize(in set1, DirectionBlue, set1.B0 + 1, set1.B1, out int cutB, wholeR, wholeG, wholeB, wholeW);

            int direction;
            if (maxR >= maxG && maxR >= maxB)
            {
                direction = DirectionRed;
                if (cutR < 0)
                {
                    return false; // The box holds a single colour cell.
                }
            }
            else if (maxG >= maxR && maxG >= maxB)
            {
                direction = DirectionGreen;
            }
            else
            {
                direction = DirectionBlue;
            }

            set2.R1 = set1.R1;
            set2.G1 = set1.G1;
            set2.B1 = set1.B1;
            switch (direction)
            {
                case DirectionRed:
                    set2.R0 = set1.R1 = cutR;
                    set2.G0 = set1.G0;
                    set2.B0 = set1.B0;
                    break;
                case DirectionGreen:
                    set2.G0 = set1.G1 = cutG;
                    set2.R0 = set1.R0;
                    set2.B0 = set1.B0;
                    break;
                default:
                    set2.B0 = set1.B1 = cutB;
                    set2.R0 = set1.R0;
                    set2.G0 = set1.G0;
                    break;
            }

            set1.Volume = (set1.R1 - set1.R0) * (set1.G1 - set1.G0) * (set1.B1 - set1.B0);
            set2.Volume = (set2.R1 - set2.R0) * (set2.G1 - set2.G0) * (set2.B1 - set2.B0);
            return true;
        }

        /// <summary>Finds the split position along one axis that maximises the variance reduction; -1 when no split is possible.</summary>
        private double Maximize(
            in Box cube, int direction, int first, int last, out int cut,
            long wholeR, long wholeG, long wholeB, long wholeW)
        {
            long baseR = this.Bottom(in cube, direction, this.SumR);
            long baseG = this.Bottom(in cube, direction, this.SumG);
            long baseB = this.Bottom(in cube, direction, this.SumB);
            long baseW = this.Bottom(in cube, direction, this.Weights);

            double max = 0d;
            cut = -1;
            for (int i = first; i < last; i++)
            {
                long halfR = baseR + this.Top(in cube, direction, i, this.SumR);
                long halfG = baseG + this.Top(in cube, direction, i, this.SumG);
                long halfB = baseB + this.Top(in cube, direction, i, this.SumB);
                long halfW = baseW + this.Top(in cube, direction, i, this.Weights);
                if (halfW == 0)
                {
                    continue; // Never split into an empty box.
                }

                double temp = ((double)halfR * halfR + (double)halfG * halfG + (double)halfB * halfB) / halfW;

                halfR = wholeR - halfR;
                halfG = wholeG - halfG;
                halfB = wholeB - halfB;
                halfW = wholeW - halfW;
                if (halfW == 0)
                {
                    continue;
                }

                temp += ((double)halfR * halfR + (double)halfG * halfG + (double)halfB * halfB) / halfW;
                if (temp > max)
                {
                    max = temp;
                    cut = i;
                }
            }

            return max;
        }

        /// <summary>The moment sum over the lower face of the box perpendicular to <paramref name="direction"/>.</summary>
        private long Bottom(in Box cube, int direction, long[] moment) => direction switch
        {
            DirectionRed => -moment[Index(cube.R0, cube.G1, cube.B1)]
                            + moment[Index(cube.R0, cube.G1, cube.B0)]
                            + moment[Index(cube.R0, cube.G0, cube.B1)]
                            - moment[Index(cube.R0, cube.G0, cube.B0)],
            DirectionGreen => -moment[Index(cube.R1, cube.G0, cube.B1)]
                              + moment[Index(cube.R1, cube.G0, cube.B0)]
                              + moment[Index(cube.R0, cube.G0, cube.B1)]
                              - moment[Index(cube.R0, cube.G0, cube.B0)],
            _ => -moment[Index(cube.R1, cube.G1, cube.B0)]
                 + moment[Index(cube.R1, cube.G0, cube.B0)]
                 + moment[Index(cube.R0, cube.G1, cube.B0)]
                 - moment[Index(cube.R0, cube.G0, cube.B0)],
        };

        /// <summary>The moment sum over the face of the box at <paramref name="position"/> along <paramref name="direction"/>.</summary>
        private long Top(in Box cube, int direction, int position, long[] moment) => direction switch
        {
            DirectionRed => moment[Index(position, cube.G1, cube.B1)]
                            - moment[Index(position, cube.G1, cube.B0)]
                            - moment[Index(position, cube.G0, cube.B1)]
                            + moment[Index(position, cube.G0, cube.B0)],
            DirectionGreen => moment[Index(cube.R1, position, cube.B1)]
                              - moment[Index(cube.R1, position, cube.B0)]
                              - moment[Index(cube.R0, position, cube.B1)]
                              + moment[Index(cube.R0, position, cube.B0)],
            _ => moment[Index(cube.R1, cube.G1, position)]
                 - moment[Index(cube.R1, cube.G0, position)]
                 - moment[Index(cube.R0, cube.G1, position)]
                 + moment[Index(cube.R0, cube.G0, position)],
        };
    }
}
