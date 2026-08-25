namespace EasyImageSharp.Metadata.Exif;

/// <content>Well-known tag definitions (EXIF 2.32, TIFF 6.0). Types follow the specification tables.</content>
public abstract partial class ExifTag
{
    /// <summary>Tags whose field type is UNDEFINED by specification although their CLR type is <see cref="byte"/>[] or <see cref="string"/>.</summary>
    private static readonly HashSet<(ExifIfd, ushort)> UndefinedTags = new()
    {
        (ExifIfd.Exif, 0x8828), // OECF
        (ExifIfd.Exif, 0x9000), // ExifVersion
        (ExifIfd.Exif, 0x9101), // ComponentsConfiguration
        (ExifIfd.Exif, 0x927C), // MakerNote
        (ExifIfd.Exif, 0x9286), // UserComment
        (ExifIfd.Exif, 0xA000), // FlashpixVersion
        (ExifIfd.Exif, 0xA20C), // SpatialFrequencyResponse
        (ExifIfd.Exif, 0xA300), // FileSource
        (ExifIfd.Exif, 0xA301), // SceneType
        (ExifIfd.Exif, 0xA302), // CFAPattern
        (ExifIfd.Exif, 0xA40B), // DeviceSettingDescription
        (ExifIfd.Gps, 0x001B), // GPSProcessingMethod
        (ExifIfd.Gps, 0x001C), // GPSAreaInformation
        (ExifIfd.Interop, 0x0002), // InteroperabilityVersion
    };

    // ----- IFD0 / TIFF tags -----

    public static ExifTag<uint> ImageWidth { get; } = Register(new ExifTag<uint>(0x0100, ExifIfd.Ifd0, "ImageWidth"));

    public static ExifTag<uint> ImageLength { get; } = Register(new ExifTag<uint>(0x0101, ExifIfd.Ifd0, "ImageLength"));

    public static ExifTag<ushort[]> BitsPerSample { get; } = Register(new ExifTag<ushort[]>(0x0102, ExifIfd.Ifd0, "BitsPerSample"));

    public static ExifTag<ushort> Compression { get; } = Register(new ExifTag<ushort>(0x0103, ExifIfd.Ifd0, "Compression"));

    public static ExifTag<ushort> PhotometricInterpretation { get; } = Register(new ExifTag<ushort>(0x0106, ExifIfd.Ifd0, "PhotometricInterpretation"));

    public static ExifTag<string> ImageDescription { get; } = Register(new ExifTag<string>(0x010E, ExifIfd.Ifd0, "ImageDescription"));

    public static ExifTag<string> Make { get; } = Register(new ExifTag<string>(0x010F, ExifIfd.Ifd0, "Make"));

    public static ExifTag<string> Model { get; } = Register(new ExifTag<string>(0x0110, ExifIfd.Ifd0, "Model"));

    public static ExifTag<uint[]> StripOffsets { get; } = Register(new ExifTag<uint[]>(0x0111, ExifIfd.Ifd0, "StripOffsets"));

    /// <summary>Image orientation, 1 (top-left, normal) to 8. See <c>AutoOrient()</c>.</summary>
    public static ExifTag<ushort> Orientation { get; } = Register(new ExifTag<ushort>(0x0112, ExifIfd.Ifd0, "Orientation"));

    public static ExifTag<ushort> SamplesPerPixel { get; } = Register(new ExifTag<ushort>(0x0115, ExifIfd.Ifd0, "SamplesPerPixel"));

    public static ExifTag<uint> RowsPerStrip { get; } = Register(new ExifTag<uint>(0x0116, ExifIfd.Ifd0, "RowsPerStrip"));

    public static ExifTag<uint[]> StripByteCounts { get; } = Register(new ExifTag<uint[]>(0x0117, ExifIfd.Ifd0, "StripByteCounts"));

    public static ExifTag<Rational> XResolution { get; } = Register(new ExifTag<Rational>(0x011A, ExifIfd.Ifd0, "XResolution"));

