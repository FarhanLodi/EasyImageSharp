using System.Globalization;

namespace EasyImageSharp.Metadata.Exif;

/// <summary>
/// Identifies an EXIF field: a 16-bit tag number together with the directory it lives in. Well-known tags
/// are exposed as static properties (<see cref="Orientation"/>, <see cref="Make"/>, ...); any other field can be
/// addressed with <see cref="ExifTag{TValueType}"/>.
/// </summary>
public abstract partial class ExifTag : IEquatable<ExifTag>
{
    private static List<ExifTag>? knownList;
    private static Dictionary<(ExifIfd Ifd, ushort Id), ExifTag>? registry;
    private static Dictionary<ushort, ExifIfd>? defaultIfds;

    internal ExifTag(ushort id, ExifIfd ifd, string? name)
    {
        this.Id = id;
        this.Ifd = ifd;
        this.Name = name ?? TryGetKnown(ifd, id)?.Name ?? string.Create(CultureInfo.InvariantCulture, $"0x{id:X4}");
    }

    /// <summary>The 16-bit tag number.</summary>
    public ushort Id { get; }

    /// <summary>The directory the tag is stored in.</summary>
    public ExifIfd Ifd { get; }

    /// <summary>The well-known name of the tag, or <c>0xNNNN</c> for tags the library does not know.</summary>
    public string Name { get; }

    /// <summary>The CLR type values of this tag are exposed as.</summary>
    internal abstract Type ValueType { get; }

    /// <summary>The tag number as an unsigned short.</summary>
    public static implicit operator ushort(ExifTag tag) => tag?.Id ?? 0;

    /// <summary>Returns a tag with the same number and value type in another directory.</summary>
    internal abstract ExifTag WithIfd(ExifIfd ifd);

    /// <summary>
    /// Creates a value of this tag's CLR type from decoded raw data, converting numeric shapes where possible.
    /// Returns <see langword="null"/> when the raw data cannot be represented (the caller then keeps it raw).
    /// </summary>
    internal abstract IExifValue? TryCreateValue(ExifDataType dataType, object? raw);

    /// <summary>Creates a value holding <paramref name="value"/>, which must already be of this tag's CLR type.</summary>
    internal abstract IExifValue CreateValue(ExifDataType dataType, object? value);

    /// <summary>The default field type used when a value of this tag is written and no better one is known.</summary>
    internal ExifDataType DefaultDataType => ExifValueConverter.DefaultDataType(this.ValueType, this.IsUndefinedByConvention);

    /// <summary>True for tags whose payload the EXIF specification types as UNDEFINED even though it is text or bytes.</summary>
    internal bool IsUndefinedByConvention => UndefinedTags.Contains((this.Ifd, this.Id));

    public bool Equals(ExifTag? other) => other is not null && other.Id == this.Id && other.Ifd == this.Ifd;

    public override bool Equals(object? obj) => obj is ExifTag other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Id, (int)this.Ifd);

    public static bool operator ==(ExifTag? left, ExifTag? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ExifTag? left, ExifTag? right) => !(left == right);

    public override string ToString() => this.Name;

    /// <summary>Returns the well-known tag definition for the given directory and number, if any.</summary>
    internal static ExifTag? TryGetKnown(ExifIfd ifd, ushort id)
    {
        Dictionary<(ExifIfd, ushort), ExifTag> map = registry ??= BuildRegistry();
        if (map.TryGetValue((ifd, id), out ExifTag? tag))
        {
            return tag;
        }

        // The thumbnail directory reuses the TIFF tag space of IFD0.
        return ifd == ExifIfd.Ifd1 && map.TryGetValue((ExifIfd.Ifd0, id), out ExifTag? ifd0Tag)
            ? ifd0Tag.WithIfd(ExifIfd.Ifd1)
            : null;
    }

    /// <summary>The directory a bare tag number most likely belongs to (IFD0 for numbers the library does not know).</summary>
    internal static ExifIfd DefaultIfd(ushort id)
    {
        Dictionary<ushort, ExifIfd> map = defaultIfds ??= BuildDefaultIfds();
        return map.TryGetValue(id, out ExifIfd ifd) ? ifd : ExifIfd.Ifd0;
    }

    private static T Register<T>(T tag)
        where T : ExifTag
    {
        (knownList ??= new List<ExifTag>(160)).Add(tag);
        return tag;
    }

    private static Dictionary<(ExifIfd, ushort), ExifTag> BuildRegistry()
    {
        var map = new Dictionary<(ExifIfd, ushort), ExifTag>();
        foreach (ExifTag tag in knownList ?? new List<ExifTag>())
        {
            map[(tag.Ifd, tag.Id)] = tag;
        }

        return map;
    }

    private static Dictionary<ushort, ExifIfd> BuildDefaultIfds()
    {
        // Preference when the same number exists in several directories: IFD0, then Exif, then GPS, then Interop.
        var map = new Dictionary<ushort, ExifIfd>();
        foreach (ExifIfd ifd in new[] { ExifIfd.Interop, ExifIfd.Gps, ExifIfd.Exif, ExifIfd.Ifd0 })
        {
            foreach (ExifTag tag in knownList ?? new List<ExifTag>())
            {
                if (tag.Ifd == ifd)
                {
                    map[tag.Id] = ifd;
                }
            }
        }

        return map;
    }
}

/// <summary>An EXIF tag whose values are of type <typeparamref name="TValueType"/>.</summary>
/// <typeparam name="TValueType">
/// One of: <see cref="byte"/>, <see cref="sbyte"/>, <see cref="ushort"/>, <see cref="short"/>, <see cref="uint"/>,
/// <see cref="int"/>, <see cref="float"/>, <see cref="double"/>, <see cref="Rational"/>, <see cref="SignedRational"/>,
/// <see cref="string"/>, or an array of any of the numeric types.
/// </typeparam>
public sealed class ExifTag<TValueType> : ExifTag
{
    /// <summary>Creates a tag for the given number, placed in the directory the number conventionally belongs to (IFD0 when unknown).</summary>
    public ExifTag(ushort id)
        : base(id, DefaultIfd(id), null)
    {
    }

    /// <summary>Creates a tag for the given number in the given directory.</summary>
    public ExifTag(ushort id, ExifIfd ifd)
        : base(id, ifd, null)
    {
    }

    internal ExifTag(ushort id, ExifIfd ifd, string name)
        : base(id, ifd, name)
    {
    }

    internal override Type ValueType => typeof(TValueType);

    internal override ExifTag WithIfd(ExifIfd ifd) => new ExifTag<TValueType>(this.Id, ifd, this.Name);

    internal override IExifValue? TryCreateValue(ExifDataType dataType, object? raw)
        => ExifValueConverter.TryConvert(raw, out TValueType? converted)
            ? new ExifValue<TValueType>(this, ExifValueConverter.ReconcileDataType(dataType, this.DefaultDataType), converted)
            : null;

    internal override IExifValue CreateValue(ExifDataType dataType, object? value)
        => new ExifValue<TValueType>(this, dataType, (TValueType?)value);
}
