namespace Avion2.Protocol.Parsers;

/// <summary>World events and schedules: the Airspace Battle, field events, leaving a dungeon, NPC conversations. See docs/PROTOCOL.md.</summary>
public static class WorldEventParsers
{
    /// <summary>Decodes the opcode if it is one of this area's; false otherwise.</summary>
    public static bool TryParse(ushort opcode, ReadOnlySpan<byte> body, long ts, Action<GameEvent> emit) => opcode switch
    {
        _ => false,
    };
}
