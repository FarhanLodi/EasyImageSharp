namespace EasyImageSharp.Fonts;

/// <summary>
/// The faces of one family in a <see cref="FontCollection"/> (for example Regular, Bold, Italic and Bold Italic).
/// Faces discovered on disk are loaded on first use. Safe to use from several threads.
/// </summary>
public sealed class FontFamily
{
    private readonly object gate = new();
    private readonly List<FaceEntry> entries = new();

    internal FontFamily(string name) => this.Name = name;

    /// <summary>The family name.</summary>
    public string Name { get; }

    /// <summary>The styles the family has a face for.</summary>
    public IReadOnlyList<FontStyle> AvailableStyles
    {
        get
        {
            lock (this.gate)
            {
                return this.entries.Select(e => e.Style).Distinct().Order().ToArray();
            }
        }
    }

    /// <summary>
    /// Returns the face that best matches <paramref name="style"/>: an exact style match (of several, the one
    /// whose weight is closest to 400 for regular or 700 for bold), otherwise the closest available style.
    /// </summary>
    /// <exception cref="InvalidFontException">The face is loaded from disk now and turns out to be malformed.</exception>
    public FontFace GetFace(FontStyle style)
    {
        FaceEntry entry;
        lock (this.gate)
        {
            entry = this.Match(style);
        }

        return entry.Load();
    }

    /// <summary>Returns the regular face, or the closest available style.</summary>
    /// <inheritdoc cref="GetFace(FontStyle)" path="/exception"/>
    public FontFace GetFace() => this.GetFace(FontStyle.Regular);

    /// <summary>Gets a face with exactly <paramref name="style"/>, when the family has one.</summary>
    /// <inheritdoc cref="GetFace(FontStyle)" path="/exception"/>
    public bool TryGetFace(FontStyle style, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FontFace? face)
    {
        FaceEntry? entry;
        lock (this.gate)
        {
            entry = this.Match(style);
            if (entry.Style != style)
            {
                entry = null;
            }
        }

        face = entry?.Load();
        return face is not null;
    }

    /// <summary>Creates a font from the face that best matches <paramref name="style"/>, sized in pixels.</summary>
    /// <inheritdoc cref="Font(FontFace, float)" path="/exception"/>
    public Font CreateFont(float sizeInPixels, FontStyle style) => new(this.GetFace(style), sizeInPixels);

    /// <summary>Creates a font from the regular face (or the closest style), sized in pixels.</summary>
    /// <inheritdoc cref="Font(FontFace, float)" path="/exception"/>
    public Font CreateFont(float sizeInPixels) => new(this.GetFace(FontStyle.Regular), sizeInPixels);

    /// <inheritdoc/>
    public override string ToString() => this.Name;

    internal void Add(FaceEntry entry)
    {
        lock (this.gate)
        {
            this.entries.Add(entry);
        }
    }

    private FaceEntry Match(FontStyle style)
    {
        // Prefer the exact style, then the same slant, then the same weight, then anything.
        FaceEntry? best = null;
        int bestScore = int.MaxValue;
        int targetWeight = (style & FontStyle.Bold) != 0 ? 700 : 400;
        foreach (FaceEntry entry in this.entries)
        {
            int score = Math.Abs(entry.Weight - targetWeight);
            if (((entry.Style ^ style) & FontStyle.Italic) != 0)
            {
                score += 100_000;
            }

            if (((entry.Style ^ style) & FontStyle.Bold) != 0)
            {
                score += 10_000;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }

        return best ?? throw new InvalidOperationException($"The font family '{this.Name}' has no faces.");
    }

    /// <summary>One face of the family: already loaded, or a file and face index loaded on first use.</summary>
    internal sealed class FaceEntry
    {
        private readonly Lazy<FontFace> face;

        public FaceEntry(FontFace face)
        {
            this.face = new Lazy<FontFace>(face);
            this.Style = face.Style;
            this.Weight = face.Weight;
        }

        public FaceEntry(FontStyle style, int weight, Func<FontFace> loader)
        {
            this.face = new Lazy<FontFace>(loader, LazyThreadSafetyMode.ExecutionAndPublication);
            this.Style = style;
            this.Weight = weight;
        }

        public FontStyle Style { get; }

        public int Weight { get; }

        public FontFace Load() => this.face.Value;
    }
}
