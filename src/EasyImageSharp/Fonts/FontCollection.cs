using System.Diagnostics.CodeAnalysis;
using EasyImageSharp.Fonts.Tables;

namespace EasyImageSharp.Fonts;

/// <summary>
/// A set of font faces grouped into families by name. Add faces from files, streams or memory, then look families
/// up by name (case-insensitively). Safe to use from several threads.
/// </summary>
public sealed class FontCollection
{
    private readonly object gate = new();
    private readonly Dictionary<string, FontFamily> families = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The families in the collection, ordered by name.</summary>
    public IReadOnlyList<FontFamily> Families
    {
        get
        {
            lock (this.gate)
            {
                return this.families.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
    }

    /// <summary>Loads every face of a font file and adds it.</summary>
    /// <returns>The faces added (several for a collection file).</returns>
    /// <inheritdoc cref="FontFace.Load(string)" path="/exception"/>
    public IReadOnlyList<FontFace> Add(string path)
    {
        IReadOnlyList<FontFace> faces = FontFace.LoadAll(path);
        foreach (FontFace face in faces)
        {
            this.Add(face);
        }

        return faces;
    }

    /// <summary>Loads every face of a font read from <paramref name="stream"/> and adds it.</summary>
    /// <returns>The faces added (several for a collection).</returns>
    /// <inheritdoc cref="FontFace.Load(string)" path="/exception"/>
    public IReadOnlyList<FontFace> Add(Stream stream)
    {
        IReadOnlyList<FontFace> faces = FontFace.LoadAll(stream);
        foreach (FontFace face in faces)
        {
            this.Add(face);
        }

        return faces;
    }

    /// <summary>Loads every face of font data (the bytes are copied) and adds it.</summary>
    /// <returns>The faces added (several for a collection).</returns>
    /// <inheritdoc cref="FontFace.Load(string)" path="/exception"/>
    public IReadOnlyList<FontFace> Add(ReadOnlySpan<byte> data)
    {
        IReadOnlyList<FontFace> faces = FontFace.LoadAll(data);
        foreach (FontFace face in faces)
        {
            this.Add(face);
        }

        return faces;
    }

    /// <summary>Adds an already loaded face to the family named by its <see cref="FontFace.FamilyName"/>.</summary>
    public void Add(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        this.GetOrCreateFamily(face.FamilyName).Add(new FontFamily.FaceEntry(face));
    }

    /// <summary>Gets the family named <paramref name="name"/> (case-insensitive).</summary>
    public bool TryGetFamily(string name, [NotNullWhen(true)] out FontFamily? family)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (this.gate)
        {
            return this.families.TryGetValue(name, out family);
        }
    }

    /// <summary>Gets the family named <paramref name="name"/> (case-insensitive).</summary>
    /// <exception cref="KeyNotFoundException">The collection has no such family.</exception>
    public FontFamily GetFamily(string name)
        => this.TryGetFamily(name, out FontFamily? family)
            ? family
            : throw new KeyNotFoundException($"The font family '{name}' is not in the collection.");

    /// <summary>
    /// Indexes a font file without loading it: its names and styles are read now, and each face is parsed when it
    /// is first used. Files that cannot be read or are not fonts are skipped.
    /// </summary>
    /// <returns>The number of faces indexed.</returns>
    internal int AddLazy(string path)
    {
        IReadOnlyList<FontDescriptor> descriptors = FontDescriptor.TryRead(path);
        foreach (FontDescriptor d in descriptors)
        {
            int index = d.FaceIndex;
            this.GetOrCreateFamily(d.FamilyName).Add(new FontFamily.FaceEntry(d.Style, d.Weight, () => FontFace.Load(path, index)));
        }

        return descriptors.Count;
    }

    private FontFamily GetOrCreateFamily(string name)
    {
        lock (this.gate)
        {
            if (!this.families.TryGetValue(name, out FontFamily? family))
            {
                family = new FontFamily(name);
                this.families.Add(name, family);
            }

            return family;
        }
    }
}
