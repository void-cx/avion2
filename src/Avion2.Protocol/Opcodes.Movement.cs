namespace Avion2.Protocol;

public static partial class Opcodes
{
    // Movement offsets: int8 cm per 100 ms tick from the entity's previous movement record (see docs/PROTOCOL.md)
    public const ushort MoveOffset = 0x1D37;
    public const ushort FallOffset = 0x2037;
    public const ushort GlideOffset = 0x2537;
    public const ushort FlyOffset = 0x2A37;
    public const ushort RollOffset = 0x3237;
    public const ushort SwimOffset = 0x3837;

    // Keyframes (absolute position) of the jump/fall, glide, flight, dodge roll and swim streams
    public const ushort FallStart = 0x1E37;
    public const ushort FallSnapshot = 0x1F37;
    public const ushort GlideStart = 0x2337;
    public const ushort GlideSnapshot = 0x2437;
    public const ushort FlyStart = 0x2837;
    public const ushort FlySnapshot = 0x2937;
    public const ushort RollStart = 0x3037;
    public const ushort SwimStart = 0x3637;
    public const ushort SwimSnapshot = 0x3737;

    /// <summary><c>3E</c>/<c>3F</c>/<c>40 37</c>: rare position records, mostly ours.</summary>
    public const ushort PositionNote1 = 0x3E37;
    public const ushort PositionNote2 = 0x3F37;
    public const ushort PositionNote3 = 0x4037;

    /// <summary><c>46 37</c>: the height (z) where we last stood on the ground.</summary>
    public const ushort GroundHeight = 0x4637;

    /// <summary><c>4E 36</c>: an entity turns (also sent for us).</summary>
    public const ushort Rotate = 0x4E36;
}
