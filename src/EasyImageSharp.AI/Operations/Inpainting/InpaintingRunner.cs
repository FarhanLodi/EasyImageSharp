using System.Numerics;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;
using Microsoft.ML.OnnxRuntime;

namespace EasyImageSharp.AI;

/// <summary>
/// The synchronous core of <c>Inpaint</c> (LaMa contract). Masked pixels are grouped into regions on an 8 px grid; each
/// region is filled from a crop around it (at least the model's working size, matched to its aspect ratio, shifted
/// inside the image) that is downscaled when larger than the model input and mirror-padded up to it. Only hole pixels
/// are written back, at full resolution; every other pixel keeps its exact original value. Regions lying entirely in
/// one crop share a single inference, and holes already filled by an earlier crop are context for later ones.
/// </summary>
internal static class InpaintingRunner
{
    /// <summary>Region-planning granularity: holes closer than this merge into one region.</summary>
    internal const int CellSize = 8;

    private const int DynamicSizeMultiple = 8;
    private const byte Keep = 0;
    private const byte Hole = 1;
    private const byte Filled = 2;

    /// <summary>Fills the pixels of every frame where <paramref name="mask"/> is at or above the threshold.</summary>
    public static void Inpaint<TPixel>(InferenceSession session, Image<TPixel> image, Image<L8> mask, InpaintingOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        options.Validate();
        for (int i = 0; i < image.Frames.Count; i++)
        {
            ImageFrame<TPixel> frame = image.Frames[i];
            if (frame.Width != mask.Width || frame.Height != mask.Height)
            {
                throw new ArgumentException(
                    $"The mask is {mask.Width}x{mask.Height} but frame {i} is {frame.Width}x{frame.Height}; the mask must match the image size.", nameof(mask));
            }
        }

        byte[] holes = Dilate(HolesFromMask(mask.Frames.RootFrame, options.MaskThreshold), mask.Width, mask.Height, options.MaskDilation);
        Run(session, image, _ => (byte[])holes.Clone(), options, cancellationToken);
    }

    /// <summary>Fills the given rectangles (clipped to each frame) of every frame.</summary>
    public static void Inpaint<TPixel>(InferenceSession session, Image<TPixel> image, IReadOnlyList<Rectangle> regions, InpaintingOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        options.Validate();
        Run(session, image, size => Dilate(HolesFromRectangles(size, regions), size.Width, size.Height, options.MaskDilation), options, cancellationToken);
    }

    private static void Run<TPixel>(InferenceSession session, Image<TPixel> image, Func<Size, byte[]> holesFor, InpaintingOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelBinding binding = Bind(session, options);
        var maps = new byte[image.Frames.Count][];
        bool anyHole = false;
        for (int i = 0; i < maps.Length; i++)
        {
            maps[i] = holesFor(new Size(image.Frames[i].Width, image.Frames[i].Height));
            anyHole |= Array.IndexOf(maps[i], Hole) >= 0;
        }

        if (!anyHole)
        {
            return;
        }

        int index = 0;
        AiOperations.ForEachFrame(image, frame => InpaintFrame(session, binding, frame, maps[index++], options, cancellationToken));
    }

    // ----- Model binding -----

    /// <summary>The resolved inputs / output of an inpainting export and the size its input runs at.</summary>
    internal readonly record struct ModelBinding(string ImageInput, string MaskInput, string Output, int Width, int Height, bool IsFixedSize);

    internal static ModelBinding Bind(InferenceSession session, InpaintingOptions options)
    {
        IReadOnlyDictionary<string, NodeMetadata> inputs = session.InputMetadata;
        if (inputs.Count != 2)
        {
            throw new ModelContractException(
                $"An inpainting model must have two inputs, image [N,3,H,W] and mask [N,1,H,W]; this one has {inputs.Count}.");
        }

        string imageName, maskName;
        if (inputs.ContainsKey("image") && inputs.ContainsKey("mask"))
        {
            (imageName, maskName) = ("image", "mask");
        }
        else
        {
            string[] names = inputs.Keys.ToArray();
            bool firstIsMask = inputs[names[0]].Dimensions is [_, 1, ..];
            bool secondIsMask = inputs[names[1]].Dimensions is [_, 1, ..];
            if (firstIsMask == secondIsMask)
            {
                throw new ModelContractException(
                    "Cannot tell the inpainting model's image and mask inputs apart: name them 'image' and 'mask', or give the mask one channel.");
            }

            (imageName, maskName) = firstIsMask ? (names[1], names[0]) : (names[0], names[1]);
        }

        int[] imageDims = ValidateInput(inputs[imageName], imageName, 3);
        int[] maskDims = ValidateInput(inputs[maskName], maskName, 1);
        if (imageDims[2] != maskDims[2] || imageDims[3] != maskDims[3])
        {
            throw new ModelContractException(
                $"The inpainting model's image [{string.Join(",", imageDims)}] and mask [{string.Join(",", maskDims)}] inputs must have the same spatial size.");
        }

        string output = OrtRunner.ResolveOutputName(session, "output");
        int height = imageDims[2];
        int width = imageDims[3];
        if (width > 0 && height > 0)
        {
            return new ModelBinding(imageName, maskName, output, width, height, IsFixedSize: true);
        }

        if (width > 0 || height > 0)
        {
            throw new ModelContractException("The inpainting model's spatial axes must be both fixed or both dynamic.");
        }

        return new ModelBinding(imageName, maskName, output, options.WorkingSize, options.WorkingSize, IsFixedSize: false);
    }

