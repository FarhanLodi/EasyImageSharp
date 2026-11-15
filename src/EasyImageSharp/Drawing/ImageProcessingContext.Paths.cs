using EasyImageSharp.Drawing;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Processing;

/// <summary>Vector drawing operations; see <c>IImageProcessingContext.Paths.cs</c> for the contract.</summary>
internal sealed partial class ImageProcessingContext<TPixel>
{
    public IImageProcessingContext Fill(Brush brush, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(options);
        return this.PerFrame(frame =>
        {
            DrawOps.Fill(frame, brush, options);
            return frame;
        });
    }

    public IImageProcessingContext Fill(Brush brush, VectorPath path, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (path.IsEmpty)
        {
            return this;
        }

        return this.PerFrame(frame =>
        {
            DrawOps.FillPath(frame, brush, path, options);
            return frame;
        });
    }

    public IImageProcessingContext Draw(Pen pen, VectorPath path, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (path.IsEmpty)
        {
            return this;
        }

        return this.PerFrame(frame =>
        {
            DrawOps.StrokePath(frame, pen, path, options);
            return frame;
        });
    }

    public IImageProcessingContext FillRectangle(Brush brush, RectangleF rectangle, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(options);
        RequireFinite(rectangle, nameof(rectangle));
        return this.PerFrame(frame =>
        {
            DrawOps.FillRectangle(frame, brush, rectangle, options);
            return frame;
        });
    }

    public IImageProcessingContext FillEllipse(Brush brush, RectangleF bounds, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(options);
        RequireFinite(bounds, nameof(bounds));
        return this.PerFrame(frame =>
        {
            DrawOps.FillEllipse(frame, brush, bounds, options);
            return frame;
        });
    }

    public IImageProcessingContext FillPolygon(Brush brush, ReadOnlySpan<PointF> points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(brush);
        ArgumentNullException.ThrowIfNull(options);
        PointF[] polygon = CopyPath(points);
        return this.PerFrame(frame =>
        {
            DrawOps.FillPolygon(frame, brush, polygon, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawRectangle(Pen pen, RectangleF rectangle, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireFinite(rectangle, nameof(rectangle));
        return this.PerFrame(frame =>
        {
            DrawOps.DrawRectangle(frame, pen, rectangle, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawEllipse(Pen pen, RectangleF bounds, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireFinite(bounds, nameof(bounds));
        return this.PerFrame(frame =>
        {
            DrawOps.DrawEllipse(frame, pen, bounds, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawLines(Pen pen, ReadOnlySpan<PointF> points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PointF[] path = CopyPath(points);
        return this.PerFrame(frame =>
        {
            DrawOps.DrawPath(frame, pen, path, closed: false, options);
            return frame;
        });
    }

    public IImageProcessingContext DrawPolygon(Pen pen, ReadOnlySpan<PointF> points, DrawingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PointF[] path = CopyPath(points);
        return this.PerFrame(frame =>
        {
            DrawOps.DrawPath(frame, pen, path, closed: true, options);
            return frame;
        });
    }

    public IImageProcessingContext Clip(VectorPath path, DrawingOptions options, Action<IImageProcessingContext> operations)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operations);

        using Image<TPixel> scratch = this.image.Clone();
        operations(new ImageProcessingContext<TPixel>(scratch));
        List<ImageFrame<TPixel>> processed = scratch.Frames.InnerList;
        List<ImageFrame<TPixel>> frames = this.image.Frames.InnerList;
        if (processed.Count != frames.Count || scratch.Width != this.image.Width || scratch.Height != this.image.Height)
        {
            throw new InvalidOperationException("Operations inside a clip must keep the image's size and frame count.");
        }

        this.EnsureOwnBuffers();
        for (int i = 0; i < frames.Count; i++)
        {
            ImageFrame<TPixel> frame = frames[i];
            ImageFrame<TPixel> result = processed[i];
            if (result.Width != frame.Width || result.Height != frame.Height)
            {
                throw new InvalidOperationException("Operations inside a clip must keep the image's size and frame count.");
            }

            CoverageMask mask = CoverageMask.Create(path, options.ClipFillRule, options.Antialias, options.CurveTolerance, frame.Width, frame.Height);
            if (DrawOps.CreateClipMask(frame, options) is CoverageMask outer)
            {
                mask = mask.Intersect(outer);
            }

            ClipComposite.Apply(frame, result, mask);
        }

        return this;
    }
}