    public static ExifTag<Rational> YResolution { get; } = Register(new ExifTag<Rational>(0x011B, ExifIfd.Ifd0, "YResolution"));

    public static ExifTag<ushort> PlanarConfiguration { get; } = Register(new ExifTag<ushort>(0x011C, ExifIfd.Ifd0, "PlanarConfiguration"));

    /// <summary>Unit of <see cref="XResolution"/>/<see cref="YResolution"/>: 1 none, 2 inch, 3 centimeter.</summary>
    public static ExifTag<ushort> ResolutionUnit { get; } = Register(new ExifTag<ushort>(0x0128, ExifIfd.Ifd0, "ResolutionUnit"));

    public static ExifTag<ushort[]> TransferFunction { get; } = Register(new ExifTag<ushort[]>(0x012D, ExifIfd.Ifd0, "TransferFunction"));

    public static ExifTag<string> Software { get; } = Register(new ExifTag<string>(0x0131, ExifIfd.Ifd0, "Software"));

    /// <summary>File change date and time in the form "YYYY:MM:DD HH:MM:SS".</summary>
    public static ExifTag<string> DateTime { get; } = Register(new ExifTag<string>(0x0132, ExifIfd.Ifd0, "DateTime"));

    public static ExifTag<string> Artist { get; } = Register(new ExifTag<string>(0x013B, ExifIfd.Ifd0, "Artist"));

    public static ExifTag<string> HostComputer { get; } = Register(new ExifTag<string>(0x013C, ExifIfd.Ifd0, "HostComputer"));

    public static ExifTag<Rational[]> WhitePoint { get; } = Register(new ExifTag<Rational[]>(0x013E, ExifIfd.Ifd0, "WhitePoint"));

    public static ExifTag<Rational[]> PrimaryChromaticities { get; } = Register(new ExifTag<Rational[]>(0x013F, ExifIfd.Ifd0, "PrimaryChromaticities"));

    public static ExifTag<uint> JpegInterchangeFormat { get; } = Register(new ExifTag<uint>(0x0201, ExifIfd.Ifd0, "JPEGInterchangeFormat"));

    public static ExifTag<uint> JpegInterchangeFormatLength { get; } = Register(new ExifTag<uint>(0x0202, ExifIfd.Ifd0, "JPEGInterchangeFormatLength"));

    public static ExifTag<Rational[]> YCbCrCoefficients { get; } = Register(new ExifTag<Rational[]>(0x0211, ExifIfd.Ifd0, "YCbCrCoefficients"));

    public static ExifTag<ushort[]> YCbCrSubSampling { get; } = Register(new ExifTag<ushort[]>(0x0212, ExifIfd.Ifd0, "YCbCrSubSampling"));

    public static ExifTag<ushort> YCbCrPositioning { get; } = Register(new ExifTag<ushort>(0x0213, ExifIfd.Ifd0, "YCbCrPositioning"));

    public static ExifTag<Rational[]> ReferenceBlackWhite { get; } = Register(new ExifTag<Rational[]>(0x0214, ExifIfd.Ifd0, "ReferenceBlackWhite"));

    public static ExifTag<ushort> Rating { get; } = Register(new ExifTag<ushort>(0x4746, ExifIfd.Ifd0, "Rating"));

    public static ExifTag<ushort> RatingPercent { get; } = Register(new ExifTag<ushort>(0x4749, ExifIfd.Ifd0, "RatingPercent"));

    public static ExifTag<string> Copyright { get; } = Register(new ExifTag<string>(0x8298, ExifIfd.Ifd0, "Copyright"));

    /// <summary>Windows Explorer title (UTF-16LE bytes).</summary>
    public static ExifTag<byte[]> XPTitle { get; } = Register(new ExifTag<byte[]>(0x9C9B, ExifIfd.Ifd0, "XPTitle"));

    public static ExifTag<byte[]> XPComment { get; } = Register(new ExifTag<byte[]>(0x9C9C, ExifIfd.Ifd0, "XPComment"));

