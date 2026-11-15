namespace EasyImageSharp.Metadata.Iptc;

/// <summary>
/// An IPTC-IIM dataset, identified by its record number (high byte) and dataset number (low byte): for example
/// <see cref="Keywords"/> is dataset 2:25, so its value is <c>(2 &lt;&lt; 8) | 25</c>. The named members cover the
/// envelope record (1) and the application record (2) of the IPTC Information Interchange Model 4.2; any other
/// record/dataset pair can still be addressed by casting, e.g. <c>(IptcTag)((7 &lt;&lt; 8) | 10)</c>, and such
/// datasets round-trip unchanged.
/// </summary>
public enum IptcTag : ushort
{
    // ----- Envelope record (1) -----

    /// <summary>1:00 Envelope record version (binary, 2 bytes). Written as 4 when a profile gains an envelope record.</summary>
    EnvelopeRecordVersion = 1 << 8,

    /// <summary>1:05 Destination (text, up to 1024 bytes, repeatable).</summary>
    Destination = (1 << 8) | 5,

    /// <summary>1:20 File format (binary, 2 bytes).</summary>
    FileFormat = (1 << 8) | 20,

    /// <summary>1:22 File format version (binary, 2 bytes).</summary>
    FileFormatVersion = (1 << 8) | 22,

    /// <summary>1:30 Service identifier (text, up to 10 bytes).</summary>
    ServiceIdentifier = (1 << 8) | 30,

    /// <summary>1:40 Envelope number (8 digits).</summary>
    EnvelopeNumber = (1 << 8) | 40,

    /// <summary>1:50 Product identifier (text, up to 32 bytes, repeatable).</summary>
    ProductId = (1 << 8) | 50,

    /// <summary>1:60 Envelope priority (1 digit).</summary>
    EnvelopePriority = (1 << 8) | 60,

    /// <summary>1:70 Date sent (CCYYMMDD).</summary>
    DateSent = (1 << 8) | 70,

    /// <summary>1:80 Time sent (HHMMSS±HHMM).</summary>
    TimeSent = (1 << 8) | 80,

    /// <summary>
    /// 1:90 Coded character set (ISO 2022 escape sequences, binary). <c>ESC % G</c> declares UTF-8 for the text
    /// datasets of the profile; see <see cref="IptcProfile.IsUtf8"/>.
    /// </summary>
    CodedCharacterSet = (1 << 8) | 90,

    /// <summary>1:100 Unique name of object (text, 14 to 80 bytes).</summary>
    UniqueObjectName = (1 << 8) | 100,

    /// <summary>1:120 ARM identifier (binary, 2 bytes).</summary>
    ArmIdentifier = (1 << 8) | 120,

    /// <summary>1:122 ARM version (binary, 2 bytes).</summary>
    ArmVersion = (1 << 8) | 122,

    // ----- Application record (2) -----

    /// <summary>2:00 Application record version (binary, 2 bytes). Written as 4 when a profile gains an application record.</summary>
    ApplicationRecordVersion = 2 << 8,

    /// <summary>2:03 Object type reference (text, 3 to 67 bytes).</summary>
    ObjectTypeReference = (2 << 8) | 3,

    /// <summary>2:04 Object attribute reference (text, 4 to 68 bytes, repeatable).</summary>
    ObjectAttributeReference = (2 << 8) | 4,

    /// <summary>2:05 Object name, the shorthand title (text, up to 64 bytes).</summary>
    ObjectName = (2 << 8) | 5,

    /// <summary>2:07 Edit status (text, up to 64 bytes).</summary>
    EditStatus = (2 << 8) | 7,

    /// <summary>2:08 Editorial update (2 digits).</summary>
    EditorialUpdate = (2 << 8) | 8,

    /// <summary>2:10 Urgency (1 digit, 1 most urgent to 8 least urgent).</summary>
    Urgency = (2 << 8) | 10,

    /// <summary>2:12 Subject reference (text, 13 to 236 bytes, repeatable).</summary>
    SubjectReference = (2 << 8) | 12,

    /// <summary>2:15 Category (text, up to 3 bytes).</summary>
    Category = (2 << 8) | 15,

    /// <summary>2:20 Supplemental category (text, up to 32 bytes, repeatable).</summary>
    SupplementalCategories = (2 << 8) | 20,

    /// <summary>2:22 Fixture identifier (text, up to 32 bytes).</summary>
    FixtureIdentifier = (2 << 8) | 22,

    /// <summary>2:25 Keywords (text, up to 64 bytes each, repeatable).</summary>
    Keywords = (2 << 8) | 25,

    /// <summary>2:26 Content location code (text, exactly 3 bytes, repeatable).</summary>
    ContentLocationCode = (2 << 8) | 26,

    /// <summary>2:27 Content location name (text, up to 64 bytes, repeatable).</summary>
    ContentLocationName = (2 << 8) | 27,

    /// <summary>2:30 Release date (CCYYMMDD).</summary>
    ReleaseDate = (2 << 8) | 30,

    /// <summary>2:35 Release time (HHMMSS±HHMM).</summary>
    ReleaseTime = (2 << 8) | 35,

    /// <summary>2:37 Expiration date (CCYYMMDD).</summary>
    ExpirationDate = (2 << 8) | 37,

    /// <summary>2:38 Expiration time (HHMMSS±HHMM).</summary>
    ExpirationTime = (2 << 8) | 38,

