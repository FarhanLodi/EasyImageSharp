using System.Buffers;
using EasyImageSharp.Metadata;
using EasyImageSharp.PixelFormats;

namespace EasyImageSharp.Streaming;

/// <summary>
/// A pipeline stage that places the rows it receives on a canvas of a fixed size, row by row: content rows
/// are shifted by the offset and clipped to the canvas, and everything the content does not cover is the
/// background colour. Negative offsets crop. This is the row-streaming form of the canvas placement the
/// padding and cropping resize modes apply after scaling, and it produces the same pixels.
/// </summary>
/// <typeparam name="TPixel">The pixel format of the rows.</typeparam>
internal sealed class CanvasPlacementStage<TPixel> : IScanlineSink<TPixel>
    where TPixel : unmanaged, IPixel<TPixel>
{
    private readonly IScanlineSink<TPixel> next;
    private readonly int canvasWidth;
    private readonly int canvasHeight;
    private readonly int offsetX;
    private readonly int offsetY;
    private readonly TPixel background;

    private int contentWidth;
    private int contentHeight;
    private int sourceStartX;
    private int sourceStartY;
    private int destinationStartX;
    private int destinationStartY;
    private int copyWidth;
    private int copyHeight;
    private int rowsReceived;
    private int rowsEmitted;
    private TPixel[]? row;
    private State state;

    /// <summary>Creates the stage.</summary>
    /// <param name="next">The stage that receives the canvas rows.</param>
    /// <param name="canvasWidth">The canvas width.</param>
    /// <param name="canvasHeight">The canvas height.</param>
    /// <param name="offsetX">Where the content's left edge lands on the canvas; negative crops.</param>
    /// <param name="offsetY">Where the content's top edge lands on the canvas; negative crops.</param>
    /// <param name="background">The colour of the canvas where the content does not reach.</param>
    public CanvasPlacementStage(
        IScanlineSink<TPixel> next, int canvasWidth, int canvasHeight, int offsetX, int offsetY, Rgba32 background)
    {
        ArgumentNullException.ThrowIfNull(next);
        Guard.MustBePositive(canvasWidth, nameof(canvasWidth));
        Guard.MustBePositive(canvasHeight, nameof(canvasHeight));
        this.next = next;
        this.canvasWidth = canvasWidth;
        this.canvasHeight = canvasHeight;
        this.offsetX = offsetX;
        this.offsetY = offsetY;
        this.background = TPixel.FromRgba32(background);
    }

    private enum State
    {
        Created,
        Writing,
        Completed,
        Disposed,
    }

    public void Begin(int width, int height, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        this.EnsureState(State.Created);
        Guard.MustBePositive(width, nameof(width));
        Guard.MustBePositive(height, nameof(height));
        this.contentWidth = width;
        this.contentHeight = height;

        // The same clipping arithmetic as FrameOps.PlaceOnCanvas.
        this.sourceStartX = Math.Max(0, -this.offsetX);
        this.sourceStartY = Math.Max(0, -this.offsetY);
        this.destinationStartX = Math.Max(0, this.offsetX);
        this.destinationStartY = Math.Max(0, this.offsetY);
        this.copyWidth = (int)Math.Min((long)width - this.sourceStartX, (long)this.canvasWidth - this.destinationStartX);
        this.copyHeight = (int)Math.Min((long)height - this.sourceStartY, (long)this.canvasHeight - this.destinationStartY);
        if (this.copyWidth <= 0 || this.copyHeight <= 0)
        {
            // The content lies entirely outside the canvas: every row is background.
            this.copyWidth = 0;
            this.copyHeight = 0;
        }

        this.row = ArrayPool<TPixel>.Shared.Rent(this.canvasWidth);
        this.state = State.Writing;
        this.next.Begin(this.canvasWidth, this.canvasHeight, metadata);
    }

    public void WriteRow(ReadOnlySpan<TPixel> row)
    {
        this.EnsureState(State.Writing);
        if (this.rowsReceived >= this.contentHeight)
        {
            throw new InvalidOperationException($"All {this.contentHeight} content rows have already been written.");
        }

        if (row.Length < this.contentWidth)
        {
            throw new ArgumentException($"The row holds {row.Length} pixels but the content is {this.contentWidth} wide.", nameof(row));
        }

        int index = this.rowsReceived++ - this.sourceStartY;
        if (index < 0 || index >= this.copyHeight)
        {
            return; // Clipped by the canvas.
        }

        this.EmitBackgroundRows(this.destinationStartY + index);
        Span<TPixel> canvasRow = this.row.AsSpan(0, this.canvasWidth);
        if (this.copyWidth < this.canvasWidth)
        {
            canvasRow.Fill(this.background);
        }

        row.Slice(this.sourceStartX, this.copyWidth).CopyTo(canvasRow[this.destinationStartX..]);
        this.next.WriteRow(canvasRow);
        this.rowsEmitted++;
    }

    public void Complete()
    {
        this.EnsureState(State.Writing);
        if (this.rowsReceived != this.contentHeight)
        {
            throw new InvalidOperationException($"Only {this.rowsReceived} of {this.contentHeight} content rows were written.");
        }

        this.EmitBackgroundRows(this.canvasHeight);
        this.state = State.Completed;
        this.Release();
        this.next.Complete();
    }

    public void Dispose()
    {
        if (this.state == State.Disposed)
        {
            return;
        }

        this.state = State.Disposed;
        this.Release();
        this.next.Dispose();
    }

    /// <summary>Emits background rows until <paramref name="untilRow"/> canvas rows have been written.</summary>
    private void EmitBackgroundRows(int untilRow)
    {
        if (this.rowsEmitted >= untilRow)
        {
            return;
        }

        Span<TPixel> canvasRow = this.row.AsSpan(0, this.canvasWidth);
        canvasRow.Fill(this.background);
        while (this.rowsEmitted < untilRow)
        {
            this.next.WriteRow(canvasRow);
            this.rowsEmitted++;
        }
    }

    private void EnsureState(State expected)
    {
        ObjectDisposedException.ThrowIf(this.state == State.Disposed, this);
        if (this.state != expected)
        {
            throw new InvalidOperationException($"The canvas stage is {this.state}; this call needs it {expected}.");
        }
    }

    private void Release()
    {
        if (this.row is not null)
        {
            ArrayPool<TPixel>.Shared.Return(this.row);
            this.row = null;
        }
    }
}