    public static ExifTag<byte[]> XPAuthor { get; } = Register(new ExifTag<byte[]>(0x9C9D, ExifIfd.Ifd0, "XPAuthor"));

    public static ExifTag<byte[]> XPKeywords { get; } = Register(new ExifTag<byte[]>(0x9C9E, ExifIfd.Ifd0, "XPKeywords"));

    public static ExifTag<byte[]> XPSubject { get; } = Register(new ExifTag<byte[]>(0x9C9F, ExifIfd.Ifd0, "XPSubject"));

    // ----- Exif IFD -----

    /// <summary>Exposure time in seconds.</summary>
    public static ExifTag<Rational> ExposureTime { get; } = Register(new ExifTag<Rational>(0x829A, ExifIfd.Exif, "ExposureTime"));

    public static ExifTag<Rational> FNumber { get; } = Register(new ExifTag<Rational>(0x829D, ExifIfd.Exif, "FNumber"));

    public static ExifTag<ushort> ExposureProgram { get; } = Register(new ExifTag<ushort>(0x8822, ExifIfd.Exif, "ExposureProgram"));

    public static ExifTag<string> SpectralSensitivity { get; } = Register(new ExifTag<string>(0x8824, ExifIfd.Exif, "SpectralSensitivity"));

    /// <summary>ISO speed ratings (called PhotographicSensitivity since EXIF 2.3).</summary>
    public static ExifTag<ushort[]> ISOSpeedRatings { get; } = Register(new ExifTag<ushort[]>(0x8827, ExifIfd.Exif, "ISOSpeedRatings"));

    public static ExifTag<byte[]> OECF { get; } = Register(new ExifTag<byte[]>(0x8828, ExifIfd.Exif, "OECF"));

    public static ExifTag<ushort> SensitivityType { get; } = Register(new ExifTag<ushort>(0x8830, ExifIfd.Exif, "SensitivityType"));

    public static ExifTag<uint> RecommendedExposureIndex { get; } = Register(new ExifTag<uint>(0x8832, ExifIfd.Exif, "RecommendedExposureIndex"));

    /// <summary>Four ASCII bytes, e.g. "0232".</summary>
    public static ExifTag<byte[]> ExifVersion { get; } = Register(new ExifTag<byte[]>(0x9000, ExifIfd.Exif, "ExifVersion"));

    public static ExifTag<string> DateTimeOriginal { get; } = Register(new ExifTag<string>(0x9003, ExifIfd.Exif, "DateTimeOriginal"));

    public static ExifTag<string> DateTimeDigitized { get; } = Register(new ExifTag<string>(0x9004, ExifIfd.Exif, "DateTimeDigitized"));

    public static ExifTag<string> OffsetTime { get; } = Register(new ExifTag<string>(0x9010, ExifIfd.Exif, "OffsetTime"));

    public static ExifTag<string> OffsetTimeOriginal { get; } = Register(new ExifTag<string>(0x9011, ExifIfd.Exif, "OffsetTimeOriginal"));

    public static ExifTag<string> OffsetTimeDigitized { get; } = Register(new ExifTag<string>(0x9012, ExifIfd.Exif, "OffsetTimeDigitized"));

    public static ExifTag<byte[]> ComponentsConfiguration { get; } = Register(new ExifTag<byte[]>(0x9101, ExifIfd.Exif, "ComponentsConfiguration"));

    public static ExifTag<Rational> CompressedBitsPerPixel { get; } = Register(new ExifTag<Rational>(0x9102, ExifIfd.Exif, "CompressedBitsPerPixel"));

    public static ExifTag<SignedRational> ShutterSpeedValue { get; } = Register(new ExifTag<SignedRational>(0x9201, ExifIfd.Exif, "ShutterSpeedValue"));

    public static ExifTag<Rational> ApertureValue { get; } = Register(new ExifTag<Rational>(0x9202, ExifIfd.Exif, "ApertureValue"));

