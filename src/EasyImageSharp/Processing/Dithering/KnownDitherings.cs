namespace EasyImageSharp.Processing.Dithering;

/// <summary>The classic error-diffusion kernels and ordered threshold matrices.</summary>
public static class KnownDitherings
{
    // ----- Error diffusion -----

    /// <summary>Floyd–Steinberg (1976): the standard 4-tap kernel; the default dither of every quantizer.</summary>
    public static IDither FloydSteinberg { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 7 },
            { 3, 5, 1 },
        },
        originColumn: 1,
        divisor: 16);

    /// <summary>Bill Atkinson's kernel: diffuses only 6/8 of the error, giving high-contrast results.</summary>
    public static IDither Atkinson { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 1, 1 },
            { 1, 1, 1, 0 },
            { 0, 1, 0, 0 },
        },
        originColumn: 1,
        divisor: 8);

    /// <summary>Daniel Burkes' kernel: a two-row simplification of Stucki.</summary>
    public static IDither Burks { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 0, 8, 4 },
            { 2, 4, 8, 4, 2 },
        },
        originColumn: 2,
        divisor: 32);

    /// <summary>Jarvis, Judice and Ninke (1976): a 12-tap, three-row kernel.</summary>
    public static IDither JarvisJudiceNinke { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 0, 7, 5 },
            { 3, 5, 7, 5, 3 },
            { 1, 3, 5, 3, 1 },
        },
        originColumn: 2,
        divisor: 48);

    /// <summary>Frankie Sierra's two-row kernel.</summary>
    public static IDither Sierra2 { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 0, 4, 3 },
            { 1, 2, 3, 2, 1 },
        },
        originColumn: 2,
        divisor: 16);

    /// <summary>Frankie Sierra's three-row kernel.</summary>
    public static IDither Sierra3 { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 0, 5, 3 },
            { 2, 4, 5, 4, 2 },
            { 0, 2, 3, 2, 0 },
        },
        originColumn: 2,
        divisor: 32);

    /// <summary>Frankie Sierra's "Filter Lite": three taps, very fast.</summary>
    public static IDither SierraLite { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 2 },
            { 1, 1, 0 },
        },
        originColumn: 1,
        divisor: 4);

    /// <summary>Peter Stucki's kernel: a sharper variant of Jarvis–Judice–Ninke.</summary>
    public static IDither Stucki { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 0, 8, 4 },
            { 2, 4, 8, 4, 2 },
            { 1, 2, 4, 2, 1 },
        },
        originColumn: 2,
        divisor: 42);

    /// <summary>Stevenson and Arce's hexagonal 12-tap kernel over four rows.</summary>
    public static IDither StevensonArce { get; } = new ErrorDither(
        new[,]
        {
            { 0, 0, 0, 0, 0, 32, 0 },
            { 12, 0, 26, 0, 30, 0, 16 },
            { 0, 12, 0, 26, 0, 12, 0 },
            { 5, 0, 12, 0, 12, 0, 5 },
        },
        originColumn: 3,
        divisor: 200);

    // ----- Ordered -----

    /// <summary>The 2x2 Bayer matrix.</summary>
    public static IDither Bayer2x2 { get; } = OrderedDither.CreateBayer(2);

    /// <summary>The 4x4 Bayer matrix.</summary>
    public static IDither Bayer4x4 { get; } = OrderedDither.CreateBayer(4);

    /// <summary>The 8x8 Bayer matrix.</summary>
    public static IDither Bayer8x8 { get; } = OrderedDither.CreateBayer(8);

    /// <summary>The 16x16 Bayer matrix.</summary>
    public static IDither Bayer16x16 { get; } = OrderedDither.CreateBayer(16);

    /// <summary>A 3x3 dispersed-dot threshold matrix.</summary>
    public static IDither Ordered3x3 { get; } = new OrderedDither(
        new[,]
        {
            { 0, 5, 2 },
            { 3, 8, 7 },
            { 6, 1, 4 },
        });
}
