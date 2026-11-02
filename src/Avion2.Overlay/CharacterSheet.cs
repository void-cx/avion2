using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avion2.Engine;
using Avion2.Protocol;

namespace Avion2.Overlay;

/// <summary>A character in the sidebar: one of ours, or a search hit.</summary>
public sealed class CharacterRow(string name, ushort serverId, GameClass cls, string detail) : Bindable
{
    private string _detail = detail;

    public string Name { get; } = name;
    public ushort ServerId { get; } = serverId;
    public ImageSource? Icon { get; } = ClassIcons.For(cls);
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

/// <summary>An item, skin or skill as a card: icon, name in its grade color, a line below.</summary>
public sealed record SheetCard(string Title, string Line, Brush Brush, Brush Border, ImageSource? Icon, string Tip, double Opacity = 1);

public sealed record SheetGroup(string Name, IReadOnlyList<SheetCard> Items);

public sealed record StatCard(string Name, string Value, string Effects);

public sealed record StatGroup(string Name, IReadOnlyList<StatCard> Items);

public sealed record BoardTab(int Id, string Name, string Count, double Fraction, bool Selected);

/// <summary>What the Characters page shows for one character from the official site.</summary>
public sealed class CharacterSheet(ArmoryProfile profile)
{
    private static readonly string[] MainStats = ["STR", "DEX", "INT", "CON", "AGI", "WIS"];

    public ArmoryProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string ServerName => Profile.ServerName;
    public string Level => Profile.Level.ToString(CultureInfo.InvariantCulture);
    public ImageSource? Portrait { get; } = WebImage(profile.ImageUrl, 180);
    public GameClass Class { get; } = ParseClass(profile.ClassName);
    public ImageSource? ClassIcon => ClassIcons.For(Class);
    public string ClassLine => $"{Profile.ClassName}  ·  {Profile.Race}";
    public string ItemLevel => Profile.ItemLevel?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";
    public string CombatPower => Profile.CombatPower.ToString("N0", CultureInfo.InvariantCulture);
    public string Legion => Profile.Legion is { } legion ? $"‹{legion}›" : string.Empty;

    /// <summary>"64,358 CP · iLvl 1,415": for the sidebar.</summary>
    public string Summary => $"{CombatPower} CP · iLvl {ItemLevel}";

    public IReadOnlyList<SheetCard> Titles => Profile.Titles
        .OrderBy(t => t.Category switch { "Attack" => 0, "Defense" => 1, _ => 2 })
        .Select(t => new SheetCard(t.Name, t.Category, ItemGrades.Brush(t.Grade), Outline(t.Grade), null,
            $"{t.Name} ({t.Category} title, worn)\n{string.Join("\n", t.Effects)}"))
        .ToList();

    public IReadOnlyList<SheetGroup> Equipment
    {
        get
        {
            var groups = new List<SheetGroup>();
            void Add(string name, Func<int, bool> slots)
            {
                var items = Profile.Gear.Where(g => slots(g.Slot)).Select(g => Card(g, SlotName(g.SlotName))).ToList();
                if (items.Count > 0)
                {
                    groups.Add(new SheetGroup(name, items));
                }
            }

            Add("Weapons", s => s is 1 or 2);
            Add("Armor", s => s is >= 3 and <= 8 or 19);
            Add("Accessories", s => s is >= 10 and <= 17 or 22);
            Add("Runes", s => s is 23 or 24);
            Add("Other", s => s is not (>= 1 and <= 8 or >= 10 and <= 17 or 19 or 22 or 23 or 24));
            return groups;
        }
    }

    public IReadOnlyList<SheetGroup> Skills => Profile.Skills
        .GroupBy(s => s.Category)
        .OrderBy(g => g.Key switch { "Active" => 0, "Passive" => 1, _ => 2 })
        .Select(g => new SheetGroup(g.Key switch { "Active" => "Active skills", "Passive" => "Passive skills", "Dp" => "DP skills", _ => g.Key },
            g.OrderByDescending(Slotted).ThenByDescending(s => s.Level)
             .Select(s => new SheetCard(s.Name, $"Level {s.Level}{(Slotted(s) ? string.Empty : " · not on the skill bar")}", Resource("TextBrush"),
                 Resource("LineBrush"), WebImage(s.Icon, 76), s.Name, Slotted(s) ? 1 : 0.55))
             .ToList()))
        .ToList();

