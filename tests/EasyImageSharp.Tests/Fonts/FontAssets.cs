using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using EasyImageSharp.Fonts;

namespace EasyImageSharp.Tests.Fonts;

/// <summary>
/// Locates the committed test fonts and the independent reference data (<c>reference.json</c>, written by
/// <c>gen_fonts.py</c> with fontTools and FreeType). The files live in the test project's <c>Fonts</c> folder;
/// they are found next to the test binaries when copied there, and otherwise in the source tree.
/// </summary>
internal static class FontAssets
{
    public const string Regular = "DejaVuSans-Subset.ttf";
    public const string Bold = "DejaVuSans-Bold-Subset.ttf";
    public const string Cff = "DejaVuSans-Subset.otf";
    public const string Cid = "DejaVuSans-Subset-CID.otf";
    public const string Collection = "DejaVuSans-Subset.ttc";
    public const string Woff = "DejaVuSans-Subset.woff";
    public const string Woff2 = "DejaVuSans-Subset.woff2";

    private static readonly Lazy<string> Root = new(Locate);
    private static readonly Lazy<JsonDocument> ReferenceDocument = new(() => JsonDocument.Parse(File.ReadAllBytes(Path(("reference.json")))));
    private static readonly ConcurrentDictionary<string, FontFace> Faces = new();

    public static string Path(string name) => System.IO.Path.Combine(Root.Value, name);

    public static byte[] Read(string name) => File.ReadAllBytes(Path(name));

    /// <summary>A shared, loaded face (faces are immutable, so tests can share them).</summary>
    public static FontFace Face(string name) => Faces.GetOrAdd(name, static n => FontFace.Load(Path(n)));

    /// <summary>The reference entry of one font file.</summary>
    public static JsonElement Reference(string name) => ReferenceDocument.Value.RootElement.GetProperty("fonts").GetProperty(name);

    private static string Locate()
    {
        string copied = System.IO.Path.Combine(AppContext.BaseDirectory, "Fonts");
        if (File.Exists(System.IO.Path.Combine(copied, "reference.json")))
        {
            return copied;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (string candidate in new[]
            {
                System.IO.Path.Combine(dir.FullName, "Fonts"),
                System.IO.Path.Combine(dir.FullName, "tests", "EasyImageSharp.Tests", "Fonts"),
            })
            {
                if (File.Exists(System.IO.Path.Combine(candidate, "reference.json")) && File.Exists(System.IO.Path.Combine(candidate, Regular)))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("The test fonts (tests/EasyImageSharp.Tests/Fonts) were not found.");
    }
}

/// <summary>Assembles minimal sfnt fonts from raw tables, for crafted and malformed-input tests.</summary>
internal sealed class SfntBuilder
{
    private readonly SortedDictionary<string, byte[]> tables = new(StringComparer.Ordinal);

    public uint Flavor { get; set; } = 0x00010000;

    public SfntBuilder Add(string tag, byte[] data)
    {
        this.tables[tag] = data;
        return this;
    }

    public SfntBuilder Remove(string tag)
    {
        this.tables.Remove(tag);
        return this;
    }

    public byte[] Build()
    {
        int count = this.tables.Count;
        int offset = 12 + (count * 16);
        using var output = new MemoryStream();
        var header = new byte[12 + (count * 16)];
        BinaryPrimitives.WriteUInt32BigEndian(header, this.Flavor);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)count);
        int i = 0;
        var bodies = new List<byte[]>();
        foreach ((string tag, byte[] data) in this.tables)
        {
            int record = 12 + (i * 16);
            for (int k = 0; k < 4; k++)
            {
                header[record + k] = (byte)tag[k];
            }

            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(record + 12), (uint)data.Length);
            int padded = (data.Length + 3) & ~3;
            byte[] body = new byte[padded];
            data.CopyTo(body, 0);
            bodies.Add(body);
            offset += padded;
            i++;
        }

        output.Write(header);
        foreach (byte[] body in bodies)
        {
            output.Write(body);
        }

