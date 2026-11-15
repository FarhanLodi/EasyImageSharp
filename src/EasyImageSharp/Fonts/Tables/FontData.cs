namespace EasyImageSharp.Fonts.Tables;

/// <summary>Reads whole font files and streams, refusing anything larger than <see cref="FontContainer.MaxFontBytes"/>.</summary>
internal static class FontData
{
    public static byte[] ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
        return ReadStream(stream);
    }

    public static byte[] ReadStream(Stream stream)
    {
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining > FontContainer.MaxFontBytes)
            {
                throw TooLarge();
            }

            byte[] buffer = new byte[Math.Max(0, remaining)];
            stream.ReadExactly(buffer);
            return buffer;
        }

        using var copy = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (copy.Length + read > FontContainer.MaxFontBytes)
            {
                throw TooLarge();
            }

            copy.Write(chunk, 0, read);
        }

        return copy.ToArray();
    }

    private static InvalidFontException TooLarge()
        => new($"Malformed font: the data exceeds the {FontContainer.MaxFontBytes} byte limit.");
}
