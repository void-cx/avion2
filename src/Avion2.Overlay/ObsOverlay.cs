using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// The OBS overlay (Settings): serves the meter and our character card on http://localhost:port/overlay for an
/// OBS Browser Source, and feeds it four times a second. Local only, off by default.
/// </summary>
public sealed class ObsOverlay
{
    private readonly AppState _state;
    private readonly ObsNames _names = new(StreamerMode.Names);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ObsServer? _server;
    private IReadOnlyDictionary<string, ObsFile>? _files;

    // this session, for the character card
    private MeterEngine? _engine;
    private long _sessionStartMs;
    private ulong? _kinahAtStart;

    public ObsOverlay(AppState state)
    {
        _state = state;
        _timer.Tick += (_, _) => Publish();
        state.InventoryUpdated += () =>
        {
            SyncSession();
            if (_engine is not null && _kinahAtStart is null && _state.Inventory is { } inventory)
            {
                _kinahAtStart = inventory.Kinah;
            }
        };
    }

    /// <summary>The address to paste into OBS.</summary>
    public string Address => _state.Stored.ObsPage.Url(_state.Stored.ObsPort);

    /// <summary>Served for this run whatever the setting says (<c>--obs</c>, for UI work).</summary>
    public bool Forced { get; set; }

    /// <summary>Why the overlay isn't served (port taken), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Starts, restarts or stops the server to match the settings.</summary>
    public void Apply()
    {
        var s = _state.Stored;
        _names.HideOthers = s.ObsHideOthers;
        _names.HideSelf = s.ObsHideSelf;
        bool on = s.ObsOverlay || Forced;
        if (_server is not null && (!on || _server.Port != s.ObsPort))
        {
            Stop();
        }

        Problem = null;
        if (!on || _server is not null)
        {
            return;
        }

        var server = new ObsServer(s.ObsPort, _files ??= LoadFiles());
        try
        {
            server.Start();
        }
        catch (SocketException)
        {
            server.Dispose();
            Problem = $"Port {s.ObsPort} is in use by another program. Choose another port.";
            return;
        }

        _server = server;
        Publish();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _server?.Dispose();
        _server = null;
    }

    private void Publish()
    {
        if (_server is null)
        {
            return;
        }

        SyncSession();
        var engine = _engine;
        string theme = _state.Stored.Theme;
        if (engine is null)
        {
            _server.Publish(ObsFeed.Build(false, theme, null, null, null, _names));
            return;
        }

        var status = engine.Status();
        var fight = engine.Snapshot(_state.Settings.Scope);
        _server.Publish(ObsFeed.Build(true, theme, fight, Character(engine, status, fight), Session(), _names));
    }

    /// <summary>A new meter session starts the session stats over (kinah from its first inventory).</summary>
    private void SyncSession()
    {
        if (_state.Engine != _engine)
        {
            _engine = _state.Engine;
            _sessionStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _kinahAtStart = null;
        }
    }

    private ObsCharacter? Character(MeterEngine engine, EngineStatus status, Avion2.Analysis.MeterSnapshot? fight)
    {
        // before a teleport tells us our name, the meter's own row knows it
        if ((status.SelfName ?? fight?.Self?.Name) is not { } name)
        {
            return null;
        }

        var cls = status.SelfClass != Avion2.Protocol.GameClass.Unknown ? status.SelfClass : fight?.Self?.Class ?? status.SelfClass;

        string? server = status.SelfServer is { } id ? BundledGameData.Server(id)?.Name : null;
        string? map = engine.LiveMap().MapId is { } mapId ? _state.Data.MapName(mapId) : null;
        double? hp = status.SelfMaxHp > 0 ? Math.Min(1.0, (double)status.SelfHp / status.SelfMaxHp) : null;
        return new ObsCharacter(name, cls, server, status.SelfLegion,
            status.SelfCombatPower ?? fight?.Self?.CombatPower, map, hp);
    }

    private ObsSession Session()
    {
        var today = DateTime.Today;
        var fights = _state.Fights
            .Select(f => f.Entry.Snapshot)
            .Where(s => s.StartedAtMs > 0 && DateTimeOffset.FromUnixTimeMilliseconds(s.StartedAtMs).LocalDateTime.Date == today)
            .ToList();
        double? best = fights.Select(s => s.Self?.Dps).Max();
        long? kinah = _kinahAtStart is { } start && _state.Inventory is { } inventory ? (long)inventory.Kinah - (long)start : null;
        int runs = _state.Runs.Count(r => r.Run.StartMs >= _sessionStartMs);
        return new ObsSession(fights.Count, best, kinah, runs);
    }

    /// <summary>The page and the theme fonts, from the app's resources.</summary>
    private static Dictionary<string, ObsFile> LoadFiles()
    {
        var files = new Dictionary<string, ObsFile>(StringComparer.Ordinal)
        {
            ["/overlay"] = new("text/html; charset=utf-8", Resource("Assets/obs-overlay.html")),
        };
        foreach (string font in new[] { "Cinzel", "SpaceGrotesk-Regular", "SpaceGrotesk-Medium", "SpaceGrotesk-Bold", "JetBrainsMono-Regular" })
        {
            files[$"/fonts/{font}.ttf"] = new("font/ttf", Resource($"Fonts/{font}.ttf"));
        }

        return files;
    }

    private static byte[] Resource(string path)
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/{path}")).Stream;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
