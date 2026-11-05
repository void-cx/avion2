using System.Windows.Threading;
using Avion2.Analysis;
using Avion2.Engine;

namespace Avion2.Overlay;

/// <summary>
/// Opens the applicant card (<see cref="ApplicantWindow"/>) while someone asks to join the group-finder room we lead,
/// and looks each applicant up on the official site. The card closes by itself once nobody is waiting (they joined,
/// withdrew or the request ran out); ✕ hides it until the next new request. Settings → Overlays → Applicant card.
/// </summary>
public sealed class ApplicantPopup
{
    private readonly AppState _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>The site's answer per applicant; null while it is asked.</summary>
    private readonly Dictionary<uint, ArmoryLookup?> _lookups = [];

    /// <summary>When each open request was first seen (epoch ms), for the expiry bar.</summary>
    private readonly Dictionary<uint, long> _firstSeen = [];

    private ApplicantWindow? _window;
    private uint? _current;
    private bool _hidden;
    private string? _shownKey;

    public ApplicantPopup(AppState state)
    {
        _state = state;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        StreamerMode.Changed += () => _shownKey = null;
    }

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Refresh()
    {
        var room = _state.Engine?.FinderRoom();
        var requests = room is { SelfIsLeader: true } && _state.Settings.ApplicantCard
            ? room.Requests.OrderByDescending(r => r.ExpiresAtMs).ToList()
            : [];

        // forget the requests that are gone; a new one shows itself (newest first), even after ✕
        foreach (uint gone in _firstSeen.Keys.Where(id => requests.All(r => r.CharacterId != id)).ToList())
        {
            _firstSeen.Remove(gone);
            _lookups.Remove(gone);
        }

        foreach (var request in requests.Where(r => !_firstSeen.ContainsKey(r.CharacterId)))
        {
            _firstSeen[request.CharacterId] = NowMs;
            _hidden = false;
            _current = requests[0].CharacterId;
            _ = LookUp(request);
        }

        if (requests.Count == 0 || _hidden)
        {
            _current = null;
            _window?.Hide();
            return;
        }

        int index = Math.Max(0, requests.FindIndex(r => r.CharacterId == _current));
        var shown = requests[index];
        _current = shown.CharacterId;
        _lookups.TryGetValue(shown.CharacterId, out var lookup);
        string key = $"{shown}|{index}/{requests.Count}|{lookup is not null}|{room!.Filled}|{string.Join(",", room.Slots.Select(s => s.ItemLevel))}";
        var window = Window();
        if (key != _shownKey)
        {
            _shownKey = key;
            window.Fill(new ApplicantShown(
                shown,
                FinderData.DungeonName(room.DungeonId),
                ApplicantCard.Room(room),
                index,
                requests.Count,
                lookup,
                SeenLine(shown.Name),
                _state.Engine?.Legion()?.Name,
                _firstSeen[shown.CharacterId]));
        }

        window.Tick(NowMs);
        if (!window.IsVisible)
        {
            window.Show();
        }
    }

    private ApplicantWindow Window()
    {
        if (_window is null)
        {
            _window = new ApplicantWindow(_state);
            _window.Dismissed += () =>
            {
                _hidden = true;
                _window.Hide();
            };
            _window.PageClicked += step =>
            {
                var requests = _state.Engine?.FinderRoom()?.Requests.OrderByDescending(r => r.ExpiresAtMs).ToList() ?? [];
                int at = requests.FindIndex(r => r.CharacterId == _current);
                if (at >= 0 && at + step >= 0 && at + step < requests.Count)
                {
                    _current = requests[at + step].CharacterId;
                    Refresh();
                }
            };
        }

        return _window;
    }

    private async Task LookUp(FinderJoinRequestView request)
    {
        _lookups[request.CharacterId] = null;
        var lookup = await _state.Armory.LoadAsync(request.Name, request.Server);
        if (_firstSeen.ContainsKey(request.CharacterId))
        {
            _lookups[request.CharacterId] = lookup;
            _shownKey = null;
        }
    }

    /// <summary>"3 runs with you, last Krao Cave 2 days ago"; null when we never ran with them.</summary>
    private string? SeenLine(string name)
    {
        var seen = ApplicantCard.SeenWith(_state.RunLog.Runs, name);
        if (seen.Last is not { } last)
        {
            return null;
        }

        string title = last.Title(BundledGameData.Dungeons, _state.Data);
        string ago = ApplicantCard.Ago(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(last.StartMs));
        return $"{seen.Count} {(seen.Count == 1 ? "run" : "runs")} with you, last {title} {ago}";
    }

    /// <summary>The app is closing.</summary>
    public void Close()
    {
        _timer.Stop();
        _window?.Close();
    }
}
