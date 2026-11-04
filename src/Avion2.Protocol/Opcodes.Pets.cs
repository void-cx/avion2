namespace Avion2.Protocol;

public static partial class Opcodes
{
    /// <summary>
    /// <c>14 90</c>: one of our pets went up a level (25 souls of its kind are spent with it, by <c>0E 90</c> in the
    /// same instant). The layout follows <c>09 90</c> (a new pet).
    /// </summary>
    public const ushort PetLevelUp = 0x1490;
}
