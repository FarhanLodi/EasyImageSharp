namespace EasyImageSharp.Metadata.Exif;

/// <summary>A single EXIF field: a tag together with its value.</summary>
public interface IExifValue : IDeepCloneable<IExifValue>
{
    /// <summary>The tag this value belongs to.</summary>
    ExifTag Tag { get; }

    /// <summary>The field type the value is (or will be) stored as.</summary>
    ExifDataType DataType { get; }

    /// <summary>True when the value holds several elements (an array).</summary>
    bool IsArray { get; }

    /// <summary>Returns the value as an untyped object (a scalar, a string or an array).</summary>
    object? GetValue();

    /// <summary>
    /// Sets the value from an untyped object, converting between compatible representations (e.g. an
    /// <see cref="int"/> for a <see cref="ushort"/> field, or a single element for an array field).
    /// Returns false when the object cannot be represented by this field's type.
    /// </summary>
    bool TrySetValue(object? value);
}

/// <summary>A strongly typed EXIF field.</summary>
/// <typeparam name="TValueType">The CLR type of the value.</typeparam>
public interface IExifValue<TValueType> : IExifValue
{
    /// <summary>The value. Setting it changes the profile the value was obtained from.</summary>
    TValueType? Value { get; set; }
}

/// <summary>Default <see cref="IExifValue{TValueType}"/> implementation.</summary>
internal sealed class ExifValue<TValueType> : IExifValue<TValueType>
{
    private static readonly bool IsArrayType = typeof(TValueType).IsArray;

    public ExifValue(ExifTag tag, ExifDataType dataType, TValueType? value)
    {
        this.Tag = tag;
        this.DataType = dataType;
        this.Value = value;
    }

    public ExifTag Tag { get; }

    public ExifDataType DataType { get; private set; }

    public bool IsArray => IsArrayType;

    public TValueType? Value { get; set; }

    public object? GetValue() => this.Value;

    public bool TrySetValue(object? value)
    {
        if (value is null)
        {
            this.Value = default;
            return true;
        }

        if (!ExifValueConverter.TryConvert(value, out TValueType? converted))
        {
            return false;
        }

        this.Value = converted;
        return true;
    }

    /// <summary>Changes the stored field type (used when a value is re-typed on write).</summary>
    internal void SetDataType(ExifDataType dataType) => this.DataType = dataType;

    public IExifValue DeepClone()
        => new ExifValue<TValueType>(this.Tag, this.DataType, ExifValueConverter.CloneValue(this.Value));

    public override string ToString() => $"{this.Tag.Name}: {ExifValueConverter.Format(this.Value)}";
}
