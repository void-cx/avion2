namespace Avion2.Protocol.Parsers;

/// <summary>Boss and fight mechanics: casts with a cast time, telegraphs, the stagger gauge. See docs/PROTOCOL.md.</summary>
public static class MechanicsParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        _ => false,
    };
}