    public static ExifTag<SignedRational> BrightnessValue { get; } = Register(new ExifTag<SignedRational>(0x9203, ExifIfd.Exif, "BrightnessValue"));

    public static ExifTag<SignedRational> ExposureBiasValue { get; } = Register(new ExifTag<SignedRational>(0x9204, ExifIfd.Exif, "ExposureBiasValue"));

    public static ExifTag<Rational> MaxApertureValue { get; } = Register(new ExifTag<Rational>(0x9205, ExifIfd.Exif, "MaxApertureValue"));

    public static ExifTag<Rational> SubjectDistance { get; } = Register(new ExifTag<Rational>(0x9206, ExifIfd.Exif, "SubjectDistance"));

    public static ExifTag<ushort> MeteringMode { get; } = Register(new ExifTag<ushort>(0x9207, ExifIfd.Exif, "MeteringMode"));

    public static ExifTag<ushort> LightSource { get; } = Register(new ExifTag<ushort>(0x9208, ExifIfd.Exif, "LightSource"));

    public static ExifTag<ushort> Flash { get; } = Register(new ExifTag<ushort>(0x9209, ExifIfd.Exif, "Flash"));

    /// <summary>Lens focal length in millimeters.</summary>
    public static ExifTag<Rational> FocalLength { get; } = Register(new ExifTag<Rational>(0x920A, ExifIfd.Exif, "FocalLength"));

    public static ExifTag<ushort[]> SubjectArea { get; } = Register(new ExifTag<ushort[]>(0x9214, ExifIfd.Exif, "SubjectArea"));

    public static ExifTag<byte[]> MakerNote { get; } = Register(new ExifTag<byte[]>(0x927C, ExifIfd.Exif, "MakerNote"));

    /// <summary>
    /// Free-text comment. Stored with the EXIF character-code prefix: read as ASCII, UTF-16 ("UNICODE") or
    /// undefined-encoding text; written as ASCII when possible and as UNICODE otherwise.
    /// </summary>
    public static ExifTag<string> UserComment { get; } = Register(new ExifTag<string>(0x9286, ExifIfd.Exif, "UserComment"));

    public static ExifTag<string> SubsecTime { get; } = Register(new ExifTag<string>(0x9290, ExifIfd.Exif, "SubsecTime"));

    public static ExifTag<string> SubsecTimeOriginal { get; } = Register(new ExifTag<string>(0x9291, ExifIfd.Exif, "SubsecTimeOriginal"));

    public static ExifTag<string> SubsecTimeDigitized { get; } = Register(new ExifTag<string>(0x9292, ExifIfd.Exif, "SubsecTimeDigitized"));

    public static ExifTag<byte[]> FlashpixVersion { get; } = Register(new ExifTag<byte[]>(0xA000, ExifIfd.Exif, "FlashpixVersion"));

    /// <summary>1 = sRGB, 0xFFFF = uncalibrated.</summary>
    public static ExifTag<ushort> ColorSpace { get; } = Register(new ExifTag<ushort>(0xA001, ExifIfd.Exif, "ColorSpace"));

    public static ExifTag<uint> PixelXDimension { get; } = Register(new ExifTag<uint>(0xA002, ExifIfd.Exif, "PixelXDimension"));

    public static ExifTag<uint> PixelYDimension { get; } = Register(new ExifTag<uint>(0xA003, ExifIfd.Exif, "PixelYDimension"));

    public static ExifTag<string> RelatedSoundFile { get; } = Register(new ExifTag<string>(0xA004, ExifIfd.Exif, "RelatedSoundFile"));

    public static ExifTag<Rational> FlashEnergy { get; } = Register(new ExifTag<Rational>(0xA20B, ExifIfd.Exif, "FlashEnergy"));

    public static ExifTag<byte[]> SpatialFrequencyResponse { get; } = Register(new ExifTag<byte[]>(0xA20C, ExifIfd.Exif, "SpatialFrequencyResponse"));

