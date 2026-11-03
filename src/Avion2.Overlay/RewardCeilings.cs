using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// The guaranteed rewards of the expedition reward cubes, per character: the counters the game sends at every world
/// entry, saved in cube_ceilings.json, with the cube of each expedition map learned from the bosses of saved runs.
/// </summary>
public sealed class RewardCeilings
{
    private readonly AppState _state;
    private readonly Func<bool> _keep;

    private static string File =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "cube_ceilings.json");

    public RewardCeilings(AppState state, Func<bool> keep)
    {
        _state = state;
        _keep = keep;
        Book = Load();
        Book.Learn(state.Runs.Select(r => r.Run), BundledGameData.Cubes);
        state.Runs.CollectionChanged += OnRunsChanged;
    }

    /// <summary>Every character's counters and the learned maps.</summary>
    public CeilingBook Book { get; }

    /// <summary>Raised on the UI thread when counters changed.</summary>
    public event Action? Updated;

    /// <summary>The counters of the character the pages show.</summary>
    public IReadOnlyList<CeilingProgress> Viewed => Book.Progress(_state.ViewedCharacter, BundledGameData.Cubes);

    /// <summary>The counter of a run's expedition for the run's character, or null (not an expedition, nothing sent yet).</summary>
    public CeilingProgress? For(DungeonRun run) =>
        Book.Progress(run.Character, BundledGameData.Cubes).FirstOrDefault(p => run.Maps.Contains(p.DungeonId));

    /// <summary>The cube of a run's expedition, from its bosses or the map learned for it.</summary>
    public CubeInfo? CubeOf(DungeonRun run) =>
        run.Bosses.Select(b => b.Npc is { } npc ? BundledGameData.Cubes.ByNpc(npc) : null).FirstOrDefault(c => c is not null) ??
        run.Maps.Select(m => BundledGameData.Cubes.ByKey(Book.CubeOf(m))).FirstOrDefault(c => c is not null);

    /// <summary>Follows the meter's counters while it runs.</summary>
    public void Attach(MeterEngine engine) => engine.RewardsChanged += () =>
    {
        var (ceilings, maps) = engine.Ceilings();
        Application.Current.Dispatcher.BeginInvoke(() => Take(ceilings, maps));
    };

    private void Take(CeilingState ceilings, IReadOnlyDictionary<uint, string> maps)
    {
        bool changed = Book.Take(ceilings);
        foreach (var (map, cube) in maps)
        {
            changed |= Book.Learn(map, cube);
        }

        if (changed)
        {
            Save();
            Updated?.Invoke();
        }
    }

    private void OnRunsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is { } added && Book.Learn(added.OfType<RunRow>().Select(r => r.Run), BundledGameData.Cubes) > 0)
        {
            Save();
            Updated?.Invoke();
        }
    }

    private static CeilingBook Load()
    {
        try
        {
            return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<CeilingBook>(System.IO.File.ReadAllText(File)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private void Save()
    {
        if (!_keep())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(Book));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the game sends the counters again at the next world entry
        }
    }
}

/// <summary>One expedition's guaranteed reward in the Runs sidebar.</summary>
public sealed record CeilingRow(string Name, string Detail, string Value, Brush ValueBrush)
{
    public static CeilingRow From(CeilingProgress progress)
    {
        string name = progress.Cube?.Name ?? progress.Title;
        string detail = progress.Cube is { } cube ? $"{cube.ModeName} · {progress.Status}" : progress.Status;
        string key = progress.Ready ? "LiveBrush" : progress.AllClaimed ? "MutedBrush" : "TitleBrush";
        var brush = Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        return new CeilingRow(name, detail, progress.Value, brush);
    }
}
