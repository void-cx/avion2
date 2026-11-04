using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Tests;

public class GameIconTests
{
    private static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    [Fact]
    public void SkillVariantsTakeTheirBaseSkillsIcon()
    {
        var table = IconTable.Parse("""{"icons": {"18010000": "ICON_CH_SKILL_001", "11000000": "ICON_GL_SKILL_027"}}""");
        Assert.Equal(2, table.Count);
        Assert.Equal("ICON_CH_SKILL_001", table.Skill(18010000));
        Assert.Equal("ICON_CH_SKILL_001", table.Skill(18010010)); // one specialisation
        Assert.Equal("ICON_CH_SKILL_001", table.Skill(18013450)); // three
        Assert.Equal("ICON_GL_SKILL_027", table.Skill(11000000)); // listed exactly
        Assert.Null(table.Skill(18020000)); // another skill of the class
        Assert.Null(table.Skill(11000100)); // a basic action is not a variant of 11000000
        Assert.Null(table.Skill(1801000));  // monster skill
        Assert.Null(table.Skill(0));
    }

    [Fact]
    public void ItemsAreLookedUpExactly()
    {
        var table = IconTable.Parse("""{"icons": {"110630047": "Icon_WP_OR_0078_T05"}}""");
        Assert.Equal("Icon_WP_OR_0078_T05", table.Exact(110630047));
        Assert.Null(table.Exact(110630048));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"icons": {"x": "A", "5": 7, "6": ""}}""")]
    public void BrokenTablesAreEmpty(string? json) => Assert.Equal(0, IconTable.Parse(json).Count);

    [Fact]
    public void ShipsSkillIconsForEveryClass()
    {
        var skills = GameIcons.Skills;
        Assert.Equal("ICON_CH_SKILL_001", skills.Skill(18010000)); // Onslaught
        Assert.Equal("ICON_CH_SKILL_001", skills.Skill(18011450));
        foreach (uint prefix in new uint[] { 11, 12, 13, 14, 15, 16, 17, 18, 19 })
        {
            Assert.NotNull(skills.Skill(prefix * 1_000_000 + 10_000)); // each class's first skill
        }

        Assert.Null(skills.Skill(1801000));
        Assert.Null(skills.Skill(999));
    }

    [Fact]
    public void EveryListedIconIsBuiltIn()
    {
        foreach (var (set, table) in new[] { ("skillicons", GameIcons.Skills), ("itemicons", GameIcons.Items) })
        {
            Assert.True(table.Count > 100, set);
            string json = BundledGameData.Read($"{set}/index.json")!;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var names = doc.RootElement.GetProperty("icons").EnumerateObject().Select(e => e.Value.GetString()!).Distinct();
            foreach (string name in names)
            {
                var png = GameIcons.Bundled(set, name);
                Assert.True(png is { Length: > 8 } && png.AsSpan(0, 4).SequenceEqual(PngMagic), $"{set}/{name}");
            }
        }
    }

    [Fact]
    public void LoadsBuiltInIcons()
    {
        string empty = Path.Combine(Path.GetTempPath(), "avion2-icons-" + Guid.NewGuid().ToString("N"));
        Assert.True(GameIcons.SkillIcon(18010010, empty) is { } skill && skill.AsSpan(0, 4).SequenceEqual(PngMagic));
        Assert.True(GameIcons.ItemIcon(ItemIds.Kinah, empty) is { } kinah && kinah.AsSpan(0, 4).SequenceEqual(PngMagic));
        Assert.Null(GameIcons.SkillIcon(1801000, empty));
        Assert.Null(GameIcons.ItemIcon(1, empty));
    }

    [Fact]
    public void TheOverrideFolderReplacesIcons()
    {
        string dir = Path.Combine(Path.GetTempPath(), "avion2-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // By the icon's name: every skill using it
            File.WriteAllBytes(Path.Combine(dir, "ICON_CH_SKILL_001.png"), [1]);
            Assert.Equal(new byte[] { 1 }, GameIcons.SkillIcon(18010000, dir));
            Assert.Equal(new byte[] { 1 }, GameIcons.SkillIcon(18010120, dir));

            // By the base skill's id, then by the exact id
            File.WriteAllBytes(Path.Combine(dir, "18010000.png"), [2]);
            Assert.Equal(new byte[] { 2 }, GameIcons.SkillIcon(18010120, dir));
            File.WriteAllBytes(Path.Combine(dir, "18010120.png"), [3]);
            Assert.Equal(new byte[] { 3 }, GameIcons.SkillIcon(18010120, dir));
            Assert.Equal(new byte[] { 2 }, GameIcons.SkillIcon(18010010, dir));

            // A skill or item without a built-in icon can get one
            File.WriteAllBytes(Path.Combine(dir, "1801000.png"), [4]);
            Assert.Equal(new byte[] { 4 }, GameIcons.SkillIcon(1801000, dir));
            File.WriteAllBytes(Path.Combine(dir, "42.png"), [5]);
            Assert.Equal(new byte[] { 5 }, GameIcons.ItemIcon(42, dir));

            // Other files leave the built-in icons alone
            Assert.True(GameIcons.ItemIcon(ItemIds.Kinah, dir) is { Length: > 8 });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
