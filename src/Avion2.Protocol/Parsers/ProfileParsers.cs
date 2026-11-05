namespace Avion2.Protocol.Parsers;

/// <summary>Another player's profile as the game shows it when we open it. See docs/PROTOCOL.md.</summary>
public static class ProfileParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.CharacterProfile => ParseProfile(body, ts, emit),
        _ => false,
    };

    /// <summary>Right before the combat power, near the end of the record: a list of 4 (6, 14, 16, 20).</summary>
    private static ReadOnlySpan<byte> CombatPowerMarker => [0x04, 0x06, 0x0E, 0x10, 0x14];

    /// <summary>
    /// <c>50 36</c>: <c>u16</c> result (0), <c>u8</c>, name <c>str8</c>, class <c>u32</c>, faction <c>u8</c>,
    /// <c>u8</c>, level <c>u32</c>, <c>u32</c>, item level <c>u32</c>, server <c>u16</c>, <c>u16</c>, <c>u8</c>,
    /// title <c>u32</c>, legion id <c>u64</c>, legion <c>str8</c> (empty without one), rank <c>u16</c>, then the
    /// gear. The combat power (<c>u64</c>) follows the last <see cref="CombatPowerMarker"/>.
    /// </summary>
    public static bool ParseProfile(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (!r.TryU16(out ushort result) || result != 0 || !r.TrySkip(1) ||
            !r.TryU8(out byte nameLength) || nameLength is 0 or > 36 || !r.TryUtf8(nameLength, out string name) ||
            !r.TryU32(out uint classCode) || GameClasses.FromRosterCode(classCode) == GameClass.Unknown ||
            !r.TryU8(out byte faction) || faction is not (1 or 2) || !r.TrySkip(1) ||
            !r.TryU32(out uint level) || level is 0 or > 100 || !r.TrySkip(4) ||
            !r.TryU32(out uint itemLevel) || itemLevel > 100_000 ||
            !r.TryU16(out ushort server) || server is < 1000 or > 2999 ||
            !r.TrySkip(2 + 1 + 4 + 8) || !r.TryU8(out byte legionLength))
        {
            return false;
        }

        string? legion = null;
        if (legionLength > 0)
        {
            if (legionLength > 36 || !r.TryUtf8(legionLength, out string named))
            {
                return false;
            }

            legion = named;
        }

        emit(new CharacterProfileEvent(ts, name, server, GameClasses.FromRosterCode(classCode), level, itemLevel, legion, CombatPower(body)));
        return true;
    }

    private static uint? CombatPower(ReadOnlySpan<byte> body)
    {
        int at = body.LastIndexOf(CombatPowerMarker);
        if (at < 0 || at + CombatPowerMarker.Length + 8 > body.Length)
        {
            return null;
        }

        ulong power = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(body[(at + CombatPowerMarker.Length)..]);
        return power is > 0 and < 10_000_000 ? (uint)power : null;
    }
}
