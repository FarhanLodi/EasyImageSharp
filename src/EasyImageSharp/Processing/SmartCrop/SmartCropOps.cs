using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Classical content-aware crop selection; see <see cref="SmartCropOptions"/> for the model.</summary>
internal static class SmartCropOps
{
    /// <summary>Cells of the scoring grid along the analysis copy's longer side.</summary>
    private const int ScoreGridSize = 64;

    /// <summary>Normalised skin chromaticity (the direction of RGB 0.78, 0.57, 0.44).</summary>
    private static readonly (double R, double G, double B) Skin = Normalise(0.78, 0.57, 0.44);

    public static Rectangle Find<TPixel>(ImageFrame<TPixel> frame, int width, int height, SmartCropOptions options)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        ArgumentNullException.ThrowIfNull(options);
        int sourceWidth = frame.Width;
        int sourceHeight = frame.Height;
        double aspect = (double)width / height;

        // The largest crop of the target aspect that fits the source.
        double baseWidth = Math.Min(sourceWidth, sourceHeight * aspect);
        double baseHeight = baseWidth / aspect;

        // Analysis copy: an area-averaged reduction to at most AnalysisSize per side.
        double reduce = Math.Min(1, options.AnalysisSize / (double)Math.Max(sourceWidth, sourceHeight));
        int aw = Math.Max(1, (int)Math.Round(sourceWidth * reduce));
        int ah = Math.Max(1, (int)Math.Round(sourceHeight * reduce));
        ImageFrame<TPixel> small = aw == sourceWidth && ah == sourceHeight ? frame : FrameOps.Resize(frame, aw, ah, KnownResamplers.Box);
        Rgba32[] pixels = RowProcessor.ReadRegion(small, new Rectangle(0, 0, aw, ah));
        double[] importance = Importance(pixels, aw, ah, options, sourceWidth / (double)aw, sourceHeight / (double)ah);

        // Coarse scoring grid: each cell sums the importance of the analysis pixels whose centres fall in it.
        double cellSize = Math.Max(1, Math.Max(aw, ah) / (double)ScoreGridSize);
        int gw = Math.Max(1, (int)Math.Ceiling(aw / cellSize));
        int gh = Math.Max(1, (int)Math.Ceiling(ah / cellSize));
        double[] cells = new double[gw * gh];
        for (int y = 0; y < ah; y++)
        {
            int cy = Math.Min(gh - 1, (int)((y + 0.5) / cellSize));
            for (int x = 0; x < aw; x++)
            {
                cells[(cy * gw) + Math.Min(gw - 1, (int)((x + 0.5) / cellSize))] += importance[(y * aw) + x];
            }
        }

        double total = 0;
        foreach (double c in cells)
        {
            total += c;
        }

        if (!(total > 1e-12))
        {
            // Nothing stands out (a flat or fully transparent image): fall back to the centre.
            return Centered(sourceWidth, sourceHeight, baseWidth, baseHeight);
        }

        // Grid units per source pixel along each axis.
        double unitX = gw / (double)sourceWidth;
        double unitY = gh / (double)sourceHeight;
        Rectangle best = Centered(sourceWidth, sourceHeight, baseWidth, baseHeight);
        double bestScore = double.NegativeInfinity;
        double bestDistance = double.PositiveInfinity;
        int scaleSteps = Math.Max(1, (int)Math.Round((1 - options.MinScale) / 0.1)) + 1;
        for (int s = 0; s < scaleSteps; s++)
        {
            OperationExecution.Checkpoint();
            double scale = scaleSteps == 1 ? 1 : 1 - ((1 - options.MinScale) * s / (scaleSteps - 1));
            int cropWidth = Math.Clamp((int)Math.Round(baseWidth * scale), 1, sourceWidth);
            int cropHeight = Math.Clamp((int)Math.Round(baseHeight * scale), 1, sourceHeight);
            double candidateWidth = cropWidth * unitX;
            double candidateHeight = cropHeight * unitY;
            int stepsX = Math.Max(0, (int)Math.Floor(gw - candidateWidth + 1e-9));
            int stepsY = Math.Max(0, (int)Math.Floor(gh - candidateHeight + 1e-9));

            // The exactly centred crop is a candidate too (evaluated first), then every whole-cell position; the last
            // position on each axis is flush with the far edge.
            for (int py = -1; py <= stepsY; py++)
            {
                for (int px = py < 0 ? -1 : 0; px <= (py < 0 ? -1 : stepsX); px++)
                {
                    bool centred = py < 0;
                    double left = centred ? (gw - candidateWidth) / 2 : px == stepsX ? gw - candidateWidth : px;
                    double top = centred ? (gh - candidateHeight) / 2 : py == stepsY ? gh - candidateHeight : py;
                    double score = Score(cells, gw, gh, total, left, top, candidateWidth, candidateHeight, options);
                    double dx = left + (candidateWidth / 2) - (gw / 2.0);
                    double dy = top + (candidateHeight / 2) - (gh / 2.0);
                    double distance = (dx * dx) + (dy * dy);

                    // Scores within a relative 1e-9 are ties, settled in favour of the more central candidate.
                    double tolerance = 1e-9 * Math.Max(1e-12, Math.Abs(bestScore));
                    if (score > bestScore + tolerance || (Math.Abs(score - bestScore) <= tolerance && distance < bestDistance - 1e-12))
                    {
                        bestScore = Math.Max(score, bestScore);
                        bestDistance = distance;
                        int x = centred ? (sourceWidth - cropWidth) / 2 : Math.Clamp((int)Math.Round(left / unitX), 0, sourceWidth - cropWidth);
                        int y = centred ? (sourceHeight - cropHeight) / 2 : Math.Clamp((int)Math.Round(top / unitY), 0, sourceHeight - cropHeight);
                        best = new Rectangle(x, y, cropWidth, cropHeight);
                    }
                }
            }
        }

