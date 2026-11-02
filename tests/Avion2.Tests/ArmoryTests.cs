using System.Net;
using Avion2.Engine;

namespace Avion2.Tests;

public class ArmoryTests
{
    // Shortened answers of the official site for a character on Gauss (the id is made up)
    private const string Search =
        """{"list":[{"characterId":"abcDEF123%3D","name":"<strong>Doxy</strong>lamin","race":1,"pcId":34,"level":45,"serverId":1321,"serverName":"Gauss","region":"eu"}],"pagination":{"page":1,"size":20,"total":1,"endPage":1}}""";

    private const string Info = """
        {"profile":{"characterId":"abcDEF123=","characterLevel":45,"characterName":"Doxylamin","className":"Chanter","combatPower":64358,
          "raceName":"Elyos","regionName":"VOIDcx","serverId":1321,"serverName":"Gauss","profileImage":"https://profileimg.plaync.com/x","titleName":""},
         "stat":{"statList":[{"name":"Might","statSecondList":["Attack increase +2.5%"],"type":"STR","value":25},
                             {"name":"Justice [Nezekan]","statSecondList":[],"type":"Justice","value":41},
                             {"name":"아이템레벨","type":"ItemLevel","value":1415}]},
         "title":{"ownedCount":172,"titleList":[{"equipCategory":"Etc","equipStatList":[{"desc":"HP +900"}],"grade":"Unique","id":12010061,"name":"Through Hell and Back"}],"totalCount":304},
         "ranking":{"rankingList":null},
         "daevanion":{"boardList":[{"id":81,"name":"Nezekan","open":1,"openNodeCount":60,"openPercent":68,"totalNodeCount":88}]}}
        """;

    private const string Equipment = """
        {"equipment":{"equipmentList":[
            {"enchantLevel":5,"exceedLevel":0,"grade":"Legend","icon":"","id":210740031,"name":"Pilgrim Cloak","slotPos":19,"slotPosName":"Cape"},
            {"enchantLevel":10,"exceedLevel":0,"grade":"Unique","icon":"","id":110830047,"name":"Spiritforged Staff","slotPos":1,"slotPosName":"MainHand"},
            {"enchantLevel":5,"exceedLevel":0,"grade":"Legend","icon":"","id":210340031,"name":"Pilgrim Helm","slotPos":3,"slotPosName":"Helmet"}],
          "skinList":[]},
         "petwing":{"pet":{"id":1204,"level":3,"name":"Pagati"},"wing":{"enchantLevel":0,"grade":"Legend","id":30200703,"name":"Superior Daeva Wings"}},
         "skill":{"skillList":[{"acquired":1,"category":"Active","equip":1,"id":18010000,"name":"Onslaught","needLevel":1,"skillLevel":14},
                               {"acquired":0,"category":"Dp","equip":0,"id":18990000,"name":"Not learned","needLevel":40,"skillLevel":0}]}}
        """;

    [Fact]
    public void SearchHitsHaveTheirNameAndId()
    {
        var hit = Assert.Single(ArmoryClient.ParseSearch(Search));
        Assert.Equal("Doxylamin", hit.Name);
        Assert.Equal("abcDEF123=", hit.CharacterId);
        Assert.Equal((ushort)1321, hit.ServerId);
    }

    [Fact]
    public void ProfileHasCombatPowerItemLevelAndGear()
    {
        var p = ArmoryClient.ParseProfile("abcDEF123=", Info, Equipment, DateTimeOffset.UnixEpoch);
        Assert.Equal(64_358, p.CombatPower);
        Assert.Equal(1_415, p.ItemLevel);
        Assert.Equal("VOIDcx", p.Legion);
        Assert.DoesNotContain(p.Stats, s => s.Type == "ItemLevel");
        Assert.Equal("Attack increase +2.5%", Assert.Single(p.Stats, s => s.Type == "STR").Effects.Single());

        // In the game's order: weapon, helmet, cape
        Assert.Equal(["Spiritforged Staff", "Pilgrim Helm", "Pilgrim Cloak"], p.Gear.Select(g => g.Name));
        Assert.Equal(10, p.Gear[0].Enchant);
        Assert.Null(p.Gear[0].Icon);

        Assert.Equal("Superior Daeva Wings", p.Wings?.Name);
        Assert.Equal(("Pagati", 3), (p.Pet?.Name, p.Pet?.Level));
        Assert.Equal("Onslaught", Assert.Single(p.Skills).Name); // skills not learned are left out
        Assert.Equal((81, "Nezekan", 60, 88), (p.Daevanion[0].Id, p.Daevanion[0].Name, p.Daevanion[0].Open, p.Daevanion[0].Total));
        Assert.Equal((172, "Through Hell and Back"), (p.TitlesOwned, p.Titles.Single().Name));
    }

