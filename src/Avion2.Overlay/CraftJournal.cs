using System.IO;
using System.Text.Json;
using System.Windows;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Our crafts per character (what each made and used), saved in crafts.json, and the pet-soul notices; both follow the
/// meter while it runs.
/// </summary>
public sealed class CraftJournal
{
    private readonly AppState _state;
    private readonly Func<bool> _keep;

    private static string File =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AVION2", "crafts.json");

    public CraftJournal(AppState state, Func<bool> keep)
    {
        _state = state;
        _keep = keep;
        Book = Load();
    }

    /// <summary>Every character's crafts.</summary>
    public CraftBook Book { get; }

    /// <summary>Raised on the UI thread when a craft was added.</summary>
    public event Action? Updated;

    /// <summary>The crafts of the character the pages show, newest first.</summary>
    public IReadOnlyList<CraftEntry> Viewed => Book.For(_state.ViewedCharacter);

    /// <summary>Follows the meter: its crafts, and a notice when pet souls come in.</summary>
    public void Attach(MeterEngine engine)
    {
        engine.Crafted += (craft, character) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Book.Add(character, craft);
            Save();
            Updated?.Invoke();
        });
        // Queued after the progress update the same souls raised, so the counts are the new ones
        engine.SoulsGained += souls => Application.Current.Dispatcher.BeginInvoke(() => NotifySouls(souls));
    }

    /// <summary>"Soul of Zelophi: 3/5", one line per kind.</summary>
    private void NotifySouls(Avion2.Protocol.PetSoulsEvent souls)
    {
        // Only once the gain is counted (not while the game has yet to name us)
        if (!_state.Settings.NotifyPetSouls || _state.Progresses.LatestPets() is not { } pets || pets.PetsMs < souls.TimestampMs)
        {
            return;
        }

        foreach (var change in souls.Changes.DistinctBy(c => c.PetId))
        {
            if (PetSoulTable.Notice(pets, change.PetId, _state.PetName) is { } text)
            {
                _state.Notices.Notify(text, "Pet soul");
            }
        }
    }

    private static CraftBook Load()
    {
        try
        {
            return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<CraftBook>(System.IO.File.ReadAllText(File)) ?? new() : new();
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
            // the next craft tries again
        }
    }
}
