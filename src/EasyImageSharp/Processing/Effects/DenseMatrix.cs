namespace EasyImageSharp.Processing;

/// <summary>An immutable row-major dense matrix, used for convolution kernels.</summary>
/// <typeparam name="T">The element type.</typeparam>
public readonly struct DenseMatrix<T> : IEquatable<DenseMatrix<T>>
    where T : unmanaged, IEquatable<T>
{
    private readonly T[] data;

    /// <summary>Initializes a matrix from a two-dimensional array (<c>[row, column]</c>).</summary>
    public DenseMatrix(T[,] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        this.Rows = values.GetLength(0);
        this.Columns = values.GetLength(1);
        Guard.MustBePositive(this.Rows, nameof(values));
        Guard.MustBePositive(this.Columns, nameof(values));
        this.data = new T[this.Rows * this.Columns];
        for (int y = 0; y < this.Rows; y++)
        {
            for (int x = 0; x < this.Columns; x++)
            {
                this.data[(y * this.Columns) + x] = values[y, x];
            }
        }
    }

    /// <summary>Initializes a matrix from row-major data.</summary>
    public DenseMatrix(int columns, int rows, ReadOnlySpan<T> values)
    {
        Guard.MustBePositive(columns, nameof(columns));
        Guard.MustBePositive(rows, nameof(rows));
        if (values.Length != columns * rows)
        {
            throw new ArgumentException($"Expected {columns * rows} values for a {columns}x{rows} matrix but got {values.Length}.", nameof(values));
        }

        this.Columns = columns;
        this.Rows = rows;
        this.data = values.ToArray();
    }

    /// <summary>The number of columns (kernel width).</summary>
    public int Columns { get; }

    /// <summary>The number of rows (kernel height).</summary>
    public int Rows { get; }

    /// <summary>The number of elements.</summary>
    public int Count => this.data?.Length ?? 0;

    /// <summary>The row-major elements.</summary>
    public ReadOnlySpan<T> Span => this.data;

    /// <summary>The row-major elements as memory.</summary>
    public ReadOnlyMemory<T> Memory => this.data;

    /// <summary>Gets the element at the given row and column.</summary>
    public T this[int row, int column]
    {
        get
        {
            if ((uint)row >= (uint)this.Rows || (uint)column >= (uint)this.Columns)
            {
                throw new ArgumentOutOfRangeException(nameof(row), $"({row}, {column}) is outside the {this.Columns}x{this.Rows} matrix.");
            }

            return this.data[(row * this.Columns) + column];
        }
    }

    /// <summary>Converts a two-dimensional array to a matrix.</summary>
    public static implicit operator DenseMatrix<T>(T[,] values) => new(values);

    /// <summary>Returns the transposed matrix.</summary>
    public DenseMatrix<T> Transpose()
    {
        // The result has [original columns] rows and [original rows] columns.
        var result = new T[this.Columns, this.Rows];
        for (int y = 0; y < this.Rows; y++)
        {
            for (int x = 0; x < this.Columns; x++)
            {
                result[x, y] = this.data[(y * this.Columns) + x];
            }
        }

        return new DenseMatrix<T>(result);
    }

    /// <inheritdoc/>
    public bool Equals(DenseMatrix<T> other)
    {
        if (this.Columns != other.Columns || this.Rows != other.Rows)
        {
            return false;
        }

        return this.Span.SequenceEqual(other.Span);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DenseMatrix<T> other && this.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(this.Columns);
        hash.Add(this.Rows);
        foreach (T value in this.Span)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => $"DenseMatrix<{typeof(T).Name}> [{this.Columns}x{this.Rows}]";

    /// <summary>Whether two matrices have the same shape and elements.</summary>
    public static bool operator ==(DenseMatrix<T> left, DenseMatrix<T> right) => left.Equals(right);

    /// <summary>Whether two matrices differ.</summary>
    public static bool operator !=(DenseMatrix<T> left, DenseMatrix<T> right) => !left.Equals(right);
}