        return best;
    }

    private static double Score(double[] cells, int gw, int gh, double total, double left, double top, double width, double height, SmartCropOptions options)
    {
        double inside = 0, insideRaw = 0;
        int x0 = Math.Max(0, (int)Math.Floor(left));
        int x1 = Math.Min(gw - 1, (int)Math.Ceiling(left + width));
        int y0 = Math.Max(0, (int)Math.Floor(top));
        int y1 = Math.Min(gh - 1, (int)Math.Ceiling(top + height));
        for (int cy = y0; cy <= y1; cy++)
        {
            double v = ((cy + 0.5) - top) / height;
            if (v < 0 || v >= 1)
            {
                continue;
            }

            for (int cx = x0; cx <= x1; cx++)
            {
                double u = ((cx + 0.5) - left) / width;
                if (u < 0 || u >= 1)
                {
                    continue;
                }

                double c = cells[(cy * gw) + cx];
                inside += c * Placement(u, v, options.RuleOfThirds);
                insideRaw += c;
            }
        }

        double outside = total - insideRaw;
        return (inside - (options.OutsidePenalty * outside)) / (width * height);
    }

    /// <summary>
    /// The weight of content at normalised position (u, v) inside a candidate: a mild preference for the centre,
    /// a bonus on the third lines and a falloff in the outer 10% so subjects do not touch the crop border.
    /// </summary>
    private static double Placement(double u, double v, bool ruleOfThirds)
    {
        double px = Math.Abs((2 * u) - 1);
        double py = Math.Abs((2 * v) - 1);
        double weight = 1 - (0.5 * Math.Sqrt(((px * px) + (py * py)) / 2));
        if (ruleOfThirds)
        {
            weight *= 1 + (0.5 * (Thirds(px) + Thirds(py)));
        }

        double edge = Math.Max(px, py);
        if (edge > 0.8)
        {
            weight *= Math.Max(0, (1 - edge) / 0.2);
        }

        return weight;
    }

    /// <summary>1 on a third line (|2u - 1| = 1/3), falling linearly to 0 at 1/6 of the crop away from it.</summary>
    private static double Thirds(double p) => Math.Max(0, 1 - (Math.Abs(p - (1.0 / 3)) * 6));

    private static double[] Importance(Rgba32[] pixels, int width, int height, SmartCropOptions options, double sourcePerX, double sourcePerY)
    {
        double[] luma = new double[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            luma[i] = ((0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B)) / 255;
        }

        double[] importance = new double[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                Rgba32 p = pixels[i];
                double l = luma[i];
                double up = luma[(Math.Max(0, y - 1) * width) + x];
                double down = luma[(Math.Min(height - 1, y + 1) * width) + x];
                double left = luma[(y * width) + Math.Max(0, x - 1)];
                double right = luma[(y * width) + Math.Min(width - 1, x + 1)];
                double detail = Math.Min(1, Math.Abs((4 * l) - up - down - left - right));

                double r = p.R / 255.0, g = p.G / 255.0, b = p.B / 255.0;
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double saturation = 0;
                if (max > min && l > 0.05 && l < 0.95)
                {
                    double sum = max + min;
                    saturation = (max - min) / (sum > 1 ? 2 - sum : sum);
                }

                double skin = 0;
                double magnitude = Math.Sqrt((r * r) + (g * g) + (b * b));
                if (magnitude > 0 && l > 0.2 && l < 0.95)
                {
                    double dr = (r / magnitude) - Skin.R, dg = (g / magnitude) - Skin.G, db = (b / magnitude) - Skin.B;
                    skin = Math.Max(0, 1 - (Math.Sqrt((dr * dr) + (dg * dg) + (db * db)) / 0.12));
                }

                importance[i] = ((options.DetailWeight * detail) + (options.SaturationWeight * saturation) + (options.SkinWeight * skin))
                    * (p.A / 255.0);
            }
        }

        foreach (SmartCropBoost boost in options.Boosts)
        {
            int x0 = Math.Max(0, (int)Math.Floor(boost.Region.Left / sourcePerX));
            int x1 = Math.Min(width, (int)Math.Ceiling(boost.Region.Right / sourcePerX));
            int y0 = Math.Max(0, (int)Math.Floor(boost.Region.Top / sourcePerY));
            int y1 = Math.Min(height, (int)Math.Ceiling(boost.Region.Bottom / sourcePerY));
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    importance[(y * width) + x] += boost.Weight;
                }
            }
        }

        return importance;
    }

    private static Rectangle Centered(int sourceWidth, int sourceHeight, double cropWidth, double cropHeight)
    {
        int w = Math.Clamp((int)Math.Round(cropWidth), 1, sourceWidth);
        int h = Math.Clamp((int)Math.Round(cropHeight), 1, sourceHeight);
        return new Rectangle((sourceWidth - w) / 2, (sourceHeight - h) / 2, w, h);
    }

    private static (double R, double G, double B) Normalise(double r, double g, double b)
    {
        double m = Math.Sqrt((r * r) + (g * g) + (b * b));
        return (r / m, g / m, b / m);
    }
}
