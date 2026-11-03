namespace Avion2.Protocol.Parsers;

/// <summary>Movement offsets (10 Hz deltas) and the rarer records that carry our own position or facing. See docs/PROTOCOL.md.</summary>
public static class MovementParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        _ => false,
    };
}
