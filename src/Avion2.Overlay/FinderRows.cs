using System.Globalization;
using Avion2.Analysis;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>One line of the group-finder card on Home: a seated member, an open slot or a join request.</summary>
/// <param name="Role">The role the slot asks for ("Healer", "Any role").</param>
/// <param name="State">"Ready", "Leader", "Not ready", "Asks to join" …</param>
public sealed record FinderRow(string Name, string Role, string Stats, string State, bool Open, bool Ready, bool Away);

/// <summary>The group-finder card's texts, from the room the engine tracks. Names go through streamer mode.</summary>
public static class FinderCardText
{
    /// <summary>The recruiting text; the dungeon in streamer mode (the leader's free text can name people).</summary>
    public static string Title(FinderRoomView room) =>
        string.IsNullOrWhiteSpace(room.Title) || StreamerMode.On ? FinderData.DungeonName(room.DungeonId) : room.Title.Trim();

    /// <summary>"Krao Cave (Exploration) · Recruiting · 4/5".</summary>
    public static string Detail(FinderRoomView room) =>
        $"{FinderData.DungeonName(room.DungeonId)} · {FinderDungeons.Label(room.State)} · {room.Filled}/{Math.Max(room.Size, (byte)room.Slots.Count)}" +
        (room.SelfIsLeader ? " · you lead" : string.Empty);

    public static IReadOnlyList<FinderRow> Rows(FinderRoomView room)
    {
        var rows = new List<FinderRow>(room.Slots.Count + room.Requests.Count);
        foreach (var slot in room.Slots)
        {
            if (slot.IsOpen)
            {
                rows.Add(new FinderRow("Open slot", FinderDungeons.Label(slot.Role), string.Empty, string.Empty, Open: true, Ready: false, Away: false));
                continue;
            }

            string state = slot.IsLeader ? "Leader" : slot.Ready ? "Ready" : room.State == FinderRoomState.Recruiting ? "Not ready" : string.Empty;
            rows.Add(new FinderRow(StreamerMode.Name(slot.Name, slot.IsSelf), FinderDungeons.Label(slot.Role),
                Stats(slot.Class, slot.Level, slot.ItemLevel, slot.CombatPower), state, Open: false, Ready: slot.Ready && !slot.IsLeader,
                Away: !slot.Online));
        }

        foreach (var request in room.Requests)
        {
            rows.Add(new FinderRow(StreamerMode.Name(request.Name), string.Empty,
                Stats(request.Class, request.Level, request.ItemLevel, request.CombatPower), "Asks to join", Open: false, Ready: false,
                Away: false));
        }

        return rows;
    }

    /// <summary>"Cleric · Lv 45 · iLv 1,512 · CP 81,957", leaving out what is not known.</summary>
    public static string Stats(GameClass cls, uint level, uint itemLevel, uint combatPower) => string.Join(" · ", new[]
    {
        cls == GameClass.Unknown ? null : cls.ToString(),
        level > 0 ? $"Lv {level}" : null,
        itemLevel > 0 ? string.Create(CultureInfo.InvariantCulture, $"iLv {itemLevel:N0}") : null,
        combatPower > 0 ? string.Create(CultureInfo.InvariantCulture, $"CP {combatPower:N0}") : null,
    }.OfType<string>());
}