    public static ExifTag<Rational> FocalPlaneXResolution { get; } = Register(new ExifTag<Rational>(0xA20E, ExifIfd.Exif, "FocalPlaneXResolution"));

    public static ExifTag<Rational> FocalPlaneYResolution { get; } = Register(new ExifTag<Rational>(0xA20F, ExifIfd.Exif, "FocalPlaneYResolution"));

    public static ExifTag<ushort> FocalPlaneResolutionUnit { get; } = Register(new ExifTag<ushort>(0xA210, ExifIfd.Exif, "FocalPlaneResolutionUnit"));

    public static ExifTag<ushort[]> SubjectLocation { get; } = Register(new ExifTag<ushort[]>(0xA214, ExifIfd.Exif, "SubjectLocation"));

    public static ExifTag<Rational> ExposureIndex { get; } = Register(new ExifTag<Rational>(0xA215, ExifIfd.Exif, "ExposureIndex"));

    public static ExifTag<ushort> SensingMethod { get; } = Register(new ExifTag<ushort>(0xA217, ExifIfd.Exif, "SensingMethod"));

    public static ExifTag<byte> FileSource { get; } = Register(new ExifTag<byte>(0xA300, ExifIfd.Exif, "FileSource"));

    public static ExifTag<byte> SceneType { get; } = Register(new ExifTag<byte>(0xA301, ExifIfd.Exif, "SceneType"));

    public static ExifTag<byte[]> CFAPattern { get; } = Register(new ExifTag<byte[]>(0xA302, ExifIfd.Exif, "CFAPattern"));

    public static ExifTag<ushort> CustomRendered { get; } = Register(new ExifTag<ushort>(0xA401, ExifIfd.Exif, "CustomRendered"));

    public static ExifTag<ushort> ExposureMode { get; } = Register(new ExifTag<ushort>(0xA402, ExifIfd.Exif, "ExposureMode"));

    public static ExifTag<ushort> WhiteBalance { get; } = Register(new ExifTag<ushort>(0xA403, ExifIfd.Exif, "WhiteBalance"));

    public static ExifTag<Rational> DigitalZoomRatio { get; } = Register(new ExifTag<Rational>(0xA404, ExifIfd.Exif, "DigitalZoomRatio"));

    public static ExifTag<ushort> FocalLengthIn35mmFilm { get; } = Register(new ExifTag<ushort>(0xA405, ExifIfd.Exif, "FocalLengthIn35mmFilm"));

    public static ExifTag<ushort> SceneCaptureType { get; } = Register(new ExifTag<ushort>(0xA406, ExifIfd.Exif, "SceneCaptureType"));

    public static ExifTag<ushort> GainControl { get; } = Register(new ExifTag<ushort>(0xA407, ExifIfd.Exif, "GainControl"));

    public static ExifTag<ushort> Contrast { get; } = Register(new ExifTag<ushort>(0xA408, ExifIfd.Exif, "Contrast"));

    public static ExifTag<ushort> Saturation { get; } = Register(new ExifTag<ushort>(0xA409, ExifIfd.Exif, "Saturation"));

    public static ExifTag<ushort> Sharpness { get; } = Register(new ExifTag<ushort>(0xA40A, ExifIfd.Exif, "Sharpness"));

    public static ExifTag<byte[]> DeviceSettingDescription { get; } = Register(new ExifTag<byte[]>(0xA40B, ExifIfd.Exif, "DeviceSettingDescription"));

    public static ExifTag<ushort> SubjectDistanceRange { get; } = Register(new ExifTag<ushort>(0xA40C, ExifIfd.Exif, "SubjectDistanceRange"));

    public static ExifTag<string> ImageUniqueID { get; } = Register(new ExifTag<string>(0xA420, ExifIfd.Exif, "ImageUniqueID"));

    public static ExifTag<string> CameraOwnerName { get; } = Register(new ExifTag<string>(0xA430, ExifIfd.Exif, "CameraOwnerName"));

