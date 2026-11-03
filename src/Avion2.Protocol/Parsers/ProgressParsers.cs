namespace Avion2.Protocol.Parsers;

/// <summary>Character progress: item level, experience, currencies with daily or weekly caps, tickets, the pass, pets, items used. See docs/PROTOCOL.md.</summary>
public static class ProgressParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        _ => false,
    };
}