    public IReadOnlyList<SheetGroup> Cosmetics
    {
        get
        {
            var groups = new List<SheetGroup>();
            var wings = new[] { (Profile.Wings, "Wings"), (Profile.WingSkin, "Wing look") }
                .Where(w => w.Item1 is not null).Select(w => Card(w.Item1!, w.Item2)).ToList();
            if (Profile.Pet is { } pet)
            {
                wings.Add(new SheetCard(pet.Name, $"Pet · level {pet.Level}", Resource("TextBrush"), Resource("LineBrush"), WebImage(pet.Icon, 76), pet.Name));
            }

            if (wings.Count > 0)
            {
                groups.Add(new SheetGroup("Wings and pet", wings));
            }

            if (Profile.Skins.Count > 0)
            {
                groups.Add(new SheetGroup("Appearance", Profile.Skins.Select(s => Card(s, SlotName(s.SlotName))).ToList()));
            }

            return groups;
        }
    }

    public IReadOnlyList<StatGroup> StatGroups =>
    [
        new("Attributes", Profile.Stats.Where(s => MainStats.Contains(s.Type)).Select(Stat).ToList()),
        new("Daevanion gods", Profile.Stats.Where(s => !MainStats.Contains(s.Type)).Select(Stat).ToList()),
    ];

    public IReadOnlyList<BoardTab> Boards(int selected) => Profile.Daevanion
        .Select(b => new BoardTab(b.Id, b.Name, $"{b.Open}/{b.Total}", b.Total > 0 ? (double)b.Open / b.Total : 0, b.Id == selected))
        .ToList();

    /// <summary>Passives always work; active and DP skills only from the skill bar.</summary>
    private static bool Slotted(ArmorySkill skill) => skill.Equipped || skill.Category == "Passive";

    private static StatCard Stat(ArmoryStat s) =>
        new(s.Name, s.Value.ToString("N0", CultureInfo.InvariantCulture), string.Join("\n", s.Effects));

    private static SheetCard Card(ArmoryItem item, string line)
    {
        string level = item.Enchant > 0 ? $"+{item.Enchant} " : string.Empty;
        string tip = $"{item.Name}\n{item.Grade}{(item.Enchant > 0 ? $" · +{item.Enchant}" : string.Empty)}" +
                     (item.Exceed > 0 ? $" · breakthrough {item.Exceed}" : string.Empty);
        return new SheetCard(level + item.Name, line, ItemGrades.Brush(item.Grade), Outline(item.Grade), WebImage(item.Icon, 76), tip);
    }

    /// <summary>"Earring1" → "Earring", "SubHand" → "Off-hand", as the game labels its slots.</summary>
    public static string SlotName(string slot) => slot switch
    {
        "MainHand" => "Main hand",
        "SubHand" => "Off-hand",
        "Pants" => "Legs",
        "Shoulder" => "Shoulders",
        "Rune1" => "Rune slot 1",
        "Rune2" => "Rune slot 2",
        _ => Spaced(slot.TrimEnd('1', '2', 'L', 'R')),
    };

    private static string Spaced(string text) =>
        string.Concat(text.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    /// <summary>The site's class names; Spiritmaster is the Elementalist, Brawler the Fighter.</summary>
    public static GameClass ParseClass(string name) => name switch
    {
        "Spiritmaster" => GameClass.Elementalist,
        "Brawler" => GameClass.Fighter,
        _ => Enum.TryParse<GameClass>(name, ignoreCase: true, out var cls) ? cls : GameClass.Unknown,
    };

    /// <summary>The grade color, faded for a card's outline.</summary>
    private static Brush Outline(string grade)
    {
        if (ItemGrades.Brush(grade) is not SolidColorBrush { } solid || grade is "Common" or "")
        {
            return Resource("LineBrush");
        }

        var brush = new SolidColorBrush(Color.FromArgb(0x90, solid.Color.R, solid.Color.G, solid.Color.B));
        brush.Freeze();
        return brush;
    }

    private static Brush Resource(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static readonly Dictionary<string, ImageSource> Images = new();

    /// <summary>An icon or portrait from the official site's servers, downloaded once per run.</summary>
    public static ImageSource? WebImage(string? url, int width)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (!Images.TryGetValue(url, out var image))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = width;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            Images[url] = image = bitmap;
        }

        return image;
    }
}
