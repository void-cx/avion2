using Avion2.Analysis;

namespace Avion2.Engine;

/// <summary>Names for group-finder rooms from the built-in dungeon name table (English).</summary>
public static class FinderData
{
    private static readonly Lazy<IReadOnlyDictionary<uint, string>> Names =
        new(() => FinderDungeons.Parse(BundledGameData.Read("dungeon_names.json")));

    /// <summary>"Krao Cave (Conquest)", or "Dungeon 600021" when the tables do not know the id.</summary>
    public static string DungeonName(uint dungeonId) => FinderDungeons.Name(Names.Value, dungeonId);
}