    public static ExifTag<string> BodySerialNumber { get; } = Register(new ExifTag<string>(0xA431, ExifIfd.Exif, "BodySerialNumber"));

    public static ExifTag<Rational[]> LensSpecification { get; } = Register(new ExifTag<Rational[]>(0xA432, ExifIfd.Exif, "LensSpecification"));

    public static ExifTag<string> LensMake { get; } = Register(new ExifTag<string>(0xA433, ExifIfd.Exif, "LensMake"));

    public static ExifTag<string> LensModel { get; } = Register(new ExifTag<string>(0xA434, ExifIfd.Exif, "LensModel"));

    public static ExifTag<string> LensSerialNumber { get; } = Register(new ExifTag<string>(0xA435, ExifIfd.Exif, "LensSerialNumber"));

    public static ExifTag<Rational> Gamma { get; } = Register(new ExifTag<Rational>(0xA500, ExifIfd.Exif, "Gamma"));

    // ----- GPS IFD -----

    public static ExifTag<byte[]> GPSVersionID { get; } = Register(new ExifTag<byte[]>(0x0000, ExifIfd.Gps, "GPSVersionID"));

    /// <summary>"N" or "S".</summary>
    public static ExifTag<string> GPSLatitudeRef { get; } = Register(new ExifTag<string>(0x0001, ExifIfd.Gps, "GPSLatitudeRef"));

    /// <summary>Degrees, minutes, seconds.</summary>
    public static ExifTag<Rational[]> GPSLatitude { get; } = Register(new ExifTag<Rational[]>(0x0002, ExifIfd.Gps, "GPSLatitude"));

    /// <summary>"E" or "W".</summary>
    public static ExifTag<string> GPSLongitudeRef { get; } = Register(new ExifTag<string>(0x0003, ExifIfd.Gps, "GPSLongitudeRef"));

    /// <summary>Degrees, minutes, seconds.</summary>
    public static ExifTag<Rational[]> GPSLongitude { get; } = Register(new ExifTag<Rational[]>(0x0004, ExifIfd.Gps, "GPSLongitude"));

    /// <summary>0 = above sea level, 1 = below.</summary>
    public static ExifTag<byte> GPSAltitudeRef { get; } = Register(new ExifTag<byte>(0x0005, ExifIfd.Gps, "GPSAltitudeRef"));

    public static ExifTag<Rational> GPSAltitude { get; } = Register(new ExifTag<Rational>(0x0006, ExifIfd.Gps, "GPSAltitude"));

    public static ExifTag<Rational[]> GPSTimestamp { get; } = Register(new ExifTag<Rational[]>(0x0007, ExifIfd.Gps, "GPSTimeStamp"));

    public static ExifTag<string> GPSSatellites { get; } = Register(new ExifTag<string>(0x0008, ExifIfd.Gps, "GPSSatellites"));

    public static ExifTag<string> GPSStatus { get; } = Register(new ExifTag<string>(0x0009, ExifIfd.Gps, "GPSStatus"));

    public static ExifTag<string> GPSMeasureMode { get; } = Register(new ExifTag<string>(0x000A, ExifIfd.Gps, "GPSMeasureMode"));

    public static ExifTag<Rational> GPSDOP { get; } = Register(new ExifTag<Rational>(0x000B, ExifIfd.Gps, "GPSDOP"));

    public static ExifTag<string> GPSSpeedRef { get; } = Register(new ExifTag<string>(0x000C, ExifIfd.Gps, "GPSSpeedRef"));

    public static ExifTag<Rational> GPSSpeed { get; } = Register(new ExifTag<Rational>(0x000D, ExifIfd.Gps, "GPSSpeed"));

    public static ExifTag<string> GPSTrackRef { get; } = Register(new ExifTag<string>(0x000E, ExifIfd.Gps, "GPSTrackRef"));

