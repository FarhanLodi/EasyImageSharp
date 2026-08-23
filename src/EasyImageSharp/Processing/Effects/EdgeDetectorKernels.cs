namespace EasyImageSharp.Processing;

/// <summary>An edge detector built from a single convolution kernel (e.g. a Laplacian).</summary>
public readonly struct EdgeDetectorKernel : IEquatable<EdgeDetectorKernel>
{
    /// <summary>Initializes the detector from its kernel.</summary>
    public EdgeDetectorKernel(DenseMatrix<float> kernel)
    {
        if (kernel.Count == 0)
        {
            throw new ArgumentException("The kernel must not be empty.", nameof(kernel));
        }

        this.Kernel = kernel;
    }

    /// <summary>The convolution kernel.</summary>
    public DenseMatrix<float> Kernel { get; }

    /// <inheritdoc/>
    public bool Equals(EdgeDetectorKernel other) => this.Kernel.Equals(other.Kernel);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EdgeDetectorKernel other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => this.Kernel.GetHashCode();

    /// <summary>Whether two detectors use the same kernel.</summary>
    public static bool operator ==(EdgeDetectorKernel left, EdgeDetectorKernel right) => left.Equals(right);

    /// <summary>Whether two detectors differ.</summary>
    public static bool operator !=(EdgeDetectorKernel left, EdgeDetectorKernel right) => !left.Equals(right);
}

/// <summary>
/// An edge detector built from horizontal and vertical gradient kernels; the result is the gradient
/// magnitude <c>sqrt(gx² + gy²)</c> per channel.
/// </summary>
public readonly struct EdgeDetector2DKernel : IEquatable<EdgeDetector2DKernel>
{
    /// <summary>Initializes the detector from its two gradient kernels.</summary>
    public EdgeDetector2DKernel(DenseMatrix<float> kernelX, DenseMatrix<float> kernelY)
    {
        if (kernelX.Count == 0 || kernelY.Count == 0)
        {
            throw new ArgumentException("Kernels must not be empty.");
        }

        this.KernelX = kernelX;
        this.KernelY = kernelY;
    }

    /// <summary>The horizontal gradient kernel.</summary>
    public DenseMatrix<float> KernelX { get; }

    /// <summary>The vertical gradient kernel.</summary>
    public DenseMatrix<float> KernelY { get; }

    /// <inheritdoc/>
    public bool Equals(EdgeDetector2DKernel other) => this.KernelX.Equals(other.KernelX) && this.KernelY.Equals(other.KernelY);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EdgeDetector2DKernel other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(this.KernelX, this.KernelY);

    /// <summary>Whether two detectors use the same kernels.</summary>
    public static bool operator ==(EdgeDetector2DKernel left, EdgeDetector2DKernel right) => left.Equals(right);

    /// <summary>Whether two detectors differ.</summary>
    public static bool operator !=(EdgeDetector2DKernel left, EdgeDetector2DKernel right) => !left.Equals(right);
}

/// <summary>
/// A compass edge detector built from eight directional kernels; the result is the maximum response over
/// all directions per channel.
/// </summary>
public readonly struct EdgeDetectorCompassKernel : IEquatable<EdgeDetectorCompassKernel>
{
    /// <summary>Initializes the detector from its eight directional kernels.</summary>
    public EdgeDetectorCompassKernel(
        DenseMatrix<float> north,
        DenseMatrix<float> northWest,
        DenseMatrix<float> west,
        DenseMatrix<float> southWest,
        DenseMatrix<float> south,
        DenseMatrix<float> southEast,
        DenseMatrix<float> east,
        DenseMatrix<float> northEast)
    {
        this.North = north;
        this.NorthWest = northWest;
        this.West = west;
        this.SouthWest = southWest;
        this.South = south;
        this.SouthEast = southEast;
        this.East = east;
        this.NorthEast = northEast;
        foreach (DenseMatrix<float> kernel in this.Flatten())
        {
            if (kernel.Count == 0)
            {
                throw new ArgumentException("Kernels must not be empty.");
            }
        }
    }

    /// <summary>The north kernel.</summary>
    public DenseMatrix<float> North { get; }

    /// <summary>The north-west kernel.</summary>
    public DenseMatrix<float> NorthWest { get; }

    /// <summary>The west kernel.</summary>
    public DenseMatrix<float> West { get; }

    /// <summary>The south-west kernel.</summary>
    public DenseMatrix<float> SouthWest { get; }

    /// <summary>The south kernel.</summary>
    public DenseMatrix<float> South { get; }

    /// <summary>The south-east kernel.</summary>
    public DenseMatrix<float> SouthEast { get; }

    /// <summary>The east kernel.</summary>
    public DenseMatrix<float> East { get; }

    /// <summary>The north-east kernel.</summary>
    public DenseMatrix<float> NorthEast { get; }

    /// <summary>Returns the eight kernels in order N, NW, W, SW, S, SE, E, NE.</summary>
    public DenseMatrix<float>[] Flatten() =>
    [
        this.North, this.NorthWest, this.West, this.SouthWest,
        this.South, this.SouthEast, this.East, this.NorthEast,
    ];

    /// <inheritdoc/>
    public bool Equals(EdgeDetectorCompassKernel other)
        => this.North.Equals(other.North) && this.NorthWest.Equals(other.NorthWest)
        && this.West.Equals(other.West) && this.SouthWest.Equals(other.SouthWest)
        && this.South.Equals(other.South) && this.SouthEast.Equals(other.SouthEast)
        && this.East.Equals(other.East) && this.NorthEast.Equals(other.NorthEast);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EdgeDetectorCompassKernel other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(this.North, this.NorthWest, this.West, this.SouthWest, this.South, this.SouthEast, this.East, this.NorthEast);

    /// <summary>Whether two detectors use the same kernels.</summary>
    public static bool operator ==(EdgeDetectorCompassKernel left, EdgeDetectorCompassKernel right) => left.Equals(right);

    /// <summary>Whether two detectors differ.</summary>
    public static bool operator !=(EdgeDetectorCompassKernel left, EdgeDetectorCompassKernel right) => !left.Equals(right);
}

