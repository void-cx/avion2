using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using Avion2.Analysis;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>One other party member on the overlay: class icon, name, HP bar, dead mark. Updated in place.</summary>
public sealed class PartyFrame(uint characterId) : Bindable
{
    private string _name = string.Empty;
    private string _emblem = "?";
    private Brush _classBrush = Brushes.Gray;
    private ImageSource? _icon;
    private double _hpRatio;
    private bool _hasHp;
    private bool _low;
    private bool _dead;
    private bool _away;
    private bool _bossTarget;
    private string _detail = string.Empty;

    public uint CharacterId { get; } = characterId;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Emblem { get => _emblem; set => Set(ref _emblem, value); }
    public Brush ClassBrush { get => _classBrush; set => Set(ref _classBrush, value); }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>0..1; meaningful only with <see cref="HasHp"/>.</summary>
    public double HpRatio { get => _hpRatio; set => Set(ref _hpRatio, value); }

    /// <summary>The HP is known (the member is in our zone).</summary>
    public bool HasHp { get => _hasHp; set => Set(ref _hasHp, value); }

    /// <summary>Under 30 % HP: the bar turns red.</summary>
    public bool Low { get => _low; set => Set(ref _low, value); }

    public bool Dead { get => _dead; set => Set(ref _dead, value); }

    /// <summary>Offline or in another zone: drawn faded.</summary>
    public bool Away { get => _away; set => Set(ref _away, value); }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>A boss targets this member (<c>20 92</c>).</summary>
    public bool BossTarget { get => _bossTarget; set => Set(ref _bossTarget, value); }
}

/// <summary>
/// The overlay's party panel: every other member of our party (not us), in slot order. Hidden when we are not in a
/// party or it is switched off in Settings.
/// </summary>
public sealed class PartyPanel : Bindable
{
    private static readonly Dictionary<GameClass, Brush> ClassBrushes = Enum.GetValues<GameClass>().ToDictionary(c => c, c =>
    {
        var brush = new SolidColorBrush(OverlayViewModel.ClassColor(c));
        brush.Freeze();
        return (Brush)brush;
    });

    private bool _visible;

    public ObservableCollection<PartyFrame> Members { get; } = new();

    public bool Visible { get => _visible; private set => Set(ref _visible, value); }

    /// <param name="mapName">Names a map, for the tooltip of members in another zone.</param>
    /// <param name="bossTarget">The entity id a boss targets, if any: that member gets the target mark.</param>
    public void Update(IReadOnlyList<PartyMemberView> party, bool enabled, Func<uint, string> mapName, uint? bossTarget = null)
    {
        var others = enabled ? party.Where(m => !m.IsSelf).ToList() : [];
        for (int i = Members.Count - 1; i >= 0; i--)
        {
            if (others.All(m => m.CharacterId != Members[i].CharacterId))
            {
                Members.RemoveAt(i);
            }
        }

        for (int i = 0; i < others.Count; i++)
        {
            var member = others[i];
            int at = IndexOf(member.CharacterId);
            PartyFrame frame;
            if (at < 0)
            {
                frame = new PartyFrame(member.CharacterId);
                Members.Insert(i, frame);
            }
            else
            {
                frame = Members[at];
                if (at != i)
                {
                    Members.Move(at, i);
                }
            }

            Fill(frame, member, mapName);
            frame.BossTarget = member.EntityId != 0 && member.EntityId == bossTarget && member.Alive;
            if (frame.BossTarget)
            {
                frame.Detail += "\nTargeted by the boss";
            }
        }

        Visible = Members.Count > 0;
    }

    private int IndexOf(uint characterId)
    {
        for (int i = 0; i < Members.Count; i++)
        {
            if (Members[i].CharacterId == characterId)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Fill(PartyFrame frame, PartyMemberView member, Func<uint, string> mapName)
    {
        frame.Name = StreamerMode.Name(member.Name);
        frame.Emblem = member.Class == GameClass.Unknown ? "?" : member.Class.ToString()[..2].ToUpperInvariant();
        frame.Icon = ClassIcons.For(member.Class);
        frame.ClassBrush = ClassBrushes[member.Class];
        frame.HasHp = member.HpRatio is not null && member.Online;
        frame.HpRatio = member.Alive ? member.HpRatio ?? 0 : 0;
        frame.Low = member.HpRatio < 0.3;
        frame.Dead = !member.Alive;
        frame.Away = !member.Online || member.HpRatio is null;

        var lines = new List<string>
        {
            string.Join(" · ", new[]
            {
                member.Class == GameClass.Unknown ? null : member.Class.ToString(),
                member.Status is { Level: > 0 } s ? $"Lv {s.Level}" : null,
                member.Status is { ItemLevel: > 0 } il ? $"iLv {il.ItemLevel}" : null,
                member.IsLeader ? "Leader" : null,
            }.OfType<string>()),
        };
        if (!member.Alive)
        {
            lines.Add("Dead");
        }
        else if (member.HpRatio is { } ratio)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"HP {member.Hp:N0} / {member.MaxHp:N0} ({ratio * 100:0}%)"));
        }

        if (!member.Online)
        {
            lines.Add("Offline or changing servers");
        }
        else if (member.HpRatio is null && member.Status is { MapId: not 0 } where)
        {
            lines.Add($"In {mapName(where.MapId)}");
        }

        frame.Detail = string.Join("\n", lines.Where(l => l.Length > 0));
    }
}
