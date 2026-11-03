namespace Avion2.Protocol.Parsers;

/// <summary>Party member status and party-wide records beyond the roster. See docs/PROTOCOL.md.</summary>
public static class PartyParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        _ => false,
    };
}
