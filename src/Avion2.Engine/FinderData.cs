using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>Names for group-finder rooms from the built-in tables (English, like the dungeon table).</summary>
public static class FinderData
{
    private static readonly Lazy<IReadOnlyDictionary<uint, string>> Names =
        new(() => FinderDungeons.Build(BundledGameData.Read("npcs/en.json"), BundledGameData.Dungeons));

    /// <summary>"Krao Cave (Conquest [Normal])", or "Dungeon 600021" when the tables do not know the id.</summary>
    public static string DungeonName(uint dungeonId) => FinderDungeons.Name(Names.Value, dungeonId);
}
