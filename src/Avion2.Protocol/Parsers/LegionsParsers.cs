namespace Avion2.Protocol.Parsers;

/// <summary>The legion parts of records that are about something else.</summary>
public static class LegionsParsers
{
    /// <summary>
    /// The legion block of a player spawn (<c>45 36</c>), present only for players in a legion: after the position,
    /// <c>u16</c>, <c>00 00 00 00</c>, server <c>u16</c>, legion (u8 len, UTF-8), then <c>01 00</c> or <c>02 00</c>
    /// (e.g. <c>06 01 00 00 00 00 29 05 07 …legion… 01 00 02 00</c>). The first such spot after the name is taken.
    /// Checked against the legion tag (<c>33 8A</c>) of the same player: equal in 148 of 148 spawns that had one.
    /// </summary>
    /// <param name="from">Where to start looking (after the name).</param>
    public static (ushort Server, string Legion)? FindSpawnLegion(ReadOnlySpan<byte> body, int from)
    {
        for (int at = Math.Max(0, from); at + 9 <= body.Length; at++)
        {
            if (body[at] != 0 || body[at + 1] != 0 || body[at + 2] != 0 || body[at + 3] != 0)
            {
                continue;
            }

            var r = new SpanReader(body, at + 4);
            if (!r.TryU16(out ushort server) || server is < 1000 or > 2999 || !r.TryU8(out byte length) || length is 0 or > 36 ||
                !r.TryUtf8(length, out string legion) || !r.TryU8(out byte next) || next is not (1 or 2) ||
                !r.TryU8(out byte zero) || zero != 0)
            {
                continue;
            }

            return (server, legion);
        }

        return null;
    }
}