    private static int[] ValidateInput(NodeMetadata metadata, string name, int channels)
    {
        int[] dims = metadata.Dimensions;
        if (metadata.ElementType != typeof(float) || dims.Length != 4 || (dims[1] > 0 && dims[1] != channels))
        {
            throw new ModelContractException(
                $"Inpainting input '{name}' must be a float32 [N,{channels},H,W] tensor; the model declares {metadata.ElementType.Name} [{string.Join(",", dims)}].");
        }

        return dims;
    }

    // ----- Hole maps -----

    internal static byte[] HolesFromMask(ImageFrame<L8> mask, byte threshold)
    {
        var holes = new byte[checked(mask.Width * mask.Height)];
        for (int y = 0; y < mask.Height; y++)
        {
            Span<L8> row = mask.GetRowSpan(y);
            int offset = y * mask.Width;
            for (int x = 0; x < row.Length; x++)
            {
                holes[offset + x] = row[x].PackedValue >= threshold ? Hole : Keep;
            }
        }

        return holes;
    }

    internal static byte[] HolesFromRectangles(Size size, IReadOnlyList<Rectangle> regions)
    {
        var holes = new byte[checked(size.Width * size.Height)];
        foreach (Rectangle region in regions)
        {
            // Clip in 64-bit so extreme coordinates cannot overflow.
            long left = Math.Max(0L, region.X);
            long top = Math.Max(0L, region.Y);
            long right = Math.Min(size.Width, (long)region.X + region.Width);
            long bottom = Math.Min(size.Height, (long)region.Y + region.Height);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            for (long y = top; y < bottom; y++)
            {
                holes.AsSpan((int)((y * size.Width) + left), (int)(right - left)).Fill(Hole);
            }
        }

        return holes;
    }

    /// <summary>Grows the holes by <paramref name="radius"/> pixels (square structuring element, two separable passes).</summary>
    internal static byte[] Dilate(byte[] holes, int width, int height, int radius)
    {
        if (radius == 0)
        {
            return holes;
        }

        var horizontal = new byte[holes.Length];
        var counts = new int[Math.Max(width, height) + 1];
        for (int y = 0; y < height; y++)
        {
            int offset = y * width;
            for (int x = 0; x < width; x++)
            {
                counts[x + 1] = counts[x] + (holes[offset + x] == Hole ? 1 : 0);
            }

            for (int x = 0; x < width; x++)
            {
                int from = Math.Max(0, x - radius);
                int to = Math.Min(width, x + radius + 1);
                horizontal[offset + x] = counts[to] - counts[from] > 0 ? Hole : Keep;
            }
        }

        var result = new byte[holes.Length];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                counts[y + 1] = counts[y] + (horizontal[(y * width) + x] == Hole ? 1 : 0);
            }

