namespace ExtractGameData.Core;

/// <summary>
/// Reads a decrypted text file (<c>L10N/Text/&lt;locale&gt;/L10NString.dat</c>): <c>i32 1</c>, a string (the
/// namespace, "AION2"), <c>i32 n</c>, n pairs of strings (key, text) and <c>i32 0</c> (an empty second list). The
/// strings are plain: <c>i32 n</c> and n UTF-8 bytes, or -n UTF-16 units, each including the closing NUL.
/// </summary>
public static class L10nReader
{
    public static IReadOnlyDictionary<string, string> Read(byte[] body, out string ns)
    {
        var r = new ByteReader(body);
        int version = r.Int32();
        if (version != 1)
        {
            throw new GameDataFormatException($"text file version {version}, expected 1");
        }

        ns = r.String(masked: false);
        int count = r.Int32();
        if (count < 0 || count > r.Remaining / 8)
        {
            throw new GameDataFormatException($"bad text count {count}");
        }

        var texts = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = r.String(masked: false);
            if (!texts.TryAdd(key, r.String(masked: false)))
            {
                throw new GameDataFormatException($"text key '{key}' twice");
            }
        }

        int trailer = r.Int32();
        if (trailer != 0 || r.Remaining != 0)
        {
            throw new GameDataFormatException($"text file ends with {trailer} and {r.Remaining} more bytes");
        }

        return texts;
    }
}
