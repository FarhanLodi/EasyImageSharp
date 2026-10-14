namespace EasyImageSharp.Processing;

internal sealed class DocumentCoordinateMap : IDocumentCoordinateMap
{
    private readonly List<(double[] Forward, double[] Inverse)> steps = new();

    internal void Add(double[] forward, double[] inverse) => this.steps.Add((forward, inverse));

    internal void AddRotation(double degrees, Size before, Size after)
    {
        double radians = degrees * Math.PI / 180;
        double c = Math.Cos(radians), s = Math.Sin(radians);
        this.Add(
            [c, -s, (after.Width - (c * before.Width) + (s * before.Height)) / 2,
             s, c, (after.Height - (s * before.Width) - (c * before.Height)) / 2, 0, 0, 1],
            [c, s, (before.Width - (c * after.Width) - (s * after.Height)) / 2,
             -s, c, (before.Height + (s * after.Width) - (c * after.Height)) / 2, 0, 0, 1]);
    }

    public bool TryMapToProcessed(PointF source, out PointF processed)
    {
        processed = source;
        if (!Finite(source))
        {
            return false;
        }

        foreach ((double[] forward, _) in this.steps)
        {
            if (!TryApply(forward, processed, out processed))
            {
                return false;
            }
        }

        return true;
    }

    public bool TryMapToSource(PointF processed, out PointF source)
    {
        source = processed;
        if (!Finite(processed))
        {
            return false;
        }

        for (int i = this.steps.Count - 1; i >= 0; i--)
        {
            if (!TryApply(this.steps[i].Inverse, source, out source))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryApply(double[] matrix, PointF point, out PointF mapped)
    {
        double denominator = (matrix[6] * point.X) + (matrix[7] * point.Y) + matrix[8];
        mapped = default;
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
        {
            return false;
        }

        mapped = new PointF((float)(((matrix[0] * point.X) + (matrix[1] * point.Y) + matrix[2]) / denominator),
            (float)(((matrix[3] * point.X) + (matrix[4] * point.Y) + matrix[5]) / denominator));
        return Finite(mapped);
    }

    private static bool Finite(PointF point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