            for (int y = 0; y < height; y++)
            {
                int from = Math.Max(0, y - radius);
                int to = Math.Min(height, y + radius + 1);
                result[(y * width) + x] = counts[to] - counts[from] > 0 ? Hole : Keep;
            }
        }

        return result;
    }

    // ----- Region planning -----

    /// <summary>
    /// Bounding boxes (snapped outward to the <see cref="CellSize"/> grid and clipped to the frame) of the 8-connected
    /// groups of grid cells that contain at least one hole, in scan order of their first cell. A group longer than
    /// <paramref name="splitSize"/> whose mean thickness (hole cells per cell of its longer side) is at most a quarter of
    /// <paramref name="splitSize"/>, like a wire, a scribble or an outline, is split into chunks of half that size, so each
    /// chunk is filled at native resolution from its own surroundings instead of from one heavily downscaled crop; solid
    /// groups stay whole so the model sees their full context. 0 disables splitting.
    /// </summary>
    internal static List<Rectangle> FindRegions(byte[] state, int width, int height, int splitSize = 0)
    {
        int gridWidth = (width + CellSize - 1) / CellSize;
        int gridHeight = (height + CellSize - 1) / CellSize;
        var occupied = new bool[gridWidth * gridHeight];
        for (int y = 0; y < height; y++)
        {
            int offset = y * width;
            int cellRow = (y / CellSize) * gridWidth;
            for (int x = 0; x < width; x++)
            {
                if (state[offset + x] == Hole)
                {
                    occupied[cellRow + (x / CellSize)] = true;
                }
            }
        }

        var regions = new List<Rectangle>();
        var visited = new bool[occupied.Length];
        var stack = new Stack<int>();
        var cells = new List<int>();
        int chunkCells = splitSize / 2 / CellSize;
        for (int start = 0; start < occupied.Length; start++)
        {
            if (!occupied[start] || visited[start])
            {
                continue;
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            cells.Clear();
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int cell = stack.Pop();
                cells.Add(cell);
                int cx = cell % gridWidth;
                int cy = cell / gridWidth;
                minX = Math.Min(minX, cx);
                maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy);
                maxY = Math.Max(maxY, cy);
                for (int ny = Math.Max(0, cy - 1); ny <= Math.Min(gridHeight - 1, cy + 1); ny++)
                {
                    for (int nx = Math.Max(0, cx - 1); nx <= Math.Min(gridWidth - 1, cx + 1); nx++)
                    {
                        int neighbour = (ny * gridWidth) + nx;
                        if (occupied[neighbour] && !visited[neighbour])
                        {
                            visited[neighbour] = true;
                            stack.Push(neighbour);
                        }
                    }
                }
            }

            // Mean thickness of the group = occupied cells / cells along its longer side.
            long longSide = Math.Max(maxX - minX + 1, maxY - minY + 1);
            bool large = longSide * CellSize > splitSize;
            bool thin = (long)cells.Count * CellSize * 4 <= longSide * splitSize;
            if (chunkCells < 1 || !large || !thin)
            {
                regions.Add(CellBounds(minX, minY, maxX, maxY, width, height));
                continue;
            }

            // Sparse and large: bucket the group's cells into chunkCells-sized squares, in row-major chunk order.
            int chunkColumns = ((maxX - minX) / chunkCells) + 1;
            var chunks = new SortedDictionary<int, (int MinX, int MinY, int MaxX, int MaxY)>();
            foreach (int cell in cells)
            {
                int cx = cell % gridWidth;
                int cy = cell / gridWidth;
                int key = (((cy - minY) / chunkCells) * chunkColumns) + ((cx - minX) / chunkCells);
                chunks[key] = chunks.TryGetValue(key, out var box)
                    ? (Math.Min(box.MinX, cx), Math.Min(box.MinY, cy), Math.Max(box.MaxX, cx), Math.Max(box.MaxY, cy))
                    : (cx, cy, cx, cy);
            }

            foreach ((int chunkMinX, int chunkMinY, int chunkMaxX, int chunkMaxY) in chunks.Values)
            {
                regions.Add(CellBounds(chunkMinX, chunkMinY, chunkMaxX, chunkMaxY, width, height));
            }
        }

        return regions;
    }

    private static Rectangle CellBounds(int minX, int minY, int maxX, int maxY, int width, int height)
        => Rectangle.FromLTRB(minX * CellSize, minY * CellSize, Math.Min(width, (maxX + 1) * CellSize), Math.Min(height, (maxY + 1) * CellSize));

    /// <summary>
    /// The crop that gives the model context around <paramref name="region"/>: the region plus
    /// <paramref name="contextMargin"/> times its longer side on every side, widened to the model's aspect ratio and to at
    /// least the model size, clipped to the frame and centred on the region as far as the frame allows. Always contains
    /// the region.
    /// </summary>
    internal static Rectangle PlanCrop(Rectangle region, int width, int height, int modelWidth, int modelHeight, float contextMargin)
    {
        long margin = (long)Math.Ceiling(Math.Max(region.Width, region.Height) * (double)contextMargin);
        long cropWidth = region.Width + (2 * margin);
        long cropHeight = region.Height + (2 * margin);
        if (cropWidth * modelHeight < cropHeight * modelWidth)
        {
            cropWidth = ((cropHeight * modelWidth) + modelHeight - 1) / modelHeight;
        }
        else
        {
            cropHeight = ((cropWidth * modelHeight) + modelWidth - 1) / modelWidth;
        }

        cropWidth = Math.Min(Math.Max(cropWidth, modelWidth), width);
        cropHeight = Math.Min(Math.Max(cropHeight, modelHeight), height);
        long left = Math.Clamp(region.X + (long)Math.Floor((region.Width - cropWidth) / 2.0), 0, width - cropWidth);
        long top = Math.Clamp(region.Y + (long)Math.Floor((region.Height - cropHeight) / 2.0), 0, height - cropHeight);
        return new Rectangle((int)left, (int)top, (int)cropWidth, (int)cropHeight);
    }

    // ----- Per-frame fill -----

    private static Image<TPixel> InpaintFrame<TPixel>(
        InferenceSession session, ModelBinding binding, Image<TPixel> frame, byte[] state, InpaintingOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        Image<TPixel> result = frame.Clone();
        try
        {
            List<Rectangle> regions = FindRegions(state, frame.Width, frame.Height, Math.Min(binding.Width, binding.Height));
            var done = new bool[regions.Count];
            for (int i = 0; i < regions.Count; i++)
            {
                if (done[i])
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                Rectangle crop = PlanCrop(regions[i], frame.Width, frame.Height, binding.Width, binding.Height, options.ContextMargin);
                var fill = new List<Rectangle> { regions[i] };
                done[i] = true;
                for (int j = i + 1; j < regions.Count; j++)
                {
                    if (!done[j] && crop.Contains(regions[j]))
                    {
                        done[j] = true;
                        fill.Add(regions[j]);
                    }
                }

                FillCrop(session, binding, result, state, crop, fill, cancellationToken);
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static void FillCrop<TPixel>(
        InferenceSession session, ModelBinding binding, Image<TPixel> target, byte[] state, Rectangle crop, List<Rectangle> fill, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        double scale = Math.Min(1.0, Math.Min((double)binding.Width / crop.Width, (double)binding.Height / crop.Height));
        int scaledWidth = Math.Clamp((int)Math.Round(crop.Width * scale), 1, binding.Width);
        int scaledHeight = Math.Clamp((int)Math.Round(crop.Height * scale), 1, binding.Height);
        bool rescaled = scaledWidth != crop.Width || scaledHeight != crop.Height;
        int inputWidth = binding.IsFixedSize ? binding.Width : RoundUp(scaledWidth, DynamicSizeMultiple);
        int inputHeight = binding.IsFixedSize ? binding.Height : RoundUp(scaledHeight, DynamicSizeMultiple);

        float[] imageTensor;
        float[] maskTensor;
        using (Image<TPixel> cropImage = FrameUtil.CropCopy(target, crop))
        {
            Image<TPixel> scaled = rescaled ? FrameUtil.ResizeCopy(cropImage, scaledWidth, scaledHeight, KnownResamplers.Bicubic) : cropImage;
            try
            {
                byte[] scaledMask = DownscaleHoles(state, target.Width, crop, scaledWidth, scaledHeight);
                (imageTensor, maskTensor) = BuildInputs(scaled.Frames.RootFrame, scaledMask, inputWidth, inputHeight);
            }
            finally
            {
                if (rescaled)
                {
                    scaled.Dispose();
                }
            }
        }

        TensorResult output = VisionInference.Run(
            session,
            [
                new ModelInput(binding.ImageInput, imageTensor, [1, 3, inputHeight, inputWidth]),
                new ModelInput(binding.MaskInput, maskTensor, [1, 1, inputHeight, inputWidth]),
            ],
            [binding.Output],
            cancellationToken)[0];
        if (output.Shape.Length != 4 || output.Shape[0] != 1 || output.Shape[1] != 3 || output.Shape[2] != inputHeight || output.Shape[3] != inputWidth)
        {
            throw new ModelContractException(
                $"The inpainting model must return [1,3,{inputHeight},{inputWidth}] (RGB in 0-255); output '{binding.Output}' has shape [{string.Join(",", output.Shape)}].");
        }

        using Image<Rgb24> patch = ToPatch(output.Data, inputWidth, inputHeight, scaledWidth, scaledHeight);
        if (!rescaled)
        {
            Composite(target, state, crop, patch.Frames.RootFrame, fill);
            return;
        }

        using Image<Rgb24> full = FrameUtil.ResizeCopy(patch, crop.Width, crop.Height, KnownResamplers.Bicubic);
        Composite(target, state, crop, full.Frames.RootFrame, fill);
    }

    /// <summary>
    /// The model-resolution mask of a crop: a model pixel is a hole when any unfilled hole pixel falls in its source
    /// footprint, so a downscaled hole never shrinks.
    /// </summary>
    internal static byte[] DownscaleHoles(byte[] state, int stride, Rectangle crop, int width, int height)
    {
        var mask = new byte[width * height];
        var columnStart = new int[width];
        var columnEnd = new int[width];
        for (int x = 0; x < width; x++)
        {
            columnStart[x] = (int)((long)x * crop.Width / width);
            columnEnd[x] = Math.Max(columnStart[x] + 1, (int)((((long)x + 1) * crop.Width + width - 1) / width));
        }

        for (int y = 0; y < height; y++)
        {
            int rowStart = (int)((long)y * crop.Height / height);
            int rowEnd = Math.Max(rowStart + 1, (int)((((long)y + 1) * crop.Height + height - 1) / height));
            for (int x = 0; x < width; x++)
            {
                bool hole = false;
                for (int sy = rowStart; sy < rowEnd && !hole; sy++)
                {
                    int offset = ((crop.Y + sy) * stride) + crop.X;
                    for (int sx = columnStart[x]; sx < columnEnd[x]; sx++)
                    {
                        if (state[offset + sx] == Hole)
                        {
                            hole = true;
                            break;
                        }
                    }
                }

                mask[(y * width) + x] = hole ? (byte)1 : (byte)0;
            }
        }

        return mask;
    }

    /// <summary>Planar RGB 0-1 image and 0/1 mask tensors, mirror-padded (edge sample repeated) on the right / bottom.</summary>
    private static (float[] Image, float[] Mask) BuildInputs<TPixel>(ImageFrame<TPixel> source, byte[] holes, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        int plane = width * height;
        var image = new float[3 * plane];
        var mask = new float[plane];
        var columns = new int[width];
        for (int x = 0; x < width; x++)
        {
            columns[x] = Mirror(x, source.Width);
        }

        var row = new Rgba32[source.Width];
        for (int y = 0; y < height; y++)
        {
            int sourceY = Mirror(y, source.Height);
            Span<TPixel> pixels = source.GetRowSpan(sourceY);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = pixels[x].ToRgba32();
            }

            for (int x = 0; x < width; x++)
            {
                Rgba32 p = row[columns[x]];
                int i = (y * width) + x;
                image[i] = p.R / 255f;
                image[plane + i] = p.G / 255f;
                image[(2 * plane) + i] = p.B / 255f;
                mask[i] = holes[(sourceY * source.Width) + columns[x]];
            }
        }

        return (image, mask);
    }

    /// <summary>The top-left <paramref name="width"/> x <paramref name="height"/> of a planar 0-255 RGB output.</summary>
    private static Image<Rgb24> ToPatch(float[] data, int stride, int rows, int width, int height)
    {
        int plane = stride * rows;
        var patch = new Image<Rgb24>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb24> row = patch.Frames.RootFrame.GetRowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int i = (y * stride) + x;
                row[x] = new Rgb24(ToByte(data[i]), ToByte(data[plane + i]), ToByte(data[(2 * plane) + i]));
            }
        }

        return patch;
    }

    /// <summary>Writes the patch into every still-unfilled hole pixel of the fill rectangles, keeping each pixel's alpha.</summary>
    private static void Composite<TPixel>(Image<TPixel> target, byte[] state, Rectangle crop, ImageFrame<Rgb24> patch, List<Rectangle> fill)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ImageFrame<TPixel> frame = target.Frames.RootFrame;
        int stride = target.Width;
        foreach (Rectangle rect in fill)
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                Span<TPixel> row = frame.GetRowSpan(y);
                Span<Rgb24> patchRow = patch.GetRowSpan(y - crop.Y);
                int offset = y * stride;
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    if (state[offset + x] != Hole)
                    {
                        continue;
                    }

                    Rgb24 p = patchRow[x - crop.X];
                    float alpha = row[x].ToScaledVector4().W;
                    row[x] = TPixel.FromScaledVector4(new Vector4(p.R / 255f, p.G / 255f, p.B / 255f, alpha));
                    state[offset + x] = Filled;
                }
            }
        }
    }

    /// <summary>Symmetric (edge-repeating) reflection of index <paramref name="i"/> into 0..n-1.</summary>
    internal static int Mirror(int i, int n)
    {
        if (n == 1)
        {
            return 0;
        }

        int period = 2 * n;
        i %= period;
        if (i < 0)
        {
            i += period;
        }

        return i < n ? i : period - 1 - i;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;
}
