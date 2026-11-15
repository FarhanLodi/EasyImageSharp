using System.Buffers.Binary;
using System.Text;

namespace EasyImageSharp.Avif.Tests;

/// <summary>
/// Writes a small, valid ICC v2.1 RGB display profile (matrix/TRC with a pure 2.2 gamma and D50-adapted BT.709
/// primaries) straight from the ICC.1:2001-04 layout, so tests need no third-party profile file. The gamma makes it
/// deliberately different from sRGB.
/// </summary>
internal static class SyntheticIcc
{
    internal static byte[] CreateRgbMatrixProfile()
    {
        byte[] desc = TextDescription("EasyImageSharp test RGB gamma 2.2");
        byte[] cprt = Text("No copyright, use freely.");
        byte[] white = Xyz(0.9642, 1.0, 0.8249);
        byte[] red = Xyz(0.4361, 0.2225, 0.0139);
        byte[] green = Xyz(0.3851, 0.7169, 0.0971);
        byte[] blue = Xyz(0.1431, 0.0606, 0.7141);
        byte[] curve = new byte[14];
        "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), 0x0233); // u8Fixed8 2.2 (2 + 51/256)

        (string Signature, byte[] Data)[] tags =
        [
            ("desc", desc), ("cprt", cprt), ("wtpt", white), ("rXYZ", red), ("gXYZ", green), ("bXYZ", blue),
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
        ];
        int offset = 128 + 4 + (tags.Length * 12);
        using var body = new MemoryStream();
        var entries = new List<(string Signature, int Offset, int Size)>();
        var shared = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        foreach ((string signature, byte[] data) in tags)
        {
            if (!shared.TryGetValue(data, out int position))
            {
                position = offset + (int)body.Length;
                body.Write(data);
                while (body.Length % 4 != 0)
                {
                    body.WriteByte(0);
                }

                shared[data] = position;
            }

            entries.Add((signature, position, data.Length));
        }

        byte[] profile = new byte[offset + (int)body.Length];
        Span<byte> header = profile;
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 0x02100000);
        Encoding.ASCII.GetBytes("mntr").CopyTo(header[12..]);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(header[16..]);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(header[20..]);
        BinaryPrimitives.WriteUInt16BigEndian(header[24..], 2026);
        BinaryPrimitives.WriteUInt16BigEndian(header[26..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(header[28..], 1);
        Encoding.ASCII.GetBytes("acsp").CopyTo(header[36..]);
        Xyz(0.9642, 1.0, 0.8249).AsSpan(8, 12).CopyTo(header[68..]);
        BinaryPrimitives.WriteUInt32BigEndian(header[128..], (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Span<byte> entry = header.Slice(132 + (i * 12), 12);
            Encoding.ASCII.GetBytes(entries[i].Signature).CopyTo(entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)entries[i].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)entries[i].Size);
        }

        body.ToArray().CopyTo(profile, offset);
        return profile;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        byte[] data = new byte[20];
        "XYZ "u8.CopyTo(data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), (int)Math.Round(x * 65536));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), (int)Math.Round(y * 65536));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), (int)Math.Round(z * 65536));
        return data;
    }

    private static byte[] Text(string value)
    {
        byte[] data = new byte[8 + value.Length + 1];
        "text"u8.CopyTo(data);
        Encoding.ASCII.GetBytes(value).CopyTo(data, 8);
        return data;
    }

    private static byte[] TextDescription(string value)
    {
        // 'desc' (textDescriptionType): ASCII part, empty Unicode part, empty ScriptCode part with its 67-byte field.
        byte[] data = new byte[8 + 4 + value.Length + 1 + 4 + 4 + 2 + 1 + 67];
        "desc"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)(value.Length + 1));
        Encoding.ASCII.GetBytes(value).CopyTo(data, 12);
        return data;
    }
}
