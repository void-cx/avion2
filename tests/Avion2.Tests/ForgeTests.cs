using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Tests;

public class ForgeTests
{
    private static readonly GearForge Forge = BundledGameData.Forge;

    private static ForgeItem Item(string name) => Forge.Items.First(i => i.Name == name);

    [Fact]
    public void BuiltInTableHasItemsAndLadders()
    {
        Assert.True(Forge.Items.Count > 1000);
        var ludra = Item("Ludra's Blade of Extinction");
        Assert.Equal(15, Forge.Ladder(ludra, ForgeMode.Enchant)!.MaxLevel);
        Assert.Equal(5, Forge.Ladder(ludra, ForgeMode.Amplify)!.MaxLevel);
    }

    [Fact]
    public void EnchantPlanMatchesAion2Hub()
    {
        // aion2hub Gear Forge, Ludra's Blade of Extinction +0 → +15: 190,650 materials, 14,986,721 Kinah, 22.2 attempts
        var ladder = Forge.Ladder(Item("Ludra's Blade of Extinction"), ForgeMode.Enchant)!;
        var plan = GearForge.Plan(ladder, 0, 15);

        Assert.Equal(22.2, plan.Attempts.Average, 1);
        Assert.InRange(plan.Materials.Single(m => m.Name == "Enhance Stone").Average, 190_645, 190_655);
        Assert.InRange(plan.Gold.Average, 14_986_700, 14_986_750);
        Assert.InRange(plan.Attempts.P90, 26, 28); // aion2hub: 27

        // +10 → +11: 65%, +5% per fail → 1.49 attempts
        Assert.Equal(1.49, plan.Steps.Single(s => s.Step.Level == 10).ExpectedAttempts, 2);
    }

    [Fact]
    public void PityRampAndPartialRange()
    {
        var step = new ForgeStep(0, 20, 5, 100, [new ForgeMaterial("Stone", 10)]);
        Assert.Equal(0.20, step.ChanceAt(1), 6);
        Assert.Equal(0.30, step.ChanceAt(3), 6);
        Assert.Equal(1.0, step.ChanceAt(100), 6);

        var ladder = new ForgeLadder("Test", [step, new ForgeStep(1, 100, 0, 50, [new ForgeMaterial("Stone", 1)])]);
        var plan = GearForge.Plan(ladder, 1, 2, runs: 100);
        Assert.Equal(1, plan.Attempts.Average, 6);
        Assert.Equal(50, plan.Gold.P90, 6);
    }

    [Fact]
    public void SearchMatchesAllWords()
    {
        Assert.Contains(Forge.Search("ludra blade"), i => i.Name == "Ludra's Blade of Extinction");
        Assert.DoesNotContain(Forge.Search("ludra bow"), i => i.Name == "Ludra's Blade of Extinction");
    }
}
