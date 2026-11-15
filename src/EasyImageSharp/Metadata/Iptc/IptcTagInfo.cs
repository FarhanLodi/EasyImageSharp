namespace EasyImageSharp.Metadata.Iptc;

/// <summary>How a dataset's octets are interpreted.</summary>
internal enum IptcValueFormat
{
    /// <summary>Graphic characters in the profile's coded character set.</summary>
    Text,

    /// <summary>Numeric characters '0'-'9' only (dates, counts, priorities).</summary>
    Digits,

    /// <summary>Binary data (record versions, preview data, the coded character set itself).</summary>
    Binary,
}

/// <summary>
/// The constraints the IPTC Information Interchange Model 4.2 places on one dataset: its octet length range, whether
/// it may repeat, and how its octets are interpreted. Lengths count encoded octets, not characters.
/// </summary>
internal readonly record struct IptcTagInfo(int MinLength, int MaxLength, bool Repeatable, IptcValueFormat Format)
{
    /// <summary>Datasets longer than this need the extended (long) length form of section 1.5.</summary>
    public const int MaxStandardLength = 0x7FFF;

    private static readonly Dictionary<IptcTag, IptcTagInfo> Known = new()
    {
        [IptcTag.EnvelopeRecordVersion] = Binary(2),
        [IptcTag.Destination] = Text(0, 1024, repeatable: true),
        [IptcTag.FileFormat] = Binary(2),
        [IptcTag.FileFormatVersion] = Binary(2),
        [IptcTag.ServiceIdentifier] = Text(0, 10),
        [IptcTag.EnvelopeNumber] = Digits(8, 8),
        [IptcTag.ProductId] = Text(0, 32, repeatable: true),
        [IptcTag.EnvelopePriority] = Digits(1, 1),
        [IptcTag.DateSent] = Digits(8, 8),
        [IptcTag.TimeSent] = Text(11, 11),
        [IptcTag.CodedCharacterSet] = new(0, 32, false, IptcValueFormat.Binary),
        [IptcTag.UniqueObjectName] = Text(14, 80),
        [IptcTag.ArmIdentifier] = Binary(2),
        [IptcTag.ArmVersion] = Binary(2),

        [IptcTag.ApplicationRecordVersion] = Binary(2),
        [IptcTag.ObjectTypeReference] = Text(3, 67),
        [IptcTag.ObjectAttributeReference] = Text(4, 68, repeatable: true),
        [IptcTag.ObjectName] = Text(0, 64),
        [IptcTag.EditStatus] = Text(0, 64),
        [IptcTag.EditorialUpdate] = Digits(2, 2),
        [IptcTag.Urgency] = Digits(1, 1),
        [IptcTag.SubjectReference] = Text(13, 236, repeatable: true),
        [IptcTag.Category] = Text(0, 3),
        [IptcTag.SupplementalCategories] = Text(0, 32, repeatable: true),
        [IptcTag.FixtureIdentifier] = Text(0, 32),
        [IptcTag.Keywords] = Text(0, 64, repeatable: true),
        [IptcTag.ContentLocationCode] = Text(3, 3, repeatable: true),
        [IptcTag.ContentLocationName] = Text(0, 64, repeatable: true),
        [IptcTag.ReleaseDate] = Digits(8, 8),
        [IptcTag.ReleaseTime] = Text(11, 11),
        [IptcTag.ExpirationDate] = Digits(8, 8),
        [IptcTag.ExpirationTime] = Text(11, 11),
        [IptcTag.SpecialInstructions] = Text(0, 256),
        [IptcTag.ActionAdvised] = Digits(2, 2),
        [IptcTag.ReferenceService] = Text(0, 10, repeatable: true),
        [IptcTag.ReferenceDate] = Digits(8, 8, repeatable: true),
        [IptcTag.ReferenceNumber] = Digits(8, 8, repeatable: true),
        [IptcTag.DateCreated] = Digits(8, 8),
        [IptcTag.TimeCreated] = Text(11, 11),
        [IptcTag.DigitalCreationDate] = Digits(8, 8),
        [IptcTag.DigitalCreationTime] = Text(11, 11),
        [IptcTag.OriginatingProgram] = Text(0, 32),
        [IptcTag.ProgramVersion] = Text(0, 10),
        [IptcTag.ObjectCycle] = Text(1, 1),
        [IptcTag.Byline] = Text(0, 32, repeatable: true),
        [IptcTag.BylineTitle] = Text(0, 32, repeatable: true),
        [IptcTag.City] = Text(0, 32),
        [IptcTag.SubLocation] = Text(0, 32),
        [IptcTag.ProvinceState] = Text(0, 32),
        [IptcTag.CountryCode] = Text(3, 3),
        [IptcTag.CountryName] = Text(0, 64),
        [IptcTag.OriginalTransmissionReference] = Text(0, 32),
        [IptcTag.Headline] = Text(0, 256),
        [IptcTag.Credit] = Text(0, 32),
        [IptcTag.Source] = Text(0, 32),
        [IptcTag.CopyrightNotice] = Text(0, 128),
        [IptcTag.Contact] = Text(0, 128, repeatable: true),
        [IptcTag.Caption] = Text(0, 2000),
        [IptcTag.WriterEditor] = Text(0, 32, repeatable: true),
        [IptcTag.RasterizedCaption] = new(0, 7360, false, IptcValueFormat.Binary),
        [IptcTag.ImageType] = Text(2, 2),
        [IptcTag.ImageOrientation] = Text(1, 1),
        [IptcTag.LanguageIdentifier] = Text(2, 3),
        [IptcTag.AudioType] = Text(2, 2),
        [IptcTag.AudioSamplingRate] = Digits(6, 6),
        [IptcTag.AudioSamplingResolution] = Digits(2, 2),
        [IptcTag.AudioDuration] = Digits(6, 6),
        [IptcTag.AudioOutcue] = Text(0, 64),
        [IptcTag.ObjectPreviewFileFormat] = Binary(2),
        [IptcTag.ObjectPreviewFileFormatVersion] = Binary(2),
        [IptcTag.ObjectPreviewData] = new(0, 256000, false, IptcValueFormat.Binary),
    };

    /// <summary>
    /// Looks up the constraints of a dataset. Datasets the table does not name are treated as repeatable text of
    /// any length, so unknown data is never refused.
    /// </summary>
    public static IptcTagInfo Get(IptcTag tag, out bool known)
    {
        known = Known.TryGetValue(tag, out IptcTagInfo info);
        return known ? info : new IptcTagInfo(0, int.MaxValue, true, IptcValueFormat.Text);
    }

    /// <summary>True when the dataset is one this table knows to carry text (or digits) rather than binary data.</summary>
    public static bool IsKnownText(IptcTag tag)
        => Known.TryGetValue(tag, out IptcTagInfo info) && info.Format != IptcValueFormat.Binary;

    /// <summary>The time dataset paired with a date dataset, or null when the date stands alone.</summary>
    public static IptcTag? PairedTime(IptcTag dateTag) => dateTag switch
    {
        IptcTag.DateSent => IptcTag.TimeSent,
        IptcTag.ReleaseDate => IptcTag.ReleaseTime,
        IptcTag.ExpirationDate => IptcTag.ExpirationTime,
        IptcTag.DateCreated => IptcTag.TimeCreated,
        IptcTag.DigitalCreationDate => IptcTag.DigitalCreationTime,
        _ => null,
    };

    /// <summary>True for the datasets that hold a CCYYMMDD date.</summary>
    public static bool IsDate(IptcTag tag) => tag is IptcTag.DateSent or IptcTag.ReleaseDate or IptcTag.ExpirationDate
        or IptcTag.ReferenceDate or IptcTag.DateCreated or IptcTag.DigitalCreationDate;

    private static IptcTagInfo Text(int min, int max, bool repeatable = false) => new(min, max, repeatable, IptcValueFormat.Text);

    private static IptcTagInfo Digits(int min, int max, bool repeatable = false) => new(min, max, repeatable, IptcValueFormat.Digits);

    private static IptcTagInfo Binary(int length) => new(length, length, false, IptcValueFormat.Binary);
}