        return output.ToArray();
    }

    /// <summary>A builder holding the tables every face needs (head, hhea, maxp, hmtx, cmap) for <paramref name="glyphs"/> glyphs.</summary>
    public static SfntBuilder Minimal(int glyphs, int unitsPerEm = 1000, short indexToLocFormat = 1)
    {
        var builder = new SfntBuilder();
        builder.Add("head", Head(unitsPerEm, indexToLocFormat));
        builder.Add("hhea", Hhea(800, -200, 0, glyphs));
        builder.Add("maxp", Maxp(glyphs));
        var hmtx = new byte[glyphs * 4];
        for (int g = 0; g < glyphs; g++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), 600);
        }

        builder.Add("hmtx", hmtx);
        builder.Add("cmap", CmapFormat4((char)'A', 1));
        return builder;
    }

    public static byte[] Head(int unitsPerEm, short indexToLocFormat)
    {
        var head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head, 1);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), (ushort)unitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), indexToLocFormat);
        return head;
    }

    public static byte[] Hhea(short ascender, short descender, short lineGap, int metrics)
    {
        var hhea = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(hhea, 1);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), ascender);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), descender);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(8), lineGap);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)metrics);
        return hhea;
    }

    public static byte[] Maxp(int glyphs, bool cff = false)
    {
        var maxp = new byte[cff ? 6 : 32];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, cff ? 0x00005000u : 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphs);
        return maxp;
    }

    /// <summary>A (3,1) format 4 cmap mapping one character to one glyph.</summary>
    public static byte[] CmapFormat4(char c, int glyph)
    {
        // Two segments: [c, c] with an idDelta mapping to the glyph, and the 0xFFFF terminator.
        byte[] sub = new byte[16 + (2 * 8)];
        BinaryPrimitives.WriteUInt16BigEndian(sub, 4);
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(2), (ushort)sub.Length);
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(6), 4);
        int p = 14;
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(p), c);
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(p + 2), 0xFFFF);
        p += 6;
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(p), c);
        BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(p + 2), 0xFFFF);
        p += 4;
        BinaryPrimitives.WriteInt16BigEndian(sub.AsSpan(p), (short)(glyph - c));
        BinaryPrimitives.WriteInt16BigEndian(sub.AsSpan(p + 2), 1);
        return CmapWith(3, 1, sub);
    }

    /// <summary>Wraps one subtable in a cmap header with a single encoding record.</summary>
    public static byte[] CmapWith(int platform, int encoding, byte[] subtable)
    {
        byte[] cmap = new byte[12 + subtable.Length];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), (ushort)platform);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), (ushort)encoding);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        subtable.CopyTo(cmap, 12);
        return cmap;
    }

    /// <summary>A glyf/loca pair (long loca) from per-glyph data.</summary>
    public static (byte[] Glyf, byte[] Loca) GlyfLoca(params byte[][] glyphs)
    {
        using var glyf = new MemoryStream();
        byte[] loca = new byte[(glyphs.Length + 1) * 4];
        for (int g = 0; g < glyphs.Length; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Length);
            glyf.Write(glyphs[g]);
            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyphs.Length * 4), (uint)glyf.Length);
        return (glyf.ToArray(), loca);
    }

    /// <summary>A simple glyph: one closed polygon of on-curve points (16-bit coordinates).</summary>
    public static byte[] SimpleGlyph(params (short X, short Y)[] points)
    {
        using var ms = new MemoryStream();
        void W16(int v)
        {
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }

        W16(1);
        W16(points.Min(p => p.X));
        W16(points.Min(p => p.Y));
        W16(points.Max(p => p.X));
        W16(points.Max(p => p.Y));
        W16(points.Length - 1);
        W16(0);
        foreach (var _ in points)
        {
            ms.WriteByte(0x01);
        }

        int last = 0;
        foreach ((short x, _) in points)
        {
            W16(x - last);
            last = x;
        }

        last = 0;
        foreach ((_, short y) in points)
        {
            W16(y - last);
            last = y;
        }

        return ms.ToArray();
    }

    /// <summary>A composite glyph referencing <paramref name="components"/> with byte offsets (0, 0).</summary>
    public static byte[] CompositeGlyph(params int[] components)
    {
        using var ms = new MemoryStream();
        void W16(int v)
        {
            ms.WriteByte((byte)(v >> 8));
            ms.WriteByte((byte)v);
        }

        W16(-1);
        W16(0);
        W16(0);
        W16(100);
        W16(100);
        for (int i = 0; i < components.Length; i++)
        {
            int flags = 0x0002 | (i < components.Length - 1 ? 0x0020 : 0);
            W16(flags);
            W16(components[i]);
            ms.WriteByte(0);
            ms.WriteByte(0);
        }

        return ms.ToArray();
    }
}