/// <summary>The standard edge detection kernels.</summary>
public static class KnownEdgeDetectorKernels
{
    /// <summary>The Kayyali operator (diagonal gradient pair).</summary>
    public static EdgeDetector2DKernel Kayyali { get; } = new(
        new float[,] { { 6, 0, -6 }, { 0, 0, 0 }, { -6, 0, 6 } },
        new float[,] { { -6, 0, 6 }, { 0, 0, 0 }, { 6, 0, -6 } });

    /// <summary>The Kirsch compass operator (eight 3x3 kernels).</summary>
    public static EdgeDetectorCompassKernel Kirsch { get; } = new(
        north: new float[,] { { 5, 5, 5 }, { -3, 0, -3 }, { -3, -3, -3 } },
        northWest: new float[,] { { 5, 5, -3 }, { 5, 0, -3 }, { -3, -3, -3 } },
        west: new float[,] { { 5, -3, -3 }, { 5, 0, -3 }, { 5, -3, -3 } },
        southWest: new float[,] { { -3, -3, -3 }, { 5, 0, -3 }, { 5, 5, -3 } },
        south: new float[,] { { -3, -3, -3 }, { -3, 0, -3 }, { 5, 5, 5 } },
        southEast: new float[,] { { -3, -3, -3 }, { -3, 0, 5 }, { -3, 5, 5 } },
        east: new float[,] { { -3, -3, 5 }, { -3, 0, 5 }, { -3, -3, 5 } },
        northEast: new float[,] { { -3, 5, 5 }, { -3, 0, 5 }, { -3, -3, -3 } });

    /// <summary>The 3x3 Laplacian operator (8-connected).</summary>
    public static EdgeDetectorKernel Laplacian3x3 { get; } = new(
        new float[,] { { -1, -1, -1 }, { -1, 8, -1 }, { -1, -1, -1 } });

    /// <summary>The 5x5 Laplacian operator.</summary>
    public static EdgeDetectorKernel Laplacian5x5 { get; } = new(
        new float[,]
        {
            { -1, -1, -1, -1, -1 },
            { -1, -1, -1, -1, -1 },
            { -1, -1, 24, -1, -1 },
            { -1, -1, -1, -1, -1 },
            { -1, -1, -1, -1, -1 },
        });

    /// <summary>The 5x5 Laplacian of Gaussian operator.</summary>
    public static EdgeDetectorKernel LaplacianOfGaussian { get; } = new(
        new float[,]
        {
            { 0, 0, -1, 0, 0 },
            { 0, -1, -2, -1, 0 },
            { -1, -2, 16, -2, -1 },
            { 0, -1, -2, -1, 0 },
            { 0, 0, -1, 0, 0 },
        });

    /// <summary>The Prewitt operator.</summary>
    public static EdgeDetector2DKernel Prewitt { get; } = new(
        new float[,] { { -1, 0, 1 }, { -1, 0, 1 }, { -1, 0, 1 } },
        new float[,] { { 1, 1, 1 }, { 0, 0, 0 }, { -1, -1, -1 } });

    /// <summary>The Roberts cross operator (2x2 kernels anchored at the top-left pixel).</summary>
    public static EdgeDetector2DKernel RobertsCross { get; } = new(
        new float[,] { { 1, 0 }, { 0, -1 } },
        new float[,] { { 0, 1 }, { -1, 0 } });

    /// <summary>The Robinson compass operator (eight 3x3 kernels).</summary>
    public static EdgeDetectorCompassKernel Robinson { get; } = new(
        north: new float[,] { { 1, 2, 1 }, { 0, 0, 0 }, { -1, -2, -1 } },
        northWest: new float[,] { { 2, 1, 0 }, { 1, 0, -1 }, { 0, -1, -2 } },
        west: new float[,] { { 1, 0, -1 }, { 2, 0, -2 }, { 1, 0, -1 } },
        southWest: new float[,] { { 0, -1, -2 }, { 1, 0, -1 }, { 2, 1, 0 } },
        south: new float[,] { { -1, -2, -1 }, { 0, 0, 0 }, { 1, 2, 1 } },
        southEast: new float[,] { { -2, -1, 0 }, { -1, 0, 1 }, { 0, 1, 2 } },
        east: new float[,] { { -1, 0, 1 }, { -2, 0, 2 }, { -1, 0, 1 } },
        northEast: new float[,] { { 0, 1, 2 }, { -1, 0, 1 }, { -2, -1, 0 } });

    /// <summary>The Scharr operator.</summary>
    public static EdgeDetector2DKernel Scharr { get; } = new(
        new float[,] { { -3, 0, 3 }, { -10, 0, 10 }, { -3, 0, 3 } },
        new float[,] { { 3, 10, 3 }, { 0, 0, 0 }, { -3, -10, -3 } });

    /// <summary>The Sobel operator.</summary>
    public static EdgeDetector2DKernel Sobel { get; } = new(
        new float[,] { { -1, 0, 1 }, { -2, 0, 2 }, { -1, 0, 1 } },
        new float[,] { { -1, -2, -1 }, { 0, 0, 0 }, { 1, 2, 1 } });
}