    [Fact]
    public void BoardsHaveTheirNodesAndEffects()
    {
        var board = ArmoryClient.ParseBoard("""
            {"nodeList":[{"boardId":81,"col":12,"effectList":[],"grade":"None","icon":"","name":"","nodeId":810012,"open":0,"row":1,"type":"None"},
                         {"boardId":81,"col":11,"effectList":[{"desc":"Protection Circle +1"}],"grade":"Rare","name":"Skill Level Up - Protection Circle","nodeId":810041,"open":1,"row":3,"type":"SkillLevel"},
                         {"boardId":81,"col":12,"effectList":[{"desc":"Critical Hit +5"}],"grade":"Common","name":"Critical Hit","nodeId":810042,"open":0,"row":3,"type":"Stat"}],
             "openSkillEffectList":[{"desc":"Onslaught +1"}],"openStatEffectList":[{"desc":"HP +800"}]}
            """);
        Assert.Equal(2, board.Nodes.Count); // empty cells are left out
        Assert.Equal((3, 11, true, "Protection Circle +1"), (board.Nodes[0].Row, board.Nodes[0].Column, board.Nodes[0].Open, board.Nodes[0].Effects.Single()));
        Assert.False(board.Nodes[1].Open);
        Assert.Equal(["Onslaught +1"], board.SkillEffects);
        Assert.Equal(["HP +800"], board.StatEffects);
    }

    [Fact]
    public void RegionsComeFromTheServerList()
    {
        Assert.Equal("eu", ArmoryClient.RegionCode(1321));
        Assert.Equal("nae", ArmoryClient.RegionCode(1101));
        Assert.Null(ArmoryClient.RegionCode(9999));
    }

    [Fact]
    public async Task LoadsOnceAndKeepsItForTenMinutes()
    {
        var now = DateTimeOffset.UnixEpoch;
        var site = new FakeSite();
        var armory = new ArmoryClient(site, () => now);

        var first = await armory.LoadAsync("doxylamin", 1321);
        Assert.Equal("Doxylamin", first.Profile?.Name);
        Assert.Equal(3, site.Requests.Count); // search, info, equipment
        Assert.Contains(site.Requests, r => r.Contains("characterId=abcDEF123%3D", StringComparison.Ordinal) && r.Contains("region=eu", StringComparison.Ordinal));

        await armory.LoadAsync("Doxylamin", 1321);
        Assert.Equal(3, site.Requests.Count);

        now += ArmoryClient.CacheFor;
        await armory.LoadAsync("Doxylamin", 1321);
        Assert.Equal(6, site.Requests.Count);
    }

    [Fact]
    public async Task UnknownNamesAndSilenceSayWhy()
    {
        var armory = new ArmoryClient(new FakeSite());
        Assert.Equal("No character named Nobody on Gauss.", (await armory.LoadAsync("Nobody", 1321)).Problem);
        Assert.StartsWith("The official AION2 site did not answer", (await new ArmoryClient(new FakeSite { Down = true }).LoadAsync("Doxylamin", 1321)).Problem);
        Assert.NotNull((await armory.LoadAsync("Doxylamin", 9999)).Problem);
    }

    private sealed class FakeSite : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        public bool Down { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (Down)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            string body = url.Contains("/search/", StringComparison.Ordinal)
                ? url.Contains("keyword=Nobody", StringComparison.Ordinal) ? """{"list":[]}""" : Search
                : url.Contains("/info?", StringComparison.Ordinal) ? Info : Equipment;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