    public static ExifTag<Rational> GPSTrack { get; } = Register(new ExifTag<Rational>(0x000F, ExifIfd.Gps, "GPSTrack"));

    public static ExifTag<string> GPSImgDirectionRef { get; } = Register(new ExifTag<string>(0x0010, ExifIfd.Gps, "GPSImgDirectionRef"));

    public static ExifTag<Rational> GPSImgDirection { get; } = Register(new ExifTag<Rational>(0x0011, ExifIfd.Gps, "GPSImgDirection"));

    public static ExifTag<string> GPSMapDatum { get; } = Register(new ExifTag<string>(0x0012, ExifIfd.Gps, "GPSMapDatum"));

    public static ExifTag<string> GPSDestLatitudeRef { get; } = Register(new ExifTag<string>(0x0013, ExifIfd.Gps, "GPSDestLatitudeRef"));

    public static ExifTag<Rational[]> GPSDestLatitude { get; } = Register(new ExifTag<Rational[]>(0x0014, ExifIfd.Gps, "GPSDestLatitude"));

    public static ExifTag<string> GPSDestLongitudeRef { get; } = Register(new ExifTag<string>(0x0015, ExifIfd.Gps, "GPSDestLongitudeRef"));

    public static ExifTag<Rational[]> GPSDestLongitude { get; } = Register(new ExifTag<Rational[]>(0x0016, ExifIfd.Gps, "GPSDestLongitude"));

    public static ExifTag<string> GPSDestBearingRef { get; } = Register(new ExifTag<string>(0x0017, ExifIfd.Gps, "GPSDestBearingRef"));

    public static ExifTag<Rational> GPSDestBearing { get; } = Register(new ExifTag<Rational>(0x0018, ExifIfd.Gps, "GPSDestBearing"));

    public static ExifTag<string> GPSDestDistanceRef { get; } = Register(new ExifTag<string>(0x0019, ExifIfd.Gps, "GPSDestDistanceRef"));

    public static ExifTag<Rational> GPSDestDistance { get; } = Register(new ExifTag<Rational>(0x001A, ExifIfd.Gps, "GPSDestDistance"));

    public static ExifTag<byte[]> GPSProcessingMethod { get; } = Register(new ExifTag<byte[]>(0x001B, ExifIfd.Gps, "GPSProcessingMethod"));

    public static ExifTag<byte[]> GPSAreaInformation { get; } = Register(new ExifTag<byte[]>(0x001C, ExifIfd.Gps, "GPSAreaInformation"));

    /// <summary>"YYYY:MM:DD".</summary>
    public static ExifTag<string> GPSDateStamp { get; } = Register(new ExifTag<string>(0x001D, ExifIfd.Gps, "GPSDateStamp"));

    public static ExifTag<ushort> GPSDifferential { get; } = Register(new ExifTag<ushort>(0x001E, ExifIfd.Gps, "GPSDifferential"));

    public static ExifTag<Rational> GPSHPositioningError { get; } = Register(new ExifTag<Rational>(0x001F, ExifIfd.Gps, "GPSHPositioningError"));

    // ----- Interoperability IFD -----

    public static ExifTag<string> InteroperabilityIndex { get; } = Register(new ExifTag<string>(0x0001, ExifIfd.Interop, "InteroperabilityIndex"));

    public static ExifTag<byte[]> InteroperabilityVersion { get; } = Register(new ExifTag<byte[]>(0x0002, ExifIfd.Interop, "InteroperabilityVersion"));

    public static ExifTag<string> RelatedImageFileFormat { get; } = Register(new ExifTag<string>(0x1000, ExifIfd.Interop, "RelatedImageFileFormat"));

    public static ExifTag<ushort> RelatedImageWidth { get; } = Register(new ExifTag<ushort>(0x1001, ExifIfd.Interop, "RelatedImageWidth"));

    public static ExifTag<ushort> RelatedImageLength { get; } = Register(new ExifTag<ushort>(0x1002, ExifIfd.Interop, "RelatedImageLength"));
}
