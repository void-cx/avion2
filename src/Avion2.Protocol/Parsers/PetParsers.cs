namespace Avion2.Protocol.Parsers;

/// <summary>Pet records beyond the roster, souls and new pets (those are in <see cref="ProgressParsers"/>). See docs/PROTOCOL.md.</summary>
public static class PetParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        Opcodes.PetLevelUp => ParsePetLevelUp(body, ts, emit),
        _ => false,
    };

    /// <summary>Pet levels above this are not real (the highest seen is 3).</summary>
    private const uint MaxLevel = 100;

    /// <summary>
    /// <c>14 90</c>: <c>u16</c> result (0), <c>u32</c> pet, <c>u32</c> new level, <c>u8</c> creature type, <c>u32</c>
    /// board level, <c>u64</c> board experience (23 bytes).
    /// </summary>
    public static bool ParsePetLevelUp(ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit)
    {
        var r = new SpanReader(body);
        if (body.Length != 23 || !r.TryU16(out ushort result) || result != 0 || !r.TryU32(out uint pet) || pet == 0 ||
            !r.TryU32(out uint level) || level is 0 or > MaxLevel || !r.TryU8(out byte type))
        {
            return false;
        }

        emit(new PetLevelUpEvent(ts, pet, level, type));
        return true;
    }
}