    /// <summary>2:40 Special instructions (text, up to 256 bytes).</summary>
    SpecialInstructions = (2 << 8) | 40,

    /// <summary>2:42 Action advised (2 digits).</summary>
    ActionAdvised = (2 << 8) | 42,

    /// <summary>2:45 Reference service (text, up to 10 bytes, repeatable).</summary>
    ReferenceService = (2 << 8) | 45,

    /// <summary>2:47 Reference date (CCYYMMDD, repeatable).</summary>
    ReferenceDate = (2 << 8) | 47,

    /// <summary>2:50 Reference number (8 digits, repeatable).</summary>
    ReferenceNumber = (2 << 8) | 50,

    /// <summary>2:55 Date created (CCYYMMDD).</summary>
    DateCreated = (2 << 8) | 55,

    /// <summary>2:60 Time created (HHMMSS±HHMM).</summary>
    TimeCreated = (2 << 8) | 60,

    /// <summary>2:62 Digital creation date (CCYYMMDD).</summary>
    DigitalCreationDate = (2 << 8) | 62,

    /// <summary>2:63 Digital creation time (HHMMSS±HHMM).</summary>
    DigitalCreationTime = (2 << 8) | 63,

    /// <summary>2:65 Originating program (text, up to 32 bytes).</summary>
    OriginatingProgram = (2 << 8) | 65,

    /// <summary>2:70 Program version (text, up to 10 bytes).</summary>
    ProgramVersion = (2 << 8) | 70,

    /// <summary>2:75 Object cycle (text, 1 byte: 'a' morning, 'p' evening, 'b' both).</summary>
    ObjectCycle = (2 << 8) | 75,

    /// <summary>2:80 By-line, the creator (text, up to 32 bytes, repeatable).</summary>
    Byline = (2 << 8) | 80,

    /// <summary>2:85 By-line title, the creator's job title (text, up to 32 bytes, repeatable).</summary>
    BylineTitle = (2 << 8) | 85,

    /// <summary>2:90 City (text, up to 32 bytes).</summary>
    City = (2 << 8) | 90,

    /// <summary>2:92 Sub-location (text, up to 32 bytes).</summary>
    SubLocation = (2 << 8) | 92,

    /// <summary>2:95 Province/State (text, up to 32 bytes).</summary>
    ProvinceState = (2 << 8) | 95,

    /// <summary>2:100 Country/Primary location code (text, exactly 3 bytes, ISO 3166 alpha-3).</summary>
    CountryCode = (2 << 8) | 100,

    /// <summary>2:101 Country/Primary location name (text, up to 64 bytes).</summary>
    CountryName = (2 << 8) | 101,

    /// <summary>2:103 Original transmission reference, the job identifier (text, up to 32 bytes).</summary>
    OriginalTransmissionReference = (2 << 8) | 103,

    /// <summary>2:105 Headline (text, up to 256 bytes).</summary>
    Headline = (2 << 8) | 105,

    /// <summary>2:110 Credit (text, up to 32 bytes).</summary>
    Credit = (2 << 8) | 110,

    /// <summary>2:115 Source (text, up to 32 bytes).</summary>
    Source = (2 << 8) | 115,

    /// <summary>2:116 Copyright notice (text, up to 128 bytes).</summary>
    CopyrightNotice = (2 << 8) | 116,

    /// <summary>2:118 Contact (text, up to 128 bytes, repeatable).</summary>
    Contact = (2 << 8) | 118,

    /// <summary>2:120 Caption/Abstract, the description (text, up to 2000 bytes).</summary>
    Caption = (2 << 8) | 120,

    /// <summary>2:122 Writer/Editor of the caption (text, up to 32 bytes, repeatable).</summary>
    WriterEditor = (2 << 8) | 122,

    /// <summary>2:125 Rasterized caption (binary, 7360 bytes).</summary>
    RasterizedCaption = (2 << 8) | 125,

    /// <summary>2:130 Image type (text, 2 bytes).</summary>
    ImageType = (2 << 8) | 130,

    /// <summary>2:131 Image orientation (text, 1 byte: 'P', 'L' or 'S').</summary>
    ImageOrientation = (2 << 8) | 131,

    /// <summary>2:135 Language identifier (text, 2 or 3 bytes, ISO 639).</summary>
    LanguageIdentifier = (2 << 8) | 135,

    /// <summary>2:150 Audio type (text, 2 bytes).</summary>
    AudioType = (2 << 8) | 150,

    /// <summary>2:151 Audio sampling rate (6 digits).</summary>
    AudioSamplingRate = (2 << 8) | 151,

    /// <summary>2:152 Audio sampling resolution (2 digits).</summary>
    AudioSamplingResolution = (2 << 8) | 152,

    /// <summary>2:153 Audio duration (6 digits, HHMMSS).</summary>
    AudioDuration = (2 << 8) | 153,

    /// <summary>2:154 Audio outcue (text, up to 64 bytes).</summary>
    AudioOutcue = (2 << 8) | 154,

    /// <summary>2:200 Object data preview file format (binary, 2 bytes).</summary>
    ObjectPreviewFileFormat = (2 << 8) | 200,

    /// <summary>2:201 Object data preview file format version (binary, 2 bytes).</summary>
    ObjectPreviewFileFormatVersion = (2 << 8) | 201,

    /// <summary>2:202 Object data preview data (binary, up to 256 000 bytes).</summary>
    ObjectPreviewData = (2 << 8) | 202,
}
